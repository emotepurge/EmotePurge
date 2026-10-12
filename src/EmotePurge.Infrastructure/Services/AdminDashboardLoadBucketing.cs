using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Pure bucket math of the admin dashboard load range (#381, spec 5.1/5.2): the fixed bucket ladder
/// with its hard 400-bucket cap, and the two streaming accumulators that turn raw samples or hourly
/// rows into one column-oriented <see cref="AdminDashboardLoad"/>. No database access here; the query
/// service streams projected rows in and reads the finished load out.
/// </summary>
internal static class AdminDashboardLoadBucketing
{
    internal const int MaximumBuckets = 400;

    /// <summary>
    /// toUtc is now; fromUtc is (now - duration) floored to a UTC multiple of the bucket width; the
    /// buckets run from fromUtc up to and including the one that holds now.
    /// </summary>
    public static AdminDashboardLoadWindow GetWindow(AdminDashboardLoadRange range, DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("nowUtc must have DateTimeKind.Utc.", nameof(nowUtc));
        }

        var bucketSeconds = AdminDashboardLoadRanges.GetBucketSeconds(range);
        var start = nowUtc - AdminDashboardLoadRanges.GetDuration(range, nowUtc);
        var bucketTicks = bucketSeconds * TimeSpan.TicksPerSecond;
        var fromUtc = new DateTime(start.Ticks - start.Ticks % bucketTicks, DateTimeKind.Utc);
        return CreateWindow(fromUtc, nowUtc, bucketSeconds);
    }

    /// <summary>
    /// The hard cap: a window of more than <see cref="MaximumBuckets"/> buckets is a programming error
    /// in the ladder, never a user error, and fails loudly instead of silently exceeding the budget.
    /// </summary>
    internal static AdminDashboardLoadWindow CreateWindow(DateTime fromUtc, DateTime toUtc, int bucketSeconds)
    {
        var bucketTicks = bucketSeconds * TimeSpan.TicksPerSecond;
        if (bucketSeconds <= 0 || fromUtc.Ticks % bucketTicks != 0 || toUtc < fromUtc)
        {
            throw new ArgumentException($"Invalid load window {fromUtc:O}..{toUtc:O} at {bucketSeconds} s.");
        }

        var bucketCount = (toUtc.Ticks - fromUtc.Ticks) / bucketTicks + 1;
        if (bucketCount > MaximumBuckets)
        {
            throw new InvalidOperationException(
                $"The load window {fromUtc:O}..{toUtc:O} at {bucketSeconds} s would need {bucketCount} buckets; the budget is {MaximumBuckets}.");
        }

        return new AdminDashboardLoadWindow(
            DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(toUtc, DateTimeKind.Utc),
            bucketSeconds,
            (int)bucketCount);
    }

    internal static double? Round(double? value, int digits) => value is null ? null : Math.Round(value.Value, digits);

    internal static double? Percent(double? share) => share is null ? null : Math.Round(share.Value * 100d, 1);

    internal static string SourceCode(bool isWorker) => isWorker ? "W" : "A";

    internal static string? Provenance(bool api, bool worker) => (api, worker) switch
    {
        (true, true) => "AW",
        (true, false) => "A",
        (false, true) => "W",
        _ => null,
    };
}

/// <summary>The UTC window of one load response and its bucket arithmetic.</summary>
internal readonly record struct AdminDashboardLoadWindow(DateTime FromUtc, DateTime ToUtc, int BucketSeconds, int BucketCount)
{
    public long BucketTicks => BucketSeconds * TimeSpan.TicksPerSecond;

    public DateTime BucketStartUtc(int index) => new(FromUtc.Ticks + index * BucketTicks, DateTimeKind.Utc);

    /// <summary>Bucket index of a timestamp, or -1 when it lies outside [FromUtc, ToUtc].</summary>
    public int IndexOf(DateTime atUtc) =>
        atUtc < FromUtc || atUtc > ToUtc ? -1 : (int)((atUtc.Ticks - FromUtc.Ticks) / BucketTicks);
}

/// <summary>The projected columns of one SystemLoadSamples row that the raw load path reads.</summary>
internal sealed class AdminDashboardLoadSampleRow
{
    public long Id { get; set; }
    public DateTime SampledAtUtc { get; set; }
    public bool IsWorker { get; set; }
    public Guid ProcessInstanceId { get; set; }
    public DateTime? PreviousSampledAtUtc { get; set; }
    public long? ProcessCpuTimeTicks { get; set; }
    public long? CgroupCpuUsageMicroseconds { get; set; }
    public long? CgroupCpuQuotaMicroseconds { get; set; }
    public long? CgroupCpuPeriodMicroseconds { get; set; }
    public long? CgroupMemoryCurrentBytes { get; set; }
    public long? CgroupMemoryMaxBytes { get; set; }
    public long? HostCpuBusyJiffies { get; set; }
    public long? HostCpuTotalJiffies { get; set; }
    public double? HostLoadAverage1 { get; set; }
    public long? HostMemoryTotalBytes { get; set; }
    public long? HostMemoryAvailableBytes { get; set; }
    public long? HostSwapTotalBytes { get; set; }
    public long? HostSwapFreeBytes { get; set; }
    public int? ActiveChannels { get; set; }
    public int? LiveChannels { get; set; }
    public long? ChatLines { get; set; }
    public long? EmoteMatches { get; set; }
}

