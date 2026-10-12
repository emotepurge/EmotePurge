using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Services;

/// <summary>Identifies one hourly rollup row: the source and the UTC hour it summarises.</summary>
public readonly record struct SystemLoadHourKey(SystemLoadSource Source, DateTime HourUtc);

/// <summary>
/// Pure decisions of the hourly rollup that need no database: which window hours must be recomputed, and
/// which stored CPU figures survive a recompute that found no valid pairs.
/// </summary>
/// <remarks>
/// Raw samples are only ever inserted, never updated, and retention keeps them for 7 days. So for one
/// (source, hour) an unchanged raw sample count means an unchanged sample set, and recomputing it under a
/// different <c>SystemLoad:IntervalSeconds</c> could only lose information.
/// </remarks>
public static class SystemLoadRollupSelection
{
    /// <summary>
    /// Picks the window hours to recompute. An hour is dirty when it has raw samples and either no stored
    /// row or a raw sample count that differs from the stored <see cref="SystemLoadHourly.SampleCount"/>.
    /// Per source, every dirty hour H is recomputed together with the next window hour after H that has raw
    /// samples: only that hour's first pair can have its predecessor in H, and a late sample cannot change
    /// any later hour, whose samples and last preceding sample are unchanged. An hour without raw samples is
    /// never selected, even when a row is stored for it (that happens only after a manual deletion, and a
    /// recompute would write an empty row).
    /// </summary>
    /// <param name="windowHours">The closable window hours in ascending order.</param>
    /// <param name="rawCounts">Raw sample count per (source, hour); a missing key means 0.</param>
    /// <param name="storedCounts">Stored <see cref="SystemLoadHourly.SampleCount"/> per existing row.</param>
    public static IReadOnlyList<SystemLoadHourKey> SelectDirtyHours(
        IReadOnlyList<DateTime> windowHours,
        IReadOnlyDictionary<SystemLoadHourKey, long> rawCounts,
        IReadOnlyDictionary<SystemLoadHourKey, int> storedCounts)
    {
        var selected = new List<SystemLoadHourKey>();
        foreach (var source in Enum.GetValues<SystemLoadSource>())
        {
            var predecessorDirty = false;
            foreach (var hour in windowHours.Order())
            {
                var key = new SystemLoadHourKey(source, hour);
                if (rawCounts.GetValueOrDefault(key) == 0)
                {
                    continue;
                }

                var dirty = !storedCounts.TryGetValue(key, out var stored) || rawCounts[key] != stored;
                if (dirty || predecessorDirty)
                {
                    selected.Add(key);
                }

                predecessorDirty = dirty;
            }
        }

        return selected;
    }

    /// <summary>
    /// Copies the stored CPU figure group into <paramref name="recomputed"/> for each of the three groups
    /// (process, cgroup, host) whose recomputed mean is null while the stored mean is not. Returns how many
    /// groups were kept. Every other column of <paramref name="recomputed"/> is left untouched.
    /// </summary>
    public static int KeepStoredCpuGroups(SystemLoadHourly stored, SystemLoadHourly recomputed)
    {
        var kept = 0;
        if (recomputed.ProcessCpuRateMean is null && stored.ProcessCpuRateMean is not null)
        {
            recomputed.ProcessCpuRateMean = stored.ProcessCpuRateMean;
            recomputed.ProcessCpuRateMax = stored.ProcessCpuRateMax;
            recomputed.ProcessCpuRateP95 = stored.ProcessCpuRateP95;
            kept++;
        }

        if (recomputed.CgroupCpuRateMean is null && stored.CgroupCpuRateMean is not null)
        {
            recomputed.CgroupCpuRateMean = stored.CgroupCpuRateMean;
            recomputed.CgroupCpuRateMax = stored.CgroupCpuRateMax;
            recomputed.CgroupCpuRateP95 = stored.CgroupCpuRateP95;
            kept++;
        }

        if (recomputed.HostCpuBusyMean is null && stored.HostCpuBusyMean is not null)
        {
            recomputed.HostCpuBusyMean = stored.HostCpuBusyMean;
            recomputed.HostCpuBusyMax = stored.HostCpuBusyMax;
            recomputed.HostCpuBusyP95 = stored.HostCpuBusyP95;
            kept++;
        }

        return kept;
    }
}
