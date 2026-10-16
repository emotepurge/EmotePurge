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

// The match map (plan #373 §3.2, E7; spec 6.1, D15, AK 15, AK 17): one entry per active, settled slot of
// an active emote, nothing for an emote with an unsettled seed of the active set (all its slots), no
// virtual entries, the collision report over exactly that input.
[Collection("Postgres")]
public class SevenTvSyncServiceSlotMapTests(PostgresFixture fixture)
{
    private const string NotCountedLine = "active emotes are not counted until";
    private const string CountedAgainLine = "every active emote with a slot is counted again";
    private const string CollisionLine = "are shared by more than one emote";

    // AK 17: a boot with a seed of the active set leaves the emote out of the map, so nothing of it is
    // counted and a flush before the settle writes nothing for it; the information line names it once.
    // The dated reconcile settles the seed onto the oldest entry in place: ome44 then counts on the slot
    // with the seed's history, hob44 on its new slot (O1: one message with both names hits both).
    [Fact]
    public async Task SeedOfTheActiveSet_KeepsTheEmoteOutUntilTheSettle_ThenBothSlotsCount()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_ak17");
        var x = await SeedEmoteAsync(db, channel, "x", "hob44");
        var seed = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seed, SetA, 5, 7);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();
        var service = CreateService(db, ApiReturning(channel, SetA, Live("x", "ome44", T1), Live("x", "hob44", T2)), cache, logger);

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
    public async Task DeferredSettle_KeepsTheEmoteOutOverTwoTicks_AndADatedTickBringsItIn()
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
            await CreateService(db, ApiReturning(channel, SetA, undated), cache, logger, exclusions).SyncChannelAsync(channel.ChannelName);

            var map = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
            Assert.Equal(["Other"], map.Keys);
            Assert.True((await SlotAsync(db, seed.Id)).SeedUnsettled);
        }

        // The information line names the left-out emote once, not on every tick.
        Assert.Single(Lines(logger, NotCountedLine));

        await CreateService(db, ApiReturning(channel, SetA, Live("x", "A", T1), Live("x", "B", T2), Live("y", "Other", T1)), cache, logger, exclusions)
            .SyncChannelAsync(channel.ChannelName);

        var settled = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        Assert.Equal(seed.Id, settled["A"]);
        Assert.Equal(["A", "B", "Other"], settled.Keys.Order(StringComparer.Ordinal));
        Assert.Single(Lines(logger, CountedAgainLine));
    }

    // E7, revision 1: an already settled slot T of the emote does not get it into the map while an
    // unsettled seed S of the active set exists — the emote is left out with all of its slots.
    [Fact]
    public async Task SettledSlotBesideAnUnsettledSeedOfTheActiveSet_TheWholeEmoteIsMissing()
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

        await CreateService(db, ApiReturning(channel, SetA), cache).WarmChannelAsync(channel.ChannelName);

        Assert.Equal(["Other"], cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId.Keys);
    }

    // Only a seed of the active set excludes: a seed carrying rows of another set leaves the emote's
    // settled slot in the map, and no information line is written.
    [Fact]
    public async Task SeedOfAnotherSetOnly_TheEmoteCounts()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_foreignseed");
        var x = await SeedEmoteAsync(db, channel, "x", "A");
        var slot = await SeedSlotAsync(db, x, "A", T1);
        var seedB = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seedB, SetB, 6);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();

        await CreateService(db, ApiReturning(channel, SetA), cache, logger).WarmChannelAsync(channel.ChannelName);

        Assert.Equal(slot.Id, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId["A"]);
        Assert.Empty(Lines(logger, NotCountedLine));
    }

    // No virtual entries: an active emote without an active own slot (before its first reconcile) has no
    // slot id and counts from the next reconcile on.
    [Fact]
    public async Task ActiveEmoteWithoutAnOwnSlot_HasNoEntry()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotmap_novirtual");
        await SeedEmoteAsync(db, channel, "x", "Lonely");
        var y = await SeedEmoteAsync(db, channel, "y", "Archived");
        await SeedSlotAsync(db, y, "Archived", T1, archived: true, archivedAt: T2);
        var cache = new EmoteMatchCache();

        await CreateService(db, ApiReturning(channel, SetA), cache).WarmChannelAsync(channel.ChannelName);

        Assert.Empty(cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId);
    }

    // AK 15: two different emotes listing the same name — one entry (the first loaded wins, as today) and
    // the tracker warns; two aliases of one emote under different names are no collision.
    [Fact]
    public async Task NameCollisionOfTwoEmotes_OneEntryAndAWarning_TwoAliasesOfOneEmoteNoWarning()
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

        await CreateService(db, ApiReturning(channel, SetA), cache, logger).WarmChannelAsync(channel.ChannelName);

        var map = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        Assert.Equal(["Dup", "hob44", "ome44"], map.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(map["Dup"], new[] { slotA.Id, slotB.Id });
        var warning = Assert.Single(Lines(logger, CollisionLine));
        Assert.Contains("1 active emote names", warning, StringComparison.Ordinal);
        Assert.Contains(": Dup —", warning, StringComparison.Ordinal);
    }
}
