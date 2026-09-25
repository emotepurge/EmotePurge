using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class EmoteService(AppDbContext db, ILogger<EmoteService> logger, IExcludedChannelFilter excludedChannelFilter) : IEmoteService
{
    public async Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default)
    {
        // E4: measures how often the legacy body form is still in use, so retiring it (Folge-Issue 1,
        // 14 days after deploy at the earliest) is a decision the log can answer, not a guess.
        logger.LogInformation("sync-deleted: legacy body form {EmoteIds} used", emoteIds);

        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new SyncDeletedResultDto(0, emoteIds);
        }

        // H4/spec 5.6: this form carries no set, so blindly archiving a Guid match here could hit a
        // row of a set the browser switched away from without ever telling us. It therefore never
        // touches IsArchived/ArchivedAt/LastSyncedAt any more — it only counts which of the reported
        // ids are rows of this channel; the endpoint's own guarded resync (stage 7) is what actually
        // reconciles the channel against 7TV afterwards.
        var matchedIds = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && emoteIds.Contains(e.Id))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        // E24: the *found* count, not a changed one — there is none any more — so an old open tab
        // still reads this report as succeeded instead of a false partial. A retried report can write
        // a second entry, same as before: the log records the reports, and a duplicate beats a gap.
        if (matchedIds.Count > 0)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncDeleted,
                channelName: normalized,
                details: new { emoteCount = matchedIds.Count, legacyBodyForm = true });
        }

        await db.SaveChangesAsync(cancellationToken);

        var foundIds = matchedIds.ToHashSet();
        var notFoundIds = emoteIds.Where(id => !foundIds.Contains(id)).ToList();

        return new SyncDeletedResultDto(matchedIds.Count, notFoundIds);
    }

    public async Task<SyncRestoredResultDto> MarkRestoredAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default)
    {
        // Mirror of MarkDeletedAsync's legacy-form log line (E4).
        logger.LogInformation("sync-restored: legacy body form {EmoteIds} used", emoteIds);

        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return new SyncRestoredResultDto(0, emoteIds);
        }

        // Mirror of MarkDeletedAsync, in the opposite direction: no row is touched any more either.
        var matchedIds = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && emoteIds.Contains(e.Id))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        if (matchedIds.Count > 0)
        {
            db.AddAuditEntry(
                actor,
                AuditActions.EmotesSyncRestored,
                channelName: normalized,
                details: new { emoteCount = matchedIds.Count, legacyBodyForm = true });
        }

        await db.SaveChangesAsync(cancellationToken);

        var foundIds = matchedIds.ToHashSet();
        var notFoundIds = emoteIds.Where(id => !foundIds.Contains(id)).ToList();

        return new SyncRestoredResultDto(matchedIds.Count, notFoundIds);
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

    public async Task<SyncDeletedInSetResultDto> MarkDeletedInSetAsync(
        string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, string ownerTwitchUserId,
        IReadOnlyList<string> sevenTvEmoteIds, string? expectedChannelName, AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        var outcome = await MarkInSetAsync(
            InSetDirection.Delete, emoteSetId, new InSetOwner(ownerSevenTvUserId, ownerTwitchLogin, ownerTwitchUserId),
            sevenTvEmoteIds, expectedChannelName, actor, cancellationToken);

        return new SyncDeletedInSetResultDto(outcome.ReportedCount, outcome.Channels, outcome.UnresolvedChannel);
    }

    public async Task<SyncRestoredInSetResultDto> MarkRestoredInSetAsync(
        string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, string ownerTwitchUserId,
        IReadOnlyList<string> sevenTvEmoteIds, string? expectedChannelName, AuditActor actor,
        CancellationToken cancellationToken = default)
    {
        var outcome = await MarkInSetAsync(
            InSetDirection.Restore, emoteSetId, new InSetOwner(ownerSevenTvUserId, ownerTwitchLogin, ownerTwitchUserId),
            sevenTvEmoteIds, expectedChannelName, actor, cancellationToken);

        return new SyncRestoredInSetResultDto(outcome.ReportedCount, outcome.Channels, outcome.UnresolvedChannel);
    }

    // Both directions of the set-centric report (restore-per-set spec 5.2), step by step; they differ
    // only in the target state of IsArchived and in the audit action.
    private async Task<InSetOutcome> MarkInSetAsync(
        InSetDirection direction, string emoteSetId, InSetOwner owner,
        IReadOnlyList<string> sevenTvEmoteIds, string? expectedChannelName, AuditActor actor, CancellationToken cancellationToken)
    {
        // Step 1: ordinal dedup, the same discipline as MarkImportedAsync — a client that reported an
        // id twice did not delete it twice. Regel 9 for the expected channel.
        var dedupedIds = sevenTvEmoteIds.Distinct(StringComparer.Ordinal).ToList();
        var normalizedExpected = expectedChannelName is null ? null : ChannelName.Normalize(expectedChannelName);
        var archive = direction == InSetDirection.Delete;
        var action = archive ? AuditActions.EmotesSyncDeleted : AuditActions.EmotesSyncRestored;

        // Step 2 (E8): the hit channels. The block list is applied in memory through the same
        // IsExcluded the join path and ListActiveChannelNamesAsync use, rather than as a second copy
        // of the rule in SQL; the ordering is ordinal in memory too, so it cannot follow the
        // database collation.
        var candidates = await db.Channels
            .AsNoTracking()
            .Where(c => c.IsBotActive && c.ActiveEmoteSetId == emoteSetId)
            .Select(c => new { c.Id, c.ChannelName, c.TwitchChannelId })
            .ToListAsync(cancellationToken);
        var hits = candidates
            .Where(c => !excludedChannelFilter.IsExcluded(c.TwitchChannelId))
            .OrderBy(c => c.ChannelName, StringComparer.Ordinal)
            .ToList();

        // Step 3 (E18): the expected channel, when it is not a hit. Missing, left or blocked all read
        // notTracked — the block is never revealed. Only an active, unblocked channel with another
        // active set is activeSetDiffers (its stored ActiveEmoteSetId may lag a 7TV set switch, F13).
        // Either way, none of its rows is touched or counted.
        UnresolvedChannelDto? unresolved = null;
        if (normalizedExpected is not null && !hits.Exists(hit => hit.ChannelName == normalizedExpected))
        {
            var expected = await db.LoadChannelReadOnlyAsync(normalizedExpected, cancellationToken);
            var reason = expected is null || !expected.IsBotActive || excludedChannelFilter.IsExcluded(expected.TwitchChannelId)
                ? UnresolvedChannelReasons.NotTracked
                : UnresolvedChannelReasons.ActiveSetDiffers;
            unresolved = new UnresolvedChannelDto(normalizedExpected, reason);
        }

        // Step 3a (addendum N3): the owner's tracked channel, by the owner's Twitch id — exactly the
        // rule of IChannelService.GetActiveByTwitchChannelIdAsync (active row, not on the block
        // list), which is also how the target list derives trackedChannelName, so the entry names the
        // channel the client saw. By id, not login: logins move (#44), the id does not. It only names
        // the paper entry below; no row of it is touched or counted, and a blocked channel is as
        // invisible here as in step 3.
        var ownerChannelName = await ResolveOwnerChannelNameAsync(owner.TwitchUserId, cancellationToken);

        // Step 4: the rows of every hit channel, matched by (ChannelId, SevenTvEmoteId) — one query
        // for all hits, split per channel below. The same goal-state semantics as the channel-bound
        // active branch: every found row counts, only the ones not yet in the target state are
        // written, and an already archived row keeps its earlier, more accurate ArchivedAt.
        var hitIds = hits.Select(hit => hit.Id).ToList();
        var rows = hitIds.Count == 0
            ? []
            : await db.Emotes
                .Where(e => hitIds.Contains(e.ChannelId) && dedupedIds.Contains(e.SevenTvEmoteId))
                .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var channels = new List<SyncInSetChannelResultDto>(hits.Count);
        var channelEntryWritten = false;
        foreach (var hit in hits)
        {
            var found = rows.Where(e => e.ChannelId == hit.Id).ToList();
            var changed = found.Where(e => e.IsArchived != archive).ToList();
            foreach (var emote in changed)
            {
                emote.IsArchived = archive;
                emote.ArchivedAt = archive ? now : null;
                emote.LastSyncedAt = now;
            }

            // Step 5, channel entries (spec 5.5): byte-identical with the set-scoped active branch, so
            // audit-row renders them as before. Gated on the goal-state count, like every
            // bookkeeping entry: the 7TV mutation happened whoever wrote the row first.
            if (found.Count > 0)
            {
                db.AddAuditEntry(
                    actor,
                    action,
                    channelName: hit.ChannelName,
                    targetType: "emoteSet",
                    targetId: emoteSetId,
                    details: new { emoteCount = found.Count, emoteSetId, targetIsActiveSetOfChannel = true });
                channelEntryWritten = true;
            }

            var foundSevenTvIds = found.Select(e => e.SevenTvEmoteId).ToHashSet(StringComparer.Ordinal);
            channels.Add(new SyncInSetChannelResultDto(
                hit.ChannelName, found.Count, changed.Count, dedupedIds.Where(id => !foundSevenTvIds.Contains(id)).ToList()));
        }

        // Step 5, the paper entry: whenever no channel entry carries the report, or the expected
        // channel was missed — never a successful report without a trail, never a missed channel
        // without one (#224). With an owner channel it names that channel and says
        // targetIsActiveSetOfChannel: false (the pre-#253 form, shown in the channel's audit view);
        // without one it has no channel and the owner identity instead, which the audit view renders
        // "for <ownerLogin>". Never both groups of fields (spec 5.5 invariant). The unresolved ids
        // are all reported ids, since none of them was matched in that channel.
        if (!channelEntryWritten || unresolved is not null)
        {
            db.AddAuditEntry(
                actor,
                action,
                channelName: ownerChannelName,
                targetType: "emoteSet",
                targetId: emoteSetId,
                details: ownerChannelName is null
                    ? BuildOwnerPaperDetails(dedupedIds, emoteSetId, owner, unresolved)
                    : BuildOwnerChannelPaperDetails(dedupedIds, emoteSetId, unresolved));
        }

        // Step 6: rows and audit entries in one transaction.
        await db.SaveChangesAsync(cancellationToken);

        return new InSetOutcome(dedupedIds.Count, channels, unresolved);
    }

    private async Task<string?> ResolveOwnerChannelNameAsync(string ownerTwitchUserId, CancellationToken cancellationToken)
    {
        var ownerChannel = await db.Channels
            .AsNoTracking()
            .Where(c => c.TwitchChannelId == ownerTwitchUserId && c.IsBotActive)
            .Select(c => new { c.ChannelName, c.TwitchChannelId })
            .FirstOrDefaultAsync(cancellationToken);

        return ownerChannel is null || excludedChannelFilter.IsExcluded(ownerChannel.TwitchChannelId)
            ? null
            : ownerChannel.ChannelName;
    }

    // The paper entry without an owner channel: the owner identity names the target.
    private static object BuildOwnerPaperDetails(
        IReadOnlyList<string> dedupedIds, string emoteSetId, InSetOwner owner, UnresolvedChannelDto? unresolved) =>
        unresolved is null
            ? new
            {
                emoteCount = dedupedIds.Count,
                emoteSetId,
                targetOwnerSevenTvUserId = owner.SevenTvUserId,
                targetOwnerTwitchLogin = owner.TwitchLogin,
            }
            : new
            {
                emoteCount = dedupedIds.Count,
                emoteSetId,
                targetOwnerSevenTvUserId = owner.SevenTvUserId,
                targetOwnerTwitchLogin = owner.TwitchLogin,
                unresolvedChannelName = unresolved.ChannelName,
                unresolvedReason = unresolved.Reason,
                unresolvedSevenTvEmoteIds = dedupedIds,
            };

    // The paper entry with the owner's channel as ChannelName: the channel names the owner, so the
    // targetOwner* fields stay out.
    private static object BuildOwnerChannelPaperDetails(
        IReadOnlyList<string> dedupedIds, string emoteSetId, UnresolvedChannelDto? unresolved) =>
        unresolved is null
            ? new
            {
                emoteCount = dedupedIds.Count,
                emoteSetId,
                targetIsActiveSetOfChannel = false,
            }
            : new
            {
                emoteCount = dedupedIds.Count,
                emoteSetId,
                targetIsActiveSetOfChannel = false,
                unresolvedChannelName = unresolved.ChannelName,
                unresolvedReason = unresolved.Reason,
                unresolvedSevenTvEmoteIds = dedupedIds,
            };

    private enum InSetDirection
    {
        Delete,
        Restore,
    }

    private sealed record InSetOwner(string SevenTvUserId, string TwitchLogin, string TwitchUserId);

    private sealed record InSetOutcome(int ReportedCount, IReadOnlyList<SyncInSetChannelResultDto> Channels, UnresolvedChannelDto? UnresolvedChannel);
}
