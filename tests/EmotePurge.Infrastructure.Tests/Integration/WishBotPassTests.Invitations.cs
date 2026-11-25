using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Step 2 of the bot pass (#487 plan 2.5b, 2.9; T8c) against real Postgres: the acceptance of invitations after [F7], the
// acceptance attempt and its resolution by the editor read, the consent bound to the bot id, the retry stamp, the
// visibility window and the confirmation of an activation. The write client is a substitute that counts every acceptance
// and can make it take effect at "7TV" (the editor read's answer) or not; the session is the hand-made one.
public partial class WishBotPassTests
{
    private readonly List<AcceptCall> _acceptCalls = [];
    private readonly Queue<SevenTvWriteResult> _acceptResults = new();
    private bool _acceptTakesEffect = true;
    private Func<Task>? _whileAccepting;
    private FakeSession? _lastSession;

    // ------------------------------------------------------------------------------------ Revision 6: the happy path

    // A request and an invitation: exactly one acceptance with the bound bot id and the owner, stamped right before the
    // call; one audit (trigger activation); the fresh read shows editor and the request is confirmed in the same pass.
    [Fact]
    public async Task ARequestAndAnInvitation_AcceptsOnce_ReadsAgain_AndConfirmsInTheSamePass()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));

        await RunPassAsync();

        var call = Assert.Single(_acceptCalls);
        Assert.Equal((BotA, OwnerOf(channel), "sent"), (call.BotId, call.OwnerId, call.LastSessionCall));
        var calls = _lastSession!.Calls;
        Assert.True(calls.IndexOf("action") < calls.IndexOf("wait") && calls.IndexOf("wait") < calls.IndexOf("sent"));
        var settings = await SettingsAsync(channel);
        Assert.True(settings.BotModeEnabled);
        Assert.Equal((Activator, "requester", Now.AddMinutes(-1)), (settings.BotModeActivatedByTwitchUserId, settings.BotModeActivatedByLogin, settings.BotModeActivatedAtUtc));
        Assert.Equal((Now, Identity), (settings.BotEditorCheckedAtUtc, settings.BotEditorCheckedIdentityVersion));
        Assert.Null(settings.BotActivationRequestedAtUtc);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.Null(settings.BotActivationError);
        Assert.Equal(BotA, settings.BotConsentSevenTvUserId);
        Assert.Equal(WishBotChannelMode.Bot, ModeOf(settings, listed: true));
        var accept = Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation));
        Assert.Equal(AuditActor.System.TwitchUserId, accept.Actor);
        Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activation" }, accept.Details);
        Assert.Equal(Activator, Assert.Single(await AuditsAsync(AuditActions.WishBotActivate)).Actor);
        await _sevenTv.Received(2).GetEditorForAsync(BotA, Arg.Any<CancellationToken>());
    }

    // Idempotent: the read shows editor already, nothing is sent, the request is confirmed.
    [Fact]
    public async Task ARequestWhereTheBotIsEditor_IsConfirmedWithoutAcceptance()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync(configure: s => s.BotInvitationRetryAtUtc = Now.AddMinutes(-1));
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));

        await RunPassAsync();

        Assert.Empty(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.True(settings.BotModeEnabled);
        Assert.Null(settings.BotInvitationRetryAtUtc);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAcceptInvitation));
        Assert.Single(await AuditsAsync(AuditActions.WishBotActivate));
    }

    // Ok, but the fresh read still shows invited: the attempt stays open, no second acceptance in this pass, no
    // confirmation; the next pass's read resolves it (no second audit) and confirms.
    [Fact]
    public async Task AnOkWhoseFreshReadStillShowsInvited_StaysOpen_AndTheNextPassConfirms()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptTakesEffect = false;

        await RunPassAsync();

        Assert.Single(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.Equal(Now, settings.BotInvitationAttemptAtUtc);
        Assert.False(settings.BotModeEnabled);
        Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation));

        TakeEffect(BotA, OwnerOf(channel));
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.Single(_acceptCalls);
        settings = await SettingsAsync(channel);
        Assert.True(settings.BotModeEnabled);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation));
    }

    // An invitation without "manage emote sets" brings no write right: no acceptance; after the window the request ends
    // with not_editor.
    [Fact]
    public async Task AnInvitationWithoutManage_IsNotAccepted_AndTheRequestEndsNotEditorAfterTheWindow()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync(requestedAt: Now);
        Editors(BotA, (channel.TwitchId, "PENDING", false));

        await RunPassAsync();
        Assert.Null((await SettingsAsync(channel)).BotActivationError);

        _clock.Advance(TimeSpan.FromSeconds(300));
        await RunPassAsync();

        Assert.Empty(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.Equal((false, (DateTime?)null, WishBotChannelReasons.NotEditor), (settings.BotModeEnabled, settings.BotActivationRequestedAtUtc, settings.BotActivationError));
    }

    // [F7]: an invitation without request or activation, also of a channel Emote Purge does not know, is never answered,
    // over several passes.
    [Fact]
    public async Task AnInvitationWithoutRequestOrActivation_IsNeverAnswered_OverSeveralPasses()
    {
        await SeedAccountAsync(BotA);
        var manual = await SeedChannelAsync(botMode: false);
        var bound = await SeedChannelAsync(botMode: false, configure: s => s.BotConsentSevenTvUserId = BotA);
        Editors(BotA, (manual.TwitchId, "PENDING", true), (bound.TwitchId, "PENDING", true), ("999999999", "PENDING", true));

        for (var pass = 0; pass < 3; pass++)
        {
            await RunPassAsync();
            _clock.Advance(TimeSpan.FromSeconds(61));
        }

        Assert.Empty(_acceptCalls);
        Assert.Null((await SettingsAsync(manual)).BotInvitationAttemptAtUtc);
        Assert.Null((await SettingsAsync(bound)).BotInvitationAttemptAtUtc);
    }

    // Case (b): an activated channel suspended as not_editor is invited anew: accepted at the next read and resumed, two
    // audits (wish.botAcceptInvitation with trigger activated, wish.botSuspend resumed).
    [Fact]
    public async Task ASuspendedActivatedChannel_InvitedAnew_IsAcceptedAndResumed()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedChannelAsync(configure: s => s.BotSuspendedReason = WishBotChannelReasons.NotEditor);
        Editors(BotA, (channel.TwitchId, "PENDING", true));

        await RunPassAsync();

        Assert.Single(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.Null(settings.BotSuspendedReason);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.Equal(WishBotChannelMode.Bot, ModeOf(settings, listed: true));
        Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activated" }, Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Details);
        Assert.Equal(new[] { (channel.Name, "resumed", "True") }, await SuspendAuditsAsync());
    }

    // An activated channel with editor turns invited between two reads: accepted without a suspension.
    [Fact]
    public async Task AnEditorChannelTurnedInvited_IsAcceptedWithoutSuspension()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedChannelAsync();
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        await RunPassAsync();

        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.Single(_acceptCalls);
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Empty(await SuspendAuditsAsync());
    }

    // Not covered (not_listed), the kill switch, an invalid token or the bot switch off: no acceptance.
    [Theory]
    [InlineData("unlisted")]
    [InlineData("killSwitch")]
    [InlineData("invalid")]
    [InlineData("switchedOff")]
    public async Task UncoveredKillSwitchInvalidOrSwitchedOff_AcceptsNothing(string blocker)
    {
        await SeedAccountAsync(BotA, status: blocker == "invalid" ? BotAccountStatus.Invalid : BotAccountStatus.Valid);
        if (blocker == "killSwitch")
        {
            await using var db = CreateDb();
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.KillSwitch, true));
        }

        var activated = await SeedChannelAsync();
        var requested = await SeedRequestAsync();
        Editors(BotA, (activated.TwitchId, "PENDING", true), (requested.TwitchId, "PENDING", true));

        await RunPassAsync(pilot: blocker == "unlisted" ? "999" : "*", botAccountEnabled: blocker != "switchedOff");

        Assert.Empty(_acceptCalls);
        Assert.Equal(Now.AddMinutes(-1), (await SettingsAsync(requested)).BotActivationRequestedAtUtc);
        Assert.Null((await SettingsAsync(requested)).BotInvitationAttemptAtUtc);
        if (blocker == "unlisted")
        {
            Assert.Equal(WishBotChannelReasons.NotListed, (await SettingsAsync(activated)).BotSuspendedReason);
        }
    }

    // AK85, E8: a request outside the pilot list is covered only when its requester is a global admin by id; then it is
    // confirmed (the admin becomes the activator), otherwise it waits.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARequestOutsideTheList_IsConfirmedOnlyForAGlobalAdminById(bool admin)
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        _admins.IsAdminById(Activator).Returns(admin);

        await RunPassAsync(pilot: "");

        var settings = await SettingsAsync(channel);
        Assert.Equal(admin, settings.BotModeEnabled);
        Assert.Equal(admin ? null : Now.AddMinutes(-1), settings.BotActivationRequestedAtUtc);
        Assert.Null(settings.BotActivationError);
    }

    // ------------------------------------------------------------------------------------ the answers of the acceptance

    // RateLimited: nothing was done at 7TV, so the attempt is cleared and the pause reported in the same transaction; the
    // pass calls nothing with the token afterwards (K6d-6), the request stays, the activated channel is invitation_pending.
    [Fact]
    public async Task RateLimited_ClearsTheAttempt_PausesAndCallsNothingMoreWithTheToken()
    {
        await SeedAccountAsync(BotA);
        var requested = await SeedRequestAsync();
        var activated = await SeedChannelAsync();
        Editors(BotA, (requested.TwitchId, "PENDING", true), (activated.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(30)));
        _acceptTakesEffect = false;

        await RunPassAsync();

        Assert.Single(_acceptCalls);
        Assert.Equal([TimeSpan.FromSeconds(30)], _lastSession!.Pauses);
        foreach (var channel in new[] { requested, activated })
        {
            Assert.Null((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        }

        Assert.Equal(Now.AddMinutes(-1), (await SettingsAsync(requested)).BotActivationRequestedAtUtc);
        Assert.Equal(WishBotChannelReasons.InvitationPending, (await SettingsAsync(activated)).BotSuspendedReason);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAcceptInvitation));
    }

    // K6b-3: Unauthorized is reported (the next pass's verification decides), the attempt cleared, nothing else called with
    // the token in this pass, request and channel unchanged.
    [Fact]
    public async Task Unauthorized_IsReported_ClearsTheAttempt_AndNothingElseIsSent()
    {
        await SeedAccountAsync(BotA);
        var requested = await SeedRequestAsync();
        var activated = await SeedChannelAsync();
        Editors(BotA, (requested.TwitchId, "PENDING", true), (activated.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Unauthorized());
        _acceptTakesEffect = false;

        await RunPassAsync();

        Assert.Single(_acceptCalls);
        Assert.Contains("unauthorized", _lastSession!.Calls);
        Assert.Null((await SettingsAsync(requested)).BotInvitationAttemptAtUtc);
        Assert.Equal(Now.AddMinutes(-1), (await SettingsAsync(requested)).BotActivationRequestedAtUtc);
        Assert.Equal(BotAccountStatus.Valid, (await CredentialAsync()).Status);
    }

    // LackingPrivileges ends this run: case (a) the request with invitation_not_accepted; case (b) invitation_pending, one
    // wish.botSuspend and the retry stamp in 15 minutes.
    [Fact]
    public async Task LackingPrivileges_EndsTheRequest_OrSuspendsInvitationPendingWithARetryStamp()
    {
        await SeedAccountAsync(BotA);
        var requested = await SeedRequestAsync();
        var activated = await SeedChannelAsync();
        Editors(BotA, (requested.TwitchId, "PENDING", true), (activated.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.LackingPrivileges());
        _acceptResults.Enqueue(SevenTvWriteResult.LackingPrivileges());
        _acceptTakesEffect = false;

        await RunPassAsync();

        Assert.Equal(2, _acceptCalls.Count);
        var request = await SettingsAsync(requested);
        Assert.Equal((false, (DateTime?)null, WishBotChannelReasons.InvitationNotAccepted, (DateTime?)null),
            (request.BotModeEnabled, request.BotActivationRequestedAtUtc, request.BotActivationError, request.BotInvitationAttemptAtUtc));
        var channel = await SettingsAsync(activated);
        Assert.Equal((WishBotChannelReasons.InvitationPending, Now.AddMinutes(15), (DateTime?)null),
            (channel.BotSuspendedReason, channel.BotInvitationRetryAtUtc, channel.BotInvitationAttemptAtUtc));
        Assert.Equal(new[] { (activated.Name, "reason", WishBotChannelReasons.InvitationPending) }, await SuspendAuditsAsync());
    }

    // ------------------------------------------------------------------------------------ S6-1: the attempt survives

    // A crash between the attempt's commit and the send: the next holder does not send but reads; invited before the
    // window → open, after it → the attempt is cleared and exactly one new acceptance follows.
    [Fact]
    public async Task ACrashBetweenStampAndSend_TheNextHolderReads_AndAcceptsOnceAfterTheWindow()
    {
        await SeedAccountAsync(BotA);
        var stamp = Now;
        var channel = await SeedRequestAsync(configure: s => s.BotInvitationAttemptAtUtc = stamp);
        Editors(BotA, (channel.TwitchId, "PENDING", true));

        await RunPassAsync();
        Assert.Empty(_acceptCalls);
        Assert.Equal(stamp, (await SettingsAsync(channel)).BotInvitationAttemptAtUtc);

        _clock.Advance(TimeSpan.FromSeconds(340));
        await RunPassAsync();

        Assert.Single(_acceptCalls);
        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotInvitationUnresolved));
    }

    // Timeout, then DELETE (invitation_accept_unresolved), and the acceptance takes effect late: the next read shows
    // editor → invitation_accepted_after_abort, one audit with resolved and afterAbort, the bot mode stays off.
    [Fact]
    public async Task ATimeoutThenDelete_ALateEffect_IsAcceptedAfterAbort()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Timeout());
        _acceptTakesEffect = false;

        await RunPassAsync();
        await DeleteBotAsync(channel);
        Assert.Equal(WishBotChannelReasons.InvitationAcceptUnresolved, (await SettingsAsync(channel)).BotActivationError);

        TakeEffect(BotA, OwnerOf(channel));
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.Single(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.Equal((false, (DateTime?)null, (string?)null, WishBotChannelReasons.InvitationAcceptedAfterAbort),
            (settings.BotModeEnabled, settings.BotInvitationAttemptAtUtc, settings.BotConsentSevenTvUserId, settings.BotActivationError));
        Assert.Equal(
            new Dictionary<string, string> { ["resolved"] = "True", ["afterAbort"] = "True" },
            Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Details);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));
    }

    // K6e-1: Timeout, DELETE, and the acceptance never takes effect: a read before the end of the window books nothing;
    // the first after it clears attempt, error and binding with exactly one wish.botInvitationUnresolved (attemptedAtUtc
    // only). Without the DELETE (the consent still stands) the same negative read clears the attempt without that audit.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATimeoutNeverTakingEffect_ResolvesNegativelyAfterTheWindow_AuditedOnlyWithoutConsent(bool delete)
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Timeout());
        _acceptTakesEffect = false;
        var stamp = Now;

        await RunPassAsync();
        if (delete)
        {
            await DeleteBotAsync(channel);
        }

        Editors(BotA);
        _clock.Advance(TimeSpan.FromSeconds(339));
        await RunPassAsync();
        Assert.Equal(stamp, (await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotInvitationUnresolved));

        _clock.Advance(TimeSpan.FromSeconds(1));
        await RunPassAsync();

        var settings = await SettingsAsync(channel);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAcceptInvitation));
        Assert.Single(_acceptCalls);
        if (delete)
        {
            Assert.Equal(((string?)null, (string?)null), (settings.BotActivationError, settings.BotConsentSevenTvUserId));
            var unresolved = Assert.Single(await AuditsAsync(AuditActions.WishBotInvitationUnresolved));
            Assert.Equal(AuditActor.System.TwitchUserId, unresolved.Actor);
            Assert.Equal(["attemptedAtUtc"], unresolved.Details.Keys);
            Assert.Equal(
                stamp,
                DateTime.Parse(unresolved.Details["attemptedAtUtc"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal));
        }
        else
        {
            Assert.Empty(await AuditsAsync(AuditActions.WishBotInvitationUnresolved));
            Assert.Equal(WishBotChannelReasons.NotEditor, settings.BotActivationError);
        }
    }

    // Ok, then DELETE between the send transaction and the fresh read: like a late effect (after abort), and no second
    // audit (the Ok booked one).
    [Fact]
    public async Task AnOkThenDeleteBeforeTheFreshRead_IsAcceptedAfterAbort_WithoutASecondAudit()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        var session = await SessionAsync();
        session.OnProbe = async probe =>
        {
            // 1, 2: the first read; 3: before the stamp; 4: after it; 5: after the send transaction.
            if (probe == 5)
            {
                await DeleteBotAsync(channel);
            }

            return true;
        };

        await RunPassAsync(session: session);

        var settings = await SettingsAsync(channel);
        Assert.Equal((false, WishBotChannelReasons.InvitationAcceptedAfterAbort), (settings.BotModeEnabled, settings.BotActivationError));
        Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activation" }, Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Details);
    }

    // The kill switch, a 429 pause and an invalid token with an open attempt: the unauthenticated read resolves it
    // anyway (nothing is sent); while the account is unavailable the request waits, and it is confirmed once lifted. A
    // pause is no unavailability: confirmed at once.
    [Theory]
    [InlineData("killSwitch")]
    [InlineData("paused")]
    [InlineData("invalid")]
    public async Task AnOpenAttempt_IsResolvedByTheRead_UnderKillSwitchPauseOrInvalid(string blocker)
    {
        await SeedAccountAsync(BotA, status: blocker == "invalid" ? BotAccountStatus.Invalid : BotAccountStatus.Valid);
        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u
                .SetProperty(c => c.KillSwitch, blocker == "killSwitch")
                .SetProperty(c => c.PausedUntilUtc, blocker == "paused" ? Now.AddMinutes(10) : null));
        }

        var channel = await SeedRequestAsync(configure: s => s.BotInvitationAttemptAtUtc = Now.AddSeconds(-5));
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));

        await RunPassAsync();

        Assert.Empty(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activation", ["resolved"] = "True" }, Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Details);
        Assert.Equal(blocker == "paused", settings.BotModeEnabled);

        await SeedAccountAsync(BotA);
        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.KillSwitch, false).SetProperty(c => c.PausedUntilUtc, (DateTime?)null));
        }

        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
        Assert.Empty(_acceptCalls);
    }

    // activate after a DELETE with an open attempt (same bot id): the read's editor confirms the new request normally.
    [Fact]
    public async Task ActivateAfterDeleteWithAnOpenAttempt_SameBotId_TheReadConfirmsTheNewRequest()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync(configure: s => s.BotInvitationAttemptAtUtc = Now.AddSeconds(-5));
        await DeleteBotAsync(channel);
        await ActivateAsync(channel);
        var settings = await SettingsAsync(channel);
        Assert.Equal((BotA, (string?)null), (settings.BotConsentSevenTvUserId, settings.BotActivationError));
        Assert.NotNull(settings.BotInvitationAttemptAtUtc);
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));

        await RunPassAsync();

        settings = await SettingsAsync(channel);
        Assert.True(settings.BotModeEnabled);
        Assert.Equal("900001", settings.BotModeActivatedByTwitchUserId);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.Empty(_acceptCalls);
    }

    // ------------------------------------------------------------------------------------ K6b-1: Rejected is unclear

    // Rejected (as 7TV answers an invitation that is accepted already) + a later read with editor: accepted, one audit with
    // resolved, the wait cleared, confirmed as after an Ok.
    [Fact]
    public async Task Rejected_ThenAReadWithEditor_IsAccepted_ResolvedAndConfirmed()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Rejected(SevenTvWriteRejection.Unknown));

        await RunPassAsync();
        var settings = await SettingsAsync(channel);
        Assert.Equal((Now, Now.AddMinutes(15)), (settings.BotInvitationAttemptAtUtc, settings.BotInvitationRetryAtUtc));

        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.Single(_acceptCalls);
        settings = await SettingsAsync(channel);
        Assert.True(settings.BotModeEnabled);
        Assert.Null(settings.BotInvitationRetryAtUtc);
        Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activation", ["resolved"] = "True" }, Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Details);
    }

    // Rejected + invited: open before the window, no second call; after it "not accepted": the request ends with
    // invitation_not_accepted, the activated channel is invitation_pending and accepts exactly once after the wait. The
    // successor (a new session, a new epoch) tells Rejected from Timeout by the wait.
    [Fact]
    public async Task Rejected_ThenInvited_IsNotAcceptedAfterTheWindow_AndRetriedOnceAfterTheWait()
    {
        await SeedAccountAsync(BotA);
        var requested = await SeedRequestAsync();
        var activated = await SeedChannelAsync();
        Editors(BotA, (requested.TwitchId, "PENDING", true), (activated.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Rejected(SevenTvWriteRejection.Unknown));
        _acceptResults.Enqueue(SevenTvWriteResult.Rejected(SevenTvWriteRejection.Unknown));
        _acceptTakesEffect = false;

        await RunPassAsync();
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();
        Assert.Equal(2, _acceptCalls.Count);

        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.RunnerEpoch, Epoch + 1));
        }

        _clock.Advance(TimeSpan.FromSeconds(340 - 61));
        await RunPassAsync(session: await SessionAsync(epoch: Epoch + 1));

        Assert.Equal(2, _acceptCalls.Count);
        var request = await SettingsAsync(requested);
        Assert.Equal((WishBotChannelReasons.InvitationNotAccepted, (DateTime?)null), (request.BotActivationError, request.BotActivationRequestedAtUtc));
        var channel = await SettingsAsync(activated);
        Assert.Equal(WishBotChannelReasons.InvitationPending, channel.BotSuspendedReason);
        Assert.NotNull(channel.BotInvitationRetryAtUtc);

        _acceptTakesEffect = true;
        _clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(340));
        await RunPassAsync(session: await SessionAsync(epoch: Epoch + 1));

        Assert.Equal(3, _acceptCalls.Count);
        Assert.Equal(OwnerOf(activated), _acceptCalls[^1].OwnerId);
        Assert.Null((await SettingsAsync(activated)).BotSuspendedReason);
    }

    // Ok, then the lock is lost before the fresh read: the successor's read shows editor and books no second audit.
    // Timeout + a read with editor: exactly one audit, with resolved.
    [Fact]
    public async Task AnOkResolvedByTheSuccessor_HasOneAudit_ATimeoutResolvedPositively_HasOneWithResolved()
    {
        await SeedAccountAsync(BotA);
        var okChannel = await SeedRequestAsync();
        Editors(BotA, (okChannel.TwitchId, "PENDING", true));
        var session = await SessionAsync();
        session.OnProbe = probe => Task.FromResult(probe != 5);

        await RunPassAsync(session: session);
        Assert.Equal(Now, (await SettingsAsync(okChannel)).BotInvitationAttemptAtUtc);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();
        Assert.True((await SettingsAsync(okChannel)).BotModeEnabled);
        Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activation" }, Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Details);

        var timeoutChannel = await SeedRequestAsync();
        Editors(BotA, (okChannel.TwitchId, "ACCEPTED", true), (timeoutChannel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Timeout());
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.True((await SettingsAsync(timeoutChannel)).BotModeEnabled);
        var audits = await AuditsAsync(AuditActions.WishBotAcceptInvitation);
        Assert.Equal(2, audits.Count);
        Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activation", ["resolved"] = "True" }, audits[^1].Details);
    }

    // K6d-2: ACCEPTED without "manage" after a DELETE resolves as after abort; without the DELETE the attempt is cleared
    // with one audit (resolved), nothing is confirmed, and the request ends with not_editor after the window; no write.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptedWithoutManage_ResolvesTheAttempt_ButNeverConfirms(bool delete)
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync(requestedAt: Now);
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Timeout());
        _acceptTakesEffect = false;

        await RunPassAsync();
        if (delete)
        {
            await DeleteBotAsync(channel);
        }

        TakeEffect(BotA, OwnerOf(channel), manage: false);
        _clock.Advance(TimeSpan.FromSeconds(340));
        await RunPassAsync();

        var settings = await SettingsAsync(channel);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.False(settings.BotModeEnabled);
        Assert.Single(_acceptCalls);
        var audit = Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation));
        if (delete)
        {
            Assert.Equal(WishBotChannelReasons.InvitationAcceptedAfterAbort, settings.BotActivationError);
            Assert.Equal(new Dictionary<string, string> { ["resolved"] = "True", ["afterAbort"] = "True" }, audit.Details);
        }
        else
        {
            Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activation", ["resolved"] = "True" }, audit.Details);
            Assert.Equal(WishBotChannelReasons.NotEditor, settings.BotActivationError);
            Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));
        }
    }

    // ------------------------------------------------------------------------------------ S6-2: the consent's bot id

    // Request bound to A, a hand PUT sets B: whatever the read for B shows (invited or editor), no acceptance, no
    // confirmation, the request ends with bot_account_changed.
    [Theory]
    [InlineData("PENDING")]
    [InlineData("ACCEPTED")]
    public async Task ARequestForA_WithTheTokenOfB_EndsBotAccountChanged_WithoutAcceptance(string state)
    {
        await SeedAccountAsync(BotB, identity: Identity + 1);
        var channel = await SeedRequestAsync();
        Editors(BotB, (channel.TwitchId, state, true));

        await RunPassAsync();

        Assert.Empty(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.Equal((false, WishBotChannelReasons.BotAccountChanged), (settings.BotModeEnabled, settings.BotActivationError));
        Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));
    }

    // An activated channel bound to A, B invited: suspended bot_account_changed, no acceptance; DELETE and a new
    // activation with B bind B, and B accepts.
    [Fact]
    public async Task AnActivationForA_WithB_IsBotAccountChanged_ANewActivationBindsB_AndBAccepts()
    {
        await SeedAccountAsync(BotB, identity: Identity + 1);
        var channel = await SeedChannelAsync();
        Editors(BotB, (channel.TwitchId, "PENDING", true));

        await RunPassAsync();
        Assert.Empty(_acceptCalls);
        Assert.Equal(WishBotChannelReasons.BotAccountChanged, (await SettingsAsync(channel)).BotSuspendedReason);

        await DeleteBotAsync(channel);
        await ActivateAsync(channel);
        Assert.Equal(BotB, (await SettingsAsync(channel)).BotConsentSevenTvUserId);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.Equal(BotB, Assert.Single(_acceptCalls).BotId);
        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
    }

    // A hand PUT with the same account (a new identity, same bot id) keeps the consent: accepted and confirmed with the new
    // identity's proof, once the new token is verified (the session here is that of a valid token).
    [Fact]
    public async Task AHandPutWithTheSameAccount_AcceptsAndConfirmsAsUsual()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        await SeedAccountAsync(BotA, identity: Identity + 1);
        Editors(BotA, (channel.TwitchId, "PENDING", true));

        await RunPassAsync();

        Assert.Equal(BotA, Assert.Single(_acceptCalls).BotId);
        var settings = await SettingsAsync(channel);
        Assert.Equal((true, Identity + 1), (settings.BotModeEnabled, settings.BotEditorCheckedIdentityVersion));
    }

    // R2-5: the positive read for A is held, a hand PUT sets B (unverified) meanwhile: the confirmation is discarded, the
    // request stays, no audit. Once B is valid the request (bound to A) ends with bot_account_changed: never bot for B.
    [Fact]
    public async Task ARotationDuringTheActivation_DiscardsTheConfirmation_AndBNeverInherits()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        HoldNextRead();

        var pass = RunPassAsync();
        await _heldReadEntered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u
                .SetProperty(c => c.IdentityVersion, Identity + 1)
                .SetProperty(c => c.CredentialVersion, c => c.CredentialVersion + 1)
                .SetProperty(c => c.Status, BotAccountStatus.Unverified)
                .SetProperty(c => c.BotSevenTvUserId, (string?)null));
        }

        ReleaseHeldRead();
        await pass;

        var settings = await SettingsAsync(channel);
        Assert.Equal((false, Now.AddMinutes(-1)), (settings.BotModeEnabled, settings.BotActivationRequestedAtUtc));
        Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));

        await SeedAccountAsync(BotB, identity: Identity + 1);
        Editors(BotB, (channel.TwitchId, "ACCEPTED", true));
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        settings = await SettingsAsync(channel);
        Assert.Equal((false, WishBotChannelReasons.BotAccountChanged), (settings.BotModeEnabled, settings.BotActivationError));
        Assert.Empty(_acceptCalls);
    }

    // Plan 2.9 "Bestätigung im Worker" (Sol-P2-6): the read for a request is held; meanwhile a manager cancels it and asks
    // anew for the same bot account. The held read decided on the old request: neither the confirmation (editor) nor the
    // acceptance (invited) is booked for the new one, which waits for a decision of its own.
    [Theory]
    [InlineData("ACCEPTED")]
    [InlineData("PENDING")]
    public async Task ARequestMadeAnewWhileItsReadRuns_IsNotDecidedByThatRead(string state)
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, state, true));
        HoldNextRead();

        var pass = RunPassAsync();
        await _heldReadEntered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await DeleteBotAsync(channel);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ActivateAsync(channel);
        var renewed = Now;
        ReleaseHeldRead();
        await pass;

        var settings = await SettingsAsync(channel);
        Assert.Equal(
            (false, renewed, (DateTime?)null),
            (settings.BotModeEnabled, settings.BotActivationRequestedAtUtc!.Value, settings.BotInvitationAttemptAtUtc));
        Assert.Empty(_acceptCalls);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));
    }

    // The same through the forced read (S6-3): a cached negative read began a second before the window's end, so the pass
    // reads again; that read is held, and meanwhile a manager cancels the request and asks anew. The fresh read (an
    // editor now) was made for the old request: the new one is neither confirmed nor accepted in this pass, nor in the
    // next one within the read's cached minute, which reads 7TV again (no entry by then) instead of reusing it.
    [Theory]
    [InlineData("ACCEPTED")]
    [InlineData("PENDING")]
    public async Task ARequestMadeAnewWhileTheForcedReadRuns_IsNotDecidedByThatRead(string state)
    {
        _visibilitySeconds = 60;
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync(requestedAt: Now);
        Editors(BotA);
        _clock.Advance(TimeSpan.FromSeconds(59));
        await RunPassAsync();
        Assert.NotNull((await SettingsAsync(channel)).BotActivationRequestedAtUtc);

        _clock.Advance(TimeSpan.FromSeconds(2));
        HoldNextRead();
        var pass = RunPassAsync();
        await _heldReadEntered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await DeleteBotAsync(channel);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await ActivateAsync(channel);
        var renewed = Now;
        Editors(BotA, (channel.TwitchId, state, true));
        ReleaseHeldRead();
        await pass;

        var settings = await SettingsAsync(channel);
        Assert.Equal(
            (false, (DateTime?)renewed, (DateTime?)null),
            (settings.BotModeEnabled, settings.BotActivationRequestedAtUtc, settings.BotInvitationAttemptAtUtc));
        Assert.Empty(_acceptCalls);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));

        Editors(BotA);
        _sevenTv.ClearReceivedCalls();
        _clock.Advance(TimeSpan.FromSeconds(10));
        await RunPassAsync();

        await _sevenTv.Received(1).GetEditorForAsync(BotA, Arg.Any<CancellationToken>());
        settings = await SettingsAsync(channel);
        Assert.Equal(
            (false, (DateTime?)renewed, (DateTime?)null),
            (settings.BotModeEnabled, settings.BotActivationRequestedAtUtc, settings.BotInvitationAttemptAtUtc));
        Assert.Empty(_acceptCalls);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));
    }

    // ------------------------------------------------------------------------------------ S6-3: the visibility window

    // The invitation becomes visible a few seconds before the end of the window; a negative read begins a second before
    // it and is cached for a minute: it decides nothing, the next pass reads anew and accepts. A held read that began
    // before the end and answers after it decides nothing either: a fresh read does (here: not_editor).
    [Theory]
    [InlineData(60)]
    [InlineData(1800)]
    public async Task AReadThatBeganBeforeTheWindowsEnd_NeverDecidesANegative(int windowSeconds)
    {
        _visibilitySeconds = windowSeconds;
        await SeedAccountAsync(BotA);
        var requestedAt = Now;
        var early = await SeedRequestAsync(requestedAt: requestedAt);
        var late = await SeedRequestAsync(requestedAt: requestedAt);
        Editors(BotA);

        _clock.Advance(TimeSpan.FromSeconds(windowSeconds - 1));
        await RunPassAsync();
        Assert.NotNull((await SettingsAsync(early)).BotActivationRequestedAtUtc);

        Editors(BotA, (early.TwitchId, "PENDING", true));
        _clock.Advance(TimeSpan.FromSeconds(59));
        await RunPassAsync();

        Assert.Equal(OwnerOf(early), Assert.Single(_acceptCalls).OwnerId);
        Assert.True((await SettingsAsync(early)).BotModeEnabled);
        Assert.Equal(WishBotChannelReasons.NotEditor, (await SettingsAsync(late)).BotActivationError);

        // The held case on a fresh request: the read begins a second before the end, answers after it.
        _clock.Advance(TimeSpan.FromSeconds(61));
        var held = await SeedRequestAsync(requestedAt: Now - TimeSpan.FromSeconds(windowSeconds - 1));
        var before = _sevenTv.ReceivedCalls().Count();
        HoldNextRead();
        var pass = RunPassAsync();
        await _heldReadEntered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        _clock.Advance(TimeSpan.FromSeconds(2));
        ReleaseHeldRead();
        await pass;

        Assert.Equal(2, _sevenTv.ReceivedCalls().Count() - before);
        Assert.Equal(WishBotChannelReasons.NotEditor, (await SettingsAsync(held)).BotActivationError);
    }

    // ------------------------------------------------------------------------------------ the credential between preflight and send

    // Revision 3: a renewal swap between the preflight and the send: not sent, the attempt taken back, the pass ends (no
    // channel suspended, no fallback); the next pass sends with the new token.
    [Fact]
    public async Task ASwapBetweenPreflightAndSend_SendsNothing_EndsThePass_AndTheNextPassSends()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        var other = await SeedChannelAsync(configure: s => s.BotConsentSevenTvUserId = BotB);
        var unconfirmed = await SeedWishAsync(other, WishStatus.Unconfirmed, w => w.BotWriteAtUtc = Now.AddMinutes(-3));
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        var session = await SessionAsync();
        session.OnProbe = async probe =>
        {
            if (probe == 4)
            {
                await using var db = CreateDb();
                await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.CredentialVersion, c => c.CredentialVersion + 1));
            }

            return true;
        };

        await RunPassAsync(session: session);

        Assert.Empty(_acceptCalls);
        Assert.Null((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        Assert.Null((await SettingsAsync(other)).BotSuspendedReason);
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(unconfirmed)).Status);

        await RunPassAsync();

        Assert.Single(_acceptCalls);
        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
    }

    // ------------------------------------------------------------------------------------ helpers (invitations)

    // An activation request of the activator (not activated), bound to the given bot id, a minute old unless given.
    private Task<Seeded> SeedRequestAsync(DateTime? requestedAt = null, string consent = BotA, Action<ChannelWishSettings>? configure = null)
    {
        var at = requestedAt ?? Now.AddMinutes(-1);
        return SeedChannelAsync(botMode: false, configure: s =>
        {
            s.BotActivationRequestedAtUtc = at;
            s.BotActivationRequestedByTwitchUserId = Activator;
            s.BotActivationRequestedByLogin = "requester";
            s.BotConsentSevenTvUserId = consent;
            configure?.Invoke(s);
        });
    }

    private static string OwnerOf(Seeded channel) => $"01OWNER{channel.TwitchId}";

    private async Task<SevenTvWriteResult> AnswerAcceptAsync(string botId, string ownerId)
    {
        _acceptCalls.Add(new AcceptCall(botId, ownerId, Now, _lastSession?.Calls.LastOrDefault()));
        if (_whileAccepting is { } hook)
        {
            await hook();
        }

        if (_acceptTakesEffect)
        {
            TakeEffect(botId, ownerId);
        }

        return _acceptResults.Count > 0 ? _acceptResults.Dequeue() : SevenTvWriteResult.Ok();
    }

    // The acceptance takes effect at "7TV": the editor read for that bot id shows ACCEPTED for the owner's channel.
    private void TakeEffect(string botId, string ownerId, bool manage = true)
    {
        if (!_editors.TryGetValue(botId, out var entries))
        {
            _editors[botId] = entries = new Dictionary<string, (string State, bool Manage)>(StringComparer.Ordinal);
        }

        entries[ownerId["01OWNER".Length..]] = ("ACCEPTED", manage);
    }

    private WishBotService BotService(AppDbContext db)
    {
        var wishOptions = new WishOptions { Enabled = true, PilotChannelIds = "*" };
        return new WishBotService(
            db,
            new WishBotAvailability(wishOptions, new BotAccountAvailability(db, new BotAccountOptions { Enabled = true }, _clock)),
            wishOptions,
            Substitute.For<ISevenTvEditorService>(),
            Substitute.For<IModRoleCache>(),
            new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
            Substitute.For<IBotAccountNudge>(),
            _clock);
    }

    // DELETE …/wishes/bot through the real service, as a global admin.
    private async Task DeleteBotAsync(Seeded channel, AppDbContext? db = null)
    {
        var context = db ?? CreateDb();
        try
        {
            Assert.Equal(WishOutcome.Success, (await BotService(context).DeactivateAsync(channel.Name, new AuditActor("900001", "admin"), isGlobalAdmin: true)).Outcome);
        }
        finally
        {
            if (db is null)
            {
                await context.DisposeAsync();
            }
        }
    }

    // POST …/wishes/bot/activate through the real service.
    private async Task ActivateAsync(Seeded channel)
    {
        await using var db = CreateDb();
        Assert.Equal(WishOutcome.Success, (await BotService(db).ActivateAsync(channel.Name, new AuditActor("900001", "admin"))).Outcome);
    }

    private async Task<List<(string? Channel, string Actor, Dictionary<string, string> Details)>> AuditsAsync(string action)
    {
        await using var db = CreateDb();
        var entries = await db.AuditLogEntries.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync();
        return entries.Select(e =>
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            if (e.DetailsJson is { } json)
            {
                using var document = JsonDocument.Parse(json);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    details[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.ToString();
                }
            }

            return (e.ChannelName, e.ActorTwitchUserId, details);
        }).ToList();
    }

    private sealed record AcceptCall(string BotId, string OwnerId, DateTime At, string? LastSessionCall);
}
