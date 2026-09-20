using System.Net;
using System.Text;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvApiClient.GetEditorOfChannelsAsync"/>'s mapping of the additive
/// <c>user.id</c> field on <c>editor_of { user { id connections { ... } } } }</c> (spec 2026-09-20,
/// E22 — the set-centric import's owner check, T2.4). The query shape and the 7TV account id
/// (<c>01FY9A4ZG8000BH1HKPGP0R1S0</c>) are the ones the operator measured live against
/// <c>https://7tv.io/v3/gql</c> for T0.6 (spec section 19, "Prüfaufgabe erledigt") — the field exists
/// on 7TV's actual response, this is not a papered-over assumption (F17: a wrong query looks
/// identical to a real outage, so the fixture must be built from what 7TV actually answered).
/// </summary>
public class SevenTvApiClientEditorOfTests
{
    private const string MeasuredOwnerSevenTvUserId = "01FY9A4ZG8000BH1HKPGP0R1S0";

    [Fact]
    public async Task EditorOf_MapsTheMeasuredUserId_OntoEveryGrantOfThatUser()
    {
        const string payload =
            """
            {"data":{"user":{"editor_of":[{"user":{"id":"01FY9A4ZG8000BH1HKPGP0R1S0",
            "connections":[{"platform":"TWITCH","id":"49140130","username":"handofblood"}]}}]}}}
            """;
        var client = CreateClient(payload);

        var result = await client.GetEditorOfChannelsAsync("some-actor-seventv-id");

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        var grant = Assert.Single(result.Grants!);
        Assert.Equal("handofblood", grant.TwitchChannelLogin);
        Assert.Equal("49140130", grant.TwitchChannelId);
        Assert.Equal(MeasuredOwnerSevenTvUserId, grant.SevenTvUserId);
    }

    /// <summary>
    /// A grant whose <c>user</c> object carries no <c>id</c> at all — the shape a response captured
    /// before this field was added to the query would have. Must map to null, never to an empty
    /// string or a throw: the set-centric import's owner check (E22) treats a missing id as "resolve
    /// this channel's identity live", not as a match or a crash.
    /// </summary>
    [Fact]
    public async Task EditorOf_WithNoUserId_MapsSevenTvUserIdToNull()
    {
        const string payload =
            """
            {"data":{"user":{"editor_of":[{"user":
            {"connections":[{"platform":"TWITCH","id":"49140130","username":"handofblood"}]}}]}}}
            """;
        var client = CreateClient(payload);

        var result = await client.GetEditorOfChannelsAsync("some-actor-seventv-id");

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        var grant = Assert.Single(result.Grants!);
        Assert.Equal("handofblood", grant.TwitchChannelLogin);
        Assert.Null(grant.SevenTvUserId);
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
