using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging.Query;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Opt-in performance gate of the admin log viewer's read paths (spec 2026-10-14, section 13.3):
//
//   EMOTEPURGE_PERF_GATES=1 dotnet test tests/EmotePurge.Infrastructure.Tests \
//     --filter "FullyQualifiedName~LogQueryPerformanceGateTests" --logger "console;verbosity=detailed"
//
// A throwaway Postgres 16 Alpine with the migration schema holds the sizing worst case of the spec
// (2 sources x 20,000 entries x 8 days = 320,000 rows) from ONE INSERT ... SELECT generate_series; the
// statement is written to the test output so the same data can be built against another database. The real
// services run (LogQueryService, LogStatusReader), never hand-written SQL: an interceptor records the
// statements they send, and each case replays the last one under EXPLAIN (ANALYZE, BUFFERS).
//
// Warm: two untimed runs, then the median of five. A failing limit is reported, never relaxed here.
[Collection("PerformanceGate")]
[Trait("Category", "PerformanceGate")]
public sealed class LogQueryPerformanceGateTests(ITestOutputHelper output)
{
    private const int Rows = 320_000;
    private const int WarmupRuns = 2;
    private const int MeasuredRuns = 5;

    private static readonly DateTime NowUtc = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly LogRangeRequest Last24Hours = new("24h", null, null);
    private static readonly LogRangeRequest Last7Days = new("7d", null, null);

    // 90 / 9 / 1 % Information / Warning / Error, properties on 60 %, exceptions on 2 %, spread over 8 days
    // ending at the fixed "now". Two process instances per source (the second one is the newest, so
    // instance:latest has real work). Pseudo-random via multiplicative hashes of the row number so level,
    // properties and category are not correlated with time.
    private const string SeedStatement = """
        INSERT INTO "LogEntries" ("CaptureId", "TimestampUtc", "Source", "InstanceId", "ProcessStartedUtc", "Level", "Category", "EventId", "EventName", "Message", "ExceptionType", "Exception", "Properties", "Truncated")
        SELECT gen_random_uuid(),
               g.ts,
               g.src,
               CASE WHEN g.src = 1 THEN (CASE WHEN g.i <= 192000 THEN 'aaaa0001' ELSE 'aaaa0002' END)
                    ELSE (CASE WHEN g.i <= 288000 THEN 'wwww0001' ELSE 'wwww0002' END) END,
               CASE WHEN g.src = 1 THEN (CASE WHEN g.i <= 192000 THEN TIMESTAMPTZ '2026-10-07 11:00:00+00' ELSE TIMESTAMPTZ '2026-10-12 07:00:00+00' END)
                    ELSE (CASE WHEN g.i <= 288000 THEN TIMESTAMPTZ '2026-10-07 11:00:00+00' ELSE TIMESTAMPTZ '2026-10-14 17:00:00+00' END) END,
               CASE WHEN (g.i * 7919) % 100 = 0 THEN 4 WHEN (g.i * 7919) % 100 < 10 THEN 3 ELSE 2 END,
               (ARRAY['EmotePurge.Worker.SevenTv.SevenTvEventClient', 'EmotePurge.Worker.TwitchChatManager', 'Microsoft.Hosting.Lifetime',
                      'EmotePurge.Infrastructure.Services.EmoteService', 'EmotePurge.Worker.UsageFlushWorker', 'TwitchLib.Client.TwitchClient',
                      'EmotePurge.Api.Endpoints.AuthEndpoints', 'EmotePurge.Worker.SevenTv.SevenTvPeriodicResyncWorker',
                      'EmotePurge.Infrastructure.Services.ChannelService', 'EmotePurge.Worker.DataRetentionWorker'])[1 + (g.i * 31) % 10],
               CASE WHEN g.i % 17 = 0 THEN 100 + g.i % 7 END,
               CASE WHEN g.i % 17 = 0 THEN 'LogEvent' || (g.i % 7) END,
               CASE g.i % 20
                   WHEN 7 THEN 'Connection timeout while syncing set ' || g.i
                   WHEN 8 THEN 'Joined channel ch' || ((g.i * 17) % 200) || ' (' || g.i || ')'
                   WHEN 9 THEN 'Flushed ' || (g.i % 400) || ' usage rows'
                   ELSE 'Processed batch ' || g.i || ' of ' || (g.i % 50) || ' items in ' || (g.i % 900) || ' ms'
               END,
               CASE WHEN (g.i * 13) % 50 = 0 THEN 'System.InvalidOperationException' END,
               CASE WHEN (g.i * 13) % 50 = 0 THEN 'System.InvalidOperationException: sequence contains no elements' || E'\n' || repeat('   at EmotePurge.Worker.Something.Run() in /src/Something.cs:line 42' || E'\n', 20) END,
               CASE WHEN (g.i * 31) % 10 < 6 THEN jsonb_build_object('channel', 'ch' || ((g.i * 17) % 200), 'emoteId', md5(g.i::text), 'count', g.i % 400) END,
               false
        FROM (SELECT i,
                     TIMESTAMPTZ '2026-10-15 12:00:00+00' - interval '8 days' + (i - 1) * (interval '8 days' / 320000) AS ts,
                     CASE WHEN i % 3 = 0 THEN 1::smallint ELSE 2::smallint END AS src
              FROM generate_series(1::bigint, 320000::bigint) AS i) AS g
        """;

