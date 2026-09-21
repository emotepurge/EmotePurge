using System.Net;
using System.Net.Http.Headers;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="GuardedSevenTvEditorGrantsService"/> — the grant refresh of the set-centric
/// import's owner check (spec 2026-09-20, section 32, second review round). Every case runs a real
/// <see cref="SevenTvApiClient"/> over a counting handler and a counting request budget: what is
/// counted is what would have left the process, not what a substitute was asked.
/// </summary>
public class GuardedSevenTvEditorGrantsServiceTests
{
    private const string ActorTwitchId = "100";
    private const string EditedTwitchId = "200";

    private const string IdentityAnswer =
        """{"data":{"user_by_connection":{"id":"01ACTOR","connections":[{"platform":"TWITCH","id":"100","emote_set_id":null}]}}}""";

    private const string EditorOfAnswer =
        """{"data":{"user":{"editor_of":[{"user":{"connections":[{"platform":"TWITCH","id":"200","username":"EditedChannel"}]}}]}}}""";

    [Fact]
    public async Task ACacheHit_CostsNoRequestAndNoPermit()
    {
        var chain = new Chain();
        chain.GrantsCache.TryGetSevenTvEditorGrantsAsync(ActorTwitchId, Arg.Any<CancellationToken>())
            .Returns(new SevenTvEditorGrants(new HashSet<string>(), new HashSet<string>(), []));

        var result = await chain.Service.GetEditorGrantsAsync(ActorTwitchId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.Equal(0, chain.Handler.Requests);
        Assert.Equal(0, chain.RequestBudget.Charges);
    }

    /// <summary>
    /// A miss is two upstream requests, and each one pays its own permit. The answer lands in the
    /// grant cache every reader shares, in the shape <see cref="SevenTvEditorService"/> writes.
    /// </summary>
    [Fact]
    public async Task ACacheMiss_ChargesOnePermitPerUpstreamRequest_AndWritesTheSharedGrantCache()
    {
        var chain = new Chain();
        chain.Handler
            .Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityAnswer)
            .Answer(SevenTvGqlRouteHandler.EditorOf, HttpStatusCode.OK, EditorOfAnswer);

        var result = await chain.Service.GetEditorGrantsAsync(ActorTwitchId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        var entry = Assert.Single(result.Grants!.Entries);
        Assert.Equal(new SevenTvEditorGrantEntry("editedchannel", EditedTwitchId), entry);
        Assert.Equal(2, chain.Handler.Requests);
        Assert.Equal(2, chain.RequestBudget.Charges);
        await chain.GrantsCache.Received(1).SetSevenTvEditorGrantsAsync(
            ActorTwitchId,
            Arg.Is<SevenTvEditorGrants>(grants =>
                grants.ChannelLogins.Contains("editedchannel") && grants.TwitchChannelIds.Contains(EditedTwitchId) && grants.Entries.Count == 1),
            Arg.Any<CancellationToken>());
        Assert.Empty(chain.Holds.Writes);
    }

    [Fact]
    public async Task ARefusedPermit_IsUnavailable_SendsNothing_AndIsHeldForThirtySeconds()
    {
        var chain = new Chain(grantCount: 0);
        chain.Handler.Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityAnswer);

        var result = await chain.Service.GetEditorGrantsAsync(ActorTwitchId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        Assert.Equal(0, chain.Handler.Requests);
        Assert.Equal(GuardedSevenTvEditorGrantsService.GuardRefusedTimeToLive, Assert.Single(chain.Holds.Writes).TimeToLive);
    }

    /// <summary>The second request needs a permit of its own; without one it is not sent.</summary>
    [Fact]
    public async Task APermitRefusedForTheSecondRequest_StopsAfterTheFirst()
    {
        var chain = new Chain(grantCount: 1);
        chain.Handler
            .Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityAnswer)
            .Answer(SevenTvGqlRouteHandler.EditorOf, HttpStatusCode.OK, EditorOfAnswer);

        var result = await chain.Service.GetEditorGrantsAsync(ActorTwitchId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        Assert.Equal(1, chain.Handler.CountOf(SevenTvGqlRouteHandler.Identity));
        Assert.Equal(0, chain.Handler.CountOf(SevenTvGqlRouteHandler.EditorOf));
        Assert.Equal(2, chain.RequestBudget.Charges);
    }

    [Fact]
    public async Task AnOpenBreaker_IsUnavailable_WithoutARequestOrAPermit_AndIsHeldForThirtySeconds()
    {
        var chain = new Chain();
        var decision = chain.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.EditorGrants);
        chain.Breaker.RecordFailure(
            ForeignSevenTvBreakerOperations.EditorGrants, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromMinutes(5), decision.Generation);

        var result = await chain.Service.GetEditorGrantsAsync(ActorTwitchId);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        Assert.Equal(0, chain.Handler.Requests);
        Assert.Equal(0, chain.RequestBudget.Charges);
        Assert.Equal(GuardedSevenTvEditorGrantsService.GuardRefusedTimeToLive, Assert.Single(chain.Holds.Writes).TimeToLive);
    }

