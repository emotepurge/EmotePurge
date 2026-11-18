using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The expiry run with the real writers around it (reconcile, extend, settings) and against held row locks (plan #484
// T3 cases 13 to 15, 19 to 23). The races wait on Postgres itself (pg_stat_activity), never on a clock.
public partial class WishExpiryServiceTests
{
    private const string Second = "wish-expiry-second-run";

    // Case 13: the reconcile runs first. An expired wish whose slot was archived ends as removed_externally and is no
    // candidate any more: no wish.due.
    [Fact]
    public async Task RealReconcileFirst_AnArchivedSlot_EndsTheWish_AndNoDueEntryIsWritten()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var startsAt = Now.AddDays(-31);
        var slotId = await SeedSlotAsync(emote, "Kappa", startsAt, archived: true);
        var wishId = await SeedLinkedWishAsync(channel, emote, slotId, "Kappa", startsAt, confirmedAt: Now.AddHours(-1));

        var result = await Sut(reconcile: RealReconcile()).RunAsync();

        Assert.Equal(WishStatus.RemovedExternally, (await LoadAsync(wishId)).Status);
        Assert.Empty(await DueEntriesAsync(wishId));
        Assert.Equal((1, 0, 0), (result.ReconciledChannels, result.Presented, result.Queued));
    }

    // Case 14: a failing reconcile in channel A is counted and stops nothing; A's due wish is still presented and
    // channel B runs as usual.
    [Fact]
    public async Task AFailingReconcile_IsCounted_AndTheDueStepRunsForThatChannelAnyway()
    {
        var a = await SeedChannelAsync();
        var b = await SeedChannelAsync();
        var inA = await SeedWishAsync(a, expiresAt: Now.AddMinutes(-1));
        var inB = await SeedWishAsync(b, expiresAt: Now.AddMinutes(-1));
        _reconcile.ReconcileChannelAsync(a.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("reconcile failed")));

        var result = await Sut().RunAsync();

        Assert.Equal((1, 1), (result.ReconciledChannels, result.FailedReconciles));
        Assert.Equal((WishStatus.Presented, WishStatus.Presented), ((await LoadAsync(inA)).Status, (await LoadAsync(inB)).Status));
        await _reconcile.Received(1).ReconcileChannelAsync(b.Id, Arg.Any<CancellationToken>());
        Assert.Equal(2, result.Presented);
    }

    // Contract 2.3, errors: a failure at one wish rolls back that wish alone; the next one is decided.
    [Fact]
    public async Task AFailureAtOneWish_RollsBackOnlyThatWish_AndTheRunGoesOn()
    {
        var channel = await SeedChannelAsync();
        var first = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-2));
        var second = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1));
        var before = await SnapshotAsync(first);
        var hook = new CommandHook(
            c => c.Contains("UPDATE \"Wishes\"", StringComparison.Ordinal),
            n => n == 1 ? throw new InvalidOperationException("forced failure at the first wish") : Task.CompletedTask);
        await using var failing = fixture.CreateTaggedDbContext("wish-expiry-failing", [hook], databaseName: DatabaseName);

        var result = await Sut(failing).RunAsync();

        Assert.Equal((1, 1), (result.Failed, result.Presented));
        Assert.Equal(before, await SnapshotAsync(first));
        Assert.Empty(await DueEntriesAsync(first));
        Assert.Equal(WishStatus.Presented, (await LoadAsync(second)).Status);
        Assert.Single(await DueEntriesAsync(second));
    }

    // Case 15, first half: two passes at once (two worker containers). The second waits at the row the first holds,
    // reads the committed state afterwards and skips: one transition, one entry.
    [Fact]
    public async Task TwoRunsAtOnce_MeetAtTheRowLock_AndWriteOneDueEntry()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1));
        Task<WishExpiryRunResult>? second = null;
        var hook = new CommandHook(IsWishLock, async n =>
        {
            if (n == 1)
            {
                second = Sut(fixture.CreateTaggedDbContext(Second, DatabaseName)).RunAsync();
                await fixture.WaitUntilBlockedOnLockAsync(Second, second);
            }
        });
        await using var firstDb = fixture.CreateTaggedDbContext("wish-expiry-first-run", [hook], databaseName: DatabaseName);

        var first = await Sut(firstDb).RunAsync();
        var secondResult = await second!;

        Assert.Equal((1, 0), (first.Presented, first.Skipped));
        Assert.Equal((0, 1), (secondResult.Presented, secondResult.Skipped));
        Assert.Single(await DueEntriesAsync(wishId));
        Assert.Equal(1, EventsFor(channel));
    }

    // Case 15, second half: a foreign transaction holds the row and keeps the wish. The run waits there, then reads
    // kept and skips.
    [Fact]
    public async Task AHeldRowThatIsKeptMeanwhile_IsSkippedAfterTheWait()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1));
        await using var holder = CreateDb();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        Assert.Single(await holder.LockWishesByIdsAsync([wishId], CancellationToken.None));
        await holder.Wishes.Where(w => w.Id == wishId).ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WishStatus.Kept));

        var run = Sut(fixture.CreateTaggedDbContext(Second, DatabaseName)).RunAsync();
        await fixture.WaitUntilBlockedOnLockAsync(Second, run);
        Assert.Contains("\"Wishes\"", await fixture.WaitingQueryAsync(Second));
        await holderTransaction.CommitAsync();
        var result = await run;

        Assert.Equal((0, 1), (result.Presented, result.Skipped));
        Assert.Equal(WishStatus.Kept, (await LoadAsync(wishId)).Status);
        Assert.Empty(await DueEntriesAsync(wishId));
        Assert.Empty(PublishedEvents());
    }

    // Case 19 (Sol-P2-2) with the real writers: queued, then the slot is renamed and the real reconcile ends the
    // ownership (queue dropped, version + 1, still active). The next pass presents it as not_bot_owned with one
    // event of its own, and the log still holds exactly one wish.due. Only an extension allows the second entry.
    [Fact]
    public async Task DroppedQueueAfterARename_IsDecidedAgain_WithoutASecondDueEntry_UntilANewExpiry()
    {
        var channel = await SeedChannelAsync(botMode: true);
        var emote = await SeedEmoteAsync(channel);
        var startsAt = Now.AddDays(-30);
        var slotId = await SeedSlotAsync(emote, "Kappa", startsAt);
        var wishId = await SeedLinkedWishAsync(channel, emote, slotId, "Kappa", startsAt, confirmedAt: Now, botOwned: true);
        var expiry = (await LoadAsync(wishId)).ExpiresAtUtc!.Value;
        _availability.Available = true;

        await Sut(reconcile: RealReconcile()).RunAsync();
        var queued = await LoadAsync(wishId);
        Assert.Equal((Now, (int?)SeededVersion), (queued.RemovalQueuedAtUtc, queued.RemovalQueuedVersion));
        Assert.Equal(WishExpiryPolicy.QueuedOutcome, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));

        await using (var db = CreateDb())
        {
            await db.EmoteSlots.Where(s => s.Id == slotId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Name, "KappaRenamed"));
        }

        _clock.Advance(TimeSpan.FromMinutes(15));
        _publisher.ClearReceivedCalls();
        await Sut(reconcile: RealReconcile()).RunAsync();

        var presented = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.NotBotOwned), (presented.Status, presented.StatusReason));
        Assert.Equal((false, SeededVersion + 1, (DateTime?)null), (presented.BotOwned, presented.DecisionVersion, presented.RemovalQueuedAtUtc));
        Assert.Equal([expiry], (await DueEntriesAsync(wishId)).Select(RecordedExpiry));
        Assert.Equal(1, EventsFor(channel));

        Assert.Equal(WishOutcome.Success, (await Commands().ExtendAsync(channel.Name, wishId, Actor)).Outcome);
        var newExpiry = (await LoadAsync(wishId)).ExpiresAtUtc!.Value;
        _clock.SetUtcNow(new DateTimeOffset(newExpiry, TimeSpan.Zero).AddMinutes(1));
        await Sut(reconcile: RealReconcile()).RunAsync();

        var entries = await DueEntriesAsync(wishId);
        Assert.Equal([expiry, newExpiry], entries.Select(RecordedExpiry));
        Assert.Equal(WishStatusReason.NotBotOwned, OutcomeOf(entries[1]));
    }

    // Case 19, addendum: an entry of the same wish for another expiry suppresses nothing; one for this expiry does
    // (the state is written either way).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEarlierDueEntry_SuppressesOnlyForTheSameExpiry(bool sameExpiry)
    {
        var channel = await SeedChannelAsync();
        var expiry = Now.AddMinutes(-1);
        var wishId = await SeedWishAsync(channel, expiresAt: expiry);
        await using (var db = CreateDb())
        {
            db.AddAuditEntry(
                AuditActor.System,
                AuditActions.WishDue,
                channelName: channel.Name,
                targetType: "wish",
                targetId: wishId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                details: new { wishId, outcome = WishStatusReason.Expired, expiresAtUtc = sameExpiry ? expiry : expiry.AddDays(-1) },
                occurredAtUtc: Now);
            await db.SaveChangesAsync();
        }

        await Sut().RunAsync();

        Assert.Equal(WishStatus.Presented, (await LoadAsync(wishId)).Status);
        Assert.Equal(sameExpiry ? 1 : 2, (await DueEntriesAsync(wishId)).Count);
        Assert.Equal(1, EventsFor(channel));
    }

    // Case 20 (Sol-P1-1), order A: the run queues with protection off; the real settings command then sets a
    // threshold below the use, which drops the queue and raises the version. The next pass presents as protected and
    // writes no second entry.
    [Fact]
    public async Task OrderA_AThresholdSetAfterTheQueue_DropsIt_AndTheNextRunPresentsAsProtected()
    {
        var channel = await SeedChannelAsync(botMode: true);
        var (wishId, _) = await SeedUsedWishAsync(channel, startsAt: Now.AddDays(-30), perDay: [10]);
        _availability.Available = true;
        await Sut().RunAsync();
        Assert.NotNull((await LoadAsync(wishId)).RemovalQueuedAtUtc);

        Assert.True((await Commands().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, 5m), Actor)).IsSuccess);
        var dropped = await LoadAsync(wishId);
        Assert.Equal(((DateTime?)null, SeededVersion + 1), (dropped.RemovalQueuedAtUtc, dropped.DecisionVersion));

        _clock.Advance(TimeSpan.FromMinutes(15));
        await Sut().RunAsync();

        var presented = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Protected, SeededVersion + 1), (presented.Status, presented.StatusReason, presented.DecisionVersion));
        Assert.Equal(WishExpiryPolicy.QueuedOutcome, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));
    }

    // Case 21, order B: a foreign transaction standing for the settings command holds the wish row with a new,
    // uncommitted threshold. The run waits at the row, then decides with the new threshold: protected, never queued,
    // and the version is the command's, not raised by the run.
    [Fact]
    public async Task OrderB_ARunWaitingBehindAThresholdChange_DecidesWithTheNewThreshold()
    {
        var channel = await SeedChannelAsync(botMode: true);
        var (wishId, _) = await SeedUsedWishAsync(channel, startsAt: Now.AddDays(-30), perDay: [10]);
        _availability.Available = true;
        await using var holder = CreateDb();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        Assert.Single(await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None));
        Assert.Single(await holder.LockWishesByIdsAsync([wishId], CancellationToken.None));
        await holder.ChannelWishSettings.Where(s => s.ChannelId == channel.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ProtectionThresholdPerDay, 5m));
        await holder.Wishes.Where(w => w.Id == wishId).ExecuteUpdateAsync(u => u.SetProperty(w => w.DecisionVersion, w => w.DecisionVersion + 1));

        var run = Sut(fixture.CreateTaggedDbContext(Second, DatabaseName)).RunAsync();
        await fixture.WaitUntilBlockedOnLockAsync(Second, run);
        Assert.Contains("\"Wishes\"", await fixture.WaitingQueryAsync(Second));
        Assert.Null((await LoadAsync(wishId)).RemovalQueuedAtUtc);
        await holderTransaction.CommitAsync();
        var result = await run;

        var wish = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Protected, (DateTime?)null), (wish.Status, wish.StatusReason, wish.RemovalQueuedAtUtc));
        Assert.Equal(SeededVersion + 1, wish.DecisionVersion);
        Assert.Equal((0, 1), (result.Queued, result.Presented));
    }

    // Case 22 (Sol-R2-1): a provisional, owned wish falling due while the bot is out (globally, or the channel
    // suspended) is unconfirmed_start, never bot_unavailable. Once the bot is back it is no candidate: nothing moves.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProvisionalStart_DuringABotOutage_IsUnconfirmedStart_AndStaysSoOnceTheBotIsBack(bool globalOutage)
    {
        var channel = await SeedChannelAsync(botMode: true, suspendedReason: globalOutage ? null : "not_editor", lastSyncedAt: Now.AddHours(-25));
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), authoritative: false, botOwned: true);
        _availability.Available = !globalOutage;

        await Sut().RunAsync();

        var wish = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.UnconfirmedStart, (DateTime?)null), (wish.Status, wish.StatusReason, wish.RemovalQueuedAtUtc));
        Assert.Equal(WishStatusReason.UnconfirmedStart, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));

        var before = await SnapshotAsync(wishId);
        _availability.Available = true;
        await using (var db = CreateDb())
        {
            await db.ChannelWishSettings.Where(s => s.ChannelId == channel.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.BotSuspendedReason, (string?)null));
        }

        _clock.Advance(TimeSpan.FromMinutes(15));
        var again = await Sut().RunAsync();

        Assert.Equal(before, await SnapshotAsync(wishId));
        Assert.Single(await DueEntriesAsync(wishId));
        Assert.Equal((0, 0), (again.Queued, again.Presented));
    }

    // Case 23 (Sol-R2-2, with T2c): 30 hours overdue and no pass in between; the manager extends first. The extend
    // entry carries the old expiry, and the next pass finds nothing due and writes nothing.
    [Fact]
    public async Task LateExtendBeforeTheFirstRun_CarriesThePreviousExpiry_AndTheRunWritesNothing()
    {
        var channel = await SeedChannelAsync();
        var oldExpiry = Now.AddHours(-30);
        var wishId = await SeedWishAsync(channel, expiresAt: oldExpiry);

        Assert.Equal(WishOutcome.Success, (await Commands().ExtendAsync(channel.Name, wishId, Actor)).Outcome);
        await using (var db = CreateDb())
        {
            var extend = await db.AuditLogEntries.AsNoTracking().SingleAsync(a => a.Action == AuditActions.WishExtend && a.ChannelName == channel.Name);
            using var details = System.Text.Json.JsonDocument.Parse(extend.DetailsJson!);
            Assert.Equal(oldExpiry, details.RootElement.GetProperty("previousExpiresAtUtc").GetDateTime());
        }

        var before = await SnapshotAsync(wishId);
        var result = await Sut().RunAsync();

        Assert.Equal(before, await SnapshotAsync(wishId));
        Assert.Empty(await DueEntriesAsync(wishId));
        Assert.Empty(PublishedEvents());
        Assert.Equal((0, 0), (result.Presented, result.Queued));
    }

    // ------------------------------------------------------------------------------------ helpers (races)

    private static bool IsWishLock(string command) =>
        command.Contains("FROM \"Wishes\"", StringComparison.Ordinal) && command.Contains("FOR UPDATE", StringComparison.Ordinal);

    // An active wish linked to its slot the way the reconcile leaves it: alias, generation and a confirmation stamp.
    private async Task<long> SeedLinkedWishAsync(
        Seeded channel, Emote emote, string slotId, string alias, DateTime startsAt, DateTime confirmedAt, bool botOwned = false)
    {
        var wishId = await SeedWishAsync(
            channel, expiresAt: startsAt.AddDays(30), botOwned: botOwned, emoteId: emote.SevenTvEmoteId, slotId: slotId, alias: alias, startsAt: startsAt);
        await using var db = CreateDb();
        await db.Wishes.Where(w => w.Id == wishId).ExecuteUpdateAsync(u => u
            .SetProperty(w => w.LinkOrigin, WishLinkOrigin.Auto)
            .SetProperty(w => w.SlotAddedAtUtc, startsAt)
            .SetProperty(w => w.SlotConfirmedAtUtc, confirmedAt)
            .SetProperty(w => w.FirstActivatedAtUtc, startsAt)
            .SetProperty(w => w.BotOwnedAddedAtUtc, botOwned ? startsAt : null));
        return wishId;
    }
}
