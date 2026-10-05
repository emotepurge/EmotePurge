using System.Collections.ObjectModel;
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
/// <b>Read-time rule.</b> The sync never deletes a placement; it records leave observations. Whether a
/// placement still holds is decided here, on every read, against those observations — see
/// <see cref="LoadPlacementStatesAsync"/>.
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

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<long>> EmptyTagIdsByEmote =
        ReadOnlyDictionary<string, IReadOnlySet<long>>.Empty;

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

        // Placements and activations are per set, so they are computed for any resolved set — a
        // non-active one included — and stay empty without one. Placements are counted in memory, over
        // the valid ones only.
        Dictionary<long, ActivationState> activations = [];
        Dictionary<long, int> placedCounts = [];
        if (setId is not null)
        {
            activations = await LoadActivationsAsync(tagIds, setId, cancellationToken);
            placedCounts = (await LoadPlacementStatesAsync(channel.Id, setId, tagIds, null, cancellationToken))
                .Where(p => p.Holds)
                .CountBy(p => p.TagId)
                .ToDictionary(c => c.Key, c => c.Value);
        }

        var summaries = tags
            .Select(t =>
            {
                var count = counts.GetValueOrDefault(t.Id);
                var activation = activations.GetValueOrDefault(t.Id);
                return new EmoteTagSummaryDto(
                    t.Id,
                    t.Name,
                    count?.EntryCount ?? 0,
                    isActiveSet ? count?.InSetCount ?? 0 : null,
                    placedCounts.GetValueOrDefault(t.Id),
                    activation is not null,
                    activation?.ActivatedAtUtc);
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
            return new EmoteTagEntriesResult(EmoteTagEntriesStatus.ChannelNotFound, null, false, [], null);
        }

        var (setId, isActiveSet) = ResolveSet(channel, emoteSetId);

        // All of the channel's tags, oldest first (spec 5.5 rule 3): the existence check for this one,
        // and the names and order of the other tags the entries refer to. At most MaxTagsPerChannel.
        var channelTags = await db.EmoteTags
            .AsNoTracking()
            .Where(t => t.ChannelId == channel.Id)
            .OrderBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .Select(t => new EmoteTagRefDto(t.Id, t.Name))
            .ToListAsync(cancellationToken);
        if (!channelTags.Any(t => t.Id == tagId))
        {
            return new EmoteTagEntriesResult(EmoteTagEntriesStatus.TagNotFound, setId, isActiveSet, [], null);
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
        var entryIds = entries.Select(e => e.SevenTvEmoteId).ToList();

        Dictionary<string, string> currentNames = [];
        if (isActiveSet && entries.Count > 0)
        {
            // One row per (channel, 7TV id) by the unique index, so the dictionary cannot collide.
            currentNames = await db.Emotes
                .Where(e => e.ChannelId == channel.Id && !e.IsArchived && entryIds.Contains(e.SevenTvEmoteId))
                .ToDictionaryAsync(e => e.SevenTvEmoteId, e => e.Name, StringComparer.Ordinal, cancellationToken);
        }

        var placements = setId is null
            ? EntryPlacements.None
            : await LoadEntryPlacementsAsync(channel.Id, tagId, setId, channelTags, entryIds, cancellationToken);

        var dtos = entries
            .Select(e =>
            {
                bool? inSet = isActiveSet ? currentNames.ContainsKey(e.SevenTvEmoteId) : null;
                var own = placements.Own.GetValueOrDefault(e.SevenTvEmoteId);
                return new EmoteTagEntryDto(
                    e.SevenTvEmoteId,
                    e.Alias,
                    e.ImageUrl,
                    inSet,
                    inSet == true ? currentNames[e.SevenTvEmoteId] : null,
                    own is not null,
                    own?.PlacedAtUtc,
                    own?.OperationId,
                    OtherTagsIn(channelTags, tagId, placements.HeldByActive.GetValueOrDefault(e.SevenTvEmoteId)),
                    OtherTagsIn(channelTags, tagId, placements.PlacedByOthers.GetValueOrDefault(e.SevenTvEmoteId)));
            })
            .ToList();
        return new EmoteTagEntriesResult(EmoteTagEntriesStatus.Ok, setId, isActiveSet, dtos, placements.ActivationOperationId);
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

    /// <summary>The activations of <paramref name="tagIds"/> in one set, keyed by tag id (one per tag by the primary key).</summary>
    private async Task<Dictionary<long, ActivationState>> LoadActivationsAsync(
        IReadOnlyCollection<long> tagIds, string emoteSetId, CancellationToken cancellationToken)
    {
        var ids = tagIds.ToArray();
        return await db.EmoteTagActivations
            .AsNoTracking()
            .Where(a => ids.Contains(a.TagId) && a.SevenTvEmoteSetId == emoteSetId)
            .Select(a => new ActivationState(a.TagId, a.ActivatedAtUtc, a.OperationId))
            .ToDictionaryAsync(a => a.TagId, cancellationToken);
    }

    /// <summary>
    /// The placements of <paramref name="tagIds"/> in one set — narrowed to
    /// <paramref name="sevenTvEmoteIds"/> when given — each judged by the read-time rule (spec 5.5 rule 5,
    /// E33 rev. 4): it holds unless the channel has a leave observation for the same emote and set that
    /// is later than the placement's own <see cref="EmoteTagPlacement.RegisteredAtUtc"/>. Valid and
    /// expired rows are both returned; reads filter on <see cref="PlacementState.Holds"/>.
    /// <para>
    /// Two queries over scalar keys, joined in memory (rule 10): the placements (the caller passes the
    /// channel's tag ids, which is what scopes them to the channel); the latest observation of their
    /// emote ids in that channel and set (<see cref="EmoteSetLeaveObservations.LoadLatestAsync"/>, shared
    /// with the sync's post-check).
    /// </para>
    /// <para>
    /// The observation is compared per channel and set: one recorded for another set, or another
    /// channel, says nothing about this placement. The operation row is deliberately not consulted:
    /// operations cascade with their tag, and a transferred placement points at the removal operation
    /// of the tag it came from, so that row can legitimately be gone while the placement still holds.
    /// </para>
    /// </summary>
    private async Task<List<PlacementState>> LoadPlacementStatesAsync(
        string channelId,
        string emoteSetId,
        IReadOnlyCollection<long> tagIds,
        IReadOnlyCollection<string>? sevenTvEmoteIds,
        CancellationToken cancellationToken)
    {
        var tagIdArray = tagIds.ToArray();
        var query = db.EmoteTagPlacements
            .AsNoTracking()
            .Where(p => tagIdArray.Contains(p.TagId) && p.SevenTvEmoteSetId == emoteSetId);
        if (sevenTvEmoteIds is not null)
        {
            var emoteIdArray = sevenTvEmoteIds.ToArray();
            query = query.Where(p => emoteIdArray.Contains(p.SevenTvEmoteId));
        }

        var placements = await query
            .Select(p => new { p.TagId, p.SevenTvEmoteId, p.PlacedAtUtc, p.OperationId, p.RegisteredAtUtc })
            .ToListAsync(cancellationToken);
        if (placements.Count == 0)
        {
            return [];
        }

        var observedAt = await EmoteSetLeaveObservations.LoadLatestAsync(
            db, channelId, emoteSetId, placements.Select(p => p.SevenTvEmoteId).ToList(), cancellationToken);

        return placements
            .Select(p => new PlacementState(
                p.TagId,
                p.SevenTvEmoteId,
                p.PlacedAtUtc,
                p.OperationId,
                p.RegisteredAtUtc,
                Holds(p.RegisteredAtUtc, observedAt.TryGetValue(p.SevenTvEmoteId, out var observed) ? observed : null)))
            .ToList();
    }

    /// <summary>
    /// The set-related part of the entry read: this tag's activation and valid placements, and per
    /// emote the other tags that hold it (active in the set with an entry for it) or have a valid
    /// placement of it.
    /// </summary>
    private async Task<EntryPlacements> LoadEntryPlacementsAsync(
        string channelId,
        long tagId,
        string emoteSetId,
        IReadOnlyList<EmoteTagRefDto> channelTags,
        IReadOnlyList<string> entryIds,
        CancellationToken cancellationToken)
    {
        var channelTagIds = channelTags.Select(t => t.Id).ToList();
        var activations = await LoadActivationsAsync(channelTagIds, emoteSetId, cancellationToken);
        var activationOperationId = activations.GetValueOrDefault(tagId)?.OperationId;
        if (entryIds.Count == 0)
        {
            return EntryPlacements.None with { ActivationOperationId = activationOperationId };
        }

        var heldByActive = await LoadActiveHoldersAsync(activations.Keys, tagId, entryIds, cancellationToken);

        var valid = (await LoadPlacementStatesAsync(channelId, emoteSetId, channelTagIds, entryIds, cancellationToken))
            .Where(p => p.Holds)
            .ToList();
        // One placement per (tag, emote, set) by the primary key, so this tag's dictionary cannot collide.
        var own = valid
            .Where(p => p.TagId == tagId)
            .ToDictionary(p => p.SevenTvEmoteId, StringComparer.Ordinal);
        var placedByOthers = GroupTagIdsByEmote(valid.Where(p => p.TagId != tagId).Select(p => (p.SevenTvEmoteId, p.TagId)));

        return new EntryPlacements(activationOperationId, own, heldByActive, placedByOthers);
    }

    /// <summary>
    /// Per emote among <paramref name="sevenTvEmoteIds"/>, the tags among <paramref name="activeTagIds"/>
    /// (other than <paramref name="excludeTagId"/>) that have an entry for it — the tags that still need
    /// the emote in the set their activations were loaded for (<see cref="LoadActivationsAsync"/>).
    /// Independent of placements: an active tag that merely has an entry for the emote still holds it.
    /// One query; the caller orders the result by <c>OtherTagsIn</c> (oldest first, spec 5.5 rule 3),
    /// so the first tag is also the transfer target of a removal report.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlySet<long>>> LoadActiveHoldersAsync(
        IEnumerable<long> activeTagIds,
        long excludeTagId,
        IReadOnlyCollection<string> sevenTvEmoteIds,
        CancellationToken cancellationToken)
    {
        var holderTagIds = activeTagIds.Where(id => id != excludeTagId).ToArray();
        if (holderTagIds.Length == 0 || sevenTvEmoteIds.Count == 0)
        {
            return EmptyTagIdsByEmote;
        }

        var ids = sevenTvEmoteIds.ToArray();
        var holderEntries = await db.EmoteTagEntries
            .AsNoTracking()
            .Where(e => holderTagIds.Contains(e.TagId) && ids.Contains(e.SevenTvEmoteId))
            .Select(e => new { e.TagId, e.SevenTvEmoteId })
            .ToListAsync(cancellationToken);
        return GroupTagIdsByEmote(holderEntries.Select(e => (e.SevenTvEmoteId, e.TagId)));
    }

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

    /// <summary>
    /// The read-time rule for one emote in one set: a placement anchored at
    /// <paramref name="registeredAtUtc"/> (or a play-in registered then) holds when no leave observation
    /// is later than the anchor. An observation at exactly the anchor does not expire it — only a leave
    /// seen after the run was registered can be one the run did not cause. A missing observation
    /// holds. Both stamps are app-clock UTC compared after their round trip through Postgres, i.e.
    /// truncated to microseconds. Shared by the reads and the play-in report's stale check.
    /// </summary>
    private static bool Holds(DateTime registeredAtUtc, DateTime? observedAtUtc) =>
        observedAtUtc is not { } observed || observed <= registeredAtUtc;

    private static IReadOnlyDictionary<string, IReadOnlySet<long>> GroupTagIdsByEmote(
        IEnumerable<(string SevenTvEmoteId, long TagId)> pairs) =>
        pairs
            .GroupBy(p => p.SevenTvEmoteId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<long>)g.Select(p => p.TagId).ToHashSet(),
                StringComparer.Ordinal);

    /// <summary>
    /// The other tags among <paramref name="tagIds"/>, in the order of <paramref name="channelTags"/>
    /// (oldest first, spec 5.5 rule 3).
    /// </summary>
    private static List<EmoteTagRefDto> OtherTagsIn(IReadOnlyList<EmoteTagRefDto> channelTags, long tagId, IReadOnlySet<long>? tagIds) =>
        tagIds is null ? [] : channelTags.Where(t => t.Id != tagId && tagIds.Contains(t.Id)).ToList();

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

    private sealed record ActivationState(long TagId, DateTime ActivatedAtUtc, Guid OperationId);

    /// <param name="OperationId">Provenance and snapshot revision — the operation that last wrote the placement; not the validity anchor.</param>
    /// <param name="RegisteredAtUtc">The validity anchor the verdict was computed against.</param>
    /// <param name="Holds">The read-time rule's verdict; an expired placement still exists until a play-in or sweep replaces it.</param>
    private sealed record PlacementState(
        long TagId, string SevenTvEmoteId, DateTime PlacedAtUtc, Guid OperationId, DateTime RegisteredAtUtc, bool Holds);

    private sealed record EntryPlacements(
        Guid? ActivationOperationId,
        IReadOnlyDictionary<string, PlacementState> Own,
        IReadOnlyDictionary<string, IReadOnlySet<long>> HeldByActive,
        IReadOnlyDictionary<string, IReadOnlySet<long>> PlacedByOthers)
    {
        public static EntryPlacements None { get; } = new(
            null,
            ReadOnlyDictionary<string, PlacementState>.Empty,
            EmptyTagIdsByEmote,
            EmptyTagIdsByEmote);
    }
}
