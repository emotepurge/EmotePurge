using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Raw-path semantics of the admin dashboard load range (#381, spec 5.1/5.2, section 11 B) through the
// real read: the typed reader of AdminDashboardRawLoadQuery feeding AdminDashboardRawLoadAccumulator,
// against Postgres. Covers predecessor, gap rule, outages, host union and value source with its tie
// rule, quota and memory shares, resets, workload per minute, trimming and swap. Each case seeds its
// samples and reads a 10:00-12:00 UTC window in 5-minute buckets (25 buckets).
[Collection("Postgres")]
public sealed class AdminDashboardRawLoadReaderTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "admin_dashboard_raw_load_reader_tests";
    private const long Megabyte = 1_000_000;
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static readonly DateTime NowUtc = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T0 = new(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);
    private static bool _databaseReady;

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var db = fixture.CreateDbContext(DatabaseName);
                await db.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await TruncateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BucketWidth_DoesNotDependOnTheSamplingCadence()
    {
        var window = AdminDashboardLoadBucketing.GetWindow(AdminDashboardLoadRange.Hours24, NowUtc);
        var fast = new List<SystemLoadSample>(3_600);
        var slow = new List<SystemLoadSample>(120);
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

        await SeedAsync(fast);
        var fastLoad = await ReadAsync(window, samplingIntervalSeconds: 1);
        await TruncateAsync();
        await SeedAsync(slow);
        var slowLoad = await ReadAsync(window, samplingIntervalSeconds: 30);

        Assert.Equal(300, fastLoad.BucketSeconds);
        Assert.Equal(fastLoad.BucketSeconds, slowLoad.BucketSeconds);
        Assert.Equal(fastLoad.BucketStartsUtc, slowLoad.BucketStartsUtc);
        Assert.Equal(289, fastLoad.BucketStartsUtc.Count);
        Assert.Equal(1, fastLoad.SamplingIntervalSeconds);
        Assert.Equal(30, slowLoad.SamplingIntervalSeconds);
        Assert.Equal(0.1, fastLoad.Api.ProcessCpuP95[0]);
        Assert.Equal(0.1, slowLoad.Api.ProcessCpuP95[0]);
    }

    [Fact]
    public async Task Raw_PairsAcrossOutagesAreDropped_PairsBeforeAndAfterStay()
    {
        var lonely = Guid.NewGuid();
        var gappy = Guid.NewGuid();
        var run = new RawRun(samplingIntervalSeconds: 30);

        // Api: two samples 600 s apart; the instance's only pair is still not a pair.
        // Worker: valid pair (0.5 cores, bucket 0), 3,600 s gap (lands alone in bucket 12), 120 s gap
        // with a huge delta and a valid pair of 0.25 cores (both bucket 13). Rows go in time order.
        run.Add(Row(T0.AddMinutes(1), lonely, processTicks: 0));
        run.Add(Row(T0.AddSeconds(195), gappy, worker: true, processTicks: 0));
        run.Add(Row(T0.AddSeconds(225), gappy, worker: true, processTicks: 150_000_000));
        run.Add(Row(T0.AddMinutes(11), lonely, processTicks: 6_000_000_000));
        run.Add(Row(T0.AddSeconds(3_825), gappy, worker: true, processTicks: 90_000_000_000));
        run.Add(Row(T0.AddSeconds(3_945), gappy, worker: true, processTicks: 180_000_000_000));
        run.Add(Row(T0.AddSeconds(3_975), gappy, worker: true, processTicks: 180_075_000_000));
        var load = await LoadAsync(run);

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
    public async Task Raw_PartialHostCoverage_ShowsTheApiPeakAfterTheWorkerStopped()
    {
        var worker = Guid.NewGuid();
        var api = Guid.NewGuid();
        var run = new RawRun(samplingIntervalSeconds: 30);

        // Worker: five quiet minutes at 10 % busy, then gone.
        for (var i = 0; i < 10; i++)
        {
            run.Add(Row(T0.AddSeconds(15 + 30 * i), worker, worker: true, busy: 3L * i, total: 30L * i, memTotal: 4_000 * Megabyte, memAvailable: 3_000 * Megabyte, load1: 0.2));
        }

        // Api from minute 5 on: a 90 % peak.
        for (var i = 0; i < 10; i++)
        {
            run.Add(Row(T0.AddMinutes(5).AddSeconds(15 + 30 * i), api, busy: 1_000 + 27L * i, total: 2_000 + 30L * i, memTotal: 4_000 * Megabyte, memAvailable: 1_000 * Megabyte, load1: 3.5));
        }

        var load = await LoadAsync(run);

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
    public async Task Raw_HostObservationsOfBothSourcesAreUnited_NeverSummed()
    {
        var worker = Guid.NewGuid();
        var api = Guid.NewGuid();
        var run = new RawRun(samplingIntervalSeconds: 30);
        for (var i = 0; i < 10; i++)
        {
            var at = T0.AddSeconds(15 + 30 * i);
            run.Add(Row(at, api, busy: 6L * i, total: 30L * i, memTotal: 4_000 * Megabyte, memAvailable: 3_000 * Megabyte, load1: 1.0));
            run.Add(Row(at, worker, worker: true, busy: 9L * i, total: 30L * i, memTotal: 4_000 * Megabyte, memAvailable: 2_500 * Megabyte, load1: 1.25));
        }

        var load = await LoadAsync(run);

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
    public async Task Raw_ValueSourceTie_HigherMaxWins_ThenWorker()
    {
        var api = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var run = new RawRun();

        // Bucket 0: both sources at 1.0 only -> full tie -> W.
        run.Add(Row(T0.AddSeconds(1), api, load1: 1.0));
        run.Add(Row(T0.AddSeconds(2), worker, worker: true, load1: 1.0));
        run.Add(Row(T0.AddSeconds(3), api, load1: 1.0));
        run.Add(Row(T0.AddSeconds(4), worker, worker: true, load1: 1.0));

        // Bucket 1: 20 x 1.0 per source plus one Api 3.0 -> P95 is 1.0 from both, Api has the higher max.
        for (var i = 0; i < 20; i++)
        {
            run.Add(Row(T0.AddMinutes(5).AddSeconds(i * 2), api, load1: 1.0));
            run.Add(Row(T0.AddMinutes(5).AddSeconds(i * 2 + 1), worker, worker: true, load1: 1.0));
        }

        run.Add(Row(T0.AddMinutes(6), api, load1: 3.0));
        var load = await LoadAsync(run);

        Assert.Equal(1.0, load.Host.LoadAverage1P95[0]);
        Assert.Equal("W", load.Host.LoadAverageValueSource[0]);
        Assert.Equal(1.0, load.Host.LoadAverage1P95[1]);
        Assert.Equal(3.0, load.Host.LoadAverage1Max[1]);
        Assert.Equal("A", load.Host.LoadAverageValueSource[1]);
    }

    [Fact]
    public async Task Raw_QuotaAndMemoryShares_OnlyAgainstTheSameFiniteLimit()
    {
        var api = Guid.NewGuid();
        var run = new RawRun(samplingIntervalSeconds: 30);

        // Bucket 0: finite 1-core quota on both ends -> 0.5 cores = 50 %; memory 100 of 400 MB = 25 %.
        run.Add(Row(T0.AddSeconds(15), api, cgroupMicros: 0, quota: 100_000, period: 100_000, memCurrent: 100 * Megabyte, memMax: 400 * Megabyte));
        run.Add(Row(T0.AddSeconds(45), api, cgroupMicros: 15_000_000, quota: 100_000, period: 100_000, memCurrent: 100 * Megabyte, memMax: 400 * Megabyte));

        // Bucket 1: quota changes inside the pair (1 -> 2 cores): cores yes, share no; real zero memory, no finite limit.
        run.Add(Row(T0.AddMinutes(5).AddSeconds(15), api, cgroupMicros: 100_000_000, quota: 100_000, period: 100_000, memCurrent: 0));
        run.Add(Row(T0.AddMinutes(5).AddSeconds(45), api, cgroupMicros: 115_000_000, quota: 200_000, period: 100_000, memCurrent: 0));

        // Bucket 2: cpu.max = max on both ends (null quota): cores only; memory current unknown.
        run.Add(Row(T0.AddMinutes(10).AddSeconds(15), api, cgroupMicros: 200_000_000, memMax: 400 * Megabyte));
        run.Add(Row(T0.AddMinutes(10).AddSeconds(45), api, cgroupMicros: 230_000_000, memMax: 400 * Megabyte));
        var load = await LoadAsync(run);

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
    public async Task Raw_InstanceChangeCounterResetAndHostCounterDrop_DropThePair()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var run = new RawRun(samplingIntervalSeconds: 30);

        // Bucket 0: new ProcessInstanceId with a higher counter -> no pair at all.
        run.Add(Row(T0.AddSeconds(15), first, processTicks: 1_000_000_000, busy: 100, total: 1_000));
        run.Add(Row(T0.AddSeconds(45), second, processTicks: 2_000_000_000, busy: 110, total: 1_100));

        // Bucket 1: process counter reset -> no process rate; host pair stays valid (10 %).
        run.Add(Row(T0.AddMinutes(5), third, processTicks: 5_000_000_000, busy: 1_000, total: 2_000));
        run.Add(Row(T0.AddMinutes(5).AddSeconds(30), third, processTicks: 1_000_000_000, busy: 1_010, total: 2_100));

        // Bucket 2: host counters fall (host reboot) -> no host rate; process pair stays valid (1 core).
        run.Add(Row(T0.AddMinutes(10), third, processTicks: 1_000_000_000, busy: 5_000, total: 9_000));
        run.Add(Row(T0.AddMinutes(10).AddSeconds(30), third, processTicks: 1_300_000_000, busy: 5, total: 50));
        var load = await LoadAsync(run);

        Assert.Null(load.Api.ProcessCpuP95[0]);
        Assert.Null(load.Host.CpuBusyP95[0]);
        Assert.Null(load.Api.ProcessCpuP95[1]);
        Assert.Equal(10.0, load.Host.CpuBusyP95[1]);
        Assert.Equal(1.0, load.Api.ProcessCpuP95[2]);
        Assert.Null(load.Host.CpuBusyP95[2]);
    }

    [Fact]
    public async Task Raw_Workload_IsCountersPerActualIntervalMinute_WithoutExtrapolation()
    {
        var worker = Guid.NewGuid();
        var api = Guid.NewGuid();
        var run = new RawRun(samplingIntervalSeconds: 30);

        // First sample of an instance: its interval is unknown, so its counter does not enter the rate.
        run.Add(Row(T0.AddSeconds(15), worker, worker: true, chat: 999, emotes: 999, active: 4, live: 1));
        run.Add(Row(T0.AddSeconds(45), worker, worker: true, previous: T0.AddSeconds(15), chat: 30, emotes: 3, active: 5, live: 2));
        run.Add(Row(T0.AddSeconds(75), worker, worker: true, previous: T0.AddSeconds(45), chat: 60, emotes: 6, active: 5));
        run.Add(Row(T0.AddSeconds(80), api, active: 99));

        // Interval 4:40-5:10 crosses the bucket boundary: it belongs to the bucket of its sample.
        run.Add(Row(T0.AddMinutes(5).AddSeconds(10), worker, worker: true, previous: T0.AddMinutes(4).AddSeconds(40), chat: 15, emotes: 0));
        var load = await LoadAsync(run);

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
    public async Task Raw_LeadingEmptyBucketsAreLeftOut_InnerAndTrailingGapsStayNull()
    {
        var api = Guid.NewGuid();
        var run = new RawRun(samplingIntervalSeconds: 30);
        run.Add(Row(T0.AddMinutes(-2), api, processTicks: 0));
        run.Add(Row(T0.AddMinutes(15).AddSeconds(5), api, memCurrent: 5));
        run.Add(Row(T0.AddMinutes(25).AddSeconds(5), api, memCurrent: 7, swapTotal: 1_000, swapFree: 400));
        var load = await LoadAsync(run);

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
    public async Task Raw_NoObservationInTheWindow_IsNotAvailable()
    {
        var run = new RawRun();
        run.Add(Row(T0.AddSeconds(-10), Guid.NewGuid(), processTicks: 0));

        var load = await LoadAsync(run);

        Assert.False(load.Available);
        Assert.Empty(load.BucketStartsUtc);
        Assert.Null(load.DataThroughUtc);
        Assert.Null(load.Swap);
        AssertAllListsHaveTheBucketCount(load);
    }

    // A minority of Api pairs at 90 % reaches the union P95 against a quiet Worker majority at 10 %:
    // 19 + 2 = 21 values, nearest rank ceil(0.95 x 21) = 20 is the first 0.9.
    [Fact]
    public async Task Raw_MinorityApiPeakBeatsAQuietWorkerMajority()
    {
        var worker = Guid.NewGuid();
        var api = Guid.NewGuid();
        var run = new RawRun(1);
        for (var i = 0; i < 20; i++)
        {
            run.Add(Row(T0.AddSeconds(10 + i), worker, worker: true, busy: 10L * i, total: 100L * i));
        }

        for (var i = 0; i < 3; i++)
        {
            run.Add(Row(T0.AddSeconds(100 + i), api, busy: 1_000 + 90L * i, total: 5_000 + 100L * i));
        }

        var load = await LoadAsync(run);

        Assert.Equal("AW", load.Host.Provenance[0]);
        Assert.Equal(90.0, load.Host.CpuBusyP95[0]);
        Assert.Equal(90.0, load.Host.CpuBusyMax[0]);
        Assert.Equal("A", load.Host.CpuValueSource[0]);
        Assert.Equal(20, load.Host.WorkerSampleCount[0]);
        Assert.Equal(3, load.Host.ApiSampleCount[0]);
        Assert.Equal(10.0, load.Host.WorkerCpuBusyMean[0]);
        Assert.Equal(90.0, load.Host.ApiCpuBusyMean[0]);
    }

    // Swap is the last known value (spec 5.1): an unknown or inconsistent reading keeps it, an explicit
    // SwapTotal of 0 means "now none", and a later valid reading replaces it.
    [Fact]
    public async Task Raw_Swap_IsTheLastKnownValue()
    {
        var api = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var run = new RawRun(30);
        run.Add(Row(T0.AddMinutes(1), api, swapTotal: 1_000, swapFree: 400));
        run.Add(Row(T0.AddMinutes(2), worker, worker: true));
        run.Add(Row(T0.AddMinutes(3), api, swapTotal: 1_000, swapFree: 2_000));

        var kept = await LoadAsync(run);
        Assert.Equal(new AdminDashboardSwap(600, 1_000, T0.AddMinutes(1)), kept.Swap);

        await TruncateAsync();
        run.Add(Row(T0.AddMinutes(4), worker, worker: true, swapTotal: 0, swapFree: 0));
        var none = await LoadAsync(run);
        Assert.Null(none.Swap);

        await TruncateAsync();
        run.Add(Row(T0.AddMinutes(5), api, swapTotal: 2_000, swapFree: 500));
        var latest = await LoadAsync(run);
        Assert.Equal(new AdminDashboardSwap(1_500, 2_000, T0.AddMinutes(5)), latest.Swap);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<AdminDashboardLoad> LoadAsync(RawRun run)
    {
        await SeedAsync(run.Samples);
        return await ReadAsync(AdminDashboardLoadBucketing.CreateWindow(T0, T0.AddHours(2), 300), run.SamplingIntervalSeconds);
    }

    private async Task<AdminDashboardLoad> ReadAsync(AdminDashboardLoadWindow window, int samplingIntervalSeconds)
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await new AdminDashboardQueryService(db).GetRawLoadAsync(window, "24h", NowUtc, samplingIntervalSeconds, CancellationToken.None);
    }

    private async Task SeedAsync(IEnumerable<SystemLoadSample> samples)
    {
        await using var seed = fixture.CreateDbContext(DatabaseName);
        seed.ChangeTracker.AutoDetectChangesEnabled = false;

        // Fresh entities each time, so a run can be seeded again after a truncate.
        seed.SystemLoadSamples.AddRange(samples.Select(static sample => Copy(sample)));
        await seed.SaveChangesAsync();
    }

    private async Task TruncateAsync()
    {
        await using AppDbContext reset = fixture.CreateDbContext(DatabaseName);
        await reset.Database.ExecuteSqlRawAsync("TRUNCATE \"SystemLoadSamples\", \"SystemLoadHourly\";");
    }

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

    private static SystemLoadSample Row(
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
            Source = worker ? SystemLoadSource.Worker : SystemLoadSource.Api,
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

    private static SystemLoadSample Copy(SystemLoadSample sample) => new()
    {
        SampledAtUtc = sample.SampledAtUtc,
        Source = sample.Source,
        ProcessInstanceId = sample.ProcessInstanceId,
        PreviousSampledAtUtc = sample.PreviousSampledAtUtc,
        ProcessCpuTimeTicks = sample.ProcessCpuTimeTicks,
        CgroupCpuUsageMicroseconds = sample.CgroupCpuUsageMicroseconds,
        CgroupCpuQuotaMicroseconds = sample.CgroupCpuQuotaMicroseconds,
        CgroupCpuPeriodMicroseconds = sample.CgroupCpuPeriodMicroseconds,
        CgroupMemoryCurrentBytes = sample.CgroupMemoryCurrentBytes,
        CgroupMemoryMaxBytes = sample.CgroupMemoryMaxBytes,
        HostCpuBusyJiffies = sample.HostCpuBusyJiffies,
        HostCpuTotalJiffies = sample.HostCpuTotalJiffies,
        HostLoadAverage1 = sample.HostLoadAverage1,
        HostMemoryTotalBytes = sample.HostMemoryTotalBytes,
        HostMemoryAvailableBytes = sample.HostMemoryAvailableBytes,
        HostSwapTotalBytes = sample.HostSwapTotalBytes,
        HostSwapFreeBytes = sample.HostSwapFreeBytes,
        ActiveChannels = sample.ActiveChannels,
        LiveChannels = sample.LiveChannels,
        ChatLines = sample.ChatLines,
        EmoteMatches = sample.EmoteMatches,
    };

    // The samples of one case and the interval it is read with.
    private sealed class RawRun(int samplingIntervalSeconds = 30)
    {
        public int SamplingIntervalSeconds { get; } = samplingIntervalSeconds;
        public List<SystemLoadSample> Samples { get; } = [];

        public void Add(SystemLoadSample sample) => Samples.Add(sample);
    }
}
