namespace EmotePurge.Core.Services;

/// <summary>Reads one process and host observation without persisting it.</summary>
public interface ISystemLoadSnapshotSource
{
    Task<SystemLoadSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>The parsed operating-system values observed at one UTC instant.</summary>
public sealed record SystemLoadSnapshot(
    DateTime SampledAtUtc,
    long? ProcessCpuTimeTicks,
    long? CgroupCpuUsageMicroseconds,
    long? CgroupCpuQuotaMicroseconds,
    long? CgroupCpuPeriodMicroseconds,
    long? CgroupMemoryCurrentBytes,
    long? CgroupMemoryMaxBytes,
    long? HostCpuBusyJiffies,
    long? HostCpuTotalJiffies,
    long? HostCpuIowaitJiffies,
    long? HostCpuStealJiffies,
    double? HostLoadAverage1,
    double? HostLoadAverage5,
    double? HostLoadAverage15,
    long? HostMemoryTotalBytes,
    long? HostMemoryAvailableBytes,
    long? HostSwapTotalBytes,
    long? HostSwapFreeBytes);