/// <summary>
/// Raw path. Rows arrive in ascending SampledAtUtc order; rows before the window are the per-instance
/// predecessors and only seed the pairing. Pairs follow <see cref="SystemLoadRollupMath"/> exactly
/// (same source and instance, positive gap of at most 2.5 configured intervals, monotonic counter) and
/// count in the bucket of the later sample. Only the current bucket's values are held: a bucket is
/// finished as soon as a row of a later bucket arrives.
/// </summary>
internal sealed class AdminDashboardRawLoadAccumulator(AdminDashboardLoadWindow window, int samplingIntervalSeconds)
{
    private readonly AdminDashboardLoadSeriesBuilder _series = new(window);
    private readonly Dictionary<(bool IsWorker, Guid ProcessInstanceId), AdminDashboardLoadSampleRow> _previous = [];
    private readonly RawBucket _bucket = new();
    private int _bucketIndex = -1;
    private DateTime _lastSampledAtUtc = DateTime.MinValue;
    private DateTime? _dataThroughUtc;
    private AdminDashboardSwap? _swap;

    public void Add(AdminDashboardLoadSampleRow row)
    {
        if (row.SampledAtUtc < _lastSampledAtUtc)
        {
            throw new InvalidOperationException("Raw load rows must arrive in ascending SampledAtUtc order.");
        }

        _lastSampledAtUtc = row.SampledAtUtc;
        var key = (row.IsWorker, row.ProcessInstanceId);
        _previous.TryGetValue(key, out var previous);
        _previous[key] = row;

        var index = window.IndexOf(row.SampledAtUtc);
        if (index < 0)
        {
            return;
        }

        if (index != _bucketIndex)
        {
            FlushBucket();
            _bucketIndex = index;
        }

        _dataThroughUtc = row.SampledAtUtc;
        if (row.HostSwapTotalBytes is > 0 && row.HostSwapFreeBytes is >= 0 && row.HostSwapFreeBytes <= row.HostSwapTotalBytes)
        {
            _swap = new AdminDashboardSwap(
                row.HostSwapTotalBytes.Value - row.HostSwapFreeBytes.Value,
                row.HostSwapTotalBytes.Value,
                row.SampledAtUtc);
        }

        _bucket.Add(row, previous, samplingIntervalSeconds);
    }

    public AdminDashboardLoad Complete(string range, DateTime generatedAtUtc)
    {
        FlushBucket();
        return _series.Build(range, "raw", "perMinute", generatedAtUtc, _dataThroughUtc, samplingIntervalSeconds, _swap);
    }

    private void FlushBucket()
    {
        if (_bucketIndex >= 0)
        {
            _bucket.WriteTo(_series, _bucketIndex);
            _bucket.Reset();
        }
    }

    private sealed class RawBucket
    {
        private readonly List<SourcedValue> _hostCpu = [];
        private readonly List<SourcedValue> _hostMemory = [];
        private readonly List<SourcedValue> _hostLoad = [];
        private readonly RawSourceBucket _api = new();
        private readonly RawSourceBucket _worker = new();
        private long? _memoryTotalBytes;
        private int? _activeChannelsMax;
        private int? _liveChannelsMax;
        private long _chatLines;
        private double _chatSeconds;
        private long _emoteMatches;
        private double _emoteSeconds;
        private double _workloadSeconds;

        public void Add(AdminDashboardLoadSampleRow row, AdminDashboardLoadSampleRow? previous, int samplingIntervalSeconds)
        {
            var source = row.IsWorker ? _worker : _api;
            source.SampleCount++;

            if (previous is not null)
            {
                var elapsed = row.SampledAtUtc - previous.SampledAtUtc;
                if (SystemLoadRollupMath.IsPairWithinGap(elapsed, samplingIntervalSeconds))
                {
                    AddPair(row, previous, elapsed, source);
                }
            }

            var memoryUsed = SystemLoadRollupMath.HostMemoryUsed(row.HostMemoryTotalBytes, row.HostMemoryAvailableBytes);
            if (memoryUsed.HasValue)
            {
                _hostMemory.Add(new SourcedValue(memoryUsed.Value, row.IsWorker));
                source.MemoryUsedSum += memoryUsed.Value;
                source.MemoryUsedCount++;
            }

            if (row.HostMemoryTotalBytes is > 0)
            {
                _memoryTotalBytes = row.HostMemoryTotalBytes;
            }

            if (row.HostLoadAverage1.HasValue)
            {
                _hostLoad.Add(new SourcedValue(row.HostLoadAverage1.Value, row.IsWorker));
                source.LoadSum += row.HostLoadAverage1.Value;
                source.LoadCount++;
            }

            if (row.CgroupMemoryCurrentBytes.HasValue)
            {
                source.CgroupMemory.Add(row.CgroupMemoryCurrentBytes.Value);
                if (row.CgroupMemoryMaxBytes is > 0)
                {
                    source.MemoryShares.Add(100d * row.CgroupMemoryCurrentBytes.Value / row.CgroupMemoryMaxBytes.Value);
                }
            }

            source.MemoryLimitBytes = row.CgroupMemoryMaxBytes is > 0 ? row.CgroupMemoryMaxBytes : null;
            source.QuotaCores = QuotaCores(row.CgroupCpuQuotaMicroseconds, row.CgroupCpuPeriodMicroseconds);

            if (row.IsWorker)
            {
                AddWorkload(row);
            }
        }

