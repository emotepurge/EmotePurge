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
            return new ChannelIdentityReconcileSummary(0, 0, 0, 0, 0, 0);
        }

        var appToken = await appTokenProvider.GetTokenAsync(ct);
        if (appToken is null)
        {
            logger.LogInformation(
                "Kein App-Token verfügbar — Identitätsabgleich für {ChannelCount} Kanäle übersprungen.", rows.Count);
            return null;
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
            return null;
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

        var counters = new ReconcileCounters();
        // Rows this pass is done with: either merged away (the snapshot still lists them) or one
        // half of a duplicate pair the pass already ruled on. A duplicate is visible from both ends
        // — the id row that wants the name and the id-less row that holds it — and without this the
        // same merge would be attempted, refused and counted twice per tick.
        var settledChannelIds = new HashSet<string>(StringComparer.Ordinal);

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
            counters.LoginsMissing);
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

        var occupant = await db.LoadChannelAsync(newLogin, ct);
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
        var survivor = await db.LoadChannelByTwitchIdAsync(twitchChannelId, ct);
        if (survivor is null)
        {
            // The snapshot said this row exists; it no longer does. A concurrent purge is the only
            // known cause, and it is benign — but a silent return would leave a counter that simply
            // never moves and no trace of why.
            logger.LogInformation(
                "Überlebende Zeile mit Twitch-ID {TwitchChannelId} war beim Zusammenführen nicht mehr auffindbar — übersprungen.",
                twitchChannelId);
            return;
        }

        await MergeAsync(survivor, occupant, newLogin, twitchChannelId, counters, settledChannelIds, ct);
        warningState.Clear(ChannelIdentityWarningState.BlockedKey(row.Id));
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

        var holder = await db.LoadChannelByTwitchIdAsync(identity.Id, ct);
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
        // row survives — it holds the emotes and the usage history.
        var loser = await db.LoadChannelAsync(row.ChannelName, ct);
        if (loser is null)
        {
            logger.LogInformation(
                "Zusammenzuführende Zeile {ChannelName} war nicht mehr auffindbar — übersprungen.", row.ChannelName);
            return;
        }

        await MergeAsync(holder, loser, row.ChannelName, identity.Id, counters, settledChannelIds, ct);
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
    /// Folds <paramref name="loser"/> into <paramref name="survivor"/> and puts the survivor under
    /// <paramref name="newLogin"/>. Guarded: the loser must be emote-less.
    /// </summary>
    private async Task MergeAsync(
        Channel survivor,
        Channel loser,
        string newLogin,
        string twitchChannelId,
        ReconcileCounters counters,
        HashSet<string> settledChannelIds,
        CancellationToken ct)
    {
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

            return;
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
    }
}
