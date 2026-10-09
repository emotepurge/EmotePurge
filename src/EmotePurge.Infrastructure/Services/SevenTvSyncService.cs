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
    ILogger<SevenTvSyncService> logger)
    : ISevenTvSyncService
{
    private const string EmoteKeyIndexName = "IX_Emotes_ChannelId_SevenTvEmoteId";

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

        // The row as the warm-up saw it. The save's E10 retry may hand back a re-read instance, but
        // any cache entry the warm-up left behind sits under this one's login.
        var loaded = channel;
        var saved = await SaveSyncAsync(channel, normalized, twitchUserId, emoteSet, cancellationToken);
        if (saved is null)
        {
            return null;
        }

        (channel, var inventoryChanged) = saved.Value;
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
        if (current is null || !current.IsBotActive)
        {
            await RemoveOldNameCacheEntryAsync(loaded, cancellationToken);
            logger.LogInformation("SyncChannelAsync: row was deleted or deactivated while the sync was in flight — match cache left empty.");
            return null;
        }

        var finalName = current.ChannelName;
        if (!string.Equals(finalName, loaded.ChannelName, StringComparison.Ordinal))
        {
            // Renamed meanwhile: the handover's LEAVE for the old login may already have run, so
            // anything the warm-up put under it would never be cleaned up by anyone.
            await RemoveOldNameCacheEntryAsync(loaded, cancellationToken);
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

        var existing = await db.Emotes
            .Where(e => e.ChannelId == channel.Id)
            .ToDictionaryAsync(e => e.SevenTvEmoteId, cancellationToken);

        // Same asymmetry as the empty-set guard in SyncChannelAsync: one malformed or malicious
        // delta that would archive the channel's entire active set kills chat matching until the
        // next resync. Simulated first, and skipped when the result would be a wipe — the caller
        // reacts with a full resync, which re-checks against 7TV's authoritative state.
        var activeBefore = existing.Values.Count(e => !e.IsArchived);
        var activeAfter = existing.Values
            .Where(e => !e.IsArchived)
            .Select(e => e.SevenTvEmoteId)
            .ToHashSet();
        foreach (var emote in delta.Pushed)
        {
            activeAfter.Add(emote.Id);
        }
        foreach (var emote in delta.Updated)
        {
            activeAfter.Add(emote.Id);
        }
        activeAfter.ExceptWith(delta.PulledIds);

        if (activeBefore > 0 && activeAfter.Count == 0)
        {
            logger.LogWarning(
                "7TV-Dispatch würde alle {Count} aktiven Emotes von {Channel} entfernen — als unplausibel übersprungen.",
                activeBefore, normalized);
            return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.ImplausibleSkipped, currentName);
        }

        // A dispatch that adds or changes an emote is live evidence that the set is not empty, so a
        // zero streak built from earlier resyncs is stale. Placed after the plausibility return and
        // regardless of Applied vs NoChange; pull-only dispatches prove nothing of the sort.
        if (delta.Pushed.Count > 0 || delta.Updated.Count > 0)
        {
            emptySetConfirmations.Reset(channel.ChannelName);
        }

        foreach (var emote in delta.Pushed)
        {
            // A push IS the moment the emote enters the set, so "now" is the true added-at — the
            // one place in the delta path where the date is known without asking v4.
            UpsertEmote(channel.Id, existing, emote with { AddedToSetAt = DateTime.UtcNow }, fromDispatch: true);
        }

        foreach (var emote in delta.Updated)
        {
            UpsertEmote(channel.Id, existing, emote, fromDispatch: true);
        }

        foreach (var pulledId in delta.PulledIds)
        {
            if (existing.TryGetValue(pulledId, out var emote) && !emote.IsArchived)
            {
                emote.IsArchived = true;
                emote.ArchivedAt = DateTime.UtcNow;
                emote.LastSyncedAt = DateTime.UtcNow;
                // D37: the row was active, so it is no placeholder, whatever a stale marker says.
                emote.IsPlaceholder = false;
            }
        }

        // A dispatch's REMOVE is always a credible leave (spec E34), so every pulled id is recorded —
        // whether its row exists, is archived already or is being archived just now. Recording only the
        // rows the loop above flips would lose exactly the case that matters: a REMOVE for a row a stale
        // REST resync archived minutes earlier without an observation (inside the window). That case
        // stages no change, so this runs before the NoChange guard, and the outcome stays NoChange (no
        // match-cache refresh, no live event). One explicit transaction per call that pulled anything, so
        // the observation and the archive commit together: the raw upsert does not join SaveChangesAsync's
        // implicit one. A dispatch without pulls has nothing to record and keeps the implicit transaction.
        // Observation rows first, then the emote rows — the same order as the set-centric delete report
        // in the Api, and the sync takes no tag table, so no lock cycle (spec 5.5 rule 6). A channel
        // purged meanwhile fails the upsert with 23503 and propagates, like the save's FK failure always has.
        await using var transaction = delta.PulledIds.Count > 0
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (transaction is not null)
        {
            await EmoteSetLeaveObservations.RecordAsync(
                db, channel.Id, emoteSetId, delta.PulledIds, DateTime.UtcNow, cancellationToken);
        }

        if (!db.ChangeTracker.HasChanges())
        {
            await CommitIfOpenAsync(transaction, cancellationToken);
            return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.NoChange, currentName);
        }

        await db.SaveChangesAsync(cancellationToken);
        await CommitIfOpenAsync(transaction, cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);

        logger.LogInformation(
            "7TV-Dispatch auf {Channel} angewendet: {Pushed} hinzugefügt, {Updated} aktualisiert, {Pulled} entfernt.",
            normalized, delta.Pushed.Count, delta.Updated.Count, delta.PulledIds.Count);
        return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.Applied, currentName);
    }

    /// <summary>
    /// Runs <see cref="ApplyAndSaveAsync"/> with both of the sync's recoveries around it: the single
    /// retry after a concurrent emote-row insert (spec E10), and the abandonment of a row that
    /// vanished while the 7TV call was in flight. Returns the row the save finally wrote (the
    /// retry re-reads it) and whether an emote row changed, or null when the row is gone — in which
    /// case nothing is left behind and the caller must return null as well.
    /// </summary>
    private async Task<(Channel Channel, bool InventoryChanged)?> SaveSyncAsync(
        Channel channel, string normalized, string twitchUserId, SevenTvEmoteSet emoteSet, CancellationToken cancellationToken)
    {
        // The row as the warm-up saw it: its login is the one any cache entry left behind sits under.
        var loaded = channel;
        try
        {
            try
            {
                return (channel, await ApplyAndSaveAsync(channel, twitchUserId, emoteSet, cancellationToken));
            }
            catch (DbUpdateException ex) when (IsEmoteKeyConflict(ex))
            {
                // Spec E10: the Api's vote-session upsert (INSERT … ON CONFLICT DO NOTHING) inserted
                // one of this set's members between ReconcileAsync's read and the save above. Retried
                // exactly once from a clean change tracker and a re-read row, still under both gates;
                // the retry reads the Api's row and adopts it instead of inserting. A second conflict
                // propagates.
                logger.LogWarning(
                    "SyncChannelAsync: an emote row of {Channel} ({ChannelId}) was inserted concurrently during the sync — retrying once.",
                    normalized, channel.Id);
                db.ChangeTracker.Clear();
                var reloaded = await db.Channels.SingleOrDefaultAsync(c => c.Id == channel.Id, cancellationToken);
                if (reloaded is null)
                {
                    // The row gate does not hold off a purge or a merge (see DECISIONS), so the row
                    // can be gone here — the same "row vanished" exit as below.
                    await AbandonVanishedRowAsync(loaded, cancellationToken);
                    return null;
                }

                channel = reloaded;
                return (channel, await ApplyAndSaveAsync(channel, twitchUserId, emoteSet, cancellationToken));
            }
        }
        catch (Exception ex) when (IsRowVanishedFor(ex, channel.Id) || IsLeaveObservationOrphan(ex))
        {
            // The row was purged or merged away while the 7TV call was in flight. The row gate only
            // coordinates sync callers, not those writers (see DECISIONS), so this is an expected
            // interleaving, not a fault: drop the pending changes and leave nothing behind. The
            // attempt's transaction has already rolled back on dispose.
            await AbandonVanishedRowAsync(loaded, cancellationToken);
            return null;
        }
    }

    private async Task AbandonVanishedRowAsync(Channel loaded, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await RemoveOldNameCacheEntryAsync(loaded, cancellationToken);
        logger.LogInformation("SyncChannelAsync: row vanished while the sync was in flight — sync abandoned.");
    }

    /// <summary>
    /// The writing half of <see cref="SyncChannelAsync"/>: records the observed set, stamps the
    /// channel row, reconciles the emote rows and saves it all in one call. Split out so the
    /// unique-violation retry (spec E10) can run the exact same sequence a second time — the
    /// observation call included, because the row it may have staged is tracked only and a cleared
    /// change tracker would otherwise drop it. Returns true when an emote row changed.
    /// </summary>
    private async Task<bool> ApplyAndSaveAsync(
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

        // One explicit transaction per save attempt, so the leave observations ReconcileAsync upserts
        // (raw SQL, outside SaveChangesAsync's implicit transaction) commit together with the archive
        // and the channel row. Opened only here, after RecordObservedSetAsync: that call commits a set
        // switch in a transaction of its own, and EF cannot nest them. The assignments above are
        // in-memory and ride the same save. A failed attempt rolls back on dispose, and the E10 retry
        // in SaveSyncAsync runs this method again with a new transaction — the upsert is idempotent.
        // Lock order inside: observation rows, then the save's emote rows and the channel UPDATE. The
        // sync writes no tag table and a tag report only reads observations with a plain SELECT, so
        // the report never waits on an observation row. The only wait is the sync's, for the channel
        // row a report holds; the report holds nothing while it waits for its first lock, and neither
        // transaction does network I/O: no cycle, millisecond waits (spec 5.5 rule 6).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inventoryChanged = await ReconcileAsync(channel.Id, emoteSet.Id, emoteSet.Emotes, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inventoryChanged;
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
        if (emoteMatchCache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.Count != 0)
        {
            return;
        }

        await RefreshMatchCacheAsync(channel, cancellationToken);

        var warmedCount = emoteMatchCache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.Count;
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
    /// the consequences are wildly asymmetric: ReconcileAsync would archive every emote of the channel
    /// and RefreshMatchCacheAsync would install an empty dictionary, so chat matching stops entirely
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
        var activeEmotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
            .Select(e => new { e.Name, e.Id })
            .ToListAsync(cancellationToken);

        // 7TV active sets can legitimately contain two emotes sharing the same chat alias
        // (observed live) — ToDictionary would throw, so duplicates are coalesced instead,
        // keeping whichever was loaded first (the load order above has no OrderBy, so "first" is
        // whatever Postgres returns). Logged only when the collision set changes: this method
        // runs on every resync tick, and a static collision would spam the log otherwise. The
        // full current state is served by EmoteSetStatusService's DuplicateNames field instead.
        var (emoteNameToId, duplicateNames) = EmoteNameMatching.Coalesce(
            activeEmotes.Select(e => new KeyValuePair<string, string>(e.Name, e.Id)));

        if (duplicateNameTracker.Update(channelName, duplicateNames))
        {
            if (duplicateNames.Count > 0)
            {
                logger.LogWarning(
                    "{Count} doppelte aktive Emote-Namen in Channel {Channel}: {Names} — Chat-Matching zählt je Name nur auf die zuerst geladene Emote-Id.",
                    duplicateNames.Count, channelName,
                    string.Join(", ", duplicateNames.Order(StringComparer.Ordinal)));
            }
            else
            {
                logger.LogInformation(
                    "Namenskollisionen in Channel {Channel} aufgelöst — alle aktiven Emote-Namen sind wieder eindeutig.",
                    channelName);
            }
        }

        // The set id comes off the very row this method was handed, and that row is also where the
        // sync writes ActiveEmoteSetId — so the set the dictionary was built for travels with it
        // without a second lookup that could answer for a different moment. This holds for the warm
        // start too: it runs before either 7TV call and therefore pairs the emote rows in Postgres
        // with the set id those rows were last reconciled against.
        var previous = emoteMatchCache.GetChannelSnapshot(channelName);
        emoteMatchCache.ReplaceChannel(channelName, channel.ActiveEmoteSetId, emoteNameToId);

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
    /// Returns true when at least one emote row was added, archived or altered.
    /// <para>
    /// Also records the credible leaves from <paramref name="activeEmoteSetId"/> (spec 5.5 rule 5,
    /// E34) — immediately, as a raw upsert, so the caller must hold a transaction around this call and
    /// its save. A row missing from the live set is a credible leave only outside
    /// <see cref="TagLeaveCredibilityWindow"/> after it last entered the set (or when that is unknown).
    /// Recording is independent of the archive transition, because the archive loop skips rows that are
    /// archived already: (b1) a row archived now is observed when credible; (b2) a row archived
    /// earlier — typically inside the window, without an observation — is observed by the post-check
    /// once the window is over, but only when no observation from after its last entry exists yet, so
    /// it is written once and not on every resync while the stale cache still shows the row missing
    /// (counter-example 8). Observations are not inventory changes and never set the return value.
    /// </para>
    /// <para>
    /// (b2) skips placeholder rows (<see cref="IsNeverActivePlaceholder"/>, D37): they were created
    /// archived for an emote never observed in the active set, so their absence from it is no leave.
    /// Only (b2): a row that was active when this pass began was in the set, whatever its marker says,
    /// and (b1) records its leave as before and drops the marker.
    /// </para>
    /// </summary>
    private async Task<bool> ReconcileAsync(
        string channelId, string activeEmoteSetId, IReadOnlyList<SevenTvEmote> liveEmotes, CancellationToken cancellationToken)
    {
        var existing = await db.Emotes
            .Where(e => e.ChannelId == channelId)
            .ToDictionaryAsync(e => e.SevenTvEmoteId, cancellationToken);

        // 7TV can list one emote id twice in a set under two alias names, but the unique index
        // (ChannelId, SevenTvEmoteId) allows one row, so both entries hit it and the second would
        // rewrite the name back on every pass: the channel would report a change (and fire
        // channel.synced) on every resync. The LAST entry wins because that is the name the old
        // per-entry loop left stored and counted, so the deploy renames nothing and no usage series
        // switches to a different word. The other alias is not countable. Reversing twice keeps
        // the remaining entries in their original order.
        liveEmotes = liveEmotes.Reverse().DistinctBy(e => e.Id).Reverse().ToList();
        var liveIds = liveEmotes.Select(e => e.Id).ToHashSet();
        var changed = false;
        var now = DateTime.UtcNow;
        var credibleIfEnteredBefore = now - TagLeaveCredibilityWindow;
        var leaves = new List<string>();
        var postCheck = new List<Emote>();

        foreach (var emote in liveEmotes)
        {
            changed |= UpsertEmote(channelId, existing, emote);
        }

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

        await EmoteSetLeaveObservations.RecordAsync(db, channelId, activeEmoteSetId, leaves, now, cancellationToken);
        return changed;
    }

    /// <summary>
    /// Returns true when the row was created or actually modified.
    /// <para>
    /// <paramref name="fromDispatch"/> marks the EventAPI delta path, whose payloads are less
    /// complete and less trusted than a REST answer. Two behaviours hang off it, both about not
    /// letting a thin payload destroy known state.
    /// </para>
    /// </summary>
    private bool UpsertEmote(string channelId, Dictionary<string, Emote> existing, SevenTvEmote live, bool fromDispatch = false)
    {
        if (existing.TryGetValue(live.Id, out var emote))
        {
            // Correction, not write-once backfill: the column was originally filled from the v3
            // payload's timestamp, which turned out to be the emote's upload date rather than the
            // set-entry date — so a known value may be known-wrong, and the v4-sourced answer must
            // win. It also moves a re-added emote's date forward to its latest set entry. Kept
            // outside the change detection below: learning *when* an emote joined the set is not an
            // inventory change; counted as one, the first resync after a deploy would fire
            // channel.synced for every channel at once and make every open page refetch.
            //
            // REST only, because ApplyEmoteSetUpdateAsync decides NoChange vs Applied by asking the
            // ChangeTracker — a correction there would turn a no-op dispatch into a live event. The
            // periodic resync fills the same gap within a tick, so nothing is lost by waiting.
            // Null never overwrites: losing a known date to a failed v4 lookup would reopen the gap.
            if (!fromDispatch && live.AddedToSetAt is not null && emote.FirstSeenAt != live.AddedToSetAt)
            {
                emote.FirstSeenAt = live.AddedToSetAt;
            }

            // An active row still marked as a placeholder can only be left over from an image older
            // than the marker, which un-archives without clearing it: the old worker's sync or the old
            // Api's set-centric restore report, in the window between a manual migration and the
            // redeploy. Listed in the active set means observed there, so the
            // marker goes. REST only and outside the change detection, for the same reasons as the
            // FirstSeenAt correction above; an archived placeholder is cleared by the un-archive below.
            if (!fromDispatch && emote.IsPlaceholder && !emote.IsArchived)
            {
                emote.IsPlaceholder = false;
            }

            // Dispatch payloads have not been proven to always carry the image-host block, so the
            // delta path never overwrites a known image URL with an empty one (the REST path keeps
            // its verbatim behaviour — there an empty URL is 7TV's authoritative answer).
            var imageUrl = fromDispatch && live.ImageUrl.Length == 0 && emote.ImageUrl.Length > 0
                ? emote.ImageUrl
                : live.ImageUrl;

            if (emote.Name != live.Name || emote.ImageUrl != imageUrl || emote.IsArchived)
            {
                if (emote.IsArchived)
                {
                    // E34: an un-archive is a fresh entry into the active set, which opens the
                    // credibility window for the next REST leave. Read before the flag flips below; a
                    // rename of an active row is no entry and keeps its stamp.
                    emote.LastEnteredSetAtUtc = DateTime.UtcNow;
                    // D37: the emote has now been observed in the active set, so a placeholder is
                    // one no longer — its next absence from the set is an ordinary leave. Never set
                    // back to true.
                    emote.IsPlaceholder = false;
                }

                emote.Name = live.Name;
                emote.ImageUrl = imageUrl;
                emote.IsArchived = false;
                // Inside the change block, not part of the condition: clearing an archive date is
                // only meaningful on a real un-archive, and putting it into the condition would turn
                // no-op rows into inventory changes that fire channel.synced for every open page.
                emote.ArchivedAt = null;
                emote.LastSyncedAt = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        emote = new Emote
        {
            ChannelId = channelId,
            SevenTvEmoteId = live.Id,
            Name = live.Name,
            ImageUrl = live.ImageUrl,
            FirstSeenAt = live.AddedToSetAt,
            // E34: a new row is an entry into the active set (see the un-archive branch above).
            LastEnteredSetAtUtc = DateTime.UtcNow
        };
        db.Emotes.Add(emote);
        existing[live.Id] = emote;
        return true;
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

    private static Task CommitIfOpenAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken) =>
        transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    // Removes the cache entry under the login the row carried when it was loaded — unless another
    // active row carries that login now (login swap, double rename): that row's live entry must
    // survive, and the convergence net is the backstop for a genuine ghost.
    private async Task RemoveOldNameCacheEntryAsync(Channel channel, CancellationToken cancellationToken)
    {
        var oldName = channel.ChannelName;
        var heldElsewhere = await db.Channels
            .AsNoTracking()
            .AnyAsync(c => c.Id != channel.Id && c.IsBotActive && c.ChannelName == oldName, cancellationToken);
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
}