        public void WriteTo(AdminDashboardLoadSeriesBuilder series, int index)
        {
            series.MarkObserved(index);
            var host = series.Host;
            host.Provenance[index] = AdminDashboardLoadBucketing.Provenance(_api.SampleCount > 0, _worker.SampleCount > 0);
            if (Percentile(_hostCpu, out var cpuP95, out var cpuMax, out var cpuSource))
            {
                host.CpuBusyP95[index] = AdminDashboardLoadBucketing.Percent(cpuP95);
                host.CpuBusyMax[index] = AdminDashboardLoadBucketing.Percent(cpuMax);
                host.CpuValueSource[index] = cpuSource;
            }

            if (Percentile(_hostMemory, out var memoryP95, out var memoryMax, out var memorySource))
            {
                host.MemoryUsedP95Bytes[index] = (long)memoryP95;
                host.MemoryUsedMaxBytes[index] = (long)memoryMax;
                host.MemoryValueSource[index] = memorySource;
            }

            if (Percentile(_hostLoad, out var loadP95, out var loadMax, out var loadSource))
            {
                host.LoadAverage1P95[index] = Math.Round(loadP95, 2);
                host.LoadAverage1Max[index] = Math.Round(loadMax, 2);
                host.LoadAverageValueSource[index] = loadSource;
            }

            host.MemoryTotalBytes[index] = _memoryTotalBytes;
            host.ApiSampleCount[index] = _api.SampleCount;
            host.WorkerSampleCount[index] = _worker.SampleCount;
            host.ApiCpuBusyMean[index] = _api.HostCpuBusyMean();
            host.WorkerCpuBusyMean[index] = _worker.HostCpuBusyMean();
            host.ApiMemoryUsedMeanBytes[index] = _api.MemoryUsedMean();
            host.WorkerMemoryUsedMeanBytes[index] = _worker.MemoryUsedMean();
            host.ApiLoadAverage1Mean[index] = _api.LoadMean();
            host.WorkerLoadAverage1Mean[index] = _worker.LoadMean();

            _api.WriteTo(series.Api, index);
            _worker.WriteTo(series.Worker, index);

            var workload = series.Workload;
            workload.ActiveChannelsMax[index] = _activeChannelsMax;
            workload.LiveChannelsMax[index] = _liveChannelsMax;
            workload.ChatLines[index] = _chatSeconds > 0 ? Math.Round(_chatLines / _chatSeconds * 60d, 2) : null;
            workload.EmoteMatches[index] = _emoteSeconds > 0 ? Math.Round(_emoteMatches / _emoteSeconds * 60d, 2) : null;
            workload.WorkloadIntervalSeconds[index] = _workloadSeconds > 0 ? Math.Round(_workloadSeconds, 1) : null;
        }

        public void Reset()
        {
            _hostCpu.Clear();
            _hostMemory.Clear();
            _hostLoad.Clear();
            _api.Reset();
            _worker.Reset();
            _memoryTotalBytes = null;
            _activeChannelsMax = null;
            _liveChannelsMax = null;
            _chatLines = 0;
            _chatSeconds = 0;
            _emoteMatches = 0;
            _emoteSeconds = 0;
            _workloadSeconds = 0;
        }

        private void AddPair(
            AdminDashboardLoadSampleRow row,
            AdminDashboardLoadSampleRow previous,
            TimeSpan elapsed,
            RawSourceBucket source)
        {
            var processRate = SystemLoadRollupMath.RateFromDelta(
                SystemLoadRollupMath.Delta(row.ProcessCpuTimeTicks, previous.ProcessCpuTimeTicks),
                SystemLoadRollupMath.ProcessorTicksPerSecond,
                elapsed);
            if (processRate.HasValue)
            {
                source.ProcessRates.Add(processRate.Value);
            }

            var cgroupRate = SystemLoadRollupMath.RateFromDelta(
                SystemLoadRollupMath.Delta(row.CgroupCpuUsageMicroseconds, previous.CgroupCpuUsageMicroseconds),
                SystemLoadRollupMath.MicrosecondsPerSecond,
                elapsed);
            if (cgroupRate.HasValue)
            {
                source.CgroupRates.Add(cgroupRate.Value);

                // A share only against the same finite quota at both ends of the pair (spec 5.1).
                var quotaCores = QuotaCores(row.CgroupCpuQuotaMicroseconds, row.CgroupCpuPeriodMicroseconds);
                if (quotaCores.HasValue
                    && previous.CgroupCpuQuotaMicroseconds == row.CgroupCpuQuotaMicroseconds
                    && previous.CgroupCpuPeriodMicroseconds == row.CgroupCpuPeriodMicroseconds)
                {
                    source.QuotaShares.Add(100d * cgroupRate.Value / quotaCores.Value);
                }
            }

            if (SystemLoadRollupMath.TryGetHostCpuDelta(
                row.HostCpuBusyJiffies,
                previous.HostCpuBusyJiffies,
                row.HostCpuTotalJiffies,
                previous.HostCpuTotalJiffies,
                out var busyDelta,
                out var totalDelta))
            {
                _hostCpu.Add(new SourcedValue((double)busyDelta / totalDelta, row.IsWorker));
                source.HostBusyDelta += busyDelta;
                source.HostTotalDelta += totalDelta;
            }
        }

