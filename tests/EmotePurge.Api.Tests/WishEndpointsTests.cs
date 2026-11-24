using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The wish routes (#481, K1) over the real pipeline: the filter ladder (400, 401, 403, then the switch's 404)
/// on every route, the settings filter, the mapping of every service outcome, the wire shape, the channel
/// stream's predicate and the layering of the handlers.
/// </summary>
public class WishEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string Manager = "manager-1";

    private readonly ApiFactory _factory;

    public WishEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Wishes.ClearReceivedCalls();
        _factory.WishOverview.ClearReceivedCalls();
        _factory.ChannelAccess.ClearReceivedCalls();

        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
    }

    private const string RemovalBody =
        """{"slotId":"slot-1","slotAddedAtUtc":"2026-11-02T10:00:00.123456Z","emoteSetId":"01H0000000000000000000SET1","evidence":"acknowledged"}""";

    public static TheoryData<string, string, string?> Routes => new()
    {
        { "GET", "/api/channels/{0}/wishes", null },
        { "GET", "/api/channels/{0}/wishes/7", null },
        { "GET", "/api/channels/{0}/wishes/settings", null },
        { "GET", "/api/channels/{0}/wishes/slots", null },
        { "PUT", "/api/channels/{0}/wishes/settings", """{"defaultDurationDays":30,"probationLeadDays":7,"protectionThresholdPerDay":null}""" },
        { "POST", "/api/channels/{0}/wishes", """{"sevenTvEmoteId":"01H0000000000000000000000A"}""" },
        { "POST", "/api/channels/{0}/wishes/adopt", """{"slotId":"01H0000000000000000000000B","expectedAlias":"Kappa","expectedAddedAtUtc":null}""" },
        { "PATCH", "/api/channels/{0}/wishes/7", """{"alias":"Kappa"}""" },
        { "POST", "/api/channels/{0}/wishes/7/extend", null },
        { "POST", "/api/channels/{0}/wishes/7/keep", null },
        { "POST", "/api/channels/{0}/wishes/7/close", null },
        { "POST", "/api/channels/{0}/wishes/7/retry", null },
        { "POST", "/api/channels/{0}/wishes/7/re-add", """{"mode":"timed"}""" },
        { "POST", "/api/channels/{0}/wishes/7/complete-removal", RemovalBody },
        { "POST", "/api/channels/{0}/wishes/7/recheck", null },
        // K6 (plan #486 2.9, W7): the commands on an open wish follow the wish ladder, not the capture switch.
        { "POST", "/api/channels/{0}/wishes/7/approve", "{}" },
        { "POST", "/api/channels/{0}/wishes/7/reject", null },
        { "POST", "/api/channels/{0}/wishes/7/unlink", null },
        // K7a (spec E38, E41): the delete route carries WishIdAccessFilter behind the same ladder.
        { "DELETE", "/api/channels/{0}/wishes/7?endedAtUtc=2026-11-02T10:00:00.1234567Z", null },
    };

    // ---- AK13: the filter ladder on every route ----

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_Answers400InvalidChannelName_BeforeAnyAccessCheck(string method, string pathFormat, string? body)
    {
        var response = await SendAsync(_factory, method, Path(pathFormat, "bad-name"), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
        await _factory.ChannelAccess.DidNotReceiveWithAnyArgs().CanManageChannelAsync(default!, default!, default);
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_Answers401_WithoutASession(string method, string pathFormat, string? body)
    {
        var response = await SendAsync(_factory, method, Path(pathFormat, Channel), body, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_Answers403_ForACallerWhoCannotManage_AndNeverAsksTheWiderCheck(string method, string pathFormat, string? body)
    {
        // A 7TV editor without mod status: the wider usage-stats check would say yes, the manager check says no.
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _factory.ChannelAccess.CanViewUsageStatsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var response = await SendAsync(_factory, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.ChannelAccess.DidNotReceiveWithAnyArgs().CanViewUsageStatsAsync(default!, default!, default);
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task SwitchOff_Answers404WishesDisabled_ForAManager_AndNeverCallsTheService(string method, string pathFormat, string? body)
    {
        using var disabled = _factory.WithWishesDisabled();

        var response = await SendAsync(disabled, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishesDisabled, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task SwitchOff_StillAnswers403_ForACallerWhoCannotManage_SoTheFeatureIsNotRevealed(string method, string pathFormat, string? body)
    {
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        using var disabled = _factory.WithWishesDisabled();

        var response = await SendAsync(disabled, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    [Theory]
    [InlineData("POST", "/api/channels/handofblood/wishes")]
    [InlineData("POST", "/api/channels/handofblood/wishes/adopt")]
    [InlineData("PATCH", "/api/channels/handofblood/wishes/7")]
    [InlineData("POST", "/api/channels/handofblood/wishes/7/re-add")]
    [InlineData("POST", "/api/channels/handofblood/wishes/7/complete-removal")]
    [InlineData("POST", "/api/channels/handofblood/wishes/7/approve")]
    [InlineData("PUT", "/api/channels/handofblood/wishes/settings")]
    public async Task SwitchOff_ChecksTheSwitch_BeforeTheBody(string method, string path)
    {
        using var disabled = _factory.WithWishesDisabled();

        // An empty body would be a 400 invalid_request_body with the switch on (next test). A body that is
        // not JSON at all fails in the framework's binding, before any handler, as for every route.
        var response = await SendAsync(disabled, method, path, "");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishesDisabled, await ReadErrorCodeAsync(response));
    }

    [Theory]
    [InlineData("POST", "/api/channels/handofblood/wishes")]
    [InlineData("POST", "/api/channels/handofblood/wishes/adopt")]
    [InlineData("PATCH", "/api/channels/handofblood/wishes/7")]
    [InlineData("POST", "/api/channels/handofblood/wishes/7/re-add")]
    [InlineData("POST", "/api/channels/handofblood/wishes/7/complete-removal")]
    [InlineData("POST", "/api/channels/handofblood/wishes/7/approve")]
    [InlineData("PUT", "/api/channels/handofblood/wishes/settings")]
    public async Task SwitchOn_AnEmptyOrUnbindableBody_Answers400InvalidRequestBody(string method, string path)
    {
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var empty = await SendAsync(_factory, method, path, "");
        var unbindable = await SendAsync(_factory, method, path, "not json");

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(empty));
        Assert.Equal(HttpStatusCode.BadRequest, unbindable.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(unbindable));
        // Only the settings filter's read-only decision may have reached the service, never a write.
        Assert.All(
            _factory.Wishes.ReceivedCalls(),
            call => Assert.Equal(nameof(IWishService.CanChangeSettingsAsync), call.GetMethodInfo().Name));
    }

    [Fact]
    public async Task List_AnUnknownView_Answers400InvalidRequestBody()
    {
        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes?view=bogus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
    }

    // ---- AK14: the settings filter ----

    [Fact]
    public async Task PutSettings_InBotMode_AdmitsBroadcasterActivatorAndAdmin_AndRefusesAnyOtherManager()
    {
        // The service decides; the filter forwards the caller's id and the admin flag.
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(1) is "broadcaster" or "activator" || call.ArgAt<bool>(2));
        _factory.Wishes.UpdateSettingsAsync(Arg.Any<string>(), Arg.Any<UpdateWishSettingsRequest>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Settings: Settings()));
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Is<TwitchPrincipalInfo>(p => p.TwitchUserId == "admin")).Returns(true);
        const string body = """{"defaultDurationDays":30,"probationLeadDays":7,"protectionThresholdPerDay":null}""";
        var path = $"/api/channels/{Channel}/wishes/settings";

        var mod = await SendAsync(_factory, "PUT", path, body, userId: "plain-mod");
        Assert.Equal(HttpStatusCode.Forbidden, mod.StatusCode);
        Assert.Equal(ApiErrorCodes.WishSettingsRestricted, await ReadErrorCodeAsync(mod));
        await _factory.Wishes.DidNotReceiveWithAnyArgs().UpdateSettingsAsync(default!, default!, default!, default);

        foreach (var allowed in new[] { "broadcaster", "activator", "admin" })
        {
            var response = await SendAsync(_factory, "PUT", path, body, userId: allowed);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task PutSettings_ForANonManager_Answers403BeforeTheSettingsDecision()
    {
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync(_factory, "PUT", $"/api/channels/{Channel}/wishes/settings", "{}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.Wishes.DidNotReceiveWithAnyArgs().CanChangeSettingsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task PutSettings_SwitchOff_LetsTheFilterThrough_SoTheHandlerAnswers404()
    {
        using var disabled = _factory.WithWishesDisabled();

        var response = await SendAsync(disabled, "PUT", $"/api/channels/{Channel}/wishes/settings", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await _factory.Wishes.DidNotReceiveWithAnyArgs().CanChangeSettingsAsync(default!, default!, default, default);
    }

    // ---- Plan #485 T0a: canEdit on the settings read ----

    [Fact]
    public async Task GetSettings_CarriesCanEdit_AsTheSettingsWriteDecision_PerCaller()
    {
        // The same decision as the PUT filter: in bot mode broadcaster, activator and admin, no other manager.
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(1) is "broadcaster" or "activator" || call.ArgAt<bool>(2));
        _factory.Wishes.GetSettingsAsync(Channel, Arg.Any<CancellationToken>()).Returns(Settings());
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Is<TwitchPrincipalInfo>(p => p.TwitchUserId == "admin")).Returns(true);
        var path = $"/api/channels/{Channel}/wishes/settings";

        foreach (var (userId, expected) in new[] { ("plain-mod", false), ("broadcaster", true), ("activator", true), ("admin", true) })
        {
            var response = await SendAsync(_factory, "GET", path, userId: userId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(expected, json.RootElement.GetProperty("canEdit").GetBoolean());
            string[] names =
                ["defaultDurationDays", "probationLeadDays", "protectionThresholdPerDay", "botModeEnabled", "botSuspendedReason",
                    "pendingExpiryDays", "canEdit"];
            Assert.True(names.ToHashSet().SetEquals(json.RootElement.EnumerateObject().Select(p => p.Name)));
        }

        await _factory.Wishes.Received(1).CanChangeSettingsAsync(Channel, "admin", true, Arg.Any<CancellationToken>());
        await _factory.Wishes.Received(1).CanChangeSettingsAsync(Channel, "plain-mod", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSettings_OfAnUnknownChannel_Answers404_WithoutTheSettingsDecision()
    {
        _factory.Wishes.GetSettingsAsync(Channel, Arg.Any<CancellationToken>()).Returns((WishSettingsDto?)null);

        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/settings");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(response));
        await _factory.Wishes.DidNotReceiveWithAnyArgs().CanChangeSettingsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task PutSettings_AnswersThePlainSettings_WithoutCanEdit()
    {
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.Wishes.UpdateSettingsAsync(Arg.Any<string>(), Arg.Any<UpdateWishSettingsRequest>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Settings: Settings()));

        var response = await SendAsync(
            _factory, "PUT", $"/api/channels/{Channel}/wishes/settings",
            """{"defaultDurationDays":30,"probationLeadDays":7,"protectionThresholdPerDay":null}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.TryGetProperty("canEdit", out _));
    }

    // ---- Plan #485 T0a: the adoptable entries ----

    [Fact]
    public async Task Slots_ForwardsTheEmoteFilter_AndSerializesEveryEntryField()
    {
        _factory.Wishes.ListAdoptableSlotsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([
                new WishAdoptableSlotDto(
                    "slot-1", "01H0000000000000000000000A", "Kappa", new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), true,
                    "https://cdn.7tv.app/emote/01H0000000000000000000000A/2x.webp", null, 41, true),
                new WishAdoptableSlotDto("slot-2", "01H0000000000000000000000A", "KappaToo", null, false, null, 42, null, false),
            ]);

        var filtered = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/slots?sevenTvEmoteId=01H0000000000000000000000A");
        var all = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/slots");

        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        await _factory.Wishes.Received(1).ListAdoptableSlotsAsync(Channel, "01H0000000000000000000000A", Arg.Any<CancellationToken>());
        await _factory.Wishes.Received(1).ListAdoptableSlotsAsync(Channel, null, Arg.Any<CancellationToken>());
        using var json = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        var first = json.RootElement[0];
        string[] names =
            ["slotId", "sevenTvEmoteId", "alias", "addedAtUtc", "addedAtAuthoritative", "imageUrl", "heldByWishId", "runningWishId",
                "runningWishAdoptable"];
        Assert.True(names.ToHashSet().SetEquals(first.EnumerateObject().Select(p => p.Name)));
        Assert.Equal("2026-11-02T10:00:00Z", first.GetProperty("addedAtUtc").GetString());
        Assert.Equal(41, first.GetProperty("runningWishId").GetInt64());
        Assert.True(first.GetProperty("runningWishAdoptable").GetBoolean());
        var second = json.RootElement[1];
        Assert.Equal(JsonValueKind.Null, second.GetProperty("addedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("imageUrl").ValueKind);
        Assert.Equal(42, second.GetProperty("heldByWishId").GetInt64());
    }

    [Fact]
    public async Task Slots_OfAnUnknownChannel_Answers404ChannelNotFound_AndAnEmptyListIsNoError()
    {
        _factory.Wishes.ListAdoptableSlotsAsync(Channel, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<WishAdoptableSlotDto>?)null, (IReadOnlyList<WishAdoptableSlotDto>?)Array.Empty<WishAdoptableSlotDto>());
        var path = $"/api/channels/{Channel}/wishes/slots";

        var unknown = await SendAsync(_factory, "GET", path);
        var empty = await SendAsync(_factory, "GET", path);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(unknown));
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal("[]", await empty.Content.ReadAsStringAsync());
    }

    // ---- 2.4: every outcome has a mapping ----

    public static TheoryData<WishOutcome, HttpStatusCode, string> FailureOutcomes => new()
    {
        { WishOutcome.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound },
        { WishOutcome.WishNotFound, HttpStatusCode.NotFound, ApiErrorCodes.WishNotFound },
        // The three outcomes without a code of their own reuse existing ones (no thirteenth code).
        { WishOutcome.EmoteIdInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.EmoteIdsInvalid },
        { WishOutcome.StartsAtInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequestBody },
        { WishOutcome.ModeInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequestBody },
        { WishOutcome.RequestInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequestBody },
        { WishOutcome.DurationOutOfRange, HttpStatusCode.BadRequest, ApiErrorCodes.WishDurationOutOfRange },
        { WishOutcome.AliasInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.WishAliasInvalid },
        { WishOutcome.RequesterInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.WishRequesterInvalid },
        { WishOutcome.SettingsOutOfRange, HttpStatusCode.BadRequest, ApiErrorCodes.WishSettingsOutOfRange },
        { WishOutcome.EmoteAlreadyInSet, HttpStatusCode.Conflict, ApiErrorCodes.WishEmoteAlreadyInSet },
        { WishOutcome.AlreadyRunning, HttpStatusCode.Conflict, ApiErrorCodes.WishAlreadyRunning },
        { WishOutcome.SlotNotFound, HttpStatusCode.NotFound, ApiErrorCodes.WishSlotNotFound },
        { WishOutcome.SlotAlreadyLinked, HttpStatusCode.Conflict, ApiErrorCodes.WishSlotAlreadyLinked },
        { WishOutcome.StateConflict, HttpStatusCode.Conflict, ApiErrorCodes.WishStateConflict },
        { WishOutcome.EmoteRequired, HttpStatusCode.BadRequest, ApiErrorCodes.WishEmoteRequired },
        { WishOutcome.RunningNotExtendable, HttpStatusCode.Conflict, ApiErrorCodes.WishRunningNotExtendable },
    };

    [Theory]
    [MemberData(nameof(FailureOutcomes))]
    public async Task EveryFailureOutcome_MapsToItsStatusAndErrorCode_WithItsPayload(WishOutcome outcome, HttpStatusCode status, string code)
    {
        _factory.Wishes.ExtendAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(outcome, SlotIds: ["slot-a", "slot-b"], ConflictingWishId: 42));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/extend");

        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(outcome == WishOutcome.EmoteAlreadyInSet, json.RootElement.TryGetProperty("slotIds", out var slotIds));
        if (outcome == WishOutcome.EmoteAlreadyInSet)
        {
            Assert.Equal(["slot-a", "slot-b"], slotIds.EnumerateArray().Select(s => s.GetString()));
        }

        // Plan #485 2.1a: the three conflicts about another wish name it; K6 adds the running wish without an expiry.
        var namesAWish = outcome is WishOutcome.AlreadyRunning or WishOutcome.SlotAlreadyLinked or WishOutcome.EmoteAlreadyInSet
            or WishOutcome.RunningNotExtendable;
        Assert.Equal(namesAWish, json.RootElement.TryGetProperty("wishId", out var wishId));
        if (namesAWish)
        {
            Assert.Equal(42, wishId.GetInt64());
        }
    }

    // wish_emote_already_in_set without a running wish still carries the field, as null.
    [Fact]
    public async Task EmoteAlreadyInSet_WithoutARunningWish_CarriesWishIdNull()
    {
        _factory.Wishes.CreateAsync(Arg.Any<string>(), Arg.Any<CreateWishRequest>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.EmoteAlreadyInSet, SlotIds: ["slot-a"]));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes", """{"sevenTvEmoteId":"01H0000000000000000000000A"}""");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("wishId").ValueKind);
    }

    // K7a (spec E41, AK113): the service's second wall behind the access filter is a 403 in the form of the manager
    // filter, without a body. Its own case because the theory above always reads a JSON body.
    [Fact]
    public async Task AccessDenied_MapsTo403_WithoutABody()
    {
        _factory.Wishes.ExtendAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.AccessDenied));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/extend");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void EveryOutcome_HasAMappingCase_InThisTheory()
    {
        // Success is the 2xx tests below and AccessDenied has its own bodiless case above; every other member must
        // have a row, so a new outcome cannot slip into the service without a case in the handler's switch (and a
        // row here).
        var covered = FailureOutcomes.Select(row => (WishOutcome)row[0]).Append(WishOutcome.AccessDenied).ToHashSet();
        var all = Enum.GetValues<WishOutcome>().Where(o => o != WishOutcome.Success);

        Assert.True(covered.SetEquals(all));
    }

    [Fact]
    public async Task Create_Answers201WithTheWish_AndCarriesSetFullOnlyAsAHint()
    {
        _factory.Wishes.CreateAsync(Arg.Any<string>(), Arg.Any<CreateWishRequest>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(
                new WishCommandResult(WishOutcome.Success, Wish: Wish(), SetFull: true),
                new WishCommandResult(WishOutcome.Success, Wish: Wish(), SetFull: false));
        var path = $"/api/channels/{Channel}/wishes";
        const string body = """{"sevenTvEmoteId":"01H0000000000000000000000A","durationDays":30}""";

        var full = await SendAsync(_factory, "POST", path, body);
        var roomy = await SendAsync(_factory, "POST", path, body);

        Assert.Equal(HttpStatusCode.Created, full.StatusCode);
        using var fullJson = JsonDocument.Parse(await full.Content.ReadAsStringAsync());
        Assert.True(fullJson.RootElement.GetProperty("setFull").GetBoolean());
        Assert.Equal(7, fullJson.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.Created, roomy.StatusCode);
        using var roomyJson = JsonDocument.Parse(await roomy.Content.ReadAsStringAsync());
        Assert.False(roomyJson.RootElement.TryGetProperty("setFull", out _));
    }

    [Fact]
    public async Task Adopt_Answers201_AndTheOtherCommandsAnswer200()
    {
        _factory.Wishes.AdoptAsync(Arg.Any<string>(), Arg.Any<AdoptWishRequest>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));
        _factory.Wishes.KeepAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));

        var adopt = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/adopt", """{"slotId":"s1"}""");
        var keep = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/keep");

        Assert.Equal(HttpStatusCode.Created, adopt.StatusCode);
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
    }

    [Fact]
    public async Task Commands_ForwardTheChannelTheIdAndTheActor()
    {
        var userId = Guid.NewGuid().ToString("N");
        _factory.Wishes.CloseAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));

        await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/close", userId: userId);

        var call = Assert.Single(_factory.Wishes.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IWishService.CloseAsync));
        var args = call.GetArguments();
        Assert.Equal(Channel, args[0]);
        Assert.Equal(7L, args[1]);
        Assert.Equal(userId, ((AuditActor)args[2]!).TwitchUserId);
    }

    // ---- Plan #485 T0b: the bodies of complete-removal, adopt and the settings write ----

    [Fact]
    public async Task CompleteRemoval_ForwardsTheReportedEntryVerbatim_AndAnswers200()
    {
        _factory.Wishes.CompleteRemovalAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CompleteWishRemovalRequest>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/complete-removal", RemovalBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(_factory.Wishes.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IWishService.CompleteRemovalAsync));
        var args = call.GetArguments();
        Assert.Equal((Channel, 7L), ((string)args[0]!, (long)args[1]!));
        var request = (CompleteWishRemovalRequest)args[2]!;
        Assert.Equal(
            new CompleteWishRemovalRequest(
                "slot-1", new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560), "01H0000000000000000000SET1", "acknowledged"),
            request with { SlotAddedAtUtc = request.SlotAddedAtUtc?.ToUniversalTime() });
    }

    // A JSON null body is no report: the handler answers 400 itself and never calls the service (the empty and the
    // unbindable body are in SwitchOn_AnEmptyOrUnbindableBody_Answers400InvalidRequestBody).
    [Fact]
    public async Task CompleteRemoval_WithANullBody_Answers400_WithoutCallingTheService()
    {
        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/complete-removal", "null");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.Wishes.ReceivedCalls());
    }

    // A body without one of the three fields reaches the service with the gaps as null; the service refuses it
    // (RequestInvalid), and that is a 400.
    [Fact]
    public async Task CompleteRemoval_WithAPartialBody_ForwardsTheGaps_AndTheRefusalIs400()
    {
        _factory.Wishes.CompleteRemovalAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CompleteWishRemovalRequest>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(WishCommandResult.Of(WishOutcome.RequestInvalid));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/complete-removal", """{"slotId":"slot-1"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        await _factory.Wishes.Received(1).CompleteRemovalAsync(
            Channel, 7, new CompleteWishRemovalRequest("slot-1", null, null, null), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adopt_ForwardsTheExpectedAliasAndDate_AndAnAbsentAliasAsNull()
    {
        _factory.Wishes.AdoptAsync(Arg.Any<string>(), Arg.Any<AdoptWishRequest>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()), WishCommandResult.Of(WishOutcome.RequestInvalid));
        var path = $"/api/channels/{Channel}/wishes/adopt";

        var bound = await SendAsync(
            _factory, "POST", path, """{"slotId":"s1","expectedAlias":"Kappa","expectedAddedAtUtc":"2026-11-02T10:00:00.123456Z","wishId":5}""");
        var withoutAlias = await SendAsync(_factory, "POST", path, """{"slotId":"s1"}""");

        Assert.Equal(HttpStatusCode.Created, bound.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutAlias.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(withoutAlias));
        var requests = _factory.Wishes.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IWishService.AdoptAsync))
            .Select(c => (AdoptWishRequest)c.GetArguments()[1]!)
            .ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(
            ("s1", "Kappa", (DateTime?)new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560), (long?)5),
            (requests[0].SlotId, requests[0].ExpectedAlias, requests[0].ExpectedAddedAtUtc?.ToUniversalTime(), requests[0].WishId));
        Assert.Equal(("s1", (string?)null, (DateTime?)null), (requests[1].SlotId, requests[1].ExpectedAlias, requests[1].ExpectedAddedAtUtc));
    }

    // Sol-P2-4 at the real binding: an absent threshold keeps the stored one, an explicit null switches protection
    // off, a value sets it.
    [Theory]
    [InlineData("""{"defaultDurationDays":14,"probationLeadDays":5}""", true, null)]
    [InlineData("""{"defaultDurationDays":14,"probationLeadDays":5,"protectionThresholdPerDay":null}""", false, null)]
    [InlineData("""{"defaultDurationDays":14,"probationLeadDays":5,"protectionThresholdPerDay":2.5}""", false, "2.5")]
    public async Task PutSettings_TellsAnAbsentThresholdFromNull(string body, bool keep, string? threshold)
    {
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.Wishes.UpdateSettingsAsync(Arg.Any<string>(), Arg.Any<UpdateWishSettingsRequest>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Settings: Settings()));

        var response = await SendAsync(_factory, "PUT", $"/api/channels/{Channel}/wishes/settings", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = new UpdateWishSettingsRequest(
            14, 5, threshold is null ? null : decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture), keep);
        await _factory.Wishes.Received(1).UpdateSettingsAsync(Channel, expected, Arg.Any<AuditActor>(), Arg.Any<CancellationToken>());
    }

    // K6 (plan #486 2.8): the expiry of open wishes is optional on the write; absent keeps the stored value.
    [Theory]
    [InlineData("""{"defaultDurationDays":14,"probationLeadDays":5,"pendingExpiryDays":30}""", 30)]
    [InlineData("""{"defaultDurationDays":14,"probationLeadDays":5,"pendingExpiryDays":null}""", null)]
    [InlineData("""{"defaultDurationDays":14,"probationLeadDays":5}""", null)]
    public async Task PutSettings_ForwardsThePendingExpiry_OrNullToKeepIt(string body, int? pendingExpiryDays)
    {
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.Wishes.UpdateSettingsAsync(Arg.Any<string>(), Arg.Any<UpdateWishSettingsRequest>(), Arg.Any<AuditActor>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Settings: Settings() with { PendingExpiryDays = 30 }));

        var response = await SendAsync(_factory, "PUT", $"/api/channels/{Channel}/wishes/settings", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(30, json.RootElement.GetProperty("pendingExpiryDays").GetInt32());
        var request = (UpdateWishSettingsRequest)Assert.Single(
            _factory.Wishes.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IWishService.UpdateSettingsAsync)).GetArguments()[1]!;
        Assert.Equal(pendingExpiryDays, request.PendingExpiryDays);
    }

    [Fact]
    public async Task GetSettings_CarriesThePendingExpiry()
    {
        _factory.Wishes.GetSettingsAsync(Channel, Arg.Any<CancellationToken>()).Returns(Settings() with { PendingExpiryDays = 3 });

        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/settings");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("pendingExpiryDays").GetInt32());
    }

    // ---- Wire shape ----

    [Fact]
    public async Task Get_SerializesWishDtoFlatInCamelCase_WithZTimesAndNoRequesterTwitchId()
    {
        _factory.Wishes.GetAsync(Channel, 7, Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>()).Returns(new WishDetailDto(Wish(), UsageDto()));

        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        // Flat: the usage is one extra property next to the wish's own (member order is not part of the contract).
        string[] expected =
            ["id", "sevenTvEmoteId", "emoteName", "alias", "source", "status", "statusReason", "requesterLogin", "durationDays",
                "createdAtUtc", "approvedAtUtc", "startsAtUtc", "startsAtAuthoritative", "probationStartsAtUtc", "expiresAtUtc",
                "endedAtUtc", "slotId", "slotAddedAtUtc", "imageUrl", "linkOrigin", "extensionCount", "permanent", "botOwned", "botWritePending",
                "botNextAttemptAtUtc", "removalQueuedAtUtc", "mode",
                // K6 (plan #486 2.6): the redeemer fields, null unless the caller is confirmed on the channel's id.
                "requestText", "requestedName", "matchKind", "rewardLabel", "mergedIntoWishId", "usage"];
        Assert.True(expected.ToHashSet().SetEquals(root.EnumerateObject().Select(p => p.Name)));
        Assert.False(root.TryGetProperty("requesterTwitchUserId", out _));
        Assert.Equal("2026-11-01T10:00:00Z", root.GetProperty("createdAtUtc").GetString());
        Assert.Equal("2026-11-02T10:00:00Z", root.GetProperty("startsAtUtc").GetString());
        Assert.Equal("manual", root.GetProperty("mode").GetString());

        var usage = root.GetProperty("usage");
        Assert.Equal(
            ["windowFromUtc", "windowToUtc", "total", "perDayMean", "perDayMeanWithheld", "rank", "rankOf", "coverageGap", "daily"],
            usage.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-11-02T10:00:00Z", usage.GetProperty("windowFromUtc").GetString());
        // Plan #485 2.1a: the generation as a UTC instant, the image as stored, the series as date + count.
        Assert.Equal("2026-11-02T10:00:00.123456Z", root.GetProperty("slotAddedAtUtc").GetString());
        Assert.Equal("https://cdn.7tv.app/emote/01H0000000000000000000000A/2x_static.webp", root.GetProperty("imageUrl").GetString());
        var day = usage.GetProperty("daily")[0];
        Assert.Equal(["date", "useCount"], day.EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-11-02", day.GetProperty("date").GetString());
        Assert.Equal(120, day.GetProperty("useCount").GetInt64());
        Assert.Equal(120, usage.GetProperty("total").GetInt64());
        Assert.Equal(3, usage.GetProperty("rank").GetInt32());
        Assert.False(usage.GetProperty("coverageGap").GetBoolean());
    }

    [Fact]
    public async Task Get_WithoutAStart_SerializesUsageAsNull_NotOmitted()
    {
        _factory.Wishes.GetAsync(Channel, 7, Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>()).Returns(new WishDetailDto(Wish(), null));

        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/7");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("usage").ValueKind);
    }

    [Fact]
    public async Task Get_UnknownWish_Answers404WishNotFound()
    {
        _factory.Wishes.GetAsync(Channel, 7, Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>()).Returns((WishDetailDto?)null);

        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/7");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishNotFound, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task List_SerializesTheListWithoutUsage()
    {
        _factory.Wishes.ListAsync(Channel, WishListView.Running, 2, 10, Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<WishDto>([Wish()], 2, 10, 11));

        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes?view=running&page=2&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(11, json.RootElement.GetProperty("totalCount").GetInt32());
        var item = json.RootElement.GetProperty("items")[0];
        Assert.False(item.TryGetProperty("usage", out _));
        Assert.Equal("2026-11-01T10:00:00Z", item.GetProperty("createdAtUtc").GetString());
    }

    // ---- Cross-channel overview ----

    [Fact]
    public async Task Summary_AnswersTheCountAsActionCount()
    {
        _factory.WishOverview.CountActionAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>()).Returns(3);

        var response = await SendAsync(_factory, "GET", "/api/wishes/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("actionCount").GetInt32());
        Assert.Equal(["actionCount"], json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Mine_AnswersTheOverviewItems()
    {
        _factory.WishOverview.ListMineAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns([new WishOverviewItemDto(Channel, Wish())]);

        var response = await SendAsync(_factory, "GET", "/api/wishes/mine");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Channel, json.RootElement[0].GetProperty("channelName").GetString());
        Assert.Equal(7, json.RootElement[0].GetProperty("wish").GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData("/api/wishes/mine")]
    [InlineData("/api/wishes/summary")]
    public async Task Overview_WithoutASession_Answers401(string path)
    {
        var response = await SendAsync(_factory, "GET", path, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.WishOverview.ReceivedCalls());
    }

    [Theory]
    [InlineData("/api/wishes/mine")]
    [InlineData("/api/wishes/summary")]
    public async Task Overview_SwitchOff_Answers404WishesDisabled_WithoutCallingTheService(string path)
    {
        using var disabled = _factory.WithWishesDisabled();

        var response = await SendAsync(disabled, "GET", path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishesDisabled, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.WishOverview.ReceivedCalls());
    }

    // ---- /permissions ----

    [Fact]
    public async Task Permissions_CarryTheWishesFlag_AsConfigured()
    {
        using var disabled = _factory.WithWishesDisabled();
        var path = $"/api/channels/{Channel}/permissions";

        using var off = JsonDocument.Parse(await (await SendAsync(disabled, "GET", path)).Content.ReadAsStringAsync());
        using var on = JsonDocument.Parse(await (await SendAsync(_factory, "GET", path)).Content.ReadAsStringAsync());

        Assert.False(off.RootElement.GetProperty("wishesEnabled").GetBoolean());
        Assert.True(on.RootElement.GetProperty("wishesEnabled").GetBoolean());
    }

    // ---- AK16: wish.changed reaches the channel stream only ----

    [Fact]
    public async Task ChannelStream_ForwardsWishChanged_ForItsOwnChannelOnly()
    {
        var filter = await CaptureStreamFilterAsync($"/api/channels/{Channel}/live");

        Assert.True(filter(new LiveEvent(LiveEvents.WishChanged, Channel)));
        Assert.False(filter(new LiveEvent(LiveEvents.WishChanged, "someoneelse")));
    }

    [Fact]
    public async Task CrossChannelStream_NeverForwardsWishChanged()
    {
        var filter = await CaptureStreamFilterAsync("/api/channels/live-events");

        Assert.False(filter(new LiveEvent(LiveEvents.WishChanged, Channel)));
        Assert.True(filter(new LiveEvent(LiveEvents.LiveChanged, Channel)));
    }

    // ---- AK20: layering ----

    [Fact]
    public void NoWishHandler_TakesTheDbContextOrTheRedisMultiplexer()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw
                && (raw.StartsWith("/api/channels/{channelName}/wishes", StringComparison.Ordinal)
                    || raw.StartsWith("/api/wishes", StringComparison.Ordinal)))
            .ToList();

        // 19 channel routes (the Routes table), the six reward routes (WishCaptureEndpointsTests) and the two overview
        // reads: the scan is not vacuous.
        Assert.Equal(27, endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
            Assert.NotNull(handler);
            var forbidden = handler!.GetParameters()
                .Select(p => p.ParameterType)
                .Where(t => t == typeof(EmotePurge.Infrastructure.Persistence.AppDbContext)
                    || t == typeof(StackExchange.Redis.IConnectionMultiplexer));
            Assert.Empty(forbidden);
        }
    }

    // ---- Helpers ----

    private async Task<Func<LiveEvent, bool>> CaptureStreamFilterAsync(string path)
    {
        Func<LiveEvent, bool>? captured = null;
        _factory.LiveEventStream
            .SubscribeAsync(Arg.Any<string>(), Arg.Any<Func<LiveEvent, bool>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.ArgAt<Func<LiveEvent, bool>>(1);
                return LiveEventSubscribeResult.Failed(LiveEventSubscribeStatus.QuotaExhausted);
            });

        using var response = await SendAsync(_factory, "GET", path);

        Assert.NotNull(captured);
        return captured!;
    }

    private static string Path(string format, string channel) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, format, channel);

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();
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

    private static WishSettingsDto Settings() => new(30, 7, null, BotModeEnabled: false, BotSuspendedReason: null);

    private static WishUsageDto UsageDto() => new(
        new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
        Total: 120,
        PerDayMean: 17.14m,
        PerDayMeanWithheld: false,
        Rank: 3,
        RankOf: 40,
        CoverageGap: false,
        Daily: [new WishUsageDayDto(new DateOnly(2026, 11, 2), 120)]);

    private static WishDto Wish() => new(
        Id: 7,
        SevenTvEmoteId: "01H0000000000000000000000A",
        EmoteName: "Kappa",
        Alias: "Kappa",
        Source: WishSource.Manual,
        Status: WishStatus.Active,
        StatusReason: null,
        RequesterLogin: "viewer",
        DurationDays: 30,
        CreatedAtUtc: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
        ApprovedAtUtc: new DateTime(2026, 11, 1, 11, 0, 0, DateTimeKind.Utc),
        StartsAtUtc: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
        StartsAtAuthoritative: true,
        ProbationStartsAtUtc: null,
        ExpiresAtUtc: new DateTime(2026, 12, 2, 10, 0, 0, DateTimeKind.Utc),
        EndedAtUtc: null,
        SlotId: "slot-1",
        SlotAddedAtUtc: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560),
        ImageUrl: "https://cdn.7tv.app/emote/01H0000000000000000000000A/2x_static.webp",
        LinkOrigin: WishLinkOrigin.Auto,
        ExtensionCount: 0,
        Permanent: false,
        BotOwned: false,
        BotWritePending: false,
        BotNextAttemptAtUtc: null,
        RemovalQueuedAtUtc: null,
        Mode: "manual");
}
