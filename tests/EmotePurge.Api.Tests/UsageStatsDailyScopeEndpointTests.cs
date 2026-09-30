using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The wiring of <c>GET /usage-stats/daily</c>'s set scope. The combination rule itself is pinned
/// against the pure <see cref="EmoteSetScopeParserTests"/>; the handler stays thin delegation (rule
/// 11), so these two cases only prove the parser is actually on the route: the service is asked for
/// the scope the query named, and a contradictory query never reaches it.
/// </summary>
public class UsageStatsDailyScopeEndpointTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string Daily = $"/api/channels/{Channel}/usage-stats/daily?emoteId=e1&from=2026-08-01&to=2026-08-02";

    private readonly ApiFactory _factory;

    public UsageStatsDailyScopeEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.UsageStats.ClearReceivedCalls();
        _factory.ChannelAccess.ClearReceivedCalls();
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    public async Task ScopeAll_AsksTheServiceForEverySet()
    {
        await SendAsync("&setScope=all");

        await _factory.UsageStats.Received(1).GetDailySeriesAsync(
            Channel, "e1", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2), EmoteSetScope.AllSets, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScopeAllWithASetId_Answers400InvalidEmoteSetId_WithoutAskingTheService()
    {
        var response = await SendAsync("&setScope=all&emoteSetId=x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.InvalidEmoteSetId, body.RootElement.GetProperty("errorCode").GetString());
        await _factory.UsageStats.DidNotReceive().GetDailySeriesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>());
    }

    private async Task<HttpResponseMessage> SendAsync(string query)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Get, Daily + query);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return await client.SendAsync(request);
    }
}
