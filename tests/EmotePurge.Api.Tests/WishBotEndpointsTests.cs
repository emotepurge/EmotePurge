using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The channel routes of the wish bot mode (#487 plan 2.9, T7) over the real pipeline: the activation filter's ladder
/// (switches, admin by id, then the service's decision as 403, 403 or 503) behind the manager filter (AK82, AK85
/// activation part), the settings filter in front of <c>DELETE …/wishes/bot</c>, the handlers' status codes, the read's
/// wire shape, <c>bot-remove</c> and <c>recheck</c> (AK84 route part), and <c>wishBotModeAvailable</c> on
/// <c>/permissions</c>. The group ladder (400, 401, 403, the switch's 404) of these routes runs in
/// <see cref="WishEndpointsTests"/>.
/// </summary>
public class WishBotEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string Manager = "manager-1";
    private const string ActivatePath = $"/api/channels/{Channel}/wishes/bot/activate";
    private const string BotPath = $"/api/channels/{Channel}/wishes/bot";

    private readonly ApiFactory _factory;

    public WishBotEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.WishBot.ClearReceivedCalls();
        _factory.Wishes.ClearReceivedCalls();
        _factory.ChannelAccess.ClearReceivedCalls();

        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        _factory.ChannelAccess.IsGlobalAdminById(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.WishBot.CheckActivationAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(WishBotActivationAccess.Allowed);
        _factory.WishBot.ActivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.Success));
        _factory.WishBot.DeactivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.Success));
    }

    // ---- activate: the activation filter (E24, [W12]) ----

    // (1): with BotAccount:Enabled off the filter asks nothing; the handler answers the service's 409.
    [Fact]
    public async Task Activate_BotSwitchOff_SkipsTheFilter_AndTheHandlerAnswers409()
    {
        _factory.WishBot.ActivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.BotUnavailable));

        var response = await SendAsync(_factory, "POST", ActivatePath);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishBotUnavailable, await ReadErrorCodeAsync(response));
        await _factory.WishBot.DidNotReceiveWithAnyArgs().CheckActivationAccessAsync(default!, default!, default);
        _factory.ChannelAccess.DidNotReceiveWithAnyArgs().IsGlobalAdminById(default!);
    }

    // (1) with the wishes off: the handler's 404, the filter asks nothing.
    [Fact]
    public async Task Activate_WishesOff_Answers404_WithoutAskingTheFilter()
    {
        using var enabled = BotEnabled();
        using var off = enabled.WithWebHostBuilder(b => b.UseSetting("Wishes:Enabled", "false"));

        var response = await SendAsync(off, "POST", ActivatePath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishesDisabled, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.WishBot.ReceivedCalls());
    }

    // (2): a global admin by id passes without the service's decision (an unlisted channel included, AK85).
    [Fact]
    public async Task Activate_AnAdminById_Passes_WithoutTheServicesDecision()
    {
        using var enabled = BotEnabled();
        _factory.ChannelAccess.IsGlobalAdminById(Arg.Any<TwitchPrincipalInfo>()).Returns(true);

        var response = await SendAsync(enabled, "POST", ActivatePath);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _factory.WishBot.DidNotReceiveWithAnyArgs().CheckActivationAccessAsync(default!, default!, default);
        await _factory.WishBot.Received(1).ActivateAsync(Channel, Arg.Is<AuditActor>(a => a.TwitchUserId == Manager), Arg.Any<CancellationToken>());
    }

    // (3) to (5), AK82, AK85: the service's decision becomes 403, 403 or 503 (the only filter that answers 503); the
    // handler never runs.
    [Theory]
    [InlineData(WishBotActivationAccess.NotListed, HttpStatusCode.Forbidden, ApiErrorCodes.WishBotChannelNotListed)]
    [InlineData(WishBotActivationAccess.Forbidden, HttpStatusCode.Forbidden, ApiErrorCodes.WishBotActivationForbidden)]
    [InlineData(WishBotActivationAccess.EditorUnavailable, HttpStatusCode.ServiceUnavailable, ApiErrorCodes.ForeignChannelSevenTvUnavailable)]
    public async Task Activate_TheFiltersRefusals(WishBotActivationAccess access, HttpStatusCode status, string code)
    {
        using var enabled = BotEnabled();
        _factory.WishBot.CheckActivationAccessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(access);

        var response = await SendAsync(enabled, "POST", ActivatePath);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ReadErrorCodeAsync(response));
        await _factory.WishBot.Received(1).CheckActivationAccessAsync(Channel, Manager, Arg.Any<CancellationToken>());
        await _factory.WishBot.DidNotReceiveWithAnyArgs().ActivateAsync(default!, default!, default);
    }

    [Fact]
    public async Task Activate_Allowed_Answers202ActivationPending()
    {
        using var enabled = BotEnabled();

        var response = await SendAsync(enabled, "POST", ActivatePath);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("""{"activationPending":true}""", await response.Content.ReadAsStringAsync());
    }

    // AK82: an active bot mode answers 409 and leaves the activator.
    [Fact]
    public async Task Activate_AnActiveBotMode_Answers409AlreadyActive()
    {
        using var enabled = BotEnabled();
        _factory.WishBot.ActivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.BotAlreadyActive));

        var response = await SendAsync(enabled, "POST", ActivatePath);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishBotAlreadyActive, await ReadErrorCodeAsync(response));
    }

    // Filter order (rule 11): the name's 400, the session's 401 and the manager filter's 403 before the activation
    // filter; a 7TV editor without a Twitch role never reaches the editor question (E24).
    [Theory]
    [InlineData("bad-name", Manager, true, HttpStatusCode.BadRequest)]
    [InlineData(Channel, null, true, HttpStatusCode.Unauthorized)]
    [InlineData(Channel, Manager, false, HttpStatusCode.Forbidden)]
    public async Task Activate_TheGroupLadderRunsFirst(string channel, string? userId, bool manages, HttpStatusCode status)
    {
        using var enabled = BotEnabled();
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(manages);

        var response = await SendAsync(enabled, "POST", $"/api/channels/{channel}/wishes/bot/activate", userId: userId);

        Assert.Equal(status, response.StatusCode);
        Assert.Empty(_factory.WishBot.ReceivedCalls());
        _factory.ChannelAccess.DidNotReceiveWithAnyArgs().IsGlobalAdminById(default!);
    }

    // ---- DELETE …/wishes/bot: the settings filter (D7) ----

    [Fact]
    public async Task Deactivate_TheSettingsFilterRefuses_403Restricted_WithoutTheService()
    {
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync(_factory, "DELETE", BotPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishSettingsRestricted, await ReadErrorCodeAsync(response));
        await _factory.WishBot.DidNotReceiveWithAnyArgs().DeactivateAsync(default!, default!, default, default);
    }

    // Reachable with BotAccount:Enabled off (this factory's default): rollback path (b).
    [Fact]
    public async Task Deactivate_Answers204_AndHandsTheAdminFlagOn()
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);

        var response = await SendAsync(_factory, "DELETE", BotPath);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await _factory.WishBot.Received(1).DeactivateAsync(Channel, Arg.Is<AuditActor>(a => a.TwitchUserId == Manager), true, Arg.Any<CancellationToken>());
    }

    // D7 behind the lock: a confirmation between filter and lock answers the same 403.
    [Fact]
    public async Task Deactivate_RestrictedBehindTheLock_Answers403()
    {
        _factory.WishBot.DeactivateAsync(Arg.Any<string>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.SettingsRestricted));

        var response = await SendAsync(_factory, "DELETE", BotPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishSettingsRestricted, await ReadErrorCodeAsync(response));
    }

    // ---- GET …/wishes/bot ----

    [Fact]
    public async Task Get_AnswersTheSpecShape_AndHandsTheCallersFlagsOn()
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        _factory.WishBot.GetChannelAsync(Arg.Any<string>(), Arg.Any<WishBotCaller>(), Arg.Any<CancellationToken>())
            .Returns(new WishBotChannelDto(
                true, true, false, true, new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc), WishBotChannelReasons.InvitationNotAccepted,
                null, true, WishBotChannelReasons.InvitationPending, null, null, null, "01BOT"));

        var response = await SendAsync(_factory, "GET", BotPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["activatedAtUtc", "activatedByLogin", "activationError", "activationPending", "activationRequestedAtUtc", "available",
                "botSevenTvUserId", "canActivate", "canDeactivate", "editorCheckedAtUtc", "enabled", "listed", "suspendedReason"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("invitation_not_accepted", json.RootElement.GetProperty("activationError").GetString());
        Assert.Equal("invitation_pending", json.RootElement.GetProperty("suspendedReason").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("canActivate").ValueKind);
        await _factory.WishBot.Received(1).GetChannelAsync(Channel, new WishBotCaller(Manager, true, false), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_AnUnknownChannel_Answers404ChannelNotFound()
    {
        _factory.WishBot.GetChannelAsync(Arg.Any<string>(), Arg.Any<WishBotCaller>(), Arg.Any<CancellationToken>())
            .Returns((WishBotChannelDto?)null);

        var response = await SendAsync(_factory, "GET", BotPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(response));
    }

    // ---- bot-remove and recheck (AK84 route part) ----

    [Fact]
    public async Task BotRemove_Answers202WithTheWish_AndPassesTheCaptureAccess()
    {
        var access = new WishCaptureAccess("channel-id", "123", IdConfirmed: true);
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(access);
        _factory.Wishes.RequestBotRemovalAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/bot-remove");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(7, json.RootElement.GetProperty("id").GetInt64());
        await _factory.Wishes.Received(1).RequestBotRemovalAsync(Channel, 7, Arg.Any<AuditActor>(), access, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(WishOutcome.NotBotOwned, ApiErrorCodes.WishNotBotOwned)]
    [InlineData(WishOutcome.StartUnconfirmed, ApiErrorCodes.WishStartUnconfirmed)]
    [InlineData(WishOutcome.BotUnavailable, ApiErrorCodes.WishBotUnavailable)]
    [InlineData(WishOutcome.StateConflict, ApiErrorCodes.WishStateConflict)]
    public async Task BotRemove_Refusals_Are409(WishOutcome outcome, string code)
    {
        _factory.Wishes.RequestBotRemovalAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(outcome));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/bot-remove");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(code, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Recheck_Answers200WithTheWish_AndPassesTheCaptureAccess()
    {
        var access = new WishCaptureAccess("channel-id", "123", IdConfirmed: false);
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(access);
        _factory.Wishes.RecheckAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/recheck");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.Wishes.Received(1).RecheckAsync(Channel, 7, Arg.Any<AuditActor>(), access, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recheck_ASetChange_Answers409BotSetChanged()
    {
        _factory.Wishes.RecheckAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.BotSetChanged));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/recheck");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishBotSetChanged, await ReadErrorCodeAsync(response));
    }

    // ---- /permissions: wishBotModeAvailable ----

    // A switch off reads nothing (Befund 19).
    [Fact]
    public async Task Permissions_WithTheBotSwitchOff_AreFalse_WithoutARead()
    {
        var flag = await ReadModeAvailableAsync(_factory);

        Assert.False(flag);
        await _factory.WishBot.DidNotReceiveWithAnyArgs().IsModeAvailableAsync(default!, default, default);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Permissions_WithBothSwitchesOn_AreTheServicesAnswer_WithTheAdminByIdFlag(bool available, bool adminById)
    {
        using var enabled = BotEnabled();
        _factory.ChannelAccess.IsGlobalAdminById(Arg.Any<TwitchPrincipalInfo>()).Returns(adminById);
        _factory.WishBot.IsModeAvailableAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(available);

        Assert.Equal(available, await ReadModeAvailableAsync(enabled));
        await _factory.WishBot.Received(1).IsModeAvailableAsync(Channel, adminById, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Permissions_ForANonManager_AreFalse_WithoutARead()
    {
        using var enabled = BotEnabled();
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Assert.False(await ReadModeAvailableAsync(enabled));
        await _factory.WishBot.DidNotReceiveWithAnyArgs().IsModeAvailableAsync(default!, default, default);
    }

    // ---- helpers ----

    private WebApplicationFactory<Program> BotEnabled() => _factory.WithBotAccountEnabled();

    private static async Task<bool> ReadModeAvailableAsync(WebApplicationFactory<Program> factory)
    {
        var response = await SendAsync(factory, "GET", $"/api/channels/{Channel}/permissions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("wishBotModeAvailable").GetBoolean();
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("errorCode").GetString();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> factory, string method, string path, string? body = null, string? userId = Manager)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (userId is not null)
        {
            request.Headers.Add(TestAuthHandler.UserIdHeader, userId);
            request.Headers.Add(TestAuthHandler.LoginHeader, "someuser");
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static WishDto Wish() => new(
        Id: 7,
        SevenTvEmoteId: "01H0000000000000000000000A",
        EmoteName: "Kappa",
        Alias: "Kappa",
        Source: WishSource.Manual,
        Status: WishStatus.Presented,
        StatusReason: WishStatusReason.Protected,
        RequesterLogin: null,
        DurationDays: 30,
        CreatedAtUtc: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
        ApprovedAtUtc: new DateTime(2026, 11, 1, 11, 0, 0, DateTimeKind.Utc),
        StartsAtUtc: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
        StartsAtAuthoritative: true,
        ProbationStartsAtUtc: null,
        ExpiresAtUtc: new DateTime(2026, 12, 2, 10, 0, 0, DateTimeKind.Utc),
        EndedAtUtc: null,
        SlotId: "slot-1",
        SlotAddedAtUtc: null,
        ImageUrl: null,
        LinkOrigin: WishLinkOrigin.Bot,
        ExtensionCount: 0,
        Permanent: false,
        BotOwned: true,
        BotWritePending: false,
        BotNextAttemptAtUtc: null,
        RemovalQueuedAtUtc: new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc),
        Mode: "bot");
}
