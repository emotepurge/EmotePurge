using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

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
            var wins = P95 is null || Beats(p95.Value, rowMax, isWorker);
            if (wins)
            {
                P95 = p95;
                Source = AdminDashboardLoadBucketing.SourceCode(isWorker);
                _winnerMax = rowMax;
                _winnerIsWorker = isWorker;
            }
        }

        public void Offer(long? p95, long? max, bool isWorker) => Offer((double?)p95, (double?)max, isWorker);

        // Higher P95 wins; equal P95 -> higher max wins; fully equal -> the Worker wins over the Api.
        // CompareTo instead of ==/> so no floating-point equality test is involved.
        private bool Beats(double p95, double rowMax, bool isWorker)
        {
            var byP95 = p95.CompareTo(P95!.Value);
            if (byP95 != 0)
            {
                return byP95 > 0;
            }

            var byMax = rowMax.CompareTo(_winnerMax);
            return byMax > 0 || (byMax == 0 && isWorker && !_winnerIsWorker);
        }
    }
}
