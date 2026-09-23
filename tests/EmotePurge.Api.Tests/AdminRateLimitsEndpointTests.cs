using System.Net;
using System.Reflection;
using System.Text.Json;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The read-only rate-limit snapshot, <c>GET /api/admin/rate-limits</c> (Task 16 of the #33 plan).
/// </summary>
/// <remarks>
/// Two contracts are pinned here at once, and neither may hide the other: the *effective
/// configuration*, which comes from <see cref="RateLimitingOptions"/> and is always complete, and the
/// *counters*, which come from <see cref="IRateLimitTelemetryReader"/> and are only ever a partial
/// view — either because Redis is unreachable (<c>telemetryAvailable: false</c>) or because a policy
/// simply saw no traffic inside the retained window. Both cases must still list every policy the app
/// registers, with zero counters rather than a missing row: a policy that disappears from the page the
/// moment it goes quiet is indistinguishable from a policy that was never wired up.
/// </remarks>
public class AdminRateLimitsEndpointTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/admin/rate-limits";

    private readonly ApiFactory _factory;

    public AdminRateLimitsEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ChannelAccess.ClearReceivedCalls();
    }

    [Fact]
    public async Task Get_ReturnsConfigurationAndCounters_WhenTelemetryIsAvailable()
    {
        var reader = Substitute.For<IRateLimitTelemetryReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(new RateLimitTelemetrySnapshot(
            TelemetryAvailable: true,
            Policies:
            [
                new RateLimitPolicyCounters(RateLimitPolicyNames.ChannelResync, AcceptedLastMinute: 3, RejectedLastMinute: 1, AcceptedLast24Hours: 30, RejectedLast24Hours: 2),
            ],
            LastLocalRejection: new RateLimitLastRejection(
                new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc),
                "POST",
                "/api/channels/{channelName}/resync",
                RateLimitPolicyNames.ChannelResync,
                "user:42",
                30),
            Caches:
            [
                new RateLimitCacheCounters(RateLimitCacheNames.ModeratedChannels, HitsLastMinute: 5, MissesLastMinute: 1, HitsLast24Hours: 100, MissesLast24Hours: 4),
            ],
            Providers:
            [
                new RateLimitProviderCounters(
                    RateLimitProviders.Twitch,
                    RateLimitCallSources.TwitchHelix,
                    RequestsLastMinute: 12,
                    RequestsLast24Hours: 900,
                    RateLimitedLastMinute: 0,
                    RateLimitedLast24Hours: 0,
                    LastRetryAfterSeconds: null,
                    LastRateLimitedAtUtc: null,
                    LastHeaderSample: new ProviderRateLimitHeaderSample(
                        new DateTime(2026, 8, 30, 11, 59, 0, DateTimeKind.Utc), "800", "799", "60")),
            ]));

        var (statusCode, body) = await SendAsAdminAsync(reader);
        using var bodyDisposal = body;

        Assert.Equal(HttpStatusCode.OK, statusCode);
        var root = body.RootElement;

        Assert.True(root.GetProperty("telemetryAvailable").GetBoolean());

        var policies = root.GetProperty("policies").EnumerateArray().ToList();
        // Every registered policy shows up, not only the one with traffic.
        Assert.Equal(8, policies.Count);

        var resync = policies.Single(p => p.GetProperty("name").GetString() == RateLimitPolicyNames.ChannelResync);
        Assert.Equal("fixed-window", resync.GetProperty("type").GetString());
        Assert.Equal(3, resync.GetProperty("acceptedLastMinute").GetInt64());
        Assert.Equal(1, resync.GetProperty("rejectedLastMinute").GetInt64());
        Assert.Equal(30, resync.GetProperty("acceptedLast24Hours").GetInt64());
        Assert.Equal(2, resync.GetProperty("rejectedLast24Hours").GetInt64());
        Assert.Equal("twitch-user", resync.GetProperty("partition").GetString());
        Assert.Equal(0, resync.GetProperty("queueLimit").GetInt32());

        var voting = policies.Single(p => p.GetProperty("name").GetString() == RateLimitPolicyNames.Voting);
        Assert.Equal("token-bucket", voting.GetProperty("type").GetString());
        Assert.Equal("twitch-user+vote-session", voting.GetProperty("partition").GetString());

        var publicHealth = policies.Single(p => p.GetProperty("name").GetString() == RateLimitPolicyNames.PublicHealth);
        Assert.Equal("remote-ip", publicHealth.GetProperty("partition").GetString());

        var lastRejection = root.GetProperty("lastLocalRejection");
        Assert.Equal(RateLimitPolicyNames.ChannelResync, lastRejection.GetProperty("policyName").GetString());
        Assert.Equal("user:42", lastRejection.GetProperty("partition").GetString());
        Assert.Equal(30, lastRejection.GetProperty("retryAfterSeconds").GetInt32());

        var cache = Assert.Single(root.GetProperty("caches").EnumerateArray());
        Assert.Equal(RateLimitCacheNames.ModeratedChannels, cache.GetProperty("cacheName").GetString());
        Assert.Equal(5, cache.GetProperty("hitsLastMinute").GetInt64());

        var provider = Assert.Single(root.GetProperty("providers").EnumerateArray());
        Assert.Equal(RateLimitProviders.Twitch, provider.GetProperty("providerName").GetString());
        Assert.Equal(RateLimitCallSources.TwitchHelix, provider.GetProperty("callSource").GetString());
        Assert.Equal(12, provider.GetProperty("requestsLastMinute").GetInt64());
        // Deliberately absent: there is no defensible denominator for a percentage, and the spec is
        // explicit that this round never invents one.
        Assert.False(provider.TryGetProperty("percentage", out _));
        var headerSample = provider.GetProperty("lastHeaderSample");
        Assert.Equal("800", headerSample.GetProperty("limit").GetString());
    }

    /// <summary>
    /// The edge case Task 14 flagged explicitly: a policy nobody hit inside the retention window is
    /// not the same thing as a policy that does not exist. It must still appear, fully configured,
    /// with zero counters — because the counters are derived from configuration, never the reverse.
    /// </summary>
    [Fact]
    public async Task Get_ListsAPolicyWithoutTraffic_WithFullConfigurationAndZeroCounters()
    {
        var reader = Substitute.For<IRateLimitTelemetryReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(
            new RateLimitTelemetrySnapshot(true, [], null, [], []));

        var (_, body) = await SendAsAdminAsync(reader);
        using var bodyDisposal = body;

        var interactiveRead = body.RootElement.GetProperty("policies").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == RateLimitPolicyNames.InteractiveRead);

        Assert.Equal("token-bucket", interactiveRead.GetProperty("type").GetString());
        Assert.Equal(300, interactiveRead.GetProperty("capacity").GetInt32());
        Assert.Equal(5, interactiveRead.GetProperty("tokensPerPeriod").GetInt32());
        Assert.Equal(1, interactiveRead.GetProperty("replenishmentPeriodSeconds").GetInt32());
        Assert.Equal(0, interactiveRead.GetProperty("acceptedLastMinute").GetInt64());
        Assert.Equal(0, interactiveRead.GetProperty("rejectedLastMinute").GetInt64());
        Assert.Equal(0, interactiveRead.GetProperty("acceptedLast24Hours").GetInt64());
        Assert.Equal(0, interactiveRead.GetProperty("rejectedLast24Hours").GetInt64());
    }

    [Fact]
    public async Task Get_ReturnsPartial200_WithFullPolicyConfiguration_WhenTheStoreIsUnavailable()
    {
        var reader = Substitute.For<IRateLimitTelemetryReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(RateLimitTelemetrySnapshot.Unavailable);

        var (statusCode, body) = await SendAsAdminAsync(reader);
        using var bodyDisposal = body;

        Assert.Equal(HttpStatusCode.OK, statusCode);
        var root = body.RootElement;

        Assert.False(root.GetProperty("telemetryAvailable").GetBoolean());
        // The effective configuration is unaffected by a Redis outage — it comes from options, not
        // from the counter store.
        var policies = root.GetProperty("policies").EnumerateArray().ToList();
        Assert.Equal(8, policies.Count);
        Assert.All(policies, p =>
        {
            Assert.Equal(0, p.GetProperty("acceptedLastMinute").GetInt64());
            Assert.Equal(0, p.GetProperty("rejectedLastMinute").GetInt64());
        });
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastLocalRejection").ValueKind);
        Assert.Empty(root.GetProperty("caches").EnumerateArray());
        Assert.Empty(root.GetProperty("providers").EnumerateArray());
    }

    [Fact]
    public async Task Get_ReflectsAnOverriddenBudget_FromConfiguration()
    {
        var reader = Substitute.For<IRateLimitTelemetryReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(RateLimitTelemetrySnapshot.Unavailable);

        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);

        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:InteractiveRead:TokenLimit", "777");
            builder.ConfigureTestServices(services => services.AddSingleton(reader));
        });
        using var client = factory.CreateClient();

        var (_, body) = await SendAsync(client);
        using var bodyDisposal = body;

        var interactiveRead = body.RootElement.GetProperty("policies").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == RateLimitPolicyNames.InteractiveRead);

        Assert.Equal(777, interactiveRead.GetProperty("capacity").GetInt32());
    }


    /// <summary>
    /// The failure this file's own remarks describe — "a policy that disappears from the page is
    /// indistinguishable from one that was never wired up" — happened for real: the foreign-channel
    /// preview's <c>ForeignEmoteLookup</c> policy guarded a live route while the admin snapshot, built
    /// solely from the descriptor list, showed no trace of it (AK 15). Pinned by comparing against
    /// <see cref="RateLimitPolicyNames"/> itself rather than by counting rows, so the next policy
    /// added without a descriptor fails here instead of shipping invisible.
    /// </summary>
    [Fact]
    public async Task Get_ListsEveryPolicyNameTheAppKnows_NoneMissingFromTheDescriptorList()
    {
        var reader = Substitute.For<IRateLimitTelemetryReader>();
        reader.ReadAsync(Arg.Any<CancellationToken>()).Returns(RateLimitTelemetrySnapshot.Unavailable);

        var (_, body) = await SendAsAdminAsync(reader);
        using var bodyDisposal = body;

        var expected = typeof(RateLimitPolicyNames)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false })
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToList();
        var listed = body.RootElement.GetProperty("policies").EnumerateArray()
            .Select(policy => policy.GetProperty("name").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, listed);

        var foreignLookup = body.RootElement.GetProperty("policies").EnumerateArray()
            .Single(policy => policy.GetProperty("name").GetString() == RateLimitPolicyNames.ForeignEmoteLookup);
        Assert.Equal("fixed-window", foreignLookup.GetProperty("type").GetString());
        Assert.Equal(10, foreignLookup.GetProperty("capacity").GetInt32());
        Assert.Equal("twitch-user", foreignLookup.GetProperty("partition").GetString());
    }

    /// <summary>
    /// A host of its own per call, like the sibling rate-limit test files: a substituted
    /// <see cref="IRateLimitTelemetryReader"/> is host state and must not leak between test methods
    /// that share the class fixture's underlying factory.
    /// </summary>
    private async Task<(HttpStatusCode StatusCode, JsonDocument Body)> SendAsAdminAsync(IRateLimitTelemetryReader reader)
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);

        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(reader)));
        using var client = factory.CreateClient();

        return await SendAsync(client);
    }

    /// <summary>
    /// Buffers the body before the caller's <c>using</c>-scoped client can be disposed — reading
    /// <see cref="HttpResponseMessage.Content"/> after that point is unreliable against the in-memory
    /// <c>TestServer</c> transport.
    /// </summary>
    private static async Task<(HttpStatusCode StatusCode, JsonDocument Body)> SendAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someadmin");

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text));
    }
}
