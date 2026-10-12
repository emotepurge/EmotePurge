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
    private const string DailyBase = $"/api/channels/{Channel}/usage-stats/daily?from=2026-08-01&to=2026-08-02";
    private const string Daily = $"{DailyBase}&emoteId=e1";

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

    // Plan 376 2.5 / AK 30: the drilldown addresses a slot or an emote, and the slot wins when both come.
    [Fact]
    public async Task OnlySlotId_AsksTheSlotMethod_WithTheRoutesChannelAndTheScope()
    {
        var response = await SendRawAsync($"{DailyBase}&slotId=s1&setScope=all");

        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await _factory.UsageStats.Received(1).GetDailySlotSeriesAsync(
            Channel, "s1", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2), EmoteSetScope.AllSets, Arg.Any<CancellationToken>());
        await _factory.UsageStats.DidNotReceive().GetDailySeriesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnlyEmoteId_AsksTheEmoteMethod()
    {
        await SendRawAsync($"{DailyBase}&emoteId=e1");

        await _factory.UsageStats.Received(1).GetDailySeriesAsync(
            Channel, "e1", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>());
        await _factory.UsageStats.DidNotReceive().GetDailySlotSeriesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BothIds_AskTheSlotMethod()
    {
        await SendRawAsync($"{DailyBase}&slotId=s1&emoteId=e1");

        await _factory.UsageStats.Received(1).GetDailySlotSeriesAsync(
            Channel, "s1", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>());
        await _factory.UsageStats.DidNotReceive().GetDailySeriesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("&emoteId=")]
    [InlineData("&slotId=%20&emoteId=%20")]
    public async Task NeitherId_Answers400EmoteIdEmpty_WithoutAskingTheService(string ids)
    {
        var response = await SendRawAsync(DailyBase + ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.EmoteIdEmpty, body.RootElement.GetProperty("errorCode").GetString());
        await _factory.UsageStats.DidNotReceive().GetDailySlotSeriesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>());
        await _factory.UsageStats.DidNotReceive().GetDailySeriesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>());
    }

    // A slot id the service cannot resolve inside the route's channel (unknown, or another channel's) is the
    // same bare 404 as an unknown emote id: the channel is bound by the route, never by the slot.
    [Fact]
    public async Task ASlotTheServiceDoesNotResolveInTheChannel_Answers404()
    {
        _factory.UsageStats.GetDailySlotSeriesAsync(Channel, "foreign", Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>())
            .Returns((EmoteUsageSeriesDto?)null);

        var response = await SendRawAsync($"{DailyBase}&slotId=foreign");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private Task<HttpResponseMessage> SendRawAsync(string pathAndQuery) => SendToAsync(pathAndQuery);

    private async Task<HttpResponseMessage> SendAsync(string query) => await SendToAsync(Daily + query);

    private async Task<HttpResponseMessage> SendToAsync(string pathAndQuery)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return await client.SendAsync(request);
    }
}
