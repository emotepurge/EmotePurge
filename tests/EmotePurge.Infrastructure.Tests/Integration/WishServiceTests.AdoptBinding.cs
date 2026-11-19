using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 6 (plan #485 T0b, 2.1a, Sol-R2-P2-4): "take over from the set" is bound to the entry the manager
// picked, by alias and generation, checked under the channel lock right after the slot is loaded and before the
// holder and running checks. A mismatch is 404 wish_slot_not_found and writes nothing.
public partial class WishServiceTests
{
    [Fact]
    public async Task Adopt_WithoutTheExpectedAlias_IsRequestInvalid_AndWritesNothing()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var added = Now.AddDays(-2);
        var slot = await SeedSlotAsync(emote, "Kappa", added);

        var result = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, null, added, null, null, null), Actor);

        Assert.Equal(WishOutcome.RequestInvalid, result.Outcome);
        await AssertNothingWrittenAsync(channel);
    }

    // The case of Sol-R2-P2-4: the slot id the manager saw is reused by a revived row with a new date. The old
    // generation finds nothing; the current one adopts and binds that generation.
    [Fact]
    public async Task Adopt_WithTheOldGenerationOfAReusedSlotId_IsSlotNotFound_TheCurrentOneAdopts()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var seen = Now.AddDays(-20);
        var slot = await SeedSlotAsync(emote, "Kappa", seen);
        var revived = Now.AddHours(-3);
        await UpdateSlotAsync(slot, s => s.AddedAt = revived);

        var stale = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, "Kappa", seen, null, null, null), Actor);

        Assert.Equal(WishOutcome.SlotNotFound, stale.Outcome);
        await AssertNothingWrittenAsync(channel);
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);

        var current = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, "Kappa", revived, null, null, null), Actor);

        Assert.Equal(WishOutcome.Success, current.Outcome);
        Assert.Equal((slot, (DateTime?)revived, (DateTime?)revived), (current.Wish!.SlotId, current.Wish.SlotAddedAtUtc, current.Wish.StartsAtUtc));
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("alias-case")]
    [InlineData("archived")]
    [InlineData("undated-expected-dated-found")]
    [InlineData("dated-expected-undated-found")]
    [InlineData("millisecond-date")]
    public async Task Adopt_OfAnEntryThatChangedSinceItWasPicked_IsSlotNotFound(string change)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var added = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        var slot = await SeedSlotAsync(emote, "Kappa", change == "dated-expected-undated-found" ? null : added);
        DateTime? expectedDate = change switch
        {
            "undated-expected-dated-found" => null,
            "millisecond-date" => added.AddTicks(-(added.Ticks % TimeSpan.TicksPerMillisecond)),
            _ => added,
        };
        var expectedAlias = change == "alias-case" ? "kappa" : "Kappa";
        switch (change)
        {
            case "renamed":
                await UpdateSlotAsync(slot, s => s.Name = "KappaRenamed");
                break;
            case "archived":
                await UpdateSlotAsync(slot, s => (s.IsArchived, s.ArchivedAt) = (true, Now));
                break;
        }

        var result = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, expectedAlias, expectedDate, null, null, null), Actor);

        Assert.Equal(WishOutcome.SlotNotFound, result.Outcome);
        await AssertNothingWrittenAsync(channel);
    }

    // An undated entry stays adoptable while it is still undated: no generation, a provisional start.
    [Fact]
    public async Task Adopt_UndatedExpected_AndUndatedFound_Adopts_WithoutAGeneration()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", null, fromRest: false);

        var result = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, "Kappa", null, null, null, null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(((DateTime?)null, (DateTime?)Now, false), (result.Wish!.SlotAddedAtUtc, result.Wish.StartsAtUtc, result.Wish.StartsAtAuthoritative));
    }

    // The date compares at database precision: a value that differs only below the microsecond still matches.
    [Fact]
    public async Task Adopt_ComparesTheDateAsTheDatabaseStoresIt()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var added = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        var slot = await SeedSlotAsync(emote, "Kappa", added);

        var result = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, "Kappa", added.AddTicks(9), null, null, null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(added, result.Wish!.SlotAddedAtUtc);
    }

    // The generation check comes before the holder, the running and the state checks: a stale pick is "not found"
    // whatever else would have refused it.
    [Theory]
    [InlineData("held")]
    [InlineData("running")]
    [InlineData("closed-wish")]
    public async Task Adopt_WithAStaleGeneration_IsSlotNotFound_BeforeTheOtherRefusals(string other)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var added = Now.AddDays(-4);
        var slot = await SeedSlotAsync(emote, "Kappa", added);
        long? wishId = null;
        switch (other)
        {
            case "held":
                await SeedRowAsync(channel, WishStatus.Kept, w =>
                {
                    w.SevenTvEmoteId = emote.SevenTvEmoteId;
                    w.SlotId = slot;
                });
                break;
            case "running":
                await SeedWishAsync(channel, emote.SevenTvEmoteId, WishStatus.Approved);
                break;
            default:
                wishId = await SeedWishAsync(channel, emote.SevenTvEmoteId, WishStatus.Closed);
                break;
        }

        var stale = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, "Kappa", added.AddSeconds(-1), wishId, null, null), Actor);
        var current = await Sut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, "Kappa", added, wishId, null, null), Actor);

        Assert.Equal(WishOutcome.SlotNotFound, stale.Outcome);
        var expected = other switch
        {
            "held" => WishOutcome.SlotAlreadyLinked,
            "running" => WishOutcome.AlreadyRunning,
            _ => WishOutcome.StateConflict,
        };
        Assert.Equal(expected, current.Outcome);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
    }
}
