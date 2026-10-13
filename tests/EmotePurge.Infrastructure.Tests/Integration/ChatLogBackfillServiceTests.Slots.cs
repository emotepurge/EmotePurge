using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Slot plan #373, child 3 of the emote slots epic (§4, E6, E11, E11a): the enqueue binds every snapshot
// entry to a slot, the block replace writes that slot, and both serialize with the other slot writers.
public partial class ChatLogBackfillServiceTests
{
    // ---------------------------------------------------------------- AK 56 (spec §13): the merge boundary of child 3

    // On an empty, migrated database an enqueue for a previously unknown single preview emote leads to
    // one emote row, one base slot and a snapshot with a non-null SlotId; a real EnqueueAsync →
    // ReplaceBlockAsync run writes the usage row with exactly that SlotId.
    [Fact]
    public async Task Ak56_EnqueueOfAnUnknownEmote_CreatesOneBaseSlot_AndTheBlockWritesExactlyThatSlot()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfak56", createdAt: NowUtc.AddDays(-5));
        await SeedUserAsync();
        await using (var empty = CreateDbContext())
        {
            Assert.False(await empty.Emotes.AnyAsync());
            Assert.False(await empty.EmoteSlots.AnyAsync());
            Assert.False(await empty.UsageStats.AnyAsync());
        }