    [PerformanceGateFact]
    public async Task LogQueries_OnTheWorstCaseCorpus_StayWithinTheSpecLimits()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("emotepurge")
            .WithUsername("emotepurge")
            .WithPassword("emotepurge-test")
            .Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        await SeedAsync(connectionString);

        var results = new List<CaseResult>();

        foreach (var (label, filter) in PageFilters())
        {
            foreach (var (rangeLabel, range) in new[] { ("24h", Last24Hours), ("7d", Last7Days) })
            {
                results.Add(await MeasureAsync(
                    connectionString,
                    $"page {rangeLabel} [{label}]",
                    200,
                    (service, ct) => service.GetPageAsync(filter, range, null, 100, ct)));
            }
        }

        results.Add(await MeasureAsync(
            connectionString, "summary 24h [no filter]", 300, (service, ct) => service.GetSummaryAsync(null, Last24Hours, ct)));
        results.Add(await MeasureAsync(
            connectionString, "summary 7d [free text: timeout]", 800, (service, ct) => service.GetSummaryAsync("timeout", Last7Days, ct)));
        results.Add(await MeasureAsync(
            connectionString, "keysOnly 7d [no filter]", 200, (service, ct) => service.GetKeysAsync(null, NowUtc.AddDays(-7), null, ct)));
        results.Add(await MeasureAsync(
            connectionString, "keysOnly 7d [level>=warn]", 200, (service, ct) => service.GetKeysAsync("level>=warn", NowUtc.AddDays(-7), null, ct)));

        // Not part of the spec table; measured to show where the summary's time goes.
        results.Add(await MeasureAsync(
            connectionString, "info: summary 7d [no filter]", null, (service, ct) => service.GetSummaryAsync(null, Last7Days, ct)));
        results.Add(await MeasureAsync(
            connectionString, "info: summary 24h [free text: timeout]", null, (service, ct) => service.GetSummaryAsync("timeout", Last24Hours, ct)));

        // The cached status reader is rebuilt per run, so every run is a cold fill (error count 24 h plus liveness).
        results.Add(await MeasureStatusAsync(connectionString));
        // instance:latest is a filter on the list; the spec's 50 ms covers the status read, this one is held to the page limit.
        results.Add(await MeasureAsync(
            connectionString, "page 24h [instance:latest]", 200, (service, ct) => service.GetPageAsync("instance:latest", Last24Hours, null, 100, ct)));

        output.WriteLine("==== RESULTS (median of " + MeasuredRuns + " after " + WarmupRuns + " warm-ups) ====");
        foreach (var result in results)
        {
            output.WriteLine(result.Summary());
        }

