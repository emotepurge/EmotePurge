using EmotePurge.Core.Entities;
using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmotePurge.Infrastructure.Services;

public class SevenTvSyncService(
    AppDbContext db,
    ISevenTvApiClient sevenTvApiClient,
    IEmoteMatchCache emoteMatchCache,
    IDuplicateEmoteNameTracker duplicateNameTracker,
    IChannelEmoteSetObservationService emoteSetObservationService,
    ChannelSyncGate channelSyncGate,
    IExcludedChannelFilter excludedChannelFilter,
    IBroadcasterChannelLockService broadcasterChannelLocks,
    ISevenTvSearchBudget searchBudget,
    TwitchIdResolutionBackoff resolutionBackoff,
    IEmptySetConfirmationTracker emptySetConfirmations,
    ILogger<SevenTvSyncService> logger,
    MatchMapExclusionTracker? matchMapExclusions = null)
    : ISevenTvSyncService
{
    private const string EmoteKeyIndexName = "IX_Emotes_ChannelId_SevenTvEmoteId";

    // Plan #372 E24: how often a save restarts because an emote row the reconcile can write appeared
    // between the emote lock and the read. Counted across the restarts of one save, not per attempt.
    private const int MaxLockCoverageRestarts = 2;

    // From the third miss in a row on, every further one gets an Information line: by then the
    // channel is not a transient hiccup, and the backoff itself keeps the line rare (24 a day at the
    // one-hour ceiling).
    private const int MissesBeforeLogging = 3;

    // How long after a row entered the active set a REST full sync's "it is missing" is NOT taken as a
    // credible leave (spec E34). 7TV's REST cache lags 10–30 min behind (SevenTV/SevenTV#81), so a
    // resync regularly archives an emote a dispatch or a tag play-in added moments ago; recorded as a
    // leave, that would invalidate the fresh placement at once (spec 5.5, counter-example 8). 30 min is
    // the documented upper end of the lag: shorter costs the feature, longer only delays noticing a
    // genuine manual removal while the EventAPI is off (spec 13.1 R1). A constant, not configuration
    // (spec 13.4/4): it encodes a property of 7TV, not of a deployment. Deltas and our own set-centric
    // delete report are always credible and never consult it.
    private static readonly TimeSpan TagLeaveCredibilityWindow = TimeSpan.FromMinutes(30);

    // Plan #373, E7: the process-wide memory of the match map's left-out count per channel, so its
    // information line is written on change only. Absent (tests) means a private one.
    private readonly MatchMapExclusionTracker exclusionTracker = matchMapExclusions ?? new MatchMapExclusionTracker();

    public async Task WarmChannelAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);
        using var nameGate = await channelSyncGate.AcquireByNameAsync(normalized, cancellationToken);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return;
        }

        using var rowGate = await AcquireRowGateAsync(channel, cancellationToken);
        if (rowGate is null)
        {
            return;
        }

        if (excludedChannelFilter.IsExcluded(channel.TwitchChannelId))
        {
            RefuseExcludedChannel(channel);
            return;
        }

        // D3 = A (#245): an id-less row is not warmed here at all. Nothing proves yet which Twitch
        // account it is, so counting its chat from Postgres before the identity and both gates are
        // through would observe a channel its broadcaster may have locked. Boot recovery syncs it right
        // after, and SyncChannelAsync warms it once the resolved id has passed the gates.
        if (channel.TwitchChannelId is null)
        {
            return;
        }

        if (await IsLockedByBroadcasterAsync(channel.TwitchChannelId, cancellationToken))
        {
            RefuseLockedChannel(channel);
            return;
        }

        await WarmMatchCacheIfEmptyAsync(channel, cancellationToken);
    }

    public async Task<SevenTvSyncResult?> SyncChannelAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        // Two concurrent syncs of the same channel collide on the (ChannelId, SevenTvEmoteId)
        // unique index — see ChannelSyncGate.
        using var nameGate = await channelSyncGate.AcquireByNameAsync(normalized, cancellationToken);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            logger.LogWarning("SyncChannelAsync: {Channel} nicht in Postgres gefunden.", normalized);
            return null;
        }

        using var rowGate = await AcquireRowGateAsync(channel, cancellationToken);
        if (rowGate is null)
        {
            logger.LogInformation(
                "SyncChannelAsync: Zeile von {Channel} ({ChannelId}) ist zwischenzeitlich verschwunden (vermutlich zusammengeführt) — Sync übersprungen.",
                normalized, channel.Id);
            return null;
        }

        // Objection gate (fourth Codex review of the block list): a row whose stored Twitch id is
        // excluded is never synced — no 7TV call, no match-cache warm-up, no EventAPI subscription
        // for the caller to register. Checked after the row gate so the decision is taken on the
        // re-read row. Every caller reaches this: boot recovery, the periodic resync, the JOIN and
        // RESYNC handlers and the EventAPI follow-ups, so it holds even for a path that got past its
        // own roster check.
        if (excludedChannelFilter.IsExcluded(channel.TwitchChannelId))
        {
            RefuseExcludedChannel(channel);
            return null;
        }

        // The broadcaster lock's twin of the gate above (#245), on the stored id — after it, the env
        // list wins. Same effect: no 7TV call, no warm-up, no observation interval, no subscription.
        if (await IsLockedByBroadcasterAsync(channel.TwitchChannelId, cancellationToken))
        {
            RefuseLockedChannel(channel);
            return null;
        }

        // A row with a stored id is warmed before either 7TV call (DECISIONS 2026-09-08: it counts
        // from the join, even while 7TV does not answer). An id-less row only once its resolved id
        // has passed both gates (D3 = A, #245) — see below.
        var hasStoredTwitchId = channel.TwitchChannelId is not null;
        if (hasStoredTwitchId)
        {
            await WarmMatchCacheIfEmptyAsync(channel, cancellationToken);
        }

        var twitchUserId = await ResolveTwitchUserIdAsync(channel, normalized, cancellationToken);
        if (twitchUserId is null)
        {
            // Only an id-less row gets here (a stored id is returned as is). Every way this ends —
            // backoff not due, search budget refused, 7TV failing, the env list or the broadcaster
            // lock, a rename duplicate — leaves the row unproven, so its match cache stays empty:
            // nothing about it may be counted until 7TV names an id that passes both gates (D3 = A).
            // Since #165 that can take up to the backoff ceiling plus a budget lockout (both about an
            // hour by default) between attempts; see docs/DECISIONS.md, #245.
            emoteMatchCache.RemoveChannel(channel.ChannelName);
            return null;
        }

        if (!hasStoredTwitchId)
        {
            await WarmMatchCacheIfEmptyAsync(channel, cancellationToken);
        }

        var channelState = await sevenTvApiClient.GetChannelStateForTwitchUserAsync(twitchUserId, cancellationToken);
        if (channelState.Status != SevenTvLookupStatus.Ok || channelState.State is null)
        {
            await RecordFailedAttemptAsync(channel, channelState.Status, cancellationToken);
            return null;
        }

        var emoteSet = channelState.State.EmoteSet;

        // Before the first write, which is the whole point of standing here rather than further
        // down: 7TV can answer Ok with a set whose id is missing, and its DTO defaults that field to
        // an empty string, so nothing further up notices. Carrying on would stamp the empty string
        // onto Channel.ActiveEmoteSetId — a value the delta path then compares against every
        // incoming dispatch, and the usage page reads as "the first sync is still running". Rejected
        // here with a reason of its own instead, so the UI can say what happened.
        if (!SevenTvIds.IsUsable(emoteSet.Id))
        {
            await RecordFailedAttemptAsync(channel, SevenTvSyncFailureReasons.ResponseUnusable, cancellationToken);
            return null;
        }

        // Read before the guard and before the assignment below: a switched active set is a content
        // change of its own, even when the two sets happen to hold identical emotes, and a zero that
        // arrives with a new set id is the one zero that needs no confirmation. A first-time
        // TwitchChannelId backfill deliberately does not count — it changes no emote the UI could show.
        var emoteSetSwitched = channel.ActiveEmoteSetId != emoteSet.Id;

        var implausibleWipeResult = await TryGuardAgainstImplausibleWipeAsync(
            channel, normalized, emoteSet, emoteSetSwitched, channelState.State, cancellationToken);
        if (implausibleWipeResult is not null)
        {
            return implausibleWipeResult;
        }

        // Whether this sync is the one that stores the id — and so the one that settles the
        // resolution backoff's provisional miss (ResolveTwitchUserIdAsync) once the save succeeds.
        var storesFreshTwitchId = channel.TwitchChannelId is null;

        // Plan #372 E28: the login the warm-up filled the cache under, as an immutable value taken
        // before anything can re-read or reload the row. Every cleanup below and in the save's
        // recoveries reads this, never a Channel instance a retry or a reload may have changed since.
        var cacheIdentity = new ChannelCacheIdentity(channel.Id, channel.ChannelName);
        var saved = await SaveSyncAsync(channel, cacheIdentity, normalized, twitchUserId, emoteSet, cancellationToken);
        if (saved is null)
        {
            return null;
        }

        (channel, var inventoryChanged, var slotSummary) = saved.Value;
        if (storesFreshTwitchId)
        {
            resolutionBackoff.RecordSuccess(channel.Id);
        }

        // The 7TV call above held no lock on the row, so a rename, deactivation or delete may have
        // committed meanwhile. Re-read before anything is published to process memory.
        var current = await db.Channels
            .AsNoTracking()
            .Where(c => c.Id == channel.Id)
            .Select(c => new { c.ChannelName, c.IsBotActive })
            .FirstOrDefaultAsync(cancellationToken);
        // After the commit, so a retried save logs once (E19), and under the login the row carries now:
        // the tracked instance keeps the login it was loaded with when a rename committed while the 7TV
        // call was in flight (E28 deliberately does not reload it).
        LogSlotSummary(current?.ChannelName ?? channel.ChannelName, slotSummary);

        if (current is null || !current.IsBotActive)
        {
            await RemoveOldNameCacheEntryAsync(cacheIdentity, cancellationToken);
            logger.LogInformation("SyncChannelAsync: row was deleted or deactivated while the sync was in flight — match cache left empty.");
            return null;
        }

        var finalName = current.ChannelName;
        if (!string.Equals(finalName, cacheIdentity.CacheName, StringComparison.Ordinal))
        {
            // Renamed meanwhile: the handover's LEAVE for the old login may already have run, so
            // anything the warm-up put under it would never be cleaned up by anyone.
            await RemoveOldNameCacheEntryAsync(cacheIdentity, cancellationToken);
        }

        await RefreshMatchCacheAsync(channel, finalName, cancellationToken);

        // The re-read name, not the caller's `normalized` nor the row as loaded: it is the login the
        // row carries now. See SevenTvSyncResult.ChannelName (issue #60).
        return SevenTvSyncResult.Create(
            finalName,
            emoteSet.Id,
            channelState.State.SevenTvUserId,
            emoteSetSwitched || inventoryChanged);
    }

    public async Task<SevenTvDeltaResult> ApplyEmoteSetUpdateAsync(
        string channelName,
        string emoteSetId,
        SevenTvEmoteSetDelta delta,
        CancellationToken cancellationToken = default)
    {
        if (delta.IsEmpty)
        {
            // The one NoChange that never sees a row, and therefore never learns a login.
            return SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.NoChange);
        }

        var normalized = ChannelName.Normalize(channelName);
        using var nameGate = await channelSyncGate.AcquireByNameAsync(normalized, cancellationToken);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            logger.LogWarning("ApplyEmoteSetUpdateAsync: {Channel} nicht in Postgres gefunden.", normalized);
            return SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.ChannelUnknown);
        }

        // Same two-step gate as the full sync, and for the same reason: the name this dispatch
        // arrived under says nothing about which row it will end up writing.
        using var rowGate = await AcquireRowGateAsync(channel, cancellationToken);
        if (rowGate is null)
        {
            logger.LogInformation(
                "ApplyEmoteSetUpdateAsync: Zeile von {Channel} ({ChannelId}) ist zwischenzeitlich verschwunden (vermutlich zusammengeführt) — Dispatch verworfen.",
                normalized, channel.Id);
            return SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.ChannelUnknown);
        }

        // One retry of the whole attempt when Postgres picks this dispatch as a deadlock victim (plan
        // #372 cycle Z8). The attempt takes the slot work's lock order (channel, emote, then leave rows)
        // and meets the sync and the in-set report at the channel row; the retry stays as a guard against
        // any writer that meets it in a cycle anyway. It is result-neutral: the victim's transaction has
        // rolled back as a whole, and the retry starts on a cleared change tracker and a re-read row, as a
        // fresh dispatch would. A second failure propagates as before.
        //
        // The same single retry also covers (plan #374 E9) a lock coverage miss (E2: an emote row of a
        // dispatch id appeared between the id read and the lock) and a concurrent insert of the same emote
        // row at save time (IX_Emotes_ChannelId_SevenTvEmoteId). One retry for all three causes together.
        var retried = false;
        while (true)
        {
            try
            {
                return await ApplyDeltaAttemptAsync(channel, normalized, emoteSetId, delta, cancellationToken);
            }
            catch (Exception ex) when (!retried && DatabaseErrors.IsDeadlock(ex))
            {
                retried = true;
                logger.LogWarning(
                    "ApplyEmoteSetUpdateAsync: the dispatch for {Channel} ({ChannelId}) was chosen as a deadlock victim — retrying once.",
                    normalized, channel.Id);
            }
            catch (DeltaLockCoverageMissException) when (!retried)
            {
                retried = true;
                logger.LogWarning(
                    "ApplyEmoteSetUpdateAsync: an emote row of the dispatch for {Channel} ({ChannelId}) appeared between its row lock and its read — retrying once.",
                    normalized, channel.Id);
            }
            catch (DbUpdateException ex) when (!retried && IsEmoteKeyConflict(ex))
            {
                retried = true;
                logger.LogWarning(
                    "ApplyEmoteSetUpdateAsync: an emote row of the dispatch for {Channel} ({ChannelId}) was inserted concurrently — retrying once.",
                    normalized, channel.Id);
            }

            db.ChangeTracker.Clear();
            var reloaded = await db.Channels.SingleOrDefaultAsync(c => c.Id == channel.Id, cancellationToken);
            if (reloaded is null)
            {
                // The same answer as the row gate's for a row that is gone (purged or merged away).
                logger.LogInformation(
                    "ApplyEmoteSetUpdateAsync: the row of {Channel} ({ChannelId}) is gone after the deadlock (purged or merged meanwhile) — dispatch dropped.",
                    normalized, channel.Id);
                return SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.ChannelUnknown);
            }

            channel = reloaded;
        }
    }

    /// <summary>
    /// One attempt of <see cref="ApplyEmoteSetUpdateAsync"/> past both gates, on a row that has just
    /// been read: the objection gates, the set check, the plausibility guard and the write. Run once
    /// more on a deadlock (see the caller), so everything in it is idempotent.
    /// </summary>
    private async Task<SevenTvDeltaResult> ApplyDeltaAttemptAsync(
        Channel channel,
        string normalized,
        string emoteSetId,
        SevenTvEmoteSetDelta delta,
        CancellationToken cancellationToken)
    {
        // Same objection gate as the full sync. ChannelUnknown rather than a new outcome: the
        // EventAPI client answers it by dropping the subscription, which is exactly what a blocked
        // channel needs, and it reports no login back.
        if (excludedChannelFilter.IsExcluded(channel.TwitchChannelId))
        {
            RefuseExcludedChannel(channel);
            return SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.ChannelUnknown);
        }

        // The broadcaster lock (#245), same answer for the same reason: the subscription is dropped.
        if (await IsLockedByBroadcasterAsync(channel.TwitchChannelId, cancellationToken))
        {
            RefuseLockedChannel(channel);
            return SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.ChannelUnknown);
        }

        // Past the row gate the row has been re-read, so this — not the name the dispatch arrived
        // under — is the login every outcome below reports back (issue #60).
        var currentName = channel.ChannelName;

        if (channel.ActiveEmoteSetId != emoteSetId)
        {
            logger.LogInformation(
                "7TV-Dispatch für Set {SetId} ignoriert — {Channel} hat inzwischen Set {ActiveSetId} aktiv.",
                emoteSetId, normalized, channel.ActiveEmoteSetId);
            return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.SetNotActive, currentName);
        }

        // The slot path (plan #374 3.2): per (id, name), in the slot work's lock order.
        return await ApplySlotDeltaAttemptAsync(channel, normalized, emoteSetId, delta, currentName, afterSlotKeyConflict: false, cancellationToken);
    }

    /// <summary>
    /// One attempt of a dispatch (plan #374 3.2; spec §7.2, D17), in one transaction:
    /// <list type="number">
    /// <item>locks (plan #372 Z8): the channel row, then the existing emote rows of the dispatch ids in
    /// one sorted statement — the sync's and the in-set report's order;</item>
    /// <item>reads behind the locks, from the database: the emote rows of the dispatch ids with all their
    /// slots, the channel's active emote count (plausibility) and which of those emotes carry an
    /// unsettled seed of the active set (<see cref="SetSeeds.SeedsOfSet"/>, the settle's and the map's
    /// definition);</item>
    /// <item>checks lock coverage (E2): a row of a dispatch id read here but not locked was created by a
    /// writer without the channel lock (a ballot placeholder) in between — the attempt throws
    /// <see cref="DeltaLockCoverageMissException"/> and the caller retries (E9);</item>
    /// <item>plans the dispatch with <see cref="EmoteSlotDeltaPlanner"/> against that snapshot;</item>
    /// <item>ambiguous (or <paramref name="afterSlotKeyConflict"/>): no emote and no slot row is written, only
    /// the certain last-slot leaves of the plan (E7, operator decision R5) — <c>AmbiguousDelta</c>;</item>
    /// <item>otherwise the plausibility guard on the plan, then the plan onto the tracked rows (plan 3.3,
    /// D13), the leave upsert, the save and the commit — <c>Applied</c> when an emote or slot row
    /// changed, else <c>NoChange</c>.</item>
    /// </list>
    /// A slot key conflict at save time (E12's belt, plan 3.4) is not retried: the transaction rolls back
    /// and the dispatch runs once more with <paramref name="afterSlotKeyConflict"/> set, which answers
    /// <c>AmbiguousDelta</c> and records the certain leaves in a new transaction under the same locks.
    /// </summary>
    private async Task<SevenTvDeltaResult> ApplySlotDeltaAttemptAsync(
        Channel channel,
        string normalized,
        string emoteSetId,
        SevenTvEmoteSetDelta delta,
        string currentName,
        bool afterSlotKeyConflict,
        CancellationToken cancellationToken)
    {
        var dispatchIds = delta.Pushed.Select(e => e.Id)
            .Concat(delta.Updated.Select(e => e.Id))
            .Concat(delta.Pulled.Select(e => e.Id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await LockDeltaRowsAsync(channel.Id, dispatchIds, cancellationToken);
        if (locked is null)
        {
            // A purge or merge committed after the row gate's re-read: the row gate's answer.
            logger.LogInformation(
                "ApplyEmoteSetUpdateAsync: the row of {Channel} ({ChannelId}) was gone at its row lock (purged or merged meanwhile) — dispatch dropped.",
                normalized, channel.Id);
            return SevenTvDeltaResult.WithoutChannel(SevenTvDeltaOutcome.ChannelUnknown);
        }

        // Behind the locks and from the database (plan #372 2.2 step 4): no tracked instance of an earlier
        // read may stand in for the locked state.
        DetachTrackedEmotesOf(channel.Id);
        var emotes = await db.Emotes
            .Include(e => e.Slots)
            .Where(e => e.ChannelId == channel.Id && dispatchIds.Contains(e.SevenTvEmoteId))
            .ToListAsync(cancellationToken);
        if (emotes.Any(e => !locked.Contains(e.Id)))
        {
            throw new DeltaLockCoverageMissException();
        }

        var activeIdsBefore = (await db.Emotes
                .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
                .Select(e => e.SevenTvEmoteId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var activeBefore = activeIdsBefore.Count;
        var emoteIds = emotes.Select(e => e.Id).ToList();
        var seedIds = (await db.SeedsOfSet(channel.Id, emoteSetId)
                .Where(s => emoteIds.Contains(s.EmoteId))
                .Select(s => s.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var plan = EmoteSlotDeltaPlanner.Plan(
            delta,
            emotes.Select(e => new SlotDeltaEmoteSnapshot(
                e.SevenTvEmoteId, e.Name, e.ImageUrl, e.IsArchived, e.Slots.Select(SlotState.From).ToList())).ToList(),
            seedIds,
            now);

        // A dispatch that adds or renames is live evidence that the set is not empty — also when it is
        // ambiguous per slot; not after the plausibility return.
        var provesNonEmpty = delta.Pushed.Count > 0 || delta.Updated.Count > 0;
        if (plan.IsAmbiguous || afterSlotKeyConflict)
        {
            // The plausibility guard stands ahead of the leaves of an ambiguous dispatch too, as an
            // emote-wide wipe simulation (every pulled id counts as gone), so a malformed dispatch
            // writes no leaves — the worker resyncs on ImplausibleSkipped.
            var simulatedActive = new HashSet<string>(activeIdsBefore, StringComparer.Ordinal);
            simulatedActive.UnionWith(delta.Pushed.Select(e => e.Id));
            simulatedActive.UnionWith(delta.Updated.Select(e => e.Id));
            simulatedActive.ExceptWith(delta.PulledIds);
            if (activeBefore > 0 && simulatedActive.Count == 0)
            {
                logger.LogWarning(
                    "7TV dispatch would remove all {Count} active emotes of {Channel} — skipped as implausible.",
                    activeBefore, normalized);
                return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.ImplausibleSkipped, currentName);
            }

            if (provesNonEmpty)
            {
                emptySetConfirmations.Reset(channel.ChannelName);
            }

            return await AnswerAmbiguousAsync(transaction, channel, emoteSetId, plan, currentName, now, cancellationToken);
        }

        if (activeBefore > 0 && activeBefore + plan.ActiveEmoteCountChange == 0)
        {
            logger.LogWarning(
                "7TV dispatch would remove all {Count} active emotes of {Channel} — skipped as implausible.",
                activeBefore, normalized);
            return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.ImplausibleSkipped, currentName);
        }

        if (provesNonEmpty)
        {
            emptySetConfirmations.Reset(channel.ChannelName);
        }

        var byId = emotes.ToDictionary(e => e.SevenTvEmoteId, StringComparer.Ordinal);
        var createdSlots = 0;
        var updatedSlots = 0;
        foreach (var emotePlan in plan.Emotes.Where(p => p.WritesInventory))
        {
            ApplySlotDeltaEmotePlan(channel.Id, byId, emotePlan, now);
            createdSlots += emotePlan.CreatedSlots.Count;
            updatedSlots += emotePlan.UpdatedSlots.Count;
        }

        // E8: the leave rows come third (after the channel and emote rows), as in the sync and the report.
        var leaveIds = plan.LeaveIds;
        if (leaveIds.Count > 0)
        {
            await EmoteSetLeaveObservations.RecordAsync(db, channel.Id, emoteSetId, leaveIds, now, cancellationToken);
        }

        if (!plan.HasInventoryChanges)
        {
            // A leave alone is no inventory change: no map refresh, no live event.
            await transaction.CommitAsync(cancellationToken);
            return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.NoChange, currentName);
        }

        AssertWritesAreLocked(channel.Id, locked);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (EmoteSlotKeys.IsSlotKeyConflict(ex))
        {
            // Plan 3.4 (Sol-P2-1): E12 rules this out, so it is a belt — the same rename would collide
            // again, so no retry. Rolled back; answered as ambiguous, with the certain leaves recorded in
            // a new transaction under the same locks, and REST resolves it.
            var collidingIds = ex.Entries
                .Select(entry => entry.Entity)
                .OfType<EmoteSlot>()
                .Select(slot => byId.Values.FirstOrDefault(e => e.Id == slot.EmoteId)?.SevenTvEmoteId)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            logger.LogWarning(
                "7TV dispatch for {Channel} met the authoritative slot key at save time ({Reason}, 7TV emote {SevenTvEmoteIds}) — rolled back and answered as ambiguous.",
                normalized, SlotDeltaAmbiguity.AuthoritativeKeyCollision, string.Join(", ", collidingIds));
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await ApplySlotDeltaAttemptAsync(
                channel, normalized, emoteSetId, delta, currentName, afterSlotKeyConflict: true, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // Plan #373 E7: the map counts per slot, so a push slot counts from this refresh on.
        await RefreshMatchCacheAsync(channel, cancellationToken);

        logger.LogInformation(
            "7TV dispatch applied per slot on {Channel}: {CreatedSlots} slots created, {UpdatedSlots} slots updated, {Leaves} leaves recorded.",
            normalized, createdSlots, updatedSlots, leaveIds.Count);
        return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.Applied, currentName);
    }

    /// <summary>
    /// The ambiguous end of <see cref="ApplySlotDeltaAttemptAsync"/> (spec §7.2, plan #374 E7, operator
    /// decision R5): no inventory, one information line per ambiguous emote with its reason code (4.3,
    /// no alias names), the certain last-slot leaves of the plan, commit, <c>AmbiguousDelta</c>. The
    /// caller runs the REST resync.
    /// </summary>
    private async Task<SevenTvDeltaResult> AnswerAmbiguousAsync(
        IDbContextTransaction transaction,
        Channel channel,
        string emoteSetId,
        SlotDeltaPlan plan,
        string currentName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var ambiguity in plan.Ambiguities)
        {
            logger.LogInformation(
                "7TV dispatch for {Channel} is ambiguous per slot ({Reason}, 7TV emote {SevenTvEmoteId}) — no inventory written, a REST resync follows.",
                currentName, ambiguity.AmbiguityReason, ambiguity.SevenTvEmoteId);
        }

        var leaveIds = plan.LeaveIds;
        if (leaveIds.Count > 0)
        {
            await EmoteSetLeaveObservations.RecordAsync(db, channel.Id, emoteSetId, leaveIds, now, cancellationToken);
            logger.LogInformation(
                "7TV dispatch for {Channel}: {Count} certain leaves recorded despite the ambiguity.",
                currentName, leaveIds.Count);
        }

        await transaction.CommitAsync(cancellationToken);
        return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.AmbiguousDelta, currentName);
    }

    /// <summary>
    /// Applies one unambiguous emote plan of a dispatch to the tracked rows (plan #374 3.3): the emote
    /// action (create, un-archive, archive — with the entry stamp, E34, and the placeholder marker,
    /// D37), the image, the planned slot updates and new slots, and <c>Emote.Name</c>/<c>FirstSeenAt</c>
    /// strictly from the D13 derivation. The dispatch never renames the emote row itself.
    /// </summary>
    private void ApplySlotDeltaEmotePlan(string channelId, Dictionary<string, Emote> byId, SlotDeltaEmotePlan plan, DateTime now)
    {
        Emote emote;
        if (plan.EmoteAction == SlotDeltaEmoteAction.Create)
        {
            // S0 push: always at least one new slot, so the derivation is set.
            var derived = plan.Derivation
                ?? throw new InvalidOperationException($"A created emote {plan.SevenTvEmoteId} without a derived name.");
            emote = new Emote
            {
                ChannelId = channelId,
                SevenTvEmoteId = plan.SevenTvEmoteId,
                Name = derived.Name,
                ImageUrl = plan.ImageUrl ?? string.Empty,
                FirstSeenAt = derived.FirstSeenAt,
                // E34: a new row is an entry into the active set.
                LastEnteredSetAtUtc = now,
                LastSyncedAt = now
            };
            db.Emotes.Add(emote);
            byId[plan.SevenTvEmoteId] = emote;
        }
        else
        {
            emote = byId[plan.SevenTvEmoteId];
            var rowChanged = false;
            switch (plan.EmoteAction)
            {
                case SlotDeltaEmoteAction.Unarchive:
                    // E34 and D37: a fresh entry, observed in the active set.
                    emote.IsArchived = false;
                    emote.ArchivedAt = null;
                    emote.LastEnteredSetAtUtc = now;
                    emote.IsPlaceholder = false;
                    rowChanged = true;
                    break;

                case SlotDeltaEmoteAction.Archive:
                    // D37: the row was active, so it is no placeholder, whatever a stale marker says.
                    emote.IsArchived = true;
                    emote.ArchivedAt = now;
                    emote.IsPlaceholder = false;
                    rowChanged = true;
                    break;
            }

            if (plan.ImageUrl is { } imageUrl)
            {
                emote.ImageUrl = imageUrl;
                rowChanged = true;
            }

            if (plan.Derivation is { } derived)
            {
                // D13 strictly (plan #372 2.3 step 8), as in the reconcile: FirstSeenAt follows the oldest
                // active own slot, null included.
                if (!string.Equals(emote.Name, derived.Name, StringComparison.Ordinal))
                {
                    emote.Name = derived.Name;
                    rowChanged = true;
                }

                emote.FirstSeenAt = derived.FirstSeenAt;
            }

            if (rowChanged)
            {
                emote.LastSyncedAt = now;
            }
        }

        if (plan.UpdatedSlots.Count > 0)
        {
            var slotsById = emote.Slots.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var state in plan.UpdatedSlots)
            {
                state.ApplyTo(slotsById[state.Id], now);
            }
        }

        foreach (var state in plan.CreatedSlots)
        {
            db.EmoteSlots.Add(state.ToNewEntity(emote.Id, now));
        }
    }

    /// <summary>
    /// The locks of a dispatch (Z8): the channel row through the pure channel entry,
    /// then the existing emote rows of the channel whose 7TV ids the dispatch pushes, updates or pulls,
    /// in one sorted statement. A pushed id without a row becomes an insert and needs no lock. Returns
    /// the locked emote row ids, or null when the channel row is gone.
    /// </summary>
    private async Task<HashSet<string>?> LockDeltaRowsAsync(
        string channelId, IReadOnlyCollection<string> sevenTvIds, CancellationToken cancellationToken)
    {
        var lockedChannels = await db.LockChannelsByIdsAsync([channelId], cancellationToken);
        if (lockedChannels.Count == 0)
        {
            return null;
        }

        var emoteIds = await db.Emotes
            .AsNoTracking()
            .Where(e => e.ChannelId == channelId && sevenTvIds.Contains(e.SevenTvEmoteId))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        return (await db.LockEmotesByIdsAsync(emoteIds, cancellationToken)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs <see cref="ApplyAndSaveAsync"/> with the sync's recoveries around it and returns the row the
    /// save finally wrote (a retry re-reads it), whether the visible inventory changed and the slot
    /// summary, or null when the row is gone — in which case nothing is left behind and the
    /// caller must return null as well. Every error that propagates leaves a cleared change tracker.
    /// <list type="bullet">
    /// <item><b>One retry of the whole save</b> (plan #372 E17), shared by three causes: a concurrent
    /// emote-row insert (spec E10), a concurrent authoritative slot with the same key, and a deadlock
    /// (40P01, E27). The retry starts from step 1 — the whole
    /// <see cref="ApplyAndSaveAsync"/> on a cleared change tracker and a re-read row — because the
    /// observation interval of a first sync is tracked only and would be lost otherwise. A second
    /// failure propagates.</item>
    /// <item><b>Up to <see cref="MaxLockCoverageRestarts"/> restarts</b> when the lock coverage check
    /// fails (E24), counted separately and the same way from step 1. One more miss
    /// propagates like a second failure; the next tick tries again.</item>
    /// <item><b>Row vanished</b>: a purge or merge while the 7TV call was in flight ends the sync
    /// without leaving anything behind.</item>
    /// </list>
    /// Every cache cleanup on the way reads <paramref name="cacheIdentity"/> (E28).
    /// </summary>
    private async Task<(Channel Channel, bool InventoryChanged, SlotReconcileSummary SlotSummary)?> SaveSyncAsync(
        Channel channel,
        ChannelCacheIdentity cacheIdentity,
        string normalized,
        string twitchUserId,
        SevenTvEmoteSet emoteSet,
        CancellationToken cancellationToken)
    {
        var retried = false;
        var coverageRestarts = 0;
        try
        {
            while (true)
            {
                try
                {
                    var (inventoryChanged, slotSummary) = await ApplyAndSaveAsync(channel, twitchUserId, emoteSet, cancellationToken);
                    return (channel, inventoryChanged, slotSummary);
                }
                catch (LockCoverageMissException) when (coverageRestarts < MaxLockCoverageRestarts)
                {
                    coverageRestarts++;
                    logger.LogWarning(
                        "SyncChannelAsync: an emote row of {Channel} ({ChannelId}) the reconcile can write appeared between its row lock and its read — restarting the save ({Restart} of {MaxRestarts}).",
                        normalized, channel.Id, coverageRestarts, MaxLockCoverageRestarts);
                }
                catch (DbUpdateException ex) when (!retried && IsEmoteKeyConflict(ex))
                {
                    // Spec E10: the Api's vote-session upsert (INSERT … ON CONFLICT DO NOTHING) inserted
                    // one of this set's members between ReconcileSlotsAsync's read and the save. The
                    // retry reads the Api's row and adopts it instead of inserting.
                    retried = true;
                    logger.LogWarning(
                        "SyncChannelAsync: an emote row of {Channel} ({ChannelId}) was inserted concurrently during the sync — retrying once.",
                        normalized, channel.Id);
                }
                catch (DbUpdateException ex) when (!retried && EmoteSlotKeys.IsSlotKeyConflict(ex))
                {
                    // A writer that does not take the emote lock (or one that committed before it)
                    // inserted the same authoritative slot. The retry reads it and binds to it by date.
                    retried = true;
                    logger.LogWarning(
                        "SyncChannelAsync: a slot of {Channel} ({ChannelId}) with the same authoritative key was inserted concurrently during the sync — retrying once.",
                        normalized, channel.Id);
                }
                catch (Exception ex) when (!retried && DatabaseErrors.IsDeadlock(ex))
                {
                    // Plan #372 2.2a: the cycles left over (backfill block Z4, flush Z5) are recovered by
                    // the side Postgres picks as the victim.
                    retried = true;
                    logger.LogWarning(
                        "SyncChannelAsync: the save of {Channel} ({ChannelId}) was chosen as a deadlock victim — retrying once.",
                        normalized, channel.Id);
                }

                // The attempt's transaction has rolled back on dispose. Start over from step 1 on a
                // clean change tracker and a re-read row, still under both gates.
                db.ChangeTracker.Clear();
                var reloaded = await db.Channels.SingleOrDefaultAsync(c => c.Id == channel.Id, cancellationToken);
                if (reloaded is null)
                {
                    // The row gate does not hold off a purge or a merge (see DECISIONS), so the row
                    // can be gone here — the same "row vanished" exit as below.
                    await AbandonVanishedRowAsync(cacheIdentity, cancellationToken);
                    return null;
                }

                channel = reloaded;
            }
        }
        catch (Exception ex) when (ex is ChannelRowVanishedException || IsRowVanishedFor(ex, cacheIdentity.ChannelId) || IsLeaveObservationOrphan(ex))
        {
            // The row was purged or merged away while the 7TV call was in flight. The row gate only
            // coordinates sync callers, not those writers (see DECISIONS), so this is an expected
            // interleaving, not a fault: drop the pending changes and leave nothing behind. The
            // attempt's transaction has already rolled back on dispose.
            await AbandonVanishedRowAsync(cacheIdentity, cancellationToken);
            return null;
        }
        catch
        {
            // Every other way out propagates: a second conflict or deadlock, a coverage miss past the
            // restarts, a programming error, a failed re-read, a cancellation. The attempt's transaction
            // has rolled back, but its tracked changes (the channel's success stamps, a first
            // observation interval, new emotes and slots) would stay in the context — and the periodic
            // resync shares one context across every channel of its tick, where the next channel's
            // save would write them without their locks, or its lock assertion would refuse them.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task AbandonVanishedRowAsync(ChannelCacheIdentity cacheIdentity, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await RemoveOldNameCacheEntryAsync(cacheIdentity, cancellationToken);
        logger.LogInformation("SyncChannelAsync: row vanished while the sync was in flight — sync abandoned.");
    }

    /// <summary>
    /// The writing half of <see cref="SyncChannelAsync"/>: records the observed set, stamps the
    /// channel row, reconciles the emote rows and saves it all in one call. Split out so every
    /// recovery in <see cref="SaveSyncAsync"/> can run the exact same sequence again — the
    /// observation call included, because the row it may have staged is tracked only and a cleared
    /// change tracker would otherwise drop it. Returns whether the visible inventory changed and the slot
    /// summary the caller logs once the save is through (E19).
    /// </summary>
    private async Task<(bool InventoryChanged, SlotReconcileSummary SlotSummary)> ApplyAndSaveAsync(
        Channel channel, string twitchUserId, SevenTvEmoteSet emoteSet, CancellationToken cancellationToken)
    {
        // The observation log's own decision, independent of SyncChannelAsync's ActiveEmoteSetId check:
        // it looks at whether an interval is *currently open* in its own table, not at this channel
        // column, so a channel whose row was closed by a rename/merge without ActiveEmoteSetId
        // changing still gets a fresh interval here (spec 4.3, "auch wenn die zuletzt geschlossene
        // dieselbe ID trug"). Called before the assignments below, while db has no other pending
        // changes yet, so a set switch's own transaction (see
        // ChannelEmoteSetObservationService.RecordObservedSetAsync) never has to share a commit with
        // unrelated in-flight state from this method.
        await emoteSetObservationService.RecordObservedSetAsync(channel.Id, emoteSet.Id, cancellationToken);

        channel.TwitchChannelId ??= twitchUserId;
        channel.ActiveEmoteSetId = emoteSet.Id;
        // Written here and only here, in lockstep with the set id: the EventAPI delta path carries no
        // capacity, so writing it there would leave a switched set showing the old set's limit until
        // the next resync. Deliberately outside the inventory-change bookkeeping below — a changed
        // capacity is not a changed emote and must not make every open page refetch.
        channel.ActiveEmoteSetCapacity = emoteSet.Capacity;
        // Unconditional, and deliberately not part of the change bookkeeping below: "we successfully
        // reached 7TV and reconciled" is true even when nothing moved, and that is precisely what
        // makes it worth recording separately from the inventory timestamps. Written only here, never
        // in the delta path — a dispatch is not a full reconciliation, and ApplyEmoteSetUpdateAsync
        // decides NoChange vs Applied by asking the ChangeTracker, so a write there would turn every
        // no-op dispatch into a live event.
        var syncedAt = DateTime.UtcNow;
        channel.LastSyncedAtUtc = syncedAt;
        // The reset half of the contract, and the one that gets forgotten: a channel that activated
        // an emote set on 7TV must stop being told it has none. Written in the same block as the
        // success stamp so the two cannot drift apart — a reason cleared anywhere else would need a
        // second place to remember it.
        channel.LastSyncAttemptAtUtc = syncedAt;
        channel.LastSyncFailureReason = null;

        // One explicit transaction per save attempt, so the leave observations the reconcile upserts
        // (raw SQL, outside SaveChangesAsync's implicit transaction) and the usage rebinds of the settle
        // commit together with the archive and the channel row. Opened only here, after
        // RecordObservedSetAsync: that call commits a set switch in a transaction of its own, and EF
        // cannot nest them. The assignments above are in-memory and ride the same save. A failed attempt
        // rolls back on dispose, and SaveSyncAsync runs this method again with a new transaction — every
        // statement in it is idempotent.
        //
        // Lock order (plan #372 E4, E23): the channel row first, then the emote rows the reconcile can
        // write (sorted), then the usage rows of the settle and the backfill snapshot rows it rebinds
        // (plan #373 E10, E11a), then the observation rows, then the save (slots, emotes ⊆ locked ∪ new,
        // the channel row already held). Purge, merge, join, identity reconcile, tag report and backfill
        // enqueue all take Channels before Emotes as well, so none of them forms a cycle with the sync;
        // the cycles that remain (2.2a) end in the deadlock retry of SaveSyncAsync.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // E23: a pure lock statement that loads no Channel and reloads nothing — the tracked row was
        // changed above, and its login must stay what the cache identity says (E28). A row committed as
        // deleted meanwhile is not returned.
        var lockedChannels = await db.LockChannelsByIdsAsync([channel.Id], cancellationToken);
        if (lockedChannels.Count == 0)
        {
            throw new ChannelRowVanishedException();
        }

        var (inventoryChanged, slotSummary) = await ReconcileSlotsAsync(channel.Id, emoteSet.Id, emoteSet.Emotes, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (inventoryChanged, slotSummary);
    }

    /// <summary>
    /// What every objection-gate refusal in this class shares: the channel's match-cache entry is
    /// dropped, so the chat of a blocked channel the worker may still sit in counts nothing, and a
    /// line that names neither the channel nor its id says why the sync stopped. Nothing is written
    /// to the row.
    /// <para>
    /// Debug, not Information: the line names nothing, but it follows lines of the same call that do
    /// — boot recovery's or the JOIN handler's "joining {Channel}", the warm-up's "match cache for
    /// {Channel} warmed" — and next to them it would tie the block to that channel all the same. The
    /// operator-visible signal is the identity reconcile's count of deactivated rows.
    /// </para>
    /// </summary>
    private void RefuseExcludedChannel(Channel channel)
    {
        emoteMatchCache.RemoveChannel(channel.ChannelName);
        logger.LogDebug("7TV sync skipped: the channel is on the excluded-channel list.");
    }

    /// <summary>
    /// <see cref="RefuseExcludedChannel"/> for the broadcaster lock (#245): the same effect, and the
    /// same nameless Debug line. The lock is not secret, but the line has nothing to add — the identity
    /// reconcile's LockedDeactivated count is the operator-visible signal.
    /// </summary>
    private void RefuseLockedChannel(Channel channel)
    {
        emoteMatchCache.RemoveChannel(channel.ChannelName);
        logger.LogDebug("7TV sync skipped: the channel is locked by its broadcaster.");
    }

    private async Task<bool> IsLockedByBroadcasterAsync(string? twitchChannelId, CancellationToken cancellationToken) =>
        await broadcasterChannelLocks.GetLockedAtUtcAsync(twitchChannelId, cancellationToken) is not null;

    /// <summary>
    /// Takes the row gate for a channel that was just looked up by name, and re-reads the row under
    /// it. Returns null when the row is gone, in which case the gate is already released and the
    /// caller must not write anything.
    /// </summary>
    private async Task<IDisposable?> AcquireRowGateAsync(Channel channel, CancellationToken cancellationToken)
    {
        var gate = await channelSyncGate.AcquireByChannelIdAsync(channel.Id, cancellationToken);
        try
        {
            // The row was read *without* the row gate — it had to be, since the id is what the gate
            // keys on. A rename handover is the case that makes that matter: while this call sat
            // queued behind the sync running under the channel's other login, the row may have been
            // renamed out from under it, or merged away entirely. Re-reading here is what keeps
            // ChannelName — the key the match cache and the duplicate-name tracker are written
            // under — from being the one the handover just retired.
            await db.Entry(channel).ReloadAsync(cancellationToken);
            if (db.Entry(channel).State == EntityState.Detached)
            {
                // Reload detaches an entity whose row has disappeared. Writing on from here would
                // mean a DbUpdateConcurrencyException on the channel update, or a foreign-key
                // violation on freshly inserted emote rows.
                gate.Dispose();
                return null;
            }

            return gate;
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The bot joins a channel's chat as soon as it is added, well before <see cref="SyncChannelAsync"/>
    /// ever runs successfully — and if the very first 7TV call after a worker restart fails (timeout,
    /// outage), the match cache for this channel stays empty. OnMessageReceived bails out silently on
    /// an empty set with no retry of its own, so the channel would count nothing until the periodic
    /// resync happens to succeed. Warming the cache from Postgres here, right after the row gate (so
    /// <c>channel.ChannelName</c> is the re-read, current login) and before either 7TV call, closes
    /// that gap: both calls can hang for up to 10s (see the client timeout), and a warm-up that only
    /// ran after such a timeout would lose exactly those seconds in the loudest moment — right after
    /// boot. The condition is "cache empty for this channel", not "process just started" — that keeps
    /// the existing asymmetry intact: a cache a previous successful sync already filled is never
    /// touched by a failed one, here or in <see cref="RecordFailedAttemptAsync(Channel, string?, CancellationToken)"/>.
    /// <para>
    /// Since #245 (D3 = A) that early warm-up holds only for a row with a stored Twitch id. An id-less
    /// row is warmed only after 7TV has resolved its id and the id has passed the env list and the
    /// broadcaster lock; until then its cache stays empty and nothing is counted for it.
    /// </para>
    /// </summary>
    private async Task WarmMatchCacheIfEmptyAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (emoteMatchCache.GetChannelSnapshot(channel.ChannelName).NameToSlotId.Count != 0)
        {
            return;
        }

        await RefreshMatchCacheAsync(channel, cancellationToken);

        var warmedCount = emoteMatchCache.GetChannelSnapshot(channel.ChannelName).NameToSlotId.Count;
        if (warmedCount > 0)
        {
            logger.LogInformation(
                "Match-Cache für {Channel} aus Postgres vorgewärmt ({Count} Namen) — 7TV-Sync folgt.",
                channel.ChannelName, warmedCount);
        }
    }

    /// <summary>
    /// The channel's already-known Twitch id, or a freshly resolved one. Null means the caller must
    /// abort the sync — either the resolution itself failed (recorded via
    /// <see cref="RecordFailedAttemptAsync(Channel, SevenTvLookupStatus, CancellationToken)"/>) or the
    /// resolved id belongs to a different, already-tracked row (a rename duplicate — logged, not
    /// recorded as a failure, since nothing about this attempt actually failed), or the search was
    /// held back by the channel's backoff or refused by the shared 7TV search budget (neither
    /// recorded: no search was made).
    /// </summary>
    private async Task<string?> ResolveTwitchUserIdAsync(Channel channel, string normalized, CancellationToken cancellationToken)
    {
        if (channel.TwitchChannelId is { } knownTwitchUserId)
        {
            return knownTwitchUserId;
        }

        // The id-less path costs a search from 7TV's shared search bucket, so it runs behind two
        // gates (design note docs/Konzept-7TV-Such-Budget-2026-10-03.md). Neither writes a failure
        // reason when it holds the sync back: nothing was asked, so the last real answer stands.
        if (!resolutionBackoff.IsDue(channel.Id, out var retryIn))
        {
            logger.LogDebug(
                "Twitch id resolution for {Channel} ({ChannelId}) backed off, next attempt in {RetrySeconds}s.",
                channel.ChannelName, channel.Id, Math.Ceiling(retryIn.TotalSeconds));
            return null;
        }

        var permit = await searchBudget.TryChargeAsync(SevenTvSearchConsumer.ChannelIdentity, cancellationToken);
        if (!permit.Granted)
        {
            // Debug: this repeats on every tick for as long as the bucket is blocked, and the block
            // itself has already been announced once, at Warning, by whoever observed it.
            logger.LogDebug(
                "Twitch id resolution for {Channel} ({ChannelId}) skipped: 7TV search budget refused ({Refusal}).",
                channel.ChannelName, channel.Id, permit.Refusal);
            return null;
        }

        // The search is paid for from here on, so it counts as a miss now — before the request, not
        // after it. Provisional: only the successful save of the id at the very end of the sync
        // clears it (SyncChannelAsync). Recording it up front is what keeps every way out of this
        // attempt honest: a failed lookup, a resolved id the sync then cannot store (no active set,
        // say), and an exception anywhere below — a cancelled save included — all leave the channel
        // backed off instead of letting the next tick spend another search.
        var (misses, delay) = resolutionBackoff.RecordMiss(channel.Id);

        var (twitchUserId, silent) = await ResolveWithChargedSearchAsync(channel, normalized, cancellationToken);
        if (twitchUserId is null)
        {
            LogResolutionMiss(channel, silent, misses, delay);
        }

        return twitchUserId;
    }

    /// <summary>
    /// The resolution proper, once a search has been paid for. A null id for every way it can end
    /// without an id this row may store — the caller turns each of them into one backoff step.
    /// <c>Silent</c> marks the ones that must stay out of the default log level: the env list and the
    /// broadcaster lock.
    /// </summary>
    private async Task<(string? TwitchUserId, bool Silent)> ResolveWithChargedSearchAsync(Channel channel, string normalized, CancellationToken cancellationToken)
    {
        // channel.ChannelName, not `normalized`: this runs after the row gate re-read the row, so
        // asking 7TV about the caller's name would ask about a login the rename has already retired.
        // Same root cause as the propagation issue #60 fixes for the callers.
        var resolved = await sevenTvApiClient.ResolveTwitchUserIdAsync(channel.ChannelName, cancellationToken);
        if (resolved.Status != SevenTvLookupStatus.Ok || resolved.TwitchUserId is null)
        {
            await RecordFailedAttemptAsync(channel, resolved.Status, cancellationToken);
            return (null, false);
        }

        var twitchUserId = resolved.TwitchUserId;

        // The id-less half of the objection gate in SyncChannelAsync: a row created while Twitch
        // could not be asked learns its id only here, from 7TV — independently of Helix, so this
        // also catches a blocked channel joined during a Helix outage. Checked before the duplicate
        // lookup below, whose warning would otherwise name the blocked id, and before the backfill:
        // nothing is written for it. No warm-up has run for this id-less row (D3 = A, #245).
        if (excludedChannelFilter.IsExcluded(twitchUserId))
        {
            RefuseExcludedChannel(channel);
            return (null, true);
        }

        // The broadcaster lock's twin (#245), after the env list and at the same place: before the
        // duplicate lookup and the backfill, so a row created during a Helix outage after the purge
        // never stores the locked id from here, never gets a set and counts nothing. Silent like the
        // env case; the backoff counts it as a miss — intended, the identity reconcile deactivates the
        // row within the hour. No failure reason is recorded: 7TV answered fine.
        if (await IsLockedByBroadcasterAsync(twitchUserId, cancellationToken))
        {
            RefuseLockedChannel(channel);
            return (null, true);
        }

        // A rename leaves this exact shape: a second row under the new name, still without its own
        // TwitchChannelId, resolving to the Twitch account the original row already holds. Writing it
        // here via the backfill below would collide with the unique index on Channel.TwitchChannelId —
        // so this row is left untouched. Reconciling the duplicate (folding it into the original, or
        // vice versa) is not this method's job.
        var existingOwner = await db.LoadChannelByTwitchIdAsync(twitchUserId, cancellationToken);
        if (existingOwner is not null && existingOwner.Id != channel.Id)
        {
            logger.LogWarning(
                "SyncChannelAsync: {Channel} ({ChannelId}) löst dieselbe Twitch-ID {TwitchId} auf wie bereits getrackter Channel {ExistingChannel} ({ExistingChannelId}) — vermutlich ein Rename-Duplikat, Sync übersprungen.",
                normalized, channel.Id, twitchUserId, existingOwner.ChannelName, existingOwner.Id);
            return (null, false);
        }

        return (twitchUserId, false);
    }

    private void LogResolutionMiss(Channel channel, bool silent, int misses, TimeSpan delay)
    {
        // An excluded or locked channel backs off like any other, but silently: a recurring line
        // naming it would tie the block to that channel, which RefuseExcludedChannel keeps at Debug
        // for (and RefuseLockedChannel follows suit).
        if (misses >= MissesBeforeLogging && !silent)
        {
            logger.LogInformation(
                "Twitch id of {Channel} ({ChannelId}) still unresolved after {Misses} attempts in a row, next 7TV search in {DelaySeconds}s.",
                channel.ChannelName, channel.Id, misses, (long)delay.TotalSeconds);
        }
    }

    /// <summary>
    /// A successful response with an empty emote list is indistinguishable from a real set wipe, but
    /// the consequences are wildly asymmetric: ReconcileSlotsAsync would archive every emote of the
    /// channel and RefreshMatchCacheAsync would install an empty dictionary, so chat matching stops entirely
    /// until a later sync fills the set again (thousands of lost matches at HandOfBlood's message
    /// rate). Known triggers: a set change in progress, a partial 7TV outage, an owner emptying the
    /// set. A zero is therefore held back, but not forever — a set that really is empty would
    /// otherwise stay frozen at its old content indefinitely (issue #76). It is believed when
    /// <list type="bullet">
    /// <item>it comes with a new set id (<paramref name="emoteSetSwitched"/>): a freshly created set
    /// is expected to be empty, or</item>
    /// <item>it is the same set's zero for the configured number of consecutive, time-spaced syncs
    /// (<see cref="IEmptySetConfirmationTracker"/>); any other observation — a non-empty answer, a
    /// failed or unusable lookup, a live push — starts the count over.</item>
    /// </list>
    /// Neither applies when v4 contradicts the zero (<see cref="SevenTvEmoteSet.RemoteEntryCount"/>
    /// above 0): v3's REST answer can lag the set by 10-30 min, so v4 listing entries for the very
    /// same set is treated as evidence that it is not empty. Such a zero is held back and resets the
    /// streak like a push does, also for a new set id; it is not a failure, so no failure reason is
    /// recorded. An unknown (null) or zero v4 count does not veto. v4's own freshness is not
    /// measured systematically, and the veto does not rely on it: it can only hold a zero back, so a
    /// lagging v4 merely delays accepting a really emptied set until it catches up (liveness, not
    /// safety).
    /// Every entry point that reaches this method counts the same way — the periodic resync, boot
    /// recovery, the JOIN/RESYNC handlers and the EventAPI follow-ups all call SyncChannelAsync — and
    /// the tracker's spacing is what keeps a burst of them from confirming itself.
    /// <para>Returns the result to return immediately while the zero is still held back, or null when
    /// the caller should continue the normal sync.</para>
    /// </summary>
    private async Task<SevenTvSyncResult?> TryGuardAgainstImplausibleWipeAsync(
        Channel channel, string normalized, SevenTvEmoteSet emoteSet, bool emoteSetSwitched, SevenTvChannelState state, CancellationToken cancellationToken)
    {
        if (emoteSet.Emotes.Count != 0)
        {
            emptySetConfirmations.Reset(channel.ChannelName);
            return null;
        }

        var knownActiveEmotes = await db.Emotes
            .CountAsync(e => e.ChannelId == channel.Id && !e.IsArchived, cancellationToken);
        if (knownActiveEmotes == 0)
        {
            emptySetConfirmations.Reset(channel.ChannelName);
            return null;
        }

        // Ahead of the switch acceptance on purpose: a new set id that v3 shows empty but v4 shows
        // filled is the same v3 lag as on the old set, and accepting it would archive everything.
        if (emoteSet.RemoteEntryCount is > 0)
        {
            emptySetConfirmations.Reset(channel.ChannelName);
            logger.LogWarning(
                "7TV v3 reports 0 emotes for set {SetId} of {Channel} but v4 lists {RemoteCount} entries — zero rejected, sync skipped ({Count} known emotes kept).",
                emoteSet.Id, normalized, emoteSet.RemoteEntryCount, knownActiveEmotes);
            return SevenTvSyncResult.Create(channel.ChannelName, emoteSet.Id, state.SevenTvUserId, hasChanges: false);
        }

        if (emoteSetSwitched)
        {
            emptySetConfirmations.Reset(channel.ChannelName);
            logger.LogInformation(
                "7TV reports an empty new set {SetId} for {Channel} — accepted, {Count} known emotes are archived.",
                emoteSet.Id, normalized, knownActiveEmotes);
            return null;
        }

        var verdict = emptySetConfirmations.ObserveZero(channel.ChannelName, emoteSet.Id);
        if (verdict.Accept)
        {
            emptySetConfirmations.Reset(channel.ChannelName);
            logger.LogInformation(
                "7TV has reported 0 emotes for set {SetId} of {Channel} in {Streak} consecutive syncs — accepted as really empty, {Count} known emotes are archived.",
                emoteSet.Id, normalized, verdict.Streak, knownActiveEmotes);
            return null;
        }

        // Only a counted zero is logged at Warning (at most once per spacing window and channel, so
        // about once a minute while it lasts — the same volume as before); a zero that was too close
        // to the previous one changes nothing and stays at Debug.
        logger.Log(
            verdict.Counted ? LogLevel.Warning : LogLevel.Debug,
            "7TV reports 0 active emotes for {Channel}, although {Count} were known — sync skipped (empty answer {Streak} of {Required} needed to accept it; {CrossCheck}).",
            normalized, knownActiveEmotes, verdict.Streak, verdict.Required,
            emoteSet.RemoteEntryCount is null ? "v4 cross-check unavailable" : "v4 lists 0 entries too");
        return SevenTvSyncResult.Create(channel.ChannelName, emoteSet.Id, state.SevenTvUserId, hasChanges: false);
    }

    /// <summary>
    /// Records why an attempt produced nothing. Writes the reason and the attempt timestamp and
    /// nothing else — deliberately not <c>ActiveEmoteSetId</c>, the capacity or any emote row: a
    /// 7TV outage must not take the mass-delete panel away or archive a whole set, and
    /// <c>LastSyncedAtUtc</c> keeps meaning "last *successful* sync".
    /// </summary>
    private Task RecordFailedAttemptAsync(Channel channel, SevenTvLookupStatus status, CancellationToken cancellationToken) =>
        RecordFailedAttemptAsync(channel, SevenTvSyncFailureReasons.FromStatus(status), cancellationToken);

    /// <summary>
    /// The reason-taking half, for the one failure that has no <see cref="SevenTvLookupStatus"/>
    /// behind it (see <see cref="SevenTvSyncFailureReasons.ResponseUnusable"/>).
    /// </summary>
    private async Task RecordFailedAttemptAsync(Channel channel, string? reason, CancellationToken cancellationToken)
    {
        // A failed or unusable lookup is an observation too, and not a zero: the empty-set streak
        // counts consecutive observations, so it starts over here (issue #76).
        emptySetConfirmations.Reset(channel.ChannelName);

        // Logged only when the reason changes: the periodic resync runs this for every broken
        // channel every 60 seconds, and an unconditional line would bury everything else in the log.
        // The stored value is what the UI reads, so nothing is lost by staying quiet.
        var changed = channel.LastSyncFailureReason != reason;

        channel.LastSyncAttemptAtUtc = DateTime.UtcNow;
        channel.LastSyncFailureReason = reason;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsRowVanishedFor(ex, channel.Id))
        {
            // Same expected interleaving as in SyncChannelAsync: the row was purged or merged away
            // while the 7TV call was in flight. Nothing to record; do not poison the shared context.
            db.ChangeTracker.Clear();
            logger.LogInformation("SyncChannelAsync: row vanished while recording a failed attempt — nothing recorded.");
            return;
        }

        if (changed)
        {
            logger.LogInformation(
                "7TV-Sync für {Channel} ohne Ergebnis: {Reason}.", channel.ChannelName, reason);
        }
    }

    private Task RefreshMatchCacheAsync(Channel channel, CancellationToken cancellationToken)
        => RefreshMatchCacheAsync(channel, channel.ChannelName, cancellationToken);

    private async Task RefreshMatchCacheAsync(Channel channel, string channelName, CancellationToken cancellationToken)
    {
        // Plan #373 §3.2: one entry per settled slot, with the slot id as value. Nothing here writes: a
        // slot is only ever created by a reconcile.
        var (nameToSlotKey, duplicateNames) = await BuildSlotMatchMapAsync(channel, channelName, cancellationToken);

        if (duplicateNameTracker.Update(channelName, duplicateNames))
        {
            if (duplicateNames.Count > 0)
            {
                logger.LogWarning(
                    "{Count} active emote names in channel {Channel} are shared by more than one emote: {Names} — chat matching counts each name for one emote id only.",
                    duplicateNames.Count, channelName,
                    string.Join(", ", duplicateNames.Order(StringComparer.Ordinal)));
            }
            else
            {
                logger.LogInformation(
                    "Name collisions in channel {Channel} resolved — all active emote names are unique again.",
                    channelName);
            }
        }

        // The set id comes off the very row this method was handed, and that row is also where the
        // sync writes ActiveEmoteSetId — so the set the dictionary was built for travels with it
        // without a second lookup that could answer for a different moment. This holds for the warm
        // start too: it runs before either 7TV call and therefore pairs the emote rows in Postgres
        // with the set id those rows were last reconciled against.
        var previous = emoteMatchCache.GetChannelSnapshot(channelName);
        emoteMatchCache.ReplaceChannel(channelName, channel.ActiveEmoteSetId, nameToSlotKey);

        // Only a swap between two known sets is worth a line — it is what makes the delay between
        // a set switch on 7TV and our observing it measurable in production. Two non-cases: a first
        // population replaces the empty snapshot (EmoteSetId == ""), which is not a switch; and a
        // refresh onto the same set is the ordinary resync tick, which runs once a minute per
        // channel and would drown the log. The new generation is read back rather than guessed,
        // because ReplaceChannel stamps it.
        if (previous.EmoteSetId.Length > 0
            && !string.Equals(previous.EmoteSetId, channel.ActiveEmoteSetId, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Match cache for {Channel} switched from set {OldSetId} (generation {OldGeneratedAt}) to {NewSetId} (generation {NewGeneratedAt})",
                channelName,
                previous.EmoteSetId,
                previous.GeneratedAtUtc,
                channel.ActiveEmoteSetId,
                emoteMatchCache.GetChannelSnapshot(channelName).GeneratedAtUtc);
        }
    }

    /// <summary>
    /// Plan #373, E7 (D15, AK 15, AK 17): one entry per active, settled slot (not archived,
    /// not an unsettled seed) of an active emote, under the slot's name, with the slot id as value —
    /// except for every emote that still has an unsettled seed of the active set, which is left out
    /// with all of its slots, settled ones included, until a dated reconcile settles it (a deferred
    /// settle keeps it out, spec §14 (c)). "Seed of the active set" is the settle's own population
    /// (<see cref="SetSeeds.SeedsOfSet"/>), so map and settle can never disagree. No virtual entries: an
    /// active emote without an active own slot has no slot id and counts from the next reconcile on.
    /// The collision report reads exactly this input, as distinct (name, emote) pairs, so two aliases
    /// of one emote are no collision. An information line names the number of left-out emotes when it
    /// changes for the channel. Read-only.
    /// </summary>
    private async Task<(Dictionary<string, string> NameToSlotKey, IReadOnlySet<string> DuplicateNames)> BuildSlotMatchMapAsync(
        Channel channel, string channelName, CancellationToken cancellationToken)
    {
        var activeSetSeeds = db.SeedsOfSet(channel.Id, channel.ActiveEmoteSetId);
        var entries = await db.EmoteSlots
            .Where(s => !s.IsArchived && !s.SeedUnsettled
                        && s.Emote.ChannelId == channel.Id && !s.Emote.IsArchived
                        && !activeSetSeeds.Any(seed => seed.EmoteId == s.EmoteId))
            .Select(s => new { s.Name, s.Id, s.EmoteId })
            .ToListAsync(cancellationToken);

        var excludedEmotes = await activeSetSeeds
            .Where(seed => !seed.Emote.IsArchived)
            .Select(seed => seed.EmoteId)
            .Distinct()
            .CountAsync(cancellationToken);
        if (exclusionTracker.Update(channelName, excludedEmotes))
        {
            if (excludedEmotes > 0)
            {
                logger.LogInformation(
                    "{Channel}: {Count} active emotes are not counted until their unsettled seed of the active set is settled.",
                    channelName, excludedEmotes);
            }
            else
            {
                logger.LogInformation(
                    "{Channel}: every active emote with a slot is counted again — no unsettled seed of the active set is left.",
                    channelName);
            }
        }

        var map = EmoteNameMatching.Coalesce(entries.Select(e => new KeyValuePair<string, string>(e.Name, e.Id)));
        var duplicateNames = EmoteNameMatching.Coalesce(
            entries
                .Select(e => new KeyValuePair<string, string>(e.Name, e.EmoteId))
                .Distinct()).AmbiguousNames;
        return (map.NameToId, duplicateNames);
    }

    /// <summary>
    /// The slot-aware REST reconcile (spec §6.2–6.5, plan #372 2.2–2.7). Runs inside the
    /// save's transaction, after <see cref="ApplyAndSaveAsync"/> has locked the channel row (E23):
    /// <list type="number">
    /// <item>locks every emote row this pass can write (E3, one sorted statement);</item>
    /// <item>reads the channel's emotes with all their slots behind that lock, from the database (no
    /// stale tracked instance survives);</item>
    /// <item>checks lock coverage (E24): every row the lock predicate selects on the re-read state must
    /// be locked, otherwise <see cref="LockCoverageMissException"/> makes the save restart from step
    /// 1;</item>
    /// <item>finds the unsettled seeds of the active set with one usage query (plan 2.2 step 6);</item>
    /// <item>per live group: step 1 on the emote row (find, create, un-archive, image — never a
    /// rename), then <see cref="EmoteSlotReconcilePlanner.PlanLiveGroup"/> and its application
    /// (slots, D13 name and date);</item>
    /// <item>per emote without live entry: its active own slots are archived, then the emote-level
    /// archive, leave detection and measurement log (<see cref="ArchiveAbsentEmotesAsync"/>);</item>
    /// <item>moves the active-set usage rows of every seed settled onto an existing target, as a merge
    /// (plan #373 E10), and the backfill snapshot rows pointing at it (E11a), then upserts the
    /// leaves.</item>
    /// </list>
    /// Returns whether the visible inventory changed (E18) and the counts for the per-channel log
    /// lines, which <see cref="ApplyAndSaveAsync"/> writes after the commit.
    /// </summary>
    private async Task<(bool Changed, SlotReconcileSummary Summary)> ReconcileSlotsAsync(
        string channelId, string activeEmoteSetId, IReadOnlyList<SevenTvEmote> liveEmotes, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var liveIds = liveEmotes.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        // E3: every row this pass can write — live, active, or owning an active own slot.
        var locked = (await db.LockEmotesForReconcileAsync(channelId, liveIds, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        // Behind the lock, from the database: a tick's context may still track this channel's rows
        // from an earlier pass, and a tracking query would hand those instances back unrefreshed.
        DetachTrackedEmotesOf(channelId);
        var emotes = await db.Emotes
            .Include(e => e.Slots)
            .Where(e => e.ChannelId == channelId)
            .ToListAsync(cancellationToken);

        // E24: a writer that does not take the channel row (a ballot or backfill upsert of an
        // archived row, a report un-archiving one) can create or reactivate a row between the lock
        // statement's snapshot and this read. Writing such a row unlocked would let two resolutions of
        // the same emote run side by side, so the whole save starts over and locks it as well.
        if (emotes.Any(e => RowLocks.IsReconcileTarget(e, liveIds) && !locked.Contains(e.Id)))
        {
            throw new LockCoverageMissException();
        }

        var existing = emotes.ToDictionary(e => e.SevenTvEmoteId, StringComparer.Ordinal);
        var activeSetSeedIds = await LoadActiveSetSeedIdsAsync(channelId, activeEmoteSetId, cancellationToken);

        var changed = false;
        var counts = new SlotReconcileCounter();
        var rebinds = new List<SlotUsageRebind>();

        // GroupBy keeps the groups in order of first appearance and each group's entries in v3 order.
        foreach (var group in liveEmotes.GroupBy(e => e.Id, StringComparer.Ordinal))
        {
            existing.TryGetValue(group.Key, out var known);
            // The planner needs the name and the active state from before step 1 (E18, virtual slot).
            var nameBefore = known?.Name ?? string.Empty;
            var wasActive = known is { IsArchived: false };

            var (emote, rowChanged) = UpsertEmoteRowForSlots(channelId, existing, group.First(), now);
            changed |= rowChanged;

            var plan = EmoteSlotReconcilePlanner.PlanLiveGroup(
                group.Select(e => new SlotLiveEntry(e.Name, e.AddedToSetAt)).ToList(),
                emote.Slots.Select(SlotState.From).ToList(),
                activeSetSeedIds,
                nameBefore,
                wasActive,
                now);
            if (plan.SettleDeferred)
            {
                // §6.3: the group waits for v4 dates; only step 1 applies.
                counts.Deferred++;
                continue;
            }

            ApplySlotPlan(emote, plan, now, counts);
            rebinds.AddRange(plan.UsageRebinds);
            changed |= plan.InventoryChanged;

            if (plan.Derivation is { } derived)
            {
                // D13 strictly (plan 2.3 step 8): FirstSeenAt follows the oldest active slot, null
                // included. A changed name is already part of plan.InventoryChanged.
                if (!string.Equals(emote.Name, derived.Name, StringComparison.Ordinal))
                {
                    emote.Name = derived.Name;
                    emote.LastSyncedAt = now;
                }

                emote.FirstSeenAt = derived.FirstSeenAt;
            }
        }

        // Spec §6.2 "after all groups": the active own slots of an emote without live entry are
        // archived — also under an emote that is archived already (a delta pull before this tick).
        // Unsettled seeds stay as they are. Never an inventory change on its own (E18).
        foreach (var emote in emotes.Where(e => !liveIds.Contains(e.SevenTvEmoteId)))
        {
            ApplySlotPlan(emote, EmoteSlotReconcilePlanner.PlanAbsentEmote(emote.Slots.Select(SlotState.From).ToList(), now), now, counts);
        }

        var (absentChanged, leaves) = await ArchiveAbsentEmotesAsync(channelId, activeEmoteSetId, existing, liveIds, now, cancellationToken);
        changed |= absentChanged;

        // Lock order E4: usage rows (settle), then the backfill snapshot rows, before the observation
        // rows. Only the rows of the active set move; the seed's other sets keep theirs (D12).
        foreach (var rebind in rebinds.OrderBy(r => r.FromSeedId, StringComparer.Ordinal))
        {
            var (moved, merged) = await SeedSettleWrites.MoveSeedUsageAsync(db, rebind, activeEmoteSetId, cancellationToken);
            counts.ReboundRows += moved + merged;
            counts.MergedRows += merged;
            var (snapshotMoved, snapshotDropped) = await SeedSettleWrites.MoveSeedSnapshotEntriesAsync(db, rebind, cancellationToken);
            counts.SnapshotEntries += snapshotMoved + snapshotDropped;
        }

        await EmoteSetLeaveObservations.RecordAsync(db, channelId, activeEmoteSetId, leaves, now, cancellationToken);

        AssertWritesAreLocked(channelId, locked);
        return (changed, counts.ToSummary());
    }

    /// <summary>
    /// The ids of the channel's unsettled seeds that carry usage rows of the active set (plan 2.2 step
    /// 6, spec §6.3).
    /// </summary>
    private async Task<IReadOnlySet<string>> LoadActiveSetSeedIdsAsync(
        string channelId, string activeEmoteSetId, CancellationToken cancellationToken)
    {
        var ids = await db.SeedsOfSet(channelId, activeEmoteSetId)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Step 1 of spec §6.2: find, create or un-archive the emote row, image from the group's first
    /// entry, entry stamp (E34) and placeholder marker (D37) — but never a rename and never a date:
    /// both are derived from the slots afterwards (D13). Returns the row and
    /// whether this step changed it visibly (created, un-archived, image).
    /// </summary>
    private (Emote Emote, bool Changed) UpsertEmoteRowForSlots(
        string channelId, Dictionary<string, Emote> existing, SevenTvEmote first, DateTime now)
    {
        if (existing.TryGetValue(first.Id, out var emote))
        {
            // Listed in the active set means observed there (D37). Not a change.
            if (emote.IsPlaceholder && !emote.IsArchived)
            {
                emote.IsPlaceholder = false;
            }

            if (emote.ImageUrl == first.ImageUrl && !emote.IsArchived)
            {
                return (emote, false);
            }

            if (emote.IsArchived)
            {
                // E34 and D37: an un-archive is a fresh entry into the set, which opens the credibility
                // window for the next REST leave, and the emote is no placeholder any more.
                emote.LastEnteredSetAtUtc = now;
                emote.IsPlaceholder = false;
            }

            emote.ImageUrl = first.ImageUrl;
            emote.IsArchived = false;
            emote.ArchivedAt = null;
            emote.LastSyncedAt = now;
            return (emote, true);
        }

        // Name and FirstSeenAt are provisional here; the plan's derivation sets both.
        emote = new Emote
        {
            ChannelId = channelId,
            SevenTvEmoteId = first.Id,
            Name = first.Name,
            ImageUrl = first.ImageUrl,
            FirstSeenAt = first.AddedToSetAt,
            LastEnteredSetAtUtc = now,
            LastSyncedAt = now
        };
        db.Emotes.Add(emote);
        existing[first.Id] = emote;
        return (emote, true);
    }

    /// <summary>
    /// Applies a planner result to the tracked slots of <paramref name="emote"/>: planned updates onto
    /// the slots of the same id, created slots as new entities. Usage rebinds and the derivation are
    /// the caller's.
    /// </summary>
    private void ApplySlotPlan(Emote emote, SlotReconcilePlan plan, DateTime now, SlotReconcileCounter counts)
    {
        if (plan.UpdatedSlots.Count > 0)
        {
            var slotsById = emote.Slots.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var state in plan.UpdatedSlots)
            {
                var slot = slotsById[state.Id];
                if (slot.SeedUnsettled && !state.SeedUnsettled)
                {
                    counts.Settled++;
                }

                state.ApplyTo(slot, now);
                counts.Updated++;
            }
        }

        foreach (var state in plan.CreatedSlots)
        {
            db.EmoteSlots.Add(state.ToNewEntity(emote.Id, now));
            counts.Created++;
        }
    }

    /// <summary>
    /// Plan #372 2.2 step 4: before the reconcile reads the channel's emotes, no instance of them (or
    /// of their slots) may stay tracked from an earlier pass in the same context, because a tracking
    /// query returns tracked instances without refreshing them. Unchanged entries are detached; a
    /// pending change on one of them at this point is a programming error.
    /// </summary>
    private void DetachTrackedEmotesOf(string channelId)
    {
        var emoteEntries = db.ChangeTracker.Entries<Emote>()
            .Where(e => e.Entity.ChannelId == channelId)
            .ToList();
        var emoteIds = emoteEntries.Select(e => e.Entity.Id).ToHashSet(StringComparer.Ordinal);
        var slotEntries = db.ChangeTracker.Entries<EmoteSlot>()
            .Where(e => emoteIds.Contains(e.Entity.EmoteId))
            .ToList();

        if (emoteEntries.Any(e => e.State != EntityState.Unchanged) || slotEntries.Any(e => e.State != EntityState.Unchanged))
        {
            throw new InvalidOperationException(
                $"Channel {channelId} has pending emote or slot changes before the slot reconcile reads its rows.");
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

    /// <summary>
    /// Plan #372 E24, second half: every emote row the save will change is locked or new in this
    /// attempt, and so is the owner of every slot it will write. This is what keeps the sync out of
    /// lock cycle Z1 (and the delta path, plan #374 3.2 step 3); a violation is a programming
    /// error (no retry).
    /// </summary>
    private void AssertWritesAreLocked(string channelId, IReadOnlySet<string> locked)
    {
        var allowed = new HashSet<string>(locked, StringComparer.Ordinal);
        foreach (var entry in db.ChangeTracker.Entries<Emote>().Where(e => e.Entity.ChannelId == channelId))
        {
            if (entry.State == EntityState.Added)
            {
                allowed.Add(entry.Entity.Id);
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted && !locked.Contains(entry.Entity.Id))
            {
                throw new InvalidOperationException(
                    $"A slot-aware write of channel {channelId} is about to change emote row {entry.Entity.Id} without holding its lock.");
            }
        }

        foreach (var entry in db.ChangeTracker.Entries<EmoteSlot>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && !allowed.Contains(entry.Entity.EmoteId))
            {
                throw new InvalidOperationException(
                    $"A slot-aware write of channel {channelId} is about to write slot {entry.Entity.Id} of an emote row it does not hold.");
            }
        }
    }

    /// <summary>
    /// Plan #372 E19: the per-channel lines of a slot reconcile, written after the commit so a retried
    /// save logs once. The settle line's wording is what the deploy check reads (spec §11.2 step 6 a);
    /// the summary only appears when a slot was written, and names no emote.
    /// </summary>
    private void LogSlotSummary(string channelName, SlotReconcileSummary summary)
    {
        if (summary.Deferred > 0)
        {
            logger.LogInformation(
                "{Channel}: settle deferred: v4 dates missing for {Count} emotes", channelName, summary.Deferred);
        }

        if (summary.Created > 0 || summary.Updated > 0)
        {
            logger.LogInformation(
                "{Channel}: slot reconcile created {Created} and updated {Updated} slots, settled {Settled} seeds and moved {ReboundRows} usage rows and {SnapshotEntries} backfill snapshot entries.",
                channelName, summary.Created, summary.Updated, summary.Settled, summary.ReboundRows, summary.SnapshotEntries);
        }

        if (summary.MergedRows > 0)
        {
            // Plan #373 E10: no live path is known to collide, so a merge is worth a look.
            logger.LogWarning(
                "{Channel}: settle merged {MergedRows} usage rows into existing cells of their target slots.",
                channelName, summary.MergedRows);
        }
    }

    /// <summary>
    /// The emote-level half of spec §6.2 "after all groups", unchanged by the slot work: archives every
    /// active row without a live entry and decides the credible leaves. Returns whether a row was
    /// archived and the 7TV ids to record as leaves; the caller upserts them after the settle's usage
    /// rebinds (lock order E4).
    /// </summary>
    private async Task<(bool Changed, List<string> Leaves)> ArchiveAbsentEmotesAsync(
        string channelId,
        string activeEmoteSetId,
        Dictionary<string, Emote> existing,
        IReadOnlySet<string> liveIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var credibleIfEnteredBefore = now - TagLeaveCredibilityWindow;
        var leaves = new List<string>();
        var postCheck = new List<Emote>();

        foreach (var (sevenTvEmoteId, emote) in existing)
        {
            if (!liveIds.Contains(sevenTvEmoteId) && !emote.IsArchived)
            {
                // Measurement only (no protection): 7TV's REST cache can lag 10-30 min behind
                // (SevenTV/SevenTV#81), so a resync may archive an emote a live dispatch added
                // moments ago — and dispatches never repeat. Logged to quantify how often that
                // actually happens before deciding whether a guard is worth weakening the
                // reconciliation for. The archive itself stays unguarded; whether the leave is
                // recorded is decided separately, by TagLeaveCredibilityWindow (b1 below).
                if (emote.LastSyncedAt > DateTime.UtcNow.AddMinutes(-15))
                {
                    logger.LogInformation(
                        "REST-Resync archiviert Emote {Name} ({SevenTvEmoteId}), das vor weniger als 15 Minuten synchronisiert wurde — möglicher 7TV-REST-Cache-Lag.",
                        emote.Name, sevenTvEmoteId);
                }

                emote.IsArchived = true;
                emote.ArchivedAt = DateTime.UtcNow;
                // D37: the row was active, so it is no placeholder, whatever a stale marker says. Part
                // of the same archive change, so no extra inventory change.
                emote.IsPlaceholder = false;
                changed = true;

                // (b1)
                if (IsCredibleRestLeave(emote, credibleIfEnteredBefore))
                {
                    leaves.Add(sevenTvEmoteId);
                }
            }
            else if (!liveIds.Contains(sevenTvEmoteId) && !IsNeverActivePlaceholder(emote) && IsCredibleRestLeave(emote, credibleIfEnteredBefore))
            {
                // (b2) archived before this pass; decided after the loop with one read. A placeholder
                // never was in the active set, so it has nothing to leave (D37).
                postCheck.Add(emote);
            }
        }

        if (postCheck.Count > 0)
        {
            var latest = await EmoteSetLeaveObservations.LoadLatestAsync(
                db, channelId, activeEmoteSetId, postCheck.ConvertAll(e => e.SevenTvEmoteId), cancellationToken);
            foreach (var emote in postCheck)
            {
                var observedSinceEntry = latest.TryGetValue(emote.SevenTvEmoteId, out var observedAt)
                    && (emote.LastEnteredSetAtUtc is null || observedAt > emote.LastEnteredSetAtUtc);
                if (!observedSinceEntry)
                {
                    leaves.Add(emote.SevenTvEmoteId);
                }
            }
        }

        return (changed, leaves);
    }

    /// <summary>
    /// True only for a unique violation on the (ChannelId, SevenTvEmoteId) index — the one conflict
    /// a concurrent vote-session upsert can cause. Every other failure propagates unchanged.
    /// </summary>
    private static bool IsEmoteKeyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: EmoteKeyIndexName,
        };

    // D37: a placeholder that never was in the active set. Both halves: a real placeholder is created
    // archived without an archive date and never gets one, because every archive (sync, dispatch pull,
    // set-centric report, in every image since ArchivedAt exists) only touches active rows and stamps
    // ArchivedAt. A marked row WITH a date was active once, so its marker is stale (left by an image
    // older than the marker) and its leave must be observed like any other.
    private static bool IsNeverActivePlaceholder(Emote emote) =>
        emote.IsPlaceholder && emote.ArchivedAt is null;

    // E34: a REST-observed leave counts only for a row that entered the set before the window, or
    // whose entry time is unknown (rows from before the column existed are certainly older).
    private static bool IsCredibleRestLeave(Emote emote, DateTime credibleIfEnteredBefore) =>
        emote.LastEnteredSetAtUtc is null || emote.LastEnteredSetAtUtc <= credibleIfEnteredBefore;

    // The raw leave-observation upsert's shape of "the row is gone": the channel foreign key, raised
    // straight from ExecuteSqlRawAsync as a PostgresException, not wrapped in the DbUpdateException
    // IsRowVanishedFor inspects. The table is the observation table, so the key is always this sync's
    // own channel id.
    private static bool IsLeaveObservationOrphan(Exception exception) =>
        exception is PostgresException
        {
            SqlState: PostgresErrorCodes.ForeignKeyViolation,
            TableName: "EmoteSetLeaveObservations",
        };

    // Removes the cache entry under the login the warm-up filled it under (the snapshot, E28) — unless
    // another active row carries that login now (login swap, double rename): that row's live entry
    // must survive, and the convergence net is the backstop for a genuine ghost.
    private async Task RemoveOldNameCacheEntryAsync(ChannelCacheIdentity cacheIdentity, CancellationToken cancellationToken)
    {
        var (channelId, oldName) = cacheIdentity;
        var heldElsewhere = await db.Channels
            .AsNoTracking()
            .AnyAsync(c => c.Id != channelId && c.IsBotActive && c.ChannelName == oldName, cancellationToken);
        if (!heldElsewhere)
        {
            emoteMatchCache.RemoveChannel(oldName);
        }
    }

    // The two shapes "the row is gone" takes at SaveChanges: the channel UPDATE matching no row
    // (purge, merge loser), or the emote INSERT hitting the channel foreign key (23503). Only when
    // every entry of the failed save belongs to this channel: the context is shared across a whole
    // resync tick, so a failure of an earlier channel's entry must not be blamed on this one.
    internal static bool IsRowVanishedFor(Exception exception, string channelId)
    {
        var vanished = exception is DbUpdateConcurrencyException
            || exception is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.ForeignKeyViolation } };
        if (!vanished || exception is not DbUpdateException { Entries.Count: > 0 } update)
        {
            return false;
        }

        return update.Entries.All(entry => entry.Entity switch
        {
            Channel c => c.Id == channelId,
            Emote e => e.ChannelId == channelId,
            // The epic's observation row rides the same save (and a set switch's own two saves):
            // its insert hits the same channel foreign key once the row is gone.
            ChannelEmoteSetObservation o => o.ChannelId == channelId,
            _ => false
        });
    }

    /// <summary>
    /// Plan #372 E28: the channel id and the login the match cache was warmed under, fixed before the
    /// save. A value, so no reload, retry or lock helper can change it behind the cleanup's back.
    /// </summary>
    private readonly record struct ChannelCacheIdentity(string ChannelId, string CacheName);

    /// <summary>The per-channel counts of one slot reconcile pass, for the E19 log lines.</summary>
    private readonly record struct SlotReconcileSummary(
        int Created, int Updated, int Settled, int ReboundRows, int MergedRows, int SnapshotEntries, int Deferred);

    private sealed class SlotReconcileCounter
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Settled { get; set; }

        // Usage rows that left a settled seed — re-keyed and merged ones alike.
        public int ReboundRows { get; set; }

        // The part of ReboundRows that was added onto an existing cell of the target (E10).
        public int MergedRows { get; set; }

        // Backfill snapshot rows rebound from a seed to its target (E11a).
        public int SnapshotEntries { get; set; }
        public int Deferred { get; set; }

        public SlotReconcileSummary ToSummary() => new(Created, Updated, Settled, ReboundRows, MergedRows, SnapshotEntries, Deferred);
    }

    /// <summary>
    /// Plan #372 E24: an emote row the reconcile can write was not covered by its lock. Caught by
    /// <see cref="SaveSyncAsync"/>, which restarts the save; after the last restart it propagates.
    /// </summary>
    private sealed class LockCoverageMissException()
        : InvalidOperationException("An emote row the slot reconcile can write appeared between its row lock and its read; the lock did not cover it.");

    /// <summary>
    /// Plan #374 E2: an emote row of a dispatch id was read behind the delta's locks without being
    /// covered by them (created between the id read and the lock by a writer without the channel lock).
    /// Caught by <see cref="ApplyEmoteSetUpdateAsync"/>, which retries the dispatch once (E9).
    /// </summary>
    private sealed class DeltaLockCoverageMissException()
        : InvalidOperationException("An emote row of the dispatch appeared between its row lock and its read; the lock did not cover it.");

    /// <summary>
    /// Plan #372 E23: the channel row lock found no row — a purge committed meanwhile. Ends in the
    /// "row vanished" exit of <see cref="SaveSyncAsync"/>.
    /// </summary>
    private sealed class ChannelRowVanishedException()
        : InvalidOperationException("The channel row was deleted while the sync was in flight.");

}
