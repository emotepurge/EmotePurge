using EmotePurge.Core.Entities;
using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The match map in both flag states (plan #373 §3.2, E7/E8; spec 6.1, D15, AK 15, AK 17). Flag on: one
// entry per active, settled slot of an active emote, nothing for an emote with an unsettled seed of the
// active set (all its slots), no virtual entries, the collision report over exactly that input. Flag
// off: today's names (Emote.Name), the value is the pair's base slot or a pending key, and building the
// map writes nothing.
[Collection("Postgres")]
public class SevenTvSyncServiceSlotMapTests(PostgresFixture fixture)
{
    private const string NotCountedLine = "active emotes are not counted until";
    private const string CountedAgainLine = "every active emote with a slot is counted again";
    private const string CollisionLine = "are shared by more than one emote";

    // ------------------------------------------------------------------ flag on

    // AK 17: a boot with a seed of the active set leaves the emote out of the map, so nothing of it is
    // counted and a flush before the settle writes nothing for it; the information line names it once.
    // The dated reconcile settles the seed onto the oldest entry in place: ome44 then counts on the slot
    // with the seed's history, hob44 on its new slot (O1: one message with both names hits both).
    [Fact]
    public async Task FlagOn_SeedOfTheActiveSet_KeepsTheEmoteOutUntilTheSettle_ThenBothSlotsCount()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_ak17");
        var x = await SeedEmoteAsync(db, channel, "x", "hob44");
        var seed = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seed, SetA, 5, 7);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();
        var service = CreateService(db, ApiReturning(channel, SetA, Live("x", "ome44", T1), Live("x", "hob44", T2)), true, cache, logger);

        await service.WarmChannelAsync(channel.ChannelName);

        var warmed = cache.GetChannelSnapshot(channel.ChannelName);
        Assert.Empty(warmed.NameToSlotId);
        Assert.Empty(EmoteNameMatching.MatchEmoteIds("hob44 ome44", warmed.NameToSlotId));
        Assert.Equal([$"{channel.ChannelName}: 1 {NotCountedLine} their unsettled seed of the active set is settled."], Lines(logger, NotCountedLine));

        await service.SyncChannelAsync(channel.ChannelName);

        var map = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        var hob44 = Assert.Single(await SlotsAsync(db, x.Id), s => s.Name == "hob44");
        Assert.Equal(seed.Id, map["ome44"]);
        Assert.Equal(hob44.Id, map["hob44"]);
        Assert.Equal(2, map.Count);
        Assert.Equal(2, EmoteNameMatching.MatchEmoteIds("ome44 hob44 ome44", map).Count);
        Assert.Single(Lines(logger, NotCountedLine));
        Assert.Single(Lines(logger, CountedAgainLine));
    }

    // Without v4 dates the settle stays deferred, and the emote stays out of the map — over as many ticks
    // as it takes; the first dated tick settles it and brings it in.
    [Fact]
    public async Task FlagOn_DeferredSettle_KeepsTheEmoteOutOverTwoTicks_AndADatedTickBringsItIn()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_deferred");
        var x = await SeedEmoteAsync(db, channel, "x", "B");
        await SeedEmoteAsync(db, channel, "y", "Other");
        var seed = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seed, SetA, 3);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();
        var exclusions = new MatchMapExclusionTracker();
        SevenTvEmote[] undated = [Live("x", "A", T1), Live("x", "B", null), Live("y", "Other", T1)];

        for (var tick = 0; tick < 2; tick++)
        {
            await CreateService(db, ApiReturning(channel, SetA, undated), true, cache, logger, exclusions).SyncChannelAsync(channel.ChannelName);

            var map = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
            Assert.Equal(["Other"], map.Keys);
            Assert.True((await SlotAsync(db, seed.Id)).SeedUnsettled);
        }

        // The information line names the left-out emote once, not on every tick.
        Assert.Single(Lines(logger, NotCountedLine));

        await CreateService(db, ApiReturning(channel, SetA, Live("x", "A", T1), Live("x", "B", T2), Live("y", "Other", T1)), true, cache, logger, exclusions)
            .SyncChannelAsync(channel.ChannelName);

        var settled = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        Assert.Equal(seed.Id, settled["A"]);
        Assert.Equal(["A", "B", "Other"], settled.Keys.Order(StringComparer.Ordinal));
        Assert.Single(Lines(logger, CountedAgainLine));
    }

    // E7, revision 1: an already settled slot T of the emote does not get it into the map while an
    // unsettled seed S of the active set exists — the emote is left out with all of its slots.
    [Fact]
    public async Task FlagOn_SettledSlotBesideAnUnsettledSeedOfTheActiveSet_TheWholeEmoteIsMissing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_beside");
        var x = await SeedEmoteAsync(db, channel, "x", "A");
        var other = await SeedEmoteAsync(db, channel, "o", "Other");
        await SeedSlotAsync(db, x, "A", T1);
        await SeedSlotAsync(db, other, "Other", T1);
        var seed = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seed, SetA, 4);
        var cache = new EmoteMatchCache();

        await CreateService(db, ApiReturning(channel, SetA), true, cache).WarmChannelAsync(channel.ChannelName);

        Assert.Equal(["Other"], cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId.Keys);
    }

    // Only a seed of the active set excludes: a seed carrying rows of another set leaves the emote's
    // settled slot in the map, and no information line is written.
    [Fact]
    public async Task FlagOn_SeedOfAnotherSetOnly_TheEmoteCounts()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_foreignseed");
        var x = await SeedEmoteAsync(db, channel, "x", "A");
        var slot = await SeedSlotAsync(db, x, "A", T1);
        var seedB = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seedB, SetB, 6);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();

        await CreateService(db, ApiReturning(channel, SetA), true, cache, logger).WarmChannelAsync(channel.ChannelName);

        Assert.Equal(slot.Id, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId["A"]);
        Assert.Empty(Lines(logger, NotCountedLine));
    }

    // No virtual entries: an active emote without an active own slot (before the first reconcile with the
    // flag on) has no slot id and counts from the next reconcile on.
    [Fact]
    public async Task FlagOn_ActiveEmoteWithoutAnOwnSlot_HasNoEntry()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_novirtual");
        await SeedEmoteAsync(db, channel, "x", "Lonely");
        var y = await SeedEmoteAsync(db, channel, "y", "Archived");
        await SeedSlotAsync(db, y, "Archived", T1, archived: true, archivedAt: T2);
        var cache = new EmoteMatchCache();

        await CreateService(db, ApiReturning(channel, SetA), true, cache).WarmChannelAsync(channel.ChannelName);

        Assert.Empty(cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId);
    }

    // AK 15: two different emotes listing the same name — one entry (the first loaded wins, as today) and
    // the tracker warns; two aliases of one emote under different names are no collision.
    [Fact]
    public async Task FlagOn_NameCollisionOfTwoEmotes_OneEntryAndAWarning_TwoAliasesOfOneEmoteNoWarning()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_ak15");
        var a = await SeedEmoteAsync(db, channel, "a", "Dup");
        var b = await SeedEmoteAsync(db, channel, "b", "Dup");
        var c = await SeedEmoteAsync(db, channel, "c", "ome44");
        var slotA = await SeedSlotAsync(db, a, "Dup", T1);
        var slotB = await SeedSlotAsync(db, b, "Dup", T2);
        await SeedSlotAsync(db, c, "ome44", T1);
        await SeedSlotAsync(db, c, "hob44", T2);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();

        await CreateService(db, ApiReturning(channel, SetA), true, cache, logger).WarmChannelAsync(channel.ChannelName);

        var map = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        Assert.Equal(["Dup", "hob44", "ome44"], map.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(map["Dup"], new[] { slotA.Id, slotB.Id });
        var warning = Assert.Single(Lines(logger, CollisionLine));
        Assert.Contains("1 active emote names", warning, StringComparison.Ordinal);
        Assert.Contains(": Dup —", warning, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ flag off

    // E8: Emote.Name as today; the value is the slot carrying rows of the active set, else the pending
    // key — never a seed of another set; archived emotes are missing.
    [Fact]
    public async Task FlagOff_NamesFromTheEmote_ValueIsThePairsSlotOrThePendingKey_NeverAnotherSetsSeed()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_off");
        var counted = await SeedEmoteAsync(db, channel, "c", "Counted");
        var fresh = await SeedEmoteAsync(db, channel, "f", "Fresh");
        var foreign = await SeedEmoteAsync(db, channel, "g", "ForeignOnly");
        var archived = await SeedEmoteAsync(db, channel, "z", "Gone", archived: true);
        var countedSeed = await SeedSeedAsync(db, counted);
        await SeedUsageAsync(db, counted, countedSeed, SetA, 2);
        var foreignSeed = await SeedSeedAsync(db, foreign);
        await SeedUsageAsync(db, foreign, foreignSeed, SetB, 3);
        var archivedSeed = await SeedSeedAsync(db, archived);
        await SeedUsageAsync(db, archived, archivedSeed, SetA, 1);
        var cache = new EmoteMatchCache();

        await CreateService(db, ApiReturning(channel, SetA), false, cache).WarmChannelAsync(channel.ChannelName);

        var map = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        Assert.Equal(3, map.Count);
        Assert.Equal(countedSeed.Id, map["Counted"]);
        Assert.Equal(PendingSlotKey.For(fresh.Id), map["Fresh"]);
        Assert.Equal(PendingSlotKey.For(foreign.Id), map["ForeignOnly"]);
    }

    // A set switch with the flag off: the pair of the new set has no slot yet → the pending key; a seed of
    // the new set is the value. A rename in 7TV (K1 renames the emote) keeps the pair's slot.
    [Fact]
    public async Task FlagOff_SetSwitchHandsOutThePendingKey_AndARenameKeepsThePairsSlot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_off_switch");
        var x = await SeedEmoteAsync(db, channel, "x", "X");
        var y = await SeedEmoteAsync(db, channel, "y", "Y");
        var seedXA = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seedXA, SetA, 2);
        var seedYB = await SeedSeedAsync(db, y);
        await SeedUsageAsync(db, y, seedYB, SetB, 2);
        var cache = new EmoteMatchCache();

        await SyncOffAsync(db, channel, cache, SetA, Live("x", "Renamed", T1), Live("y", "Y", T1));
        var onA = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        Assert.Equal(seedXA.Id, onA["Renamed"]);
        Assert.Equal(PendingSlotKey.For(y.Id), onA["Y"]);

        await SyncOffAsync(db, channel, cache, SetB, Live("x", "Renamed", T1), Live("y", "Y", T1));
        var onB = cache.GetChannelSnapshot(channel.ChannelName);
        Assert.Equal(SetB, onB.EmoteSetId);
        Assert.Equal(PendingSlotKey.For(x.Id), onB.NameToSlotId["Renamed"]);
        Assert.Equal(seedYB.Id, onB.NameToSlotId["Y"]);
    }

    // Building the map never writes: no slot and no usage row appears or changes, however many pending
    // keys it hands out. The first flush of a pending key then creates the pair's seed together with its
    // row, and the next refresh hands out that seed.
    [Fact]
    public async Task FlagOff_BuildingTheMapWritesNothing_AndTheFirstFlushCreatesTheSeedWithTheRow()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_off_nowrite");
        var x = await SeedEmoteAsync(db, channel, "x", "X");
        var cache = new EmoteMatchCache();
        await SyncOffAsync(db, channel, cache, SetA, Live("x", "X", T1));
        var before = await ChannelStateAsync(db, channel);

        await SyncOffAsync(db, channel, cache, SetA, Live("x", "X", T1));

        Assert.Equal(before, await ChannelStateAsync(db, channel));
        var pending = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId["X"];
        Assert.Equal(PendingSlotKey.For(x.Id), pending);

        await new UsageStatFlushService(db, NullLogger<UsageStatFlushService>.Instance).FlushAsync(
            new Dictionary<UsageCounterKey, EmoteUsageCounts> { [new UsageCounterKey(pending, SetA)] = new(1, 0, 0) });
        var seed = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.True(seed.SeedUnsettled);
        Assert.Equal((1, 1), (await UsageBySlotAsync(db, x.Id, SetA))[seed.Id]);

        await SyncOffAsync(db, channel, cache, SetA, Live("x", "X", T1));
        Assert.Equal(seed.Id, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId["X"]);
    }

    private static async Task SyncOffAsync(AppDbContext db, Channel channel, EmoteMatchCache cache, string setId, params SevenTvEmote[] emotes) =>
        Assert.NotNull(await CreateService(db, ApiReturning(channel, setId, emotes), false, cache).SyncChannelAsync(channel.ChannelName));

    // Every slot and usage row of the channel as text.
    private static async Task<string> ChannelStateAsync(AppDbContext db, Channel channel)
    {
        var slots = await db.EmoteSlots.AsNoTracking().Where(s => s.Emote.ChannelId == channel.Id).ToListAsync();
        var rows = await db.UsageStats.AsNoTracking().Where(u => u.Emote.ChannelId == channel.Id)
            .Select(u => u.Id + ":" + u.SlotId + ":" + u.UseCount).ToListAsync();
        return string.Join("|", slots.Select(Columns).Order(StringComparer.Ordinal)) + "#" + string.Join("|", rows.Order(StringComparer.Ordinal));
    }
}