        // Interval counters belong to the bucket of their sample and are divided by the actual seconds
        // since the instance's previous persisted sample. The first sample of an instance has no known
        // interval and does not enter the rate.
        private void AddWorkload(AdminDashboardLoadSampleRow row)
        {
            _activeChannelsMax = MaxOf(_activeChannelsMax, row.ActiveChannels);
            _liveChannelsMax = MaxOf(_liveChannelsMax, row.LiveChannels);
            if (row.PreviousSampledAtUtc is not { } previousAtUtc || row.SampledAtUtc <= previousAtUtc)
            {
                return;
            }

            var seconds = (row.SampledAtUtc - previousAtUtc).TotalSeconds;
            if (row.ChatLines.HasValue)
            {
                _chatLines += row.ChatLines.Value;
                _chatSeconds += seconds;
            }

            if (row.EmoteMatches.HasValue)
            {
                _emoteMatches += row.EmoteMatches.Value;
                _emoteSeconds += seconds;
            }

            if (row.ChatLines.HasValue || row.EmoteMatches.HasValue)
            {
                _workloadSeconds += seconds;
            }
        }

        // Nearest-rank P95 and max over the union of both sources. The value source is the source of the
        // value at the P95 rank; when both sources hold that exact value, the higher per-source max wins,
        // and a full tie goes to the Worker.
        private static bool Percentile(List<SourcedValue> values, out double p95, out double max, out string? source)
        {
            if (values.Count == 0)
            {
                p95 = 0;
                max = 0;
                source = null;
                return false;
            }

            values.Sort(static (left, right) => left.Value.CompareTo(right.Value));
            p95 = values[SystemLoadRollupMath.Percentile95Index(values.Count)].Value;
            max = values[^1].Value;
            var apiHolds = false;
            var workerHolds = false;
            var apiMax = double.NegativeInfinity;
            var workerMax = double.NegativeInfinity;
            foreach (var value in values)
            {
                if (value.IsWorker)
                {
                    workerHolds |= value.Value == p95;
                    workerMax = Math.Max(workerMax, value.Value);
                }
                else
                {
                    apiHolds |= value.Value == p95;
                    apiMax = Math.Max(apiMax, value.Value);
                }
            }

            source = apiHolds && (!workerHolds || apiMax > workerMax) ? "A" : "W";
            return true;
        }

        private static double? QuotaCores(long? quotaMicroseconds, long? periodMicroseconds) =>
            quotaMicroseconds is > 0 && periodMicroseconds is > 0
                ? (double)quotaMicroseconds.Value / periodMicroseconds.Value
                : null;

        private static int? MaxOf(int? current, int? value) =>
            value is null ? current : current is null ? value : Math.Max(current.Value, value.Value);
    }

    private sealed class RawSourceBucket
    {
        public List<double> ProcessRates { get; } = [];
        public List<double> CgroupRates { get; } = [];
        public List<double> QuotaShares { get; } = [];
        public List<double> CgroupMemory { get; } = [];
        public List<double> MemoryShares { get; } = [];
        public int SampleCount { get; set; }
        public double? QuotaCores { get; set; }
        public long? MemoryLimitBytes { get; set; }
        public long HostBusyDelta { get; set; }
        public long HostTotalDelta { get; set; }
        public double MemoryUsedSum { get; set; }
        public int MemoryUsedCount { get; set; }
        public double LoadSum { get; set; }
        public int LoadCount { get; set; }

        // Same definition as the rollup's HostCpuBusyMean: jiffy-weighted, not a mean of rates.
        public double? HostCpuBusyMean() =>
            HostTotalDelta > 0 ? AdminDashboardLoadBucketing.Percent((double)HostBusyDelta / HostTotalDelta) : null;

        public long? MemoryUsedMean() => MemoryUsedCount > 0 ? (long)Math.Round(MemoryUsedSum / MemoryUsedCount) : null;

        public double? LoadMean() => LoadCount > 0 ? Math.Round(LoadSum / LoadCount, 2) : null;

