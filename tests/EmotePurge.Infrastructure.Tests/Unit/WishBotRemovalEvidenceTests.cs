using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The comparisons of a bot removal (#487 plan 2.7, spec 8, AK77, AK129) with the values of the live probe T0 (P4: a rename
// keeps addedAt and addedById; P5: a human re-add carries the owner's addedById and a new addedAt).
public class WishBotRemovalEvidenceTests
{
    private const string Set = "01SETA00000000000000000000";
    private const string Emote = "01FZ8W19JG000DX0N9GHCRXSK8";
    private const string Bot = "01M7XAHYKT63HSZMS71AW0C5TG";
    private const string Owner = "01KYJM4NCAXW8FASM2KWG38SBT";

    private static readonly DateTime Added = new(2026, 11, 24, 16, 55, 23, 498, DateTimeKind.Utc);
    private static readonly DateTime HumanReAdded = new(2026, 11, 24, 17, 27, 32, 213, DateTimeKind.Utc);
    private static readonly DateTime OtherAdded = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AllFourFeatures_AreOwned()
    {
        Assert.Equal(WishBotRemovalRead.Owned, Classify([new(Emote, "epT0dino", Added, Bot)]));
    }

    // One feature off at a time (AK77): the alias renamed (P4), a human re-add (P5: owner and a new addedAt), only the
    // addedById, only the addedAt, and the emote id (then nothing of the emote is under the alias: gone).
    [Theory]
    [InlineData("alias", WishBotRemovalRead.Changed)]
    [InlineData("readd", WishBotRemovalRead.Changed)]
    [InlineData("addedBy", WishBotRemovalRead.Changed)]
    [InlineData("addedAt", WishBotRemovalRead.Changed)]
    [InlineData("emote", WishBotRemovalRead.Gone)]
    [InlineData("gone", WishBotRemovalRead.Gone)]
    public void OneFeatureOff_IsNotOwned(string off, WishBotRemovalRead expected)
    {
        SevenTvSetEntry[] entries = off switch
        {
            "alias" => [new(Emote, "epT0dinoX", Added, Bot)],
            "readd" => [new(Emote, "epT0dino", HumanReAdded, Owner)],
            "addedBy" => [new(Emote, "epT0dino", Added, Owner)],
            "addedAt" => [new(Emote, "epT0dino", Added.AddMilliseconds(1), Bot)],
            "emote" => [new("01OTHEREMOTE00000000000000", "epT0dino", Added, Bot)],
            _ => [],
        };

        Assert.Equal(expected, Classify(entries));
    }

    // The stored instant is compared to the microsecond (timestamptz), 7TV reports milliseconds.
    [Fact]
    public void TheStoredInstant_MatchesToTheMicrosecond()
    {
        var stored = new DateTime(Added.Ticks + 4, DateTimeKind.Utc);
        Assert.Equal(WishBotRemovalRead.Owned, WishBotRemovalEvidence.Classify([new(Emote, "epT0dino", Added, Bot)], Emote, "epT0dino", stored, Bot));
    }

    // The others are compared by alias and addedAt; a second alias coming in, one leaving or one re-added is a change.
    [Theory]
    [InlineData("same", true)]
    [InlineData("added", false)]
    [InlineData("left", false)]
    [InlineData("readded", false)]
    public void TheOtherEntries_AreComparedByAliasAndAddedAt(string change, bool unchanged)
    {
        var evidence = Evidence();
        SevenTvSetEntry[] entries = change switch
        {
            "same" => [Target(), Other("frenn", OtherAdded)],
            "added" => [Target(), Other("frenn", OtherAdded), Other("dino2", HumanReAdded)],
            "left" => [Target()],
            _ => [Target(), Other("frenn", HumanReAdded)],
        };

        Assert.Equal(unchanged, evidence.OthersUnchanged(entries));
    }

    // After the removal, a recorded other entry counts as missing only when no entry of the emote has its alias any more;
    // a repaired one (new addedAt) is there.
    [Fact]
    public void MissingOthers_CountsByAliasOnly()
    {
        var evidence = Evidence();

        Assert.Equal(["frenn"], evidence.MissingOthers([]).Select(o => o.Alias));
        Assert.Empty(evidence.MissingOthers([Other("frenn", HumanReAdded)]));
        Assert.Equal(WishBotRemovalRead.Gone, evidence.Classify([Other("frenn", OtherAdded)]));
    }

    // The jsonb form keeps the set (K6c-2) and every feature; nothing, garbage and a form without the bot's entry read as none.
    [Fact]
    public void TheJsonForm_RoundTrips_AndAnUnreadableOneIsNone()
    {
        var evidence = Evidence();

        var back = WishBotRemovalEvidence.TryParse(evidence.ToJson())!;

        Assert.Equal((Set, Emote, "epT0dino", Added, Bot), (back.SetId, back.EmoteId, back.Alias, back.AddedAtUtc, back.AddedById));
        Assert.Equal(evidence.Others, back.Others);
        Assert.DoesNotContain(Owner, evidence.ToJson(), StringComparison.Ordinal);
        Assert.Null(WishBotRemovalEvidence.TryParse(null));
        Assert.Null(WishBotRemovalEvidence.TryParse("{"));
        Assert.Null(WishBotRemovalEvidence.TryParse("""{"setId":"x"}"""));
    }

    private static WishBotRemovalEvidence Evidence() =>
        WishBotRemovalEvidence.Capture([Target(), Other("frenn", OtherAdded)], Set, Emote, "epT0dino", Added, Bot);

    private static SevenTvSetEntry Target() => new(Emote, "epT0dino", Added, Bot);

    private static SevenTvSetEntry Other(string alias, DateTime addedAt) => new(Emote, alias, addedAt, Owner);

    private static WishBotRemovalRead Classify(SevenTvSetEntry[] entries) =>
        WishBotRemovalEvidence.Classify(entries, Emote, "epT0dino", Added, Bot);
}
