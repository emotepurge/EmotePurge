using System.Diagnostics;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Tests.Fixtures;
using EmotePurge.Infrastructure.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The races and deadlines of an add (#487 plan 2.5a (a) to (g), 2.6; T9, Revisions 2 to 6e) against real Postgres:
// manager commands, DELETE …/wishes/bot, the kill switch, the reconcile, a set change, a second runner and a hand PUT
// between intent and send; two channels on one set; the full sync against the send transaction; the lock deadline (the
// HTTP budget, a held wish row, a deadline that ends after the send); the write spacing; and, with the real runner, the
// Unauthorized over two passes and the pauses booked in the send commit. A wait the deadline would make real is moved
// onto the test clock by the step hook of WishBotPassTests.InvitationRaces.cs.
public partial class WishBotPassTests
{
    private static readonly AuditActor Manager = new("900001", "admin");

    // ------------------------------------------------------------------------------------ between intent and send

    // Sol-P1-1: while the intent is open (during the pre-read) PATCH, close and adopt answer 409 and change nothing; the
    // bot then sends as planned.
    [Theory]
    [InlineData("patch")]
    [InlineData("close")]
    [InlineData("adopt")]
    public async Task ADecisionBetweenIntentAndSend_Is409_AndTheBotSendsAsPlanned(string command)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        WishOutcome? outcome = null;
        _whileReading = async read =>
        {
            await using var db = CreateDb();
            var service = WishSut(db);
            outcome = command switch
            {
                "patch" => (await service.UpdateAsync(channel.Name, id, new UpdateWishRequest("Other", null), Manager)).Outcome,
                "close" => (await service.CloseAsync(channel.Name, id, Manager)).Outcome,
                _ => (await service.AdoptAsync(channel.Name, new AdoptWishRequest("01SLOT", "Alias", null, id, null, null), Manager)).Outcome,
            };
        };

        await RunPassAsync();

        Assert.Equal(WishOutcome.StateConflict, outcome);
        Assert.Equal(("Alias", WishStatus.Active, true), (Assert.Single(_addCalls).Alias, (await LoadAsync(id)).Status, (await LoadAsync(id)).BotOwned));
    }

    // DELETE …/wishes/bot between intent and send resolves the open add without 7TV (presented, unconfirmed); the send
    // transaction then waits for nothing and sends nothing.
    [Fact]
    public async Task DeleteBotBetweenIntentAndSend_SendsNothing_AndLeavesPresentedUnconfirmed()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _whileReading = _ => DeleteBotAsync(channel);

        await RunPassAsync();

        Assert.Empty(_addCalls);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(id)));
    }

    // AK81: the kill switch between intent and send: nothing is sent, the never-sent intent is taken back (approved).
    [Fact]
    public async Task TheKillSwitchBetweenIntentAndSend_SendsNothing_AndTakesTheIntentBack()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _whileReading = _ => KillSwitchAsync(true);

        await RunPassAsync();

        Assert.Empty(_addCalls);
        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Approved, null), StatusOf(wish));
        AssertAttemptCleared(wish);
    }

    // AK81, plan 2.5: the kill switch goes on during the first of several authenticated reads of one pass (two repair
    // checks and two confirmations queued). The session still carries the account of the pass start; every later read
    // asks the credential row itself, so not one more call with the token goes out.
    [Fact]
    public async Task TheKillSwitchDuringAPass_LetsNoFurtherAuthenticatedReadGoOut()
    {
        var channel = await SeedBotChannelAsync();
        await SeedPhaseAsync(channel, WishStatus.Removed, SetA);
        await SeedPhaseAsync(channel, WishStatus.Removed, SetA);
        var first = await SeedUnconfirmedAsync(channel, writtenSecondsAgo: 40, attempt: 1);
        var second = await SeedUnconfirmedAsync(channel, writtenSecondsAgo: 30, attempt: 1);
        _whileReading = number => number == 1 ? KillSwitchAsync(true) : Task.CompletedTask;

        await RunPassAsync();

        Assert.Single(_writes.ReceivedCalls());
        Assert.Single(_readCalls);
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(first)).Status);
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(second)).Status);
    }

    // AK81: transaction 1 of a removal waits for its wish lock while a PUT /kill-switch commits; it reads the credential
    // row before its read and sends nothing, the queue stays for the fallback run.
    [Fact]
    public async Task TheKillSwitchWhileARemovalTakesItsWishLock_SendsNoRead()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var hook = new CommandHook(IsWishLock, occurrence => occurrence == 1 ? KillSwitchAsync(true) : Task.CompletedTask);
        await using var db = fixture.CreateTaggedDbContext("wish-bot-killswitch-removal", [hook], databaseName: DatabaseName);

        await RunPassAsync(db: db);

        Assert.Empty(_writes.ReceivedCalls());
        var wish = await LoadAsync(id);
        Assert.Equal(((DateTime?)null, WishStatus.Active), (wish.BotRemoveAtUtc, wish.Status));
        Assert.NotNull(wish.RemovalQueuedAtUtc);
    }

    // Sol-P3: a PATCH before the intent (between selection and intent) moves the decision version: no intent in this pass,
    // the next pass writes the new alias.
    [Fact]
    public async Task APatchBeforeTheIntent_IsTakenUpByTheNextPass_WithTheNewAlias()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        var session = await SessionAsync();
        session.OnWait = async _ =>
        {
            await using var db = CreateDb();
            Assert.Equal(WishOutcome.Success, (await WishSut(db).UpdateAsync(channel.Name, id, new UpdateWishRequest("Other", null), Manager)).Outcome);
        };

        await RunPassAsync(session: session);
        Assert.Empty(_addCalls);
        Assert.Null((await LoadAsync(id)).BotWriteAtUtc);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.Equal("Other", Assert.Single(_addCalls).Alias);
        Assert.Equal(WishStatus.Active, (await LoadAsync(id)).Status);
    }

    // AK83: the reconcile linked the approved wish (a human added it in 7TV) before the intent: the intent misses, nothing
    // is sent, the wish stays active without ownership.
    [Fact]
    public async Task TheReconcileLinksBeforeTheIntent_NothingIsSent_ActiveWithoutOwnership()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        var session = await SessionAsync();
        session.OnWait = async _ =>
        {
            await using var db = CreateDb();
            await db.Wishes.Where(w => w.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, WishStatus.Active)
                .SetProperty(w => w.LinkOrigin, WishLinkOrigin.Auto)
                .SetProperty(w => w.StartsAtUtc, Now)
                .SetProperty(w => w.ExpiresAtUtc, Now.AddDays(7)));
        };

        await RunPassAsync(session: session);

        Assert.Empty(_addCalls);
        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, false, (DateTime?)null), (wish.Status, wish.BotOwned, wish.BotWriteAtUtc));
    }

    // Set change before the re-check: nothing is sent, the intent is taken back.
    [Fact]
    public async Task ASetChangeBeforeTheRecheck_SendsNothing_AndTakesTheIntentBack()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _whileReading = async _ =>
        {
            await using var db = CreateDb();
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.ActiveEmoteSetId, SetB));
        };

        await RunPassAsync();

        Assert.Empty(_addCalls);
        AssertAttemptCleared(await LoadAsync(id));
        Assert.Equal(WishStatus.Approved, (await LoadAsync(id)).Status);
    }

    // 2.5a (g): a second runner raises the epoch between pre-read and re-check: nothing is sent, the lock counts as lost,
    // the intent stays for the successor's crash path.
    [Fact]
    public async Task AnEpochRaisedBeforeTheRecheck_SendsNothing_AndLeavesTheIntent()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _whileReading = async _ =>
        {
            await using var db = CreateDb();
            await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.RunnerEpoch, Epoch + 1));
        };

        await RunPassAsync();

        Assert.Empty(_addCalls);
        Assert.True(_lastSession!.LockLostReported);
        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Approved, SetA), (wish.Status, wish.BotSetId));
        Assert.NotNull(wish.BotWriteAtUtc);
    }

    // S6-2: a hand PUT to another account between intent and re-check: no addEmote, the intent taken back, the pass ends;
    // the next pass (with B's token) suspends the channel as bot_account_changed.
    [Fact]
    public async Task AHandPutBetweenIntentAndSend_SendsNothing_AndTheChannelBecomesBotAccountChanged()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _whileReading = _ => SeedAccountAsync(BotB, identity: Identity + 1);

        await RunPassAsync();

        Assert.Empty(_addCalls);
        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Approved, null), StatusOf(wish));
        AssertAttemptCleared(wish);

        _whileReading = null;
        await RunPassAsync();

        Assert.Equal(WishBotChannelReasons.BotAccountChanged, (await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Empty(_addCalls);
    }

    // S6-2: a hand PUT after the send and before the confirmation read: no confirmation (not even without ownership); the
    // next pass's fallback presents it (unconfirmed).
    [Fact]
    public async Task AHandPutBeforeTheConfirmationRead_ConfirmsNothing_AndTheFallbackPresents()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _addAnswer = call =>
        {
            Set(call.SetId).Add(new SevenTvSetEntry(call.EmoteId, call.Alias, Now, BotA));
            return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(null);
        };
        _whileReading = async read =>
        {
            if (read == 2)
            {
                await SeedAccountAsync(BotB, identity: Identity + 1);
            }
        };

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Unconfirmed, false, 1), (wish.Status, wish.BotOwned, wish.BotAttemptCount));

        _whileReading = null;
        await RunPassAsync();

        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(id)));
        Assert.Single(_addCalls);
    }

    // ------------------------------------------------------------------------------------ R2-2, R2-3

    // R2-2: two channels on one set, the same emote. A's intent survived a crash; B's intent is not committed while A's
    // attempt is open; A is resumed by reads and confirms the bot entry that is already there only without ownership.
    [Fact]
    public async Task TwoChannelsOneSet_BWaitsWhileAsAttemptIsOpen_AndAConfirmsOnlyWithoutOwnership()
    {
        var channelA = await SeedBotChannelAsync();
        var channelB = await SeedBotChannelAsync();
        var written = Now.AddMinutes(-1);
        var a = await SeedApprovedAsync(channelA, approvedMinutesAgo: 5, configure: w =>
        {
            w.BotWriteAtUtc = written;
            w.BotSetId = SetA;
            w.BotAttemptVersion = SeededVersion;
        });
        var emote = (await LoadAsync(a)).SevenTvEmoteId;
        var b = await SeedApprovedAsync(channelB, approvedMinutesAgo: 10, configure: w => w.SevenTvEmoteId = emote);
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias", Now.AddSeconds(-50), BotA));

        await RunPassAsync();

        Assert.Empty(_addCalls);
        var wishA = await LoadAsync(a);
        Assert.Equal((WishStatus.Active, false, (string?)null), (wishA.Status, wishA.BotOwned, wishA.BotSetId));
        var wishB = await LoadAsync(b);
        Assert.Equal((WishStatus.Approved, (DateTime?)null, SeededVersion), (wishB.Status, wishB.BotWriteAtUtc, wishB.DecisionVersion));
    }

    // R2-3: the full sync wants to switch the active set while the send transaction holds the channel FOR SHARE: it waits
    // and commits right after; the add went into the old set, which was still the active one at the booking.
    [Fact]
    public async Task AFullSyncSwitchingTheSet_WaitsForTheAddsSendTransaction_TheAddWentIntoTheOldSet()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        Task? sync = null;
        _whileAdding = async () =>
        {
            sync = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-sync", DatabaseName);
                await using var transaction = await other.Database.BeginTransactionAsync();
                await other.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""");
                await other.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Channels" SET "ActiveEmoteSetId" = {SetB} WHERE "Id" = {channel.Id}""");
                await transaction.CommitAsync();
            });
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-sync", sync);
        };

        await RunPassAsync();
        await sync!.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(SetA, Assert.Single(_addCalls).SetId);
        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, true, SetA), (wish.Status, wish.BotOwned, wish.BotSetId));
        await using var db = CreateDb();
        Assert.Equal(SetB, await db.Channels.Where(c => c.Id == channel.Id).Select(c => c.ActiveEmoteSetId).SingleAsync());
    }

    // ------------------------------------------------------------------------------------ the lock deadline

    // K6e-3: the monotonic clock passes F right after addEmote answered (stand-in for a lock lost between the call and its
    // booking): nothing is booked; the next pass finds the intent, makes it unconfirmed without the acknowledgement and
    // confirms only without ownership, without a second addEmote.
    [Fact]
    public async Task ADeadlineThatEndsAfterTheSend_BooksNothing_AndTheNextPassResolvesWithoutASecondAdd()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _whileAdding = () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        };

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Approved, (DateTime?)null), (wish.Status, wish.BotAddAckAtUtc));
        Assert.NotNull(wish.BotWriteAtUtc);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAdd));

        _whileAdding = null;
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, false), (wish.Status, wish.BotOwned));
        Assert.Single(_addCalls);
    }

    // Revision 6: addEmote answering after 9 s is still Ok and booked; the send transaction stays inside its deadline.
    [Fact]
    public async Task AnAddAnsweringAfter9Seconds_IsStillBooked()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _whileAdding = () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(9));
            return Task.CompletedTask;
        };

        await RunPassAsync();

        Assert.Equal((WishStatus.Active, true), ((await LoadAsync(id)).Status, (await LoadAsync(id)).BotOwned));
    }

    // K6d-5: the send transaction's lock wait used up so much (16 s, on the test clock) that the call's 10 s no longer fit
    // before F − 3 s: no addEmote, the intent taken back, approved.
    [Fact]
    public async Task ALockWaitThatLeavesNoRoomForTheCall_SendsNothing_AndTakesTheIntentBack()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        var hook = ClockHook((IsChannelShareLock, TimeSpan.Zero), (IsChannelShareLock, TimeSpan.FromSeconds(16)));
        await using var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);

        await RunPassAsync(db: db);

        Assert.Empty(_addCalls);
        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Approved, null), StatusOf(wish));
        AssertAttemptCleared(wish);
    }

    // K6e-3: the test holds the wish row while the full sync waits for the channel row: the wish lock statement ends at F
    // (the clock moved 25 s, Postgres enforces the 3 s left), the send transaction rolls back, the sync commits well within
    // its 30 s, no addEmote. The take-back runs into the same held row and fails: the intent stays and is resolved as a
    // crash remnant without a second add (unconfirmed without the acknowledgement, then presented at the deadline). Released
    // before the take-back, the take-back succeeds: approved.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWishRowHeldPastF_TheSyncCommitsInTime_NothingIsSent(bool releasedBeforeTheTakeBack)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        await using var holder = fixture.CreateTaggedDbContext("wish-bot-holder", DatabaseName);
        IAsyncDisposable? held = null;
        _whileReading = async read =>
        {
            if (read == 1)
            {
                held = await holder.Database.BeginTransactionAsync();
                await holder.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Wishes" WHERE "Id" = {id} FOR UPDATE""");
            }
        };
        Task? sync = null;
        var syncTime = new Stopwatch();
        var clockHook = releasedBeforeTheTakeBack
            ? ClockHook((IsChannelShareLock, TimeSpan.Zero), (IsChannelShareLock, TimeSpan.FromSeconds(25)))
            : ClockHook((IsChannelShareLock, TimeSpan.Zero), (IsChannelShareLock, TimeSpan.FromSeconds(25)), (IsChannelShareLock, TimeSpan.FromSeconds(25)));
        var trigger = new CommandHook(IsChannelShareLock, async lockNumber =>
        {
            if (lockNumber == 2)
            {
                syncTime.Start();
                sync = Task.Run(async () =>
                {
                    await using var other = fixture.CreateTaggedDbContext("wish-bot-sync", DatabaseName);
                    await using var transaction = await other.Database.BeginTransactionAsync();
                    await other.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""");
                    await other.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Channels" SET "ActiveEmoteSetId" = {SetB} WHERE "Id" = {channel.Id}""");
                    await transaction.CommitAsync();
                    syncTime.Stop();
                });
                await fixture.WaitUntilBlockedOnLockAsync("wish-bot-sync", sync);
            }

            if (lockNumber == 3 && releasedBeforeTheTakeBack && held is not null)
            {
                await held.DisposeAsync();
                held = null;
            }
        });
        await using var db = fixture.CreateTaggedDbContext(PassTag, [clockHook, trigger], databaseName: DatabaseName);

        try
        {
            await RunPassAsync(db: db);
            await sync!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync();
            }
        }

        Assert.True(syncTime.Elapsed < TimeSpan.FromSeconds(30), $"The sync took {syncTime.Elapsed}.");
        Assert.Empty(_addCalls);
        var wish = await LoadAsync(id);
        if (releasedBeforeTheTakeBack)
        {
            Assert.Equal((WishStatus.Approved, null), StatusOf(wish));
            AssertAttemptCleared(wish);
            return;
        }

        Assert.Equal(WishStatus.Approved, wish.Status);
        Assert.NotNull(wish.BotWriteAtUtc);

        _whileReading = null;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await RunPassAsync();
        wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Unconfirmed, (DateTime?)null), (wish.Status, wish.BotAddAckAtUtc));

        _clock.Advance(TimeSpan.FromMinutes(16));
        await RunDeadlineStepAsync();
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(id)));
        Assert.Empty(_addCalls);
    }

    // K6d-8: MarkWriteSent stands right before addEmote; the first send transaction waited 3 s for its locks, and the two
    // calls are still at least the write spacing (2 s) apart, counted from the send.
    [Fact]
    public async Task TheWriteSpacing_CountsFromTheSend_AndMarkWriteSentStandsRightBeforeTheCall()
    {
        var channel = await SeedBotChannelAsync();
        await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        var session = await SessionAsync();
        session.WriteDelay = TimeSpan.FromSeconds(2);
        var hook = ClockHook((IsChannelShareLock, TimeSpan.Zero), (IsChannelShareLock, TimeSpan.FromSeconds(3)));
        await using var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);

        await RunPassAsync(db: db, session: session);

        Assert.Equal(2, _addCalls.Count);
        Assert.All(_addCalls, call => Assert.Equal("sent", call.LastSessionCall));
        Assert.True(_addCalls[1].At - _addCalls[0].At >= TimeSpan.FromSeconds(2));
    }

    // ------------------------------------------------------------------------------------ the real runner

    // K6b-3, K6c-5 over two passes. Pass 1: addEmote Unauthorized, the wish stays approved. Pass 2's verification decides:
    // 401 → invalid, approved stays (and needs a human); Ok → the wish is tried again and added; Timeout → not tried.
    [Theory]
    [InlineData("unauthorized")]
    [InlineData("ok")]
    [InlineData("timeout")]
    public async Task AnUnauthorizedAdd_TheNextPassVerificationDecidesWhetherItIsTriedAgain(string secondVerification)
    {
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        var token = Jwt(Now.AddDays(30));
        await SeedSealedAccountAsync(sealer.Seal(token));
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _writes.VerifyAsync(token, Arg.Any<CancellationToken>()).Returns(
            SevenTvWriteResult<string>.Ok(BotA),
            secondVerification switch
            {
                "unauthorized" => SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unauthorized()),
                "timeout" => SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Timeout()),
                _ => SevenTvWriteResult<string>.Ok(BotA),
            });
        var adds = 0;
        _addAnswer = call => ++adds == 1
            ? SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unauthorized())
            : TakeEffect(call);
        var (runner, _) = CreateRunner(_writes);

        await runner.RunPassAsync();

        Assert.Equal((WishStatus.Approved, (DateTime?)null), ((await LoadAsync(id)).Status, (await LoadAsync(id)).BotWriteAtUtc));
        Assert.Equal(BotAccountStatus.Valid, (await CredentialAsync()).Status);
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);

        _clock.Advance(TimeSpan.FromSeconds(61));
        await runner.RunPassAsync();

        var wish = await LoadAsync(id);
        switch (secondVerification)
        {
            case "unauthorized":
                Assert.Equal(BotAccountStatus.Invalid, (await CredentialAsync()).Status);
                Assert.Equal(WishStatus.Approved, wish.Status);
                Assert.Single(_addCalls);
                break;
            case "ok":
                Assert.Equal(WishStatus.Active, wish.Status);
                Assert.Equal(2, _addCalls.Count);
                break;
            default:
                Assert.Equal(WishStatus.Approved, wish.Status);
                Assert.Single(_addCalls);
                break;
        }
    }

    // K6c-3: RateLimited books the cleared attempt and PausedUntilUtc in the send commit; nothing else is called with the
    // token in the pass.
    [Fact]
    public async Task ARateLimitedAdd_BooksThePauseInTheSendCommit_WithTheRealRunner()
    {
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        var token = Jwt(Now.AddDays(30));
        await SeedSealedAccountAsync(sealer.Seal(token));
        _writes.VerifyAsync(token, Arg.Any<CancellationToken>()).Returns(SevenTvWriteResult<string>.Ok(BotA));
        var channel = await SeedBotChannelAsync();
        var first = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var second = await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        _addAnswer = _ => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(120)));
        var (runner, _) = CreateRunner(_writes);
        var at = Now;

        await runner.RunPassAsync();

        Assert.Equal(at.AddSeconds(120), (await CredentialAsync()).PausedUntilUtc);
        AssertAttemptCleared(await LoadAsync(first));
        Assert.Equal(WishStatus.Approved, (await LoadAsync(first)).Status);
        Assert.Null((await LoadAsync(second)).BotWriteAtUtc);
        Assert.Single(_addCalls);
        Assert.Single(_readCalls);
    }

    // K6e-4 with the real runner: Ok with a Retry-After books unconfirmed (acknowledged), wish.botAdd and PausedUntilUtc in
    // one commit; no read and no next add in that pass. After a short pause the read confirms with ownership; a pause longer
    // than the deadline leaves it to the deadline step (presented, unconfirmed), without a second addEmote.
    [Theory]
    [InlineData(30)]
    [InlineData(1200)]
    public async Task AnOkWithARetryAfter_WithTheRealRunner_ConfirmsAfterThePause_OrIsPresentedAtTheDeadline(int pauseSeconds)
    {
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        var token = Jwt(Now.AddDays(30));
        await SeedSealedAccountAsync(sealer.Seal(token));
        _writes.VerifyAsync(token, Arg.Any<CancellationToken>()).Returns(SevenTvWriteResult<string>.Ok(BotA));
        var channel = await SeedBotChannelAsync();
        var first = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var second = await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        var adds = 0;
        _addAnswer = call =>
        {
            var answer = TakeEffect(call);
            return ++adds == 1 ? SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(null, TimeSpan.FromSeconds(pauseSeconds)) : answer;
        };
        var (runner, _) = CreateRunner(_writes);
        var at = Now;

        await runner.RunPassAsync();

        var wish = await LoadAsync(first);
        Assert.Equal((WishStatus.Unconfirmed, at), (wish.Status, wish.BotAddAckAtUtc!.Value));
        Assert.Equal(at.AddSeconds(pauseSeconds), (await CredentialAsync()).PausedUntilUtc);
        Assert.Equal(1, await CountAuditsAsync(AuditActions.WishBotAdd));
        Assert.Single(_addCalls);
        Assert.Single(_readCalls);
        Assert.Null((await LoadAsync(second)).BotWriteAtUtc);

        if (pauseSeconds < 60)
        {
            _clock.Advance(TimeSpan.FromSeconds(pauseSeconds + 1));
            await runner.RunPassAsync();
            wish = await LoadAsync(first);
            Assert.Equal((WishStatus.Active, true), (wish.Status, wish.BotOwned));
            return;
        }

        _clock.Advance(TimeSpan.FromMinutes(17));
        await runner.RunPassAsync();
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(first)));
        Assert.Single(_addCalls);
    }

    // The default answer of "7TV" to an add: it takes effect (an entry by _addedBy at the test clock), the set comes back.
    private SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>> TakeEffect(AddCall call)
    {
        var entries = Set(call.SetId);
        entries.Add(new SevenTvSetEntry(call.EmoteId, call.Alias, call.At, _addedBy));
        return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(entries.ToList());
    }
}
