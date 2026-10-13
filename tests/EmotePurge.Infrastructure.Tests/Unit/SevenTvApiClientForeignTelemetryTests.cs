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
/// The telemetry vertrag (spec 2026-09-09, section 6, AK 15) for
/// <see cref="SevenTvApiClient.GetEmoteSetPreviewAsync"/>: <see cref="ProviderRequestTelemetryHandler"/>
/// is wired into the pipeline for real here — the point is proving it stays silent for these requests
/// (<see cref="ProviderTelemetrySuppression"/>) while the client reports itself instead, exactly once
/// per upstream request, under <see cref="RateLimitCallSources.SevenTvForeignPreview"/> rather than
/// <see cref="RateLimitCallSources.SevenTvRest"/> — and that a real HTTP 429 is no longer
/// indistinguishable from a generic failure, which it was before this class existed
/// (<c>EnsureSuccessStatusCode()</c> used to throw before either form of 429 could be told apart from
/// any other non-2xx status).
/// </summary>
public class SevenTvApiClientForeignTelemetryTests
{
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";

    [Fact]
    public async Task Success_IsCountedExactlyOnce_UnderTheForeignPreviewCallSource()
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SinglePagePayload(), Encoding.UTF8, "application/json"),
        };
        var client = CreateClient(response, telemetry);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(RateLimitCallSources.SevenTvForeignPreview, observation.CallSource);
        Assert.Equal(RateLimitProviders.SevenTv, observation.ProviderName);
        Assert.Equal(200, observation.StatusCode);
    }

    /// <summary>
    /// The single most expensive mistake in the spec (AK 7), now also proven at the telemetry layer:
    /// a 7TV overload disguised as HTTP 200 must not be filed as a plain success just because the raw
    /// HTTP status was 200 — it has to carry the real, semantic outcome.
    /// </summary>
    [Fact]
    public async Task DisguisedRateLimit_IsCountedAsARateLimitEvent_DespiteTheRawHttp200()
    {
        const string rateLimitedPayload =
            """{"data":null,"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""";
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rateLimitedPayload, Encoding.UTF8, "application/json"),
        };
        var client = CreateClient(response, telemetry);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(429, observation.StatusCode);
        Assert.Equal(RateLimitCallSources.SevenTvForeignPreview, observation.CallSource);
    }

    /// <summary>
    /// A real HTTP 429 (not the GraphQL-disguised form above) must be told apart from a generic
    /// upstream failure — previously it was not: <c>EnsureSuccessStatusCode()</c> threw before the two
    /// could ever be distinguished, so this exact case used to fall into the generic catch and come
    /// back as plain <c>Unavailable</c>, four extra attempts and a starker 60 s clock away from what
    /// E4 actually needs.
    /// </summary>
    [Fact]
    public async Task ARealHttp429_IsCountedAndReported_AsRateLimited_WithRetryAfter()
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "12");
        var client = CreateClient(response, telemetry);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(12), result.RetryAfter);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(429, observation.StatusCode);
        Assert.Equal(12, observation.RetryAfterSeconds);
        Assert.Equal(RateLimitCallSources.SevenTvForeignPreview, observation.CallSource);
    }

    [Fact]
    public async Task AGenericUpstreamFailure_IsCountedWithItsRealStatus_NotAs429()
    {
        var telemetry = new RecordingRateLimitTelemetry();
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var client = CreateClient(response, telemetry);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Unavailable, result.Status);
        var observation = Assert.Single(telemetry.Observations);
        Assert.Equal(500, observation.StatusCode);
        Assert.Equal(RateLimitCallSources.SevenTvForeignPreview, observation.CallSource);
    }

    // Built through JsonNode-free string literal — deliberately the minimal single-item, single-page
    // shape; pagination itself is SevenTvApiClientEmoteSetPreviewTests's job, not this file's.
    private static string SinglePagePayload() =>
        """
        {"data":{"emote_sets":{"emote_set":{"emotes":{
            "total_count":1,"page_count":1,
            "items":[{"alias":"PogU","emote":{"id":"e1","default_name":"PogChamp","scores":{"top_all_time":1,"trending_day":1}}}]
        }}}}}
        """;

    // Wires the REAL ProviderRequestTelemetryHandler ahead of the client, under the pre-existing
    // SevenTvRest call source — exactly the registration ServiceCollectionExtensions uses in
    // production. If suppression ever regressed, these tests would see two observations instead of
    // one, one of them wrongly filed under SevenTvRest.
    private static SevenTvApiClient CreateClient(HttpResponseMessage response, IRateLimitTelemetry telemetry)
    {
        var handler = new ProviderRequestTelemetryHandler(RateLimitProviders.SevenTv, RateLimitCallSources.SevenTvRest, telemetry)
        {
            InnerHandler = new StubHandler(response),
        };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(httpClient, telemetry, new RecordingForeignUpstreamRequestBudget(), new RecordingSevenTvSearchBudget(), new RecordingLogger<SevenTvApiClient>());
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
