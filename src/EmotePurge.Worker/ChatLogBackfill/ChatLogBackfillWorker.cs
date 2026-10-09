using System.Data.Common;
using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Matching;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Services;

namespace EmotePurge.Worker.ChatLogBackfill;

/// <summary>
/// The chat-log backfill's sequential queue (#346, spec section 4): claims one run at a time, reads
/// its window block by block from the archive, counts every block against the run's persisted
/// matching snapshot and commits it through <see cref="IChatLogBackfillService"/>. Off unless
/// <c>ChatLogBackfill:Enabled</c> is true; then it waits for the boot recovery like
/// <see cref="DataRetentionWorker"/> and loops for the worker's lifetime.
/// <para>
/// <b>Scopes and instances.</b> The loop resolves its <see cref="IChatLogBackfillService"/> from
/// exactly one async scope that lives as long as the loop and is that instance's only user: the
/// instance holds the advisory-lock connection (D46) and is not thread-safe. The per-run status
/// monitor (D39) resolves its own instance from its own async scope and only ever asks
/// <see cref="IChatLogBackfillService.IsRunActiveAsync"/>. The archive client is resolved once per
/// run from a third scope and held for the run, because its pacing is instance state (D7).
/// </para>
/// <para>
/// <b>The database decides, the worker follows.</b> Every counter the worker acts on comes from the
/// last transition the service returned (D38); a status other than <c>running</c> stops the run at
/// once. A 429 ends the run's turn (D44): the pause is spent in the loop, which waits out the persisted
/// provider cooldown (D31) before it claims again. The loop lock is (re)acquired only before a claim;
/// inside a run it is probed — before every archive request and right after every read, before any
/// write — and never retaken: a lock lost during a run stays lost until the next claim (D46). Nothing
/// here holds a database lock of its own — the service takes them, channel row before run row.
/// </para>
/// <para>
/// <b>Abandon and resume</b> (operator decision 2026-10-09). When the worker cannot tell whether it may
/// still write — the lock is gone, the service refused a block while the row still says
/// <c>running</c>, a transient database error, an exception before the block loop, a failure that could
/// not be recorded — it writes nothing, treats the lock as lost and resumes at the next claim from the
/// persisted <c>WeeksDone</c>. Three such events in a row for one run fail it with
/// <c>worker_error</c>; a committed block resets the count.
/// </para>
/// <para>
/// <b>Logs</b> name the channel, the run id, the block range and counts — never message text or a
/// chatter id. A run failed at claim for an excluded channel is logged by the service without a name
/// (D32).
/// </para>
/// </summary>
public sealed class ChatLogBackfillWorker(
    ILogger<ChatLogBackfillWorker> logger,
    IServiceScopeFactory scopeFactory,
    ChatLogBackfillOptions options,
    ChatLogArchiveOptions archiveOptions,
    BootRecoveryGate bootRecoveryGate,
    ChatLogBackfillSignal signal,
    IRedisPublisher redisPublisher,
    IBotChatterDetector botChatterDetector,
    IExcludedChatterFilter excludedChatterFilter,
    TimeProvider timeProvider) : BackgroundService
{
    /// <summary>Transport attempts on one block are exhausted (spec 4.5).</summary>
    public const string TransportFailureErrorCode = "transport_failure";

    /// <summary>The archive answered with something the line parser could not read, or a line over the limit.</summary>
    public const string MalformedResponseErrorCode = "malformed_response";

    /// <summary>A block exceeded <c>MaxBlockMegabytes</c>.</summary>
    public const string BlockTooLargeErrorCode = "block_too_large";

    /// <summary>The run's matching snapshot is empty (defensive; the enqueue refuses an empty set).</summary>
    public const string SnapshotMissingErrorCode = "snapshot_missing";

    /// <summary>A live usage row exists for an imported cell (D3, D48).</summary>
    public const string LiveRowConflictErrorCode = "live_row_conflict";

    /// <summary>Anything unexpected: a block plan that does not fit the row, or too many abandons in a row.</summary>
    public const string WorkerErrorCode = "worker_error";

    /// <summary>Consecutive abandon-and-resume events on one run before it fails with <c>worker_error</c>.</summary>
    public const int MaxConsecutiveAbandons = 3;

    private const long BytesPerMegabyte = 1024 * 1024;

    // Consecutive abandon-and-resume events per run id, with the WeeksDone they happened at; reset by a
    // committed block, dropped when the run ends or a later claim shows progress made elsewhere. In
    // memory on purpose: a process restart is itself a resume and starts the count afresh.
    private readonly Dictionary<long, (int Count, int WeeksDone)> _abandonsInARow = [];

    // The start of the previous archive request, for the worker-side spacing (D7). Across runs on
    // purpose: a fresh run's fresh client knows nothing about the previous run's last request.
    private DateTime? _lastRequestStartedUtc;

    private TimeSpan IdlePoll => TimeSpan.FromSeconds(options.IdlePollSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await bootRecoveryGate.Completed.WaitAsync(stoppingToken);

        if (!options.Enabled)
        {
            logger.LogInformation("Chat-log backfill disabled.");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // The loop's one service instance: it owns the advisory-lock connection, and disposing
                // this scope at shutdown is what releases the lock.
                var scope = scopeFactory.CreateAsyncScope();
                await using (scope)
                {
                    IChatLogBackfillService service;
                    try
                    {
                        service = scope.ServiceProvider.GetRequiredService<IChatLogBackfillService>();
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Never out of ExecuteAsync: StopHost would take the live counting down with it.
                        logger.LogWarning(ex, "Chat-log backfill could not start its loop; retrying in {Seconds} s.", options.IdlePollSeconds);
                        await Task.Delay(IdlePoll, timeProvider, stoppingToken);
                        continue;
                    }

                    await RunLoopAsync(service, stoppingToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular shutdown. A run interrupted mid-block stays running and is requeued at the next boot.
        }
    }

    // Spec 4.2: lock, boot reset under the lock, then claim the head whenever the cooldown allows.
    private async Task RunLoopAsync(IChatLogBackfillService service, CancellationToken stoppingToken)
    {
        var holdsLock = false;
        var warnedAboutLock = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Before every claim (D46): a lock lost with its connection is noticed here, before
                // anything else is claimed, reset or committed. Probed first: TryAcquireLoopLockAsync
                // would retake a lock that died since the run's last in-run probe without saying so,
                // and a retake must always go through the reset below.
                if (holdsLock && !await service.HoldsLoopLockAsync(stoppingToken))
                {
                    holdsLock = false;
                }

                if (!await service.TryAcquireLoopLockAsync(stoppingToken))
                {
                    if (!warnedAboutLock)
                    {
                        logger.LogWarning(
                            "Chat-log backfill: another backfill loop holds the lock; retrying every {Seconds} s.",
                            options.IdlePollSeconds);
                        warnedAboutLock = true;
                    }

                    holdsLock = false;
                    await Task.Delay(IdlePoll, timeProvider, stoppingToken);
                    continue;
                }

                if (!holdsLock)
                {
                    // Only under the lock, and again after the lock was lost and retaken: no run of
                    // this loop is in flight here, so every running row is an interrupted one.
                    var requeued = await service.ResetInterruptedRunsAsync(stoppingToken);
                    holdsLock = true;
                    warnedAboutLock = false;
                    logger.LogInformation("Chat-log backfill loop holds the lock; {Count} interrupted runs requeued.", requeued);
                }

                // The provider cooldown (D31) holds every claim, not only the paused run's: a 429 is the
                // archive's answer to us, whichever run asked. It is written in the same transaction as a
                // pause, so it also covers the paused head's own due time (D44). Waited in slices of at
                // most one idle poll, so the lock is re-checked while waiting.
                var cooldown = ChatLogBackfillRetryPolicy.CooldownWait(await service.GetCooldownUntilAsync(stoppingToken), UtcNow());
                if (cooldown > TimeSpan.Zero)
                {
                    await Task.Delay(cooldown < IdlePoll ? cooldown : IdlePoll, timeProvider, stoppingToken);
                    continue;
                }

                var claim = await service.ClaimNextAsync(UtcNow(), stoppingToken);
                if (claim is null)
                {
                    // Nothing to do, or the head is paused and not yet due: a BACKFILL: nudge or the idle
                    // poll wakes the loop (D8).
                    await signal.WaitAsync(IdlePoll, timeProvider, stoppingToken);
                    continue;
                }

                if (await RunAsync(service, claim, stoppingToken) == RunOutcome.Abandoned)
                {
                    // The lost-lock path: retake the lock (and with it the reset) before anything else.
                    holdsLock = false;
                    await Task.Delay(IdlePoll, timeProvider, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A database outage costs a poll, never the host (StopHost would take the live
                // counting down with it). The lock may have died with its connection: the next turn
                // takes it again and repeats the reset.
                holdsLock = false;
                logger.LogWarning(ex, "Chat-log backfill loop failed; retrying in {Seconds} s.", options.IdlePollSeconds);
                await Task.Delay(IdlePoll, timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Works one claimed run until it completes, fails, pauses (a 429 returns here, D44), is no longer
    /// running, or has to be abandoned. A process stop leaves the row <c>running</c> for the next boot's
    /// reset.
    /// </summary>
    private async Task<RunOutcome> RunAsync(IChatLogBackfillService service, ChatLogBackfillClaim claim, CancellationToken stoppingToken)
    {
        // Claim → running, a fresh start and a resume after a pause alike.
        await PublishProgressAsync(claim.ChannelName, stoppingToken);
        logger.LogInformation(
            "Chat-log backfill run {RunId} for {Channel} running: {From:yyyy-MM-dd}..{To:yyyy-MM-dd}, week {WeeksDone} of {WeeksTotal} done, pause count {PauseCount}, attempts {BlockAttempts}.",
            claim.RunId, claim.ChannelName, claim.WindowFrom, claim.WindowTo, claim.WeeksDone, claim.WeeksTotal, claim.PauseCount,
            claim.BlockAttempts);

        var outcome = await RunClaimedAsync(service, claim, stoppingToken);
        if (outcome == RunOutcome.Finished)
        {
            _abandonsInARow.Remove(claim.RunId);
        }

        return outcome;
    }

    private async Task<RunOutcome> RunClaimedAsync(IChatLogBackfillService service, ChatLogBackfillClaim claim, CancellationToken stoppingToken)
    {
        var state = new RunState { WeeksDone = claim.WeeksDone };

        // A count from before progress somebody else made (another loop that held the lock meanwhile, or
        // a commit the server applied while this process saw an error) is stale: the run moved on.
        if (_abandonsInARow.TryGetValue(claim.RunId, out var pending) && claim.WeeksDone != pending.WeeksDone)
        {
            _abandonsInARow.Remove(claim.RunId);
        }

        // Abandoned three times already at this very progress, and the failure was deferred or could not
        // be recorded then: record it now, before a single further request (no read, so 0 bytes).
        if (_abandonsInARow.GetValueOrDefault(claim.RunId).Count >= MaxConsecutiveAbandons)
        {
            return await FailOrAbandonAsync(service, claim, state, WorkerErrorCode, null, stoppingToken);
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = Task.CompletedTask;
        AsyncServiceScope? monitorScope = null;
        try
        {
            // One client for the whole run: its pacing is instance state (D7). Resolved before the
            // monitor starts, so a resolution failure leaves nothing running behind it.
            await using var clientScope = scopeFactory.CreateAsyncScope();
            var client = clientScope.ServiceProvider.GetRequiredService<IChatLogArchiveClient>();

            // Spec 4.3: the persisted snapshot, never Emotes or the channel's active set (D10).
            var snapshot = await service.GetSnapshotAsync(claim.RunId, stoppingToken);
            if (snapshot.Emotes.Count == 0)
            {
                return await FailOrAbandonAsync(service, claim, state, SnapshotMissingErrorCode, null, stoppingToken);
            }

            var map = EmoteNameMatching.Coalesce(snapshot.Emotes.Select(e => new KeyValuePair<string, string>(e.Name, e.EmoteId)));
            if (map.AmbiguousNames.Count > 0)
            {
                logger.LogInformation(
                    "Chat-log backfill run {RunId}: {Count} aliases appear twice in the snapshot; the first emote id wins.",
                    claim.RunId, map.AmbiguousNames.Count);
            }

            var addedToSetDay = new Dictionary<string, DateOnly?>(StringComparer.Ordinal);
            foreach (var emote in snapshot.Emotes)
            {
                addedToSetDay.TryAdd(emote.EmoteId, emote.AddedToSetDay);
            }

            var plan = ChatLogBackfillBlockPlanner.Plan(claim.WindowFrom, claim.WindowTo);
            if (plan.Count != claim.WeeksTotal || claim.WeeksDone < 0 || claim.WeeksDone >= plan.Count)
            {
                // Deterministic: a resume would find the same row, so this fails at once.
                logger.LogError(
                    "Chat-log backfill run {RunId}: the block plan ({Blocks} blocks) does not fit the row (week {WeeksDone} of {WeeksTotal}).",
                    claim.RunId, plan.Count, claim.WeeksDone, claim.WeeksTotal);
                return await FailOrAbandonAsync(service, claim, state, WorkerErrorCode, null, stoppingToken);
            }

            // The persisted attempt limit holds across a resume (operator decision 2026-10-09, Codex/Fable):
            // the attempt that exhausted it is booked, but its failure was not recorded (FailAsync threw,
            // or the process stopped between the two writes). Record it now, without another request;
            // that attempt's bytes are already booked.
            if (claim.BlockAttempts >= options.TransportRetries)
            {
                logger.LogWarning(
                    "Chat-log backfill run {RunId} ({Channel}): {Attempts} transport attempts already used on its next block; failing it without another request.",
                    claim.RunId, claim.ChannelName, claim.BlockAttempts);
                return await FailOrAbandonAsync(service, claim, state, TransportFailureErrorCode, null, stoppingToken);
            }

            // The status monitor's own instance (D39): a second user of the loop's instance would race it.
            monitorScope = scopeFactory.CreateAsyncScope();
            monitor = MonitorAsync(monitorScope.Value.ServiceProvider.GetRequiredService<IChatLogBackfillService>(), claim.RunId, runCancellation);
            state.InBlockLoop = true;
            return await RunBlocksAsync(service, client, claim, plan, map.NameToId, addedToSetDay, state, runCancellation.Token, stoppingToken);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // The monitor saw the row leave running (cancel, leave, purge) and aborted a read or a wait.
            LogStopped(claim);
            return RunOutcome.Finished;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Before the block loop (client, snapshot, monitor scope) every failure is resumable, and so
            // is a transient database error inside it. A transient error on a write may hide a write the
            // server did apply: resuming re-reads WeeksDone from the row, so that block is simply not
            // read again — and the third strike of such an error is deferred to the next claim, which
            // sees that progress and drops the count instead of failing a run that just moved on.
            if (!state.InBlockLoop)
            {
                return await AbandonAsync(service, claim, state, "an error before the block loop", ex, deferFailure: false, stoppingToken);
            }

            if (IsTransient(ex))
            {
                return await AbandonAsync(service, claim, state, "a transient database error", ex, deferFailure: true, stoppingToken);
            }

            logger.LogError(ex, "Chat-log backfill run {RunId} ({Channel}) failed with an unexpected error.", claim.RunId, claim.ChannelName);
            return await FailOrAbandonAsync(service, claim, state, WorkerErrorCode, null, stoppingToken);
        }
        finally
        {
            // Every way out stops the monitor and waits for it before its scope (and the token source)
            // goes away: the monitor never outlives its run.
            await runCancellation.CancelAsync();
            await monitor;
            if (monitorScope is { } scope)
            {
                await scope.DisposeAsync();
            }
        }
    }

    private async Task<RunOutcome> RunBlocksAsync(
        IChatLogBackfillService service,
        IChatLogArchiveClient client,
        ChatLogBackfillClaim claim,
        IReadOnlyList<ChatLogBackfillBlock> plan,
        IReadOnlyDictionary<string, string> nameToId,
        IReadOnlyDictionary<string, DateOnly?> addedToSetDay,
        RunState state,
        CancellationToken runToken,
        CancellationToken stoppingToken)
    {
        var maxBytes = options.MaxBlockMegabytes * BytesPerMegabyte;
        var index = claim.WeeksDone;
        while (index < plan.Count)
        {
            var block = plan[index];
            var isLastBlock = index + 1 == plan.Count;

            // Before every request, first attempt or retry: the provider cooldown (D31), the spacing
            // (D7), the loop lock (D46), and the row itself (D39) — a non-running row stops the run
            // without a request.
            await WaitForCooldownAsync(service, runToken, stoppingToken);
            await WaitForSpacingAsync(runToken);
            if (!await service.HoldsLoopLockAsync(stoppingToken))
            {
                return await AbandonAsync(service, claim, state, "the loop lock was lost before a request", null, deferFailure: false, stoppingToken);
            }

            if (!await service.IsRunActiveAsync(claim.RunId, stoppingToken))
            {
                LogStopped(claim);
                return RunOutcome.Finished;
            }

            var counter = new ChatLogBackfillBlockCounter(block, nameToId, addedToSetDay, botChatterDetector.IsBot);
            _lastRequestStartedUtc = UtcNow();
            var result = await client.ReadRangeAsync(
                claim.TwitchChannelId,
                block.FromUtc,
                block.ToUtcExclusive,
                maxBytes,
                message =>
                {
                    // Objection gate (GDPR Art. 21) before anything counts — the harness's placement
                    // (HarnessRunner) and the live path's. The client's MessageCount still includes
                    // the message (D47: a count before the gate, no identity).
                    if (!excludedChatterFilter.IsExcluded(message.UserId))
                    {
                        counter.Count(message);
                    }

                    return ValueTask.CompletedTask;
                },
                runToken);
            state.UnbookedBytes = result.BytesReceived;

            logger.LogInformation(
                "Chat-log backfill run {RunId} ({Channel}): block {Block}/{Blocks} {From:yyyy-MM-dd}..{To:yyyy-MM-dd} {Status}, {Lines} lines, {Bytes} bytes.",
                claim.RunId, claim.ChannelName, index + 1, plan.Count, block.From, block.ToExclusive, result.Status, result.MessageCount,
                result.BytesReceived);

            // Every write after a read happens only under the lock (D46): a read can last up to the body
            // timeout, and another loop that took the lock meanwhile may have reset and re-claimed this
            // very run — a commit, a pause, an attempt or a failure written now would land on its run.
            // The lock is probed, never retaken: a connection that died during the read means the lock
            // was free for a while, and retaking it now would hide another loop's turn. Not held:
            // nothing is written for this read, not even a 429's cooldown (the holder gets its own 429 if
            // the archive still means it), and the lock stays lost until the next claim.
            if (result.Status != ChatLogDayStatus.Cancelled && !await service.HoldsLoopLockAsync(stoppingToken))
            {
                return await AbandonAsync(service, claim, state, "the loop lock was lost during a read", null, deferFailure: false, stoppingToken);
            }

            switch (result.Status)
            {
                case ChatLogDayStatus.Complete:
                case ChatLogDayStatus.NoLogDay:
                    // A 404 is a block without data (D12): committed like any other, replacing and covering its days.
                    var outcome = await service.ReplaceBlockAsync(
                        claim.RunId, block.From, block.ToExclusive, counter.Aggregates(), result.BytesReceived, result.MessageCount,
                        isLastBlock, stoppingToken);
                    if (outcome is ChatLogBackfillBlockResult.Committed committed)
                    {
                        state.UnbookedBytes = 0;
                        state.WeeksDone++;
                        _abandonsInARow.Remove(claim.RunId);
                        await redisPublisher.PublishChannelEventAsync(logger, LiveEvents.UsageFlushed, claim.ChannelName, stoppingToken);
                        await PublishProgressAsync(claim.ChannelName, stoppingToken);
                        if (committed.Transition.Status == ChatLogBackfillRunStatus.Completed)
                        {
                            logger.LogInformation(
                                "Chat-log backfill run {RunId} ({Channel}) completed: {Blocks} blocks.", claim.RunId, claim.ChannelName, plan.Count);
                            return RunOutcome.Finished;
                        }

                        if (committed.Transition.Status != ChatLogBackfillRunStatus.Running)
                        {
                            LogStopped(claim);
                            return RunOutcome.Finished;
                        }

                        index++;
                        continue;
                    }

                    return await HandleUncommittedBlockAsync(service, claim, state, outcome, stoppingToken);

                case ChatLogDayStatus.RateLimited:
                    // A 429 books no bytes (D47), also when the pause write below fails.
                    state.UnbookedBytes = 0;

                    // D38/D44: the delay and the rate_limited threshold are computed in the database
                    // from the row's own PauseCount; the pause is spent outside this method.
                    var paused = await service.PauseAsync(claim.RunId, result.RetryAfter, UtcNow(), stoppingToken);
                    await PublishProgressAsync(claim.ChannelName, stoppingToken);
                    if (paused.Status == ChatLogBackfillRunStatus.Paused)
                    {
                        logger.LogWarning(
                            "Chat-log backfill run {RunId} ({Channel}) paused by a 429 until {PausedUntil:O} (pause {PauseCount} on this block, Retry-After {RetryAfter}).",
                            claim.RunId, claim.ChannelName, paused.PausedUntilUtc, paused.PauseCount, result.RetryAfter?.ToString() ?? "none");
                        return RunOutcome.Paused;
                    }

                    if (paused.Status == ChatLogBackfillRunStatus.Failed)
                    {
                        logger.LogError(
                            "Chat-log backfill run {RunId} ({Channel}) failed: {ErrorCode} after {PauseCount} pauses on one block.",
                            claim.RunId, claim.ChannelName, ChatLogBackfillService.RateLimitedErrorCode, paused.PauseCount);
                    }
                    else
                    {
                        LogStopped(claim);
                    }

                    return RunOutcome.Finished;

                case ChatLogDayStatus.TransportFailure:
                case ChatLogDayStatus.BodyTimeout:
                    var attempt = await service.RecordBlockAttemptAsync(claim.RunId, result.BytesReceived, stoppingToken);
                    state.UnbookedBytes = 0;
                    if (attempt.Status != ChatLogBackfillRunStatus.Running)
                    {
                        LogStopped(claim);
                        return RunOutcome.Finished;
                    }

                    if (!ChatLogBackfillRetryPolicy.ShouldRetryTransport(attempt.BlockAttempts, options.TransportRetries))
                    {
                        // This attempt's bytes are already booked by RecordBlockAttemptAsync.
                        return await FailOrAbandonAsync(service, claim, state, TransportFailureErrorCode, result.HttpStatusCode, stoppingToken);
                    }

                    var delay = ChatLogBackfillRetryPolicy.TransportRetryDelay(
                        attempt.BlockAttempts, TimeSpan.FromSeconds(options.MaxRetryAfterSeconds));
                    logger.LogWarning(
                        "Chat-log backfill run {RunId} ({Channel}): attempt {Attempt} of {Attempts} on block {Block}/{Blocks} failed ({Status}, HTTP {HttpStatus}); retrying in {Delay}.",
                        claim.RunId, claim.ChannelName, attempt.BlockAttempts, options.TransportRetries, index + 1, plan.Count, result.Status,
                        result.HttpStatusCode?.ToString() ?? "none", delay);
                    await Task.Delay(delay, timeProvider, runToken);
                    continue;

                case ChatLogDayStatus.MalformedResponse:
                case ChatLogDayStatus.LineTooLong:
                    return await FailOrAbandonAsync(service, claim, state, MalformedResponseErrorCode, result.HttpStatusCode, stoppingToken);

                case ChatLogDayStatus.ByteCapExceeded:
                    return await FailOrAbandonAsync(service, claim, state, BlockTooLargeErrorCode, result.HttpStatusCode, stoppingToken);

                case ChatLogDayStatus.Cancelled:
                    // Either the process is stopping (the row stays running for the next boot's reset) or
                    // the monitor aborted the read; nothing of the block is committed in both cases.
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        LogStopped(claim);
                    }

                    return RunOutcome.Finished;

                default:
                    logger.LogError("Chat-log backfill run {RunId}: unexpected archive status {Status}.", claim.RunId, result.Status);
                    return await FailOrAbandonAsync(service, claim, state, WorkerErrorCode, result.HttpStatusCode, stoppingToken);
            }
        }

        return RunOutcome.Finished;
    }

    // D48: RunNotActive, LiveRowConflict and ChannelGone roll the block back.
    private async Task<RunOutcome> HandleUncommittedBlockAsync(
        IChatLogBackfillService service, ChatLogBackfillClaim claim, RunState state, ChatLogBackfillBlockResult outcome, CancellationToken stoppingToken)
    {
        switch (outcome)
        {
            case ChatLogBackfillBlockResult.LiveRowConflict:
                // The bytes were received and the run is still running: booked with the failure (D47,
                // operator decision 2026-10-09).
                return await FailOrAbandonAsync(service, claim, state, LiveRowConflictErrorCode, null, stoppingToken);

            case ChatLogBackfillBlockResult.RunNotActive:
                // RunNotActive also answers a block that is not the run's next planned one, while the
                // row may still be running (§3 step 4) — so the status is read again instead of
                // assuming a cancel. Still running: abandon and resume from the persisted WeeksDone
                // (operator decision 2026-10-09); three in a row fail the run.
                if (await service.IsRunActiveAsync(claim.RunId, stoppingToken))
                {
                    return await AbandonAsync(
                        service, claim, state, "the service did not accept the block as the run's next one", null, deferFailure: false, stoppingToken);
                }

                LogStopped(claim);
                return RunOutcome.Finished;

            default:
                // ChannelGone: the purge's cascade took the run along; nothing left to record.
                logger.LogInformation("Chat-log backfill run {RunId} stopped: its channel is gone (purged).", claim.RunId);
                return RunOutcome.Finished;
        }
    }

    /// <summary>
    /// One abandon-and-resume event: nothing is written, the loop takes the lost-lock path and the run
    /// resumes at the next claim. Counted per run and per <c>WeeksDone</c>: the third event in a row at
    /// the same progress fails the run with <c>worker_error</c>, booking the read this loop could not
    /// commit (<see cref="RunState.UnbookedBytes"/>) — but only while this loop still holds the lock,
    /// and not for a transient write error (<paramref name="deferFailure"/>), whose write may have been
    /// applied. Those wait for the next claim, which re-reads <c>WeeksDone</c> and records the failure
    /// with 0 bytes before any request — or drops the count when the run has moved on.
    /// </summary>
    private async Task<RunOutcome> AbandonAsync(
        IChatLogBackfillService service, ChatLogBackfillClaim claim, RunState state, string reason, Exception? exception, bool deferFailure,
        CancellationToken stoppingToken)
    {
        var previous = _abandonsInARow.GetValueOrDefault(claim.RunId);
        var abandons = previous.Count > 0 && previous.WeeksDone == state.WeeksDone ? previous.Count + 1 : 1;
        _abandonsInARow[claim.RunId] = (abandons, state.WeeksDone);

        if (abandons >= MaxConsecutiveAbandons && !deferFailure && await HoldsLockAsync(service, stoppingToken))
        {
            logger.LogError(
                exception, "Chat-log backfill run {RunId} ({Channel}) abandoned {Abandons} times in a row (last: {Reason}).",
                claim.RunId, claim.ChannelName, abandons, reason);
            if (await TryFailRunAsync(service, claim, WorkerErrorCode, null, state.UnbookedBytes, stoppingToken))
            {
                return RunOutcome.Finished;
            }
        }
        else
        {
            logger.LogWarning(
                exception, "Chat-log backfill run {RunId} ({Channel}) abandoned ({Reason}, {Abandons} of {Max}); it resumes from its persisted progress at the next claim.",
                claim.RunId, claim.ChannelName, reason, abandons, MaxConsecutiveAbandons);
        }

        return RunOutcome.Abandoned;
    }

    // Every failure is a write after a read, so it is lock-guarded like any other (D46); one that is not
    // held, or cannot be recorded, is itself an abandon event: the row stays running and is resumed.
    private async Task<RunOutcome> FailOrAbandonAsync(
        IChatLogBackfillService service, ChatLogBackfillClaim claim, RunState state, string errorCode, int? httpStatus, CancellationToken stoppingToken)
    {
        if (!await HoldsLockAsync(service, stoppingToken))
        {
            return await AbandonAsync(
                service, claim, state, $"the loop lock was lost before recording {errorCode}", null, deferFailure: false, stoppingToken);
        }

        return await TryFailRunAsync(service, claim, errorCode, httpStatus, state.UnbookedBytes, stoppingToken)
            ? RunOutcome.Finished
            : await AbandonAsync(service, claim, state, $"recording {errorCode} failed", null, deferFailure: false, stoppingToken);
    }

    // D39: polls the row every CancelPollSeconds on its own service instance and cancels the run's
    // token as soon as the row is no longer running — independent of the serial Redis command queue.
    private async Task MonitorAsync(IChatLogBackfillService monitorService, long runId, CancellationTokenSource runCancellation)
    {
        var token = runCancellation.Token;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.CancelPollSeconds), timeProvider);
            while (await timer.WaitForNextTickAsync(token))
            {
                bool active;
                try
                {
                    active = await monitorService.IsRunActiveAsync(runId, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A database hiccup is not a cancel; the pre-request check and the counter writes
                    // still stop the run if the row really changed.
                    logger.LogWarning(ex, "Chat-log backfill status monitor could not read run {RunId}; the next poll retries.", runId);
                    continue;
                }

                if (!active)
                {
                    await runCancellation.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended (or the process is stopping); the monitor ends with it.
        }
    }

    // D7: a request starts no earlier than the effective spacing after the previous one started — never a
    // millisecond earlier. The effective spacing is the larger of RequestDelaySeconds and the archive
    // client's own ChatLogArchive:RequestDelay (spec 8): the client paces only within its run, and each
    // run gets a fresh client, so across runs only this wait keeps the larger delay. A timer may fire up
    // to a tick early against the wall clock (and a delay is truncated to whole milliseconds), so the
    // wait is rounded up and the clock checked again after it.
    private async Task WaitForSpacingAsync(CancellationToken runToken)
    {
        var workerSpacing = TimeSpan.FromSeconds(options.RequestDelaySeconds);
        var spacing = workerSpacing > archiveOptions.RequestDelay ? workerSpacing : archiveOptions.RequestDelay;
        while (ChatLogBackfillRetryPolicy.SpacingWait(_lastRequestStartedUtc, spacing, UtcNow()) is var wait && wait > TimeSpan.Zero)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds)), timeProvider, runToken);
        }
    }

    private async Task WaitForCooldownAsync(IChatLogBackfillService service, CancellationToken runToken, CancellationToken stoppingToken)
    {
        while (true)
        {
            var wait = ChatLogBackfillRetryPolicy.CooldownWait(await service.GetCooldownUntilAsync(stoppingToken), UtcNow());
            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            logger.LogInformation("Chat-log backfill: waiting {Wait} for the archive's cooldown.", wait);
            await Task.Delay(wait, timeProvider, runToken);
        }
    }

    private async Task<bool> TryFailRunAsync(
        IChatLogBackfillService service, ChatLogBackfillClaim claim, string errorCode, int? httpStatus, long bytes, CancellationToken stoppingToken)
    {
        try
        {
            await service.FailAsync(claim.RunId, errorCode, httpStatus, bytes, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Chat-log backfill run {RunId}: recording the failure {ErrorCode} failed.", claim.RunId, errorCode);
            return false;
        }

        logger.LogError(
            "Chat-log backfill run {RunId} ({Channel}) failed: {ErrorCode} (HTTP {HttpStatus}).",
            claim.RunId, claim.ChannelName, errorCode, httpStatus?.ToString() ?? "none");
        await PublishProgressAsync(claim.ChannelName, stoppingToken);
        return true;
    }

    private async Task<bool> HoldsLockAsync(IChatLogBackfillService service, CancellationToken stoppingToken)
    {
        try
        {
            // A probe, never an acquisition: see the post-read check in RunBlocksAsync.
            return await service.HoldsLoopLockAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Chat-log backfill: the loop lock could not be checked.");
            return false;
        }
    }

    private Task PublishProgressAsync(string channelName, CancellationToken cancellationToken) =>
        redisPublisher.PublishChannelEventAsync(logger, LiveEvents.BackfillProgress, channelName, cancellationToken);

    private void LogStopped(ChatLogBackfillClaim claim) =>
        logger.LogInformation(
            "Chat-log backfill run {RunId} ({Channel}) stopped: it is no longer running (cancelled, left or purged).",
            claim.RunId, claim.ChannelName);

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    // A database error a retry may clear: a timeout, or a provider exception that says so itself.
    private static bool IsTransient(Exception exception)
    {
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is TimeoutException or DbException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }

    // What the current attempt has not booked yet, for a failure recorded from a catch.
    private sealed class RunState
    {
        public bool InBlockLoop { get; set; }

        /// <summary>The run's progress as this loop knows it: the claim's value plus its own commits.</summary>
        public int WeeksDone { get; set; }

        public long UnbookedBytes { get; set; }
    }

    private enum RunOutcome
    {
        /// <summary>The run completed, failed, or is no longer running.</summary>
        Finished,

        /// <summary>A 429 paused it; the loop re-claims it when due (D44).</summary>
        Paused,

        /// <summary>Nothing written; the lock is suspect and the run resumes at the next claim.</summary>
        Abandoned,
    }
}
