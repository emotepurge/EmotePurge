namespace EmotePurge.Infrastructure.Services;

/// <summary>One confirmed coverage interval <c>[From, ToExclusive)</c> of a single set and archive host (#384, D15).</summary>
internal readonly record struct ImportScopeInterval(DateOnly From, DateOnly ToExclusive, string EmoteSetId, string ArchiveHost);

/// <summary>One day of the channel's current backfill coverage (#384, D15).</summary>
internal readonly record struct ImportScopeDay(DateOnly Day, string EmoteSetId, string ArchiveHost);

/// <summary>Outcome of <see cref="ChatLogImportScope.Compare"/>; the day counts are for logging only.</summary>
internal readonly record struct ImportScopeComparison(bool Matches, long ExpectedDays, long CurrentDays);

/// <summary>
/// Pure comparison of the coverage a manager confirmed with the channel's current coverage (#384, D15).
/// Equality is by day: the same set of <c>(day, set, host)</c> triples, regardless of how the days are
/// split into intervals. Intervals are never expanded into days, so a huge span costs nothing.
/// </summary>
internal static class ChatLogImportScope
{
    public static ImportScopeComparison Compare(
        IReadOnlyList<ImportScopeInterval> expected,
        IReadOnlyList<ImportScopeDay> current)
    {
        var expectedDays = expected.Sum(e => e.ToExclusive.DayNumber - (long)e.From.DayNumber);
        var currentDays = current.Count;
        var expectedRuns = NormalizeExpected(expected);
        if (expectedRuns is null)
        {
            return new ImportScopeComparison(false, expectedDays, currentDays);
        }

        var currentRuns = BuildRuns(current);
        var matches = expectedRuns.Count == currentRuns.Count;
        for (var i = 0; matches && i < expectedRuns.Count; i++)
        {
            matches = expectedRuns[i] == currentRuns[i];
        }

        return new ImportScopeComparison(matches, expectedDays, currentDays);
    }

    // Sorted and merged where adjacent with the same set and host; null on an empty or overlapping
    // interval (the same day claimed twice, whatever its set or host, counts as a deviation).
    private static List<ImportScopeInterval>? NormalizeExpected(IReadOnlyList<ImportScopeInterval> expected)
    {
        var sorted = expected.OrderBy(e => e.From).ThenBy(e => e.ToExclusive).ToList();
        var result = new List<ImportScopeInterval>(sorted.Count);
        foreach (var interval in sorted)
        {
            if (interval.From >= interval.ToExclusive)
            {
                return null;
            }

            if (result.Count > 0)
            {
                var last = result[^1];
                if (interval.From < last.ToExclusive)
                {
                    return null;
                }

                if (interval.From == last.ToExclusive && SameSource(last.EmoteSetId, last.ArchiveHost, interval.EmoteSetId, interval.ArchiveHost))
                {
                    result[^1] = last with { ToExclusive = interval.ToExclusive };
                    continue;
                }
            }

            result.Add(interval);
        }

        return result;
    }

    private static List<ImportScopeInterval> BuildRuns(IReadOnlyList<ImportScopeDay> current)
    {
        var runs = new List<ImportScopeInterval>();
        foreach (var day in current
                     .OrderBy(d => d.Day)
                     .ThenBy(d => d.EmoteSetId, StringComparer.Ordinal)
                     .ThenBy(d => d.ArchiveHost, StringComparer.Ordinal))
        {
            // A day that follows a run directly extends it; a repeated day starts a run of its own, so a
            // duplicate can never be absorbed into an equal expectation.
            var extended = false;
            for (var i = runs.Count - 1; i >= 0 && !extended; i--)
            {
                var run = runs[i];
                if (run.ToExclusive == day.Day && SameSource(run.EmoteSetId, run.ArchiveHost, day.EmoteSetId, day.ArchiveHost))
                {
                    runs[i] = run with { ToExclusive = day.Day.AddDays(1) };
                    extended = true;
                }
            }

            if (!extended)
            {
                runs.Add(new ImportScopeInterval(day.Day, day.Day.AddDays(1), day.EmoteSetId, day.ArchiveHost));
            }
        }

        return runs.OrderBy(r => r.From).ThenBy(r => r.EmoteSetId, StringComparer.Ordinal).ThenBy(r => r.ArchiveHost, StringComparer.Ordinal).ToList();
    }

    private static bool SameSource(string setA, string hostA, string setB, string hostB) =>
        string.Equals(setA, setB, StringComparison.Ordinal) && string.Equals(hostA, hostB, StringComparison.Ordinal);
}
