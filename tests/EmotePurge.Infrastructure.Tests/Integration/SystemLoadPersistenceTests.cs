using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
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
        Assert.Equal(1, await service.RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), 30));

        Assert.Equal(1, await rollupDb.SystemLoadHourlies.CountAsync());
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
