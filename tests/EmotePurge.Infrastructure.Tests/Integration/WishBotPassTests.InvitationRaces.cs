using System.Diagnostics;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using EmotePurge.Infrastructure.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The races and deadlines of the acceptance (#487 plan 2.5a (c), 2.5b, 2.9; T8c, Revisions 6b to 6f) against real
// Postgres: the channel row FOR SHARE against the full sync and DELETE, the lock deadline (statement and idle timeout)
// against a held credential row and a hanging client, the lock probe outside every row lock, and, with the real runner,
// the pause joined to the send transaction, booked again at the end of a pass and honoured by a successor. Where a wait
// of 28 s would be real, a command hook moves the test's monotonic clock instead, right after the first statement that
// follows the transaction's first lock (so the deadline has started), and Postgres enforces the shortened rest.
public partial class WishBotPassTests
{
    private const string PassTag = "wish-bot-pass-under-test";

    // ------------------------------------------------------------------------------------ the channel row FOR SHARE

    // K6b-6: a full sync that wants to switch the active set waits for the acceptance's send transaction and commits right
    // after it, without a timeout.
    [Fact]
    public async Task AFullSyncSwitchingTheSet_WaitsForTheSendTransaction_AndCommitsAfterIt()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        Task? sync = null;
        _whileAccepting = async () =>
        {
            sync = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-sync", DatabaseName);
                await using var transaction = await other.Database.BeginTransactionAsync();
                await other.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""");
                await other.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Channels" SET "ActiveEmoteSetId" = 'set-after-sync' WHERE "Id" = {channel.Id}""");
                await transaction.CommitAsync();
            });
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-sync", sync);
        };

        await RunPassAsync();
        await sync!.WaitAsync(TimeSpan.FromSeconds(15));

        await using var db = CreateDb();
        Assert.Equal("set-after-sync", await db.Channels.Where(c => c.Id == channel.Id).Select(c => c.ActiveEmoteSetId).SingleAsync());
        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
    }

    // K6b-10, the worker's half of "the runner is about to accept": a DELETE during the send transaction waits for it
    // (channel row FOR SHARE) and then sees the open attempt: invitation_accept_unresolved.
    [Fact]
    public async Task ADeleteDuringTheSendTransaction_WaitsForIt_AndSeesTheOpenAttempt()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Timeout());
        _acceptTakesEffect = false;
        Task? delete = null;
        _whileAccepting = async () =>
        {
            delete = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-delete", DatabaseName);
                await DeleteBotAsync(channel, other);
            });
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-delete", delete);
        };

        await RunPassAsync();
        await delete!.WaitAsync(TimeSpan.FromSeconds(15));

        var settings = await SettingsAsync(channel);
        Assert.Equal((WishBotChannelReasons.InvitationAcceptUnresolved, BotA), (settings.BotActivationError, settings.BotConsentSevenTvUserId));
        Assert.NotNull(settings.BotInvitationAttemptAtUtc);
        Assert.Equal(true.ToString(), Assert.Single(await AuditsAsync(AuditActions.WishBotDeactivate)).Details["invitationAcceptUnresolved"]);
    }

    // K6b-10: DELETE ends the request while the worker reads; the confirmation after it hits nothing, the mode stays off.
    [Fact]
    public async Task ADeleteDuringTheConfirmingRead_TheConfirmationHitsNothing()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "ACCEPTED", true));
        HoldNextRead();

        var pass = RunPassAsync();
        await _heldReadEntered!.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await DeleteBotAsync(channel);
        ReleaseHeldRead();
        await pass;

        var settings = await SettingsAsync(channel);
        Assert.Equal((false, (DateTime?)null), (settings.BotModeEnabled, settings.BotActivationRequestedAtUtc));
        Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));
    }

    // K6b-10: DELETE between the read and the attempt: the stamp's condition misses, nothing is stamped or sent.
    [Fact]
    public async Task ADeleteBetweenReadAndStamp_StampsAndSendsNothing()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        var session = await SessionAsync();
        session.OnProbe = async probe =>
        {
            if (probe == 3)
            {
                await DeleteBotAsync(channel);
            }

            return true;
        };

        await RunPassAsync(session: session);

        Assert.Empty(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.Equal(((DateTime?)null, (string?)null, (string?)null), (settings.BotInvitationAttemptAtUtc, settings.BotConsentSevenTvUserId, settings.BotActivationError));
    }

    // K6b-10, S6-1: DELETE (or a new request) between the stamp and the send transaction: the re-check fails, nothing is
    // sent, the never-sent attempt is taken back; after a DELETE the row returns to what DELETE leaves without an attempt.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeleteOrANewRequestBetweenStampAndSend_SendsNothing_AndTakesTheAttemptBack(bool delete)
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        var session = await SessionAsync();
        session.OnProbe = async probe =>
        {
            if (probe == 4)
            {
                if (delete)
                {
                    await DeleteBotAsync(channel);
                }
                else
                {
                    await using var db = CreateDb();
                    await db.ChannelWishSettings.Where(s => s.ChannelId == channel.Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.BotActivationRequestedAtUtc, Now.AddSeconds(1)));
                }
            }

            return true;
        };

        await RunPassAsync(session: session);

        Assert.Empty(_acceptCalls);
        Assert.DoesNotContain("sent", session.Calls);
        var settings = await SettingsAsync(channel);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.Null(settings.BotActivationError);
        Assert.Equal(delete ? null : BotA, settings.BotConsentSevenTvUserId);
    }

    // K6c-1 (A→B→A), the worker's half: an attempt for A stays unclear, DELETE, a hand PUT to B, activate (gives the
    // attempt up, binds B), the account goes back to A and A's acceptance takes effect late: the request (bound to B)
    // ends with bot_account_changed, no acceptance, no confirmation, and the only audit is wish.botInvitationAbandoned.
    [Fact]
    public async Task AToBToA_TheRequestBoundToB_EndsBotAccountChanged_AndAGetsNothing()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Timeout());
        _acceptTakesEffect = false;
        await RunPassAsync();

        await DeleteBotAsync(channel);
        await SeedAccountAsync(BotB, identity: Identity + 1);
        await ActivateAsync(channel);
        await SeedAccountAsync(BotA, identity: Identity + 2);
        TakeEffect(BotA, OwnerOf(channel));
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.Single(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.Equal((false, WishBotChannelReasons.BotAccountChanged, (DateTime?)null), (settings.BotModeEnabled, settings.BotActivationError, settings.BotInvitationAttemptAtUtc));
        Assert.Empty(await AuditsAsync(AuditActions.WishBotActivate));
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAcceptInvitation));
        Assert.Empty(await AuditsAsync(AuditActions.WishBotInvitationUnresolved));
        Assert.Equal(BotA, Assert.Single(await AuditsAsync(AuditActions.WishBotInvitationAbandoned)).Details["abandonedBotSevenTvUserId"]);
    }

    // ------------------------------------------------------------------------------------ K6c-4: stamp and send

    // The write spacing lies before the stamp: with 60 s to wait, stamp and send are less than 30 s apart.
    [Fact]
    public async Task TheWriteSpacing_LiesBeforeTheStamp()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Timeout());
        _acceptTakesEffect = false;
        var session = await SessionAsync();
        session.WriteDelay = TimeSpan.FromSeconds(60);
        session.MarkWriteSent();
        var before = Now;

        await RunPassAsync(session: session);

        var call = Assert.Single(_acceptCalls);
        var stamp = (await SettingsAsync(channel)).BotInvitationAttemptAtUtc!.Value;
        Assert.Equal(before.AddSeconds(60), stamp);
        Assert.InRange(call.At - stamp, TimeSpan.Zero, WishBotInvitationPolicy.SendWindow);
    }

    // The send transaction begins 31 s after the stamp: not sent, nothing marked as sent, the attempt taken back; the next
    // pass makes a new attempt and sends it.
    [Fact]
    public async Task ASendTransaction31SecondsAfterTheStamp_SendsNothing_AndTheNextPassTriesAnew()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        var session = await SessionAsync();
        session.OnProbe = probe =>
        {
            if (probe == 4)
            {
                _clock.Advance(TimeSpan.FromSeconds(31));
            }

            return Task.FromResult(true);
        };

        await RunPassAsync(session: session);

        Assert.Empty(_acceptCalls);
        Assert.DoesNotContain("sent", session.Calls);
        Assert.Null((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);

        await RunPassAsync();

        Assert.Single(_acceptCalls);
        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
    }

    // Sent 29 s after the stamp, then DELETE; the acceptance becomes visible only at stamp + 40 s + window − 1 s: a read
    // that began before shows invited and resolves nothing, the next one shows editor (after abort). Never visible: the
    // first read from stamp + 40 s + window on clears the attempt with one wish.botInvitationUnresolved.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADelayedSend_ThenDelete_IsResolvedOnlyFromTheLatestEndOfTheWait(bool takesEffect)
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Timeout());
        _acceptTakesEffect = false;
        var stamp = Now;
        var session = await SessionAsync();
        session.OnProbe = probe =>
        {
            if (probe == 4)
            {
                _clock.Advance(TimeSpan.FromSeconds(29));
            }

            return Task.FromResult(true);
        };

        await RunPassAsync(session: session);
        Assert.Equal(stamp.AddSeconds(29), Assert.Single(_acceptCalls).At);
        await DeleteBotAsync(channel);

        _clock.Advance(stamp.AddSeconds(338) - Now);
        await RunPassAsync();
        Assert.Equal(stamp, (await SettingsAsync(channel)).BotInvitationAttemptAtUtc);

        if (takesEffect)
        {
            TakeEffect(BotA, OwnerOf(channel));
        }

        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        var settings = await SettingsAsync(channel);
        Assert.Null(settings.BotInvitationAttemptAtUtc);
        Assert.Equal(takesEffect ? WishBotChannelReasons.InvitationAcceptedAfterAbort : null, settings.BotActivationError);
        Assert.Equal(takesEffect ? 1 : 0, (await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Count);
        Assert.Equal(takesEffect ? 0 : 1, (await AuditsAsync(AuditActions.WishBotInvitationUnresolved)).Count);
    }

    // K6d-8: two acceptances in two channels with 2 s spacing; the first send transaction waits 3 s for its channel lock
    // (the clock moved by the hook): the stub sees the two calls at least 2 s apart, because the spacing counts from the send.
    [Fact]
    public async Task TwoAcceptances_AreSpacedFromTheirSends()
    {
        await SeedAccountAsync(BotA);
        var first = await SeedRequestAsync();
        var second = await SeedRequestAsync();
        Editors(BotA, (first.TwitchId, "PENDING", true), (second.TwitchId, "PENDING", true));
        var session = await SessionAsync();
        session.WriteDelay = TimeSpan.FromSeconds(2);
        var hook = ClockHook((IsChannelShareLock, TimeSpan.FromSeconds(3)));
        await using var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);

        await RunPassAsync(db: db, session: session);

        Assert.Equal(2, _acceptCalls.Count);
        Assert.True(_acceptCalls[1].At - _acceptCalls[0].At >= TimeSpan.FromSeconds(2));
        Assert.Equal(2, session.Calls.Count(c => c == "sent"));
    }

    // ------------------------------------------------------------------------------------ the lock deadline

    // What the deadline sets in Postgres: before the first lock 10 s and 28 s, after it both the time left; with no time
    // left it throws without sending a statement.
    [Fact]
    public async Task TheDeadline_SetsBothTimeouts_AndSendsNothingOnceUsedUp()
    {
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);
        await using var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var deadline = new BotAccountLockDeadline(_clock);

        await deadline.ApplyAsync(db, CancellationToken.None);
        Assert.Equal(("10s", "28s"), await TimeoutsAsync(db));

        deadline.Start();
        _clock.Advance(TimeSpan.FromSeconds(15.5));
        await deadline.ApplyAsync(db, CancellationToken.None);
        Assert.Equal(("12500ms", "12500ms"), await TimeoutsAsync(db));

        _clock.Advance(TimeSpan.FromSeconds(13));
        var sent = hook.Count(_ => true);
        await Assert.ThrowsAsync<BotAccountLockDeadlineExceededException>(() => deadline.ApplyAsync(db, CancellationToken.None));
        Assert.Equal(sent, hook.Count(_ => true));
    }

    // K6d-5: the credential row is held FOR UPDATE until 16 s after the channel lock: the fence then still gets it, but a
    // write with its 10 s no longer fits before F − 3 s: nothing is sent, the attempt is taken back conditionally.
    [Fact]
    public async Task ACredentialRowHeldUntil16SecondsAfterTheLock_SendsNothing_AndTakesTheAttemptBack()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        await using var holder = fixture.CreateTaggedDbContext("wish-bot-holder", DatabaseName);
        IAsyncDisposable? held = null;
        var session = await SessionAsync();
        session.OnProbe = async probe =>
        {
            if (probe == 4)
            {
                held = await holder.Database.BeginTransactionAsync();
                await holder.Database.ExecuteSqlRawAsync("""SELECT 1 FROM "BotAccountCredentials" FOR UPDATE""");
            }

            return true;
        };
        var hook = ClockHook((IsChannelShareLock, TimeSpan.FromSeconds(16)));
        await using var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);

        var pass = RunPassAsync(db: db, session: session);
        await fixture.WaitUntilBlockedOnLockAsync(PassTag, pass);
        await held!.DisposeAsync();
        await pass.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Empty(_acceptCalls);
        Assert.Null((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
    }

    // K6e-3: the credential row is held FOR UPDATE (as long as the test likes) while a DELETE waits for the channel row:
    // the fence's statement ends at F, the transaction rolls back, the DELETE commits well within its 30 s; the take-back
    // runs into the same lock and fails, so the attempt stays open, and a later read resolves it.
    [Fact]
    public async Task AHeldCredentialRow_TheFenceEndsAtF_DeleteCommitsInTime_AndTheReadResolvesTheAttempt()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        await using var holder = fixture.CreateTaggedDbContext("wish-bot-holder", DatabaseName);
        IAsyncDisposable? held = null;
        Task? delete = null;
        var deleteTime = new Stopwatch();
        var session = await SessionAsync();
        session.OnProbe = async probe =>
        {
            if (probe == 4)
            {
                held = await holder.Database.BeginTransactionAsync();
                await holder.Database.ExecuteSqlRawAsync("""SELECT 1 FROM "BotAccountCredentials" FOR UPDATE""");
            }

            return true;
        };
        var hook = ClockHook(
            (IsChannelShareLock, TimeSpan.FromSeconds(25)),
            (IsChannelExclusiveLock, TimeSpan.FromSeconds(25)));
        hook.AfterFirst = async () =>
        {
            deleteTime.Start();
            delete = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-delete", DatabaseName);
                await DeleteBotAsync(channel, other);
                deleteTime.Stop();
            });
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-delete", delete);
        };
        await using var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);

        try
        {
            await RunPassAsync(db: db, session: session);
            await delete!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await held!.DisposeAsync();
        }

        Assert.True(deleteTime.Elapsed < TimeSpan.FromSeconds(30), $"DELETE took {deleteTime.Elapsed}.");
        Assert.Empty(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.NotNull(settings.BotInvitationAttemptAtUtc);
        Assert.Equal(WishBotChannelReasons.InvitationAcceptUnresolved, settings.BotActivationError);

        _clock.Advance(TimeSpan.FromSeconds(340));
        await RunPassAsync();

        settings = await SettingsAsync(channel);
        Assert.Equal(((DateTime?)null, (string?)null), (settings.BotInvitationAttemptAtUtc, settings.BotActivationError));
        Assert.Single(await AuditsAsync(AuditActions.WishBotInvitationUnresolved));
    }

    // K6e-3: the monotonic clock moves past F right after an Ok: nothing is booked, the transaction rolls back, the attempt
    // stays open; the next pass resolves it by the read: exactly one wish.botAcceptInvitation with resolved, no second
    // acceptance.
    [Fact]
    public async Task AnOkAfterWhichTheDeadlinePassed_BooksNothing_AndTheNextPassResolvesItOnce()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        _whileAccepting = () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        };

        await RunPassAsync();

        Assert.NotNull((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAcceptInvitation));

        _whileAccepting = null;
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync();

        Assert.Single(_acceptCalls);
        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
        Assert.Equal(new Dictionary<string, string> { ["trigger"] = "activation", ["resolved"] = "True" }, Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Details);
    }

    // K6f-1: the lock probe hangs while an acceptance is due: the kill switch and DELETE …/wishes/bot both commit within
    // 5 s, because the probe never runs under a row lock: pg_locks shows no lock of the working backend meanwhile.
    [Fact]
    public async Task AHangingLockProbe_HoldsNoRowLock_KillSwitchAndDeleteCommitAtOnce()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await SessionAsync();
        session.OnProbe = probe =>
        {
            if (probe != 4)
            {
                return Task.FromResult(true);
            }

            probeEntered.TrySetResult();
            return probeReleased.Task;
        };
        await using var db = fixture.CreateTaggedDbContext(PassTag, databaseName: DatabaseName);

        var pass = RunPassAsync(db: db, session: session);
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var commands = Stopwatch.StartNew();
        await Task.WhenAll(KillSwitchAsync(true), DeleteBotAsync(channel)).WaitAsync(TimeSpan.FromSeconds(5));
        commands.Stop();
        var rowLocks = await RowLocksOfAsync(PassTag);
        probeReleased.SetResult(true);
        await pass;

        Assert.True(commands.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(0, rowLocks);
        Assert.Empty(_acceptCalls);
        Assert.Null((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
    }

    // K6f-1, the backstop: the write stub ignores its time limit and hangs; the kill switch waits for the credential row,
    // DELETE for the channel row; Postgres ends the idle session at F, both commit within their 30 s; the attempt stays
    // open and is resolved by the read; the next pass on the same context runs normally with a new connection.
    [Fact]
    public async Task AHangingClient_TheServerEndsTheSessionAtF_WaitersCommitInTime_AndTheNextPassRunsNormally()
    {
        await SeedAccountAsync(BotA);
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        var waiters = new Stopwatch();
        _whileAccepting = async () =>
        {
            waiters.Start();
            var killSwitch = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-killswitch", DatabaseName);
                await KillSwitchAsync(true, other);
            });
            var delete = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-delete", DatabaseName);
                await DeleteBotAsync(channel, other);
            });
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-killswitch", killSwitch);
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-delete", delete);
            await Task.WhenAll(killSwitch, delete).WaitAsync(TimeSpan.FromSeconds(30));
            waiters.Stop();
        };

        // 15 s after the first lock: 13 s left, so the call (10 s) still fits and the idle timeout is 13 s.
        var hook = ClockHook((IsChannelShareLock, TimeSpan.FromSeconds(15)));
        await using var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);
        await RunPassAsync(db: db);

        Assert.True(waiters.Elapsed < TimeSpan.FromSeconds(30), $"The waiters took {waiters.Elapsed}.");
        Assert.Single(_acceptCalls);
        var settings = await SettingsAsync(channel);
        Assert.NotNull(settings.BotInvitationAttemptAtUtc);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAcceptInvitation));

        _whileAccepting = null;
        _clock.Advance(TimeSpan.FromSeconds(61));
        await RunPassAsync(db: db);

        settings = await SettingsAsync(channel);
        Assert.Equal(((DateTime?)null, WishBotChannelReasons.InvitationAcceptedAfterAbort), (settings.BotInvitationAttemptAtUtc, settings.BotActivationError));
        Assert.Single(_acceptCalls);
    }

    // ------------------------------------------------------------------------------------ the real runner: pauses

    // K6c-3: RateLimited with a Retry-After: attempt cleared and PausedUntilUtc set in the send transaction's commit, no
    // second transaction. The runner is dropped right after (a crash), a successor takes the lock: no acceptance and no
    // other call with the token before the pause ends, exactly one after it.
    [Fact]
    public async Task RateLimited_PausesInTheSendCommit_AndASuccessorHonoursThePause()
    {
        var (token, channel) = await SeedRunnerChannelAsync();
        _acceptResults.Enqueue(SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(120)));
        _acceptTakesEffect = false;
        var (runner, _) = CreateRunner(_writes);
        var pausedFrom = Now;

        Assert.Equal(BotAccountPassResult.Completed, await runner.RunPassAsync());

        Assert.Single(_acceptCalls);
        Assert.Equal(pausedFrom.AddSeconds(120), (await CredentialAsync()).PausedUntilUtc);
        Assert.Null((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        await runner.DisposeAsync();

        var (successor, _) = CreateRunner(_writes);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await successor.RunPassAsync();
        Assert.Single(_acceptCalls);
        await _writes.Received(1).VerifyAsync(token, Arg.Any<CancellationToken>());

        _acceptTakesEffect = true;
        _clock.Advance(TimeSpan.FromSeconds(60));
        await successor.RunPassAsync();

        Assert.Equal(2, _acceptCalls.Count);
        await _writes.Received(2).VerifyAsync(token, Arg.Any<CancellationToken>());
        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
    }

    // K6c-3 with K6f-4: the connection breaks before the send transaction's commit: neither the clearing nor the pause is
    // committed with it, the attempt stays open; the end of the pass books the reported pause after all, and the read
    // resolves the attempt.
    [Fact]
    public async Task ABrokenConnectionBeforeTheCommit_LeavesTheAttemptOpen_AndThePassEndBooksThePause()
    {
        var (_, channel) = await SeedRunnerChannelAsync();
        _acceptResults.Enqueue(SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(120)));
        _acceptTakesEffect = false;
        _whileAccepting = TerminatePassBackendAsync;
        await using var db = fixture.CreateTaggedDbContext(PassTag, databaseName: DatabaseName);
        var (runner, _) = CreateRunner(_writes, db);
        var reportedAt = Now;

        await runner.RunPassAsync();

        Assert.NotNull((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        Assert.Equal(reportedAt.AddSeconds(120), (await CredentialAsync()).PausedUntilUtc);

        _whileAccepting = null;
        _acceptTakesEffect = true;
        _clock.Advance(TimeSpan.FromSeconds(340));
        await runner.RunPassAsync();

        Assert.Null((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        Assert.True((await SettingsAsync(channel)).BotModeEnabled);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotInvitationUnresolved));
    }

    // K6e-4: an Ok with a Retry-After books the audit and the pause in one commit; the fresh (unauthenticated) read
    // confirms; nothing else is called with the token in that pass (the second channel's invitation waits).
    [Fact]
    public async Task AnOkWithARetryAfter_BooksAuditAndPauseTogether_TheReadConfirms_AndTheTokenRests()
    {
        var (_, channel) = await SeedRunnerChannelAsync();
        var second = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true), (second.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.Ok(TimeSpan.FromSeconds(30)));
        var (runner, _) = CreateRunner(_writes);
        var at = Now;

        await runner.RunPassAsync();

        Assert.Single(_acceptCalls);
        Assert.Equal(at.AddSeconds(30), (await CredentialAsync()).PausedUntilUtc);
        var accepted = new[] { channel, second }.Single(c => OwnerOf(c) == _acceptCalls[0].OwnerId);
        Assert.True((await SettingsAsync(accepted)).BotModeEnabled);
        Assert.Single(await AuditsAsync(AuditActions.WishBotAcceptInvitation));
    }

    // K6e-6: RateLimited with Retry-After: 1, the clock moved 2 s on: no further call with the token in this pass; the next
    // pass calls again.
    [Fact]
    public async Task AShortRateLimit_StopsTheTokenForTheRestOfThePass_TheNextPassCallsAgain()
    {
        var (_, channel) = await SeedRunnerChannelAsync();
        var second = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true), (second.TwitchId, "PENDING", true));
        _acceptResults.Enqueue(SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(1)));

        // The pause (now + 1 s) is reported in the send transaction; its settings update then moves the clock 2 s on, so
        // the booked pause is over before the second channel is looked at.
        var hook = new CommandHook(
            command => command.Contains("UPDATE \"ChannelWishSettings\"", StringComparison.Ordinal),
            occurrence =>
            {
                // 1: the attempt's stamp; 2: the send transaction's booking.
                if (occurrence == 2)
                {
                    _clock.Advance(TimeSpan.FromSeconds(2));
                }

                return Task.CompletedTask;
            });
        var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);
        var (runner, _) = CreateRunner(_writes, db);

        await runner.RunPassAsync();
        Assert.Single(_acceptCalls);
        Assert.True((await CredentialAsync()).PausedUntilUtc < Now);

        await runner.RunPassAsync();

        Assert.Equal(3, _acceptCalls.Count);
    }

    // K6f-4: Ok (and RateLimited) with a Retry-After of 120 s, the monotonic clock moved past F before the commit: rollback,
    // nothing of the acceptance booked; after the pass PausedUntilUtc is the report time + 120 s (not recomputed). The next
    // pass within the pause calls nothing with the token (no users.me, no acceptance); after it the read resolves the attempt.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARollbackAfterARetryAfter_TheEndOfThePassSecuresThePause(bool ok)
    {
        var (token, channel) = await SeedRunnerChannelAsync();
        _acceptResults.Enqueue(ok ? SevenTvWriteResult.Ok(TimeSpan.FromSeconds(120)) : SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(120)));
        _acceptTakesEffect = false;
        _whileAccepting = () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        };
        var (runner, _) = CreateRunner(_writes);
        var reportedAt = Now.AddSeconds(30);

        await runner.RunPassAsync();

        Assert.Equal(reportedAt.AddSeconds(120), (await CredentialAsync()).PausedUntilUtc);
        Assert.NotNull((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAcceptInvitation));

        _whileAccepting = null;
        _clock.Advance(TimeSpan.FromSeconds(61));
        _writes.ClearReceivedCalls();
        await runner.RunPassAsync();
        await _writes.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
        Assert.Single(_acceptCalls);

        if (ok)
        {
            TakeEffect(BotA, OwnerOf(channel));
        }

        _acceptTakesEffect = true;
        _clock.Advance(TimeSpan.FromSeconds(340));
        await runner.RunPassAsync();

        Assert.Null((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);
        Assert.Equal(ok ? 1 : 0, (await AuditsAsync(AuditActions.WishBotAcceptInvitation)).Count(a => a.Details.ContainsKey("resolved")));
        _ = token;
    }

    // ------------------------------------------------------------------------------------ helpers (races)

    // A valid sealed token of bot A for the real runner, verified by the stub, and a request with an invitation.
    private async Task<(string Token, Seeded Channel)> SeedRunnerChannelAsync()
    {
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        var token = Jwt(Now.AddDays(30));
        await SeedSealedAccountAsync(sealer.Seal(token));
        _writes.VerifyAsync(token, Arg.Any<CancellationToken>()).Returns(SevenTvWriteResult<string>.Ok(BotA));
        var channel = await SeedRequestAsync();
        Editors(BotA, (channel.TwitchId, "PENDING", true));
        return (token, channel);
    }

    private async Task KillSwitchAsync(bool enabled, AppDbContext? db = null)
    {
        var context = db ?? CreateDb();
        try
        {
            var service = new BotAccountService(
                context,
                Substitute.For<IBotAccountTokenSealer>(),
                new BotAccountOptions { Enabled = true },
                Substitute.For<IRedisPublisher>(),
                _clock,
                NullLogger<BotAccountService>.Instance);
            await service.SetKillSwitchAsync(enabled, new AuditActor("900001", "admin"));
        }
        finally
        {
            if (db is null)
            {
                await context.DisposeAsync();
            }
        }
    }

    private async Task TerminatePassBackendAsync()
    {
        await using var db = CreateDb();
        // Only the working connection, idle in its send transaction during the call; the runner's lock connection carries
        // the same name but is idle outside a transaction.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = {PassTag} AND state = 'idle in transaction'");
    }

    private async Task<int> RowLocksOfAsync(string applicationName)
    {
        await using var db = CreateDb();
        return await db.Database.SqlQuery<int>(
                $"""
                 SELECT count(*)::int AS "Value" FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                 WHERE a.application_name = {applicationName} AND l.mode IN ('RowShareLock', 'RowExclusiveLock')
                 """)
            .SingleAsync();
    }

    private static async Task<(string Statement, string Idle)> TimeoutsAsync(AppDbContext db)
    {
        var statement = await db.Database.SqlQuery<string>($"""SELECT current_setting('statement_timeout') AS "Value" """).SingleAsync();
        var idle = await db.Database.SqlQuery<string>($"""SELECT current_setting('idle_in_transaction_session_timeout') AS "Value" """).SingleAsync();
        return (statement, idle);
    }

    private static bool IsChannelShareLock(string command) =>
        command.Contains("FROM \"Channels\"", StringComparison.Ordinal) && command.Contains("FOR SHARE", StringComparison.Ordinal);

    private static bool IsChannelExclusiveLock(string command) =>
        command.Contains("FROM \"Channels\"", StringComparison.Ordinal) && command.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal);

    // Moves the test clock by each step's amount right after the first set_config that follows a command of the step's
    // kind (the step's lock), in order: the deadline started with that lock, so the rest Postgres enforces is shortened.
    private StepClockHook ClockHook(params (Func<string, bool> AfterLock, TimeSpan By)[] steps) => new(_clock, steps);

    private sealed class StepClockHook : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _clock;
        private readonly Queue<(Func<string, bool> AfterLock, TimeSpan By)> _steps;
        private bool _armed;
        private bool _first = true;

        public StepClockHook(Microsoft.Extensions.Time.Testing.FakeTimeProvider clock, (Func<string, bool>, TimeSpan)[] steps)
        {
            _clock = clock;
            _steps = new Queue<(Func<string, bool>, TimeSpan)>(steps);
        }

        // Runs once, right after the first step moved the clock, inside the caller's transaction.
        public Func<Task>? AfterFirst { get; set; }

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await StepAsync(command.CommandText);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            await StepAsync(command.CommandText);
            return result;
        }

        private async Task StepAsync(string text)
        {
            if (_steps.Count == 0)
            {
                return;
            }

            var (afterLock, by) = _steps.Peek();
            if (!_armed)
            {
                _armed = afterLock(text);
                return;
            }

            if (!text.Contains("set_config", StringComparison.Ordinal))
            {
                return;
            }

            _steps.Dequeue();
            _armed = false;
            _clock.Advance(by);
            if (_first && AfterFirst is { } after)
            {
                _first = false;
                await after();
            }

            _first = false;
        }
    }
}
