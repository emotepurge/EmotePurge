using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
                [new ChatLogBackfillAggregate(counted.EmoteId, claim.WindowFrom, 3, 1, 0)], 100, 5, isLastBlock: false));
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

    // E6: the enqueue and a flush-shaped get-or-create (flush phase 2: its own locks, then a usage row on
    // the seed) for the same pair end with one seed, whichever starts first. Flush first: the enqueue
    // waits at the channel row, then binds the flush's seed through rule (a). Enqueue first: the flush
    // waits, then finds the enqueue's row-less seed through rule (b).
    [Fact]
    public async Task Enqueue_AndAFlushCreatingTheSameSeed_EndWithOneSeed_WhenTheFlushStartsFirst()
    {
        var channel = await SeedChannelAsync("bfseedflushfirst", activeSetId: SetB);
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "e1", "E1");
        PreviewReturns(SetB, "Set B", Member("e1", "BE1", null));

        await using var flush = CreateDbContext();
        await using var flushTransaction = await flush.Database.BeginTransactionAsync();
        var seed = await FlushShapedGetOrCreateAsync(flush, emote.Id, SetB);

        const string tag = "bfseedflushfirst-enq";
        await using var enqueueDb = fixture.CreateTaggedDbContext(tag, DatabaseName);
        var enqueue = Task.Run(() => CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor));
        await fixture.WaitUntilBlockedOnLockAsync(tag, enqueue);

        await flushTransaction.CommitAsync();
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);

        var entry = await SingleSnapshotEntryAsync();
        Assert.Equal((seed.SlotId, false), (entry.SlotId, entry.CreatedSlot));
        Assert.True(seed.Created);
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
    }

    [Fact]
    public async Task Enqueue_AndAFlushCreatingTheSameSeed_EndWithOneSeed_WhenTheEnqueueStartsFirst()
    {
        var channel = await SeedChannelAsync("bfseedenqfirst", activeSetId: SetB);
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "e1", "E1");
        PreviewReturns(SetB, "Set B", Member("e1", "BE1", null));

        const string tag = "bfseedenqfirst-flush";
        await using var flush = fixture.CreateTaggedDbContext(tag, DatabaseName);
        Task<(string SlotId, bool Created)>? flushing = null;

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

                flushing = Task.Run(async () =>
                {
                    await using var transaction = await flush.Database.BeginTransactionAsync();
                    var result = await FlushShapedGetOrCreateAsync(flush, emote.Id, SetB);
                    await transaction.CommitAsync();
                    return result;
                });
                await fixture.WaitUntilBlockedOnLockAsync(tag, flushing);
            });
        await using (var enqueueDb = fixture.CreateTaggedDbContext("bfseedenqfirst-enq", [hook], databaseName: DatabaseName))
        {
            Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued,
                (await CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor)).Status);
        }

        Assert.NotNull(flushing);
        var flushed = await flushing;
        var entry = await SingleSnapshotEntryAsync();
        Assert.Equal((entry.SlotId, false), (flushed.SlotId, flushed.Created));
        Assert.True(entry.CreatedSlot);
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
        Assert.Equal(entry.SlotId, (await verify.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == emote.Id)).SlotId);
    }

    // ---------------------------------------------------------------- ReplaceBlockAsync with the snapshot's slot (§4, E11a)

    // The snapshot's slot must exist and belong to the aggregate's emote: a deleted slot, a snapshot row
    // with SlotId NULL (a run from before #373) and a slot of another emote all drop the aggregate with
    // the existing warning, never a failure; the rest of the block is written.
    [Fact]
    public async Task ReplaceBlock_DropsAggregatesWhoseSnapshotSlotIsMissing_NullOrAnotherEmotes()
    {
        var channel = await SeedChannelAsync("bfreplaceslots", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var kept = await SeedEmoteAsync(channel, "e1", "E1");
        var slotDeleted = await SeedEmoteAsync(channel, "e2", "E2");
        var slotNull = await SeedEmoteAsync(channel, "e3", "E3");
        var slotForeign = await SeedEmoteAsync(channel, "e4", "E4");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksTotal: 3, windowFrom: BlockFrom);
        var slots = await BindSnapshotAsync(run, kept, slotDeleted, slotNull, slotForeign);
        await using (var db = CreateDbContext())
        {
            await db.EmoteSlots.Where(s => s.Id == slots[slotDeleted.Id]).ExecuteDeleteAsync();
            await db.ChatLogBackfillRunEmotes.Where(e => e.RunId == run.Id && e.EmoteId == slotNull.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.SlotId, (string?)null));
            await db.ChatLogBackfillRunEmotes.Where(e => e.RunId == run.Id && e.EmoteId == slotForeign.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.SlotId, slots[kept.Id]));
        }

        var logger = new RecordingLogger<ChatLogBackfillService>();
        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await CreateService(db, logger: logger).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo,
                [.. new[] { kept, slotDeleted, slotNull, slotForeign }.Select(e => new ChatLogBackfillAggregate(e.Id, BlockFrom, 2, 0, 0))],
                10, 1, isLastBlock: false));
        }

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("3 aggregates", StringComparison.Ordinal));
        await using var verify = CreateDbContext();
        var row = await verify.UsageStats.AsNoTracking().SingleAsync();
        Assert.Equal((kept.Id, (string?)slots[kept.Id], 2), (row.EmoteId, row.SlotId, row.UseCount));
        Assert.Equal(1, (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).WeeksDone);
    }

    // E11a, settle first: a writer holding the emote row FOR NO KEY UPDATE (the settle moving the
    // snapshot rows from seed S to target T) makes the block wait at its FOR SHARE, before it reads the
    // snapshot; after that commit the block writes on T. Mutation probe: without the FOR SHARE the block
    // never waits and writes on S.
    [Fact]
    public async Task ReplaceBlock_WaitsForAWriterHoldingTheEmoteRow_AndThenReadsTheSlotItMovedTheSnapshotTo()
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
            run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(emote.Id, BlockFrom, 5, 0, 0)], 10, 1, isLastBlock: false));
        await fixture.WaitUntilBlockedOnLockAsync(tag, block);
        Assert.Contains("FOR SHARE", await fixture.WaitingQueryAsync(tag), StringComparison.Ordinal);

        await settleTransaction.CommitAsync();
        Assert.IsType<ChatLogBackfillBlockResult.Committed>(await block);

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
            run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(emote.Id, BlockFrom, 5, 0, 0)], 10, 1, isLastBlock: false));
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

    // ---------------------------------------------------------------- Helpers

    // Flush phase 2's shape (plan #373, E9): the base slot rule behind its own locks, then the pair's
    // first usage row on the slot, in the caller's open transaction.
    private static async Task<(string SlotId, bool Created)> FlushShapedGetOrCreateAsync(AppDbContext db, string emoteId, string emoteSetId)
    {
        var pair = new BaseSlotPair(emoteId, emoteSetId);
        var result = await BaseSlotRule.LockAndGetOrCreateAsync(db, [pair], NowUtc, CancellationToken.None);
        var slotId = result.SlotIds[pair];
        db.UsageStats.Add(new UsageStat { SlotId = slotId, EmoteId = emoteId, EmoteSetId = emoteSetId, Date = Today, UseCount = 1 });
        await db.SaveChangesAsync();
        return (slotId, result.Created.Contains(pair));
    }

    private async Task<string> SeedSlotAsync(string emoteId, string name, DateTime addedAt)
    {
        await using var db = CreateDbContext();
        var slot = new EmoteSlot { EmoteId = emoteId, Name = name, AddedAt = addedAt, AddedAtFromRest = true, LastSyncedAt = NowUtc };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private async Task<string> SlotOfRowAsync(string emoteId, string emoteSetId)
    {
        await using var db = CreateDbContext();
        return (await db.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == emoteId && u.EmoteSetId == emoteSetId)).SlotId!;
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
