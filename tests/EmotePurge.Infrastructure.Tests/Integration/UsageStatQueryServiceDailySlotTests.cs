using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The drilldown of /daily per slot or per emote (plan #376, 2.5; spec 5.2; AK 30).
[Collection("Postgres")]
public class UsageStatQueryServiceDailySlotTests(PostgresFixture fixture)
{
    private static readonly DateOnly From = new(2026, 8, 1);
    private static readonly DateOnly To = new(2026, 8, 31);

    [Fact]
    public async Task SlotSeries_ShowsOnlyThatSlot_AndTheEmoteSeriesIsTheSum()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailyslot_two");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var ome44 = await SeedSlotAsync(db, emote, "ome44", T1);
        var hob44 = await SeedSlotAsync(db, emote, "hob44", T2);
        await AddUsageAsync(db, emote, ome44, (From, 3), (From.AddDays(1), 4));
        await AddUsageAsync(db, emote, hob44, (From.AddDays(1), 5));

        var service = Service(db);
        var slot = await service.GetDailySlotSeriesAsync(channel.ChannelName, hob44.Id, From, To);
        var summed = await service.GetDailySeriesAsync(channel.ChannelName, emote.Id, From, To);

        Assert.NotNull(slot);
        Assert.Equal(hob44.Id, slot.SlotId);
        Assert.Equal(emote.Id, slot.EmoteId);
        Assert.Equal("hob44", slot.EmoteName);
        Assert.Equal(5, slot.TotalUseCount);
        Assert.Equal([new EmoteDailyUsageDto(From.AddDays(1), 5)], slot.Days);
        Assert.Equal(From.AddDays(1), slot.FirstUsedDate);
        Assert.NotNull(summed);
        Assert.Null(summed.SlotId);
        Assert.Equal(12, summed.TotalUseCount);
        Assert.Equal([new EmoteDailyUsageDto(From, 3), new EmoteDailyUsageDto(From.AddDays(1), 9)], summed.Days);
    }

    // The invariant of plan 376 R2: the series of a row's slotId carries the numbers of that row.
    [Fact]
    public async Task SlotSeries_AgreesWithTheSlotsRowInTotals()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailyslot_totals");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var ome44 = await SeedSlotAsync(db, emote, "ome44", T1);
        var hob44 = await SeedSlotAsync(db, emote, "hob44", T2);
        await AddUsageAsync(db, emote, ome44, (From, 3));
        await AddUsageAsync(db, emote, hob44, (From, 6), (From.AddDays(2), 1));

        var service = Service(db);
        var row = (await service.GetUsageContextAsync(channel.ChannelName, From, To)).Single(t => t.SlotId == hob44.Id);
        var series = await service.GetDailySlotSeriesAsync(channel.ChannelName, hob44.Id, From, To);

        Assert.NotNull(series);
        Assert.Equal(row.TotalUseCount, series.TotalUseCount);
        Assert.Equal(row.EmoteName, series.EmoteName);
        Assert.Equal(row.LastUsedDate, series.LastUsedDate);
    }

    [Fact]
    public async Task SlotSeries_OfAnotherChannel_IsNull()
    {
        await using var db = fixture.CreateDbContext();
        var mine = await SeedChannelAsync(db, "dailyslot_mine");
        var theirs = await SeedChannelAsync(db, "dailyslot_theirs");
        var emote = await SeedEmoteAsync(db, theirs, "x", "ome44", firstSeenAt: T0);
        var slot = await SeedSlotAsync(db, emote, "ome44", T1);
        await AddUsageAsync(db, emote, slot, (From, 3));

        var service = Service(db);

        Assert.Null(await service.GetDailySlotSeriesAsync(mine.ChannelName, slot.Id, From, To));
        Assert.Null(await service.GetDailySlotSeriesAsync(mine.ChannelName, "nope", From, To));
        Assert.NotNull(await service.GetDailySlotSeriesAsync(theirs.ChannelName, slot.Id, From, To));
    }

    [Fact]
    public async Task SlotSeries_OfAnUnsettledSeed_NamesTheEmote_AndAnArchivedSlotKeepsItsHistory()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailyslot_seed");
        var emote = await SeedEmoteAsync(db, channel, "x", "CurrentName", firstSeenAt: T0);
        var seed = await SeedSeedAsync(db, emote);
        seed.Name = "StaleName";
        await db.SaveChangesAsync();
        var other = await SeedEmoteAsync(db, channel, "y", "Gone", firstSeenAt: T0);
        var archived = await SeedSlotAsync(db, other, "Gone", T0, archived: true, archivedAt: T1);
        await AddUsageAsync(db, emote, seed, (From, 2));
        await AddUsageAsync(db, other, archived, (From, 7));

        var service = Service(db);
        var seedSeries = await service.GetDailySlotSeriesAsync(channel.ChannelName, seed.Id, From, To);
        var archivedSeries = await service.GetDailySlotSeriesAsync(channel.ChannelName, archived.Id, From, To);

        Assert.Equal("CurrentName", seedSeries!.EmoteName);
        Assert.Equal(2, seedSeries.TotalUseCount);
        Assert.Equal(7, archivedSeries!.TotalUseCount);
    }

    [Fact]
    public async Task SlotSeries_WithoutUsageInTheScope_IsEmptyNotNull_AndTheScopeSelectsTheSet()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dailyslot_scope");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var slot = await SeedSlotAsync(db, emote, "ome44", T1);
        await AddUsageAsync(db, emote, slot, (From, 3));
        await AddUsageAsync(db, emote, slot, "01SLOTSETB00000000000000BB", (From.AddDays(1), 4));

        var service = Service(db);
        var active = await service.GetDailySlotSeriesAsync(channel.ChannelName, slot.Id, From, To);
        var other = await service.GetDailySlotSeriesAsync(channel.ChannelName, slot.Id, From, To, EmoteSetScope.Set("01SLOTSETZ00000000000000ZZ"));
        var all = await service.GetDailySlotSeriesAsync(channel.ChannelName, slot.Id, From, To, EmoteSetScope.AllSets);

        Assert.Equal(3, active!.TotalUseCount);
        Assert.Empty(other!.Days);
        Assert.Null(other.FirstUsedDate);
        Assert.Equal(7, all!.TotalUseCount);
    }

    private static UsageStatQueryService Service(AppDbContext db) =>
        new(db, new SlotReconcileOptions { Enabled = true });

    private static Task AddUsageAsync(AppDbContext db, Emote emote, EmoteSlot slot, params (DateOnly Date, int UseCount)[] days) =>
        AddUsageAsync(db, emote, slot, SetA, days);

    private static async Task AddUsageAsync(AppDbContext db, Emote emote, EmoteSlot slot, string setId, params (DateOnly Date, int UseCount)[] days)
    {
        foreach (var (date, useCount) in days)
        {
            db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, SlotId = slot.Id, EmoteSetId = setId, Date = date, UseCount = useCount });
        }

        await db.SaveChangesAsync();
    }
}
