using System.Data.Common;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Migrations;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Fixtures.ImportedBackfillSeed;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The cases of the imported-data removal (#384, spec 11.1) that need the real ChatLogBackfillService
// (task T7, after #379 part B): the coverage readers of S3, the races against the enqueue (S10), against
// the block of a just cancelled run (S11, both variants) and a new run afterwards (S13), plus the sweep
// equivalence of S6 against part B's constants, which are only called, never changed.
//
// The backfill service is built in exactly one place (CreateBackfillService). It publishes to its own
// substitute, so _publisher still sees only what the removal sent. Every race is ordered by Postgres
// (PostgresLockProbe on tagged contexts, or an interceptor that parks a command or a commit), never by
// sleeps.
public partial class ChatLogImportRemovalServiceTests
{
    // The enqueue's clock and "today": a month back from here reaches 2026-04-15, and the seeded channels
    // start counting on 2026-05-01, so every run plans [04-15, 05-01): three blocks, the last two days long.
    private static readonly DateTimeOffset BackfillNow = new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BackfillToday = DateOnly.FromDateTime(BackfillNow.UtcDateTime);

    // The entry day of both members of set B, before the window: no entry gate cuts a block row.
    private static readonly DateTime EntryDay = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IRedisPublisher _backfillPublisher = Substitute.For<IRedisPublisher>();
    private readonly IForeignEmoteSetService _foreign = Substitute.For<IForeignEmoteSetService>();
    private readonly ITrackedEmoteSetMembershipService _membership = MemberOfEverySet();

    // ---------------------------------------------------------------- S3: the coverage readers

    // S3 / AK 2, the readers: after the removal GetCoverageAsync reads "nothing imported" for every set
    // together and for each of the two sets the coverage had.
    [Fact]
    public async Task Remove_LeavesTheCoverageReadersEmpty_ForAllSetsAndForEachSet()
    {
        var main = await SeedMainScenarioAsync("rmreaders");
        await using (var read = CreateDbContext())
        {
            var reader = CreateBackfillService(read);
            Assert.Equal(2, (await reader.GetCoverageAsync(main.Channel.Id, EmoteSetScope.AllSets)).Intervals.Count);
            Assert.Equal(April1, (await reader.GetCoverageAsync(main.Channel.Id, EmoteSetScope.Set(SetA))).ImportedFrom);
            Assert.Equal(April1.AddDays(9), (await reader.GetCoverageAsync(main.Channel.Id, EmoteSetScope.Set(SetB))).ImportedFrom);
        }

        Assert.Equal(ChatLogImportRemovalStatus.Removed, (await RemoveAsync(main.Channel.ChannelName, main.Expectation)).Status);

        await using var verify = CreateDbContext();
        var service = CreateBackfillService(verify);
        foreach (var scope in new[] { EmoteSetScope.AllSets, EmoteSetScope.Set(SetA), EmoteSetScope.Set(SetB) })
        {
            var coverage = await service.GetCoverageAsync(main.Channel.Id, scope);
            Assert.Equal(
                ((DateOnly?)null, (DateOnly?)null, false, (DateOnly?)null, 0),
                (coverage.ImportedFrom, coverage.ImportedTo, coverage.HasGaps, coverage.ContiguousFrom, coverage.Intervals.Count));
        }
    }

    // ---------------------------------------------------------------- S10: against the enqueue

    // S10 / AK 12, the enqueue first: it holds the channel row (parked on the requester's row, which a
    // third transaction holds like a running account deletion). The removal waits at the channel row,
    // then sees the committed run and refuses: nothing deleted, no audit, no event.
    [Fact]
    public async Task Race_EnqueueFirst_TheRemovalWaitsAtTheChannel_AndIsRefusedForTheNewRun()
    {
        var channel = await SeedBackfillChannelAsync("rmenqfirst", olderImport: (April1, April1.AddDays(5)));
        var expectation = await ExpectationAsync(channel);
        var importedBefore = await ImportedStateAsync(channel);

        await using var holder = CreateDbContext();
        await using var holderTx = await HoldRequesterRowAsync(holder);

        await using var enqueueDb = fixture.CreateTaggedDbContext("rmenqfirst-enq", DatabaseName);
        var enqueue = Task.Run(() => CreateBackfillService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 1, BackfillToday, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("rmenqfirst-enq", enqueue);

        await using var removalDb = fixture.CreateTaggedDbContext("rmenqfirst-rm", DatabaseName);
        var removal = Task.Run(() => CreateService(removalDb).RemoveImportedAsync(channel.ChannelName, expectation, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("rmenqfirst-rm", removal);
        Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync("rmenqfirst-rm"), StringComparison.Ordinal);

        await holderTx.RollbackAsync();
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);
        Assert.Equal(ChatLogImportRemovalStatus.RunActive, (await removal).Status);

        await using var verify = CreateDbContext();
        Assert.Equal(importedBefore, await ImportedStateAsync(channel));
        Assert.Equal(ChatLogBackfillRunStatus.Queued, (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await RemovalAuditCountAsync(verify));
        Assert.Empty(_publisher.ReceivedCalls());
    }

    // S10 / AK 12, the removal first: it holds the channel and emote rows and is about to delete. The
    // enqueue waits at the channel row, then stores its run against a channel without imports; the run's
    // first block then writes and keeps its rows — the removal never takes data of the new run.
    [Fact]
    public async Task Race_RemovalFirst_TheEnqueueWaitsAtTheChannel_AndItsRunStartsOnACleanChannel()
    {
        var channel = await SeedBackfillChannelAsync("rmremfirst", olderImport: (April1, April1.AddDays(5)));
        var expectation = await ExpectationAsync(channel);

        var atDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var removalDb = CreateDbContext(new PauseBeforeCommand("DELETE FROM \"UsageStats\"", atDelete, proceed));
        var removal = Task.Run(() => CreateService(removalDb).RemoveImportedAsync(channel.ChannelName, expectation, Actor));
        await atDelete.Task;

        await using var enqueueDb = fixture.CreateTaggedDbContext("rmremfirst-enq", DatabaseName);
        var enqueue = Task.Run(() => CreateBackfillService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 1, BackfillToday, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("rmremfirst-enq", enqueue);
        Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync("rmremfirst-enq"), StringComparison.Ordinal);

        proceed.SetResult();
        var result = await removal;
        Assert.Equal((ChatLogImportRemovalStatus.Removed, 5, 5), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);

        await using (var verify = CreateDbContext())
        {
            Assert.Equal((0, 0), (await ImportedRowCountAsync(verify, channel.Id), await CoverageDayCountAsync(verify, channel.Id)));
            Assert.Equal(ChatLogBackfillRunStatus.Queued, (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync()).Status);
        }

        var claim = await ClaimAsync();
        var snapshot = await SnapshotAsync(claim.RunId);
        await using (var blockDb = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await ReplaceBlockAsync(blockDb, claim, 0, snapshot));
        }

        await using var after = CreateDbContext();
        Assert.Equal((2, 7), (await ImportedRowCountAsync(after, channel.Id), await CoverageDayCountAsync(after, channel.Id)));
    }

    // ---------------------------------------------------------------- S11: against the block of a cancelled run

    // S11 / AK 12 (spec 6.3): block 1 of a running run has written its rows and coverage (steps 1–3) and
    // holds its emotes FOR SHARE when the cancel commits; the removal starts with the coverage from before
    // that block. Sharing an emote with the block, it waits at its emote lock; sharing none, it waits at
    // the row the block's step 1 deleted (an older import inside the block's days). Either way the block
    // then fails its progress update and rolls back, the removal deletes block 0 and the older import,
    // no deadlock, no imported row is left, the run stays cancelled.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Race_ACancelledRunsLastBlock_HoldsTheRemovalUntilItRollsBack_AndNothingImportedIsLeft(bool sharesAnEmote)
    {
        var name = sharesAnEmote ? "rmblockshared" : "rmblockapart";
        var channel = await SeedBackfillChannelAsync(name, olderImport: sharesAnEmote ? null : (April1.AddDays(22), April1.AddDays(23)));
        var (claim, snapshot) = await EnqueueAndClaimAsync(channel);
        var kept = snapshot.Single(e => e.Name == "KInB");
        var placeholder = snapshot.Single(e => e.Name == "NInB");
        await using (var block0Db = CreateDbContext())
        {
            IReadOnlyList<ChatLogBackfillSnapshotEmote> block0Rows = sharesAnEmote ? snapshot : [kept];
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await ReplaceBlockAsync(block0Db, claim, 0, block0Rows));
        }

        // What the dialog shows: the coverage before block 1.
        var expectation = await ExpectationAsync(channel);

        var atProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var blockDb = CreateDbContext(new PauseBeforeCommand("\"WeeksDone\" = \"WeeksDone\" + 1", atProgress, proceed));
        IReadOnlyList<ChatLogBackfillSnapshotEmote> block1Rows = sharesAnEmote ? snapshot : [placeholder];
        var block = Task.Run(() => ReplaceBlockAsync(blockDb, claim, 1, block1Rows));
        await atProgress.Task;

        await using (var cancelDb = CreateDbContext())
        {
            Assert.Equal(ChatLogBackfillCancelResult.Cancelled, await CreateBackfillService(cancelDb).CancelAsync(channel.ChannelName, Actor));
        }

        await using var removalDb = fixture.CreateTaggedDbContext($"{name}-rm", DatabaseName);
        var removal = Task.Run(() => CreateService(removalDb).RemoveImportedAsync(channel.ChannelName, expectation, Actor));
        await fixture.WaitUntilBlockedOnLockAsync($"{name}-rm", removal);
        var waitingAt = await fixture.WaitingQueryAsync($"{name}-rm");
        Assert.Contains(sharesAnEmote ? "FROM \"Emotes\"" : "DELETE FROM \"UsageStats\"", waitingAt, StringComparison.Ordinal);

        proceed.SetResult();
        Assert.IsType<ChatLogBackfillBlockResult.RunNotActive>(await block);
        var result = await removal;
        Assert.Equal(
            (ChatLogImportRemovalStatus.Removed, 2, sharesAnEmote ? 7 : 8),
            (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays));

        await using var verify = CreateDbContext();
        Assert.Equal((0, 0), (await ImportedRowCountAsync(verify, channel.Id), await CoverageDayCountAsync(verify, channel.Id)));
        var run = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync();
        Assert.Equal((ChatLogBackfillRunStatus.Cancelled, 1), (run.Status, run.WeeksDone));
    }

    // S11 variant / AK 14 (spec 6.3, Sol finding 2): block 1 has run its progress update and is about to
    // commit when the cancel arrives; the cancel waits for the run row and commits after the block. A
    // removal whose expectation still shows the coverage from before block 1 is ScopeChanged: nothing
    // deleted, no audit, no event.
    [Fact]
    public async Task Race_ABlockCommittedBeforeTheCancel_IsNotInTheExpectation_AndTheRemovalIsScopeChanged()
    {
        var channel = await SeedBackfillChannelAsync("rmblockfirst");
        var (claim, snapshot) = await EnqueueAndClaimAsync(channel);
        await using (var block0Db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await ReplaceBlockAsync(block0Db, claim, 0, snapshot));
        }

        var expectation = await ExpectationAsync(channel);

        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var blockDb = CreateDbContext(new PauseBeforeCommit(atCommit, proceed));
        var block = Task.Run(() => ReplaceBlockAsync(blockDb, claim, 1, snapshot));
        await atCommit.Task;

        await using var cancelDb = fixture.CreateTaggedDbContext("rmblockfirst-cancel", DatabaseName);
        var cancel = Task.Run(() => CreateBackfillService(cancelDb).CancelAsync(channel.ChannelName, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("rmblockfirst-cancel", cancel);

        proceed.SetResult();
        Assert.IsType<ChatLogBackfillBlockResult.Committed>(await block);
        Assert.Equal(ChatLogBackfillCancelResult.Cancelled, await cancel);

        ChannelHashes before;
        await using (var read = CreateDbContext())
        {
            before = await ChannelHashesAsync(read, channel.Id);
            Assert.Equal((4, 14), (await ImportedRowCountAsync(read, channel.Id), await CoverageDayCountAsync(read, channel.Id)));
        }

        var result = await RemoveAsync(channel.ChannelName, expectation);

        Assert.Equal((ChatLogImportRemovalStatus.ScopeChanged, 0, 0), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays));
        await using var verify = CreateDbContext();
        Assert.Equal(before, await ChannelHashesAsync(verify, channel.Id));
        var run = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync();
        Assert.Equal((ChatLogBackfillRunStatus.Cancelled, 2), (run.Status, run.WeeksDone));
        Assert.Equal(0, await RemovalAuditCountAsync(verify));
        Assert.Empty(_publisher.ReceivedCalls());
    }

    // ---------------------------------------------------------------- S13: a new run afterwards

    // S13 / AK 13: a completed run of set B is removed; an enqueue of the same set and window binds the
    // placeholder and the slots the first run created (no second emote row, no new slot, neither marked
    // as created by the new run), and its first block commits and covers its days again.
    [Fact]
    public async Task NewRunAfterTheRemoval_BindsTheExistingPlaceholderAndSlots_AndItsBlockCoversAgain()
    {
        var channel = await SeedBackfillChannelAsync("rmnewrun");
        var (claim, snapshot) = await EnqueueAndClaimAsync(channel);
        for (var index = 0; index < claim.WeeksTotal; index++)
        {
            await using var blockDb = CreateDbContext();
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await ReplaceBlockAsync(blockDb, claim, index, snapshot));
        }

        int emotesBefore, slotsBefore;
        await using (var read = CreateDbContext())
        {
            Assert.Equal(ChatLogBackfillRunStatus.Completed, (await read.ChatLogBackfillRuns.AsNoTracking().SingleAsync()).Status);
            emotesBefore = await read.Emotes.CountAsync(e => e.ChannelId == channel.Id);
            slotsBefore = await read.EmoteSlots.CountAsync(s => s.Emote.ChannelId == channel.Id);
        }

        var removed = await RemoveAsync(channel.ChannelName, await ExpectationAsync(channel));
        Assert.Equal((ChatLogImportRemovalStatus.Removed, 6, 16, 0), (removed.Status, removed.RemovedUsageRows, removed.RemovedCoverageDays, removed.RemovedSeeds));

        var (second, secondSnapshot) = await EnqueueAndClaimAsync(channel);
        Assert.NotEqual(claim.RunId, second.RunId);
        Assert.Equal(
            snapshot.Select(e => (e.EmoteId, e.SlotId, e.Name)).Order(),
            secondSnapshot.Select(e => (e.EmoteId, e.SlotId, e.Name)).Order());

        await using (var read = CreateDbContext())
        {
            Assert.Equal(emotesBefore, await read.Emotes.CountAsync(e => e.ChannelId == channel.Id));
            Assert.Equal(slotsBefore, await read.EmoteSlots.CountAsync(s => s.Emote.ChannelId == channel.Id));
            var provenance = await read.ChatLogBackfillRunEmotes.AsNoTracking().Where(e => e.RunId == second.RunId).ToListAsync();
            Assert.All(provenance, e => Assert.Equal((false, false), (e.CreatedRow, e.CreatedSlot)));
        }

        await using (var blockDb = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await ReplaceBlockAsync(blockDb, second, 0, secondSnapshot));
        }

        await using var verify = CreateDbContext();
        Assert.Equal(2, await ImportedRowCountAsync(verify, channel.Id));
        Assert.All(await verify.ChatLogBackfillCoverage.AsNoTracking().Where(d => d.ChannelId == channel.Id).ToListAsync(), d => Assert.Equal(second.RunId, d.RunId));
        var coverage = await CreateBackfillService(verify).GetCoverageAsync(channel.Id, EmoteSetScope.Set(SetB));
        Assert.Equal(
            ((DateOnly?)claim.WindowFrom, (DateOnly?)claim.WindowFrom.AddDays(7)),
            (coverage.ImportedFrom, coverage.ImportedTo));
    }

    // ---------------------------------------------------------------- S6: equivalence with part B's sweep

    // S6 / AK 7 against #379 part B's sweep constants (called, never changed): the removal deletes exactly
    // the seeds the sweep would newly find once the channel's imported rows were gone — the flag-off
    // enqueue seed with CreatedSlot included — and afterwards the sweep finds exactly what it found before,
    // and counts as much. Kept: an unsettled seed with live rows, a settled slot left rowless, a seed that
    // was rowless before, another channel's seed with only imported rows, and the rowless seed an active
    // run of another channel points at (excluded by the sweep's run conjunct either way).
    [Fact]
    public async Task Remove_DeletesExactlyTheSeedsTheSweepWouldNewlyFind_AndLeavesTheSweepWhereItWas()
    {
        Channel channel;
        EmoteSlot onlyImported, enqueueSeed, rowlessBefore, otherRowless;
        await using (var seed = CreateDbContext())
        {
            channel = await AddChannelAsync(seed, "rmsweep", SetA);
            var other = await AddChannelAsync(seed, "rmsweepother", SetA);
            var to = April1.AddDays(3);

            onlyImported = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "a"), "A", seedUnsettled: true, archived: true);
            await AddUsageAsync(seed, onlyImported, SetA, April1, to, UsageStatSource.ChatLogArchive);

            var withLive = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "b"), "B", seedUnsettled: true);
            await AddUsageAsync(seed, withLive, SetA, April1, to, UsageStatSource.ChatLogArchive);
            await AddUsageAsync(seed, withLive, SetA, May1, May1.AddDays(1), UsageStatSource.Live);

            var settled = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "c"), "C");
            await AddUsageAsync(seed, settled, SetA, April1, to, UsageStatSource.ChatLogArchive);

            var placeholder = await AddEmoteAsync(seed, channel, "p", isPlaceholder: true);
            enqueueSeed = await AddSlotAsync(seed, placeholder, "P", seedUnsettled: true, archived: true);
            await AddUsageAsync(seed, enqueueSeed, SetA, April1, to, UsageStatSource.ChatLogArchive);
            await AddRunAsync(seed, channel, ChatLogBackfillRunStatus.Completed, SetA, new SnapshotEntry(placeholder, enqueueSeed, CreatedRow: true, CreatedSlot: true));

            rowlessBefore = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "e"), "E", seedUnsettled: true, archived: true);
            await AddCoverageAsync(seed, channel, April1, to, SetA);

            var otherImported = await AddSlotAsync(seed, await AddEmoteAsync(seed, other, "f"), "F", seedUnsettled: true, archived: true);
            await AddUsageAsync(seed, otherImported, SetA, April1, to, UsageStatSource.ChatLogArchive);
            otherRowless = await AddSlotAsync(seed, await AddEmoteAsync(seed, other, "g"), "G", seedUnsettled: true);
            var runEmote = await AddEmoteAsync(seed, other, "h");
            var runSeed = await AddSlotAsync(seed, runEmote, "H", seedUnsettled: true, archived: true);
            await AddRunAsync(seed, other, ChatLogBackfillRunStatus.Running, SetA, new SnapshotEntry(runEmote, runSeed, CreatedSlot: true));
        }

        HashSet<string> sweptBefore, sweptWithoutImports;
        long countedBefore;
        List<string> slotsBefore;
        await using (var read = CreateDbContext())
        {
            sweptBefore = await SweepTargetsAsync(read, deleteImportsOfChannelId: null);
            sweptWithoutImports = await SweepTargetsAsync(read, deleteImportsOfChannelId: channel.Id);
            countedBefore = await CountRowlessUnsettledSeedsAsync(read);
            slotsBefore = await read.EmoteSlots.Select(s => s.Id).ToListAsync();
        }

        Assert.Equal(new[] { rowlessBefore.Id, otherRowless.Id }.Order(), sweptBefore.Order());
        Assert.Equal(2, countedBefore);

        var result = await RemoveAsync(channel.ChannelName, [new ExpectedCoverageInterval(April1, April1.AddDays(3), SetA, ArchiveHost)]);

        await using var verify = CreateDbContext();
        var removedSeeds = slotsBefore.Except(await verify.EmoteSlots.Select(s => s.Id).ToListAsync()).ToHashSet();
        var newlySwept = sweptWithoutImports.Except(sweptBefore).ToHashSet();
        Assert.Equal(new[] { onlyImported.Id, enqueueSeed.Id }.Order(), newlySwept.Order());
        Assert.Equal(newlySwept.Order(), removedSeeds.Order());
        Assert.Equal((ChatLogImportRemovalStatus.Removed, 2), (result.Status, result.RemovedSeeds));
        Assert.Equal(sweptBefore.Order(), (await SweepTargetsAsync(verify, deleteImportsOfChannelId: null)).Order());
        Assert.Equal(countedBefore, await CountRowlessUnsettledSeedsAsync(verify));
    }

    // ---------------------------------------------------------------- Helpers

    private static ITrackedEmoteSetMembershipService MemberOfEverySet()
    {
        var membership = Substitute.For<ITrackedEmoteSetMembershipService>();
        membership.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TrackedEmoteSetMembership.Member);
        return membership;
    }

    // The one place the backfill service is built (plan T7): one archive, the seed's host; the fixed clock.
    private ChatLogBackfillService CreateBackfillService(AppDbContext db) =>
        new(
            db,
            _backfillPublisher,
            Substitute.For<IExcludedChannelFilter>(),
            _membership,
            _foreign,
            new ChatLogBackfillOptions(),
            new ChatLogArchiveOptions { BaseUrl = $"https://{ArchiveHost}/" },
            new HandWoundTimeProvider(BackfillNow),
            NullLogger<ChatLogBackfillService>.Instance);

    // A channel counting from 2026-05-01 with one emote "k" (a settled slot, live rows from the counting
    // start on), the requester's user row, and set B listing "k" and the unknown "n" (a placeholder once
    // enqueued), both dated before the window. Optionally an older import of set A on "k" with its coverage.
    private async Task<Channel> SeedBackfillChannelAsync(string name, (DateOnly From, DateOnly ToExclusive)? olderImport = null)
    {
        await using var db = CreateDbContext();
        var channel = await AddChannelAsync(db, name, SetA);
        db.Users.Add(new User { Id = Actor.TwitchUserId, TwitchUsername = Actor.Login, DisplayName = Actor.Login });
        await db.SaveChangesAsync();

        var kept = await AddSlotAsync(db, await AddEmoteAsync(db, channel, "k"), "Kept");
        await AddUsageAsync(db, kept, SetA, May1, May1.AddDays(3), UsageStatSource.Live);
        if (olderImport is { } older)
        {
            await AddUsageAsync(db, kept, SetA, older.From, older.ToExclusive, UsageStatSource.ChatLogArchive);
            await AddCoverageAsync(db, channel, older.From, older.ToExclusive, SetA);
        }

        ForeignEmoteRow[] members =
        [
            new("k", "KInB", "KDefault", "https://cdn.7tv.app/emote/k/2x.webp", null, null, EntryDay),
            new("n", "NInB", "NDefault", "https://cdn.7tv.app/emote/n/2x.webp", null, null, EntryDay),
        ];
        _foreign.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), SetB, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(new ForeignEmoteSet(name, null, SetB, members.Length, false, members, "Set B", 1000)));
        return channel;
    }

    private async Task<(ChatLogBackfillClaim Claim, IReadOnlyList<ChatLogBackfillSnapshotEmote> Snapshot)> EnqueueAndClaimAsync(Channel channel)
    {
        await using (var db = CreateDbContext())
        {
            var enqueued = await CreateBackfillService(db).EnqueueAsync(channel.ChannelName, SetB, 1, BackfillToday, Actor);
            Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, enqueued.Status);
        }

        var claim = await ClaimAsync();
        Assert.Equal((BackfillToday.AddMonths(-1), May1, 3), (claim.WindowFrom, claim.WindowTo, claim.WeeksTotal));
        var snapshot = await SnapshotAsync(claim.RunId);
        Assert.Equal(2, snapshot.Count);
        return (claim, snapshot);
    }

    private async Task<ChatLogBackfillClaim> ClaimAsync()
    {
        await using var db = CreateDbContext();
        var claim = await CreateBackfillService(db).ClaimNextAsync(BackfillNow.UtcDateTime);
        Assert.NotNull(claim);
        return claim;
    }

    private async Task<IReadOnlyList<ChatLogBackfillSnapshotEmote>> SnapshotAsync(long runId)
    {
        await using var db = CreateDbContext();
        return (await CreateBackfillService(db).GetSnapshotAsync(runId)).Emotes;
    }

    // Block <paramref name="index"/> of the claimed run's plan: one row per given snapshot entry on the
    // block's first day.
    private Task<ChatLogBackfillBlockResult> ReplaceBlockAsync(
        AppDbContext db, ChatLogBackfillClaim claim, int index, IReadOnlyList<ChatLogBackfillSnapshotEmote> entries)
    {
        var from = claim.WindowFrom.AddDays(ChatLogBackfillWindow.BlockDays * index);
        var isLast = index == claim.WeeksTotal - 1;
        var to = isLast ? claim.WindowTo : from.AddDays(ChatLogBackfillWindow.BlockDays);
        var rows = entries.Select(e => new ChatLogBackfillAggregate(e.SlotId, e.EmoteId, from, 3, 0, 0)).ToList();
        return CreateBackfillService(db).ReplaceBlockAsync(claim.RunId, from, to, rows, 10, rows.Count, isLast);
    }

    // The expectation the settings dialog sends: the status read's coverage intervals, unchanged.
    private async Task<IReadOnlyList<ExpectedCoverageInterval>> ExpectationAsync(Channel channel)
    {
        await using var db = CreateDbContext();
        var coverage = await CreateBackfillService(db).GetCoverageAsync(channel.Id, EmoteSetScope.AllSets);
        return coverage.Intervals.Select(i => new ExpectedCoverageInterval(i.From, i.To, i.EmoteSetId, i.ArchiveHost)).ToList();
    }

    private async Task<(string UsageStats, string Coverage)> ImportedStateAsync(Channel channel)
    {
        await using var db = CreateDbContext();
        var hashes = await ChannelHashesAsync(db, channel.Id);
        return (hashes.UsageStats, hashes.Coverage);
    }

    // Holds the requester's user row FOR UPDATE, like a running account deletion: an enqueue that got its
    // channel lock parks on its FOR SHARE until this transaction ends.
    private static async Task<IDbContextTransaction> HoldRequesterRowAsync(AppDbContext holder)
    {
        var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Users" WHERE "Id" = {Actor.TwitchUserId} FOR UPDATE""");
        return transaction;
    }

    private static Task<int> RemovalAuditCountAsync(AppDbContext db) =>
        db.AuditLogEntries.CountAsync(a => a.Action == AuditActions.BackfillRemoveImported);

    // The slots part B's sweep would delete now, read inside a transaction that is rolled back — first
    // deleting the given channel's imported rows when asked (spec 5.3 step 7 as plain EF, not the removal's SQL).
    private static async Task<HashSet<string>> SweepTargetsAsync(AppDbContext db, string? deleteImportsOfChannelId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (deleteImportsOfChannelId is { } channelId)
        {
            await db.UsageStats
                .Where(u => u.Source == UsageStatSource.ChatLogArchive && db.Emotes.Any(e => e.Id == u.EmoteId && e.ChannelId == channelId))
                .ExecuteDeleteAsync();
        }

        var before = await db.EmoteSlots.Select(s => s.Id).ToListAsync();
        await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.SweepRowlessUnsettledSeedsSql);
        var after = await db.EmoteSlots.Select(s => s.Id).ToListAsync();
        await transaction.RollbackAsync();
        return before.Except(after).ToHashSet();
    }

    private static async Task<long> CountRowlessUnsettledSeedsAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = EmoteSlotRollbackSql.CountRowlessUnsettledSeedsSql;
            return (long)(await command.ExecuteScalarAsync())!;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    // Parks the first commit of its context until the test lets it go — after every statement of the
    // transaction, with all its row locks held.
    private sealed class PauseBeforeCommit(TaskCompletionSource reached, TaskCompletionSource proceed) : DbTransactionInterceptor
    {
        private int _paused;

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _paused, 1) == 0)
            {
                reached.TrySetResult();
                await proceed.Task;
            }

            return result;
        }
    }
}
