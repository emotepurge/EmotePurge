using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 3 (wish emotes, T6): the detail with its usage window (E4), the coverage-gap flag (E5) and the
// rank among the active slots (E16), against UsageStats seeded per slot and day. The clock shows
// 2026-09-14 12:00 UTC, so "today" is 2026-09-14.
public partial class WishServiceTests
{
    private static DateOnly Day(int daysAgo) => new DateOnly(2026, 9, 14).AddDays(-daysAgo);

    [Fact]
    public async Task Detail_SumsAllSlotsOfTheEmote_OverTheWindowDays_AndDividesByTheFractionalLength()
    {
        var channel = await SeedHealthyChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var current = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-3));
        var earlierStay = await SeedSlotAsync(emote, "OldKappa", Now.AddDays(-20), archived: true);
        var wishId = await SeedLinkedWishAsync(channel, emote, current, startsAt: Now.AddDays(-3), expiresAt: Now.AddDays(27));

        // The window starts at the wish's start (2026-09-11 12:00): that UTC day counts, the day before does not.
        await SeedUsageAsync(emote, current, Day(4), 100);
        await SeedUsageAsync(emote, current, Day(3), 10);
        await SeedUsageAsync(emote, earlierStay, Day(3), 5);
        await SeedUsageAsync(emote, current, Day(0), 7);

        var detail = (await Sut().GetAsync(channel.Name, wishId))!;

        var usage = detail.Usage!;
        Assert.Equal(Now.AddDays(-3), usage.WindowFromUtc);
        Assert.Equal(Now, usage.WindowToUtc);
        Assert.Equal(22, usage.Total);
        Assert.Equal(22m / 3m, usage.PerDayMean);
        Assert.Equal(wishId, detail.Id);
    }

    [Fact]
    public async Task Detail_Window_IsCappedAtSevenDays_ForAnOlderWish()
    {
        var channel = await SeedHealthyChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-20));
        var wishId = await SeedLinkedWishAsync(channel, emote, slot, startsAt: Now.AddDays(-20), expiresAt: Now.AddDays(10));
        await SeedUsageAsync(emote, slot, Day(8), 50);
        await SeedUsageAsync(emote, slot, Day(7), 4);
        await SeedUsageAsync(emote, slot, Day(1), 3);

        var usage = (await Sut().GetAsync(channel.Name, wishId))!.Usage!;

        Assert.Equal(Now.AddDays(-7), usage.WindowFromUtc);
        Assert.Equal(7, usage.Total);
        Assert.Equal(1m, usage.PerDayMean);
        Assert.False(usage.CoverageGap);
    }

    [Fact]
    public async Task Detail_IgnoresUsageOfOtherChannels_EvenForTheSameSevenTvEmote()
    {
        var channel = await SeedHealthyChannelAsync();
        var other = await SeedHealthyChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-2));
        var foreignEmote = await SeedEmoteAsync(other);
        await using (var db = fixture.CreateDbContext())
        {
            var tracked = await db.Emotes.SingleAsync(e => e.Id == foreignEmote.Id);
            tracked.SevenTvEmoteId = emote.SevenTvEmoteId;
            await db.SaveChangesAsync();
        }

        var foreignSlot = await SeedSlotAsync(foreignEmote, "Kappa", Now.AddDays(-2));
        var wishId = await SeedLinkedWishAsync(channel, emote, slot, startsAt: Now.AddDays(-2), expiresAt: Now.AddDays(28));
        await SeedUsageAsync(emote, slot, Day(1), 6);
        await SeedUsageAsync(foreignEmote, foreignSlot, Day(1), 600);

        Assert.Equal(6, (await Sut().GetAsync(channel.Name, wishId))!.Usage!.Total);
    }

    [Fact]
    public async Task Detail_RanksTheTotalAmongTheActiveSlots_TiesShareTheBetterRank_ArchivedSlotsDoNotCount()
    {
        var channel = await SeedHealthyChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Mine", Now.AddDays(-3));
        var wishId = await SeedLinkedWishAsync(channel, emote, slot, startsAt: Now.AddDays(-3), expiresAt: Now.AddDays(27));
        await SeedUsageAsync(emote, slot, Day(1), 5);

        var top = await SeedEmoteAsync(channel);
        var topSlot = await SeedSlotAsync(top, "Top", Now.AddDays(-9));
        await SeedUsageAsync(top, topSlot, Day(1), 10);
        var tie = await SeedEmoteAsync(channel);
        var tieSlot = await SeedSlotAsync(tie, "Tie", Now.AddDays(-9));
        await SeedUsageAsync(tie, tieSlot, Day(2), 5);
        var unused = await SeedEmoteAsync(channel);
        await SeedSlotAsync(unused, "Unused", Now.AddDays(-9));
        var gone = await SeedEmoteAsync(channel);
        var goneSlot = await SeedSlotAsync(gone, "Gone", Now.AddDays(-9), archived: true);
        await SeedUsageAsync(gone, goneSlot, Day(1), 1_000);

        var usage = (await Sut().GetAsync(channel.Name, wishId))!.Usage!;

        // Active slots: Mine (5), Top (10), Tie (5), Unused (0). Mine and Tie share rank 2.
        Assert.Equal(2, usage.Rank);
        Assert.Equal(4, usage.RankOf);
    }

    [Fact]
    public async Task Detail_HasNoRank_WhenTheEmoteHasNoActiveSlot()
    {
        var channel = await SeedHealthyChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Gone", Now.AddDays(-3), archived: true);
        var wishId = await SeedLinkedWishAsync(channel, emote, slot, startsAt: Now.AddDays(-3), expiresAt: Now.AddDays(27), status: WishStatus.Removed);

        var usage = (await Sut().GetAsync(channel.Name, wishId))!.Usage!;

        Assert.Null(usage.Rank);
        Assert.Null(usage.RankOf);
    }

    [Fact]
    public async Task Detail_WithoutAStart_HasNoUsage()
    {
        var channel = await SeedHealthyChannelAsync();
        var wishId = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);

        var detail = (await Sut().GetAsync(channel.Name, wishId))!;

        Assert.Null(detail.Usage);
        Assert.Equal(WishStatus.Approved, detail.Status);
    }

    [Fact]
    public async Task Detail_FlagsACoverageGap_ForADeactivatedChannel_AnInactiveBot_AnUnresumedTracking_AndAShortWindow()
    {
        var healthy = await SeedHealthyChannelAsync();
        Assert.False(await GapAsync(healthy, startsAt: Now.AddDays(-3)));

        var deactivated = await SeedHealthyChannelAsync();
        await UpdateChannelAsync(deactivated, c => c.DeactivatedAtUtc = Now.AddDays(-1));
        Assert.True(await GapAsync(deactivated, startsAt: Now.AddDays(-3)));

        var botOff = await SeedHealthyChannelAsync();
        await UpdateChannelAsync(botOff, c => c.IsBotActive = false);
        Assert.True(await GapAsync(botOff, startsAt: Now.AddDays(-3)));

        var resumed = await SeedHealthyChannelAsync();
        await UpdateChannelAsync(resumed, c => c.TrackingResumedAt = Now.AddDays(-1));
        Assert.True(await GapAsync(resumed, startsAt: Now.AddDays(-3)));

        // Exactly 24 hours is valid, less is a gap (E4).
        var shortWindow = await SeedHealthyChannelAsync();
        Assert.False(await GapAsync(shortWindow, startsAt: Now.AddHours(-24)));
        var tooShort = await SeedHealthyChannelAsync();
        Assert.True(await GapAsync(tooShort, startsAt: Now.AddHours(-23)));
    }

    [Fact]
    public async Task Detail_OfAnotherChannelsWish_IsNull()
    {
        var channel = await SeedHealthyChannelAsync();
        var other = await SeedHealthyChannelAsync();
        var foreign = await SeedWishAsync(other, EmoteId(), WishStatus.Approved);

        Assert.Null(await Sut().GetAsync(channel.Name, foreign));
    }

    // ------------------------------------------------------------------------------------ detail helpers

    private async Task<bool> GapAsync(Seeded channel, DateTime startsAt)
    {
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", startsAt);
        var wishId = await SeedLinkedWishAsync(channel, emote, slot, startsAt, startsAt.AddDays(30));
        return (await Sut().GetAsync(channel.Name, wishId))!.Usage!.CoverageGap;
    }

    // A channel that counted the whole time: bot active, tracked since long before every window used here.
    private async Task<Seeded> SeedHealthyChannelAsync()
    {
        var channel = await SeedChannelAsync();
        await UpdateChannelAsync(channel, c =>
        {
            c.IsBotActive = true;
            c.CreatedAt = Now.AddDays(-400);
        });
        return channel;
    }

    private async Task UpdateChannelAsync(Seeded channel, Action<Channel> change)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.Channels.SingleAsync(c => c.Id == channel.Id);
        change(row);
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedLinkedWishAsync(
        Seeded channel, Emote emote, string slotId, DateTime startsAt, DateTime expiresAt, string status = WishStatus.Active)
    {
        await using var db = fixture.CreateDbContext();
        var wish = NewWish(channel.Id, emote.SevenTvEmoteId, status, slotId);
        wish.StartsAtUtc = startsAt;
        wish.StartsAtAuthoritative = true;
        wish.ExpiresAtUtc = expiresAt;
        wish.EndedAtUtc = WishStatus.IsEnded(status) ? Now : null;
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private async Task SeedUsageAsync(Emote emote, string slotId, DateOnly date, int useCount)
    {
        await using var db = fixture.CreateDbContext();
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, SlotId = slotId, EmoteSetId = "set-1", Date = date, UseCount = useCount });
        await db.SaveChangesAsync();
    }
}
