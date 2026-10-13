using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Oracle for the flag-off backfill (plan #375, T0): pins the measured DB effects of today's path (snapshot
// rows and UsageStats rows) so the slot-keyed snapshot (M3) and the slot-keyed worker/block key (T2) have
// to stay observably identical while the flag is off. Only effects are asserted, never DTO or key shapes.
public partial class ChatLogBackfillServiceTests
{
    // Two entries of one 7TV id in the previewed set (older alias first, newer last): one snapshot row,
    // carrying the LAST entry's alias and day, bound to the pair's existing base slot; nothing created.
    [Fact]
    public async Task Oracle_FlagOff_TwoEntriesOfOneEmote_OneSnapshotRowWithTheLastEntry_OnTheExistingBaseSlot()
    {
        var channel = await SeedChannelAsync("bforacle1");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        await using (var db = CreateDbContext())
        {
            db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, EmoteSetId = SetB, Date = new DateOnly(2026, 9, 1), UseCount = 4 });
            await db.AssignUsageSlotsAsync();
            await db.SaveChangesAsync();
        }

        var baseSlot = await SlotOfRowAsync(emote.Id, SetB);
        var older = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 5, 2, 18, 30, 0, DateTimeKind.Utc);
        PreviewReturns(SetB, "Set B", Member("X", "ome44", older), Member("X", "hob44", newer));

        var result = await EnqueueAsync(channel, SetB, months: 6);

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, result.Status);
        Assert.Equal(1, result.Run!.EmoteCount);
        await using var verify = CreateDbContext();
        var entry = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
        Assert.Equal(
            (emote.Id, "hob44", (DateOnly?)DateOnly.FromDateTime(newer), (string?)baseSlot, false, false),
            (entry.EmoteId, entry.Name, entry.AddedToSetDay, entry.SlotId, entry.CreatedSlot, entry.CreatedRow));
        Assert.Equal(1, await verify.EmoteSlots.CountAsync());
        Assert.Equal(1, await verify.Emotes.CountAsync());
    }

    // The same preview for an emote unknown to the channel: one placeholder emote and one new base slot,
    // both marked as created by this enqueue, one snapshot row with the last entry's alias.
    [Fact]
    public async Task Oracle_FlagOff_TwoEntriesOfAnUnknownEmote_CreateOneEmoteAndOneSlot_MarkedCreated()
    {
        var channel = await SeedChannelAsync("bforacle2");
        await SeedUserAsync();
        var older = new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 5, 2, 18, 30, 0, DateTimeKind.Utc);
        PreviewReturns(SetB, "Set B", Member("X", "ome44", older), Member("X", "hob44", newer));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 6)).Status);

        await using var verify = CreateDbContext();
        var emote = await verify.Emotes.AsNoTracking().SingleAsync();
        var slot = await verify.EmoteSlots.AsNoTracking().SingleAsync();
        var entry = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
        Assert.Equal(("X", "hob44", true), (emote.SevenTvEmoteId, emote.Name, emote.IsArchived));
        Assert.Equal(emote.Id, slot.EmoteId);
        Assert.Equal(
            (emote.Id, "hob44", (DateOnly?)DateOnly.FromDateTime(newer), (string?)slot.Id, true, true),
            (entry.EmoteId, entry.Name, entry.AddedToSetDay, entry.SlotId, entry.CreatedSlot, entry.CreatedRow));
    }

    // A preview of a set that is NOT the channel's active set, for an emote that already has rows of that
    // set: the snapshot binds the existing seed; no slot is created, moved or changed.
    [Fact]
    public async Task Oracle_FlagOff_InactiveSetPreview_BindsTheSetsExistingSlot_AndChangesNoSlot()
    {
        var channel = await SeedChannelAsync("bforacle3");
        await SeedUserAsync();
        var emote = await SeedEmoteAsync(channel, "X", "XNow");
        await using (var db = CreateDbContext())
        {
            db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, EmoteSetId = SetB, Date = new DateOnly(2026, 9, 1), UseCount = 2 });
            await db.AssignUsageSlotsAsync();
            await db.SaveChangesAsync();
        }

        var seed = await SlotOfRowAsync(emote.Id, SetB);
        string slotsBefore;
        await using (var db = CreateDbContext())
        {
            slotsBefore = await SlotFingerprintAsync(db);
        }

        PreviewReturns(SetB, "Set B", Member("X", "BX", null));

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 6)).Status);

        await using var verify = CreateDbContext();
        var entry = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
        Assert.Equal(((string?)seed, false), (entry.SlotId, entry.CreatedSlot));
        Assert.Equal(slotsBefore, await SlotFingerprintAsync(verify));
    }

    // Real run, flag off: enqueue, claim, two blocks (the last one completes the run). The imported rows
    // are exactly the aggregates, each on the snapshot's slot of its emote, tagged as archive rows, in the
    // run's set; the coverage covers the whole window; rows already present elsewhere are untouched.
    [Fact]
    public async Task Oracle_FlagOff_RunWritesExactlyTheAggregatesAsUsageRows_AndCompletesWithFullCoverage()
    {
        var channel = await SeedChannelAsync("bforacle4", createdAt: NowUtc.AddDays(-20));
        await SeedUserAsync();
        var known = await SeedEmoteAsync(channel, "k1", "Known");
        await using (var db = CreateDbContext())
        {
            // A live (chat) row of the same pair on a day outside the window: must survive the run.
            db.UsageStats.Add(new UsageStat { EmoteId = known.Id, EmoteSetId = SetB, Date = new DateOnly(2026, 10, 8), UseCount = 9 });
            await db.AssignUsageSlotsAsync();
            await db.SaveChangesAsync();
        }

        PreviewReturns(SetB, "Set B", Member("k1", "BKnown", null), Member("n1", "BNew1", null));
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await EnqueueAsync(channel, SetB, months: 1)).Status);

        Dictionary<string, string> slotOf;
        Dictionary<string, string> emoteOfSeven;
        await using (var db = CreateDbContext())
        {
            var snapshot = await db.ChatLogBackfillRunEmotes.AsNoTracking().ToListAsync();
            Assert.Equal(2, snapshot.Count);
            slotOf = snapshot.ToDictionary(e => e.EmoteId, e => e.SlotId!);
            emoteOfSeven = snapshot.ToDictionary(e => e.SevenTvEmoteId, e => e.EmoteId);
        }

        var claim = await ClaimAsync();
        Assert.Equal(2, claim.WeeksTotal);
        var knownId = emoteOfSeven["k1"];
        var newId = emoteOfSeven["n1"];
        var d0 = claim.WindowFrom;

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await service.ReplaceBlockAsync(
                claim.RunId, d0, d0.AddDays(7),
                [new ChatLogBackfillAggregate(knownId, d0, 3, 1, 2), new ChatLogBackfillAggregate(newId, d0.AddDays(2), 5, 0, 0)],
                100, 5, isLastBlock: false));
        }

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            Assert.IsType<ChatLogBackfillBlockResult.Committed>(await service.ReplaceBlockAsync(
                claim.RunId, d0.AddDays(7), claim.WindowTo,
                [new ChatLogBackfillAggregate(knownId, d0.AddDays(8), 7, 2, 1)],
                100, 5, isLastBlock: true));
        }

        await using var verify = CreateDbContext();
        var rows = await verify.UsageStats.AsNoTracking()
            .Where(u => u.Source == UsageStatSource.ChatLogArchive)
            .OrderBy(u => u.Date).ThenBy(u => u.EmoteId)
            .ToListAsync();
        Assert.Equal(
            [
                (knownId, slotOf[knownId], SetB, d0, 3, 1, 2),
                (newId, slotOf[newId], SetB, d0.AddDays(2), 5, 0, 0),
                (knownId, slotOf[knownId], SetB, d0.AddDays(8), 7, 2, 1),
            ],
            rows.Select(u => (u.EmoteId, u.SlotId, u.EmoteSetId, u.Date, u.UseCount, u.BotUseCount, u.SharedChatUseCount)).ToList());

        var live = await verify.UsageStats.AsNoTracking().SingleAsync(u => u.Source != UsageStatSource.ChatLogArchive);
        Assert.Equal((known.Id, SetB, new DateOnly(2026, 10, 8), 9), (live.EmoteId, live.EmoteSetId, live.Date, live.UseCount));

        var run = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == claim.RunId);
        Assert.Equal(ChatLogBackfillRunStatus.Completed, run.Status);
        var days = await verify.ChatLogBackfillCoverage.AsNoTracking().Where(d => d.ChannelId == channel.Id).Select(d => d.Day).OrderBy(d => d).ToListAsync();
        Assert.Equal(claim.WindowTo.DayNumber - d0.DayNumber, days.Count);
        Assert.Equal((d0, claim.WindowTo.AddDays(-1)), (days[0], days[^1]));
        Assert.All(await verify.ChatLogBackfillCoverage.AsNoTracking().ToListAsync(), d => Assert.Equal(SetB, d.EmoteSetId));
        Assert.Equal(2, await verify.EmoteSlots.CountAsync());
    }

    private static async Task<string> SlotFingerprintAsync(AppDbContext db) =>
        string.Join(
            ";",
            (await db.EmoteSlots.AsNoTracking().OrderBy(s => s.Id).ToListAsync())
            .Select(s => $"{s.Id}|{s.EmoteId}|{s.Name}|{s.AddedAt:O}|{s.IsArchived}|{s.SeedUnsettled}"));
}
