using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public sealed class SystemLoadSamplingService(AppDbContext db) : ISystemLoadSamplingService
{
    public async Task RecordAsync(
        SystemLoadSource source,
        Guid processInstanceId,
        SystemLoadSnapshot snapshot,
        SystemLoadWorkloadSnapshot workload,
        CancellationToken cancellationToken = default)
    {
        var previousSampledAtUtc = await db.SystemLoadSamples
            .Where(sample => sample.Source == source && sample.ProcessInstanceId == processInstanceId)
            .OrderByDescending(sample => sample.SampledAtUtc)
            .Select(sample => (DateTime?)sample.SampledAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        db.SystemLoadSamples.Add(new SystemLoadSample
        {
            SampledAtUtc = snapshot.SampledAtUtc,
            Source = source,
            ProcessInstanceId = processInstanceId,
            PreviousSampledAtUtc = previousSampledAtUtc,
            ProcessCpuTimeTicks = snapshot.ProcessCpuTimeTicks,
            CgroupCpuUsageMicroseconds = snapshot.CgroupCpuUsageMicroseconds,
            CgroupCpuQuotaMicroseconds = snapshot.CgroupCpuQuotaMicroseconds,
            CgroupCpuPeriodMicroseconds = snapshot.CgroupCpuPeriodMicroseconds,
            CgroupMemoryCurrentBytes = snapshot.CgroupMemoryCurrentBytes,
            CgroupMemoryMaxBytes = snapshot.CgroupMemoryMaxBytes,
            HostCpuBusyJiffies = snapshot.HostCpuBusyJiffies,
            HostCpuTotalJiffies = snapshot.HostCpuTotalJiffies,
            HostCpuIowaitJiffies = snapshot.HostCpuIowaitJiffies,
            HostCpuStealJiffies = snapshot.HostCpuStealJiffies,
            HostLoadAverage1 = snapshot.HostLoadAverage1,
            HostLoadAverage5 = snapshot.HostLoadAverage5,
            HostLoadAverage15 = snapshot.HostLoadAverage15,
            HostMemoryTotalBytes = snapshot.HostMemoryTotalBytes,
            HostMemoryAvailableBytes = snapshot.HostMemoryAvailableBytes,
            HostSwapTotalBytes = snapshot.HostSwapTotalBytes,
            HostSwapFreeBytes = snapshot.HostSwapFreeBytes,
            ActiveChannels = workload.ActiveChannels,
            LiveChannels = workload.LiveChannels,
            ChatLines = workload.ChatLines,
            EmoteMatches = workload.EmoteMatches,
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
