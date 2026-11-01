using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real Redis (redis:7.2-alpine via RedisFixture) for the one failure mode that cannot be reproduced
// through a substituted IDatabase: StringGetAsync succeeding but returning a payload JsonSerializer
// cannot parse. Same corrupt-payload precedent as ModeratedChannelsProviderTests
// .GetModeratedChannelsAsync_TreatsAnUnreadablePayload_AsAMiss. The RedisException/TimeoutException
// half of ForeignEmoteSetCache's class remark lives container-free in
// Unit/ForeignEmoteSetCacheFailureModeTests.cs instead — no real outage needed to prove a caught
// exception type never leaves the method.
[Collection("Redis")]
public class ForeignEmoteSetCacheTests(RedisFixture fixture)
{
    [Fact]
    public async Task TryGetAsync_UnreadablePayload_ReturnsNullInsteadOfThrowing()
    {
        const string channel = "foreign-cache-corrupt-payload";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        await fixture.Connection.GetDatabase().StringSetAsync(Key(channel), "not-json");

        var result = await cache.TryGetAsync(channel);

        Assert.Null(result);
    }

    [Fact]
    public async Task ARoundTrip_KeepsAddedAt_ForATimestampAndForNull_AndStoresTheSchemaVersionOutsideTheSet()
    {
        const string channel = "foreign-cache-added-at";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        var addedAt = new DateTime(2026, 3, 14, 18, 22, 5, DateTimeKind.Utc);
        var stored = new ForeignEmoteSet(
            channel, null, "set-1", 2, false,
            [
                new ForeignEmoteRow("e1", "A", "A", "https://cdn.example/e1", null, null, addedAt),
                new ForeignEmoteRow("e2", "B", "B", "https://cdn.example/e2", null, null)
            ]);

        await cache.SetAsync(channel, stored);
        var viaSet = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        await viaSet.SetBySetIdAsync("set-1", stored);
        var fromLogin = await cache.TryGetAsync(channel);
        var fromSet = await viaSet.TryGetBySetIdAsync("set-1");

        // The version is in the stored envelope, never on the set itself: the set is also the public
        // HTTP answer and must not carry a cache internal.
        var raw = (string?)await fixture.Connection.GetDatabase().StringGetAsync($"7tvforeign:v3:login:{channel}");
        Assert.Contains("\"schemaVersion\":3", raw);
        Assert.DoesNotContain("schemaVersion", System.Text.Json.JsonSerializer.Serialize(stored, System.Text.Json.JsonSerializerOptions.Web));
        foreach (var read in new[] { fromLogin, fromSet })
        {
            Assert.NotNull(read);
            Assert.Equal(addedAt, read.Emotes[0].AddedAt);
            Assert.Null(read.Emotes[1].AddedAt);
        }
    }

    [Fact]
    public async Task TheKeysCarryTheV3Prefixes_ForTheLoginAndTheSetIdSpace()
    {
        const string channel = "foreign-cache-v3-keys";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        var set = new ForeignEmoteSet(channel, null, "set-v3", 0, false, []);

        await cache.SetAsync(channel, set);
        await cache.SetBySetIdAsync("set-v3", set);

        var db = fixture.Connection.GetDatabase();
        Assert.True(await db.KeyExistsAsync($"7tvforeign:v3:login:{channel}"));
        Assert.True(await db.KeyExistsAsync("7tvforeign:v3:set:set-v3"));
        Assert.False(await db.KeyExistsAsync($"7tvforeign:v2:login:{channel}"));
        Assert.False(await db.KeyExistsAsync("7tvforeign:v2:set:set-v3"));
    }

    /// <summary>
    /// #457 (AK 4): a round-trip keeps the preview's three-way verdict and omitted count, in both key
    /// spaces. fails_when: either field is lost, which would turn a gap back into the fail-closed
    /// state derived from <c>truncated</c> on every cache hit.
    /// </summary>
    [Fact]
    public async Task ARoundTrip_KeepsReadStateAndOmittedCount()
    {
        const string channel = "foreign-cache-read-state";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        var stored = new ForeignEmoteSet(
            channel, null, "set-gap", 496, true,
            [new ForeignEmoteRow("e1", "A", "A", "https://cdn.example/e1", null, null)],
            ReadState: SevenTvSetReadState.Gap, OmittedCount: 2);

        await cache.SetAsync(channel, stored);
        await cache.SetBySetIdAsync("set-gap", stored);

        foreach (var read in new[] { await cache.TryGetAsync(channel), await cache.TryGetBySetIdAsync("set-gap") })
        {
            Assert.NotNull(read);
            Assert.Equal(SevenTvSetReadState.Gap, read.ReadState);
            Assert.Equal(2, read.OmittedCount);
            Assert.True(read.Truncated);
        }
    }

