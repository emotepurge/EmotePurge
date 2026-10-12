using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Pure bucket math of the admin dashboard load range (#381, spec 5.1/5.2, section 11 B). The SQL side
// (predecessor loading, raw/hourly boundary, rollup equivalence) is in AdminDashboardLoadQueryTests.
public class AdminDashboardLoadBucketingTests
{
    private const long Megabyte = 1_000_000;
    private static readonly DateTime NowUtc = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    // Raw unit-test window: 10:00-12:00 UTC in 5-minute buckets (25 buckets).
    private static readonly DateTime T0 = new(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void BucketWidth_DoesNotDependOnTheSamplingCadence()
    {
        var window = AdminDashboardLoadBucketing.GetWindow(AdminDashboardLoadRange.Hours24, NowUtc);
        var fast = new AdminDashboardRawLoadAccumulator(window, samplingIntervalSeconds: 1);
        var slow = new AdminDashboardRawLoadAccumulator(window, samplingIntervalSeconds: 30);
        var fastProcess = Guid.NewGuid();
        var slowProcess = Guid.NewGuid();
        for (var second = 0; second < 3_600; second++)
        {
            fast.Add(Row(window.FromUtc.AddSeconds(second), fastProcess, processTicks: second * 1_000_000L));
            if (second % 30 == 0)
            {
                slow.Add(Row(window.FromUtc.AddSeconds(second), slowProcess, processTicks: second * 1_000_000L));
            }
        }

        var fastLoad = fast.Complete("24h", NowUtc);
        var slowLoad = slow.Complete("24h", NowUtc);

        Assert.Equal(300, fastLoad.BucketSeconds);
        Assert.Equal(fastLoad.BucketSeconds, slowLoad.BucketSeconds);
        Assert.Equal(fastLoad.BucketStartsUtc, slowLoad.BucketStartsUtc);
        Assert.Equal(289, fastLoad.BucketStartsUtc.Count);
        Assert.Equal(1, fastLoad.SamplingIntervalSeconds);
        Assert.Equal(30, slowLoad.SamplingIntervalSeconds);
        Assert.Equal(0.1, fastLoad.Api.ProcessCpuP95[0]);
        Assert.Equal(0.1, slowLoad.Api.ProcessCpuP95[0]);
    }

    // ---- raw path -------------------------------------------------------------------------------

    [Fact]
    public void Raw_PredecessorBeforeTheWindowGivesTheFirstPointARate_ANewInstanceHasNone()
    {
        var api = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var accumulator = RawAccumulator();

        accumulator.Add(Row(T0.AddSeconds(-20), api, processTicks: 0));
        accumulator.Add(Row(T0.AddSeconds(10), api, processTicks: 5_000_000));
        accumulator.Add(Row(T0.AddMinutes(4).AddSeconds(50), worker, worker: true, processTicks: 100_000_000));
        accumulator.Add(Row(T0.AddMinutes(5).AddSeconds(20), worker, worker: true, processTicks: 130_000_000));
        var load = accumulator.Complete("24h", NowUtc);

        Assert.Equal(T0, load.BucketStartsUtc[0]);
        Assert.Equal(0.0167, load.Api.ProcessCpuP95[0]);
        Assert.Equal(1, load.Api.SampleCount[0]);
        Assert.Null(load.Worker.ProcessCpuP95[0]);
        Assert.Equal(1, load.Worker.SampleCount[0]);
        Assert.Equal(0.1, load.Worker.ProcessCpuP95[1]);
    }

    [Fact]
    public void Raw_PairsAcrossOutagesAreDropped_PairsBeforeAndAfterStay()
    {
        var lonely = Guid.NewGuid();
        var gappy = Guid.NewGuid();
        var accumulator = RawAccumulator(samplingIntervalSeconds: 30);

        // Api: two samples 600 s apart; the instance's only pair is still not a pair.
        // Worker: valid pair (0.5 cores, bucket 0), 3,600 s gap (lands alone in bucket 12), 120 s gap
        // with a huge delta and a valid pair of 0.25 cores (both bucket 13). Rows go in time order.
        accumulator.Add(Row(T0.AddMinutes(1), lonely, processTicks: 0));
        accumulator.Add(Row(T0.AddSeconds(195), gappy, worker: true, processTicks: 0));
        accumulator.Add(Row(T0.AddSeconds(225), gappy, worker: true, processTicks: 150_000_000));
        accumulator.Add(Row(T0.AddMinutes(11), lonely, processTicks: 6_000_000_000));
        accumulator.Add(Row(T0.AddSeconds(3_825), gappy, worker: true, processTicks: 90_000_000_000));
        accumulator.Add(Row(T0.AddSeconds(3_945), gappy, worker: true, processTicks: 180_000_000_000));
        accumulator.Add(Row(T0.AddSeconds(3_975), gappy, worker: true, processTicks: 180_075_000_000));
        var load = accumulator.Complete("24h", NowUtc);

        Assert.All(load.Api.ProcessCpuP95, Assert.Null);
        Assert.Equal(2, load.Api.SampleCount.Sum());
        Assert.Equal(0.5, load.Worker.ProcessCpuP95[0]);
        for (var bucket = 1; bucket <= 12; bucket++)
        {
            Assert.Null(load.Worker.ProcessCpuP95[bucket]);
        }

        Assert.Equal(1, load.Worker.SampleCount[12]);
        Assert.Equal(0.25, load.Worker.ProcessCpuP95[13]);
        Assert.Equal(0.25, load.Worker.ProcessCpuMax[13]);
    }

    [Fact]
    public void Raw_PartialHostCoverage_ShowsTheApiPeakAfterTheWorkerStopped()
    {
        var worker = Guid.NewGuid();
        var api = Guid.NewGuid();
        var accumulator = RawAccumulator(samplingIntervalSeconds: 30);

        // Worker: five quiet minutes at 10 % busy, then gone.
        for (var i = 0; i < 10; i++)
        {
            accumulator.Add(Row(T0.AddSeconds(15 + 30 * i), worker, worker: true, busy: 3L * i, total: 30L * i, memTotal: 4_000 * Megabyte, memAvailable: 3_000 * Megabyte, load1: 0.2));
        }

        // Api from minute 5 on: a 90 % peak.
        for (var i = 0; i < 10; i++)
        {
            accumulator.Add(Row(T0.AddMinutes(5).AddSeconds(15 + 30 * i), api, busy: 1_000 + 27L * i, total: 2_000 + 30L * i, memTotal: 4_000 * Megabyte, memAvailable: 1_000 * Megabyte, load1: 3.5));
        }

        var load = accumulator.Complete("24h", NowUtc);

        Assert.Equal(10.0, load.Host.CpuBusyP95[0]);
        Assert.Equal("W", load.Host.Provenance[0]);
        Assert.Equal("W", load.Host.CpuValueSource[0]);
        Assert.Equal(90.0, load.Host.CpuBusyP95[1]);
        Assert.Equal(90.0, load.Host.CpuBusyMax[1]);
        Assert.Equal("A", load.Host.Provenance[1]);
        Assert.Equal("A", load.Host.CpuValueSource[1]);
        Assert.Equal("A", load.Host.MemoryValueSource[1]);
        Assert.Equal("A", load.Host.LoadAverageValueSource[1]);
        Assert.Equal(3_000 * Megabyte, load.Host.MemoryUsedP95Bytes[1]);
        Assert.Equal(3.5, load.Host.LoadAverage1Max[1]);
        Assert.Equal(0, load.Host.WorkerSampleCount[1]);
        Assert.Equal(10, load.Host.ApiSampleCount[1]);
        Assert.Equal(90.0, load.Host.ApiCpuBusyMean[1]);
        Assert.Null(load.Host.WorkerCpuBusyMean[1]);
    }

    [Fact]
    public void Raw_HostObservationsOfBothSourcesAreUnited_NeverSummed()
    {
        var worker = Guid.NewGuid();
        var api = Guid.NewGuid();
        var accumulator = RawAccumulator(samplingIntervalSeconds: 30);
        for (var i = 0; i < 10; i++)
        {
            var at = T0.AddSeconds(15 + 30 * i);
            accumulator.Add(Row(at, api, busy: 6L * i, total: 30L * i, memTotal: 4_000 * Megabyte, memAvailable: 3_000 * Megabyte, load1: 1.0));
            accumulator.Add(Row(at, worker, worker: true, busy: 9L * i, total: 30L * i, memTotal: 4_000 * Megabyte, memAvailable: 2_500 * Megabyte, load1: 1.25));
        }

        var load = accumulator.Complete("24h", NowUtc);

        Assert.Equal("AW", load.Host.Provenance[0]);
        Assert.Equal(30.0, load.Host.CpuBusyP95[0]);
        Assert.Equal(30.0, load.Host.CpuBusyMax[0]);
        Assert.Equal("W", load.Host.CpuValueSource[0]);
        Assert.Equal(1_500 * Megabyte, load.Host.MemoryUsedMaxBytes[0]);
        Assert.Equal(1.25, load.Host.LoadAverage1Max[0]);
        Assert.Equal(20.0, load.Host.ApiCpuBusyMean[0]);
        Assert.Equal(30.0, load.Host.WorkerCpuBusyMean[0]);
        Assert.Equal(1_000 * Megabyte, load.Host.ApiMemoryUsedMeanBytes[0]);
        Assert.Equal(1_500 * Megabyte, load.Host.WorkerMemoryUsedMeanBytes[0]);
        Assert.Equal(4_000 * Megabyte, load.Host.MemoryTotalBytes[0]);
    }

    [Fact]
    public void Raw_ValueSourceTie_HigherMaxWins_ThenWorker()
    {
        var api = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var accumulator = RawAccumulator();

        // Bucket 0: both sources at 1.0 only -> full tie -> W.
        accumulator.Add(Row(T0.AddSeconds(1), api, load1: 1.0));
        accumulator.Add(Row(T0.AddSeconds(2), worker, worker: true, load1: 1.0));
        accumulator.Add(Row(T0.AddSeconds(3), api, load1: 1.0));
        accumulator.Add(Row(T0.AddSeconds(4), worker, worker: true, load1: 1.0));

        // Bucket 1: 20 x 1.0 per source plus one Api 3.0 -> P95 is 1.0 from both, Api has the higher max.
        for (var i = 0; i < 20; i++)
        {
            accumulator.Add(Row(T0.AddMinutes(5).AddSeconds(i * 2), api, load1: 1.0));
            accumulator.Add(Row(T0.AddMinutes(5).AddSeconds(i * 2 + 1), worker, worker: true, load1: 1.0));
        }

        accumulator.Add(Row(T0.AddMinutes(6), api, load1: 3.0));
        var load = accumulator.Complete("24h", NowUtc);

        Assert.Equal(1.0, load.Host.LoadAverage1P95[0]);
        Assert.Equal("W", load.Host.LoadAverageValueSource[0]);
        Assert.Equal(1.0, load.Host.LoadAverage1P95[1]);
        Assert.Equal(3.0, load.Host.LoadAverage1Max[1]);
        Assert.Equal("A", load.Host.LoadAverageValueSource[1]);
    }

    [Fact]
    public void Raw_QuotaAndMemoryShares_OnlyAgainstTheSameFiniteLimit()
    {
        var api = Guid.NewGuid();
        var accumulator = RawAccumulator(samplingIntervalSeconds: 30);

        // Bucket 0: finite 1-core quota on both ends -> 0.5 cores = 50 %; memory 100 of 400 MB = 25 %.
        accumulator.Add(Row(T0.AddSeconds(15), api, cgroupMicros: 0, quota: 100_000, period: 100_000, memCurrent: 100 * Megabyte, memMax: 400 * Megabyte));
        accumulator.Add(Row(T0.AddSeconds(45), api, cgroupMicros: 15_000_000, quota: 100_000, period: 100_000, memCurrent: 100 * Megabyte, memMax: 400 * Megabyte));

        // Bucket 1: quota changes inside the pair (1 -> 2 cores): cores yes, share no; real zero memory, no finite limit.
        accumulator.Add(Row(T0.AddMinutes(5).AddSeconds(15), api, cgroupMicros: 100_000_000, quota: 100_000, period: 100_000, memCurrent: 0));
        accumulator.Add(Row(T0.AddMinutes(5).AddSeconds(45), api, cgroupMicros: 115_000_000, quota: 200_000, period: 100_000, memCurrent: 0));

        // Bucket 2: cpu.max = max on both ends (null quota): cores only; memory current unknown.
        accumulator.Add(Row(T0.AddMinutes(10).AddSeconds(15), api, cgroupMicros: 200_000_000, memMax: 400 * Megabyte));
        accumulator.Add(Row(T0.AddMinutes(10).AddSeconds(45), api, cgroupMicros: 230_000_000, memMax: 400 * Megabyte));
        var load = accumulator.Complete("24h", NowUtc);

        Assert.Equal(0.5, load.Api.CgroupCpuP95[0]);
        Assert.Equal(1.0, load.Api.CgroupCpuQuotaCores[0]);
        Assert.Equal(50.0, load.Api.CgroupCpuQuotaShareP95[0]);
        Assert.Equal(50.0, load.Api.CgroupCpuQuotaShareMax[0]);
        Assert.Equal(100 * Megabyte, load.Api.CgroupMemoryP95Bytes[0]);
        Assert.Equal(400 * Megabyte, load.Api.CgroupMemoryLimitBytes[0]);
        Assert.Equal(25.0, load.Api.CgroupMemoryShareP95[0]);

        Assert.Equal(0.5, load.Api.CgroupCpuP95[1]);
        Assert.Equal(2.0, load.Api.CgroupCpuQuotaCores[1]);
        Assert.Null(load.Api.CgroupCpuQuotaShareP95[1]);
        Assert.Equal(0L, load.Api.CgroupMemoryP95Bytes[1]);
        Assert.Null(load.Api.CgroupMemoryLimitBytes[1]);
        Assert.Null(load.Api.CgroupMemoryShareP95[1]);

        Assert.Equal(1.0, load.Api.CgroupCpuP95[2]);
        Assert.Null(load.Api.CgroupCpuQuotaCores[2]);
        Assert.Null(load.Api.CgroupCpuQuotaShareP95[2]);
        Assert.Null(load.Api.CgroupMemoryP95Bytes[2]);
        Assert.Null(load.Api.CgroupMemoryShareP95[2]);
        Assert.Equal(400 * Megabyte, load.Api.CgroupMemoryLimitBytes[2]);
    }

    [Fact]
    public void Raw_InstanceChangeCounterResetAndHostCounterDrop_DropThePair()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var accumulator = RawAccumulator(samplingIntervalSeconds: 30);

        // Bucket 0: new ProcessInstanceId with a higher counter -> no pair at all.
        accumulator.Add(Row(T0.AddSeconds(15), first, processTicks: 1_000_000_000, busy: 100, total: 1_000));
        accumulator.Add(Row(T0.AddSeconds(45), second, processTicks: 2_000_000_000, busy: 110, total: 1_100));

        // Bucket 1: process counter reset -> no process rate; host pair stays valid (10 %).
        accumulator.Add(Row(T0.AddMinutes(5), third, processTicks: 5_000_000_000, busy: 1_000, total: 2_000));
        accumulator.Add(Row(T0.AddMinutes(5).AddSeconds(30), third, processTicks: 1_000_000_000, busy: 1_010, total: 2_100));

        // Bucket 2: host counters fall (host reboot) -> no host rate; process pair stays valid (1 core).
        accumulator.Add(Row(T0.AddMinutes(10), third, processTicks: 1_000_000_000, busy: 5_000, total: 9_000));
        accumulator.Add(Row(T0.AddMinutes(10).AddSeconds(30), third, processTicks: 1_300_000_000, busy: 5, total: 50));
        var load = accumulator.Complete("24h", NowUtc);

        Assert.Null(load.Api.ProcessCpuP95[0]);
        Assert.Null(load.Host.CpuBusyP95[0]);
        Assert.Null(load.Api.ProcessCpuP95[1]);
        Assert.Equal(10.0, load.Host.CpuBusyP95[1]);
        Assert.Equal(1.0, load.Api.ProcessCpuP95[2]);
        Assert.Null(load.Host.CpuBusyP95[2]);
    }

    [Fact]
    public void Raw_Workload_IsCountersPerActualIntervalMinute_WithoutExtrapolation()
    {
        var worker = Guid.NewGuid();
        var api = Guid.NewGuid();
        var accumulator = RawAccumulator(samplingIntervalSeconds: 30);

        // First sample of an instance: its interval is unknown, so its counter does not enter the rate.
        accumulator.Add(Row(T0.AddSeconds(15), worker, worker: true, chat: 999, emotes: 999, active: 4, live: 1));
        accumulator.Add(Row(T0.AddSeconds(45), worker, worker: true, previous: T0.AddSeconds(15), chat: 30, emotes: 3, active: 5, live: 2));
        accumulator.Add(Row(T0.AddSeconds(75), worker, worker: true, previous: T0.AddSeconds(45), chat: 60, emotes: 6, active: 5));
        accumulator.Add(Row(T0.AddSeconds(80), api, active: 99));

        // Interval 4:40-5:10 crosses the bucket boundary: it belongs to the bucket of its sample.
        accumulator.Add(Row(T0.AddMinutes(5).AddSeconds(10), worker, worker: true, previous: T0.AddMinutes(4).AddSeconds(40), chat: 15, emotes: 0));
        var load = accumulator.Complete("24h", NowUtc);

        Assert.Equal("perMinute", load.Workload.WorkloadUnit);
        Assert.Equal(90.0, load.Workload.ChatLines[0]);
        Assert.Equal(9.0, load.Workload.EmoteMatches[0]);
        Assert.Equal(60.0, load.Workload.WorkloadIntervalSeconds[0]);
        Assert.Equal(5, load.Workload.ActiveChannelsMax[0]);
        Assert.Equal(2, load.Workload.LiveChannelsMax[0]);

        Assert.Equal(30.0, load.Workload.ChatLines[1]);
        Assert.Equal(0.0, load.Workload.EmoteMatches[1]);
        Assert.Equal(30.0, load.Workload.WorkloadIntervalSeconds[1]);
        Assert.Null(load.Workload.LiveChannelsMax[1]);
        Assert.All(load.Workload.WorkloadHoursWithData, Assert.Null);
    }

    [Fact]
    public void Raw_LeadingEmptyBucketsAreLeftOut_InnerAndTrailingGapsStayNull()
    {
        var api = Guid.NewGuid();
        var accumulator = RawAccumulator(samplingIntervalSeconds: 30);
        accumulator.Add(Row(T0.AddMinutes(-2), api, processTicks: 0));
        accumulator.Add(Row(T0.AddMinutes(15).AddSeconds(5), api, memCurrent: 5));
        accumulator.Add(Row(T0.AddMinutes(25).AddSeconds(5), api, memCurrent: 7, swapTotal: 1_000, swapFree: 400));
        var load = accumulator.Complete("24h", NowUtc);

        Assert.True(load.Available);
        Assert.Equal("raw", load.SourceKind);
        Assert.Equal(T0, load.FromUtc);
        Assert.Equal(T0.AddMinutes(15), load.BucketStartsUtc[0]);
        Assert.Equal(25 - 3, load.BucketStartsUtc.Count);
        Assert.Equal(T0.AddMinutes(25).AddSeconds(5), load.DataThroughUtc);
        Assert.Equal(5L, load.Api.CgroupMemoryP95Bytes[0]);
        Assert.Null(load.Api.CgroupMemoryP95Bytes[1]);
        Assert.Equal(7L, load.Api.CgroupMemoryP95Bytes[2]);
        Assert.Null(load.Api.CgroupMemoryP95Bytes[^1]);
        AssertAllListsHaveTheBucketCount(load);
        Assert.Equal(new AdminDashboardSwap(600, 1_000, T0.AddMinutes(25).AddSeconds(5)), load.Swap);
    }

    [Fact]
    public void Raw_NoObservationInTheWindow_IsNotAvailable()
    {
        var accumulator = RawAccumulator();
        accumulator.Add(Row(T0.AddSeconds(-10), Guid.NewGuid(), processTicks: 0));

        var load = accumulator.Complete("24h", NowUtc);

        Assert.False(load.Available);
        Assert.Empty(load.BucketStartsUtc);
        Assert.Null(load.DataThroughUtc);
        Assert.Null(load.Swap);
        AssertAllListsHaveTheBucketCount(load);
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

    private static AdminDashboardRawLoadAccumulator RawAccumulator(int samplingIntervalSeconds = 30) =>
        new(AdminDashboardLoadBucketing.CreateWindow(T0, T0.AddHours(2), 300), samplingIntervalSeconds);

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

    private static void AssertAllListsHaveTheBucketCount(AdminDashboardLoad load)
    {
        var count = load.BucketStartsUtc.Count;
        foreach (var series in new object[] { load.Host, load.Api, load.Worker, load.Workload })
        {
            foreach (var property in series.GetType().GetProperties())
            {
                if (property.GetValue(series) is System.Collections.ICollection list)
                {
                    Assert.True(list.Count == count, $"{series.GetType().Name}.{property.Name} has {list.Count} entries, expected {count}.");
                }
            }
        }
    }

    private static AdminDashboardLoadSampleRow Row(
        DateTime atUtc,
        Guid processInstanceId,
        bool worker = false,
        long? processTicks = null,
        long? cgroupMicros = null,
        long? busy = null,
        long? total = null,
        long? quota = null,
        long? period = null,
        long? memCurrent = null,
        long? memMax = null,
        double? load1 = null,
        long? memTotal = null,
        long? memAvailable = null,
        DateTime? previous = null,
        long? chat = null,
        long? emotes = null,
        int? active = null,
        int? live = null,
        long? swapTotal = null,
        long? swapFree = null) => new()
        {
            SampledAtUtc = atUtc,
            IsWorker = worker,
            ProcessInstanceId = processInstanceId,
            PreviousSampledAtUtc = previous,
            ProcessCpuTimeTicks = processTicks,
            CgroupCpuUsageMicroseconds = cgroupMicros,
            CgroupCpuQuotaMicroseconds = quota,
            CgroupCpuPeriodMicroseconds = period,
            CgroupMemoryCurrentBytes = memCurrent,
            CgroupMemoryMaxBytes = memMax,
            HostCpuBusyJiffies = busy,
            HostCpuTotalJiffies = total,
            HostLoadAverage1 = load1,
            HostMemoryTotalBytes = memTotal,
            HostMemoryAvailableBytes = memAvailable,
            HostSwapTotalBytes = swapTotal,
            HostSwapFreeBytes = swapFree,
            ActiveChannels = active,
            LiveChannels = live,
            ChatLines = chat,
            EmoteMatches = emotes,
        };

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
