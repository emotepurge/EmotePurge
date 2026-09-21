using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Runs against a real redis:7.2-alpine container — verifies the TTL cache's actual
// StackExchange.Redis wiring (key format, string-encoded bool, TTL), not just the interface
// contract mocks would give us.
//
// The moderated-channel list itself belongs to ModeratedChannelsProvider, not to this cache; only
// its invalidation is pinned here, because the admin escape hatch has to clear it along with the
// role answers it feeds.
[Collection("Redis")]
public class ModRoleCacheTests(RedisFixture fixture)
{
    private static IConfiguration BuildConfiguration(int ttlMinutes = 10) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ModCheckCacheTtlMinutes"] = ttlMinutes.ToString() })
            .Build();

    [Fact]
    public async Task InvalidateUserAsync_RemovesModeratedChannelListSubscriberAndEditorEntries()
    {
        // The admin escape hatch has to reach every key shape at once — a surviving moderated-channel
        // list would leave exactly the stale answer the invalidation was triggered for.
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);
        await WriteModeratedChannelListAsync("user-invalidate-1");
        await cache.SetIsSubscriberAsync("user-invalidate-1", "broadcaster-1", isSubscriber: true);
        await cache.SetSevenTvEditorGrantsAsync(
            "user-invalidate-1",
            new SevenTvEditorGrants(new HashSet<string> { "channel-a" }, new HashSet<string> { "broadcaster-1" }));

        var removed = await cache.InvalidateUserAsync("user-invalidate-1");

        Assert.Equal(3, removed);
        Assert.False(await fixture.Connection.GetDatabase().KeyExistsAsync("modlist:user-invalidate-1"));
        Assert.Null(await cache.TryGetIsSubscriberAsync("user-invalidate-1", "broadcaster-1"));
        Assert.Null(await cache.TryGetSevenTvEditorGrantsAsync("user-invalidate-1"));
    }

    [Fact]
    public async Task InvalidateUserAsync_LeavesOtherUsersEntriesUntouched()
    {
        // Both the SCAN pattern and the two directly addressed keys are built from the user id, so a
        // too-greedy glob would silently flush every logged-in user's role answers instead of one
        // person's.
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);
        await WriteModeratedChannelListAsync("user-invalidate-2");
        await WriteModeratedChannelListAsync("user-invalidate-3");
        await cache.SetIsSubscriberAsync("user-invalidate-3", "broadcaster-1", isSubscriber: true);

        var removed = await cache.InvalidateUserAsync("user-invalidate-2");

        Assert.Equal(1, removed);
        Assert.True(await fixture.Connection.GetDatabase().KeyExistsAsync("modlist:user-invalidate-3"));
        Assert.True(await cache.TryGetIsSubscriberAsync("user-invalidate-3", "broadcaster-1"));
    }

    [Fact]
    public async Task InvalidateUserAsync_ForUserWithoutEntries_ReturnsZero()
    {
        // Including the unconditionally probed 7tveditor and modlist keys: they are only counted
        // when they actually existed.
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);

        Assert.Equal(0, await cache.InvalidateUserAsync("user-invalidate-nobody"));
    }

    [Fact]
    public async Task SetSevenTvEditorGrantsAsync_ThenTryGet_RoundTripsTheLoginIdPairs()
    {
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);
        var grants = new SevenTvEditorGrants(
            new HashSet<string> { "channel-a" },
            new HashSet<string> { "111" },
            [new SevenTvEditorGrantEntry("channel-a", "111")]);

        await cache.SetSevenTvEditorGrantsAsync("user-entries-roundtrip", grants);
        var result = await cache.TryGetSevenTvEditorGrantsAsync("user-entries-roundtrip");

        Assert.NotNull(result);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("channel-a", entry.ChannelLogin);
        Assert.Equal("111", entry.TwitchChannelId);
    }

    [Fact]
    public async Task TryGetSevenTvEditorGrantsAsync_ReadsALegacyPayloadWithoutEntries_AsAMiss()
    {
        // Written by hand, not through SetSevenTvEditorGrantsAsync: this is exactly the shape a
        // cache entry from before the Entries field existed still has in Redis, up to its TTL.
        // Handed on, its non-empty grant sets beside an empty Entries would read as "edits
        // nothing" to every reader that walks Entries — so it is a miss, and the caller refreshes.
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);
        const string legacyJson = """{"channelLogins":["legacy-channel"],"twitchChannelIds":["222"]}""";
        await fixture.Connection.GetDatabase().StringSetAsync("7tveditor:user-legacy-payload", legacyJson);

        Assert.Null(await cache.TryGetSevenTvEditorGrantsAsync("user-legacy-payload"));
    }

    // Written directly rather than through ModeratedChannelsProvider: this test is about the key
    // the invalidation has to hit, not about how the list gets there.
    private Task WriteModeratedChannelListAsync(string twitchUserId) =>
        fixture.Connection.GetDatabase().StringSetAsync($"modlist:{twitchUserId}", "[]");
}
