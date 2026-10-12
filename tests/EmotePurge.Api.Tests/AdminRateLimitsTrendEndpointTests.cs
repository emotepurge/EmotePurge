using System.Net;
using System.Text.Json;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary><c>GET /api/admin/rate-limits/trend</c> (#381).</summary>
public class AdminRateLimitsTrendEndpointTests : IClassFixture<ApiFactory>
{
    private const string Path = "/api/admin/rate-limits/trend";

    private readonly ApiFactory _factory;

    public AdminRateLimitsTrendEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ChannelAccess.ClearReceivedCalls();
    }

    [Fact]
    public async Task Get_ReturnsOneValuePerMinuteForTheLast24Hours()
    {
        var to = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var minutes = Enumerable.Range(0, 1440).Select(i => to.AddMinutes(i - 1439)).ToList();
        var reader = Substitute.For<IRateLimitTelemetryReader>();
        reader.ReadTrendAsync(Arg.Any<CancellationToken>()).Returns(new RateLimitTelemetryTrend(
            true,
            minutes[0],
            to,
            minutes,
            Enumerable.Repeat(0L, 1440).ToList(),
            [new RateLimitProviderTrend("7tv", "rest", Enumerable.Repeat(1L, 1440).ToList())]));

        var (status, body) = await SendAsync(reader);
        using var disposal = body;

        Assert.Equal(HttpStatusCode.OK, status);
        var root = body.RootElement;
        Assert.True(root.GetProperty("telemetryAvailable").GetBoolean());
        Assert.Equal(1440, root.GetProperty("minuteStartsUtc").GetArrayLength());
        Assert.Equal(1440, root.GetProperty("localRejections").GetArrayLength());
        Assert.Equal(JsonValueKind.String, root.GetProperty("fromUtc").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("toUtc").ValueKind);
        var provider = root.GetProperty("providers")[0];
        Assert.Equal("7tv", provider.GetProperty("providerName").GetString());
        Assert.Equal("rest", provider.GetProperty("callSource").GetString());
        Assert.Equal(1440, provider.GetProperty("rateLimited").GetArrayLength());
    }

    [Fact]
    public async Task Get_WhenTelemetryIsUnavailable_Answers200WithEmptyListsAndNoErrorCode()
    {
        var reader = Substitute.For<IRateLimitTelemetryReader>();
        reader.ReadTrendAsync(Arg.Any<CancellationToken>()).Returns(RateLimitTelemetryTrend.Unavailable);

        var (status, body) = await SendAsync(reader);
        using var disposal = body;

        Assert.Equal(HttpStatusCode.OK, status);
        var root = body.RootElement;
        Assert.False(root.GetProperty("telemetryAvailable").GetBoolean());
        Assert.Equal(0, root.GetProperty("minuteStartsUtc").GetArrayLength());
        Assert.Equal(0, root.GetProperty("localRejections").GetArrayLength());
        Assert.Equal(0, root.GetProperty("providers").GetArrayLength());
        Assert.False(root.TryGetProperty("errorCode", out _));
    }

    [Fact]
    public async Task Get_TwiceWithinTheTtl_ReadsTheCountersOnce()
    {
        var reader = Substitute.For<IRateLimitTelemetryReader>();
        reader.ReadTrendAsync(Arg.Any<CancellationToken>()).Returns(RateLimitTelemetryTrend.Unavailable);
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddSingleton(reader)));

        (await SendAsync(factory)).Body.Dispose();
        (await SendAsync(factory)).Body.Dispose();

        await reader.Received(1).ReadTrendAsync(Arg.Any<CancellationToken>());
    }

    private async Task<(HttpStatusCode StatusCode, JsonDocument Body)> SendAsync(IRateLimitTelemetryReader reader)
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => services.AddSingleton(reader)));
        return await SendAsync(factory);
    }

    private static async Task<(HttpStatusCode StatusCode, JsonDocument Body)> SendAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someadmin");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text));
    }
}
