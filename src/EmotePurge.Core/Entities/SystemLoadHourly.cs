namespace EmotePurge.Core.Entities;

/// <summary>An idempotently recomputed summary for one UTC hour and application source.</summary>
public class SystemLoadHourly
{
    public SystemLoadSource Source { get; set; }
    public DateTime HourUtc { get; set; }
    public int SampleCount { get; set; }

    public double? ProcessCpuRateMean { get; set; }
    public double? ProcessCpuRateMax { get; set; }
    public double? ProcessCpuRateP95 { get; set; }
    public double? CgroupCpuRateMean { get; set; }
    public double? CgroupCpuRateMax { get; set; }
    public double? CgroupCpuRateP95 { get; set; }
    public double? HostCpuBusyMean { get; set; }
    public double? HostCpuBusyMax { get; set; }
    public double? HostCpuBusyP95 { get; set; }

    public double? CgroupMemoryCurrentMeanBytes { get; set; }
    public long? CgroupMemoryCurrentMaxBytes { get; set; }
    public long? CgroupMemoryCurrentP95Bytes { get; set; }
    public double? HostMemoryUsedMeanBytes { get; set; }
    public long? HostMemoryUsedMaxBytes { get; set; }
    public long? HostMemoryUsedP95Bytes { get; set; }
    public double? HostLoadAverage1Mean { get; set; }
    public double? HostLoadAverage1Max { get; set; }
    public double? HostLoadAverage1P95 { get; set; }

    public long? LastCpuQuotaMicroseconds { get; set; }
    public long? LastCpuPeriodMicroseconds { get; set; }
    public long? LastCgroupMemoryMaxBytes { get; set; }
    public long? LastHostMemoryTotalBytes { get; set; }

    public long? ChatLines { get; set; }
    public long? EmoteMatches { get; set; }
    public double? ActiveChannelsMean { get; set; }
    public int? ActiveChannelsMax { get; set; }
    public double? LiveChannelsMean { get; set; }
    public int? LiveChannelsMax { get; set; }
}
