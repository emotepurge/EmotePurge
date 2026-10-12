using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class SystemLoadPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "system_load_tests";
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;
    private static readonly DateTime NowUtc = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

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
    public async Task SamplingServiceStoresThePreviousSampleOfTheSameProcess()
    {
        await using var db = CreateDbContext();
        var service = new SystemLoadSamplingService(db);
        var processInstanceId = Guid.NewGuid();
        var workload = new SystemLoadWorkloadSnapshot(4, null, null, null);
        var firstAtUtc = NowUtc.AddSeconds(-60);
        var secondAtUtc = NowUtc.AddSeconds(-30);

        await service.RecordAsync(SystemLoadSource.Api, processInstanceId, Snapshot(firstAtUtc), workload);
        await service.RecordAsync(SystemLoadSource.Api, processInstanceId, Snapshot(secondAtUtc), workload);

        var samples = await db.SystemLoadSamples.OrderBy(sample => sample.SampledAtUtc).ToListAsync();
        Assert.Null(samples[0].PreviousSampledAtUtc);
        Assert.Equal(firstAtUtc, samples[1].PreviousSampledAtUtc);
        Assert.Equal(processInstanceId, samples[1].ProcessInstanceId);
        Assert.Null(samples[1].LiveChannels);
        Assert.Null(samples[1].ChatLines);
        Assert.Null(samples[1].EmoteMatches);
    }

    [Fact]
    public async Task Rollup_IsIdempotentForTheSameHour()
    {
        await using (var db = CreateDbContext())
        {
            var identity = Guid.NewGuid();
            db.SystemLoadSamples.AddRange(
                Sample(NowUtc.AddHours(-2).AddSeconds(10), identity, 0, load: 0.2),
                Sample(NowUtc.AddHours(-2).AddSeconds(40), identity, 300_000_000, load: 0.4));
            await db.SaveChangesAsync();
        }

        await using var rollupDb = CreateDbContext();
        var service = new SystemLoadRollupService(rollupDb);

        Assert.Equal(1, await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30));
        // Steady state: the second run finds no changed hour, so it recomputes nothing.
        Assert.Equal(0, await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30));

        Assert.Equal(1, await rollupDb.SystemLoadHourlies.CountAsync());
    }

    [Fact]
    public async Task Rollup_ClosesAnHourExactlyAtTheGraceBoundary()
    {
        var hourStartUtc = NowUtc.AddHours(12);
        var hourEndUtc = hourStartUtc.AddHours(1);
        await using var db = CreateDbContext();
        db.SystemLoadSamples.Add(Sample(hourStartUtc.AddMinutes(30), Guid.NewGuid(), 0));
        await db.SaveChangesAsync();
        var service = new SystemLoadRollupService(db);
        var grace = TimeSpan.FromMinutes(5);

        var beforeBoundary = await service.RecomputeClosableHoursAsync(hourEndUtc + grace - TimeSpan.FromSeconds(1), grace, 30);
        var atBoundary = await service.RecomputeClosableHoursAsync(hourEndUtc + grace, grace, 30);
        var afterBoundary = await service.RecomputeClosableHoursAsync(hourEndUtc + grace + TimeSpan.FromSeconds(1), grace, 30);

        Assert.Equal(0, beforeBoundary);
        Assert.Equal(1, atBoundary);
        Assert.Equal(0, afterBoundary);
    }

    [Fact]
    public async Task ApiWorkloadRemainsUnknownInTheHourlyRow()
    {
        await using var db = CreateDbContext();
        db.SystemLoadSamples.Add(Sample(NowUtc.AddHours(-2).AddMinutes(10), Guid.NewGuid(), 0));
        await db.SaveChangesAsync();

        await new SystemLoadRollupService(db).RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);

        var hourly = await db.SystemLoadHourlies.SingleAsync();
        Assert.Null(hourly.ChatLines);
        Assert.Null(hourly.EmoteMatches);
    }

    [Fact]
    public async Task Rollup_CatchesUpMissingHoursAcrossAnOutage()
    {
        await using var db = CreateDbContext();
        var identity = Guid.NewGuid();
        db.SystemLoadSamples.AddRange(
            Sample(NowUtc.AddHours(-14).AddMinutes(10), identity, 0),
            Sample(NowUtc.AddHours(-12).AddMinutes(10), identity, 10_000_000_000));
        await db.SaveChangesAsync();

        var service = new SystemLoadRollupService(db);
        await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);

        var hours = await db.SystemLoadHourlies.OrderBy(hourly => hourly.HourUtc).Select(hourly => hourly.HourUtc).ToListAsync();
        Assert.Equal([NowUtc.AddHours(-14), NowUtc.AddHours(-12)], hours);
    }

    [Fact]
    public async Task Rollup_RecomputesARecentHourAfterALateInsert()
    {
        await using var db = CreateDbContext();
        var identity = Guid.NewGuid();
        db.SystemLoadSamples.AddRange(
            Sample(NowUtc.AddHours(-2).AddMinutes(10), identity, 0, load: 0.2),
            Sample(NowUtc.AddHours(-2).AddMinutes(40), identity, 300_000_000, load: 0.4));
        await db.SaveChangesAsync();
        var service = new SystemLoadRollupService(db);

        await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);
        db.SystemLoadSamples.Add(Sample(NowUtc.AddHours(-2).AddMinutes(25), identity, 150_000_000, load: 0.3));
        await db.SaveChangesAsync();
        await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);

        var hourly = await db.SystemLoadHourlies.SingleAsync();
        Assert.Equal(3, hourly.SampleCount);
        Assert.Equal(0.3, hourly.HostLoadAverage1Mean);
    }

    // protects: the stored CPU figures of hours that were already rolled up under a 30 s interval when the
    //   interval is lowered to 1 s and the rollup runs again (two process instances, as after a restart).
    // fails_when: the second run recomputes the already rolled-up hours with the new 1 s gap rule and
    //   overwrites their CPU columns with null (or changes any other column).
    // no_claim: hours first rolled up after the change and the switch hour itself (documented limit);
    //   the instances are synthetic samples, not a real restarted Worker.
    [Fact]
    public async Task Rollup_KeepsRolledUpHoursIdenticalAfterTheIntervalIsLowered()
    {
        var dayUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var oldInstance = Guid.NewGuid();
        var newInstance = Guid.NewGuid();
        await using var db = CreateDbContext();
        db.SystemLoadSamples.AddRange(CpuSamples(
            SystemLoadSource.Worker, oldInstance, dayUtc.AddHours(8), dayUtc.AddHours(12).AddMinutes(20), TimeSpan.FromSeconds(30)));
        db.SystemLoadSamples.AddRange(CpuSamples(
            SystemLoadSource.Worker, newInstance, dayUtc.AddHours(12).AddMinutes(20), dayUtc.AddHours(12).AddMinutes(24), TimeSpan.FromSeconds(1)));
        await db.SaveChangesAsync();
        var service = new SystemLoadRollupService(db);
        var grace = TimeSpan.FromMinutes(5);

        await service.RecomputeClosableHoursAsync(dayUtc.AddHours(12).AddMinutes(10), grace, 30);
        var before = await ReadHourliesAsync(db);
        await service.RecomputeClosableHoursAsync(dayUtc.AddHours(12).AddMinutes(25), grace, 1);
        var after = await ReadHourliesAsync(db);

        var rolledHours = new[] { 9, 10, 11 }.Select(hour => dayUtc.AddHours(hour)).ToArray();
        foreach (var hour in rolledHours)
        {
            Assert.NotNull(before[hour].ProcessCpuRateMean);
            Assert.NotNull(before[hour].HostCpuBusyMean);
            Assert.Equal(Describe(before[hour]), Describe(after[hour]));
        }
    }

    // protects: the run count of a steady-state rollup with two sources: only newly closed hours count.
    // fails_when: an unchanged window row is recomputed again (old behaviour: up to 3 hours per source).
    // no_claim: row contents; the numbers come from synthetic 30 s samples of both sources.
    [Fact]
    public async Task Rollup_RecomputesOnlyNewlyClosedHoursInSteadyState()
    {
        var dayUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        await using var db = CreateDbContext();
        foreach (var source in new[] { SystemLoadSource.Api, SystemLoadSource.Worker })
        {
            db.SystemLoadSamples.AddRange(CpuSamples(source, Guid.NewGuid(), dayUtc.AddHours(8), dayUtc.AddHours(11).AddMinutes(30), TimeSpan.FromSeconds(30)));
        }

        await db.SaveChangesAsync();
        var service = new SystemLoadRollupService(db);
        var grace = TimeSpan.FromMinutes(5);

        // 10:10 closes up to hour 09 (hours 08 and 09 are rolled up for the first time, per source).
        Assert.Equal(4, await service.RecomputeClosableHoursAsync(dayUtc.AddHours(10).AddMinutes(10), grace, 30));
        Assert.Equal(0, await service.RecomputeClosableHoursAsync(dayUtc.AddHours(10).AddMinutes(20), grace, 30));
        // One hour later only hour 10 is new: one per source, although the window still holds hours 08 to 10.
        Assert.Equal(2, await service.RecomputeClosableHoursAsync(dayUtc.AddHours(11).AddMinutes(10), grace, 30));
    }

    // protects: forward propagation; a late sample in hour H also refreshes H+1, whose first pair uses it
    //   as predecessor.
    // fails_when: only hour H is recomputed and hour H+1 keeps the figures computed without the late sample.
    // no_claim: late samples older than the recompute window; the samples are synthetic.
    [Fact]
    public async Task Rollup_RecomputesTheNextHourWhenALateSampleIsItsPredecessor()
    {
        var dayUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var identity = Guid.NewGuid();
        var hour9 = dayUtc.AddHours(9);
        var hour10 = dayUtc.AddHours(10);
        await using var db = CreateDbContext();
        db.SystemLoadSamples.AddRange(
            Sample(hour9.AddMinutes(10), identity, 0),
            Sample(hour10.AddSeconds(15), identity, 400_000_000),
            Sample(hour10.AddSeconds(45), identity, 550_000_000));
        await db.SaveChangesAsync();
        var service = new SystemLoadRollupService(db);
        var grace = TimeSpan.FromMinutes(5);
        var nowUtc = dayUtc.AddHours(12).AddMinutes(10);

        await service.RecomputeClosableHoursAsync(nowUtc, grace, 30);
        var before = (await ReadHourliesAsync(db))[hour10];
        db.SystemLoadSamples.Add(Sample(hour9.AddMinutes(59).AddSeconds(45), identity, 100_000_000));
        await db.SaveChangesAsync();
        await service.RecomputeClosableHoursAsync(nowUtc, grace, 30);
        var after = (await ReadHourliesAsync(db))[hour10];

        Assert.Equal(0.5, before.ProcessCpuRateMean);
        Assert.Equal(0.75, after.ProcessCpuRateMean);
        Assert.Equal(1d, after.ProcessCpuRateMax);
    }

    // protects: stored CPU groups survive a recompute without valid pairs while memory/workload columns
    //   are refreshed, and the run logs exactly one Warning.
    // fails_when: the CPU groups become null, the other columns stay stale, or the warning is missing/repeated.
    // no_claim: a recompute that still has some valid pairs (overwritten by design); synthetic samples.
    [Fact]
    public async Task Rollup_KeepsStoredCpuGroupsWhenALateInsertCoincidesWithALoweredInterval()
    {
        var dayUtc = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var identity = Guid.NewGuid();
        var hour9 = dayUtc.AddHours(9);
        await using var db = CreateDbContext();
        db.SystemLoadSamples.AddRange(CpuSamples(SystemLoadSource.Worker, identity, hour9, hour9.AddMinutes(30), TimeSpan.FromSeconds(30)));
        await db.SaveChangesAsync();
        var logger = new RecordingLogger<SystemLoadRollupService>();
        var service = new SystemLoadRollupService(db, logger);
        var grace = TimeSpan.FromMinutes(5);
        var nowUtc = dayUtc.AddHours(11).AddMinutes(10);

        await service.RecomputeClosableHoursAsync(nowUtc, grace, 30);
        var before = (await ReadHourliesAsync(db))[hour9];
        db.SystemLoadSamples.Add(new SystemLoadSample
        {
            SampledAtUtc = hour9.AddMinutes(15).AddSeconds(10),
            Source = SystemLoadSource.Worker,
            ProcessInstanceId = identity,
            HostLoadAverage1 = 9.5,
        });
        await db.SaveChangesAsync();
        await service.RecomputeClosableHoursAsync(nowUtc, grace, 1);
        var after = (await ReadHourliesAsync(db))[hour9];

        Assert.NotNull(before.ProcessCpuRateMean);
        Assert.Equal(before.ProcessCpuRateMean, after.ProcessCpuRateMean);
        Assert.Equal(before.ProcessCpuRateMax, after.ProcessCpuRateMax);
        Assert.Equal(before.CgroupCpuRateP95, after.CgroupCpuRateP95);
        Assert.Equal(before.HostCpuBusyMean, after.HostCpuBusyMean);
        Assert.Equal(before.SampleCount + 1, after.SampleCount);
        Assert.NotEqual(before.HostLoadAverage1Max, after.HostLoadAverage1Max);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("kept stored CPU figures", warning.Message);
    }

    [Fact]
    public async Task RetentionBeforeCatchUpKeepsRawRowsThenDeletesThemAfterRollup()
    {
        await using var db = CreateDbContext();
        db.SystemLoadSamples.Add(Sample(NowUtc.AddDays(-8).AddMinutes(10), Guid.NewGuid(), 0));
        await db.SaveChangesAsync();

        var firstPass = await RunRetentionAsync(db, enforce: true);
        Assert.Equal(1, firstPass.SystemLoadRawSamplesWithoutHourlyRow);
        Assert.Equal(1, await db.SystemLoadSamples.CountAsync());

        await new SystemLoadRollupService(db)
            .RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);
        var secondPass = await RunRetentionAsync(db, enforce: true);

        Assert.Equal(1, secondPass.SystemLoadRawSamplesDeleted);
        Assert.Equal(0, await db.SystemLoadSamples.CountAsync());
    }

    [Fact]
    public async Task CatchUpBeforeRetentionCreatesTheHourAndAllowsRawDeletion()
    {
        await using var db = CreateDbContext();
        db.SystemLoadSamples.Add(Sample(NowUtc.AddDays(-8).AddMinutes(10), Guid.NewGuid(), 0));
        await db.SaveChangesAsync();

        await new SystemLoadRollupService(db)
            .RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);
        var summary = await RunRetentionAsync(db, enforce: true);

        Assert.Equal(1, summary.SystemLoadRawSamplesDeleted);
        Assert.Equal(0, summary.SystemLoadRawSamplesWithoutHourlyRow);
        Assert.Equal(0, await db.SystemLoadSamples.CountAsync());
        Assert.Equal(1, await db.SystemLoadHourlies.CountAsync());
    }

    [Fact]
    public async Task RetentionKeepsBoundaryPredecessorUntilItsSuccessorHourIsRolledUp()
    {
        await using var db = CreateDbContext();
        var processInstanceId = Guid.NewGuid();
        var predecessorAtUtc = NowUtc.AddDays(-8).Date.AddHours(12).AddMinutes(59).AddSeconds(45);
        var successorAtUtc = predecessorAtUtc.AddSeconds(30);
        db.SystemLoadSamples.AddRange(
            Sample(predecessorAtUtc, processInstanceId, 0),
            Sample(successorAtUtc, processInstanceId, 150_000_000));
        db.SystemLoadHourlies.Add(Hourly(predecessorAtUtc));
        await db.SaveChangesAsync();

        var firstPass = await RunRetentionAsync(db, enforce: true);

        Assert.Equal(0, firstPass.SystemLoadRawSamplesDeleted);
        Assert.Equal(2, await db.SystemLoadSamples.CountAsync());

        await new SystemLoadRollupService(db)
            .RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);
        var successorHour = await db.SystemLoadHourlies.SingleAsync(hourly => hourly.HourUtc == FloorHour(successorAtUtc));
        Assert.Equal(0.5, successorHour.ProcessCpuRateMean);

        var secondPass = await RunRetentionAsync(db, enforce: true);

        Assert.Equal(2, secondPass.SystemLoadRawSamplesDeleted);
        Assert.Equal(0, await db.SystemLoadSamples.CountAsync());
    }

    [Fact]
    public async Task RetentionKeepsRawSamplesWhenTheirHourHasNoHourlyRow()
    {
        await using var db = CreateDbContext();
        db.SystemLoadSamples.Add(Sample(NowUtc.AddDays(-10), Guid.NewGuid(), 0));
        await db.SaveChangesAsync();

        var summary = await RunRetentionAsync(db, enforce: true);

        Assert.Equal(1, summary.SystemLoadRawSamplesWithoutHourlyRow);
        Assert.Equal(0, summary.SystemLoadRawSamplesDeleted);
        Assert.Equal(1, await db.SystemLoadSamples.CountAsync());
    }

    [Fact]
    public async Task RetentionDeletesRowsPastSevenDaysAndThirteenMonthsOnly()
    {
        await using var db = CreateDbContext();
        var oldRaw = Sample(NowUtc.AddDays(-8), Guid.NewGuid(), 0);
        var atRawCutoff = Sample(NowUtc.AddDays(-7), Guid.NewGuid(), 0);
        var oldHourly = Hourly(NowUtc.AddMonths(-13).AddHours(-1));
        var atHourlyCutoff = Hourly(NowUtc.AddMonths(-13));
        db.SystemLoadSamples.AddRange(oldRaw, atRawCutoff);
        db.SystemLoadHourlies.AddRange(Hourly(FloorHour(oldRaw.SampledAtUtc)), oldHourly, atHourlyCutoff);
        await db.SaveChangesAsync();

        var summary = await RunRetentionAsync(db, enforce: true);

        Assert.Equal(1, summary.SystemLoadRawSamplesDeleted);
        Assert.Equal(1, summary.SystemLoadHourlyRowsDeleted);
        Assert.Equal([atRawCutoff.Id], await db.SystemLoadSamples.Select(sample => sample.Id).ToListAsync());
        Assert.Contains(await db.SystemLoadHourlies.Select(hourly => hourly.HourUtc).ToListAsync(), hour => hour == atHourlyCutoff.HourUtc);
        Assert.DoesNotContain(await db.SystemLoadHourlies.Select(hourly => hourly.HourUtc).ToListAsync(), hour => hour == oldHourly.HourUtc);
    }

    [Fact]
    public async Task RetentionDryRunCountsDueRowsWithoutDeletingThem()
    {
        await using var db = CreateDbContext();
        var oldRaw = Sample(NowUtc.AddDays(-8), Guid.NewGuid(), 0);
        var oldHourly = Hourly(NowUtc.AddMonths(-14));
        db.SystemLoadSamples.Add(oldRaw);
        db.SystemLoadHourlies.AddRange(Hourly(FloorHour(oldRaw.SampledAtUtc)), oldHourly);
        await db.SaveChangesAsync();

        var summary = await RunRetentionAsync(db, enforce: false);

        Assert.Equal(1, summary.SystemLoadRawSamplesDeleted);
        Assert.Equal(1, summary.SystemLoadHourlyRowsDeleted);
        Assert.Equal(1, await db.SystemLoadSamples.CountAsync());
        Assert.Equal(2, await db.SystemLoadHourlies.CountAsync());
    }

    private static async Task<RetentionRunSummary> RunRetentionAsync(AppDbContext db, bool enforce)
    {
        var service = new DataRetentionService(
            db,
            Substitute.For<IAccountDeletionService>(),
            Substitute.For<IChannelService>(),
            new RetentionOptions(),
            new FixedTimeProvider(NowUtc),
            NullLogger<DataRetentionService>.Instance);
        return await service.RunAsync(enforce);
    }

    private static async Task<Dictionary<DateTime, SystemLoadHourly>> ReadHourliesAsync(AppDbContext db) =>
        (await db.SystemLoadHourlies.AsNoTracking().ToListAsync()).ToDictionary(hourly => hourly.HourUtc);

    private static string Describe(SystemLoadHourly hourly) => string.Join(
        "; ",
        typeof(SystemLoadHourly).GetProperties().Select(property => $"{property.Name}={property.GetValue(hourly) ?? "null"}"));

    private static List<SystemLoadSample> CpuSamples(
        SystemLoadSource source,
        Guid identity,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan step)
    {
        var samples = new List<SystemLoadSample>();
        var elapsedSeconds = 0d;
        for (var at = fromUtc; at < toUtc; at += step)
        {
            // Half a core of process CPU, 20 % host busy, 0.25 core of cgroup CPU.
            samples.Add(new SystemLoadSample
            {
                SampledAtUtc = at,
                Source = source,
                ProcessInstanceId = identity,
                ProcessCpuTimeTicks = (long)(elapsedSeconds * 0.5 * TimeSpan.TicksPerSecond),
                CgroupCpuUsageMicroseconds = (long)(elapsedSeconds * 0.25 * 1_000_000d),
                HostCpuBusyJiffies = (long)(elapsedSeconds * 20),
                HostCpuTotalJiffies = (long)(elapsedSeconds * 100),
                HostLoadAverage1 = 0.5,
            });
            elapsedSeconds += step.TotalSeconds;
        }

        return samples;
    }

    private AppDbContext CreateDbContext() => fixture.CreateDbContext(DatabaseName);

    private static SystemLoadSample Sample(DateTime atUtc, Guid identity, long processTicks, double? load = null) => new()
    {
        SampledAtUtc = atUtc,
        Source = SystemLoadSource.Api,
        ProcessInstanceId = identity,
        ProcessCpuTimeTicks = processTicks,
        HostLoadAverage1 = load,
    };

    private static SystemLoadHourly Hourly(DateTime hourUtc) => new()
    {
        Source = SystemLoadSource.Api,
        HourUtc = FloorHour(hourUtc),
    };

    private static SystemLoadSnapshot Snapshot(DateTime sampledAtUtc) => new(
        sampledAtUtc,
        123,
        456,
        null,
        100_000,
        1_024,
        null,
        20,
        100,
        5,
        1,
        0.4,
        0.3,
        0.2,
        16_384,
        9_216,
        0,
        0);

    private static DateTime FloorHour(DateTime timestampUtc) =>
        new(timestampUtc.Ticks - timestampUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private sealed class FixedTimeProvider(DateTime nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(nowUtc);
    }
}
