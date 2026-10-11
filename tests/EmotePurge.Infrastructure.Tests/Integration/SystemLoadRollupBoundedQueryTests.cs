using System.Data.Common;
using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public sealed class SystemLoadRollupBoundedQueryTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "system_load_rollup_bounded_tests";
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
    public async Task RecomputeLoadsCandidateHoursAndTheirPredecessorsButSkipsAlreadyRolledHistory()
    {
        var targetHourUtc = FloorHour(NowUtc.AddHours(-4));
        var predecessorHourUtc = targetHourUtc.AddHours(-1);
        var oldRolledHourUtc = targetHourUtc.AddDays(-30);
        var targetProcess = Guid.NewGuid();
        var oldProcess = Guid.NewGuid();
        var capture = new CandidateSampleQueryCapture();

        await using (var seed = CreateDbContext())
        {
            seed.SystemLoadSamples.AddRange(
                Sample(predecessorHourUtc.AddMinutes(59).AddSeconds(45), targetProcess, 0),
                Sample(targetHourUtc.AddSeconds(15), targetProcess, TimeSpan.TicksPerSecond),
                Sample(targetHourUtc.AddSeconds(45), targetProcess, TimeSpan.TicksPerSecond * 2),
                Sample(oldRolledHourUtc.AddSeconds(10), oldProcess, 0));
            seed.SystemLoadHourlies.AddRange(
                Hourly(predecessorHourUtc, sampleCount: 99),
                Hourly(oldRolledHourUtc, sampleCount: 777));
            await seed.SaveChangesAsync();
        }

        await using var db = CreateDbContext(capture);
        var recomputed = await new SystemLoadRollupService(db)
            .RecomputeClosableHoursAsync(NowUtc, TimeSpan.FromMinutes(5), samplingIntervalSeconds: 30);

        Assert.Equal(1, recomputed);
        var target = await db.SystemLoadHourlies.SingleAsync(hourly => hourly.HourUtc == targetHourUtc);
        Assert.Equal(2, target.SampleCount);
        Assert.Equal(1d / 30d, target.ProcessCpuRateMean!.Value, 10);

        var oldRolled = await db.SystemLoadHourlies.SingleAsync(hourly => hourly.HourUtc == oldRolledHourUtc);
        Assert.Equal(777, oldRolled.SampleCount);

        var sampleLoad = Assert.Single(capture.CandidateSampleLoads);
        Assert.Contains("JOIN LATERAL", sampleLoad.CommandText, StringComparison.OrdinalIgnoreCase);
        var requestedHours = sampleLoad.Parameters
            .OfType<DateTime[]>()
            .SelectMany(static hours => hours)
            .ToArray();
        Assert.Contains(targetHourUtc, requestedHours);
        Assert.DoesNotContain(predecessorHourUtc, requestedHours);
        Assert.DoesNotContain(oldRolledHourUtc, requestedHours);
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

    private static SystemLoadSample Sample(DateTime atUtc, Guid identity, long processTicks) => new()
    {
        SampledAtUtc = atUtc,
        Source = SystemLoadSource.Api,
        ProcessInstanceId = identity,
        ProcessCpuTimeTicks = processTicks,
    };

    private static SystemLoadHourly Hourly(DateTime hourUtc, int sampleCount) => new()
    {
        Source = SystemLoadSource.Api,
        HourUtc = hourUtc,
        SampleCount = sampleCount,
    };

    private static DateTime FloorHour(DateTime timestampUtc) =>
        new(timestampUtc.Ticks - timestampUtc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private sealed class CandidateSampleQueryCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> CandidateSampleLoads { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("candidate_hours", StringComparison.OrdinalIgnoreCase))
            {
                CandidateSampleLoads.Add(new CapturedCommand(
                    command.CommandText,
                    command.Parameters.Cast<DbParameter>().Select(static parameter => parameter.Value).ToArray()));
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record CapturedCommand(string CommandText, object?[] Parameters);
}
