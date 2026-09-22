using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class EmoteService(AppDbContext db, ILogger<EmoteService> logger) : IEmoteService
{
    public async Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default)
    {
        // E3: measures how often the legacy body form is still in use, so retiring it (Folge-Issue 1,
        // 14 days after deploy at the earliest) is a decision the log can answer, not a guess.
        logger.LogInformation("sync-deleted: legacy body form {EmoteIds} used", emoteIds);

        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new SyncDeletedResultDto(0, emoteIds, 0);
        }

        // Already-archived rows are matched on purpose: with the EventAPI live sync enabled, the
        // worker usually archives the emote off the 7TV dispatch before this bookkeeping call
        // arrives. The goal state is reached either way, so both count as archived — reporting
        // them as "not found" made every successful delete look like a failed sync in the UI.
        var emotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && emoteIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        var newlyArchived = emotes.Where(e => !e.IsArchived).ToList();
        var now = DateTime.UtcNow;
        foreach (var emote in newlyArchived)
        {
            emote.IsArchived = true;
            // Only for the newly archived: a row the live sync already archived keeps the earlier
            // (more accurate) date — this call is bookkeeping that may arrive minutes later.
            emote.ArchivedAt = now;
            emote.LastSyncedAt = now;
        }

        // Audited on the goal-state count, not on newlyArchived: the user's delete on 7TV happened
        // either way, and with the live sync usually winning the race, gating on "this call changed
        // rows" left most real deletes without a paper trail. A retried report can write a second
        // row — the log records the reports, and a duplicate beats a gap. Only the live event stays
        // tied to an actual state change (see the endpoint).
        if (emotes.Count > 0)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncDeleted,
                channelName: normalized,
                details: new { emoteCount = emotes.Count });
        }

        await db.SaveChangesAsync(cancellationToken);

        var foundIds = emotes.Select(e => e.Id).ToHashSet();
        var notFoundIds = emoteIds.Where(id => !foundIds.Contains(id)).ToList();

        return new SyncDeletedResultDto(emotes.Count, notFoundIds, newlyArchived.Count);
    }

    public async Task<SyncRestoredResultDto> MarkRestoredAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default)
    {
        // Mirror of MarkDeletedAsync's legacy-form log line (E3).
        logger.LogInformation("sync-restored: legacy body form {EmoteIds} used", emoteIds);

        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new SyncRestoredResultDto(0, emoteIds, 0);
        }

        // Mirror of MarkDeletedAsync, in the opposite direction: the live sync usually un-archives
        // the emote off the 7TV ADD dispatch before this call arrives, so already-active rows count
        // as restored (goal state reached) instead of landing in NotFoundIds.
        var emotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && emoteIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        var newlyRestored = emotes.Where(e => e.IsArchived).ToList();
        var now = DateTime.UtcNow;
        foreach (var emote in newlyRestored)
        {
            emote.IsArchived = false;
            // Active again, so the archive date is meaningless — same clearing UpsertEmote does.
            emote.ArchivedAt = null;
            emote.LastSyncedAt = now;
        }

        // Same audit semantics as the delete: the restore happened on 7TV regardless of who
        // un-archived the row first.
        if (emotes.Count > 0)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncRestored,
                channelName: normalized,
                details: new { emoteCount = emotes.Count });
        }

        await db.SaveChangesAsync(cancellationToken);

        var foundIds = emotes.Select(e => e.Id).ToHashSet();
        var notFoundIds = emoteIds.Where(id => !foundIds.Contains(id)).ToList();

        return new SyncRestoredResultDto(emotes.Count, notFoundIds, newlyRestored.Count);
    }

    public async Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, string emoteSetId, IReadOnlyList<string> sevenTvEmoteIds, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);
        var dedupedIds = sevenTvEmoteIds.Distinct(StringComparer.Ordinal).ToList();

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new SyncDeletedResultDto(0, dedupedIds, 0, TargetIsActiveSetOfChannel: false);
        }

        var targetIsActiveSetOfChannel = string.Equals(emoteSetId, channel.ActiveEmoteSetId, StringComparison.Ordinal);
        if (!targetIsActiveSetOfChannel)
        {
            // Spec 6.6: a non-active set has no Emote row to match against here — the active set is
            // the only one this database ever archived rows under — so this is paper-only bookkeeping.
            // Written unconditionally (unlike the active-set branch below), because there is no
            // per-id match to gate it on: the report itself, not a row change, is what is recorded.
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncDeleted,
                channelName: normalized,
                targetType: "emoteSet",
                targetId: emoteSetId,
                details: new { emoteCount = dedupedIds.Count, emoteSetId, targetIsActiveSetOfChannel = false });

            await db.SaveChangesAsync(cancellationToken);

            return new SyncDeletedResultDto(0, [], 0, TargetIsActiveSetOfChannel: false);
        }

        // Matches by (ChannelId, SevenTvEmoteId) instead of Emote.Id — the unique index on that pair
        // (AppDbContext.cs:31) gives the same precision the Guid match had, without requiring the
        // caller to know an internal id for a row it may never have seen (a live-only member has none).
        var emotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && dedupedIds.Contains(e.SevenTvEmoteId))
            .ToListAsync(cancellationToken);

        var newlyArchived = emotes.Where(e => !e.IsArchived).ToList();
        var now = DateTime.UtcNow;
        foreach (var emote in newlyArchived)
        {
            emote.IsArchived = true;
            emote.ArchivedAt = now;
            emote.LastSyncedAt = now;
        }

        if (emotes.Count > 0)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncDeleted,
                channelName: normalized,
                targetType: "emoteSet",
                targetId: emoteSetId,
                details: new { emoteCount = emotes.Count, emoteSetId, targetIsActiveSetOfChannel = true });
        }

        await db.SaveChangesAsync(cancellationToken);

        var foundSevenTvIds = emotes.Select(e => e.SevenTvEmoteId).ToHashSet(StringComparer.Ordinal);
        var notFoundIds = dedupedIds.Where(id => !foundSevenTvIds.Contains(id)).ToList();

        return new SyncDeletedResultDto(emotes.Count, notFoundIds, newlyArchived.Count, TargetIsActiveSetOfChannel: true);
    }

    public async Task<SyncRestoredResultDto> MarkRestoredAsync(string channelName, string emoteSetId, IReadOnlyList<string> sevenTvEmoteIds, AuditActor actor, CancellationToken cancellationToken = default)
    {
        // Mirror of the set-scoped MarkDeletedAsync above, in the restore direction.
        var normalized = ChannelName.Normalize(channelName);
        var dedupedIds = sevenTvEmoteIds.Distinct(StringComparer.Ordinal).ToList();

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new SyncRestoredResultDto(0, dedupedIds, 0, TargetIsActiveSetOfChannel: false);
        }

        var targetIsActiveSetOfChannel = string.Equals(emoteSetId, channel.ActiveEmoteSetId, StringComparison.Ordinal);
        if (!targetIsActiveSetOfChannel)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncRestored,
                channelName: normalized,
                targetType: "emoteSet",
                targetId: emoteSetId,
                details: new { emoteCount = dedupedIds.Count, emoteSetId, targetIsActiveSetOfChannel = false });

            await db.SaveChangesAsync(cancellationToken);

            return new SyncRestoredResultDto(0, [], 0, TargetIsActiveSetOfChannel: false);
        }

        var emotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && dedupedIds.Contains(e.SevenTvEmoteId))
            .ToListAsync(cancellationToken);

        var newlyRestored = emotes.Where(e => e.IsArchived).ToList();
        var now = DateTime.UtcNow;
        foreach (var emote in newlyRestored)
        {
            emote.IsArchived = false;
            emote.ArchivedAt = null;
            emote.LastSyncedAt = now;
        }

        if (emotes.Count > 0)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncRestored,
                channelName: normalized,
                targetType: "emoteSet",
                targetId: emoteSetId,
                details: new { emoteCount = emotes.Count, emoteSetId, targetIsActiveSetOfChannel = true });
        }

        await db.SaveChangesAsync(cancellationToken);

        var foundSevenTvIds = emotes.Select(e => e.SevenTvEmoteId).ToHashSet(StringComparer.Ordinal);
        var notFoundIds = dedupedIds.Where(id => !foundSevenTvIds.Contains(id)).ToList();

        return new SyncRestoredResultDto(emotes.Count, notFoundIds, newlyRestored.Count, TargetIsActiveSetOfChannel: true);
    }

    public async Task<bool> MarkImportedAsync(
        string channelName, IReadOnlyList<string> sevenTvEmoteIds, string? sourceChannelName, string sourceKind,
        string? leaderboardSort, AuditActor actor, string? targetEmoteSetId = null, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return false;
        }

        // No db.Emotes query here on purpose (R10/R9 in the import plan): an import creates or
        // un-archives nothing, so there is nothing to match the reported ids against — the target
        // channel's own resync populates the rows afterwards.
        var normalizedSourceChannelName = sourceChannelName is null ? null : ChannelName.Normalize(sourceChannelName);
        // Deduplicated ordinally, the same comparison the import's name-collision check uses (R4):
        // a client that reported the same 7TV id twice did not import it twice.
        var emoteCount = sevenTvEmoteIds.Distinct(StringComparer.Ordinal).Count();

        // E5/6.7: targetEmoteSetId stays optional forever, so targetIsActiveSetOfChannel is a
        // three-valued fact, not a bool — null means "no set was reported", not "not active". The
        // comparison is taken at write time against the channel row loaded moments ago, deliberately
        // never against a live 7TV read: this call is bookkeeping after the mutation already
        // happened, not a place to add a second source of truth.
        var targetIsActiveSetOfChannel = targetEmoteSetId is null
            ? (bool?)null
            : string.Equals(targetEmoteSetId, channel.ActiveEmoteSetId, StringComparison.Ordinal);

        // leaderboardSort is written verbatim — it already passed EmoteEndpoints' allowlist check
        // and is language-neutral by design (E9, leaderboard-import spec), the same wire code
        // SevenTvLeaderboardSortWireCode uses. AuditLogQueryService.ProjectDetail reads it back
        // under the same property name, alongside the three targetEmoteSetId/targetIsActiveSetOfChannel
        // fields it now also learns to read.
        db.AddAuditEntry(
            actor,
            AuditActions.EmotesSyncImported,
            channelName: normalized,
            // TargetType/TargetId mirror the set-centric endpoint's own entry (6.7) whenever a set is
            // actually known here — null/null when targetEmoteSetId was not reported, exactly like
            // the JSON detail below.
            targetType: targetEmoteSetId is null ? null : "emoteSet",
            targetId: targetEmoteSetId,
            details: new
            {
                emoteCount,
                sourceChannelName = normalizedSourceChannelName,
                sourceKind,
                leaderboardSort,
                targetEmoteSetId,
                targetIsActiveSetOfChannel,
            });

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task MarkImportedToSetAsync(
        string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, IReadOnlyList<string> sevenTvEmoteIds,
        string? sourceChannelName, string sourceKind, string? leaderboardSort, AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        // Same dedup/normalization discipline as MarkImportedAsync — no Channel row to load here at
        // all (the target may not be a tracked channel, or a channel we track), which is exactly why
        // this is a separate method rather than an overload with a nullable channelName.
        var normalizedSourceChannelName = sourceChannelName is null ? null : ChannelName.Normalize(sourceChannelName);
        var emoteCount = sevenTvEmoteIds.Distinct(StringComparer.Ordinal).Count();

        db.AddAuditEntry(
            actor,
            AuditActions.EmotesSyncImported,
            // ChannelName = null is deliberate (6.7): the row surfaces in the global admin log, not
            // a channel's own activity feed — there may be no channel behind the target set at all.
            channelName: null,
            targetType: "emoteSet",
            targetId: emoteSetId,
            details: new
            {
                emoteCount,
                sourceChannelName = normalizedSourceChannelName,
                sourceKind,
                leaderboardSort,
                targetEmoteSetId = emoteSetId,
                targetOwnerSevenTvUserId = ownerSevenTvUserId,
                targetOwnerTwitchLogin = ownerTwitchLogin,
            });

        await db.SaveChangesAsync(cancellationToken);
    }
}
