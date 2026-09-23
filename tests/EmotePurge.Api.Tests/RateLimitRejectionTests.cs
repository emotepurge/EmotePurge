using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The rejected half of the rate limiter, over the real Program.cs pipeline. Until 2026-08-29 there
/// was none: every policy answered a bare 429 — no body, no Retry-After, no log line — so a
/// throttled user got the frontend's generic status message and the server side of the story did not
/// exist at all. Finding out that issues #33/#35 were self-inflicted took two rounds and a read of
/// the production nginx access log; that is what these three assertions are meant to prevent.
/// </summary>
/// <remarks>
/// <para>
/// The budget is no longer mirrored from Program.cs but overridden through configuration, which the
/// policies now read from <see cref="RateLimitingOptions"/>. Two things follow. The suite no longer
/// carries a copy of a production number that goes stale the moment the number moves — the earlier
/// version spent eighty-one requests per case because it had to out-run a forty-permit window. And
/// the override itself is the assertion that environment configuration really steers the policies:
/// if binding broke, these tests would never see a 429 at all.
/// </para>
/// <para>
/// The exhausted route is the manual resync, not because resync is interesting here but because it
/// is the route whose policy already reads from the options — and it does so on both sides of the
/// route rehang, so this contract does not move when the rest of the policies do.
/// </para>
/// <para>
/// The last two cases cover the token-bucket half of the limiter, which the three above cannot reach:
/// a fixed window and a bucket answer <c>Retry-After</c> from different sources, and the Voting policy
/// is the only one in the app whose partition is not simply "one user". Both were written the day the
/// buckets were first put on real routes, because until then they were registered code no test touched.
/// </para>
/// </remarks>
public class RateLimitRejectionTests : IClassFixture<ApiFactory>
{
    /// <summary>
    /// A budget small enough to spend in two requests, and far below the configured default — a run
    /// that ignored the override would therefore not reject inside this test's loop at all, which is
    /// what makes the override itself an assertion rather than a decoration.
    /// </summary>
    private const int TestPermitLimit = 2;

    /// <summary>
    /// The exhausted route. Its own authorization filter answers 403 long before the handler, so
    /// nothing here touches the database, Redis or the resync cooldown — and the cooldown's own 429,
    /// the one other 429 this route can produce, is unreachable. The two are told apart by errorCode
    /// regardless.
    /// </summary>
    private const string ResyncPath = "/api/channels/testchannel/resync";

    /// <summary>
    /// The accepted route: both services its handler resolves are substituted in ApiFactory, so it
    /// answers a plain 200 and shows what an untouched response looks like. Since the route rehang it
    /// is also the cheapest reachable <c>InteractiveRead</c> route, which makes it the one the token
    /// bucket is exhausted on below.
    /// </summary>
    private const string PermissionsPath = "/api/channels/testchannel/permissions";

    /// <summary>Capacity of the token bucket under test — three requests, then empty.</summary>
    private const int TestTokenLimit = 3;

    /// <summary>
    /// Deliberately neither 1 nor 60: a period of one second could be refilled mid-loop and make the
    /// exhaustion flaky, and 60 is exactly the fixed window's fallback, so a bucket that wrongly
    /// reported the window would still look right. Seven can only have come from the bucket.
    /// </summary>
    private const int TestReplenishmentPeriodSeconds = 7;

    /// <summary>Capacity of the Voting bucket under test — two mutations per (user, session).</summary>
    private const int TestVoteTokenLimit = 2;

    private readonly ApiFactory _factory;

