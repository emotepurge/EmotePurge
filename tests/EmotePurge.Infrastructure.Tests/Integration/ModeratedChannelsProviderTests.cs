using System.Collections.Concurrent;
using System.Text.Json;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Redis is real (redis:7.2-alpine via RedisFixture) because the cache contract under test is the
// actual key/TTL/payload wiring; Helix and the token service are substituted at the outgoing
// boundary. Every test uses its own Twitch user id, so the shared container and the process-wide
// single-flight gate cannot leak state between them.
[Collection("Redis")]
public class ModeratedChannelsProviderTests(RedisFixture fixture)
{
    [Fact]
    public async Task GetModeratedChannelsAsync_PaginatesOnce_AndServesTheSecondCallFromRedis()
    {
        const string userId = "modlist-user-hit";
        var helix = HelixReturning(userId, new TwitchModeratedChannelInfo("HandOfBlood", "111"));
        var provider = CreateProvider(helix, TokenService("token"));

        var first = await provider.GetModeratedChannelsAsync(Principal(userId));
        var second = await provider.GetModeratedChannelsAsync(Principal(userId));

        Assert.NotNull(first.Channels);
        Assert.NotNull(second.Channels);
        // Normalized on write (Regel 9): Helix answers with the display-cased login here.
        Assert.Equal("handofblood", Assert.Single(second.Channels!).Login);
        Assert.Equal("111", second.Channels![0].BroadcasterId);
        await helix.Received(1).GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>());

        var stored = await fixture.Connection.GetDatabase().StringGetAsync(Key(userId));
        Assert.False(stored.IsNullOrEmpty);
        var ttl = await fixture.Connection.GetDatabase().KeyTimeToLiveAsync(Key(userId));
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_MergesConcurrentMisses_IntoASinglePagination()
    {
        // The second caller is only started once the first is confirmed to be inside Helix, so a
        // provider without the single-flight gate would deterministically produce a second
        // pagination during the wait below (falsification-checked by removing the gate).
        const string userId = "modlist-user-singleflight";
        var callCount = 0;
        var firstCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCallEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>())
            .Returns(_ => FetchAsync());

        var provider = CreateProvider(helix, TokenService("token"));

        var firstCall = Task.Run(() => provider.GetModeratedChannelsAsync(Principal(userId)));
        await firstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondCall = Task.Run(() => provider.GetModeratedChannelsAsync(Principal(userId)));

        // Nothing to await positively here: the assertion is that the second caller does *not*
        // reach Helix, so the wait is bounded by a timeout and cut short if it ever does.
        await Task.WhenAny(secondCallEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        release.SetResult();

        var results = await Task.WhenAll(firstCall, secondCall);

        Assert.Equal(1, callCount);
        await helix.Received(1).GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>());
        Assert.All(results, result => Assert.Equal("streamer", Assert.Single(result.Channels!).Login));

