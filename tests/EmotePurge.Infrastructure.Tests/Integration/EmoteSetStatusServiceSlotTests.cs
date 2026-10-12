using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The slot-aware readers of EmoteSetStatusService (plan #372, 2.5 / 6.7): OccupiedSlots and
// DuplicateNames count active own slots, with the virtual slot of E14 for an emote that has none.
// The flag-off cases prove that slots left behind by an earlier on-phase do not change the answer.
[Collection("Postgres")]
public class EmoteSetStatusServiceSlotTests(PostgresFixture fixture)
{
    private async Task<EmoteSetStatusDto> StatusAsync(Channel channel, bool slotsEnabled)
    {
        await using var db = fixture.CreateDbContext();
        var status = await new EmoteSetStatusService(db, new UsageStatQueryService(db), new SlotReconcileOptions { Enabled = slotsEnabled })
            .GetAsync(channel.ChannelName);
        return status!;
    }

    // AK 32: two slots of one emote are two places and never a duplicate.
    [Fact]
    public async Task On_TwoSlotsOfOneEmote_AreTwoPlacesAndNoDuplicate()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotrd_two_slots");
        var x = await SeedEmoteAsync(db, channel, "x", "Same");
        await SeedSlotAsync(db, x, "Same", T1);
        await SeedSlotAsync(db, x, "Same", T2);
        var y = await SeedEmoteAsync(db, channel, "y", "Other");
        await SeedSlotAsync(db, y, "Other", T1);

        var status = await StatusAsync(channel, true);

        Assert.Equal(3, status.OccupiedSlots);
        Assert.Empty(status.DuplicateNames);
    }

    // AK 32: a collision across emotes is found through a second name, not only Emote.Name.
    [Fact]
    public async Task On_CollisionThroughASecondAlias_ReportsBothEmotes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotrd_collision");
        var x = await SeedEmoteAsync(db, channel, "x", "First");
        await SeedSlotAsync(db, x, "First", T1);
        await SeedSlotAsync(db, x, "Shared", T2);
        var y = await SeedEmoteAsync(db, channel, "y", "Shared");
        await SeedSlotAsync(db, y, "Shared", T3);

        var status = await StatusAsync(channel, true);

        var group = Assert.Single(status.DuplicateNames);
        Assert.Equal("Shared", group.Name);
        Assert.Equal(["x", "y"], group.Emotes.Select(e => e.SevenTvEmoteId));
        Assert.Equal(3, status.OccupiedSlots);
    }

    // E14: no active own slot means exactly one virtual slot under Emote.Name; an unsettled seed is
    // not a slot of its own; slots under an archived emote and archived slots do not count.
    [Fact]
    public async Task On_VirtualSlotRule()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotrd_virtual");
        await SeedEmoteAsync(db, channel, "bare", "Bare");
        var seeded = await SeedEmoteAsync(db, channel, "seeded", "Seeded");
        await SeedSeedAsync(db, seeded);
        var onlyArchivedSlot = await SeedEmoteAsync(db, channel, "oas", "Oas");
        await SeedSlotAsync(db, onlyArchivedSlot, "Oas", T1, archived: true, archivedAt: T2);
        var gone = await SeedEmoteAsync(db, channel, "gone", "Gone", archived: true);
        await SeedSlotAsync(db, gone, "Gone", T1);
        var clash = await SeedEmoteAsync(db, channel, "clash", "Clash");
        await SeedSlotAsync(db, clash, "Bare", T1);

        var status = await StatusAsync(channel, true);

        // bare (virtual) + seeded (virtual) + oas (virtual) + clash (one slot) = 4
        Assert.Equal(4, status.OccupiedSlots);
        var group = Assert.Single(status.DuplicateNames);
        Assert.Equal("Bare", group.Name);
        Assert.Equal(["bare", "clash"], group.Emotes.Select(e => e.SevenTvEmoteId));
    }

    // 2.0: with the flag off, own slots left from an earlier on-phase change nothing.
    [Fact]
    public async Task Off_WithOwnSlotsPresent_AnswersEmoteWide()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slotrd_off");
        var x = await SeedEmoteAsync(db, channel, "x", "Plain");
        await SeedSlotAsync(db, x, "Plain", T1);
        await SeedSlotAsync(db, x, "Second", T2);
        var y = await SeedEmoteAsync(db, channel, "y", "Second");
        await SeedSlotAsync(db, y, "Second", T3);
        await SeedEmoteAsync(db, channel, "z", "Plain");

        var status = await StatusAsync(channel, false);

        Assert.Equal(3, status.OccupiedSlots); // three active emotes, however many slots they hold
        var group = Assert.Single(status.DuplicateNames);
        Assert.Equal("Plain", group.Name);
        Assert.Equal(["x", "z"], group.Emotes.Select(e => e.SevenTvEmoteId));
    }
}
