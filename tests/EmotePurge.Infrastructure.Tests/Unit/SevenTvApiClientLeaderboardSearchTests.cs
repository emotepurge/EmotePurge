using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Telemetry;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvApiClient.SearchEmotesAsync"/> — the leaderboard import source's own v4
/// query (spec 2026-09-13, F7/T1), built on the same shared page-fetch
/// <see cref="SevenTvApiClientEmoteSetPreviewTests"/> and
/// <see cref="SevenTvApiClientPreviewCharacterizationTests"/> pin for the foreign-channel preview.
/// Three things this file exists to prove that the preview's own tests cannot: the flat
/// <c>Emote</c> shape (no per-set alias, <c>flags.animated</c> feeding the image url), the
/// <c>x-ratelimit-search-*</c> HTTP headers this call source — and only this one — reads, and that
/// the resulting header sample survives into <see cref="SevenTvEmoteSearchPageResult"/> even on a
/// failure, since the client writes no log line of its own for a search request (see
/// <see cref="SevenTvApiClient.SearchEmotesAsync"/>'s doc comment).
/// </summary>
public class SevenTvApiClientLeaderboardSearchTests
{
    [Fact]
    public async Task SinglePage_ParsesTotalCountPageCountAndItems_WithAnimatedFlagControllingTheImageUrl()
    {
        // Third item carries no "scores" object at all — 7TV's two score fields are always sent
        // together as plain (non-nullable) ints when present, so the defensive `dto.Scores?.X`
        // null-propagation is exercised by omitting the whole object, not by a null field inside it.
        var payload = Page(
            totalCount: 705,
            pageCount: 3,
            ("e1", "PogChamp", true, 4, 1),
            ("e2", "Kappa", false, 227959, 0),
            ("e3", "NoScoreEmote", false, null, null));
        var client = CreateClient(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TrendingDaily, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.Ok, result.Status);
        Assert.Equal(705, result.Page!.TotalCount);
        Assert.Equal(3, result.Page.PageCount);
        Assert.Equal(3, result.Page.Items.Count);

        var animated = result.Page.Items[0];
        Assert.Equal("e1", animated.SevenTvEmoteId);
        // Alias == DefaultName (spec T1): a leaderboard hit has no per-set alias.
        Assert.Equal("PogChamp", animated.Alias);
        Assert.Equal("PogChamp", animated.DefaultName);
        Assert.Equal("https://cdn.7tv.app/emote/e1/4x_static.webp", animated.ImageUrl);
        Assert.Equal(4, animated.TopAllTime);
        Assert.Equal(1, animated.Trending);

        var still = result.Page.Items[1];
        Assert.Equal("https://cdn.7tv.app/emote/e2/4x.webp", still.ImageUrl);
        Assert.Equal(227959, still.TopAllTime);

        var withoutScores = result.Page.Items[2];
        Assert.Null(withoutScores.TopAllTime);
        Assert.Null(withoutScores.Trending);
    }

