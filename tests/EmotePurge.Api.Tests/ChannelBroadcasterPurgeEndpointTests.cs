using System.Net;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The broadcaster self-purge surface (#245): <c>DELETE /data</c>, <c>GET /data-summary</c>, the
/// lock-aware join arms, the tracked-channel gate on the audit log and <c>canPurgeAsBroadcaster</c>.
/// Kept apart from <see cref="AuthFilterMatrixTests"/>, which is already past 1,500 lines.
/// </summary>
public class ChannelBroadcasterPurgeEndpointTests : IClassFixture<ApiFactory>
{
    private const string Channel = "testchannel";
    private const string OwnerId = "4711";
    private readonly ApiFactory _factory;

    public ChannelBroadcasterPurgeEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        factory.Channels.ClearReceivedCalls();
        factory.ChannelAccess.ClearReceivedCalls();
        factory.AuditLogQuery.ClearReceivedCalls();
        factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
    }

    [Fact]
    public async Task Data_Answers400_ForAMalformedName_BeforeAnythingElse()
    {
        var response = await SendAsync("DELETE", "/api/channels/bad-name/data?expectedTwitchUserId=" + OwnerId, OwnerId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
        await _factory.Channels.DidNotReceive().GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Data_Answers404_WithoutARow_AndNeverCallsTheService()
    {
        ArrangeRow(null);

        var response = await SendAsync("DELETE", DataPath(OwnerId), OwnerId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await _factory.Channels.DidNotReceive()
            .PurgeByBroadcasterAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Data_Answers403_ForARowWithAForeignId_AndNeverCallsTheService()
    {
        ArrangeRow(RowWithId("999"));

        var response = await SendAsync("DELETE", DataPath(OwnerId), OwnerId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.Channels.DidNotReceive()
            .PurgeByBroadcasterAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Data_Answers403_ForAGlobalAdminWithoutTheBroadcasterId()
    {
        // No admin override (E7): the purge is the owner's alone.
        ArrangeRow(RowWithId("999"));
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);

        var response = await SendAsync("DELETE", DataPath(OwnerId), OwnerId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.Channels.DidNotReceive()
            .PurgeByBroadcasterAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Data_Answers403_ForAModerator_WithoutAskingTheManagementCheck()
    {
        ArrangeRow(RowWithId("999"));
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await SendAsync("DELETE", DataPath(OwnerId), OwnerId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.ChannelAccess.DidNotReceive()
            .CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("someone-else")]
    public async Task Data_Answers409AccountMismatch_WhenTheExpectedIdIsMissingOrWrong_BeforeTheService(string? expected)
    {
        ArrangeRow(RowWithId(OwnerId));
        var path = expected is null
            ? $"/api/channels/{Channel}/data"
            : DataPath(expected);

        var response = await SendAsync("DELETE", path, OwnerId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.AccountMismatch, await ReadErrorCodeAsync(response));
        await _factory.Channels.DidNotReceive()
            .PurgeByBroadcasterAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ChannelBroadcasterPurgeResult.Purged, HttpStatusCode.NoContent, null)]
    [InlineData(ChannelBroadcasterPurgeResult.NotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound)]
    [InlineData(ChannelBroadcasterPurgeResult.NotBroadcaster, HttpStatusCode.Forbidden, null)]
    [InlineData(ChannelBroadcasterPurgeResult.IdentityUnresolved, HttpStatusCode.Conflict, ApiErrorCodes.ChannelIdentityUnresolved)]
    public async Task Data_MapsTheServiceResult(ChannelBroadcasterPurgeResult result, HttpStatusCode expectedStatus, string? expectedCode)
    {
        ArrangeRow(RowWithId(OwnerId));
        _factory.Channels.PurgeByBroadcasterAsync(Channel, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var response = await SendAsync("DELETE", DataPath(OwnerId), OwnerId);

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedCode is not null)
        {
            Assert.Equal(expectedCode, await ReadErrorCodeAsync(response));
        }

        await _factory.Channels.Received(1)
            .PurgeByBroadcasterAsync(Channel, Arg.Is<AuditActor>(a => a.TwitchUserId == OwnerId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Data_LetsAnIdlessRow_ThroughToTheService()
    {
        // The filter cannot prove ownership of a row without a stored id; the service does, live.
        ArrangeRow(new Channel { ChannelName = Channel });
        _factory.Channels.PurgeByBroadcasterAsync(Channel, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(ChannelBroadcasterPurgeResult.NotBroadcaster);

        var response = await SendAsync("DELETE", DataPath(OwnerId), OwnerId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.Channels.Received(1)
            .PurgeByBroadcasterAsync(Channel, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DataSummary_Answers404_WithoutARow()
    {
        ArrangeRow(null);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/data-summary", OwnerId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DataSummary_Answers403_ForAForeignId()
    {
        ArrangeRow(RowWithId("999"));

        var response = await SendAsync("GET", $"/api/channels/{Channel}/data-summary", OwnerId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.Channels.DidNotReceive().GetDataSummaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DataSummary_Answers403_ForAnIdLessRow_WhoseNameIsNotTheCallersLogin()
    {
        // The purge may let an id-less row through, because the service proves it live against Twitch.
        // The summary has no such proof, so it admits only the caller whose current login is the row's
        // name — otherwise any logged-in account could read a stranger's numbers.
        ArrangeRow(new Channel { ChannelName = Channel });

        var response = await SendAsync("GET", $"/api/channels/{Channel}/data-summary", OwnerId, login: "someoneelse");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.Channels.DidNotReceive().GetDataSummaryAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DataSummary_Answers200_ForAnIdLessRow_WhoseNameIsTheCallersLogin()
    {
        // Compared on the normalized login, like canPurgeAsBroadcaster.
        ArrangeRow(new Channel { ChannelName = Channel });
        _factory.Channels.GetDataSummaryAsync(Channel, OwnerId, Arg.Any<CancellationToken>())
            .Returns(new ChannelDataSummary(EmoteCount: 1, VoteSessionCount: 0, LiveDayCount: 0, TagCount: 0));

        var response = await SendAsync("GET", $"/api/channels/{Channel}/data-summary", OwnerId, login: "TestChannel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DataSummary_Answers200_WithAllFourCounts()
    {
        ArrangeRow(RowWithId(OwnerId));
        _factory.Channels.GetDataSummaryAsync(Channel, OwnerId, Arg.Any<CancellationToken>())
            .Returns(new ChannelDataSummary(EmoteCount: 12, VoteSessionCount: 3, LiveDayCount: 40, TagCount: 5));

        var response = await SendAsync("GET", $"/api/channels/{Channel}/data-summary", OwnerId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(12, root.GetProperty("emoteCount").GetInt32());
        Assert.Equal(3, root.GetProperty("voteSessionCount").GetInt32());
        Assert.Equal(40, root.GetProperty("liveDayCount").GetInt32());
        Assert.Equal(5, root.GetProperty("tagCount").GetInt32());
    }

    [Fact]
    public async Task DataSummary_Answers404_WhenTheServiceFindsNoRow()
    {
        // The row vanished between the filter and the service.
        ArrangeRow(RowWithId(OwnerId));
        _factory.Channels.GetDataSummaryAsync(Channel, OwnerId, Arg.Any<CancellationToken>())
            .Returns((ChannelDataSummary?)null);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/data-summary", OwnerId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Join_Answers403_WithTheLockCode_ForANonAdmin()
    {
        ArrangeJoin(isAdmin: false, ChannelJoinResult.LockedByBroadcaster(new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc)));

        var response = await SendAsync("POST", $"/api/channels/{Channel}/join", OwnerId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelLockedByBroadcaster, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Join_Answers409_WithCodeAndIsoDate_ForAnAdminWithoutTheFlag()
    {
        ArrangeJoin(isAdmin: true, ChannelJoinResult.LockedByBroadcaster(new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc)));

        var response = await SendAsync("POST", $"/api/channels/{Channel}/join", OwnerId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.ChannelLockedByBroadcaster, doc.RootElement.GetProperty("errorCode").GetString());
        // The raw text, not a parsed DateTime: the wire format is ISO-8601 with an explicit UTC marker.
        Assert.Equal("2026-10-03T12:00:00Z", doc.RootElement.GetProperty("lockedAtUtc").GetString());
        await _factory.Channels.Received(1)
            .JoinAsync(Channel, Arg.Any<AuditActor>(), true, false, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, "true", true)]
    [InlineData(true, "false", false)]
    [InlineData(false, "true", false)]
    public async Task Join_PassesTheLiftFlagToTheService_OnlyForAGlobalAdmin(bool isAdmin, string flag, bool expectedLift)
    {
        ArrangeJoin(isAdmin, ChannelJoinResult.Joined(new Channel { ChannelName = Channel }));

        var response = await SendAsync("POST", $"/api/channels/{Channel}/join?liftBroadcasterLock={flag}", OwnerId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.Channels.Received(1)
            .JoinAsync(Channel, Arg.Any<AuditActor>(), isAdmin, expectedLift, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditLog_Answers404_WithoutARow_EvenForAnAccountWithTheSameLogin()
    {
        ArrangeRow(null);
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/audit-log", OwnerId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(response));
        await _factory.ChannelAccess.DidNotReceive()
            .CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditLog_BoundsTheLogByTheCurrentRowsCreatedAt()
    {
        var createdAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        ArrangeRow(new Channel { ChannelName = Channel, CreatedAt = createdAt });
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/audit-log", OwnerId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.AuditLogQuery.Received(1).ListAsync(
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Is<AuditLogFilter?>(f => f != null && f.ChannelName == Channel && f.OccurredAfterUtc == createdAt),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("4711", "other", true)]       // id match
    [InlineData("999", "testchannel", false)] // foreign id, even with a matching login
    [InlineData(null, "testchannel", true)]   // id-less row, login match: visibility only
    [InlineData(null, "TestChannel", true)]   // login compared normalized
    [InlineData(null, "other", false)]        // id-less row, other login
    public async Task Permissions_ReportsCanPurgeAsBroadcaster(string? rowId, string login, bool expected)
    {
        ArrangeRow(new Channel { ChannelName = Channel, TwitchChannelId = rowId });

        var response = await SendAsync("GET", $"/api/channels/{Channel}/permissions", OwnerId, login);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, doc.RootElement.GetProperty("canPurgeAsBroadcaster").GetBoolean());
    }

    [Fact]
    public async Task Permissions_ReportsFalse_WithoutARow()
    {
        ArrangeRow(null);

        var response = await SendAsync("GET", $"/api/channels/{Channel}/permissions", OwnerId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("canPurgeAsBroadcaster").GetBoolean());
    }

    private static string DataPath(string expected) =>
        $"/api/channels/{Channel}/data?expectedTwitchUserId={Uri.EscapeDataString(expected)}";

    private static Channel RowWithId(string twitchChannelId) =>
        new() { ChannelName = Channel, TwitchChannelId = twitchChannelId };

    private void ArrangeRow(Channel? row) =>
        _factory.Channels.GetByNameAsync(Channel, Arg.Any<CancellationToken>()).Returns(row);

    private void ArrangeJoin(bool isAdmin, ChannelJoinResult result)
    {
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(isAdmin);
        _factory.Channels.JoinAsync(Channel, Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(result);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string userId, string login = "someuser")
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        // The user id is the claim under test here (the broadcaster id), so it cannot be randomized
        // per request like the matrix does; each test sends a handful of requests, far below the
        // rate-limit budgets.
        request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
        request.Headers.Add(TestAuthHandler.LoginHeader, login);
        return await client.SendAsync(request);
    }
}
