using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The client's half of the shared 7TV search budget: both search queries report what 7TV said
/// about the bucket, in every outcome, so a lockout one process walks into blocks the other too
/// (design note <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>, 2.2).
/// </summary>
public class SevenTvApiClientSearchBucketObservationTests
{
    private const string Channel = "somechannel";

    [Fact]
    public async Task ResolveTwitchUserId_OnSuccess_ReportsRemainingAndReset()
    {
        var budget = new RecordingSevenTvSearchBudget();
        var client = CreateClient(Json(HttpStatusCode.OK, UsersPayload(), remaining: "42", reset: "37"), budget);

        var result = await client.ResolveTwitchUserIdAsync(Channel);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.Equal("123", result.TwitchUserId);
        var observation = Assert.Single(budget.Observations);
        Assert.Equal(new SevenTvSearchObservation(42, 37, RateLimited: false), observation);
    }

    [Fact]
    public async Task ResolveTwitchUserId_OnA429DisguisedAs200_ReportsARateLimitWithTheSearchReset_AndAnswersUnavailable()
    {
        // The v3 user search draws from the same bucket as the leaderboard and is overdrawn the same
        // way. Before the shared budget this read as a plain Unavailable and was retried every minute.
        var budget = new RecordingSevenTvSearchBudget();
        var client = CreateClient(Json(HttpStatusCode.OK, RateLimitedPayload(), remaining: "0", reset: "3583"), budget);

        var result = await client.ResolveTwitchUserIdAsync(Channel);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        var observation = Assert.Single(budget.Observations);
        Assert.True(observation.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(3583), observation.RetryAfter);
        Assert.Equal(0, observation.Remaining);
    }

    [Fact]
    public async Task ResolveTwitchUserId_OnALiteralHttp429WithoutHeaders_ReportsARateLimitWithoutAHint()
    {
        var budget = new RecordingSevenTvSearchBudget();
        var client = CreateClient(Json(HttpStatusCode.TooManyRequests, "{}"), budget);

        var result = await client.ResolveTwitchUserIdAsync(Channel);

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        var observation = Assert.Single(budget.Observations);
        Assert.True(observation.RateLimited);
        Assert.Null(observation.RetryAfter);
    }

    [Fact]
    public async Task ResolveTwitchUserId_OnAnImplausibleResetHeader_DropsTheReset()
    {
        // An epoch-shaped value must not become a block duration.
        var budget = new RecordingSevenTvSearchBudget();
        var client = CreateClient(Json(HttpStatusCode.OK, UsersPayload(), remaining: "5", reset: "1790000000"), budget);

        await client.ResolveTwitchUserIdAsync(Channel);

        var observation = Assert.Single(budget.Observations);
        Assert.Equal(5, observation.Remaining);
        Assert.Null(observation.ResetSeconds);
    }

    [Fact]
    public async Task SearchEmotes_OnSuccess_ReportsRemainingAndReset()
    {
        var budget = new RecordingSevenTvSearchBudget();
        var client = CreateClient(Json(HttpStatusCode.OK, EmptySearchPayload(), remaining: "97", reset: "55"), budget);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TrendingDaily, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.Ok, result.Status);
        Assert.Equal(new SevenTvSearchObservation(97, 55, RateLimited: false), Assert.Single(budget.Observations));
    }

    [Fact]
    public async Task SearchEmotes_OnA429DisguisedAs200_ReportsARateLimit()
    {
        var budget = new RecordingSevenTvSearchBudget();
        var client = CreateClient(Json(HttpStatusCode.OK, RateLimitedPayload(), remaining: "0", reset: "3583"), budget);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TopAllTime, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.RateLimited, result.Status);
        var observation = Assert.Single(budget.Observations);
        Assert.True(observation.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(3583), observation.RetryAfter);
    }

    [Fact]
    public async Task NonSearchQueries_ReportNothing()
    {
        // userByConnection does not draw from the search bucket; reporting its (absent) headers
        // would only add noise to the minimum-remaining telemetry.
        var budget = new RecordingSevenTvSearchBudget();
        var payload = """{"data":{"user_by_connection":{"id":"u1","connections":[{"platform":"TWITCH","id":"123","emote_set_id":"s1"}]}}}""";
        var client = CreateClient(Json(HttpStatusCode.OK, payload), budget);

        await client.ResolveSevenTvIdentityAsync("123");

        Assert.Empty(budget.Observations);
    }

    private static string UsersPayload() =>
        $$$"""{"data":{"users":[{"id":"u1","username":"{{{Channel}}}","connections":[{"platform":"TWITCH","username":"{{{Channel}}}","id":"123"}]}]}}""";

    private static string EmptySearchPayload() =>
        """{"data":{"emotes":{"search":{"total_count":0,"page_count":1,"items":[]}}}}""";

    private static string RateLimitedPayload() =>
        new JsonObject
        {
            ["data"] = null,
            ["errors"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = "too many requests",
                    ["extensions"] = new JsonObject { ["code"] = "RATE_LIMITED", ["status"] = 429 },
                },
            },
        }.ToJsonString();

    private static HttpResponseMessage Json(HttpStatusCode status, string payload, string? remaining = null, string? reset = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (remaining is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ratelimit-search-limit", "100");
            response.Headers.TryAddWithoutValidation("x-ratelimit-search-remaining", remaining);
        }

        if (reset is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ratelimit-search-reset", reset);
        }

        return response;
    }

    private static SevenTvApiClient CreateClient(HttpResponseMessage response, RecordingSevenTvSearchBudget budget)
    {
        var httpClient = new HttpClient(new StubHandler(response)) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(
            httpClient,
            new RecordingRateLimitTelemetry(),
            new RecordingForeignUpstreamRequestBudget(),
            budget,
            new RecordingLogger<SevenTvApiClient>());
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
