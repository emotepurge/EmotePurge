using EmotePurge.Core.Entities;
using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class SevenTvSyncService(
    AppDbContext db,
    ISevenTvApiClient sevenTvApiClient,
    IEmoteMatchCache emoteMatchCache,
    IDuplicateEmoteNameTracker duplicateNameTracker,
    ChannelSyncGate channelSyncGate,
    ILogger<SevenTvSyncService> logger)
    : ISevenTvSyncService
{
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

        await WarmMatchCacheIfEmptyAsync(channel, cancellationToken);

        var twitchUserId = await ResolveTwitchUserIdAsync(channel, normalized, cancellationToken);
        if (twitchUserId is null)
        {
            return null;
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
        if (string.IsNullOrWhiteSpace(emoteSet.Id))
        {
            await RecordFailedAttemptAsync(channel, SevenTvSyncFailureReasons.ResponseUnusable, cancellationToken);
            return null;
        }

        var implausibleWipeResult = await TryGuardAgainstImplausibleWipeAsync(
            channel, normalized, emoteSet, channelState.State, cancellationToken);
        if (implausibleWipeResult is not null)
        {
            return implausibleWipeResult;
        }

        // Read before the assignment below: a switched active set is a content change of its own,
        // even when the two sets happen to hold identical emotes. A first-time TwitchChannelId
        // backfill deliberately does not count — it changes no emote the UI could show.
        var emoteSetSwitched = channel.ActiveEmoteSetId != emoteSet.Id;

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

        var inventoryChanged = await ReconcileAsync(channel.Id, emoteSet.Emotes, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);

        // channel.ChannelName, not the caller's `normalized`: the row gate re-read the row, so this
        // is the login the sync actually finished on. See SevenTvSyncResult.ChannelName (issue #60).
        return SevenTvSyncResult.Create(
            channel.ChannelName,
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
            }
        }

        if (!db.ChangeTracker.HasChanges())
        {
            return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.NoChange, currentName);
        }

        await db.SaveChangesAsync(cancellationToken);
        await RefreshMatchCacheAsync(channel, cancellationToken);

        logger.LogInformation(
            "7TV-Dispatch auf {Channel} angewendet: {Pushed} hinzugefügt, {Updated} aktualisiert, {Pulled} entfernt.",
            normalized, delta.Pushed.Count, delta.Updated.Count, delta.PulledIds.Count);
        return SevenTvDeltaResult.ForChannel(SevenTvDeltaOutcome.Applied, currentName);
    }

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
    /// </summary>
    private async Task WarmMatchCacheIfEmptyAsync(Channel channel, CancellationToken cancellationToken)
    {
        if (emoteMatchCache.GetChannelEmotes(channel.ChannelName).Count != 0)
        {
            return;
        }

        await RefreshMatchCacheAsync(channel, cancellationToken);

        var warmedCount = emoteMatchCache.GetChannelEmotes(channel.ChannelName).Count;
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
    /// recorded as a failure, since nothing about this attempt actually failed).
    /// </summary>
    private async Task<string?> ResolveTwitchUserIdAsync(Channel channel, string normalized, CancellationToken cancellationToken)
    {
        if (channel.TwitchChannelId is { } knownTwitchUserId)
        {
            return knownTwitchUserId;
        }

        // channel.ChannelName, not `normalized`: this runs after the row gate re-read the row, so
        // asking 7TV about the caller's name would ask about a login the rename has already retired.
        // Same root cause as the propagation issue #60 fixes for the callers.
        var resolved = await sevenTvApiClient.ResolveTwitchUserIdAsync(channel.ChannelName, cancellationToken);
        if (resolved.Status != SevenTvLookupStatus.Ok || resolved.TwitchUserId is null)
        {
            await RecordFailedAttemptAsync(channel, resolved.Status, cancellationToken);
            return null;
        }

        var twitchUserId = resolved.TwitchUserId;

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
            return null;
        }

        return twitchUserId;
    }

    /// <summary>
    /// A successful response with an empty emote list is indistinguishable from a real set wipe, but
    /// the consequences are wildly asymmetric: ReconcileAsync would archive every emote of the channel
    /// and RefreshMatchCacheAsync would install an empty dictionary, so chat matching stops entirely
    /// until the next successful sync (up to 60s — thousands of lost matches at HandOfBlood's message
    /// rate). Known triggers: a set change in progress, a partial 7TV outage, an owner briefly emptying
    /// the set. Treated as implausible and skipped; the next tick recovers on its own.
    /// <para>Returns the result to return immediately when the wipe looks implausible, or null when
    /// the caller should continue the normal sync.</para>
    /// </summary>
    private async Task<SevenTvSyncResult?> TryGuardAgainstImplausibleWipeAsync(
        Channel channel, string normalized, SevenTvEmoteSet emoteSet, SevenTvChannelState state, CancellationToken cancellationToken)
    {
        if (emoteSet.Emotes.Count != 0)
        {
            return null;
        }

        var knownActiveEmotes = await db.Emotes
            .CountAsync(e => e.ChannelId == channel.Id && !e.IsArchived, cancellationToken);
        if (knownActiveEmotes == 0)
        {
            return null;
        }

        logger.LogWarning(
            "7TV meldet 0 aktive Emotes für {Channel}, obwohl bisher {Count} bekannt waren — Sync übersprungen.",
            normalized, knownActiveEmotes);
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
        // Logged only when the reason changes: the periodic resync runs this for every broken
        // channel every 60 seconds, and an unconditional line would bury everything else in the log.
        // The stored value is what the UI reads, so nothing is lost by staying quiet.
        var changed = channel.LastSyncFailureReason != reason;

        channel.LastSyncAttemptAtUtc = DateTime.UtcNow;
        channel.LastSyncFailureReason = reason;
        await db.SaveChangesAsync(cancellationToken);

        if (changed)
        {
            logger.LogInformation(
                "7TV-Sync für {Channel} ohne Ergebnis: {Reason}.", channel.ChannelName, reason);
        }
    }

    private async Task RefreshMatchCacheAsync(Channel channel, CancellationToken cancellationToken)
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

        if (duplicateNameTracker.Update(channel.ChannelName, duplicateNames))
        {
            if (duplicateNames.Count > 0)
            {
                logger.LogWarning(
                    "{Count} doppelte aktive Emote-Namen in Channel {Channel}: {Names} — Chat-Matching zählt je Name nur auf die zuerst geladene Emote-Id.",
                    duplicateNames.Count, channel.ChannelName,
                    string.Join(", ", duplicateNames.Order(StringComparer.Ordinal)));
            }
            else
            {
                logger.LogInformation(
                    "Namenskollisionen in Channel {Channel} aufgelöst — alle aktiven Emote-Namen sind wieder eindeutig.",
                    channel.ChannelName);
            }
        }

        emoteMatchCache.ReplaceChannel(channel.ChannelName, emoteNameToId);
    }

    /// <summary>Returns true when at least one emote row was added, archived or altered.</summary>
    private async Task<bool> ReconcileAsync(string channelId, IReadOnlyList<SevenTvEmote> liveEmotes, CancellationToken cancellationToken)
    {
        var existing = await db.Emotes
            .Where(e => e.ChannelId == channelId)
            .ToDictionaryAsync(e => e.SevenTvEmoteId, cancellationToken);

        var liveIds = liveEmotes.Select(e => e.Id).ToHashSet();
        var changed = false;

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
                // reconciliation for.
                if (emote.LastSyncedAt > DateTime.UtcNow.AddMinutes(-15))
                {
                    logger.LogInformation(
                        "REST-Resync archiviert Emote {Name} ({SevenTvEmoteId}), das vor weniger als 15 Minuten synchronisiert wurde — möglicher 7TV-REST-Cache-Lag.",
                        emote.Name, sevenTvEmoteId);
                }

                emote.IsArchived = true;
                emote.ArchivedAt = DateTime.UtcNow;
                changed = true;
            }
        }

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

            // Dispatch payloads have not been proven to always carry the image-host block, so the
            // delta path never overwrites a known image URL with an empty one (the REST path keeps
            // its verbatim behaviour — there an empty URL is 7TV's authoritative answer).
            var imageUrl = fromDispatch && live.ImageUrl.Length == 0 && emote.ImageUrl.Length > 0
                ? emote.ImageUrl
                : live.ImageUrl;

            if (emote.Name != live.Name || emote.ImageUrl != imageUrl || emote.IsArchived)
            {
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
            FirstSeenAt = live.AddedToSetAt
        };
        db.Emotes.Add(emote);
        existing[live.Id] = emote;
        return true;
    }
}
