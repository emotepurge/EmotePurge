namespace EmotePurge.Core.Services;

/// <summary>Facts the coverage-gap rule needs (E5); the caller reads them, this class decides.</summary>
public sealed record WishCoverageFacts(
    DateTime TrackedSinceUtc,
    DateTime? ChannelDeactivatedAtUtc,
    bool IsBotActive);

/// <summary>The usage window of the protection and the detail view (E4, E5, E16), pure.</summary>
public static class WishUsageWindow
{
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(7);
    public static readonly TimeSpan MinimumLength = TimeSpan.FromHours(24);

    /// <summary>E4: the later of the wish's start and (evaluation time minus 7 days).</summary>
    public static DateTime WindowStart(DateTime startsAtUtc, DateTime evaluatedAtUtc)
    {
        var lookbackStart = evaluatedAtUtc - Lookback;
        return lookbackStart > startsAtUtc ? lookbackStart : startsAtUtc;
    }

    /// <summary>Window length in days, fractions allowed; never negative.</summary>
    public static double LengthInDays(DateTime windowStartUtc, DateTime evaluatedAtUtc)
    {
        var span = evaluatedAtUtc - windowStartUtc;
        return span <= TimeSpan.Zero ? 0d : span.TotalDays;
    }

    /// <summary>Mean per day from a delivered sum over the window's UTC days; 0 for an empty window.</summary>
    public static decimal MeanPerDay(long total, double lengthInDays) =>
        lengthInDays <= 0d ? 0m : total / (decimal)lengthInDays;

    /// <summary>
    /// E4: whether the product withholds the per-day mean. A mean over less than <see cref="MinimumLength"/>
    /// extrapolates a few uses into hundreds per day. Exactly 24 hours shows it.
    /// </summary>
    public static bool IsMeanWithheld(DateTime windowStartUtc, DateTime evaluatedAtUtc) =>
        evaluatedAtUtc - windowStartUtc < MinimumLength;

    /// <summary>E5: whether the window has a counting gap. Exactly 24 hours is valid, less is a gap.</summary>
    public static bool HasCoverageGap(WishCoverageFacts facts, DateTime windowStartUtc, DateTime evaluatedAtUtc) =>
        facts.TrackedSinceUtc > windowStartUtc
        || facts.ChannelDeactivatedAtUtc is not null
        || !facts.IsBotActive
        || evaluatedAtUtc - windowStartUtc < MinimumLength;

    /// <summary>E16: rank of a value among the delivered sums; ties share the better rank.</summary>
    public static int Rank(long value, IReadOnlyCollection<long> allTotals) => 1 + allTotals.Count(t => t > value);
}
