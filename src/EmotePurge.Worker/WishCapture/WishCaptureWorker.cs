using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.WishCapture;

/// <summary>
/// The worker side of the K6 chat capture (plan #486 2.3 and 2.7): one hosted service, two loops. Concrete class, only
/// registered through <c>AddHostedService</c> (rule 5); registered last, so it starts last and stops first.
/// <para>
/// <b>Whether it captures</b> is decided by <see cref="WishOptions.CaptureActive"/> of this process alone (Sol-P1-2): one
/// information line at start says which, and the worker health carries the same value. The api's switch appears
/// nowhere here.
/// </para>
/// <para>
/// <b>The tick</b> runs always, whatever the switches say (E26, AK66): at start and then every minute one housekeeping
/// pass (the pending expiry on the first tick and every fifteenth). With the capture effective it also reads the gap set,
/// starts the <see cref="CaptureGapTracker"/> once with the life stamp read before the first write, samples, stores
/// closed gaps and writes the life stamp unless a connection gap is open. After a rejoin round it samples once at once.
/// Each step takes the service from a scope of its own; a failing step warns at most once an hour per step (exception
/// type and SQLSTATE only), so a database without the migration does not warn every minute.
/// </para>
/// <para>
/// <b>The queue</b> is drained only with the capture effective: one consumer, strictly in order, each observation through
/// <see cref="IWishCaptureService.ProcessAsync"/> in a scope of its own. A failure is retried after 1, 5 and 15 seconds,
/// then the observation is dropped and counted.
/// </para>
/// <para>
/// <b>Stopping</b> (Sol-R2-P2-8) shares the host's 30 seconds with every other service, above all the final usage flush:
/// close the queue, cancel the loops, drain what is left with a budget of its own of <see cref="StopDrainBudget"/> (one
/// attempt per observation, the budget cancels a running statement too), count and log the rest, then take the counters
/// once more so the refusals after the close are not lost.
/// </para>
/// </summary>
public sealed class WishCaptureWorker(
    ILogger<WishCaptureWorker> logger,
    IServiceScopeFactory scopeFactory,
    RewardObservationQueue queue,
    CaptureGapTracker gapTracker,
    ITwitchChatManager chatManager,
    WorkerStats stats,
    WishOptions options,
    TimeProvider timeProvider) : BackgroundService
{
    /// <summary>The budget of the drain at shutdown, out of the host's shutdown timeout.</summary>
    internal static readonly TimeSpan StopDrainBudget = TimeSpan.FromSeconds(5);

    private const int PendingExpiryEveryTicks = 15;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WarningInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan[] RetryPauses = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    private readonly Dictionary<string, DateTimeOffset> _lastWarningAt = new(StringComparer.Ordinal);
    private readonly List<WishCaptureGapRecord> _unwrittenGaps = [];

    // Tick loop only: the set of the last successful read, and whether the tracker has started.
    private IReadOnlyList<WishCaptureGapChannel> _gapSet = [];
    private bool _gapSetKnown;
    private bool _trackerStarted;

    // The observation the consumer holds; the drain at shutdown takes it first.
    private RewardObservation? _inFlight;

    private long _created;
    private long _candidates;
    private long _duplicates;
    private long _requesterLimit;
    private long _unbound;
    private long _failed;
    private long _channelLimit;
    private DateTimeOffset? _lastChannelLimitWarningAt;

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // (1) From here on the chat handler's TryEnqueue refuses and counts ("rejected at shutdown").
        queue.Complete();

        // (2) Cancels both loops, including a running statement and a retry pause.
        await base.StopAsync(cancellationToken);

        // (3) and (4). Only once the loops have ended: the queue has a single reader.
        if (options.CaptureActive && ExecuteTask is null or { IsCompleted: true })
        {
            await DrainAtShutdownAsync(cancellationToken);
        }

        // (5) The counters once more, after the close.
        LogCounters(atShutdown: true);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.CaptureActive)
        {
            logger.LogInformation("Wish capture is enabled.");
        }
        else
        {
            logger.LogInformation(
                "Wish capture is disabled (Wishes:Enabled={Enabled}, Wishes:Capture:Enabled={CaptureEnabled}); housekeeping keeps running.",
                options.Enabled,
                options.Capture.Enabled);
        }

        try
        {
            if (options.CaptureActive)
            {
                await Task.WhenAll(RunTicksAsync(stoppingToken), ConsumeAsync(stoppingToken));
            }
            else
            {
                await RunTicksAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Regular shutdown.
        }
    }

    private async Task RunTicksAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval, timeProvider);
        var tick = 0L;
        await TickAsync(tick++, ct);

        if (!options.CaptureActive)
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await TickAsync(tick++, ct);
            }

            return;
        }

        // PeriodicTimer allows one pending wait at a time, so a wait that loses to a sample request is kept.
        Task<bool>? nextTick = null;
        while (true)
        {
            nextTick ??= timer.WaitForNextTickAsync(ct).AsTask();
            if (await Task.WhenAny(nextTick, gapTracker.SampleRequested) == nextTick)
            {
                if (!await nextTick)
                {
                    return;
                }

                nextTick = null;
                await TickAsync(tick++, ct);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                gapTracker.AcknowledgeSampleRequest();
                await SampleGapsAsync(ct);
            }
        }
    }

    private async Task TickAsync(long tick, CancellationToken ct)
    {
        await RunHousekeepingAsync(tick % PendingExpiryEveryTicks == 0, ct);
        if (options.CaptureActive)
        {
            await RunCaptureTickAsync(ct);
        }

        LogCounters(atShutdown: false);
    }

    private async Task RunHousekeepingAsync(bool includePendingExpiry, CancellationToken ct)
    {
        var result = await RunStepAsync(
            "housekeeping",
            service => service.RunHousekeepingAsync(includePendingExpiry, ct),
            ct);
        if (result is null)
        {
            return;
        }

        if (result.WishesExpired > 0)
        {
            logger.LogInformation(
                "Wish capture housekeeping expired {Wishes} open wishes in {Channels} channels.",
                result.WishesExpired, result.ChannelsExpired);
        }

        if (result.ChannelsFailed > 0)
        {
            WarnThrottled("pending expiry", () => logger.LogWarning(
                "Wish capture housekeeping failed to expire the open wishes of {Channels} channels.",
                result.ChannelsFailed));
        }
    }

    private async Task RunCaptureTickAsync(CancellationToken ct)
    {
        var set = await RunStepAsync("gap channel set", service => service.ListGapChannelsAsync(ct), ct);
        if (set is not null)
        {
            _gapSet = set;
            _gapSetKnown = true;
        }

        if (!_trackerStarted)
        {
            // The old stamp is read before the first write, and only once the set is known: a start without the set
            // would open no restart gap at all.
            if (!_gapSetKnown)
            {
                return;
            }

            var stamp = await RunStepAsync("life stamp read", async service => new StampRead(await service.ReadLastAliveAsync(ct)), ct);
            if (stamp is null)
            {
                return;
            }

            gapTracker.Start(stamp.Value, _gapSet);
            _trackerStarted = true;
        }

        await SampleGapsAsync(ct);

        // "Last surely captured" (plan 2.7, W6): not while a connection gap is open.
        if (!gapTracker.IsConnectionGapOpen)
        {
            var now = UtcNow();
            await RunStepAsync<object>("life stamp write", async service =>
            {
                await service.WriteLastAliveAsync(now, ct);
                return true;
            }, ct);
        }
    }

    private async Task SampleGapsAsync(CancellationToken ct)
    {
        if (!_trackerStarted)
        {
            return;
        }

        try
        {
            // Inside the try like everything else in the tick: an escaping exception would stop the whole host.
            _unwrittenGaps.AddRange(gapTracker.Sample(_gapSet, chatManager.GetRoster(), UtcNow()));
            if (_unwrittenGaps.Count == 0)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IWishCaptureService>();
            var written = await service.RecordGapsAsync([.. _unwrittenGaps], ct);
            _unwrittenGaps.Clear();
            if (written > 0)
            {
                logger.LogInformation("Wish capture recorded {Count} capture gaps.", written);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            // A list the service refuses as a whole would be refused on every tick.
            _unwrittenGaps.Clear();
            WarnThrottled("gap write", () => logger.LogWarning("Wish capture dropped capture gaps the service refused: {Failure}.", WishExpiryWorker.DescribeFailure(ex)));
        }
        catch (Exception ex)
        {
            // Nothing was written; the same list goes again on the next sample.
            WarnThrottled("gap write", () => logger.LogWarning("Wish capture could not store capture gaps: {Failure}.", WishExpiryWorker.DescribeFailure(ex)));
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        while (await queue.WaitToReadAsync(ct))
        {
            while (queue.TryDequeue(out var observation))
            {
                _inFlight = observation;
                await ProcessWithRetriesAsync(observation, ct);
                _inFlight = null;
            }
        }
    }

    private async Task ProcessWithRetriesAsync(RewardObservation observation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Count(await ProcessOnceAsync(observation, ct));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= RetryPauses.Length)
                {
                    Interlocked.Increment(ref _failed);
                    WarnThrottled("processing", () => logger.LogWarning(
                        "Wish capture dropped an observation after {Attempts} failed attempts: {Failure}.",
                        attempt + 1, WishExpiryWorker.DescribeFailure(ex)));
                    return;
                }
            }

            await Task.Delay(RetryPauses[attempt], timeProvider, ct);
        }
    }

    private async Task<WishCaptureOutcome> ProcessOnceAsync(RewardObservation observation, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWishCaptureService>();
        return await service.ProcessAsync(observation, ct);
    }

    private async Task DrainAtShutdownAsync(CancellationToken hostToken)
    {
        using var budget = new CancellationTokenSource(StopDrainBudget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken, budget.Token);
        try
        {
            if (_inFlight is { } current)
            {
                await ProcessAtShutdownAsync(current, linked.Token);
            }

            while (queue.TryDequeue(out var observation))
            {
                _inFlight = observation;
                await ProcessAtShutdownAsync(observation, linked.Token);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // The budget is spent; what is left is counted below.
        }

        var left = _inFlight is null ? 0 : 1;
        _inFlight = null;
        while (queue.TryDequeue(out _))
        {
            left++;
        }

        if (left > 0)
        {
            logger.LogWarning("Wish capture stopped with {Count} observations not processed; dropped.", left);
        }
    }

    // One attempt, no retry pause. A failure is counted; cancellation ends the drain.
    private async Task ProcessAtShutdownAsync(RewardObservation observation, CancellationToken ct)
    {
        try
        {
            Count(await ProcessOnceAsync(observation, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _failed);
        }

        _inFlight = null;
    }

    // Exception type and SQLSTATE only; null when the step failed.
    private async Task<T?> RunStepAsync<T>(string step, Func<IWishCaptureService, Task<T>> action, CancellationToken ct)
        where T : class
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<IWishCaptureService>());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WarnThrottled(step, () => logger.LogWarning(
                "Wish capture step '{Step}' failed: {Failure}.", step, WishExpiryWorker.DescribeFailure(ex)));
            return null;
        }
    }

    private void Count(WishCaptureOutcome outcome)
    {
        switch (outcome)
        {
            case WishCaptureOutcome.WishCreated:
                Interlocked.Increment(ref _created);
                break;
            case WishCaptureOutcome.CandidateStored:
                Interlocked.Increment(ref _candidates);
                break;
            case WishCaptureOutcome.Duplicate:
                Interlocked.Increment(ref _duplicates);
                break;
            case WishCaptureOutcome.RequesterLimit:
                Interlocked.Increment(ref _requesterLimit);
                break;
            case WishCaptureOutcome.ChannelLimit:
                Interlocked.Increment(ref _channelLimit);
                break;
            case WishCaptureOutcome.ChannelUnbound:
                Interlocked.Increment(ref _unbound);
                break;
            default:
                // Discarded: foreign rewards and the like, the normal case; not worth a number.
                break;
        }
    }

    // Numbers only (AK53, AK64). The minute line appears only when one of them is not zero; the per-channel limit has
    // its own warning, at most once an hour, with the count since the last one.
    private void LogCounters(bool atShutdown)
    {
        var reader = stats.TakeRewardCaptureCounters();
        var created = Interlocked.Exchange(ref _created, 0);
        var candidates = Interlocked.Exchange(ref _candidates, 0);
        var duplicates = Interlocked.Exchange(ref _duplicates, 0);
        var requesterLimit = Interlocked.Exchange(ref _requesterLimit, 0);
        var unbound = Interlocked.Exchange(ref _unbound, 0);
        var failed = Interlocked.Exchange(ref _failed, 0);

        if (!reader.IsZero || created + candidates + duplicates + requesterLimit + unbound + failed > 0)
        {
            logger.LogInformation(
                "Wish capture {Window}: {Created} created, {Candidates} candidates, {Duplicates} duplicates, {RequesterLimit} over the per-viewer limit, {Unbound} unbound channel, {Overflowed} overflowed, {RejectedAtShutdown} rejected at shutdown, {Spliced} spliced, {Faults} capture faults, {EmptySnapshot} at an empty emote snapshot, {Failed} failed.",
                atShutdown ? "at shutdown" : "in the last minute",
                created, candidates, duplicates, requesterLimit, unbound,
                reader.Overflowed, reader.RejectedAtShutdown, reader.SplicedRedemptions, reader.CaptureFaults,
                reader.ObservedAtEmptySnapshot, failed);
        }

        var now = timeProvider.GetUtcNow();
        if (Interlocked.Read(ref _channelLimit) > 0
            && (atShutdown || _lastChannelLimitWarningAt is not { } last || now - last >= WarningInterval))
        {
            _lastChannelLimitWarningAt = now;
            logger.LogWarning(
                "Wish capture refused {Count} observations at the per-channel limit of {Limit} open wishes since the last warning.",
                Interlocked.Exchange(ref _channelLimit, 0), WishLimits.MaxPendingPerChannel);
        }
    }

    private void WarnThrottled(string step, Action warn)
    {
        var now = timeProvider.GetUtcNow();
        lock (_lastWarningAt)
        {
            if (_lastWarningAt.TryGetValue(step, out var last) && now - last < WarningInterval)
            {
                return;
            }

            _lastWarningAt[step] = now;
        }

        warn();
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    // A class wrapper, so a successful read of "no stamp" (null) is told apart from a failed read.
    private sealed record StampRead(DateTime? Value);
}
