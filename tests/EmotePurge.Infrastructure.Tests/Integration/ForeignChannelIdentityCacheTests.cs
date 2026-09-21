using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real Redis (redis:7.2-alpine via RedisFixture), same reasoning as SevenTvEmoteSetListCacheTests
// next door — what a substituted IDatabase cannot prove is that the round trip survives at all, and
// that the cache's own key space (7tvforeignidentity:) never collides with a neighbouring channel's
// entry.
[Collection("Redis")]
public class ForeignChannelIdentityCacheTests(RedisFixture fixture)
{
    [Fact]
    public async Task AWrittenTwitchId_SurvivesTheRoundTrip()
    {
        const string channelName = "foreign-identity-cache-answer";
        var cache = NewCache();

        await cache.SetTwitchUserIdAsync(channelName, "36340781");

        Assert.Equal("36340781", await cache.TryGetTwitchUserIdAsync(channelName));
    }

    [Fact]
    public async Task AnUnwrittenChannel_ReadsAsAMiss()
    {
        var cache = NewCache();

        Assert.Null(await cache.TryGetTwitchUserIdAsync("foreign-identity-cache-never-written"));
    }

    [Fact]
    public async Task TwoChannels_DoNotShareAKey()
    {
        var cache = NewCache();

        await cache.SetTwitchUserIdAsync("foreign-identity-cache-channel-a", "111");
        await cache.SetTwitchUserIdAsync("foreign-identity-cache-channel-b", "222");

        Assert.Equal("111", await cache.TryGetTwitchUserIdAsync("foreign-identity-cache-channel-a"));
        Assert.Equal("222", await cache.TryGetTwitchUserIdAsync("foreign-identity-cache-channel-b"));
    }

    private ForeignChannelIdentityCache NewCache() =>
        new(fixture.Connection, NullLogger<ForeignChannelIdentityCache>.Instance);
}
