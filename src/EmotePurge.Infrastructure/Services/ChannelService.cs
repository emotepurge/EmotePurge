using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class ChannelService(
    AppDbContext db,
    IRedisPublisher redisPublisher,
    IChannelIdentityService channelIdentityService,
    IChannelEmoteSetObservationService emoteSetObservationService,
    IBroadcasterChannelLockService broadcasterChannelLocks,
    ChannelCapacityOptions channelCapacityOptions,
    IExcludedChannelFilter excludedChannelFilter,
    ILogger<ChannelService> logger) : IChannelService
{
    // The reason detail of a channel.purge entry written by the retention job, which is what tells it
    // apart from an admin's purge (that one carries no details).
    private const string RetentionPurgeReason = "retention";

    // The reason detail of a channel.purge entry written by PurgeByBroadcasterAsync (#245).
    private const string BroadcasterPurgeReason = "broadcasterRequest";

    // Attempts in total, not retries: the first try plus two more on a deadlock (R5).
    private const int BroadcasterPurgeMaxAttempts = 3;

    public async Task<ChannelJoinResult> JoinAsync(
        string channelName,
        AuditActor actor,
        bool isGlobalAdmin = false,
        bool liftBroadcasterLock = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalized = ChannelName.Normalize(channelName);

        // Asked before anything is written, and the only place in the join path that talks to
        // Twitch. The three answers are three different jobs: reject, follow the id, or carry on.
        var lookup = await channelIdentityService.LookupByLoginAsync(normalized, cancellationToken);

        // Opened only after the Helix call, so no row lock is ever held across an HTTP round trip.
        // Everything from here to the commit runs under SELECT ... FOR UPDATE on each existing row the
        // join may activate: without it, a retention purge committing between this join's read of an
        // inactive row and its save cascaded the channel's whole history away and then failed the join
        // with a concurrency exception (data-retention plan, finding 9). With it the two serialise —
        // see PurgeIfInactiveSinceAsync. Disposing without a commit (every rejection below) rolls back.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (lookup.Status == TwitchUserLookupStatus.NotFound)
        {
            return await HandleUnknownTwitchLoginAsync(
                normalized, actor, isGlobalAdmin, liftBroadcasterLock, transaction, cancellationToken);
        }

        // Null for Unavailable, and that is the whole contract of that status: without an identity
        // every branch below falls through to the name path this method has always run, so an outage
        // on our side costs nothing but the id — which the periodic reconciliation backfills.
        var identity = lookup.User;
        // Derived from Helix's answer rather than reused from `normalized`, although the two are
        // provably equal at this point: LookupByLoginAsync reports Found only when the normalized
        // logins match, so this cannot canonicalize anything the caller typed. It is written this
        // way because the stored name belongs to the identity that was resolved, not to the request
        // — should that matching rule ever loosen, this is the line that keeps the row on Helix's
        // spelling instead of silently storing the caller's.
        var targetName = identity is null ? normalized : ChannelName.Normalize(identity.Login);

        // Objection gate (GDPR Art. 21, issue #252): checked here, once the immutable Twitch id
        // is known and before ResolveJoinTargetAsync can create or lock a single row — never on the
        // login, which PurgeAsync's deletion of the old row would let a rejoin sidestep entirely.
        // Only reachable when identity is non-null (a Found lookup); an Unavailable lookup has no id
        // to check and falls through unchanged, the same gap PurgeAsync's own comments already
        // accept for "costs nothing but the id" (see docs/DECISIONS.md, this entry).
        if (identity is not null && excludedChannelFilter.IsExcluded(identity.Id))
        {
            logger.LogWarning("Join rejected: the target channel is on the excluded-channel list.");
            return ChannelJoinResult.Failed(ChannelJoinStatus.ChannelExcluded);
        }

        var (channel, isNewRow, renamedFrom) = await ResolveJoinTargetAsync(identity, targetName, actor, cancellationToken);

        // Objection gate, defense in depth (issue #260, P1 Codex finding): the check above only
        // ever sees the identity Helix resolved *this* call — never reached at all when Helix is
        // Unavailable, and blind to a stale row that already carries a blocked id from an earlier
        // resolution (the mirror image of HandleUnknownTwitchLoginAsync's own defense-in-depth
        // check, for the paths that go through ResolveJoinTargetAsync instead). Checked here
        // against the row that path actually picked for creation or reactivation — immediately
        // before CompleteJoinAsync can write to it, and after every branch that could have found an
        // existing row. Refusing is the only safe answer for a stale occupant carrying a blocked id
        // (docs/DECISIONS.md, this entry): joining it anyway would let Twitch's own bookkeeping
        // resurface a channel this codebase was told to stop observing. A brand-new row can never
        // trip this — its TwitchChannelId is either null (Unavailable) or the identity already
        // cleared above — so this only ever fires for a row ResolveJoinTargetAsync reused.
        if (!isNewRow && excludedChannelFilter.IsExcluded(channel.TwitchChannelId))
        {
            logger.LogWarning("Join rejected: the target channel is on the excluded-channel list.");
            return ChannelJoinResult.Failed(ChannelJoinStatus.ChannelExcluded);
        }

        // Broadcaster re-add lock (#245, plan P3): after the env gates (the env list wins) and only
        // once the row this join lands on is locked FOR UPDATE — the broadcaster's purge and the
        // identity reconcile's lock pass take the same row lock, so the decision cannot interleave
        // with either. Checked on the resolved identity and on the stored id of the chosen row: a
        // join during a Helix outage has no identity, but the row it reuses may carry the locked id.
        var lockDecision = await DecideBroadcasterLockAsync(
            [identity?.Id, channel.TwitchChannelId], actor, isGlobalAdmin, liftBroadcasterLock, cancellationToken);
        if (lockDecision.Refusal is { } refusal)
        {
            return refusal;
        }

        return await CompleteJoinAsync(
            channel, actor, isNewRow, renamedFrom, isGlobalAdmin, lockDecision.Lift, transaction, cancellationToken);
    }

    public async Task<bool> LeaveAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return false;
        }

        // Soft deactivate, not Remove(): the row hangs on four cascade edges (Channel -> Emote,
        // Emote -> UsageStat, Channel -> VoteSession, VoteSession -> Vote, Emote -> Vote), so a
        // hard delete threw away every emote, the entire daily usage history since the bot joined,
        // all vote sessions and all cast votes — none of it reconstructible, since 7TV only ever
        // returns the *current* set and past Twitch chat cannot be queried after the fact. A leave
        // is an operational action a moderator may perform; destroying history is not.
        // SevenTvPeriodicResyncWorker and Worker's boot recovery both filter on IsBotActive, and
        // JoinAsync reactivates the row, so nothing else needs to change. DeactivatedAtUtc is the
        // measuring point for the 180-day retention purge (RetentionPolicy) — nulled again by
        // whatever reactivates the row (CompleteJoinAsync's reactivation branch, the identity
        // merge). The write itself is shared with ChannelIdentityService's own objection-gate
        // deactivation — see ChannelDeactivation for why that could not just inject this service.
        //
        // Committed before published: if this throws (Redis outage), the row is already the source
        // of truth and SevenTvPeriodicResyncWorker's prune step (RosterPrunePolicy, issue #41) picks
        // the channel up within one resync interval regardless — this publish is an acceleration, not
        // a prerequisite. Same is true for JoinAsync below and TriggerResyncAsync via the periodic
        // sync loop itself; only this method needed a new convergence net, since JOIN/RESYNC already
        // had one.
        // The helper also closes the open observation interval (spec 4.3) in the same save.
        await ChannelDeactivation.DeactivateAsync(
            db, redisPublisher, emoteSetObservationService, channel, actor, ChannelDeactivationReason.Leave, cancellationToken);

        return true;
    }

    public async Task<bool> PurgeAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return false;
        }

        // The deliberate hard delete, cascading through emotes, usage stats, vote sessions and
        // votes. Publishes LEAVE first so the worker stops matching chat for a channel whose
        // emotes are about to disappear.
        //
        // Issue #41 checked this ordering rather than assuming it: unlike JoinAsync/LeaveAsync/
        // TriggerResyncAsync, the publish here already precedes SaveChangesAsync, so a Redis outage
        // aborts the method (500) before anything is written — consistent, if unavailable, and left
        // unchanged. A dedicated 503 for that case was considered and deferred (docs/DECISIONS.md,
        // 2026-09-01): lowest-priority of the three points in #41, and today's UnexpectedError/500
        // is at least honest about "nothing happened".
        await redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}{normalized}", cancellationToken);
        // The admin purge carries no details; that is what tells it apart from the other two.
        StagePurge(channel, actor, details: null);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<ChannelRetentionPurgeResult> PurgeIfInactiveSinceAsync(
        string channelName, DateTime deactivatedBeforeUtc, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (deactivatedBeforeUtc.Kind != DateTimeKind.Utc)
        {
            // Compared in memory against a timestamptz Npgsql reads back as UTC; DateTime comparison
            // ignores Kind, so a local cutoff would silently shift the retention period by the offset.
            throw new ArgumentException("The cutoff must be a UTC timestamp.", nameof(deactivatedBeforeUtc));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var channel = await db.LoadChannelForUpdateAsync(channelName, cancellationToken);
        if (channel is null)
        {
            // Nothing written, not even an audit entry: a no-op is not an event.
            return ChannelRetentionPurgeResult.NotFound;
        }

        // Checked here, under the lock, rather than trusted from the caller's candidate selection: a
        // join that committed in between has made the row active (and nulled DeactivatedAtUtc), and one
        // still in flight holds this lock until it has. A null stamp never compares as due.
        if (channel.IsBotActive || !(channel.DeactivatedAtUtc < deactivatedBeforeUtc))
        {
            return ChannelRetentionPurgeResult.StillActive;
        }

        // Same cascade and same audit action as the admin purge, told apart by the reason detail. No
        // LEAVE publish, unlike PurgeAsync: the worker is not in an inactive channel, and publishing
        // under the row lock would only hold the lock across a Redis round trip.
        StagePurge(channel, actor, details: new { reason = RetentionPurgeReason });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ChannelRetentionPurgeResult.Purged;
    }

    public async Task<ChannelBroadcasterPurgeResult> PurgeByBroadcasterAsync(
        string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalized = ChannelName.Normalize(channelName);

        // Steps 1-3 (plan T3): the ownership proof, read unlocked and settled before any transaction
        // opens, so no row lock is ever held across the Helix round trip (the join's rule too).
        var routed = await db.LoadChannelReadOnlyAsync(normalized, cancellationToken);
        if (routed is null)
        {
            return ChannelBroadcasterPurgeResult.NotFound;
        }

        bool loginProven;
        if (routed.TwitchChannelId is not null)
        {
            // Twitch ids are opaque digit strings, compared ordinally and never normalized. A stored id
            // is the proof; Twitch is not asked.
            if (!string.Equals(routed.TwitchChannelId, actor.TwitchUserId, StringComparison.Ordinal))
            {
                return ChannelBroadcasterPurgeResult.NotBroadcaster;
            }

            loginProven = false;
        }
        else
        {
            // An id-less row proves nothing by itself: only Twitch saying that this login is the
            // caller's account does. NotFound and Unavailable both leave the question open.
            var lookup = await channelIdentityService.LookupByLoginAsync(normalized, cancellationToken);
            if (lookup.Status != TwitchUserLookupStatus.Found)
            {
                return ChannelBroadcasterPurgeResult.IdentityUnresolved;
            }

            if (!string.Equals(lookup.User!.Id, actor.TwitchUserId, StringComparison.Ordinal))
            {
                return ChannelBroadcasterPurgeResult.NotBroadcaster;
            }

            loginProven = true;
        }

        // Steps 4-6 as one retry unit (R5): the full 7TV sync locks an existing emote-set leave
        // observation row before it updates Channels, this purge locks Channels and then cascades into
        // that row — the reverse order, so Postgres may pick either as a deadlock victim (40P01).
        // Reordering the sync was rejected; instead the whole transaction runs again from an empty change
        // tracker, so nothing staged by the failed attempt (audit entries, the lock, the removals)
        // survives into the next. The proof above is not repeated: the attempt re-checks it under the
        // row locks anyway.
        var outcome = default(BroadcasterPurgeOutcome);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                outcome = await PurgeProvenRowsAsync(normalized, actor, loginProven, cancellationToken);
                break;
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < BroadcasterPurgeMaxAttempts)
            {
                // The failed attempt's transaction has rolled back on dispose.
                db.ChangeTracker.Clear();
                logger.LogWarning(
                    "Broadcaster purge hit a database deadlock (attempt {Attempt} of {MaxAttempts}); retrying.",
                    attempt, BroadcasterPurgeMaxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 150)), cancellationToken);
            }
        }

        if (outcome.Result != ChannelBroadcasterPurgeResult.Purged)
        {
            return outcome.Result;
        }

        // Step 7: LEAVE only after the commit (unlike PurgeAsync, which publishes before it writes): a
        // LEAVE for a purge that then rolled back would drop a channel that is still tracked, while a
        // lost LEAVE after a committed purge is caught up by the periodic resync's roster prune within
        // two ticks — the row is the source of truth. So a failed publish is logged, never thrown: the
        // purge has happened and the caller must hear so. Neither name nor id in the line (#246).
        var failedPublishes = 0;
        foreach (var purgedName in outcome.PurgedChannelNames)
        {
            try
            {
                await redisPublisher.PublishAsync(
                    BotCommands.Channel, $"{BotCommands.LeavePrefix}{purgedName}", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedPublishes++;
            }
        }

        if (failedPublishes > 0)
        {
            logger.LogWarning(
                "Broadcaster purge committed, but {FailedCount} of {TotalCount} LEAVE publish(es) failed; the periodic roster prune catches up.",
                failedPublishes, outcome.PurgedChannelNames.Count);
        }

        return ChannelBroadcasterPurgeResult.Purged;
    }

    public async Task<ChannelDataSummary?> GetDataSummaryAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);
        var channelId = await db.Channels
            .AsNoTracking()
            .Where(c => c.ChannelName == normalized)
            .Select(c => c.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (channelId is null)
        {
            return null;
        }

        // Four scalar COUNTs over the channel id (Regel 10: no navigation GroupBy). Not one snapshot —
        // the numbers feed a confirmation dialog, not a ledger.
        return new ChannelDataSummary(
            EmoteCount: await db.Emotes.CountAsync(e => e.ChannelId == channelId, cancellationToken),
            VoteSessionCount: await db.VoteSessions.CountAsync(s => s.ChannelId == channelId, cancellationToken),
            LiveDayCount: await db.ChannelLiveDays.CountAsync(d => d.ChannelId == channelId, cancellationToken),
            TagCount: await db.EmoteTags.CountAsync(t => t.ChannelId == channelId, cancellationToken));
    }

    public async Task<Channel?> GetByNameAsync(string channelName, CancellationToken cancellationToken = default)
    {
        return await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
    }

    public Task<Channel?> GetActiveByTwitchChannelIdAsync(string twitchChannelId, CancellationToken cancellationToken = default) =>
        // The objection gate (an active row whose stored Twitch id is excluded reads as
        // untracked, same rule as ListActiveChannelNamesAsync) now lives in the shared query, next to
        // EmoteService's step 3a, which means the exact same thing by "this account's tracked
        // channel". Without it, the target picker (GET /api/seventv/me/emote-set-targets) surfaced a
        // channel the operator had blocked as a valid transfer target.
        // Since #245 the same query also hides a row whose id the broadcaster locked.
        db.LoadActiveChannelByTwitchIdReadOnlyAsync(twitchChannelId, excludedChannelFilter, broadcasterChannelLocks, cancellationToken);

    public async Task<IReadOnlyList<string>> ListActiveChannelNamesAsync(CancellationToken cancellationToken = default)
    {
        // AsNoTracking because every caller only ever reads the names: this runs once per minute
        // forever in SevenTvPeriodicResyncWorker, and tracking entities nobody mutates is pure cost.
        // The broadcaster lock (#245) is the opposite of the env list below: a table that changes at
        // runtime, so it is an anti-join in SQL rather than a filter on a cached set. An id-less row
        // never matches it (the lock's key is non-null); the 7TV sync's gate on the resolved id is what
        // holds such a row unobserved until the identity reconcile deactivates it.
        var activeRows = await db.Channels
            .AsNoTracking()
            .Where(c => c.IsBotActive)
            .Where(c => !broadcasterChannelLocks.Locks.Any(l => l.TwitchChannelId == c.TwitchChannelId))
            .Select(c => new { c.ChannelName, c.TwitchChannelId })
            .OrderBy(row => row.ChannelName)
            .ToListAsync(cancellationToken);

        // Objection gate (fourth Codex review of the block list): an active row whose stored Twitch
        // id is excluded is left out here, in memory and through the same IsExcluded the join path
        // uses, rather than as a second copy of the rule in SQL. The roster is capped well below a
        // hundred rows, so filtering after the read costs nothing. This is the one change that keeps
        // boot recovery from joining such a row after the operator adds the id and restarts the
        // worker (the identity reconcile only runs once boot recovery is over), and it makes the
        // periodic resync's roster prune part it within two ticks even when a LEAVE was lost. The
        // reconcile's own deactivation stays the durable, database-side step.
        return activeRows
            .Where(row => !excludedChannelFilter.IsExcluded(row.TwitchChannelId))
            .Select(row => row.ChannelName)
            .ToList();
    }

    public async Task<ChannelResyncResult> TriggerResyncAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return ChannelResyncResult.NotFound;
        }

        if (!channel.IsBotActive)
        {
            // See the interface remarks: syncing an inactive channel would subscribe its emote set
            // on the EventAPI while nothing consumes the events. No audit entry — nothing happened.
            return ChannelResyncResult.NotActive;
        }

        // Audited like JoinAsync's always-audit case: the row is untouched, but a command is
        // published and the worker will do real work because of it.
        db.AddAuditEntry(actor, AuditActions.ChannelResync, channelName: normalized);
        await db.SaveChangesAsync(cancellationToken);
        await redisPublisher.PublishAsync(BotCommands.Channel, $"{BotCommands.ResyncPrefix}{normalized}", cancellationToken);

        return ChannelResyncResult.Triggered;
    }

    /// <summary>
    /// Steps 4-6 of <see cref="PurgeByBroadcasterAsync"/>: one transaction that locks the candidate
    /// rows, re-checks the proof under the locks, stages the purge of every proven row and the re-add
    /// lock, and commits. Runs again in full after a deadlock, so it reads nothing it did not load itself.
    /// </summary>
    private async Task<BroadcasterPurgeOutcome> PurgeProvenRowsAsync(
        string normalized, AuditActor actor, bool loginProven, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Merge order (ChannelQueries): the row holding the Twitch id first, then the row holding the
        // name — the join and the identity merge lock in the same order, so none of them can wait on
        // another in a cycle.
        var idRow = await db.LoadChannelByTwitchIdForUpdateAsync(actor.TwitchUserId, cancellationToken);
        var nameRow = idRow is not null && string.Equals(idRow.ChannelName, normalized, StringComparison.Ordinal)
            ? null
            : await db.LoadChannelForUpdateAsync(normalized, cancellationToken);

        // The proof, checked again under the locks: the id row carries the caller's id by construction.
        // The name row belongs only while it is still id-less and its login was proven by Twitch; a row
        // that carries an id here and is not the id row carries someone else's (a concurrent backfill),
        // since the id is unique — it drops out.
        var targets = new List<Channel>(capacity: 2);
        if (idRow is not null)
        {
            targets.Add(idRow);
        }

        if (nameRow is not null && nameRow.TwitchChannelId is null && loginProven)
        {
            targets.Add(nameRow);
        }

        if (targets.Count == 0)
        {
            // Nothing written either way. Both rows gone means a concurrent purge or merge got there
            // first; a remaining name row that no longer qualifies is someone else's channel.
            return new BroadcasterPurgeOutcome(
                nameRow is null ? ChannelBroadcasterPurgeResult.NotFound : ChannelBroadcasterPurgeResult.NotBroadcaster,
                []);
        }

        foreach (var target in targets)
        {
            StagePurge(target, actor, details: new { reason = BroadcasterPurgeReason });
        }

        // Staged before the commit, in the same transaction as the deletion: a join waiting on these
        // row locks sees the lock the moment it gets them, and a purge that rolls back leaves no lock.
        await broadcasterChannelLocks.LockAsync(actor.TwitchUserId, DateTime.UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new BroadcasterPurgeOutcome(
            ChannelBroadcasterPurgeResult.Purged,
            targets.ConvertAll(target => target.ChannelName));
    }

    /// <summary>
    /// The one place a channel.purge entry is written (all three purge paths): the audit entry and the
    /// <c>Remove</c>, staged together for the caller's save. The cascade does the rest. The entry is
    /// the only trace this channel ever existed once the cascade has run, which is exactly why
    /// AuditLogEntry.ChannelName is a snapshot string and not a foreign key — an FK would have
    /// cascaded the entry away too.
    /// </summary>
    private void StagePurge(Channel channel, AuditActor actor, object? details)
    {
        db.AddAuditEntry(actor, AuditActions.ChannelPurge, channelName: channel.ChannelName, details: details);
        db.Channels.Remove(channel);
    }

    /// <summary>
    /// Twitch was reachable and knows no account under this login — but only a join that would
    /// *create* a row, or reactivate an inactive row that carries no Twitch id, is refused for it. That is what the rejection was for: a typo becoming a
    /// permanent, never-syncing row. On a channel we already track it would buy nothing and cost
    /// something real, because Helix answers the same way for a deleted account and for a banned
    /// one, and a ban can be lifted. Refusing here would let a temporary state block a moderator from
    /// rejoining a channel whose whole history we hold — the same restraint that keeps the
    /// reconciliation from ever leaving or purging a channel Twitch stopped knowing.
    /// <para>
    /// Deliberately not folded into the Unavailable path even though the two now agree in this one
    /// case: the whole point of the three-state lookup is that "no such account" and "we could not
    /// ask" stay distinct, and only one of them can refuse a join at all.
    /// </para>
    /// <para>
    /// The lookup is by name, and that is not a shortcut: this branch has no identity, so there is no
    /// id to look up by.
    /// </para>
    /// </summary>
    private async Task<ChannelJoinResult> HandleUnknownTwitchLoginAsync(
        string normalized,
        AuditActor actor,
        bool isGlobalAdmin,
        bool liftBroadcasterLock,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var knownChannel = await db.LoadChannelForUpdateAsync(normalized, cancellationToken);
        if (knownChannel is null)
        {
            // Nothing is written — not even an audit entry, because nothing happened.
            logger.LogInformation(
                "Join für {ChannelName} abgelehnt: Twitch kennt diesen Login nicht und wir führen keine Zeile dazu.",
                normalized);
            return ChannelJoinResult.Failed(ChannelJoinStatus.ChannelNotOnTwitch);
        }

        // Objection gate, defense in depth: realistically unreachable in the normal objection
        // procedure (PurgeAsync deletes the row a blocked id would be found under), but a known row
        // can still carry a blocked id here if Twitch stopped answering for this login afterward —
        // refuse the same way the identity-resolved path above does, rather than silently reactivate
        // a row this codebase otherwise treats as still worth rejoining (see the class remark below).
        if (knownChannel.TwitchChannelId is not null && excludedChannelFilter.IsExcluded(knownChannel.TwitchChannelId))
        {
            logger.LogWarning("Join rejected: the target channel is on the excluded-channel list.");
            return ChannelJoinResult.Failed(ChannelJoinStatus.ChannelExcluded);
        }

        // Broadcaster re-add lock (#245), the counterpart of the check in JoinAsync for this path:
        // without an identity the stored id is all there is, and the row is already locked FOR UPDATE.
        // The likely shape is a row the identity reconcile deactivated for its lock, joined while
        // Twitch does not know the login.
        var lockDecision = await DecideBroadcasterLockAsync(
            [knownChannel.TwitchChannelId], actor, isGlobalAdmin, liftBroadcasterLock, cancellationToken);
        if (lockDecision.Refusal is { } refusal)
        {
            return refusal;
        }

        // An inactive row without a Twitch id is refused like an unknown login (fourth Codex review of
        // the block list). The ban restraint in the summary above rests on the stored id: it is what
        // says this row is the channel whose ban may be lifted. Without one, nothing ties the row to
        // any account at all — Twitch does not know the login, and we never learned an id — and one
        // such row is exactly what the objection gate cannot recognise: an id-less duplicate the
        // identity reconcile deactivated because its login resolved to an excluded id that another
        // row already holds, so the id could not be written onto it. Reactivating it here would put
        // a blocked channel back under observation. An active row is left alone: joining it
        // reactivates nothing. Logged without the name, since this may be that row.
        if (!knownChannel.IsBotActive && knownChannel.TwitchChannelId is null)
        {
            logger.LogInformation(
                "Join rejected: Twitch does not know this login and the inactive row under it has no Twitch id to confirm it by.");
            return ChannelJoinResult.Failed(ChannelJoinStatus.ChannelNotOnTwitch);
        }

        // No rename: there is nothing to rename onto. The stored TwitchChannelId stays exactly as it
        // is — it remains the best information we have about this channel, and clearing it would
        // throw away the one field that survives a login change.
        logger.LogInformation(
            "Twitch kennt den Login {ChannelName} gerade nicht (gesperrt oder gelöscht) — Join läuft auf die bestehende Zeile weiter, die gespeicherte Twitch-ID bleibt unverändert.",
            normalized);
        return await CompleteJoinAsync(
            knownChannel, actor, isNewRow: false, renamedFrom: null, isGlobalAdmin, lockDecision.Lift, transaction, cancellationToken);
    }

    /// <summary>
    /// The broadcaster re-add lock's decision for one join (#245, plan contract "Join"), over the ids
    /// the join could be about — the resolved identity and the stored id of the row it landed on.
    /// For every locked one, in this binding order: the actor is that broadcaster → lifted; a global
    /// admin who asked for it → lifted, recorded as the admin's; anyone else, an admin without the
    /// flag included → refused with the lock's date. Nothing is staged here: a lift is carried to
    /// <see cref="CompleteJoinAsync"/>, which stages it only once the cap has let the join through.
    /// </summary>
    private async Task<BroadcasterLockDecision> DecideBroadcasterLockAsync(
        IEnumerable<string?> candidateIds,
        AuditActor actor,
        bool isGlobalAdmin,
        bool liftBroadcasterLock,
        CancellationToken cancellationToken)
    {
        var liftedIds = new List<string>(capacity: 2);
        DateTime? liftedByAdminLockedAtUtc = null;
        foreach (var candidateId in candidateIds.Distinct(StringComparer.Ordinal))
        {
            var lockedAtUtc = await broadcasterChannelLocks.GetLockedAtUtcAsync(candidateId, cancellationToken);
            if (lockedAtUtc is null)
            {
                continue;
            }

            // Twitch ids are opaque digit strings, compared ordinally. The owner case comes first, so
            // a broadcaster who is also a global admin lifts their own lock without the admin dialog.
            if (string.Equals(actor.TwitchUserId, candidateId, StringComparison.Ordinal))
            {
                liftedIds.Add(candidateId!);
                continue;
            }

            if (isGlobalAdmin && liftBroadcasterLock)
            {
                liftedIds.Add(candidateId!);
                liftedByAdminLockedAtUtc = lockedAtUtc;
                continue;
            }

            // Neither the id nor the name: the lock is not secret, but the line has nothing to add
            // that the 403/409 does not already tell the caller.
            logger.LogInformation("Join rejected: the broadcaster has locked the channel against re-adding.");
            return new BroadcasterLockDecision(ChannelJoinResult.LockedByBroadcaster(lockedAtUtc.Value), Lift: null);
        }

        return liftedIds.Count == 0
            ? new BroadcasterLockDecision(Refusal: null, Lift: null)
            : new BroadcasterLockDecision(Refusal: null, new BroadcasterLockLift(liftedIds, liftedByAdminLockedAtUtc));
    }

    /// <summary>
    /// Decides which row a join with a resolved identity (or none, for Unavailable) lands on: the row
    /// already holding the Twitch id if there is one, otherwise the row already holding the target
    /// name, otherwise a freshly created row. Every existing row it returns is locked; when it looks at
    /// two, the id row is locked before the name row (the order the identity merge uses too).
    /// </summary>
    private async Task<(Channel Channel, bool IsNewRow, string? RenamedFrom)> ResolveJoinTargetAsync(
        TwitchUserIdentity? identity, string targetName, AuditActor actor, CancellationToken cancellationToken)
    {
        Channel? channel = null;
        string? renamedFrom = null;
        if (identity is not null)
        {
            (channel, renamedFrom) = await ResolveChannelByIdentityAsync(identity, targetName, actor, cancellationToken);
        }

        if (channel is not null)
        {
            return (channel, false, renamedFrom);
        }

        var (resolvedChannel, isNewRow) = await ResolveOrCreateChannelByNameAsync(identity, targetName, cancellationToken);
        return (resolvedChannel, isNewRow, renamedFrom);
    }

    /// <summary>
    /// The id-first half of <see cref="ResolveJoinTargetAsync"/>: follows the row Helix's identity
    /// already points at, handling the rename it may reveal along the way. Returns a null channel when
    /// no row holds this Twitch id yet, so the caller falls back to matching by name.
    /// </summary>
    private async Task<(Channel? Channel, string? RenamedFrom)> ResolveChannelByIdentityAsync(
        TwitchUserIdentity identity, string targetName, AuditActor actor, CancellationToken cancellationToken)
    {
        // Twitch ids are opaque digit strings — never normalized, always compared ordinally.
        var rowWithId = await db.LoadChannelByTwitchIdForUpdateAsync(identity.Id, cancellationToken);
        if (rowWithId is null)
        {
            return (null, null);
        }

        if (string.Equals(rowWithId.ChannelName, targetName, StringComparison.Ordinal))
        {
            return (rowWithId, null);
        }

        var occupant = await db.LoadChannelForUpdateAsync(targetName, cancellationToken);
        if (occupant is null)
        {
            // The channel was renamed on Twitch since we last looked, and this join is the moment we
            // find out. Also the only route for an *inactive* row: the periodic reconciliation scans
            // active channels only, so nothing else would ever bring it back under its real name.
            var renamedFrom = rowWithId.ChannelName;
            rowWithId.ChannelName = targetName;
            // The rename is its own tracking gap — the IRC join pointed at a name that no longer
            // answered — independent of whether the row was also inactive.
            rowWithId.TrackingResumedAt = DateTime.UtcNow;
            // Closes the open observation interval (spec 4.3) — tracked only, riding
            // CompleteJoinAsync's SaveChangesAsync together with this rename's audit entry. The next
            // successful sync opens a fresh interval; this method never does.
            await emoteSetObservationService.CloseOpenIntervalAsync(
                rowWithId.Id, ChannelEmoteSetObservationClosedBy.Rename, cancellationToken);
            db.AddAuditEntry(
                actor,
                AuditActions.ChannelRename,
                channelName: targetName,
                details: new { twitchChannelId = identity.Id, oldLogin = renamedFrom, newLogin = targetName });
            return (rowWithId, renamedFrom);
        }

        // A second row already sits on the new name — the duplicate a rename leaves behind. Renaming
        // into it would violate IX_Channels_ChannelName and turn this join into a 500; merging the two
        // is the reconciliation's job, which refuses rather than guesses when emote histories are
        // involved. So the join proceeds on the occupant, exactly as it did before identities were
        // resolved here.
        //
        // Not logged when the occupant carries an excluded id (fourth Codex review of the block
        // list): JoinAsync refuses that join right after this returns, and a line naming the target
        // name — the blocked channel's last login — next to that refusal would tie the block to it.
        if (!excludedChannelFilter.IsExcluded(occupant.TwitchChannelId))
        {
            logger.LogWarning(
                "Kanal {ChannelName} (Twitch-ID {TwitchChannelId}) heißt auf Twitch jetzt {NewChannelName}, aber dieser Name gehört bereits einer anderen Zeile — Join läuft auf die bestehende Zeile, die Zusammenführung übernimmt der periodische Abgleich.",
                rowWithId.ChannelName, identity.Id, targetName);
        }

        return (occupant, null);
    }

    /// <summary>
    /// The name-fallback half of <see cref="ResolveJoinTargetAsync"/>, reached whenever the identity
    /// path found no row to join (no identity at all, or no row holding that Twitch id yet).
    /// </summary>
    private async Task<(Channel Channel, bool IsNewRow)> ResolveOrCreateChannelByNameAsync(
        TwitchUserIdentity? identity, string targetName, CancellationToken cancellationToken)
    {
        // Locked like the id path's rows. Null also when a purge held this row and committed while we
        // waited — then this join simply creates the channel afresh, the outcome the purge's contract
        // promises instead of a 500.
        var channel = await db.LoadChannelForUpdateAsync(targetName, cancellationToken);
        if (channel is null)
        {
            // A new row gets the id straight away, so this channel's first rename is already
            // followable — that is the point of asking Helix before writing.
            channel = new Channel { ChannelName = targetName, TwitchChannelId = identity?.Id, IsBotActive = true };
            db.Channels.Add(channel);
            return (channel, true);
        }

        if (identity is not null && channel.TwitchChannelId is null)
        {
            // Free backfill on a row that predates this: reached only when no row holds the id, so
            // the unique index on TwitchChannelId cannot object. Not audited and no
            // TrackingResumedAt — nothing about the channel changed, we merely wrote down what it
            // always was.
            channel.TwitchChannelId = identity.Id;
        }
        else if (identity is not null
                 && !string.Equals(channel.TwitchChannelId, identity.Id, StringComparison.Ordinal)
                 // Silent when the stored id is excluded, for the same reason as the occupant warning
                 // in ResolveChannelByIdentityAsync: JoinAsync refuses this join next, and the line
                 // would name the blocked id and its row's login right beside that refusal.
                 && !excludedChannelFilter.IsExcluded(channel.TwitchChannelId))
        {
            // The row under this name claims a different Twitch id than Helix does — the mirror image
            // of the occupant case in ResolveChannelByIdentityAsync, reached when the id's own row
            // does not exist (or no longer does). Nothing is written: overwriting the stored id would
            // fuse two genuinely different channels, and the periodic reconciliation resolves the pair
            // from its own side. Logged only so the state is diagnosable while it lasts; it is not an
            // error, and a join in this state behaves exactly as it did before.
            logger.LogInformation(
                "Kanal {ChannelName} trägt die Twitch-ID {StoredTwitchChannelId}, Helix nennt für diesen Login aber {TwitchChannelId} — Join läuft unverändert auf der bestehenden Zeile, die Auflösung übernimmt der periodische Abgleich.",
                channel.ChannelName, channel.TwitchChannelId, identity.Id);
        }

        return (channel, false);
    }

    /// <summary>
    /// The half of a join every path shares once the row to join has been decided: cap check,
    /// reactivate, audit, commit (of the transaction <see cref="JoinAsync"/> opened), publish. A method rather than a fall-through, so the branch that
    /// joins a channel Twitch has stopped knowing can reach it without being merged into the identity
    /// logic it deliberately has none of.
    /// </summary>
    private async Task<ChannelJoinResult> CompleteJoinAsync(
        Channel channel,
        AuditActor actor,
        bool isNewRow,
        string? renamedFrom,
        bool isGlobalAdmin,
        BroadcasterLockLift? lockLift,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        // A join only ever *activates* a channel on a new row (constructed with IsBotActive = true
        // and not yet saved) or on an existing row that is currently inactive — never on one that is
        // already active, which is the idempotency this cap must not break: a moderator clicking
        // "join" twice, or two open tabs doing the same thing, must never turn into a 409 just
        // because the cap happens to be full.
        var activatesChannel = isNewRow || !channel.IsBotActive;
        if (activatesChannel && !isGlobalAdmin)
        {
            // No row exclusion needed either way: a brand-new row has not been saved yet and cannot
            // appear in this count, and an inactive row is filtered out by IsBotActive itself.
            //
            // Deliberately unlocked — two joins racing this check can both read a count below the
            // cap and both proceed, overshooting it by (at most) the number of concurrent joins. The
            // operator accepted that: a hard lock around every join is not worth it for a cap whose
            // whole purpose is staying comfortably clear of Twitch's real 100-chatroom ceiling, not
            // hitting it to the channel (docs/DECISIONS.md, this entry).
            var activeChannelCount = await db.Channels.CountAsync(c => c.IsBotActive, cancellationToken);
            if (activeChannelCount >= channelCapacityOptions.MaxActiveChannels)
            {
                logger.LogWarning(
                    "Join for {ChannelName} rejected: the active-channel cap of {MaxActiveChannels} is reached ({ActiveChannelCount} channels active).",
                    channel.ChannelName, channelCapacityOptions.MaxActiveChannels, activeChannelCount);
                return ChannelJoinResult.Failed(ChannelJoinStatus.CapacityReached);
            }
        }

        if (!isNewRow)
        {
            // Only a join that actually reactivates the channel restarts the tracking clock. A join
            // on an already-active channel is a no-op for coverage — it publishes a JOIN command,
            // but nothing was ever missed, so moving the marker would falsely shorten the history
            // we claim to have.
            if (!channel.IsBotActive)
            {
                channel.TrackingResumedAt = DateTime.UtcNow;
                // The retention clock (DeactivatedAtUtc) stops here too — the channel is no longer
                // deactivated, so it must not become a purge candidate while active again.
                channel.DeactivatedAtUtc = null;
            }

            channel.IsBotActive = true;
        }

        // The broadcaster lock's lift (#245), staged only now — after the cap check above, so a join
        // the cap refuses leaves the lock exactly as it was (plan P4) — and in this transaction, so
        // the lock disappears in the same commit as the reactivation it allows.
        object? joinDetails = null;
        if (lockLift is not null)
        {
            foreach (var lockedId in lockLift.TwitchChannelIds)
            {
                await broadcasterChannelLocks.UnlockAsync(lockedId, cancellationToken);
            }

            // The admin variant has to stay recognisable as a third party overriding the
            // broadcaster's decision, with the date of the decision it overrode.
            joinDetails = lockLift.LiftedByAdminLockedAtUtc is { } lockedAtUtc
                ? new { broadcasterLockLifted = true, liftedByAdmin = true, lockedAtUtc }
                : new { broadcasterLockLifted = true };
        }

        // Audited unconditionally, including the "already active" case: every join publishes a JOIN
        // command and makes the worker (re)enter the channel, so something did happen even when the
        // row itself is unchanged. This is the one place the no-op rule does not apply.
        db.AddAuditEntry(actor, AuditActions.ChannelJoin, channelName: channel.ChannelName, details: joinDetails);

        await db.SaveChangesAsync(cancellationToken);
        // Releases the row locks. The publishes below stay after it, as before: the worker resolves the
        // row by name when it handles the JOIN, so it must already see the committed state.
        await transaction.CommitAsync(cancellationToken);

        if (renamedFrom is not null)
        {
            // LEAVE before JOIN, both after the commit — the same handover order the reconciliation
            // publishes: the worker resolves the row by name when it handles the JOIN, and the LEAVE
            // is what drops the old name's match cache and its 7TV EventAPI subscription.
            await redisPublisher.PublishAsync(
                BotCommands.Channel, $"{BotCommands.LeavePrefix}{renamedFrom}", cancellationToken);
        }

        await redisPublisher.PublishAsync(
            BotCommands.Channel, $"{BotCommands.JoinPrefix}{channel.ChannelName}", cancellationToken);

        return ChannelJoinResult.Joined(channel);
    }

    // A deadlock surfaces either straight from a locking query (PostgresException) or from a save
    // (DbUpdateException around it), so the whole chain is searched.
    private static bool IsDeadlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct BroadcasterPurgeOutcome(
        ChannelBroadcasterPurgeResult Result, IReadOnlyList<string> PurgedChannelNames);

    // Refusal non-null: the join stops here with LockedByBroadcaster. Otherwise Lift says which locks
    // the join removes (null when none applied).
    private readonly record struct BroadcasterLockDecision(ChannelJoinResult? Refusal, BroadcasterLockLift? Lift);

    // LiftedByAdminLockedAtUtc is set when a global admin lifted a lock that is not their own.
    private sealed record BroadcasterLockLift(IReadOnlyList<string> TwitchChannelIds, DateTime? LiftedByAdminLockedAtUtc);
}
