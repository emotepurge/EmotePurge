using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The test helper of plan #373 (E14): one slot per (emote, set) pair, reused across calls, kind
// selectable, rows that name a slot left alone.
[Collection("Postgres")]
public class UsageSlotsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AssignUsageSlots_GivesOneUnsettledSeedPerPair_AndReusesItAcrossCalls()
    {
        var emoteId = await SeedEmoteAsync("usageslots_pair");
        await using var db = fixture.CreateDbContext();
        db.UsageStats.AddRange(Row(emoteId, "SET1", 1), Row(emoteId, "SET1", 2), Row(emoteId, "SET2", 1));
        await db.AssignUsageSlotsAsync();
        await db.SaveChangesAsync();

        db.UsageStats.Add(Row(emoteId, "SET1", 3));
        await db.AssignUsageSlotsAsync();
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.UsageStats.Where(u => u.EmoteId == emoteId).ToListAsync();
        Assert.All(rows, r => Assert.NotNull(r.SlotId));
        Assert.Single(rows.Where(r => r.EmoteSetId == "SET1").Select(r => r.SlotId).Distinct());
        Assert.NotEqual(rows.First(r => r.EmoteSetId == "SET1").SlotId, rows.First(r => r.EmoteSetId == "SET2").SlotId);
        var slots = await verify.EmoteSlots.Where(s => s.EmoteId == emoteId).ToListAsync();
        Assert.Equal(2, slots.Count);
        Assert.All(slots, s =>
        {
            Assert.True(s.SeedUnsettled);
            Assert.Null(s.AddedAt);
        });
    }

    [Fact]
    public async Task AssignUsageSlots_CreatesTheRequestedKind_AndLeavesANamedSlotAlone()
    {
        var emoteId = await SeedEmoteAsync("usageslots_kind");
        await using var db = fixture.CreateDbContext();
        var named = await db.SeedUsageSlotAsync(emoteId);
        var own = Row(emoteId, "SETN", 1);
        own.SlotId = named;
        db.UsageStats.AddRange(own, Row(emoteId, "SETK", 1));
        await db.AssignUsageSlotsAsync(UsageSlotKind.Settled);
        await db.SaveChangesAsync();

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(named, (await verify.UsageStats.SingleAsync(u => u.EmoteId == emoteId && u.EmoteSetId == "SETN")).SlotId);
        var kindRow = await verify.UsageStats.SingleAsync(u => u.EmoteId == emoteId && u.EmoteSetId == "SETK");
        var slot = await verify.EmoteSlots.SingleAsync(s => s.Id == kindRow.SlotId);
        Assert.False(slot.SeedUnsettled);
        Assert.True(slot.AddedAtFromRest);
        Assert.NotNull(slot.AddedAt);
    }

    private static UsageStat Row(string emoteId, string setId, int day) =>
        new() { EmoteId = emoteId, EmoteSetId = setId, Date = new DateOnly(2026, 8, day), UseCount = 1 };

    private async Task<string> SeedEmoteAsync(string channelName)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        var emote = new Emote { ChannelId = channel.Id, SevenTvEmoteId = "7tv_" + channelName, Name = "Emote", ImageUrl = "i" };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote.Id;
    }
}