    /// <summary>
    /// #457 (AK 4): what the previous code wrote — a schema-2 envelope, which has no read state — is a
    /// miss, both under its own v2 keys and if it were ever found under a current v3 key. Served, its
    /// under-count would read as inconsistent where the fresh read might be a gap.
    /// </summary>
    [Fact]
    public async Task AV2Envelope_IsAMiss_UnderItsOwnKeysAndUnderTheCurrentOnes()
    {
        const string channel = "foreign-cache-v2-envelope";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        const string v2 =
            """{"schemaVersion":2,"emoteSet":{"channelName":"x","sevenTvUserId":null,"emoteSetId":"s","totalCount":496,"truncated":true,"emotes":[]}}""";
        var db = fixture.Connection.GetDatabase();
        await db.StringSetAsync($"7tvforeign:v2:login:{channel}", v2);
        await db.StringSetAsync("7tvforeign:v2:set:v2-set", v2);

        Assert.Null(await cache.TryGetAsync(channel));
        Assert.Null(await cache.TryGetBySetIdAsync("v2-set"));

        await db.StringSetAsync(Key(channel), v2);
        await db.StringSetAsync("7tvforeign:v3:set:v2-set", v2);

        Assert.Null(await cache.TryGetAsync(channel));
        Assert.Null(await cache.TryGetBySetIdAsync("v2-set"));
    }

    [Fact]
    public async Task APayloadWithoutASchemaVersion_IsAMiss_EvenUnderTheCurrentKey()
    {
        // What the previous code wrote: no schemaVersion, rows without addedAt. If such an entry were
        // ever found under a current key (it cannot be, by prefix - this is the second belt), reading
        // it would turn every row's missing date into "7TV reported none" and drop every gate.
        const string channel = "foreign-cache-pre-v2";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        const string preV2 =
            """{"channelName":"x","sevenTvUserId":null,"emoteSetId":"s","totalCount":1,"truncated":false,"emotes":[{"sevenTvEmoteId":"e1","name":"A","defaultName":"A","imageUrl":"u","topAllTime":null,"trending":null}],"emoteSetName":null,"capacity":null}""";
        await fixture.Connection.GetDatabase().StringSetAsync(Key(channel), preV2);

        Assert.Null(await cache.TryGetAsync(channel));
    }

    [Fact]
    public async Task APayloadWithAnOlderExplicitSchemaVersion_IsAMiss()
    {
        const string channel = "foreign-cache-v1-explicit";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        const string v1 =
            """{"schemaVersion":1,"emoteSet":{"channelName":"x","sevenTvUserId":null,"emoteSetId":"s","totalCount":0,"truncated":false,"emotes":[]}}""";
        await fixture.Connection.GetDatabase().StringSetAsync(Key(channel), v1);

        Assert.Null(await cache.TryGetAsync(channel));
    }

    [Fact]
    public async Task AnOldKeyOfThePreviousCode_IsNeverRead()
    {
        const string channel = "foreign-cache-old-key";
        var cache = new ForeignEmoteSetCache(fixture.Connection, NullLogger<ForeignEmoteSetCache>.Instance);
        const string valid =
            """{"schemaVersion":2,"emoteSet":{"channelName":"x","sevenTvUserId":null,"emoteSetId":"s","totalCount":0,"truncated":false,"emotes":[]}}""";
        await fixture.Connection.GetDatabase().StringSetAsync($"7tvforeign:{channel}", valid);
        await fixture.Connection.GetDatabase().StringSetAsync("7tvforeign:set:old-set", valid);

        Assert.Null(await cache.TryGetAsync(channel));
        Assert.Null(await cache.TryGetBySetIdAsync("old-set"));
    }

    private static RedisKey Key(string channel) => $"7tvforeign:v3:login:{channel}";
}
