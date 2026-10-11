using System.ComponentModel;
using System.Diagnostics;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>Reads Linux cgroup v2 and proc metrics; unavailable files become null values.</summary>
public sealed class LinuxSystemLoadSnapshotSource(TimeProvider timeProvider) : ISystemLoadSnapshotSource
{
    private const string CgroupPath = "/sys/fs/cgroup";
    private const string ProcPath = "/proc";

    public Task<SystemLoadSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sampledAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var cpuStat = CgroupV2Parser.ParseCpuStat(ReadFile(Path.Combine(CgroupPath, "cpu.stat")));
        var cpuLimit = CgroupV2Parser.ParseCpuMax(ReadFile(Path.Combine(CgroupPath, "cpu.max")));
        var memoryCurrent = CgroupV2Parser.ParseMemoryCurrent(ReadFile(Path.Combine(CgroupPath, "memory.current")));
        var memoryMax = CgroupV2Parser.ParseMemoryMax(ReadFile(Path.Combine(CgroupPath, "memory.max")));
        var cpu = ProcFileParser.ParseCpuStat(ReadFile(Path.Combine(ProcPath, "stat")));
        var load = ProcFileParser.ParseLoadAverage(ReadFile(Path.Combine(ProcPath, "loadavg")));
        var memory = ProcFileParser.ParseMemoryInfo(ReadFile(Path.Combine(ProcPath, "meminfo")));

        return Task.FromResult(new SystemLoadSnapshot(
            sampledAtUtc,
            ReadProcessCpuTime(),
            cpuStat,
            cpuLimit.QuotaMicroseconds,
            cpuLimit.PeriodMicroseconds,
            memoryCurrent,
            memoryMax,
            cpu?.BusyJiffies,
            cpu?.TotalJiffies,
            cpu?.IowaitJiffies,
            cpu?.StealJiffies,
            load.OneMinute,
            load.FiveMinutes,
            load.FifteenMinutes,
            memory.MemTotalBytes,
            memory.MemAvailableBytes,
            memory.SwapTotalBytes,
            memory.SwapFreeBytes));
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadProcessCpuTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime.Ticks;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or Win32Exception
            or PlatformNotSupportedException
            or NotSupportedException)
        {
            return null;
        }
    }
}