        PreviewReturns(SetB, "Set B", Member("n1", "BNew1", new DateTime(2026, 5, 2, 18, 30, 0, 123, DateTimeKind.Utc)));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 1)).Status);

        EmoteSlot slot;
        ChatLogBackfillRunEmote entry;
        await using (var verify = CreateDbContext())
        {
            var emote = await verify.Emotes.AsNoTracking().SingleAsync();
            slot = await verify.EmoteSlots.AsNoTracking().SingleAsync();
            entry = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
            Assert.Equal((emote.Id, "BNew1", true, (DateTime?)null), (slot.EmoteId, slot.Name, slot.IsArchived, slot.ArchivedAt));
            Assert.Equal(((DateTime?)null, false, true, NowUtc), (slot.AddedAt, slot.AddedAtFromRest, slot.SeedUnsettled, slot.LastSyncedAt));
            Assert.Equal((emote.Id, (string?)slot.Id, true, true), (entry.EmoteId, entry.SlotId, entry.CreatedSlot, entry.CreatedRow));
        }

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            var claim = await service.ClaimNextAsync(NowUtc);
            Assert.NotNull(claim);
            var snapshot = await service.GetSnapshotAsync(claim.RunId);
            var counted = Assert.Single(snapshot.Emotes);
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await service.ReplaceBlockAsync(
                claim.RunId, claim.WindowFrom, claim.WindowFrom.AddDays(7),
                [new ChatLogBackfillAggregate(counted.SlotId, counted.EmoteId, claim.WindowFrom, 3, 1, 0)], 100, 5, isLastBlock: false));
        }

        await using (var verify = CreateDbContext())
        {
            var row = await verify.UsageStats.AsNoTracking().SingleAsync();
            Assert.Equal(
                ((string?)slot.Id, entry.EmoteId, SetB, 3, 1, UsageStatSource.ChatLogArchive),
                (row.SlotId, row.EmoteId, row.EmoteSetId, row.UseCount, row.BotUseCount, row.Source));
            Assert.Equal(1, await verify.EmoteSlots.CountAsync());
        }
    }

    // ---------------------------------------------------------------- Enqueue binding (E11)

    // Rule (2): an emote whose slot already carries rows of the run's set is bound to it (nothing
    // created); an emote with a seed of another set only gets a new seed for the run's set, and the
    // other set's seed stays untouched (a seed carries one set only, E6a).
    [Fact]
    public async Task Enqueue_BindsTheSlotCarryingRowsOfTheSet_AndCreatesASeedWhereOnlyAnotherSetHasOne()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfbindbase");
        await SeedUserAsync();
        var known = await SeedEmoteAsync(channel, "e-known", "Known");
        var otherSetOnly = await SeedEmoteAsync(channel, "e-other", "Other");
        await using (var db = CreateDbContext())
        {
            db.UsageStats.AddRange(
                new UsageStat { EmoteId = known.Id, EmoteSetId = SetB, Date = new DateOnly(2026, 9, 1), UseCount = 4 },
                new UsageStat { EmoteId = otherSetOnly.Id, EmoteSetId = ActiveSetId, Date = new DateOnly(2026, 9, 1), UseCount = 2 });
            await db.AssignUsageSlotsAsync();
            await db.SaveChangesAsync();
        }

        var seedOfB = await SlotOfRowAsync(known.Id, SetB);
        var seedOfActive = await SlotOfRowAsync(otherSetOnly.Id, ActiveSetId);
        PreviewReturns(SetB, "Set B", Member("e-known", "BKnown", null), Member("e-other", "BOther", null));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 6)).Status);

        await using var verify = CreateDbContext();
        var snapshot = await verify.ChatLogBackfillRunEmotes.AsNoTracking().ToDictionaryAsync(e => e.EmoteId);
        Assert.Equal(((string?)seedOfB, false), (snapshot[known.Id].SlotId, snapshot[known.Id].CreatedSlot));
        Assert.True(snapshot[otherSetOnly.Id].CreatedSlot);
        Assert.NotEqual(seedOfActive, snapshot[otherSetOnly.Id].SlotId);
        var created = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == snapshot[otherSetOnly.Id].SlotId);
        Assert.Equal((otherSetOnly.Id, true, (DateTime?)null), (created.EmoteId, created.SeedUnsettled, created.AddedAt));
        Assert.Equal(1, await verify.EmoteSlots.CountAsync(s => s.EmoteId == known.Id));
        Assert.Equal(2, await verify.EmoteSlots.CountAsync(s => s.EmoteId == otherSetOnly.Id));
    }

    // Rule (1): a dated entry binds to the authoritative slot with exactly its addedAt (millisecond
    // precision), whatever set it counts for and ahead of rule (2): of two slots with that date the one
    // with the entry's name, else the first in D2 order (ordinal: "Zed" before "alpha"). A dated entry
    // without such a slot falls through to rule (2), as does an undated one.
    [Fact]
    public async Task Enqueue_BindsADatedEntryToTheAuthoritativeSlotOfExactlyThatDate()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfbinddated");
        await SeedUserAsync();
        var added = new DateTime(2026, 4, 20, 9, 0, 0, 123, DateTimeKind.Utc);
        var byName = await SeedEmoteAsync(channel, "e-name", "NameNow");
        var byOrder = await SeedEmoteAsync(channel, "e-order", "OrderNow");
        var otherDate = await SeedEmoteAsync(channel, "e-date", "DateNow");
        var nameMatch = await SeedSlotAsync(byName.Id, "BName", added);
        await SeedSlotAsync(byName.Id, "AnotherName", added);
        var zed = await SeedSlotAsync(byOrder.Id, "Zed", added);
        await SeedSlotAsync(byOrder.Id, "alpha", added);
        var elsewhere = await SeedSlotAsync(otherDate.Id, "BDate", added.AddMilliseconds(1));

        // A slot carrying rows of the run's set would win rule (2); rule (1) comes first.
        await using (var db = CreateDbContext())
        {
            db.UsageStats.Add(new UsageStat { EmoteId = byName.Id, EmoteSetId = SetB, Date = new DateOnly(2026, 9, 1), UseCount = 1 });
            await db.AssignUsageSlotsAsync();
            await db.SaveChangesAsync();
        }

        PreviewReturns(SetB, "Set B", Member("e-name", "BName", added), Member("e-order", "BOrder", added), Member("e-date", "BDate", added));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 6)).Status);

        await using var verify = CreateDbContext();
        var snapshot = await verify.ChatLogBackfillRunEmotes.AsNoTracking().ToDictionaryAsync(e => e.EmoteId);
        Assert.Equal(((string?)nameMatch, false), (snapshot[byName.Id].SlotId, snapshot[byName.Id].CreatedSlot));
        Assert.Equal(((string?)zed, false), (snapshot[byOrder.Id].SlotId, snapshot[byOrder.Id].CreatedSlot));
        Assert.NotEqual(elsewhere, snapshot[otherDate.Id].SlotId);
        Assert.True(snapshot[otherDate.Id].CreatedSlot);
        Assert.True((await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == snapshot[otherDate.Id].SlotId)).SeedUnsettled);
    }

    // Rule (b) of the base slot rule through the enqueue: a follow-up run of the same set reuses the
    // row-less seed a previous run created; a run of another set never does.
    [Fact]
    public async Task Enqueue_AFollowUpRunOfTheSameSetReusesTheRowlessSeed_AnotherSetGetsItsOwn()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfbindreuse");
        await SeedUserAsync();
        PreviewReturns(SetB, "Set B", Member("n1", "BNew1", null));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 6)).Status);
        var first = await SingleSnapshotEntryAsync();
        Assert.True(first.CreatedSlot);
        await CancelAsync(channel);

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 6)).Status);
        var again = await SingleSnapshotEntryAsync(newest: true);
        Assert.Equal((first.SlotId, false), (again.SlotId, again.CreatedSlot));
        await CancelAsync(channel);

        PreviewReturns(SetC, "Set C", Member("n1", "CNew1", null));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetC, months: 6)).Status);
        var otherSet = await SingleSnapshotEntryAsync(newest: true);
        Assert.True(otherSet.CreatedSlot);
        Assert.NotEqual(first.SlotId, otherSet.SlotId);

        await using var verify = CreateDbContext();
        Assert.Equal(2, await verify.EmoteSlots.CountAsync());
    }

    // Plan #373 revision 2 (arbiter decision 12.10.): the enqueue is a second seed creator, so rule (b)
    // must find a seed that lost every binding while counts are still buffered under it. Seed S of
    // (E, B) carries only imported rows; retention deletes its run with the snapshot; the map hands out
    // S (rule (a)); a block of a run of set C deletes S's imported rows channel-wide; a new enqueue of B
    // runs before the buffered S counts are flushed. It must bind S, not create S2 next to it — else
    // flush phase 1 writes on S and the run imports on S2, and two slots of the pair carry rows (E6a).
    [Fact]
    public async Task Enqueue_ReusesAnOrphanedSeedWithCountsStillBufferedUnderIt_SoThePairKeepsOneSlot()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bforphanseed", createdAt: NowUtc.AddDays(-20), activeSetId: SetB);
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "e1", "E1");
        var other = await SeedEmoteAsync(channel, "e2", "E2");

        // 1. A run of B creates S and imports onto it (both blocks, so it completes); retention later
        // deletes the finished run, and its snapshot rows go with it (cascade).
        PreviewReturns(SetB, "Set B", Member("e1", "BE1", null));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 1)).Status);
        var seed = (await SingleSnapshotEntryAsync()).SlotId!;
        var firstRun = await ClaimAsync();
        await ReplaceBlockOfAsync(firstRun, 0, new ChatLogBackfillAggregate(seed, emote.Id, firstRun.WindowFrom, 3, 0, 0));
        await ReplaceBlockOfAsync(firstRun, 1, new ChatLogBackfillAggregate(seed, emote.Id, firstRun.WindowFrom.AddDays(8), 2, 0, 0));
        await using (var db = CreateDbContext())
        {
            Assert.Equal(ChatLogBackfillRunStatus.Completed, (await db.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == firstRun.RunId)).Status);
            Assert.Equal(1, await db.ChatLogBackfillRuns.Where(r => r.Id == firstRun.RunId).ExecuteDeleteAsync());
            Assert.False(await db.ChatLogBackfillRunEmotes.AnyAsync());
        }

        // 2. The flag-off map finds S through rule (a); chat counts are buffered under the real key S.
        var buffered = new UsageCounterKey(seed, SetB);
        await using (var db = CreateDbContext())
        {
            Assert.Equal(seed, (await BaseSlotRule.FindAsync(db, [new(emote.Id, SetB)], CancellationToken.None))[new(emote.Id, SetB)]);
        }

        // 3. A run of C on the same channel deletes the channel's imported rows in its blocks, S's included.
        PreviewReturns(SetC, "Set C", Member("e2", "CE2", null));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetC, months: 1)).Status);
        var slotOfC = (await SingleSnapshotEntryAsync(newest: true)).SlotId;
        var runOfC = await ClaimAsync();
        await ReplaceBlockOfAsync(runOfC, 0, new ChatLogBackfillAggregate(slotOfC, other.Id, runOfC.WindowFrom, 1, 0, 0));
        await ReplaceBlockOfAsync(runOfC, 1, new ChatLogBackfillAggregate(slotOfC, other.Id, runOfC.WindowFrom.AddDays(8), 1, 0, 0));
        await using (var db = CreateDbContext())
        {
            Assert.False(await db.UsageStats.AnyAsync(u => u.SlotId == seed));
            Assert.Empty(await BaseSlotRule.FindAsync(db, [new(emote.Id, SetB)], CancellationToken.None));
        }

        // 4. Before the buffered S counts are flushed, a new run of B is enqueued.
        PreviewReturns(SetB, "Set B", Member("e1", "BE1", null));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 1)).Status);
        var entry = await SingleSnapshotEntryAsync(newest: true);
        Assert.Equal(((string?)seed, false), (entry.SlotId, entry.CreatedSlot));

        // 5. The flush writes the buffered counts onto S (phase 1), the run imports onto its snapshot slot.
        await using (var db = CreateDbContext())
        {
            var result = await new UsageStatFlushService(db, NullLogger<UsageStatFlushService>.Instance).FlushAsync(
                new Dictionary<UsageCounterKey, EmoteUsageCounts> { [buffered] = new(4, 0, 0) });
            Assert.Empty(result.Requeued);
        }

        var secondRun = await ClaimAsync();
        await ReplaceBlockOfAsync(secondRun, 0, new ChatLogBackfillAggregate(entry.SlotId, emote.Id, secondRun.WindowFrom, 5, 0, 0));

        await using var verify = CreateDbContext();
        var slotsWithRows = await verify.UsageStats
            .Where(u => u.EmoteId == emote.Id && u.EmoteSetId == SetB)
            .Select(u => u.SlotId)
            .Distinct()
            .ToListAsync();
        Assert.Equal([seed], slotsWithRows);
        Assert.Equal(2, await verify.UsageStats.CountAsync(u => u.SlotId == seed));
        Assert.Equal(1, await verify.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
    }

    // E6 / plan 6.2: the enqueue and the real usage flush resolving a pending key (phase 2: its own
    // locks, then the pair's first usage row on the seed) for the same pair end with one seed, whichever
    // starts first. Flush first: the enqueue waits at the channel row, then binds the flush's seed
    // through rule (a). Enqueue first: the flush waits, then finds the enqueue's row-less seed through
    // rule (b).
    [Fact]
    public async Task Enqueue_AndAFlushCreatingTheSameSeed_EndWithOneSeed_WhenTheFlushStartsFirst()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfseedflushfirst", activeSetId: SetB);
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "e1", "E1");
        PreviewReturns(SetB, "Set B", Member("e1", "BE1", null));

        const string tag = "bfseedflushfirst-enq";
        await using var enqueueDb = fixture.CreateTaggedDbContext(tag, DatabaseName);
        Task<ChatLogBackfillEnqueueResult>? enqueue = null;

        // Right after phase 2's upsert, before its commit: the seed exists and carries the row, but only
        // inside the flush's transaction.
        var hook = new CommandHook(
            sql => sql.Contains("INSERT INTO \"UsageStats\"", StringComparison.Ordinal),
            async _ =>
            {
                enqueue = Task.Run(() => CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor));
                await fixture.WaitUntilBlockedOnLockAsync(tag, enqueue);
            });
        await using (var flush = fixture.CreateTaggedDbContext("bfseedflushfirst-flush", [hook], databaseName: DatabaseName))
        {
            var result = await new UsageStatFlushService(flush, NullLogger<UsageStatFlushService>.Instance).FlushAsync(
                new Dictionary<UsageCounterKey, EmoteUsageCounts> { [new UsageCounterKey(PendingSlotKey.For(emote.Id), SetB)] = new(1, 0, 0) });
            Assert.Empty(result.Requeued);
        }

        Assert.NotNull(enqueue);
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);

        var seed = await SlotOfRowAsync(emote.Id, SetB);
        var entry = await SingleSnapshotEntryAsync();
        Assert.Equal((seed, false), (entry.SlotId, entry.CreatedSlot));
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
    }

    [Fact]
    public async Task Enqueue_AndAFlushCreatingTheSameSeed_EndWithOneSeed_WhenTheEnqueueStartsFirst()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfseedenqfirst", activeSetId: SetB);
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "e1", "E1");
        PreviewReturns(SetB, "Set B", Member("e1", "BE1", null));

        const string tag = "bfseedenqfirst-flush";
        await using var flush = fixture.CreateTaggedDbContext(tag, DatabaseName);
        Task<UsageFlushResult>? flushing = null;

        // Right after the snapshot rows are inserted, before the enqueue commits: the seed exists and is
        // bound, but only inside the enqueue's transaction.
        var hook = new CommandHook(
            sql => sql.Contains("INSERT INTO \"ChatLogBackfillRunEmotes\"", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence != 1)
                {
                    return;
                }

                flushing = Task.Run(() => new UsageStatFlushService(flush, NullLogger<UsageStatFlushService>.Instance).FlushAsync(
                    new Dictionary<UsageCounterKey, EmoteUsageCounts> { [new UsageCounterKey(PendingSlotKey.For(emote.Id), SetB)] = new(1, 0, 0) }));
                await fixture.WaitUntilBlockedOnLockAsync(tag, flushing);
            });
        await using (var enqueueDb = fixture.CreateTaggedDbContext("bfseedenqfirst-enq", [hook], databaseName: DatabaseName))
        {
            Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued,
                (await CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor)).Status);
        }

        Assert.NotNull(flushing);
        Assert.Empty((await flushing).Requeued);
        var entry = await SingleSnapshotEntryAsync();
        Assert.True(entry.CreatedSlot);
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
        Assert.Equal(entry.SlotId, (await verify.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == emote.Id)).SlotId);
    }

    // ---------------------------------------------------------------- ReplaceBlockAsync with the snapshot's slot (§4, E11a)

    // The existence stage after the pair stage (D25, plan #375 E9): an aggregate that is a snapshot row
    // of the run, but whose slot was deleted or (by now) belongs to another emote, is dropped with the
    // existing warning, never a failure; the rest of the block is written. Since M3 a snapshot row can
    // neither lack a slot nor share one with another row of its run (the key is (RunId, SlotId)): the
    // foreign slot is one of an emote outside the snapshot, and the NULL case of runs from before #373
    // is M3's (SwitchBackfillSnapshotToSlotKeyMigrationTests).
    [Fact]
    public async Task ReplaceBlock_DropsAggregatesWhoseSnapshotSlotIsMissingOrAnotherEmotes()
    {
        var channel = await SeedChannelAsync("bfreplaceslots", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var kept = await SeedEmoteAsync(channel, "e1", "E1");
        var slotDeleted = await SeedEmoteAsync(channel, "e2", "E2");
        var slotForeign = await SeedEmoteAsync(channel, "e4", "E4");
        var outsider = await SeedEmoteAsync(channel, "e5", "E5");
        var outsiderSlot = await SeedSlotAsync(outsider.Id, "E5", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksTotal: 3, windowFrom: BlockFrom);
        var slots = await BindSnapshotAsync(run, kept, slotDeleted, slotForeign);
        await using (var db = CreateDbContext())
        {
            await db.EmoteSlots.Where(s => s.Id == slots[slotDeleted.Id]).ExecuteDeleteAsync();
            await db.ChatLogBackfillRunEmotes.Where(e => e.RunId == run.Id && e.EmoteId == slotForeign.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.SlotId, outsiderSlot));
        }

        var logger = new RecordingLogger<ChatLogBackfillService>();
        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await CreateService(db, logger: logger).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo,
                [
                    new ChatLogBackfillAggregate(slots[kept.Id], kept.Id, BlockFrom, 2, 0, 0),
                    new ChatLogBackfillAggregate(slots[slotDeleted.Id], slotDeleted.Id, BlockFrom, 2, 0, 0),
                    new ChatLogBackfillAggregate(outsiderSlot, slotForeign.Id, BlockFrom, 2, 0, 0),
                ],
                10, 1, isLastBlock: false));
        }

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("2 aggregates", StringComparison.Ordinal));
        await using var verify = CreateDbContext();
        var row = await verify.UsageStats.AsNoTracking().SingleAsync();
        Assert.Equal((kept.Id, (string?)slots[kept.Id], 2), (row.EmoteId, row.SlotId, row.UseCount));
        Assert.Equal(1, (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).WeeksDone);
    }

    // E11a, settle first: a writer holding the emote row FOR NO KEY UPDATE (the settle moving the
    // snapshot rows from seed S to target T) makes the block wait at its FOR SHARE, before it reads the
    // snapshot; after that commit the block, counted on S, finds S gone from the snapshot and answers
    // SnapshotChanged without writing (plan #375 E9); handed in on T it commits there. Mutation probe:
    // without the FOR SHARE the block never waits and writes on S.
    [Fact]
    public async Task ReplaceBlock_WaitsForAWriterHoldingTheEmoteRow_AndThenSeesTheSlotItMovedTheSnapshotTo()
    {
        var channel = await SeedChannelAsync("bfshareafter", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var emote = await SeedEmoteAsync(channel, "e1", "E1");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksTotal: 3, windowFrom: BlockFrom);
        var seed = (await BindSnapshotAsync(run, emote))[emote.Id];
        var target = await SeedSlotAsync(emote.Id, "E1", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        await using var settle = CreateDbContext();
        await using var settleTransaction = await settle.Database.BeginTransactionAsync();
        await settle.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);
        await settle.LockEmotesByIdsAsync([emote.Id], CancellationToken.None);
        await settle.ChatLogBackfillRunEmotes.Where(e => e.SlotId == seed)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.SlotId, target).SetProperty(e => e.CreatedSlot, false));

        const string tag = "bfshareafter-block";
        await using var blockDb = fixture.CreateTaggedDbContext(tag, DatabaseName);
        var block = Task.Run(() => CreateService(blockDb).ReplaceBlockAsync(
            run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(seed, emote.Id, BlockFrom, 5, 0, 0)], 10, 1, isLastBlock: false));
        await fixture.WaitUntilBlockedOnLockAsync(tag, block);
        Assert.Contains("FOR SHARE", await fixture.WaitingQueryAsync(tag), StringComparison.Ordinal);

        await settleTransaction.CommitAsync();
        Assert.IsType<ChatLogBackfillBlockResult.SnapshotChanged>(await block);
        await using (var check = CreateDbContext())
        {
            Assert.False(await check.UsageStats.AnyAsync(u => u.EmoteId == emote.Id));
            Assert.Equal(0, (await check.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).WeeksDone);
        }

        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await CreateService(db).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(target, emote.Id, BlockFrom, 5, 0, 0)], 10, 1, isLastBlock: false));
        }

        await using var verify = CreateDbContext();
        Assert.Equal(target, (await verify.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == emote.Id)).SlotId);
    }

    // E11a, block first: the block holds the emote row FOR SHARE from before its snapshot read until its
    // commit, so a FOR NO KEY UPDATE writer of that emote (the settle) waits and then sees the block's
    // fresh rows — the ones it has to move along.
    [Fact]
    public async Task ReplaceBlock_HoldsTheEmoteRowUntilItsCommit_SoAWriterWaitsAndSeesTheBlocksRows()
    {
        var channel = await SeedChannelAsync("bfsharebefore", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var emote = await SeedEmoteAsync(channel, "e1", "E1");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksTotal: 3, windowFrom: BlockFrom);
        var seed = (await BindSnapshotAsync(run, emote))[emote.Id];
        var target = await SeedSlotAsync(emote.Id, "E1", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var atInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var blockDb = CreateDbContext(new PauseBeforeCommand("INSERT INTO \"UsageStats\"", atInsert, proceed));
        var block = Task.Run(() => CreateService(blockDb).ReplaceBlockAsync(
            run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(seed, emote.Id, BlockFrom, 5, 0, 0)], 10, 1, isLastBlock: false));
        await atInsert.Task;

        const string tag = "bfsharebefore-settle";
        await using var settle = fixture.CreateTaggedDbContext(tag, DatabaseName);
        var moving = Task.Run(async () =>
        {
            await using var transaction = await settle.Database.BeginTransactionAsync();
            await settle.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);
            await settle.LockEmotesByIdsAsync([emote.Id], CancellationToken.None);
            var moved = await settle.UsageStats.Where(u => u.SlotId == seed).ExecuteUpdateAsync(s => s.SetProperty(u => u.SlotId, target));
            await transaction.CommitAsync();
            return moved;
        });
        await fixture.WaitUntilBlockedOnLockAsync(tag, moving);
        Assert.Contains("FOR NO KEY UPDATE", await fixture.WaitingQueryAsync(tag), StringComparison.Ordinal);

        proceed.SetResult();
        Assert.IsType<ChatLogBackfillBlockResult.Committed>(await block);
        Assert.Equal(1, await moving);

        await using var verify = CreateDbContext();
        Assert.Equal(target, (await verify.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == emote.Id)).SlotId);
    }

    // ---------------------------------------------------------------- The settle carries the snapshot along (E10, E11a)

    // Plan #373 §6.3 E11a, in sequence: the enqueue binds the snapshot to a new seed S next to the REST
    // slot T, block 1 writes on S, the real flag-on settle finds T ("target exists"), merges S's rows
    // onto T — one cell collides with a live row of T (E10) — and rebinds the snapshot to T with
    // CreatedSlot false; block 2, still counted on S, is refused as SnapshotChanged (plan #375 E9), and
    // after the worker's re-read — the row (EmoteId, Name) now on T — is handed in on T and written there;
    // the next tick leaves nothing on S.
    [Fact]
    public async Task Settle_OntoAnExistingTarget_MergesTheBlocksRows_AndTheNextBlockWritesOnTheTarget()
    {
        PinFlagOff();
        var (channel, emote, target, seed, claim) = await EnqueueNextToRestSlotAsync("bfsettleseq");
        var collidingLive = new UsageStat
        {
            EmoteId = emote.Id,
            SlotId = target,
            EmoteSetId = SetB,
            Date = claim.WindowFrom,
            UseCount = 10,
            BotUseCount = 2,
            SharedChatUseCount = 1,
            Source = UsageStatSource.Live
        };
        await using (var db = CreateDbContext())
        {
            db.UsageStats.Add(collidingLive);
            await db.SaveChangesAsync();
        }

        await ReplaceBlockOfAsync(claim, 0, new ChatLogBackfillAggregate(seed, emote.Id, claim.WindowFrom, 3, 1, 0));
        await using (var db = CreateDbContext())
        {
            var imported = await db.UsageStats.AsNoTracking().SingleAsync(u => u.Source == UsageStatSource.ChatLogArchive);
            Assert.Equal((seed, claim.WindowFrom), (imported.SlotId, imported.Date));
        }

        var logger = new RecordingLogger<SevenTvSyncService>();
        await SettleAsync(channel, logger);

        await using (var verify = CreateDbContext())
        {
            var entry = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
            Assert.Equal(((string?)target, false), (entry.SlotId, entry.CreatedSlot));
            var settled = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == seed);
            Assert.Equal((false, true, (DateTime?)null), (settled.SeedUnsettled, settled.IsArchived, settled.ArchivedAt));
            Assert.False(await verify.UsageStats.AnyAsync(u => u.SlotId == seed));
            var merged = await verify.UsageStats.AsNoTracking().SingleAsync();
            Assert.Equal(
                (collidingLive.Id, target, claim.WindowFrom, 13, 3, 1, UsageStatSource.Live),
                (merged.Id, merged.SlotId, merged.Date, merged.UseCount, merged.BotUseCount, merged.SharedChatUseCount, merged.Source));
        }

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("settle merged 1 usage rows", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.Contains("1 backfill snapshot entries", StringComparison.Ordinal));

        var secondDay = claim.WindowFrom.AddDays(8);
        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            Assert.IsType<ChatLogBackfillBlockResult.SnapshotChanged>(
                await ReplaceBlockOfAsync(db, claim, 1, new ChatLogBackfillAggregate(seed, emote.Id, secondDay, 4, 0, 0)));
            var reread = Assert.Single((await service.GetSnapshotAsync(claim.RunId)).Emotes);
            Assert.Equal((target, emote.Id, "A"), (reread.SlotId, reread.EmoteId, reread.Name));
        }

        await ReplaceBlockOfAsync(claim, 1, new ChatLogBackfillAggregate(target, emote.Id, secondDay, 4, 0, 0));
        Assert.Equal(target, await SlotOfRowOnAsync(emote.Id, secondDay));

        await SettleAsync(channel, logger: null);
        await using var final = CreateDbContext();
        Assert.False(await final.UsageStats.AnyAsync(u => u.SlotId == seed));
        Assert.Equal(2, await final.UsageStats.CountAsync(u => u.SlotId == target));
        Assert.Equal(ChatLogBackfillRunStatus.Completed, (await final.ChatLogBackfillRuns.AsNoTracking().SingleAsync()).Status);
    }

    // E11a, settle first: the real settle holds the emote row from its lock until its commit and has
    // already rebound the snapshot; block 2, counted on S, waits at its FOR SHARE, before it reads the
    // snapshot, and after the settle's commit sees the rebound row: SnapshotChanged, nothing written
    // (plan #375 E9). Handed in again on T, it writes there.
    [Fact]
    public async Task Settle_FirstHoldingTheEmoteRow_TheWaitingBlockSeesTheReboundSnapshot_AndIsWrittenOnTheTargetWhenHandedInAgain()
    {
        PinFlagOff();
        var (channel, emote, target, seed, claim) = await EnqueueNextToRestSlotAsync("bfsettlefirst");
        await ReplaceBlockOfAsync(claim, 0, new ChatLogBackfillAggregate(seed, emote.Id, claim.WindowFrom, 3, 0, 0));

        const string tag = "bfsettlefirst-block";
        var secondDay = claim.WindowFrom.AddDays(8);
        Task<ChatLogBackfillBlockResult>? block = null;
        var hook = new CommandHook(
            sql => sql.Contains("UPDATE \"ChatLogBackfillRunEmotes\"", StringComparison.Ordinal),
            async _ =>
            {
                var blockDb = fixture.CreateTaggedDbContext(tag, DatabaseName);
                block = Task.Run(async () =>
                {
                    await using (blockDb)
                    {
                        return await ReplaceBlockOfAsync(blockDb, claim, 1, new ChatLogBackfillAggregate(seed, emote.Id, secondDay, 4, 0, 0));
                    }
                });
                await fixture.WaitUntilBlockedOnLockAsync(tag, block);
                Assert.Contains("FOR SHARE", await fixture.WaitingQueryAsync(tag), StringComparison.Ordinal);
            });
        await using (var settleDb = fixture.CreateTaggedDbContext("bfsettlefirst-settle", [hook], databaseName: DatabaseName))
        {
            Assert.NotNull(await SlotSyncKit.CreateService(settleDb, RestOf(channel), slotsEnabled: true).SyncChannelAsync(channel.ChannelName));
        }

        Assert.NotNull(block);
        Assert.IsType<ChatLogBackfillBlockResult.SnapshotChanged>(await block);
        await using (var check = CreateDbContext())
        {
            Assert.False(await check.UsageStats.AnyAsync(u => u.EmoteId == emote.Id && u.Date == secondDay));
        }

        await ReplaceBlockOfAsync(claim, 1, new ChatLogBackfillAggregate(target, emote.Id, secondDay, 4, 0, 0));
        await using var verify = CreateDbContext();
        Assert.Equal(target, await SlotOfRowOnAsync(emote.Id, secondDay));
        Assert.Equal(target, await SlotOfRowOnAsync(emote.Id, claim.WindowFrom));
        Assert.False(await verify.UsageStats.AnyAsync(u => u.SlotId == seed));
        var entry = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
        Assert.Equal(((string?)target, false), (entry.SlotId, entry.CreatedSlot));
    }

    // E11a, block first: block 1 holds the emote row FOR SHARE until its commit, so the real settle
    // waits at its emote lock — when the seed still has no row at all — and afterwards finds the seed
    // in the active set's population through the block's fresh rows and moves them, with the snapshot.
    [Fact]
    public async Task Block_FirstHoldingTheEmoteRow_TheWaitingSettleMovesTheBlocksFreshRowsAndTheSnapshot()
    {
        PinFlagOff();
        var (channel, emote, target, seed, claim) = await EnqueueNextToRestSlotAsync("bfblockfirst");

        var atInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var blockDb = CreateDbContext(new PauseBeforeCommand("INSERT INTO \"UsageStats\"", atInsert, proceed));
        var block = Task.Run(() => ReplaceBlockOfAsync(blockDb, claim, 0, new ChatLogBackfillAggregate(seed, emote.Id, claim.WindowFrom, 3, 0, 0)));
        await atInsert.Task;

        const string tag = "bfblockfirst-settle";
        await using var settleDb = fixture.CreateTaggedDbContext(tag, DatabaseName);
        var settle = Task.Run(() => SlotSyncKit.CreateService(settleDb, RestOf(channel), slotsEnabled: true).SyncChannelAsync(channel.ChannelName));
        await fixture.WaitUntilBlockedOnLockAsync(tag, settle);
        Assert.Contains("FOR NO KEY UPDATE OF e", await fixture.WaitingQueryAsync(tag), StringComparison.Ordinal);

        proceed.SetResult();
        Assert.IsType<ChatLogBackfillBlockResult.Committed>(await block);
        Assert.NotNull(await settle);

        await using (var verify = CreateDbContext())
        {
            Assert.Equal(target, await SlotOfRowOnAsync(emote.Id, claim.WindowFrom));
            Assert.False(await verify.UsageStats.AnyAsync(u => u.SlotId == seed));
            var entry = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
            Assert.Equal(((string?)target, false), (entry.SlotId, entry.CreatedSlot));
            var settled = await verify.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == seed);
            Assert.Equal((false, true, (DateTime?)null), (settled.SeedUnsettled, settled.IsArchived, settled.ArchivedAt));
        }

        // The worker's next block counts on the re-read snapshot's slot.
        var secondDay = claim.WindowFrom.AddDays(8);
        await ReplaceBlockOfAsync(claim, 1, new ChatLogBackfillAggregate(target, emote.Id, secondDay, 4, 0, 0));
        Assert.Equal(target, await SlotOfRowOnAsync(emote.Id, secondDay));
    }

    // E11a: an in-place settle (the seed becomes the slot itself, same id) leaves the snapshot alone —
    // it still points at the slot the run created.
    [Fact]
    public async Task Settle_InPlace_LeavesTheSnapshotUnchanged()
    {
        PinFlagOff();
        var channel = await SeedChannelAsync("bfsettleinplace", createdAt: NowUtc.AddDays(-20), activeSetId: SetB);
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "x", "A");
        PreviewReturns(SetB, "Set B", Member("x", "A", null));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 1)).Status);
        var claim = await ClaimAsync();
        var before = await SingleSnapshotEntryAsync();
        await ReplaceBlockOfAsync(claim, 0, new ChatLogBackfillAggregate(before.SlotId, emote.Id, claim.WindowFrom, 3, 0, 0));

        await SettleAsync(channel, logger: null);

        var after = await SingleSnapshotEntryAsync();
        Assert.Equal((before.SlotId, true), (after.SlotId, after.CreatedSlot));
        await using var verify = CreateDbContext();
        var slot = await verify.EmoteSlots.AsNoTracking().SingleAsync();
        Assert.Equal((before.SlotId, false, false), ((string?)slot.Id, slot.SeedUnsettled, slot.IsArchived));
        Assert.Equal(slot.Id, await SlotOfRowOnAsync(emote.Id, claim.WindowFrom));
    }

    // ---------------------------------------------------------------- Helpers

    private async Task<string> SeedSlotAsync(string emoteId, string name, DateTime addedAt)
    {
        await using var db = CreateDbContext();
        var slot = new EmoteSlot { EmoteId = emoteId, Name = name, AddedAt = addedAt, AddedAtFromRest = true, LastSyncedAt = NowUtc };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    // The REST slot's date; the live entry of the settle carries it, so the settle binds to that slot.
    private static readonly DateTime RestSlotAddedAt = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    // A channel whose active set is the run's set B, one active emote with a dated REST slot T that
    // carries no rows of B, and a real enqueue of B with an undated preview entry: the enqueue cannot
    // bind T (no rows of B, no date to match), so it creates the seed S. Then the run is claimed; its
    // window of ten days makes two blocks.
    private async Task<(Channel Channel, Emote Emote, string Target, string Seed, ChatLogBackfillClaim Claim)> EnqueueNextToRestSlotAsync(string name)
    {
        var channel = await SeedChannelAsync(name, createdAt: NowUtc.AddDays(-20), activeSetId: SetB);
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "x", "A");
        var target = await SeedSlotAsync(emote.Id, "A", RestSlotAddedAt);
        PreviewReturns(SetB, "Set B", Member("x", "A", null));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 1)).Status);

        var entry = await SingleSnapshotEntryAsync();
        Assert.NotEqual(target, entry.SlotId);
        Assert.True(entry.CreatedSlot);
        var claim = await ClaimAsync();
        Assert.Equal(2, claim.WeeksTotal);
        return (channel, emote, target, entry.SlotId!, claim);
    }

    private async Task<ChatLogBackfillClaim> ClaimAsync()
    {
        await using var db = CreateDbContext();
        var claim = await CreateService(db).ClaimNextAsync(NowUtc);
        Assert.NotNull(claim);
        return claim;
    }

    private async Task ReplaceBlockOfAsync(ChatLogBackfillClaim claim, int index, ChatLogBackfillAggregate row)
    {
        await using var db = CreateDbContext();
        Assert.IsType<ChatLogBackfillBlockResult.Committed>(await ReplaceBlockOfAsync(db, claim, index, row));
    }

    // Block <paramref name="index"/> of the claimed run's 7-day plan, with one aggregate.
    private Task<ChatLogBackfillBlockResult> ReplaceBlockOfAsync(AppDbContext db, ChatLogBackfillClaim claim, int index, ChatLogBackfillAggregate row)
    {
        var from = claim.WindowFrom.AddDays(7 * index);
        var isLast = index == claim.WeeksTotal - 1;
        var to = isLast ? claim.WindowTo : from.AddDays(7);
        return CreateService(db).ReplaceBlockAsync(claim.RunId, from, to, [row], 10, 1, isLast);
    }

    // The real flag-on REST resync of the channel: set B with the one emote, dated like the REST slot.
    private async Task SettleAsync(Channel channel, RecordingLogger<SevenTvSyncService>? logger)
    {
        await using var db = CreateDbContext();
        Assert.NotNull(await SlotSyncKit.CreateService(db, RestOf(channel), slotsEnabled: true, logger: logger).SyncChannelAsync(channel.ChannelName));
    }

    private static ISevenTvApiClient RestOf(Channel channel) =>
        SlotSyncKit.ApiReturning(channel, SetB, Live("x", "A", RestSlotAddedAt));

    private async Task<string> SlotOfRowOnAsync(string emoteId, DateOnly date)
    {
        await using var db = CreateDbContext();
        return (await db.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == emoteId && u.Date == date)).SlotId;
    }

    private async Task<string> SlotOfRowAsync(string emoteId, string emoteSetId)
    {
        await using var db = CreateDbContext();
        return (await db.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == emoteId && u.EmoteSetId == emoteSetId)).SlotId;
    }

    private async Task<ChatLogBackfillRunEmote> SingleSnapshotEntryAsync(bool newest = false)
    {
        await using var db = CreateDbContext();
        var entries = db.ChatLogBackfillRunEmotes.AsNoTracking();
        return newest
            ? await entries.OrderByDescending(e => e.RunId).FirstAsync()
            : await entries.SingleAsync();
    }

    private async Task CancelAsync(Channel channel)
    {
        await using var db = CreateDbContext();
        Assert.Equal(ChatLogBackfillCancelResult.Cancelled, await CreateService(db).CancelAsync(channel.ChannelName, Actor));
    }
}
