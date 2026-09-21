using System.Net;
using System.Text;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvApiClient.GetEditorOfChannelsAsync"/>'s mapping of
/// <c>editor_of { user { connections { ... } } }</c> onto one grant per Twitch connection. The
/// payload shape is the one the operator measured live against <c>https://7tv.io/v3/gql</c> for T0.6
/// (spec 2026-09-20, section 19) — F17: a fixture invented alongside the query would feed both sides
/// of the test from the same assumption.
/// </summary>
public class SevenTvApiClientEditorOfTests
{
    [Fact]
    public async Task EditorOf_MapsTheMeasuredAnswer_OntoOneGrantPerTwitchConnection()
    {
        const string payload =
            """
            {"data":{"user":{"editor_of":[{"user":{
            "connections":[{"platform":"TWITCH","id":"49140130","username":"handofblood"}]}}]}}}
            """;
        var client = CreateClient(payload);

        var result = await client.GetEditorOfChannelsAsync("some-actor-seventv-id");

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        var grant = Assert.Single(result.Grants!);
        Assert.Equal("handofblood", grant.TwitchChannelLogin);
        Assert.Equal("49140130", grant.TwitchChannelId);
    }

    private static SevenTvApiClient CreateClient(string jsonPayload)
    {
        var handler = new StubHandler(jsonPayload);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(httpClient, new RecordingRateLimitTelemetry(), new RecordingForeignUpstreamRequestBudget(), new RecordingLogger<SevenTvApiClient>());
    }

    private sealed class StubHandler(string jsonPayload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
