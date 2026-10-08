using System.Net;
using System.Text.Json;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The handler contract of <c>GET /api/channels/{channelName}/emote-sets/{emoteSetId}/emotes</c>
/// (#220): membership before preview, the preview's status mapping shared with the foreign-channel
/// route, and the route's own rate-limit bucket. The filter order (400/401/403) is pinned in
/// <c>AuthFilterMatrixTests</c>, the policy name in <c>EmoteRoutePolicyTests</c>.
/// </summary>
public class TrackedEmoteSetPreviewEndpointTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";

    private readonly ApiFactory _factory;

    public TrackedEmoteSetPreviewEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ChannelAccess.ClearReceivedCalls();
        _factory.ForeignEmoteSet.ClearReceivedCalls();
        _factory.TrackedEmoteSetMembership.ClearReceivedCalls();

        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.TrackedEmoteSetMembership.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TrackedEmoteSetMembership.Member);
    }

    [Fact]
    public async Task NotMember_Answers404EmoteSetNotFound_WithoutReadingThePreview()
    {
        _factory.TrackedEmoteSetMembership.CheckAsync(Channel, SetId, Arg.Any<CancellationToken>())
            .Returns(TrackedEmoteSetMembership.NotMember);

        var response = await SendAsync(NewUserId());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.EmoteSetNotFound, await ReadErrorCodeAsync(response));
        await _factory.ForeignEmoteSet.DidNotReceive().GetForeignEmoteSetBySetIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SevenTvUnavailable_Answers503_WithoutReadingThePreview()
    {
        _factory.TrackedEmoteSetMembership.CheckAsync(Channel, SetId, Arg.Any<CancellationToken>())
            .Returns(TrackedEmoteSetMembership.SevenTvUnavailable);

        var response = await SendAsync(NewUserId());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ForeignChannelSevenTvUnavailable, await ReadErrorCodeAsync(response));
        await _factory.ForeignEmoteSet.DidNotReceive().GetForeignEmoteSetBySetIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Member_Answers200WithThePreviewUnchanged()
    {
        var emoteSet = new ForeignEmoteSet(
            Channel, null, SetId, 1, false,
            [new ForeignEmoteRow("e1", "Alias", "Default", "https://cdn.7tv.app/emote/e1/4x_static.webp", 500, 12)],
            "Some set", 956);
        _factory.ForeignEmoteSet.GetForeignEmoteSetBySetIdAsync(Channel, SetId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(emoteSet));

        var response = await SendAsync(NewUserId());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(SetId, body.GetProperty("emoteSetId").GetString());
        Assert.Equal("Some set", body.GetProperty("emoteSetName").GetString());
        Assert.Equal(956, body.GetProperty("capacity").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sevenTvUserId").ValueKind);
        Assert.Single(body.GetProperty("emotes").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Member_PassesRefreshToThePreviewRead_AndDefaultsToFalse(bool refresh)
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetBySetIdAsync(Channel, SetId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(new ForeignEmoteSet(Channel, null, SetId, 0, false, [])));

        var response = await SendAsync(NewUserId(), query: refresh ? "?refresh=true" : string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.ForeignEmoteSet.Received(1).GetForeignEmoteSetBySetIdAsync(Channel, SetId, refresh, Arg.Any<CancellationToken>());
        // The membership proof is not bypassed by refresh.
        await _factory.TrackedEmoteSetMembership.Received(1).CheckAsync(Channel, SetId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ForeignEmoteSetLookupStatus.ChannelNotOnTwitch, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotOnTwitch)]
    [InlineData(ForeignEmoteSetLookupStatus.TwitchUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelTwitchUnavailable)]
    [InlineData(ForeignEmoteSetLookupStatus.NoSevenTvAccount, HttpStatusCode.NotFound, ApiErrorCodes.ForeignChannelNoSevenTvAccount)]
    [InlineData(ForeignEmoteSetLookupStatus.NoActiveEmoteSet, HttpStatusCode.NotFound, ApiErrorCodes.ForeignChannelNoActiveEmoteSet)]
    [InlineData(ForeignEmoteSetLookupStatus.SevenTvUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    [InlineData(ForeignEmoteSetLookupStatus.SevenTvRateLimited, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    [InlineData(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    public async Task Member_EveryFailedPreviewStatus_MapsToTheSameAnswerAsTheForeignRoute(
        ForeignEmoteSetLookupStatus status, HttpStatusCode expectedStatusCode, string expectedErrorCode)
    {
        _factory.ForeignEmoteSet.GetForeignEmoteSetBySetIdAsync(Channel, SetId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(status));

        var response = await SendAsync(NewUserId());

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(expectedErrorCode, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task OverBudget_Gets429()
    {
        var userId = NewUserId();
        StubSuccessfulPreview();

        await SpendAsync(TrackedPermits, userId);

        var response = await SendAsync(userId);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task SpentTrackedBucket_LeavesTheForeignRouteServing_ForTheSameUser()
    {
        var userId = NewUserId();
        StubSuccessfulPreview();
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        await SpendAsync(TrackedPermits, userId);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(userId)).StatusCode);

        var foreign = await SendForeignAsync(userId);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, foreign.StatusCode);
    }

    [Fact]
    public async Task SpentForeignBucket_LeavesTheTrackedRouteServing_ForTheSameUser()
    {
        var userId = NewUserId();
        StubSuccessfulPreview();
        _factory.ForeignEmoteSet.GetForeignEmoteSetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount));

        for (var i = 0; i < ForeignPermits; i++)
        {
            await SendForeignAsync(userId);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendForeignAsync(userId)).StatusCode);

        var tracked = await SendAsync(userId);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, tracked.StatusCode);
    }

    private static int TrackedPermits => new RateLimitingOptions().TrackedEmoteSetPreview.PermitLimit;

    private static int ForeignPermits => new RateLimitingOptions().ForeignEmoteLookup.PermitLimit;

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private void StubSuccessfulPreview() =>
        _factory.ForeignEmoteSet.GetForeignEmoteSetBySetIdAsync(Channel, SetId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(new ForeignEmoteSet(Channel, null, SetId, 0, false, [])));

    private async Task SpendAsync(int permits, string userId)
    {
        for (var i = 0; i < permits; i++)
        {
            var spend = await SendAsync(userId);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, spend.StatusCode);
        }
    }

    private Task<HttpResponseMessage> SendAsync(string userId, string query = "") =>
        SendRequestAsync($"/api/channels/{Channel}/emote-sets/{SetId}/emotes{query}", userId);

    private Task<HttpResponseMessage> SendForeignAsync(string userId) =>
        SendRequestAsync($"/api/seventv/channels/{Channel}/emotes", userId);

    private async Task<HttpResponseMessage> SendRequestAsync(string path, string userId)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return await client.SendAsync(request);
    }
}
