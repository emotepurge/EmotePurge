using EmotePurge.Core.Chat;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// The requeue side of the flush's return contract (plan #373, E9): a throw puts the whole batch back
/// under the overall failure streak; a result with handed-back keys books the rest as written and puts
/// back only those keys, under a streak of their own with the same bound of five. Container-free: the
/// flush service is a substitute. <see cref="UsageFlushWorker.FlushOnceAsync"/> runs exactly one flush
/// cycle; the final flush of <see cref="UsageFlushWorker.StopAsync"/> (plan #373, E9a: bounded retries)
/// is driven on a worker that was never started, with the retry pause turned off.
/// </summary>
public class UsageFlushWorkerTests
{
    private const string Set = "set-a";

    private readonly EmoteUsageCounter _counter = new();
    private readonly WorkerStats _stats = new();
    private readonly IUsageStatFlushService _flush = Substitute.For<IUsageStatFlushService>();
    private readonly List<Dictionary<UsageCounterKey, EmoteUsageCounts>> _batches = [];

    [Fact]
    public async Task APartialResult_PutsBackOnlyTheHandedBackKeys_AndBooksTheRestAsWritten()
    {
        var worker = CreateWorker();
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        _counter.Increment("pending-slot:e2", Set, UsageCategory.Human);
        _counter.Increment("pending-slot:e2", Set, UsageCategory.Bot);
        var pending = new UsageCounterKey("pending-slot:e2", Set);
        ReturnsInOrder(Partial(pending, new EmoteUsageCounts(1, 1, 0)), Written());

        await worker.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(0, _stats.ConsecutiveFlushFailures);
        Assert.Equal(1, _stats.LastFlushRowCount);
        Assert.Equal(new EmoteUsageCounts(1, 1, 0), Assert.Single(_counter.DrainAndReset(), kvp => kvp.Key == pending).Value);
    }

    [Fact]
    public async Task HandedBackKeys_ArePutBackFiveTimesInARow_ThenDropped_AndAFullFlushResetsTheirStreak()
    {
        var worker = CreateWorker();
        var pending = new UsageCounterKey("pending-slot:e1", Set);
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Record(call);
                return Partial(pending, new EmoteUsageCounts(2, 0, 0));
            });
        _counter.Increment(pending.SlotId, Set, UsageCategory.Human);
        _counter.Increment(pending.SlotId, Set, UsageCategory.Human);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await worker.FlushOnceAsync(CancellationToken.None);
            Assert.Equal(1, _counter.PendingEmoteCount);
        }

        await worker.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(0, _counter.PendingEmoteCount);
        Assert.Equal(6, _batches.Count);
        // The overall streak never moved: the flush itself did not fail.
        Assert.Equal(0, _stats.ConsecutiveFlushFailures);

        // A flush that hands nothing back ends the streak: the next handed-back key gets five tries again.
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(Written(), Partial(pending, new EmoteUsageCounts(1, 0, 0)));
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        await worker.FlushOnceAsync(CancellationToken.None);
        _counter.Increment(pending.SlotId, Set, UsageCategory.Human);
        await worker.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(1, _counter.PendingEmoteCount);
    }

    [Fact]
    public async Task AThrowPutsBackTheWholeBatch_UnderTheOverallStreak()
    {
        var worker = CreateWorker();
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        _counter.Increment("pending-slot:e2", Set, UsageCategory.Human);
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("phase 1 failed"));

        await worker.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(1, _stats.ConsecutiveFlushFailures);
        Assert.Equal(2, _counter.PendingEmoteCount);
    }

    [Fact]
    public async Task TheFinalFlush_RetriesAThrownPhaseOne_AndSucceedsOnTheSecondAttempt()
    {
        var worker = CreateWorker();
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        var calls = 0;
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Record(call);
                return ++calls == 1
                    ? throw new InvalidOperationException("40P01 deadlock detected")
                    : Written();
            });

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, _batches.Count);
        Assert.Equal(_batches[0], _batches[1]);
        Assert.Equal(0, _counter.PendingEmoteCount);
        Assert.Equal(0, _stats.ConsecutiveFlushFailures);
    }

    [Fact]
    public async Task TheFinalFlush_RetriesAPartialResult_WithOnlyTheRemainingKeys()
    {
        var worker = CreateWorker();
        var pending = new UsageCounterKey("pending-slot:e2", Set);
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        _counter.Increment(pending.SlotId, Set, UsageCategory.Human);
        _counter.Increment(pending.SlotId, Set, UsageCategory.Bot);
        ReturnsInOrder(Partial(pending, new EmoteUsageCounts(1, 1, 0)), Written());

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, _batches.Count);
        Assert.Equal(2, _batches[0].Count);
        var retry = Assert.Single(_batches[1]);
        Assert.Equal(pending, retry.Key);
        Assert.Equal(new EmoteUsageCounts(1, 1, 0), retry.Value);
        Assert.Equal(0, _counter.PendingEmoteCount);
    }

    [Fact]
    public async Task TheFinalFlush_GivesUpAfterThreeAttempts_AndDropsWhatIsLeftWithAnErrorLine()
    {
        var logger = new CapturingLogger();
        var worker = CreateWorker(logger);
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Record(call);
                return Task.FromException<UsageFlushResult>(new InvalidOperationException("lock timeout"));
            });

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(3, _batches.Count);
        Assert.Equal(0, _counter.PendingEmoteCount);
        Assert.Contains(logger.Errors, m => m.Contains("1 counter keys with 2 counted uses dropped"));
    }

    [Fact]
    public async Task TheFinalFlush_StopsRetryingOnceTheShutdownTokenIsCancelled_AndDrops()
    {
        var logger = new CapturingLogger();
        var worker = CreateWorker(logger);
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        using var cts = new CancellationTokenSource();
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Record(call);
                cts.Cancel();
                return Task.FromException<UsageFlushResult>(new OperationCanceledException(cts.Token));
            });

        await worker.StopAsync(cts.Token);

        Assert.Single(_batches);
        Assert.Equal(0, _counter.PendingEmoteCount);
        Assert.Contains(logger.Errors, m => m.Contains("dropped"));
    }

    private UsageFlushWorker CreateWorker(ILogger<UsageFlushWorker>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _flush);
        return new UsageFlushWorker(
            logger ?? NullLogger<UsageFlushWorker>.Instance,
            _counter,
            _stats,
            Substitute.For<IRedisPublisher>(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>())
        {
            FinalFlushMaxPause = TimeSpan.Zero,
        };
    }

    private void ReturnsInOrder(params UsageFlushResult[] results)
    {
        var next = 0;
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Record(call);
                return results[Math.Min(next++, results.Length - 1)];
            });
    }

    private void Record(NSubstitute.Core.CallInfo call) =>
        _batches.Add(new Dictionary<UsageCounterKey, EmoteUsageCounts>(call.Arg<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>()));

    private static UsageFlushResult Written() => new(["chan"], new Dictionary<UsageCounterKey, EmoteUsageCounts>());

    private static UsageFlushResult Partial(UsageCounterKey key, EmoteUsageCounts counts) =>
        new(["chan"], new Dictionary<UsageCounterKey, EmoteUsageCounts> { [key] = counts });

    private sealed class CapturingLogger : ILogger<UsageFlushWorker>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
