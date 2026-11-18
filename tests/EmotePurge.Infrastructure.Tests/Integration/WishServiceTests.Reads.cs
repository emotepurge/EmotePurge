using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService, the read fields of the manager UI (plan #485, T0a): the image at the DTO, the daily series of the
// usage window, and the adoptable entries with their conflict, checked against adopt itself. The clock shows
// 2026-09-14 12:00 UTC.
public partial class WishServiceTests
{
    private const string StoredImage = "https://cdn.7tv.app/emote/stored/2x_static.webp";

    // ------------------------------------------------------------------------------------ image

    [Fact]
    public async Task Image_ListDetailAndCommands_CarryTheChannelsStoredImage_OrNullWhenTheChannelNeverHadIt()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var withImage = await SeedEmoteRowAsync(channel, EmoteId(), StoredImage);
        var noRow = EmoteId();
        var placeholder = await SeedEmoteRowAsync(channel, EmoteId(), StoredImage, placeholder: true);
        var empty = await SeedEmoteRowAsync(channel, EmoteId(), "");
        // Another channel's row of the same emote never lends its image.
        await SeedEmoteRowAsync(other, noRow, "https://cdn.7tv.app/emote/foreign/2x.webp");
        var withImageWish = await SeedWishAsync(channel, withImage, WishStatus.Approved, alias: "A");
        await SeedWishAsync(channel, noRow, WishStatus.Approved, alias: "B");
        await SeedWishAsync(channel, placeholder, WishStatus.Approved, alias: "C");
        await SeedWishAsync(channel, empty, WishStatus.Approved, alias: "D");

        var listed = (await ListAsync(channel, WishListView.Open)).Items.ToDictionary(w => w.SevenTvEmoteId, w => w.ImageUrl);

        Assert.Equal(StoredImage, listed[withImage]);
        Assert.Null(listed[noRow]);
        Assert.Null(listed[placeholder]);
        Assert.Null(listed[empty]);
        Assert.Equal(StoredImage, (await Sut().GetAsync(channel.Name, withImageWish))!.ImageUrl);
        Assert.Equal(StoredImage, (await Sut().CloseAsync(channel.Name, withImageWish, Actor)).Wish!.ImageUrl);

        var created = await CreateAsync(channel, await SeedEmoteRowAsync(channel, EmoteId(), StoredImage), "E");
        Assert.Equal(StoredImage, created.Wish!.ImageUrl);
        Assert.Null((await CreateAsync(channel, EmoteId(), "F")).Wish!.ImageUrl);
    }

    [Fact]
    public async Task Image_OfAnAdoption_IsTheSlotsEmoteImage()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-1));

        var result = await AdoptAsync(channel, slot);

        Assert.Equal(emote.ImageUrl, result.Wish!.ImageUrl);
    }

    // Plan #485 2.1a: one extra query per answer over the emote ids in it, never one per wish.
    [Fact]
    public async Task Image_IsReadWithOneQueryForAWholePage()
    {
        var channel = await SeedChannelAsync();
        foreach (var alias in new[] { "A", "B", "C" })
        {
            await SeedWishAsync(channel, await SeedEmoteRowAsync(channel, EmoteId(), StoredImage), WishStatus.Approved, alias: alias);
        }

        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);
        await using var db = fixture.CreateDbContext([hook]);

        var page = (await Sut(db).ListAsync(channel.Name, WishListView.Open, 1, 50))!;

        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, w => Assert.Equal(StoredImage, w.ImageUrl));
        Assert.Equal(1, hook.Count(c => c.Contains("FROM \"Emotes\"", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------------------------ the generation at the DTO

    [Fact]
    public async Task SlotGeneration_IsTheWishesBoundGeneration_NullWithoutOne()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var addedAt = new DateTime(2026, 9, 12, 8, 30, 15, DateTimeKind.Utc).AddTicks(1230);
        var slot = await SeedSlotAsync(emote, "Kappa", addedAt);
        var adopted = (await AdoptAsync(channel, slot)).Wish!;
        var unbound = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);

        Assert.Equal((slot, addedAt), (adopted.SlotId, adopted.SlotAddedAtUtc));
        var detail = (await Sut().GetAsync(channel.Name, adopted.Id))!;
        Assert.Equal(addedAt, detail.SlotAddedAtUtc);
        Assert.Null((await Sut().GetAsync(channel.Name, unbound))!.SlotAddedAtUtc);
    }

    // ------------------------------------------------------------------------------------ daily series

    [Fact]
    public async Task Daily_HasEveryDayOfTheWindow_ZeroFilled_OverAllSlotsOfTheEmote_AndSumsToTheTotal()
    {
        var channel = await SeedHealthyChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var first = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-3));
        var second = await SeedSlotAsync(emote, "KappaToo", Now.AddDays(-3));
        var wishId = await SeedLinkedWishAsync(channel, emote, first, startsAt: Now.AddDays(-3), expiresAt: Now.AddDays(27));
        await SeedUsageAsync(emote, first, Day(4), 100);
        await SeedUsageAsync(emote, first, Day(3), 10);
        await SeedUsageAsync(emote, second, Day(3), 5);
        await SeedUsageAsync(emote, second, Day(1), 7);
        await SeedUsageAsync(emote, first, Day(0), 2);

        var usage = (await Sut().GetAsync(channel.Name, wishId))!.Usage!;

        Assert.Equal(
            [new WishUsageDayDto(Day(3), 15), new WishUsageDayDto(Day(2), 0), new WishUsageDayDto(Day(1), 7), new WishUsageDayDto(Day(0), 2)],
            usage.Daily);
        Assert.Equal(usage.Total, usage.Daily.Sum(d => d.UseCount));
        Assert.Equal(24, usage.Total);
    }

    [Fact]
    public async Task Daily_OfAWindowWithinOneDay_HasOneEntry_AndOfTheCappedWindow_EightAtMost()
    {
        var channel = await SeedHealthyChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddHours(-2));
        var young = await SeedLinkedWishAsync(channel, emote, slot, startsAt: Now.AddHours(-2), expiresAt: Now.AddDays(30));
        await SeedUsageAsync(emote, slot, Day(0), 3);

        var oneDay = (await Sut().GetAsync(channel.Name, young))!.Usage!;
        Assert.Equal([new WishUsageDayDto(Day(0), 3)], oneDay.Daily);

        var old = await SeedEmoteAsync(channel);
        var oldSlot = await SeedSlotAsync(old, "Old", Now.AddDays(-20));
        var oldWish = await SeedLinkedWishAsync(channel, old, oldSlot, startsAt: Now.AddDays(-20), expiresAt: Now.AddDays(10));
        await SeedUsageAsync(old, oldSlot, Day(8), 50);
        await SeedUsageAsync(old, oldSlot, Day(7), 4);

        var capped = (await Sut().GetAsync(channel.Name, oldWish))!.Usage!;
        Assert.Equal(8, capped.Daily.Count);
        Assert.Equal((Day(7), Day(0)), (capped.Daily[0].Date, capped.Daily[^1].Date));
        Assert.Equal(capped.Total, capped.Daily.Sum(d => d.UseCount));
        Assert.Equal(4, capped.Total);
    }

    // ------------------------------------------------------------------------------------ adoptable entries

    // The list against adopt itself: every listed entry without holder and running wish is taken over, every
    // entry left out is refused as not found.
    [Fact]
    public async Task Slots_ListExactlyTheEntriesAdoptAccepts_OrderedByAlias()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var free = await SeedSlotAsync(await SeedEmoteAsync(channel), "Delta", Now.AddDays(-2));
        var provisional = await SeedSlotAsync(await SeedEmoteAsync(channel), "Bravo", Now.AddDays(-1), fromRest: false);
        var undated = await SeedSlotAsync(await SeedEmoteAsync(channel), "Charlie", null);
        var archived = await SeedSlotAsync(await SeedEmoteAsync(channel), "Archived", Now.AddDays(-5), archived: true);
        // Sol-P2-6: an unsettled seed next to an own slot of the same emote; the own slot is listed, the seed not.
        var seeded = await SeedEmoteAsync(channel);
        var own = await SeedSlotAsync(seeded, "Alpha", Now.AddDays(-3));
        var seed = await SeedSlotAsync(seeded, "AlphaSeed", null, seed: true);
        var foreign = await SeedSlotAsync(await SeedEmoteAsync(other), "Foreign", Now.AddDays(-2));

        var entries = (await Sut().ListAdoptableSlotsAsync(channel.Name, null))!;

        Assert.Equal([own, provisional, undated, free], entries.Select(e => e.SlotId));
        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta"], entries.Select(e => e.Alias));
        var byId = entries.ToDictionary(e => e.SlotId);
        Assert.Equal((Now.AddDays(-2), true), (byId[free].AddedAtUtc, byId[free].AddedAtAuthoritative));
        Assert.Equal((Now.AddDays(-1), false), (byId[provisional].AddedAtUtc, byId[provisional].AddedAtAuthoritative));
        Assert.Equal(((DateTime?)null, false), (byId[undated].AddedAtUtc, byId[undated].AddedAtAuthoritative));
        Assert.All(entries, e =>
        {
            Assert.Equal("https://cdn.7tv.app/emote/example/2x.webp", e.ImageUrl);
            Assert.Equal(((long?)null, (long?)null, false), (e.HeldByWishId, e.RunningWishId, e.RunningWishAdoptable));
        });

        foreach (var listed in entries)
        {
            Assert.Equal(WishOutcome.Success, (await AdoptAsync(channel, listed.SlotId)).Outcome);
        }

        foreach (var left in new[] { archived, seed, foreign })
        {
            Assert.Equal(WishOutcome.SlotNotFound, (await AdoptAsync(channel, left)).Outcome);
        }
    }

    [Fact]
    public async Task Slots_NameTheHolder_AlsoAKeptOne_AndAdoptRefusesThatEntry()
    {
        var channel = await SeedChannelAsync();
        var keptEmote = await SeedEmoteAsync(channel);
        var keptSlot = await SeedSlotAsync(keptEmote, "Kept", Now.AddDays(-4));
        var kept = await SeedHolderAsync(channel, keptEmote, keptSlot, WishStatus.Kept);
        var activeEmote = await SeedEmoteAsync(channel);
        var activeSlot = await SeedSlotAsync(activeEmote, "Live", Now.AddDays(-4));
        var active = await SeedHolderAsync(channel, activeEmote, activeSlot, WishStatus.Active);

        var entries = (await Sut().ListAdoptableSlotsAsync(channel.Name, null))!.ToDictionary(e => e.SlotId);

        Assert.Equal((kept, (long?)null), (entries[keptSlot].HeldByWishId, entries[keptSlot].RunningWishId));
        // The holder of this very slot is no "other running wish" of it.
        Assert.Equal((active, (long?)null, false), (entries[activeSlot].HeldByWishId, entries[activeSlot].RunningWishId, entries[activeSlot].RunningWishAdoptable));
        Assert.Equal(WishOutcome.SlotAlreadyLinked, (await AdoptAsync(channel, keptSlot)).Outcome);
        Assert.Equal(WishOutcome.SlotAlreadyLinked, (await AdoptAsync(channel, activeSlot)).Outcome);
    }

    // An emote under two aliases: the second entry names the wish running at the first, and that wish does not
    // accept a second entry.
    [Fact]
    public async Task Slots_TheSecondAliasOfAnEmote_NamesTheWishRunningAtTheFirst_NotAdoptable()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var first = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-4));
        var second = await SeedSlotAsync(emote, "KappaToo", Now.AddDays(-1));
        var running = await SeedHolderAsync(channel, emote, first, WishStatus.Active);

        var entries = (await Sut().ListAdoptableSlotsAsync(channel.Name, emote.SevenTvEmoteId))!.ToDictionary(e => e.SlotId);

        Assert.Equal(2, entries.Count);
        Assert.Equal(((long?)null, running, false), (entries[second].HeldByWishId, entries[second].RunningWishId, entries[second].RunningWishAdoptable));
        var plain = await AdoptAsync(channel, second);
        Assert.Equal((WishOutcome.AlreadyRunning, running), (plain.Outcome, plain.ConflictingWishId));
        Assert.Equal(WishOutcome.StateConflict, (await AdoptAsync(channel, second, wishId: running)).Outcome);
    }

    // runningWishAdoptable is the WishTransitions row adopt asks; checked here against adopt with that wish's id.
    [Theory]
    [InlineData(WishStatus.Approved, null, true)]
    [InlineData(WishStatus.Presented, WishStatusReason.Unconfirmed, true)]
    [InlineData(WishStatus.Presented, WishStatusReason.AlreadyInSet, true)]
    [InlineData(WishStatus.Presented, WishStatusReason.Expired, false)]
    [InlineData(WishStatus.Unconfirmed, null, false)]
    public async Task Slots_AFreeEntryOfARunningWishesEmote_SaysWhetherThatWishAcceptsIt(string status, string? reason, bool adoptable)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-1));
        var wishId = await SeedWishAsync(channel, emote.SevenTvEmoteId, status);
        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Wishes.SingleAsync(w => w.Id == wishId);
            row.StatusReason = reason;
            await db.SaveChangesAsync();
        }

        var entry = Assert.Single((await Sut().ListAdoptableSlotsAsync(channel.Name, emote.SevenTvEmoteId))!);

        Assert.Equal(((long?)null, wishId, adoptable), (entry.HeldByWishId, entry.RunningWishId, entry.RunningWishAdoptable));
        var result = await AdoptAsync(channel, slot, wishId: wishId);
        Assert.Equal(adoptable ? WishOutcome.Success : WishOutcome.StateConflict, result.Outcome);
    }

    [Fact]
    public async Task Slots_FilterByEmote_AnEmptyChannelIsAnEmptyList_AnUnknownChannelIsNull()
    {
        var channel = await SeedChannelAsync();
        var wanted = await SeedEmoteAsync(channel);
        var wantedSlot = await SeedSlotAsync(wanted, "Wanted", Now.AddDays(-1));
        await SeedSlotAsync(await SeedEmoteAsync(channel), "Other", Now.AddDays(-1));
        var empty = await SeedChannelAsync();

        var filtered = (await Sut().ListAdoptableSlotsAsync(channel.Name.ToUpperInvariant(), wanted.SevenTvEmoteId))!;

        Assert.Equal([wantedSlot], filtered.Select(e => e.SlotId));
        Assert.Equal(2, (await Sut().ListAdoptableSlotsAsync(channel.Name, "  "))!.Count);
        Assert.Empty((await Sut().ListAdoptableSlotsAsync(empty.Name, null))!);
        Assert.Null(await Sut().ListAdoptableSlotsAsync("nosuchchannel" + Guid.NewGuid().ToString("N")[..8], null));
    }

    // ------------------------------------------------------------------------------------ helpers

    private async Task<string> SeedEmoteRowAsync(Seeded channel, string sevenTvEmoteId, string imageUrl, bool placeholder = false)
    {
        await using var db = fixture.CreateDbContext();
        db.Emotes.Add(new Emote
        {
            ChannelId = channel.Id,
            Name = "Emote",
            SevenTvEmoteId = sevenTvEmoteId,
            ImageUrl = imageUrl,
            IsPlaceholder = placeholder,
            IsArchived = placeholder
        });
        await db.SaveChangesAsync();
        return sevenTvEmoteId;
    }

    private async Task<long> SeedHolderAsync(Seeded channel, Emote emote, string slotId, string status)
    {
        await using var db = fixture.CreateDbContext();
        var wish = NewWish(channel.Id, emote.SevenTvEmoteId, status, slotId);
        wish.StartsAtUtc = Now.AddDays(-4);
        wish.StartsAtAuthoritative = true;
        wish.ExpiresAtUtc = Now.AddDays(26);
        wish.EndedAtUtc = WishStatus.IsEnded(status) ? Now : null;
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }
}
