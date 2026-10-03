using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The budget's behaviour without a healthy Redis: charging is fail-closed — unreachable and slow
/// alike — observing is fail-open, a failing telemetry write never costs a block, and neither method
/// throws for a store failure (design note <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>, 2.5).
/// </summary>
public class RedisSevenTvSearchBudgetFailureTests
{
    [Fact]
    public async Task TryCharge_RefusesWhenRedisIsUnreachable()
    {
        var logger = new RecordingLogger<RedisSevenTvSearchBudget>();
        var budget = new RedisSevenTvSearchBudget(UnreachableRedis(), new SevenTvSearchBudgetOptions(), TimeProvider.System, logger);

        var permit = await budget.TryChargeAsync(SevenTvSearchConsumer.ChannelIdentity);

        Assert.False(permit.Granted);
        Assert.Equal(SevenTvSearchRefusal.StoreUnavailable, permit.Refusal);
        Assert.Contains(logger.Entries, e => e.Message.Contains("fail-closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryCharge_RefusesWhenRedisStalls_WithinTheOperationTimeout()
    {
        // A slow Redis must cost the leaderboard a bounded wait and a refusal, never seconds of
        // latency — and never a search that went out unguarded.
        var (multiplexer, _) = RedisWith(_ => new TaskCompletionSource<RedisResult>().Task);
        var budget = new RedisSevenTvSearchBudget(
            multiplexer,
            new SevenTvSearchBudgetOptions(),
            TimeProvider.System,
            new RecordingLogger<RedisSevenTvSearchBudget>(),
            operationTimeout: TimeSpan.FromMilliseconds(50));

        var charge = budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard);
        var finished = await Task.WhenAny(charge, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(charge, finished);
        Assert.Equal(SevenTvSearchRefusal.StoreUnavailable, (await charge).Refusal);
    }

    [Fact]
    public async Task TryCharge_HonoursTheCallersToken_WhileRedisStalls()
    {
        var (multiplexer, _) = RedisWith(_ => new TaskCompletionSource<RedisResult>().Task);
        var budget = new RedisSevenTvSearchBudget(
            multiplexer,
            new SevenTvSearchBudgetOptions(),
            TimeProvider.System,
            new RecordingLogger<RedisSevenTvSearchBudget>(),
            operationTimeout: TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard, cancellation.Token));
    }

    [Fact]
    public async Task ObserveResponse_SwallowsAnUnreachableRedis()
    {
        var budget = new RedisSevenTvSearchBudget(
            UnreachableRedis(), new SevenTvSearchBudgetOptions(), TimeProvider.System, new RecordingLogger<RedisSevenTvSearchBudget>());

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(0, 3583, RateLimited: true));
    }

    [Fact]
    public async Task ObserveResponse_StillSetsTheBlock_WhenTheTelemetryWriteFails()
    {
        // The block is what keeps every consumer out of a locked bucket; the minimum-remaining
        // sample is only telemetry. A transient failure of the latter must not cost the former.
        var (multiplexer, writtenKeys) = RedisWith(keys =>
            keys.Any(k => k.ToString().Contains(":min-remaining:", StringComparison.Ordinal))
                ? Task.FromException<RedisResult>(new RedisException("telemetry write failed"))
                : Task.FromResult(RedisResult.Create((RedisValue)1)));
        var logger = new RecordingLogger<RedisSevenTvSearchBudget>();
        var budget = new RedisSevenTvSearchBudget(multiplexer, new SevenTvSearchBudgetOptions(), TimeProvider.System, logger);

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(0, 3583, RateLimited: true, TimeSpan.FromSeconds(3583)));

        var blockIndex = writtenKeys.FindIndex(k => k.EndsWith(":blocked-until:rate-limited", StringComparison.Ordinal));
        var telemetryIndex = writtenKeys.FindIndex(k => k.Contains(":min-remaining:", StringComparison.Ordinal));
        Assert.True(blockIndex >= 0, "The block was not written.");
        Assert.True(telemetryIndex > blockIndex, "The block must be written before the telemetry.");
        Assert.Contains(logger.Entries, e => e.Message.Contains("blocked for 3583s", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("minimum-remaining sample was dropped", StringComparison.Ordinal));
    }

    private static IConnectionMultiplexer UnreachableRedis()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>())
            .Returns(_ => throw new RedisException("Redis is down."));
        return multiplexer;
    }

    // A database whose every script call is answered by `answer`, recording the first key of each
    // call in order.
    private static (IConnectionMultiplexer Multiplexer, List<string> WrittenKeys) RedisWith(
        Func<RedisKey[], Task<RedisResult>> answer)
    {
        var writtenKeys = new List<string>();
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var keys = call.ArgAt<RedisKey[]?>(1) ?? [];
                writtenKeys.Add(keys.Length > 0 ? keys[0].ToString() : string.Empty);
                return answer(keys);
            });
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return (multiplexer, writtenKeys);
    }
}
