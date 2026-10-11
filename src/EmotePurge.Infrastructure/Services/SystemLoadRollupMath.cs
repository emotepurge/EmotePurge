using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Services;

/// <summary>Builds one hourly value from raw observations using the #380 pair rules.</summary>
public static class SystemLoadRollupMath
{
    private const double ProcessorTicksPerSecond = TimeSpan.TicksPerSecond;
    private const double MicrosecondsPerSecond = 1_000_000d;

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

    private static List<SamplePair> GetPairs(
        IReadOnlyList<SystemLoadSample> samples,
        SystemLoadSource source,
        DateTime hourStartUtc,
        DateTime hourEndUtc,
        int samplingIntervalSeconds)
    {
        var pairs = new List<SamplePair>();
        var maxGap = TimeSpan.FromSeconds(samplingIntervalSeconds * 2.5d);
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
                if (elapsed <= TimeSpan.Zero || elapsed > maxGap)
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
            var delta = getDelta(pair);
            if (delta is >= 0)
            {
                rates.Add(new WeightedRate(
                    delta.Value / unitsPerSecond / pair.Elapsed.TotalSeconds,
                    pair.Elapsed.TotalSeconds));
            }
        }

        return rates;
    }

    private static List<HostRate> GetHostRates(IEnumerable<SamplePair> pairs)
    {
        var rates = new List<HostRate>();
        foreach (var pair in pairs)
        {
            var totalDelta = Delta(pair.Current.HostCpuTotalJiffies, pair.Previous.HostCpuTotalJiffies);
            var busyDelta = Delta(pair.Current.HostCpuBusyJiffies, pair.Previous.HostCpuBusyJiffies);
            if (totalDelta is > 0 && busyDelta is >= 0 && busyDelta <= totalDelta)
            {
                rates.Add(new HostRate(busyDelta.Value, totalDelta.Value));
            }
        }

        return rates;
    }

    private static long? Delta(long? current, long? previous) =>
        current is not null && previous is not null && current.Value >= previous.Value
            ? current.Value - previous.Value
            : null;

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
        .Select(static sample => sample.HostMemoryTotalBytes is >= 0
            && sample.HostMemoryAvailableBytes is >= 0
            && sample.HostMemoryTotalBytes >= sample.HostMemoryAvailableBytes
                ? sample.HostMemoryTotalBytes - sample.HostMemoryAvailableBytes
                : null)
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
        return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * 0.95d) - 1];
    }

    private static long? Percentile95(IEnumerable<long?> values)
    {
        var sorted = values.Where(static value => value.HasValue).Select(static value => value!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * 0.95d) - 1];
    }

    private static double? Percentile95(IEnumerable<double?> values)
    {
        var sorted = values.Where(static value => value.HasValue).Select(static value => value!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * 0.95d) - 1];
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
