using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IEmoteTagService"/> on Postgres.
/// <para>
/// <b>Locking.</b> Every mutation — create, rename, delete, add entries, remove entries — runs in one
/// transaction that first takes the channel row with <c>FOR UPDATE</c>
/// (<see cref="ChannelQueries.LoadChannelForUpdateAsync"/>) and then touches only the tag tables. That
/// lock is the contract for both limits: under READ COMMITTED a count taken before an insert enforces
/// nothing on its own (two assignments of one emote each to a tag at 999 would both count 999 and both
/// commit 1001), while behind the lock the second caller counts what the first committed. It is also
/// the ordering contract part C of #201 builds on: its reports read entries under the same lock, so a
/// removal can never overtake one half-way. The sync updates the channel row without a lock and writes
/// no tag table, so the order is always channel row first, tag tables second — no cycle. Reads take no
/// lock.
/// </para>
/// <para>
/// Input is checked before the database is asked anything, so an unfit request is answered the same
/// way whether or not the channel or tag exists.
/// </para>
/// </summary>
public class EmoteTagService(AppDbContext db) : IEmoteTagService
{
    private const string TagTargetType = "emoteTag";
    private const string TagNameIndexName = "IX_EmoteTags_ChannelId_NormalizedName";

    public async Task<EmoteTagListResult> ListAsync(
        string channelName, string? emoteSetId, CancellationToken cancellationToken = default)
    {
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new EmoteTagListResult(EmoteTagListStatus.ChannelNotFound, null, false, []);
        }

        var (setId, isActiveSet) = ResolveSet(channel, emoteSetId);

