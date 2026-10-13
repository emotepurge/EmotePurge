using System.Net;
using System.Text;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Telemetry;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// AK 15's second half, against the real store rather than a fake: a foreign-set preview's
/// <c>extensions.status: 429</c> has to actually land in <c>RateLimitTelemetryStore</c> — the store
/// <see cref="EmotePurge.Api.Endpoints"/>'s <c>/api/admin/rate-limits</c> reads — as a rate-limit
/// event under the new <see cref="RateLimitCallSources.SevenTvForeignPreview"/> call source, not as
/// a plain success under the old <see cref="RateLimitCallSources.SevenTvRest"/> one. The suppression
/// and exactly-one-observation contract itself is <c>SevenTvApiClientForeignTelemetryTests</c>'s job
/// (Unit/, no container needed for that part); this file only needs to prove the end of the pipe.
/// </summary>
[Collection("Redis")]
public class SevenTvApiClientForeignTelemetryStoreTests(RedisFixture fixture)
{
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";

    [Fact]
    public async Task DisguisedRateLimit_AppearsInTheStore_AsARateLimitEvent_UnderTheNewCallSource()
    {
        const string rateLimitedPayload =
            """{"data":null,"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""";
        var store = new RateLimitTelemetryStore(fixture.Connection, TimeProvider.System, NullLogger<RateLimitTelemetryStore>.Instance);
        var client = CreateClient(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rateLimitedPayload, Encoding.UTF8, "application/json"),
        }, store);

        var result = await client.GetEmoteSetPreviewAsync(SetId);
        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);

        var snapshot = await store.ReadAsync();
        var foreignCounters = Assert.Single(snapshot.Providers, p =>
            p.ProviderName == RateLimitProviders.SevenTv && p.CallSource == RateLimitCallSources.SevenTvForeignPreview);
        Assert.Equal(1, foreignCounters.RateLimitedLastMinute);
        Assert.Equal(1, foreignCounters.RequestsLastMinute);
        // And it must not also have landed under the shared handler's usual call source — that would
        // mean both the handler and the client reported the same request.
        Assert.DoesNotContain(snapshot.Providers, p => p.CallSource == RateLimitCallSources.SevenTvRest);
    }

    private static SevenTvApiClient CreateClient(HttpResponseMessage response, IRateLimitTelemetry telemetry)
    {
        // The real handler, wired exactly like production (ServiceCollectionExtensions), so a
        // regression in suppression would show up here as a doubled or misfiled count too.
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
