using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging.Query;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// Liveness (S9), coverage (R5), retention (S5) and the 30 s cache of the log capture status against a real
/// Postgres. Its own database, emptied before every test: every case relies on an empty
/// <c>LogCaptureStatus</c>.
/// </summary>
[Collection("Postgres")]
public class LogStatusTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "log_status_tests";

    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));

    public Task InitializeAsync() => LogEntrySeed.PrepareDatabaseAsync(fixture, DatabaseName);

    public Task DisposeAsync() => LogEntrySeed.ReleasePoolAsync(fixture, DatabaseName);

    [Fact]
    public async Task Status_ColdStart_ReportsBothSourcesMissingAndIncomplete()
    {
        var status = await ComputeAsync();

        Assert.Equal(Now, status.GeneratedAtUtc);
        Assert.Equal([LogSource.Api, LogSource.Worker], status.Sources.Select(s => s.Source));
        Assert.All(status.Sources, s =>
        {
            Assert.Equal(LogSourceState.Missing, s.State);
            Assert.Null(s.InstanceId);
            Assert.Null(s.StartedUtc);
            Assert.Null(s.LastFlushUtc);
        });
        Assert.False(status.Coverage24h.Complete);
        Assert.Equal(0, status.Coverage24h.RecordedErrors);
        Assert.Null(status.Retention.OldestEntryUtc);
        Assert.False(status.Retention.Overdue);
    }

    [Fact]
    public async Task Status_FirstHeartbeat_IsHealthyForThatSourceOnly()
    {
        await SeedStatusAsync(Row(LogSource.Api, "api00001", Now.AddMinutes(-3), Now.AddSeconds(-10)));

        var status = await ComputeAsync();

        var api = Assert.Single(status.Sources, s => s.Source == LogSource.Api);
        Assert.Equal(LogSourceState.Healthy, api.State);
        Assert.Equal("api00001", api.InstanceId);
        Assert.Equal(Now.AddMinutes(-3), api.StartedUtc);
        Assert.Equal(Now.AddSeconds(-10), api.LastFlushUtc);
        Assert.Equal(LogSourceState.Missing, Assert.Single(status.Sources, s => s.Source == LogSource.Worker).State);
        Assert.Equal(DateTimeKind.Utc, api.LastFlushUtc!.Value.Kind);
    }

    [Fact]
    public async Task Status_EmptiedTable_AfterARestore_IsMissingAgain()
    {
        await SeedStatusAsync(Row(LogSource.Api, "api00001", Now.AddHours(-2), Now.AddSeconds(-5)));
        Assert.Equal(LogSourceState.Healthy, (await ComputeAsync()).Sources[0].State);

        await using (var db = fixture.CreateDbContext(DatabaseName))
        {
            await db.LogCaptureStatuses.ExecuteDeleteAsync();
        }

        var status = await ComputeAsync();
        Assert.All(status.Sources, s => Assert.Equal(LogSourceState.Missing, s.State));
        Assert.False(status.Coverage24h.Complete);
    }

    [Fact]
    public async Task Status_LastRowExpiredAfterSevenDays_IsMissingAndStaysInTheList()
    {
        // Retention deletes a status row 7 days after its last flush; the source must not drop out of the list.
        await SeedStatusAsync(
            Row(LogSource.Worker, "wrk00001", Now.AddDays(-9), Now.AddDays(-8)),
            Row(LogSource.Api, "api00001", Now.AddHours(-1), Now.AddSeconds(-5)));
        await using (var db = fixture.CreateDbContext(DatabaseName))
        {
            var cutoff = Now - RetentionPolicy.LogEntry;
            await db.LogCaptureStatuses.Where(s => s.LastFlushUtc < cutoff).ExecuteDeleteAsync();
        }

        var status = await ComputeAsync();

        Assert.Equal(2, status.Sources.Count);
        var worker = Assert.Single(status.Sources, s => s.Source == LogSource.Worker);
        Assert.Equal(LogSourceState.Missing, worker.State);
    }

    [Fact]
    public async Task Status_CaptureDisabledWithFreshHeartbeat_IsDisabled()
    {
        var row = Row(LogSource.Worker, "wrk00001", Now.AddHours(-1), Now.AddSeconds(-20));
        row.CaptureEnabled = false;
        await SeedStatusAsync(row);

        var worker = Assert.Single((await ComputeAsync()).Sources, s => s.Source == LogSource.Worker);

        Assert.Equal(LogSourceState.Disabled, worker.State);
        Assert.Equal("wrk00001", worker.InstanceId);
    }

    [Theory]
    [InlineData(299, LogSourceState.Healthy)]
    [InlineData(300, LogSourceState.Healthy)]
    [InlineData(301, LogSourceState.Stale)]
    [InlineData(360, LogSourceState.Stale)]
    public async Task Status_LastFlushAge_DecidesStale(int ageSeconds, LogSourceState expected)
    {
        await SeedStatusAsync(Row(LogSource.Worker, "wrk00001", Now.AddHours(-1), Now.AddSeconds(-ageSeconds)));

        var worker = Assert.Single((await ComputeAsync()).Sources, s => s.Source == LogSource.Worker);

        Assert.Equal(expected, worker.State);
    }

    [Fact]
    public async Task Status_StaleBeatsDisabled()
    {
        var row = Row(LogSource.Worker, "wrk00001", Now.AddHours(-1), Now.AddMinutes(-6));
        row.CaptureEnabled = false;
        await SeedStatusAsync(row);

        Assert.Equal(LogSourceState.Stale, Assert.Single((await ComputeAsync()).Sources, s => s.Source == LogSource.Worker).State);
    }

    [Fact]
    public async Task Status_TwoLiveInstancesOfOneSource_ReportsNewest()
    {
        // Deploy overlap: the old instance still writes fresher heartbeats than the new one.
        await SeedStatusAsync(
            Row(LogSource.Worker, "old00001", Now.AddHours(-5), Now.AddSeconds(-1)),
            Row(LogSource.Worker, "new00002", Now.AddMinutes(-2), Now.AddSeconds(-30)),
            Row(LogSource.Api, "api00001", Now.AddHours(-5), Now.AddSeconds(-1)));

        var worker = Assert.Single((await ComputeAsync()).Sources, s => s.Source == LogSource.Worker);

        Assert.Equal("new00002", worker.InstanceId);
        Assert.Equal(Now.AddMinutes(-2), worker.StartedUtc);
        Assert.Equal(Now.AddSeconds(-30), worker.LastFlushUtc);
    }

    [Fact]
    public async Task Status_NewestInstanceStale_IsStaleEvenIfTheOlderOneStillBeats()
    {
        await SeedStatusAsync(
            Row(LogSource.Worker, "old00001", Now.AddHours(-5), Now.AddSeconds(-1)),
            Row(LogSource.Worker, "new00002", Now.AddMinutes(-20), Now.AddMinutes(-10)));

        var worker = Assert.Single((await ComputeAsync()).Sources, s => s.Source == LogSource.Worker);

        Assert.Equal(LogSourceState.Stale, worker.State);
        Assert.Equal("new00002", worker.InstanceId);
    }

    [Fact]
    public async Task Coverage_BothMarkersOlderThan24Hours_IsComplete()
    {
        await SeedHealthyPairAsync(apiMarker: Now.AddHours(-24).AddSeconds(-1), workerMarker: Now.AddDays(-3));
        await SeedErrorsAsync(2);

        var coverage = (await ComputeAsync()).Coverage24h;

        Assert.True(coverage.Complete);
        Assert.Equal(2, coverage.RecordedErrors);
        Assert.Empty(coverage.IncompleteSince);
    }

    [Fact]
    public async Task Coverage_BudgetDropError_IsIncompleteDespiteHealthyLiveness()
    {
        var markerAt = Now.AddMinutes(-40);
        await SeedHealthyPairAsync(apiMarker: Now.AddDays(-3), workerMarker: markerAt);

        var status = await ComputeAsync();

        Assert.All(status.Sources, s => Assert.Equal(LogSourceState.Healthy, s.State));
        Assert.False(status.Coverage24h.Complete);
        var since = Assert.Single(status.Coverage24h.IncompleteSince);
        Assert.Equal(LogSource.Worker, since.Source);
        Assert.Equal(markerAt, since.LastIncompleteAtUtc);
        Assert.Equal(DateTimeKind.Utc, since.LastIncompleteAtUtc.Kind);
    }

    [Fact]
    public async Task Coverage_SimulatedRestore_IsIncompleteFor24Hours()
    {
        // A restore brings fresh status rows whose marker is the restore moment.
        var restoredAt = Now.AddMinutes(-5);
        await SeedHealthyPairAsync(apiMarker: restoredAt, workerMarker: restoredAt);

        Assert.False((await ComputeAsync()).Coverage24h.Complete);
        Assert.Equal(2, (await ComputeAsync()).Coverage24h.IncompleteSince.Count);

        _time.Advance(TimeSpan.FromHours(24).Subtract(TimeSpan.FromMinutes(5)).Add(TimeSpan.FromSeconds(1)));
        await using (var db = fixture.CreateDbContext(DatabaseName))
        {
            // Heartbeats keep the rows fresh; only the marker stays.
            await db.LogCaptureStatuses.ExecuteUpdateAsync(s => s.SetProperty(r => r.LastFlushUtc, _time.GetUtcNow().UtcDateTime));
        }

        Assert.True((await ComputeAsync()).Coverage24h.Complete);
    }

    [Fact]
    public async Task Coverage_UsesTheMarkerOfTheNewestInstanceOnly()
    {
        // The old instance carries a fresh marker, the new one does not: the new one is what counts.
        await SeedStatusAsync(
            Row(LogSource.Api, "api00001", Now.AddDays(-4), Now.AddSeconds(-5)),
            Row(LogSource.Worker, "old00001", Now.AddHours(-40), Now.AddSeconds(-2), Now.AddMinutes(-1)),
            Row(LogSource.Worker, "new00002", Now.AddHours(-30), Now.AddSeconds(-2), Now.AddHours(-30)));

        var coverage = (await ComputeAsync()).Coverage24h;

        Assert.True(coverage.Complete);
    }

    [Fact]
    public async Task Coverage_RecordedErrors_CountsErrorAndCriticalInTheLast24HoursOnly()
    {
        await SeedHealthyPairAsync(Now.AddDays(-3), Now.AddDays(-3));
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddHours(-1), level: LogEntryLevel.Error),
            LogEntrySeed.Entry(Now.AddHours(-2), level: LogEntryLevel.Critical, source: LogSource.Api),
            LogEntrySeed.Entry(Now.AddHours(-3), level: LogEntryLevel.Warning),
            LogEntrySeed.Entry(Now.AddHours(-3), level: LogEntryLevel.Information),
            LogEntrySeed.Entry(Now.AddHours(-24).AddSeconds(-1), level: LogEntryLevel.Error),
            LogEntrySeed.Entry(Now.AddHours(-24), level: LogEntryLevel.Error));

        Assert.Equal(3, (await ComputeAsync()).Coverage24h.RecordedErrors);
    }

    [Theory]
    [InlineData(7 * 24 * 3600 + 2 * 3600, false)]
    [InlineData(7 * 24 * 3600 + 2 * 3600 + 1, true)]
    [InlineData(6 * 24 * 3600, false)]
    public async Task Retention_OverdueFromSevenDaysPlusTwoHours(int ageSeconds, bool overdue)
    {
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddSeconds(-ageSeconds)),
            LogEntrySeed.Entry(Now.AddMinutes(-1)));

        var retention = (await ComputeAsync()).Retention;

        Assert.Equal(Now.AddSeconds(-ageSeconds), retention.OldestEntryUtc);
        Assert.Equal(overdue, retention.Overdue);
    }

    [Fact]
    public async Task Reader_TwoCallsWithin30Seconds_FillOnce_ThenRefreshes()
    {
        var query = Substitute.For<ILogQueryService>();
        query.ComputeStatusAsync(Arg.Any<CancellationToken>()).Returns(_ => View());
        var reader = NewReader(query);

        var first = await reader.ReadAsync(default);
        _time.Advance(TimeSpan.FromSeconds(29));
        var second = await reader.ReadAsync(default);

        Assert.Same(first, second);
        await query.Received(1).ComputeStatusAsync(Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        await reader.ReadAsync(default);
        await query.Received(2).ComputeStatusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reader_ConcurrentCallers_ShareOneFill()
    {
        var gate = new TaskCompletionSource<LogCaptureStatusView>();
        var query = Substitute.For<ILogQueryService>();
        query.ComputeStatusAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        var reader = NewReader(query);

        var readers = Enumerable.Range(0, 5).Select(_ => reader.ReadAsync(default)).ToList();
        gate.SetResult(View());
        await Task.WhenAll(readers);

        await query.Received(1).ComputeStatusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reader_FailedFill_IsNotCached()
    {
        var query = Substitute.For<ILogQueryService>();
        query.ComputeStatusAsync(Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromException<LogCaptureStatusView>(new LogQueryTimeoutException()),
            _ => Task.FromResult(View()));
        var reader = NewReader(query);

        await Assert.ThrowsAsync<LogQueryTimeoutException>(() => reader.ReadAsync(default));
        var recovered = await reader.ReadAsync(default);

        Assert.NotNull(recovered);
        await query.Received(2).ComputeStatusAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reader_Fill_RunsInSuppressionScopeAndInItsOwnScope()
    {
        var suppressed = false;
        var query = Substitute.For<ILogQueryService>();
        query.ComputeStatusAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            suppressed = Logging.LogCaptureSuppression.IsActive;
            return View();
        });
        var reader = NewReader(query);

        await reader.ReadAsync(default);

        Assert.True(suppressed);
        Assert.False(Logging.LogCaptureSuppression.IsActive);
    }

    [Fact]
    public async Task Reader_AgainstRealDatabase_ReturnsTheComputedStatus()
    {
        await SeedHealthyPairAsync(Now.AddDays(-3), Now.AddDays(-3));
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext(DatabaseName));
        services.AddSingleton(new LogViewerOptions());
        services.AddSingleton<TimeProvider>(_time);
        services.AddScoped<ILogQueryService, LogQueryService>();
        services.AddSingleton<ILogStatusReader, LogStatusReader>();
        await using var provider = services.BuildServiceProvider();

        var status = await provider.GetRequiredService<ILogStatusReader>().ReadAsync(default);

        Assert.All(status.Sources, s => Assert.Equal(LogSourceState.Healthy, s.State));
        Assert.True(status.Coverage24h.Complete);
    }

    private static LogCaptureStatusView View() =>
        new(Now, [], new LogCoverage(true, 0, []), new LogRetentionStatus(null, false));

    private LogStatusReader NewReader(ILogQueryService query)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => query);
        return new LogStatusReader(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _time);
    }

    private async Task<LogCaptureStatusView> ComputeAsync()
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await new LogQueryService(db, new LogViewerOptions(), _time).ComputeStatusAsync(default);
    }

    private static LogCaptureStatus Row(LogSource source, string instance, DateTime started, DateTime lastFlush, DateTime? marker = null)
    {
        var row = LogEntrySeed.Status(source, instance, started, lastFlush);
        row.LastIncompleteAtUtc = DateTime.SpecifyKind(marker ?? started, DateTimeKind.Utc);
        return row;
    }

    private Task SeedStatusAsync(params LogCaptureStatus[] rows) => LogEntrySeed.InsertStatusAsync(fixture, DatabaseName, rows);

    private Task SeedHealthyPairAsync(DateTime apiMarker, DateTime workerMarker) =>
        SeedStatusAsync(
            Row(LogSource.Api, "api00001", Now.AddDays(-4), Now.AddSeconds(-5), apiMarker),
            Row(LogSource.Worker, "wrk00001", Now.AddDays(-4), Now.AddSeconds(-5), workerMarker));

    private Task SeedErrorsAsync(int count) =>
        LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            [.. Enumerable.Range(1, count).Select(i => LogEntrySeed.Entry(Now.AddMinutes(-i), level: LogEntryLevel.Error))]);
}
