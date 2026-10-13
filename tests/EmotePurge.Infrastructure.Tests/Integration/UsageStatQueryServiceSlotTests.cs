using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The slot read path of /totals and /series (plan #376, 2.2, 2.4, 2.6; spec 5.1, 5.3; AK 28, 31). The
// pre-slot cases in UsageStatQueryServiceTests run with the flag off and are the proof that production
// reads as before; the Off_ case here adds the same proof on a database that does carry settled slots.
[Collection("Postgres")]
public class UsageStatQueryServiceSlotTests(PostgresFixture fixture)
{
    private const string SetC = "01SLOTSETC00000000000000CC";
    private const string SetD = "01SLOTSETD00000000000000DD";

    private static readonly DateOnly From = new(2026, 8, 1);
    private static readonly DateOnly To = new(2026, 8, 31);

    // AK 28, active view: two settled slots of one emote are two rows, each with its own numbers, date and
    // provenance, each naming the other as its sibling. An archived own slot does not show, even with rows
    // in the set (D1/D7: its history returns on the same card when it is resumed).
    [Fact]
    public async Task On_ActiveView_TwoSlotsOfOneEmote_AreTwoRowsWithTheirOwnNumbers()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_two_slots");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var ome44 = await SeedSlotAsync(db, emote, "ome44", T1);
        var hob44 = await SeedSlotAsync(db, emote, "hob44", T2, fromRest: false);
        var gone = await SeedSlotAsync(db, emote, "old44", T0, archived: true, archivedAt: T1);
        await AddUsageAsync(db, emote, ome44, SetA, (new DateOnly(2026, 7, 20), 2), (From, 3), (From.AddDays(1), 4));
        await AddUsageAsync(db, emote, hob44, SetA, (From.AddDays(1), 5));
        await AddUsageAsync(db, emote, gone, SetA, (From.AddDays(2), 40));

        var totals = await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To);

        Assert.Equal([ome44.Id, hob44.Id], totals.Select(t => t.SlotId));
        var first = totals[0];
        Assert.Equal(emote.Id, first.EmoteId);
        Assert.Equal("ome44", first.EmoteName);
        Assert.Equal("x", first.SevenTvEmoteId);
        Assert.Equal(Url("x"), first.ImageUrl);
        Assert.Equal(7, first.TotalUseCount);
        Assert.Equal(2, first.PreviousWindowUseCount);
        Assert.Equal(From.AddDays(1), first.LastUsedDate);
        Assert.Equal(T1, first.FirstSeenAt);
        Assert.True(first.FirstSeenAtFromRest);
        Assert.False(first.IsArchived);
        Assert.Equal(["hob44"], first.SiblingNames);
        var second = totals[1];
        Assert.Equal("hob44", second.EmoteName);
        Assert.Equal(5, second.TotalUseCount);
        Assert.Equal(0, second.PreviousWindowUseCount);
        Assert.Equal(T2, second.FirstSeenAt);
        Assert.False(second.FirstSeenAtFromRest);
        Assert.Equal(["ome44"], second.SiblingNames);
    }

    // AK 28, D7: a resumed slot is active again under a new date and shows the usage it carried before.
    [Fact]
    public async Task On_ActiveView_AResumedSlot_ShowsItsEarlierUsageOnTheSameRow()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_resumed");
        var emote = await SeedEmoteAsync(db, channel, "x", "Back", firstSeenAt: T3);
        var resumed = await SeedSlotAsync(db, emote, "Back", T3);
        await AddUsageAsync(db, emote, resumed, SetA, (From, 9));

        var row = Assert.Single(await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To));

        Assert.Equal(resumed.Id, row.SlotId);
        Assert.Equal(9, row.TotalUseCount);
        Assert.Equal(T3, row.FirstSeenAt);
    }

    // AK 28, non-active view: one row per own slot with rows under the set, archived ones included; a slot
    // without rows under that set has no row, whatever it carries elsewhere.
    [Fact]
    public async Task On_NonActiveView_ShowsOwnSlotsWithRowsInTheSet_ArchivedOnesIncluded()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_nonactive");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var live = await SeedSlotAsync(db, emote, "ome44", T1);
        var archived = await SeedSlotAsync(db, emote, "hob44", T0, archived: true, archivedAt: T1);
        var elsewhere = await SeedSlotAsync(db, emote, "new44", T2);
        await AddUsageAsync(db, emote, live, SetB, (From, 2));
        await AddUsageAsync(db, emote, archived, SetB, (From, 6));
        await AddUsageAsync(db, emote, elsewhere, SetA, (From, 50));

        var totals = await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To, SetB);

        Assert.Equal(2, totals.Count);
        var archivedRow = totals[0];
        Assert.Equal(archived.Id, archivedRow.SlotId);
        Assert.Equal(6, archivedRow.TotalUseCount);
        Assert.True(archivedRow.IsArchived);
        Assert.Equal(["ome44"], archivedRow.SiblingNames);
        var liveRow = totals[1];
        Assert.Equal(live.Id, liveRow.SlotId);
        Assert.Equal(2, liveRow.TotalUseCount);
        Assert.False(liveRow.IsArchived);
        Assert.Equal(["hob44"], liveRow.SiblingNames);
    }

    // R2: an emote whose counts under the set still sit on an unsettled seed is one emote-mode row — the
    // emote's name and date, every slot's counts, no slot id, no siblings — even when it already has a
    // settled slot beside the seed (the accepted transient blur of plan section 0).
    [Fact]
    public async Task On_SeedOfTheViewedSet_IsOneEmoteModeRow()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_seed");
        var emote = await SeedEmoteAsync(db, channel, "x", "Stale", firstSeenAt: T0);
        var seed = await SeedSeedAsync(db, emote);
        var settled = await SeedSlotAsync(db, emote, "ome44", T1);
        await AddUsageAsync(db, emote, seed, SetA, (From, 3));
        await AddUsageAsync(db, emote, settled, SetA, (From, 4), (From.AddDays(4), 1));

        var series = await Service(db, on: true).GetChannelSeriesAsync(channel.ChannelName, From, To);
        var row = Assert.Single(await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To));

        Assert.Null(row.SlotId);
        Assert.Equal("Stale", row.EmoteName);
        Assert.Equal(T0, row.FirstSeenAt);
        Assert.False(row.FirstSeenAtFromRest);
        Assert.Equal(8, row.TotalUseCount);
        Assert.Empty(row.SiblingNames);
        var entry = Assert.Single(series.Emotes);
        Assert.Null(entry.SlotId);
        Assert.Equal("Stale", entry.Name);
        Assert.Equal([[0, 7], [4, 1]], entry.Days);
    }

    // R2 (E14): in the active view an emote without an active own slot is one emote-mode row, zero-filled
    // when it has nothing, carrying an archived own slot's counts when it has those. A seed of another set
    // decides nothing.
    [Fact]
    public async Task On_ActiveView_EmoteWithoutActiveOwnSlot_IsOneEmoteModeRow()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_virtual");
        var bare = await SeedEmoteAsync(db, channel, "bare", "Bare");
        var onlyArchived = await SeedEmoteAsync(db, channel, "oas", "Oas", firstSeenAt: T0);
        var archivedSlot = await SeedSlotAsync(db, onlyArchived, "OasOld", T0, archived: true, archivedAt: T1);
        await AddUsageAsync(db, onlyArchived, archivedSlot, SetA, (From, 5));
        var foreignSeed = await SeedSeedAsync(db, onlyArchived);
        await AddUsageAsync(db, onlyArchived, foreignSeed, SetB, (From, 70));

        var totals = await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To);

        Assert.Equal(2, totals.Count);
        Assert.All(totals, t => Assert.Null(t.SlotId));
        var archivedRow = totals.Single(t => t.EmoteId == onlyArchived.Id);
        Assert.Equal("Oas", archivedRow.EmoteName);
        Assert.Equal(5, archivedRow.TotalUseCount);
        Assert.Equal(T0, archivedRow.FirstSeenAt);
        var bareRow = totals.Single(t => t.EmoteId == bare.Id);
        Assert.Equal(0, bareRow.TotalUseCount);
        Assert.Null(bareRow.LastUsedDate);
    }

    // R1: the database of the AK 28 case read with the flag off is the pre-slot reading — one row per emote
    // with its name, date and whole count, the new fields at their off values, one series entry summed per
    // day. The same database with the flag on shows the two slots, so the difference is the flag alone.
    [Fact]
    public async Task Off_TheSameSlots_ReadAsBeforeSlots()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_off");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var ome44 = await SeedSlotAsync(db, emote, "ome44", T1);
        var hob44 = await SeedSlotAsync(db, emote, "hob44", T2);
        await AddUsageAsync(db, emote, ome44, SetA, (From, 3), (From.AddDays(1), 4));
        await AddUsageAsync(db, emote, hob44, SetA, (From.AddDays(1), 5));

        var off = Service(db, on: false);
        var row = Assert.Single(await off.GetUsageContextAsync(channel.ChannelName, From, To));
        var entry = Assert.Single((await off.GetChannelSeriesAsync(channel.ChannelName, From, To)).Emotes);

        Assert.Equal(
            (emote.Id, (string?)null, "ome44", "x", Url("x"), 12, (DateOnly?)From.AddDays(1), 0, (DateTime?)T0, false, false),
            (row.EmoteId, row.SlotId, row.EmoteName, row.SevenTvEmoteId, row.ImageUrl, row.TotalUseCount, row.LastUsedDate,
                row.PreviousWindowUseCount, row.FirstSeenAt, row.FirstSeenAtFromRest, row.IsArchived));
        Assert.Empty(row.NameTwinEmoteSetIds);
        Assert.Empty(row.SiblingNames);
        Assert.Equal(("x", emote.Id, (string?)null, "ome44"), (entry.SevenTvEmoteId, entry.EmoteId, entry.SlotId, entry.Name));
        Assert.Equal([[0, 3], [1, 9]], entry.Days);
        Assert.Equal(2, (await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To)).Count);
    }

    // AK 31: one entry per grid row — a slot-mode entry is its slot's days, an emote-mode entry sums each day
    // over the emote's slots; ordered by EmoteId, within one emote in the D2 order of its slots (by date,
    // not by name); a row without own usage in the range has no entry.
    [Fact]
    public async Task On_Series_OneEntryPerRow_InRowOrder()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_series");
        var slotted = await SeedEmoteAsync(db, channel, "x", "zzz", firstSeenAt: T0);
        var older = await SeedSlotAsync(db, slotted, "zzz", T1);
        var newer = await SeedSlotAsync(db, slotted, "aaa", T2);
        var quiet = await SeedSlotAsync(db, slotted, "mmm", T3);
        await AddUsageAsync(db, slotted, older, SetA, (From, 3));
        await AddUsageAsync(db, slotted, newer, SetA, (From, 4), (From.AddDays(2), 1));
        await AddUsageAsync(db, slotted, quiet, SetA, (new DateOnly(2026, 7, 3), 8), (From.AddDays(3), 0));
        var seeded = await SeedEmoteAsync(db, channel, "y", "Seeded");
        var seed = await SeedSeedAsync(db, seeded);
        var settled = await SeedSlotAsync(db, seeded, "Seeded", T1);
        await AddUsageAsync(db, seeded, seed, SetA, (From.AddDays(1), 2));
        await AddUsageAsync(db, seeded, settled, SetA, (From.AddDays(1), 5));

        var series = await Service(db, on: true).GetChannelSeriesAsync(channel.ChannelName, From, To);

        var slottedEntries = new[]
        {
            new EntryShape(slotted.Id, older.Id, "zzz", [[0, 3]]),
            new EntryShape(slotted.Id, newer.Id, "aaa", [[0, 4], [2, 1]])
        };
        var seededEntries = new[] { new EntryShape(seeded.Id, null, "Seeded", [[1, 7]]) };
        var expected = string.CompareOrdinal(slotted.Id, seeded.Id) < 0
            ? slottedEntries.Concat(seededEntries)
            : seededEntries.Concat(slottedEntries);
        Assert.Equal(
            expected.Select(e => (e.EmoteId, e.SlotId, e.Name, Days: Flatten(e.Days))),
            series.Emotes.Select(e => (e.EmoteId, e.SlotId, e.Name, Days: Flatten(e.Days))));
    }

    // Plan 2.6: a slot-mode row's twins are found through its slot name — another emote's Emote.Name or one
    // of its own slot names — and point at every set that other emote was counted under, the viewed one
    // excepted. The row's own emote is never its twin, whatever it was counted under. Flag off: the
    // pre-slot rule over Emote.Name alone.
    [Fact]
    public async Task On_NameTwins_AreFoundThroughSlotNames()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_twins");
        var emote = await SeedEmoteAsync(db, channel, "x", "First", firstSeenAt: T0);
        var first = await SeedSlotAsync(db, emote, "First", T1);
        var shared = await SeedSlotAsync(db, emote, "Shared", T2);
        await AddUsageAsync(db, emote, first, SetD, (new DateOnly(2026, 5, 1), 1));
        var namedTwin = await SeedEmoteAsync(db, channel, "y", "Shared", archived: true);
        var namedTwinSlot = await SeedSlotAsync(db, namedTwin, "Shared", T0, archived: true, archivedAt: T1);
        await AddUsageAsync(db, namedTwin, namedTwinSlot, SetB, (new DateOnly(2026, 6, 1), 4));
        var aliasTwin = await SeedEmoteAsync(db, channel, "z", "Zed", archived: true);
        var aliasTwinSlot = await SeedSlotAsync(db, aliasTwin, "First", T0, archived: true, archivedAt: T1);
        await AddUsageAsync(db, aliasTwin, aliasTwinSlot, SetC, (new DateOnly(2026, 6, 1), 2));
        await AddUsageAsync(db, aliasTwin, aliasTwinSlot, SetA, (new DateOnly(2026, 6, 2), 2));

        var on = await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To);
        var off = Assert.Single(await Service(db, on: false).GetUsageContextAsync(channel.ChannelName, From, To));

        Assert.Equal([SetC], on.Single(t => t.SlotId == first.Id).NameTwinEmoteSetIds);
        Assert.Equal([SetB], on.Single(t => t.SlotId == shared.Id).NameTwinEmoteSetIds);
        Assert.Empty(off.NameTwinEmoteSetIds);
    }

    // F6 (plan #377 2.12), non-active view: only one of two own slots with the same authoritative date has
    // rows under the viewed set. The filtered totals hide the twin; the flag is how the reader still knows.
    [Fact]
    public async Task On_NonActiveView_ATwinWithoutRowsInTheSet_StillFlagsTheVisibleRow()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_shared_hidden");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var a = await SeedSlotAsync(db, emote, "ome44", T1);
        await SeedSlotAsync(db, emote, "hob44", T1);
        await AddUsageAsync(db, emote, a, SetB, (From, 100));

        var row = Assert.Single(await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To, SetB));

        Assert.Equal(a.Id, row.SlotId);
        Assert.Equal(100, row.TotalUseCount);
        Assert.True(row.FirstSeenAtShared);
    }

    [Fact]
    public async Task On_NonActiveView_TwinsWithRowsInTheSet_AreBothFlagged()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_shared_both");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var a = await SeedSlotAsync(db, emote, "ome44", T1);
        var b = await SeedSlotAsync(db, emote, "hob44", T1);
        await AddUsageAsync(db, emote, a, SetB, (From, 100));
        await AddUsageAsync(db, emote, b, SetB, (From, 7));

        var totals = await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To, SetB);

        Assert.Equal(2, totals.Count);
        Assert.All(totals, t => Assert.True(t.FirstSeenAtShared));
    }

    [Fact]
    public async Task On_NonActiveView_DifferentDates_AreNotFlagged()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_shared_distinct");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var a = await SeedSlotAsync(db, emote, "ome44", T1);
        var b = await SeedSlotAsync(db, emote, "hob44", T2);
        await AddUsageAsync(db, emote, a, SetB, (From, 100));
        await AddUsageAsync(db, emote, b, SetB, (From, 7));

        var totals = await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To, SetB);

        Assert.Equal(2, totals.Count);
        Assert.All(totals, t => Assert.False(t.FirstSeenAtShared));
    }

    [Fact]
    public async Task Off_SlotsSharingADate_AreNeverFlagged_AndOnTheActiveViewTheRuleIsTheSame()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotread_shared_off");
        var emote = await SeedEmoteAsync(db, channel, "x", "ome44", firstSeenAt: T0);
        var a = await SeedSlotAsync(db, emote, "ome44", T1);
        var b = await SeedSlotAsync(db, emote, "hob44", T1);
        await AddUsageAsync(db, emote, a, SetA, (From, 3));
        await AddUsageAsync(db, emote, b, SetA, (From, 4));

        var off = await Service(db, on: false).GetUsageContextAsync(channel.ChannelName, From, To);
        var on = await Service(db, on: true).GetUsageContextAsync(channel.ChannelName, From, To);

        var offRow = Assert.Single(off);
        Assert.Null(offRow.SlotId);
        Assert.False(offRow.FirstSeenAtShared);
        Assert.Equal(2, on.Count);
        Assert.All(on, t => Assert.True(t.FirstSeenAtShared));
    }

    private static UsageStatQueryService Service(AppDbContext db, bool on) =>
        new(db, new SlotReconcileOptions { Enabled = on });

    private static async Task AddUsageAsync(AppDbContext db, Emote emote, EmoteSlot slot, string setId, params (DateOnly Date, int UseCount)[] days)
    {
        foreach (var (date, useCount) in days)
        {
            db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, SlotId = slot.Id, EmoteSetId = setId, Date = date, UseCount = useCount });
        }

        await db.SaveChangesAsync();
    }

    private static string Flatten(IEnumerable<int[]> days) => string.Join(" ", days.Select(d => $"{d[0]}:{d[1]}"));

    private sealed record EntryShape(string EmoteId, string? SlotId, string Name, int[][] Days);
}
