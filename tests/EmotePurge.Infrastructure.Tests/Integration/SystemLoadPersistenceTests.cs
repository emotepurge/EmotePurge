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

    // protects: the second run of an unchanged hour recomputes nothing and keeps one row.
    // fails_when: the unchanged hour is recomputed again or duplicated.
    // no_claim: row contents; two synthetic samples.
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
        var service = new SystemLoadRollupService(rollupDb, NullLogger<SystemLoadRollupService>.Instance);

        Assert.Equal(1, await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30));
        // Steady state: the second run finds no changed hour, so it recomputes nothing.
        Assert.Equal(0, await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30));

        Assert.Equal(1, await rollupDb.SystemLoadHourlies.CountAsync());
    }

    // protects: an hour is closed exactly when the grace period has elapsed and then stays untouched.
    // fails_when: the hour closes early or late, or the third run (closable and clean) reports work; the row
    //   is checked after all runs, so a missing row or a changed SampleCount fails.
    // no_claim: row contents beyond SampleCount; a single synthetic sample.
    [Fact]
    public async Task Rollup_ClosesAnHourExactlyAtTheGraceBoundary()
    {
        var hourStartUtc = NowUtc.AddHours(12);
        var hourEndUtc = hourStartUtc.AddHours(1);
        await using var db = CreateDbContext();
        db.SystemLoadSamples.Add(Sample(hourStartUtc.AddMinutes(30), Guid.NewGuid(), 0));
        await db.SaveChangesAsync();
        var service = new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance);
        var grace = TimeSpan.FromMinutes(5);

        var beforeBoundary = await service.RecomputeClosableHoursAsync(hourEndUtc + grace - TimeSpan.FromSeconds(1), grace, 30);
        var atBoundary = await service.RecomputeClosableHoursAsync(hourEndUtc + grace, grace, 30);
        var afterBoundary = await service.RecomputeClosableHoursAsync(hourEndUtc + grace + TimeSpan.FromSeconds(1), grace, 30);

        Assert.Equal(0, beforeBoundary);
        Assert.Equal(1, atBoundary);
        Assert.Equal(0, afterBoundary);
        // "Closable and clean" must be told apart from "not closable": the row exists and is untouched.
        var row = await db.SystemLoadHourlies.SingleAsync();
        Assert.Equal(1, row.SampleCount);
    }

    [Fact]
    public async Task ApiWorkloadRemainsUnknownInTheHourlyRow()
    {
        await using var db = CreateDbContext();
        db.SystemLoadSamples.Add(Sample(NowUtc.AddHours(-2).AddMinutes(10), Guid.NewGuid(), 0));
        await db.SaveChangesAsync();

        await new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance).RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);

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

        var service = new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance);
        await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);

        var hours = await db.SystemLoadHourlies.OrderBy(hourly => hourly.HourUtc).Select(hourly => hourly.HourUtc).ToListAsync();
        Assert.Equal([NowUtc.AddHours(-14), NowUtc.AddHours(-12)], hours);
    }

    // protects: an hour 30 days back with samples and no row gets its row in a single run, while recent hours
    //   that are already rolled up stay untouched; the backfill has no lower time bound.
    // fails_when: a lower bound or a retention-style cutoff strands the old hour, or the recent hour is recomputed.
    // no_claim: cost of the candidate query; two synthetic hours and one source.
    [Fact]
    public async Task Rollup_CreatesAStrandedHourThirtyDaysBack()
    {
        var identity = Guid.NewGuid();
        var oldHourUtc = FloorHour(NowUtc.AddDays(-30));
        var recentHourUtc = FloorHour(NowUtc.AddHours(-5));
        await using var db = CreateDbContext();
        db.SystemLoadSamples.AddRange(
            Sample(oldHourUtc.AddMinutes(10), identity, 0),
            Sample(oldHourUtc.AddMinutes(40), identity, 1_000),
            Sample(recentHourUtc.AddMinutes(10), identity, 0),
            Sample(recentHourUtc.AddMinutes(40), identity, 1_000));
        db.SystemLoadHourlies.Add(new SystemLoadHourly { Source = SystemLoadSource.Api, HourUtc = recentHourUtc, SampleCount = 2 });
        await db.SaveChangesAsync();

        var written = await new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance)
            .RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);

        Assert.Equal(1, written);
        var rows = await db.SystemLoadHourlies.AsNoTracking().OrderBy(hourly => hourly.HourUtc).ToListAsync();
        Assert.Equal([oldHourUtc, recentHourUtc], rows.Select(static hourly => hourly.HourUtc));
        Assert.Equal(2, rows[0].SampleCount);
    }

    // protects: a backlog larger than one batch is rolled up completely, and the first hour of a later batch
    //   still pairs with its predecessor sample from the earlier batch; every row equals an unbatched calculation.
    // fails_when: a batch loses its predecessor, a batch is skipped, or batching changes any column.
    // no_claim: memory behaviour; one source, one instance, 60 s synthetic samples, expectation from SystemLoadRollupMath.
    [Fact]
    public async Task Rollup_CatchUpAcrossBatchBoundaries_KeepsThePredecessorPair()
    {
        var identity = Guid.NewGuid();
        var firstHourUtc = FloorHour(NowUtc.AddHours(-100));
        const int hourCount = 60;
        var samples = CpuSamples(
            SystemLoadSource.Worker, identity, firstHourUtc, firstHourUtc.AddHours(hourCount), TimeSpan.FromSeconds(60));
        // The counter jumps by 30 CPU seconds across the batch boundary, so the pair crossing into the first hour
        // of batch 2 has rate (30 + 60 * 0.5) / 60 = 1.0 while every other pair has 0.5: only that pair makes the
        // hour's maximum differ.
        var firstOfSecondBatch = firstHourUtc.AddHours(24);
        foreach (var sample in samples.Where(sample => sample.SampledAtUtc >= firstOfSecondBatch))
        {
            sample.ProcessCpuTimeTicks += 30 * TimeSpan.TicksPerSecond;
        }

        await using var db = CreateDbContext();
        db.SystemLoadSamples.AddRange(samples);
        await db.SaveChangesAsync();

        var written = await new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance)
            .RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);

        Assert.Equal(hourCount, written);
        var rows = await ReadHourliesAsync(db);
        Assert.Equal(hourCount, rows.Count);
        Assert.NotNull(rows[firstOfSecondBatch].ProcessCpuRateMean);
        Assert.Equal(1.0, rows[firstOfSecondBatch].ProcessCpuRateMax!.Value, 9);
        Assert.NotNull(rows[firstOfSecondBatch].HostCpuBusyMean);
        for (var offset = 0; offset < hourCount; offset++)
        {
            var hourUtc = firstHourUtc.AddHours(offset);
            var window = samples
                .Where(sample => sample.SampledAtUtc < hourUtc.AddHours(1))
                .OrderBy(static sample => sample.SampledAtUtc)
                .Where(sample => sample.SampledAtUtc >= hourUtc.AddMinutes(-1))
                .ToArray();
            var expected = SystemLoadRollupMath.Calculate(window, SystemLoadSource.Worker, hourUtc, 30);
            Assert.Equal(Describe(expected), Describe(rows[hourUtc]));
        }
    }

    // protects: the backfill set is exactly the (source, hour) pairs with samples before the close boundary and no
    //   row: several instances, an hour with one source only, a row for one source only, a sample at the boundary.
    // fails_when: a pair is missed or added, a boundary sample opens its hour, or an existing row is recomputed.
    // no_claim: the cost of the candidate query; synthetic samples far outside the dirty window.
    [Fact]
    public Task Rollup_BackfillsExactlyTheHoursWithSamplesAndNoRow() => BackfillScenarioAsync(null);

    // protects: the hour bucketing of the rollup does not depend on the session time zone, including a zone
    //   with a non-whole-hour offset (+05:30), so the same rows appear as under UTC.
    // fails_when: a time-zone-dependent date_trunc or cast shifts an hour key or sample into another hour.
    // no_claim: other zones; the setting is applied on the opened connection of the rollup's own context.
    [Fact]
    public Task Rollup_BackfillIsIndependentOfTheSessionTimeZone() => BackfillScenarioAsync("Asia/Kolkata");

    private async Task BackfillScenarioAsync(string? sessionTimeZone)
    {
        var dayUtc = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        var closeEndUtc = new DateTime(2026, 10, 10, 23, 0, 0, DateTimeKind.Utc);
        var instanceA = Guid.NewGuid();
        var instanceB = Guid.NewGuid();
        var worker = Guid.NewGuid();
        await using var db = CreateDbContext();
        if (sessionTimeZone is not null)
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync($"SET TIME ZONE '{sessionTimeZone}'");
            var active = await db.Database.SqlQueryRaw<string>("SELECT current_setting('TimeZone') AS \"Value\"").ToListAsync();
            Assert.Equal([sessionTimeZone], active);
        }

        db.SystemLoadSamples.AddRange(
            Sample(dayUtc.AddHours(1).AddMinutes(5), instanceA, 0),
            Sample(dayUtc.AddHours(1).AddMinutes(35), instanceA, 10),
            Sample(dayUtc.AddHours(2).AddMinutes(5), instanceA, 20),
            Sample(dayUtc.AddHours(2).AddMinutes(7), instanceB, 0),
            Sample(closeEndUtc, instanceA, 30),
            Sample(dayUtc.AddHours(2).AddMinutes(9), worker, 0, source: SystemLoadSource.Worker),
            Sample(dayUtc.AddHours(3).AddMinutes(9), worker, 5, source: SystemLoadSource.Worker));
        db.SystemLoadHourlies.Add(new SystemLoadHourly
        {
            Source = SystemLoadSource.Api,
            HourUtc = dayUtc.AddHours(2),
            SampleCount = 99,
        });
        await db.SaveChangesAsync();

        var written = await new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance)
            .RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30);

        Assert.Equal(3, written);
        var rows = (await db.SystemLoadHourlies.AsNoTracking().ToListAsync())
            .OrderBy(static hourly => hourly.HourUtc).ThenBy(static hourly => hourly.Source)
            .Select(static hourly => (hourly.Source, hourly.HourUtc, hourly.SampleCount))
            .ToArray();
        Assert.Equal(
            [
                (SystemLoadSource.Api, dayUtc.AddHours(1), 2),
                (SystemLoadSource.Api, dayUtc.AddHours(2), 99),
                (SystemLoadSource.Worker, dayUtc.AddHours(2), 1),
                (SystemLoadSource.Worker, dayUtc.AddHours(3), 1),
            ],
            rows);
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
        var service = new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance);

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
        var service = new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance);
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
        var service = new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance);
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
    //   Later hours are not refreshed (covered by the unit tests).
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
        var service = new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance);
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

        await new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance)
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

        await new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance)
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

        await new SystemLoadRollupService(db, NullLogger<SystemLoadRollupService>.Instance)
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

    private static SystemLoadSample Sample(
        DateTime atUtc,
        Guid identity,
        long processTicks,
        double? load = null,
        SystemLoadSource source = SystemLoadSource.Api) => new()
        {
            SampledAtUtc = atUtc,
            Source = source,
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
