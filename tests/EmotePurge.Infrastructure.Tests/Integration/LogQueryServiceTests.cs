using System.Buffers.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging.Query;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The log query service against a real Postgres: every filter kind, the seeded "Twitch-Reconnects"
/// filter, keyset paging, frozen bounds, <c>instance:latest</c>, the detail, the id reload, the summary and
/// the statement time limit. Its own database, emptied before every test.
/// </summary>
[Collection("Postgres")]
public class LogQueryServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "log_query_service_tests";

    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly LogRangeRequest LastHour = new("1h", null, null);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));

    public Task InitializeAsync() => LogEntrySeed.PrepareDatabaseAsync(fixture, DatabaseName);

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("", "A,B,C,D,E,F,H,I")]
    [InlineData("cat:TwitchChatManager", "A")]
    [InlineData("cat:twitchchatmanager", "A")]
    [InlineData("cat:EmotePurge.Worker.TwitchChatManager", "A")]
    [InlineData("cat:SevenTv*", "C,D")]
    [InlineData("cat:TwitchLib.*", "E")]
    [InlineData("-cat:TwitchChatManager,TwitchLib.*", "B,C,D,F,H,I")]
    [InlineData("channel:handofblood", "A")]
    [InlineData("channel:HANDOFBLOOD", "A")]
    [InlineData("channel:hand*", "A")]
    [InlineData("channel:*", "A,F,I")]
    [InlineData("channel:\"o'brien\"", "I")]
    [InlineData("channel:\"x' OR '1'='1\"", "")]
    [InlineData("-channel:handofblood", "B,C,D,E,F,H,I")]
    [InlineData("-channel:*", "B,C,D,E,H")]
    [InlineData("gejoint", "A")]
    [InlineData("GEJOINT", "A")]
    [InlineData("boom", "D")]
    [InlineData("-boom", "A,B,C,E,F,H,I")]
    [InlineData("\"50%\"", "H")]
    [InlineData("\"progress 5*done\"", "B,H")]
    [InlineData("uptime<15m", "F")]
    [InlineData("uptime>=15m", "A,B,C,D,E,H,I")]
    [InlineData("has:exception", "D")]
    [InlineData("-has:exception", "A,B,C,E,F,H,I")]
    [InlineData("event:LogUnaccountedFor", "E")]
    [InlineData("event:logunaccounted*", "E")]
    [InlineData("event:42", "E")]
    [InlineData("-event:42", "A,B,C,D,F,H,I")]
    [InlineData("level>=error", "D,H")]
    [InlineData("level:warn", "C,E")]
    [InlineData("level:info,crit", "A,B,F,H,I")]
    [InlineData("level<warn", "A,B,F,I")]
    [InlineData("-level>=warn", "A,B,F,I")]
    [InlineData("source:api", "F")]
    [InlineData("-source:api", "A,B,C,D,E,H,I")]
    [InlineData("instance:bbbb0002", "F")]
    [InlineData("source:worker level>=warn", "C,D,E,H")]
    [InlineData("source:worker cat:SevenTv* -boom", "C")]
    public async Task Filter_MatchesTheExpectedEntries(string filter, string expectedTags)
    {
        await SeedCorpusAsync();

        var tags = await ReadTagsAsync(filter);

        Assert.Equal(Split(expectedTags), tags);
    }

    [Fact]
    public async Task Seed_TwitchReconnects_KeepsReconnectLines_DropsJoinLines()
    {
        string seed;
        await using (var db = fixture.CreateDbContext(DatabaseName))
        {
            seed = await db.LogFilters.Where(f => f.NameKey == "twitch-reconnects").Select(f => f.Filter).SingleAsync();
        }

        var chatManager = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "EmotePurge.Worker", "TwitchChatManager.cs"));
        var watchdog = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "EmotePurge.Worker", "TwitchConnectionWatchdog.cs"));
        var joinStarted = Render(TemplateContaining(chatManager, "angestoßen (Quelle"));        // TwitchChatManager.cs:540
        var replacedClient = Render(TemplateContaining(chatManager, "gejoint (Client"));         // TwitchChatManager.cs:998
        var joined = Render(TemplateContaining(chatManager, "{Channel} gejoint."));              // TwitchChatManager.cs:1010
        var forcedContext = Render(TemplateContaining(watchdog, "gejointen Channels"));          // TwitchConnectionWatchdog.cs:218
        Assert.Equal("Channel handofblood gejoint.", joined);

        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-6), joinStarted, category: "EmotePurge.Worker.TwitchChatManager"),
            LogEntrySeed.Entry(Now.AddMinutes(-5), replacedClient, category: "EmotePurge.Worker.TwitchChatManager"),
            LogEntrySeed.Entry(Now.AddMinutes(-4), joined, category: "EmotePurge.Worker.TwitchChatManager"),
            LogEntrySeed.Entry(Now.AddMinutes(-3), forcedContext, LogEntryLevel.Warning, category: "EmotePurge.Worker.TwitchConnectionWatchdog"),
            LogEntrySeed.Entry(Now.AddMinutes(-2), "Twitch-Verbindung verloren", LogEntryLevel.Warning, category: "EmotePurge.Worker.TwitchConnectionWatchdog"),
            LogEntrySeed.Entry(Now.AddMinutes(-1), "Disconnected from Twitch", category: "TwitchLib.Client.TwitchClient"),
            LogEntrySeed.Entry(Now.AddMinutes(-1), "Channel handofblood gejoint.", source: LogSource.Api, category: "EmotePurge.Worker.TwitchChatManager"),
            LogEntrySeed.Entry(Now.AddMinutes(-1), "7TV sync done", category: "EmotePurge.Worker.SevenTv.SevenTvEventClient"));

        var page = await WithService(s => s.GetPageAsync(seed, LastHour, null, 100, default));
        var messages = page.Entries.Select(e => e.Message).ToList();

        Assert.Contains(forcedContext, messages);
        Assert.Contains(replacedClient, messages);
        Assert.Contains("Twitch-Verbindung verloren", messages);
        Assert.Contains("Disconnected from Twitch", messages);
        Assert.DoesNotContain(joinStarted, messages);
        Assert.DoesNotContain(joined, messages);
        Assert.Equal(4, messages.Count);
    }

    [Fact]
    public async Task Page_IsNewestFirst_AndCarriesEveryListField_ButNotTheExceptionText()
    {
        var started = Now.AddHours(-2);
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-30), "older"),
            LogEntrySeed.Entry(
                Now.AddMinutes(-10),
                "newer",
                LogEntryLevel.Error,
                LogSource.Api,
                "EmotePurge.Api.Endpoints.AdminEndpoints",
                "cccc0003",
                started,
                eventId: 7,
                eventName: "Seven",
                exception: "System.InvalidOperationException: boom\n   at Somewhere()",
                properties: "{\"channel\": \"handofblood\"}"));

        var page = await WithService(s => s.GetPageAsync(null, LastHour, null, 100, default));

        Assert.Equal(["newer", "older"], page.Entries.Select(e => e.Message));
        var entry = page.Entries[0];
        Assert.Equal(Now.AddMinutes(-10), entry.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, entry.TimestampUtc.Kind);
        Assert.Equal(LogEntryLevel.Error, entry.Level);
        Assert.Equal(LogSource.Api, entry.Source);
        Assert.Equal("cccc0003", entry.InstanceId);
        Assert.Equal("EmotePurge.Api.Endpoints.AdminEndpoints", entry.Category);
        Assert.Equal(7, entry.EventId);
        Assert.Equal("Seven", entry.EventName);
        Assert.True(entry.HasException);
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
        using (var json = JsonDocument.Parse(entry.PropertiesJson!))
        {
            Assert.Equal("handofblood", json.RootElement.GetProperty("channel").GetString());
        }

        Assert.False(entry.Truncated);
        Assert.False(page.Entries[1].HasException);
        Assert.Null(page.Entries[1].PropertiesJson);
        Assert.Null(page.NextCursor);
        Assert.False(page.Truncated);
        Assert.Equal(Now.AddHours(-1), page.RangeFromUtc);
        Assert.Equal(Now, page.RangeToUtc);
    }

    [Fact]
    public async Task Page_ExactBounds_IncludeFrom_ExcludeTo()
    {
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddHours(-1).AddTicks(-10), "before"),
            LogEntrySeed.Entry(Now.AddHours(-1), "at from"),
            LogEntrySeed.Entry(Now.AddTicks(-10), "just before to"),
            LogEntrySeed.Entry(Now, "at to"));

        var page = await WithService(s => s.GetPageAsync(null, LastHour, null, 100, default));

        Assert.Equal(["just before to", "at from"], page.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Keyset_SameTimestamps_NoGapsNoDuplicates()
    {
        var entries = new List<LogEntry>();
        for (var i = 0; i < 60; i++)
        {
            // Four distinct timestamps, fifteen entries each: the id breaks every tie.
            entries.Add(LogEntrySeed.Entry(Now.AddMinutes(-10 - (i % 4)), $"entry {i}"));
        }

        await LogEntrySeed.InsertAsync(fixture, DatabaseName, [.. entries]);

        var read = new List<LogEntryView>();
        string? cursor = null;
        var pages = 0;
        LogRangeRequest? frozen = null;
        do
        {
            var page = await WithService(s => s.GetPageAsync(null, frozen ?? LastHour, cursor, 7, default));
            frozen ??= new LogRangeRequest(null, page.RangeFromUtc, page.RangeToUtc);
            Assert.InRange(page.Entries.Count, 1, 7);
            read.AddRange(page.Entries);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(9, pages);
        Assert.Equal(60, read.Count);
        Assert.Equal(60, read.Select(e => e.Id).Distinct().Count());
        Assert.Equal(entries.Select(e => e.Id).Order(), read.Select(e => e.Id).Order());
        for (var i = 1; i < read.Count; i++)
        {
            var previous = read[i - 1];
            var current = read[i];
            Assert.True(
                previous.TimestampUtc > current.TimestampUtc || (previous.TimestampUtc == current.TimestampUtc && previous.Id > current.Id),
                $"Not strictly descending at {i}.");
        }
    }

    [Fact]
    public async Task Paging_WithFrozenBounds_IgnoresNewerEntries_AndContinuesWhereItStopped()
    {
        for (var i = 0; i < 5; i++)
        {
            await LogEntrySeed.InsertAsync(fixture, DatabaseName, LogEntrySeed.Entry(Now.AddMinutes(-50 + i), $"old {i}"));
        }

        var first = await WithService(s => s.GetPageAsync(null, LastHour, null, 3, default));
        Assert.Equal(["old 4", "old 3", "old 2"], first.Entries.Select(e => e.Message));

        // Fifteen minutes later: newer entries arrive, and the relative range would now start later than "old 0".
        _time.Advance(TimeSpan.FromMinutes(15));
        await LogEntrySeed.InsertAsync(fixture, DatabaseName, LogEntrySeed.Entry(Now.AddMinutes(5), "newer"));

        var frozen = new LogRangeRequest(null, first.RangeFromUtc, first.RangeToUtc);
        var second = await WithService(s => s.GetPageAsync(null, frozen, first.NextCursor, 3, default));

        Assert.Equal(["old 1", "old 0"], second.Entries.Select(e => e.Message));
        Assert.Null(second.NextCursor);
        Assert.Equal(first.RangeFromUtc, second.RangeFromUtc);
        Assert.Equal(first.RangeToUtc, second.RangeToUtc);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    [InlineData(1, 1)]
    [InlineData(150, 150)]
    [InlineData(1000, 200)]
    public async Task Limit_IsClamped(int limit, int expected)
    {
        var entries = Enumerable.Range(0, 250).Select(i => LogEntrySeed.Entry(Now.AddSeconds(-10 - i), $"entry {i}")).ToArray();
        await LogEntrySeed.InsertAsync(fixture, DatabaseName, entries);

        var page = await WithService(s => s.GetPageAsync(null, LastHour, null, limit, default));

        Assert.Equal(expected, page.Entries.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task BrokenCursor_ThrowsLogRangeException(string cursor)
    {
        await Assert.ThrowsAsync<LogRangeException>(() => WithService(s => s.GetPageAsync(null, LastHour, cursor, 100, default)));
    }

    [Fact]
    public async Task Cursor_WithImpossibleValues_ThrowsLogRangeException()
    {
        var bytes = new byte[16];
        // Ticks beyond DateTime.MaxValue.
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), long.MaxValue);
        var badTicks = Base64Url.EncodeToString(bytes);
        var zeroId = Base64Url.EncodeToString(new byte[16]);

        await Assert.ThrowsAsync<LogRangeException>(() => WithService(s => s.GetPageAsync(null, LastHour, badTicks, 100, default)));
        await Assert.ThrowsAsync<LogRangeException>(() => WithService(s => s.GetPageAsync(null, LastHour, zeroId, 100, default)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankCursor_IsTheFirstPage(string? cursor)
    {
        await LogEntrySeed.InsertAsync(fixture, DatabaseName, LogEntrySeed.Entry(Now.AddMinutes(-1), "only"));

        var page = await WithService(s => s.GetPageAsync(null, LastHour, cursor, 100, default));

        Assert.Equal(["only"], page.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task InvalidFilter_And_InvalidRange_Throw()
    {
        var syntax = await Assert.ThrowsAsync<LogFilterSyntaxException>(() => WithService(s => s.GetPageAsync("level:debug", LastHour, null, 100, default)));
        Assert.Equal(LogFilterErrorReasons.InvalidLevel, syntax.Reason);

        await Assert.ThrowsAsync<LogRangeException>(() => WithService(s => s.GetPageAsync(null, new LogRangeRequest("1h", Now.AddHours(-1), Now), null, 100, default)));
        await Assert.ThrowsAsync<LogRangeException>(() => WithService(s => s.GetSummaryAsync(null, new LogRangeRequest("2d", null, null), default)));
        await Assert.ThrowsAsync<LogFilterSyntaxException>(() => WithService(s => s.GetSummaryAsync("\"open", LastHour, default)));
        await Assert.ThrowsAsync<LogFilterSyntaxException>(() => WithService(s => s.GetKeysAsync("-", Now.AddHours(-1), null, default)));
        await Assert.ThrowsAsync<LogRangeException>(() => WithService(s => s.GetKeysAsync(null, Now.AddDays(-9), null, default)));
    }

    [Fact]
    public async Task InstanceLatest_TwoInstancesPerSource_KeepsTheNewestOfEach()
    {
        await LogEntrySeed.InsertStatusAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Status(LogSource.Api, "a0000001", Now.AddHours(-5)),
            LogEntrySeed.Status(LogSource.Api, "a0000002", Now.AddMinutes(-40)),
            LogEntrySeed.Status(LogSource.Worker, "b0000001", Now.AddHours(-6)),
            LogEntrySeed.Status(LogSource.Worker, "b0000002", Now.AddMinutes(-30)));
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-50), "api old", source: LogSource.Api, instanceId: "a0000001", processStartedUtc: Now.AddHours(-5)),
            LogEntrySeed.Entry(Now.AddMinutes(-20), "api new", source: LogSource.Api, instanceId: "a0000002", processStartedUtc: Now.AddMinutes(-40)),
            LogEntrySeed.Entry(Now.AddMinutes(-35), "worker old", instanceId: "b0000001", processStartedUtc: Now.AddHours(-6)),
            LogEntrySeed.Entry(Now.AddMinutes(-10), "worker new", instanceId: "b0000002", processStartedUtc: Now.AddMinutes(-30)),
            LogEntrySeed.Entry(Now.AddMinutes(-5), "worker old still writing", instanceId: "b0000001", processStartedUtc: Now.AddHours(-6)));

        Assert.Equal(["worker new", "api new"], await ReadMessagesAsync("instance:latest"));
        Assert.Equal(["worker old still writing", "worker old", "api old"], await ReadMessagesAsync("-instance:latest"));
        Assert.Equal(["worker new"], await ReadMessagesAsync("instance:latest source:worker"));
        Assert.Equal(["worker new", "api old"], await ReadMessagesAsync("instance:latest,a0000001 -instance:a0000002"));
    }

    [Fact]
    public async Task InstanceLatest_IgnoresOlderInstanceStillWriting()
    {
        // Deploy overlap: the old instance still writes fresher heartbeats than the new one.
        var oldRow = LogEntrySeed.Status(LogSource.Worker, "b0000001", Now.AddHours(-6), Now.AddSeconds(-1));
        var newRow = LogEntrySeed.Status(LogSource.Worker, "b0000002", Now.AddMinutes(-30), Now.AddSeconds(-30));
        await LogEntrySeed.InsertStatusAsync(fixture, DatabaseName, oldRow, newRow);
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-10), "new", instanceId: "b0000002", processStartedUtc: Now.AddMinutes(-30)),
            LogEntrySeed.Entry(Now.AddMinutes(-5), "old still writing", instanceId: "b0000001", processStartedUtc: Now.AddHours(-6)),
            LogEntrySeed.Entry(Now.AddSeconds(-2), "old heartbeat era", instanceId: "b0000001", processStartedUtc: Now.AddHours(-6)));

        Assert.Equal(["new"], await ReadMessagesAsync("instance:latest"));
        await using var db = fixture.CreateDbContext(DatabaseName);
        var status = await new LogQueryService(db, new LogViewerOptions(), _time).ComputeStatusAsync(default);
        Assert.Equal("b0000002", Assert.Single(status.Sources, x => x.Source == LogSource.Worker).InstanceId);
    }

    [Fact]
    public async Task InstanceLatest_WithoutStatusRows_FallsBackToTheEntries()
    {
        await LogEntrySeed.InsertStatusAsync(fixture, DatabaseName, LogEntrySeed.Status(LogSource.Api, "a0000009", Now.AddMinutes(-40)));
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-20), "api", source: LogSource.Api, instanceId: "a0000009", processStartedUtc: Now.AddMinutes(-40)),
            LogEntrySeed.Entry(Now.AddMinutes(-35), "worker old", instanceId: "b0000001", processStartedUtc: Now.AddHours(-6)),
            LogEntrySeed.Entry(Now.AddMinutes(-10), "worker new", instanceId: "b0000002", processStartedUtc: Now.AddMinutes(-30)));

        Assert.Equal(["worker new", "api"], await ReadMessagesAsync("instance:latest"));
    }

    [Fact]
    public async Task InstanceLatest_SourceWithoutAnyRows_KeepsTheOtherSourceAndItsNegation()
    {
        // The Api has neither a status row nor an entry, so its branch of the lookup yields (api, NULL).
        // That pair must neither match nor turn the worker's comparison into NULL, also under negation.
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-35), "worker old", instanceId: "b0000001", processStartedUtc: Now.AddHours(-6)),
            LogEntrySeed.Entry(Now.AddMinutes(-10), "worker new", instanceId: "b0000002", processStartedUtc: Now.AddMinutes(-30)));

        Assert.Equal(["worker new"], await ReadMessagesAsync("instance:latest"));
        Assert.Equal(["worker old"], await ReadMessagesAsync("-instance:latest"));
        Assert.Empty(await ReadMessagesAsync("instance:latest source:api"));
    }

    [Fact]
    public async Task GetEntry_ReturnsExceptionProcessStartAndUptime_OrNull()
    {
        var started = Now.AddMinutes(-90);
        var inserted = await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-10).AddMilliseconds(-700), "failed", LogEntryLevel.Error, processStartedUtc: started, exception: "System.InvalidOperationException: boom"));

        var detail = await WithService(s => s.GetEntryAsync(inserted[0].Id, default));

        Assert.NotNull(detail);
        Assert.Equal(inserted[0].Id, detail.Entry.Id);
        Assert.Equal("failed", detail.Entry.Message);
        Assert.True(detail.Entry.HasException);
        Assert.Equal("System.InvalidOperationException: boom", detail.Exception);
        Assert.Equal(started, detail.ProcessStartedUtc);
        // Whole seconds, rounded down: 80 min minus 0.7 s.
        Assert.Equal((80 * 60) - 1, detail.UptimeSeconds);
        Assert.Null(await WithService(s => s.GetEntryAsync(inserted[0].Id + 1000, default)));
    }

    [Fact]
    public async Task GetByIds_ReturnsExactlyTheRequestedEntries_WithoutRangeAndFilter()
    {
        var inserted = await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddDays(-30), "a month old"),
            LogEntrySeed.Entry(Now.AddMinutes(-3), "recent", LogEntryLevel.Warning),
            LogEntrySeed.Entry(Now.AddMinutes(-2), "not requested"),
            LogEntrySeed.Entry(Now.AddHours(5), "in the future"));

        var entries = await WithService(s => s.GetByIdsAsync([inserted[0].Id, inserted[1].Id, inserted[3].Id, inserted[1].Id, 999_999], default));

        Assert.Equal(["in the future", "recent", "a month old"], entries.Select(e => e.Message));
        Assert.Empty(await WithService(s => s.GetByIdsAsync([], default)));
    }

    [Fact]
    public async Task GetByIds_MoreThan200_ThrowsLogRangeException()
    {
        var ids = Enumerable.Range(1, 201).Select(i => (long)i).ToArray();

        await Assert.ThrowsAsync<LogRangeException>(() => WithService(s => s.GetByIdsAsync(ids, default)));
        Assert.Empty(await WithService(s => s.GetByIdsAsync(ids[..200], default)));
    }

    [Fact]
    public async Task Summary_CountsPerLevelClassAndBucket_OnlyInsideTheExactBounds()
    {
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(new DateTime(2026, 10, 15, 10, 59, 59, DateTimeKind.Utc), "before from"),
            LogEntrySeed.Entry(new DateTime(2026, 10, 15, 11, 0, 0, DateTimeKind.Utc), "info at from"),
            LogEntrySeed.Entry(new DateTime(2026, 10, 15, 11, 0, 30, DateTimeKind.Utc), "info"),
            LogEntrySeed.Entry(new DateTime(2026, 10, 15, 11, 0, 40, DateTimeKind.Utc), "warn", LogEntryLevel.Warning),
            LogEntrySeed.Entry(new DateTime(2026, 10, 15, 11, 30, 0, DateTimeKind.Utc), "warn", LogEntryLevel.Warning),
            LogEntrySeed.Entry(new DateTime(2026, 10, 15, 11, 59, 10, DateTimeKind.Utc), "error", LogEntryLevel.Error),
            LogEntrySeed.Entry(new DateTime(2026, 10, 15, 11, 59, 50, DateTimeKind.Utc), "crit", LogEntryLevel.Critical),
            LogEntrySeed.Entry(Now, "at to"));

        var summary = await WithService(s => s.GetSummaryAsync(null, LastHour, default));

        Assert.Equal(Now.AddHours(-1), summary.RangeFromUtc);
        Assert.Equal(Now, summary.RangeToUtc);
        Assert.Equal(Now.AddHours(-1), summary.BucketFromUtc);
        Assert.Equal(Now, summary.BucketToUtc);
        Assert.Equal(60, summary.BucketSeconds);
        Assert.Equal(60, summary.BucketStartsUtc.Count);
        Assert.Equal(Now.AddHours(-1), summary.BucketStartsUtc[0]);
        Assert.Equal(Now.AddMinutes(-1), summary.BucketStartsUtc[59]);
        Assert.All(new[] { summary.Info, summary.Warn, summary.Error }, series => Assert.Equal(60, series.Count));
        Assert.Equal(2, summary.Info[0]);
        Assert.Equal(1, summary.Warn[0]);
        Assert.Equal(1, summary.Warn[30]);
        Assert.Equal(2, summary.Error[59]);
        Assert.Equal(6, summary.Info.Sum() + summary.Warn.Sum() + summary.Error.Sum());
        Assert.Equal(6, summary.Total);
        Assert.Equal(0, summary.Info[1]);
    }

    [Fact]
    public async Task Summary_UnalignedRange_CountsTheEntryAt100305_AK24()
    {
        var day = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(day.Add(new TimeSpan(0, 10, 3, 0, 500)), "before from, same bucket"),
            LogEntrySeed.Entry(day.AddHours(10).AddMinutes(3).AddSeconds(5), "first"),
            LogEntrySeed.Entry(day.AddHours(10).AddMinutes(17), "last", LogEntryLevel.Warning),
            LogEntrySeed.Entry(day.AddHours(10).AddMinutes(17).AddSeconds(1), "at to, same bucket"));
        var range = new LogRangeRequest(null, day.AddHours(10).AddMinutes(3).AddSeconds(1), day.AddHours(10).AddMinutes(17).AddSeconds(1));

        var summary = await WithService(s => s.GetSummaryAsync(null, range, default));

        Assert.Equal(range.FromUtc, summary.RangeFromUtc);
        Assert.Equal(range.ToUtc, summary.RangeToUtc);
        Assert.Equal(day.AddHours(10).AddMinutes(3), summary.BucketFromUtc);
        Assert.Equal(day.AddHours(10).AddMinutes(17).AddSeconds(10), summary.BucketToUtc);
        Assert.Equal(10, summary.BucketSeconds);
        Assert.Equal(85, summary.BucketStartsUtc.Count);
        Assert.Equal(1, summary.Info[0]);
        Assert.Equal(1, summary.Warn[84]);
        Assert.Equal(2, summary.Total);
    }

    [Fact]
    public async Task Summary_Facets_Top15PlusOther_ShortName_SourceAndLevelInEnumOrder()
    {
        var entries = new List<LogEntry>();
        for (var c = 0; c < 18; c++)
        {
            // Category c has 18 - c entries.
            for (var n = 0; n < 18 - c; n++)
            {
                entries.Add(LogEntrySeed.Entry(Now.AddMinutes(-1 - n), $"c{c}", category: $"EmotePurge.Worker.Area{c:D2}.Cat{c:D2}"));
            }
        }

        entries.Add(LogEntrySeed.Entry(Now.AddMinutes(-2), "api warn", LogEntryLevel.Warning, LogSource.Api, "Plain"));
        entries.Add(LogEntrySeed.Entry(Now.AddMinutes(-2), "api crit", LogEntryLevel.Critical, LogSource.Api, "Plain"));
        await LogEntrySeed.InsertAsync(fixture, DatabaseName, [.. entries]);

        var summary = await WithService(s => s.GetSummaryAsync(null, LastHour, default));
        var facets = summary.Facets;

        Assert.Equal([new LogFacetValue("api", 2), new LogFacetValue("worker", 171)], facets.Source);
        Assert.Equal([new LogFacetValue("info", 171), new LogFacetValue("warn", 1), new LogFacetValue("crit", 1)], facets.Level);
        Assert.Equal(15, facets.Category.Count);
        Assert.Equal(new LogCategoryFacetValue("EmotePurge.Worker.Area00.Cat00", "Cat00", 18), facets.Category[0]);
        // Below the top 15: Cat15 (3), Cat16 (2), "Plain" (2), Cat17 (1).
        Assert.Equal(new LogCategoryFacetValue("EmotePurge.Worker.Area14.Cat14", "Cat14", 4), facets.Category[14]);
        Assert.Equal(3 + 2 + 2 + 1, facets.CategoryOther);
        Assert.Equal(173, summary.Total);
    }

    [Fact]
    public async Task Summary_CategoryTies_OrderByName_AndShortOfAnUndottedName_IsTheName()
    {
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-1), "x", category: "Zeta.B"),
            LogEntrySeed.Entry(Now.AddMinutes(-1), "x", category: "Alpha.B"),
            LogEntrySeed.Entry(Now.AddMinutes(-1), "x", category: "Plain"));

        var summary = await WithService(s => s.GetSummaryAsync(null, LastHour, default));

        Assert.Equal(
            [new LogCategoryFacetValue("Alpha.B", "B", 1), new LogCategoryFacetValue("Plain", "Plain", 1), new LogCategoryFacetValue("Zeta.B", "B", 1)],
            summary.Facets.Category);
        Assert.Equal(0, summary.Facets.CategoryOther);
    }

    [Fact]
    public async Task Summary_FilterNarrowsCountsAndFacets_ButNeverTheDropTotals()
    {
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-50), "api line", source: LogSource.Api, category: "EmotePurge.Api.Program"),
            LogEntrySeed.Entry(Now.AddMinutes(-40), "worker line"),
            DropReport(Now.AddMinutes(-30), queueFull: 3, budget: 2, rejected: 1),
            DropReport(Now.AddMinutes(-20), writeUnconfirmed: 5, captureFailed: 1, reentrant: 4, source: LogSource.Api),
            DropReport(Now.AddHours(-2), queueFull: 100),
            LogEntrySeed.Entry(Now.AddMinutes(-10), "malformed drop report", LogEntryLevel.Warning, category: "EmotePurge.LogCapture", properties: "{\"dropped\": \"many\", \"queuefull\": true}"));

        var summary = await WithService(s => s.GetSummaryAsync("source:api -cat:LogCapture", LastHour, default));

        Assert.Equal(1, summary.Total);
        Assert.Equal([new LogFacetValue("api", 1)], summary.Facets.Source);
        Assert.Equal(new LogDropTotals(Total: 16, QueueFull: 3, Budget: 2, WriteUnconfirmed: 5, CaptureFailed: 1, Reentrant: 4, Rejected: 1), summary.Dropped);
    }

    [Fact]
    public async Task Summary_ProcessStarts_ListsEveryInstanceStartedInTheRange_Once()
    {
        await LogEntrySeed.InsertStatusAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Status(LogSource.Api, "a0000001", Now.AddHours(-3)),
            LogEntrySeed.Status(LogSource.Api, "a0000002", Now.AddMinutes(-40)),
            LogEntrySeed.Status(LogSource.Worker, "b0000002", Now.AddMinutes(-30)));
        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(Now.AddMinutes(-35), "api new", source: LogSource.Api, instanceId: "a0000002", processStartedUtc: Now.AddMinutes(-40)),
            LogEntrySeed.Entry(Now.AddMinutes(-25), "worker new", instanceId: "b0000002", processStartedUtc: Now.AddMinutes(-30)),
            LogEntrySeed.Entry(Now.AddMinutes(-15), "worker without status row", instanceId: "b0000003", processStartedUtc: Now.AddMinutes(-16)),
            LogEntrySeed.Entry(Now.AddMinutes(-5), "api old", source: LogSource.Api, instanceId: "a0000001", processStartedUtc: Now.AddHours(-3)));

        var summary = await WithService(s => s.GetSummaryAsync("cat:nothing", LastHour, default));

        Assert.Equal(
            [
                new LogProcessStart(LogSource.Api, "a0000002", Now.AddMinutes(-40)),
                new LogProcessStart(LogSource.Worker, "b0000002", Now.AddMinutes(-30)),
                new LogProcessStart(LogSource.Worker, "b0000003", Now.AddMinutes(-16)),
            ],
            summary.ProcessStarts);
        Assert.Equal(0, summary.Total);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("keys")]
    [InlineData("ids")]
    [InlineData("entry")]
    [InlineData("summary")]
    public async Task Timeout_MapsToLogQueryTimeoutException(string operation)
    {
        AppDbContext? context = null;
        // pg_sleep on the service's own connection right after it set its limit: the next statement in that
        // transaction runs past the limit, exactly like a slow query.
        var hook = new CommandHook(
            text => text.Contains("statement_timeout", StringComparison.Ordinal),
            async _ =>
            {
                await using var sleep = new NpgsqlCommand("SELECT pg_sleep(2)", (NpgsqlConnection)context!.Database.GetDbConnection());
                await sleep.ExecuteNonQueryAsync();
            });
        await using var db = context = CreateContext([hook]);
        var service = new LogQueryService(db, new LogViewerOptions { QueryTimeoutMilliseconds = 50 }, _time);

        var timeout = await Assert.ThrowsAsync<LogQueryTimeoutException>(() => Run(service, operation));

        Assert.Equal("57014", Assert.IsType<PostgresException>(timeout.InnerException).SqlState);
    }

    [Fact]
    public async Task Timeout_InTheQueryItself_MapsToLogQueryTimeoutException()
    {
        await using var db = CreateContext([new SleepBeforeReaderInterceptor()]);
        var service = new LogQueryService(db, new LogViewerOptions { QueryTimeoutMilliseconds = 50 }, _time);

        await Assert.ThrowsAsync<LogQueryTimeoutException>(() => service.GetPageAsync(null, LastHour, null, 100, default));
        await Assert.ThrowsAsync<LogQueryTimeoutException>(() => service.GetSummaryAsync(null, LastHour, default));
    }

    [Fact]
    public async Task Cancellation_IsNotATimeout()
    {
        await using var db = CreateContext([new SleepBeforeReaderInterceptor(seconds: 5)]);
        var service = new LogQueryService(db, new LogViewerOptions(), _time);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var exception = await Record.ExceptionAsync(() => service.GetPageAsync(null, LastHour, null, 100, cancel.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }

    [Fact]
    public async Task EveryQuery_RunsUnderTheConfiguredStatementTimeout_InAReadOnlyTransaction()
    {
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);
        await using var db = CreateContext([hook]);
        var service = new LogQueryService(db, new LogViewerOptions { QueryTimeoutMilliseconds = 1234 }, _time);

        await service.GetPageAsync(null, LastHour, null, 100, default);
        await service.GetSummaryAsync(null, LastHour, default);

        // The limit travels as a bound parameter, so only the statement text is observable here; the value itself
        // is proven by the timeout tests, where a 1 ms limit has to fire.
        Assert.Equal(2, hook.Count(c => c.Contains("set_config('statement_timeout'", StringComparison.Ordinal)));
        Assert.Equal(2, hook.Count(c => c.Contains("SET TRANSACTION READ ONLY", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FilterValues_NeverReachTheSqlText()
    {
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);
        await using var db = CreateContext([hook]);
        var service = new LogQueryService(db, new LogViewerOptions(), _time);

        await service.GetPageAsync("channel:handofblood cat:SevenTv* event:LogUnaccountedFor marker1 -\"marker2\" instance:abcdef12", LastHour, null, 100, default);
        await service.GetSummaryAsync("channel:handofblood cat:SevenTv* marker1", LastHour, default);

        foreach (var value in new[] { "handofblood", "channel", "SevenTv", "LogUnaccountedFor", "marker1", "marker2", "abcdef12" })
        {
            Assert.Equal(0, hook.Count(c => c.Contains(value, StringComparison.OrdinalIgnoreCase)));
        }
    }

    internal static Task Run(ILogQueryService service, string operation) => operation switch
    {
        "page" => service.GetPageAsync(null, LastHour, null, 100, default),
        "keys" => service.GetKeysAsync(null, Now.AddHours(-1), null, default),
        "ids" => service.GetByIdsAsync([1, 2, 3], default),
        "entry" => service.GetEntryAsync(1, default),
        "summary" => service.GetSummaryAsync(null, LastHour, default),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static LogEntry DropReport(
        DateTime at,
        long queueFull = 0,
        long budget = 0,
        long writeUnconfirmed = 0,
        long captureFailed = 0,
        long reentrant = 0,
        long rejected = 0,
        LogSource source = LogSource.Worker)
    {
        var total = queueFull + budget + writeUnconfirmed + captureFailed + reentrant + rejected;
        var properties = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["dropped"] = total,
            ["queuefull"] = queueFull,
            ["budget"] = budget,
            ["writeunconfirmed"] = writeUnconfirmed,
            ["capturefailed"] = captureFailed,
            ["reentrant"] = reentrant,
            ["rejected"] = rejected,
            ["firstdroputc"] = at.AddSeconds(-30).ToString("O"),
            ["lastdroputc"] = at.ToString("O"),
        });
        return LogEntrySeed.Entry(at, $"{total} log entries were dropped", LogEntryLevel.Warning, source, "EmotePurge.LogCapture", properties: properties);
    }

    private async Task SeedCorpusAsync()
    {
        static DateTime At(int minutesAgo) => Now.AddMinutes(-minutesAgo);

        await LogEntrySeed.InsertAsync(
            fixture,
            DatabaseName,
            LogEntrySeed.Entry(At(50), "[A] Channel handofblood gejoint.", category: "EmotePurge.Worker.TwitchChatManager", properties: "{\"channel\": \"handofblood\", \"index\": 3}"),
            LogEntrySeed.Entry(At(45), "[B] progress 500 done", category: "EmotePurge.Worker.NotTwitchChatManager"),
            LogEntrySeed.Entry(At(40), "[C] EventAPI reconnect", LogEntryLevel.Warning, category: "EmotePurge.Worker.SevenTv.SevenTvEventClient"),
            LogEntrySeed.Entry(At(35), "[D] sync failed", LogEntryLevel.Error, category: "EmotePurge.Infrastructure.Services.SevenTvSyncService", exception: "System.TimeoutException: boom deep inside"),
            LogEntrySeed.Entry(At(30), "[E] Unaccounted for: NOTICE", LogEntryLevel.Warning, category: "TwitchLib.Client.TwitchClient", eventId: 42, eventName: "LogUnaccountedFor"),
            LogEntrySeed.Entry(At(25), "[F] Application started", source: LogSource.Api, category: "Microsoft.Hosting.Lifetime", instanceId: "bbbb0002", processStartedUtc: At(30), properties: "{\"channel\": \"sensitron\"}"),
            LogEntrySeed.Entry(At(70), "[G] outside the range", category: "EmotePurge.Worker.TwitchChatManager", properties: "{\"channel\": \"handofblood\"}"),
            LogEntrySeed.Entry(At(20), "[H] progress 50% done", LogEntryLevel.Critical),
            LogEntrySeed.Entry(At(15), "[I] quoting", properties: "{\"channel\": \"o'brien\"}"));
    }

    private async Task<List<string>> ReadTagsAsync(string filter)
    {
        var messages = await ReadMessagesAsync(filter);
        return messages.Select(m => m.Substring(1, 1)).Order(StringComparer.Ordinal).ToList();
    }

    private async Task<List<string>> ReadMessagesAsync(string filter)
    {
        var page = await WithService(s => s.GetPageAsync(filter, LastHour, null, 200, default));
        Assert.Null(page.NextCursor);
        return page.Entries.Select(e => e.Message).ToList();
    }

    private static List<string> Split(string tags) =>
        tags.Length == 0 ? [] : [.. tags.Split(',').Order(StringComparer.Ordinal)];

    private async Task<T> WithService<T>(Func<ILogQueryService, Task<T>> call)
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await call(new LogQueryService(db, new LogViewerOptions(), _time));
    }

    private AppDbContext CreateContext(IEnumerable<IInterceptor> interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionStringFor(DatabaseName))
            .AddInterceptors(interceptors)
            .Options);

    private static string TemplateContaining(string source, string marker)
    {
        // Line-based: the template is the string literal on the line that holds the marker.
        foreach (var line in source.Split('\n'))
        {
            if (!line.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match literal in Regex.Matches(line, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                if (literal.Groups[1].Value.Contains(marker, StringComparison.Ordinal))
                {
                    return literal.Groups[1].Value;
                }
            }
        }

        throw new InvalidOperationException($"No string literal containing '{marker}' — the log template was reworded; update the seed filter with it.");
    }

    private static string Render(string template)
    {
        var examples = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Channel"] = "handofblood",
            ["Source"] = "rejoin",
            ["Generation"] = "3",
            ["LiveCount"] = "4",
            ["TotalCount"] = "12",
        };
        return Regex.Replace(template, "\\{(\\w+)\\}", m => examples[m.Groups[1].Value]);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EmotePurge.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("EmotePurge.slnx not found above the test output directory.");
    }
}

/// <summary>Prepends a <c>pg_sleep</c> to every reader command, so the query's own statement runs into the limit.</summary>
internal sealed class SleepBeforeReaderInterceptor(int seconds = 2) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        command.CommandText = $"SELECT pg_sleep({seconds});\n{command.CommandText}";
        return ValueTask.FromResult(result);
    }
}
