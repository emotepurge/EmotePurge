using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class EmoteService(
    AppDbContext db,
    ILogger<EmoteService> logger,
    IExcludedChannelFilter excludedChannelFilter,
    IBroadcasterChannelLockService broadcasterChannelLocks,
    SlotReconcileOptions? slotReconcile = null) : IEmoteService
{
    // Plan #372 2.0 (E26) and plan 376 R3/R6: with the flag on, the set-centric report locks its hit
    // channels, then the emote rows, reads behind the locks and applies slot entries; off, it keeps
    // the order and the semantics it had before the slot work and ignores entries. Optional like in
    // SevenTvSyncService: no options means off.
    private readonly bool slotReconcileEnabled = slotReconcile?.Enabled ?? false;

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
        IReadOnlyList<SyncInSetEntryDto>? entries = null, CancellationToken cancellationToken = default)
    {
        var outcome = await MarkInSetAsync(
            new InSetReport(
                InSetDirection.Delete, emoteSetId, new InSetOwner(ownerSevenTvUserId, ownerTwitchLogin, ownerTwitchUserId),
                sevenTvEmoteIds, entries ?? [], expectedChannelName, actor),
            cancellationToken);

        return new SyncDeletedInSetResultDto(outcome.ReportedCount, outcome.Channels, outcome.UnresolvedChannel, outcome.Rejection);
    }

    public async Task<SyncRestoredInSetResultDto> MarkRestoredInSetAsync(
        string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, string ownerTwitchUserId,
        IReadOnlyList<string> sevenTvEmoteIds, string? expectedChannelName, AuditActor actor,
        IReadOnlyList<SyncInSetEntryDto>? entries = null, CancellationToken cancellationToken = default)
    {
        var outcome = await MarkInSetAsync(
            new InSetReport(
                InSetDirection.Restore, emoteSetId, new InSetOwner(ownerSevenTvUserId, ownerTwitchLogin, ownerTwitchUserId),
                sevenTvEmoteIds, entries ?? [], expectedChannelName, actor),
            cancellationToken);

        return new SyncRestoredInSetResultDto(outcome.ReportedCount, outcome.Channels, outcome.UnresolvedChannel, outcome.Rejection);
    }

    // Both directions of the set-centric report (restore-per-set spec 5.2); they differ only in the
    // target state of IsArchived and in the audit action. This wrapper is the recovery of plan #372
    // 2.9 (E27): one attempt runs from the first read to the commit, and a failed attempt — chosen as
    // a deadlock victim (40P01, both flag states) or, with the flag on, finding a hit channel
    // deleted at its channel lock — rolls back, clears the change tracker and runs once more from the
    // first read. One retry in total, whichever the reason; the second failure propagates (500), as
    // every database failure here did before. The attempt's audit entries are dropped with the
    // cleared tracker, so they exist exactly once; nothing after the commit runs twice.
    private async Task<InSetOutcome> MarkInSetAsync(InSetReport report, CancellationToken cancellationToken)
    {
        var retried = false;
        while (true)
        {
            try
            {
                return slotReconcileEnabled
                    ? await MarkInSetLockedOnceAsync(report, cancellationToken)
                    : await MarkInSetUnlockedOnceAsync(report, cancellationToken);
            }
            catch (HitChannelVanishedException) when (!retried)
            {
                retried = true;
                logger.LogWarning(
                    "In-set report for emote set {EmoteSetId}: a channel it writes was deleted meanwhile — retrying once without it.",
                    report.EmoteSetId);
            }
            catch (Exception ex) when (!retried && DatabaseErrors.IsDeadlock(ex))
            {
                retried = true;
                logger.LogWarning(
                    "In-set report for emote set {EmoteSetId}: the save was chosen as a deadlock victim — retrying once.",
                    report.EmoteSetId);
            }

            // The attempt's transaction has rolled back on dispose; its staged rows and audit entries
            // go with the tracker.
            db.ChangeTracker.Clear();
        }
    }

    // One attempt with the flag off: the order and the semantics from before the slot work, row for
    // row. Entries are ignored (plan 376 R3 a) — the flag-off state writes no slot (plan 372 2.0) — so
    // the report reads, writes and audits exactly what it did without them.
    private async Task<InSetOutcome> MarkInSetUnlockedOnceAsync(InSetReport report, CancellationToken cancellationToken)
    {
        // Step 1: ordinal dedup, the same discipline as MarkImportedAsync — a client that reported an
        // id twice did not delete it twice. Regel 9 for the expected channel.
        var dedupedIds = report.SevenTvEmoteIds.Distinct(StringComparer.Ordinal).ToList();
        var archive = report.Direction == InSetDirection.Delete;
        var action = archive ? AuditActions.EmotesSyncDeleted : AuditActions.EmotesSyncRestored;

        // Step 2 (E8): the hit channels, read without a lock.
        var hits = await LoadHitsAsync(report.EmoteSetId, cancellationToken);

        // Step 3 (E18): the expected channel, when it is not a hit.
        var unresolved = await ResolveUnresolvedAsync(report.NormalizedExpectedChannelName, hits, cancellationToken);

        // Step 4: the rows of every hit channel, matched by (ChannelId, SevenTvEmoteId) — one query
        // for all hits, split per channel below. The same goal-state semantics as the channel-bound
        // active branch: every found row counts, only the ones not yet in the target state are
        // written, and an already archived row keeps its earlier, more accurate ArchivedAt.
        var hitIds = hits.ConvertAll(hit => hit.Id);
        var rows = hitIds.Count == 0
            ? []
            : await db.Emotes
                .Where(e => hitIds.Contains(e.ChannelId) && dedupedIds.Contains(e.SevenTvEmoteId))
                .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var channels = new List<SyncInSetChannelResultDto>(hits.Count);
        var channelEntryWritten = false;
        var leaves = new List<(string ChannelId, List<string> SevenTvEmoteIds)>();
        foreach (var hit in hits)
        {
            var found = rows.Where(e => e.ChannelId == hit.Id).ToList();
            var changed = found.Where(e => e.IsArchived != archive).ToList();
            foreach (var emote in changed)
            {
                if (archive)
                {
                    ArchiveEmote(emote, now);
                }
                else
                {
                    UnarchiveEmote(emote, now);
                }
            }

            // Spec 5.5 rule 5 (c): our own set-centric delete report is always a credible leave, for
            // every found row of the hit channel — an already archived one too, because the report is
            // what we know, whatever an earlier sync decided. Ids without a row have no channel fact.
            if (archive && found.Count > 0)
            {
                leaves.Add((hit.Id, found.ConvertAll(e => e.SevenTvEmoteId)));
            }

            if (found.Count > 0)
            {
                AddChannelEntry(report, action, hit, found.Count, entryCount: null);
                channelEntryWritten = true;
            }

            channels.Add(new SyncInSetChannelResultDto(hit.ChannelName, found.Count, changed.Count, NotFoundIn(found, dedupedIds)));
        }

        await AddPaperEntryIfNeededAsync(report, action, dedupedIds, hits, unresolved, channelEntryWritten, entryCount: null, cancellationToken);

        // Step 6: rows, leave observations and audit entries in one transaction. Explicit, because the
        // observation upsert is raw SQL outside SaveChangesAsync's implicit transaction. Neither this
        // report nor the sync touches a tag table (spec 5.5 rule 6). Lock order, flag off (as before
        // the slot work): observation rows, then the emote rows in the save — the order of the
        // worker's sync with the flag off. A channel purged meanwhile fails the upsert with 23503 and
        // surfaces as a 500, like any database failure here; the browser retries the report.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (channelId, leftIds) in leaves)
        {
            await EmoteSetLeaveObservations.RecordAsync(db, channelId, report.EmoteSetId, leftIds, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new InSetOutcome(dedupedIds.Count, channels, unresolved, Rejection: null);
    }

    // One attempt with the flag on (plan 376 4.3, R6; plan 372 E25): lock first, then read. The order
    // is the slot writers' one — channel rows, emote rows, leave rows, then the save (which updates
    // emote and slot rows already covered by the emote lock) — the order of the slot-aware sync (Z1),
    // the EventAPI delta (Z8) and the admin purge, whose cascading DELETE takes the channel row before
    // any emote row (Z6): whichever comes first, the other waits at the channel row.
    //
    // No coverage check like the sync's E24 is needed: the rows are read by the ids the emote lock
    // returned, so nothing this attempt writes is unlocked. A row another writer creates meanwhile
    // without the channel lock (a set-session ballot through ArchivedEmoteRowUpsert: archived, without
    // slots) is simply not part of this report, as if it had been created after it.
    private async Task<InSetOutcome> MarkInSetLockedOnceAsync(InSetReport report, CancellationToken cancellationToken)
    {
        var dedupedIds = report.SevenTvEmoteIds.Distinct(StringComparer.Ordinal).ToList();
        var archive = report.Direction == InSetDirection.Delete;
        var action = archive ? AuditActions.EmotesSyncDeleted : AuditActions.EmotesSyncRestored;
        // R3 b: an id with at least one entry runs the slot semantics only, whatever its entries hold.
        var entriesById = report.Entries.ToLookup(e => e.SevenTvEmoteId, StringComparer.Ordinal);
        int? reportEntryCount = report.Entries.Count > 0 ? report.Entries.Count : null;

        // Step 1: the hit candidates, read without a lock (block list, broadcaster lock, ordinal order).
        var candidates = await LoadHitsAsync(report.EmoteSetId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Step 2: the rows of every candidate channel, one statement sorted by id — never channel by
        // channel in hit order, which follows the names and would not agree with a second report over
        // other channels. A candidate missing from the lock was deleted (a purge committed): the
        // attempt is stale, MarkInSetAsync runs it once more, and there it is no candidate any more.
        var lockedChannelIds = await db.LockChannelsByIdsAsync(candidates.ConvertAll(c => c.Id), cancellationToken);
        if (lockedChannelIds.Count < candidates.Count)
        {
            throw new HitChannelVanishedException();
        }

        // Step 3: behind the lock, the channels again. One deactivated or switched to another set since
        // step 1 is no hit (and no retry): that is what the lock is for — before child 6 such a
        // channel was still written. Then the expected channel and the origin of every entry.
        var hits = await ReReadHitsAsync(candidates, report.EmoteSetId, cancellationToken);
        var unresolved = await ResolveUnresolvedAsync(report.NormalizedExpectedChannelName, hits, cancellationToken);
        var slotOwners = await LoadSlotOwnersAsync(report.Entries, cancellationToken);
        if (FindSlotOfAnotherEmote(report.Entries, slotOwners) is { } foreign)
        {
            // Arbitration on B3, point 2: a contract violation, decided before any write; the
            // transaction rolls back on dispose and no audit entry is staged yet.
            logger.LogWarning(
                "In-set report for emote set {EmoteSetId} refused: slot {SlotId} belongs to another 7TV emote than its entry {SevenTvEmoteId}.",
                report.EmoteSetId, foreign.SlotId, foreign.SevenTvEmoteId);
            return new InSetOutcome(dedupedIds.Count, [], null, SyncInSetRejection.SlotOfAnotherEmote);
        }

        // Step 4: the emote rows of the hits for the reported ids, locked in one sorted statement, then
        // read with all their slots behind the lock, from the database.
        var rows = await LockAndLoadRowsAsync(hits, dedupedIds, cancellationToken);

        // Step 5: apply, per hit channel and reported id.
        var now = DateTime.UtcNow;
        var channels = new List<SyncInSetChannelResultDto>(hits.Count);
        var channelEntryWritten = false;
        var leaves = new List<(string ChannelId, List<string> SevenTvEmoteIds)>();
        foreach (var hit in hits)
        {
            var found = rows.Where(e => e.ChannelId == hit.Id).ToList();
            var changedCount = 0;
            var resolvedEntries = 0;
            var leftIds = new List<string>();
            var onceLeaveCandidates = new List<Emote>();
            foreach (var emote in found)
            {
                var entriesOfId = entriesById[emote.SevenTvEmoteId].ToList();
                var applied = entriesOfId.Count > 0
                    ? ApplyEntries(emote, hit.Id, entriesOfId, slotOwners, archive, now)
                    : ApplyWithoutEntry(emote, archive, now);
                changedCount += applied.Changed ? 1 : 0;
                resolvedEntries += applied.ResolvedEntries;
                if (applied.Leave)
                {
                    leftIds.Add(emote.SevenTvEmoteId);
                }
                else if (applied.LeaveOnceUnobserved)
                {
                    onceLeaveCandidates.Add(emote);
                }
            }

            // B2: the once-leave of a delete that found its slot archived by REST already, written only
            // when no leave was observed for (channel, emote, set) since the emote last entered the set
            // — the sync's b2 check. Read behind the channel lock that every other leave writer of the
            // channel takes first, so the check is race-free; a retry finds the observation and neither
            // writes a second one nor moves its timestamp.
            if (onceLeaveCandidates.Count > 0)
            {
                var latest = await EmoteSetLeaveObservations.LoadLatestAsync(
                    db, hit.Id, report.EmoteSetId, onceLeaveCandidates.ConvertAll(e => e.SevenTvEmoteId), cancellationToken);
                leftIds.AddRange(onceLeaveCandidates.Where(e => !ObservedSinceEntry(e, latest)).Select(e => e.SevenTvEmoteId));
            }

            if (leftIds.Count > 0)
            {
                leaves.Add((hit.Id, leftIds));
            }

            if (found.Count > 0)
            {
                AddChannelEntry(report, action, hit, found.Count, reportEntryCount is null ? null : resolvedEntries);
                channelEntryWritten = true;
            }

            channels.Add(new SyncInSetChannelResultDto(hit.ChannelName, found.Count, changedCount, NotFoundIn(found, dedupedIds)));
        }

        await AddPaperEntryIfNeededAsync(report, action, dedupedIds, hits, unresolved, channelEntryWritten, reportEntryCount, cancellationToken);

        foreach (var (channelId, leftIds) in leaves)
        {
            await EmoteSetLeaveObservations.RecordAsync(db, channelId, report.EmoteSetId, leftIds, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new InSetOutcome(dedupedIds.Count, channels, unresolved, Rejection: null);
    }

    // Step 2 (E8): the hit channels. The block list is applied in memory through the same IsExcluded
    // the join path and ListActiveChannelNamesAsync use, rather than as a second copy of the rule in
    // SQL; the ordering is ordinal in memory too, so it cannot follow the database collation. The
    // broadcaster lock (#245) the same way: a locked channel is not a hit, so none of its rows is
    // archived or restored and no leave observation is written for it. One batch lookup over the
    // candidates' ids, not a query per channel.
    private async Task<List<HitChannel>> LoadHitsAsync(string emoteSetId, CancellationToken cancellationToken)
    {
        var candidates = await db.Channels
            .AsNoTracking()
            .Where(c => c.IsBotActive && c.ActiveEmoteSetId == emoteSetId)
            .Select(c => new { c.Id, c.ChannelName, c.TwitchChannelId })
            .ToListAsync(cancellationToken);
        var lockedIds = await LoadLockedIdsAsync(candidates.Select(c => c.TwitchChannelId), cancellationToken);
        return candidates
            .Where(c => !excludedChannelFilter.IsExcluded(c.TwitchChannelId))
            .Where(c => c.TwitchChannelId is null || !lockedIds.Contains(c.TwitchChannelId))
            .OrderBy(c => c.ChannelName, StringComparer.Ordinal)
            .Select(c => new HitChannel(c.Id, c.ChannelName, c.TwitchChannelId))
            .ToList();
    }

    // Plan 376 4.3 step 3: the locked candidates as the database holds them now. Only IsBotActive and
    // the active set decide again; the block list and the broadcaster lock were decided in step 1 and
    // are not channel columns. The name is the current one, still in ordinal order.
    private async Task<List<HitChannel>> ReReadHitsAsync(List<HitChannel> candidates, string emoteSetId, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var candidateIds = candidates.ConvertAll(c => c.Id);
        var current = await db.Channels
            .AsNoTracking()
            .Where(c => candidateIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ChannelName, c.TwitchChannelId, c.IsBotActive, c.ActiveEmoteSetId })
            .ToListAsync(cancellationToken);
        return current
            .Where(c => c.IsBotActive && c.ActiveEmoteSetId == emoteSetId)
            .OrderBy(c => c.ChannelName, StringComparer.Ordinal)
            .Select(c => new HitChannel(c.Id, c.ChannelName, c.TwitchChannelId))
            .ToList();
    }

    // Step 3 (E18): the expected channel, when it is not a hit. Missing, left, blocked or locked by
    // its broadcaster (#245) all read notTracked — the block is never revealed, and a locked channel
    // looks untracked like a blocked one. Only an active, unblocked, unlocked channel with another
    // active set is activeSetDiffers (its stored ActiveEmoteSetId may lag a 7TV set switch, F13).
    // Either way, none of its rows is touched or counted.
    private async Task<UnresolvedChannelDto?> ResolveUnresolvedAsync(
        string? normalizedExpected, List<HitChannel> hits, CancellationToken cancellationToken)
    {
        if (normalizedExpected is null || hits.Exists(hit => hit.ChannelName == normalizedExpected))
        {
            return null;
        }

        var expected = await db.LoadChannelReadOnlyAsync(normalizedExpected, cancellationToken);
        var reason = expected is null
                     || !expected.IsBotActive
                     || excludedChannelFilter.IsExcluded(expected.TwitchChannelId)
                     || await broadcasterChannelLocks.GetLockedAtUtcAsync(expected.TwitchChannelId, cancellationToken) is not null
            ? UnresolvedChannelReasons.NotTracked
            : UnresolvedChannelReasons.ActiveSetDiffers;
        return new UnresolvedChannelDto(normalizedExpected, reason);
    }

    // Arbitration on B3, point 1: every reported slot id resolved to the channel and the 7TV emote of
    // its emote, in one query. A slot id that is not returned is unknown (its row is gone: purge,
    // self-purge, retention) — no error, its entry has no origin and fans out.
    private async Task<Dictionary<string, SlotOwner>> LoadSlotOwnersAsync(
        IReadOnlyList<SyncInSetEntryDto> entries, CancellationToken cancellationToken)
    {
        var slotIds = entries.Where(e => e.SlotId is not null).Select(e => e.SlotId!).Distinct(StringComparer.Ordinal).ToList();
        if (slotIds.Count == 0)
        {
            return new Dictionary<string, SlotOwner>(StringComparer.Ordinal);
        }

        return await db.EmoteSlots
            .AsNoTracking()
            .Where(s => slotIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Emote.ChannelId, s.Emote.SevenTvEmoteId })
            .ToDictionaryAsync(s => s.Id, s => new SlotOwner(s.ChannelId, s.SevenTvEmoteId), StringComparer.Ordinal, cancellationToken);
    }

    // Plan 376 4.3 step 4. A tracking query hands back instances the context already tracks without
    // refreshing them, so unchanged tracked instances of these rows (a caller that seeded them through
    // the same context) are detached first; a pending change on one of them is a programming error.
    private async Task<List<Emote>> LockAndLoadRowsAsync(
        List<HitChannel> hits, List<string> dedupedIds, CancellationToken cancellationToken)
    {
        var hitIds = hits.ConvertAll(hit => hit.Id);
        if (hitIds.Count == 0 || dedupedIds.Count == 0)
        {
            return [];
        }

        var emoteIds = await db.Emotes
            .AsNoTracking()
            .Where(e => hitIds.Contains(e.ChannelId) && dedupedIds.Contains(e.SevenTvEmoteId))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        var lockedIds = (await db.LockEmotesByIdsAsync(emoteIds, cancellationToken)).ToList();
        if (lockedIds.Count == 0)
        {
            return [];
        }

        DetachTracked(lockedIds.ToHashSet(StringComparer.Ordinal));
        return await db.Emotes
            .Include(e => e.Slots)
            .Where(e => lockedIds.Contains(e.Id))
            .ToListAsync(cancellationToken);
    }

    private void DetachTracked(IReadOnlySet<string> emoteIds)
    {
        var emoteEntries = db.ChangeTracker.Entries<Emote>().Where(e => emoteIds.Contains(e.Entity.Id)).ToList();
        var slotEntries = db.ChangeTracker.Entries<EmoteSlot>().Where(e => emoteIds.Contains(e.Entity.EmoteId)).ToList();
        if (emoteEntries.Any(e => e.State != EntityState.Unchanged) || slotEntries.Any(e => e.State != EntityState.Unchanged))
        {
            throw new InvalidOperationException("The in-set report found pending emote or slot changes before it read its rows.");
        }

        foreach (var entry in slotEntries)
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in emoteEntries)
        {
            entry.State = EntityState.Detached;
        }
    }

    // Step 5, channel entries (spec 5.5): byte-identical with the set-scoped active branch, so
    // audit-row renders them as before. Gated on the goal-state count, like every bookkeeping entry:
    // the 7TV mutation happened whoever wrote the row first. A report with entries (flag on) adds
    // entryCount, the entries that found their slot in this channel (plan 376 4.1).
    private void AddChannelEntry(InSetReport report, string action, HitChannel hit, int foundCount, int? entryCount) =>
        db.AddAuditEntry(
            report.Actor,
            action,
            channelName: hit.ChannelName,
            targetType: "emoteSet",
            targetId: report.EmoteSetId,
            details: WithEntryCount(
                new { emoteCount = foundCount, emoteSetId = report.EmoteSetId, targetIsActiveSetOfChannel = true }, entryCount));

    // Step 5, the paper entry: whenever no channel entry carries the report, or the expected
    // channel was missed — never a successful report without a trail, never a missed channel
    // without one (#224). With an owner channel it names that channel; targetIsActiveSetOfChannel
    // reflects what the database stores for it right now (addendum N3 corrected) — true
    // exactly when the owner channel is itself one of the hit channels, false otherwise. A hard-coded
    // false was wrong two ways: a hit owner channel that matched zero reported rows got no channel
    // entry (the channel entry gates on a found row) but was in fact active, and a shared set whose
    // owner channel is a hit while a second tracked channel lags wrote a channel entry saying true
    // right next to a paper entry — same channel — saying false. Without an owner channel the entry
    // has no channel and the owner identity instead, which the audit view renders "for <ownerLogin>".
    // Never both groups of fields (spec 5.5 invariant). The unresolved ids are all reported ids, since
    // none of them was matched in that channel. A report with entries (flag on) adds entryCount, the
    // number of entries it carried.
    private async Task AddPaperEntryIfNeededAsync(
        InSetReport report, string action, List<string> dedupedIds, List<HitChannel> hits, UnresolvedChannelDto? unresolved,
        bool channelEntryWritten, int? entryCount, CancellationToken cancellationToken)
    {
        if (channelEntryWritten && unresolved is null)
        {
            return;
        }

        // Step 3a (addendum N3): the owner's tracked channel, by the owner's Twitch id — the same
        // rule as ChannelService.GetActiveByTwitchChannelIdAsync (active row, not on the block
        // list, now one shared query), which is also how the target list derives
        // trackedChannelName, so the entry names the channel the client saw. By id, not login:
        // logins move (#44), the id does not. It only names the paper entry; no row of the owner
        // channel is touched or counted here, and a blocked one is as invisible as in step 3.
        var ownerChannel = await db.LoadActiveChannelByTwitchIdReadOnlyAsync(
            report.Owner.TwitchUserId, excludedChannelFilter, broadcasterChannelLocks, cancellationToken);
        var ownerChannelIsActiveHere = ownerChannel is not null && hits.Exists(hit => hit.TwitchChannelId == ownerChannel.TwitchChannelId);
        db.AddAuditEntry(
            report.Actor,
            action,
            channelName: ownerChannel?.ChannelName,
            targetType: "emoteSet",
            targetId: report.EmoteSetId,
            details: WithEntryCount(
                ownerChannel is null
                    ? BuildOwnerPaperDetails(dedupedIds, report.EmoteSetId, report.Owner, unresolved)
                    : BuildOwnerChannelPaperDetails(dedupedIds, report.EmoteSetId, unresolved, ownerChannelIsActiveHere),
                entryCount));
    }

    // The subset of these Twitch ids its broadcaster has locked (#245), in one query.
    private async Task<HashSet<string>> LoadLockedIdsAsync(IEnumerable<string?> twitchChannelIds, CancellationToken cancellationToken)
    {
        var ids = twitchChannelIds.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var locked = await broadcasterChannelLocks.Locks
            .Where(l => ids.Contains(l.TwitchChannelId))
            .Select(l => l.TwitchChannelId)
            .ToListAsync(cancellationToken);
        return locked.ToHashSet(StringComparer.Ordinal);
    }

    // An id without an entry (plan 376 4.2a): the id-level semantics of the flag-off path — a delete
    // archives the emote row and is a leave for every found row, a restore un-archives the emote row
    // only — plus B1: a delete also archives every active own slot of the emote in the same
    // transaction, under the emote lock (unsettled seeds stay as they are, E7). Otherwise a quick undo
    // leaves S1(A, t1) active and authoritative, the next REST with A(t9) creates S2 and archives S1,
    // and the history drops out of the active view; archived, S1 is what D7 resumes on t9.
    private static EmoteApplication ApplyWithoutEntry(Emote emote, bool archive, DateTime now)
    {
        var emoteChanged = false;
        var slotChanged = false;
        if (emote.IsArchived != archive)
        {
            if (archive)
            {
                ArchiveEmote(emote, now);
            }
            else
            {
                UnarchiveEmote(emote, now);
            }

            emoteChanged = true;
        }

        if (archive)
        {
            foreach (var slot in emote.Slots.Where(IsActiveOwnSlot))
            {
                ArchiveSlot(slot, now);
                slotChanged = true;
            }
        }

        return new EmoteApplication(emoteChanged || slotChanged, ResolvedEntries: 0, Leave: archive, LeaveOnceUnobserved: false);
    }

    // An id with entries in one hit channel (plan 376 4.2). Entries without a slot id are skipped
    // (R3 b, no fall-back to the emote level, which would archive a multi-slot emote whole).
    private static EmoteApplication ApplyEntries(
        Emote emote, string channelId, List<SyncInSetEntryDto> entries, Dictionary<string, SlotOwner> slotOwners,
        bool archive, DateTime now)
    {
        var targets = new List<EmoteSlot>();
        var resolvedEntries = 0;
        foreach (var entry in entries.Where(e => e.SlotId is not null))
        {
            if (ResolveSlot(emote, channelId, entry, slotOwners) is not { } slot)
            {
                continue;
            }

            resolvedEntries++;
            if (!targets.Contains(slot))
            {
                targets.Add(slot);
            }
        }

        return archive
            ? DeleteSlots(emote, targets, resolvedEntries, now)
            : RestoreSlots(emote, targets, resolvedEntries, now);
    }

    // Step 1 of plan 376 4.2. The origin of an entry is the channel its slot id belongs to (arbitration
    // on B3): there the slot must still be the reported generation, else the report is stale and a
    // no-op (a seed's null date, a provisional re-date, a D7 re-key all differ). In every other hit
    // channel — and in all of them when the slot id is unknown or belongs to a channel that is no hit —
    // the local slot is the one own authoritative slot with the same date, on a tie the one with the
    // alias as its name (D4); none or still several → nothing here, REST reconciles.
    private static EmoteSlot? ResolveSlot(Emote emote, string channelId, SyncInSetEntryDto entry, Dictionary<string, SlotOwner> slotOwners)
    {
        if (slotOwners.TryGetValue(entry.SlotId!, out var owner) && owner.ChannelId == channelId)
        {
            var own = emote.Slots.FirstOrDefault(s => s.Id == entry.SlotId);
            return own is not null && SameGeneration(own.AddedAt, entry.AddedAt) ? own : null;
        }

        var sameDate = emote.Slots
            .Where(s => !s.SeedUnsettled && s.AddedAtFromRest && SameGeneration(s.AddedAt, entry.AddedAt))
            .ToList();
        if (sameDate.Count > 1)
        {
            sameDate = sameDate.Where(s => string.Equals(s.Name, entry.Alias, StringComparison.Ordinal)).ToList();
        }

        return sameDate.Count == 1 ? sameDate[0] : null;
    }

    // Steps 2 and 4 of plan 376 4.2, delete. An active target is archived. The emote row goes, with
    // its leave, only when this report removed its last active own slot. A target REST archived
    // already is a no-op — except when the emote has no active own slot left, is archived and is no
    // never-active placeholder: then this report is the confirmed leave REST did not record inside its
    // credibility window (B2), written once if none was observed since the last entry. An active own
    // slot under an archived emote (a delta pull before the next tick) does not count as active: the
    // readers ignore it too (plan 376 4.2a, invariant 4.1).
    private static EmoteApplication DeleteSlots(Emote emote, List<EmoteSlot> targets, int resolvedEntries, DateTime now)
    {
        var activeBefore = HasActiveOwnSlot(emote);
        var slotChanged = false;
        var targetArchivedBefore = false;
        foreach (var slot in targets)
        {
            if (slot.IsArchived)
            {
                targetArchivedBefore = true;
                continue;
            }

            ArchiveSlot(slot, now);
            slotChanged = true;
        }

        var emoteChanged = false;
        var leave = false;
        if (activeBefore && !HasActiveOwnSlot(emote))
        {
            ArchiveEmote(emote, now);
            emoteChanged = true;
            leave = true;
        }

        var leaveOnce = !activeBefore && targetArchivedBefore && emote.IsArchived && !IsNeverActivePlaceholder(emote);
        if (slotChanged)
        {
            ApplyDerivedNameAndDate(emote, now);
        }

        return new EmoteApplication(emoteChanged || slotChanged, resolvedEntries, leave, leaveOnce);
    }

    // Steps 3 and 4 of plan 376 4.2, restore. An archived target is reactivated with a provisional date
    // (the next REST reconcile authorizes the new added_at on the same slot id, plan 372 2.3 step 5);
    // its usage rows stay with it. An active target is a no-op — no date reset on a retry or after a
    // D7 resumption. The emote row is un-archived when a slot came back.
    private static EmoteApplication RestoreSlots(Emote emote, List<EmoteSlot> targets, int resolvedEntries, DateTime now)
    {
        var slotChanged = false;
        foreach (var slot in targets.Where(s => s.IsArchived))
        {
            slot.IsArchived = false;
            slot.ArchivedAt = null;
            slot.AddedAt = now;
            slot.AddedAtFromRest = false;
            slot.LastSyncedAt = now;
            slotChanged = true;
        }

        var emoteChanged = false;
        if (slotChanged && emote.IsArchived)
        {
            UnarchiveEmote(emote, now);
            emoteChanged = true;
        }

        if (slotChanged)
        {
            ApplyDerivedNameAndDate(emote, now);
        }

        return new EmoteApplication(emoteChanged || slotChanged, resolvedEntries, Leave: false, LeaveOnceUnobserved: false);
    }

    // D13 through the reconcile's own derivation (plan 376 A11): name and date of the oldest active own
    // slot. An archived emote keeps its last state, and without an active own slot nothing changes.
    private static void ApplyDerivedNameAndDate(Emote emote, DateTime now)
    {
        if (emote.IsArchived
            || EmoteSlotReconcilePlanner.Derive(emote.Slots.Where(s => !s.SeedUnsettled).Select(SlotState.From)) is not { } derived)
        {
            return;
        }

        if (!string.Equals(emote.Name, derived.Name, StringComparison.Ordinal))
        {
            emote.Name = derived.Name;
            emote.LastSyncedAt = now;
        }

        emote.FirstSeenAt = derived.FirstSeenAt;
    }

    private static void ArchiveEmote(Emote emote, DateTime now)
    {
        emote.IsArchived = true;
        emote.ArchivedAt = now;
        emote.LastSyncedAt = now;
        // D37: either way the row is no placeholder. A delete archives a row that was active, so a
        // marker on it can only be stale (left by an image older than the marker). Never set back to
        // true.
        emote.IsPlaceholder = false;
    }

    private static void UnarchiveEmote(Emote emote, DateTime now)
    {
        emote.IsArchived = false;
        emote.ArchivedAt = null;
        emote.LastSyncedAt = now;
        // D37: a restore puts the row into the channel's active set, so it is no placeholder.
        emote.IsPlaceholder = false;
        // E34: a restore is a fresh entry into the set, like the sync's un-archive. An existing leave
        // observation stays: it is history, and the read-time rule compares it against
        // registrations, not against this stamp.
        emote.LastEnteredSetAtUtc = now;
    }

    private static void ArchiveSlot(EmoteSlot slot, DateTime now)
    {
        slot.IsArchived = true;
        slot.ArchivedAt = now;
        slot.LastSyncedAt = now;
    }

    private static bool IsActiveOwnSlot(EmoteSlot slot) => !slot.SeedUnsettled && !slot.IsArchived;

    private static bool HasActiveOwnSlot(Emote emote) => !emote.IsArchived && emote.Slots.Any(IsActiveOwnSlot);

    // The sync's placeholder rule for b2 (D37): an archived row never observed in the active set has
    // nothing to leave.
    private static bool IsNeverActivePlaceholder(Emote emote) => emote.IsPlaceholder && emote.ArchivedAt is null;

    // The sync's b2 check: an observation counts when it is later than the last entry into the set, or
    // when that entry is unknown.
    private static bool ObservedSinceEntry(Emote emote, Dictionary<string, DateTime> latest) =>
        latest.TryGetValue(emote.SevenTvEmoteId, out var observedAt)
        && (emote.LastEnteredSetAtUtc is null || observedAt > emote.LastEnteredSetAtUtc);

    // Plan 376 4.1: the generation is compared in UTC to the microsecond — Postgres stores timestamptz at
    // that precision, 7TV reports milliseconds. A missing date on either side never matches.
    private static bool SameGeneration(DateTime? stored, DateTime? reported) =>
        stored is { } s && reported is { } r && Microseconds(s) == Microseconds(r);

    private static long Microseconds(DateTime value) =>
        (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).Ticks / TimeSpan.TicksPerMicrosecond;

    private static SyncInSetEntryDto? FindSlotOfAnotherEmote(IReadOnlyList<SyncInSetEntryDto> entries, Dictionary<string, SlotOwner> slotOwners) =>
        entries.FirstOrDefault(e =>
            e.SlotId is not null
            && slotOwners.TryGetValue(e.SlotId, out var owner)
            && !string.Equals(owner.SevenTvEmoteId, e.SevenTvEmoteId, StringComparison.Ordinal));

    private static List<string> NotFoundIn(List<Emote> found, List<string> dedupedIds)
    {
        var foundSevenTvIds = found.Select(e => e.SevenTvEmoteId).ToHashSet(StringComparer.Ordinal);
        return dedupedIds.Where(id => !foundSevenTvIds.Contains(id)).ToList();
    }

    // Without entries the details object goes out as it is, so the JSON is byte-identical to before;
    // with them, entryCount is appended as the last field.
    private static object WithEntryCount(object details, int? entryCount)
    {
        if (entryCount is not { } count)
        {
            return details;
        }

        var node = JsonSerializer.SerializeToNode(details)!.AsObject();
        node["entryCount"] = count;
        return node;
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
    // targetOwner* fields stay out. targetIsActiveSetOfChannel is the caller's own read of whether
    // that channel is a hit — never hard-coded here.
    private static object BuildOwnerChannelPaperDetails(
        IReadOnlyList<string> dedupedIds, string emoteSetId, UnresolvedChannelDto? unresolved, bool targetIsActiveSetOfChannel) =>
        unresolved is null
            ? new
            {
                emoteCount = dedupedIds.Count,
                emoteSetId,
                targetIsActiveSetOfChannel,
            }
            : new
            {
                emoteCount = dedupedIds.Count,
                emoteSetId,
                targetIsActiveSetOfChannel,
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

    // One set-centric report as the two public methods hand it over. Regel 9 for the expected channel.
    private sealed record InSetReport(
        InSetDirection Direction,
        string EmoteSetId,
        InSetOwner Owner,
        IReadOnlyList<string> SevenTvEmoteIds,
        IReadOnlyList<SyncInSetEntryDto> Entries,
        string? ExpectedChannelName,
        AuditActor Actor)
    {
        public string? NormalizedExpectedChannelName { get; } =
            ExpectedChannelName is null ? null : ChannelName.Normalize(ExpectedChannelName);
    }

    private sealed record InSetOutcome(
        int ReportedCount, IReadOnlyList<SyncInSetChannelResultDto> Channels, UnresolvedChannelDto? UnresolvedChannel,
        SyncInSetRejection? Rejection);

    private sealed record HitChannel(string Id, string ChannelName, string? TwitchChannelId);

    // The channel and the 7TV emote a reported slot id belongs to.
    private sealed record SlotOwner(string ChannelId, string SevenTvEmoteId);

    // What a report did to one emote row of one hit channel: whether the row or one of its slots
    // changed (NewlyChangedCount), how many of its entries found their slot here (audit entryCount), and
    // its leave — always (Leave) or only when none was observed since the last entry (B2).
    private readonly record struct EmoteApplication(bool Changed, int ResolvedEntries, bool Leave, bool LeaveOnceUnobserved);

    // Flag on: a hit channel of the report was deleted between its read and its channel lock (a purge
    // committed). Handled by MarkInSetAsync's one retry; propagates when it happens again.
    private sealed class HitChannelVanishedException()
        : InvalidOperationException("A channel the in-set report writes was deleted while the report was in flight.");
}
