using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class EmoteService(AppDbContext db) : IEmoteService
{
    public async Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default)
    {
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
