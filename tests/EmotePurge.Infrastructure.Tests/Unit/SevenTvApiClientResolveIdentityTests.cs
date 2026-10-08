using System.Net;
using System.Text;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins down 7TV's actual contract for <c>userByConnection</c>, which the previous implementation
/// of <see cref="SevenTvApiClient.ResolveSevenTvIdentityAsync"/> got wrong (issue #37): a Twitch id
/// with no linked 7TV account does not come back as a GraphQL <c>null</c>, it comes back as HTTP 200
/// with a placeholder user. All three payloads below were captured live against
/// <c>https://7tv.io/v3/gql</c> on 2026-08-31 — they are not invented, and this is exactly the gap
/// the previous mocked tests could not see because they baked in the same wrong assumption.
/// </summary>
public class SevenTvApiClientResolveIdentityTests
{
    // Measured live 2026-08-31 for a Twitch id with no 7TV account:
    // POST https://7tv.io/v3/gql, query userByConnection(platform: TWITCH, id: <id>)
    // -> HTTP 200 {"data":{"user_by_connection":{"id":"00000000000000000000000000","connections":[]}}}
    private const string PlaceholderUserPayload =
        """{"data":{"user_by_connection":{"id":"00000000000000000000000000","connections":[]}}}""";

    // Measured live 2026-08-31 for a Twitch id WITH a 7TV account:
    // -> HTTP 200 {"data":{"user_by_connection":{"id":"01FRY81K4800085N93FNKSBYXS","connections":
    //      [{"platform":"TWITCH","id":"36340781","emote_set_id":"01FRY81K4800085N93FNKSBYXS"}]}}}
    private const string MatchedUserPayload =
        """
        {"data":{"user_by_connection":{"id":"01FRY81K4800085N93FNKSBYXS","connections":
        [{"platform":"TWITCH","id":"36340781","emote_set_id":"01FRY81K4800085N93FNKSBYXS"}]}}}
        """;

    // Measured live 2026-08-31 for an unknown 7TV ObjectID (a GraphQL error, not "no account"):
    // -> HTTP 200 {"data":null,"errors":[{"message":"LOAD_ERROR user not found",
    //      "extensions":{"code":"LOAD_ERROR","status":404}}]}
    private const string GraphQlErrorPayload =
        """{"data":null,"errors":[{"message":"LOAD_ERROR user not found","extensions":{"code":"LOAD_ERROR","status":404}}]}""";

    [Fact]
    public async Task GraphQlErrorResponse_IsUnavailable_NotNoSevenTvAccount()
    {
        var client = CreateClient(GraphQlErrorPayload);

        var result = await client.ResolveSevenTvIdentityAsync("999999999");

        Assert.Equal(SevenTvLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task PlaceholderUser_WithEmptyConnections_IsNoSevenTvAccount()
    {
        var client = CreateClient(PlaceholderUserPayload);

        var result = await client.ResolveSevenTvIdentityAsync("999999999");

        Assert.Equal(SevenTvLookupStatus.NoSevenTvAccount, result.Status);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task MatchingTwitchConnection_IsOk_WithUserIdAndActiveEmoteSetId()
    {
        var client = CreateClient(MatchedUserPayload);

        var result = await client.ResolveSevenTvIdentityAsync("36340781");

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.NotNull(result.Identity);
        Assert.Equal("01FRY81K4800085N93FNKSBYXS", result.Identity!.SevenTvUserId);
        Assert.Equal("01FRY81K4800085N93FNKSBYXS", result.Identity.ActiveEmoteSetId);
    }

    /// <summary>
    /// The distinction that must not break: an account exists and the Twitch connection is real,
    /// but it currently has no active emote set. This is a legitimate <c>Ok</c> with a null
    /// <c>ActiveEmoteSetId</c> — a naive "connection found, but EmoteSetId is null, so treat it like
    /// the placeholder" fix would collapse this back into NoSevenTvAccount.
    /// </summary>
    [Fact]
    public async Task MatchingTwitchConnection_WithNullEmoteSetId_IsStillOk()
    {
        const string payload =
            """{"data":{"user_by_connection":{"id":"01FRY81K4800085N93FNKSBYXS","connections":[{"platform":"TWITCH","id":"36340781","emote_set_id":null}]}}}""";
        var client = CreateClient(payload);

        var result = await client.ResolveSevenTvIdentityAsync("36340781");

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        Assert.NotNull(result.Identity);
        Assert.Equal("01FRY81K4800085N93FNKSBYXS", result.Identity!.SevenTvUserId);
        Assert.Null(result.Identity.ActiveEmoteSetId);
    }

    private static SevenTvApiClient CreateClient(string jsonPayload)
    {
        var handler = new StubHandler(jsonPayload);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(httpClient, new RecordingRateLimitTelemetry(), new RecordingForeignUpstreamRequestBudget(), new RecordingSevenTvSearchBudget(), new RecordingLogger<SevenTvApiClient>());
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
