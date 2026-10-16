using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging;
using EmotePurge.Infrastructure.Logging.Query;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The capture writer against a real Postgres: batches, idempotent retries, record isolation, drop
/// reports, heartbeat and coverage marker, shutdown drain. Its own database (the status-row tests depend
/// on an empty <c>LogCaptureStatus</c>), cleared before every test.
/// </summary>
[Collection("Postgres")]
public class LogCaptureWriterTests(PostgresFixture fixture) : IAsyncLifetime
{
    internal static readonly LogCaptureWriter.Timings Fast = new()
    {
        BatchWindow = TimeSpan.FromMilliseconds(50),
        RetryDelay = TimeSpan.FromMilliseconds(100),
        InitialBackoff = TimeSpan.FromMilliseconds(50),
        MaxBackoff = TimeSpan.FromMilliseconds(200),
        DropReportInterval = TimeSpan.Zero,
    };

    private const string DatabaseName = "log_capture_writer_tests";
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private string ConnectionString => fixture.ConnectionStringFor(DatabaseName);

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
#pragma warning disable EF1002 // constant name, CREATE DATABASE cannot be parameterized
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

        await using var reset = fixture.CreateDbContext(DatabaseName);
        await reset.Database.ExecuteSqlRawAsync("TRUNCATE \"LogEntries\", \"LogCaptureStatus\";");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Timings_DefaultsAreTheSpecValues()
    {
        var timings = LogCaptureWriter.Timings.Default;

        Assert.Equal(500, timings.MaxBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(1), timings.BatchWindow);
        Assert.Equal(TimeSpan.FromSeconds(2), timings.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), timings.InitialBackoff);
        Assert.Equal(TimeSpan.FromSeconds(30), timings.MaxBackoff);
        Assert.Equal(TimeSpan.FromSeconds(60), timings.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), timings.DropReportInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), timings.DrainBudget);
        Assert.Equal(TimeSpan.FromMinutes(5), timings.ConsoleInterval);
        Assert.Equal(64, timings.MaxStatementsPerBatch);
        Assert.Equal(5, PostgresLogCaptureStore.DefaultCommandTimeoutSeconds);
    }

    [Fact]
    public void DataSource_HasItsOwnSmallPoolAndApplicationName()
    {
        using var source = new LogCaptureDataSource(ConnectionString);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(source.DataSource.ConnectionString);

        Assert.Equal(2, builder.MaxPoolSize);
        Assert.Equal("emotepurge-logcapture", builder.ApplicationName);
        Assert.Equal(5, builder.Timeout);
        Assert.Equal(DatabaseName, builder.Database);
    }

    [Fact]
    public void DataSource_ReadsDefaultConnectionFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = ConnectionString })
            .Build();

        using var source = new LogCaptureDataSource(configuration);

        Assert.Equal(DatabaseName, new Npgsql.NpgsqlConnectionStringBuilder(source.DataSource.ConnectionString).Database);
    }

    [Fact]
    public void DropEntry_ListsOnlyReasonsAboveZero_AndCarriesEveryCount()
    {
        var first = new DateTime(2026, 10, 15, 11, 59, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 10, 15, 11, 59, 30, 500, DateTimeKind.Utc);
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new LogDropSnapshot(QueueFull: 3, Budget: 2, CaptureFailed: 0, Reentrant: 0, Rejected: 1, WriteUnconfirmed: 0, first, last);

        var entry = LogCaptureWriter.BuildDropEntry(snapshot, now);

        Assert.Equal("6 log entries were dropped (queue_full=3, budget=2, rejected=1)", entry.Message);
        Assert.Equal("EmotePurge.LogCapture", entry.Category);
        Assert.Equal(LogEntryLevel.Warning, entry.Level);
        Assert.True(entry.IsSynthetic);
        Assert.False(entry.Truncated);
        Assert.Equal(now, entry.TimestampUtc);
        Assert.Equal(7, entry.CaptureId.Version);
        Assert.Null(entry.EventId);
        Assert.Null(entry.Exception);
        using var json = JsonDocument.Parse(entry.PropertiesJson!);
        var root = json.RootElement;
        Assert.Equal(6, root.GetProperty("dropped").GetInt64());
        Assert.Equal(3, root.GetProperty("queuefull").GetInt64());
        Assert.Equal(2, root.GetProperty("budget").GetInt64());
        Assert.Equal(0, root.GetProperty("writeunconfirmed").GetInt64());
        Assert.Equal(0, root.GetProperty("capturefailed").GetInt64());
        Assert.Equal(0, root.GetProperty("reentrant").GetInt64());
        Assert.Equal(1, root.GetProperty("rejected").GetInt64());
        Assert.Equal("2026-10-15T11:59:00.0000000Z", root.GetProperty("firstdroputc").GetString());
        Assert.Equal("2026-10-15T11:59:30.5000000Z", root.GetProperty("lastdroputc").GetString());
        Assert.Equal(9, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Batch_LandsCompletely()
    {
        await using var harness = new Harness(ConnectionString, Fast with { BatchWindow = TimeSpan.FromMilliseconds(200) });
        var logger = harness.Logger("EmotePurge.Worker.Worker");
        await harness.StartAsync();

        for (var i = 0; i < 1234; i++)
        {
            logger.LogWarning(new EventId(7, "Seven"), "Entry {Index} of {Run}", i, "batch");
        }

        var rows = await WaitForEntriesAsync(1234);

        Assert.All(harness.Store.InsertSizes, size => Assert.InRange(size, 1, 500));
        Assert.Equal(1234, rows.Count);
        var messages = rows.Select(r => r.Message).ToHashSet(StringComparer.Ordinal);
        Assert.All(Enumerable.Range(0, 1234), i => Assert.Contains($"Entry {i} of batch", messages));
        var row = rows.Single(r => r.Message == "Entry 17 of batch");
        Assert.Equal(LogSource.Worker, row.Source);
        Assert.Equal(harness.Identity.InstanceId, row.InstanceId);
        Assert.Equal(harness.Identity.ProcessStartedUtc, row.ProcessStartedUtc);
        Assert.Equal(LogEntryLevel.Warning, row.Level);
        Assert.Equal("EmotePurge.Worker.Worker", row.Category);
        Assert.Equal(7, row.EventId);
        Assert.Equal("Seven", row.EventName);
        Assert.False(row.Truncated);
        using var properties = JsonDocument.Parse(row.Properties!);
        Assert.Equal(17, properties.RootElement.GetProperty("index").GetInt32());
        Assert.Equal(0, harness.Drops.PendingTotal);
    }

    [Fact]
    public async Task LostAck_RetryWritesNoDuplicate()
    {
        await using var harness = new Harness(ConnectionString, Fast);
        var lostOnce = new ConcurrentDictionary<bool, bool>();
        harness.Store.OnInsert = (batch, _) =>
            lostOnce.TryAdd(batch[0].IsSynthetic, true) ? HookAction.ExecuteThenThrow : HookAction.Pass;
        var logger = harness.Logger("Test.LostAck");
        harness.Drops.Record(LogDropReason.Budget, DateTime.UtcNow);
        await harness.StartAsync();

        for (var i = 0; i < 40; i++)
        {
            logger.LogInformation("Lost ack {Index}", i);
        }

        await WaitForEntriesAsync(41);
        await harness.StopAsync();

        var rows = await ReadEntriesAsync();
        Assert.Equal(41, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.CaptureId).Distinct().Count());
        var drop = Assert.Single(rows, r => r.Category == "EmotePurge.LogCapture");
        Assert.Equal(1, DropCount(drop, "dropped"));
        Assert.Equal(1, DropCount(drop, "budget"));
        Assert.Equal(0, DropCount(drop, "writeunconfirmed"));
        Assert.Equal(0, harness.Drops.PendingTotal);
        Assert.True(harness.Store.InsertCalls >= 4, "both the batch and the drop entry were written twice");

        // Spec 13.1: the writer's own drop entry, not a hand-seeded one, counts exactly once in the summary.
        await using var db = fixture.CreateDbContext(DatabaseName);
        var summary = await new LogQueryService(db, new LogViewerOptions(), TimeProvider.System)
            .GetSummaryAsync(null, new LogRangeRequest("1h", null, null), default);
        Assert.Equal(new LogDropTotals(Total: 1, QueueFull: 0, Budget: 1, WriteUnconfirmed: 0, CaptureFailed: 0, Reentrant: 0, Rejected: 0), summary.Dropped);
    }

    [Fact]
    public async Task TwoFailures_CountWriteUnconfirmed_NoDuplicates()
    {
        await using var harness = new Harness(ConnectionString, Fast);
        harness.Store.OnInsert = (batch, call) =>
            !batch[0].IsSynthetic && call <= 2 ? HookAction.ExecuteThenThrow : HookAction.Pass;
        var logger = harness.Logger("Test.Unconfirmed");
        for (var i = 0; i < 30; i++)
        {
            logger.LogInformation("Unconfirmed {Index}", i);
        }

        await harness.StartAsync();

        var drop = await WaitForDropEntryAsync();
        Assert.Equal(30, DropCount(drop, "writeunconfirmed"));
        Assert.Equal(30, DropCount(drop, "dropped"));
        var rows = await ReadEntriesAsync();
        Assert.Equal(30, rows.Count(r => r.Category == "Test.Unconfirmed"));
        Assert.Equal(rows.Count, rows.Select(r => r.CaptureId).Distinct().Count());
        Assert.Equal([30, 30], harness.Store.InsertSizes.Take(2));
    }

    [Fact]
    public async Task Isolation_499ValidOneRejected()
    {
        await using var harness = new Harness(ConnectionString, Fast);
        var bad = Entry(category: new string('c', 300), level: LogEntryLevel.Error);
        for (var i = 0; i < 500; i++)
        {
            harness.Enqueue(i == 321 ? bad : Entry(message: $"valid {i}"));
        }

        await harness.StartAsync();

        var drop = await WaitForDropEntryAsync();
        Assert.Equal(1, DropCount(drop, "rejected"));
        Assert.Equal(1, DropCount(drop, "dropped"));
        Assert.Contains("rejected=1", drop.Message, StringComparison.Ordinal);
        var rows = await ReadEntriesAsync();
        Assert.Equal(499, rows.Count(r => r.Message.StartsWith("valid ", StringComparison.Ordinal)));
        Assert.DoesNotContain(rows, r => r.CaptureId == bad.CaptureId);
        Assert.InRange(harness.Store.InsertCalls, 2, 1 + 64);
        // A rejected Error is a lost Error: the coverage marker moved past the process start.
        Assert.True(harness.Coverage.LastIncompleteAtUtc > harness.Identity.ProcessStartedUtc);
    }

    [Fact]
    public async Task Isolation_ManyRejected_StopsAt64Statements()
    {
        await using var harness = new Harness(ConnectionString, Fast);
        for (var i = 0; i < 500; i++)
        {
            harness.Enqueue(i % 5 == 0 ? Entry(category: new string('c', 300)) : Entry(message: $"valid {i}"));
        }

        await harness.StartAsync();

        var drop = await WaitForDropEntryAsync();
        Assert.Equal(64, harness.Store.InsertCalls - 1);
        var stored = (await ReadEntriesAsync()).Count(r => r.Message.StartsWith("valid ", StringComparison.Ordinal));
        Assert.True(stored > 0);
        Assert.Equal(500 - stored, DropCount(drop, "rejected"));
        Assert.True(DropCount(drop, "rejected") >= 100);
    }

    [Fact]
    public async Task TransientError_Retries_DoesNotBisect()
    {
        await using var harness = new Harness(ConnectionString, Fast);
        harness.Store.OnInsert = (_, call) => call == 1 ? HookAction.ThrowBefore : HookAction.Pass;
        for (var i = 0; i < 200; i++)
        {
            harness.Enqueue(Entry(message: $"transient {i}"));
        }

        await harness.StartAsync();
        await WaitForEntriesAsync(200);
        await harness.StopAsync();

        Assert.Equal([200, 200], harness.Store.InsertSizes);
        Assert.Equal(0, harness.Drops.PendingTotal);
        Assert.DoesNotContain(await ReadEntriesAsync(), r => r.Category == "EmotePurge.LogCapture");
    }

    [Fact]
    public async Task NulAndLoneSurrogate_StoredAsReplacementChars()
    {
        await using var harness = new Harness(ConnectionString, Fast);
        var logger = harness.Logger("Test.Strings");
        await harness.StartAsync();

        logger.LogWarning("frame {Frame} end", "a\0b");
        logger.LogWarning("lone \uD800 surrogate");
        logger.LogWarning("neighbour stays");

        var rows = await WaitForEntriesAsync(3);
        var nul = rows.Single(r => r.Message.StartsWith("frame", StringComparison.Ordinal));
        Assert.Equal("frame a␀b end", nul.Message);
        Assert.Equal("a␀b", JsonDocument.Parse(nul.Properties!).RootElement.GetProperty("frame").GetString());
        Assert.Contains(rows, r => r.Message == "lone � surrogate");
        Assert.Contains(rows, r => r.Message == "neighbour stays");
        Assert.Equal(0, harness.Drops.PendingTotal);
    }

    [Fact]
    public async Task Heartbeat_WithoutEntries_UpdatesLastFlush()
    {
        await using var harness = new Harness(ConnectionString, Fast with { HeartbeatInterval = TimeSpan.FromMilliseconds(200) });
        await harness.StartAsync();

        var first = await WaitForStatusAsync(harness.Identity, _ => true);
        var later = await WaitForStatusAsync(harness.Identity, s => s.LastFlushUtc > first.LastFlushUtc);

        Assert.True(later.CaptureEnabled);
        Assert.Equal(harness.Identity.ProcessStartedUtc, later.ProcessStartedUtc);
        Assert.Null(later.CleanShutdownAtUtc);
        Assert.Empty(await ReadEntriesAsync());
    }

    [Fact]
    public async Task DropReport_AtMostOnePer60Seconds()
    {
        await using var harness = new Harness(ConnectionString, Fast with { DropReportInterval = TimeSpan.FromSeconds(60) });
        var logger = harness.Logger("Test.Throttle");
        harness.Drops.Record(LogDropReason.QueueFull, DateTime.UtcNow);
        await harness.StartAsync();
        await WaitForDropEntryAsync();

        harness.Drops.Record(LogDropReason.QueueFull, DateTime.UtcNow);
        logger.LogInformation("first after the second drop");
        await WaitForEntriesAsync(2);
        logger.LogInformation("second after the second drop");
        await WaitForEntriesAsync(3);

        Assert.Single(await ReadEntriesAsync(), r => r.Category == "EmotePurge.LogCapture");
        Assert.Equal(1, harness.Drops.PendingTotal);
    }

    [Fact]
    public async Task Shutdown_DrainsWithin3Seconds()
    {
        await using var harness = new Harness(ConnectionString, LogCaptureWriter.Timings.Default);
        var logger = harness.Logger("Test.Drain");
        await harness.StartAsync();
        await WaitForStatusAsync(harness.Identity, _ => true);

        for (var i = 0; i < 3000; i++)
        {
            logger.LogInformation("Drain {Index}", i);
        }

        var stopwatch = Stopwatch.StartNew();
        await harness.StopAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(3), $"stop took {stopwatch.Elapsed}");
        Assert.Equal(3000, (await ReadEntriesAsync()).Count(r => r.Category == "Test.Drain"));
        var status = await ReadStatusAsync(harness.Identity);
        Assert.NotNull(status!.CleanShutdownAtUtc);
        Assert.DoesNotContain(harness.WriterConsole.Lines, l => l.Message.Contains("not stored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shutdown_HostBudgetAlreadySpent_StillDrains()
    {
        // The host's stop token is shared by every hosted service in turn. The writer stops last, so a slow
        // service ahead of it can hand it a token that is already cancelled; the drain keeps its own budget.
        await using var harness = new Harness(ConnectionString, Fast);
        var logger = harness.Logger("Test.LateStop");
        await harness.StartAsync();
        await WaitForStatusAsync(harness.Identity, _ => true);
        for (var i = 0; i < 200; i++)
        {
            logger.LogInformation("Late stop {Index}", i);
        }

        var stopwatch = Stopwatch.StartNew();
        await harness.StopAsync(new CancellationToken(canceled: true));
        stopwatch.Stop();

        Assert.True(harness.Writer.ExecuteTask!.IsCompleted, "the stop returned before the drain finished");
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(3.5), $"stop took {stopwatch.Elapsed}");
        Assert.Equal(200, (await ReadEntriesAsync()).Count(r => r.Category == "Test.LateStop"));
        var status = await ReadStatusAsync(harness.Identity);
        Assert.NotNull(status!.CleanShutdownAtUtc);
    }

    [Fact]
    public async Task Shutdown_LostErrorInTheDrain_WritesNoCleanShutdown()
    {
        // The error is logged right before the stop; the writer's retry delay keeps its loss inside the drain.
        await using var harness = new Harness(ConnectionString, Fast with { RetryDelay = TimeSpan.FromMilliseconds(500) });
        await harness.StartAsync();
        await WaitForStatusAsync(harness.Identity, _ => true);
        harness.Store.OnInsert = (_, _) => HookAction.ThrowBefore;
        harness.Logger("Test.LostError").LogError("this error never reaches the table");

        await harness.StopAsync();

        var status = await ReadStatusAsync(harness.Identity);
        Assert.Null(status!.CleanShutdownAtUtc);
        Assert.True(status.LastIncompleteAtUtc > harness.Identity.ProcessStartedUtc);
        Assert.Contains(harness.WriterConsole.Lines, l => l.Message.Contains("not stored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriterSql_NeverCaptured_EvenWithEfAndNpgsqlAtDebug()
    {
        var console = new CapturingLoggerProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Debug",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Debug",
                ["Logging:LogLevel:Npgsql"] = "Debug",
            })
            .Build();
        await using var harness = new Harness(ConnectionString, Fast, configure: builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddProvider(console);
        });
        await harness.StartAsync();

        // The application's own context, registered like in Api and Worker: EF takes the logger factory from
        // the container and hands it to the Npgsql data source it builds, so EF and Npgsql log at Debug
        // through the same factory as the capture sink.
        var services = new ServiceCollection();
        services.AddSingleton(harness.Factory);
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(ConnectionString));
        await using (var provider = services.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Channels.CountAsync();
            await db.LogFilters.Select(f => f.Name).ToListAsync();
        }

        // EF Core's Npgsql provider builds its data source without the logger factory, so a positive control:
        // a data source of the same process that does log through the factory stays open while the writer
        // works. If Npgsql logging were process-wide, the writer's SQL would show up through it.
        await using var loggingSource = new Npgsql.NpgsqlDataSourceBuilder(ConnectionString).UseLoggerFactory(harness.Factory).Build();
        await using (var probe = loggingSource.CreateCommand("SELECT count(*) FROM \"LogFilters\""))
        {
            await probe.ExecuteScalarAsync();
        }

        var app = harness.Logger("Test.Application");
        for (var i = 0; i < 25; i++)
        {
            app.LogWarning("application line {Index}", i);
        }

        await WaitForEntriesAsync(25);
        await harness.StopAsync();

        // Logging really was on: the console stand-in saw EF commands and Npgsql's own command log.
        var categories = string.Join(", ", console.Lines.Select(l => $"{l.Category}/{l.Level}").Distinct());
        Assert.True(console.Lines.Any(l => l.Category == "Microsoft.EntityFrameworkCore.Database.Command"), categories);
        Assert.True(console.Lines.Any(l => l.Category.StartsWith("Npgsql", StringComparison.Ordinal) && l.AllText.Contains("LogFilters", StringComparison.Ordinal)), categories);
        // ...but nothing of the writer's own path: no SQL against the capture tables, no log of its pool.
        Assert.DoesNotContain(console.Lines, l => l.AllText.Contains("\"LogEntries\"", StringComparison.Ordinal));
        Assert.DoesNotContain(console.Lines, l => l.AllText.Contains("\"LogCaptureStatus\"", StringComparison.Ordinal));
        Assert.DoesNotContain(console.Lines, l => l.AllText.Contains("emotepurge-logcapture", StringComparison.Ordinal));
        var rows = await ReadEntriesAsync();
        Assert.Equal(25, rows.Count);
        Assert.All(rows, r => Assert.Equal("Test.Application", r.Category));
    }

    [Fact]
    public async Task BudgetDroppedError_MarksIncomplete()
    {
        await using var harness = new Harness(ConnectionString, Fast, budget: 1);
        var logger = harness.Logger("Test.Budget");
        await harness.StartAsync();
        var initial = await WaitForStatusAsync(harness.Identity, _ => true);
        Assert.Equal(harness.Identity.ProcessStartedUtc, initial.LastIncompleteAtUtc);

        logger.LogWarning("uses up the budget");
        var beforeDrop = DateTime.UtcNow;
        logger.LogError("dropped by the budget");

        var drop = await WaitForDropEntryAsync();
        Assert.Equal(1, DropCount(drop, "budget"));
        var status = await WaitForStatusAsync(harness.Identity, s => s.LastIncompleteAtUtc >= beforeDrop);
        Assert.True(status.CaptureEnabled);
    }

    [Fact]
    public async Task FailedErrorBatchAndFailedDropReport_MarkerSurvives()
    {
        // The error batch fails twice (outage), then its drop report fails too and is never stored (the
        // synthetic insert keeps failing, as if the process lost it); heartbeats recover meanwhile.
        await using var harness = new Harness(ConnectionString, Fast with { HeartbeatInterval = TimeSpan.FromMilliseconds(200) });
        var outage = false;
        var dropReportAttempts = 0;
        harness.Store.OnInsert = (batch, _) =>
        {
            if (batch[0].IsSynthetic)
            {
                Interlocked.Increment(ref dropReportAttempts);
                return HookAction.ThrowBefore;
            }

            return Volatile.Read(ref outage) ? HookAction.ThrowBefore : HookAction.Pass;
        };
        harness.Store.FailStatus = () => Volatile.Read(ref outage);
        await harness.StartAsync();
        var beforeOutage = await WaitForStatusAsync(harness.Identity, _ => true);
        Assert.Equal(harness.Identity.ProcessStartedUtc, beforeOutage.LastIncompleteAtUtc);

        var outageStart = DateTime.UtcNow;
        Volatile.Write(ref outage, true);
        harness.Logger("Test.Outage").LogError("error lost in the outage");
        await Eventually(() => Task.FromResult(harness.Store.InsertCalls >= 2 && harness.Drops.PendingTotal > 0));
        Volatile.Write(ref outage, false);
        await Eventually(() => Task.FromResult(Volatile.Read(ref dropReportAttempts) >= 2));

        var recovered = await WaitForStatusAsync(harness.Identity, s => s.LastFlushUtc > beforeOutage.LastFlushUtc);
        var fresher = await WaitForStatusAsync(harness.Identity, s => s.LastFlushUtc > recovered.LastFlushUtc);
        Assert.Equal(0, harness.Drops.PendingTotal);
        Assert.True(fresher.LastIncompleteAtUtc >= outageStart, "the marker stays on the outage after fresh heartbeats");
        Assert.True(fresher.LastIncompleteAtUtc > harness.Identity.ProcessStartedUtc);
        Assert.DoesNotContain(await ReadEntriesAsync(), r => r.Category == "EmotePurge.LogCapture");
    }

    [Fact]
    public async Task MarkerNeverDecreases()
    {
        using var source = new LogCaptureDataSource(ConnectionString);
        var identity = new LogCaptureIdentity(LogSource.Api);
        var store = new PostgresLogCaptureStore(source, identity);
        var start = identity.ProcessStartedUtc;
        var later = start.AddHours(2);

        await store.UpsertStatusAsync(Status(identity, flush: start.AddHours(3), marker: later), CancellationToken.None);
        await store.UpsertStatusAsync(Status(identity, flush: start.AddHours(4), marker: start.AddHours(1)), CancellationToken.None);

        var status = await ReadStatusAsync(identity);
        Assert.Equal(later, status!.LastIncompleteAtUtc);
        Assert.Equal(start.AddHours(4), status.LastFlushUtc);

        await store.UpsertStatusAsync(Status(identity, flush: start.AddHours(5), marker: start.AddHours(6), clean: start.AddHours(5)), CancellationToken.None);
        status = await ReadStatusAsync(identity);
        Assert.Equal(start.AddHours(6), status!.LastIncompleteAtUtc);
        Assert.Equal(start.AddHours(5), status.CleanShutdownAtUtc);
    }

    [Fact]
    public async Task ReadLatestOtherInstance_TakesNewestOfTheSourceExceptOwn()
    {
        using var source = new LogCaptureDataSource(ConnectionString);
        var identity = new LogCaptureIdentity(LogSource.Worker);
        var store = new PostgresLogCaptureStore(source, identity);
        var start = identity.ProcessStartedUtc;
        await SeedStatusAsync(LogSource.Worker, "old00001", start.AddHours(-3), clean: start.AddHours(-2), marker: start.AddDays(-1));
        await SeedStatusAsync(LogSource.Worker, "new00002", start.AddHours(-1), clean: null, marker: start.AddHours(-1));
        await SeedStatusAsync(LogSource.Api, "api00003", start.AddMinutes(-1), clean: start, marker: start);
        await store.UpsertStatusAsync(Status(identity, flush: start, marker: start), CancellationToken.None);

        var latest = await store.ReadLatestOtherInstanceAsync(LogSource.Worker, identity.InstanceId, CancellationToken.None);

        Assert.Equal("new00002", latest!.InstanceId);
        Assert.Null(latest.CleanShutdownAtUtc);
        Assert.Null(await store.ReadLatestOtherInstanceAsync(LogSource.Api, "api00003", CancellationToken.None));
    }

    [Fact]
    public async Task CleanHandover_Within5Min_AdoptsMarker()
    {
        var identity = new LogCaptureIdentity(LogSource.Worker);
        var start = identity.ProcessStartedUtc;
        var predecessorMarker = start.AddDays(-2);
        await SeedStatusAsync(LogSource.Worker, "prev0001", start.AddHours(-6), clean: start.AddMinutes(-4), marker: predecessorMarker);
        await using var harness = new Harness(ConnectionString, Fast, identity: identity);

        await harness.StartAsync();

        var status = await WaitForStatusAsync(identity, _ => true);
        Assert.Equal(predecessorMarker, status.LastIncompleteAtUtc);
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("six-minute pause")]
    [InlineData("capture disabled")]
    [InlineData("older clean instance, newer one still writing")]
    public async Task CrashOrSixMinutePause_UsesProcessStart(string scenario)
    {
        var identity = new LogCaptureIdentity(LogSource.Worker);
        var start = identity.ProcessStartedUtc;
        var marker = start.AddDays(-2);
        switch (scenario)
        {
            case "crash":
                await SeedStatusAsync(LogSource.Worker, "prev0001", start.AddHours(-6), clean: null, marker: marker);
                break;
            case "six-minute pause":
                await SeedStatusAsync(LogSource.Worker, "prev0001", start.AddHours(-6), clean: start.AddMinutes(-6), marker: marker);
                break;
            case "capture disabled":
                await SeedStatusAsync(LogSource.Worker, "prev0001", start.AddHours(-6), clean: start.AddMinutes(-1), marker: marker, enabled: false);
                break;
            default:
                await SeedStatusAsync(LogSource.Worker, "prev0001", start.AddHours(-6), clean: start.AddMinutes(-1), marker: marker);
                await SeedStatusAsync(LogSource.Worker, "prev0002", start.AddMinutes(-30), clean: null, marker: marker);
                break;
        }

        await using var harness = new Harness(ConnectionString, Fast, identity: identity);
        await harness.StartAsync();

        var status = await WaitForStatusAsync(identity, _ => true);
        Assert.Equal(start, status.LastIncompleteAtUtc);
    }

    [Fact]
    public async Task DisabledHeartbeat_WritesCaptureEnabledFalse()
    {
        using var source = new LogCaptureDataSource(ConnectionString);
        var identity = new LogCaptureIdentity(LogSource.Api);
        var heartbeat = new LogCaptureHeartbeat(
            new PostgresLogCaptureStore(source, identity),
            identity,
            TimeProvider.System,
            NullLogger<LogCaptureHeartbeat>.Instance,
            TimeSpan.FromMilliseconds(200));

        await heartbeat.StartAsync(CancellationToken.None);
        try
        {
            var first = await WaitForStatusAsync(identity, _ => true);
            var later = await WaitForStatusAsync(identity, s => s.LastFlushUtc > first.LastFlushUtc);

            Assert.False(later.CaptureEnabled);
            Assert.Equal(identity.ProcessStartedUtc, later.ProcessStartedUtc);
            Assert.Equal(identity.ProcessStartedUtc, later.LastIncompleteAtUtc);
            Assert.Null(later.CleanShutdownAtUtc);
        }
        finally
        {
            await heartbeat.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DisabledHeartbeat_UnreachableDatabase_NeverBlocksStartAndSwallowsErrors()
    {
        using var source = new LogCaptureDataSource("Host=127.0.0.1;Port=1;Database=none;Username=x;Password=y");
        var identity = new LogCaptureIdentity(LogSource.Api);
        var heartbeat = new LogCaptureHeartbeat(
            new PostgresLogCaptureStore(source, identity),
            identity,
            TimeProvider.System,
            NullLogger<LogCaptureHeartbeat>.Instance,
            TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        await heartbeat.StartAsync(CancellationToken.None);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        await Eventually(() => Task.FromResult(heartbeat.FailedBeats >= 2));
        await heartbeat.StopAsync(CancellationToken.None);

        Assert.True(heartbeat.ExecuteTask!.IsCompletedSuccessfully);
    }

    internal static CapturedLogEntry Entry(string message = "m", string category = "Test.Category", LogEntryLevel level = LogEntryLevel.Information) =>
        new(Guid.CreateVersion7(), DateTime.UtcNow, level, category, null, null, message, null, null, null, false, false);

    internal static async Task Eventually(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            if (deadline.Elapsed > limit)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(25);
        }
    }

    private async Task SeedStatusAsync(LogSource source, string instanceId, DateTime processStarted, DateTime? clean, DateTime marker, bool enabled = true)
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        db.LogCaptureStatuses.Add(new LogCaptureStatus
        {
            Source = source,
            InstanceId = instanceId,
            ProcessStartedUtc = processStarted,
            LastFlushUtc = clean ?? processStarted,
            CaptureEnabled = enabled,
            LastIncompleteAtUtc = marker,
            CleanShutdownAtUtc = clean,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<LogEntry>> ReadEntriesAsync()
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await db.LogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<List<LogEntry>> WaitForEntriesAsync(int count)
    {
        List<LogEntry> rows = [];
        await Eventually(async () => (rows = await ReadEntriesAsync()).Count >= count);
        return rows;
    }

    private async Task<LogEntry> WaitForDropEntryAsync()
    {
        LogEntry? drop = null;
        await Eventually(async () => (drop = (await ReadEntriesAsync()).FirstOrDefault(r => r.Category == "EmotePurge.LogCapture")) is not null);
        return drop!;
    }

    private async Task<LogCaptureStatus?> ReadStatusAsync(LogCaptureIdentity identity)
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await db.LogCaptureStatuses.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Source == identity.Source && s.InstanceId == identity.InstanceId);
    }

    private async Task<LogCaptureStatus> WaitForStatusAsync(LogCaptureIdentity identity, Func<LogCaptureStatus, bool> predicate)
    {
        LogCaptureStatus? status = null;
        await Eventually(async () => (status = await ReadStatusAsync(identity)) is not null && predicate(status));
        return status!;
    }

    private static long DropCount(LogEntry drop, string property) =>
        JsonDocument.Parse(drop.Properties!).RootElement.GetProperty(property).GetInt64();

    private static LogCaptureStatusWrite Status(LogCaptureIdentity identity, DateTime flush, DateTime marker, DateTime? clean = null) =>
        new(identity.Source, identity.InstanceId, identity.ProcessStartedUtc, flush, true, marker, clean);

    /// <summary>
    /// Real outages: a container of its own is paused, so the shared "Postgres" collection is never
    /// affected; the collection runs alone, which keeps the latency measurement free of neighbours.
    /// </summary>
    [Collection(LogCaptureOutageCollection.Name)]
    public class Outage(OutagePostgres postgres) : IAsyncLifetime
    {
        public async Task InitializeAsync()
        {
            await using var db = postgres.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"LogEntries\", \"LogCaptureStatus\";");
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task PostgresPaused_LogP99StaysUnder1ms_ThenDropEntryFirst()
        {
            const int Measured = 20_000;
            await using var harness = new Harness(
                postgres.ConnectionString,
                Fast with { RetryDelay = TimeSpan.FromMilliseconds(200), InitialBackoff = TimeSpan.FromMilliseconds(200), MaxBackoff = TimeSpan.FromMilliseconds(500) },
                commandTimeoutSeconds: 1,
                connectTimeoutSeconds: 1);
            var logger = harness.Logger("Test.Outage");
            await harness.StartAsync();

            // Warm-up: the whole logging path is JIT-compiled and stored before the outage starts.
            for (var i = 0; i < 2000; i++)
            {
                logger.LogInformation("warm-up {Index} of {Phase}", i, "warm");
            }

            var lastIdBeforeOutage = (await WaitForRowsAsync(rows => rows.Count >= 2000)).Max(r => r.Id);

            var latencies = new long[Measured];
            await postgres.PauseAsync();
            try
            {
                for (var i = 0; i < Measured; i++)
                {
                    var started = Stopwatch.GetTimestamp();
                    logger.LogInformation("outage {Index} of {Phase}", i, "outage");
                    latencies[i] = Stopwatch.GetTimestamp() - started;
                }

                // The batch taken at the start of the outage has failed twice by now.
                await Eventually(() => Task.FromResult(harness.WriterConsole.Lines.Any(l => l.Message.Contains("write_unconfirmed", StringComparison.Ordinal))), TimeSpan.FromSeconds(60));
            }
            finally
            {
                await postgres.UnpauseAsync();
            }

            Array.Sort(latencies);
            var p99 = Stopwatch.GetElapsedTime(0, latencies[(int)(Measured * 0.99)]);
            Assert.True(p99 < TimeSpan.FromMilliseconds(1), $"Log() p99 during the outage was {p99.TotalMilliseconds:F3} ms");

            // A batch that hung when the container froze may still commit after the unpause: Postgres reads the
            // statement from its socket buffer. That is exactly "write_unconfirmed — possibly stored". Everything
            // the writer itself wrote after the recovery comes after the drop report, though.
            List<LogEntry> fromBuffer = [];
            LogEntry? drop = null;
            await WaitForRowsAsync(rows =>
            {
                var unconfirmedIds = harness.Store.NeverConfirmedIds();
                fromBuffer = rows.Where(r => r.Id > lastIdBeforeOutage && !unconfirmedIds.Contains(r.CaptureId)).ToList();
                drop = fromBuffer.FirstOrDefault(r => r.Category == "EmotePurge.LogCapture");
                return drop is not null
                    && fromBuffer.Count(r => r.Message.StartsWith("outage ", StringComparison.Ordinal)) + DropCount(drop, "dropped") == Measured;
            });

            Assert.Same(drop, fromBuffer[0]);
            Assert.Single(fromBuffer, r => r.Category == "EmotePurge.LogCapture");
            Assert.Equal(harness.Store.NeverConfirmedIds().Count, DropCount(drop!, "writeunconfirmed"));
            Assert.True(DropCount(drop!, "queuefull") > 0);
            Assert.True(DropCount(drop!, "writeunconfirmed") > 0);
            Assert.Equal(DropCount(drop!, "dropped"), DropCount(drop!, "queuefull") + DropCount(drop!, "writeunconfirmed"));
            Assert.Equal(0, harness.Drops.PendingTotal);
            Assert.Contains("queue_full=", drop!.Message, StringComparison.Ordinal);
            Assert.Contains("write_unconfirmed=", drop.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("budget=", drop.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Stop_WithPostgresPaused_ReturnsWithinDrainBudget()
        {
            await using var harness = new Harness(postgres.ConnectionString, LogCaptureWriter.Timings.Default);
            var logger = harness.Logger("Test.Stop");
            await harness.StartAsync();
            await Eventually(async () =>
            {
                await using var db = postgres.CreateDbContext();
                return await db.LogCaptureStatuses.AnyAsync();
            });

            TimeSpan elapsed;
            await postgres.PauseAsync();
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    logger.LogError("stuck {Index}", i);
                }

                // The writer has taken the entries: its batch now hangs in the 5 s command timeout.
                await Eventually(() => Task.FromResult(harness.Buffer.Reader.Count == 0));
                var stopwatch = Stopwatch.StartNew();
                await harness.StopAsync();
                elapsed = stopwatch.Elapsed;
            }
            finally
            {
                await postgres.UnpauseAsync();
            }

            Assert.True(elapsed <= TimeSpan.FromSeconds(3.5), $"stop took {elapsed}");
            var line = Assert.Single(harness.WriterConsole.Lines, l => l.Message.Contains("not stored", StringComparison.Ordinal));
            Assert.Contains("100", line.Message, StringComparison.Ordinal);
        }

        private async Task<List<LogEntry>> WaitForRowsAsync(Func<List<LogEntry>, bool> predicate)
        {
            List<LogEntry> rows = [];
            await Eventually(
                async () =>
                {
                    await using var db = postgres.CreateDbContext();
                    rows = await db.LogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
                    return predicate(rows);
                },
                TimeSpan.FromSeconds(60));
            return rows;
        }
    }
}

/// <summary>A Postgres container of its own for the outage tests (paused and resumed by them).</summary>
public sealed class OutagePostgres : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("emotepurge")
        .WithUsername("emotepurge")
        .WithPassword("emotepurge-test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public Task PauseAsync() => _container.PauseAsync();

    public Task UnpauseAsync() => _container.UnpauseAsync();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LogCaptureOutageCollection : ICollectionFixture<OutagePostgres>
{
    public const string Name = "LogCaptureOutage";
}

/// <summary>What a <see cref="HookedStore"/> does with one insert call.</summary>
internal enum HookAction
{
    Pass,

    /// <summary>Fails before anything reaches Postgres (a transient error such as a dropped connection).</summary>
    ThrowBefore,

    /// <summary>Commits, then fails like a lost acknowledgement.</summary>
    ExecuteThenThrow,
}

/// <summary>Store-level hook around the real store: the writer's own data source is raw Npgsql, so EF interceptors never see it.</summary>
internal sealed class HookedStore(ILogCaptureStore inner) : ILogCaptureStore
{
    private int _insertCalls;

    public ConcurrentQueue<int> InsertSizes { get; } = new();

    public int InsertCalls => Volatile.Read(ref _insertCalls);

    public Func<IReadOnlyList<CapturedLogEntry>, int, HookAction>? OnInsert { get; set; }

    public Func<bool>? FailStatus { get; set; }

    /// <summary>Every insert call with the ids it carried and whether the writer saw it succeed.</summary>
    public ConcurrentQueue<(Guid[] Ids, bool Succeeded)> Attempts { get; } = new();

    public async Task InsertAsync(IReadOnlyList<CapturedLogEntry> batch, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _insertCalls);
        InsertSizes.Enqueue(batch.Count);
        var ids = batch.Select(e => e.CaptureId).ToArray();
        try
        {
            switch (OnInsert?.Invoke(batch, call) ?? HookAction.Pass)
            {
                case HookAction.ThrowBefore:
                    throw new IOException("simulated: connection lost before the insert");
                case HookAction.ExecuteThenThrow:
                    await inner.InsertAsync(batch, ct);
                    throw new IOException("simulated: acknowledgement lost after the commit");
                default:
                    await inner.InsertAsync(batch, ct);
                    break;
            }
        }
        catch (Exception)
        {
            Attempts.Enqueue((ids, false));
            throw;
        }

        Attempts.Enqueue((ids, true));
    }

    /// <summary>Ids the writer never saw stored: they were counted as write_unconfirmed (or rejected).</summary>
    public HashSet<Guid> NeverConfirmedIds()
    {
        var attempts = Attempts.ToArray();
        var confirmed = attempts.Where(a => a.Succeeded).SelectMany(a => a.Ids).ToHashSet();
        return attempts.Where(a => !a.Succeeded).SelectMany(a => a.Ids).Where(id => !confirmed.Contains(id)).ToHashSet();
    }

    public Task UpsertStatusAsync(LogCaptureStatusWrite status, CancellationToken ct) =>
        FailStatus?.Invoke() == true ? Task.FromException(new IOException("simulated: database unreachable")) : inner.UpsertStatusAsync(status, ct);

    public Task<LogCaptureStatusWrite?> ReadLatestOtherInstanceAsync(LogSource source, string ownInstanceId, CancellationToken ct) =>
        inner.ReadLatestOtherInstanceAsync(source, ownInstanceId, ct);
}

/// <summary>
/// One capture pipeline wired by hand: sink (real provider) → buffer → writer → hooked real store, with a
/// console stand-in for the writer's own log lines.
/// </summary>
internal sealed class Harness : IAsyncDisposable
{
    private bool _started;
    private bool _stopped;

    public Harness(
        string connectionString,
        LogCaptureWriter.Timings timings,
        int budget = 1_000_000,
        LogCaptureIdentity? identity = null,
        Action<ILoggingBuilder>? configure = null,
        int commandTimeoutSeconds = PostgresLogCaptureStore.DefaultCommandTimeoutSeconds,
        int connectTimeoutSeconds = LogCaptureDataSource.DefaultConnectTimeoutSeconds,
        IServiceProvider? notifierServices = null)
    {
        Identity = identity ?? new LogCaptureIdentity(LogSource.Worker);
        Budget = new LogCaptureBudget(budget, TimeProvider.System);
        Provider = new LogCaptureProvider(Buffer, Budget, Drops, Coverage, TimeProvider.System);
        Factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            configure?.Invoke(builder);
            builder.AddProvider(Provider);
        });
        DataSource = new LogCaptureDataSource(connectionString, connectTimeoutSeconds);
        Store = new HookedStore(new PostgresLogCaptureStore(DataSource, Identity) { CommandTimeoutSeconds = commandTimeoutSeconds });
        var writerLoggers = LoggerFactory.Create(builder => builder.AddProvider(WriterConsole));
        // Without notifierServices the hint resolves no publisher and goes nowhere; the viewer's suppression
        // test passes a provider with a real Redis publisher.
        Notifier = new LogsAppendedNotifier(notifierServices ?? Substitute.For<IServiceProvider>(), TimeProvider.System);
        Writer = new LogCaptureWriter(
            Buffer,
            Drops,
            Coverage,
            Identity,
            Store,
            Notifier,
            TimeProvider.System,
            configure is null ? writerLoggers.CreateLogger<LogCaptureWriter>() : Factory.CreateLogger<LogCaptureWriter>(),
            timings);
    }

    public LogCaptureBuffer Buffer { get; } = new();

    public LogCaptureBudget Budget { get; }

    public LogCaptureDropCounters Drops { get; } = new();

    public LogCaptureCoverage Coverage { get; } = new();

    public LogCaptureIdentity Identity { get; }

    public LogCaptureProvider Provider { get; }

    public ILoggerFactory Factory { get; }

    public LogCaptureDataSource DataSource { get; }

    public HookedStore Store { get; }

    public LogsAppendedNotifier Notifier { get; }

    public LogCaptureWriter Writer { get; }

    public CapturingLoggerProvider WriterConsole { get; } = new();

    public ILogger Logger(string category) => Factory.CreateLogger(category);

    public void Enqueue(CapturedLogEntry entry)
    {
        Assert.True(Buffer.TryReserveSlot());
        Assert.True(Buffer.TryCommit(entry, 2L * entry.Message.Length));
    }

    public async Task StartAsync()
    {
        _started = true;
        await Writer.StartAsync(CancellationToken.None);
    }

    public Task StopAsync() => StopAsync(CancellationToken.None);

    public async Task StopAsync(CancellationToken hostToken)
    {
        if (_started && !_stopped)
        {
            _stopped = true;
            await Writer.StopAsync(hostToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Factory.Dispose();
        await DataSource.DisposeAsync();
    }
}
