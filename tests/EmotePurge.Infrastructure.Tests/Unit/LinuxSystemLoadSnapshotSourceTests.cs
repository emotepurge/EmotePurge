using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public sealed class LinuxSystemLoadSnapshotSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("system-load-source-tests-").FullName;

    [Fact]
    public async Task ReadsCgroupMetricsFromTheResolvedProcessCgroupDirectory()
    {
        WriteProcessCgroup("0::/system.slice/emotepurge.service\n");
        WriteCgroupMetrics("", "usage_usec 11\n", "100000 100000\n", "22\n", "33\n");
        WriteCgroupMetrics("system.slice/emotepurge.service", "usage_usec 222\n", "300000 100000\n", "444\n", "555\n");

        var snapshot = await CreateSource().ReadAsync();

        Assert.Equal(222, snapshot.CgroupCpuUsageMicroseconds);
        Assert.Equal(300_000, snapshot.CgroupCpuQuotaMicroseconds);
        Assert.Equal(100_000, snapshot.CgroupCpuPeriodMicroseconds);
        Assert.Equal(444, snapshot.CgroupMemoryCurrentBytes);
        Assert.Equal(555, snapshot.CgroupMemoryMaxBytes);
    }

    [Fact]
    public async Task ResolvesPrivateRootCgroupPathToTheInjectedCgroupRoot()
    {
        WriteProcessCgroup("0::/\n");
        WriteCgroupMetrics("", "usage_usec 12\n", "200000 100000\n", "34\n", "56\n");

        var snapshot = await CreateSource().ReadAsync();

        Assert.Equal(12, snapshot.CgroupCpuUsageMicroseconds);
        Assert.Equal(200_000, snapshot.CgroupCpuQuotaMicroseconds);
        Assert.Equal(100_000, snapshot.CgroupCpuPeriodMicroseconds);
        Assert.Equal(34, snapshot.CgroupMemoryCurrentBytes);
        Assert.Equal(56, snapshot.CgroupMemoryMaxBytes);
    }

    [Fact]
    public async Task LeavesCgroupMetricsNullWhenTheResolvedDirectoryIsMissingInsteadOfUsingTheRoot()
    {
        WriteProcessCgroup("0::/missing/container\n");
        WriteCgroupMetrics("", "usage_usec 12\n", "200000 100000\n", "34\n", "56\n");

        var snapshot = await CreateSource().ReadAsync();

        Assert.Null(snapshot.CgroupCpuUsageMicroseconds);
        Assert.Null(snapshot.CgroupCpuQuotaMicroseconds);
        Assert.Null(snapshot.CgroupCpuPeriodMicroseconds);
        Assert.Null(snapshot.CgroupMemoryCurrentBytes);
        Assert.Null(snapshot.CgroupMemoryMaxBytes);
    }

    [Fact]
    public async Task LeavesMetricsNullForMissingOrMalformedCgroupMetricFiles()
    {
        WriteProcessCgroup("0::/worker\n");
        var directory = CgroupDirectory("worker");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cpu.stat"), "usage_usec invalid\n");
        File.WriteAllText(Path.Combine(directory, "cpu.max"), "max\n");
        File.WriteAllText(Path.Combine(directory, "memory.max"), "not-a-limit\n");

        var snapshot = await CreateSource().ReadAsync();

        Assert.Null(snapshot.CgroupCpuUsageMicroseconds);
        Assert.Null(snapshot.CgroupCpuQuotaMicroseconds);
        Assert.Null(snapshot.CgroupCpuPeriodMicroseconds);
        Assert.Null(snapshot.CgroupMemoryCurrentBytes);
        Assert.Null(snapshot.CgroupMemoryMaxBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a cgroup entry\n")]
    [InlineData("12:memory:/worker\n")]
    [InlineData("0::worker\n")]
    public async Task LeavesCgroupMetricsNullWhenProcessCgroupFileIsMissingOrMalformed(string? content)
    {
        WriteCgroupMetrics("", "usage_usec 12\n", "200000 100000\n", "34\n", "56\n");
        if (content is not null)
        {
            WriteProcessCgroup(content);
        }

        var snapshot = await CreateSource().ReadAsync();

        Assert.Null(snapshot.CgroupCpuUsageMicroseconds);
        Assert.Null(snapshot.CgroupCpuQuotaMicroseconds);
        Assert.Null(snapshot.CgroupCpuPeriodMicroseconds);
        Assert.Null(snapshot.CgroupMemoryCurrentBytes);
        Assert.Null(snapshot.CgroupMemoryMaxBytes);
    }

    [Fact]
    public async Task LeavesHostMetricsNullWhenProcMetricFilesAreMissing()
    {
        WriteProcessCgroup("0::/worker\n");
        WriteCgroupMetrics("worker", "usage_usec 12\n", "200000 100000\n", "34\n", "56\n");

        var snapshot = await CreateSource().ReadAsync();

        Assert.Null(snapshot.HostCpuBusyJiffies);
        Assert.Null(snapshot.HostCpuTotalJiffies);
        Assert.Null(snapshot.HostCpuIowaitJiffies);
        Assert.Null(snapshot.HostCpuStealJiffies);
        Assert.Null(snapshot.HostLoadAverage1);
        Assert.Null(snapshot.HostLoadAverage5);
        Assert.Null(snapshot.HostLoadAverage15);
        Assert.Null(snapshot.HostMemoryTotalBytes);
        Assert.Null(snapshot.HostMemoryAvailableBytes);
        Assert.Null(snapshot.HostSwapTotalBytes);
        Assert.Null(snapshot.HostSwapFreeBytes);
        Assert.Equal(12, snapshot.CgroupCpuUsageMicroseconds);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LinuxSystemLoadSnapshotSource CreateSource() =>
        new(TimeProvider.System, Path.Combine(_root, "proc"), Path.Combine(_root, "cgroup"));

    private void WriteProcessCgroup(string content)
    {
        var selfDirectory = Path.Combine(_root, "proc", "self");
        Directory.CreateDirectory(selfDirectory);
        File.WriteAllText(Path.Combine(selfDirectory, "cgroup"), content);
    }

    private void WriteCgroupMetrics(
        string relativeDirectory,
        string cpuStat,
        string cpuMax,
        string memoryCurrent,
        string memoryMax)
    {
        var directory = CgroupDirectory(relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cpu.stat"), cpuStat);
        File.WriteAllText(Path.Combine(directory, "cpu.max"), cpuMax);
        File.WriteAllText(Path.Combine(directory, "memory.current"), memoryCurrent);
        File.WriteAllText(Path.Combine(directory, "memory.max"), memoryMax);
    }

    private string CgroupDirectory(string relativeDirectory) =>
        Path.Combine(_root, "cgroup", relativeDirectory);
}
