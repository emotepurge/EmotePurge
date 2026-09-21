using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free counterpart to Integration/ForeignChannelIdentityCacheTests.cs (real Redis, round
// trip). Reproduces the other half of the class remark on ForeignChannelIdentityCache — a Redis
// outage — the same shape as ForeignEmoteSetCacheFailureModeTests: substitute
// IConnectionMultiplexer/IDatabase so StringGetAsync/StringSetAsync throw a
// RedisConnectionException, and pin that TryGetTwitchUserIdAsync degrades to a miss while
// SetTwitchUserIdAsync swallows the failure outright — a Redis outage costs the next request a live
// Helix lookup, never a 503, since this cache is a cost optimisation, not a correctness boundary.
public class ForeignChannelIdentityCacheFailureModeTests
{
    private static RedisConnectionException BuildConnectionException() =>
        new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis ist nicht erreichbar.", null, CommandStatus.Unknown);

    private static ForeignChannelIdentityCache CreateCacheWithFailingRedis()
    {
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>())
            .Returns<RedisValue>(_ => throw BuildConnectionException());
        // Same overload-resolution trap ForeignEmoteSetCacheFailureModeTests documents:
        // SetTwitchUserIdAsync calls StringSetAsync(key, value, TimeToLive) with only three
        // positional args, and the bare TimeSpan resolves to the (RedisKey, RedisValue, Expiration,
        // ValueCondition, CommandFlags) overload via TimeSpan's implicit conversion to Expiration.
        // Arg.Any<TimeSpan>() would leave this unmatched and fall through to a real, non-throwing
        // substitute default.
        database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>())
            .Returns<bool>(_ => throw BuildConnectionException());

        var connectionMultiplexer = Substitute.For<IConnectionMultiplexer>();
        connectionMultiplexer.GetDatabase().Returns(database);

        return new ForeignChannelIdentityCache(connectionMultiplexer, NullLogger<ForeignChannelIdentityCache>.Instance);
    }

    [Fact]
    public async Task TryGetTwitchUserIdAsync_RedisConnectionFails_ReturnsNullInsteadOfThrowing()
    {
        var cache = CreateCacheWithFailingRedis();

        var result = await cache.TryGetTwitchUserIdAsync("handofblood");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetTwitchUserIdAsync_RedisConnectionFails_DoesNotThrow()
    {
        var cache = CreateCacheWithFailingRedis();

        // GetForeignEmoteSetListAsync writes this right after a Helix cache miss resolves live —
        // without this guard, that write failing would leave the request itself in the lurch one
        // step after the read guard already proved it does not.
        var exception = await Record.ExceptionAsync(
            () => cache.SetTwitchUserIdAsync("handofblood", "36340781"));

        Assert.Null(exception);
    }
}
