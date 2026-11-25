using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The channel part of the wish bot (#487 plan 2.9, T7) against real Postgres: the read, the activation filter's steps (3)
// to (5), activate (request, consent bound to the current bot id, abandoning an attempt for an earlier account), DELETE
// (D7 behind the lock, ownership ends, open attempts resolve without 7TV, an open acceptance stays unresolved), the
// /permissions flag, and the waits on a held channel row (a send transaction of the acceptance) and a held wish row
// (the runner). The bot account's state is a substitute; the real row is read where a case says so.
public partial class WishBotServiceTests
{
    private const string BotA = "01BOTACCOUNTA0000000000000";
    private const string BotB = "01BOTACCOUNTB0000000000000";
    private const string Activator = "7777";
    private const string Moderator = "4242";
    private const string ActiveSet = "01ACTIVESET000000000000000";
    private const string Evidence = """{"setId":"01ACTIVESET000000000000000","target":{}}""";

    private static readonly AuditActor ModActor = new(Moderator, "somemod");
    private static readonly BotAccountAvailabilityState AccountA = new(true, BotA, 3);

    private readonly HandWoundTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly IBotAccountNudge _nudge = Substitute.For<IBotAccountNudge>();
    private readonly ISevenTvEditorService _editors = Substitute.For<ISevenTvEditorService>();
    private readonly IModRoleCache _roleCache = Substitute.For<IModRoleCache>();

    // ------------------------------------------------------------------------------------ GET …/wishes/bot

    [Fact]
    public async Task Get_AnUnknownChannel_IsNull()
    {
        Assert.Null(await Sut().GetChannelAsync("nosuchchannel", Caller(Moderator)));
    }

    // Revision 6: the field values pass through unchanged (no codes); the bot id is the account's current one.
    [Fact]
    public async Task Get_PassesTheStoredStateOn()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotModeEnabled = true;
            s.BotModeActivatedByTwitchUserId = Activator;
            s.BotModeActivatedByLogin = "activator";
            s.BotModeActivatedAtUtc = Now.AddDays(-2);
            s.BotSuspendedReason = WishBotChannelReasons.InvitationPending;
            s.BotEditorCheckedAtUtc = Now.AddMinutes(-3);
        });

        var dto = (await Sut().GetChannelAsync(channel.Name.ToUpperInvariant(), Caller(Activator)))!;

        Assert.Equal(
            new WishBotChannelDto(true, true, true, false, null, null, true, true, WishBotChannelReasons.InvitationPending,
                Now.AddMinutes(-3), "activator", Now.AddDays(-2), BotA),
            dto with { CanActivate = true });
        Assert.True(dto.CanDeactivate);
    }

    [Fact]
    public async Task Get_APendingRequest_IsPending_AndAnActivationErrorPassesThrough()
    {
        var pending = await SeedBotChannelAsync(s => s.BotActivationRequestedAtUtc = Now.AddMinutes(-1));
        var failed = await SeedBotChannelAsync(s => s.BotActivationError = WishBotChannelReasons.InvitationNotAccepted);
        var without = await SeedChannelRowAsync();

        var a = (await Sut().GetChannelAsync(pending.Name, Caller(Moderator)))!;
        var b = (await Sut().GetChannelAsync(failed.Name, Caller(Moderator)))!;
        var c = (await Sut(BotAccountAvailabilityState.Unavailable, pilot: "").GetChannelAsync(without.Name, Caller(Moderator)))!;

        Assert.Equal((true, Now.AddMinutes(-1)), (a.ActivationPending, a.ActivationRequestedAtUtc));
        Assert.Equal((false, WishBotChannelReasons.InvitationNotAccepted), (b.ActivationPending, b.ActivationError));
        Assert.Equal((false, false, false, false, (string?)null), (c.Available, c.Listed, c.Enabled, c.ActivationPending, c.BotSevenTvUserId));
    }

    // canActivate mirrors the activation filter without a 7TV call: null while a moderator's grants are not cached.
    [Theory]
    [InlineData("admin by id, unlisted", true)]
    [InlineData("broadcaster, unlisted", false)]
    [InlineData("broadcaster", true)]
    [InlineData("moderator, cached grant", true)]
    [InlineData("moderator, cached without grant", false)]
    [InlineData("moderator, nothing cached", null)]
    public async Task Get_CanActivate_MirrorsTheFilterWithout7Tv(string who, bool? expected)
    {
        var channel = await SeedChannelRowAsync();
        var unlisted = who.EndsWith("unlisted", StringComparison.Ordinal);
        var caller = who switch
        {
            "admin by id, unlisted" => new WishBotCaller(Moderator, true, true),
            _ when who.StartsWith("broadcaster", StringComparison.Ordinal) => Caller(channel.TwitchId),
            _ => Caller(Moderator),
        };
        if (who == "moderator, cached grant")
        {
            CacheGrants(channel.TwitchId);
        }
        else if (who == "moderator, cached without grant")
        {
            CacheGrants("999999");
        }

        var dto = (await Sut(pilot: unlisted ? "" : "*").GetChannelAsync(channel.Name, caller))!;

        Assert.Equal(expected, dto.CanActivate);
        await _editors.DidNotReceiveWithAnyArgs().GetEditorGrantsAsync(default!, default);
    }

    // D7: in bot mode only broadcaster, activator and a global admin; in the manual flow every manager.
    [Fact]
    public async Task Get_CanDeactivate_IsD7()
    {
        var manual = await SeedChannelRowAsync();
        var bot = await SeedBotChannelAsync(s =>
        {
            s.BotModeEnabled = true;
            s.BotModeActivatedByTwitchUserId = Activator;
        });

        Assert.True((await Sut().GetChannelAsync(manual.Name, Caller(Moderator)))!.CanDeactivate);
        Assert.False((await Sut().GetChannelAsync(bot.Name, Caller(Moderator)))!.CanDeactivate);
        Assert.True((await Sut().GetChannelAsync(bot.Name, Caller(Activator)))!.CanDeactivate);
        Assert.True((await Sut().GetChannelAsync(bot.Name, Caller(bot.TwitchId)))!.CanDeactivate);
        Assert.True((await Sut().GetChannelAsync(bot.Name, new WishBotCaller(Moderator, true, false)))!.CanDeactivate);
    }

    // ------------------------------------------------------------------------------------ activation filter, steps (3) to (5)

    [Fact]
    public async Task Access_AnUnknownChannel_IsAllowed_SoTheHandlerAnswersItsNotFound()
    {
        Assert.Equal(WishBotActivationAccess.Allowed, (await Sut().CheckActivationAccessAsync("nosuchchannel", Moderator)).Access);
    }

    // AK85: an empty list refuses broadcaster and moderator before any 7TV call.
    [Theory]
    [InlineData("")]
    [InlineData("123")]
    public async Task Access_AnUnlistedChannel_IsNotListed_Before7Tv(string pilot)
    {
        var channel = await SeedChannelRowAsync();

        Assert.Equal(WishBotActivationAccess.NotListed, (await Sut(pilot: pilot).CheckActivationAccessAsync(channel.Name, channel.TwitchId)).Access);
        Assert.Equal(WishBotActivationAccess.NotListed, (await Sut(pilot: pilot).CheckActivationAccessAsync(channel.Name, Moderator)).Access);
        await _editors.DidNotReceiveWithAnyArgs().GetEditorGrantsAsync(default!, default);
    }

    [Fact]
    public async Task Access_TheBroadcasterById_IsAllowed_Without7Tv()
    {
        var channel = await SeedChannelRowAsync();

        Assert.Equal(WishBotActivationAccess.Allowed, (await Sut(pilot: channel.TwitchId).CheckActivationAccessAsync(channel.Name, channel.TwitchId)).Access);
        await _editors.DidNotReceiveWithAnyArgs().GetEditorGrantsAsync(default!, default);
    }

    // AK82: only a confirmed editor grant for exactly this channel's Twitch id lets a moderator through; every
    // uncertainty refuses, 7TV unavailable is its own answer (503), never a pass.
    [Theory]
    [InlineData("grant for the channel", WishBotActivationAccess.Allowed)]
    [InlineData("grant for another channel", WishBotActivationAccess.Forbidden)]
    [InlineData("no 7TV account", WishBotActivationAccess.Forbidden)]
    [InlineData("7TV unavailable", WishBotActivationAccess.EditorUnavailable)]
    public async Task Access_AModerator_NeedsAConfirmedEditorGrant(string grant, WishBotActivationAccess expected)
    {
        var channel = await SeedChannelRowAsync();
        _editors.GetEditorGrantsAsync(Moderator, Arg.Any<CancellationToken>()).Returns(grant switch
        {
            "grant for the channel" => Grants(channel.TwitchId),
            "grant for another channel" => Grants("999999"),
            "no 7TV account" => SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.NoSevenTvAccount),
            _ => SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Unavailable),
        });

        Assert.Equal(expected, (await Sut().CheckActivationAccessAsync(channel.Name, Moderator)).Access);
    }

    [Fact]
    public async Task Access_AChannelWithoutTwitchId_IsForbidden_EvenWithTheWholeList()
    {
        var channel = await SeedChannelRowAsync(twitchId: null);

        Assert.Equal(WishBotActivationAccess.Forbidden, (await Sut().CheckActivationAccessAsync(channel.Name, Moderator)).Access);
        await _editors.DidNotReceiveWithAnyArgs().GetEditorGrantsAsync(default!, default);
    }

    // ------------------------------------------------------------------------------------ activate

    [Fact]
    public async Task Activate_AnUnavailableAccount_IsBotUnavailable_AndWritesNothing()
    {
        var channel = await SeedChannelRowAsync();

        var result = await Sut(BotAccountAvailabilityState.Unavailable).ActivateAsync(channel.Name, ModActor);

        Assert.Equal(WishOutcome.BotUnavailable, result.Outcome);
        Assert.Null(await SettingsAsync(channel));
        AssertNoSignal();
    }

    // AK82, E24: the stored activator is never overwritten.
    [Fact]
    public async Task Activate_AnActiveBotMode_IsAlreadyActive_AndLeavesTheActivator()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotModeEnabled = true;
            s.BotModeActivatedByTwitchUserId = Activator;
            s.BotModeActivatedByLogin = "activator";
            s.BotConsentSevenTvUserId = BotA;
        });
        var before = await SettingsSnapshotAsync(channel);

        var result = await Sut().ActivateAsync(channel.Name, ModActor);

        Assert.Equal(WishOutcome.BotAlreadyActive, result.Outcome);
        Assert.Equal(before, await SettingsSnapshotAsync(channel));
        AssertNoSignal();
    }

    // The deleted activator (pseudonym) in an unlisted channel: the channel falls back, an activate still answers 409,
    // and the activation filter refuses everyone but an admin by id.
    [Fact]
    public async Task Activate_ADeletedActivatorInAnUnlistedChannel_IsAlreadyActive_AndTheFilterSaysNotListed()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotModeEnabled = true;
            s.BotModeActivatedByTwitchUserId = AuditActor.DeletedUser.TwitchUserId;
            s.BotModeActivatedByLogin = AuditActor.DeletedUser.Login;
            s.BotConsentSevenTvUserId = BotA;
        });

        Assert.Equal(WishOutcome.BotAlreadyActive, (await Sut(pilot: "").ActivateAsync(channel.Name, ModActor)).Outcome);
        Assert.Equal(WishBotActivationAccess.NotListed, (await Sut(pilot: "").CheckActivationAccessAsync(channel.Name, channel.TwitchId)).Access);
        Assert.Equal(AuditActor.DeletedUser.TwitchUserId, (await SettingsAsync(channel))!.BotModeActivatedByTwitchUserId);
    }

    [Fact]
    public async Task Activate_RecordsTheRequest_BindsTheConsentToTheCurrentBot_AndNudges()
    {
        var channel = await SeedBotChannelAsync(s => s.BotActivationError = WishBotChannelReasons.NotEditor);

        var result = await Sut().ActivateAsync(channel.Name, ModActor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = (await SettingsAsync(channel))!;
        Assert.False(row.BotModeEnabled);
        Assert.Equal((Now, Moderator, "somemod", BotA), (row.BotActivationRequestedAtUtc, row.BotActivationRequestedByTwitchUserId, row.BotActivationRequestedByLogin, row.BotConsentSevenTvUserId));
        Assert.Null(row.BotActivationError);
        Assert.Empty(await AuditAsync(channel));
        AssertSignalledOnce(channel);
    }

    // E24 bound to the checked channel: the filter checked the caller for one Twitch id (or found no channel), and a rename
    // and a rejoin moved the name to another channel before the write. Behind the channel lock the write sees another id:
    // channel_not_found, nothing written, no signal. Bound to the id it checked, the write goes through.
    [Fact]
    public async Task Activate_BoundToTheCheckedChannel_RefusesAnotherChannelUnderTheName()
    {
        var channel = await SeedBotChannelAsync();
        var before = await SettingsSnapshotAsync(channel);

        Assert.Equal(WishOutcome.ChannelNotFound, (await Sut().ActivateAsync(channel.Name, ModActor, "1000000001")).Outcome);
        Assert.Equal(WishOutcome.ChannelNotFound, (await Sut().ActivateAsync(channel.Name, ModActor, string.Empty)).Outcome);
        Assert.Equal(before, await SettingsSnapshotAsync(channel));
        AssertNoSignal();

        Assert.True((await Sut().ActivateAsync(channel.Name, ModActor, channel.TwitchId)).IsSuccess);
        AssertSignalledOnce(channel);
    }

    // Plan 2.9: the filter found no channel under the name (the empty string), and a join created one without a Twitch id
    // before the write. That row was never checked, and a non-admin never activates a row without a Twitch id: still
    // channel_not_found, nothing written, no signal.
    [Fact]
    public async Task Activate_AfterAFilterThatFoundNoChannel_RefusesARowWithoutATwitchId()
    {
        var channel = await SeedChannelRowAsync(twitchId: null);

        Assert.Equal(WishOutcome.ChannelNotFound, (await Sut().ActivateAsync(channel.Name, ModActor, string.Empty)).Outcome);
        Assert.Null(await SettingsAsync(channel));
        AssertNoSignal();
    }

    [Fact]
    public async Task Activate_WithoutASettingsRow_CreatesOneWithTheDefaults()
    {
        var channel = await SeedChannelRowAsync();

        Assert.True((await Sut().ActivateAsync(channel.Name, ModActor)).IsSuccess);

        var row = (await SettingsAsync(channel))!;
        Assert.Equal((WishLimits.DefaultDurationDays, BotA), (row.DefaultDurationDays, row.BotConsentSevenTvUserId));
    }

    // [E] a lying request younger than ten minutes keeps its requester and is only nudged; an older one, or one bound to
    // another account (Revision 6a), is replaced.
    [Theory]
    [InlineData(9, BotA, false)]
    [InlineData(11, BotA, true)]
    [InlineData(1, BotB, true)]
    public async Task Activate_ALyingRequest_IsKeptOrReplaced(int minutesAgo, string boundTo, bool replaced)
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotActivationRequestedAtUtc = Now.AddMinutes(-minutesAgo);
            s.BotActivationRequestedByTwitchUserId = Activator;
            s.BotActivationRequestedByLogin = "activator";
            s.BotConsentSevenTvUserId = boundTo;
        });

        Assert.True((await Sut().ActivateAsync(channel.Name, ModActor)).IsSuccess);

        var row = (await SettingsAsync(channel))!;
        Assert.Equal(
            replaced ? (Now, Moderator, BotA) : (Now.AddMinutes(-minutesAgo), Activator, BotA),
            (row.BotActivationRequestedAtUtc!.Value, row.BotActivationRequestedByTwitchUserId!, row.BotConsentSevenTvUserId!));
        AssertSignalledOnce(channel);
    }

    // Revision 6d (K6d-4), 6e (K6e-2): an open acceptance bound to A, DELETE, a hand PUT to B, activate: the attempt is
    // given up (with the rejected mark and the error), the request binds B, the audit names A.
    [Fact]
    public async Task Activate_AnOpenAttemptForAnEarlierAccount_IsGivenUp_AndTheAuditNamesIt()
    {
        var attemptedAt = Now.AddMinutes(-20);
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotModeEnabled = true;
            s.BotModeActivatedByTwitchUserId = Activator;
            s.BotConsentSevenTvUserId = BotA;
            s.BotInvitationAttemptAtUtc = attemptedAt;
            s.BotInvitationRetryAtUtc = Now.AddMinutes(-5);
        });
        Assert.True((await Sut().DeactivateAsync(channel.Name, ModActor, isGlobalAdmin: true)).IsSuccess);
        var afterDelete = (await SettingsAsync(channel))!;
        Assert.Equal(WishBotChannelReasons.InvitationAcceptUnresolved, afterDelete.BotActivationError);
        _publisher.ClearReceivedCalls();

        var result = await Sut(AccountA with { BotSevenTvUserId = BotB }).ActivateAsync(channel.Name, ModActor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = (await SettingsAsync(channel))!;
        Assert.Null(row.BotInvitationAttemptAtUtc);
        Assert.Null(row.BotInvitationRetryAtUtc);
        Assert.Null(row.BotActivationError);
        Assert.Equal((BotB, Moderator, Now), (row.BotConsentSevenTvUserId, row.BotActivationRequestedByTwitchUserId, row.BotActivationRequestedAtUtc));
        var abandoned = Assert.Single(await AuditAsync(channel), a => a.Action == AuditActions.WishBotInvitationAbandoned);
        Assert.Equal((Moderator, "somemod"), (abandoned.ActorTwitchUserId, abandoned.ActorLogin));
        using var details = JsonDocument.Parse(abandoned.DetailsJson!);
        Assert.Equal(
            ["abandonedBotSevenTvUserId", "attemptedAtUtc"],
            details.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(BotA, details.RootElement.GetProperty("abandonedBotSevenTvUserId").GetString());
        Assert.Equal(attemptedAt, details.RootElement.GetProperty("attemptedAtUtc").GetDateTime());
        AssertSignalledOnce(channel);
    }

    // The same with account A again: the request is set, the binding stays A, the attempt stays open, no audit.
    [Fact]
    public async Task Activate_AnOpenAttemptForTheSameAccount_KeepsTheAttempt_WithoutAudit()
    {
        var attemptedAt = Now.AddMinutes(-20);
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotConsentSevenTvUserId = BotA;
            s.BotInvitationAttemptAtUtc = attemptedAt;
            s.BotInvitationRetryAtUtc = Now.AddMinutes(-5);
            s.BotActivationError = WishBotChannelReasons.InvitationAcceptUnresolved;
        });

        Assert.True((await Sut().ActivateAsync(channel.Name, ModActor)).IsSuccess);

        var row = (await SettingsAsync(channel))!;
        Assert.Equal((attemptedAt, Now.AddMinutes(-5), BotA), (row.BotInvitationAttemptAtUtc!.Value, row.BotInvitationRetryAtUtc!.Value, row.BotConsentSevenTvUserId!));
        Assert.Equal(Now, row.BotActivationRequestedAtUtc);
        Assert.Null(row.BotActivationError);
        Assert.Empty(await AuditAsync(channel));
    }

    // K6d-4 behind a send transaction of the acceptance: activate waits for the channel row and gives up afterwards.
    [Fact]
    public async Task Activate_WaitsForAHeldSendTransaction_AndGivesUpAfterwards()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotConsentSevenTvUserId = BotA;
            s.BotInvitationAttemptAtUtc = Now.AddMinutes(-1);
        });
        await using var holder = fixture.CreateTaggedDbContext("bot-send-holder", DatabaseName);
        await using var holding = await holder.Database.BeginTransactionAsync();
        await HoldChannelForShareAsync(holder, channel);

        const string contender = "bot-activate-contender";
        await using var contenderDb = fixture.CreateTaggedDbContext(contender, DatabaseName);
        var activate = Sut(AccountA with { BotSevenTvUserId = BotB }, db: contenderDb).ActivateAsync(channel.Name, ModActor);
        await fixture.WaitUntilBlockedOnLockAsync(contender, activate);
        await holding.CommitAsync();

        Assert.True((await activate).IsSuccess);
        var row = (await SettingsAsync(channel))!;
        Assert.Null(row.BotInvitationAttemptAtUtc);
        Assert.Equal(BotB, row.BotConsentSevenTvUserId);
        Assert.Single(await AuditAsync(channel), a => a.Action == AuditActions.WishBotInvitationAbandoned);
    }

    // ------------------------------------------------------------------------------------ DELETE …/wishes/bot

    [Fact]
    public async Task Deactivate_NeitherActivatedNorRequested_IsIdempotent_WithoutAuditOrEvent()
    {
        var without = await SeedChannelRowAsync();
        var manual = await SeedBotChannelAsync();
        var before = await SettingsSnapshotAsync(manual);

        Assert.True((await Sut().DeactivateAsync(without.Name, ModActor, isGlobalAdmin: false)).IsSuccess);
        Assert.True((await Sut().DeactivateAsync(manual.Name, ModActor, isGlobalAdmin: false)).IsSuccess);

        Assert.Null(await SettingsAsync(without));
        Assert.Equal(before, await SettingsSnapshotAsync(manual));
        Assert.Empty(await AuditAsync(manual));
        AssertNoSignal();
    }

    // [F5]: a pending request is cancelled by anyone D7 lets through, which in the manual flow is every manager.
    [Fact]
    public async Task Deactivate_APendingRequest_IsCancelledByAModerator_PendingOnly()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotActivationRequestedAtUtc = Now.AddMinutes(-1);
            s.BotActivationRequestedByTwitchUserId = Activator;
            s.BotActivationRequestedByLogin = "activator";
            s.BotConsentSevenTvUserId = BotA;
            s.BotInvitationRetryAtUtc = Now.AddMinutes(5);
        });

        Assert.True((await Sut().DeactivateAsync(channel.Name, ModActor, isGlobalAdmin: false)).IsSuccess);

        var row = (await SettingsAsync(channel))!;
        Assert.Null(row.BotActivationRequestedAtUtc);
        Assert.Null(row.BotActivationRequestedByTwitchUserId);
        Assert.Null(row.BotConsentSevenTvUserId);
        Assert.Null(row.BotInvitationRetryAtUtc);
        var audit = Assert.Single(await AuditAsync(channel));
        Assert.Equal(AuditActions.WishBotDeactivate, audit.Action);
        AssertDetails("""{"pendingOnly":true}""", audit);
        AssertPublishedOnce(channel);
        await _nudge.DidNotReceiveWithAnyArgs().NudgeAsync(default);
    }

    // Sol-P2-6, Api side (K6b-10): a moderator passed the filter in the manual flow, the worker confirmed the activation
    // in between (test SQL), the DELETE gets the lock: 403, the row unchanged.
    [Fact]
    public async Task Deactivate_AConfirmationBetweenFilterAndLock_RestrictsAModerator()
    {
        var channel = await SeedBotChannelAsync(s => s.BotActivationRequestedAtUtc = Now.AddMinutes(-1));
        await using (var worker = CreateDb())
        {
            await worker.ChannelWishSettings.Where(s => s.ChannelId == channel.Id).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.BotModeEnabled, true)
                .SetProperty(s => s.BotModeActivatedByTwitchUserId, Activator)
                .SetProperty(s => s.BotActivationRequestedAtUtc, (DateTime?)null));
        }

        var before = await SettingsSnapshotAsync(channel);

        var result = await Sut().DeactivateAsync(channel.Name, ModActor, isGlobalAdmin: false);

        Assert.Equal(WishOutcome.SettingsRestricted, result.Outcome);
        Assert.Equal(before, await SettingsSnapshotAsync(channel));
        Assert.Empty(await AuditAsync(channel));
        AssertNoSignal();
    }

    // AK83 "switching off", 2.9: every ownership ends, queues empty (also on presented), open attempts resolve without
    // 7TV, a removal attempt's evidence stays as a repair phase with its own set (K6c-2); a plain approved wish and other
    // channels stay as they are.
    [Fact]
    public async Task Deactivate_AnActivation_EndsEveryOwnership_AndResolvesOpenAttempts()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotModeEnabled = true;
            s.BotModeActivatedByTwitchUserId = Activator;
            s.BotModeActivatedByLogin = "activator";
            s.BotModeActivatedAtUtc = Now.AddDays(-3);
            s.BotSuspendedReason = WishBotChannelReasons.NotEditor;
            s.BotConsentSevenTvUserId = BotA;
            s.BotInvitationRetryAtUtc = Now.AddMinutes(3);
        });
        var other = await SeedBotChannelAsync(s => s.BotModeEnabled = true);
        var owned = await SeedBotWishAsync(channel, WishStatus.Active, w => Own(w, queued: true));
        var presentedOrder = await SeedBotWishAsync(channel, WishStatus.Presented, w =>
        {
            w.StatusReason = WishStatusReason.Protected;
            Own(w, queued: true);
        });
        var intent = await SeedBotWishAsync(channel, WishStatus.Approved, w =>
        {
            w.BotWriteAtUtc = Now.AddMinutes(-2);
            w.BotSetId = ActiveSet;
            w.BotAttemptVersion = 1;
        });
        var unconfirmed = await SeedBotWishAsync(channel, WishStatus.Unconfirmed, w =>
        {
            w.BotWriteAtUtc = Now.AddMinutes(-2);
            w.BotSetId = ActiveSet;
            w.BotAddAckAtUtc = Now.AddMinutes(-2);
            w.BotConfirmDeadlineAtUtc = Now.AddMinutes(14);
        });
        var removing = await SeedBotWishAsync(channel, WishStatus.Active, w =>
        {
            Own(w, queued: true);
            w.BotRemoveAtUtc = Now.AddMinutes(-1);
            w.BotAttemptVersion = 1;
            w.BotRemoveEvidence = Evidence;
        });
        var plain = await SeedBotWishAsync(channel, WishStatus.Approved);
        var elsewhere = await SeedBotWishAsync(other, WishStatus.Active, w => Own(w, queued: true));
        var plainBefore = await WishSnapshotAsync(plain);
        var elsewhereBefore = await WishSnapshotAsync(elsewhere);

        var result = await Sut().DeactivateAsync(channel.Name, new AuditActor(Activator, "activator"), isGlobalAdmin: false);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = (await SettingsAsync(channel))!;
        Assert.False(row.BotModeEnabled);
        Assert.Null(row.BotModeActivatedByTwitchUserId);
        Assert.Null(row.BotModeActivatedByLogin);
        Assert.Null(row.BotModeActivatedAtUtc);
        Assert.Null(row.BotSuspendedReason);
        Assert.Null(row.BotConsentSevenTvUserId);
        Assert.Null(row.BotInvitationRetryAtUtc);

        foreach (var id in new[] { owned, presentedOrder, intent, unconfirmed, removing })
        {
            var wish = await WishAsync(id);
            Assert.False(wish.BotOwned);
            Assert.Null(wish.BotOwnedAddedAtUtc);
            Assert.Null(wish.RemovalQueuedAtUtc);
            Assert.Null(wish.RemovalQueuedVersion);
            Assert.Null(wish.BotSetId);
            Assert.Equal(2, wish.DecisionVersion);
        }

        Assert.Equal(WishStatus.Active, (await WishAsync(owned)).Status);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Protected), StatusOf(await WishAsync(presentedOrder)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await WishAsync(intent)));
        var wasUnconfirmed = await WishAsync(unconfirmed);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(wasUnconfirmed));
        Assert.Equal(Now.AddMinutes(-2), wasUnconfirmed.BotAddAckAtUtc);
        var wasRemoving = await WishAsync(removing);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(wasRemoving));
        Assert.Null(wasRemoving.BotRemoveAtUtc);
        Assert.Equal(Now, wasRemoving.BotRepairAtUtc);
        using (var evidence = JsonDocument.Parse(wasRemoving.BotRemoveEvidence!))
        {
            Assert.Equal(ActiveSet, evidence.RootElement.GetProperty("setId").GetString());
        }

        Assert.Equal(plainBefore, await WishSnapshotAsync(plain));
        Assert.Equal(elsewhereBefore, await WishSnapshotAsync(elsewhere));
        var audit = Assert.Single(await AuditAsync(channel));
        Assert.Equal((AuditActions.WishBotDeactivate, Activator, (string?)null), (audit.Action, audit.ActorTwitchUserId, audit.DetailsJson));
        AssertPublishedOnce(channel);
    }

    // S6-1 (AK126, DELETE part): with an open acceptance attempt, attempt, binding and retry stamp stay, the error says
    // unresolved, and the audit says so too; the answer is the same success.
    [Fact]
    public async Task Deactivate_WithAnOpenAcceptanceAttempt_LeavesItUnresolved()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotActivationRequestedAtUtc = Now.AddMinutes(-2);
            s.BotActivationRequestedByTwitchUserId = Activator;
            s.BotConsentSevenTvUserId = BotA;
            s.BotInvitationAttemptAtUtc = Now.AddMinutes(-1);
            s.BotInvitationRetryAtUtc = Now.AddMinutes(14);
        });

        Assert.True((await Sut().DeactivateAsync(channel.Name, ModActor, isGlobalAdmin: false)).IsSuccess);

        var row = (await SettingsAsync(channel))!;
        Assert.Null(row.BotActivationRequestedAtUtc);
        Assert.Equal((Now.AddMinutes(-1), Now.AddMinutes(14), BotA), (row.BotInvitationAttemptAtUtc!.Value, row.BotInvitationRetryAtUtc!.Value, row.BotConsentSevenTvUserId!));
        Assert.Equal(WishBotChannelReasons.InvitationAcceptUnresolved, row.BotActivationError);
        var audit = Assert.Single(await AuditAsync(channel));
        AssertDetails("""{"pendingOnly":true,"invitationAcceptUnresolved":true}""", audit);
    }

    // S6-1, K6b-10 (Api side): a send transaction of the acceptance holds the channel FOR SHARE; DELETE waits for it and
    // then sees the attempt that transaction left open.
    [Fact]
    public async Task Deactivate_WaitsForAHeldSendTransaction_AndSeesTheOpenAttempt()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotActivationRequestedAtUtc = Now.AddMinutes(-2);
            s.BotConsentSevenTvUserId = BotA;
        });
        await using var holder = fixture.CreateTaggedDbContext("bot-send-holder-delete", DatabaseName);
        await using var holding = await holder.Database.BeginTransactionAsync();
        await HoldChannelForShareAsync(holder, channel);

        const string contender = "bot-delete-contender";
        await using var contenderDb = fixture.CreateTaggedDbContext(contender, DatabaseName);
        var deactivate = Sut(db: contenderDb).DeactivateAsync(channel.Name, ModActor, isGlobalAdmin: false);
        await fixture.WaitUntilBlockedOnLockAsync(contender, deactivate);
        await holder.ChannelWishSettings.Where(s => s.ChannelId == channel.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BotInvitationAttemptAtUtc, Now.AddSeconds(-5)));
        await holding.CommitAsync();

        Assert.True((await deactivate).IsSuccess);
        var row = (await SettingsAsync(channel))!;
        Assert.Equal((WishBotChannelReasons.InvitationAcceptUnresolved, BotA), (row.BotActivationError, row.BotConsentSevenTvUserId));
        Assert.Null(row.BotActivationRequestedAtUtc);
    }

    // DELETE while the runner holds a wish row: it waits, and the re-read behind the lock ends the ownership the runner
    // just booked.
    [Fact]
    public async Task Deactivate_WaitsForAWishRowTheRunnerHolds_AndEndsWhatItBooked()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotModeEnabled = true;
            s.BotModeActivatedByTwitchUserId = Activator;
        });
        var wishId = await SeedBotWishAsync(channel, WishStatus.Unconfirmed, w =>
        {
            w.BotWriteAtUtc = Now.AddMinutes(-1);
            w.BotSetId = ActiveSet;
            w.BotAddAckAtUtc = Now.AddMinutes(-1);
        });
        await using var runner = fixture.CreateTaggedDbContext("bot-runner-holder", DatabaseName);
        await using var runnerTransaction = await runner.Database.BeginTransactionAsync();
        await runner.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Wishes" WHERE "Id" = {wishId} FOR UPDATE""");

        const string contender = "bot-delete-wish-contender";
        await using var contenderDb = fixture.CreateTaggedDbContext(contender, DatabaseName);
        var deactivate = Sut(db: contenderDb).DeactivateAsync(channel.Name, ModActor, isGlobalAdmin: true);
        await fixture.WaitUntilBlockedOnLockAsync(contender, deactivate);
        // The runner books bot_confirm with ownership.
        await runner.Wishes.Where(w => w.Id == wishId).ExecuteUpdateAsync(u => u
            .SetProperty(w => w.Status, WishStatus.Active)
            .SetProperty(w => w.BotOwned, true)
            .SetProperty(w => w.BotOwnedAddedAtUtc, Now.AddMinutes(-1))
            .SetProperty(w => w.DecisionVersion, 2));
        await runnerTransaction.CommitAsync();

        Assert.True((await deactivate).IsSuccess);
        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, false, (string?)null, 3), (wish.Status, wish.BotOwned, wish.BotSetId, wish.DecisionVersion));
    }

    // S6-2: DELETE and a new activate bind the new id.
    [Fact]
    public async Task Deactivate_ThenActivate_BindsTheCurrentAccount()
    {
        var channel = await SeedBotChannelAsync(s =>
        {
            s.BotModeEnabled = true;
            s.BotModeActivatedByTwitchUserId = Activator;
            s.BotConsentSevenTvUserId = BotA;
        });

        Assert.True((await Sut().DeactivateAsync(channel.Name, new AuditActor(Activator, "activator"), isGlobalAdmin: false)).IsSuccess);
        Assert.True((await Sut(AccountA with { BotSevenTvUserId = BotB }).ActivateAsync(channel.Name, ModActor)).IsSuccess);

        Assert.Equal(BotB, (await SettingsAsync(channel))!.BotConsentSevenTvUserId);
    }

    // ------------------------------------------------------------------------------------ wishBotModeAvailable

    [Theory]
    [InlineData(true, "*", false, true)]
    [InlineData(false, "*", false, false)]
    [InlineData(true, "", false, false)]
    [InlineData(true, "", true, true)]
    public async Task ModeAvailable_IsTheAccountAndTheListOrAnAdminById(bool accountUp, string pilot, bool adminById, bool expected)
    {
        var channel = await SeedChannelRowAsync();
        var account = accountUp ? AccountA : AccountA with { IsAvailable = false };

        Assert.Equal(expected, await Sut(account, pilot).IsModeAvailableAsync(channel.Name, adminById));
    }

    [Fact]
    public async Task ModeAvailable_AnUnknownChannel_IsFalse_ForANonAdmin()
    {
        Assert.False(await Sut().IsModeAvailableAsync("nosuchchannel", callerIsAdminById: false));
    }

    // Against the real credential row (BotAccount:Enabled on): the kill switch makes the account unavailable.
    [Fact]
    public async Task ModeAvailable_ReadsTheRealRow()
    {
        var channel = await SeedChannelRowAsync();
        await using (var db = CreateDb())
        {
            db.BotAccountCredentials.Add(new BotAccountCredential
            {
                SealedToken = "v1.x",
                Status = BotAccountStatus.Valid,
                ExpiresAtUtc = Now.AddDays(30),
                BotSevenTvUserId = BotA,
                IdentityVersion = 1,
            });
            await db.SaveChangesAsync();
        }

        Assert.True(await RealSut().IsModeAvailableAsync(channel.Name, false));

        await using (var db = CreateDb())
        {
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.KillSwitch, true));
        }

        Assert.False(await RealSut().IsModeAvailableAsync(channel.Name, false));
    }

    // ------------------------------------------------------------------------------------ helpers (channel part)

    private WishBotService Sut(BotAccountAvailabilityState? account = null, string pilot = "*", AppDbContext? db = null)
    {
        var availability = Substitute.For<IWishBotAvailability>();
        availability.GetStateAsync(Arg.Any<CancellationToken>()).Returns(account ?? AccountA);
        return Build(availability, pilot, db ?? CreateDb());
    }

    private WishBotService RealSut()
    {
        var db = CreateDb();
        var wishOptions = new WishOptions { Enabled = true, PilotChannelIds = "*" };
        var availability = new WishBotAvailability(
            wishOptions, new BotAccountAvailability(db, new BotAccountOptions { Enabled = true }, _clock));
        return Build(availability, "*", db);
    }

    private WishBotService Build(IWishBotAvailability availability, string pilot, AppDbContext db) => new(
        db,
        availability,
        new WishOptions { Enabled = true, PilotChannelIds = pilot },
        _editors,
        _roleCache,
        new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
        _nudge,
        _clock);

    private static WishBotCaller Caller(string twitchUserId) => new(twitchUserId, false, false);

    private static SevenTvEditorGrantsLookupResult Grants(string twitchChannelId) =>
        SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "somechannel" },
            new HashSet<string>(StringComparer.Ordinal) { twitchChannelId }));

    private void CacheGrants(string twitchChannelId) =>
        _roleCache.TryGetSevenTvEditorGrantsAsync(Moderator, Arg.Any<CancellationToken>())
            .Returns(Grants(twitchChannelId).Grants);

    private static void Own(Wish wish, bool queued)
    {
        wish.BotOwned = true;
        wish.BotOwnedAddedAtUtc = Now.AddDays(-5);
        wish.BotSetId = ActiveSet;
        if (queued)
        {
            wish.RemovalQueuedAtUtc = Now.AddMinutes(-10);
            wish.RemovalQueuedVersion = 1;
        }
    }

    private static (string, string?) StatusOf(Wish wish) => (wish.Status, wish.StatusReason);

    // jsonb stores its own formatting: compare the parsed documents.
    private static void AssertDetails(string expected, AuditLogEntry entry)
    {
        using var actual = JsonDocument.Parse(entry.DetailsJson!);
        using var wanted = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(wanted.RootElement, actual.RootElement), entry.DetailsJson);
    }

    private static Task HoldChannelForShareAsync(AppDbContext holder, BotChannel channel) =>
        holder.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR SHARE""");

    private async Task<BotChannel> SeedChannelRowAsync(string? twitchId = "")
    {
        await using var db = CreateDb();
        var channel = new Channel
        {
            ChannelName = $"b{Guid.NewGuid():N}"[..18],
            TwitchChannelId = twitchId == "" ? Random.Shared.NextInt64(100_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture) : twitchId,
            IsBotActive = true,
            CreatedAt = Now.AddDays(-100),
            ActiveEmoteSetId = ActiveSet,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return new BotChannel(channel.Id, channel.ChannelName, channel.TwitchChannelId ?? string.Empty);
    }

    private async Task<BotChannel> SeedBotChannelAsync(Action<ChannelWishSettings>? configure = null)
    {
        var channel = await SeedChannelRowAsync();
        await using var db = CreateDb();
        var settings = new ChannelWishSettings { ChannelId = channel.Id };
        configure?.Invoke(settings);
        db.ChannelWishSettings.Add(settings);
        await db.SaveChangesAsync();
        return channel;
    }

    private async Task<long> SeedBotWishAsync(BotChannel channel, string status, Action<Wish>? configure = null)
    {
        await using var db = CreateDb();
        var running = status is WishStatus.Active or WishStatus.Presented;
        var wish = new Wish
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            EmoteName = "Emote",
            Alias = "Alias",
            Source = WishSource.Manual,
            Status = status,
            StatusChangedAtUtc = Now.AddDays(-1),
            CreatedAtUtc = Now.AddDays(-2),
            StartsAtUtc = running ? Now.AddDays(-5) : null,
            StartsAtAuthoritative = running,
            ExpiresAtUtc = running ? Now.AddDays(25) : null,
            DecisionVersion = 1,
        };
        configure?.Invoke(wish);
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private async Task<ChannelWishSettings?> SettingsAsync(BotChannel channel)
    {
        await using var db = CreateDb();
        return await db.ChannelWishSettings.AsNoTracking().SingleOrDefaultAsync(s => s.ChannelId == channel.Id);
    }

    private async Task<string> SettingsSnapshotAsync(BotChannel channel) => JsonSerializer.Serialize(await SettingsAsync(channel));

    private async Task<Wish> WishAsync(long wishId)
    {
        await using var db = CreateDb();
        return await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == wishId);
    }

    private async Task<string> WishSnapshotAsync(long wishId) => JsonSerializer.Serialize(await WishAsync(wishId));

    private async Task<List<AuditLogEntry>> AuditAsync(BotChannel channel)
    {
        await using var db = CreateDb();
        return await db.AuditLogEntries.AsNoTracking().Where(a => a.ChannelName == channel.Name).OrderBy(a => a.Id).ToListAsync();
    }

    private void AssertPublishedOnce(BotChannel channel)
    {
        var call = Assert.Single(_publisher.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IRedisPublisher.PublishAsync));
        Assert.Equal(new LiveEvent(LiveEvents.WishChanged, channel.Name), LiveEvent.TryParse((string?)call.GetArguments()[1]));
    }

    private void AssertSignalledOnce(BotChannel channel)
    {
        AssertPublishedOnce(channel);
        _ = _nudge.Received(1).NudgeAsync(Arg.Any<CancellationToken>());
    }

    private void AssertNoSignal()
    {
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
        _ = _nudge.DidNotReceiveWithAnyArgs().NudgeAsync(default);
    }

    private sealed record BotChannel(string Id, string Name, string TwitchId);
}