    public RateLimitRejectionTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ExhaustedBudget_Answers429_WithRetryAfterAndAnErrorCode()
    {
        using var factory = CreateThrottledFactory();
        using var client = factory.CreateClient();

        // A partition key of this test's own: the limiter partitions by the NameIdentifier claim, so
        // sharing one across tests would make the outcome depend on execution order.
        using var response = await ExhaustAsync(client, "rate-limit-contract");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        var retryAfterSeconds = int.Parse(retryAfter);
        // Never zero (a client told to retry after zero seconds retries straight into the next
        // rejection) and never longer than the one-minute window this policy lives in.
        Assert.InRange(retryAfterSeconds, 1, 60);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ApiErrorCodes.RateLimitExceeded,
            body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(retryAfterSeconds, body.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task RequestWithinTheBudget_IsUntouched()
    {
        // The other direction: OnRejected must not run for an accepted request, or every response in
        // the app would carry a Retry-After telling clients to back off from nothing. Deliberately on
        // the shared fixture with its configured production budget, not on the throttled host.
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, PermissionsPath, "rate-limit-headroom");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Rejection_IsLoggedWithPolicyPathAndPartition()
    {
        var log = new CapturingLoggerProvider();
        using var factory = CreateThrottledFactory(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(log)));
        using var client = factory.CreateClient();

        using var response = await ExhaustAsync(client, "rate-limit-log");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var entry = Assert.Single(log.Entries, e => e.Category == RateLimitRejection.LogCategory);
        Assert.Equal(LogLevel.Warning, entry.Level);
        // The three things the production investigation had to reconstruct from nginx: which policy,
        // which path, whose budget.
        Assert.Contains(RateLimitPolicyNames.ChannelResync, entry.Message);
        Assert.Contains(ResyncPath, entry.Message);
        Assert.Contains("rate-limit-log", entry.Message);
    }

    /// <summary>
    /// The token bucket's own rejection contract. A bucket refills continuously, so the wait it owes a
    /// caller is its replenishment period, not the minute a fixed window owes — and OnRejectedAsync has
    /// two ways to arrive at that number (the lease's own RetryAfter metadata, or the fallback each
    /// partitioner records for its limiter). Both resolve to the configured period here, and the
    /// assertion is that the answer is the period and not the window: a bucket that told a caller to
    /// wait sixty seconds for a token returning in seven is the exact over-correction issues #33/#35
    /// were about.
    /// </summary>
    [Fact]
    public async Task ExhaustedTokenBucket_ReportsItsReplenishmentPeriod_NotTheFixedWindow()
    {
        using var factory = CreateFactory(new Dictionary<string, string>
        {
            ["RateLimiting:InteractiveRead:TokenLimit"] = TestTokenLimit.ToString(),
            // One token per period, so the bucket cannot hand out a second budget mid-loop.
            ["RateLimiting:InteractiveRead:TokensPerPeriod"] = "1",
            ["RateLimiting:InteractiveRead:ReplenishmentPeriodSeconds"] = TestReplenishmentPeriodSeconds.ToString(),
        });
        using var client = factory.CreateClient();
        const string userId = "rate-limit-bucket";

        using var response = await ExhaustAsync(
            () => SendAsync(client, HttpMethod.Get, PermissionsPath, userId),
            TestTokenLimit + 1);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        var retryAfterSeconds = int.Parse(retryAfter);
        Assert.Equal(TestReplenishmentPeriodSeconds, retryAfterSeconds);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ApiErrorCodes.RateLimitExceeded,
            body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(retryAfterSeconds, body.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    /// <summary>
    /// The Voting policy's partition, which is the reason it exists as a policy at all: user *and* vote
    /// session. Emptying one session's budget must leave a second session and ordinary navigation
    /// untouched — otherwise a voter working through a long ballot locks themselves out of the page
    /// they are voting on. The acceptance test in RateLimitPolicyBudgetTests checks the same shape
    /// against the real 120-token budget, where nothing is ever exhausted; this one empties the bucket
    /// first, so the partition is what the second request actually depends on.
    /// </summary>
    [Fact]
    public async Task VotingBudget_IsPartitionedPerSession_AndLeavesNavigationAlone()
    {
        using var factory = CreateFactory(new Dictionary<string, string>
        {
            ["RateLimiting:Voting:TokenLimit"] = TestVoteTokenLimit.ToString(),
            ["RateLimiting:Voting:TokensPerPeriod"] = "1",
            ["RateLimiting:Voting:ReplenishmentPeriodSeconds"] = TestReplenishmentPeriodSeconds.ToString(),
        });
        using var client = factory.CreateClient();
        const string userId = "rate-limit-vote-partition";

        using var rejected = await ExhaustAsync(
            () => CastVoteAsync(client, userId, sessionId: 1),
            TestVoteTokenLimit + 1);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Same user, second session: a partition of "user" alone would answer 429 here too.
        using var otherSession = await CastVoteAsync(client, userId, sessionId: 2);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherSession.StatusCode);

        // Same user, ordinary navigation: a different policy entirely, and untouched by the above.
        using var navigation = await SendAsync(client, HttpMethod.Get, PermissionsPath, userId);
        Assert.Equal(HttpStatusCode.OK, navigation.StatusCode);
    }

    /// <summary>
    /// The bypass an external review flagged: the Voting partition keys on the route's raw text, but
    /// the route is declared <c>{sessionId:long}</c> and the handler binds the parsed number. "1" and
    /// "01" parse to the very same session and are treated as such by the handler — but as raw text
    /// they are two different partition keys, so a caller could mint a fresh token budget per leading
    /// zero and vote past the configured limit indefinitely. Regression for that: exhaust the budget
    /// addressed as "1", then spend one more against "01" — a fixed partitioner must reject it from
    /// the same, already-empty bucket.
    /// </summary>
    [Fact]
    public async Task VotingBudget_TreatsAnEquivalentlyFormattedSessionId_AsTheSameBucket()
    {
        using var factory = CreateFactory(new Dictionary<string, string>
        {
            ["RateLimiting:Voting:TokenLimit"] = TestVoteTokenLimit.ToString(),
            ["RateLimiting:Voting:TokensPerPeriod"] = "1",
            ["RateLimiting:Voting:ReplenishmentPeriodSeconds"] = TestReplenishmentPeriodSeconds.ToString(),
        });
        using var client = factory.CreateClient();
        const string userId = "rate-limit-vote-session-alias";

        // Spend the whole budget against the canonical route text.
        for (var attempt = 0; attempt < TestVoteTokenLimit; attempt++)
        {
            using var accepted = await CastVoteAsync(client, userId, "1");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, accepted.StatusCode);
        }

        // Same session, same user, addressed with a leading zero — :long parses it to the identical
        // session the handler binds, so a correct partitioner draws from the very budget just emptied.
        using var padded = await CastVoteAsync(client, userId, "01");
        Assert.Equal(HttpStatusCode.TooManyRequests, padded.StatusCode);
    }

    /// <summary>
    /// The other half of the telemetry contract: a decision must be recorded even when the handler
    /// behind the limiter throws. Before the fix, <c>RateLimitTelemetryMiddleware</c> recorded only
    /// after <c>await next(context)</c> returned — an exception there skipped the recording entirely
    /// and left the spent permit uncounted, undercounting exactly the error-heavy traffic an operator
    /// most needs the numbers for.
    /// </summary>
    [Fact]
    public async Task Telemetry_IsRecorded_WhenTheHandlerBehindTheLimiterThrows()
    {
        var telemetry = new RecordingTelemetry();
        var access = Substitute.For<IChannelAccessService>();
        access.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("Provoked for the telemetry regression test."));

        using var factory = CreateFactory(
            new Dictionary<string, string>(),
            builder => builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRateLimitTelemetry>(telemetry);
                services.AddScoped(_ => access);
            }));
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, PermissionsPath, "rate-limit-exception");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var decision = Assert.Single(await telemetry.WaitForDecisionsAsync(1));
        Assert.Equal(RateLimitPolicyNames.InteractiveRead, decision.PolicyName);
        // Not the limiter's own rejection — the limiter let this request through, the handler failed.
        Assert.True(decision.Accepted);
        Assert.Null(decision.RetryAfterSeconds);
    }

    /// <summary>
    /// What the monitoring page will show, measured where it is produced. Three things are asserted
    /// together because they only mean anything together: every request under a policy is counted
    /// exactly once, an accepted request is told apart from a rejected one, and the dimension a
    /// counter is filed under is the route <em>template</em>.
    /// </summary>
    /// <remarks>
    /// The template is the load-bearing part. <c>/api/channels/{channelName}/resync</c> is one row an
    /// operator can read; the raw paths would be one row per channel anyone ever resynced — an
    /// unbounded key space in Redis, seeded with user-supplied text, in the one place whose whole
    /// purpose is to stay readable during an incident.
    /// </remarks>
    [Fact]
    public async Task PolicyDecisions_AreCountedOncePerRequest_UnderTheRouteTemplate()
    {
        var telemetry = new RecordingTelemetry();
        using var factory = CreateThrottledFactory(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IRateLimitTelemetry>(telemetry)));
        using var client = factory.CreateClient();
        const string userId = "rate-limit-telemetry";

        using var response = await ExhaustAsync(client, userId);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // Two accepted (the budget) plus the one rejection that follows them.
        var decisions = await telemetry.WaitForDecisionsAsync(TestPermitLimit + 1);

        Assert.All(decisions, decision =>
        {
            Assert.Equal(RateLimitPolicyNames.ChannelResync, decision.PolicyName);
            Assert.Equal("POST", decision.HttpMethod);
            Assert.Equal("/api/channels/{channelName}/resync", decision.RouteTemplate);
            Assert.Contains(userId, decision.Partition);
        });

        var accepted = decisions.Where(decision => decision.Accepted).ToList();
        Assert.Equal(TestPermitLimit, accepted.Count);
        // Nothing was throttled, so there is no wait to report — a Retry-After on an accepted request
        // would tell the monitoring page a rejection happened that never did.
        Assert.All(accepted, decision => Assert.Null(decision.RetryAfterSeconds));

        var rejected = Assert.Single(decisions, decision => !decision.Accepted);
        Assert.Equal(
            int.Parse(Assert.Single(response.Headers.GetValues("Retry-After"))),
            rejected.RetryAfterSeconds);
    }

    /// <summary>
    /// The distinction this whole telemetry path exists to make: a 429 is not automatically a policy
    /// violation. The resync cooldown answers 429 from inside the handler — per channel, which the
    /// per-user limiter cannot express — and the limiter let that request through. Counting it as a
    /// rejection would put a permanent baseline of "local rejections" on the monitoring page and make
    /// the number an operator watches during an incident meaningless.
    /// </summary>
    [Fact]
    public async Task DomainCooldown429_IsCountedAsAnAcceptedRequest_NotAsAPolicyRejection()
    {
        var telemetry = new RecordingTelemetry();

        // Substituted here rather than on the shared fixture: configuring the fixture's own cooldown
        // would make every other test on this route see a 429 it did not ask for, and the first one
        // that came back would look exactly like a limiter rejection.
        var access = Substitute.For<IChannelAccessService>();
        access.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var cooldown = Substitute.For<IChannelResyncCooldown>();
        cooldown.TryBeginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ResyncCooldownState(Acquired: false, RetryAfterSeconds: 30));

        using var factory = CreateFactory(
            new Dictionary<string, string>(),
            builder => builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRateLimitTelemetry>(telemetry);
                services.AddScoped(_ => access);
                services.AddSingleton(_ => cooldown);
            }));
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, ResyncPath, "rate-limit-cooldown");

        // The cooldown's 429, not the limiter's — told apart by errorCode, exactly as a client does.
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ApiErrorCodes.ResyncCooldownActive,
            body.RootElement.GetProperty("errorCode").GetString());

        var decision = Assert.Single(await telemetry.WaitForDecisionsAsync(1));
        Assert.True(decision.Accepted);
        Assert.Null(decision.RetryAfterSeconds);
    }

    /// <summary>
    /// A route with no policy moves no counter. The monitoring page marks those routes as a gap on
    /// purpose (worker health, SSE, admin, auth) — silently filing them under a policy they do not
    /// have would be worse than the gap, because the counter would then describe traffic no budget
    /// ever applied to.
    /// </summary>
    [Fact]
    public async Task PolicyFreeRoute_MovesNoCounter()
    {
        var telemetry = new RecordingTelemetry();
        using var factory = CreateFactory(
            new Dictionary<string, string>(),
            builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IRateLimitTelemetry>(telemetry)));
        using var client = factory.CreateClient();

        // First the policy-free request, then one that does carry a policy: requests are sequential
        // and the middleware records before it returns, so if the health call had produced an entry
        // it would be the first of the two — and the assertion below would see it.
        using var health = await SendAsync(client, HttpMethod.Get, "/api/worker/health", "rate-limit-policy-free");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var permissions = await SendAsync(client, HttpMethod.Get, PermissionsPath, "rate-limit-policy-free");
        Assert.Equal(HttpStatusCode.OK, permissions.StatusCode);

        var decision = Assert.Single(await telemetry.WaitForDecisionsAsync(1));
        Assert.Equal(RateLimitPolicyNames.InteractiveRead, decision.PolicyName);
        Assert.Equal("/api/channels/{channelName}/permissions", decision.RouteTemplate);
    }

    /// <summary>
    /// Codex Sol review of #247 (P2): the legal endpoints used to share <c>PublicHealth</c>'s budget
    /// with <c>GET /api/health</c> — two counters with unrelated legitimate callers (browser
    /// visitors behind a possibly-shared IP vs. two machines on fixed cadences) that had no business
    /// affecting each other. Exhausts the (overridden, tiny) <c>PublicLegal</c> budget against
    /// <c>GET /api/legal/availability</c> and asserts <c>GET /api/health</c> is untouched by it —
    /// and, since both routes are anonymous and this factory's default <c>TestAuthHandler</c> is not
    /// exercised here at all, also that emptying <c>PublicLegal</c> does not affect an authenticated
    /// <c>InteractiveRead</c> route either.
    /// </summary>
    [Fact]
    public async Task PublicLegalBudget_ExhaustsIndependently_FromPublicHealthAndInteractiveRead()
    {
        using var factory = CreateFactory(new Dictionary<string, string>
        {
            ["RateLimiting:PublicLegal:PermitLimit"] = TestPermitLimit.ToString(),
        });
        using var client = factory.CreateClient();

        using var rejected = await ExhaustAsync(
            () => client.GetAsync("/api/legal/availability"),
            TestPermitLimit + 2);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // /api/health shares no counter with the now-exhausted PublicLegal budget.
        using var health = await client.GetAsync("/api/health");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, health.StatusCode);

        // Nor does an ordinary authenticated navigation route.
        using var navigation = await SendAsync(client, HttpMethod.Get, PermissionsPath, "rate-limit-public-legal");
        Assert.Equal(HttpStatusCode.OK, navigation.StatusCode);
    }

    /// <summary>
    /// A host of its own whose ChannelResync budget is spent in two requests. Its own, because a rate
    /// limiter is host state: a shared one would carry spent permits between test cases, and a
    /// logging provider has to be registered at startup anyway.
    /// </summary>
    private WebApplicationFactory<Program> CreateThrottledFactory(Action<IWebHostBuilder>? configure = null)
        => CreateFactory(
            new Dictionary<string, string> { ["RateLimiting:ChannelResync:PermitLimit"] = TestPermitLimit.ToString() },
            configure);

    /// <summary>
    /// A host of its own with the given budget overrides. Its own, because a rate limiter is host
    /// state: a shared one would carry spent permits between test cases, and a logging provider has to
    /// be registered at startup anyway.
    /// </summary>
    private WebApplicationFactory<Program> CreateFactory(
        IReadOnlyDictionary<string, string> settings,
        Action<IWebHostBuilder>? configure = null)
        => _factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            configure?.Invoke(builder);
        });

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return client.SendAsync(request);
    }

    /// <summary>
    /// A vote mutation against one session. The empty body is enough — every answer below a 429 is
    /// some form of rejection by the filter or the binder, and the permit is spent before either runs.
    /// </summary>
    private static Task<HttpResponseMessage> CastVoteAsync(HttpClient client, string userId, long sessionId)
        => CastVoteAsync(client, userId, sessionId.ToString());

    /// <summary>
    /// As above, but with the session id given as raw route text rather than a parsed <c>long</c> —
    /// what <see cref="VotingBudget_TreatsAnEquivalentlyFormattedSessionId_AsTheSameBucket"/> needs to
    /// address the same session through two different textual encodings ("1" vs. "01").
    /// </summary>
    private static Task<HttpResponseMessage> CastVoteAsync(HttpClient client, string userId, string sessionId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/channels/testchannel/vote-sessions/{sessionId}/votes")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return client.SendAsync(request);
    }

    /// <summary>Spends the ChannelResync budget and hands back the first rejected answer.</summary>
    private static Task<HttpResponseMessage> ExhaustAsync(HttpClient client, string userId)
        // One spare attempt past the capacity, and deliberately far below the configured default: a
        // build in which the configuration override does not reach the policy runs out of attempts
        // and reports the message below, instead of quietly passing on the production budget.
        => ExhaustAsync(() => SendAsync(client, HttpMethod.Post, ResyncPath, userId), TestPermitLimit + 2);

    /// <summary>Repeats one request until it is rejected, and hands back that rejected answer.</summary>
    private static async Task<HttpResponseMessage> ExhaustAsync(
        Func<Task<HttpResponseMessage>> send,
        int attempts)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var response = await send();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            // Whatever the endpoint answers below the limit is not this test's business — only that
            // the limiter let it through. The permit is spent by the middleware either way, before
            // any filter or handler runs.
            response.Dispose();
        }

        throw new InvalidOperationException(
            $"Nach {attempts} Anfragen kam keine 429-Antwort — der Rate-Limiter greift nicht.");
    }

    /// <summary>
    /// Collects the decisions the telemetry middleware reports. A hand-written fake rather than a
    /// substitute because the assertions are about a sequence, and because the recording has to be
    /// thread-safe: the product path reports fire-and-forget, so the write can land after the client
    /// already has its response — which is also why every read below goes through
    /// <see cref="WaitForDecisionsAsync"/> instead of asserting immediately.
    /// </summary>
    private sealed class RecordingTelemetry : IRateLimitTelemetry
    {
        private readonly ConcurrentQueue<RateLimitPolicyDecision> _decisions = new();

        public Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default)
        {
            _decisions.Enqueue(decision);
            return Task.CompletedTask;
        }

        public Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> ForgetPartitionAsync(string partition, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        /// <summary>
        /// Waits until the expected number of decisions has arrived and hands them back. Then waits a
        /// short moment more and fails if another one turned up: "exactly this many" is half of what
        /// every case here asserts, and a race that over-counts would otherwise pass.
        /// </summary>
        public async Task<IReadOnlyList<RateLimitPolicyDecision>> WaitForDecisionsAsync(int expected)
        {
            var deadline = Stopwatch.StartNew();
            while (_decisions.Count < expected && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(10);
            }

            Assert.Equal(expected, _decisions.Count);
            await Task.Delay(100);
            Assert.Equal(expected, _decisions.Count);
            return _decisions.ToList();
        }
    }

    /// <summary>Captures every log entry so a test can assert on one. ILogger has no test double in
    /// this project, and the one thing under test here is that a line is written at all.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        internal sealed record LogEntry(string Category, LogLevel Level, string Message);

        private sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => provider.Add(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}
