using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Implementation notes that are not obvious from the interface:
/// <list type="bullet">
/// <item>Twitch ids are opaque digit strings and are never put through
/// <see cref="ChannelName.Normalize"/>; every comparison on them is
/// <see cref="StringComparison.Ordinal"/>. Logins are the opposite — normalized on the way in and
/// on the way out, because Helix's answer is what gets stored.</item>
/// <item>Each row is reconciled in its own transaction rather than the whole tick in one, and its
/// write failure is caught per row. Both halves are needed: a failing merge must neither roll back
/// the renames before it nor stop the rows after it, and the next tick converges anyway.</item>
/// <item>The rows are read once as a scalar projection and then acted on one by one, so a merge can
/// delete a row that is still sitting in that snapshot, and a duplicate pair is reached from both
/// ends — hence the set of settled channel ids.</item>
/// </list>
/// </summary>
public class ChannelIdentityService(
    AppDbContext db,
    ITwitchHelixClient helixClient,
    ITwitchAppTokenProvider appTokenProvider,
    IRedisPublisher redisPublisher,
    ChannelIdentityWarningState warningState,
    IExcludedChannelFilter excludedChannelFilter,
    ILogger<ChannelIdentityService> logger) : IChannelIdentityService
{
    public async Task<ChannelIdentityReconcileSummary?> ReconcileActiveChannelsAsync(CancellationToken ct = default)
    {
        // Scalar projection, not entities: the pass mutates at most a handful of the rows it looks
        // at, and each of those is re-loaded tracked at the moment it is changed. Tracking every
        // active channel for the sake of three would put the whole roster in the change tracker of
        // a context that then saves five times.
        var rows = await db.Channels
            .AsNoTracking()
            .Where(c => c.IsBotActive)
            .Select(c => new ChannelIdentityRow(c.Id, c.TwitchChannelId, c.ChannelName))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return new ChannelIdentityReconcileSummary(0, 0, 0, 0, 0, 0, 0);
        }

        var counters = new ReconcileCounters();
        // Rows this pass is done with: either merged away (the snapshot still lists them), one half
        // of a duplicate pair the pass already ruled on, or — since the pass below — a row the
        // known-id exclusion gate has already deactivated. A duplicate is visible from both ends —
        // the id row that wants the name and the id-less row that holds it — and without this the
        // same merge would be attempted, refused and counted twice per tick.
        var settledChannelIds = new HashSet<string>(StringComparer.Ordinal);

        // Objection gate, unconditional (P1 Codex finding, issue #260, third review): deactivating
        // a row whose STORED Twitch id is already known to be excluded needs no Helix answer at all
        // — unlike a newly-*backfilled* id (ReconcileIdLessRowAsync's own gate further down, which
        // has to resolve the login first). Run here, before the app-token/Helix early returns below,
        // so a token or Helix outage can no longer leave such a row under observation for as long as
        // the outage lasts — the early returns used to skip this guard along with the rest of the
        // tick, which is exactly the gap this closes. Every row it settles is recorded the same way
        // the rest of the method already tracks settled rows, so the main loop further down (reached
        // only once the token and Helix both answer) never re-decides the same row a second time.
        foreach (var row in rows)
        {
            if (row.TwitchChannelId is not { } storedTwitchChannelId || !excludedChannelFilter.IsExcluded(storedTwitchChannelId))
            {
                continue;
            }

            try
            {
                await DeactivateExcludedRowAsync(row, storedTwitchChannelId, counters, ct);
            }
            catch (DbUpdateException ex)
            {
                // Same reasoning as the identical catch around the main loop further down: one row's
                // failed write must not cost the rest of the tick, and the next tick retries it.
                logger.LogWarning(
                    ex,
                    "Identitätsabgleich für Kanal {ChannelName} ({ChannelId}) fehlgeschlagen — Zeile übersprungen, der nächste Durchlauf versucht es erneut.",
                    row.ChannelName, row.Id);
                db.ChangeTracker.Clear();
            }

            settledChannelIds.Add(row.Id);
        }

        var appToken = await appTokenProvider.GetTokenAsync(ct);
        if (appToken is null)
        {
            logger.LogInformation(
                "Kein App-Token verfügbar — Identitätsabgleich für {ChannelCount} Kanäle übersprungen.", rows.Count);
            // Not necessarily null any more (P1 Codex finding, issue #260, third review): the
            // exclusion pass above may have written something despite the outage, and that is worth
            // the worker's log line — null stays reserved for "nothing happened", now genuinely true
            // only when nothing was deactivated either.
            return counters.Deactivated > 0
                ? new ChannelIdentityReconcileSummary(rows.Count, 0, 0, 0, 0, 0, counters.Deactivated)
                : null;
        }

        var ids = rows.Where(r => r.TwitchChannelId is not null).Select(r => r.TwitchChannelId!).ToList();
        var logins = rows.Where(r => r.TwitchChannelId is null).Select(r => r.ChannelName).ToList();

        // One call for the whole roster: GetUsersAsync batches internally, and asking per channel
        // would turn one tick into as many Helix requests as there are tracked channels.
        var identities = await helixClient.GetUsersAsync(ids, logins, appToken, ct);
        if (identities is null)
        {
            logger.LogInformation(
                "Helix nicht erreichbar — Identitätsabgleich für {ChannelCount} Kanäle übersprungen.", rows.Count);
            // See the app-token early return above for why this is no longer unconditionally null.
            return counters.Deactivated > 0
                ? new ChannelIdentityReconcileSummary(rows.Count, 0, 0, 0, 0, 0, counters.Deactivated)
                : null;
        }

        var identitiesById = new Dictionary<string, TwitchUserIdentity>(StringComparer.Ordinal);
        var identitiesByLogin = new Dictionary<string, TwitchUserIdentity>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            // Indexer rather than ToDictionary: a duplicate in Helix's answer must not throw and
            // take the whole tick down with it.
            identitiesById[identity.Id] = identity;
            identitiesByLogin[ChannelName.Normalize(identity.Login)] = identity;
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (settledChannelIds.Contains(row.Id))
            {
                continue;
            }

            try
            {
                if (row.TwitchChannelId is { } twitchChannelId)
                {
                    await ReconcileKnownIdRowAsync(row, twitchChannelId, identitiesById, counters, settledChannelIds, ct);
                }
                else
                {
                    await ReconcileIdLessRowAsync(row, identitiesByLogin, counters, settledChannelIds, ct);
                }
            }
            catch (DbUpdateException ex)
            {
                // One row's failed write must not cost the rest of the tick. The realistic trigger is
                // a race the snapshot cannot close: between "is the target name free?" and the save, a
                // parallel join creates a row under exactly that name and IX_Channels_ChannelName
                // rejects the rename. Uncaught, that left every unvisited row unprocessed and threw
                // the summary away too — for a condition the next tick resolves by itself.
                logger.LogWarning(
                    ex,
                    "Identitätsabgleich für Kanal {ChannelName} ({ChannelId}) fehlgeschlagen — Zeile übersprungen, der nächste Durchlauf versucht es erneut.",
                    row.ChannelName, row.Id);

                // Mandatory, not housekeeping: EF leaves the failed changes in the tracker, so the
                // next row's SaveChangesAsync would re-send them and fail identically. Without this
                // one bad row still takes down every row behind it, which is what the catch is for.
                db.ChangeTracker.Clear();
            }
        }

        return new ChannelIdentityReconcileSummary(
            rows.Count,
            counters.IdsBackfilled,
            counters.Renamed,
            counters.Merged,
            counters.MergesRefused,
            counters.LoginsMissing,
            counters.Deactivated);
    }

    public async Task<TwitchUserLookup> LookupByLoginAsync(string login, CancellationToken ct = default)
    {
        var normalized = ChannelName.Normalize(login);

        var appToken = await appTokenProvider.GetTokenAsync(ct);
        if (appToken is null)
        {
            logger.LogInformation(
                "Kein App-Token verfügbar — Twitch-Identität für {ChannelName} nicht auflösbar.", normalized);
            return TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable);
        }

        var identities = await helixClient.GetUsersAsync([], [normalized], appToken, ct);
        if (identities is null)
        {
            logger.LogInformation(
                "Helix nicht erreichbar — Twitch-Identität für {ChannelName} nicht auflösbar.", normalized);
            return TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable);
        }

        // Matched rather than taken blindly: an empty array is Helix's way of saying the login does
        // not exist, and anything else in there would not be the account that was asked for.
        var match = identities.FirstOrDefault(
            identity => string.Equals(ChannelName.Normalize(identity.Login), normalized, StringComparison.Ordinal));

        return match is null
            ? TwitchUserLookup.Failed(TwitchUserLookupStatus.NotFound)
            : TwitchUserLookup.Found(match);
    }

    private async Task ReconcileKnownIdRowAsync(
        ChannelIdentityRow row,
        string twitchChannelId,
        Dictionary<string, TwitchUserIdentity> identitiesById,
        ReconcileCounters counters,
        HashSet<string> settledChannelIds,
        CancellationToken ct)
    {
        // Objection gate (GDPR Art. 21, issue #260, this revision — supersedes the residual gap the
        // #252 DECISIONS entry used to document): checked first, before Helix's answer is even
        // consulted, so a row whose *known* Twitch id is excluded can never reach a rename or a
        // merge decision below — not "refuse the one write that would reactivate it" (MergeAsync's
        // own guard, still in place as defense in depth) but "stop observing it at all". Without
        // this, purging the row's id-less duplicate under its current login (the operator's own
        // documented cleanup step, Operations.md) freed that name for this row to be renamed onto
        // and rejoined on the very next tick — the exact P1 Codex finding this closes.
        //
        // Realistically unreachable in ordinary operation as of the third review round: the
        // unconditional exclusion pass at the top of ReconcileActiveChannelsAsync already settles
        // every row with a *known*, excluded id — this method is only ever reached for a row whose
        // stored id was NOT excluded at that point — so `settledChannelIds` already skips this
        // method's caller for such a row before it can even be invoked. Left in place anyway, the
        // same reasoning MergeAsync's own guard already documents for itself: a second line of
        // defense costs one extra `IsExcluded` call and is worth it.
        if (excludedChannelFilter.IsExcluded(twitchChannelId))
        {
            await DeactivateExcludedRowAsync(row, twitchChannelId, counters, ct);
            return;
        }

        if (!identitiesById.TryGetValue(twitchChannelId, out var identity))
        {
            // Case 6: the id resolved to nothing in an otherwise successful response — the account
            // was deleted or banned. Nothing is written: the row is the only remaining record that
            // this channel existed, and its usage history is not reconstructible.
            counters.LoginsMissing++;
            if (warningState.ShouldWarn(ChannelIdentityWarningState.IdKey(twitchChannelId)))
            {
                logger.LogWarning(
                    "Twitch kennt die ID {TwitchChannelId} des Kanals {ChannelName} nicht mehr (Konto gelöscht oder gesperrt) — Zeile bleibt unverändert.",
                    twitchChannelId, row.ChannelName);
            }

            return;
        }

        warningState.Clear(ChannelIdentityWarningState.IdKey(twitchChannelId));

        var newLogin = ChannelName.Normalize(identity.Login);
        if (string.Equals(newLogin, row.ChannelName, StringComparison.Ordinal))
        {
            // Case 1, and by far the common one: nothing to do.
            warningState.Clear(ChannelIdentityWarningState.BlockedKey(row.Id));
            return;
        }

        // Read-only: this only decides which case applies. A merge re-reads both rows under its locks.
        var occupant = await db.LoadChannelReadOnlyAsync(newLogin, ct);
        if (occupant is null)
        {
            await RenameAsync(twitchChannelId, newLogin, counters, ct);
            warningState.Clear(ChannelIdentityWarningState.BlockedKey(row.Id));
            return;
        }

        if (occupant.TwitchChannelId is not null)
        {
            // Case 3, blocked: the row sitting on the target name claims a different Twitch id than
            // the one Helix says owns that name, so it is itself out of date. Merging into it would
            // fuse two genuinely different channels. Skipped rather than forced — once that row has
            // been reconciled (this pass or the next), the name is free and this converges.
            //
            // Deduplicated like cases 5 and 6, and for the same reason: usually this resolves on the
            // next tick, but if the blocking row is itself unreconcilable (its own id is case 6) the
            // pair never converges and an undeduplicated warning would repeat hourly forever.
            if (warningState.ShouldWarn(ChannelIdentityWarningState.BlockedKey(row.Id)))
            {
                logger.LogWarning(
                    "Kanal {ChannelName} (Twitch-ID {TwitchChannelId}) heißt auf Twitch jetzt {NewChannelName}, aber dieser Name gehört bereits Zeile {BlockingChannelId} mit abweichender Twitch-ID {BlockingTwitchChannelId} — übersprungen, der nächste Durchlauf versucht es erneut.",
                    row.ChannelName, twitchChannelId, newLogin, occupant.Id, occupant.TwitchChannelId);
            }

            return;
        }

        // Case 3, mergeable: the id-less row under the new name is the duplicate the rename created
        // — someone joined the channel again under its new name while the old row kept the history.
        if (await MergeAsync(twitchChannelId, newLogin, row.Id, occupant.Id, counters, settledChannelIds, ct))
        {
            warningState.Clear(ChannelIdentityWarningState.BlockedKey(row.Id));
        }
    }

    private async Task ReconcileIdLessRowAsync(
        ChannelIdentityRow row,
        Dictionary<string, TwitchUserIdentity> identitiesByLogin,
        ReconcileCounters counters,
        HashSet<string> settledChannelIds,
        CancellationToken ct)
    {
        if (!identitiesByLogin.TryGetValue(row.ChannelName, out var identity))
        {
            // Case 5: Helix does not know this login. Either the account is gone, or it was renamed
            // before we ever recorded its id — which is precisely the state the backfill exists to
            // end, and which nothing here can repair on its own.
            counters.LoginsMissing++;
            if (warningState.ShouldWarn(ChannelIdentityWarningState.LoginKey(row.ChannelName)))
            {
                logger.LogWarning(
                    "Twitch kennt den Login {ChannelName} nicht — die Zeile hat keine Twitch-ID und kann nicht nachgeführt werden.",
                    row.ChannelName);
            }

            return;
        }

        warningState.Clear(ChannelIdentityWarningState.LoginKey(row.ChannelName));

        // Objection gate, the id-less counterpart of the check at the top of
        // ReconcileKnownIdRowAsync: this row is about to learn (via backfill) or act on
        // (via the merge below) a Twitch id that turns out to be excluded — "newly backfilled" in
        // the DECISIONS wording. Checked before either LoadChannelByTwitchIdReadOnlyAsync or
        // BackfillIdAsync can act on the identity, so this row is deactivated instead of being
        // brought into observation under that id at all, whether or not another row already holds
        // it (MergeAsync's own exclusion guard stays in place for that case too, as defense in
        // depth — see the class remark on MergeAsync).
        if (excludedChannelFilter.IsExcluded(identity.Id))
        {
            await DeactivateExcludedRowAsync(row, identity.Id, counters, ct);
            return;
        }

        // Read-only, like the occupant in ReconcileKnownIdRowAsync: a merge re-reads it under its lock.
        var holder = await db.LoadChannelByTwitchIdReadOnlyAsync(identity.Id, ct);
        if (holder is null)
        {
            await BackfillIdAsync(row, identity.Id, counters, ct);
            return;
        }

        if (string.Equals(holder.Id, row.Id, StringComparison.Ordinal))
        {
            // Can only happen if the row acquired its id between the projection and now.
            logger.LogDebug(
                "Kanal {ChannelName} hat seine Twitch-ID {TwitchChannelId} bereits zwischenzeitlich bekommen — nichts zu tun.",
                row.ChannelName, identity.Id);
            return;
        }

        // Case 4, the duplicate with the roles swapped: another row already carries this Twitch id,
        // so *that* one is the channel and this one is the second row a rename left behind. The id
        // row survives — it holds the emotes and the usage history. It may well be inactive (only
        // active rows are in the snapshot), which is exactly the row a retention purge can be deleting
        // right now — hence the lock MergeAsync takes on it.
        await MergeAsync(identity.Id, row.ChannelName, holder.Id, row.Id, counters, settledChannelIds, ct);
    }

    /// <summary>
    /// The write both objection-gate checks above share: an active row whose (known or about-to-be-
    /// learned) Twitch id is excluded is deactivated exactly like an operator's own leave — see
    /// <see cref="ChannelDeactivation.DeactivateAsync"/> — instead of being renamed, merged, or
    /// backfilled into observation. The row is reloaded tracked here rather than passed in: the
    /// caller only has the read-only projection snapshot, and this write needs a tracked entity to
    /// mutate, same as <see cref="RenameAsync"/> and <see cref="BackfillIdAsync"/>.
    /// </summary>
    /// <param name="row">The snapshot row a caller has already decided to deactivate.</param>
    /// <param name="twitchChannelId">
    /// The Twitch id the caller found excluded — <paramref name="row"/>'s own stored id for
    /// <see cref="ReconcileKnownIdRowAsync"/>, or the id its login just resolved to for
    /// <see cref="ReconcileIdLessRowAsync"/>. In the second case it is written onto the row together
    /// with the deactivation whenever the unique index allows it, so the block survives on the row
    /// itself (see the comment at the write).
    /// </param>
    private async Task DeactivateExcludedRowAsync(
        ChannelIdentityRow row, string twitchChannelId, ReconcileCounters counters, CancellationToken ct)
    {
        // Reloaded by primary key, not by row.ChannelName (P1 Codex finding, issue #260, third
        // review): a concurrent purge of this exact row followed by a fresh join under the same
        // login creates an unrelated replacement row under that name before this method gets to run
        // — a name-based reload used to find and deactivate *that* row instead, silently undoing a
        // join this objection gate has nothing to do with. The replacement carries a different Id,
        // so reloading by row.Id simply finds nothing once the original row is gone, and this call
        // is skipped below like every other "the row is gone" case. No row lock is taken on top of
        // that (unlike MergeAsync's FOR UPDATE pair): the narrow window between this reload and the
        // write further down is already covered by the DbUpdateException catch every caller of this
        // method already wraps it in, the same as every other single-row write in this class
        // (RenameAsync, BackfillIdAsync) that reloads without a lock for the same reason.
        var channel = await db.LoadChannelByIdAsync(row.Id, ct);
        if (channel is null
            || !channel.IsBotActive
            || !string.Equals(channel.TwitchChannelId, row.TwitchChannelId, StringComparison.Ordinal)
            || !excludedChannelFilter.IsExcluded(twitchChannelId))
        {
            // Gone, already deactivated since the snapshot was taken (a concurrent leave, or this
            // row's other half already settled it — see the class remark on why a duplicate pair is
            // reached from both ends), no longer carries the id this decision was made for (a
            // concurrent rename, backfill or merge settled something else for it first), or no
            // longer excluded. Either the outcome the block list exists for already holds or this
            // row now belongs to a decision the next tick makes afresh — nothing to write and
            // nothing to warn about.
            return;
        }

        if (channel.TwitchChannelId is null
            && !await db.Channels.AnyAsync(c => c.TwitchChannelId == twitchChannelId, ct))
        {
            // Fourth Codex review of the block list: a deactivated id-less row used to keep
            // TwitchChannelId = null, so a later join by its login while Twitch answered Unavailable
            // or NotFound found the row by name, saw no id to check and reactivated it. Writing the
            // resolved id down — the same backfill BackfillIdAsync does for any other row, in the
            // same save as the deactivation — makes every join path's stored-id check refuse it from
            // then on, whatever Twitch answers. Skipped when another row already holds the id (the
            // unique index would reject it): that row carries the block itself, and this duplicate
            // keeps the join path's id-less fallback (ChannelService.HandleUnknownTwitchLoginAsync
            // refuses an inactive id-less row outright; the Unavailable case is the documented
            // outage gap, see docs/DECISIONS.md).
            channel.TwitchChannelId = twitchChannelId;
        }

        try
        {
            await ChannelDeactivation.DeactivateAsync(db, redisPublisher, channel, AuditActor.System, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DbUpdateException)
        {
            // The write already committed — DeactivateAsync saves before it publishes, same order as
            // every write-then-announce path in this class (RenameAsync/MergeAsync's own
            // PublishHandoverAsync). A thrown LEAVE publish must not escape uncaught here (P1 Codex
            // finding, issue #260, third review): unlike the DbUpdateException left to propagate
            // (both this method's callers already wrap it in the per-row catch that retries the row
            // next tick, appropriate because nothing was written that time), retrying a row that DID
            // write would just find it already inactive and never publish again — this is the one
            // chance to push the LEAVE to the worker directly. Deliberately not the shared
            // ChannelDeactivation.DeactivateAsync helper's job to swallow: ChannelService.LeaveAsync's
            // user-facing leave shares that helper and must keep failing loudly on a publish it could
            // not deliver — changing the helper would silently change that contract too — so only
            // this reconcile caller, which already treats a lost publish as self-healing elsewhere
            // (PublishHandoverAsync), catches it here.
            //
            // Not stranded until a restart, either: RosterPrunePolicy (issue #41) is exactly the
            // convergence net for a lost LEAVE — ListActiveChannelNamesAsync no longer lists this row,
            // so the worker prunes it from its roster (EmoteMatchCache, the 7TV subscription, the IRC
            // part) within two consecutive periodic-resync ticks, roughly one to two minutes, not
            // until the process restarts.
            counters.Deactivated++;
            logger.LogWarning(
                ex,
                "Channel deactivated, but the LEAVE announcement could not be published — the periodic 7TV resync's roster prune (issue #41) will still stop the worker from observing it within about two minutes.");
            return;
        }

        counters.Deactivated++;

        // Neither the id nor the login is logged — same restraint as MergeAsync's own exclusion
        // refusal: a log line naming which channel this concerns would itself leak the objection the
        // block exists to honour.
        logger.LogWarning("Channel deactivated: the channel is on the excluded-channel list.");
    }

    private async Task BackfillIdAsync(
        ChannelIdentityRow row, string twitchChannelId, ReconcileCounters counters, CancellationToken ct)
    {
        var channel = await db.LoadChannelAsync(row.ChannelName, ct);
        if (channel is null)
        {
            logger.LogInformation(
                "Kanal {ChannelName} war beim Nachtragen der Twitch-ID nicht mehr auffindbar — übersprungen.",
                row.ChannelName);
            return;
        }

        channel.TwitchChannelId = twitchChannelId;
        // No audit entry and no TrackingResumedAt: nothing about the channel changed, we merely
        // wrote down what it always was. Auditing it would fill the log with one entry per
        // pre-existing channel on the first tick after deploy.
        await db.SaveChangesAsync(ct);
        counters.IdsBackfilled++;

        logger.LogInformation(
            "Twitch-ID {TwitchChannelId} für Kanal {ChannelName} nachgetragen.", twitchChannelId, row.ChannelName);
    }

    private async Task RenameAsync(
        string twitchChannelId, string newLogin, ReconcileCounters counters, CancellationToken ct)
    {
        var channel = await db.LoadChannelByTwitchIdAsync(twitchChannelId, ct);
        if (channel is null)
        {
            logger.LogInformation(
                "Kanal mit Twitch-ID {TwitchChannelId} war beim Umbenennen nicht mehr auffindbar — übersprungen.",
                twitchChannelId);
            return;
        }

        var oldLogin = channel.ChannelName;
        channel.ChannelName = newLogin;
        // Between the rename on Twitch and this moment the IRC join pointed at a channel name that
        // no longer answered, so nothing was counted. That gap is exactly what TrackingResumedAt
        // makes honest; CreatedAt stays, because the row is the same channel it always was.
        channel.TrackingResumedAt = DateTime.UtcNow;
        db.AddAuditEntry(
            AuditActor.System,
            AuditActions.ChannelRename,
            channelName: newLogin,
            details: new { twitchChannelId, oldLogin, newLogin });
        await db.SaveChangesAsync(ct);
        counters.Renamed++;

        logger.LogInformation(
            "Kanal {ChannelName} heißt auf Twitch jetzt {NewChannelName} (Twitch-ID {TwitchChannelId}) — Zeile nachgeführt.",
            oldLogin, newLogin, twitchChannelId);
        await PublishHandoverAsync(oldLogin, newLogin, ct);
    }

    /// <summary>
    /// Folds the id-less row under <paramref name="newLogin"/> (the loser) into the row holding
    /// <paramref name="twitchChannelId"/> (the survivor) and puts the survivor under that name. Guarded:
    /// the loser must be emote-less. Returns whether the pair was ruled on — merged, or refused for the
    /// loser's emotes — as opposed to skipped because one of the two rows vanished or changed first.
    /// </summary>
    /// <remarks>
    /// One transaction, both rows locked <c>FOR UPDATE</c> before anything is decided — the survivor
    /// first, then the loser, the same id-before-name order a join takes, so the two cannot deadlock.
    /// The survivor's lock is what the retention purge serialises on (it may be an inactive row that is
    /// due): a purge holding it makes this find no survivor and skip; a merge holding it makes the purge
    /// see the survivor active (or renamed away) afterwards. Both rows are re-read under the lock, so
    /// the decision is taken on their current state, not on the caller's snapshot.
    /// </remarks>
    private async Task<bool> MergeAsync(
        string twitchChannelId,
        string newLogin,
        string survivorRowId,
        string loserRowId,
        ReconcileCounters counters,
        HashSet<string> settledChannelIds,
        CancellationToken ct)
    {
        // Objection gate (GDPR Art. 21, issue #252): a merge is the one place this pass can flip
        // an inactive row active again (`survivor.IsBotActive |= loser.IsBotActive` below) — exactly
        // the "reactivation" the join path already refuses for this id. Refused the same way the
        // loser-has-emotes case below is: nothing is written, both rows stay duplicated and
        // unresolved until the operator removes the id from the list. Checked before the transaction
        // even opens — cheaper, and it keeps a blocked id from taking either row's lock at all; the
        // two row ids needed to settle the pair are therefore taken from the caller's own snapshot
        // (already loaded, read-only, before this call) rather than from a load this branch would
        // otherwise have to do just to name them.
        //
        // Defense in depth as of issue #260 (this revision): both callers now check
        // excludedChannelFilter themselves before they ever decide to call MergeAsync at all — see
        // the gate at the top of ReconcileKnownIdRowAsync and ReconcileIdLessRowAsync — and
        // deactivate the row instead, so neither an active survivor nor an active loser can reach
        // this point carrying an excluded id in ordinary operation any more. Left in place anyway:
        // it is the guard against `survivor.IsBotActive |= loser.IsBotActive` specifically, and
        // removing it would trade a second line of defense for saving one `IsExcluded` call.
        if (excludedChannelFilter.IsExcluded(twitchChannelId))
        {
            counters.MergesRefused++;
            // Both halves settled (P2 Codex finding, issue #260): without this, the pass visits the
            // same excluded pair from both ends — the id row wanting the name and the id-less row
            // holding it — and neither this counter nor the warning line below is deduplicated
            // against a second visit, exactly like the loserHasEmotes refusal further down handles
            // it. Deliberately keyed on the row ids the caller already has rather than IDs read
            // under this method's own (never-taken, for an excluded id) lock.
            settledChannelIds.Add(survivorRowId);
            settledChannelIds.Add(loserRowId);
            // Neither the id nor either login is logged here (same restraint as the join path's own
            // rejection): a log line naming which channel this concerns would itself leak the
            // objection the block exists to honour.
            logger.LogWarning("Merge refused: the channel is on the excluded-channel list.");
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var survivor = await db.LoadChannelByTwitchIdForUpdateAsync(twitchChannelId, ct);
        if (survivor is null)
        {
            // The snapshot said this row exists; it no longer does. A concurrent purge — an admin's, or
            // the retention job's on an inactive survivor — is the only known cause, and it is benign:
            // the next pass sees the id-less row on its own and backfills the id. But a silent return
            // would leave a counter that simply never moves and no trace of why.
            logger.LogInformation(
                "Überlebende Zeile mit Twitch-ID {TwitchChannelId} war beim Zusammenführen nicht mehr auffindbar — übersprungen.",
                twitchChannelId);
            return false;
        }

        var loser = await db.LoadChannelForUpdateAsync(newLogin, ct);
        if (loser is null)
        {
            logger.LogInformation(
                "Zusammenzuführende Zeile {ChannelName} war nicht mehr auffindbar — übersprungen.", newLogin);
            return false;
        }

        if (string.Equals(survivor.Id, loser.Id, StringComparison.Ordinal) || loser.TwitchChannelId is not null)
        {
            // The pair the caller saw no longer exists as such: the survivor already answers to the
            // name, or the name's row acquired a Twitch id of its own in the meantime. Merging now could
            // fuse two different channels; the next pass decides from the new state.
            logger.LogInformation(
                "Channel {ChannelName} changed after it was picked for a merge — skipped, the next pass decides afresh.",
                newLogin);
            return false;
        }

        var oldLogin = survivor.ChannelName;

        // The invariant that makes this safe at all. Emotes carry UsageStats, VoteSessionEmotes and
        // Votes; the same 7TV emote is deliberately a *different* row per channel (Regel 8), so
        // there is no correct way to fuse two channels' emote histories — any rule would either
        // double-count usage or throw half of it away. An emote-less loser has nothing to fuse, and
        // that is the only case handled automatically. Anything else is refused, loudly and without
        // writing, for a human to sort out.
        var loserHasEmotes = await db.Emotes.AnyAsync(e => e.ChannelId == loser.Id, ct);
        if (loserHasEmotes)
        {
            counters.MergesRefused++;
            // Both halves settled: the mirror row would otherwise reach the identical refusal from
            // the other side, and neither of them has anything else to do this pass.
            settledChannelIds.Add(loser.Id);
            settledChannelIds.Add(survivor.Id);
            // Deduplicated like cases 3, 5 and 6, and with the strongest claim of the four: a refusal
            // is by definition never self-resolving — it waits for a person to move or delete the
            // emotes — so an undeduplicated warning repeats every tick for as long as the process
            // lives. Nothing is lost by warning once: MergesRefused >= 1 makes the summary differ
            // from the empty one, and the worker logs the summary on every tick that does, so the
            // state stays visible hourly; only the second, third and thousandth copy of the same
            // sentence disappear.
            if (warningState.ShouldWarn(ChannelIdentityWarningState.RefusedKey(loser.Id)))
            {
                logger.LogWarning(
                    "Zusammenführung von Kanal {LoserChannelName} ({LoserChannelId}) in {SurvivorChannelName} ({SurvivorChannelId}) verweigert: die aufzulösende Zeile hat noch Emotes.",
                    loser.ChannelName, loser.Id, survivor.ChannelName, survivor.Id);
            }

            // Nothing was written; disposing the transaction rolls back and releases both locks.
            return true;
        }

        // Regel 10: the collision check runs off two scalar date lists rather than a navigation join
        // with a GroupBy, which Npgsql would not translate.
        var survivorDates = await db.ChannelLiveDays
            .Where(d => d.ChannelId == survivor.Id)
            .Select(d => d.Date)
            .ToListAsync(ct);
        var survivorDateSet = survivorDates.ToHashSet();

        var loserDays = await db.ChannelLiveDays.Where(d => d.ChannelId == loser.Id).ToListAsync(ct);
        var collidingDates = loserDays.Where(d => survivorDateSet.Contains(d.Date)).Select(d => d.Date).ToList();
        var survivorDaysByDate = collidingDates.Count == 0
            ? []
            : await db.ChannelLiveDays
                .Where(d => d.ChannelId == survivor.Id && collidingDates.Contains(d.Date))
                .ToDictionaryAsync(d => d.Date, ct);

        // Two counters, not one: only the moved days change the survivor's row count, and an audit
        // entry claiming "4 live days taken over" against a table that grew by 3 is an entry nobody
        // can check.
        var movedLiveDays = 0;
        var collapsedLiveDays = 0;
        foreach (var day in loserDays)
        {
            if (survivorDaysByDate.TryGetValue(day.Date, out var existing))
            {
                // MAX, not a sum: both rows describe the same wall-clock day of the same stream, so
                // adding them would invent airtime. The unique (ChannelId, Date) index is also why
                // the loser row has to go rather than move.
                existing.LiveMinutes = Math.Max(existing.LiveMinutes, day.LiveMinutes);
                db.ChannelLiveDays.Remove(day);
                collapsedLiveDays++;
            }
            else
            {
                day.ChannelId = survivor.Id;
                movedLiveDays++;
            }
        }

        var sessions = await db.VoteSessions.Where(s => s.ChannelId == loser.Id).ToListAsync(ct);
        foreach (var session in sessions)
        {
            session.ChannelId = survivor.Id;
        }

        // Captured before the flip: DeactivatedAtUtc must only be nulled when the merge is what
        // makes the survivor active, not when it already was (in which case the column is already
        // null and nulling it again is a no-op, but the read makes the condition mean what it says).
        var survivorWasActive = survivor.IsBotActive;
        survivor.IsBotActive |= loser.IsBotActive;
        if (!survivorWasActive && survivor.IsBotActive)
        {
            // The retention clock stops here too, same as CompleteJoinAsync's reactivation branch —
            // an inactive survivor absorbing an active loser must not stay a purge candidate.
            survivor.DeactivatedAtUtc = null;
        }

        survivor.ChannelName = newLogin;
        survivor.TrackingResumedAt = DateTime.UtcNow;
        db.AddAuditEntry(
            AuditActor.System,
            AuditActions.ChannelMerge,
            channelName: newLogin,
            details: new
            {
                survivorChannelId = survivor.Id,
                loserChannelId = loser.Id,
                twitchChannelId,
                oldLogin,
                newLogin,
                movedLiveDays,
                collapsedLiveDays,
                movedVoteSessions = sessions.Count
            });
        db.Channels.Remove(loser);
        // One SaveChangesAsync, although the survivor takes over a name the loser still holds in the
        // same batch. What carries that is not a general "deletes run first" rule but a specific edge
        // EF Core's CommandBatchPreparer puts into its command graph: a delete and an update touching
        // the *same value of a unique index* are ordered delete-before-update, so the loser row is
        // gone before IX_Channels_ChannelName sees the new name. Asserted against the real index in
        // ChannelIdentityServiceTests rather than assumed — and note the boundary before
        // generalizing: two *updates* swapping a unique value get no such edge and still need two
        // saves.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        settledChannelIds.Add(loser.Id);
        settledChannelIds.Add(survivor.Id);
        // The one way a refusal ends: someone cleared the loser's emotes and the merge went through.
        // Forgetting it here means a *later* refusal on a row that reuses this id is reported again
        // rather than silently.
        warningState.Clear(ChannelIdentityWarningState.RefusedKey(loser.Id));
        counters.Merged++;

        logger.LogInformation(
            "Kanal {LoserChannelName} ({LoserChannelId}) in {SurvivorChannelName} ({SurvivorChannelId}) zusammengeführt und auf {NewChannelName} umbenannt: {MovedLiveDayCount} Live-Tage übernommen, {CollapsedLiveDayCount} kollidierende Tage zusammengefaltet, {SessionCount} Abstimmungen übernommen.",
            loser.ChannelName, loser.Id, oldLogin, survivor.Id, newLogin, movedLiveDays, collapsedLiveDays, sessions.Count);
        await PublishHandoverAsync(oldLogin, newLogin, ct);
        return true;
    }

    /// <summary>
    /// The two commands a name change owes the worker, in the one order that works.
    /// </summary>
    private async Task PublishHandoverAsync(string oldLogin, string newLogin, CancellationToken ct)
    {
        // LEAVE first, and both only after the commit. The worker resolves the channel row *by name*
        // when it handles a JOIN, and that row only carries the new name once committed. The LEAVE
        // is what drops the old name's EmoteMatchCache entry and its 7TV EventAPI subscription
        // (Worker.cs) — without it the worker keeps matching chat under a name Twitch no longer
        // routes anywhere.
        try
        {
            await redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}{oldLogin}", ct);
            await redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.JoinPrefix}{newLogin}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row is already committed, so letting this escape would only cost the rest of the
            // tick — it could not undo anything. It also cannot be retried later: the next pass sees
            // case 1 (the stored name already matches Helix) and never publishes again, so this LEAVE/
            // JOIN pair is the only chance to push the rename to the worker directly.
            //
            // That does not strand the worker until a restart, though — SevenTvPeriodicResyncWorker's
            // convergence net (issue #41) closes the gap on its own within a couple of its 60s ticks:
            // ListActiveChannelNamesAsync reads the already-committed new name straight from the
            // database, so the very next tick's EnsureJoinedAsync/SyncChannelAsync join and sync
            // {NewChannelName} regardless of this publish. The old name lingers in
            // ITwitchChatManager's roster — still matching chat, still holding its 7TV subscription —
            // until RosterPrunePolicy sees it missing from two *consecutive* active-channel snapshots
            // and prunes it (RemoveChannel/Unsubscribe/LeaveChannelAsync). Net effect: roughly two to
            // three minutes of chat counted under the old name, not an indefinite stall. Saying so
            // loudly enough that the delay is noticed is the remedy here, which is why this is a
            // warning rather than a swallowed exception.
            logger.LogWarning(
                ex,
                "Kanal {ChannelName} ist auf {NewChannelName} nachgeführt, aber LEAVE/JOIN konnten nicht veröffentlicht werden — der Worker holt das über den periodischen 7TV-Resync (Konvergenznetz, Issue #41) innerhalb weniger Minuten von selbst nach, statt bis zu einem Neustart im alten Kanal zu bleiben.",
                oldLogin, newLogin);
        }
    }

    // The snapshot one pass works from. A record rather than a tuple so the projection reads as
    // three named facts about a channel.
    private sealed record ChannelIdentityRow(string Id, string? TwitchChannelId, string ChannelName);

    private sealed class ReconcileCounters
    {
        public int IdsBackfilled;
        public int Renamed;
        public int Merged;
        public int MergesRefused;
        public int LoginsMissing;
        public int Deactivated;
    }
}
