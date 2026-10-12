using System.Globalization;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The D2 order of the base slot rule (plan #373 E5): AddedAt ascending with null last, then Name
// ordinal, then Id ordinal — in memory, so the database collation never decides it.
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

    private static string[] Sorted(params BaseSlotCandidate[] candidates) =>
        [.. candidates.Order(BaseSlotRule.D2Order).Select(c => c.SlotId)];
}