        var failures = results.Where(r => r.Limit is { } limit && r.MedianMs > limit).Select(r => r.Summary()).ToList();
        Assert.True(failures.Count == 0, "Limits exceeded:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<(string Label, string? Filter)> PageFilters()
    {
        yield return ("no filter", null);
        yield return ("level>=warn", "level>=warn");
        yield return ("cat:SevenTv*", "cat:SevenTv*");
        yield return ("free text: timeout", "timeout");
        yield return ("channel:ch7", "channel:ch7");
        yield return ("negation: -cat:SevenTv*", "-cat:SevenTv*");
    }

    private async Task SeedAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();

        output.WriteLine("---- seed statement (verbatim) ----");
        output.WriteLine(SeedStatement);
        output.WriteLine("---- end seed statement ----");

        var watch = Stopwatch.StartNew();
        await db.Database.ExecuteSqlRawAsync(SeedStatement);
        db.LogCaptureStatuses.AddRange(
            Status(LogSource.Api, "aaaa0001", new DateTime(2026, 10, 7, 11, 0, 0, DateTimeKind.Utc), shutdown: true),
            Status(LogSource.Api, "aaaa0002", new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc), shutdown: false),
            Status(LogSource.Worker, "wwww0001", new DateTime(2026, 10, 7, 11, 0, 0, DateTimeKind.Utc), shutdown: true),
            Status(LogSource.Worker, "wwww0002", new DateTime(2026, 10, 14, 17, 0, 0, DateTimeKind.Utc), shutdown: false));
        await db.SaveChangesAsync();

