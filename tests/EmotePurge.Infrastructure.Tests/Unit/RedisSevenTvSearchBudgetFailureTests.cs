using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The budget's behaviour without Redis: charging is fail-closed, observing is fail-open, and
/// neither throws (design note <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>, 2.5).
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
    public async Task ObserveResponse_SwallowsAnUnreachableRedis()
    {
        var budget = new RedisSevenTvSearchBudget(
            UnreachableRedis(), new SevenTvSearchBudgetOptions(), TimeProvider.System, new RecordingLogger<RedisSevenTvSearchBudget>());

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(0, 3583, RateLimited: true));
    }

    private static IConnectionMultiplexer UnreachableRedis()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>())
            .Returns(_ => throw new RedisException("Redis is down."));
        return multiplexer;
    }
}