    /// <summary>
    /// A literal HTTP 429 with 7TV's search-bucket headers attached: RetryAfter must prefer the
    /// HTTP <c>x-ratelimit-search-reset</c> header over <c>Retry-After</c> even on this code path
    /// (no GraphQL body is parsed here at all), and the header sample must land on the failed
    /// result — this is the "auch bei Fehlausgängen" half of the header-in-every-outcome contract.
    /// </summary>
    [Fact]
    public async Task LiteralHttp429_PrefersTheSearchResetHeader_OverRetryAfter_AndCarriesTheHeaderSample()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "77");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-limit", "100");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-reset", "55");
        var client = CreateClient(response);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TopAllTime, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(55), result.RetryAfter);
        Assert.Equal("100", result.RateLimitLimit);
        Assert.Equal("0", result.RateLimitRemaining);
        Assert.Equal("55", result.RateLimitReset);
    }

    /// <summary>
    /// An epoch-shaped (or otherwise implausible) <c>x-ratelimit-search-reset</c> must not become
    /// a decades-long <c>RetryAfter</c> — the same plausibility bound
    /// <c>ReadResetHintSeconds</c>/<c>MaxResetHintSeconds</c> already applies to the GraphQL hint
    /// (Fix-Lauf 1, Befund A). Falls back to <c>Retry-After</c> when the header value is rejected.
    /// The header <em>sample</em> is unaffected — it carries the header's raw string verbatim
    /// regardless of plausibility, same as every other <c>RateLimit*</c> field on the observation.
    /// </summary>
    [Fact]
    public async Task LiteralHttp429_WithAnEpochShapedSearchResetHeader_IsIgnored_FallingBackToRetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "30");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-reset", "1893456000");
        var client = CreateClient(response);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TopAllTime, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Equal("1893456000", result.RateLimitReset);
    }

    /// <summary>
    /// The disguised-as-200 form, with all three retry-hint sources present at once: the HTTP
    /// search-reset header must still win over both the GraphQL payload's own hint and
    /// <c>Retry-After</c> — the full F3 order, not just the two-way case the preview's own tests
    /// already cover.
    /// </summary>
    [Fact]
    public async Task DisguisedRateLimit_PrefersTheSearchResetHeader_OverTheGraphQlHintAndRetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(RateLimitedPayload(graphQlHintSeconds: 66), Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("Retry-After", "77");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-reset", "55");
        var client = CreateClient(response);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TrendingDaily, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(55), result.RetryAfter);
    }

    /// <summary>
    /// Without the HTTP header, the GraphQL payload's own reset hint still beats
    /// <c>Retry-After</c> — same order the preview path already has, now proven to survive
    /// unchanged once the HTTP header is added to the front of the queue.
    /// </summary>
    [Fact]
    public async Task DisguisedRateLimit_WithoutTheSearchResetHeader_PrefersTheGraphQlHint_OverRetryAfter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(RateLimitedPayload(graphQlHintSeconds: 66), Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("Retry-After", "77");
        var client = CreateClient(response);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TrendingDaily, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(66), result.RetryAfter);
    }

    [Fact]
    public async Task SearchRateLimitHeaders_AreCaptured_OnTheObservationAndTheResult_OnAnOkOutcome()
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Page(totalCount: 1, pageCount: 1, ("e1", "PogChamp", true, 1, 1)), Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-limit", "100");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-remaining", "97");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-reset", "40");
        var client = CreateClient(response, telemetry);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TrendingDaily, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.Ok, result.Status);
        Assert.Equal("100", result.RateLimitLimit);
        Assert.Equal("97", result.RateLimitRemaining);
        Assert.Equal("40", result.RateLimitReset);

        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal("100", observation.RateLimitLimit);
        Assert.Equal("97", observation.RateLimitRemaining);
        Assert.Equal("40", observation.RateLimitReset);
    }

    /// <summary>
    /// A validation rejection (e.g. an out-of-range <c>perPage</c>) is a GraphQL error without
    /// <c>extensions.status: 429</c> — measured live to carry none of 7TV's search-bucket headers
    /// (Sonde 2, 2026-09-13: an abgelehnter request costs no bucket charge). Must map to
    /// <see cref="SevenTvEmoteSearchLookupStatus.Unavailable"/>, not to an empty page.
    /// </summary>
    [Fact]
    public async Task ValidationRejection_IsReportedAsUnavailable_WithNoSearchHeaders()
    {
        const string validationRejectionPayload =
            """{"data":null,"errors":[{"message":"Failed to parse \"Int\": the value is 500, must be less than or equal to 250"}]}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(validationRejectionPayload, Encoding.UTF8, "application/json"),
        };
        var client = CreateClient(response);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TopAllTime, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Page);
        Assert.Null(result.RateLimitLimit);
        Assert.Null(result.RateLimitRemaining);
        Assert.Null(result.RateLimitReset);
    }

    [Fact]
    public async Task GenericUpstreamFailure_IsReportedAsUnavailable_AndCountedWithItsRealStatus()
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-limit", "100");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-remaining", "99");
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-reset", "58");
        var client = CreateClient(response, telemetry);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TrendingDaily, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.Unavailable, result.Status);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(500, observation.StatusCode);
        Assert.Equal(RateLimitCallSources.SevenTvLeaderboard, observation.CallSource);

        // "Header sample in every outcome" (spec 2026-09-13) also holds for a plain 5xx — the
        // sample is built before FetchV4PageAsync branches on the response's success/failure, so a
        // generic upstream failure must not silently drop it.
        Assert.Equal("100", result.RateLimitLimit);
        Assert.Equal("99", result.RateLimitRemaining);
        Assert.Equal("58", result.RateLimitReset);
    }

    /// <summary>
    /// AK 13: exactly one observation per upstream request, under the new call source — and,
    /// crucially, none under <see cref="RateLimitCallSources.SevenTvForeignPreview"/> or
    /// <see cref="RateLimitCallSources.SevenTvRest"/>. Wires the REAL
    /// <see cref="ProviderRequestTelemetryHandler"/> ahead of the client, under the pre-existing
    /// <c>SevenTvRest</c> call source — exactly the registration <c>ServiceCollectionExtensions</c>
    /// uses in production — so a suppression regression would show up here as a second, misfiled
    /// observation.
    /// </summary>
    [Fact]
    public async Task Success_IsCountedExactlyOnce_UnderTheLeaderboardCallSource_NotUnderPreviewOrRest()
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Page(totalCount: 1, pageCount: 1, ("e1", "PogChamp", true, 1, 1)), Encoding.UTF8, "application/json"),
        };
        var client = CreateClient(response, telemetry);

        var result = await client.SearchEmotesAsync(SevenTvLeaderboardSort.TopAllTime, page: 1);

        Assert.Equal(SevenTvEmoteSearchLookupStatus.Ok, result.Status);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(RateLimitCallSources.SevenTvLeaderboard, observation.CallSource);
        Assert.Equal(RateLimitProviders.SevenTv, observation.ProviderName);
        Assert.Equal(200, observation.StatusCode);
        Assert.DoesNotContain(telemetry.Observations, o =>
            o.CallSource is RateLimitCallSources.SevenTvForeignPreview or RateLimitCallSources.SevenTvRest);
    }

    /// <summary>
    /// Pins the <em>outgoing</em> request body — nothing else in this file, or in
    /// <see cref="SevenTvApiClientEmoteSetPreviewTests"/>, ever inspects what this client sends,
    /// only how it parses what comes back. That gap is exactly how an earlier revision shipped a
    /// bare-string <c>$sort</c> value: 7TV's <c>Sort</c> is an <c>INPUT_OBJECT</c>
    /// (<c>{ sortBy: SortBy!, order: SortOrder! }</c>, introspected live 2026-09-14), and every
    /// request in the wrong shape got a coercion error back — HTTP 200, no
    /// <c>extensions.status: 429</c> — which reads as a plain <see cref="V4PageStatus.Unavailable"/>
    /// here, not as an obviously broken query. One case per sort value, so a mix-up between
    /// <see cref="SevenTvLeaderboardSort.TrendingDaily"/> and
    /// <see cref="SevenTvLeaderboardSort.TopAllTime"/>'s wire codes would fail too.
    /// </summary>
    [Theory]
    [InlineData(SevenTvLeaderboardSort.TrendingDaily, "TRENDING_DAILY")]
    [InlineData(SevenTvLeaderboardSort.TopAllTime, "TOP_ALL_TIME")]
    public async Task RequestBody_SendsSortAsTheInputObjectItsSchemaDeclares_WithDescendingOrder(
        SevenTvLeaderboardSort sortBy, string expectedSortByWireCode)
    {
        var capturingHandler = new CapturingStubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Page(totalCount: 1, pageCount: 1, ("e1", "PogChamp", true, 1, 1)), Encoding.UTF8, "application/json"),
        });
        var client = CreateCapturingClient(capturingHandler);

        await client.SearchEmotesAsync(sortBy, page: 3);

        Assert.NotNull(capturingHandler.CapturedRequestBody);
        var body = JsonNode.Parse(capturingHandler.CapturedRequestBody!)!;
        var sortVariable = body["variables"]!["sort"]!;

        // An object with exactly these two fields — not a bare string, not a third field, and not
        // ASCENDING, which would rank the leaderboard from the bottom.
        Assert.Equal(expectedSortByWireCode, sortVariable["sortBy"]!.GetValue<string>());
        Assert.Equal("DESCENDING", sortVariable["order"]!.GetValue<string>());
        Assert.Equal(3, body["variables"]!["page"]!.GetValue<int>());
        Assert.Equal(250, body["variables"]!["perPage"]!.GetValue<int>());
    }

    private static SevenTvApiClient CreateCapturingClient(HttpMessageHandler handler)
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(httpClient, telemetry, new RecordingForeignUpstreamRequestBudget(), new RecordingLogger<SevenTvApiClient>());
    }

    private sealed class CapturingStubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? CapturedRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private static string Page(
        int totalCount, int pageCount, params (string Id, string DefaultName, bool Animated, int? TopAllTime, int? TrendingDay)[] items)
    {
        var itemsArray = new JsonArray();
        foreach (var item in items)
        {
            var itemObject = new JsonObject
            {
                ["id"] = item.Id,
                ["default_name"] = item.DefaultName,
                ["flags"] = new JsonObject { ["animated"] = item.Animated },
            };

            // 7TV sends both score fields together as plain (non-nullable) ints — never a JSON
            // null inside "scores" — so a caller wanting "no scores" passes null for both and gets
            // the whole object omitted, exercising the DTO's defensive `Scores?.X` null-propagation
            // instead of a shape 7TV never actually sends.
            if (item.TopAllTime is { } topAllTime && item.TrendingDay is { } trendingDay)
            {
                itemObject["scores"] = new JsonObject
                {
                    ["top_all_time"] = topAllTime,
                    ["trending_day"] = trendingDay,
                };
            }

            itemsArray.Add(itemObject);
        }

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["emotes"] = new JsonObject
                {
                    ["search"] = new JsonObject
                    {
                        ["total_count"] = totalCount,
                        ["page_count"] = pageCount,
                        ["items"] = itemsArray,
                    },
                },
            },
        }.ToJsonString();
    }

    // The disguised-as-200 semantic-429 payload, optionally carrying the unverified
    // extensions.headers reset hint — same shape SevenTvApiClientEmoteSetPreviewTests already pins
    // for the preview path (RateLimitedPayload there).
    private static string RateLimitedPayload(int? graphQlHintSeconds)
    {
        var extensions = new JsonObject { ["code"] = "RATE_LIMITED", ["status"] = 429 };
        if (graphQlHintSeconds is { } seconds)
        {
            extensions["headers"] = new JsonObject { ["x-ratelimit-search-reset"] = seconds };
        }

        return new JsonObject
        {
            ["data"] = null,
            ["errors"] = new JsonArray { new JsonObject { ["message"] = "too many requests", ["extensions"] = extensions } },
        }.ToJsonString();
    }

    // Wires the REAL ProviderRequestTelemetryHandler ahead of the client, under the pre-existing
    // SevenTvRest call source — exactly the registration ServiceCollectionExtensions uses in
    // production — so a suppression regression would show up as a doubled or misfiled observation,
    // the same wiring SevenTvApiClientForeignTelemetryTests uses for the preview path.
    private static SevenTvApiClient CreateClient(HttpResponseMessage response, IRateLimitTelemetry? telemetry = null)
    {
        telemetry ??= new RecordingRateLimitTelemetry();
        var handler = new ProviderRequestTelemetryHandler(RateLimitProviders.SevenTv, RateLimitCallSources.SevenTvRest, telemetry)
        {
            InnerHandler = new StubHandler(response),
        };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(httpClient, telemetry, new RecordingForeignUpstreamRequestBudget(), new RecordingLogger<SevenTvApiClient>());
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