        async Task<IReadOnlyList<TwitchModeratedChannelInfo>?> FetchAsync()
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstCallEntered.SetResult();
            }
            else
            {
                secondCallEntered.TrySetResult();
            }

            await release.Task;
            return [new TwitchModeratedChannelInfo("streamer", "222")];
        }
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_DoesNotCacheATransientHelixFailure()
    {
        const string userId = "modlist-user-helix-null";
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TwitchModeratedChannelInfo>?)null);
        var provider = CreateProvider(helix, TokenService("token"));

        var first = await provider.GetModeratedChannelsAsync(Principal(userId));

        Assert.Null(first.Channels);
        Assert.False(await fixture.Connection.GetDatabase().KeyExistsAsync(Key(userId)));

        // The next call must retry live rather than inherit the failure.
        await provider.GetModeratedChannelsAsync(Principal(userId));
        await helix.Received(2).GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_WithoutUsableToken_ReportsReauthAndSkipsHelixAndCache()
    {
        const string userId = "modlist-user-no-token";
        var helix = Substitute.For<ITwitchHelixClient>();
        var provider = CreateProvider(helix, TokenService(null, reauthRequired: true));

        var result = await provider.GetModeratedChannelsAsync(Principal(userId));

        Assert.Null(result.Channels);
        Assert.True(result.ReauthRequired);
        await helix.DidNotReceive().GetModeratedChannelsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.False(await fixture.Connection.GetDatabase().KeyExistsAsync(Key(userId)));
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_CachesAnEmptyResult_AsEmptyRatherThanUnavailable()
    {
        // "Moderates nothing" and "could not be determined" must not collapse into the same
        // payload — a user who moderates no channel would otherwise pay a Helix round trip on
        // every single request.
        const string userId = "modlist-user-empty";
        var helix = HelixReturning(userId);
        var provider = CreateProvider(helix, TokenService("token"));

        var first = await provider.GetModeratedChannelsAsync(Principal(userId));
        var second = await provider.GetModeratedChannelsAsync(Principal(userId));

        Assert.NotNull(first.Channels);
        Assert.Empty(first.Channels!);
        Assert.NotNull(second.Channels);
        Assert.Empty(second.Channels!);
        Assert.True(await fixture.Connection.GetDatabase().KeyExistsAsync(Key(userId)));
        await helix.Received(1).GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_PaginatesAgain_AfterTheCacheKeyIsRemoved()
    {
        // Stands in for both the TTL expiring and the admin invalidation path deleting the key.
        const string userId = "modlist-user-invalidated";
        var helix = HelixReturning(userId, new TwitchModeratedChannelInfo("streamer", "333"));
        var provider = CreateProvider(helix, TokenService("token"));

        await provider.GetModeratedChannelsAsync(Principal(userId));
        await fixture.Connection.GetDatabase().KeyDeleteAsync(Key(userId));
        var afterInvalidation = await provider.GetModeratedChannelsAsync(Principal(userId));

        Assert.Equal("streamer", Assert.Single(afterInvalidation.Channels!).Login);
        await helix.Received(2).GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_TreatsAnUnreadablePayload_AsAMiss()
    {
        const string userId = "modlist-user-broken-payload";
        await fixture.Connection.GetDatabase().StringSetAsync(Key(userId), "not-json");
        var helix = HelixReturning(userId, new TwitchModeratedChannelInfo("streamer", "444"));
        var provider = CreateProvider(helix, TokenService("token"));

        var result = await provider.GetModeratedChannelsAsync(Principal(userId));

        Assert.Equal("streamer", Assert.Single(result.Channels!).Login);
        await helix.Received(1).GetModeratedChannelsAsync(Arg.Any<string>(), userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetModeratedChannelsAsync_OnACacheHit_NeverAsksForAToken()
    {
        // Accepted staleness: a broken refresh token only surfaces on the next miss.
        const string userId = "modlist-user-cache-hit-token";
        var helix = HelixReturning(userId, new TwitchModeratedChannelInfo("streamer", "555"));
        var tokens = TokenService("token");
        var provider = CreateProvider(helix, tokens);

        await provider.GetModeratedChannelsAsync(Principal(userId));
        tokens.ClearReceivedCalls();
        var cached = await provider.GetModeratedChannelsAsync(Principal(userId));

        Assert.False(cached.ReauthRequired);
        await tokens.DidNotReceive().GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoredPayload_KeepsLoginAndBroadcasterId()
    {
        const string userId = "modlist-user-payload";
        var helix = HelixReturning(userId, new TwitchModeratedChannelInfo("Streamer", "666"));
        var provider = CreateProvider(helix, TokenService("token"));

        await provider.GetModeratedChannelsAsync(Principal(userId));

        var stored = (string?)await fixture.Connection.GetDatabase().StringGetAsync(Key(userId));
        using var document = JsonDocument.Parse(stored!);
        var entry = Assert.Single(document.RootElement.EnumerateArray().ToList());
        Assert.Equal("streamer", entry.GetProperty("login").GetString());
        Assert.Equal("666", entry.GetProperty("broadcasterId").GetString());
    }

    private static string Key(string twitchUserId) => $"modlist:{twitchUserId}";

    private static TwitchPrincipalInfo Principal(string twitchUserId) =>
        new(twitchUserId, "someuser", "cookie-token");

    private static ITwitchHelixClient HelixReturning(string twitchUserId, params TwitchModeratedChannelInfo[] channels)
    {
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetModeratedChannelsAsync(Arg.Any<string>(), twitchUserId, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TwitchModeratedChannelInfo>?>(_ => [.. channels]);
        return helix;
    }

    /// <summary>
    /// The hit rate the admin page reports, measured where it is produced. This cache is the one that
    /// removes a Helix pagination from the request path, so its hit rate is the number that says
    /// whether the provider cost was actually taken out — and a counter wired to the wrong side of the
    /// read would report a flawless 100 % forever.
    /// </summary>
    [Fact]
    public async Task GetModeratedChannelsAsync_CountsTheCacheLookup_AsAMissThenAHit()
    {
        const string userId = "modlist-user-telemetry";
        var telemetry = new RecordingTelemetry();
        var provider = CreateProvider(
            HelixReturning(userId, new TwitchModeratedChannelInfo("HandOfBlood", "111")),
            TokenService("token"),
            telemetry);

        await provider.GetModeratedChannelsAsync(Principal(userId));
        await provider.GetModeratedChannelsAsync(Principal(userId));

        // Three, not two: the miss looks a second time behind the single-flight gate, and that second
        // look is a real lookup which simply did not hit either.
        Assert.Equal(
            [(RateLimitCacheNames.ModeratedChannels, false), (RateLimitCacheNames.ModeratedChannels, false), (RateLimitCacheNames.ModeratedChannels, true)],
            telemetry.Lookups);
    }

    private static ITwitchUserTokenService TokenService(string? accessToken, bool reauthRequired = false)
    {
        var tokens = Substitute.For<ITwitchUserTokenService>();
        tokens.GetValidAccessTokenAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(new TwitchUserTokenResult(accessToken, reauthRequired));
        return tokens;
    }

    private ModeratedChannelsProvider CreateProvider(
        ITwitchHelixClient helix,
        ITwitchUserTokenService tokens,
        IRateLimitTelemetry? telemetry = null) =>
        new(
            tokens,
            helix,
            fixture.Connection,
            BuildConfiguration(),
            telemetry ?? Substitute.For<IRateLimitTelemetry>(),
            NullLogger<ModeratedChannelsProvider>.Instance);

    /// <summary>Collects the cache lookups the provider reports; every other call is unused here.</summary>
    private sealed class RecordingTelemetry : IRateLimitTelemetry
    {
        private readonly ConcurrentQueue<(string CacheName, bool Hit)> _lookups = new();

        public IReadOnlyList<(string CacheName, bool Hit)> Lookups => _lookups.ToList();

        public Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default)
        {
            _lookups.Enqueue((cacheName, hit));
            return Task.CompletedTask;
        }

        public Task<bool> ForgetPartitionAsync(string partition, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private static IConfiguration BuildConfiguration(int ttlMinutes = 10) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ModCheckCacheTtlMinutes"] = ttlMinutes.ToString() })
            .Build();
}