        public void WriteTo(AdminDashboardLoadSeriesBuilder.ProcessColumns process, int index)
        {
            process.SampleCount[index] = SampleCount;
            process.CgroupCpuQuotaCores[index] = AdminDashboardLoadBucketing.Round(QuotaCores, 4);
            process.CgroupMemoryLimitBytes[index] = MemoryLimitBytes;
            (process.ProcessCpuP95[index], process.ProcessCpuMax[index]) = P95AndMax(ProcessRates, 4);
            (process.CgroupCpuP95[index], process.CgroupCpuMax[index]) = P95AndMax(CgroupRates, 4);
            (process.CgroupCpuQuotaShareP95[index], process.CgroupCpuQuotaShareMax[index]) = P95AndMax(QuotaShares, 1);
            var (memoryP95, memoryMax) = P95AndMax(CgroupMemory, digits: null);
            process.CgroupMemoryP95Bytes[index] = memoryP95 is null ? null : (long)memoryP95.Value;
            process.CgroupMemoryMaxBytes[index] = memoryMax is null ? null : (long)memoryMax.Value;
            (process.CgroupMemoryShareP95[index], process.CgroupMemoryShareMax[index]) = P95AndMax(MemoryShares, 1);
        }

        public void Reset()
        {
            ProcessRates.Clear();
            CgroupRates.Clear();
            QuotaShares.Clear();
            CgroupMemory.Clear();
            MemoryShares.Clear();
            SampleCount = 0;
            QuotaCores = null;
            MemoryLimitBytes = null;
            HostBusyDelta = 0;
            HostTotalDelta = 0;
            MemoryUsedSum = 0;
            MemoryUsedCount = 0;
            LoadSum = 0;
            LoadCount = 0;
        }

        private static (double? P95, double? Max) P95AndMax(List<double> values, int? digits)
        {
            if (values.Count == 0)
            {
                return (null, null);
            }

            values.Sort();
            var p95 = values[SystemLoadRollupMath.Percentile95Index(values.Count)];
            var max = values[^1];
            return digits is null ? (p95, max) : (Math.Round(p95, digits.Value), Math.Round(max, digits.Value));
        }
    }

    private readonly record struct SourcedValue(double Value, bool IsWorker);
}

/// <summary>
/// Hourly path. Rows arrive in ascending HourUtc order. Per bucket and host metric the shown P95 is the
/// highest stored hourly P95 over every (source, hour) in the bucket and the shown max the highest stored
/// hourly max; the value source is the row of that P95 (tie: higher hourly max, then Worker). There is
/// no cross-source P95, no quota share and no extrapolation of the stored workload counters.
/// </summary>
internal sealed class AdminDashboardHourlyLoadAccumulator(AdminDashboardLoadWindow window, int samplingIntervalSeconds)
{
    private readonly AdminDashboardLoadSeriesBuilder _series = new(window);
    private readonly HourlyBucket _bucket = new();
    private int _bucketIndex = -1;
    private DateTime? _dataThroughUtc;

    public void Add(SystemLoadHourly row)
    {
        // Older history (retention off or failed) never widens the window: rows outside it are dropped.
        var index = row.HourUtc < window.ToUtc ? window.IndexOf(row.HourUtc) : -1;
        if (index < 0)
        {
            return;
        }

        if (index < _bucketIndex)
        {
            throw new InvalidOperationException("Hourly load rows must arrive in ascending HourUtc order.");
        }

        if (index != _bucketIndex)
        {
            FlushBucket();
            _bucketIndex = index;
        }

        var throughUtc = row.HourUtc.AddHours(1);
        if (_dataThroughUtc is null || throughUtc > _dataThroughUtc)
        {
            _dataThroughUtc = throughUtc;
        }

        _bucket.Add(row);
    }

    public AdminDashboardLoad Complete(string range, DateTime generatedAtUtc)
    {
        FlushBucket();
        return _series.Build(range, "hourly", "perBucket", generatedAtUtc, _dataThroughUtc, samplingIntervalSeconds, swap: null);
    }

    private void FlushBucket()
    {
        if (_bucketIndex >= 0)
        {
            _bucket.WriteTo(_series, _bucketIndex);
            _bucket.Reset();
        }
    }

    private sealed class HourlyBucket
    {
        private readonly HourlySourceBucket _api = new();
        private readonly HourlySourceBucket _worker = new();
        private HostCandidate _cpu;
        private HostCandidate _memory;
        private HostCandidate _load;
        private long? _memoryTotalBytes;
        private int? _activeChannelsMax;
        private int? _liveChannelsMax;
        private long? _chatLines;
        private long? _emoteMatches;
        private int _workloadHours;

        public void Add(SystemLoadHourly row)
        {
            var isWorker = row.Source == SystemLoadSource.Worker;
            var source = isWorker ? _worker : _api;
            source.Add(row);

            _cpu.Offer(row.HostCpuBusyP95, row.HostCpuBusyMax, isWorker);
            _memory.Offer(row.HostMemoryUsedP95Bytes, row.HostMemoryUsedMaxBytes, isWorker);
            _load.Offer(row.HostLoadAverage1P95, row.HostLoadAverage1Max, isWorker);
            _memoryTotalBytes = row.LastHostMemoryTotalBytes is > 0 ? row.LastHostMemoryTotalBytes : _memoryTotalBytes;

            if (!isWorker)
            {
                return;
            }

            _activeChannelsMax = MaxOf(_activeChannelsMax, row.ActiveChannelsMax);
            _liveChannelsMax = MaxOf(_liveChannelsMax, row.LiveChannelsMax);
            if (row.ChatLines.HasValue)
            {
                _chatLines = (_chatLines ?? 0) + row.ChatLines.Value;
            }

            if (row.EmoteMatches.HasValue)
            {
                _emoteMatches = (_emoteMatches ?? 0) + row.EmoteMatches.Value;
            }

            if (row.ChatLines.HasValue || row.EmoteMatches.HasValue)
            {
                _workloadHours++;
            }
        }

