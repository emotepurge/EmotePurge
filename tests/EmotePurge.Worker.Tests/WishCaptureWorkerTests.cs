using EmotePurge.Core.Chat;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Worker.WishCapture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Container-free (plan #486 T11): the capture service is a fake with the contract of T5, the chat manager a substitute
// for its roster. The service is the same path as in the integration tests of T5 — there is no second decision path in
// the worker — so these tests cover the switch, the clock, the retries, the gaps and the stop budget.
public sealed class WishCaptureWorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset StartAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly WishCaptureGapChannel Alpha = new("chan-a", "1001", "alpha");

    [Fact]
    public async Task Disabled_LogsTheDisabledLine_RunsHousekeeping_AndNeverTouchesTheQueueOrTheGaps()
    {
        await using var rig = new Rig(captureEnabled: false);
        rig.Queue.TryEnqueue(Observation(1));

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.HousekeepingCalls.Count >= 1, "first housekeeping pass");
        await rig.DriveAsync(TimeSpan.FromMinutes(2), () => rig.Service.HousekeepingCalls.Count >= 3);
        await rig.StopAsync();

        var line = Assert.Single(rig.Logger.Entries, e => e.Message.StartsWith("Wish capture is", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("Wish capture is disabled (Wishes:Enabled=True, Wishes:Capture:Enabled=False); housekeeping keeps running.", line.Message);
        Assert.Equal(0, rig.Service.ProcessCalls);
        Assert.DoesNotContain(rig.Service.Calls, c => c is "list" or "read" or "write" or "gaps");
    }

    [Fact]
    public async Task Enabled_LogsTheEnabledLine_AndReadsTheStampBeforeTheFirstWrite()
    {
        await using var rig = new Rig();

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.Calls.Contains("write"), "first life stamp write");
        await rig.StopAsync();

        Assert.Single(rig.Logger.Entries, e => e.Level == LogLevel.Information && e.Message == "Wish capture is enabled.");
        var calls = rig.Service.Calls.ToList();
        Assert.True(calls.IndexOf("read") < calls.IndexOf("write"), string.Join(",", calls));
        Assert.Single(calls, c => c == "read");
    }

    [Fact]
    public async Task ThePendingExpiry_RunsOnTheFirstTickAndOnEveryFifteenth()
    {
        await using var rig = new Rig(captureEnabled: false);

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.HousekeepingCalls.Count >= 1, "first housekeeping pass");
        for (var minute = 1; minute <= 16; minute++)
        {
            await rig.DriveAsync(TimeSpan.FromMinutes(1), () => rig.Service.HousekeepingCalls.Count > minute);
        }

        await rig.StopAsync();

        var flags = rig.Service.HousekeepingCalls.Take(17).ToList();
        Assert.Equal(17, flags.Count);
        Assert.Equal([0, 15], flags.Select((flag, index) => (flag, index)).Where(p => p.flag).Select(p => p.index));
    }

    [Fact]
    public async Task TheLifeStamp_IsNotWrittenWhileAConnectionGapIsOpen()
    {
        await using var rig = new Rig();
        rig.Tracker.ConnectionLost(StartAt.UtcDateTime);

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.Calls.Contains("read"), "life stamp read");
        await rig.DriveAsync(TimeSpan.FromMinutes(2), () => rig.Service.HousekeepingCalls.Count >= 3);
        Assert.DoesNotContain("write", rig.Service.Calls);

        rig.Tracker.RejoinRoundCompleted();
        await rig.DriveAsync(TimeSpan.FromMinutes(1), () => rig.Service.Calls.Contains("write"));
        await rig.StopAsync();
    }

    [Fact]
    public async Task ARestartGap_ReachesFromTheOldStampToTheConfirmedJoin()
    {
        await using var rig = new Rig();
        var stamp = StartAt.UtcDateTime.AddMinutes(-10);
        rig.Service.LastAlive = stamp;
        rig.Service.GapSet = [Alpha];
        rig.Roster = [];

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.Calls.Contains("write"), "first tick");
        Assert.Empty(rig.Service.RecordedGaps);

        rig.Roster = [new TwitchRosterEntry("alpha", true, null)];
        await rig.DriveAsync(TimeSpan.FromMinutes(1), () => rig.Service.RecordedGaps.Count >= 1);
        await rig.StopAsync();

        var gap = Assert.Single(Assert.Single(rig.Service.RecordedGaps));
        Assert.Equal("chan-a", gap.ChannelId);
        Assert.Equal(stamp, gap.FromUtc);
        Assert.Equal(WishCaptureGapReason.WorkerRestart, gap.Reason);
        Assert.InRange(gap.ToUtc, StartAt.UtcDateTime.AddMinutes(1), StartAt.UtcDateTime.AddMinutes(1).AddSeconds(10));
    }

    [Fact]
    public async Task TheEndOfARejoinRound_SamplesAtOnce_WithoutWaitingForTheMinute()
    {
        await using var rig = new Rig();
        rig.Service.GapSet = [Alpha];
        rig.Roster = [new TwitchRosterEntry("alpha", true, null)];

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.Calls.Contains("write"), "first tick");
        rig.Tracker.ConnectionLost(StartAt.UtcDateTime.AddMinutes(-5));
        rig.Tracker.RejoinRoundCompleted();
        await rig.WaitUntilAsync(() => rig.Service.RecordedGaps.Count >= 1, "immediate sample");
        await rig.StopAsync();

        var gap = Assert.Single(Assert.Single(rig.Service.RecordedGaps));
        Assert.Equal(WishCaptureGapReason.Disconnect, gap.Reason);
        Assert.Equal(StartAt.UtcDateTime, gap.ToUtc);
    }

    [Fact]
    public async Task AFailedGapWrite_SendsTheSameListAgainOnTheNextTick()
    {
        await using var rig = new Rig();
        rig.Service.GapSet = [Alpha];
        rig.Service.LastAlive = StartAt.UtcDateTime.AddMinutes(-10);
        rig.Roster = [new TwitchRosterEntry("alpha", true, null)];
        rig.Service.RecordGapsFailures = 1;

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.RecordedGaps.Count >= 1, "first gap write");
        await rig.DriveAsync(TimeSpan.FromMinutes(1), () => rig.Service.RecordedGaps.Count >= 2);
        await rig.StopAsync();

        Assert.Equal(rig.Service.RecordedGaps[0], rig.Service.RecordedGaps[1]);
        Assert.Single(rig.Logger.Entries, e => e.Message.Contains("recorded 1 capture gaps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreadableGapSet_KeepsTheOldSet_AndWarnsAtMostOnceAnHour()
    {
        await using var rig = new Rig();
        rig.Service.GapSet = [Alpha];

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.Calls.Contains("write"), "first tick");
        rig.Service.GapSetFailure = new InvalidOperationException("secret-row-data");
        rig.Roster = [];
        await rig.DriveAsync(TimeSpan.FromMinutes(3), () => rig.Service.HousekeepingCalls.Count >= 4);

        // The old set still holds alpha: an unconfirmed alpha opens not_joined, closed at its join.
        rig.Roster = [new TwitchRosterEntry("alpha", true, null)];
        await rig.DriveAsync(TimeSpan.FromMinutes(2), () => rig.Service.RecordedGaps.Count >= 1);
        Assert.Equal(WishCaptureGapReason.NotJoined, Assert.Single(Assert.Single(rig.Service.RecordedGaps)).Reason);

        var warnings = rig.Logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("InvalidOperationException", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-row-data", warning.Message, StringComparison.Ordinal);

        await rig.DriveAsync(TimeSpan.FromMinutes(61), () => rig.Logger.Entries.Count(e => e.Level == LogLevel.Warning) >= 2, step: TimeSpan.FromMinutes(1));
        await rig.StopAsync();
    }

    [Fact]
    public async Task AFailingObservation_IsTriedFourTimes_ThenDroppedAndCountedInTheMinuteLine()
    {
        await using var rig = new Rig();
        rig.Service.Process = (_, _) => throw new InvalidOperationException("secret-row-data");
        rig.Queue.TryEnqueue(Observation(1));

        await rig.StartAsync();
        await rig.DriveAsync(TimeSpan.FromSeconds(30), () => rig.Service.ProcessCalls >= 4, step: TimeSpan.FromMilliseconds(500));
        await rig.DriveAsync(TimeSpan.FromMinutes(1), () => rig.Logger.Entries.Any(e => e.Message.StartsWith("Wish capture in the last minute", StringComparison.Ordinal)));
        await rig.StopAsync();

        Assert.Equal(4, rig.Service.ProcessCalls);
        Assert.Contains(rig.Logger.Entries, e => e.Message.Contains("1 failed.", StringComparison.Ordinal));
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("after 4 failed attempts", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Message.Contains("secret-row-data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Outcomes_AreCountedInTheMinuteLine_AndTheChannelLimitWarnsAtMostOnceAnHour()
    {
        await using var rig = new Rig();
        var outcomes = new Queue<WishCaptureOutcome>(
            [WishCaptureOutcome.WishCreated, WishCaptureOutcome.WishCreated, WishCaptureOutcome.CandidateStored,
             WishCaptureOutcome.ChannelLimit, WishCaptureOutcome.Discarded, WishCaptureOutcome.ChannelLimit]);
        rig.Service.Process = (_, _) => Task.FromResult(outcomes.Dequeue());

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.Calls.Contains("write"), "first tick");
        for (var i = 1; i <= 4; i++)
        {
            rig.Queue.TryEnqueue(Observation(i));
        }

        await rig.WaitUntilAsync(() => rig.Service.ProcessCalls >= 4, "four observations");
        await rig.DriveAsync(TimeSpan.FromMinutes(1), () => rig.Logger.Entries.Any(e => e.Message.StartsWith("Wish capture in the last minute", StringComparison.Ordinal)));
        rig.Queue.TryEnqueue(Observation(5));
        rig.Queue.TryEnqueue(Observation(6));
        await rig.WaitUntilAsync(() => rig.Service.ProcessCalls >= 6, "two more observations");
        await rig.DriveAsync(TimeSpan.FromMinutes(1), () => rig.Service.HousekeepingCalls.Count >= 3);

        var line = Assert.Single(rig.Logger.Entries, e => e.Message.StartsWith("Wish capture in the last minute", StringComparison.Ordinal));
        Assert.Contains(": 2 created, 1 candidates, 0 duplicates", line.Message, StringComparison.Ordinal);
        var limit = Assert.Single(rig.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("refused 1 observations at the per-channel limit of 200", limit.Message, StringComparison.Ordinal);

        // The second refusal waits for the next hourly warning; the shutdown line takes what is left.
        await rig.StopAsync();
        Assert.Equal(2, rig.Logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    // Sol-R2-P2-8: a full queue against a database that needs two seconds per observation.
    [Fact]
    public async Task Stop_WithAFullQueueAndASlowDatabase_ReturnsWithinTheDrainBudget_AndCountsTheRest()
    {
        await using var rig = new Rig();
        rig.Service.Process = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), rig.Clock, ct);
            return WishCaptureOutcome.WishCreated;
        };
        for (var i = 0; i < RewardObservationQueue.Capacity; i++)
        {
            Assert.True(rig.Queue.TryEnqueue(Observation(i)));
        }

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.ProcessCalls >= 1, "consumer started");

        var stoppedAfter = await rig.StopUnderClockAsync(CancellationToken.None, () =>
            Assert.False(rig.Queue.TryEnqueue(Observation(-1))));

        Assert.True(stoppedAfter <= WishCaptureWorker.StopDrainBudget + TimeSpan.FromMilliseconds(500), $"stop took {stoppedAfter}");
        var created = rig.Service.Completed;
        Assert.InRange(created, 1, 2);
        var left = Assert.Single(rig.Logger.Entries, e => e.Message.Contains("observations not processed; dropped", StringComparison.Ordinal));
        Assert.Contains($"stopped with {RewardObservationQueue.Capacity - created} observations", left.Message, StringComparison.Ordinal);
        var shutdownLine = Assert.Single(rig.Logger.Entries, e => e.Message.StartsWith("Wish capture at shutdown", StringComparison.Ordinal));
        Assert.Contains($": {created} created", shutdownLine.Message, StringComparison.Ordinal);
        Assert.Contains("1 rejected at shutdown", shutdownLine.Message, StringComparison.Ordinal);
        Assert.Equal(0, rig.Queue.Count);
    }

    // The host stops services one after the other with one shared token; the final usage flush stops after this one
    // and must still finish a flush that takes 20 of the remaining seconds.
    [Fact]
    public async Task Stop_LeavesTheRestOfTheShutdownTimeoutToTheFinalUsageFlush()
    {
        await using var rig = new Rig();
        rig.Service.Process = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), rig.Clock, ct);
            return WishCaptureOutcome.WishCreated;
        };
        for (var i = 0; i < RewardObservationQueue.Capacity; i++)
        {
            rig.Queue.TryEnqueue(Observation(i));
        }

        var counter = new EmoteUsageCounter();
        counter.Increment("slot-1", "set-a", UsageCategory.Human);
        counter.Increment("slot-2", "set-a", UsageCategory.Human);
        var flushed = new List<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>();
        var flushService = Substitute.For<IUsageStatFlushService>();
        flushService.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20), rig.Clock, call.Arg<CancellationToken>());
                flushed.Add(call.Arg<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>());
                return new UsageFlushResult(["chan"]);
            });
        var flushServices = new ServiceCollection();
        flushServices.AddScoped(_ => flushService);
        await using var flushProvider = flushServices.BuildServiceProvider();
        var usageFlush = new UsageFlushWorker(
            NullLogger<UsageFlushWorker>.Instance,
            counter,
            new WorkerStats(),
            Substitute.For<IRedisPublisher>(),
            flushProvider.GetRequiredService<IServiceScopeFactory>())
        {
            FinalFlushMaxPause = TimeSpan.Zero,
        };

        await rig.StartAsync();
        await rig.WaitUntilAsync(() => rig.Service.ProcessCalls >= 1, "consumer started");
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30), rig.Clock);

        var captureStop = await rig.StopUnderClockAsync(shutdown.Token);
        var flushStop = await rig.RunUnderClockAsync(() => usageFlush.StopAsync(shutdown.Token));

        Assert.True(captureStop <= WishCaptureWorker.StopDrainBudget + TimeSpan.FromMilliseconds(500), $"capture stop took {captureStop}");
        Assert.True(captureStop + flushStop < TimeSpan.FromSeconds(30));
        Assert.False(shutdown.IsCancellationRequested);
        var batch = Assert.Single(flushed);
        Assert.Equal(2, batch.Count);
        Assert.Equal(0, counter.PendingEmoteCount);
    }

    private static RewardObservation Observation(int n) =>
        new("1001", "reward-1", $"msg-{n}", "2002", "viewer", "text", StartAt.UtcDateTime);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private bool _stopped;

        public Rig(bool captureEnabled = true)
        {
            var services = new ServiceCollection();
            services.AddScoped<IWishCaptureService>(_ => Service);
            _provider = services.BuildServiceProvider();
            Queue = new RewardObservationQueue(Stats);
            var manager = Substitute.For<ITwitchChatManager>();
            manager.GetRoster().Returns(_ => Roster);
            Worker = new WishCaptureWorker(
                Logger,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Queue,
                Tracker,
                manager,
                Stats,
                new WishOptions { Enabled = true, Capture = { Enabled = captureEnabled } },
                Clock);
        }

        public FakeTimeProvider Clock { get; } = new(StartAt);

        public FakeCaptureService Service { get; } = new();

        public WorkerStats Stats { get; } = new();

        public RewardObservationQueue Queue { get; }

        public CaptureGapTracker Tracker { get; } = new();

        public RecordingLogger<WishCaptureWorker> Logger { get; } = new();

        public WishCaptureWorker Worker { get; }

        public IReadOnlyList<TwitchRosterEntry> Roster { get; set; } = [];

        public Task StartAsync() => Worker.StartAsync(CancellationToken.None);

        public async Task StopAsync()
        {
            await StopUnderClockAsync(CancellationToken.None);
        }

        // Stops the worker while winding the fake clock in small steps; returns how much fake time the stop took.
        // afterClose runs right after the synchronous start of StopAsync, i.e. after the queue was closed.
        public async Task<TimeSpan> StopUnderClockAsync(CancellationToken token, Action? afterClose = null)
        {
            _stopped = true;
            return await RunUnderClockAsync(() =>
            {
                var stop = Worker.StopAsync(token);
                afterClose?.Invoke();
                return stop;
            });
        }

        public async Task<TimeSpan> RunUnderClockAsync(Func<Task> start)
        {
            var began = Clock.GetUtcNow();
            var task = start();
            var deadline = DateTime.UtcNow + TestTimeout;
            while (!task.IsCompleted)
            {
                Assert.True(DateTime.UtcNow < deadline, "The call did not finish under the fake clock.");
                await Task.Delay(2);
                if (!task.IsCompleted)
                {
                    Clock.Advance(TimeSpan.FromMilliseconds(100));
                }
            }

            await task;
            return Clock.GetUtcNow() - began;
        }

        // Moves the fake clock in steps until the condition holds or the span is used up; fails in the latter case.
        public async Task DriveAsync(TimeSpan span, Func<bool> condition, TimeSpan? step = null)
        {
            var increment = step ?? TimeSpan.FromSeconds(5);
            var moved = TimeSpan.Zero;
            var deadline = DateTime.UtcNow + TestTimeout;
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "The condition did not hold within the span.");
                await Task.Delay(2);
                if (!condition() && moved < span)
                {
                    Clock.Advance(increment);
                    moved += increment;
                }
            }
        }

        public async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + TestTimeout;
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}.");
                await Task.Delay(2);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
            {
                await StopUnderClockAsync(CancellationToken.None);
            }

            await _provider.DisposeAsync();
            Worker.Dispose();
        }
    }

    internal sealed class FakeCaptureService : IWishCaptureService
    {
        private readonly Lock _gate = new();
        private readonly List<string> _calls = [];
        private readonly List<bool> _housekeeping = [];
        private readonly List<IReadOnlyCollection<WishCaptureGapRecord>> _recorded = [];
        private int _processCalls;
        private int _completed;

        public Func<RewardObservation, CancellationToken, Task<WishCaptureOutcome>> Process { get; set; } =
            (_, _) => Task.FromResult(WishCaptureOutcome.WishCreated);

        public IReadOnlyList<WishCaptureGapChannel> GapSet { get; set; } = [];

        public Exception? GapSetFailure { get; set; }

        public DateTime? LastAlive { get; set; }

        public int RecordGapsFailures { get; set; }

        public int ProcessCalls => Volatile.Read(ref _processCalls);

        public int Completed => Volatile.Read(ref _completed);

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public IReadOnlyList<bool> HousekeepingCalls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _housekeeping];
                }
            }
        }

        public IReadOnlyList<IReadOnlyCollection<WishCaptureGapRecord>> RecordedGaps
        {
            get
            {
                lock (_gate)
                {
                    return [.. _recorded];
                }
            }
        }

        public async Task<WishCaptureOutcome> ProcessAsync(RewardObservation observation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _processCalls);
            Record("process");
            var outcome = await Process(observation, cancellationToken);
            Interlocked.Increment(ref _completed);
            return outcome;
        }

        public Task<WishCaptureHousekeepingResult> RunHousekeepingAsync(bool includePendingExpiry, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _housekeeping.Add(includePendingExpiry);
            }

            return Task.FromResult(new WishCaptureHousekeepingResult(0, 0, 0, 0, 0));
        }

        public Task<IReadOnlyList<WishCaptureGapChannel>> ListGapChannelsAsync(CancellationToken cancellationToken = default)
        {
            Record("list");
            return GapSetFailure is { } failure
                ? Task.FromException<IReadOnlyList<WishCaptureGapChannel>>(failure)
                : Task.FromResult(GapSet);
        }

        public Task<DateTime?> ReadLastAliveAsync(CancellationToken cancellationToken = default)
        {
            Record("read");
            return Task.FromResult(LastAlive);
        }

        public Task WriteLastAliveAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            Record("write");
            return Task.CompletedTask;
        }

        public Task<int> RecordGapsAsync(IReadOnlyCollection<WishCaptureGapRecord> gaps, CancellationToken cancellationToken = default)
        {
            Record("gaps");
            lock (_gate)
            {
                _recorded.Add([.. gaps]);
                if (RecordGapsFailures > 0)
                {
                    RecordGapsFailures--;
                    return Task.FromException<int>(new InvalidOperationException("write failed"));
                }
            }

            return Task.FromResult(gaps.Count);
        }

        private void Record(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }
        }
    }
}