        // Production tables are vacuumed and analysed by autovacuum; give the planner the same footing.
        await db.Database.ExecuteSqlRawAsync("VACUUM ANALYZE \"LogEntries\"");
        await db.Database.ExecuteSqlRawAsync("VACUUM ANALYZE \"LogCaptureStatus\"");
        var count = await db.LogEntries.LongCountAsync();
        output.WriteLine($"Seeded {count} LogEntries rows in {watch.ElapsedMilliseconds} ms (expected {Rows}).");
        Assert.Equal(Rows, count);
    }

    private static LogCaptureStatus Status(LogSource source, string instance, DateTime started, bool shutdown)
    {
        var row = LogEntrySeed.Status(source, instance, started, shutdown ? NowUtc.AddHours(-1) : NowUtc.AddSeconds(-20));
        row.CleanShutdownAtUtc = shutdown ? NowUtc.AddHours(-1) : null;
        return row;
    }

    private async Task<CaseResult> MeasureAsync<T>(
        string connectionString, string label, double? limitMs, Func<LogQueryService, CancellationToken, Task<T>> call)
    {
        var times = new List<double>();
        var passes = new List<List<double>>();
        IReadOnlyList<RecordedCommand> lastCommands = [];
        for (var run = 0; run < WarmupRuns + MeasuredRuns; run++)
        {
            var recorder = new CommandRecorder();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).AddInterceptors(recorder).Options;
            await using var db = new AppDbContext(options);
            var service = new LogQueryService(db, new LogViewerOptions(), new FakeTimeProvider(new DateTimeOffset(NowUtc)));

            var timer = Stopwatch.StartNew();
            var result = await call(service, CancellationToken.None);
            timer.Stop();
            AssertNotEmpty(label, result);

            lastCommands = recorder.Commands;
            if (run >= WarmupRuns)
            {
                times.Add(timer.Elapsed.TotalMilliseconds);
                passes.Add(recorder.Commands.Select(c => c.DurationMs).ToList());
            }
        }

        var explain = await ExplainAsync(connectionString, lastCommands);
        return Finish(label, limitMs, times, passes, explain);
    }

    private async Task<CaseResult> MeasureStatusAsync(string connectionString)
    {
        var times = new List<double>();
        var passes = new List<List<double>>();
        IReadOnlyList<RecordedCommand> lastCommands = [];
        for (var run = 0; run < WarmupRuns + MeasuredRuns; run++)
        {
            var recorder = new CommandRecorder();
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString).AddInterceptors(recorder));
            services.AddSingleton(new LogViewerOptions());
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(NowUtc)));
            services.AddScoped<ILogQueryService, LogQueryService>();
            services.AddSingleton<ILogStatusReader, LogStatusReader>();
            await using var provider = services.BuildServiceProvider();

            var timer = Stopwatch.StartNew();
            var status = await provider.GetRequiredService<ILogStatusReader>().ReadAsync(CancellationToken.None);
            timer.Stop();
            Assert.Equal(2, status.Sources.Count);
            Assert.True(status.Coverage24h.RecordedErrors > 0, "Invalid run: no errors counted in 24 h.");

            lastCommands = recorder.Commands;
            if (run >= WarmupRuns)
            {
                times.Add(timer.Elapsed.TotalMilliseconds);
                passes.Add(recorder.Commands.Select(c => c.DurationMs).ToList());
            }
        }

        var explain = await ExplainAsync(connectionString, lastCommands);
        return Finish("status [24 h error count + liveness]", 50, times, passes, explain);
    }

    // A run that returned nothing measured a smaller problem: invalid, not fast.
    private static void AssertNotEmpty<T>(string label, T result)
    {
        switch (result)
        {
            case LogPage page:
                Assert.True(page.Entries.Count > 0, $"Invalid run, empty page: {label}");
                break;
            case LogKeyPage keys:
                Assert.True(keys.Keys.Count > 0, $"Invalid run, no keys: {label}");
                break;
            case LogSummary summary:
                Assert.True(summary.Total > 0, $"Invalid run, empty summary: {label}");
                break;
        }
    }

    private CaseResult Finish(string label, double? limitMs, List<double> times, List<List<double>> passes, string explain)
    {
        var order = Enumerable.Range(0, times.Count).OrderBy(i => times[i]).ToList();
        var medianIndex = order[order.Count / 2];
        var result = new CaseResult(label, limitMs, times[medianIndex], times, passes[medianIndex]);
        output.WriteLine("==== CASE: " + label + " ====");
        output.WriteLine(result.Summary());
        output.WriteLine(explain);
        return result;
    }

    private static async Task<string> ExplainAsync(string connectionString, IReadOnlyList<RecordedCommand> commands)
    {
        var text = new StringBuilder();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var index = 0;
        foreach (var recorded in commands)
        {
            index++;
            text.AppendLine($"-- statement {index}/{commands.Count} (executed in {recorded.DurationMs:F1} ms)");
            text.AppendLine(recorded.Text);
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + recorded.Text, connection);
            foreach (var parameter in recorded.Parameters)
            {
                command.Parameters.Add(parameter.Clone());
            }

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                text.AppendLine(reader.GetString(0));
            }
        }

        return text.ToString();
    }

    private sealed record CaseResult(string Label, double? Limit, double MedianMs, List<double> AllMs, List<double> PassMs)
    {
        public string Summary()
        {
            var limit = Limit is { } l ? $"limit {l:F0} ms {(MedianMs <= l ? "PASS" : "FAIL")}" : "no limit (info)";
            var runs = string.Join(", ", AllMs.Select(ms => ms.ToString("F1", CultureInfo.InvariantCulture)));
            var passes = PassMs.Count > 1 ? "; statements ms [" + string.Join(", ", PassMs.Select(ms => ms.ToString("F1", CultureInfo.InvariantCulture))) + "]" : string.Empty;
            return $"{Label}: median {MedianMs:F1} ms, {limit}; runs [{runs}]{passes}";
        }
    }

    private sealed record RecordedCommand(string Text, IReadOnlyList<NpgsqlParameter> Parameters, double DurationMs);

    // Records the data statements the service sends (not the SET/transaction plumbing), with their parameters.
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<RecordedCommand> _commands = [];

        public IReadOnlyList<RecordedCommand> Commands => _commands;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Record(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        {
            Record(command, eventData);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command, CommandExecutedEventData eventData)
        {
            var parameters = command.Parameters.OfType<NpgsqlParameter>().Select(p => p.Clone()).ToList();
            _commands.Add(new RecordedCommand(command.CommandText, parameters, eventData.Duration.TotalMilliseconds));
        }
    }
}
