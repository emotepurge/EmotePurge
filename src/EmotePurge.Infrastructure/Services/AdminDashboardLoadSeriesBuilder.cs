using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

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
