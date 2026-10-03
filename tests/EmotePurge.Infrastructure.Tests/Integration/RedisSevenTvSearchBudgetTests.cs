using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Against a real redis:7.2-alpine container: what is worth asserting here is the Lua scripts —
// atomic trimming, counting and recording, an extend-only block, an atomic minimum. Every test gets
// its own key prefix, so the shared container never lets one test's window leak into another's.
[Collection("Redis")]
public class RedisSevenTvSearchBudgetTests(RedisFixture fixture)
{
    // Small figures so a window fills in a handful of calls: five in all, three for the Worker.
    private static readonly SevenTvSearchBudgetOptions Options = new()
    {
        MaxRequestsPerWindow = 5,
        ChannelIdentityMaxRequestsPerWindow = 3,
    };

    [Fact]
    public async Task TryCharge_GrantsUpToTheCeiling_ThenRefusesWithoutConsuming()
    {
        var (budget, _, _) = Create();

        for (var i = 1; i <= 5; i++)
        {
            var permit = await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard);
            Assert.True(permit.Granted);
            Assert.Equal(i, permit.UsedInWindow);
        }

        var refused = await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard);
        Assert.Equal(SevenTvSearchRefusal.WindowFull, refused.Refusal);
        Assert.Equal(5, refused.UsedInWindow);
    }

    [Fact]
    public async Task TheWorkersShare_LeavesTheLeaderboardItsReserve()
    {
        var (budget, _, _) = Create();

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await budget.TryChargeAsync(SevenTvSearchConsumer.ChannelIdentity)).Granted);
        }

        Assert.Equal(
            SevenTvSearchRefusal.ConsumerShareFull,
            (await budget.TryChargeAsync(SevenTvSearchConsumer.ChannelIdentity)).Refusal);

        // A stuck-channel storm on the Worker side never locks the leaderboard out.
        Assert.True((await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);
        Assert.True((await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);
        Assert.Equal(
            SevenTvSearchRefusal.WindowFull,
            (await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Refusal);
    }

    [Fact]
    public async Task TwoProcesses_ShareOneWindow()
    {
        // Api and Worker are two budget instances on the same keys — the whole point of Redis here.
        var prefix = NewPrefix();
        var clock = new HandWoundTimeProvider();
        var api = new RedisSevenTvSearchBudget(fixture.Connection, Options, clock, NullLogger<RedisSevenTvSearchBudget>.Instance, prefix);
        var worker = new RedisSevenTvSearchBudget(fixture.Connection, Options, clock, NullLogger<RedisSevenTvSearchBudget>.Instance, prefix);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await worker.TryChargeAsync(SevenTvSearchConsumer.ChannelIdentity)).Granted);
        }

        Assert.True((await api.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);
        Assert.True((await api.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);
        Assert.Equal(SevenTvSearchRefusal.WindowFull, (await api.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Refusal);
    }

    [Fact]
    public async Task ConcurrentCharges_NeverOvershootTheCeiling()
    {
        var (budget, _, _) = Create();

        var permits = await Task.WhenAll(
            Enumerable.Range(0, 40).Select(_ => budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)));

        Assert.Equal(5, permits.Count(p => p.Granted));
    }

    [Fact]
    public async Task TheWindowRolls_APermitAgesOutAfterExactlyOneWindow()
    {
        var (budget, clock, _) = Create();

        for (var i = 0; i < 5; i++)
        {
            await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard);
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        // t = 50 s: all five still inside the window.
        Assert.False((await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);

        // t = 60 s: the first one, granted at 0 s, has aged out — exactly one slot frees up.
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);
        Assert.False((await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);
    }

    [Fact]
    public async Task ARateLimitObservation_BlocksEveryConsumer_UntilTheResetHasPassed()
    {
        var (budget, clock, _) = Create();

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(0, 3583, RateLimited: true, TimeSpan.FromSeconds(3583)));

        var refused = await budget.TryChargeAsync(SevenTvSearchConsumer.ChannelIdentity);
        Assert.Equal(SevenTvSearchRefusal.Blocked, refused.Refusal);
        Assert.Equal(TimeSpan.FromSeconds(3583), refused.BlockedFor);
        Assert.Equal(SevenTvSearchRefusal.Blocked, (await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Refusal);

        clock.Advance(TimeSpan.FromSeconds(3582));
        Assert.Equal(TimeSpan.FromSeconds(1), (await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).BlockedFor);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);
    }

    [Fact]
    public async Task ALowWatermarkObservation_BlocksUntilTheReset()
    {
        var (budget, _, _) = Create();

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(3, 20, RateLimited: false));

        var refused = await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard);
        Assert.Equal(SevenTvSearchRefusal.Blocked, refused.Refusal);
        Assert.Equal(TimeSpan.FromSeconds(20), refused.BlockedFor);
    }

    [Fact]
    public async Task ABlock_IsOnlyEverExtended_NeverShortened()
    {
        var (budget, _, _) = Create();

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(0, null, RateLimited: true, TimeSpan.FromHours(1)));
        await budget.ObserveResponseAsync(new SevenTvSearchObservation(2, 5, RateLimited: false));

        Assert.Equal(TimeSpan.FromHours(1), (await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).BlockedFor);
    }

    [Fact]
    public async Task AHealthyObservation_BlocksNothing_AndABlockedChargeConsumesNothing()
    {
        var (budget, clock, _) = Create();

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(80, 40, RateLimited: false));
        Assert.True((await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).Granted);

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(5, 30, RateLimited: false));
        for (var i = 0; i < 10; i++)
        {
            await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard);
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, (await budget.TryChargeAsync(SevenTvSearchConsumer.Leaderboard)).UsedInWindow);
    }

    [Fact]
    public async Task Observations_KeepTheLowestRemainingOfTheHour()
    {
        var (budget, clock, prefix) = Create();

        await budget.ObserveResponseAsync(new SevenTvSearchObservation(40, 50, RateLimited: false));
        await budget.ObserveResponseAsync(new SevenTvSearchObservation(12, 50, RateLimited: false));
        await budget.ObserveResponseAsync(new SevenTvSearchObservation(30, 50, RateLimited: false));

        var key = $"{prefix}:min-remaining:{clock.Now.UtcDateTime:yyyyMMddHH}";
        var db = fixture.Connection.GetDatabase();
        Assert.Equal("12", (string?)await db.StringGetAsync(key));
        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.InRange(ttl!.Value, TimeSpan.FromHours(24), TimeSpan.FromHours(25));
    }

    private (RedisSevenTvSearchBudget Budget, HandWoundTimeProvider Clock, string Prefix) Create()
    {
        var clock = new HandWoundTimeProvider();
        var prefix = NewPrefix();
        return (new RedisSevenTvSearchBudget(fixture.Connection, Options, clock, NullLogger<RedisSevenTvSearchBudget>.Instance, prefix), clock, prefix);
    }

    private static string NewPrefix() => $"test:seventv-search-budget:{Guid.NewGuid():N}";
}