        var tags = await db.EmoteTags
            .AsNoTracking()
            .Where(t => t.ChannelId == channel.Id)
            .OrderBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);
        if (tags.Count == 0)
        {
            return new EmoteTagListResult(EmoteTagListStatus.Ok, setId, isActiveSet, []);
        }

        // Rule 10: the in-set ids are materialized into a plain list first, and the counts group a
        // single table on its FK column filtered by scalar lists — no navigation join into the GroupBy.
        var tagIds = tags.Select(t => t.Id).ToList();
        var inSetIds = isActiveSet ? await LoadInSetIdsAsync(channel.Id, cancellationToken) : [];
        var counts = await db.EmoteTagEntries
            .Where(e => tagIds.Contains(e.TagId))
            .GroupBy(e => e.TagId)
            .Select(g => new
            {
                TagId = g.Key,
                EntryCount = g.Count(),
                InSetCount = g.Count(e => inSetIds.Contains(e.SevenTvEmoteId))
            })
            .ToDictionaryAsync(c => c.TagId, cancellationToken);

        var summaries = tags
            .Select(t =>
            {
                var count = counts.GetValueOrDefault(t.Id);
                return new EmoteTagSummaryDto(
                    t.Id,
                    t.Name,
                    count?.EntryCount ?? 0,
                    isActiveSet ? count?.InSetCount ?? 0 : null);
            })
            .ToList();
        return new EmoteTagListResult(EmoteTagListStatus.Ok, setId, isActiveSet, summaries);
    }

    public async Task<EmoteTagEntriesResult> ListEntriesAsync(
        string channelName, long tagId, string? emoteSetId, CancellationToken cancellationToken = default)
    {
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new EmoteTagEntriesResult(EmoteTagEntriesStatus.ChannelNotFound, null, false, []);
        }

        var (setId, isActiveSet) = ResolveSet(channel, emoteSetId);

        var tagExists = await db.EmoteTags.AnyAsync(t => t.Id == tagId && t.ChannelId == channel.Id, cancellationToken);
        if (!tagExists)
        {
            return new EmoteTagEntriesResult(EmoteTagEntriesStatus.TagNotFound, setId, isActiveSet, []);
        }

        // Ordinal tie-break in memory rather than in SQL: the database orders text by its collation,
        // and "deterministic" should not depend on which one the server was initialised with.
        var entries = (await db.EmoteTagEntries
                .AsNoTracking()
                .Where(e => e.TagId == tagId)
                .ToListAsync(cancellationToken))
            .OrderBy(e => e.AddedAtUtc)
            .ThenBy(e => e.SevenTvEmoteId, StringComparer.Ordinal)
            .ToList();

        Dictionary<string, string> currentNames = [];
        if (isActiveSet && entries.Count > 0)
        {
            var ids = entries.Select(e => e.SevenTvEmoteId).ToList();
            // One row per (channel, 7TV id) by the unique index, so the dictionary cannot collide.
            currentNames = await db.Emotes
                .Where(e => e.ChannelId == channel.Id && !e.IsArchived && ids.Contains(e.SevenTvEmoteId))
                .ToDictionaryAsync(e => e.SevenTvEmoteId, e => e.Name, StringComparer.Ordinal, cancellationToken);
        }

        var dtos = entries
            .Select(e =>
            {
                bool? inSet = isActiveSet ? currentNames.ContainsKey(e.SevenTvEmoteId) : null;
                return new EmoteTagEntryDto(
                    e.SevenTvEmoteId,
                    e.Alias,
                    e.ImageUrl,
                    inSet,
                    inSet == true ? currentNames[e.SevenTvEmoteId] : null);
            })
            .ToList();
        return new EmoteTagEntriesResult(EmoteTagEntriesStatus.Ok, setId, isActiveSet, dtos);
    }

    public async Task<EmoteTagMutationResult> CreateAsync(
        string channelName, string? name, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!EmoteTagName.IsValid(name))
        {
            return new EmoteTagMutationResult(EmoteTagMutationStatus.NameInvalid, null);
        }

        var displayName = name!.Trim();
        var normalizedName = EmoteTagName.Normalize(displayName);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var channel = await db.LoadChannelForUpdateAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new EmoteTagMutationResult(EmoteTagMutationStatus.ChannelNotFound, null);
        }

        if (await IsNameTakenAsync(channel.Id, normalizedName, exceptTagId: null, cancellationToken))
        {
            return new EmoteTagMutationResult(EmoteTagMutationStatus.NameTaken, null);
        }

        var tagCount = await db.EmoteTags.CountAsync(t => t.ChannelId == channel.Id, cancellationToken);
        if (tagCount >= EmoteTagLimits.MaxTagsPerChannel)
        {
            return new EmoteTagMutationResult(EmoteTagMutationStatus.LimitReached, null);
        }

        var tag = new EmoteTag
        {
            ChannelId = channel.Id,
            Name = displayName,
            NormalizedName = normalizedName,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.EmoteTags.Add(tag);
        // Two saves in one transaction, as in VoteSessionService.CreateAsync: the tag id is generated by
        // the insert and the audit entry needs it. Either both rows land or neither does.
        if (!await TrySaveNameAsync(cancellationToken))
        {
            return new EmoteTagMutationResult(EmoteTagMutationStatus.NameTaken, null);
        }

        AddTagAudit(actor, AuditActions.TagCreate, channel.ChannelName, tag.Id, new { tagId = tag.Id });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new EmoteTagMutationResult(EmoteTagMutationStatus.Ok, new EmoteTagDto(tag.Id, tag.Name));
    }

    public async Task<EmoteTagMutationResult> RenameAsync(
        string channelName, long tagId, string? name, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!EmoteTagName.IsValid(name))
        {
            return new EmoteTagMutationResult(EmoteTagMutationStatus.NameInvalid, null);
        }

        var displayName = name!.Trim();
        var normalizedName = EmoteTagName.Normalize(displayName);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (status, channel, tag) = await LoadForMutationAsync(channelName, tagId, cancellationToken);
        if (status != EmoteTagMutationStatus.Ok)
        {
            return new EmoteTagMutationResult(status, null);
        }

        if (string.Equals(tag!.Name, displayName, StringComparison.Ordinal))
        {
            // Nothing changes, so nothing is written — not even an audit entry: a no-op is not an event.
            return new EmoteTagMutationResult(EmoteTagMutationStatus.Ok, new EmoteTagDto(tag.Id, tag.Name));
        }

        // The tag's own name in another casing keeps its normalized form and is always allowed.
        if (await IsNameTakenAsync(channel!.Id, normalizedName, exceptTagId: tag.Id, cancellationToken))
        {
            return new EmoteTagMutationResult(EmoteTagMutationStatus.NameTaken, null);
        }

        tag.Name = displayName;
        tag.NormalizedName = normalizedName;
        AddTagAudit(actor, AuditActions.TagRename, channel.ChannelName, tag.Id, new { tagId = tag.Id });
        if (!await TrySaveNameAsync(cancellationToken))
        {
            return new EmoteTagMutationResult(EmoteTagMutationStatus.NameTaken, null);
        }

        await transaction.CommitAsync(cancellationToken);
        return new EmoteTagMutationResult(EmoteTagMutationStatus.Ok, new EmoteTagDto(tag.Id, tag.Name));
    }

    public async Task<EmoteTagMutationStatus> DeleteAsync(
        string channelName, long tagId, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (status, channel, tag) = await LoadForMutationAsync(channelName, tagId, cancellationToken);
        if (status != EmoteTagMutationStatus.Ok)
        {
            return status;
        }

        // Counted before the cascade takes the entries; the name stays out of the audit row (E30), so
        // after the delete the entry says how much went, not what it was called.
        var entryCount = await db.EmoteTagEntries.CountAsync(e => e.TagId == tag!.Id, cancellationToken);
        AddTagAudit(actor, AuditActions.TagDelete, channel!.ChannelName, tag!.Id, new { tagId = tag.Id, entryCount });
        db.EmoteTags.Remove(tag);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return EmoteTagMutationStatus.Ok;
    }

    public async Task<EmoteTagAddEntriesResult> AddEntriesAsync(
        string channelName, long tagId, IReadOnlyList<string>? sevenTvEmoteIds, CancellationToken cancellationToken = default)
    {
        var (inputStatus, ids) = CheckEmoteIds(sevenTvEmoteIds);
        if (inputStatus is { } rejected)
        {
            return new EmoteTagAddEntriesResult(
                rejected == EmoteIdsInputStatus.Empty ? EmoteTagAddEntriesStatus.EmoteIdsEmpty : EmoteTagAddEntriesStatus.EmoteIdsInvalid,
                0, 0, []);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (status, channel, tag) = await LoadForMutationAsync(channelName, tagId, cancellationToken);
        if (status != EmoteTagMutationStatus.Ok)
        {
            return new EmoteTagAddEntriesResult(
                status == EmoteTagMutationStatus.ChannelNotFound ? EmoteTagAddEntriesStatus.ChannelNotFound : EmoteTagAddEntriesStatus.TagNotFound,
                0, 0, []);
        }

        // Snapshot source (E23): the channel's unarchived row, never the client.
        var rows = await db.Emotes
            .AsNoTracking()
            .Where(e => e.ChannelId == channel!.Id && !e.IsArchived && ids.Contains(e.SevenTvEmoteId))
            .Select(e => new { e.SevenTvEmoteId, e.Name, e.ImageUrl })
            .ToDictionaryAsync(e => e.SevenTvEmoteId, StringComparer.Ordinal, cancellationToken);
        var alreadyTagged = (await db.EmoteTagEntries
                .Where(e => e.TagId == tag!.Id && ids.Contains(e.SevenTvEmoteId))
                .Select(e => e.SevenTvEmoteId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        // Not in the set wins over already tagged: an emote that left the set since it was tagged is
        // reported as skipped, the same as one that was never there — the request could not tag it now.
        var skipped = ids.Where(id => !rows.ContainsKey(id)).ToList();
        var alreadyTaggedCount = ids.Count(id => rows.ContainsKey(id) && alreadyTagged.Contains(id));
        var toAdd = ids.Where(id => rows.ContainsKey(id) && !alreadyTagged.Contains(id)).ToList();

        // Checked before writing and against what would actually be written; at the limit nothing is
        // written at all rather than a first slice of the request.
        var existingCount = await db.EmoteTagEntries.CountAsync(e => e.TagId == tag!.Id, cancellationToken);
        if (existingCount + toAdd.Count > EmoteTagLimits.MaxEntriesPerTag)
        {
            return new EmoteTagAddEntriesResult(EmoteTagAddEntriesStatus.EntryLimitReached, 0, 0, []);
        }

        var now = DateTime.UtcNow;
        db.EmoteTagEntries.AddRange(toAdd.Select(id => new EmoteTagEntry
        {
            TagId = tag!.Id,
            SevenTvEmoteId = id,
            Alias = rows[id].Name,
            ImageUrl = rows[id].ImageUrl,
            AddedAtUtc = now
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new EmoteTagAddEntriesResult(EmoteTagAddEntriesStatus.Ok, toAdd.Count, alreadyTaggedCount, skipped);
    }

    public async Task<EmoteTagRemoveEntriesResult> RemoveEntriesAsync(
        string channelName, long tagId, IReadOnlyList<string>? sevenTvEmoteIds, CancellationToken cancellationToken = default)
    {
        var (inputStatus, ids) = CheckEmoteIds(sevenTvEmoteIds);
        if (inputStatus is { } rejected)
        {
            return new EmoteTagRemoveEntriesResult(
                rejected == EmoteIdsInputStatus.Empty ? EmoteTagRemoveEntriesStatus.EmoteIdsEmpty : EmoteTagRemoveEntriesStatus.EmoteIdsInvalid,
                0);
        }

        // Under the channel lock although no limit needs it here: part C's reports read the entries
        // under that lock and must not be overtaken by a removal half-way (see the class comment).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (status, _, tag) = await LoadForMutationAsync(channelName, tagId, cancellationToken);
        if (status != EmoteTagMutationStatus.Ok)
        {
            return new EmoteTagRemoveEntriesResult(
                status == EmoteTagMutationStatus.ChannelNotFound ? EmoteTagRemoveEntriesStatus.ChannelNotFound : EmoteTagRemoveEntriesStatus.TagNotFound,
                0);
        }

        var removed = await db.EmoteTagEntries
            .Where(e => e.TagId == tag!.Id && ids.Contains(e.SevenTvEmoteId))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new EmoteTagRemoveEntriesResult(EmoteTagRemoveEntriesStatus.Ok, removed);
    }

    /// <summary>
    /// Locks the channel row (the caller has opened the transaction) and loads one of <em>its</em>
    /// tags, tracked. A tag of another channel is <c>TagNotFound</c>, never "found but foreign".
    /// </summary>
    private async Task<(EmoteTagMutationStatus Status, Channel? Channel, EmoteTag? Tag)> LoadForMutationAsync(
        string channelName, long tagId, CancellationToken cancellationToken)
    {
        var channel = await db.LoadChannelForUpdateAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return (EmoteTagMutationStatus.ChannelNotFound, null, null);
        }

        var tag = await db.EmoteTags.SingleOrDefaultAsync(t => t.Id == tagId && t.ChannelId == channel.Id, cancellationToken);
        return tag is null
            ? (EmoteTagMutationStatus.TagNotFound, channel, null)
            : (EmoteTagMutationStatus.Ok, channel, tag);
    }

    private Task<bool> IsNameTakenAsync(string channelId, string normalizedName, long? exceptTagId, CancellationToken cancellationToken) =>
        db.EmoteTags.AnyAsync(
            t => t.ChannelId == channelId && t.NormalizedName == normalizedName && t.Id != exceptTagId,
            cancellationToken);

    /// <summary>
    /// Saves a tag-name write; <c>false</c> when the unique name index refused it. Behind the channel
    /// lock the pre-check already sees every committed name, so this is the backstop for a writer that
    /// does not take the lock, not an expected path.
    /// </summary>
    private async Task<bool> TrySaveNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: TagNameIndexName,
        })
        {
            return false;
        }
    }

    private async Task<List<string>> LoadInSetIdsAsync(string channelId, CancellationToken cancellationToken) =>
        await db.Emotes
            .Where(e => e.ChannelId == channelId && !e.IsArchived)
            .Select(e => e.SevenTvEmoteId)
            .ToListAsync(cancellationToken);

    private void AddTagAudit(AuditActor actor, string action, string channelName, long tagId, object details) =>
        db.AddAuditEntry(
            actor,
            action,
            channelName: channelName,
            targetType: TagTargetType,
            targetId: tagId.ToString(CultureInfo.InvariantCulture),
            details: details);

    /// <summary>
    /// Set resolution shared by both reads: no id means the channel's active set, and a channel without
    /// one has no set at all. Ordinal, like every set-id comparison.
    /// </summary>
    private static (string? SetId, bool IsActiveSet) ResolveSet(Channel channel, string? emoteSetId)
    {
        var setId = string.IsNullOrEmpty(emoteSetId) ? channel.ActiveEmoteSetId : emoteSetId;
        if (string.IsNullOrEmpty(setId))
        {
            return (null, false);
        }

        return (setId, string.Equals(setId, channel.ActiveEmoteSetId, StringComparison.Ordinal));
    }

    /// <summary>Dedupes ordinally in request order; empty wins over unfit, and an oversized request counts as unfit.</summary>
    private static (EmoteIdsInputStatus? Rejected, List<string> Ids) CheckEmoteIds(IReadOnlyList<string>? sevenTvEmoteIds)
    {
        if (sevenTvEmoteIds is null || sevenTvEmoteIds.Count == 0)
        {
            return (EmoteIdsInputStatus.Empty, []);
        }

        if (sevenTvEmoteIds.Count > EmoteTagLimits.MaxIdsPerRequest
            || !sevenTvEmoteIds.All(SevenTvEmoteIdValidation.IsValid))
        {
            return (EmoteIdsInputStatus.Invalid, []);
        }

        return (null, sevenTvEmoteIds.Distinct(StringComparer.Ordinal).ToList());
    }

    private enum EmoteIdsInputStatus
    {
        Empty,
        Invalid
    }
}
