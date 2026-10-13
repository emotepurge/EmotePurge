using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The snapshot half of the seed settle writes under the (RunId, SlotId) key (plan #375 E10) against
// real Postgres. The usage half is covered through SevenTvSyncServiceSlotTests.
[Collection("Postgres")]
public class SeedSettleWritesTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MoveSeedSnapshotEntriesAsync_RepointsRowsWithoutTargetRow_AndDropsTheSeedRowWhereTheRunAlreadyHasOne()
    {
        var channel = new Channel { ChannelName = "settlewrites_snapshot", IsBotActive = true };
        var emote = new Emote { ChannelId = channel.Id, SevenTvEmoteId = "7tv-settlewrites", Name = "E", ImageUrl = "i" };
        var seed = new EmoteSlot { EmoteId = emote.Id, Name = "Seed", SeedUnsettled = true };
        var target = new EmoteSlot { EmoteId = emote.Id, Name = "Target", AddedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), AddedAtFromRest = true };
        long bothRunId;
        long seedOnlyRunId;
        await using (var setup = fixture.CreateDbContext())
        {
            setup.Channels.Add(channel);
            setup.Emotes.Add(emote);
            setup.EmoteSlots.AddRange(seed, target);
            var bothRun = NewRun(channel.Id);
            var seedOnlyRun = NewRun(channel.Id);
            seedOnlyRun.Status = ChatLogBackfillRunStatus.Completed;
            seedOnlyRun.FinishedAtUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
            setup.ChatLogBackfillRuns.AddRange(bothRun, seedOnlyRun);
            await setup.SaveChangesAsync();
            bothRunId = bothRun.Id;
            seedOnlyRunId = seedOnlyRun.Id;
            setup.ChatLogBackfillRunEmotes.AddRange(
                Row(bothRunId, emote, seed, createdSlot: true),
                Row(bothRunId, emote, target, createdSlot: true),
                Row(seedOnlyRunId, emote, seed, createdSlot: true));
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var (moved, dropped) = await SeedSettleWrites.MoveSeedSnapshotEntriesAsync(
            db, new SlotUsageRebind(seed.Id, target.Id), CancellationToken.None);

        Assert.Equal((1, 1), (moved, dropped));
        var rows = await db.ChatLogBackfillRunEmotes.AsNoTracking()
            .Where(e => e.RunId == bothRunId || e.RunId == seedOnlyRunId)
            .OrderBy(e => e.RunId)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(target.Id, r.SlotId));
        // The run that held both keeps its own target row untouched; the other row is moved and no longer created the slot.
        Assert.True(rows.Single(r => r.RunId == bothRunId).CreatedSlot);
        var movedRow = rows.Single(r => r.RunId == seedOnlyRunId);
        Assert.False(movedRow.CreatedSlot);
        Assert.Equal((emote.Id, "E"), (movedRow.EmoteId, movedRow.Name));
    }

    private static ChatLogBackfillRun NewRun(string channelId) => new()
    {
        ChannelId = channelId,
        Status = ChatLogBackfillRunStatus.Running,
        RequestedMonths = 1,
        WindowFrom = new DateOnly(2026, 6, 1),
        WindowTo = new DateOnly(2026, 6, 8),
        WeeksTotal = 1,
        EmoteSetId = "set",
        ArchiveBaseUrl = "https://archive.example",
        RequestedByTwitchUserId = "1",
        RequestedByLogin = "tester",
        RequestedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ChatLogBackfillRunEmote Row(long runId, Emote emote, EmoteSlot slot, bool createdSlot) => new()
    {
        RunId = runId,
        EmoteId = emote.Id,
        SevenTvEmoteId = emote.SevenTvEmoteId,
        Name = emote.Name,
        SlotId = slot.Id,
        CreatedSlot = createdSlot,
    };
}
