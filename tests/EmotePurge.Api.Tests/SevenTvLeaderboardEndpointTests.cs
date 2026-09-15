using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The filter matrix for <c>GET /api/seventv/leaderboard</c> (7TV-leaderboard-as-import-source spec
/// 2026-09-13, section 4/9, AK 1-3 and 26) — modeled on
/// <see cref="SevenTvForeignEmoteSetEndpointTests"/>: this group carries neither
/// <see cref="ChannelNameValidationFilter"/> (there is no channel name here at all) nor a
/// <c>UsageStatsAccessAuthorizationFilter</c> (there is no role to check on a network-wide ranking),
/// and the 429-before-400 ordering below is the same deliberate middleware-before-endpoint-filter
/// contract as that group's.
/// </summary>
public class SevenTvLeaderboardEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public SevenTvLeaderboardEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Leaderboard.ClearReceivedCalls();
    }

    [Fact]
    public async Task AnonymousCaller_Gets401()
    {
        var response = await SendAsync(SevenTvLeaderboardSortWireCode.TrendingDailyWireCode, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(SevenTvLeaderboardSortWireCode.TrendingDailyWireCode)]
    [InlineData(SevenTvLeaderboardSortWireCode.TopAllTimeWireCode)]
    public async Task LoggedInCaller_Gets200_WithSortByEcho(string wireCode)
    {
        Assert.True(SevenTvLeaderboardSortWireCode.TryParse(wireCode, out var sort));
        var response = new SevenTvLeaderboardResponse(
            wireCode, 705, true,
            [new ForeignEmoteRow("e1", "Name", "Name", "https://cdn.7tv.app/emote/e1/4x_static.webp", 500, 12)]);
        _factory.Leaderboard.GetLeaderboardAsync(sort, Arg.Any<CancellationToken>())
            .Returns(SevenTvLeaderboardResult.Ok(response));

        var httpResponse = await SendAsync(wireCode, NewUserId());

        Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
        var body = JsonDocument.Parse(await httpResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(wireCode, body.GetProperty("sortBy").GetString());
        Assert.Equal(705, body.GetProperty("totalCount").GetInt32());
        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, body.GetProperty("emotes").GetArrayLength());
    }

    [Theory]
    [InlineData(null)] // missing entirely
    [InlineData("trending_daily")] // right vocabulary, wrong casing — TryParse is strict-ordinal
    [InlineData("TRENDING_WEEKLY")] // not in the two-member allowlist at all
    public async Task InvalidSortBy_Gets400_WithoutCallingTheService(string? sortBy)
    {
        var response = await SendAsync(sortBy, NewUserId());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidLeaderboardSort, await ReadErrorCodeAsync(response));

        // AK 2: no upstream, and no call to the service substitute at all.
        await _factory.Leaderboard.DidNotReceive().GetLeaderboardAsync(
            Arg.Any<SevenTvLeaderboardSort>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AK 3 / spec section 4: <c>UseRateLimiter</c> is middleware and runs before
    /// <see cref="LeaderboardSortValidationFilter"/> can, so once the per-user budget (20/min) is spent
    /// an invalid sortBy no longer gets the 400 the previous test just proved it gets with budget to
    /// spare — it gets 429 like everything else against this group. Deliberate, not a bug (mirrors
    /// <c>SevenTvForeignEmoteSetEndpointTests.InvalidChannelName_OverBudget_Gets429_...</c>).
    /// </summary>
    [Fact]
    public async Task InvalidSortBy_OverBudget_Gets429_NotThe400ItWouldGetOtherwise()
    {
        var userId = NewUserId();
        _factory.Leaderboard.GetLeaderboardAsync(Arg.Any<SevenTvLeaderboardSort>(), Arg.Any<CancellationToken>())
            .Returns(SevenTvLeaderboardResult.Ok(
                new SevenTvLeaderboardResponse(SevenTvLeaderboardSortWireCode.TrendingDailyWireCode, 0, false, [])));

        // Spend the SevenTvLeaderboard budget (20/min, RateLimitingOptions.SevenTvLeaderboard) on
        // valid requests first — LeaderboardSortValidationFilter never rejects these, so every one of
        // them is genuinely a permit spent, not a request the filter would have refused anyway.
        for (var i = 0; i < 20; i++)
        {
            var spend = await SendAsync(SevenTvLeaderboardSortWireCode.TrendingDailyWireCode, userId);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, spend.StatusCode);
        }

        var response = await SendAsync("TRENDING_WEEKLY", userId);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Theory]
    [InlineData(SevenTvLeaderboardStatus.SevenTvUnavailable)]
    [InlineData(SevenTvLeaderboardStatus.SevenTvRateLimited)]
    [InlineData(SevenTvLeaderboardStatus.BudgetRefused)]
    public async Task EveryFailureStatus_Maps503WithTheExistingForeignChannelCode(SevenTvLeaderboardStatus status)
    {
        _factory.Leaderboard.GetLeaderboardAsync(Arg.Any<SevenTvLeaderboardSort>(), Arg.Any<CancellationToken>())
            .Returns(SevenTvLeaderboardResult.Failed(status));

        var response = await SendAsync(SevenTvLeaderboardSortWireCode.TrendingDailyWireCode, NewUserId());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ForeignChannelSevenTvUnavailable, await ReadErrorCodeAsync(response));
    }

    /// <summary>
    /// AK 3 on the endpoint level (spec section 4): <c>page</c>, <c>perPage</c>, <c>refresh</c>,
    /// <c>query</c>, <c>tags</c> and <c>filters</c> are not parameters this handler knows about — an
    /// unrecognized query string is ignored, not rejected, and the service substitute still sees
    /// exactly one call for exactly the parsed <c>sortBy</c>.
    /// </summary>
    [Fact]
    public async Task UnknownQueryParameters_DoNotChangeTheCall()
    {
        _factory.Leaderboard.GetLeaderboardAsync(SevenTvLeaderboardSort.TrendingDaily, Arg.Any<CancellationToken>())
            .Returns(SevenTvLeaderboardResult.Ok(
                new SevenTvLeaderboardResponse(SevenTvLeaderboardSortWireCode.TrendingDailyWireCode, 0, false, [])));

        var response = await SendAsync(
            SevenTvLeaderboardSortWireCode.TrendingDailyWireCode,
            NewUserId(),
            additionalQuery: "refresh=true&page=3&perPage=250&query=foo&tags=bar&filters=baz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.Leaderboard.Received(1).GetLeaderboardAsync(
            SevenTvLeaderboardSort.TrendingDaily, Arg.Any<CancellationToken>());
    }

    private static string NewUserId() => Guid.NewGuid().ToString("N");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(string? sortBy, string? userId, string additionalQuery = "")
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var queryParts = new List<string>();
        if (sortBy is not null)
        {
            queryParts.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
        }
        if (additionalQuery.Length > 0)
        {
            queryParts.Add(additionalQuery);
        }
        var path = "/api/seventv/leaderboard" + (queryParts.Count > 0 ? "?" + string.Join("&", queryParts) : "");

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        return await client.SendAsync(request);
    }
}