        public void WriteTo(AdminDashboardLoadSeriesBuilder series, int index)
        {
            series.MarkObserved(index);
            var host = series.Host;
            host.Provenance[index] = AdminDashboardLoadBucketing.Provenance(_api.Rows > 0, _worker.Rows > 0);
            host.CpuBusyP95[index] = AdminDashboardLoadBucketing.Percent(_cpu.P95);
            host.CpuBusyMax[index] = AdminDashboardLoadBucketing.Percent(_cpu.Max);
            host.CpuValueSource[index] = _cpu.Source;
            host.MemoryUsedP95Bytes[index] = ToBytes(_memory.P95);
            host.MemoryUsedMaxBytes[index] = ToBytes(_memory.Max);
            host.MemoryValueSource[index] = _memory.Source;
            host.LoadAverage1P95[index] = AdminDashboardLoadBucketing.Round(_load.P95, 2);
            host.LoadAverage1Max[index] = AdminDashboardLoadBucketing.Round(_load.Max, 2);
            host.LoadAverageValueSource[index] = _load.Source;
            host.MemoryTotalBytes[index] = _memoryTotalBytes;
            host.ApiSampleCount[index] = _api.SampleCount;
            host.WorkerSampleCount[index] = _worker.SampleCount;
            host.ApiCpuBusyMean[index] = AdminDashboardLoadBucketing.Percent(_api.CpuBusyMean.Value);
            host.WorkerCpuBusyMean[index] = AdminDashboardLoadBucketing.Percent(_worker.CpuBusyMean.Value);
            host.ApiMemoryUsedMeanBytes[index] = ToBytes(_api.MemoryUsedMean.Value);
            host.WorkerMemoryUsedMeanBytes[index] = ToBytes(_worker.MemoryUsedMean.Value);
            host.ApiLoadAverage1Mean[index] = AdminDashboardLoadBucketing.Round(_api.LoadMean.Value, 2);
            host.WorkerLoadAverage1Mean[index] = AdminDashboardLoadBucketing.Round(_worker.LoadMean.Value, 2);

            _api.WriteTo(series.Api, index);
            _worker.WriteTo(series.Worker, index);

            var workload = series.Workload;
            workload.ActiveChannelsMax[index] = _activeChannelsMax;
            workload.LiveChannelsMax[index] = _liveChannelsMax;
            workload.ChatLines[index] = _chatLines;
            workload.EmoteMatches[index] = _emoteMatches;
            workload.WorkloadHoursWithData[index] = _workloadHours > 0 ? _workloadHours : null;
        }

        public void Reset()
        {
            _api.Reset();
            _worker.Reset();
            _cpu = default;
            _memory = default;
            _load = default;
            _memoryTotalBytes = null;
            _activeChannelsMax = null;
            _liveChannelsMax = null;
            _chatLines = null;
            _emoteMatches = null;
            _workloadHours = 0;
        }

        private static long? ToBytes(double? value) => value is null ? null : (long)Math.Round(value.Value);

        private static int? MaxOf(int? current, int? value) =>
            value is null ? current : current is null ? value : Math.Max(current.Value, value.Value);
    }

    private sealed class HourlySourceBucket
    {
        private double? _processP95;
        private double? _processMax;
        private double? _cgroupP95;
        private double? _cgroupMax;
        private long? _memoryP95;
        private long? _memoryMax;
        private double? _quotaCores;
        private long? _memoryLimitBytes;

        public int Rows { get; private set; }
        public int SampleCount { get; private set; }
        public WeightedMean CpuBusyMean { get; } = new();
        public WeightedMean MemoryUsedMean { get; } = new();
        public WeightedMean LoadMean { get; } = new();

        public void Add(SystemLoadHourly row)
        {
            Rows++;
            SampleCount += row.SampleCount;
            CpuBusyMean.Add(row.HostCpuBusyMean, row.SampleCount);
            MemoryUsedMean.Add(row.HostMemoryUsedMeanBytes, row.SampleCount);
            LoadMean.Add(row.HostLoadAverage1Mean, row.SampleCount);
            _processP95 = MaxOf(_processP95, row.ProcessCpuRateP95);
            _processMax = MaxOf(_processMax, row.ProcessCpuRateMax);
            _cgroupP95 = MaxOf(_cgroupP95, row.CgroupCpuRateP95);
            _cgroupMax = MaxOf(_cgroupMax, row.CgroupCpuRateMax);
            _memoryP95 = MaxOf(_memoryP95, row.CgroupMemoryCurrentP95Bytes);
            _memoryMax = MaxOf(_memoryMax, row.CgroupMemoryCurrentMaxBytes);

            // The limits are the last ones stored in the bucket; null there means "no finite limit".
            _quotaCores = row.LastCpuQuotaMicroseconds is > 0 && row.LastCpuPeriodMicroseconds is > 0
                ? (double)row.LastCpuQuotaMicroseconds.Value / row.LastCpuPeriodMicroseconds.Value
                : null;
            _memoryLimitBytes = row.LastCgroupMemoryMaxBytes is > 0 ? row.LastCgroupMemoryMaxBytes : null;
        }

