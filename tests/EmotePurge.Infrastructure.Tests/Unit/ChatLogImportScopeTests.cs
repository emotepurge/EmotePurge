using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure comparison of a confirmed backfill coverage with the current one (#384, D15, spec S17).
public class ChatLogImportScopeTests
{
    private const string SetA = "01SETAAAAAAAAAAAAAAAAAAAAA";
    private const string SetB = "01SETBBBBBBBBBBBBBBBBBBBBB";
    private const string Host = "logs.cyex.app";

    private static DateOnly D(int day) => new(2026, 9, day);

    private static ImportScopeInterval Interval(int from, int toExclusive, string set = SetA, string host = Host) =>
        new(D(from), D(toExclusive), set, host);

    private static List<ImportScopeDay> Days(int from, int toExclusive, string set = SetA, string host = Host)
    {
        var days = new List<ImportScopeDay>();
        for (var d = from; d < toExclusive; d++)
        {
            days.Add(new ImportScopeDay(D(d), set, host));
        }

        return days;
    }

    // Two sets with a gap: days 1-9 in set A, 12-19 in set B.
    private static List<ImportScopeDay> TwoSetCoverage() => [.. Days(1, 10), .. Days(12, 20, SetB)];

    private static IReadOnlyList<ImportScopeInterval> TwoSetExpectation() => [Interval(1, 10), Interval(12, 20, SetB)];

    [Fact]
    public void ExactSameCoverage_Matches()
    {
        var result = ChatLogImportScope.Compare(TwoSetExpectation(), TwoSetCoverage());

        Assert.True(result.Matches);
        Assert.Equal(17, result.ExpectedDays);
        Assert.Equal(17, result.CurrentDays);
    }

    [Fact]
    public void BothEmpty_Matches() =>
        Assert.True(ChatLogImportScope.Compare([], []).Matches);

    [Fact]
    public void EmptyExpectationAgainstCoverage_DoesNotMatch() =>
        Assert.False(ChatLogImportScope.Compare([], TwoSetCoverage()).Matches);

    [Fact]
    public void ExpectationAgainstEmptyCoverage_DoesNotMatch() =>
        Assert.False(ChatLogImportScope.Compare(TwoSetExpectation(), []).Matches);

    [Fact]
    public void OneDayMoreInExpectation_DoesNotMatch() =>
        Assert.False(ChatLogImportScope.Compare([Interval(1, 11), Interval(12, 20, SetB)], TwoSetCoverage()).Matches);

    [Fact]
    public void OneDayFewerInExpectation_DoesNotMatch() =>
        Assert.False(ChatLogImportScope.Compare([Interval(1, 9), Interval(12, 20, SetB)], TwoSetCoverage()).Matches);

    [Fact]
    public void OneDayWithAnotherSet_DoesNotMatch()
    {
        var current = TwoSetCoverage();
        current[4] = current[4] with { EmoteSetId = SetB };

        Assert.False(ChatLogImportScope.Compare(TwoSetExpectation(), current).Matches);
    }

    [Fact]
    public void AnotherArchiveHost_DoesNotMatch() =>
        Assert.False(ChatLogImportScope.Compare([Interval(1, 10, host: "logs.other.example"), Interval(12, 20, SetB)], TwoSetCoverage()).Matches);

    [Fact]
    public void SetIdComparisonIsOrdinal() =>
        Assert.False(ChatLogImportScope.Compare([Interval(1, 10, SetA.ToLowerInvariant())], Days(1, 10)).Matches);

    [Fact]
    public void AdjacentIntervalsOfOneSource_MatchTheSameDays() =>
        Assert.True(ChatLogImportScope.Compare([Interval(1, 5), Interval(5, 10), Interval(12, 20, SetB)], TwoSetCoverage()).Matches);

    [Fact]
    public void ExpectationInReverseOrder_Matches() =>
        Assert.True(ChatLogImportScope.Compare([Interval(12, 20, SetB), Interval(1, 10)], TwoSetCoverage()).Matches);

    [Fact]
    public void CurrentDaysInAnyOrder_Match()
    {
        var current = TwoSetCoverage();
        current.Reverse();

        Assert.True(ChatLogImportScope.Compare(TwoSetExpectation(), current).Matches);
    }

    [Fact]
    public void AdjacentIntervalsOfDifferentSets_MatchTheirOwnDays() =>
        Assert.True(ChatLogImportScope.Compare([Interval(1, 5), Interval(5, 10, SetB)], [.. Days(1, 5), .. Days(5, 10, SetB)]).Matches);

    [Fact]
    public void DuplicateIntervalInExpectation_DoesNotMatch() =>
        Assert.False(ChatLogImportScope.Compare([Interval(1, 10), Interval(1, 10)], Days(1, 10)).Matches);

    [Fact]
    public void OverlapAcrossSetBoundary_DoesNotMatch() =>
        Assert.False(ChatLogImportScope.Compare([Interval(1, 6), Interval(5, 10, SetB)], [.. Days(1, 5), .. Days(5, 10, SetB)]).Matches);

    [Fact]
    public void DuplicateCurrentDay_DoesNotMatch() =>
        Assert.False(ChatLogImportScope.Compare([Interval(1, 10)], [.. Days(1, 10), new ImportScopeDay(D(3), SetA, Host)]).Matches);

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    public void EmptyOrInvertedInterval_DoesNotMatch(int from, int toExclusive) =>
        Assert.False(ChatLogImportScope.Compare([Interval(from, toExclusive)], []).Matches);

    [Fact]
    public void HugeInterval_EndsQuicklyAndDoesNotMatch()
    {
        var huge = new ImportScopeInterval(DateOnly.MinValue, DateOnly.MaxValue, SetA, Host);

        var result = ChatLogImportScope.Compare([huge], Days(1, 10));

        Assert.False(result.Matches);
        Assert.Equal(DateOnly.MaxValue.DayNumber - (long)DateOnly.MinValue.DayNumber, result.ExpectedDays);
    }
}