    /// <summary>
    /// A confirmed rate limit on this path is the provider's, like on every other one: it locks the
    /// list and the preview too, with 7TV's own <c>Retry-After</c>.
    /// </summary>
    [Fact]
    public async Task AConfirmedRateLimit_LocksTheWholeProvider()
    {
        var chain = new Chain();
        chain.Handler.Answer(SevenTvGqlRouteHandler.Identity, () => RateLimitedResponse(TimeSpan.FromSeconds(120)));

        await chain.Service.GetEditorGrantsAsync(ActorTwitchId);

        var preview = chain.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.ForeignPreview);
        Assert.False(preview.Allowed);
        Assert.True(preview.OpenedByRateLimit);
    }

    public static TheoryData<string, Func<HttpResponseMessage>, SevenTvLookupStatus, TimeSpan> HeldOutcomes() => new()
    {
        { "5xx", () => new HttpResponseMessage(HttpStatusCode.BadGateway), SevenTvLookupStatus.Unavailable, TimeSpan.FromSeconds(60) },
        {
            "errors only",
            () => Json("""{"data":null,"errors":[{"message":"boom"}]}"""),
            SevenTvLookupStatus.Unavailable,
            TimeSpan.FromSeconds(60)
        },
        { "429 without a hint", () => RateLimitedResponse(null), SevenTvLookupStatus.Unavailable, TimeSpan.FromSeconds(60) },
        { "429 with a short hint", () => RateLimitedResponse(TimeSpan.FromSeconds(5)), SevenTvLookupStatus.Unavailable, TimeSpan.FromSeconds(60) },
        { "429 with a long hint", () => RateLimitedResponse(TimeSpan.FromSeconds(600)), SevenTvLookupStatus.Unavailable, TimeSpan.FromSeconds(600) },
        {
            "429 in the body",
            () => Json("""{"data":null,"errors":[{"message":"slow down","extensions":{"status":429}}]}"""),
            SevenTvLookupStatus.Unavailable,
            TimeSpan.FromSeconds(60)
        },
        {
            "no 7TV account",
            () => Json("""{"data":{"user_by_connection":{"id":"00000000000000000000000000","connections":[]}}}"""),
            SevenTvLookupStatus.NoSevenTvAccount,
            TimeSpan.FromSeconds(60)
        },
    };

    /// <summary>
    /// Every unsuccessful refresh is held for its shelf-life, and a second report inside it sends
    /// nothing — neither a request nor a permit.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeldOutcomes))]
    public async Task AnUnsuccessfulRefresh_IsHeld_AndTheNextReportSendsNothing(
        string outcome, Func<HttpResponseMessage> identityAnswer, SevenTvLookupStatus expectedStatus, TimeSpan expectedTimeToLive)
    {
        _ = outcome;
        var chain = new Chain();
        chain.Handler.Answer(SevenTvGqlRouteHandler.Identity, identityAnswer);

        var first = await chain.Service.GetEditorGrantsAsync(ActorTwitchId);
        var requestsAfterFirst = chain.Handler.Requests;
        var permitsAfterFirst = chain.RequestBudget.Charges;
        var second = await chain.Service.GetEditorGrantsAsync(ActorTwitchId);

        Assert.Equal(expectedStatus, first.Status);
        Assert.Equal(expectedStatus, second.Status);
        var hold = Assert.Single(chain.Holds.Writes);
        Assert.Equal(expectedStatus, hold.Status);
        Assert.Equal(expectedTimeToLive, hold.TimeToLive);
        Assert.Equal(requestsAfterFirst, chain.Handler.Requests);
        Assert.Equal(permitsAfterFirst, chain.RequestBudget.Charges);
    }

    /// <summary>
    /// Redis gone: neither the grant cache nor the hold can answer (reads throw; a write that seems
    /// to land is never read back), and every report
    /// tries again — but the breaker still stands between those attempts and 7TV, so a stream of
    /// reports against a 7TV outage stops at the failure threshold instead of running on.
    /// </summary>
    [Fact]
    public async Task ARedisOutage_FailsOpen_AndTheBreakerStillBoundsTheRequests()
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("down"));
        var configuration = new ConfigurationBuilder().Build();

        var chain = new Chain(
            grantsCache: new ModRoleCache(redis, configuration, NullLogger<ModRoleCache>.Instance),
            holds: new SevenTvEditorGrantsHoldCache(redis, NullLogger<SevenTvEditorGrantsHoldCache>.Instance));

        for (var report = 0; report < 20; report++)
        {
            Assert.Equal(SevenTvLookupStatus.Unavailable, (await chain.Service.GetEditorGrantsAsync(ActorTwitchId)).Status);
        }

        Assert.Equal(ForeignSevenTvBreakerPolicy.FailureThreshold, chain.Handler.Requests);
    }

    /// <summary>
    /// Reports that arrive together while the first attempt is still out: the two slots let two
    /// attempts through, and everyone queued behind them takes the answer those two held.
    /// </summary>
    [Fact]
    public async Task ABurstOfConcurrentReports_IsBoundedByTheSlots_NotByTheBurst()
    {
        var chain = new Chain();
        chain.Handler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reports = Enumerable.Range(0, 10).Select(_ => chain.Service.GetEditorGrantsAsync(ActorTwitchId)).ToList();
        while (chain.Handler.Requests < ForeignEmoteSetProviderBudget.MaxConcurrent)
        {
            await Task.Delay(10);
        }

        chain.Handler.Gate.SetResult();
        var results = await Task.WhenAll(reports);

        Assert.All(results, result => Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status));
        Assert.Equal(ForeignEmoteSetProviderBudget.MaxConcurrent, chain.Handler.Requests);
    }

    /// <summary>
    /// The other readers stay where they were (spec section 32, second round, item 5): the
    /// unguarded <see cref="SevenTvEditorService"/> behind authorization, the picker and the overview
    /// takes no permit and asks no breaker — a budget that refuses everything does not stop it.
    /// </summary>
    [Fact]
    public async Task TheAuthorizationPathsGrantLookup_DoesNotGoThroughTheGuard()
    {
        var chain = new Chain(grantCount: 0);
        chain.Handler
            .Answer(SevenTvGqlRouteHandler.Identity, HttpStatusCode.OK, IdentityAnswer)
            .Answer(SevenTvGqlRouteHandler.EditorOf, HttpStatusCode.OK, EditorOfAnswer);
        var decision = chain.Breaker.TryAcquire(ForeignSevenTvBreakerOperations.EditorGrants);
        chain.Breaker.RecordFailure(
            ForeignSevenTvBreakerOperations.EditorGrants, ForeignSevenTvBreakerOutcome.RateLimited, TimeSpan.FromMinutes(5), decision.Generation);
        await chain.Holds.SetAsync(ActorTwitchId, SevenTvLookupStatus.Unavailable, TimeSpan.FromMinutes(1));
        var unguarded = new SevenTvEditorService(
            chain.Client, chain.GrantsCache, new RecordingRateLimitTelemetry(), NullLogger<SevenTvEditorService>.Instance);

        var result = await unguarded.GetEditorGrantsAsync(ActorTwitchId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.Equal(2, chain.Handler.Requests);
        Assert.Equal(0, chain.RequestBudget.Charges);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage RateLimitedResponse(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(string.Empty) };
        if (retryAfter is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }

        return response;
    }

    /// <summary>The real service over a real client; only the caches, the handler and the permit counter are fakes.</summary>
    private sealed class Chain
    {
        public Chain(int grantCount = int.MaxValue, IModRoleCache? grantsCache = null, ISevenTvEditorGrantsHoldCache? holds = null)
        {
            RequestBudget = new RecordingForeignUpstreamRequestBudget(grantCount);
            Client = new SevenTvApiClient(
                new HttpClient(Handler) { BaseAddress = new Uri("https://7tv.io/v3/") },
                new RecordingRateLimitTelemetry(),
                RequestBudget,
                new RecordingLogger<SevenTvApiClient>());
            GrantsCache = grantsCache ?? Substitute.For<IModRoleCache>();
            var holdCache = holds ?? Holds;
            Service = new GuardedSevenTvEditorGrantsService(
                Client,
                GrantsCache,
                holdCache,
                Breaker,
                new ForeignEmoteSetProviderBudget(),
                new RecordingRateLimitTelemetry(),
                NullLogger<GuardedSevenTvEditorGrantsService>.Instance);
        }

        public SevenTvGqlRouteHandler Handler { get; } = new();

        public RecordingForeignUpstreamRequestBudget RequestBudget { get; }

        public ForeignSevenTvBreakerPolicy Breaker { get; } = new();

        public InMemoryEditorGrantsHoldCache Holds { get; } = new();

        public SevenTvApiClient Client { get; }

        public IModRoleCache GrantsCache { get; }

        public GuardedSevenTvEditorGrantsService Service { get; }
    }
}