        public void WriteTo(AdminDashboardLoadSeriesBuilder.ProcessColumns process, int index)
        {
            process.SampleCount[index] = SampleCount;
            process.ProcessCpuP95[index] = AdminDashboardLoadBucketing.Round(_processP95, 4);
            process.ProcessCpuMax[index] = AdminDashboardLoadBucketing.Round(_processMax, 4);
            process.CgroupCpuP95[index] = AdminDashboardLoadBucketing.Round(_cgroupP95, 4);
            process.CgroupCpuMax[index] = AdminDashboardLoadBucketing.Round(_cgroupMax, 4);
            process.CgroupCpuQuotaCores[index] = AdminDashboardLoadBucketing.Round(_quotaCores, 4);
            process.CgroupMemoryP95Bytes[index] = _memoryP95;
            process.CgroupMemoryMaxBytes[index] = _memoryMax;
            process.CgroupMemoryLimitBytes[index] = _memoryLimitBytes;
        }

        public void Reset()
        {
            Rows = 0;
            SampleCount = 0;
            CpuBusyMean.Reset();
            MemoryUsedMean.Reset();
            LoadMean.Reset();
            _processP95 = null;
            _processMax = null;
            _cgroupP95 = null;
            _cgroupMax = null;
            _memoryP95 = null;
            _memoryMax = null;
            _quotaCores = null;
            _memoryLimitBytes = null;
        }

        private static double? MaxOf(double? current, double? value) =>
            value is null ? current : current is null ? value : Math.Max(current.Value, value.Value);

        private static long? MaxOf(long? current, long? value) =>
            value is null ? current : current is null ? value : Math.Max(current.Value, value.Value);
    }

    // Per-source tooltip mean over several stored hours, weighted by each hour's SampleCount. Exact for
    // one hour per bucket (7d); an approximation of the true mean for coarser buckets.
    private sealed class WeightedMean
    {
        private double _sum;
        private long _weight;

        public double? Value => _weight > 0 ? _sum / _weight : null;

        public void Add(double? mean, int sampleCount)
        {
            if (mean.HasValue && sampleCount > 0)
            {
                _sum += mean.Value * sampleCount;
                _weight += sampleCount;
            }
        }

        public void Reset()
        {
            _sum = 0;
            _weight = 0;
        }
    }

    private struct HostCandidate
    {
        private double _winnerMax;
        private bool _winnerIsWorker;

        public double? P95 { get; private set; }
        public double? Max { get; private set; }
        public string? Source { get; private set; }

        public void Offer(double? p95, double? max, bool isWorker)
        {
            if (max.HasValue && (Max is null || max.Value > Max.Value))
            {
                Max = max;
            }

            if (p95 is null)
            {
                return;
            }

            var rowMax = max ?? double.NegativeInfinity;
            var wins = P95 is null
                || p95.Value > P95.Value
                || (p95.Value == P95.Value && (rowMax > _winnerMax || (rowMax == _winnerMax && isWorker && !_winnerIsWorker)));
            if (wins)
            {
                P95 = p95;
                Source = AdminDashboardLoadBucketing.SourceCode(isWorker);
                _winnerMax = rowMax;
                _winnerIsWorker = isWorker;
            }
        }

        public void Offer(long? p95, long? max, bool isWorker) => Offer((double?)p95, (double?)max, isWorker);
    }
}

/// <summary>
/// Column arrays of one load response, pre-sized to the window's bucket count. Build drops the leading
/// buckets before the first observation and copies every column from there on.
/// </summary>
internal sealed class AdminDashboardLoadSeriesBuilder(AdminDashboardLoadWindow window)
{
    private int _firstObserved = -1;

    public HostColumns Host { get; } = new(window.BucketCount);
    public ProcessColumns Api { get; } = new(window.BucketCount);
    public ProcessColumns Worker { get; } = new(window.BucketCount);
    public WorkloadColumns Workload { get; } = new(window.BucketCount);

    public void MarkObserved(int index)
    {
        if (_firstObserved < 0 || index < _firstObserved)
        {
            _firstObserved = index;
        }
    }

    public AdminDashboardLoad Build(
        string range,
        string sourceKind,
        string workloadUnit,
        DateTime generatedAtUtc,
        DateTime? dataThroughUtc,
        int samplingIntervalSeconds,
        AdminDashboardSwap? swap)
    {
        var available = _firstObserved >= 0;
        var start = available ? _firstObserved : window.BucketCount;
        var bucketStarts = new DateTime[window.BucketCount - start];
        for (var i = 0; i < bucketStarts.Length; i++)
        {
            bucketStarts[i] = window.BucketStartUtc(start + i);
        }

        return new AdminDashboardLoad(
            available,
            range,
            sourceKind,
            window.BucketSeconds,
            window.FromUtc,
            window.ToUtc,
            generatedAtUtc,
            available ? dataThroughUtc : null,
            samplingIntervalSeconds,
            bucketStarts,
            Host.Build(start),
            Api.Build(start),
            Worker.Build(start),
            Workload.Build(start, workloadUnit),
            available ? swap : null);
    }

