using System.Diagnostics;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging.Query;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The live reconcile (spec 8.3, R2/R7, Sol 3.2): the client keeps a loaded interval from <c>from_L</c> to now
/// and periodically asks for the keys in it (<c>keysOnly</c>, no <c>to</c>), then reloads the missing ids
/// (<c>ids=</c>). These tests play the client against a real Postgres, including a commit that a raw
/// Npgsql transaction holds open for 30 s while reconciles run beside it. Own database, emptied per test.
/// </summary>
[Collection("Postgres")]
public class LogQueryReconcileTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "log_query_reconcile_tests";

    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));

    public Task InitializeAsync() => LogEntrySeed.PrepareDatabaseAsync(fixture, DatabaseName);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DelayedCommit_BetweenT40AndT41_IsDeliveredByTheFirstReconcileAfterTheCommit()
    {
        // t0 … t100, one per 10 s, oldest first. The first page holds t1 … t100; t0 is still behind the cursor,
        // so pagination has not ended and the client's from_L is the oldest loaded timestamp, t1.
        var t = Enumerable.Range(0, 101).Select(i => Now.AddMinutes(-30).AddSeconds(10 * i)).ToArray();
        await LogEntrySeed.InsertAsync(fixture, DatabaseName, [.. t.Select((ts, i) => LogEntrySeed.Entry(ts, $"t{i}"))]);
        var page = await WithService(s => s.GetPageAsync(null, new LogRangeRequest("1h", null, null), null, 100, default));
        Assert.NotNull(page.NextCursor);
        Assert.Equal("t1", page.Entries[^1].Message);
        var loaded = page.Entries.Select(e => e.Id).ToHashSet();
        var fromL = page.Entries[^1].TimestampUtc;

        // The writer's insert of an entry stamped between t40 and t41, committed 30 s late.
        await using var writer = new NpgsqlConnection(fixture.ConnectionStringFor(DatabaseName));
        await writer.OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        var lateId = await LogEntrySeed.InsertRawAsync(writer, LogEntrySeed.Entry(t[40].AddSeconds(5), "late"));

        var holdOpen = Stopwatch.StartNew();
        var reconciles = 0;
        while (holdOpen.Elapsed < TimeSpan.FromSeconds(30))
        {
            // Two reconciles side by side, as two open tabs would run them; neither waits for the open transaction.
            var started = Stopwatch.StartNew();
            var pair = await Task.WhenAll(ReconcileAsync(fromL, loaded), ReconcileAsync(fromL, loaded));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"A reconcile took {started.Elapsed} beside the open transaction.");
            Assert.All(pair, missing => Assert.Empty(missing));
            reconciles += 2;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        await transaction.CommitAsync();

        var afterCommit = await ReconcileAsync(fromL, loaded);
        Assert.Equal([lateId], afterCommit);
        Assert.True(reconciles >= 20, $"Only {reconciles} reconciles ran while the commit was held.");

        // The client reloads the missing id and places it by (TimestampUtc, Id) between t41 and t40.
        var reloaded = await WithService(s => s.GetByIdsAsync(afterCommit, default));
        Assert.Equal("late", Assert.Single(reloaded).Message);
        Assert.True(reloaded[0].TimestampUtc > t[40] && reloaded[0].TimestampUtc < t[41]);
    }

    [Fact]
    public async Task KeysOnly_FromRangeFrom_OnAnEmptyList_ReturnsTheFirstMatchingEntry()
    {
        var first = await WithService(s => s.GetPageAsync("level>=error", new LogRangeRequest("24h", null, null), null, 100, default));
        Assert.Empty(first.Entries);

        // The first error arrives after the first response; with an empty list the client reconciles from rangeFromUtc.
        _time.Advance(TimeSpan.FromMinutes(2));
        var inserted = await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(1), "first error", LogEntryLevel.Error),
            LogEntrySeed.Entry(Now.AddMinutes(1), "not an error"));

        var keys = await WithService(s => s.GetKeysAsync("level>=error", first.RangeFromUtc, null, default));

        Assert.Equal([new LogEntryKey(inserted[0].Id, Now.AddMinutes(1))], keys.Keys);
        Assert.False(keys.Truncated);
        Assert.Equal(first.RangeFromUtc, keys.RangeFromUtc);
        Assert.Equal(Now.AddMinutes(2), keys.RangeToUtc);
    }

    [Fact]
    public async Task AfterPaginationEnded_ALateEntryOlderThanEveryLoadedOne_IsDelivered()
    {
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-1), "11:59"),
            LogEntrySeed.Entry(Now.AddMinutes(-2), "11:58"));
        var page = await WithService(s => s.GetPageAsync(null, new LogRangeRequest("1h", null, null), null, 100, default));
        Assert.Equal(["11:59", "11:58"], page.Entries.Select(e => e.Message));
        Assert.Null(page.NextCursor);

        // Pagination is at its end, so the client's from_L is rangeFromUtc, not 11:58.
        var late = await LogEntrySeed.InsertAsync(fixture, DatabaseName, LogEntrySeed.Entry(Now.AddMinutes(-3), "11:57"));
        _time.Advance(TimeSpan.FromSeconds(30));

        var missing = await ReconcileAsync(page.RangeFromUtc, page.Entries.Select(e => e.Id).ToHashSet());

        Assert.Equal([late[0].Id], missing);
    }

    [Fact]
    public async Task KeysOnly_MoreThan2500_SetsTruncated_AndKeepsTheNewest2500()
    {
        var entries = Enumerable.Range(0, 2501).Select(i => LogEntrySeed.Entry(Now.AddSeconds(-1 - i), $"e{i}")).ToArray();
        await LogEntrySeed.InsertAsync(fixture, DatabaseName, entries);

        var keys = await WithService(s => s.GetKeysAsync(null, Now.AddHours(-1), null, default));

        Assert.True(keys.Truncated);
        Assert.Equal(2500, keys.Keys.Count);
        Assert.Equal(Now.AddSeconds(-1), keys.Keys[0].TimestampUtc);
        Assert.Equal(Now.AddSeconds(-2500), keys.Keys[^1].TimestampUtc);
        Assert.DoesNotContain(entries[2500].Id, keys.Keys.Select(k => k.Id));

        var exactly = await WithService(s => s.GetKeysAsync(null, Now.AddSeconds(-2500), null, default));
        Assert.False(exactly.Truncated);
        Assert.Equal(2500, exactly.Keys.Count);
    }

    [Fact]
    public async Task KeysOnly_AppliesTheFilter_AndAnExplicitTo()
    {
        var inserted = await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-30), "api", source: LogSource.Api),
            LogEntrySeed.Entry(Now.AddMinutes(-20), "worker"),
            LogEntrySeed.Entry(Now.AddMinutes(-10), "api later", source: LogSource.Api));

        var keys = await WithService(s => s.GetKeysAsync("source:api", Now.AddHours(-1), Now.AddMinutes(-15), default));

        Assert.Equal([inserted[0].Id], keys.Keys.Select(k => k.Id));
        Assert.Equal(Now.AddMinutes(-15), keys.RangeToUtc);
    }

    [Fact]
    public async Task Ids_ReturnExactlyTheRequestedEntries_EvenOutsideAnyRangeAndFilter()
    {
        var inserted = await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddDays(-10), "ten days old", LogEntryLevel.Information),
            LogEntrySeed.Entry(Now.AddMinutes(-1), "error", LogEntryLevel.Error),
            LogEntrySeed.Entry(Now.AddMinutes(-2), "other"));

        var entries = await WithService(s => s.GetByIdsAsync([inserted[0].Id, inserted[1].Id], default));

        Assert.Equal([inserted[1].Id, inserted[0].Id], entries.Select(e => e.Id));
    }

    /// <summary>One client reconcile: the keys from <paramref name="fromL"/> to now, minus the loaded ids.</summary>
    private async Task<long[]> ReconcileAsync(DateTime fromL, IReadOnlySet<long> loaded)
    {
        var keys = await WithService(s => s.GetKeysAsync(null, fromL, null, default));
        Assert.False(keys.Truncated);
        return keys.Keys.Select(k => k.Id).Where(id => !loaded.Contains(id)).ToArray();
    }

    private async Task<T> WithService<T>(Func<ILogQueryService, Task<T>> call)
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await call(new LogQueryService(db, new LogViewerOptions(), _time));
    }
}
