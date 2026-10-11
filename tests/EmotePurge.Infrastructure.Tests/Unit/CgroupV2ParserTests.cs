using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class CgroupV2ParserTests
{
    [Fact]
    public void CpuStatReadsUsageFromTheUsageLine()
    {
        var result = CgroupV2Parser.ParseCpuStat(ReadSample("cgroup-cpu.stat.txt"));

        Assert.Equal(12_345_678, result);
    }

    [Fact]
    public void CpuMaxParsesQuotaAndPeriod()
    {
        var result = CgroupV2Parser.ParseCpuMax("200000 100000\n");

        Assert.Equal(200_000, result.QuotaMicroseconds);
        Assert.Equal(100_000, result.PeriodMicroseconds);
    }

    [Fact]
    public void CpuMaxTreatsMaxAsAnUnboundedQuota()
    {
        var result = CgroupV2Parser.ParseCpuMax(ReadSample("cgroup-cpu.max.txt"));

        Assert.Null(result.QuotaMicroseconds);
        Assert.Equal(100_000, result.PeriodMicroseconds);
    }

    [Fact]
    public void MemoryFilesParseBytesAndUnboundedLimit()
    {
        Assert.Equal(125_829_120, CgroupV2Parser.ParseMemoryCurrent(ReadSample("cgroup-memory.current.txt")));
        Assert.Null(CgroupV2Parser.ParseMemoryMax(ReadSample("cgroup-memory.max.txt")));
        Assert.Equal(536_870_912, CgroupV2Parser.ParseMemoryMax("536870912\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a cgroup file")]
    public void MissingEmptyAndGarbageContentsReturnNull(string? content)
    {
        Assert.Null(CgroupV2Parser.ParseCpuStat(content));
        Assert.Null(CgroupV2Parser.ParseCpuMax(content).PeriodMicroseconds);
        Assert.Null(CgroupV2Parser.ParseMemoryCurrent(content));
        Assert.Null(CgroupV2Parser.ParseMemoryMax(content));
    }

    private static string ReadSample(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Unit", "TestData", name));
}
