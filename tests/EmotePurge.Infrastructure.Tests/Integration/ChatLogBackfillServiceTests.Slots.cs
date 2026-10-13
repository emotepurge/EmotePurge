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

// Slot plan #373, child 3 of the emote slots epic (§4, E11, E11a): the block replace writes the snapshot's
// slot and serializes with the settle, which carries the snapshot rows of a seed along. Only a run enqueued
// before #379 can hold a seed (today's enqueue binds per preview entry, MultiSlot.cs); its block fails
// closed once the settle moved its snapshot row away from the slot it counts on (plan 379 §2.1).
public partial class ChatLogBackfillServiceTests
{
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
    // snapshot; after that commit the block, counted on S, finds S gone from the snapshot and throws
    // without writing (plan 379 §2.1, fail-closed). Mutation probe: without the FOR SHARE the block never
    // waits and writes on S.
    [Fact]
    public async Task ReplaceBlock_WaitsForAWriterHoldingTheEmoteRow_AndThenRefusesTheSlotItMovedTheSnapshotAwayFrom()
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
        await Assert.ThrowsAsync<ArgumentException>(() => block);
        await using var check = CreateDbContext();
        Assert.False(await check.UsageStats.AnyAsync(u => u.EmoteId == emote.Id));
        Assert.Equal(0, (await check.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).WeeksDone);
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

    // Plan #373 §6.3 E11a and plan 379 §2.1, in sequence: a run from before #379 has its snapshot on a
    // seed S next to the REST slot T, block 1 writes on S, the real settle finds T ("target exists"),
    // merges S's rows onto T — one cell collides with a live row of T (E10) — and rebinds the snapshot to
    // T with CreatedSlot false. Block 2, still counted on S as the worker's map has it, fails closed: it
    // throws and changes nothing, and the run does not complete.
    [Fact]
    public async Task Settle_OntoAnExistingTarget_MergesTheBlocksRows_AndTheRunsNextBlockOnTheSeedFailsClosed()
    {
        var (channel, emote, target, seed, claim) = await SeedRunOnASeedAsync("bfsettleseq", withRestSlot: true);
        var collidingLive = new UsageStat
        {
            EmoteId = emote.Id,
            SlotId = target!,
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
        var run = await RunOfAsync(claim);
        var before = await BlockStateAsync(channel, run);
        await using (var db = CreateDbContext())
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => ReplaceBlockOfAsync(db, claim, 1, new ChatLogBackfillAggregate(seed, emote.Id, secondDay, 4, 0, 0)));
        }

        Assert.Equal(before, await BlockStateAsync(channel, run));
        await using var final = CreateDbContext();
        Assert.False(await final.UsageStats.AnyAsync(u => u.SlotId == seed));
        Assert.Equal(1, await final.UsageStats.CountAsync(u => u.SlotId == target));
        var after = await final.ChatLogBackfillRuns.AsNoTracking().SingleAsync();
        Assert.Equal((ChatLogBackfillRunStatus.Running, 1), (after.Status, after.WeeksDone));
    }

    // E11a, settle first: the real settle holds the emote row from its lock until its commit and has
    // already rebound the snapshot; block 2, counted on S, waits at its FOR SHARE, before it reads the
    // snapshot, and after the settle's commit sees the rebound row: it throws, nothing written (plan 379
    // §2.1, fail-closed).
    [Fact]
    public async Task Settle_FirstHoldingTheEmoteRow_TheWaitingBlockSeesTheReboundSnapshot_AndFailsClosed()
    {
        var (channel, emote, target, seed, claim) = await SeedRunOnASeedAsync("bfsettlefirst", withRestSlot: true);
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
            Assert.NotNull(await SlotSyncKit.CreateService(settleDb, RestOf(channel)).SyncChannelAsync(channel.ChannelName));
        }

        Assert.NotNull(block);
        await Assert.ThrowsAsync<ArgumentException>(() => block);
        await using var verify = CreateDbContext();
        Assert.False(await verify.UsageStats.AnyAsync(u => u.EmoteId == emote.Id && u.Date == secondDay));
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
        var (channel, emote, target, seed, claim) = await SeedRunOnASeedAsync("bfblockfirst", withRestSlot: true);

        var atInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var blockDb = CreateDbContext(new PauseBeforeCommand("INSERT INTO \"UsageStats\"", atInsert, proceed));
        var block = Task.Run(() => ReplaceBlockOfAsync(blockDb, claim, 0, new ChatLogBackfillAggregate(seed, emote.Id, claim.WindowFrom, 3, 0, 0)));
        await atInsert.Task;

        const string tag = "bfblockfirst-settle";
        await using var settleDb = fixture.CreateTaggedDbContext(tag, DatabaseName);
        var settle = Task.Run(() => SlotSyncKit.CreateService(settleDb, RestOf(channel)).SyncChannelAsync(channel.ChannelName));
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
    }

    // E11a: an in-place settle (the seed becomes the slot itself, same id) leaves the snapshot alone —
    // it still points at the slot the run created, so a run from before #379 whose seed settles in
    // place goes on counting on it.
    [Fact]
    public async Task Settle_InPlace_LeavesTheSnapshotUnchanged()
    {
        var (channel, emote, _, _, claim) = await SeedRunOnASeedAsync("bfsettleinplace", withRestSlot: false);
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

    // The state a run enqueued before #379 left (plan 379 §2.1: an overlooked run): a channel whose active
    // set is the run's set B, one active emote — with a dated REST slot T that carries no rows of B when
    // withRestSlot — and a queued run of B whose snapshot binds a new unsettled seed S of the emote (the
    // old base slot binding; today's enqueue never binds a seed). Then the run is claimed; its window of
    // two weeks makes two blocks. Target is null without the REST slot.
    private async Task<(Channel Channel, Emote Emote, string? Target, string Seed, ChatLogBackfillClaim Claim)> SeedRunOnASeedAsync(
        string name, bool withRestSlot)
    {
        var createdAt = NowUtc.AddDays(-20);
        var channel = await SeedChannelAsync(name, createdAt: createdAt, activeSetId: SetB);
        var emote = await SeedEmoteAsync(channel, "x", "A");
        var target = withRestSlot ? await SeedSlotAsync(emote.Id, "A", RestSlotAddedAt) : null;
        var run = await SeedRunAsync(
            channel, ChatLogBackfillRunStatus.Queued, weeksTotal: 2, windowFrom: DateOnly.FromDateTime(createdAt).AddDays(-14));
        var seed = (await BindSnapshotAsync(run, emote))[emote.Id];

        var entry = await SingleSnapshotEntryAsync();
        Assert.Equal((seed, true), (entry.SlotId, entry.CreatedSlot));
        var claim = await ClaimAsync();
        Assert.Equal(2, claim.WeeksTotal);
        return (channel, emote, target, seed, claim);
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

    // The real REST resync of the channel: set B with the one emote, dated like the REST slot.
    private async Task SettleAsync(Channel channel, RecordingLogger<SevenTvSyncService>? logger)
    {
        await using var db = CreateDbContext();
        Assert.NotNull(await SlotSyncKit.CreateService(db, RestOf(channel), logger: logger).SyncChannelAsync(channel.ChannelName));
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

    private async Task<ChatLogBackfillRunEmote> SingleSnapshotEntryAsync()
    {
        await using var db = CreateDbContext();
        return await db.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
    }
}
