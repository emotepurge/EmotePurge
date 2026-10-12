using System.Data.Common;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The SQL side of the admin dashboard load range (#381, spec section 11 B): predecessor loading per
// (Source, ProcessInstanceId), the raw/hourly boundary, raw path = rollup for the same hour, the
// cadence change and the window edges against real Postgres. Pure bucket semantics are covered in
// AdminDashboardLoadBucketingTests.
[Collection("Postgres")]
public sealed class AdminDashboardLoadQueryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "admin_dashboard_load_tests";
    private const long Megabyte = 1_000_000;
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static readonly DateTime NowUtc = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
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

        await using var reset = CreateDbContext();
        await reset.Database.ExecuteSqlRawAsync("TRUNCATE \"SystemLoadSamples\", \"SystemLoadHourly\";");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Raw_LoadsOnePredecessorPerSourceAndInstance_ANewInstanceHasNone()
    {
        var fromUtc = NowUtc.AddHours(-24);
        var api = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var otherWorker = Guid.NewGuid();
        await SeedAsync(
            // Older Api history: if this were taken as the predecessor, the gap would drop the pair.
            Sample(SystemLoadSource.Api, api, fromUtc.AddMinutes(-10), processTicks: 0),
            Sample(SystemLoadSource.Api, api, fromUtc.AddSeconds(-20), processTicks: 1_000_000_000),
            Sample(SystemLoadSource.Api, api, fromUtc.AddSeconds(10), processTicks: 1_005_000_000),

            // A Worker instance with a predecessor right before the window ...
            Sample(SystemLoadSource.Worker, otherWorker, fromUtc.AddSeconds(-5), processTicks: 0),
            Sample(SystemLoadSource.Worker, otherWorker, fromUtc.AddSeconds(25), processTicks: 60_000_000),

            // ... and one that starts inside the window: its first point has no rate.
            Sample(SystemLoadSource.Worker, worker, fromUtc.AddMinutes(1), processTicks: 500_000_000),
            Sample(SystemLoadSource.Worker, worker, fromUtc.AddMinutes(5).AddSeconds(15), processTicks: 800_000_000),
            Sample(SystemLoadSource.Worker, worker, fromUtc.AddMinutes(5).AddSeconds(45), processTicks: 830_000_000));

        await using var db = CreateDbContext();
        var load = await new AdminDashboardQueryService(db).GetLoadAsync(AdminDashboardLoadRange.Hours24, NowUtc, 30);

        Assert.Equal(fromUtc, load.BucketStartsUtc[0]);
        Assert.Equal(0.0167, load.Api.ProcessCpuP95[0]);
        Assert.Equal(1, load.Api.SampleCount[0]);

        // Bucket 0 holds otherWorker's pair (0.2 cores) and worker's first, rate-less point.
        Assert.Equal(0.2, load.Worker.ProcessCpuP95[0]);
        Assert.Equal(0.2, load.Worker.ProcessCpuMax[0]);
        Assert.Equal(2, load.Worker.SampleCount[0]);

        // worker 01:00 -> 05:15 is a 255 s gap; 05:15 -> 05:45 is its first pair (0.1 cores).
        Assert.Equal(0.1, load.Worker.ProcessCpuP95[1]);
        Assert.Equal(0.1, load.Worker.ProcessCpuMax[1]);
    }

    // The raw read starts one maximum pair gap (2.5 x 30 s = 75 s) before the window instead of looking
    // up predecessors: a predecessor exactly 75 s before an instance's first window sample still pairs,
    // one 76 s before it would fail the gap rule anyway and is not needed.
    [Fact]
    public async Task Raw_PredecessorAtTheMaximumGapPairs_OneBeyondItDoesNot()
    {
        var fromUtc = NowUtc.AddHours(-24);
        var atGap = Guid.NewGuid();
        var beyondGap = Guid.NewGuid();
        await SeedAsync(
            Sample(SystemLoadSource.Api, atGap, fromUtc.AddSeconds(-75), processTicks: 0),
            Sample(SystemLoadSource.Api, atGap, fromUtc, processTicks: 150_000_000),
            Sample(SystemLoadSource.Worker, beyondGap, fromUtc.AddSeconds(-76), processTicks: 0),
            Sample(SystemLoadSource.Worker, beyondGap, fromUtc, processTicks: 150_000_000));

        await using var db = CreateDbContext();
        var load = await new AdminDashboardQueryService(db).GetLoadAsync(AdminDashboardLoadRange.Hours24, NowUtc, 30);

        Assert.Equal(fromUtc, load.BucketStartsUtc[0]);
        Assert.Equal(0.2, load.Api.ProcessCpuP95[0]);
        Assert.Null(load.Worker.ProcessCpuP95[0]);
        Assert.Equal(1, load.Api.SampleCount[0]);
        Assert.Equal(1, load.Worker.SampleCount[0]);
    }

    // Samples of the early-start stretch [from - 2.5 x interval, from) only seed the pairing: two of them
    // that form a valid pair with each other (here with a huge delta) show up in no bucket and change no
    // count, gauge or workload figure.
    [Fact]
    public async Task Raw_PairInsideTheEarlyStartStretchCountsNowhere()
    {
        var fromUtc = NowUtc.AddHours(-24);
        var worker = Guid.NewGuid();
        await SeedAsync(
            Full(worker, fromUtc.AddSeconds(-60), fromUtc.AddSeconds(-90), ticks: 0, busy: 0, total: 0, memory: 100 * Megabyte, chat: 1_000),
            Full(worker, fromUtc.AddSeconds(-30), fromUtc.AddSeconds(-60), ticks: 290_000_000_000, busy: 900, total: 1_000, memory: 9_000 * Megabyte, chat: 50_000),
            Full(worker, fromUtc, fromUtc.AddSeconds(-30), ticks: 290_030_000_000, busy: 930, total: 1_300, memory: 120 * Megabyte, chat: 30));

        await using var db = CreateDbContext();
        var load = await new AdminDashboardQueryService(db).GetLoadAsync(AdminDashboardLoadRange.Hours24, NowUtc, 30);

        Assert.Equal(fromUtc, load.BucketStartsUtc[0]);
        Assert.Equal(fromUtc, load.DataThroughUtc);
        Assert.Equal(1, load.Worker.SampleCount.Sum());
        Assert.Equal(0, load.Api.SampleCount.Sum());
        Assert.Equal(0.1, load.Worker.ProcessCpuP95[0]);
        Assert.Equal(0.1, load.Worker.ProcessCpuMax[0]);
        Assert.Equal(10.0, load.Host.CpuBusyP95[0]);
        Assert.Equal(10.0, load.Host.CpuBusyMax[0]);
        Assert.Equal(120 * Megabyte, load.Worker.CgroupMemoryMaxBytes[0]);
        Assert.Equal(60.0, load.Workload.ChatLines[0]);
        Assert.Equal(30.0, load.Workload.WorkloadIntervalSeconds[0]);
    }

    // Spec 5.1: raw path and rollup apply the same gap rule, so one hour read as a single raw bucket
    // equals SystemLoadRollupMath.Calculate for that hour, for every allowed cadence.
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(3_600)]
    public async Task Raw_OneHourBucketEqualsTheRollupOfThatHour(int intervalSeconds)
    {
        var hour = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
        var api = GenerateSeries(SystemLoadSource.Api, Guid.NewGuid(), hour, intervalSeconds, withHost: true);
        var worker = GenerateSeries(SystemLoadSource.Worker, Guid.NewGuid(), hour, intervalSeconds, withHost: false);
        await SeedAsync([.. api, .. worker]);

        var apiHourly = SystemLoadRollupMath.Calculate(api, SystemLoadSource.Api, hour, intervalSeconds);
        var workerHourly = SystemLoadRollupMath.Calculate(worker, SystemLoadSource.Worker, hour, intervalSeconds);
        var window = AdminDashboardLoadBucketing.CreateWindow(hour, hour.AddHours(1).AddTicks(-1), 3_600);
        await using var db = CreateDbContext();
        var load = await new AdminDashboardQueryService(db).GetRawLoadAsync(window, "test", NowUtc, intervalSeconds, CancellationToken.None);

        Assert.True(load.Available);
        Assert.Single(load.BucketStartsUtc);
        Assert.NotNull(apiHourly.ProcessCpuRateP95);
        AssertProcessMatches(apiHourly, load.Api);
        AssertProcessMatches(workerHourly, load.Worker);

        // Only the Api rows carry host counters, so the host union is exactly the Api hour.
        Assert.NotNull(apiHourly.HostCpuBusyP95);
        Assert.Equal(Percent(apiHourly.HostCpuBusyP95), load.Host.CpuBusyP95[0]);
        Assert.Equal(Percent(apiHourly.HostCpuBusyMax), load.Host.CpuBusyMax[0]);
        Assert.Equal(Percent(apiHourly.HostCpuBusyMean), load.Host.ApiCpuBusyMean[0]);
        Assert.Equal(apiHourly.HostMemoryUsedP95Bytes, load.Host.MemoryUsedP95Bytes[0]);
        Assert.Equal(apiHourly.HostMemoryUsedMaxBytes, load.Host.MemoryUsedMaxBytes[0]);
        Assert.Equal((long)Math.Round(apiHourly.HostMemoryUsedMeanBytes!.Value), load.Host.ApiMemoryUsedMeanBytes[0]);
        Assert.Equal(Round(apiHourly.HostLoadAverage1P95, 2), load.Host.LoadAverage1P95[0]);
        Assert.Equal(Round(apiHourly.HostLoadAverage1Max, 2), load.Host.LoadAverage1Max[0]);
        Assert.Equal(Round(apiHourly.HostLoadAverage1Mean, 2), load.Host.ApiLoadAverage1Mean[0]);
        Assert.Equal(apiHourly.SampleCount, load.Host.ApiSampleCount[0]);
        Assert.Equal(workerHourly.SampleCount, load.Host.WorkerSampleCount[0]);
    }

    // Spec 11 B, cadence change: 20 h at 30 s, then 4 h at 1 s, interval now 1 s. An hour rolled up
    // before the change keeps its 30-s rates in SystemLoadHourly while the raw path shows it without
    // CPU pairs (the bounded 24-h deviation named in spec 5.1); the 1-s phase agrees in both paths.
    [Fact]
    public async Task CadenceChange_RolledHourKeepsItsValues_RawShowsNoPairs_OneSecondPhaseAgrees()
    {
        var api = Guid.NewGuid();
        var startUtc = NowUtc.AddHours(-24).AddSeconds(10);
        var switchUtc = startUtc.AddHours(20);
        var samples = new List<SystemLoadSample>(2_400 + 14_400);
        long ticks = 0;
        var previousUtc = (DateTime?)null;
        var k = 0;
        for (var at = startUtc; at < NowUtc; at = at.AddSeconds(at < switchUtc ? 30 : 1), k++)
        {
            if (previousUtc is { } previous)
            {
                var cores = at <= switchUtc ? 0.25 : 0.4 + (k % 10) * 0.03;
                ticks += (long)Math.Round(cores * (at - previous).TotalSeconds * TimeSpan.TicksPerSecond);
            }

            samples.Add(Sample(SystemLoadSource.Api, api, at, processTicks: ticks, memCurrent: 100 * Megabyte + k % 7, previous: previousUtc));
            previousUtc = at;
        }

        await SeedAsync([.. samples]);

        // Rolled shortly after the switch with the then-current 30 s, then again now with 1 s.
        await using (var rollupDb = CreateDbContext())
        {
            var rollup = new SystemLoadRollupService(rollupDb);
            await rollup.RecomputeClosableHoursAsync(switchUtc.AddMinutes(10), TimeSpan.FromMinutes(5), samplingIntervalSeconds: 30);
            await rollup.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), samplingIntervalSeconds: 1);
        }

        var rolledHour = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);
        var oneSecondHour = new DateTime(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDbContext();
        var service = new AdminDashboardQueryService(db);
        var hourly = await service.GetLoadAsync(AdminDashboardLoadRange.Days7, NowUtc, 1);
        var raw = await service.GetLoadAsync(AdminDashboardLoadRange.Hours24, NowUtc, 1);

        var rolled = Index(hourly, rolledHour);
        Assert.Equal(0.25, hourly.Api.ProcessCpuP95[rolled]);
        Assert.Equal(120, hourly.Api.SampleCount[rolled]);

        for (var minute = 0; minute < 60; minute += 5)
        {
            var bucket = Index(raw, rolledHour.AddMinutes(minute));
            Assert.Null(raw.Api.ProcessCpuP95[bucket]);
            Assert.Equal(10, raw.Api.SampleCount[bucket]);
            Assert.NotNull(raw.Api.CgroupMemoryP95Bytes[bucket]);
        }

        var oneHourWindow = AdminDashboardLoadBucketing.CreateWindow(oneSecondHour, oneSecondHour.AddHours(1).AddTicks(-1), 3_600);
        var rawHour = await service.GetRawLoadAsync(oneHourWindow, "test", NowUtc, 1, CancellationToken.None);
        var stored = Index(hourly, oneSecondHour);
        Assert.NotNull(hourly.Api.ProcessCpuP95[stored]);
        Assert.Equal(hourly.Api.ProcessCpuP95[stored], rawHour.Api.ProcessCpuP95[0]);
        Assert.Equal(hourly.Api.ProcessCpuMax[stored], rawHour.Api.ProcessCpuMax[0]);
        Assert.Equal(hourly.Api.CgroupMemoryP95Bytes[stored], rawHour.Api.CgroupMemoryP95Bytes[0]);
        Assert.Equal(3_600, rawHour.Api.SampleCount[0]);
    }

    [Theory]
    [InlineData(AdminDashboardLoadRange.Hour1, true)]
    [InlineData(AdminDashboardLoadRange.Hours24, true)]
    [InlineData(AdminDashboardLoadRange.Days7, false)]
    [InlineData(AdminDashboardLoadRange.Days30, false)]
    [InlineData(AdminDashboardLoadRange.Days90, false)]
    [InlineData(AdminDashboardLoadRange.Months13, false)]
    public async Task RawRangesNeverReadHourly_HourlyRangesNeverReadSamples(AdminDashboardLoadRange range, bool raw)
    {
        await SeedAsync(Sample(SystemLoadSource.Api, Guid.NewGuid(), NowUtc.AddMinutes(-10), processTicks: 0));
        await SeedHourlyAsync(Hourly(SystemLoadSource.Api, NowUtc.AddHours(-3)));
        var capture = new CommandCapture();

        await using var db = CreateDbContext(capture);
        var load = await new AdminDashboardQueryService(db).GetLoadAsync(range, NowUtc, 30);

        Assert.True(load.Available);
        Assert.Equal(raw ? "raw" : "hourly", load.SourceKind);
        Assert.NotEmpty(capture.Commands);
        var read = raw ? "\"SystemLoadSamples\"" : "\"SystemLoadHourly\"";
        var notRead = raw ? "\"SystemLoadHourly\"" : "\"SystemLoadSamples\"";
        Assert.Contains(capture.Commands, command => command.Contains(read, StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, command => command.Contains(notRead, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AdminDashboardLoadRange.Hours24)]
    [InlineData(AdminDashboardLoadRange.Days7)]
    [InlineData(AdminDashboardLoadRange.Months13)]
    public async Task EmptyRange_IsNotAvailable(AdminDashboardLoadRange range)
    {
        // Only history outside every window: a raw predecessor-candidate and an old hourly row.
        await SeedAsync(Sample(SystemLoadSource.Api, Guid.NewGuid(), NowUtc.AddDays(-2), processTicks: 0));
        await SeedHourlyAsync(Hourly(SystemLoadSource.Worker, NowUtc.AddMonths(-14), chat: 5));

        await using var db = CreateDbContext();
        var load = await new AdminDashboardQueryService(db).GetLoadAsync(range, NowUtc, 30);

        Assert.False(load.Available);
        Assert.Empty(load.BucketStartsUtc);
        Assert.Empty(load.Host.CpuBusyP95);
        Assert.Empty(load.Workload.ChatLines);
        Assert.Null(load.DataThroughUtc);
        Assert.Equal(AdminDashboardLoadRanges.ToWire(range), load.Range);
        Assert.Equal(NowUtc, load.ToUtc);
        Assert.Equal(NowUtc, load.GeneratedAtUtc);
    }

    // R2: hourly rows 14 and 20 months old (retention off) do not widen 13mo; the answer starts at
    // fromUtc at the earliest and keeps the ladder's bucket count.
    [Fact]
    public async Task OlderHourlyHistory_NeverWidensThe13MonthRange()
    {
        var window = AdminDashboardLoadBucketing.GetWindow(AdminDashboardLoadRange.Months13, NowUtc);
        await SeedHourlyAsync(
            Hourly(SystemLoadSource.Worker, new DateTime(2024, 10, 11, 5, 0, 0, DateTimeKind.Utc), chat: 7),
            Hourly(SystemLoadSource.Worker, new DateTime(2025, 8, 11, 5, 0, 0, DateTimeKind.Utc), chat: 7),
            Hourly(SystemLoadSource.Worker, window.FromUtc, chat: 3),
            Hourly(SystemLoadSource.Api, NowUtc.AddHours(-2), cpuP95: 0.5, cpuMax: 0.6));

        await using var db = CreateDbContext();
        var load = await new AdminDashboardQueryService(db).GetLoadAsync(AdminDashboardLoadRange.Months13, NowUtc, 30);

        Assert.Equal(new DateTime(2025, 9, 11, 0, 0, 0, DateTimeKind.Utc), load.FromUtc);
        Assert.Equal(load.FromUtc, load.BucketStartsUtc[0]);
        Assert.Equal(396, load.BucketStartsUtc.Count);
        Assert.Equal(3.0, load.Workload.ChatLines[0]);
        Assert.Equal(3.0, load.Workload.ChatLines.Sum(value => value ?? 0));
        Assert.Equal(NowUtc.AddHours(-1), load.DataThroughUtc);
        Assert.Equal(50.0, load.Host.CpuBusyP95[^1]);
    }

    [Fact]
    public async Task Hourly_ThirtyDays_SumsWorkloadAndTakesTheHighestHourlyValuesPerTwoHourBucket()
    {
        var first = new DateTime(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);
        await SeedHourlyAsync(
            Hourly(SystemLoadSource.Api, first, cpuP95: 0.5, cpuMax: 0.6, sampleCount: 120),
            Hourly(SystemLoadSource.Worker, first, cpuP95: 0.2, cpuMax: 0.3, sampleCount: 4, chat: 60, emotes: 30, active: 5, live: 2),
            Hourly(SystemLoadSource.Worker, first.AddHours(1), cpuP95: 0.45, cpuMax: 0.95, sampleCount: 120, chat: 60, emotes: 30, active: 7));

        await using var db = CreateDbContext();
        var load = await new AdminDashboardQueryService(db).GetLoadAsync(AdminDashboardLoadRange.Days30, NowUtc, 30);

        Assert.Equal(first, load.BucketStartsUtc[0]);
        Assert.Equal(5, load.BucketStartsUtc.Count);
        Assert.Equal(first.AddHours(2), load.DataThroughUtc);
        Assert.Equal(50.0, load.Host.CpuBusyP95[0]);
        Assert.Equal(95.0, load.Host.CpuBusyMax[0]);
        Assert.Equal("A", load.Host.CpuValueSource[0]);
        Assert.Equal("AW", load.Host.Provenance[0]);
        Assert.Equal(120, load.Host.ApiSampleCount[0]);
        Assert.Equal(124, load.Host.WorkerSampleCount[0]);
        Assert.Equal("perBucket", load.Workload.WorkloadUnit);
        Assert.Equal(120.0, load.Workload.ChatLines[0]);
        Assert.Equal(60.0, load.Workload.EmoteMatches[0]);
        Assert.Equal(2, load.Workload.WorkloadHoursWithData[0]);
        Assert.Equal(7, load.Workload.ActiveChannelsMax[0]);
        Assert.Equal(2, load.Workload.LiveChannelsMax[0]);
        Assert.Null(load.Host.CpuBusyP95[^1]);
    }

    [Fact]
    public async Task Raw_TwentyFourHours_StartsAtTheFirstObservedBucket_AndIgnoresFutureSamples()
    {
        var worker = Guid.NewGuid();
        var first = new DateTime(2026, 10, 11, 9, 2, 0, DateTimeKind.Utc);
        await SeedAsync(
            Sample(SystemLoadSource.Worker, worker, first, chat: 500),
            Sample(SystemLoadSource.Worker, worker, first.AddSeconds(30), previous: first, chat: 30, emotes: 6, active: 3, live: 1),
            Sample(SystemLoadSource.Worker, worker, first.AddSeconds(60), previous: first.AddSeconds(30), chat: 90, emotes: 0, active: 4, live: 1),
            Sample(SystemLoadSource.Worker, worker, NowUtc.AddMinutes(1), previous: first.AddSeconds(60), chat: 1_000));

        await using var db = CreateDbContext();
        var load = await new AdminDashboardQueryService(db).GetLoadAsync(AdminDashboardLoadRange.Hours24, NowUtc, 30);

        Assert.Equal(new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc), load.BucketStartsUtc[0]);
        Assert.Equal(37, load.BucketStartsUtc.Count);
        Assert.Equal(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), load.FromUtc);
        Assert.Equal(first.AddSeconds(60), load.DataThroughUtc);
        Assert.Equal(30, load.SamplingIntervalSeconds);
        Assert.Equal(120.0, load.Workload.ChatLines[0]);
        Assert.Equal(6.0, load.Workload.EmoteMatches[0]);
        Assert.Equal(60.0, load.Workload.WorkloadIntervalSeconds[0]);
        Assert.Equal(4, load.Workload.ActiveChannelsMax[0]);
        Assert.Equal(3, load.Host.WorkerSampleCount[0]);
        Assert.All(load.Workload.ChatLines.Skip(1), Assert.Null);
    }

    [Fact]
    public async Task GetLoad_RejectsAnIntervalOutsideTheConfigurableRangeAndANonUtcNow()
    {
        await using var db = CreateDbContext();
        var service = new AdminDashboardQueryService(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetLoadAsync(AdminDashboardLoadRange.Hours24, NowUtc, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetLoadAsync(AdminDashboardLoadRange.Hours24, NowUtc, 3_601));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetLoadAsync(AdminDashboardLoadRange.Hours24, DateTime.SpecifyKind(NowUtc, DateTimeKind.Local), 30));
    }

    private static void AssertProcessMatches(SystemLoadHourly expected, AdminDashboardProcessSeries actual)
    {
        Assert.Equal(Round(expected.ProcessCpuRateP95, 4), actual.ProcessCpuP95[0]);
        Assert.Equal(Round(expected.ProcessCpuRateMax, 4), actual.ProcessCpuMax[0]);
        Assert.Equal(Round(expected.CgroupCpuRateP95, 4), actual.CgroupCpuP95[0]);
        Assert.Equal(Round(expected.CgroupCpuRateMax, 4), actual.CgroupCpuMax[0]);
        Assert.Equal(expected.CgroupMemoryCurrentP95Bytes, actual.CgroupMemoryP95Bytes[0]);
        Assert.Equal(expected.CgroupMemoryCurrentMaxBytes, actual.CgroupMemoryMaxBytes[0]);
        Assert.Equal(expected.SampleCount, actual.SampleCount[0]);
    }

    // One sample before the hour (the predecessor), then the hour at the given cadence with deterministic
    // load variation. Every 7th slot is skipped (a 2x gap, still a pair) and two consecutive slots in
    // the middle are skipped (a 3x gap, dropped); one sample after the hour must not leak in.
    private static SystemLoadSample[] GenerateSeries(
        SystemLoadSource source,
        Guid processInstanceId,
        DateTime hourUtc,
        int intervalSeconds,
        bool withHost)
    {
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var offset = TimeSpan.FromTicks(interval.Ticks * 3 / 10);
        var slots = (int)(TimeSpan.FromHours(1).Ticks / interval.Ticks);
        var samples = new List<SystemLoadSample>(slots + 2);
        long processTicks = 1_000_000_000;
        long cgroupMicros = 50_000_000;
        long busy = 100_000;
        long total = 400_000;
        DateTime? previous = null;
        for (var slot = -1; slot <= slots; slot++)
        {
            var skipped = slot > 0 && slot < slots && (slot % 7 == 0 || (slots > 20 && (slot == slots / 2 || slot == slots / 2 + 1)));
            if (skipped)
            {
                continue;
            }

            var at = hourUtc + offset + TimeSpan.FromTicks(interval.Ticks * slot);
            var n = slot + 1;
            if (previous is { } previousAt)
            {
                var seconds = (at - previousAt).TotalSeconds;
                processTicks += (long)(seconds * TimeSpan.TicksPerSecond * (0.05 + n * 37 % 100 / 120d));
                cgroupMicros += (long)(seconds * 1_000_000 * (0.1 + n * 53 % 100 / 110d));
                var totalDelta = (long)(seconds * 400);
                total += totalDelta;
                busy += totalDelta * (n * 29 % 100) / 100;
            }

            samples.Add(new SystemLoadSample
            {
                SampledAtUtc = at,
                Source = source,
                ProcessInstanceId = processInstanceId,
                PreviousSampledAtUtc = previous,
                ProcessCpuTimeTicks = processTicks,
                CgroupCpuUsageMicroseconds = cgroupMicros,
                CgroupMemoryCurrentBytes = 200 * Megabyte + n * 7_919 % 50_000,
                HostCpuBusyJiffies = withHost ? busy : null,
                HostCpuTotalJiffies = withHost ? total : null,
                HostLoadAverage1 = withHost ? 0.5 + n * 13 % 50 / 20d : null,
                HostMemoryTotalBytes = withHost ? 4_000 * Megabyte : null,
                HostMemoryAvailableBytes = withHost ? 2_000 * Megabyte + n * 104_729 % 9_000_000 : null,
            });
            previous = at;
        }

        return [.. samples];
    }

    private static int Index(AdminDashboardLoad load, DateTime atUtc)
    {
        for (var i = 0; i < load.BucketStartsUtc.Count; i++)
        {
            if (load.BucketStartsUtc[i] <= atUtc && atUtc < load.BucketStartsUtc[i].AddSeconds(load.BucketSeconds))
            {
                return i;
            }
        }

        throw new Xunit.Sdk.XunitException($"No bucket holds {atUtc:O}.");
    }

    private static double? Round(double? value, int digits) => value is null ? null : Math.Round(value.Value, digits);

    private static double? Percent(double? share) => share is null ? null : Math.Round(share.Value * 100, 1);

    private async Task SeedAsync(params SystemLoadSample[] samples)
    {
        await using var seed = CreateDbContext();
        seed.ChangeTracker.AutoDetectChangesEnabled = false;
        seed.SystemLoadSamples.AddRange(samples);
        await seed.SaveChangesAsync();
    }

    private async Task SeedHourlyAsync(params SystemLoadHourly[] rows)
    {
        await using var seed = CreateDbContext();
        seed.SystemLoadHourlies.AddRange(rows);
        await seed.SaveChangesAsync();
    }

    private AppDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        using var template = fixture.CreateDbContext(DatabaseName);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(template.Database.GetConnectionString())
            .AddInterceptors(interceptors)
            .Options;
        return new AppDbContext(options);
    }

    private static SystemLoadSample Full(
        Guid processInstanceId,
        DateTime atUtc,
        DateTime previous,
        long ticks,
        long busy,
        long total,
        long memory,
        long chat) => new()
        {
            SampledAtUtc = atUtc,
            Source = SystemLoadSource.Worker,
            ProcessInstanceId = processInstanceId,
            PreviousSampledAtUtc = previous,
            ProcessCpuTimeTicks = ticks,
            HostCpuBusyJiffies = busy,
            HostCpuTotalJiffies = total,
            CgroupMemoryCurrentBytes = memory,
            ChatLines = chat,
        };

    private static SystemLoadSample Sample(
        SystemLoadSource source,
        Guid processInstanceId,
        DateTime atUtc,
        long? processTicks = null,
        long? memCurrent = null,
        DateTime? previous = null,
        long? chat = null,
        long? emotes = null,
        int? active = null,
        int? live = null) => new()
        {
            SampledAtUtc = atUtc,
            Source = source,
            ProcessInstanceId = processInstanceId,
            PreviousSampledAtUtc = previous,
            ProcessCpuTimeTicks = processTicks,
            CgroupMemoryCurrentBytes = memCurrent,
            ChatLines = chat,
            EmoteMatches = emotes,
            ActiveChannels = active,
            LiveChannels = live,
        };

    private static SystemLoadHourly Hourly(
        SystemLoadSource source,
        DateTime hourUtc,
        double? cpuP95 = null,
        double? cpuMax = null,
        int sampleCount = 120,
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
            ChatLines = chat,
            EmoteMatches = emotes,
            ActiveChannelsMax = active,
            LiveChannelsMax = live,
        };

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
