using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The ownership decision of a bot add (#487 plan 2.6 point 6, 2.5a (d), spec 8, AK77, AK128), one case per edge: the bot's
// own entry with the proof of sending → owned; a foreign or missing addedById, or no proof of sending → confirmed without
// ownership; no entry, an entry without addedAt, one outside the generation, the alias differing in case, or two entries
// under the alias → not confirmed; a second alias of the same emote does not count.
public class WishBotOwnershipTests
{
    private const string Emote = "01EMOTE0000000000000000000";
    private const string Bot = "01BOTACCOUNTA";
    private const string Owner = "01OWNER";

    private static readonly DateTime Written = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReadAt = Written.AddSeconds(30);

    [Fact]
    public void TheBotsEntryWithTheProofOfSending_IsConfirmedWithOwnership_StartingAtAddedAt()
    {
        var result = Confirm([Entry("Alias", Written.AddSeconds(1), Bot)]);

        Assert.Equal(new WishBotConfirmation(true, Written.AddSeconds(1), true), result);
    }

    [Theory]
    [InlineData(Owner, true)]
    [InlineData(null, true)]
    [InlineData(Bot, false)]
    public void AForeignOrMissingAddedById_OrNoProofOfSending_IsConfirmedWithoutOwnership(string? addedById, bool acknowledged)
    {
        var result = Confirm([Entry("Alias", Written.AddSeconds(1), addedById)], acknowledged);

        Assert.Equal(new WishBotConfirmation(true, Written.AddSeconds(1), false), result);
    }

    [Fact]
    public void NoEntry_IsNotConfirmed()
    {
        Assert.Equal(WishBotConfirmation.NotConfirmed, Confirm([new SevenTvSetEntry("01OTHER", "Alias", Written, Bot)]));
    }

    [Fact]
    public void AnEntryWithoutAddedAt_IsNotConfirmedYet()
    {
        Assert.Equal(WishBotConfirmation.NotConfirmed, Confirm([Entry("Alias", null, Bot)]));
    }

    // The generation runs from the write minus 2 minutes to the read plus 2 minutes (clock skew).
    [Theory]
    [InlineData(-121, false)]
    [InlineData(-120, true)]
    [InlineData(150, true)]
    [InlineData(151, false)]
    public void TheGeneration_IsTheWriteMinus2Minutes_ToTheReadPlus2Minutes(int secondsFromWrite, bool confirmed)
    {
        var result = Confirm([Entry("Alias", Written.AddSeconds(secondsFromWrite), Bot)]);

        Assert.Equal(confirmed, result.Confirmed);
    }

    [Fact]
    public void TwoEntriesOfTheEmote_OnlyTheOneWithTheAliasCounts()
    {
        var result = Confirm([Entry("Other", Written.AddSeconds(2), Owner), Entry("Alias", Written.AddSeconds(1), Bot)]);

        Assert.Equal(new WishBotConfirmation(true, Written.AddSeconds(1), true), result);
    }

    [Fact]
    public void TheAlias_IsComparedOrdinally_AndTwoEntriesUnderIt_AreNotExactlyOne()
    {
        Assert.False(Confirm([Entry("alias", Written, Bot)]).Confirmed);
        Assert.False(Confirm([Entry("Alias", Written, Bot), Entry("Alias", Written.AddSeconds(1), Bot)]).Confirmed);
    }

    private static WishBotConfirmation Confirm(IReadOnlyCollection<SevenTvSetEntry> entries, bool acknowledged = true) =>
        WishBotOwnership.Confirm(entries, Emote, "Alias", Bot, Written, ReadAt, acknowledged);

    private static SevenTvSetEntry Entry(string alias, DateTime? addedAt, string? addedById) => new(Emote, alias, addedAt, addedById);
}
