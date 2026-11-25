using System.Diagnostics;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using EmotePurge.Infrastructure.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The races, deadlines and account reports of a removal (#487 plan 2.5a, 2.7; T10, revisions 6a to 6f) against real
// Postgres: manager commands and DELETE …/wishes/bot around the transactions, a human replacement while the send
// transaction waits for its lock, a hand PUT, the lock deadline (a paged read cut at its time left, a deadline that ends
// after the send, a client that ignores its time limit), the stuck order with its generation and epoch, and, with the real
// runner, the Unauthorized over two passes and the pauses. A paged read is played on the test clock by PlayPages, which
// applies the client's own rule (the smaller of 10 s and the time left per page, none under 1 s, a cut is Unavailable).
public partial class WishBotPassTests
{
    // ------------------------------------------------------------------------------------ AK83: manager commands

    // extend after the selection and before transaction 1 (the version moved, the queue is gone): no removeEmote. extend
    // against the send transaction's held lock: it waits and then answers 409 (the attempt is open). extend between the
    // send and the read after it: 409 at once. In both later cases the removal completes.
    [Theory]
    [InlineData("before")]
    [InlineData("held")]
    [InlineData("between")]
    public async Task ExtendAroundTheRemoval_BeforeIsTakenUp_DuringAndAfterTheSendIs409(string when)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented);
        var session = await SessionAsync();
        WishOutcome? outcome = null;
        Task<WishCommandResult>? waiting = null;
        if (when == "before")
        {
            session.OnWait = async _ =>
            {
                await using var db = CreateDb();
                outcome = (await WishSut(db).ExtendAsync(channel.Name, id, Manager)).Outcome;
            };
        }
        else
        {
            _whileReading = async number =>
            {
                if (when == "held" && number == 2)
                {
                    waiting = Task.Run(async () =>
                    {
                        await using var db = fixture.CreateTaggedDbContext("wish-bot-extend", DatabaseName);
                        return await WishSut(db).ExtendAsync(channel.Name, id, Manager);
                    });
                    await fixture.WaitUntilBlockedOnLockAsync("wish-bot-extend", waiting);
                }

                if (when == "between" && number == 3)
                {
                    await using var db = CreateDb();
                    outcome = (await WishSut(db).ExtendAsync(channel.Name, id, Manager)).Outcome;
                }
            };
        }

        await RunPassAsync(session: session);
        if (waiting is not null)
        {
            outcome = (await waiting.WaitAsync(TimeSpan.FromSeconds(15))).Outcome;
        }

        if (when == "before")
        {
            Assert.Equal(WishOutcome.Success, outcome);
            Assert.Empty(_removeCalls);
            Assert.Equal(WishStatus.Active, (await LoadAsync(id)).Status);
            return;
        }

        Assert.Equal(WishOutcome.StateConflict, outcome);
        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Single(_removeCalls);
    }

    // ------------------------------------------------------------------------------------ S6-5: the send transaction waits

    // Transaction 1 committed attempt and evidence; the test holds the channel row FOR UPDATE (as the full sync does) and,
    // while the send transaction waits for it, "7TV" changes: a human replacement of the same alias (P5: the owner, a new
    // addedAt) → no removeEmote, ownership_lost; only a second alias of the emote comes in → nothing sent, the attempt
    // taken back, the queue stays and the next pass records new evidence and removes; the entry gone → removed_externally;
    // the fresh read fails → taken back, nothing sent.
    [Theory]
    [InlineData("replaced")]
    [InlineData("secondAlias")]
    [InlineData("gone")]
    [InlineData("unreadable")]
    public async Task AChangeWhileTheSendTransactionWaits_IsSeenByTheFreshRead(string change)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        await using var holder = fixture.CreateTaggedDbContext("wish-bot-holder", DatabaseName);
        await using var db = fixture.CreateTaggedDbContext(PassTag, databaseName: DatabaseName);
        var session = await SessionAsync();
        Task? release = null;
        var holding = false;
        session.OnProbe = async _ =>
        {
            // The probe right after transaction 1 committed the attempt.
            if (!holding && _readCalls.Count == 1)
            {
                holding = true;
                var transaction = await holder.Database.BeginTransactionAsync();
                await holder.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""");
                release = Task.Run(async () =>
                {
                    await WaitUntilPassBlockedAsync();
                    Change(change, emote);
                    await transaction.CommitAsync();
                    await transaction.DisposeAsync();
                });
            }

            return true;
        };

        await RunPassAsync(db: db, session: session);
        await release!.WaitAsync(TimeSpan.FromSeconds(15));

        var wish = await LoadAsync(id);
        Assert.Empty(_removeCalls);
        Assert.Null(wish.BotRemoveAtUtc);
        switch (change)
        {
            case "replaced":
                Assert.Equal((WishStatus.Presented, WishStatusReason.OwnershipLost, false), (wish.Status, wish.StatusReason, wish.BotOwned));
                break;
            case "gone":
                Assert.Equal(WishStatus.RemovedExternally, wish.Status);
                break;
            default:
                Assert.Equal((WishStatus.Active, true), (wish.Status, wish.BotOwned));
                Assert.NotNull(wish.RemovalQueuedAtUtc);
                Assert.Null(wish.BotRemoveEvidence);
                break;
        }

        if (change != "secondAlias")
        {
            return;
        }

        _readStub = null;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Equal("Alias2", Assert.Single(Set(SetA)).Alias);
        Assert.Single(_removeCalls);
    }

    // The send transaction holds the waiting full sync with its two calls (9 s each on the test clock) well under 30 s;
    // the read after it runs without the channel lock, so the sync is through by then.
    [Fact]
    public async Task TheSendTransaction_HoldsTheSyncBriefly_AndTheReadAfterItLetsItThrough()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        Task? sync = null;
        var syncTime = new Stopwatch();
        var syncDoneAtTheReadAfter = false;
        _whileReading = async number =>
        {
            if (number == 2)
            {
                syncTime.Start();
                sync = Task.Run(async () =>
                {
                    await using var other = fixture.CreateTaggedDbContext("wish-bot-sync", DatabaseName);
                    await using var transaction = await other.Database.BeginTransactionAsync();
                    await other.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""");
                    await transaction.CommitAsync();
                    syncTime.Stop();
                });
                await fixture.WaitUntilBlockedOnLockAsync("wish-bot-sync", sync);
                _clock.Advance(TimeSpan.FromSeconds(9));
            }

            if (number == 3)
            {
                await sync!.WaitAsync(TimeSpan.FromSeconds(15));
                syncDoneAtTheReadAfter = true;
            }
        };
        _whileRemoving = () =>
        {
            _clock.Advance(TimeSpan.FromSeconds(9));
            return Task.CompletedTask;
        };

        await RunPassAsync();

        Assert.True(syncDoneAtTheReadAfter);
        Assert.True(syncTime.Elapsed < TimeSpan.FromSeconds(30), $"The sync took {syncTime.Elapsed}.");
        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
    }

    // ------------------------------------------------------------------------------------ S6-2: consent to another account

    // A hand PUT to B, already an editor in the channel, with a queued active wish owned under A: no removeEmote, the
    // channel bot_account_changed, the fallback presents it (bot_unavailable), BotOwned stays. Between transaction 1 and 2
    // the re-check refuses (the credential changed): the attempt is taken back, the pass ends.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandPutToAnotherAccount_RemovesNothing(bool betweenTheTransactions)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        Editors(BotB, (channel.TwitchId, "ACCEPTED", true));
        var session = await SessionAsync();
        if (betweenTheTransactions)
        {
            var done = false;
            session.OnProbe = async _ =>
            {
                if (!done && _readCalls.Count == 1)
                {
                    done = true;
                    await SeedAccountAsync(BotB, identity: Identity + 1);
                }

                return true;
            };
        }
        else
        {
            await SeedAccountAsync(BotB, identity: Identity + 1);
            session = await SessionAsync();
        }

        await RunPassAsync(session: session);

        Assert.Empty(_removeCalls);
        var wish = await LoadAsync(id);
        Assert.Null(wish.BotRemoveAtUtc);
        Assert.True(wish.BotOwned);
        if (!betweenTheTransactions)
        {
            Assert.Equal(WishBotChannelReasons.BotAccountChanged, (await SettingsAsync(channel)).BotSuspendedReason);
            Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(wish));
        }
    }

    // ------------------------------------------------------------------------------------ K6d-5, K6e-5: reads under the locks

    // Transaction 2, a pre-read over three pages of 9 s each while PUT /kill-switch and DELETE …/wishes/bot wait for its
    // locks: page 2 is cut after the 6 s left (budget end minus the write's 10 s), no page 3, no removeEmote, the attempt
    // taken back; both waiters commit without a timeout, and the next pass sends nothing.
    [Fact]
    public async Task APagedReadUnderTheSendTransaction_IsCutAtItsTimeLeft_AndTheWaitersCommit()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented);
        var pages = new List<int>();
        _readStub = (number, setId, timeLeft) => number == 2 ? PlayPages(timeLeft, 3, TimeSpan.FromSeconds(9), pages) : null;
        Task? killSwitch = null;
        Task? delete = null;
        var waiters = new Stopwatch();
        _whileReading = async number =>
        {
            if (number != 2)
            {
                return;
            }

            waiters.Start();
            killSwitch = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-killswitch", DatabaseName);
                await KillSwitchAsync(true, other);
            });
            delete = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-delete", DatabaseName);
                await DeleteBotAsync(channel, other);
            });
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-killswitch", killSwitch);
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-delete", delete);
        };

        await RunPassAsync();
        await Task.WhenAll(killSwitch!, delete!).WaitAsync(TimeSpan.FromSeconds(30));
        waiters.Stop();

        Assert.True(waiters.Elapsed < TimeSpan.FromSeconds(30), $"The waiters took {waiters.Elapsed}.");
        Assert.Equal(new[] { 2 }, pages);
        Assert.Empty(_removeCalls);
        Assert.Null((await LoadAsync(id)).BotRemoveAtUtc);

        _whileReading = null;
        _readStub = null;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();
        Assert.Empty(_removeCalls);
    }

    // Transaction 1 with a set over ten pages and slow answers: the read ends at the budget end, nothing is committed,
    // the queue stays.
    [Fact]
    public async Task ASlowLongReadInTransaction1_CommitsNothing_AndTheQueueStays()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented);
        var pages = new List<int>();
        _readStub = (_, _, timeLeft) => PlayPages(timeLeft, 10, TimeSpan.FromSeconds(9), pages);

        await RunPassAsync();

        Assert.Equal(new[] { 3 }, pages);
        var wish = await LoadAsync(id);
        Assert.Equal(((DateTime?)null, (string?)null), (wish.BotRemoveAtUtc, wish.BotRemoveEvidence));
        Assert.NotNull(wish.RemovalQueuedAtUtc);
        Assert.Empty(_removeCalls);
    }

    // Sol's case (K6e-5): a valid set over two pages, page 1 6 s and page 2 1 s: read completely in both transactions,
    // removeEmote sent, removed in the same pass.
    [Fact]
    public async Task TwoPagesOfSixAndOneSecond_AreReadCompletely_AndTheWishIsRemoved()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        _readStub = (number, _, timeLeft) => number <= 2 ? TwoPagesSixAndOne(timeLeft) : null;

        await RunPassAsync();

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Single(_removeCalls);
    }

    // Plan 2.5 (a proof at most 1 minute old at the write), Opus P3-1: transaction 1 ages the proof past
    // EditorProofRefreshAge (29 s old at the intent, a read of 24 s). It is refreshed between the two transactions, so it
    // is still within its minute when removeEmote goes out after the send transaction's fresh read of 10 s.
    [Fact]
    public async Task AProofAgedByTransaction1_IsRefreshedBeforeTheSend()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        _readStub = (number, _, timeLeft) => number switch
        {
            1 => PlayPages(timeLeft, 3, TimeSpan.FromSeconds(8)),
            2 => PlayPages(timeLeft, 1, TimeSpan.FromSeconds(10)),
            _ => null,
        };
        TimeSpan? proofAgeAtSend = null;
        _whileRemoving = async () => proofAgeAtSend = Now - (await SettingsAsync(channel)).BotEditorCheckedAtUtc!.Value;
        var session = await SessionAsync();
        session.OnWait = _ =>
        {
            _clock.Advance(TimeSpan.FromSeconds(29));
            return Task.CompletedTask;
        };

        await RunPassAsync(session: session);

        Assert.Single(_removeCalls);
        Assert.InRange(proofAgeAtSend!.Value, TimeSpan.Zero, WishBotPreflight.EditorProofFreshness);
        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
    }

    // The guard at the write: the refresh is not possible (the editor read unavailable), transaction 1 waited 10 s for its
    // wish lock and the send transaction 10 s for its channel row; the re-check still passes (49 s), but the fresh read of
    // 15 s takes the proof past its minute: nothing is sent, and the attempt goes back with the queue kept.
    [Fact]
    public async Task AProofTheFreshReadAgesPastItsMinute_SendsNothing()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        _readStub = (number, _, timeLeft) => number == 2 ? PlayPages(timeLeft, 2, TimeSpan.FromSeconds(7.5)) : null;
        var hook = new CommandHook(
            command => IsWishLock(command) || (command.Contains("FROM \"Channels\"", StringComparison.Ordinal) && command.Contains("FOR SHARE", StringComparison.Ordinal)),
            occurrence =>
            {
                if (occurrence <= 2)
                {
                    _clock.Advance(TimeSpan.FromSeconds(10));
                }

                return Task.CompletedTask;
            });
        await using var db = fixture.CreateTaggedDbContext("wish-bot-proof-guard", [hook], databaseName: DatabaseName);
        var session = await SessionAsync();
        session.OnWait = _ =>
        {
            _clock.Advance(TimeSpan.FromSeconds(29));
            _editors.Remove(BotA);
            return Task.CompletedTask;
        };

        await RunPassAsync(db: db, session: session);

        Assert.Empty(_removeCalls);
        Assert.Equal(2, _readCalls.Count);
        var wish = await LoadAsync(id);
        Assert.Equal(((DateTime?)null, WishStatus.Active), (wish.BotRemoveAtUtc, wish.Status));
        Assert.NotNull(wish.RemovalQueuedAtUtc);
    }

    // The stuck order (K6e-5): pages of 9 s in every pass, so every send transaction's pre-read is cut and nothing is ever
    // sent. Three aborts within 2 minutes: still queued. Once the first is 15 minutes old: presented (bot_unavailable),
    // the queue empty, one wish.changed, no audit. The same with a presented wish ordered by bot-remove: the order goes,
    // the reason stays; a new bot-remove queues it again and the count starts over.
    [Theory]
    [InlineData(WishStatus.Active)]
    [InlineData(WishStatus.Presented)]
    public async Task AStuckOrder_IsPresentedAfterThreeAborts_TheFirstAtLeast15MinutesOld(string status)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, status);
        _readStub = (_, _, timeLeft) => PlayPages(timeLeft, 2, TimeSpan.FromSeconds(9));
        var (db, pass) = KeptPass();

        for (var i = 0; i < 3; i++)
        {
            await RunKeptAsync(db, pass);
            _clock.Advance(TimeSpan.FromSeconds(50));
        }

        Assert.NotNull((await LoadAsync(id)).RemovalQueuedAtUtc);
        var events = EventsFor(channel);
        _clock.Advance(TimeSpan.FromMinutes(13));
        await RunKeptAsync(db, pass);

        var wish = await LoadAsync(id);
        Assert.Equal(
            (WishStatus.Presented, status == WishStatus.Active ? WishStatusReason.BotUnavailable : WishStatusReason.Protected),
            StatusOf(wish));
        Assert.Equal(((DateTime?)null, (DateTime?)null), (wish.RemovalQueuedAtUtc, wish.BotRemoveAtUtc));
        Assert.Equal(events + 1, EventsFor(channel));
        Assert.Empty(_removeCalls);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotRemove));
        if (status == WishStatus.Active)
        {
            return;
        }

        await using (var other = CreateDb())
        {
            Assert.Equal(WishOutcome.Success, (await WishSut(other).RequestBotRemovalAsync(channel.Name, id, Manager)).Outcome);
        }

        _clock.Advance(TimeSpan.FromMinutes(20));
        await RunKeptAsync(db, pass);
        Assert.NotNull((await LoadAsync(id)).RemovalQueuedAtUtc);
    }

    // What ends a count: two aborts, then an Unauthorized on page 1 (it does not count and resets), or a restart of the
    // runner (a new wish part): two more aborts, the first of the old row 30 minutes back, present nothing; the third of
    // the new row, 16 minutes after its first, does.
    [Theory]
    [InlineData("unauthorized")]
    [InlineData("restart")]
    public async Task AnUnauthorizedOrARestart_StartsTheCountAgain(string between)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var unauthorized = false;
        _readStub = (_, _, timeLeft) => unauthorized
            ? SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unauthorized())
            : PlayPages(timeLeft, 2, TimeSpan.FromSeconds(9));
        var (db, pass) = KeptPass();
        await RunKeptAsync(db, pass);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunKeptAsync(db, pass);

        if (between == "unauthorized")
        {
            unauthorized = true;
            await RunKeptAsync(db, pass);
            unauthorized = false;
        }
        else
        {
            (db, pass) = KeptPass();
        }

        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunKeptAsync(db, pass);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunKeptAsync(db, pass);
        Assert.Equal(WishStatus.Active, (await LoadAsync(id)).Status);

        _clock.Advance(TimeSpan.FromMinutes(6));
        await RunKeptAsync(db, pass);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(await LoadAsync(id)));
    }

    // K6f-2, Sol's case: three aborts over 15 minutes for (V, t1); between the third and the presentation a manager's
    // threshold change moves the version (V+1, queue empty) and the expiry run queues again (V+1, t2): the presentation
    // hits nothing, the wish stays active and queued, no wish.changed, and the count for (V+1, t2) starts at zero.
    [Fact]
    public async Task AGenerationChangedBeforeThePresentation_HitsNothing_AndTheNewOneCountsFromZero()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        _readStub = (_, _, timeLeft) => PlayPages(timeLeft, 2, TimeSpan.FromSeconds(9));

        // Opened for the third pass: its third wish lock is the presentation's (transaction 1, transaction 2, then it).
        var requeuedAt = Now.AddMinutes(16);
        var hook = new CommandHook(IsWishLock, async occurrence =>
        {
            if (occurrence == 3)
            {
                await using var other = CreateDb();
                await other.Wishes.Where(w => w.Id == id).ExecuteUpdateAsync(u => u
                    .SetProperty(w => w.DecisionVersion, SeededVersion + 1)
                    .SetProperty(w => w.RemovalQueuedVersion, SeededVersion + 1)
                    .SetProperty(w => w.RemovalQueuedAtUtc, requeuedAt));
            }
        });
        var gate = new Gate(hook);
        var (db, pass) = KeptPass(gate);
        await RunKeptAsync(db, pass);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunKeptAsync(db, pass);
        _clock.Advance(TimeSpan.FromMinutes(6));
        var events = EventsFor(channel);

        gate.Open = true;
        await RunKeptAsync(db, pass);
        gate.Open = false;

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, requeuedAt), (wish.Status, wish.RemovalQueuedAtUtc!.Value));
        Assert.Equal(events, EventsFor(channel));
        Assert.Equal(3, hook.Count(IsWishLock));

        // The new generation: two more aborts, even 15 minutes on, do not present it.
        _clock.Advance(TimeSpan.FromMinutes(16));
        await RunKeptAsync(db, pass);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await RunKeptAsync(db, pass);
        Assert.Equal(WishStatus.Active, (await LoadAsync(id)).Status);
    }

    // K6f-3: the same wish part under epoch E counts two aborts; a second runner takes over (E+1) and counts its own; the
    // first instance takes the lock again (E+2): one more abort does not present, only three under E+2 with the first of
    // them 15 minutes old.
    [Fact]
    public async Task AnotherEpoch_DropsTheCount_EvenOfTheSameInstance()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        _readStub = (_, _, timeLeft) => PlayPages(timeLeft, 2, TimeSpan.FromSeconds(9));
        var (db, pass) = KeptPass();
        await RunKeptAsync(db, pass);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunKeptAsync(db, pass);

        await SetEpochAsync(Epoch + 1);
        var (secondDb, second) = KeptPass();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunKeptAsync(secondDb, second, epoch: Epoch + 1);

        await SetEpochAsync(Epoch + 2);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await RunKeptAsync(db, pass, epoch: Epoch + 2);
        Assert.Equal(WishStatus.Active, (await LoadAsync(id)).Status);

        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunKeptAsync(db, pass, epoch: Epoch + 2);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await RunKeptAsync(db, pass, epoch: Epoch + 2);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(await LoadAsync(id)));
    }

    // An abort whose take-back failed leaves the attempt open: no presentation (2.5a (e) applies), even on the third abort
    // 15 minutes after the first.
    [Fact]
    public async Task AnAbortWithAnOpenAttempt_IsNotPresented()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        int? thirdPassFrom = null;
        _readStub = (number, _, timeLeft) =>
        {
            if (thirdPassFrom is not { } from)
            {
                return PlayPages(timeLeft, 2, TimeSpan.FromSeconds(9));
            }

            // The fresh read of transaction 2 runs past F: the transaction expires before it can take the attempt back.
            if (number == from + 2)
            {
                _clock.Advance(TimeSpan.FromSeconds(29));
            }

            return null;
        };

        // Opened for the third pass: the take-back of its own meets a wish row the test holds, with 1 s of its deadline left.
        await using var holder = fixture.CreateTaggedDbContext("wish-bot-holder", DatabaseName);
        IAsyncDisposable? held = null;
        var trigger = new CommandHook(IsChannelShareLock, async occurrence =>
        {
            if (occurrence == 2)
            {
                held = await holder.Database.BeginTransactionAsync();
                await holder.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Wishes" WHERE "Id" = {id} FOR UPDATE""");
            }

            // The presentation's channel lock: the row is free again before it asks for the wish.
            if (occurrence == 3 && held is not null)
            {
                await held.DisposeAsync();
                held = null;
            }
        });
        var gate = new Gate(trigger, ClockHook((IsChannelShareLock, TimeSpan.Zero), (IsChannelShareLock, TimeSpan.FromSeconds(27))));
        var (db, pass) = KeptPass(gate);
        await RunKeptAsync(db, pass);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await RunKeptAsync(db, pass);
        _clock.Advance(TimeSpan.FromMinutes(6));
        var events = EventsFor(channel);

        thirdPassFrom = _readCalls.Count;
        gate.Open = true;
        try
        {
            await RunKeptAsync(db, pass);
        }
        finally
        {
            gate.Open = false;
            if (held is not null)
            {
                await held.DisposeAsync();
            }
        }

        var wish = await LoadAsync(id);
        Assert.Equal(WishStatus.Active, wish.Status);
        Assert.NotNull(wish.BotRemoveAtUtc);
        Assert.Equal(events, EventsFor(channel));
        Assert.Empty(_removeCalls);
    }

    // ------------------------------------------------------------------------------------ K6e-3, K6f-1: real Postgres

    // K6e-3: removeEmote (9 s on the test clock) is the latest send that fits; then the monotonic clock passes F before the
    // booking: rollback, nothing booked, the waiting full sync and PUT /kill-switch commit within their 30 s, the attempt
    // stays open; the next pass resolves it by the read (resumed), without a second removeEmote.
    [Fact]
    public async Task ADeadlinePassedAfterTheSend_BooksNothing_AndTheNextPassResumes()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        Task? sync = null;
        Task? killSwitch = null;
        var waiters = new Stopwatch();
        _whileRemoving = async () =>
        {
            waiters.Start();
            sync = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-sync", DatabaseName);
                await using var transaction = await other.Database.BeginTransactionAsync();
                await other.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""");
                await transaction.CommitAsync();
            });
            killSwitch = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-killswitch", DatabaseName);
                await KillSwitchAsync(true, other);
            });
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-sync", sync);
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-killswitch", killSwitch);
            _clock.Advance(TimeSpan.FromSeconds(9));
            _clock.Advance(BotAccountLockDeadline.Span);
        };

        await RunPassAsync();
        await Task.WhenAll(sync!, killSwitch!).WaitAsync(TimeSpan.FromSeconds(30));
        waiters.Stop();

        Assert.True(waiters.Elapsed < TimeSpan.FromSeconds(30), $"The waiters took {waiters.Elapsed}.");
        var wish = await LoadAsync(id);
        Assert.Equal(WishStatus.Active, wish.Status);
        Assert.NotNull(wish.BotRemoveAtUtc);
        Assert.Equal(2, _readCalls.Count);

        _whileRemoving = null;
        await KillSwitchAsync(false);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Equal("resumed", Assert.Single(await AuditsAsync(AuditActions.WishBotRemove)).Details["outcome"]);
        Assert.Single(_removeCalls);
    }

    // K6f-1, the backstop: removeEmote ignores its time limit and hangs; the full sync and PUT /kill-switch wait; Postgres
    // ends the idle session at F (13 s left at the send), both commit before their 30 s; the attempt stays open; the next
    // pass on the same context resolves it by the read, without a second removeEmote.
    [Fact]
    public async Task AHangingRemoval_TheServerEndsTheSessionAtF_AndTheNextPassResumes()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);

        // The fresh read of the send transaction takes 15 s (two pages): 13 s are left at the send, its 10 s still fit.
        _readStub = (number, _, timeLeft) => number == 2 ? PlayPages(timeLeft, 2, TimeSpan.FromSeconds(7.5)) : null;
        var waiters = new Stopwatch();
        _whileRemoving = async () =>
        {
            waiters.Start();
            var sync = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-sync", DatabaseName);
                await using var transaction = await other.Database.BeginTransactionAsync();
                await other.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""");
                await transaction.CommitAsync();
            });
            var killSwitch = Task.Run(async () =>
            {
                await using var other = fixture.CreateTaggedDbContext("wish-bot-killswitch", DatabaseName);
                await KillSwitchAsync(true, other);
            });
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-sync", sync);
            await fixture.WaitUntilBlockedOnLockAsync("wish-bot-killswitch", killSwitch);
            await Task.WhenAll(sync, killSwitch).WaitAsync(TimeSpan.FromSeconds(30));
            waiters.Stop();
        };
        await using var db = fixture.CreateTaggedDbContext(PassTag, databaseName: DatabaseName);

        await RunPassAsync(db: db);

        Assert.True(waiters.Elapsed < TimeSpan.FromSeconds(30), $"The waiters took {waiters.Elapsed}.");
        Assert.NotNull((await LoadAsync(id)).BotRemoveAtUtc);
        Assert.Equal(2, _readCalls.Count);

        _whileRemoving = null;
        _readStub = null;
        await KillSwitchAsync(false);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync(db: db);

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Single(_removeCalls);
    }

    // K6f-1: the lock connection is ended during removeEmote: the send transaction ends under its fence as usual (Ok,
    // nothing more), the probe before transaction 3 reports the lock lost: no read after it, no booking, the attempt
    // open; the successor resolves it by the read.
    [Fact]
    public async Task ALockLostDuringTheRemoval_NoReadAfterIt_TheSuccessorResumes()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var session = await SessionAsync();
        session.OnProbe = _ => Task.FromResult(_removeCalls.Count == 0);

        await RunPassAsync(session: session);

        Assert.Equal(2, _readCalls.Count);
        Assert.NotNull((await LoadAsync(id)).BotRemoveAtUtc);

        await SetEpochAsync(Epoch + 1);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync(session: await SessionAsync(epoch: Epoch + 1));

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Single(_removeCalls);
    }

    // ------------------------------------------------------------------------------------ 2.5a (i): reports to the session

    // K6c-3: removeEmote RateLimited → the attempt cleared, the queue stays, the pause joined to the send commit, and no
    // other call with the token in the pass. The repair RateLimited → wish.botRepair rate_limited, phase and evidence
    // empty, the pause joined to the repair's commit.
    [Theory]
    [InlineData("remove")]
    [InlineData("repair")]
    public async Task RateLimited_IsBookedWithThePauseInTheSendCommit(string call)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var next = await SeedOwnedAsync(channel, WishStatus.Active, queuedMinutesAgo: 0);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        var limited = SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(120));
        if (call == "remove")
        {
            _removeAnswer = _ => limited;
        }
        else
        {
            Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now.AddDays(-20), Owner));
            _removeAnswer = c =>
            {
                Set(c.SetId).RemoveAll(e => e.EmoteId == c.EmoteId);
                return SevenTvWriteResult.Ok();
            };
            _addAnswer = _ => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(limited);
        }

        var session = await SessionAsync();
        await RunPassAsync(session: session);

        Assert.Equal(["pause"], session.Calls.Where(c => c.StartsWith("pause", StringComparison.Ordinal)));
        Assert.Single(_removeCalls);
        Assert.NotNull((await LoadAsync(next)).RemovalQueuedAtUtc);
        var wish = await LoadAsync(id);
        if (call == "remove")
        {
            Assert.Equal((WishStatus.Active, (DateTime?)null, (string?)null), (wish.Status, wish.BotRemoveAtUtc, wish.BotRemoveEvidence));
            Assert.NotNull(wish.RemovalQueuedAtUtc);
            return;
        }

        Assert.Equal(((DateTime?)null, (string?)null), (wish.BotRepairAtUtc, wish.BotRemoveEvidence));
        Assert.Equal("rate_limited", Assert.Single(await AuditsAsync(AuditActions.WishBotRepair)).Details["outcome"]);
    }

    // K6d-6: the fresh read in transaction 2 answers Unauthorized (on its second page): the attempt taken back,
    // ReportUnauthorized, the next queued wish not tried; a disguised 429: the pause joined to the take-back's commit. The
    // read after the send and the read of a repair phase with a 429: the pause booked on its own, attempt or phase open.
    [Theory]
    [InlineData("unauthorized")]
    [InlineData("rateLimited")]
    [InlineData("readAfter")]
    [InlineData("repairCheck")]
    public async Task EveryReadReportsToTheSession(string where)
    {
        var channel = await SeedBotChannelAsync();
        long id;
        var limited = SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(60)));
        if (where == "repairCheck")
        {
            (id, _) = await SeedPhaseAsync(channel, WishStatus.Removed, SetA);
            _readStub = (_, _, _) => limited;
        }
        else
        {
            id = await SeedOwnedAsync(channel, WishStatus.Active);
            _readStub = (number, _, _) => (where, number) switch
            {
                ("unauthorized", 2) => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unauthorized()),
                ("rateLimited", 2) => limited,
                ("readAfter", 3) => limited,
                _ => null,
            };
        }

        var next = await SeedOwnedAsync(channel, WishStatus.Active, queuedMinutesAgo: 0);
        var session = await SessionAsync();

        await RunPassAsync(session: session);

        var wish = await LoadAsync(id);
        var pauses = session.Calls.Where(c => c.StartsWith("pause", StringComparison.Ordinal)).ToList();
        Assert.Null((await LoadAsync(next)).BotRemoveAtUtc);
        Assert.NotNull((await LoadAsync(next)).RemovalQueuedAtUtc);
        switch (where)
        {
            case "unauthorized":
                Assert.Contains("unauthorized", session.Calls);
                Assert.Empty(pauses);
                Assert.Null(wish.BotRemoveAtUtc);
                Assert.Empty(_removeCalls);
                break;
            case "rateLimited":
                Assert.Equal(["pause"], pauses);
                Assert.Null(wish.BotRemoveAtUtc);
                Assert.Empty(_removeCalls);
                break;
            case "readAfter":
                Assert.Equal(["pause-alone"], pauses);
                Assert.NotNull(wish.BotRemoveAtUtc);
                Assert.Single(_removeCalls);
                break;
            default:
                Assert.Equal(["pause-alone"], pauses);
                Assert.NotNull(wish.BotRepairAtUtc);
                break;
        }
    }

    // K6e-4: removeEmote Ok with a RetryAfter: the pause joined to transaction 2's commit, no read after it in this pass;
    // the next pass after the pause resumes it (removed, outcome resumed). The repair Ok with a RetryAfter: wish.botRepair
    // ok and the pause in one commit.
    [Theory]
    [InlineData("remove")]
    [InlineData("repair")]
    public async Task AnOkWithARetryAfter_BooksThePauseWithTheOutcome(string call)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        if (call == "remove")
        {
            _removeAnswer = c =>
            {
                Set(c.SetId).RemoveAll(e => e.EmoteId == c.EmoteId);
                return SevenTvWriteResult.Ok(TimeSpan.FromSeconds(30));
            };
        }
        else
        {
            Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now.AddDays(-20), Owner));
            _removeAnswer = c =>
            {
                Set(c.SetId).RemoveAll(e => e.EmoteId == c.EmoteId);
                return SevenTvWriteResult.Ok();
            };
            _addAnswer = c =>
            {
                Set(c.SetId).Add(new SevenTvSetEntry(c.EmoteId, c.Alias, Now, BotA));
                return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(null, TimeSpan.FromSeconds(30));
            };
        }

        var session = await SessionAsync();
        await RunPassAsync(session: session);

        Assert.Equal(["pause"], session.Calls.Where(c => c.StartsWith("pause", StringComparison.Ordinal)));
        if (call == "repair")
        {
            Assert.Equal("ok", Assert.Single(await AuditsAsync(AuditActions.WishBotRepair)).Details["outcome"]);
            Assert.Null((await LoadAsync(id)).BotRepairAtUtc);
            return;
        }

        Assert.Equal(2, _readCalls.Count);
        Assert.NotNull((await LoadAsync(id)).BotRemoveAtUtc);

        _clock.Advance(TimeSpan.FromSeconds(31));
        await RunPassAsync();

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Equal("resumed", Assert.Single(await AuditsAsync(AuditActions.WishBotRemove)).Details["outcome"]);
        Assert.Single(_removeCalls);
    }

    // ------------------------------------------------------------------------------------ the real runner

    // K6b-3 over two passes: removeEmote Unauthorized → the attempt cleared, the queue stays, the next queued wish is not
    // tried. The next pass's verification answers 401: invalid, the fallback presents both (bot_unavailable).
    [Fact]
    public async Task AnUnauthorizedRemoval_TheNextPassVerificationDecides_WithTheRealRunner()
    {
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        var token = Jwt(Now.AddDays(30));
        await SeedSealedAccountAsync(sealer.Seal(token));
        _writes.VerifyAsync(token, Arg.Any<CancellationToken>()).Returns(
            SevenTvWriteResult<string>.Ok(BotA),
            SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unauthorized()));
        var channel = await SeedBotChannelAsync();
        var first = await SeedOwnedAsync(channel, WishStatus.Active, queuedMinutesAgo: 10);
        var second = await SeedOwnedAsync(channel, WishStatus.Active, queuedMinutesAgo: 5);
        _removeAnswer = _ => SevenTvWriteResult.Unauthorized();
        var (runner, _) = CreateRunner(_writes);

        await runner.RunPassAsync();

        Assert.Single(_removeCalls);
        var wish = await LoadAsync(first);
        Assert.Equal((WishStatus.Active, (DateTime?)null), (wish.Status, wish.BotRemoveAtUtc));
        Assert.NotNull(wish.RemovalQueuedAtUtc);
        Assert.Null((await LoadAsync(second)).BotRemoveAtUtc);
        Assert.Equal(BotAccountStatus.Valid, (await CredentialAsync()).Status);
        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);

        _clock.Advance(TimeSpan.FromSeconds(30));
        await runner.RunPassAsync();

        Assert.Equal(BotAccountStatus.Invalid, (await CredentialAsync()).Status);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(await LoadAsync(first)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(await LoadAsync(second)));
        Assert.Single(_removeCalls);
    }

    // K6f-4: removeEmote Ok with a RetryAfter of 60 s; the clock passes F between the joined pause and the commit:
    // rollback, and after the pass PausedUntilUtc is the report instant + 60 s (the runner books it again). The next pass
    // within the pause calls 7TV with the token not at all (no read); after it the read resumes the removal.
    [Fact]
    public async Task APauseRolledBackWithTheSendCommit_IsBookedAtThePassEnd_WithTheRealRunner()
    {
        using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
        var token = Jwt(Now.AddDays(30));
        await SeedSealedAccountAsync(sealer.Seal(token));
        _writes.VerifyAsync(token, Arg.Any<CancellationToken>()).Returns(SevenTvWriteResult<string>.Ok(BotA));
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        DateTime? reportedAt = null;
        _removeAnswer = c =>
        {
            Set(c.SetId).RemoveAll(e => e.EmoteId == c.EmoteId);
            reportedAt = Now;
            return SevenTvWriteResult.Ok(TimeSpan.FromSeconds(60));
        };
        var hook = ClockHook((IsPauseUpdate, TimeSpan.FromSeconds(30)));
        var db = fixture.CreateTaggedDbContext(PassTag, [hook], databaseName: DatabaseName);
        var (runner, _) = CreateRunner(_writes, db);

        await runner.RunPassAsync();

        // The hook moved the clock past F right after the joined pause: the send commit did not happen.
        Assert.True(Now - reportedAt!.Value >= TimeSpan.FromSeconds(30));
        Assert.Equal(reportedAt.Value.AddSeconds(60), (await CredentialAsync()).PausedUntilUtc);
        Assert.NotNull((await LoadAsync(id)).BotRemoveAtUtc);
        Assert.Equal(2, _readCalls.Count);

        _clock.Advance(TimeSpan.FromSeconds(10));
        await runner.RunPassAsync();
        Assert.Equal(2, _readCalls.Count);

        _clock.Advance(TimeSpan.FromSeconds(30));
        await runner.RunPassAsync();

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Equal("resumed", Assert.Single(await AuditsAsync(AuditActions.WishBotRemove)).Details["outcome"]);
        Assert.Single(_removeCalls);
    }

    // ------------------------------------------------------------------------------------ helpers (removal races)

    // A paged read on the test clock with the client's rule; null once every page was answered (the set as it is).
    private SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>? PlayPages(
        Func<TimeSpan>? timeLeft, int pages, TimeSpan perPage, List<int>? log = null)
    {
        var sent = 0;
        try
        {
            for (var page = 1; page <= pages; page++)
            {
                var limit = BotAccountRegistration.WriteClientTimeout;
                if (timeLeft is not null)
                {
                    var left = timeLeft();
                    if (left < BotAccountLockDeadline.MinimumPageTimeLimit)
                    {
                        return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
                    }

                    limit = left < limit ? left : limit;
                }

                sent++;
                if (perPage > limit)
                {
                    _clock.Advance(limit);
                    return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
                }

                _clock.Advance(perPage);
            }

            return null;
        }
        finally
        {
            log?.Add(sent);
        }
    }

    private SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>? TwoPagesSixAndOne(Func<TimeSpan>? timeLeft)
    {
        if (PlayPages(timeLeft, 1, TimeSpan.FromSeconds(6)) is { } failed)
        {
            return failed;
        }

        return PlayPages(timeLeft, 1, TimeSpan.FromSeconds(1));
    }

    private void Change(string change, string emote)
    {
        var entries = Set(SetA);
        var bot = entries.Single(e => e.EmoteId == emote && e.Alias == "Alias");
        switch (change)
        {
            case "replaced":
                entries.Remove(bot);
                entries.Add(bot with { AddedAtUtc = Now.AddSeconds(-2), AddedById = Owner });
                break;
            case "secondAlias":
                entries.Add(new SevenTvSetEntry(emote, "Alias2", Now.AddSeconds(-2), Owner));
                break;
            case "gone":
                entries.Remove(bot);
                break;
            default:
                _readStub = (_, _, _) => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
                break;
        }
    }

    private async Task WaitUntilPassBlockedAsync()
    {
        for (var i = 0; i < 300; i++)
        {
            await using var db = CreateDb();
            var waiting = await db.Database.SqlQuery<int>(
                    $"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE application_name = {PassTag} AND wait_event_type = 'Lock'""")
                .SingleAsync();
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The send transaction never waited for the held channel row.");
    }

    // One wish part kept across passes (its count of stuck orders lives in it), on a context of its own, tagged and with
    // the given gate of interceptors when there is one.
    private (AppDbContext Db, WishBotPass Pass) KeptPass(Gate? gate = null)
    {
        var db = gate is null ? CreateDb() : fixture.CreateTaggedDbContext(PassTag, [gate], databaseName: DatabaseName);
        _disposables.Add(db);
        return (db, Pass(db));
    }

    // A pass of the kept wish part with a fresh session of the given epoch.
    private async Task RunKeptAsync(AppDbContext db, WishBotPass pass, long epoch = Epoch)
    {
        var session = await SessionAsync(epoch);
        session.Db = db;
        await pass.RunAsync(session);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private async Task SetEpochAsync(long epoch)
    {
        await using var db = CreateDb();
        await db.BotAccountCredentials.ExecuteUpdateAsync(u => u.SetProperty(c => c.RunnerEpoch, epoch));
    }

    private static bool IsPauseUpdate(string command) =>
        command.Contains("UPDATE \"BotAccountCredentials\" SET \"PausedUntilUtc\"", StringComparison.Ordinal);

    // Passes the executed commands on to its interceptors only while it is open, so a hook counts from one pass on.
    private sealed class Gate(params Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor[] inner)
        : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Open { get; set; }

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Open)
            {
                foreach (var interceptor in inner)
                {
                    result = await interceptor.ReaderExecutedAsync(command, eventData, result, cancellationToken);
                }
            }

            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Open)
            {
                foreach (var interceptor in inner)
                {
                    result = await interceptor.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
                }
            }

            return result;
        }
    }
}