    internal sealed class HostColumns(int count)
    {
        public double?[] CpuBusyP95 { get; } = new double?[count];
        public double?[] CpuBusyMax { get; } = new double?[count];
        public long?[] MemoryUsedP95Bytes { get; } = new long?[count];
        public long?[] MemoryUsedMaxBytes { get; } = new long?[count];
        public long?[] MemoryTotalBytes { get; } = new long?[count];
        public double?[] LoadAverage1P95 { get; } = new double?[count];
        public double?[] LoadAverage1Max { get; } = new double?[count];
        public string?[] Provenance { get; } = new string?[count];
        public string?[] CpuValueSource { get; } = new string?[count];
        public string?[] MemoryValueSource { get; } = new string?[count];
        public string?[] LoadAverageValueSource { get; } = new string?[count];
        public int[] ApiSampleCount { get; } = new int[count];
        public int[] WorkerSampleCount { get; } = new int[count];
        public double?[] ApiCpuBusyMean { get; } = new double?[count];
        public double?[] WorkerCpuBusyMean { get; } = new double?[count];
        public long?[] ApiMemoryUsedMeanBytes { get; } = new long?[count];
        public long?[] WorkerMemoryUsedMeanBytes { get; } = new long?[count];
        public double?[] ApiLoadAverage1Mean { get; } = new double?[count];
        public double?[] WorkerLoadAverage1Mean { get; } = new double?[count];

        public AdminDashboardHostSeries Build(int start) => new(
            CpuBusyP95[start..],
            CpuBusyMax[start..],
            MemoryUsedP95Bytes[start..],
            MemoryUsedMaxBytes[start..],
            MemoryTotalBytes[start..],
            LoadAverage1P95[start..],
            LoadAverage1Max[start..],
            Provenance[start..],
            CpuValueSource[start..],
            MemoryValueSource[start..],
            LoadAverageValueSource[start..],
            ApiSampleCount[start..],
            WorkerSampleCount[start..],
            ApiCpuBusyMean[start..],
            WorkerCpuBusyMean[start..],
            ApiMemoryUsedMeanBytes[start..],
            WorkerMemoryUsedMeanBytes[start..],
            ApiLoadAverage1Mean[start..],
            WorkerLoadAverage1Mean[start..]);
    }

    internal sealed class ProcessColumns(int count)
    {
        public double?[] ProcessCpuP95 { get; } = new double?[count];
        public double?[] ProcessCpuMax { get; } = new double?[count];
        public double?[] CgroupCpuP95 { get; } = new double?[count];
        public double?[] CgroupCpuMax { get; } = new double?[count];
        public double?[] CgroupCpuQuotaCores { get; } = new double?[count];
        public double?[] CgroupCpuQuotaShareP95 { get; } = new double?[count];
        public double?[] CgroupCpuQuotaShareMax { get; } = new double?[count];
        public long?[] CgroupMemoryP95Bytes { get; } = new long?[count];
        public long?[] CgroupMemoryMaxBytes { get; } = new long?[count];
        public long?[] CgroupMemoryLimitBytes { get; } = new long?[count];
        public double?[] CgroupMemoryShareP95 { get; } = new double?[count];
        public double?[] CgroupMemoryShareMax { get; } = new double?[count];
        public int[] SampleCount { get; } = new int[count];

        public AdminDashboardProcessSeries Build(int start) => new(
            ProcessCpuP95[start..],
            ProcessCpuMax[start..],
            CgroupCpuP95[start..],
            CgroupCpuMax[start..],
            CgroupCpuQuotaCores[start..],
            CgroupCpuQuotaShareP95[start..],
            CgroupCpuQuotaShareMax[start..],
            CgroupMemoryP95Bytes[start..],
            CgroupMemoryMaxBytes[start..],
            CgroupMemoryLimitBytes[start..],
            CgroupMemoryShareP95[start..],
            CgroupMemoryShareMax[start..],
            SampleCount[start..]);
    }

    internal sealed class WorkloadColumns(int count)
    {
        public int?[] ActiveChannelsMax { get; } = new int?[count];
        public int?[] LiveChannelsMax { get; } = new int?[count];
        public double?[] ChatLines { get; } = new double?[count];
        public double?[] EmoteMatches { get; } = new double?[count];
        public double?[] WorkloadIntervalSeconds { get; } = new double?[count];
        public int?[] WorkloadHoursWithData { get; } = new int?[count];

        public AdminDashboardWorkloadSeries Build(int start, string workloadUnit) => new(
            workloadUnit,
            ActiveChannelsMax[start..],
            LiveChannelsMax[start..],
            ChatLines[start..],
            EmoteMatches[start..],
            WorkloadIntervalSeconds[start..],
            WorkloadHoursWithData[start..]);
    }
}
