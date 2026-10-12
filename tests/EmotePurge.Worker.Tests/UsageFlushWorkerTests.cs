using EmotePurge.Core.Chat;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// The requeue side of the flush's return contract (plan #373, E9): a throw puts the whole batch back
/// under the overall failure streak; a result with handed-back keys books the rest as written and puts
/// back only those keys, under a streak of their own with the same bound of five. Container-free: the
/// flush service is a substitute. One <see cref="UsageFlushWorker.StopAsync"/> on a worker that was
/// never started runs exactly one flush.
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

        await worker.StopAsync(CancellationToken.None);

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
            await worker.StopAsync(CancellationToken.None);
            Assert.Equal(1, _counter.PendingEmoteCount);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, _counter.PendingEmoteCount);
        Assert.Equal(6, _batches.Count);
        // The overall streak never moved: the flush itself did not fail.
        Assert.Equal(0, _stats.ConsecutiveFlushFailures);

        // A flush that hands nothing back ends the streak: the next handed-back key gets five tries again.
        _flush.FlushAsync(Arg.Any<IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts>>(), Arg.Any<CancellationToken>())
            .Returns(Written(), Partial(pending, new EmoteUsageCounts(1, 0, 0)));
        _counter.Increment("slot-1", Set, UsageCategory.Human);
        await worker.StopAsync(CancellationToken.None);
        _counter.Increment(pending.SlotId, Set, UsageCategory.Human);
        await worker.StopAsync(CancellationToken.None);

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

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, _stats.ConsecutiveFlushFailures);
        Assert.Equal(2, _counter.PendingEmoteCount);
    }

    private UsageFlushWorker CreateWorker()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _flush);
        return new UsageFlushWorker(
            NullLogger<UsageFlushWorker>.Instance,
            _counter,
            _stats,
            Substitute.For<IRedisPublisher>(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
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
}
