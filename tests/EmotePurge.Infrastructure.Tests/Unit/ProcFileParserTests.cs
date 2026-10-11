using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class ProcFileParserTests
{
    [Fact]
    public void StatParsesAggregateCpuCounters()
    {
        var result = ProcFileParser.ParseCpuStat(ReadSample("proc-stat.txt"));

        Assert.NotNull(result);
        Assert.Equal(219, result.BusyJiffies);
        Assert.Equal(1_018, result.TotalJiffies);
        Assert.Equal(10, result.IowaitJiffies);
        Assert.Equal(13, result.StealJiffies);
    }

    [Fact]
    public void LoadavgParsesTheThreeLoadAverages()
    {
        var result = ProcFileParser.ParseLoadAverage(ReadSample("proc-loadavg.txt"));

        Assert.Equal(0.42, result.OneMinute);
        Assert.Equal(0.37, result.FiveMinutes);
        Assert.Equal(0.29, result.FifteenMinutes);
    }

    [Fact]
    public void MeminfoParsesRequiredMemoryAndSwapFieldsAsBytes()
    {
        var result = ProcFileParser.ParseMemoryInfo(ReadSample("proc-meminfo.txt"));

        Assert.Equal(16_384L * 1024, result.MemTotalBytes);
        Assert.Equal(9_216L * 1024, result.MemAvailableBytes);
        Assert.Equal(2_048L * 1024, result.SwapTotalBytes);
        Assert.Equal(1_536L * 1024, result.SwapFreeBytes);
    }

    [Fact]
    public void MissingAndGarbageProcFilesReturnNullValues()
    {
        var missing = ProcFileParser.ParseCpuStat(null);
        var garbage = ReadSample("garbage.txt");

        Assert.Null(missing);
        Assert.Null(ProcFileParser.ParseCpuStat(garbage));
        Assert.Null(ProcFileParser.ParseLoadAverage(garbage).OneMinute);

        var memory = ProcFileParser.ParseMemoryInfo(garbage);
        Assert.Null(memory.MemTotalBytes);
        Assert.Null(memory.MemAvailableBytes);
        Assert.Null(memory.SwapTotalBytes);
        Assert.Null(memory.SwapFreeBytes);
    }

    private static string ReadSample(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Unit", "TestData", name));
}
