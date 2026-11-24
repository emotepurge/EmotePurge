using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The bot account's admin routes (#487, plan 2.10, T4) over the real pipeline: the admin group's filter on all four
/// routes under both switch states (rule 11), AK73's branches of <c>PUT /token</c> (the outcome mapping; the service's
/// own effects are pinned in <c>BotAccountServiceTests</c>), which routes stay reachable with <c>BotAccount:Enabled</c>
/// off, the body contracts and the exact shape of the read, which has no field a token or seal could travel in (AK74).
/// </summary>
public class AdminBotAccountEndpointTests : IClassFixture<ApiFactory>
{
    private const string Admin = "900001";
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjQxMDI0NDQ4MDB9.c2lnbmF0dXJlLW9mLXRoZS1ib3QtdG9rZW4";

    private readonly ApiFactory _factory;

    public AdminBotAccountEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.BotAccount.ClearReceivedCalls();
        _factory.WishBot.ClearReceivedCalls();
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>())
            .Returns(call => call.Arg<TwitchPrincipalInfo>().TwitchUserId == Admin);
    }

    public static TheoryData<string, string, string?> Routes => new()
    {
        { "GET", "/api/admin/bot-account", null },
        { "PUT", "/api/admin/bot-account/token", $$"""{"token":"{{Token}}"}""" },
        { "DELETE", "/api/admin/bot-account/token", null },
        { "PUT", "/api/admin/bot-account/kill-switch", """{"enabled":true}""" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_Answers401_WithoutASession(string method, string path, string? body)
    {
        var response = await SendAsync(_factory, method, path, body, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.BotAccount.ReceivedCalls());
    }

    // The admin filter sits on the group, so it answers before the switch: a non-admin never learns whether the bot
    // account is on (AK73: "kein Admin → 403").
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_Answers403_ForANonAdmin_UnderBothSwitchStates(string method, string path, string? body)
    {
        using var enabled = _factory.WithBotAccountEnabled();

        var off = await SendAsync(_factory, method, path, body, userId: "123");
        var on = await SendAsync(enabled, method, path, body, userId: "123");

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (off.StatusCode, on.StatusCode));
        Assert.Empty(_factory.BotAccount.ReceivedCalls());
        Assert.Empty(_factory.WishBot.ReceivedCalls());
    }

    // AK73, switch off: 404 after the filter, whatever the body, and the service is never asked.
    [Theory]
    [InlineData("""{"token":"eyJhbGciOiJIUzI1NiJ9.eyJleHAiOjQxMDI0NDQ4MDB9.c2ln"}""")]
    [InlineData("""{"token":"not-a-jwt"}""")]
    [InlineData("")]
    public async Task SwitchOff_PutToken_Answers404BotAccountDisabled(string body)
    {
        var response = await SendAsync(_factory, "PUT", "/api/admin/bot-account/token", body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.BotAccountDisabled, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.BotAccount.ReceivedCalls());
    }

    // Plan 2.10, Revision 4: the read, the delete and the kill switch are the emergency stop and the way back, so they
    // answer with the switch off exactly as with it on.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadDeleteAndKillSwitch_AnswerTheSame_UnderBothSwitchStates(bool switchOn)
    {
        using var enabled = _factory.WithBotAccountEnabled();
        WebApplicationFactory<Program> factory = switchOn ? enabled : _factory;

        var read = await SendAsync(factory, "GET", "/api/admin/bot-account");
        var delete = await SendAsync(factory, "DELETE", "/api/admin/bot-account/token");
        var killSwitch = await SendAsync(factory, "PUT", "/api/admin/bot-account/kill-switch", """{"enabled":true}""");

        Assert.Equal(
            (HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NoContent),
            (read.StatusCode, delete.StatusCode, killSwitch.StatusCode));
        await _factory.BotAccount.Received(1).DeleteTokenAsync(Arg.Is<AuditActor>(a => a.TwitchUserId == Admin), Arg.Any<CancellationToken>());
        await _factory.BotAccount.Received(1).SetKillSwitchAsync(true, Arg.Is<AuditActor>(a => a.TwitchUserId == Admin), Arg.Any<CancellationToken>());
    }

    // Idempotent routes: "nothing deleted" and "same value" are 204 as well.
    [Fact]
    public async Task DeleteWithoutToken_AndUnchangedKillSwitch_Answer204()
    {
        _factory.BotAccount.DeleteTokenAsync(Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns(false);
        _factory.BotAccount.SetKillSwitchAsync(Arg.Any<bool>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns(false);

        var delete = await SendAsync(_factory, "DELETE", "/api/admin/bot-account/token");
        var killSwitch = await SendAsync(_factory, "PUT", "/api/admin/bot-account/kill-switch", """{"enabled":false}""");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (delete.StatusCode, killSwitch.StatusCode));
    }

    // AK73, switch on: every outcome of the service and its answer. The token reaches the service unchanged, with the
    // caller as the actor.
    [Theory]
    [InlineData(BotAccountTokenSetOutcome.Set, HttpStatusCode.NoContent, null)]
    [InlineData(BotAccountTokenSetOutcome.Malformed, HttpStatusCode.BadRequest, ApiErrorCodes.BotAccountTokenMalformed)]
    [InlineData(BotAccountTokenSetOutcome.Expired, HttpStatusCode.BadRequest, ApiErrorCodes.BotAccountTokenExpired)]
    [InlineData(BotAccountTokenSetOutcome.Disabled, HttpStatusCode.NotFound, ApiErrorCodes.BotAccountDisabled)]
    public async Task SwitchOn_PutToken_MapsEveryOutcome(BotAccountTokenSetOutcome outcome, HttpStatusCode status, string? errorCode)
    {
        _factory.BotAccount.SetTokenAsync(Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>()).Returns(outcome);
        using var enabled = _factory.WithBotAccountEnabled();

        var response = await SendAsync(enabled, "PUT", "/api/admin/bot-account/token", $$"""{"token":"{{Token}}"}""");

        Assert.Equal(status, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        if (errorCode is null)
        {
            Assert.Empty(text);
        }
        else
        {
            Assert.Equal(errorCode, await ReadErrorCodeAsync(response));
        }

        // AK74 for the token route: no answer echoes the token.
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        await _factory.BotAccount.Received(1).SetTokenAsync(
            Token, Arg.Is<AuditActor>(a => a.TwitchUserId == Admin && a.Login == "someadmin"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"token":null}""")]
    [InlineData("{}")]
    public async Task SwitchOn_PutToken_WithoutAToken_ReachesTheServiceAsNull(string body)
    {
        _factory.BotAccount.SetTokenAsync(Arg.Any<string?>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(BotAccountTokenSetOutcome.Malformed);
        using var enabled = _factory.WithBotAccountEnabled();

        var response = await SendAsync(enabled, "PUT", "/api/admin/bot-account/token", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.BotAccountTokenMalformed, await ReadErrorCodeAsync(response));
        await _factory.BotAccount.Received(1).SetTokenAsync(null, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SwitchOn_PutToken_WithoutABody_Answers400InvalidRequestBody()
    {
        using var enabled = _factory.WithBotAccountEnabled();

        // An empty JSON body; without any content type the route does not match at all.
        var response = await SendAsync(enabled, "PUT", "/api/admin/bot-account/token", "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.BotAccount.ReceivedCalls());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"enabled":null}""")]
    public async Task KillSwitch_WithoutEnabled_Answers400InvalidRequestBody(string body)
    {
        var response = await SendAsync(_factory, "PUT", "/api/admin/bot-account/kill-switch", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.BotAccount.ReceivedCalls());
    }

    // The read is the spec's shape plus the two numbers of plan 2.10, and nothing else: no member a token, a seal or a
    // fingerprint could travel in (AK74).
    [Fact]
    public async Task Read_HasExactlyTheSpecShape_WithTheWishNumbers()
    {
        var expires = new DateTime(2026, 12, 20, 8, 0, 0, DateTimeKind.Utc);
        _factory.BotAccount.GetAsync(Arg.Any<CancellationToken>()).Returns(new BotAccountStateDto(
            true, BotAccountStatus.Valid, expires, 25, "01BOT", expires.AddDays(-25), null, true, expires.AddDays(-26)));
        _factory.WishBot.GetAdminSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(new WishBotAdminSummary(new WishBotQueueCounts(3, 2, 1), BotChannels: 4, SuspendedChannels: 1, OpenRepairChecks: 5));

        var response = await SendAsync(_factory, "GET", "/api/admin/bot-account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            ["botSevenTvUserId", "expiresAtUtc", "expiresInDays", "killSwitch", "lastErrorCode", "lastVerifiedAtUtc",
                "pausedUntilUtc", "status", "tokenPresent", "wishes"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(root.GetProperty("tokenPresent").GetBoolean());
        Assert.Equal("valid", root.GetProperty("status").GetString());
        Assert.Equal(25, root.GetProperty("expiresInDays").GetInt32());
        Assert.Equal("01BOT", root.GetProperty("botSevenTvUserId").GetString());
        Assert.True(root.GetProperty("killSwitch").GetBoolean());

        var wishes = root.GetProperty("wishes");
        Assert.Equal(
            ["botChannels", "openRepairChecks", "queue", "suspendedChannels"],
            wishes.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var queue = wishes.GetProperty("queue");
        Assert.Equal(
            (3, 2, 1),
            (queue.GetProperty("adds").GetInt32(), queue.GetProperty("confirms").GetInt32(), queue.GetProperty("removals").GetInt32()));
        Assert.Equal(
            (4, 1, 5),
            (wishes.GetProperty("botChannels").GetInt32(), wishes.GetProperty("suspendedChannels").GetInt32(),
                wishes.GetProperty("openRepairChecks").GetInt32()));
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> factory, string method, string path, string? body = null, string? userId = Admin)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someadmin");
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }
}
