using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Against a real redis:7.2-alpine container, because everything worth asserting here is Redis
// behaviour: hash counters that add up across time buckets, keys that carry a TTL, and a last
// incident entry that overwrites itself. A mock would only prove that we call StackExchange.Redis.
//
// Every test gets its own instant on a controllable clock and its own dimension names, so the
// shared container never lets one test's buckets leak into another's window.
[Collection("Redis")]
public class RateLimitTelemetryStoreTests(RedisFixture fixture)
{
    private static int _clockOffsetDays;

    [Fact]
    public async Task RecordPolicyDecisionAsync_CountsAcceptedAndRejected_InBothWindows()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var policy = NewName("policy");

        await store.RecordPolicyDecisionAsync(Accepted(policy));
        await store.RecordPolicyDecisionAsync(Accepted(policy));
        await store.RecordPolicyDecisionAsync(Rejected(policy, retryAfterSeconds: 7));

        var snapshot = await store.ReadAsync();

        Assert.True(snapshot.TelemetryAvailable);
        var counters = Assert.Single(snapshot.Policies, p => p.PolicyName == policy);
        Assert.Equal(2, counters.AcceptedLastMinute);
        Assert.Equal(1, counters.RejectedLastMinute);
        Assert.Equal(2, counters.AcceptedLast24Hours);
        Assert.Equal(1, counters.RejectedLast24Hours);
    }

    [Fact]
    public async Task ReadAsync_DropsAnEventOutOfTheMinuteWindow_ButKeepsItForTwentyFourHours()
    {
        // The boundary the two windows share: they are summed from the same events, so an event that
        // has just aged past a minute must disappear from one window and stay in the other. Getting
        // this wrong in either direction is invisible in a single-window test.
        var clock = NewClock();
        var store = NewStore(clock);
        var policy = NewName("policy");

        await store.RecordPolicyDecisionAsync(Rejected(policy, retryAfterSeconds: 3));
        clock.Advance(TimeSpan.FromSeconds(90));

        var snapshot = await store.ReadAsync();

        var counters = Assert.Single(snapshot.Policies, p => p.PolicyName == policy);
        Assert.Equal(0, counters.RejectedLastMinute);
        Assert.Equal(1, counters.RejectedLast24Hours);
    }

    [Fact]
    public async Task ReadAsync_KeepsAnEventInsideTheMinuteWindow_UntilItIsAMinuteOld()
    {
        // The other half of the boundary: half a minute in, the event is still inside both windows.
        var clock = NewClock();
        var store = NewStore(clock);
        var policy = NewName("policy");

        await store.RecordPolicyDecisionAsync(Accepted(policy));
        clock.Advance(TimeSpan.FromSeconds(30));

        var snapshot = await store.ReadAsync();

        var counters = Assert.Single(snapshot.Policies, p => p.PolicyName == policy);
        Assert.Equal(1, counters.AcceptedLastMinute);
        Assert.Equal(1, counters.AcceptedLast24Hours);
    }

    [Fact]
    public async Task ReadAsync_DropsAnEventOutOfTheDayWindow_OnceItIsOlderThanTwentyFourHours()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var policy = NewName("policy");

        await store.RecordPolicyDecisionAsync(Accepted(policy));
        clock.Advance(TimeSpan.FromHours(25));

        var snapshot = await store.ReadAsync();

        Assert.DoesNotContain(snapshot.Policies, p => p.PolicyName == policy);
    }

    [Fact]
    public async Task RecordPolicyDecisionAsync_KeepsOnlyTheLatestLocalRejection()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var first = NewName("policy");
        var second = NewName("policy");

        await store.RecordPolicyDecisionAsync(Rejected(first, retryAfterSeconds: 1));
        clock.Advance(TimeSpan.FromSeconds(5));
        await store.RecordPolicyDecisionAsync(new RateLimitPolicyDecision(
            second, Accepted: false, "POST", "/api/vote-sessions/{id}/votes", "session:42", RetryAfterSeconds: 9));

        var snapshot = await store.ReadAsync();

        Assert.NotNull(snapshot.LastLocalRejection);
        Assert.Equal(second, snapshot.LastLocalRejection!.PolicyName);
        Assert.Equal("POST", snapshot.LastLocalRejection.HttpMethod);
        Assert.Equal("/api/vote-sessions/{id}/votes", snapshot.LastLocalRejection.RouteTemplate);
        Assert.Equal("session:42", snapshot.LastLocalRejection.Partition);
        Assert.Equal(9, snapshot.LastLocalRejection.RetryAfterSeconds);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, snapshot.LastLocalRejection.ObservedAtUtc);
    }

    [Fact]
    public async Task RecordPolicyDecisionAsync_DoesNotRecordAnAcceptedRequestAsARejection()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var policy = NewName("policy");

        await store.RecordPolicyDecisionAsync(Rejected(policy, retryAfterSeconds: 4));
        await store.RecordPolicyDecisionAsync(Accepted(policy));

        var snapshot = await store.ReadAsync();

        // The accepted request must not have replaced the last rejection entry.
        Assert.Equal(policy, snapshot.LastLocalRejection?.PolicyName);
        Assert.Equal(4, snapshot.LastLocalRejection?.RetryAfterSeconds);
    }

    [Fact]
    public async Task RecordProviderResponseAsync_CountsRequestsAndRealRateLimits_AndKeepsTheHeaderSample()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var provider = NewName("provider");

        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            provider, RateLimitCallSources.TwitchHelix, StatusCode: 200,
            RateLimitLimit: "800", RateLimitRemaining: "799", RateLimitReset: "1756500000"));
        clock.Advance(TimeSpan.FromSeconds(5));
        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            provider, RateLimitCallSources.TwitchHelix, StatusCode: 429, RetryAfterSeconds: 42,
            RateLimitLimit: "800", RateLimitRemaining: "0", RateLimitReset: "1756500060"));

        var snapshot = await store.ReadAsync();

        var counters = Assert.Single(snapshot.Providers, p => p.ProviderName == provider);
        Assert.Equal(RateLimitCallSources.TwitchHelix, counters.CallSource);
        Assert.Equal(2, counters.RequestsLastMinute);
        Assert.Equal(2, counters.RequestsLast24Hours);
        Assert.Equal(1, counters.RateLimitedLastMinute);
        Assert.Equal(1, counters.RateLimitedLast24Hours);
        Assert.Equal(42, counters.LastRetryAfterSeconds);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, counters.LastRateLimitedAtUtc);
        Assert.NotNull(counters.LastHeaderSample);
        Assert.Equal("800", counters.LastHeaderSample!.Limit);
        Assert.Equal("0", counters.LastHeaderSample.Remaining);
        Assert.Equal("1756500060", counters.LastHeaderSample.Reset);
    }

    /// <summary>
    /// The leaderboard import source's own call source (spec 2026-09-13, F5): a real store round
    /// trip, not the client's own suppression/isolation contract
    /// (<see cref="EmotePurge.Infrastructure.Tests.Unit.SevenTvApiClientLeaderboardSearchTests"/>'s
    /// job). Proves the new call source lands with its own minute/24 h counters and header sample,
    /// separate from the foreign-channel preview's.
    /// </summary>
    [Fact]
    public async Task RecordProviderResponseAsync_SeparatesSevenTvLeaderboard_FromForeignPreview()
    {
        var clock = NewClock();
        var store = NewStore(clock);

        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            RateLimitProviders.SevenTv, RateLimitCallSources.SevenTvLeaderboard, StatusCode: 200,
            RateLimitLimit: "100", RateLimitRemaining: "97", RateLimitReset: "40"));
        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            RateLimitProviders.SevenTv, RateLimitCallSources.SevenTvLeaderboard, StatusCode: 429, RetryAfterSeconds: 55,
            RateLimitLimit: "100", RateLimitRemaining: "0", RateLimitReset: "55"));
        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            RateLimitProviders.SevenTv, RateLimitCallSources.SevenTvForeignPreview, StatusCode: 200));

        var snapshot = await store.ReadAsync();

        var leaderboard = Assert.Single(snapshot.Providers, p =>
            p.ProviderName == RateLimitProviders.SevenTv && p.CallSource == RateLimitCallSources.SevenTvLeaderboard);
        Assert.Equal(2, leaderboard.RequestsLastMinute);
        Assert.Equal(2, leaderboard.RequestsLast24Hours);
        Assert.Equal(1, leaderboard.RateLimitedLastMinute);
        Assert.Equal(1, leaderboard.RateLimitedLast24Hours);
        Assert.Equal(55, leaderboard.LastRetryAfterSeconds);
        Assert.NotNull(leaderboard.LastHeaderSample);
        Assert.Equal("100", leaderboard.LastHeaderSample!.Limit);
        Assert.Equal("0", leaderboard.LastHeaderSample.Remaining);
        Assert.Equal("55", leaderboard.LastHeaderSample.Reset);

        var preview = Assert.Single(snapshot.Providers, p =>
            p.ProviderName == RateLimitProviders.SevenTv && p.CallSource == RateLimitCallSources.SevenTvForeignPreview);
        Assert.Equal(1, preview.RequestsLast24Hours);
        Assert.Equal(0, preview.RateLimitedLast24Hours);
    }

    [Fact]
    public async Task RecordProviderResponseAsync_SeparatesCallSourcesOfTheSameProvider()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var provider = NewName("provider");

        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            provider, RateLimitCallSources.TwitchHelix, StatusCode: 200));
        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            provider, RateLimitCallSources.TwitchAuth, StatusCode: 200));
        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            provider, RateLimitCallSources.TwitchAuth, StatusCode: 500));

        var snapshot = await store.ReadAsync();

        var helix = Assert.Single(snapshot.Providers, p => p.ProviderName == provider && p.CallSource == RateLimitCallSources.TwitchHelix);
        var auth = Assert.Single(snapshot.Providers, p => p.ProviderName == provider && p.CallSource == RateLimitCallSources.TwitchAuth);
        Assert.Equal(1, helix.RequestsLast24Hours);
        Assert.Equal(2, auth.RequestsLast24Hours);
        // A 500 is a request, not a rate limit: only a real 429 counts as one.
        Assert.Equal(0, auth.RateLimitedLast24Hours);
        Assert.Null(auth.LastRateLimitedAtUtc);
    }

    [Fact]
    public async Task RecordCacheLookupAsync_CountsEachCacheNameSeparately()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var first = NewName("cache");
        var second = NewName("cache");

        await store.RecordCacheLookupAsync(first, hit: true);
        await store.RecordCacheLookupAsync(first, hit: true);
        await store.RecordCacheLookupAsync(first, hit: false);
        await store.RecordCacheLookupAsync(second, hit: false);

        var snapshot = await store.ReadAsync();

        var firstCounters = Assert.Single(snapshot.Caches, c => c.CacheName == first);
        var secondCounters = Assert.Single(snapshot.Caches, c => c.CacheName == second);
        Assert.Equal(2, firstCounters.HitsLast24Hours);
        Assert.Equal(1, firstCounters.MissesLast24Hours);
        Assert.Equal(2, firstCounters.HitsLastMinute);
        Assert.Equal(1, firstCounters.MissesLastMinute);
        Assert.Equal(0, secondCounters.HitsLast24Hours);
        Assert.Equal(1, secondCounters.MissesLast24Hours);
    }

    [Fact]
    public async Task EveryWrittenKey_CarriesATtl_SoTelemetryCannotGrowWithoutBound()
    {
        // The TTL is the whole retention policy: nothing else ever deletes these keys.
        var clock = NewClock();
        var store = NewStore(clock);
        var policy = NewName("policy");

        await store.RecordPolicyDecisionAsync(Rejected(policy, retryAfterSeconds: 2));

        var db = fixture.Connection.GetDatabase();
        var keys = fixture.Connection
            .GetServer(fixture.Connection.GetEndPoints()[0])
            .Keys(pattern: "ratelimit:telemetry:*")
            .ToList();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            var ttl = await db.KeyTimeToLiveAsync(key);
            Assert.True(ttl.HasValue, $"Der Telemetrie-Key '{key}' hat keine TTL.");
        }

        // The day window is only meaningful if its buckets outlive it.
        var dayBucketTtl = await db.KeyTimeToLiveAsync(keys.First(k => k.ToString().Contains(":m:", StringComparison.Ordinal)));
        Assert.True(dayBucketTtl!.Value > TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task AgainstAnUnreachableRedis_WritesStaySilent_AndTheSnapshotReportsUnavailable()
    {
        // Fail-open is an acceptance criterion, not a nicety: telemetry sits in the product path and
        // must never be the reason a request fails. Port 1 on loopback is closed, and abortConnect=false
        // hands back a multiplexer whose every command fails.
        await using var broken = await ConnectionMultiplexer.ConnectAsync(
            "127.0.0.1:1,abortConnect=false,connectTimeout=100,syncTimeout=100,asyncTimeout=100,connectRetry=0");
        var store = new RateLimitTelemetryStore(broken, NewClock(), NullLogger<RateLimitTelemetryStore>.Instance);

        await store.RecordPolicyDecisionAsync(Rejected("InteractiveRead", retryAfterSeconds: 1));
        await store.RecordProviderResponseAsync(new ProviderResponseObservation(
            RateLimitProviders.Twitch, RateLimitCallSources.TwitchHelix, StatusCode: 429, RetryAfterSeconds: 5));
        await store.RecordCacheLookupAsync(RateLimitCacheNames.ModeratedChannels, hit: false);

        var snapshot = await store.ReadAsync();

        Assert.False(snapshot.TelemetryAvailable);
        Assert.Empty(snapshot.Policies);
        Assert.Empty(snapshot.Caches);
        Assert.Empty(snapshot.Providers);
        Assert.Null(snapshot.LastLocalRejection);

        // The one call that must say whether it reached the store: the account deletion retries on false.
        Assert.False(await store.ForgetPartitionAsync("1"));
    }

    [Fact]
    public async Task ForgetPartitionAsync_ClearsTheSlotOfThatPartitionOrItsSubPartitions_AndNoOtherSlot()
    {
        var clock = NewClock();
        var store = NewStore(clock);
        var policy = NewName("policy");

        // A voting rejection partitions by "{userId}:{sessionId}" — still that user's.
        await store.RecordPolicyDecisionAsync(new RateLimitPolicyDecision(policy, false, "POST", "/api/x", "4242:7", 1));
        Assert.True(await store.ForgetPartitionAsync("4242"));
        Assert.Null((await store.ReadAsync()).LastLocalRejection);

        // Nothing stored at all is success too — there is nothing of this partition left.
        Assert.True(await store.ForgetPartitionAsync("4242"));

        // A different user whose id merely starts with the same digits keeps their slot.
        await store.RecordPolicyDecisionAsync(new RateLimitPolicyDecision(policy, false, "POST", "/api/x", "42420", 1));
        Assert.True(await store.ForgetPartitionAsync("4242"));
        Assert.Equal("42420", (await store.ReadAsync()).LastLocalRejection?.Partition);

        // Counters are keyed by policy, never by partition, and stay as they were.
        var counters = Assert.Single((await store.ReadAsync()).Policies, p => p.PolicyName == policy);
        Assert.Equal(2, counters.RejectedLast24Hours);
    }

    private static RateLimitPolicyDecision Accepted(string policy) =>
        new(policy, Accepted: true, "GET", "/api/channels/{name}/emotes", "user:1");

    private static RateLimitPolicyDecision Rejected(string policy, int retryAfterSeconds) =>
        new(policy, Accepted: false, "GET", "/api/channels/{name}/emotes", "user:1", retryAfterSeconds);

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    // A distinct day per test, so the time-indexed buckets of two tests can never overlap even
    // though they share one container.
    private static FakeClock NewClock() =>
        new(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(Interlocked.Increment(ref _clockOffsetDays) * 3));

    private RateLimitTelemetryStore NewStore(TimeProvider clock) =>
        new(fixture.Connection, clock, NullLogger<RateLimitTelemetryStore>.Instance);

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
