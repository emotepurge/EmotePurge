using System.Collections;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Opt-in response performance gate for GET /api/admin/dashboard/load at 24h (spec 2026-10-11, section 11,
// "B-Response-Performance-Gate"): the in-process half. The HTTP half runs against the container in B6.
//
//   EMOTEPURGE_PERF_GATES=1 dotnet test tests/EmotePurge.Infrastructure.Tests -c Release --filter Category=PerformanceGate
//   EMOTEPURGE_PERF_INTERVAL_SECONDS=30 ...   (optional: 30 s cadence = 5,760 rows per source instead of 1 s)
//
// Deliberately not in the shared "Postgres" collection: it owns a container (started only when the gate
// actually runs, so a skipped run costs nothing) and a non-parallel collection, because the allocation
// figure is process-wide and any concurrently running test would be counted into it.
[CollectionDefinition("PerformanceGate", DisableParallelization = true)]
public sealed class PerformanceGateCollection;

[Collection("PerformanceGate")]
[Trait("Category", "PerformanceGate")]
public sealed class AdminDashboardLoadPerformanceGateTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int Iterations = 5;
    private const double MaxMedianMilliseconds = 400;
    private const long MaxPayloadBytes = 256 * 1024;
    private const long MaxAllocatedBytes = 64L * 1024 * 1024;

    private static readonly DateTime NowUtc = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("emotepurge")
        .WithUsername("emotepurge")
        .WithPassword("emotepurge-test")
        .Build();

    private readonly RowCountingInterceptor _rows = new();
    private string _connectionString = string.Empty;
    private long _seeded;
    private int _intervalSeconds = 1;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        var configured = Environment.GetEnvironmentVariable("EMOTEPURGE_PERF_INTERVAL_SECONDS");
        _intervalSeconds = int.TryParse(configured, out var parsed) && parsed > 0 ? parsed : 1;

        var watch = Stopwatch.StartNew();
        await SystemLoadSampleSeed.SeedAsync(_connectionString, NowUtc, _intervalSeconds);
        await using var count = new AppDbContext(options);
        _seeded = await count.SystemLoadSamples.LongCountAsync();
        output.WriteLine($"Seeded {_seeded} SystemLoadSamples rows (interval {_intervalSeconds} s, gap {SystemLoadSampleSeed.GapLength.TotalMinutes} min) in {watch.ElapsedMilliseconds} ms");
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [PerformanceGateFact]
    public async Task Load24h_Uncached_StaysWithinTimeSizeAndAllocationBudget()
    {
        await using var provider = BuildProvider();

        // Warm-up: JIT, EF model/compiled query, Npgsql type handlers, connection pool. Not measured.
        await MeasureOnceAsync(provider);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var runs = new List<Run>();
        for (var i = 0; i < Iterations; i++)
        {
            runs.Add(await MeasureOnceAsync(provider));
        }

        var median = runs.Select(r => r.Milliseconds).OrderBy(ms => ms).ElementAt(Iterations / 2);
        var maxAllocated = runs.Max(r => r.AllocatedBytes);
        var maxPayload = runs.Max(r => r.PayloadBytes);
        for (var i = 0; i < runs.Count; i++)
        {
            output.WriteLine(
                $"run {i + 1}: {runs[i].Milliseconds:F1} ms, allocated {runs[i].AllocatedBytes / 1048576.0:F1} MiB, " +
                $"payload {runs[i].PayloadBytes} B, samples bucketed {runs[i].SamplesBucketed} (seeded {_seeded}), " +
                $"rows read by the Api {runs[i].RowsRead}");
        }

        output.WriteLine(
            $"median {median:F1} ms (limit {MaxMedianMilliseconds}), max allocated {maxAllocated / 1048576.0:F1} MiB " +
            $"(limit {MaxAllocatedBytes / 1048576.0:F0}), max payload {maxPayload} B (limit {MaxPayloadBytes})");

        // A run that bucketed fewer samples than were seeded measured a smaller problem: invalid, not
        // fast. The evidence is the response's own sample counts, which hold however the path reads its
        // rows; the rows the Api read are logged next to them.
        Assert.All(runs, r => Assert.True(
            r.SamplesBucketed >= _seeded,
            $"Invalid run: {r.SamplesBucketed} samples bucketed, {_seeded} seeded in the window."));
        Assert.True(median < MaxMedianMilliseconds, $"Median {median:F1} ms >= {MaxMedianMilliseconds} ms.");
        Assert.True(maxPayload <= MaxPayloadBytes, $"Payload {maxPayload} B > {MaxPayloadBytes} B.");
        Assert.True(
            maxAllocated <= MaxAllocatedBytes,
            $"Allocated {maxAllocated / 1048576.0:F1} MiB > {MaxAllocatedBytes / 1048576.0:F0} MiB.");
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(_connectionString).AddInterceptors(_rows));
        services.AddScoped<IAdminDashboardQueryService, AdminDashboardQueryService>();
        return services.BuildServiceProvider();
    }

    private async Task<Run> MeasureOnceAsync(ServiceProvider provider)
    {
        // A fresh facade per iteration is an empty cache: every call is a cold rebuild.
        var reader = new AdminDashboardReader(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IRateLimitTelemetryReader>(),
            new FakeTimeProvider(new DateTimeOffset(NowUtc)),
            new SystemLoadOptions { IntervalSeconds = 30 },
            NullLogger<AdminDashboardReader>.Instance);

        _rows.Reset();
        // Process-wide, not current-thread: EF and the cache continue on other threads, and a per-thread
        // delta would drop their allocations (or go negative). Safe only because the collection is serial.
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        var read = await reader.GetLoadAsync(AdminDashboardLoadRange.Hours24);
        // The Api sets no custom JSON options, so Results.Ok serialises with the ASP.NET web defaults.
        var payload = JsonSerializer.SerializeToUtf8Bytes(read.Load, WebJsonOptions);
        timer.Stop();
        var after = GC.GetTotalAllocatedBytes(precise: true);

        Assert.False(read.Busy);
        Assert.True(read.Load!.Available);
        var bucketed = read.Load.Api.SampleCount.Sum() + read.Load.Worker.SampleCount.Sum();
        return new Run(timer.Elapsed.TotalMilliseconds, after - before, payload.Length, bucketed, _rows.RowsRead);
    }

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Run(double Milliseconds, long AllocatedBytes, int PayloadBytes, long SamplesBucketed, long RowsRead);

    private sealed class RowCountingInterceptor : DbCommandInterceptor
    {
        private long _rowsRead;

        public long RowsRead => Interlocked.Read(ref _rowsRead);

        public void Reset() => Interlocked.Exchange(ref _rowsRead, 0);

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
            => new CountingReader(result, this);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<DbDataReader>(new CountingReader(result, this));

        private void Count() => Interlocked.Increment(ref _rowsRead);

        private sealed class CountingReader(DbDataReader inner, RowCountingInterceptor owner) : DbDataReader
        {
            public override int Depth => inner.Depth;
            public override int FieldCount => inner.FieldCount;
            public override bool HasRows => inner.HasRows;
            public override bool IsClosed => inner.IsClosed;
            public override int RecordsAffected => inner.RecordsAffected;
            public override object this[int ordinal] => inner[ordinal];
            public override object this[string name] => inner[name];

            public override bool Read()
            {
                var has = inner.Read();
                if (has)
                {
                    owner.Count();
                }

                return has;
            }

            public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
            {
                var has = await inner.ReadAsync(cancellationToken);
                if (has)
                {
                    owner.Count();
                }

                return has;
            }

            public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
            public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
            public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
                => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
            public override char GetChar(int ordinal) => inner.GetChar(ordinal);
            public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
                => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
            public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
            public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
            public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
            public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
            public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
            public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
            public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
            public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
                => inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
            public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
            public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
            public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
            public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
            public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
            public override string GetName(int ordinal) => inner.GetName(ordinal);
            public override int GetOrdinal(string name) => inner.GetOrdinal(name);
            public override string GetString(int ordinal) => inner.GetString(ordinal);
            public override object GetValue(int ordinal) => inner.GetValue(ordinal);
            public override int GetValues(object[] values) => inner.GetValues(values);
            public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
            public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
                => inner.IsDBNullAsync(ordinal, cancellationToken);
            public override bool NextResult() => inner.NextResult();
            public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
                => inner.NextResultAsync(cancellationToken);
            public override void Close() => inner.Close();
            public override Task CloseAsync() => inner.CloseAsync();
            public override ValueTask DisposeAsync() => inner.DisposeAsync();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}

// A Fact that is skipped unless EMOTEPURGE_PERF_GATES=1: the gate seeds 172,800 rows and starts a
// container, which `dotnet test EmotePurge.slnx` must not pay for.
[AttributeUsage(AttributeTargets.Method)]
public sealed class PerformanceGateFactAttribute : FactAttribute
{
    public PerformanceGateFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EMOTEPURGE_PERF_GATES") != "1")
        {
            Skip = "Opt-in performance gate: set EMOTEPURGE_PERF_GATES=1 to run.";
        }
    }
}
