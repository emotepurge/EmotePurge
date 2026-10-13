using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Plan #375 §3 and 7.4 (AK 24, 25, Sol-P2-1, E9): the block is keyed per slot. Every aggregate must be a
// snapshot row (SlotId, EmoteId) of its run — checked for the whole block before anything is written —
// and a slot the snapshot no longer holds for an emote it does hold answers SnapshotChanged. The
// snapshots with two rows of one emote are seeded by hand here; the flag-on enqueue that writes them
// comes later in this child.
public partial class ChatLogBackfillServiceTests
{
    private static readonly DateTime OlderEntry = new(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewerEntry = new(2026, 5, 2, 18, 30, 0, DateTimeKind.Utc);

    // AK 24: two aliases of one emote counted on the same day are two cells — two rows with exactly
    // their own counts per category, both under the emote and the run's set.
    [Fact]
    public async Task ReplaceBlock_TwoSlotsOfOneEmoteOnOneDay_WriteTwoRows_WithTheirOwnCounts()
    {
        var channel = await SeedChannelAsync("bfslotak24", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var emote = await SeedEmoteAsync(channel, "x", "hob44");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, windowFrom: BlockFrom);
        var (ome, hob) = await SeedTwoSlotSnapshotAsync(run, emote);

        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await CreateService(db).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo,
                [new ChatLogBackfillAggregate(hob, emote.Id, BlockFrom, 2, 1, 0), new ChatLogBackfillAggregate(ome, emote.Id, BlockFrom, 5, 0, 3)],
                10, 1, isLastBlock: false));
        }

        await using var verify = CreateDbContext();
        var rows = await verify.UsageStats.AsNoTracking()
            .OrderBy(u => u.UseCount)
            .Select(u => new { u.SlotId, u.EmoteId, u.EmoteSetId, u.Date, u.UseCount, u.BotUseCount, u.SharedChatUseCount, u.Source })
            .ToListAsync();
        Assert.Equal(
            [
                new { SlotId = hob, EmoteId = emote.Id, EmoteSetId = SetB, Date = BlockFrom, UseCount = 2, BotUseCount = 1, SharedChatUseCount = 0, Source = UsageStatSource.ChatLogArchive },
                new { SlotId = ome, EmoteId = emote.Id, EmoteSetId = SetB, Date = BlockFrom, UseCount = 5, BotUseCount = 0, SharedChatUseCount = 3, Source = UsageStatSource.ChatLogArchive },
            ],
            rows);
    }

    // AK 24 / Sol-P2-1: a pair that is no snapshot row of the run throws and writes nothing — a slot the
    // snapshot holds under another emote, and an emote of the channel without any snapshot row. The
    // block's days carry an imported row and coverage of an earlier run; both stay, as do the run's
    // progress and booked bytes.
    [Theory]
    [InlineData("slot of another emote")]
    [InlineData("emote outside the snapshot")]
    public async Task ReplaceBlock_WithAPairThatIsNoSnapshotRow_Throws_AndWritesNothing(string pair)
    {
        var channel = await SeedChannelAsync("bfslotpair" + pair.Length, createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var x = await SeedEmoteAsync(channel, "x", "X");
        var y = await SeedEmoteAsync(channel, "y", "Y");
        var outside = await SeedEmoteAsync(channel, "z", "Z");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, windowFrom: BlockFrom);
        var slots = await BindSnapshotAsync(run, x, y);
        await SeedEarlierImportAsync(channel, outside);
        var outsideSlot = await SlotOfRowAsync(outside.Id, SetC);
        var before = await BlockStateAsync(channel, run);

        var stray = pair == "slot of another emote"
            ? new ChatLogBackfillAggregate(slots[y.Id], x.Id, BlockFrom, 1, 0, 0)
            : new ChatLogBackfillAggregate(outsideSlot, outside.Id, BlockFrom.AddDays(1), 1, 0, 0);
        await using (var db = CreateDbContext())
        {
            await Assert.ThrowsAsync<ArgumentException>(() => CreateService(db).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(slots[x.Id], x.Id, BlockFrom.AddDays(2), 4, 0, 0), stray], 10, 1, isLastBlock: false));
        }

        Assert.Equal(before, await BlockStateAsync(channel, run));
    }

    // Sol-P2-1 (ii): a slot of the snapshot's emote that never was a row of the run is no foreign pair but
    // a changed snapshot as far as the block can tell: SnapshotChanged, nothing written — the earlier
    // run's imported row in the block and its coverage stay, WeeksDone and the bytes do not move.
    [Fact]
    public async Task ReplaceBlock_WithASlotOfTheEmoteThatIsNotInTheSnapshot_IsSnapshotChanged_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("bfslotdrift", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var x = await SeedEmoteAsync(channel, "x", "X");
        var other = await SeedEmoteAsync(channel, "z", "Z");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, windowFrom: BlockFrom);
        var slots = await BindSnapshotAsync(run, x);
        var neverInSnapshot = await SeedSlotAsync(x.Id, "X2", OlderEntry);
        await SeedEarlierImportAsync(channel, other);
        var before = await BlockStateAsync(channel, run);

        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.SnapshotChanged>(await CreateService(db).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo,
                [new ChatLogBackfillAggregate(slots[x.Id], x.Id, BlockFrom, 4, 0, 0), new ChatLogBackfillAggregate(neverInSnapshot, x.Id, BlockFrom, 1, 0, 0)],
                10, 1, isLastBlock: false));
        }

        Assert.Equal(before, await BlockStateAsync(channel, run));
    }

    // Sol-P2-1 (iii) / E9 end to end: a run enqueued with the flag off writes block 1 on its seed S; the
    // real flag-on settle finds the REST slot T ("target exists"), moves S's rows and rebinds the
    // snapshot row to T. Block 2, counted on S, is refused as SnapshotChanged and changes nothing — the
    // imported row of an earlier run in its days, the coverage, WeeksDone and the bytes stay. After the
    // worker's re-read (the row (EmoteId, Name) now on T) the same block lands on T, and S keeps no row.
    [Fact]
    public async Task ReplaceBlock_AfterASettleReboundTheSnapshot_IsSnapshotChanged_AndTheReReadBlockLandsOnTheTarget()
    {
        PinFlagOff();
        var (channel, emote, target, seed, claim) = await EnqueueNextToRestSlotAsync("bfslotsettle");
        await ReplaceBlockOfAsync(claim, 0, new ChatLogBackfillAggregate(seed, emote.Id, claim.WindowFrom, 3, 0, 0));
        await SettleAsync(channel, logger: null);

        var secondDay = claim.WindowFrom.AddDays(8);
        var bystander = await SeedEmoteAsync(channel, "z", "Z");
        await using (var db = CreateDbContext())
        {
            db.UsageStats.Add(Imported(bystander, SetC, secondDay, 6));
            await db.AssignUsageSlotsAsync();
            await db.SaveChangesAsync();
        }

        var run = await RunOfAsync(claim);
        var before = await BlockStateAsync(channel, run);
        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.SnapshotChanged>(
                await ReplaceBlockOfAsync(db, claim, 1, new ChatLogBackfillAggregate(seed, emote.Id, secondDay, 4, 0, 0)));
        }

        Assert.Equal(before, await BlockStateAsync(channel, run));

        ChatLogBackfillSnapshotEmote reread;
        await using (var db = CreateDbContext())
        {
            reread = Assert.Single((await CreateService(db).GetSnapshotAsync(claim.RunId)).Emotes);
        }

        Assert.Equal((target, emote.Id, "A"), (reread.SlotId, reread.EmoteId, reread.Name));
        await ReplaceBlockOfAsync(claim, 1, new ChatLogBackfillAggregate(reread.SlotId, emote.Id, secondDay, 4, 0, 0));

        await using var verify = CreateDbContext();
        Assert.Equal(target, await SlotOfRowOnAsync(emote.Id, secondDay));
        Assert.False(await verify.UsageStats.AnyAsync(u => u.SlotId == seed));
        Assert.False(await verify.UsageStats.AnyAsync(u => u.EmoteId == bystander.Id), "the block's delete took the earlier run's imported row");
        var after = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId);
        Assert.Equal((ChatLogBackfillRunStatus.Completed, 2, 20L), (after.Status, after.WeeksDone, after.BytesReceived));
    }

    // AK 25 (OD-A per slot): an earlier run of set C left imported rows on both slots of one emote in the
    // block; a run of set B replaces every imported row of the channel there in one transaction — rows of
    // both slots, whatever their set — covers the days for B and advances by one week. No count shrinks
    // per (emote, day).
    [Fact]
    public async Task ReplaceBlock_ARerunOfAnotherSet_ReplacesTheImportedRowsOfBothSlots()
    {
        var channel = await SeedChannelAsync("bfslotak25", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var emote = await SeedEmoteAsync(channel, "x", "hob44");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, windowFrom: BlockFrom);
        var (ome, hob) = await SeedTwoSlotSnapshotAsync(run, emote);
        var day = BlockFrom.AddDays(3);
        await using (var db = CreateDbContext())
        {
            db.UsageStats.AddRange(
                new UsageStat { EmoteId = emote.Id, SlotId = ome, EmoteSetId = SetC, Date = day, UseCount = 2, Source = UsageStatSource.ChatLogArchive },
                new UsageStat { EmoteId = emote.Id, SlotId = hob, EmoteSetId = SetC, Date = day, UseCount = 3, Source = UsageStatSource.ChatLogArchive });
            await db.SaveChangesAsync();
        }

        await AddCoverageAsync(channel, BlockFrom, BlockTo, SetC, runId: null);
        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await CreateService(db).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo,
                [new ChatLogBackfillAggregate(hob, emote.Id, day, 4, 0, 0), new ChatLogBackfillAggregate(ome, emote.Id, day, 2, 1, 0)],
                10, 1, isLastBlock: false));
        }

        await using var verify = CreateDbContext();
        var imported = await verify.UsageStats.AsNoTracking()
            .Where(u => u.Source == UsageStatSource.ChatLogArchive)
            .OrderBy(u => u.SlotId == ome)
            .Select(u => new { u.SlotId, u.EmoteSetId, u.Date, u.UseCount, u.BotUseCount })
            .ToListAsync();
        Assert.Equal(
            [
                new { SlotId = hob, EmoteSetId = SetB, Date = day, UseCount = 4, BotUseCount = 0 },
                new { SlotId = ome, EmoteSetId = SetB, Date = day, UseCount = 2, BotUseCount = 1 },
            ],
            imported);
        Assert.True(imported.Sum(u => u.UseCount + u.BotUseCount) >= 2 + 3);
        Assert.All(await verify.ChatLogBackfillCoverage.AsNoTracking().Where(d => d.ChannelId == channel.Id).ToListAsync(),
            d => Assert.Equal((SetB, (long?)run.Id), (d.EmoteSetId, d.RunId)));
        Assert.Equal(1, (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).WeeksDone);
    }

    // Spec 4.2 per slot (plan #375 §3): a cell is live or imported per slot. A live row on slot A and an
    // import on slot B of the same emote and day coexist; a live row on the import's own cell is a
    // LiveRowConflict and rolls the block back.
    [Fact]
    public async Task ReplaceBlock_LiveRowConflict_IsPerSlot()
    {
        var channel = await SeedChannelAsync("bfslotlive", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var emote = await SeedEmoteAsync(channel, "x", "hob44");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksTotal: 2, windowFrom: BlockFrom);
        var (ome, hob) = await SeedTwoSlotSnapshotAsync(run, emote);
        var day = BlockFrom.AddDays(1);
        await using (var db = CreateDbContext())
        {
            db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, SlotId = ome, EmoteSetId = SetB, Date = day, UseCount = 7, Source = UsageStatSource.Live });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            Assert.IsType<ChatLogBackfillBlockResult.LiveRowConflict>(await service.ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo,
                [new ChatLogBackfillAggregate(hob, emote.Id, day, 1, 0, 0), new ChatLogBackfillAggregate(ome, emote.Id, day, 1, 0, 0)],
                10, 1, isLastBlock: false));
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await service.ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(hob, emote.Id, day, 1, 0, 0)], 10, 1, isLastBlock: false));
        }

        await using var verify = CreateDbContext();
        var cells = await verify.UsageStats.AsNoTracking()
            .Where(u => u.Date == day)
            .OrderBy(u => u.Source)
            .Select(u => new { u.SlotId, u.Source, u.UseCount })
            .ToListAsync();
        Assert.Equal(
            [new { SlotId = ome, Source = UsageStatSource.Live, UseCount = 7 }, new { SlotId = hob, Source = UsageStatSource.ChatLogArchive, UseCount = 1 }],
            cells);
    }

    // ---------------------------------------------------------------- Helpers

    // Two authoritative slots of one emote — ome44 (older entry) and hob44 (newer) — and one snapshot row
    // each, the shape a flag-on enqueue writes for a set listing the emote twice.
    private async Task<(string Ome, string Hob)> SeedTwoSlotSnapshotAsync(ChatLogBackfillRun run, Emote emote)
    {
        var ome = await SeedSlotAsync(emote.Id, "ome44", OlderEntry);
        var hob = await SeedSlotAsync(emote.Id, "hob44", NewerEntry);
        await using var db = CreateDbContext();
        db.ChatLogBackfillRunEmotes.AddRange(
            new ChatLogBackfillRunEmote
            {
                RunId = run.Id,
                EmoteId = emote.Id,
                SevenTvEmoteId = emote.SevenTvEmoteId,
                Name = "ome44",
                SlotId = ome,
                AddedToSetDay = DateOnly.FromDateTime(OlderEntry),
            },
            new ChatLogBackfillRunEmote
            {
                RunId = run.Id,
                EmoteId = emote.Id,
                SevenTvEmoteId = emote.SevenTvEmoteId,
                Name = "hob44",
                SlotId = hob,
                AddedToSetDay = DateOnly.FromDateTime(NewerEntry),
            });
        await db.SaveChangesAsync();
        return (ome, hob);
    }

    // An imported row and the coverage of an earlier run of set C inside the first block.
    private async Task SeedEarlierImportAsync(Channel channel, Emote emote)
    {
        await using (var db = CreateDbContext())
        {
            db.UsageStats.Add(Imported(emote, SetC, BlockFrom.AddDays(4), 6));
            await db.AssignUsageSlotsAsync();
            await db.SaveChangesAsync();
        }

        await AddCoverageAsync(channel, BlockFrom, BlockTo, SetC, runId: null);
    }

    private async Task<ChatLogBackfillRun> RunOfAsync(ChatLogBackfillClaim claim)
    {
        await using var db = CreateDbContext();
        return await db.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId);
    }
}
