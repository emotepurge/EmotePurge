using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real Redis (redis:7.2-alpine via RedisFixture), like ForeignEmoteSetCacheTests next door: what is
// worth proving against a container rather than a substituted IDatabase is that the round trip
// survives serialization at all — the stored payload is flat while the result type it rebuilds is
// invariant-by-construction — and that a payload this cache cannot make sense of reads as a miss
// instead of as an answer.
[Collection("Redis")]
public class SevenTvEmoteSetListCacheTests(RedisFixture fixture)
{
    [Fact]
    public async Task AnAnswer_SurvivesTheRoundTrip_WithItsSetsAndActiveId()
    {
        const string twitchId = "emote-set-list-cache-answer";
        var cache = NewCache();
        var stored = EmoteSetListResult.Ok(new EmoteSetList("01ACTIVE", [
            new EmoteSetSummary("01ACTIVE", "Emotes", 1000, "NORMAL", false, "HandOfBlood"),
            new EmoteSetSummary("01PERSONAL", "Personal Emotes", null, "PERSONAL", true, null),
        ]));

        await cache.SetAsync(twitchId, stored, TimeSpan.FromSeconds(60));
        var read = await cache.TryGetAsync(twitchId);

        Assert.Equal(EmoteSetListStatus.Ok, read!.Status);
        Assert.Equal("01ACTIVE", read.List!.SevenTvActiveEmoteSetId);
        Assert.Equal(["01ACTIVE", "01PERSONAL"], read.List.Sets.Select(s => s.Id));
        Assert.Equal([false, true], read.List.Sets.Select(s => s.IsPersonal));
        Assert.Null(read.List.Sets[1].Capacity);
    }

    /// <summary>
    /// The half that makes the shelf-life rule work at all (6.1): a negative outcome is held, and it
    /// comes back as the same negative outcome rather than as an empty list — which is the one thing
    /// a caller must never mistake it for.
    /// </summary>
    [Fact]
    public async Task ANegativeOutcome_IsHeldAndReadBackAsItself_NotAsAnEmptyList()
    {
        const string twitchId = "emote-set-list-cache-negative";
        var cache = NewCache();

        await cache.SetAsync(twitchId, EmoteSetListResult.Failed(EmoteSetListStatus.Unavailable), TimeSpan.FromSeconds(60));
        var read = await cache.TryGetAsync(twitchId);

        Assert.Equal(EmoteSetListStatus.Unavailable, read!.Status);
        Assert.Null(read.List);
    }

    [Fact]
    public async Task AnUnreadablePayload_ReadsAsAMiss_InsteadOfThrowing()
    {
        const string twitchId = "emote-set-list-cache-corrupt";
        var cache = NewCache();
        await fixture.Connection.GetDatabase().StringSetAsync($"7tvsets:{twitchId}", "not-json");

        Assert.Null(await cache.TryGetAsync(twitchId));
    }

    private SevenTvEmoteSetListCache NewCache() =>
        new(fixture.Connection, NullLogger<SevenTvEmoteSetListCache>.Instance);
}
