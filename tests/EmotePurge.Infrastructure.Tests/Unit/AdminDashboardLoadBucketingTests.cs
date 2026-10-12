using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure C# of the admin dashboard load range (#381, spec 5.2, section 11 B): the ladder and the hourly
// path. The raw path reads through Postgres; its cases are in AdminDashboardRawLoadReaderTests and
// AdminDashboardLoadQueryTests.
public class AdminDashboardLoadBucketingTests
{
    private const long Megabyte = 1_000_000;
    private static readonly DateTime NowUtc = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);


    // ---- ladder ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(AdminDashboardLoadRange.Hour1, 60, 61)]
    [InlineData(AdminDashboardLoadRange.Hours24, 300, 289)]
    [InlineData(AdminDashboardLoadRange.Days7, 3_600, 169)]
    [InlineData(AdminDashboardLoadRange.Days30, 7_200, 361)]
    [InlineData(AdminDashboardLoadRange.Days90, 21_600, 361)]
    [InlineData(AdminDashboardLoadRange.Months13, 86_400, 398)]
    public void Ladder_HasTheFixedWidthAndStaysWithinItsBucketCount(
        AdminDashboardLoadRange range,
        int bucketSeconds,
        int maximumBuckets)
    {
        DateTime[] nows =
        [
            NowUtc,
            new(2026, 10, 11, 12, 34, 56, 789, DateTimeKind.Utc),
            new(2026, 10, 11, 23, 59, 59, DateTimeKind.Utc),
            new(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        ];

        foreach (var now in nows)
        {
            var window = AdminDashboardLoadBucketing.GetWindow(range, now);
            var duration = AdminDashboardLoadRanges.GetDuration(range, now);

            Assert.Equal(bucketSeconds, window.BucketSeconds);
            Assert.Equal(bucketSeconds, AdminDashboardLoadRanges.GetBucketSeconds(range));
            Assert.InRange(window.BucketCount, 1, maximumBuckets);
            Assert.InRange(window.BucketCount, 1, AdminDashboardLoadBucketing.MaximumBuckets);
            Assert.Equal(now, window.ToUtc);
            Assert.Equal(DateTimeKind.Utc, window.FromUtc.Kind);
            Assert.Equal(0, window.FromUtc.Ticks % (bucketSeconds * TimeSpan.TicksPerSecond));
            Assert.True(window.FromUtc <= now - duration, $"{range} at {now:O}: from {window.FromUtc:O} is after the range start");
            Assert.True(now - duration < window.FromUtc.AddSeconds(bucketSeconds), $"{range} at {now:O}: from is more than one bucket early");

            var lastStart = window.BucketStartUtc(window.BucketCount - 1);
            Assert.True(lastStart <= now && now < lastStart.AddSeconds(bucketSeconds), $"{range} at {now:O}: the last bucket does not hold now");
        }
    }

    [Theory]
    [InlineData(AdminDashboardLoadRange.Hour1, "raw")]
    [InlineData(AdminDashboardLoadRange.Hours24, "raw")]
    [InlineData(AdminDashboardLoadRange.Days7, "hourly")]
    [InlineData(AdminDashboardLoadRange.Days30, "hourly")]
    [InlineData(AdminDashboardLoadRange.Days90, "hourly")]
    [InlineData(AdminDashboardLoadRange.Months13, "hourly")]
    public void Ladder_RawUpTo24Hours_HourlyFrom7Days(AdminDashboardLoadRange range, string expected)
    {
        var source = AdminDashboardLoadRanges.GetSource(range);

        Assert.Equal(expected, source == AdminDashboardLoadSource.Raw ? "raw" : "hourly");
    }

    [Theory]
    [InlineData("1h", AdminDashboardLoadRange.Hour1)]
    [InlineData("24h", AdminDashboardLoadRange.Hours24)]
    [InlineData("7d", AdminDashboardLoadRange.Days7)]
    [InlineData("30d", AdminDashboardLoadRange.Days30)]
    [InlineData("90d", AdminDashboardLoadRange.Days90)]
    [InlineData("13mo", AdminDashboardLoadRange.Months13)]
    public void Ranges_ParseAndWireNamesRoundTrip(string wire, AdminDashboardLoadRange range)
    {
        Assert.Equal(range, AdminDashboardLoadRanges.Parse(wire));
        Assert.Equal(wire, AdminDashboardLoadRanges.ToWire(range));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1H")]
    [InlineData(" 24h")]
    [InlineData("12h")]
    [InlineData("13m")]
    public void Ranges_RejectAnythingButTheExactWireNames(string? wire)
    {
        Assert.Null(AdminDashboardLoadRanges.Parse(wire));
    }

    // R2: 13mo is rolling. Month ends and the leap day clamp through DateTime.AddMonths; the start is
    // midnight of the day 13 calendar months back, not the first of a month.
    [Theory]
    [InlineData("2026-10-11", "2025-09-11", 396)]
    [InlineData("2026-10-31", "2025-09-30", 397)]
    [InlineData("2027-01-31", "2025-12-31", 397)]
    [InlineData("2028-02-29", "2027-01-29", 397)]
    [InlineData("2028-03-31", "2027-02-28", 398)]
    public void Ladder_13Months_IsRollingFromMidnightThirteenCalendarMonthsBack(string now, string from, int buckets)
    {
        var nowUtc = DateTime.SpecifyKind(DateTime.Parse(now).AddHours(17).AddMinutes(3), DateTimeKind.Utc);

        var window = AdminDashboardLoadBucketing.GetWindow(AdminDashboardLoadRange.Months13, nowUtc);

        Assert.Equal(DateTime.SpecifyKind(DateTime.Parse(from), DateTimeKind.Utc), window.FromUtc);
        Assert.Equal(buckets, window.BucketCount);
        Assert.True(window.BucketCount <= 398);
    }

    [Fact]
    public void HardCap_A401BucketWindowThrowsInsteadOfSilentlyExceedingTheBudget()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var fits = AdminDashboardLoadBucketing.CreateWindow(from, from.AddMinutes(399), 60);
        Assert.Equal(400, fits.BucketCount);

        Assert.Throws<InvalidOperationException>(() => AdminDashboardLoadBucketing.CreateWindow(from, from.AddMinutes(400), 60));
    }

    [Fact]
    public void Window_RejectsANonUtcNow()
    {
        Assert.Throws<ArgumentException>(() =>
            AdminDashboardLoadBucketing.GetWindow(AdminDashboardLoadRange.Hours24, DateTime.SpecifyKind(NowUtc, DateTimeKind.Unspecified)));
    }

    // ---- hourly path ----------------------------------------------------------------------------

    [Fact]
    public void Hourly_PartialHostCoverage_ShowsTheHigherHourlyP95AndMaxAndBothSampleCounts()
    {
        var hour = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
        var accumulator = HourlyAccumulator(AdminDashboardLoadRange.Days7);
        accumulator.Add(Hourly(SystemLoadSource.Api, hour, sampleCount: 120, cpuP95: 0.8, cpuMax: 0.9, cpuMean: 0.5));
        accumulator.Add(Hourly(SystemLoadSource.Worker, hour, sampleCount: 3, cpuP95: 0.1, cpuMax: 0.2, cpuMean: 0.1));

        var load = accumulator.Complete("7d", NowUtc);
        var index = IndexOf(load, hour);

        Assert.Equal("hourly", load.SourceKind);
        Assert.Equal(80.0, load.Host.CpuBusyP95[index]);
        Assert.Equal(90.0, load.Host.CpuBusyMax[index]);
        Assert.Equal("A", load.Host.CpuValueSource[index]);
        Assert.Equal("AW", load.Host.Provenance[index]);
        Assert.Equal(120, load.Host.ApiSampleCount[index]);
        Assert.Equal(3, load.Host.WorkerSampleCount[index]);
        Assert.Equal(50.0, load.Host.ApiCpuBusyMean[index]);
        Assert.Equal(10.0, load.Host.WorkerCpuBusyMean[index]);
        Assert.Equal(hour.AddHours(1), load.DataThroughUtc);
    }

    [Fact]
    public void Hourly_EachMetricPicksItsOwnWinner_TiesGoToTheHigherMaxThenWorker()
    {
        var mixed = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
        var tie = mixed.AddHours(1);
        var accumulator = HourlyAccumulator(AdminDashboardLoadRange.Days7);
        accumulator.Add(Hourly(SystemLoadSource.Api, mixed, cpuP95: 0.7, cpuMax: 0.8, memP95: 1_000, memMax: 1_100, loadP95: 1.5, loadMax: 2.0));
        accumulator.Add(Hourly(SystemLoadSource.Worker, mixed, cpuP95: 0.3, cpuMax: 0.95, memP95: 2_000, memMax: 2_100, loadP95: 1.5, loadMax: 3.0));
        accumulator.Add(Hourly(SystemLoadSource.Api, tie, cpuP95: 0.5, cpuMax: 0.6, memP95: 1_000, memMax: 1_100, loadP95: 1.5, loadMax: 2.0));
        accumulator.Add(Hourly(SystemLoadSource.Worker, tie, cpuP95: 0.5, cpuMax: 0.6, memP95: 1_000, memMax: 1_100, loadP95: 1.5, loadMax: 2.0));

        var load = accumulator.Complete("7d", NowUtc);
        var m = IndexOf(load, mixed);
        var t = IndexOf(load, tie);

        Assert.Equal("A", load.Host.CpuValueSource[m]);
        Assert.Equal(70.0, load.Host.CpuBusyP95[m]);
        Assert.Equal(95.0, load.Host.CpuBusyMax[m]);
        Assert.Equal("W", load.Host.MemoryValueSource[m]);
        Assert.Equal(2_000L, load.Host.MemoryUsedP95Bytes[m]);
        Assert.Equal("W", load.Host.LoadAverageValueSource[m]);
        Assert.Equal(3.0, load.Host.LoadAverage1Max[m]);

        Assert.Equal("W", load.Host.CpuValueSource[t]);
        Assert.Equal("W", load.Host.MemoryValueSource[t]);
        Assert.Equal("W", load.Host.LoadAverageValueSource[t]);
    }

    [Fact]
    public void Hourly_SeveralHoursPerBucket_HighestHourlyP95AndMax_ChannelsMax()
    {
        var first = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
        var second = first.AddHours(1);
        var accumulator = HourlyAccumulator(AdminDashboardLoadRange.Days30);
        accumulator.Add(Hourly(SystemLoadSource.Api, first, cpuP95: 0.5, cpuMax: 0.6, processP95: 0.1, processMax: 0.2, sampleCount: 100));
        accumulator.Add(Hourly(SystemLoadSource.Worker, first, cpuP95: 0.2, cpuMax: 0.3, active: 5, live: 2, sampleCount: 100));
        accumulator.Add(Hourly(SystemLoadSource.Api, second, cpuP95: 0.3, cpuMax: 0.4, processP95: 0.3, processMax: 0.25, sampleCount: 20));
        accumulator.Add(Hourly(SystemLoadSource.Worker, second, cpuP95: 0.4, cpuMax: 0.95, active: 7, live: null, sampleCount: 120));

        var load = accumulator.Complete("30d", NowUtc);
        var index = IndexOf(load, first);

        Assert.Equal(7_200, load.BucketSeconds);
        Assert.Equal(50.0, load.Host.CpuBusyP95[index]);
        Assert.Equal(95.0, load.Host.CpuBusyMax[index]);
        Assert.Equal("A", load.Host.CpuValueSource[index]);
        Assert.Equal(0.3, load.Api.ProcessCpuP95[index]);
        Assert.Equal(0.25, load.Api.ProcessCpuMax[index]);
        Assert.Equal(120, load.Api.SampleCount[index]);
        Assert.Equal(220, load.Worker.SampleCount[index]);
        Assert.Equal(7, load.Workload.ActiveChannelsMax[index]);
        Assert.Equal(2, load.Workload.LiveChannelsMax[index]);
    }

    [Fact]
    public void Hourly_NeverComputesAQuota_EvenWhenTheLimitChangesInsideTheBucket()
    {
        var first = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
        var accumulator = HourlyAccumulator(AdminDashboardLoadRange.Days30);
        accumulator.Add(Hourly(SystemLoadSource.Api, first, cgroupP95: 0.9, cgroupMax: 1.0, quota: 100_000, period: 100_000, memLimit: 512 * Megabyte, memTotal: 4_000 * Megabyte));
        accumulator.Add(Hourly(SystemLoadSource.Api, first.AddHours(1), cgroupP95: 0.9, cgroupMax: 1.0, quota: 200_000, period: 100_000, memLimit: 768 * Megabyte, memTotal: 8_000 * Megabyte));

        var load = accumulator.Complete("30d", NowUtc);
        var index = IndexOf(load, first);

        Assert.Equal(0.9, load.Api.CgroupCpuP95[index]);
        Assert.Equal(2.0, load.Api.CgroupCpuQuotaCores[index]);
        Assert.Equal(768 * Megabyte, load.Api.CgroupMemoryLimitBytes[index]);
        Assert.Equal(8_000 * Megabyte, load.Host.MemoryTotalBytes[index]);
        Assert.All(load.Api.CgroupCpuQuotaShareP95, Assert.Null);
        Assert.All(load.Api.CgroupCpuQuotaShareMax, Assert.Null);
        Assert.All(load.Api.CgroupMemoryShareP95, Assert.Null);
        Assert.All(load.Api.CgroupMemoryShareMax, Assert.Null);
        Assert.Null(load.Swap);
    }

    [Fact]
    public void Hourly_Workload_SixtyStoredCountersInAnHourStaySixty()
    {
        var hour = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
        var accumulator = HourlyAccumulator(AdminDashboardLoadRange.Days7);
        accumulator.Add(Hourly(SystemLoadSource.Worker, hour, chat: 60, emotes: 60));

        var load = accumulator.Complete("7d", NowUtc);
        var index = IndexOf(load, hour);

        Assert.Equal("perBucket", load.Workload.WorkloadUnit);
        Assert.Equal(60.0, load.Workload.ChatLines[index]);
        Assert.Equal(60.0, load.Workload.EmoteMatches[index]);
        Assert.Equal(1, load.Workload.WorkloadHoursWithData[index]);
        Assert.All(load.Workload.WorkloadIntervalSeconds, Assert.Null);
    }

    // R2: 48 hours of 60 per hour; multi-hour buckets sum the stored counters, a missing hour lowers
    // the sum and the hour count, nothing is extrapolated.
    [Theory]
    [InlineData(AdminDashboardLoadRange.Days30, "30d", 2)]
    [InlineData(AdminDashboardLoadRange.Days90, "90d", 6)]
    [InlineData(AdminDashboardLoadRange.Months13, "13mo", 24)]
    public void Hourly_Workload_MultiHourBucketsSumTheStoredCounters(AdminDashboardLoadRange range, string wire, int hoursPerBucket)
    {
        var start = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var missing = new DateTime(2026, 10, 10, 5, 0, 0, DateTimeKind.Utc);
        var accumulator = HourlyAccumulator(range);
        for (var hour = 0; hour < 48; hour++)
        {
            var hourUtc = start.AddHours(hour);
            if (hourUtc != missing)
            {
                accumulator.Add(Hourly(SystemLoadSource.Worker, hourUtc, chat: 60, emotes: 60));
            }
        }

        var load = accumulator.Complete(wire, NowUtc);
        var full = IndexOf(load, start);
        var gapBucket = IndexOf(load, FloorTo(missing, hoursPerBucket));

        Assert.Equal(60.0 * hoursPerBucket, load.Workload.ChatLines[full]);
        Assert.Equal(60.0 * hoursPerBucket, load.Workload.EmoteMatches[full]);
        Assert.Equal(hoursPerBucket, load.Workload.WorkloadHoursWithData[full]);
        Assert.Equal(60.0 * (hoursPerBucket - 1), load.Workload.ChatLines[gapBucket]);
        Assert.Equal(hoursPerBucket - 1, load.Workload.WorkloadHoursWithData[gapBucket]);
        Assert.Equal(60.0 * 47, load.Workload.ChatLines.Sum(value => value ?? 0));
    }

    [Fact]
    public void Hourly_RowsOutsideTheWindowNeverWidenIt()
    {
        var window = AdminDashboardLoadBucketing.GetWindow(AdminDashboardLoadRange.Months13, NowUtc);
        var accumulator = new AdminDashboardHourlyLoadAccumulator(window, 30);
        accumulator.Add(Hourly(SystemLoadSource.Worker, NowUtc.AddMonths(-20), chat: 1));
        accumulator.Add(Hourly(SystemLoadSource.Worker, NowUtc.AddMonths(-14), chat: 1));
        accumulator.Add(Hourly(SystemLoadSource.Worker, window.FromUtc, chat: 1));

        var load = accumulator.Complete("13mo", NowUtc);

        Assert.Equal(window.FromUtc, load.BucketStartsUtc[0]);
        Assert.Equal(window.BucketCount, load.BucketStartsUtc.Count);
        Assert.Equal(396, load.BucketStartsUtc.Count);
        Assert.Equal(1.0, load.Workload.ChatLines[0]);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static AdminDashboardHourlyLoadAccumulator HourlyAccumulator(AdminDashboardLoadRange range) =>
        new(AdminDashboardLoadBucketing.GetWindow(range, NowUtc), 30);

    private static int IndexOf(AdminDashboardLoad load, DateTime atUtc)
    {
        var index = -1;
        for (var i = 0; i < load.BucketStartsUtc.Count; i++)
        {
            if (load.BucketStartsUtc[i] <= atUtc && atUtc < load.BucketStartsUtc[i].AddSeconds(load.BucketSeconds))
            {
                index = i;
            }
        }

        Assert.True(index >= 0, $"No bucket holds {atUtc:O}.");
        return index;
    }

    private static DateTime FloorTo(DateTime atUtc, int hours) =>
        new(atUtc.Ticks - atUtc.Ticks % (hours * TimeSpan.TicksPerHour), DateTimeKind.Utc);

    private static SystemLoadHourly Hourly(
        SystemLoadSource source,
        DateTime hourUtc,
        int sampleCount = 120,
        double? cpuP95 = null,
        double? cpuMax = null,
        double? cpuMean = null,
        long? memP95 = null,
        long? memMax = null,
        double? loadP95 = null,
        double? loadMax = null,
        double? processP95 = null,
        double? processMax = null,
        double? cgroupP95 = null,
        double? cgroupMax = null,
        long? quota = null,
        long? period = null,
        long? memLimit = null,
        long? memTotal = null,
        long? chat = null,
        long? emotes = null,
        int? active = null,
        int? live = null) => new()
        {
            Source = source,
            HourUtc = hourUtc,
            SampleCount = sampleCount,
            HostCpuBusyP95 = cpuP95,
            HostCpuBusyMax = cpuMax,
            HostCpuBusyMean = cpuMean,
            HostMemoryUsedP95Bytes = memP95,
            HostMemoryUsedMaxBytes = memMax,
            HostLoadAverage1P95 = loadP95,
            HostLoadAverage1Max = loadMax,
            ProcessCpuRateP95 = processP95,
            ProcessCpuRateMax = processMax,
            CgroupCpuRateP95 = cgroupP95,
            CgroupCpuRateMax = cgroupMax,
            LastCpuQuotaMicroseconds = quota,
            LastCpuPeriodMicroseconds = period,
            LastCgroupMemoryMaxBytes = memLimit,
            LastHostMemoryTotalBytes = memTotal,
            ChatLines = chat,
            EmoteMatches = emotes,
            ActiveChannelsMax = active,
            LiveChannelsMax = live,
        };
}
