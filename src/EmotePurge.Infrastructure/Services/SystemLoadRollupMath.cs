using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Services;

/// <summary>Builds one hourly value from raw observations using the #380 pair rules.</summary>
/// <remarks>
/// The pair, rate and P95 helpers are internal so the admin dashboard's raw read path
/// (<see cref="AdminDashboardLoadBucketing"/>) applies exactly the same gap rule and nearest-rank P95
/// instead of a second implementation (#381).
/// </remarks>
public static class SystemLoadRollupMath
{
    internal const double ProcessorTicksPerSecond = TimeSpan.TicksPerSecond;
    internal const double MicrosecondsPerSecond = 1_000_000d;

    public static SystemLoadHourly Calculate(
        IReadOnlyCollection<SystemLoadSample> samples,
        SystemLoadSource source,
        DateTime hourUtc,
        int samplingIntervalSeconds)
    {
        var startUtc = FloorToHour(hourUtc);
        var endUtc = startUtc.AddHours(1);
        var orderedSamples = samples
            .Where(sample => sample.Source == source)
            .OrderBy(sample => sample.SampledAtUtc)
            .ToArray();
        var hourSamples = orderedSamples
            .Where(sample => sample.SampledAtUtc >= startUtc && sample.SampledAtUtc < endUtc)
            .ToArray();
        var pairs = GetPairs(orderedSamples, source, startUtc, endUtc, samplingIntervalSeconds);
        var processRates = GetRates(
            pairs,
            static pair => Delta(pair.Current.ProcessCpuTimeTicks, pair.Previous.ProcessCpuTimeTicks),
            ProcessorTicksPerSecond);
        var cgroupRates = GetRates(
            pairs,
            static pair => Delta(pair.Current.CgroupCpuUsageMicroseconds, pair.Previous.CgroupCpuUsageMicroseconds),
            MicrosecondsPerSecond);
        var hostRates = GetHostRates(pairs);
        var last = hourSamples.LastOrDefault();

        return new SystemLoadHourly
        {
            Source = source,
            HourUtc = startUtc,
            SampleCount = hourSamples.Length,
            ProcessCpuRateMean = WeightedRateMean(processRates),
            ProcessCpuRateMax = Max(processRates.Select(static value => value.Rate)),
            ProcessCpuRateP95 = Percentile95(processRates.Select(static value => value.Rate)),
            CgroupCpuRateMean = WeightedRateMean(cgroupRates),
            CgroupCpuRateMax = Max(cgroupRates.Select(static value => value.Rate)),
            CgroupCpuRateP95 = Percentile95(cgroupRates.Select(static value => value.Rate)),
            HostCpuBusyMean = hostRates.Count == 0
                ? null
                : (double)hostRates.Sum(static value => value.BusyDelta) / hostRates.Sum(static value => value.TotalDelta),
            HostCpuBusyMax = Max(hostRates.Select(static value => value.Rate)),
            HostCpuBusyP95 = Percentile95(hostRates.Select(static value => value.Rate)),
            CgroupMemoryCurrentMeanBytes = Mean(hourSamples.Select(static sample => sample.CgroupMemoryCurrentBytes)),
            CgroupMemoryCurrentMaxBytes = Max(hourSamples.Select(static sample => sample.CgroupMemoryCurrentBytes)),
            CgroupMemoryCurrentP95Bytes = Percentile95(hourSamples.Select(static sample => sample.CgroupMemoryCurrentBytes)),
            HostMemoryUsedMeanBytes = Mean(GetHostMemoryUsed(hourSamples)),
            HostMemoryUsedMaxBytes = Max(GetHostMemoryUsed(hourSamples)),
            HostMemoryUsedP95Bytes = Percentile95(GetHostMemoryUsed(hourSamples)),
            HostLoadAverage1Mean = Mean(hourSamples.Select(static sample => sample.HostLoadAverage1)),
            HostLoadAverage1Max = Max(hourSamples.Select(static sample => sample.HostLoadAverage1)),
            HostLoadAverage1P95 = Percentile95(hourSamples.Select(static sample => sample.HostLoadAverage1)),
            LastCpuQuotaMicroseconds = last?.CgroupCpuQuotaMicroseconds,
            LastCpuPeriodMicroseconds = last?.CgroupCpuPeriodMicroseconds,
            LastCgroupMemoryMaxBytes = last?.CgroupMemoryMaxBytes,
            LastHostMemoryTotalBytes = last?.HostMemoryTotalBytes,
            ChatLines = SumNullable(hourSamples.Select(static sample => sample.ChatLines)),
            EmoteMatches = SumNullable(hourSamples.Select(static sample => sample.EmoteMatches)),
            ActiveChannelsMean = Mean(hourSamples.Select(static sample => sample.ActiveChannels)),
            ActiveChannelsMax = Max(hourSamples.Select(static sample => sample.ActiveChannels)),
            LiveChannelsMean = Mean(hourSamples.Select(static sample => sample.LiveChannels)),
            LiveChannelsMax = Max(hourSamples.Select(static sample => sample.LiveChannels)),
        };
    }

    /// <summary>The #380 gap rule: a pair counts only with a positive gap of at most 2.5 configured intervals.</summary>
    internal static bool IsPairWithinGap(TimeSpan elapsed, int samplingIntervalSeconds) =>
        elapsed > TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(samplingIntervalSeconds * 2.5d);

    /// <summary>Difference of a cumulative counter; null when either end is unknown or the counter fell.</summary>
    internal static long? Delta(long? current, long? previous) =>
        current is not null && previous is not null && current.Value >= previous.Value
            ? current.Value - previous.Value
            : null;

