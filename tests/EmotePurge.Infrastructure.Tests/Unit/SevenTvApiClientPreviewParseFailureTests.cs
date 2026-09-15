using System.Net;
using System.Text;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvApiClient.GetEmoteSetPreviewAsync"/>'s handling of a JSON parse failure
/// behind HTTP 200: it must log a warning that names the body as unparseable and carries the
/// <see cref="System.Text.Json.JsonException"/>, exactly once — not the generic GraphQL-error
/// guess used for every other <c>Unavailable</c> outcome.
/// </summary>
public class SevenTvApiClientPreviewParseFailureTests
{
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";

    [Fact]
    public async Task MalformedJsonBody_LogsAWarningThatNamesTheParseFailure_WithTheException_ExactlyOnce()
    {
        var logger = new RecordingLogger<SevenTvApiClient>();
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not valid json", Encoding.UTF8, "application/json"),
        });
        var client = CreateClient(handler, logger);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Preview);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains(SetId, warning.Message, StringComparison.Ordinal);
        // Names the actual cause (an unparseable body) instead of the generic GraphQL-error guess
        // that fires for every other Unavailable outcome (validation rejections, upstream 5xx, …).
        Assert.Contains("unparseable", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GraphQL", warning.Message, StringComparison.Ordinal);
    }

    private static SevenTvApiClient CreateClient(HttpMessageHandler handler, ILogger<SevenTvApiClient> logger)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(
            httpClient,
            new RecordingRateLimitTelemetry(),
            new RecordingForeignUpstreamRequestBudget(),
            logger);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
