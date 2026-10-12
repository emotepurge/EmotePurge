using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Pure helpers of the hourly rollup that keep memory and per-key work bounded: splitting the candidate
/// hours into transactions, and slicing one hour plus predecessors out of loaded samples without
/// regrouping per key.
/// </summary>
public static class SystemLoadRollupBatching
{
    /// <summary>
    /// Splits candidate keys into batches. Keys older than <paramref name="recomputeFromUtc"/> (backfill) go
    /// in ascending (hour, source) chunks of <paramref name="chunkSize"/>; every key at or after it (the
    /// dirty window) goes together in one final batch, so a dirty hour and its paired next hour are never
    /// split across transactions. Empty input yields no batch.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<SystemLoadHourKey>> Split(
        IEnumerable<SystemLoadHourKey> keys,
        DateTime recomputeFromUtc,
        int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        var ordered = keys.OrderBy(static key => key.HourUtc).ThenBy(static key => key.Source).ToList();
        var backfill = ordered.Where(key => key.HourUtc < recomputeFromUtc).ToList();
        var window = ordered.Where(key => key.HourUtc >= recomputeFromUtc).ToList();
        var batches = new List<IReadOnlyList<SystemLoadHourKey>>();
        for (var start = 0; start < backfill.Count; start += chunkSize)
        {
            batches.Add(backfill.GetRange(start, Math.Min(chunkSize, backfill.Count - start)));
        }

        if (window.Count > 0)
        {
            batches.Add(window);
        }

        return batches;
    }
}

/// <summary>
/// The samples of one source grouped by process instance and sorted by time once, so each (source, hour)
/// slice is two binary searches per instance instead of a regroup and re-sort of all samples.
/// </summary>
public sealed class SystemLoadSampleIndex
{
    private readonly SystemLoadSample[][] _groups;
    private readonly DateTime[][] _times;

    public SystemLoadSampleIndex(IEnumerable<SystemLoadSample> sourceSamples)
    {
        _groups = sourceSamples
            .GroupBy(static sample => sample.ProcessInstanceId)
            .Select(static group => group.OrderBy(static sample => sample.SampledAtUtc).ToArray())
            .ToArray();
        _times = _groups
            .Select(static group => group.Select(static sample => sample.SampledAtUtc).ToArray())
            .ToArray();
    }

    /// <summary>
    /// Returns, per process instance with samples in the hour, those samples plus the one sample just before
    /// the hour, ordered by time. Instances without samples in the hour contribute nothing.
    /// </summary>
    public IReadOnlyList<SystemLoadSample> Slice(DateTime hourUtc)
    {
        var hourEndUtc = hourUtc.AddHours(1);
        var result = new List<SystemLoadSample>();
        for (var group = 0; group < _groups.Length; group++)
        {
            var times = _times[group];
            var first = LowerBound(times, hourUtc);
            if (first >= times.Length || times[first] >= hourEndUtc)
            {
                continue;
            }

            var endExclusive = LowerBound(times, hourEndUtc);
            result.AddRange(_groups[group][Math.Max(0, first - 1)..endExclusive]);
        }

        return result.OrderBy(static sample => sample.SampledAtUtc).ToArray();
    }

    private static int LowerBound(DateTime[] sorted, DateTime value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (sorted[mid] < value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
