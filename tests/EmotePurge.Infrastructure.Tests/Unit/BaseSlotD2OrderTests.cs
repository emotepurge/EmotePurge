using System.Globalization;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The D2 order of the base slot rule (plan #373 E5): AddedAt ascending with null last, then Name,
// then Id, both in UTF-8 byte order like COLLATE "C" in the seed SQL — in memory, so the database
// collation never decides it.
public class BaseSlotD2OrderTests
{
    private static readonly DateTime Early = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Late = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ADatedSlot_ComesBeforeAnUndatedOne_AndTheEarlierDateFirst()
    {
        BaseSlotCandidate undated = new("s-undated", "Aaa", null);
        BaseSlotCandidate late = new("s-late", "Aaa", Late);
        BaseSlotCandidate early = new("s-early", "Zzz", Early);

        Assert.Equal(["s-early", "s-late", "s-undated"], Sorted(undated, late, early));
    }

    [Fact]
    public void OnTheSameDate_TheNameDecidesOrdinally_UppercaseBeforeLowercase()
    {
        BaseSlotCandidate lower = new("s-lower", "alpha", null);
        BaseSlotCandidate upper = new("s-upper", "Bravo", null);

        Assert.Equal(["s-upper", "s-lower"], Sorted(lower, upper));
        // The contrast that makes the assertion mean something: a linguistic order says the opposite.
        Assert.True(string.Compare("alpha", "Bravo", CultureInfo.InvariantCulture, CompareOptions.None) < 0);
    }

    [Fact]
    public void OnTheSameDateAndName_TheIdDecidesOrdinally()
    {
        BaseSlotCandidate b = new("b-id", "Same", Early);
        BaseSlotCandidate upperA = new("A-id", "Same", Early);
        BaseSlotCandidate a = new("a-id", "Same", Early);

        Assert.Equal(["A-id", "a-id", "b-id"], Sorted(b, a, upperA));
    }

    [Fact]
    public void Min_PicksTheBaseSlot_RegardlessOfInputOrder()
    {
        BaseSlotCandidate[] candidates =
        [
            new("s-undated-a", "alpha", null),
            new("s-late", "Late", Late),
            new("s-undated-b", "Bravo", null),
            new("s-early", "zulu", Early),
        ];

        Assert.Equal("s-early", candidates.Min(BaseSlotRule.D2Order)!.SlotId);
        Assert.Equal("s-early", candidates.Reverse().Min(BaseSlotRule.D2Order)!.SlotId);
    }

    [Fact]
    public void ANameTie_FollowsUtf8ByteOrder_NotUtf16CodeUnits()
    {
        // U+FF5E (fullwidth tilde, UTF-8 EF BD 9E) against U+1F600 (UTF-8 F0 9F 98 80, UTF-16 the
        // surrogate pair D83D DE00): COLLATE "C" puts the tilde first, UTF-16 ordinal the emoji.
        const string tilde = "\uFF5E";
        const string emoji = "\U0001F600";
        BaseSlotCandidate withEmoji = new("s-emoji", emoji, Early);
        BaseSlotCandidate withTilde = new("s-tilde", tilde, Early);

        Assert.Equal(["s-tilde", "s-emoji"], Sorted(withEmoji, withTilde));
        Assert.True(string.CompareOrdinal(emoji, tilde) < 0);
        Assert.True(BaseSlotRule.Utf8Order.Compare(tilde, emoji) < 0);
        Assert.Equal(
            Math.Sign(CompareBytes(System.Text.Encoding.UTF8.GetBytes(tilde), System.Text.Encoding.UTF8.GetBytes(emoji))),
            Math.Sign(BaseSlotRule.Utf8Order.Compare(tilde, emoji)));
    }

    [Theory]
    [InlineData("abc", "abd")]
    [InlineData("ab", "abc")]
    [InlineData("Zebra", "apple")]
    [InlineData("a\U0001F600", "a\uFFFF")]
    [InlineData("\u00E9", "\uE000")]
    [InlineData("same", "same")]
    public void Utf8Order_AgreesWithAByteComparisonOfTheUtf8Encoding(string x, string y)
    {
        var expected = Math.Sign(CompareBytes(System.Text.Encoding.UTF8.GetBytes(x), System.Text.Encoding.UTF8.GetBytes(y)));

        Assert.Equal(expected, Math.Sign(BaseSlotRule.Utf8Order.Compare(x, y)));
        Assert.Equal(-expected, Math.Sign(BaseSlotRule.Utf8Order.Compare(y, x)));
    }

    private static int CompareBytes(byte[] x, byte[] y) => x.AsSpan().SequenceCompareTo(y);

    private static string[] Sorted(params BaseSlotCandidate[] candidates) =>
        [.. candidates.Order(BaseSlotRule.D2Order).Select(c => c.SlotId)];
}
