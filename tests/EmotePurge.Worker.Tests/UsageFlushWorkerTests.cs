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
/// under the overall failure streak. Container-free: the flush service is a substitute. <see cref="UsageFlushWorker.FlushOnceAsync"/> runs exactly one flush
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
    public async Task AThrowPutsBackTheWholeBatch_UnderTheOverallStreak()
    {
        var worker = CreateWorker();
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        _counter.Increment("slot-2", Set, UsageCategory.Human);
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the flush failed"));

        await worker.FlushOnceAsync(CancellationToken.None);

        Assert.Equal(1, _stats.ConsecutiveFlushFailures);
        Assert.Equal(2, _counter.PendingEmoteCount);
    }

    [Fact]
    public async Task TheFinalFlush_RetriesAThrownFlush_AndSucceedsOnTheSecondAttempt()
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
        Assert.Contains(logger.At(LogLevel.Error), m => m.Contains("1 counter keys with 2 counted uses dropped"));
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
        Assert.Contains(logger.At(LogLevel.Error), m => m.Contains("dropped"));
    }

    [Fact]
    public async Task TheFinalFlush_DoesNotRetry_WhenChatKeepsCountingDuringACleanAttempt()
    {
        // Worker, which owns the Twitch client, stops after this service: messages keep arriving while
        // the final flush runs. A clean attempt must not be mistaken for a failed one.
        var logger = new CapturingLogger();
        var worker = CreateWorker(logger);
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Record(call);
                _counter.Increment("slot-2", Set, UsageCategory.Human);
                return Written();
            });

        await worker.StopAsync(CancellationToken.None);

        // The attempt plus one plain flush of what arrived during it — no retry loop.
        Assert.Equal(2, _batches.Count);
        Assert.Equal(new UsageCounterKey("slot-1", Set), Assert.Single(_batches[0]).Key);
        Assert.Equal(new UsageCounterKey("slot-2", Set), Assert.Single(_batches[1]).Key);
        Assert.Equal(0, _counter.PendingEmoteCount);
        Assert.Empty(logger.At(LogLevel.Error));
        Assert.Empty(logger.At(LogLevel.Warning));
        Assert.Contains(logger.At(LogLevel.Information), m => m.Contains("1 counter keys with 1 counted uses that arrived during shutdown"));
    }

    [Fact]
    public async Task TheFinalFlush_DropsFreshCountsWithAWarning_WhenTheirPlainFlushFails()
    {
        var logger = new CapturingLogger();
        var worker = CreateWorker(logger);
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        var calls = 0;
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Record(call);
                if (++calls > 1)
                {
                    return Task.FromException<UsageFlushResult>(new InvalidOperationException("lock timeout"));
                }

                _counter.Increment("slot-2", Set, UsageCategory.Human);
                return Task.FromResult(Written());
            });

        await worker.StopAsync(CancellationToken.None);

        // No retry for the plain flush, and no "gave up" error: the failed attempts were not exhausted.
        Assert.Equal(2, _batches.Count);
        Assert.Equal(0, _counter.PendingEmoteCount);
        Assert.Empty(logger.At(LogLevel.Error));
        Assert.Contains(logger.At(LogLevel.Warning), m => m.Contains("1 counter keys with 1 counted uses dropped"));
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

    private void Record(NSubstitute.Core.CallInfo call) =>
        _batches.Add(new Dictionary<UsageCounterKey, EmoteUsageCounts>(call.Arg<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>()));

    private static UsageFlushResult Written() => new(["chan"]);

    private sealed class CapturingLogger : ILogger<UsageFlushWorker>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IEnumerable<string> At(LogLevel level) => _entries.Where(e => e.Level == level).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
