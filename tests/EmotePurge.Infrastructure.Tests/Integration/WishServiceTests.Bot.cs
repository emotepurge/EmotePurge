using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The seams of the manager-facing wish service with the bot flow (#487, plan T6): the channel's effective bot state in
// mode and lists, botWritePending, the open-attempt guard at every mutating path, the reset at re-add and retry, the
// threshold change past an open attempt, D7 behind the channel lock, and the repair phase at the hard delete. The bot
// account's state is a substitute unless a case reads the real row; every case also pins that the manual flow is
// unchanged where it says so.
public partial class WishServiceTests
{
    private const string BotId = "01BOTACCOUNTSEVENTV00000";
    private const int BotIdentity = 4;
    private const string Activator = "7777";
    private const string BotSetId = "01WISHBOTSET0000000000000C";

    private static readonly BotAccountAvailabilityState BotUp = new(true, BotId, BotIdentity);

    // ------------------------------------------------------------------------------------ mode and lists

    [Fact]
    public async Task Bot_AnActingChannel_HasModeBot_AndApprovedRunsInsteadOfNeedingAction()
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel);
        var approved = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);

        Assert.Empty((await BotSut().ListAsync(channel.Name, WishListView.Open, 1, 50))!.Items);
        var running = (await BotSut().ListAsync(channel.Name, WishListView.Running, 1, 50))!.Items;
        Assert.Equal(approved, Assert.Single(running).Id);
        Assert.Equal("bot", running[0].Mode);
    }

    // AK79 and [W1]: every way out of "bot" is fallback, which the lists treat like manual, before the runner books a
    // reason. R2-5: a proof of identity I after a hand PUT to I+1; S6-2: another account than the consent.
    [Theory]
    [InlineData("hand put to another identity")]
    [InlineData("check older than 15 minutes")]
    [InlineData("consent to another account")]
    [InlineData("account unavailable")]
    [InlineData("suspended")]
    [InlineData("not listed")]
    public async Task Bot_AFallenBackChannel_HasModeManual_AndApprovedNeedsActionAgain(string cause)
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel, s =>
        {
            if (cause == "check older than 15 minutes")
            {
                s.BotEditorCheckedAtUtc = Now - WishBotChannelState.EditorCheckFreshness - TimeSpan.FromSeconds(1);
            }
            else if (cause == "suspended")
            {
                s.BotSuspendedReason = "not_editor";
            }
        });
        var account = cause switch
        {
            "hand put to another identity" => BotUp with { IdentityVersion = BotIdentity + 1 },
            "consent to another account" => BotUp with { BotSevenTvUserId = "01OTHERBOTACCOUNT" },
            "account unavailable" => BotUp with { IsAvailable = false },
            _ => BotUp,
        };
        var approved = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);
        var sut = BotSut(account, pilotChannelIds: cause == "not listed" ? "" : "*");

        var open = (await sut.ListAsync(channel.Name, WishListView.Open, 1, 50))!.Items;

        Assert.Equal(approved, Assert.Single(open).Id);
        Assert.Equal("manual", open[0].Mode);
        Assert.Empty((await sut.ListAsync(channel.Name, WishListView.Running, 1, 50))!.Items);
    }

    // E8: an unlisted channel is covered by an activator who is a global admin by id.
    [Fact]
    public async Task Bot_AnUnlistedChannel_ActivatedByAnAdminById_IsBot()
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel);
        var approved = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);
        var admins = Substitute.For<IGlobalAdminAllowlist>();
        admins.IsAdminById(Activator).Returns(true);

        var running = (await BotSut(pilotChannelIds: "", admins: admins).ListAsync(channel.Name, WishListView.Running, 1, 50))!.Items;

        Assert.Equal((approved, "bot"), (Assert.Single(running).Id, running[0].Mode));
    }

    // K6b-5 against the real row: the proof of I with the account at I+1 is fallback, from one read of the row per
    // request, however often the request asks; a manual channel reads the row not at all.
    [Fact]
    public async Task Bot_TheAccountRow_IsReadOncePerRequest_AndNotForAManualChannel()
    {
        var bot = await SeedChannelAsync();
        await ActivateBotAsync(bot);
        var manual = await SeedChannelAsync();
        await SeedWishAsync(bot, EmoteId(), WishStatus.Approved);
        await SeedWishAsync(manual, EmoteId(), WishStatus.Approved);
        await SeedCredentialsAsync(BotIdentity + 1);
        try
        {
            var hook = new CommandHook(_ => false, _ => Task.CompletedTask);
            await using var db = fixture.CreateTaggedDbContext("wish-bot-state-reads", [hook]);
            var sut = BotSut(db: db, realAccount: true);

            var open = (await sut.ListAsync(bot.Name, WishListView.Open, 1, 50))!.Items;
            await sut.ListAsync(bot.Name, WishListView.Running, 1, 50);
            Assert.Equal("manual", Assert.Single(open).Mode);
            Assert.Equal(1, hook.Count(IsCredentialsRead));

            var manualHook = new CommandHook(_ => false, _ => Task.CompletedTask);
            await using var manualDb = fixture.CreateTaggedDbContext("wish-bot-state-manual", [manualHook]);
            Assert.Single((await BotSut(db: manualDb, realAccount: true).ListAsync(manual.Name, WishListView.Open, 1, 50))!.Items);
            Assert.Equal(0, manualHook.Count(IsCredentialsRead));
        }
        finally
        {
            await ClearCredentialsAsync();
        }
    }

    // [W9]: only while the write is under way.
    [Theory]
    [InlineData(WishStatus.Approved, true)]
    [InlineData(WishStatus.Unconfirmed, true)]
    [InlineData(WishStatus.Active, false)]
    [InlineData(WishStatus.Kept, false)]
    [InlineData(WishStatus.Failed, false)]
    [InlineData(WishStatus.Presented, false)]
    [InlineData(WishStatus.Removed, false)]
    public async Task Bot_WritePending_OnlyInApprovedAndUnconfirmed(string status, bool expected)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, status, w => w.BotWriteAtUtc = Now.AddMinutes(-3));

        var detail = await Sut().GetAsync(channel.Name, wishId);

        Assert.Equal(expected, detail!.BotWritePending);
    }

    // ------------------------------------------------------------------------------------ the open-attempt guard

    // 2.5a (b) at every real path: an add intent in approved, a removal attempt in active and presented. Each answers a
    // state conflict and leaves the row, its decision version included, the audit log and the stream untouched.
    [Theory]
    [InlineData(WishStatus.Approved, "write", "patch")]
    [InlineData(WishStatus.Approved, "write", "close")]
    [InlineData(WishStatus.Approved, "write", "adopt")]
    [InlineData(WishStatus.Active, "remove", "extend")]
    [InlineData(WishStatus.Active, "remove", "keep")]
    [InlineData(WishStatus.Active, "remove", "complete-removal")]
    [InlineData(WishStatus.Active, "remove", "unlink")]
    [InlineData(WishStatus.Active, "remove", "adopt")]
    [InlineData(WishStatus.Presented, "remove", "extend")]
    [InlineData(WishStatus.Presented, "remove", "keep")]
    [InlineData(WishStatus.Presented, "remove", "close")]
    [InlineData(WishStatus.Presented, "remove", "complete-removal")]
    [InlineData(WishStatus.RemovedExternally, "remove", "readd")]
    [InlineData(WishStatus.RemovedExternally, "remove", "complete-removal")]
    public async Task Guard_AnOpenAttempt_BlocksTheCommand_AndChangesNothing(string status, string attempt, string command)
    {
        var channel = await SeedChannelAsync();
        var bound = await SeedCaptureBoundRowAsync(channel, status, w =>
        {
            if (attempt == "write")
            {
                w.BotWriteAtUtc = Now.AddMinutes(-1);
                w.BotSetId = BotSetId;
                w.BotAttemptVersion = w.DecisionVersion;
            }
            else
            {
                w.BotRemoveAtUtc = Now.AddMinutes(-1);
                w.BotSetId = BotSetId;
                w.BotAttemptVersion = w.DecisionVersion;
                w.BotRemoveEvidence = """{"setId":"x"}""";
            }

            if (status == WishStatus.Presented)
            {
                w.StatusReason = WishStatusReason.AlreadyInSet;
            }
        });
        var before = await SnapshotAsync(bound.WishId);

        var result = command switch
        {
            "adopt" => await Sut().AdoptAsync(
                channel.Name, await WishAdoptRequests.ForCurrentAsync(fixture, bound.SlotId, bound.WishId, null, null), Actor),
            _ => await RunAsync(command, channel, bound.WishId),
        };

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(bound.WishId));
        await AssertNoTraceAsync(channel);
    }

    // The approval of a second wish would extend the running one: with a removal attempt open on it, nothing is booked,
    // neither the extension nor the merge.
    [Fact]
    public async Task Guard_ApproveIntoARunningWishWithARemovalAttempt_IsAConflict_WithoutAnExtension()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var running = await SeedRowAsync(channel, WishStatus.Active, w =>
        {
            w.SevenTvEmoteId = emoteId;
            w.BotRemoveAtUtc = Now.AddMinutes(-1);
            w.BotSetId = BotSetId;
        });
        var open = await SeedRowAsync(channel, WishStatus.Pending, w => w.SevenTvEmoteId = emoteId);
        var before = (await SnapshotAsync(running), await SnapshotAsync(open));

        var result = await Sut().ApproveAsync(channel.Name, open, new ApproveWishRequest(null, null, null, null), Actor);

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.Equal(before, (await SnapshotAsync(running), await SnapshotAsync(open)));
        await AssertNoTraceAsync(channel);
    }

    // The defensive check at the hard delete: an ended wish with a removal attempt (no producer reaches this) is a
    // state conflict, not a delete.
    [Fact]
    public async Task Guard_TheHardDelete_RefusesAnEndedWishWithARemovalAttempt()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Removed, w => w.BotRemoveAtUtc = Now.AddMinutes(-1));
        var endedAt = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;

        var result = await Sut().DeleteAsync(channel.Name, wishId, endedAt, Actor, await AccessAsync(channel));

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.NotNull(await LoadWishOrNullAsync(wishId));
    }

    // 2.5a (b), (h): an open repair phase blocks no command (only the hard delete), and the command leaves it standing.
    [Theory]
    [InlineData("extend")]
    [InlineData("keep")]
    public async Task Guard_AnOpenRepairPhase_BlocksNoCommand_AndStays(string command)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Active, w =>
        {
            w.BotRepairAtUtc = Now.AddMinutes(-2);
            w.BotRemoveEvidence = """{"setId":"x"}""";
        });

        var result = await RunAsync(command, channel, wishId);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = await LoadWishAsync(wishId);
        Assert.Equal((Now.AddMinutes(-2), """{"setId": "x"}"""), (row.BotRepairAtUtc!.Value, row.BotRemoveEvidence));
    }

    // ------------------------------------------------------------------------------------ re-add and retry

    // 2.1 point 2, [W4]: the bot starts afresh, the link origin goes, the slot stays as history, and an open repair
    // phase with its evidence stays (2.5a (h)).
    [Theory]
    [InlineData(WishStatus.Removed, "readd", true)]
    [InlineData(WishStatus.RemovedExternally, "readd", false)]
    [InlineData(WishStatus.Failed, "retry", true)]
    public async Task Reset_ReAddAndRetry_StartTheBotAfresh_ButKeepARepairPhase(string status, string command, bool repairOpen)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slotId = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-12), archived: true);
        var wishId = await SeedRowAsync(channel, status, w =>
        {
            w.SevenTvEmoteId = emote.SevenTvEmoteId;
            w.SlotId = command == "readd" ? slotId : null;
            w.LinkOrigin = WishLinkOrigin.Bot;
            w.BotWriteAtUtc = Now.AddDays(-12);
            w.BotAttemptCount = 3;
            w.BotNextAttemptAtUtc = Now.AddMinutes(5);
            w.BotSetId = BotSetId;
            w.BotAttemptVersion = 2;
            w.BotAddAckAtUtc = Now.AddDays(-12);
            w.BotConfirmDeadlineAtUtc = Now.AddDays(-12);
            if (repairOpen)
            {
                w.BotRepairAtUtc = Now.AddHours(-1);
                w.BotRemoveEvidence = """{"setId":"x"}""";
            }
        });

        var result = await RunAsync(command, channel, wishId);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = await LoadWishAsync(wishId);
        Assert.Equal(WishStatus.Approved, row.Status);
        Assert.Equal(
            ((DateTime?)null, 0, (DateTime?)null, (string?)null, (int?)null, (DateTime?)null, (DateTime?)null, (DateTime?)null),
            (row.BotWriteAtUtc, row.BotAttemptCount, row.BotNextAttemptAtUtc, row.BotSetId, row.BotAttemptVersion, row.BotRemoveAtUtc,
                row.BotAddAckAtUtc, row.BotConfirmDeadlineAtUtc));
        Assert.Equal(repairOpen, row.BotRepairAtUtc is not null);
        Assert.Equal(repairOpen, row.BotRemoveEvidence is not null);
        if (command == "readd")
        {
            Assert.Null(row.LinkOrigin);
            Assert.Equal(slotId, row.SlotId);
        }
    }

    // ------------------------------------------------------------------------------------ threshold change

    // 2.5a (b): the change comes too late for a wish with an open attempt, like a 409; the others are superseded as before.
    [Fact]
    public async Task Threshold_AChange_SkipsAWishWithAnOpenAttempt_AndSupersedesTheOthers()
    {
        var channel = await SeedChannelAsync();
        var blocked = await SeedRowAsync(channel, WishStatus.Active, w =>
        {
            w.BotRemoveAtUtc = Now.AddMinutes(-1);
            w.BotSetId = BotSetId;
            w.BotAttemptVersion = SeededDecisionVersion;
        });
        var other = await SeedRowAsync(channel, WishStatus.Active);
        var blockedBefore = await SnapshotAsync(blocked);

        var result = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, 3m), Actor);

        Assert.True(result.IsSuccess);
        Assert.Equal(blockedBefore, await SnapshotAsync(blocked));
        var superseded = await LoadWishAsync(other);
        Assert.Equal((SeededDecisionVersion + 1, (DateTime?)null), (superseded.DecisionVersion, superseded.RemovalQueuedAtUtc));
    }

    // ------------------------------------------------------------------------------------ D7 behind the lock

    // [W2] and 2.9: the stored activation decides, also while suspended; the service checks again behind the lock, so a
    // plain manager is refused even without the filter.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D7_InAnActivatedChannel_APlainManagerIsRefused_EvenWhileSuspended(bool suspended)
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel, s => s.BotSuspendedReason = suspended ? "not_editor" : null);
        var before = await SettingsSnapshotAsync(channel);

        var result = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(14, 7, null), Actor);

        Assert.Equal(WishOutcome.SettingsRestricted, result.Outcome);
        Assert.Equal(before, await SettingsSnapshotAsync(channel));
        Assert.False(await Sut().CanChangeSettingsAsync(channel.Name, Actor.TwitchUserId, false));
        await AssertNoTraceAsync(channel);
    }

    [Theory]
    [InlineData("activator", false)]
    [InlineData("broadcaster", false)]
    [InlineData("plain", true)]
    public async Task D7_InAnActivatedChannel_TheActivatorTheBroadcasterAndAnAdminMayChange(string caller, bool isAdmin)
    {
        var broadcaster = $"bc-{Guid.NewGuid():N}"[..20];
        var channel = await SeedChannelAsync(twitchId: broadcaster);
        await ActivateBotAsync(channel);
        var actor = caller switch
        {
            "activator" => new AuditActor(Activator, "activator"),
            "broadcaster" => new AuditActor(broadcaster, "broadcaster"),
            _ => Actor,
        };

        var result = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(14, 7, null), actor, isAdmin);

        Assert.True(result.IsSuccess);
        Assert.Equal(14, result.Settings!.DefaultDurationDays);
    }

    // The regression of live K1 code: in a manual channel every manager still changes the settings.
    [Fact]
    public async Task D7_InAManualChannel_AnyManagerChangesTheSettings_AsBefore()
    {
        var channel = await SeedChannelAsync();

        var result = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(14, 7, 2m), Actor);

        Assert.True(result.IsSuccess);
        Assert.Equal((14, 2m), (result.Settings!.DefaultDurationDays, result.Settings.ProtectionThresholdPerDay));
    }

    // Sol-P2-6: a manager passed the filter while the channel ran manually; before the command gets the channel row, the
    // worker confirms an activation under that lock. Behind the lock the command sees it and refuses.
    [Fact]
    public async Task D7_AnActivationConfirmedWhileTheCommandWaitsForTheChannel_RefusesIt()
    {
        const string tag = "wish-d7-settings-contender";
        var channel = await SeedChannelAsync();

        await using var worker = fixture.CreateDbContext();
        await using var workerTransaction = await worker.Database.BeginTransactionAsync();
        Assert.Single(await worker.LockChannelsByIdsAsync([channel.Id], CancellationToken.None));
        worker.ChannelWishSettings.Add(new ChannelWishSettings
        {
            ChannelId = channel.Id,
            BotModeEnabled = true,
            BotModeActivatedByTwitchUserId = Activator,
            BotConsentSevenTvUserId = BotId
        });
        await worker.SaveChangesAsync();

        await using var contender = fixture.CreateTaggedDbContext(tag);
        var waiting = Sut(contender).UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(14, 7, null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync(tag, waiting);
        await workerTransaction.CommitAsync();

        Assert.Equal(WishOutcome.SettingsRestricted, (await waiting).Outcome);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WishLimits.DefaultDurationDays, (await db.ChannelWishSettings.SingleAsync(s => s.ChannelId == channel.Id)).DefaultDurationDays);
    }

    // ------------------------------------------------------------------------------------ the repair phase at delete

    // K6b-12: while a repair phase is open the ended wish stays with its evidence (removed, and closed after a
    // presentation that carried a phase), whatever the bot mode is now; once the phase is resolved it can be deleted.
    [Theory]
    [InlineData(WishStatus.Removed, false)]
    [InlineData(WishStatus.Closed, false)]
    [InlineData(WishStatus.Removed, true)]
    public async Task Repair_AnOpenPhase_KeepsTheEndedWish_UntilItIsResolved(string status, bool botModeOn)
    {
        var channel = await SeedChannelAsync();
        if (botModeOn)
        {
            await ActivateBotAsync(channel);
        }

        var wishId = await SeedRowAsync(channel, status, w =>
        {
            w.BotRepairAtUtc = Now.AddMinutes(-30);
            w.BotRemoveEvidence = """{"setId":"x"}""";
        });
        var endedAt = (await LoadWishAsync(wishId)).EndedAtUtc!.Value;
        var before = await SnapshotAsync(wishId);
        var access = await AccessAsync(channel);

        var refused = await Sut().DeleteAsync(channel.Name, wishId, endedAt, Actor, access);

        Assert.Equal(WishOutcome.RepairCheckOpen, refused.Outcome);
        Assert.Equal(before, await SnapshotAsync(wishId));
        await AssertNoTraceAsync(channel);

        await using (var db = fixture.CreateDbContext())
        {
            await db.Wishes.Where(w => w.Id == wishId).ExecuteUpdateAsync(u => u
                .SetProperty(w => w.BotRepairAtUtc, (DateTime?)null)
                .SetProperty(w => w.BotRemoveEvidence, (string?)null));
        }

        Assert.Equal(WishOutcome.Success, (await Sut().DeleteAsync(channel.Name, wishId, endedAt, Actor, access)).Outcome);
        Assert.Null(await LoadWishOrNullAsync(wishId));
    }

    // ------------------------------------------------------------------------------------ helpers (bot)

    // The service with the real per-channel state over a substituted (or, with realAccount, the real) bot account.
    private WishService BotSut(
        BotAccountAvailabilityState? account = null,
        AppDbContext? db = null,
        string pilotChannelIds = "*",
        IGlobalAdminAllowlist? admins = null,
        bool realAccount = false)
    {
        db ??= fixture.CreateDbContext();
        IWishBotChannelStateReader reader;
        if (realAccount)
        {
            reader = BotChannelStates.Real(db, _clock, pilotChannelIds, admins);
        }
        else
        {
            var availability = Substitute.For<IWishBotAvailability>();
            availability.GetStateAsync(Arg.Any<CancellationToken>()).Returns(account ?? BotUp);
            reader = new WishBotChannelStateReader(
                availability, new WishOptions { Enabled = true, PilotChannelIds = pilotChannelIds }, admins ?? Substitute.For<IGlobalAdminAllowlist>(), _clock);
        }

        return new(db, new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance), _clock, NullLogger<WishService>.Instance, reader);
    }

    // An activation as the worker confirms it: consent to the bot account, a fresh editor proof of its identity.
    private async Task ActivateBotAsync(Seeded channel, Action<ChannelWishSettings>? configure = null)
    {
        await using var db = fixture.CreateDbContext();
        var settings = new ChannelWishSettings
        {
            ChannelId = channel.Id,
            BotModeEnabled = true,
            BotModeActivatedByTwitchUserId = Activator,
            BotModeActivatedByLogin = "activator",
            BotModeActivatedAtUtc = Now.AddDays(-1),
            BotConsentSevenTvUserId = BotId,
            BotEditorCheckedAtUtc = Now.AddMinutes(-1),
            BotEditorCheckedIdentityVersion = BotIdentity
        };
        configure?.Invoke(settings);
        db.ChannelWishSettings.Add(settings);
        await db.SaveChangesAsync();
    }

    // The one credential row is shared with the bot account suites of this collection: a case that writes it clears it.
    private async Task SeedCredentialsAsync(int identityVersion)
    {
        await ClearCredentialsAsync();
        await using var db = fixture.CreateDbContext();
        db.BotAccountCredentials.Add(new BotAccountCredential
        {
            SealedToken = "v1.x",
            Status = BotAccountStatus.Valid,
            ExpiresAtUtc = Now.AddDays(30),
            BotSevenTvUserId = BotId,
            IdentityVersion = identityVersion
        });
        await db.SaveChangesAsync();
    }

    private async Task ClearCredentialsAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.BotAccountCredentials.ExecuteDeleteAsync();
    }

    private static bool IsCredentialsRead(string command) => command.Contains("\"BotAccountCredentials\"", StringComparison.Ordinal);

    // A row of the state bound to a live slot as an automatic capture link (unlink needs that), the attempt configured.
    private async Task<BoundRow> SeedCaptureBoundRowAsync(Seeded channel, string status, Action<Wish> configure) =>
        await SeedBoundRowAsync(channel, status, w =>
        {
            w.Source = WishSource.Irc;
            w.MatchKind = WishMatchKind.Link;
            configure(w);
        });

    private async Task<Wish?> LoadWishOrNullAsync(long wishId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Wishes.AsNoTracking().SingleOrDefaultAsync(w => w.Id == wishId);
    }

    private async Task<string> SettingsSnapshotAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return JsonSerializer.Serialize(await db.ChannelWishSettings.AsNoTracking().SingleOrDefaultAsync(s => s.ChannelId == channel.Id));
    }
}
