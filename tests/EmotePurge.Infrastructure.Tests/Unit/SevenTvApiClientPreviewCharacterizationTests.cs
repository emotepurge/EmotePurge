using System.Net;
using System.Text;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Telemetry;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Characterization tests for <see cref="SevenTvApiClient.GetEmoteSetPreviewAsync"/> written against
/// the code as it stands today (spec 2026-09-13, T0/AK 34), before the shared v4 page-fetch extraction
/// (E11/F3) touches <c>FetchPreviewPageAsync</c>. The 21 pre-existing client tests
/// (<see cref="SevenTvApiClientEmoteSetPreviewTests"/>, <see cref="SevenTvApiClientForeignTelemetryTests"/>)
/// do not cover the three behaviours below — without pinning them here first, "unchanged and green"
/// after that extraction would prove nothing about them. This file, and only this file, is the thing
/// that must stay byte-for-byte unmodified once the extraction lands; that is also why it is its own
/// file rather than additions to either existing one.
/// </summary>
public class SevenTvApiClientPreviewCharacterizationTests
{
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";

    /// <summary>
    /// Malformed JSON behind an HTTP 200 must still be counted exactly once — under the foreign-preview
    /// call source, the same way any other outcome of this path is — and the client reports it up as
    /// <see cref="SevenTvPreviewLookupStatus.Unavailable"/> rather than crashing the caller.
    /// </summary>
    [Fact]
    public async Task MalformedJsonBehindHttp200_IsCountedExactlyOnce_AndReportedAsUnavailable()
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not valid json", Encoding.UTF8, "application/json"),
        };
        var client = CreateClient(response, telemetry);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Unavailable, result.Status);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(RateLimitCallSources.SevenTvForeignPreview, observation.CallSource);
        Assert.Equal(200, observation.StatusCode);
    }

    /// <summary>
    /// The response's <c>Ratelimit-Limit</c>/<c>-Remaining</c>/<c>-Reset</c> headers — Twitch's
    /// spelling, which is what <see cref="SevenTvApiClient.RecordForeignPreviewObservation"/> reads
    /// today — land verbatim in the matching fields of the recorded observation.
    /// </summary>
    [Fact]
    public async Task TwitchStyleRatelimitHeaders_LandOnTheObservationVerbatim()
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SinglePagePayload(), Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("Ratelimit-Limit", "500");
        response.Headers.TryAddWithoutValidation("Ratelimit-Remaining", "480");
        response.Headers.TryAddWithoutValidation("Ratelimit-Reset", "1700000000");
        var client = CreateClient(response, telemetry);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal("500", observation.RateLimitLimit);
        Assert.Equal("480", observation.RateLimitRemaining);
        Assert.Equal("1700000000", observation.RateLimitReset);
    }

    /// <summary>
    /// Pins the gap F3 names: an HTTP response header <c>x-ratelimit-search-reset</c> on the preview
    /// path has zero effect today, even on a confirmed (disguised) rate limit. The reset hint this path
    /// reads comes exclusively from the GraphQL payload's <c>errors[].extensions.headers</c>
    /// (<see cref="SevenTvApiClient" />'s <c>ReadResetHintSeconds</c>) — a raw HTTP header of the same
    /// name is never inspected. This is exactly the distinction that must keep holding after the E11
    /// extraction: the new leaderboard search call source will start reading this very header, and the
    /// two must stay told apart by call source, not by some incidental difference in the shared fetch.
    /// A 42-second value is deliberately present and deliberately ignored, so the assertion cannot pass
    /// by coincidence (an absent header would too).
    /// </summary>
    [Fact]
    public async Task HttpSearchResetHeader_HasNoEffect_OnRetryAfterOrTheObservation()
    {
        const string rateLimitedPayloadWithoutHeaderHint =
            """{"data":null,"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""";
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rateLimitedPayloadWithoutHeaderHint, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("x-ratelimit-search-reset", "42");
        var client = CreateClient(response, telemetry);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);
        Assert.Null(result.RetryAfter);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(429, observation.StatusCode);
        Assert.Null(observation.RetryAfterSeconds);
        Assert.Equal(RateLimitCallSources.SevenTvForeignPreview, observation.CallSource);
    }

    // Minimal single-item, single-page shape — pagination itself is
    // SevenTvApiClientEmoteSetPreviewTests's job, not this file's.
    private static string SinglePagePayload() =>
        """
        {"data":{"emote_sets":{"emote_set":{"emotes":{
            "total_count":1,"page_count":1,
            "items":[{"alias":"PogU","emote":{"id":"e1","default_name":"PogChamp","scores":{"top_all_time":1,"trending_day":1}}}]
        }}}}}
        """;

    // Same wiring as SevenTvApiClientForeignTelemetryTests: the REAL ProviderRequestTelemetryHandler
    // ahead of the client, under the pre-existing SevenTvRest call source, exactly the registration
    // ServiceCollectionExtensions uses in production — so these tests would also notice a suppression
    // regression, not just the three behaviours they are named for.
    private static SevenTvApiClient CreateClient(HttpResponseMessage response, IRateLimitTelemetry telemetry)
    {
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