    /// <summary>Counter units per wall second (CPU cores for ticks or microseconds).</summary>
    internal static double? RateFromDelta(long? delta, double unitsPerSecond, TimeSpan elapsed) =>
        delta is >= 0 ? delta.Value / unitsPerSecond / elapsed.TotalSeconds : null;

    /// <summary>Host busy/total jiffy deltas of one pair; false when the pair is not usable.</summary>
    internal static bool TryGetHostCpuDelta(
        long? currentBusy,
        long? previousBusy,
        long? currentTotal,
        long? previousTotal,
        out long busyDelta,
        out long totalDelta)
    {
        var total = Delta(currentTotal, previousTotal);
        var busy = Delta(currentBusy, previousBusy);
        if (total is > 0 && busy is >= 0 && busy <= total)
        {
            busyDelta = busy.Value;
            totalDelta = total.Value;
            return true;
        }

        busyDelta = 0;
        totalDelta = 0;
        return false;
    }

    /// <summary>Host memory in use (MemTotal - MemAvailable); null when unknown or inconsistent.</summary>
    internal static long? HostMemoryUsed(long? totalBytes, long? availableBytes) =>
        totalBytes is >= 0 && availableBytes is >= 0 && totalBytes >= availableBytes
            ? totalBytes - availableBytes
            : null;

    /// <summary>Zero-based nearest-rank index of the 95th percentile in an ascending list of <paramref name="count"/> values.</summary>
    internal static int Percentile95Index(int count) => (int)Math.Ceiling(count * 0.95d) - 1;

    private static List<SamplePair> GetPairs(
        IReadOnlyList<SystemLoadSample> samples,
        SystemLoadSource source,
        DateTime hourStartUtc,
        DateTime hourEndUtc,
        int samplingIntervalSeconds)
    {
        var pairs = new List<SamplePair>();
        foreach (var processSamples in samples.GroupBy(static sample => sample.ProcessInstanceId))
        {
            var orderedProcessSamples = processSamples.OrderBy(static sample => sample.SampledAtUtc).ToArray();
            for (var index = 1; index < orderedProcessSamples.Length; index++)
            {
                var previous = orderedProcessSamples[index - 1];
                var current = orderedProcessSamples[index];
                if (current.Source != source
                    || current.SampledAtUtc < hourStartUtc
                    || current.SampledAtUtc >= hourEndUtc
                    || previous.Source != source)
                {
                    continue;
                }

                var elapsed = current.SampledAtUtc - previous.SampledAtUtc;
                if (!IsPairWithinGap(elapsed, samplingIntervalSeconds))
                {
                    continue;
                }

                pairs.Add(new SamplePair(previous, current, elapsed));
            }
        }

        return pairs;
    }

    private static List<WeightedRate> GetRates(
        IEnumerable<SamplePair> pairs,
        Func<SamplePair, long?> getDelta,
        double unitsPerSecond)
    {
        var rates = new List<WeightedRate>();
        foreach (var pair in pairs)
        {
            var rate = RateFromDelta(getDelta(pair), unitsPerSecond, pair.Elapsed);
            if (rate.HasValue)
            {
                rates.Add(new WeightedRate(rate.Value, pair.Elapsed.TotalSeconds));
            }
        }

        return rates;
    }

    private static List<HostRate> GetHostRates(IEnumerable<SamplePair> pairs)
    {
        var rates = new List<HostRate>();
        foreach (var pair in pairs)
        {
            if (TryGetHostCpuDelta(
                pair.Current.HostCpuBusyJiffies,
                pair.Previous.HostCpuBusyJiffies,
                pair.Current.HostCpuTotalJiffies,
                pair.Previous.HostCpuTotalJiffies,
                out var busyDelta,
                out var totalDelta))
            {
                rates.Add(new HostRate(busyDelta, totalDelta));
            }
        }

        return rates;
    }

    private static double? WeightedRateMean(IReadOnlyCollection<WeightedRate> rates)
    {
        if (rates.Count == 0)
        {
            return null;
        }

        var elapsed = rates.Sum(static value => value.ElapsedSeconds);
        return elapsed == 0 ? null : rates.Sum(static value => value.Rate * value.ElapsedSeconds) / elapsed;
    }

    private static double? Mean(IEnumerable<double?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }

    private static double? Mean(IEnumerable<long?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => (double)value!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }

    private static double? Mean(IEnumerable<int?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => (double)value!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }

    private static List<long?> GetHostMemoryUsed(IEnumerable<SystemLoadSample> samples) => samples
        .Select(static sample => HostMemoryUsed(sample.HostMemoryTotalBytes, sample.HostMemoryAvailableBytes))
        .ToList();

    private static long? SumNullable(IEnumerable<long?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Sum();
    }

    private static double? Max(IEnumerable<double> values)
    {
        var present = values.ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static double? Max(IEnumerable<double?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static long? Max(IEnumerable<long?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static int? Max(IEnumerable<int?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
    }

    private static double? Percentile95(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted[Percentile95Index(sorted.Length)];
    }

    private static long? Percentile95(IEnumerable<long?> values)
    {
        var sorted = values.Where(static value => value.HasValue).Select(static value => value!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[Percentile95Index(sorted.Length)];
    }

    private static double? Percentile95(IEnumerable<double?> values)
    {
        var sorted = values.Where(static value => value.HasValue).Select(static value => value!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[Percentile95Index(sorted.Length)];
    }

    private static DateTime FloorToHour(DateTime timestampUtc) =>
        new(timestampUtc.Ticks - timestampUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private sealed record SamplePair(SystemLoadSample Previous, SystemLoadSample Current, TimeSpan Elapsed);

    private sealed record WeightedRate(double Rate, double ElapsedSeconds);

    private sealed record HostRate(long BusyDelta, long TotalDelta)
    {
        public double Rate => (double)BusyDelta / TotalDelta;
    }
}
