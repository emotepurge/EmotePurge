using System.Collections.Concurrent;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging;
using EmotePurge.Infrastructure.Logging.Query;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// Recursion layer 4 (spec 5.4, S1/A31): the viewer's read paths run inside the capture's suppression scope,
/// so a failing log query — which EF logs on Error — neither lands in <c>LogEntries</c> nor announces
/// <c>logs.appended</c>, and cannot drive a timeout → capture → hint → query loop. A running writer, a real
/// Redis publisher, and an <see cref="AppDbContext"/> whose EF and Npgsql logging goes into the capture sink.
/// </summary>
[Collection("Postgres")]
public class LogViewerSuppressionTests(PostgresFixture fixture, RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string DatabaseName = "log_viewer_suppression_tests";
    private const string SentinelCategory = "Test.ViewerSuppression.Sentinel";

    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Operations = ["page", "keys", "ids", "entry", "summary"];

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));
    private readonly ConcurrentQueue<string> _hints = new();

    public Task InitializeAsync() => LogEntrySeed.PrepareDatabaseAsync(fixture, DatabaseName);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ViewerTimeouts_UnderSuppression_AddNoEntriesAndNoNotification()
    {
        await using var harness = CreateHarness();
        await harness.StartAsync();
        await SubscribeAsync();
        await using var dataSource = LoggingDataSource(harness.Factory);

        for (var i = 0; i < 20; i++)
        {
            // Like a request: the context is resolved before the endpoint filter, the handler runs inside it.
            await using var db = TimingOutContext(dataSource, harness.Factory);
            var service = new LogQueryService(db, new LogViewerOptions { QueryTimeoutMilliseconds = 50 }, _time);
            using (LogCaptureSuppression.Enter())
            {
                await Assert.ThrowsAsync<LogQueryTimeoutException>(() => LogQueryServiceTests.Run(service, Operations[i % Operations.Length]));
            }
        }

        // Several batch windows and more than the hint's 2 s throttle.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Empty(await ReadEntriesAsync());
        Assert.Empty(_hints);

        // A line logged afterwards, outside the scope, is stored — so everything queued before it was drained —
        // and is the only row; its hint arrives, so the subscription would have seen one for the timeouts too.
        harness.Logger(SentinelCategory).LogWarning("Sentinel after the viewer timeouts");
        var rows = await WaitForEntriesAsync(row => row.Category == SentinelCategory);
        Assert.Equal([SentinelCategory], rows.Select(r => r.Category));
        await WaitForHintAsync();
    }

    [Fact]
    public async Task ViewerTimeout_OutsideSuppression_IsCaptured_ControlForTheTestAbove()
    {
        await using var harness = CreateHarness();
        await harness.StartAsync();
        await SubscribeAsync();
        await using var dataSource = LoggingDataSource(harness.Factory);
        await using var db = TimingOutContext(dataSource, harness.Factory);
        var service = new LogQueryService(db, new LogViewerOptions { QueryTimeoutMilliseconds = 50 }, _time);

        await Assert.ThrowsAsync<LogQueryTimeoutException>(() => LogQueryServiceTests.Run(service, "page"));

        var rows = await WaitForEntriesAsync(row => row.Category.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Category == "Microsoft.EntityFrameworkCore.Database.Command" && row.Level == LogEntryLevel.Error);
        await WaitForHintAsync();
    }

    private Harness CreateHarness()
    {
        var services = new ServiceCollection()
            .AddSingleton<IRedisPublisher>(new RedisPublisher(redis.Connection))
            .BuildServiceProvider();
        return new Harness(fixture.ConnectionStringFor(DatabaseName), LogCaptureWriterTests.Fast, notifierServices: services);
    }

    private async Task SubscribeAsync()
    {
        var subscriber = redis.Connection.GetSubscriber();
        await subscriber.UnsubscribeAllAsync();
        await subscriber.SubscribeAsync(RedisChannel.Literal(LiveEvents.Channel), (_, message) =>
        {
            var payload = message.ToString();
            if (payload.Contains(LiveEvents.LogsAppended, StringComparison.Ordinal))
            {
                _hints.Enqueue(payload);
            }
        });
    }

    /// <summary>A data source whose Npgsql logging also reaches the capture sink.</summary>
    private NpgsqlDataSource LoggingDataSource(ILoggerFactory factory) =>
        new NpgsqlDataSourceBuilder(fixture.ConnectionStringFor(DatabaseName)).UseLoggerFactory(factory).Build();

    /// <summary>EF logs into the capture sink, and every query sleeps past the limit.</summary>
    private static AppDbContext TimingOutContext(NpgsqlDataSource dataSource, ILoggerFactory factory) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource)
            .UseLoggerFactory(factory)
            .AddInterceptors(new SleepBeforeReaderInterceptor())
            .Options);

    private async Task<List<LogEntry>> ReadEntriesAsync()
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await db.LogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<List<LogEntry>> WaitForEntriesAsync(Func<LogEntry, bool> arrived)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var rows = await ReadEntriesAsync();
            if (rows.Any(arrived))
            {
                return rows;
            }

            Assert.True(DateTime.UtcNow < deadline, "The expected entry was not stored within 15 s.");
            await Task.Delay(50);
        }
    }

    private async Task WaitForHintAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_hints.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < deadline, "No logs.appended hint arrived within 10 s.");
            await Task.Delay(50);
        }
    }
}
