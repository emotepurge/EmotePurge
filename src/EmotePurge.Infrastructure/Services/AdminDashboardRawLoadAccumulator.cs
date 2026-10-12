using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The projected columns of one SystemLoadSamples row that the raw load path reads. The reader loop
/// in <see cref="AdminDashboardRawLoadQuery"/> reuses one instance for every row, so nothing may keep a
/// reference to it.
/// </summary>
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
/// Raw path. Rows arrive in ascending SampledAtUtc order; rows before the window (the read starts one
/// maximum pair gap early) only seed the pairing, so the last of them is exactly the predecessor that
/// can still form a valid pair. Pairs follow <see cref="SystemLoadRollupMath"/> exactly
/// (same source and instance, positive gap of at most 2.5 configured intervals, monotonic counter) and
/// count in the bucket of the later sample. Only the current bucket's values are held: a bucket is
/// finished as soon as a row of a later bucket arrives.
/// </summary>
internal sealed class AdminDashboardRawLoadAccumulator(AdminDashboardLoadWindow window, int samplingIntervalSeconds)
{
    private readonly AdminDashboardLoadSeriesBuilder _series = new(window);
    private readonly Dictionary<(bool IsWorker, Guid ProcessInstanceId), PairEnd> _previous = [];
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
        PairEnd? previous = _previous.TryGetValue(key, out var known) ? known : null;
        _previous[key] = PairEnd.From(row);

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

        // Last known value (spec 5.1): a later valid reading replaces it, an explicit SwapTotal of 0 means
        // "now none", and an unknown or inconsistent reading keeps what was known.
        if (row.HostSwapTotalBytes is > 0 && row.HostSwapFreeBytes is >= 0 && row.HostSwapFreeBytes <= row.HostSwapTotalBytes)
        {
            _swap = new AdminDashboardSwap(
                row.HostSwapTotalBytes.Value - row.HostSwapFreeBytes.Value,
                row.HostSwapTotalBytes.Value,
                row.SampledAtUtc);
        }
        else if (row.HostSwapTotalBytes == 0)
        {
            _swap = null;
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

        public void Add(AdminDashboardLoadSampleRow row, PairEnd? previous, int samplingIntervalSeconds)
        {
            var source = row.IsWorker ? _worker : _api;
            source.SampleCount++;

            if (previous is { } end)
            {
                var elapsed = row.SampledAtUtc - end.SampledAtUtc;
                if (SystemLoadRollupMath.IsPairWithinGap(elapsed, samplingIntervalSeconds))
                {
                    AddPair(row, end, elapsed, source);
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
            PairEnd previous,
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
                    workerHolds |= value.Value.CompareTo(p95) == 0;
                    workerMax = Math.Max(workerMax, value.Value);
                }
                else
                {
                    apiHolds |= value.Value.CompareTo(p95) == 0;
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

    // The counters of an instance's previous sample: copied, because the reader reuses the row object.
    private readonly record struct PairEnd(
        DateTime SampledAtUtc,
        long? ProcessCpuTimeTicks,
        long? CgroupCpuUsageMicroseconds,
        long? CgroupCpuQuotaMicroseconds,
        long? CgroupCpuPeriodMicroseconds,
        long? HostCpuBusyJiffies,
        long? HostCpuTotalJiffies)
    {
        public static PairEnd From(AdminDashboardLoadSampleRow row) => new(
            row.SampledAtUtc,
            row.ProcessCpuTimeTicks,
            row.CgroupCpuUsageMicroseconds,
            row.CgroupCpuQuotaMicroseconds,
            row.CgroupCpuPeriodMicroseconds,
            row.HostCpuBusyJiffies,
            row.HostCpuTotalJiffies);
    }
}
