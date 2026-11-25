using System.Net;
using System.Text;
using System.Text.Json;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The chat capture's Api surface (wish emotes K6, plan #486 2.4a, 2.9) over the real pipeline: the reward routes and
/// their filter ladder (name, session, manager, the two switches, the id-confirmed access of
/// <c>WishCaptureAccessFilter</c>, then the settings filter on the writes), the mapping of every reward outcome, the
/// commands on an open wish and their reachability with the capture off, the access every wish answer is decided
/// with, <c>wishCaptureEnabled</c> and the capture fields of the admin health.
/// </summary>
public class WishCaptureEndpointsTests : IClassFixture<ApiFactory>
{
    private const string Channel = "handofblood";
    private const string Manager = "manager-1";
    private const string SessionId = "6f1d1a52-0d55-4a4e-9a0a-6a2f3e9c1b11";
    private const string CandidateId = "0b8f6a0c-5b8c-4d38-9b4d-2b7f9f0f2c22";

    private static readonly WishCaptureAccess Confirmed = new("row-1", "1001", IdConfirmed: true);

    private readonly ApiFactory _factory;

    public WishCaptureEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Wishes.ClearReceivedCalls();
        _factory.WishRewards.ClearReceivedCalls();
        _factory.ChannelAccess.ClearReceivedCalls();

        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(false);
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Confirmed);
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _factory.WishRewards.GetAsync(Arg.Any<WishCaptureAccess>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new WishRewardResult(WishRewardOutcome.Success, Rewards: RewardsDto()));
        _factory.WishRewards.StartLearningAsync(Arg.Any<WishCaptureAccess>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new WishRewardResult(WishRewardOutcome.Success, Learning: LearningDto()));
        _factory.WishRewards.ConfirmLearningAsync(
                Arg.Any<WishCaptureAccess>(), Arg.Any<ConfirmWishRewardLearningRequest>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new WishRewardResult(WishRewardOutcome.Success, Reward: RewardDto()));
        _factory.WishRewards.CancelLearningAsync(Arg.Any<WishCaptureAccess>(), Arg.Any<Guid?>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(WishRewardResult.Of(WishRewardOutcome.Success));
        _factory.WishRewards.UpdateRewardAsync(
                Arg.Any<WishCaptureAccess>(), Arg.Any<long>(), Arg.Any<UpdateWishRewardRequest>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new WishRewardResult(WishRewardOutcome.Success, Reward: RewardDto()));
        _factory.WishRewards.DeleteRewardAsync(Arg.Any<WishCaptureAccess>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(WishRewardResult.Of(WishRewardOutcome.Success));
    }

    private const string ConfirmBody =
        """{"sessionId":"6f1d1a52-0d55-4a4e-9a0a-6a2f3e9c1b11","candidateId":"0b8f6a0c-5b8c-4d38-9b4d-2b7f9f0f2c22","label":"Wunsch","defaultDurationDays":14}""";

    public static TheoryData<string, string, string?> RewardRoutes => new()
    {
        { "GET", "/api/channels/{0}/wishes/rewards", null },
        { "POST", "/api/channels/{0}/wishes/rewards/learning", null },
        { "POST", "/api/channels/{0}/wishes/rewards/learning/confirm", ConfirmBody },
        { "DELETE", "/api/channels/{0}/wishes/rewards/learning?sessionId=" + SessionId, null },
        { "PATCH", "/api/channels/{0}/wishes/rewards/5", """{"isActive":false}""" },
        { "DELETE", "/api/channels/{0}/wishes/rewards/5", null },
    };

    public static TheoryData<string, string, string?> RewardWrites => new()
    {
        { "POST", "/api/channels/{0}/wishes/rewards/learning", null },
        { "POST", "/api/channels/{0}/wishes/rewards/learning/confirm", ConfirmBody },
        { "DELETE", "/api/channels/{0}/wishes/rewards/learning?sessionId=" + SessionId, null },
        { "PATCH", "/api/channels/{0}/wishes/rewards/5", """{"isActive":false}""" },
        { "DELETE", "/api/channels/{0}/wishes/rewards/5", null },
    };

    // ---- The ladder in front of the capture filter ----

    [Theory]
    [MemberData(nameof(RewardRoutes))]
    public async Task RewardRoute_Answers400InvalidChannelName_BeforeAnyAccessCheck(string method, string pathFormat, string? body)
    {
        var response = await SendAsync(_factory, method, Path(pathFormat, "bad-name"), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidChannelName, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.ChannelAccess.ReceivedCalls());
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    [Theory]
    [MemberData(nameof(RewardRoutes))]
    public async Task RewardRoute_Answers401_WithoutASession(string method, string pathFormat, string? body)
    {
        var response = await SendAsync(_factory, method, Path(pathFormat, Channel), body, userId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    // The manager filter runs first: a non-manager is never asked the id question, under either switch state.
    [Theory]
    [MemberData(nameof(RewardRoutes))]
    public async Task RewardRoute_Answers403_ForANonManager_BeforeTheCaptureFilter(string method, string pathFormat, string? body)
    {
        _factory.ChannelAccess.CanManageChannelAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        using var captureOff = _factory.WithWishCaptureDisabled();

        var on = await SendAsync(_factory, method, Path(pathFormat, Channel), body);
        var off = await SendAsync(captureOff, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.Forbidden, on.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        await _factory.ChannelAccess.DidNotReceiveWithAnyArgs().ResolveWishCaptureAccessAsync(default!, default!, default);
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    // ---- The two switches (plan 2.9, AK63; live probe C3/C6) ----

    [Theory]
    [MemberData(nameof(RewardRoutes))]
    public async Task WishesOff_Answers404WishesDisabled_OnEveryRewardRoute(string method, string pathFormat, string? body)
    {
        using var disabled = _factory.WithWishesDisabled();

        var response = await SendAsync(disabled, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishesDisabled, await ReadErrorCodeAsync(response));
        AssertNothingAskedBehindTheSwitch();
    }

    // Answered by the capture filter, so the settings filter behind it never shows a settings refusal for a
    // switched-off feature (a mod in bot mode would otherwise see wish_settings_restricted).
    [Theory]
    [MemberData(nameof(RewardRoutes))]
    public async Task CaptureOff_Answers404WishCaptureDisabled_OnEveryRewardRoute_BeforeTheSettingsDecision(
        string method, string pathFormat, string? body)
    {
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(false);
        using var captureOff = _factory.WithWishCaptureDisabled();

        var response = await SendAsync(captureOff, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishCaptureDisabled, await ReadErrorCodeAsync(response));
        AssertNothingAskedBehindTheSwitch();
    }

    [Theory]
    [InlineData("POST", "/api/channels/handofblood/wishes/rewards/learning/confirm")]
    [InlineData("PATCH", "/api/channels/handofblood/wishes/rewards/5")]
    public async Task CaptureOff_ChecksTheSwitch_BeforeTheBody(string method, string path)
    {
        using var captureOff = _factory.WithWishCaptureDisabled();

        var response = await SendAsync(captureOff, method, path, "");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishCaptureDisabled, await ReadErrorCodeAsync(response));
    }

    // W7: deciding an open wish needs Wishes:Enabled only; with the capture off it still reaches the service.
    [Theory]
    [InlineData("/api/channels/handofblood/wishes/7/approve", "{}")]
    [InlineData("/api/channels/handofblood/wishes/7/reject", null)]
    [InlineData("/api/channels/handofblood/wishes/7/unlink", null)]
    public async Task CaptureOff_CommandsOnAnOpenWish_StayReachable(string path, string? body)
    {
        StubCommandsSucceed();
        using var captureOff = _factory.WithWishCaptureDisabled();

        var response = await SendAsync(captureOff, "POST", path, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_factory.Wishes.ReceivedCalls(), c => c.GetMethodInfo().Name is "ApproveAsync" or "RejectAsync" or "UnlinkAsync");
    }

    // ---- WishCaptureAccessFilter (plan 2.4a) ----

    // The manager filter says yes (login), the id question says no: 403 on every reward route, the read included.
    [Theory]
    [MemberData(nameof(RewardRoutes))]
    public async Task RewardRoute_ForAManagerNotConfirmedOnTheId_Answers403_BeforeTheSettingsFilter(string method, string pathFormat, string? body)
    {
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WishCaptureAccess("row-1", "1001", IdConfirmed: false));

        var response = await SendAsync(_factory, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        await _factory.Wishes.DidNotReceiveWithAnyArgs().CanChangeSettingsAsync(default!, default!, default, default);
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    [Theory]
    [MemberData(nameof(RewardWrites))]
    public async Task RewardWrite_OnARowWithoutTwitchId_Answers409WishChannelUnbound(string method, string pathFormat, string? body)
    {
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WishCaptureAccess("row-1", null, IdConfirmed: false));

        var response = await SendAsync(_factory, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishChannelUnbound, await ReadErrorCodeAsync(response));
        await _factory.Wishes.DidNotReceiveWithAnyArgs().CanChangeSettingsAsync(default!, default!, default, default);
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    // Plan 2.4: the read of an unbound row answers channelBound false (the UI explains it); the service gives out no
    // K6 data for it.
    [Fact]
    public async Task RewardRead_OnARowWithoutTwitchId_GoesThrough_WithTheUnboundAccess()
    {
        var unbound = new WishCaptureAccess("row-1", null, IdConfirmed: false);
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(unbound);
        _factory.WishRewards.GetAsync(Arg.Any<WishCaptureAccess>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new WishRewardResult(
                WishRewardOutcome.Success, Rewards: new WishRewardsDto(false, [], 10, null, [], true, 14, null)));

        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/rewards");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("channelBound").GetBoolean());
        await _factory.WishRewards.Received(1).GetAsync(unbound, Manager, false, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(RewardRoutes))]
    public async Task RewardRoute_ForAnUnknownChannel_Answers404ChannelNotFound(string method, string pathFormat, string? body)
    {
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WishCaptureAccess?)null);

        var response = await SendAsync(_factory, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ChannelNotFound, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    // Confirmed: the service gets exactly the access the filter resolved (its ChannelId, never the name again).
    [Theory]
    [MemberData(nameof(RewardRoutes))]
    public async Task RewardRoute_ForAConfirmedManager_HandsTheResolvedAccessToTheService(string method, string pathFormat, string? body)
    {
        var response = await SendAsync(_factory, method, Path(pathFormat, Channel), body);

        Assert.True(response.IsSuccessStatusCode, $"{method} {pathFormat}: {(int)response.StatusCode}");
        var call = Assert.Single(_factory.WishRewards.ReceivedCalls());
        Assert.Same(Confirmed, call.GetArguments()[0]);
        await _factory.ChannelAccess.Received(1).ResolveWishCaptureAccessAsync(
            Arg.Is<TwitchPrincipalInfo>(p => p.TwitchUserId == Manager), Channel, Arg.Any<CancellationToken>());
    }

    // ---- The settings filter behind it (bot mode) ----

    [Theory]
    [MemberData(nameof(RewardWrites))]
    public async Task RewardWrite_ForAModInBotModeWithoutActivation_Answers403WishSettingsRestricted(string method, string pathFormat, string? body)
    {
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await SendAsync(_factory, method, Path(pathFormat, Channel), body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.WishSettingsRestricted, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    // The read is not a settings write: a manager who may not edit still reads (canEdit false comes from the service).
    [Fact]
    public async Task RewardRead_IsNotBehindTheSettingsFilter_AndForwardsCallerAndAdminFlag()
    {
        _factory.Wishes.CanChangeSettingsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Is<TwitchPrincipalInfo>(p => p.TwitchUserId == "admin")).Returns(true);

        var mod = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/rewards");
        var admin = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/rewards", userId: "admin");

        Assert.Equal(HttpStatusCode.OK, mod.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        await _factory.Wishes.DidNotReceiveWithAnyArgs().CanChangeSettingsAsync(default!, default!, default, default);
        await _factory.WishRewards.Received(1).GetAsync(Confirmed, Manager, false, Arg.Any<CancellationToken>());
        await _factory.WishRewards.Received(1).GetAsync(Confirmed, "admin", true, Arg.Any<CancellationToken>());
    }

    // ---- Bodies and route values ----

    [Theory]
    [InlineData("POST", "/api/channels/handofblood/wishes/rewards/learning/confirm")]
    [InlineData("PATCH", "/api/channels/handofblood/wishes/rewards/5")]
    public async Task RewardWrite_AnEmptyNullOrUnbindableBody_Answers400InvalidRequestBody(string method, string path)
    {
        foreach (var body in new[] { "", "null", "not json" })
        {
            var response = await SendAsync(_factory, method, path, body);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        }

        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    // A confirm without the ids reaches the service with the gaps as null; the service's refusal is the 400.
    [Fact]
    public async Task Confirm_WithoutIds_ForwardsTheGaps_AndTheRefusalIs400()
    {
        _factory.WishRewards.ConfirmLearningAsync(
                Arg.Any<WishCaptureAccess>(), Arg.Any<ConfirmWishRewardLearningRequest>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(WishRewardResult.Of(WishRewardOutcome.RequestInvalid));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/rewards/learning/confirm", """{"label":"Wunsch"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        await _factory.WishRewards.Received(1).ConfirmLearningAsync(
            Confirmed, new ConfirmWishRewardLearningRequest(null, null, "Wunsch", null), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_ForwardsTheShownIdsVerbatim_AndAnswers201WithTheReward()
    {
        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/rewards/learning/confirm", ConfirmBody);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(5, json.RootElement.GetProperty("id").GetInt64());
        await _factory.WishRewards.Received(1).ConfirmLearningAsync(
            Confirmed,
            new ConfirmWishRewardLearningRequest(Guid.Parse(SessionId), Guid.Parse(CandidateId), "Wunsch", 14),
            Arg.Is<AuditActor>(a => a.TwitchUserId == Manager),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?sessionId=")]
    [InlineData("?sessionId=not-a-guid")]
    [InlineData("?sessionId=00000000-0000-0000-0000-000000000000")]
    public async Task CancelLearning_WithoutAValidSessionId_Answers400_WithoutCallingTheService(string query)
    {
        var response = await SendAsync(_factory, "DELETE", $"/api/channels/{Channel}/wishes/rewards/learning{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequestBody, await ReadErrorCodeAsync(response));
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    [Fact]
    public async Task CancelLearning_ForwardsTheSession_AndAnswers204()
    {
        var response = await SendAsync(_factory, "DELETE", $"/api/channels/{Channel}/wishes/rewards/learning?sessionId={SessionId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await _factory.WishRewards.Received(1).CancelLearningAsync(
            Confirmed, Guid.Parse(SessionId), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("PATCH", """{"isActive":false}""")]
    [InlineData("DELETE", null)]
    public async Task RewardId_ThatIsNotNumeric_Answers404FromTheRoute(string method, string? body)
    {
        var response = await SendAsync(_factory, method, $"/api/channels/{Channel}/wishes/rewards/abc", body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
    }

    [Fact]
    public async Task UpdateAndDelete_ForwardTheRowId_AndAnswer200And204()
    {
        var patch = await SendAsync(_factory, "PATCH", $"/api/channels/{Channel}/wishes/rewards/5", """{"label":"Neu","defaultDurationDays":7}""");
        var delete = await SendAsync(_factory, "DELETE", $"/api/channels/{Channel}/wishes/rewards/5");

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await _factory.WishRewards.Received(1).UpdateRewardAsync(
            Confirmed, 5, new UpdateWishRewardRequest("Neu", 7, null), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _factory.WishRewards.Received(1).DeleteRewardAsync(Confirmed, 5, Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ---- Wire shape of the reward answers ----

    [Fact]
    public async Task RewardsRead_SerializesTheReadModel()
    {
        var response = await SendAsync(_factory, "GET", $"/api/channels/{Channel}/wishes/rewards");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        string[] names = ["channelBound", "rewards", "limit", "learning", "gaps", "canEdit", "pendingExpiryDays", "captureAliveAtUtc"];
        Assert.True(names.ToHashSet().SetEquals(root.EnumerateObject().Select(p => p.Name)));
        string[] rewardNames = ["id", "label", "defaultDurationDays", "isActive", "createdAtUtc", "lastRedemptionSeenAtUtc"];
        Assert.True(rewardNames.ToHashSet().SetEquals(root.GetProperty("rewards")[0].EnumerateObject().Select(p => p.Name)));
        var learning = root.GetProperty("learning");
        Assert.Equal(SessionId, learning.GetProperty("sessionId").GetString());
        var candidate = learning.GetProperty("candidate");
        string[] candidateNames = ["candidateId", "text", "login", "seenAtUtc"];
        Assert.True(candidateNames.ToHashSet().SetEquals(candidate.EnumerateObject().Select(p => p.Name)));
        Assert.Equal("2026-11-19T10:00:30Z", candidate.GetProperty("seenAtUtc").GetString());
        Assert.Equal(["fromUtc", "toUtc", "reason"], root.GetProperty("gaps")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(WishCaptureGapReason.Disconnect, root.GetProperty("gaps")[0].GetProperty("reason").GetString());
        // Twitch's tag value never leaves the Api (plan 2.4, W18).
        Assert.DoesNotContain("rewardId", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartLearning_Answers201WithTheSession()
    {
        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/rewards/learning");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(SessionId, json.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("2026-11-19T10:10:00Z", json.RootElement.GetProperty("expiresAtUtc").GetString());
    }

    // ---- Every reward outcome has a mapping ----

    public static TheoryData<WishRewardOutcome, HttpStatusCode, string?> RewardFailureOutcomes => new()
    {
        { WishRewardOutcome.ChannelNotFound, HttpStatusCode.NotFound, ApiErrorCodes.ChannelNotFound },
        { WishRewardOutcome.ChannelUnbound, HttpStatusCode.Conflict, ApiErrorCodes.WishChannelUnbound },
        // The service's second wall: 403 in the form of the manager filter, without a body.
        { WishRewardOutcome.AccessDenied, HttpStatusCode.Forbidden, null },
        { WishRewardOutcome.LearningActive, HttpStatusCode.Conflict, ApiErrorCodes.WishLearningActive },
        { WishRewardOutcome.LearningStale, HttpStatusCode.Conflict, ApiErrorCodes.WishLearningStale },
        { WishRewardOutcome.NoCandidate, HttpStatusCode.Conflict, ApiErrorCodes.WishRewardNoCandidate },
        { WishRewardOutcome.AlreadyKnown, HttpStatusCode.Conflict, ApiErrorCodes.WishRewardAlreadyKnown },
        { WishRewardOutcome.RewardLimit, HttpStatusCode.Conflict, ApiErrorCodes.WishRewardLimit },
        { WishRewardOutcome.RewardNotFound, HttpStatusCode.NotFound, ApiErrorCodes.WishRewardNotFound },
        { WishRewardOutcome.RequestInvalid, HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequestBody },
        { WishRewardOutcome.DurationOutOfRange, HttpStatusCode.BadRequest, ApiErrorCodes.WishDurationOutOfRange },
        // #487 T6: D7 behind the channel lock.
        { WishRewardOutcome.SettingsRestricted, HttpStatusCode.Forbidden, ApiErrorCodes.WishSettingsRestricted },
    };

    [Theory]
    [MemberData(nameof(RewardFailureOutcomes))]
    public async Task EveryRewardFailureOutcome_MapsToItsStatusAndErrorCode(WishRewardOutcome outcome, HttpStatusCode status, string? code)
    {
        _factory.WishRewards.StartLearningAsync(Arg.Any<WishCaptureAccess>(), Arg.Any<AuditActor>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(WishRewardResult.Of(outcome));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/rewards/learning");

        Assert.Equal(status, response.StatusCode);
        if (code is null)
        {
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal(code, await ReadErrorCodeAsync(response));
        }
    }

    [Fact]
    public void EveryRewardOutcome_HasAMappingCase_InThisTheory()
    {
        var covered = RewardFailureOutcomes.Select(row => (WishRewardOutcome)row[0]).ToHashSet();
        var all = Enum.GetValues<WishRewardOutcome>().Where(o => o != WishRewardOutcome.Success);

        Assert.True(covered.SetEquals(all));
    }

    // ---- approve, reject, unlink (plan 2.6) ----

    [Fact]
    public async Task Approve_ForwardsTheBody_AndAnswersTheWishWithExtendedExpiresAtUtc()
    {
        var extended = new DateTime(2026, 12, 20, 10, 0, 0, DateTimeKind.Utc);
        _factory.Wishes.ApproveAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<ApproveWishRequest>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new WishCommandResult(WishOutcome.Success, Wish: Wish() with { Status = WishStatus.Approved }),
                new WishCommandResult(
                    WishOutcome.Success, Wish: Wish() with { Status = WishStatus.Merged, MergedIntoWishId = 3 }, ExtendedExpiresAtUtc: extended));
        var path = $"/api/channels/{Channel}/wishes/7/approve";

        var approved = await SendAsync(_factory, "POST", path, """{"sevenTvEmoteId":"60ae958e229664e8667aea38","alias":"Kappa","durationDays":7}""");
        var merged = await SendAsync(_factory, "POST", path, "{}");

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var approvedJson = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, approvedJson.RootElement.GetProperty("extendedExpiresAtUtc").ValueKind);
        Assert.Equal(7, approvedJson.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        using var mergedJson = JsonDocument.Parse(await merged.Content.ReadAsStringAsync());
        Assert.Equal("2026-12-20T10:00:00Z", mergedJson.RootElement.GetProperty("extendedExpiresAtUtc").GetString());
        Assert.Equal(3, mergedJson.RootElement.GetProperty("mergedIntoWishId").GetInt64());

        await _factory.Wishes.Received(1).ApproveAsync(
            Channel, 7, new ApproveWishRequest("60ae958e229664e8667aea38", null, "Kappa", 7), Arg.Any<AuditActor>(), Confirmed,
            Arg.Any<CancellationToken>());
        await _factory.Wishes.Received(1).ApproveAsync(
            Channel, 7, new ApproveWishRequest(null, null, null, null), Arg.Any<AuditActor>(), Confirmed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_Refusals_UseTheWishMapping()
    {
        _factory.Wishes.ApproveAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<ApproveWishRequest>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(),
                Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.RunningNotExtendable, ConflictingWishId: 3));

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/approve", "{}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.WishRunningNotExtendable, json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("wishId").GetInt64());
    }

    [Theory]
    [InlineData("reject", nameof(IWishService.RejectAsync))]
    [InlineData("unlink", nameof(IWishService.UnlinkAsync))]
    public async Task RejectAndUnlink_ForwardChannelIdActorAndAccess(string command, string method)
    {
        StubCommandsSucceed();

        var response = await SendAsync(_factory, "POST", $"/api/channels/{Channel}/wishes/7/{command}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var args = Assert.Single(_factory.Wishes.ReceivedCalls(), c => c.GetMethodInfo().Name == method).GetArguments();
        Assert.Equal((Channel, 7L, Manager), ((string)args[0]!, (long)args[1]!, ((AuditActor)args[2]!).TwitchUserId));
        Assert.Same(Confirmed, args[3]);
    }

    // ---- Every wish answer is decided with the id-bound access (plan 2.4a) ----

    public static TheoryData<string, string, string?, string> WishAnswerRoutes => new()
    {
        { "GET", "/api/channels/{0}/wishes", null, nameof(IWishService.ListAsync) },
        { "GET", "/api/channels/{0}/wishes/7", null, nameof(IWishService.GetAsync) },
        { "POST", "/api/channels/{0}/wishes/adopt", """{"slotId":"s1","expectedAlias":"Kappa"}""", nameof(IWishService.AdoptAsync) },
        { "PATCH", "/api/channels/{0}/wishes/7", """{"durationDays":7}""", nameof(IWishService.UpdateAsync) },
        { "POST", "/api/channels/{0}/wishes/7/extend", null, nameof(IWishService.ExtendAsync) },
        { "POST", "/api/channels/{0}/wishes/7/keep", null, nameof(IWishService.KeepAsync) },
        { "POST", "/api/channels/{0}/wishes/7/close", null, nameof(IWishService.CloseAsync) },
        { "POST", "/api/channels/{0}/wishes/7/retry", null, nameof(IWishService.RetryAsync) },
        { "POST", "/api/channels/{0}/wishes/7/re-add", """{"mode":"timed"}""", nameof(IWishService.ReAddAsync) },
        {
            "POST", "/api/channels/{0}/wishes/7/complete-removal",
            """{"slotId":"slot-1","slotAddedAtUtc":"2026-11-02T10:00:00Z","emoteSetId":"01H0000000000000000000SET1"}""",
            nameof(IWishService.CompleteRemovalAsync)
        },
        { "POST", "/api/channels/{0}/wishes/7/approve", "{}", nameof(IWishService.ApproveAsync) },
        { "POST", "/api/channels/{0}/wishes/7/reject", null, nameof(IWishService.RejectAsync) },
        { "POST", "/api/channels/{0}/wishes/7/unlink", null, nameof(IWishService.UnlinkAsync) },
    };

    // Not confirmed still reaches the route (the manager check decides access, plan 2.4a); the service only empties the
    // redeemer fields. Both answers are passed through unchanged, and so is "no row" (null).
    [Theory]
    [MemberData(nameof(WishAnswerRoutes))]
    public async Task WishAnswerRoute_HandsTheResolvedAccessToTheService_ConfirmedUnconfirmedOrNone(
        string method, string pathFormat, string? body, string serviceMethod)
    {
        var unconfirmed = new WishCaptureAccess("row-1", "1001", IdConfirmed: false);
        _factory.ChannelAccess.ResolveWishCaptureAccessAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Confirmed, unconfirmed, null);

        foreach (var expected in new[] { Confirmed, unconfirmed, null })
        {
            _factory.Wishes.ClearReceivedCalls();

            await SendAsync(_factory, method, Path(pathFormat, Channel), body);

            var call = Assert.Single(_factory.Wishes.ReceivedCalls(), c => c.GetMethodInfo().Name == serviceMethod);
            var accessIndex = Array.FindIndex(call.GetMethodInfo().GetParameters(), p => p.ParameterType == typeof(WishCaptureAccess));
            Assert.True(accessIndex >= 0, serviceMethod);
            Assert.Same(expected, call.GetArguments()[accessIndex]);
        }

        await _factory.ChannelAccess.Received(3).ResolveWishCaptureAccessAsync(
            Arg.Is<TwitchPrincipalInfo>(p => p.TwitchUserId == Manager), Channel, Arg.Any<CancellationToken>());
    }

    // ---- Same login, other Twitch id: the real ChannelAccessService behind the real pipeline (Sol-R2-P1-1) ----

    // The row "handofblood" carries Twitch id 1001. The caller moderates, per Twitch, a channel of the same login whose
    // BroadcasterId is 2002 (the login was released and taken over). The manager check matches the login and lets
    // them in; the id question says no: 403 on the reward routes, an unconfirmed access on the wish routes. With
    // BroadcasterId 1001 the same caller is confirmed.
    [Theory]
    [InlineData("2002", false)]
    [InlineData("1001", true)]
    public async Task SameLoginOtherTwitchId_IsRefusedOnRewards_AndUnconfirmedOnWishes(string listedBroadcasterId, bool confirmed)
    {
        var moderatorCheck = Substitute.For<IModeratorCheckService>();
        moderatorCheck.IsModeratorAsync(Arg.Any<TwitchPrincipalInfo>(), Channel, Arg.Any<CancellationToken>()).Returns(true);
        var moderatedChannels = Substitute.For<IModeratedChannelsProvider>();
        moderatedChannels.GetModeratedChannelsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(new ModeratedChannelsLookup([new TwitchModeratedChannelInfo(Channel, listedBroadcasterId)], ReauthRequired: false));
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync(Channel, Arg.Any<CancellationToken>())
            .Returns(new Channel { Id = "row-1", ChannelName = Channel, TwitchChannelId = "1001" });

        using var real = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IChannelAccessService>(sp => new ChannelAccessService(
                moderatorCheck,
                moderatedChannels,
                Substitute.For<ISevenTvEditorService>(),
                channels,
                sp.GetRequiredService<IGlobalAdminAllowlist>(),
                NullLogger<ChannelAccessService>.Instance))));
        _factory.Wishes.ListAsync(
                Arg.Any<string>(), Arg.Any<WishListView>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<WishDto>([], 1, 50, 0));

        var rewards = await SendAsync(real, "GET", $"/api/channels/{Channel}/wishes/rewards", userId: "mod-of-new-owner");
        var list = await SendAsync(real, "GET", $"/api/channels/{Channel}/wishes", userId: "mod-of-new-owner");

        Assert.Equal(confirmed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, rewards.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var expected = new WishCaptureAccess("row-1", "1001", confirmed);
        await _factory.Wishes.Received(1).ListAsync(
            Channel, WishListView.Open, Arg.Any<int>(), Arg.Any<int>(), expected, Arg.Any<CancellationToken>());
        if (confirmed)
        {
            await _factory.WishRewards.Received(1).GetAsync(expected, "mod-of-new-owner", false, Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.Empty(_factory.WishRewards.ReceivedCalls());
        }
    }

    // ---- /permissions ----

    [Fact]
    public async Task Permissions_CarryWishCaptureEnabled_AsTheApiSwitchTogetherWithWishes()
    {
        using var captureOff = _factory.WithWishCaptureDisabled();
        using var wishesOff = _factory.WithWishesDisabled();
        var path = $"/api/channels/{Channel}/permissions";

        using var on = JsonDocument.Parse(await (await SendAsync(_factory, "GET", path)).Content.ReadAsStringAsync());
        using var off = JsonDocument.Parse(await (await SendAsync(captureOff, "GET", path)).Content.ReadAsStringAsync());
        // Capture switched on, wishes off: not effective (WishOptions.CaptureActive).
        using var onlyCapture = JsonDocument.Parse(await (await SendAsync(wishesOff, "GET", path)).Content.ReadAsStringAsync());

        Assert.True(on.RootElement.GetProperty("wishCaptureEnabled").GetBoolean());
        Assert.False(off.RootElement.GetProperty("wishCaptureEnabled").GetBoolean());
        Assert.True(off.RootElement.GetProperty("wishesEnabled").GetBoolean());
        Assert.False(onlyCapture.RootElement.GetProperty("wishCaptureEnabled").GetBoolean());
    }

    // ---- Admin health (plan 2.3) ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AdminHealth_CarriesTheWorkersCaptureState_AndTheApisSwitch(bool? workerActive)
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        _factory.WorkerHealth.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkerHealthSnapshot(
                IsConnected: true,
                LastMessageReceivedUtc: DateTime.UtcNow,
                ConnectAttemptedUtc: DateTime.UtcNow,
                WishCaptureActive: workerActive));
        using var captureOff = _factory.WithWishCaptureDisabled();

        using var on = JsonDocument.Parse(await (await SendAsync(_factory, "GET", "/api/admin/health")).Content.ReadAsStringAsync());
        using var off = JsonDocument.Parse(await (await SendAsync(captureOff, "GET", "/api/admin/health")).Content.ReadAsStringAsync());

        var active = on.RootElement.GetProperty("wishCaptureActive");
        // Null is "a worker that predates the field", never "off".
        Assert.Equal(workerActive is null ? JsonValueKind.Null : workerActive.Value ? JsonValueKind.True : JsonValueKind.False, active.ValueKind);
        Assert.True(on.RootElement.GetProperty("apiWishCaptureEnabled").GetBoolean());
        Assert.False(off.RootElement.GetProperty("apiWishCaptureEnabled").GetBoolean());
        Assert.Equal(active.ValueKind, off.RootElement.GetProperty("wishCaptureActive").ValueKind);
    }

    [Fact]
    public async Task AdminHealth_WithoutASnapshot_CarriesUnknownWorkerCapture_AndTheApisSwitch()
    {
        _factory.ChannelAccess.IsGlobalAdmin(Arg.Any<TwitchPrincipalInfo>()).Returns(true);
        _factory.WorkerHealth.ReadAsync(Arg.Any<CancellationToken>()).Returns((WorkerHealthSnapshot?)null);

        using var json = JsonDocument.Parse(await (await SendAsync(_factory, "GET", "/api/admin/health")).Content.ReadAsStringAsync());

        Assert.False(json.RootElement.GetProperty("snapshotAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("wishCaptureActive").ValueKind);
        Assert.True(json.RootElement.GetProperty("apiWishCaptureEnabled").GetBoolean());
    }

    // ---- Helpers ----

    private void AssertNothingAskedBehindTheSwitch()
    {
        Assert.Empty(_factory.WishRewards.ReceivedCalls());
        Assert.DoesNotContain(
            _factory.ChannelAccess.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IChannelAccessService.ResolveWishCaptureAccessAsync));
        Assert.DoesNotContain(
            _factory.Wishes.ReceivedCalls(),
            c => c.GetMethodInfo().Name == nameof(IWishService.CanChangeSettingsAsync));
    }

    private void StubCommandsSucceed()
    {
        _factory.Wishes.ApproveAsync(
                Arg.Any<string>(), Arg.Any<long>(), Arg.Any<ApproveWishRequest>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(),
                Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));
        _factory.Wishes.RejectAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));
        _factory.Wishes.UnlinkAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AuditActor>(), Arg.Any<WishCaptureAccess?>(), Arg.Any<CancellationToken>())
            .Returns(new WishCommandResult(WishOutcome.Success, Wish: Wish()));
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

    private static WishRewardsDto RewardsDto() => new(
        ChannelBound: true,
        Rewards: [RewardDto()],
        Limit: 10,
        Learning: LearningDto() with
        {
            Candidate = new WishRewardCandidateDto(
                Guid.Parse(CandidateId), "EPTEST", "viewer", new DateTime(2026, 11, 19, 10, 0, 30, DateTimeKind.Utc)),
        },
        Gaps:
        [
            new WishCaptureGapDto(
                new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 18, 9, 5, 0, DateTimeKind.Utc),
                WishCaptureGapReason.Disconnect),
        ],
        CanEdit: true,
        PendingExpiryDays: 14,
        CaptureAliveAtUtc: new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc));

    private static WishRewardLearningDto LearningDto() => new(
        Guid.Parse(SessionId),
        new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 11, 19, 10, 10, 0, DateTimeKind.Utc),
        Candidate: null);

    private static WishRewardDto RewardDto() => new(
        5, "Wunsch", 14, IsActive: true, new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc), LastRedemptionSeenAtUtc: null);

    private static WishDto Wish() => new(
        Id: 7,
        SevenTvEmoteId: "01H0000000000000000000000A",
        EmoteName: "Kappa",
        Alias: "Kappa",
        Source: WishSource.Irc,
        Status: WishStatus.Pending,
        StatusReason: null,
        RequesterLogin: "viewer",
        DurationDays: 14,
        CreatedAtUtc: new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc),
        ApprovedAtUtc: null,
        StartsAtUtc: null,
        StartsAtAuthoritative: false,
        ProbationStartsAtUtc: null,
        ExpiresAtUtc: null,
        EndedAtUtc: null,
        SlotId: null,
        SlotAddedAtUtc: null,
        ImageUrl: null,
        LinkOrigin: null,
        ExtensionCount: 0,
        Permanent: false,
        BotOwned: false,
        BotWritePending: false,
        BotNextAttemptAtUtc: null,
        RemovalQueuedAtUtc: null,
        Mode: "manual");
}
