using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SystemLoadRollupMathTests
{
    private static readonly DateTime Hour = new(2026, 10, 11, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ProcessIdentityChangeDoesNotCreateAPairEvenWhenTheCounterIncreases()
    {
        var prior = Sample(Hour.AddSeconds(-30), Guid.NewGuid(), processTicks: 10_000_000);
        var current = Sample(Hour, Guid.NewGuid(), processTicks: 900_000_000);

        var result = Calculate(prior, current);

        Assert.Null(result.ProcessCpuRateMean);
        Assert.Null(result.CgroupCpuRateMean);
        Assert.Null(result.HostCpuBusyMean);
    }

    [Fact]
    public void InterleavedProcessInstancesKeepTheirOwnImmediatePredecessors()
    {
        var firstIdentity = Guid.NewGuid();
        var otherIdentity = Guid.NewGuid();
        var result = Calculate(
            Sample(Hour, firstIdentity, processTicks: 0),
            Sample(Hour.AddSeconds(15), otherIdentity, processTicks: 900_000_000),
            Sample(Hour.AddSeconds(30), firstIdentity, processTicks: 300_000_000));

        Assert.Equal(1, result.ProcessCpuRateMean);
    }

    [Fact]
    public void PairLongerThanTwoAndAHalfIntervalsIsIgnored()
    {
        var identity = Guid.NewGuid();
        var result = Calculate(
            Sample(Hour, identity, processTicks: 0),
            Sample(Hour.AddMinutes(10), identity, processTicks: 10_000_000_000));

        Assert.Null(result.ProcessCpuRateMean);
    }

    [Fact]
    public void PairAcrossHourBoundaryBelongsToTheLaterSampleHour()
    {
        var identity = Guid.NewGuid();
        var prior = Sample(Hour.AddSeconds(-15), identity, processTicks: 0);
        var current = Sample(Hour.AddSeconds(15), identity, processTicks: 150_000_000);

        var result = Calculate(prior, current);

        Assert.Equal(Hour, result.HourUtc);
        Assert.Equal(1, result.SampleCount);
        Assert.Equal(1d / 2, result.ProcessCpuRateMean);
    }

    [Fact]
    public void MeanRateIsWeightedByElapsedTime()
    {
        var identity = Guid.NewGuid();
        var result = Calculate(
            Sample(Hour, identity, processTicks: 0),
            Sample(Hour.AddSeconds(10), identity, processTicks: 100_000_000),
            Sample(Hour.AddSeconds(40), identity, processTicks: 700_000_000));

        Assert.Equal(1.75, result.ProcessCpuRateMean);
    }

    [Fact]
    public void HostBusyMeanIsWeightedByTotalJiffies()
    {
        var identity = Guid.NewGuid();
        var result = Calculate(
            Sample(Hour, identity, processTicks: 0, hostBusy: 0, hostTotal: 0),
            Sample(Hour.AddSeconds(10), identity, processTicks: 0, hostBusy: 2, hostTotal: 4),
            Sample(Hour.AddSeconds(40), identity, processTicks: 0, hostBusy: 11, hostTotal: 40));

        Assert.Equal(0.275, result.HostCpuBusyMean);
    }

    [Fact]
    public void P95UsesTheRatesOfIndividualPairs()
    {
        var identity = Guid.NewGuid();
        var samples = new List<SystemLoadSample> { Sample(Hour, identity, processTicks: 0) };
        long ticks = 0;
        for (var pair = 1; pair <= 20; pair++)
        {
            ticks += pair * 10_000_000;
            samples.Add(Sample(Hour.AddSeconds(pair * 10), identity, processTicks: ticks));
        }

        var result = Calculate(samples.ToArray());

        Assert.Equal(2, result.ProcessCpuRateMax);
        Assert.Equal(1.9, result.ProcessCpuRateP95);
    }

    [Fact]
    public void SingleSampleHourKeepsThePairFromItsPrecedingHour()
    {
        var identity = Guid.NewGuid();
        var result = Calculate(
            Sample(Hour.AddSeconds(-15), identity, processTicks: 0),
            Sample(Hour.AddSeconds(15), identity, processTicks: 150_000_000));

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(0.5, result.ProcessCpuRateMean);
    }

    [Fact]
    public void NegativeCgroupAndHostDeltasAreDiscarded()
    {
        var identity = Guid.NewGuid();
        var result = Calculate(
            Sample(Hour, identity, processTicks: 0, cgroupUsage: 100, hostBusy: 10, hostTotal: 100),
            Sample(Hour.AddSeconds(30), identity, processTicks: 300_000_000, cgroupUsage: 90, hostBusy: 9, hostTotal: 90));

        Assert.Equal(1, result.ProcessCpuRateMean);
        Assert.Null(result.CgroupCpuRateMean);
        Assert.Null(result.HostCpuBusyMean);
    }

    private static SystemLoadHourly Calculate(params SystemLoadSample[] samples) =>
        SystemLoadRollupMath.Calculate(samples, SystemLoadSource.Worker, Hour, samplingIntervalSeconds: 30);

    private static SystemLoadSample Sample(
        DateTime sampledAtUtc,
        Guid identity,
        long processTicks,
        long? cgroupUsage = 0,
        long? hostBusy = 0,
        long? hostTotal = 100)
        => new()
        {
            SampledAtUtc = sampledAtUtc,
            Source = SystemLoadSource.Worker,
            ProcessInstanceId = identity,
            ProcessCpuTimeTicks = processTicks,
            CgroupCpuUsageMicroseconds = cgroupUsage,
            HostCpuBusyJiffies = hostBusy,
            HostCpuTotalJiffies = hostTotal,
        };
}
