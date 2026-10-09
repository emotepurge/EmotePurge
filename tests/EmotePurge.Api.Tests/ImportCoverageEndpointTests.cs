using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// <c>GET /api/channels/{channelName}/usage-stats/import-coverage</c> (#351, spec 5.6): the disclosure
/// read behind the usage page's "counted since / imported from the archive" caption. Wide audience
/// (7TV editors included), not behind the backfill flag, scope from the query. The 401/400 filter
/// order is in <c>AuthFilterMatrixTests</c>.
/// </summary>
public class ImportCoverageEndpointTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string ChannelId = "channel-guid";
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";

    private readonly ApiFactory _factory;

    public ImportCoverageEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.ChatLogBackfill.ClearReceivedCalls();
        _factory.ChannelAccess.ClearReceivedCalls();

        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.Channels.GetByNameAsync(Channel, Arg.Any<CancellationToken>())
            .Returns(new Channel { Id = ChannelId, ChannelName = Channel });
    }

    [Fact]
    public async Task AUsageViewerWhoCannotManage_Gets200_LikeA7TvEditor()
    {
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _factory.ChatLogBackfill.GetCoverageAsync(ChannelId, Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>())
            .Returns(Nothing(null));

        var response = await SendAsync(_factory, "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AStranger_Gets403_AndTheServiceIsNeverAsked()
    {
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync(_factory, "");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.ChatLogBackfill.ReceivedCalls());
    }

    [Fact]
    public async Task IsUnaffectedByTheBackfillFlag()
    {
        // Imported rows outlive the flag, so their disclosure must too.
        _factory.ChatLogBackfill.GetCoverageAsync(ChannelId, Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>())
            .Returns(Nothing(null));
        using var disabled = _factory.WithBackfillDisabled();

        var response = await SendAsync(disabled, "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Answers404ChannelNotFound_ForAnUnknownChannel()
    {
        _factory.Channels.GetByNameAsync(Channel, Arg.Any<CancellationToken>()).Returns((Channel?)null);

        var response = await SendAsync(_factory, "");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.ChannelNotFound, json.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("", "active")]
    [InlineData("?emoteSetId=all", "all")]
    [InlineData("?emoteSetId=01FRY81K4800085N93FNKSBYXS", "set")]
    public async Task ReadsTheScopeFromTheQuery(string query, string expected)
    {
        _factory.ChatLogBackfill.GetCoverageAsync(ChannelId, Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>())
            .Returns(Nothing(null));

        var response = await SendAsync(_factory, query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scope = (EmoteSetScope)Assert.Single(_factory.ChatLogBackfill.ReceivedCalls()).GetArguments()[1]!;
        Assert.Equal(expected, scope switch
        {
            { IsActiveSet: true } => "active",
            { IsAllSets: true } => "all",
            _ => "set",
        });
        if (expected == "set")
        {
            Assert.Equal(SetId, scope.SetId);
        }
    }

    [Theory]
    [InlineData("?emoteSetId=")]
    [InlineData("?emoteSetId=has%20space")]
    public async Task Answers400InvalidEmoteSetId_ForAMalformedSetId(string query)
    {
        var response = await SendAsync(_factory, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.InvalidEmoteSetId, json.RootElement.GetProperty("errorCode").GetString());
        Assert.Empty(_factory.ChatLogBackfill.ReceivedCalls());
    }

    [Fact]
    public async Task SerializesTheShapeOfSpec56_WithSourcesFromTheStoredHosts()
    {
        var intervals = new[]
        {
            new ChatLogBackfillCoverageInterval(new DateOnly(2026, 4, 8), new DateOnly(2026, 7, 8), SetId, "logs.cyex.app"),
            new ChatLogBackfillCoverageInterval(new DateOnly(2026, 7, 8), new DateOnly(2026, 10, 8), SetId, "logs.old.example"),
            new ChatLogBackfillCoverageInterval(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 10), SetId, "logs.cyex.app"),
        };
        _factory.ChatLogBackfill.GetCoverageAsync(ChannelId, Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>())
            .Returns(new ChatLogBackfillCoverageDto(
                SetId, new DateOnly(2026, 4, 8), new DateOnly(2026, 10, 10), true, new DateOnly(2026, 7, 8), intervals));

        var response = await SendAsync(_factory, $"?emoteSetId={SetId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","sources":[{"name":"logs.cyex.app","url":"https://logs.cyex.app/"},{"name":"logs.old.example","url":"https://logs.old.example/"}],"importedFrom":"2026-04-08","importedTo":"2026-10-10","hasGaps":true,"contiguousFrom":"2026-07-08","intervals":[{"from":"2026-04-08","to":"2026-07-08","archiveHost":"logs.cyex.app"},{"from":"2026-07-08","to":"2026-10-08","archiveHost":"logs.old.example"},{"from":"2026-10-09","to":"2026-10-10","archiveHost":"logs.cyex.app"}]}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NothingImported_SerializesNullDates_AndEmptySourcesAndIntervals()
    {
        _factory.ChatLogBackfill.GetCoverageAsync(ChannelId, Arg.Any<EmoteSetScope>(), Arg.Any<CancellationToken>())
            .Returns(Nothing(SetId));

        var response = await SendAsync(_factory, $"?emoteSetId={SetId}");

        Assert.Equal(
            """{"emoteSetId":"01FRY81K4800085N93FNKSBYXS","sources":[],"importedFrom":null,"importedTo":null,"hasGaps":false,"contiguousFrom":null,"intervals":[]}""",
            await response.Content.ReadAsStringAsync());
    }

    private static ChatLogBackfillCoverageDto Nothing(string? setId) => new(setId, null, null, false, null, []);

    private static async Task<HttpResponseMessage> SendAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string query)
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/channels/{Channel}/usage-stats/import-coverage{query}");
        request.Headers.Add(TestAuthHandler.UserIdHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        return await client.SendAsync(request);
    }
}
