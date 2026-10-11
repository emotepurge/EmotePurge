using System.ComponentModel;
using System.Diagnostics;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>Reads Linux cgroup v2 and proc metrics; unavailable files become null values.</summary>
public sealed class LinuxSystemLoadSnapshotSource : ISystemLoadSnapshotSource
{
    private const string CgroupPath = "/sys/fs/cgroup";
    private const string ProcPath = "/proc";
    private readonly TimeProvider _timeProvider;
    private readonly string _cgroupRoot;
    private readonly string _procRoot;

    public LinuxSystemLoadSnapshotSource(
        TimeProvider timeProvider,
        string procRoot = ProcPath,
        string cgroupRoot = CgroupPath)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(procRoot);
        ArgumentNullException.ThrowIfNull(cgroupRoot);

        _timeProvider = timeProvider;
        _procRoot = Path.GetFullPath(procRoot);
        _cgroupRoot = Path.GetFullPath(cgroupRoot);
    }

    public Task<SystemLoadSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sampledAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var cgroupDirectory = ResolveProcessCgroupDirectory();
        var cpuStat = CgroupV2Parser.ParseCpuStat(ReadCgroupFile(cgroupDirectory, "cpu.stat"));
        var cpuLimit = CgroupV2Parser.ParseCpuMax(ReadCgroupFile(cgroupDirectory, "cpu.max"));
        var memoryCurrent = CgroupV2Parser.ParseMemoryCurrent(ReadCgroupFile(cgroupDirectory, "memory.current"));
        var memoryMax = CgroupV2Parser.ParseMemoryMax(ReadCgroupFile(cgroupDirectory, "memory.max"));
        var cpu = ProcFileParser.ParseCpuStat(ReadFile(Path.Combine(_procRoot, "stat")));
        var load = ProcFileParser.ParseLoadAverage(ReadFile(Path.Combine(_procRoot, "loadavg")));
        var memory = ProcFileParser.ParseMemoryInfo(ReadFile(Path.Combine(_procRoot, "meminfo")));

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

    private string? ResolveProcessCgroupDirectory()
    {
        var content = ReadFile(Path.Combine(_procRoot, "self", "cgroup"));
        if (content is null)
        {
            return null;
        }

        var line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => value.StartsWith("0::", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var cgroupPath = line[3..];
        if (cgroupPath == "/")
        {
            return Directory.Exists(_cgroupRoot) ? _cgroupRoot : null;
        }

        if (cgroupPath.Length < 2 || cgroupPath[0] != '/')
        {
            return null;
        }

        var segments = cgroupPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            return null;
        }

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(_cgroupRoot, Path.Combine(segments)));
            var relativeToRoot = Path.GetRelativePath(_cgroupRoot, candidate);
            if (Path.IsPathRooted(relativeToRoot)
                || relativeToRoot == ".."
                || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            return Directory.Exists(candidate) ? candidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadCgroupFile(string? cgroupDirectory, string fileName) =>
        cgroupDirectory is null ? null : ReadFile(Path.Combine(cgroupDirectory, fileName));

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
