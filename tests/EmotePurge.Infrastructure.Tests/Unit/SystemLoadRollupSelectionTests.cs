using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SystemLoadRollupSelectionTests
{
    private static readonly DateTime H0 = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime[] Window = [H0, H0.AddHours(1), H0.AddHours(2)];

    // protects: steady state; fails_when: an unchanged hour is recomputed; no_claim: nothing about SQL counts.
    [Fact]
    public void UnchangedCountsSelectNothing()
    {
        var result = Select(Raw((Worker(0), 10), (Worker(1), 20), (Worker(2), 30)), Stored((Worker(0), 10), (Worker(1), 20), (Worker(2), 30)));

        Assert.Empty(result);
    }

    // protects: new hours get a row and later hours follow; fails_when: only the missing hour, or nothing, is returned.
    [Fact]
    public void MissingRowSelectsThatHourAndAllLaterOnes()
    {
        var result = Select(Raw((Worker(0), 10), (Worker(1), 20), (Worker(2), 30)), Stored((Worker(0), 10)));

        Assert.Equal([Worker(1), Worker(2)], result);
    }

    // protects: forward propagation from the first window hour; fails_when: only the changed hour is returned.
    [Fact]
    public void ChangedCountInTheFirstWindowHourSelectsTheWholeWindow()
    {
        var result = Select(Raw((Worker(0), 11), (Worker(1), 20), (Worker(2), 30)), Stored((Worker(0), 10), (Worker(1), 20), (Worker(2), 30)));

        Assert.Equal([Worker(0), Worker(1), Worker(2)], result);
    }

    // protects: earlier hours stay untouched; fails_when: hours before the change are returned.
    [Fact]
    public void ChangedCountInTheLastHourSelectsOnlyTheLastHour()
    {
        var result = Select(Raw((Worker(0), 10), (Worker(1), 20), (Worker(2), 31)), Stored((Worker(0), 10), (Worker(1), 20), (Worker(2), 30)));

        Assert.Equal([Worker(2)], result);
    }

    // protects: source isolation; fails_when: a dirty Worker hour pulls in Api hours.
    [Fact]
    public void DirtyHourOfOneSourceDoesNotSelectTheOtherSource()
    {
        var result = Select(
            Raw((Worker(0), 11), (Api(0), 10), (Api(1), 20)),
            Stored((Worker(0), 10), (Api(0), 10), (Api(1), 20)));

        Assert.Equal([Worker(0)], result);
    }

    // protects: no rows for empty hours; fails_when: an hour with neither samples nor a row is selected.
    [Fact]
    public void EmptyWindowAndHoursWithoutSamplesOrRowsSelectNothing()
    {
        Assert.Empty(SystemLoadRollupSelection.SelectDirtyHours([], Raw(), Stored()));
        Assert.Empty(Select(Raw(), Stored()));
        Assert.Equal([Worker(2)], Select(Raw((Worker(2), 5)), Stored()));
    }

    // protects: stored CPU survives a recompute without pairs; fails_when: a null overwrites it or other groups follow.
    [Fact]
    public void NewNullKeepsTheStoredGroupAndGroupsAreIndependent()
    {
        var stored = Hourly(process: 0.5, cgroup: 0.25, host: 0.2);
        var recomputed = Hourly(process: null, cgroup: 0.3, host: null);

        var kept = SystemLoadRollupSelection.KeepStoredCpuGroups(stored, recomputed);

        Assert.Equal(2, kept);
        Assert.Equal(0.5, recomputed.ProcessCpuRateMean);
        Assert.Equal(0.55, recomputed.ProcessCpuRateMax);
        Assert.Equal(0.6, recomputed.ProcessCpuRateP95);
        Assert.Equal(0.3, recomputed.CgroupCpuRateMean);
        Assert.Equal(0.2, recomputed.HostCpuBusyMean);
        Assert.Equal(0.25, recomputed.HostCpuBusyMax);
    }

    // protects: a real new value wins; fails_when: the stored value replaces a valid recompute.
    [Fact]
    public void NewValueOverwritesTheStoredGroup()
    {
        var recomputed = Hourly(process: 0.9, cgroup: 0.9, host: 0.9);

        var kept = SystemLoadRollupSelection.KeepStoredCpuGroups(Hourly(0.5, 0.5, 0.5), recomputed);

        Assert.Equal(0, kept);
        Assert.Equal(0.9, recomputed.ProcessCpuRateMean);
    }

    // protects: no invention of values; fails_when: null/null produces a value or counts as kept.
    [Fact]
    public void StoredNullAndNewNullStayNull()
    {
        var recomputed = Hourly(null, null, null);

        var kept = SystemLoadRollupSelection.KeepStoredCpuGroups(Hourly(null, null, null), recomputed);

        Assert.Equal(0, kept);
        Assert.Null(recomputed.ProcessCpuRateMean);
        Assert.Null(recomputed.HostCpuBusyP95);
    }

    // protects: only CPU groups are guarded; fails_when: a non-CPU column of the recompute is replaced.
    [Fact]
    public void OtherColumnsOfTheRecomputeAreLeftAlone()
    {
        var stored = Hourly(0.5, 0.5, 0.5);
        stored.SampleCount = 1;
        var recomputed = Hourly(null, null, null);
        recomputed.SampleCount = 7;

        SystemLoadRollupSelection.KeepStoredCpuGroups(stored, recomputed);

        Assert.Equal(7, recomputed.SampleCount);
    }

    private static IReadOnlyList<SystemLoadHourKey> Select(
        Dictionary<SystemLoadHourKey, long> raw,
        Dictionary<SystemLoadHourKey, int> stored) =>
        SystemLoadRollupSelection.SelectDirtyHours(Window, raw, stored);

    private static Dictionary<SystemLoadHourKey, long> Raw(params (SystemLoadHourKey Key, long Count)[] entries) =>
        entries.ToDictionary(static e => e.Key, static e => e.Count);

    private static Dictionary<SystemLoadHourKey, int> Stored(params (SystemLoadHourKey Key, int Count)[] entries) =>
        entries.ToDictionary(static e => e.Key, static e => e.Count);

    private static SystemLoadHourKey Worker(int hour) => new(SystemLoadSource.Worker, H0.AddHours(hour));

    private static SystemLoadHourKey Api(int hour) => new(SystemLoadSource.Api, H0.AddHours(hour));

    private static SystemLoadHourly Hourly(double? process, double? cgroup, double? host) => new()
    {
        ProcessCpuRateMean = process,
        ProcessCpuRateMax = process + 0.05,
        ProcessCpuRateP95 = process + 0.1,
        CgroupCpuRateMean = cgroup,
        CgroupCpuRateMax = cgroup,
        CgroupCpuRateP95 = cgroup,
        HostCpuBusyMean = host,
        HostCpuBusyMax = host + 0.05,
        HostCpuBusyP95 = host,
    };
}
