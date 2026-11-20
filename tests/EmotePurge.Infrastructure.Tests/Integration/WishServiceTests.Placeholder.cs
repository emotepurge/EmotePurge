using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The K1 to K3 consumers of the emote id against an open wish without an emote (plan #486 2.8a, T1): the empty
// placeholder counts as running for nothing, has no image and no usage, and adopt fills all three fields in the
// same update, so the wish passes CK_Wishes_EmotePlaceholder in active. The later tasks add their own consumers.
public partial class WishServiceTests
{
    [Fact]
    public async Task OpenWishWithoutAnEmote_BlocksNoManualEntry_AndReadsWithoutImageOrUsage()
    {
        var channel = await SeedChannelAsync();
        var open = await SeedOpenWishWithoutEmoteAsync(channel);

        // Two open wishes without an emote never share a "running" emote with a new one.
        await SeedOpenWishWithoutEmoteAsync(channel);
        Assert.Equal(WishOutcome.Success, (await CreateAsync(channel, EmoteId(), "Fresh")).Outcome);

        var listed = (await ListAsync(channel, WishListView.Open)).Items;
        var row = Assert.Single(listed, w => w.Id == open);
        Assert.Equal((string.Empty, string.Empty, string.Empty), (row.SevenTvEmoteId, row.EmoteName, row.Alias));
        Assert.Null(row.ImageUrl);

        var detail = (await Sut().GetAsync(channel.Name, open))!;
        Assert.Null(detail.ImageUrl);
        Assert.Null(detail.Usage);
    }

    [Fact]
    public async Task Adopt_OfAnOpenWishWithoutAnEmote_FillsEmoteNameAndAliasFromTheSlot()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel, "catJAM");
        var slot = await SeedSlotAsync(emote, "catJAMslot", Now.AddHours(-1));
        var open = await SeedOpenWishWithoutEmoteAsync(channel);

        var result = await AdoptAsync(channel, slot, wishId: open);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == open);
        Assert.Equal(
            (WishStatus.Active, emote.SevenTvEmoteId, "catJAM", "catJAMslot", slot),
            (row.Status, row.SevenTvEmoteId, row.EmoteName, row.Alias, row.SlotId));
        Assert.True(row.HasEmote);
    }

    private async Task<long> SeedOpenWishWithoutEmoteAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        var wish = NewWish(channel.Id, string.Empty, WishStatus.Pending, slotId: null);
        wish.EmoteName = string.Empty;
        wish.Alias = string.Empty;
        wish.Source = WishSource.Irc;
        wish.MatchKind = WishMatchKind.None;
        wish.StatusReason = WishStatusReason.Unparsed;
        wish.RequestText = "something funny please";
        wish.RequesterTwitchUserId = "998877";
        wish.RequesterLogin = "viewer";
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }
}
