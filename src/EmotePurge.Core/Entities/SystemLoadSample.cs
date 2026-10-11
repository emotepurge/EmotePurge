namespace EmotePurge.Core.Entities;

/// <summary>A raw process, host, and workload observation from one application process.</summary>
public class SystemLoadSample
{
    public long Id { get; set; }
    public DateTime SampledAtUtc { get; set; }
    public SystemLoadSource Source { get; set; }
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
    public long? HostCpuIowaitJiffies { get; set; }
    public long? HostCpuStealJiffies { get; set; }
    public double? HostLoadAverage1 { get; set; }
    public double? HostLoadAverage5 { get; set; }
    public double? HostLoadAverage15 { get; set; }
    public long? HostMemoryTotalBytes { get; set; }
    public long? HostMemoryAvailableBytes { get; set; }
    public long? HostSwapTotalBytes { get; set; }
    public long? HostSwapFreeBytes { get; set; }

    public int? ActiveChannels { get; set; }
    public int? LiveChannels { get; set; }
    public long? ChatLines { get; set; }
    public long? EmoteMatches { get; set; }
}
