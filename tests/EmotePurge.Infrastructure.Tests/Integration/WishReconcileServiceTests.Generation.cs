using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Plan #485 2.1a, R4: the manager UI reads "generation confirmed" as startsAtAuthoritative. That holds only if,
// on every way a wish is bound to a slot generation, the DTO's (slotId, slotAddedAtUtc, startsAtAuthoritative)
// equals the slot's (Id, AddedAt, AddedAtFromRest) once the reconcile (or adopt) has run. The four ways the plan
// names: link, return to a revived slot row with a new generation, date correction, adoption.
public partial class WishReconcileServiceTests
{
    [Fact]
    public async Task Generation_Link_ThenDateCorrection_ThenTheSameDateConfirmed_TheFlagFollowsTheSlotEachTime()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-2));
        var provisional = Now.AddHours(-1);
        var slot = await SeedSlotAsync(emote, "Alias", provisional, fromRest: false);

        // Link to a provisional date.
        await ReconcileAsync(channel);
        await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: false);

        // Date correction: REST confirms a date not after the provisional one.
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (provisional.AddMinutes(-10), true));
        _clock.Advance(TimeSpan.FromMinutes(30));
        await ReconcileAsync(channel);
        await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: true);

        // Link to a date REST already confirmed, on a second wish.
        var other = await SeedEmoteAsync(channel);
        var secondWish = await SeedWishAsync(channel, other, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-2));
        var restSlot = await SeedSlotAsync(other, "Alias", Now.AddHours(-1));
        await ReconcileAsync(channel);
        await AssertTheDtoFollowsTheSlotAsync(channel, secondWish, restSlot, expectAuthoritative: true);
    }

    [Fact]
    public async Task Generation_ProvisionalLink_ThenRestConfirmsTheSameDate_HealsTheFlag()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-2));
        var date = Now.AddHours(-1);
        var slot = await SeedSlotAsync(emote, "Alias", date, fromRest: false);
        await ReconcileAsync(channel);
        await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: false);

        await UpdateSlotAsync(slot, s => s.AddedAtFromRest = true);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await ReconcileAsync(channel);

        await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: true);
    }

    // The return the plan names (T12 finding 6): the same slot row, removed and restored with a new date. The
    // DTO carries the new generation, unconfirmed while the date is provisional and confirmed after REST.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generation_ReturnToTheRevivedSameSlotRow_CarriesTheNewGeneration_AndItsFlag(bool revivedByRest)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var firstStay = Now.AddDays(-3);
        var slot = await SeedSlotAsync(emote, "Kappa", firstStay);
        var wishId = await SeedLinkedAsync(channel, emote, slot, firstStay, alias: "Kappa");
        await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: true);

        await UpdateSlotAsync(slot, s => (s.IsArchived, s.ArchivedAt) = (true, Now));
        _clock.Advance(TimeSpan.FromMinutes(10));
        await ReconcileAsync(channel);
        var removed = (await WishSut().GetAsync(channel.Name, wishId))!;
        Assert.Equal((WishStatus.RemovedExternally, slot, firstStay), (removed.Status, removed.SlotId, removed.SlotAddedAtUtc));

        // Restored on the same row: a provisional date (a restore) or 7TV's own date (a REST resumption).
        var secondStay = Now.AddMinutes(20);
        await UpdateSlotAsync(slot, s => (s.IsArchived, s.ArchivedAt, s.AddedAt, s.AddedAtFromRest) = (false, null, secondStay, revivedByRest));
        _clock.Advance(TimeSpan.FromMinutes(30));
        await ReconcileAsync(channel);

        var returned = (await WishSut().GetAsync(channel.Name, wishId))!;
        Assert.Equal(WishStatus.Active, returned.Status);
        Assert.NotEqual(firstStay, returned.SlotAddedAtUtc);
        await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: revivedByRest);

        if (!revivedByRest)
        {
            await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (secondStay.AddSeconds(-5), true));
            _clock.Advance(TimeSpan.FromMinutes(5));
            await ReconcileAsync(channel);
            await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: true);
        }
    }

    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("provisional", false)]
    [InlineData("provisional", true)]
    [InlineData("undated", false)]
    public async Task Generation_Adoption_TheFlagFollowsTheSlot_WithOrWithoutAnExplicitStart(string slotKind, bool explicitStart)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var addedAt = slotKind == "undated" ? (DateTime?)null : Now.AddDays(-2);
        var slot = await SeedSlotAsync(emote, "Kappa", addedAt, fromRest: slotKind == "rest");

        var result = await WishSut().AdoptAsync(
            channel.Name, await WishAdoptRequests.ForCurrentAsync(fixture, slot, null, null, explicitStart ? Now.AddDays(-1) : null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal((addedAt, slotKind == "rest"), (result.Wish!.SlotAddedAtUtc, result.Wish.StartsAtAuthoritative));
        await AssertTheDtoFollowsTheSlotAsync(channel, result.Wish.Id, slot, expectAuthoritative: slotKind == "rest");
    }

    // An adopted undated entry has no generation yet; the first REST date (not after the adoption) binds it.
    [Fact]
    public async Task Generation_AdoptedUndatedEntry_GetsItsGenerationAndFlagFromTheFirstRestDate()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", null);
        var wishId = (await WishSut().AdoptAsync(channel.Name, await WishAdoptRequests.ForCurrentAsync(fixture, slot, null, null, null), Actor)).Wish!.Id;
        await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: false);

        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (Now.AddDays(-1), true));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.Active, (await WishAsync(wishId)).Status);
        await AssertTheDtoFollowsTheSlotAsync(channel, wishId, slot, expectAuthoritative: true);
    }

    // The DTO as the client reads it, against the slot row as it stands.
    private async Task AssertTheDtoFollowsTheSlotAsync(Seeded channel, long wishId, string slotId, bool expectAuthoritative)
    {
        var dto = (await WishSut().GetAsync(channel.Name, wishId))!;
        await using var db = fixture.CreateDbContext();
        var slot = await db.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == slotId);

        Assert.Equal(expectAuthoritative, slot.AddedAtFromRest);
        Assert.Equal((slotId, slot.AddedAt, slot.AddedAtFromRest), (dto.SlotId, dto.SlotAddedAtUtc, dto.StartsAtAuthoritative));
    }
}
