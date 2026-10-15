using System.Diagnostics;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogCaptureProviderTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static Exception Thrown(Func<Exception> create)
    {
        try
        {
            throw create();
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> State(params (string Key, object? Value)[] items) =>
        items.Select(i => new KeyValuePair<string, object?>(i.Key, i.Value)).ToList();

    // --- floor and exclusions (§5.2) ---

    [Fact]
    public void Floor_DebugAndTrace_NeverCaptured()
    {
        using var capture = new Capture();
        var logger = capture.Logger();

        logger.LogTrace("trace");
        logger.LogDebug("debug");
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.Empty(capture.Drain());

        logger.LogInformation("info");
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.Equal("info", capture.Single().Message);
        Assert.Equal(0, capture.Drops.PendingTotal);
    }

    [Fact]
    public void EfCommand_BelowWarning_NotCaptured_WarningCaptured()
    {
        using var capture = new Capture();
        var logger = capture.Logger("Microsoft.EntityFrameworkCore.Database.Command");

        logger.LogInformation("Executed DbCommand");
        Assert.Empty(capture.Drain());

        logger.LogWarning("slow command");
        Assert.Equal(LogEntryLevel.Warning, capture.Single().Level);

        capture.Logger("Microsoft.EntityFrameworkCore.Database.Connection").LogInformation("other EF category");
        Assert.Single(capture.Drain());
    }

    [Fact]
    public void Npgsql_BelowWarning_NotCaptured()
    {
        using var capture = new Capture();

        capture.Logger("Npgsql").LogInformation("driver");
        capture.Logger("Npgsql.Command").LogInformation("driver command");
        Assert.Empty(capture.Drain());

        capture.Logger("Npgsql.Connection").LogError("driver error");
        Assert.Equal(LogEntryLevel.Error, capture.Single().Level);

        capture.Logger("NpgsqlLookalike").LogInformation("not the driver");
        Assert.Single(capture.Drain());
    }

    [Theory]
    [InlineData("EmotePurge.Infrastructure.Logging")]
    [InlineData("EmotePurge.Infrastructure.Logging.LogCaptureWriter")]
    [InlineData("EmotePurge.Infrastructure.Logging.ViewerQueries")]
    public void OwnCategory_NeverCaptured_AnyLevel(string category)
    {
        using var capture = new Capture();
        var logger = capture.Logger(category);

        logger.LogInformation("i");
        logger.LogWarning("w");
        logger.LogError("e");
        logger.LogCritical("c");

        Assert.False(logger.IsEnabled(LogLevel.Critical));
        Assert.Empty(capture.Drain());
        Assert.Equal(0, capture.Drops.PendingTotal);
        Assert.Equal(DateTime.MinValue, capture.Coverage.LastIncompleteAtUtc);
    }

    [Fact]
    public void OwnCategoryPrefix_IsMatchedOnTheNamespaceBoundary()
    {
        using var capture = new Capture();

        capture.Logger("EmotePurge.Infrastructure.LoggingExtras").LogInformation("not ours");

        Assert.Single(capture.Drain());
    }

    [Fact]
    public void ProviderAlias_CanRaise_CannotLower()
    {
        using var capture = new Capture(configuration: new Dictionary<string, string?>
        {
            ["Logging:LogCapture:LogLevel:Default"] = "Debug",
            ["Logging:LogCapture:LogLevel:Foo"] = "Error",
            ["Logging:LogCapture:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace",
            ["Logging:LogCapture:LogLevel:Npgsql"] = "Trace",
            ["Logging:LogCapture:LogLevel:EmotePurge.Infrastructure.Logging"] = "Trace",
        });

        capture.Logger("Foo.Bar").LogWarning("raised away");
        Assert.Empty(capture.Drain());
        capture.Logger("Foo.Bar").LogError("still above the raised level");
        Assert.Single(capture.Drain());

        capture.Logger("Other").LogDebug("lowered default does not reach below the floor");
        capture.Logger("Microsoft.EntityFrameworkCore.Database.Command").LogInformation("exclusion stands");
        capture.Logger("Npgsql.Command").LogInformation("exclusion stands");
        capture.Logger("EmotePurge.Infrastructure.Logging.LogCaptureWriter").LogCritical("exclusion stands");
        Assert.Empty(capture.Drain());

        capture.Logger("Other").LogInformation("floor level still captured");
        Assert.Single(capture.Drain());
    }

    // --- fields ---

    [Fact]
    public void Entry_MapsTimestampLevelCategoryAndEvent()
    {
        using var capture = new Capture();
        var logger = capture.Logger("EmotePurge.Worker.TwitchChatManager");

        logger.Log(LogLevel.Error, new EventId(42, "Named"), "text", null, (s, _) => s);
        var named = capture.Single();
        Assert.Equal(Noon.UtcDateTime, named.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, named.TimestampUtc.Kind);
        Assert.Equal(LogEntryLevel.Error, named.Level);
        Assert.Equal("EmotePurge.Worker.TwitchChatManager", named.Category);
        Assert.Equal(42, named.EventId);
        Assert.Equal("Named", named.EventName);
        Assert.Equal("text", named.Message);
        Assert.Null(named.Exception);
        Assert.Null(named.ExceptionType);
        Assert.Null(named.PropertiesJson);
        Assert.False(named.Truncated);
        Assert.False(named.IsSynthetic);

        logger.LogWarning("no event");
        var plain = capture.Single();
        Assert.Null(plain.EventId);
        Assert.Null(plain.EventName);
    }

    [Fact]
    public void LongCategoryAndEventName_AreCutAndMarked()
    {
        using var capture = new Capture();

        capture.Logger(new string('c', 300)).Log(LogLevel.Warning, new EventId(1, new string('e', 200)), "m", null, (s, _) => s);

        var entry = capture.Single();
        Assert.Equal(256, entry.Category.Length);
        Assert.Equal(128, entry.EventName!.Length);
        Assert.True(entry.Truncated);
    }

    [Fact]
    public void Message_IsScrubbedBeforeTheCut()
    {
        using var capture = new Capture();

        // Cutting first would leave "Bearer ABCDEFGHI", too short for the Bearer pattern.
        capture.Logger().LogWarning(new string('a', 4080) + " Bearer ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        var entry = capture.Single();
        Assert.DoesNotContain("ABCDEFGHI", entry.Message);
        Assert.EndsWith("…[truncated]", entry.Message);
        Assert.True(entry.Truncated);
    }

    [Fact]
    public void Message_HugeText_IsCutToTheLimitAndMarked()
    {
        using var capture = new Capture();

        capture.Logger().LogWarning(new string('a', 1024 * 1024));

        var entry = capture.Single();
        Assert.Equal(4096 + "…[truncated]".Length, entry.Message.Length);
        Assert.True(entry.Truncated);
    }

    [Fact]
    public void Message_SecretPastTheScrubWindowShortensTheText_StillMarkedTruncated()
    {
        using var capture = new Capture();

        capture.Logger().LogWarning("Password=" + new string('x', 20_000));

        var entry = capture.Single();
        Assert.Equal("Password=[redacted]…[truncated]", entry.Message);
        Assert.True(entry.Truncated);
    }

    [Fact]
    public void Message_NulAndLoneSurrogate_AreNormalized()
    {
        using var capture = new Capture();

        capture.Logger().LogWarning("a\0b\uD800c");

        Assert.Equal("a␀b�c", capture.Single().Message);
    }

    [Fact]
    public void Properties_ScrubbedOnceAndStoredAsValidJson()
    {
        using var capture = new Capture();

        capture.Logger().LogWarning("connect {Conn}", "Host=x;Password=hunter2;Port=5");

        var entry = capture.Single();
        Assert.Equal("connect Host=x;Password=[redacted];Port=5", entry.Message);
        var json = JsonDocument.Parse(entry.PropertiesJson!).RootElement;
        Assert.Equal("Host=x;Password=[redacted];Port=5", json.GetProperty("conn").GetString());
    }

    [Fact]
    public void Exception_RenderedBounded()
    {
        using var capture = new Capture();
        var exception = Thrown(() => new InvalidOperationException("outer", Thrown(() => new FormatException("inner"))));

        capture.Logger().LogError(exception, "failed");

        var entry = capture.Single();
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
        Assert.Contains("System.FormatException: inner", entry.Exception);
        Assert.Contains(nameof(Thrown), entry.Exception);
    }

    [Fact]
    public void Properties_EndlessEnumerable_StoredAsTypeNameAndLogReturns()
    {
        using var capture = new Capture();
        var logger = capture.Logger();
        // The caller's formatter is not ours to bound (a MEL template would enumerate the sequence for
        // the console too); this one does not, so what is measured is the sink's own work.
        void LogLazy() => logger.Log(LogLevel.Warning, default, State(("Lazy", Endless())), null, (_, _) => "lazy");
        LogLazy();
        capture.Drain();

        var stopwatch = Stopwatch.StartNew();
        LogLazy();
        stopwatch.Stop();

        // 50 ms instead of 1 ms: an endless enumeration would never return, and a loaded devbox cannot guarantee 1 ms.
        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"Log() took {stopwatch.ElapsedMilliseconds} ms");
        var json = JsonDocument.Parse(capture.Single().PropertiesJson!).RootElement;
        Assert.StartsWith("<", json.GetProperty("lazy").GetString());
    }

    private static IEnumerable<int> Endless()
    {
        var i = 0;
        while (true)
        {
            yield return i++;
        }
    }

    // --- scopes (A4) ---

    [Fact]
    public void Scopes_OnlyRequestPathAndTraceId()
    {
        using var capture = new Capture();
        var logger = capture.Logger();

        using (logger.BeginScope(State(("RequestPath", "/api/admin/users/1"), ("TraceId", "abc123"), ("ConnectionId", "c1"), ("RequestId", "r1"))))
        using (logger.BeginScope("a plain string scope"))
        using (logger.BeginScope(new Dictionary<string, object?> { ["SpanId"] = "s1", ["Other"] = "o" }))
        {
            logger.LogWarning("hello {Name}", "n");
        }

        var json = JsonDocument.Parse(capture.Single().PropertiesJson!).RootElement;
        var keys = json.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(["name", "requestpath", "traceid"], keys);
        Assert.Equal("/api/admin/users/1", json.GetProperty("requestpath").GetString());
        Assert.Equal("abc123", json.GetProperty("traceid").GetString());
    }

    [Fact]
    public void Scopes_NoScope_NoScopeProperties()
    {
        using var capture = new Capture();

        capture.Logger().LogWarning("plain");

        Assert.Null(capture.Single().PropertiesJson);
    }

    // --- admission (§5.3) ---

    [Fact]
    public void QueueFull_ReturnsImmediately_CountsQueueFull()
    {
        using var capture = new Capture(maxEntries: 10);
        var logger = capture.Logger();
        for (var i = 0; i < 10; i++)
        {
            logger.LogInformation("fill");
        }

        var formatterCalls = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            logger.Log(LogLevel.Information, default, "overflow", null, (s, _) =>
            {
                formatterCalls++;
                return s;
            });
        }

        stopwatch.Stop();

        Assert.Equal(0, formatterCalls);
        Assert.Equal(10_000, capture.Drops.CutPending()!.QueueFull);
        Assert.Equal(10, capture.Buffer.ReservedSlots);
        // Generous: 10,000 rejected calls in under a second is 100 µs each; a blocking sink would take forever.
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"10,000 rejected calls took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ByteLimit_HitsBeforeCountLimit()
    {
        using var capture = new Capture();
        var logger = capture.Logger();
        var inners = Enumerable.Range(0, 20).Select(i => (Exception)new InvalidOperationException(new string((char)('a' + i), 1024))).ToArray();
        var exception = new AggregateException(inners);
        var state = State(Enumerable.Range(0, 16).Select(i => ($"p{i:00}", (object?)new string('v', 250))).ToArray());

        for (var i = 0; i < 400; i++)
        {
            logger.Log(LogLevel.Warning, new EventId(7, "Big"), state, exception, (_, _) => new string('m', 5000));
        }

        var buffered = capture.Buffer.ReservedSlots;
        var drops = capture.Drops.CutPending()!;
        Assert.InRange(buffered, 150, 199);
        Assert.Equal(400 - buffered, drops.QueueFull);
        Assert.Equal(0, drops.Total - drops.QueueFull);
        Assert.True(capture.Buffer.BufferedBytes <= LogCaptureBuffer.DefaultMaxBytes);

        var entries = capture.Drain();
        Assert.Equal(buffered, entries.Count);
        var full = entries[0];
        Assert.Equal(4096 + "…[truncated]".Length, full.Message.Length);
        Assert.Equal(16_384, full.Exception!.Length);
        Assert.InRange(full.PropertiesJson!.Length, 3_800, 4_096);
        Assert.True(full.Truncated);
        Assert.Equal(0, capture.Buffer.ReservedSlots);
        Assert.Equal(0, capture.Buffer.BufferedBytes);

        logger.LogInformation("small");
        Assert.Equal("small", capture.Single().Message);
    }

    [Fact]
    public void FullBufferOrBudget_FormatterNeverCalled()
    {
        var formatterCalls = 0;
        string Count(string s, Exception? _)
        {
            formatterCalls++;
            return s;
        }

        using (var full = new Capture(maxEntries: 1))
        {
            var logger = full.Logger();
            logger.Log(LogLevel.Information, default, "fills", null, Count);
            Assert.Equal(1, formatterCalls);
            logger.Log(LogLevel.Error, default, "rejected", null, Count);
            Assert.Equal(1, formatterCalls);
            Assert.Equal(1, full.Drops.CutPending()!.QueueFull);
        }

        formatterCalls = 0;
        using (var broke = new Capture(budget: 1))
        {
            var logger = broke.Logger();
            logger.Log(LogLevel.Warning, default, "uses the budget", null, Count);
            logger.Log(LogLevel.Warning, default, "over budget", null, Count);
            Assert.Equal(1, formatterCalls);
            var drops = broke.Drops.CutPending()!;
            Assert.Equal(1, drops.Budget);
            Assert.Equal(0, drops.QueueFull);
            Assert.Single(broke.Drain());
        }
    }

    [Fact]
    public void Budget_InfoStopsAt80Percent_ThroughTheProvider()
    {
        using var capture = new Capture(budget: 10);
        var logger = capture.Logger();

        for (var i = 0; i < 10; i++)
        {
            logger.LogInformation("info");
        }

        for (var i = 0; i < 5; i++)
        {
            logger.LogWarning("warning");
        }

        var entries = capture.Drain();
        Assert.Equal(8, entries.Count(e => e.Level == LogEntryLevel.Information));
        Assert.Equal(2, entries.Count(e => e.Level == LogEntryLevel.Warning));
        Assert.Equal(5, capture.Drops.CutPending()!.Budget);
    }

    [Fact]
    public void FormatterThatLogs_CountsReentrant_OuterEntryCaptured()
    {
        using var capture = new Capture();
        var logger = capture.Logger();

        logger.Log(LogLevel.Warning, default, "outer", null, (s, _) =>
        {
            logger.LogError("inner");
            return s;
        });

        Assert.Equal("outer", capture.Single().Message);
        var drops = capture.Drops.CutPending()!;
        Assert.Equal(1, drops.Reentrant);
        Assert.Equal(1, drops.Total);
        Assert.Equal(Noon.UtcDateTime, capture.Coverage.LastIncompleteAtUtc);

        logger.LogWarning("after");
        Assert.Equal("after", capture.Single().Message);
    }

    [Fact]
    public void FormatterThrows_CountsCaptureFailed_DoesNotThrow()
    {
        using var capture = new Capture();
        var logger = capture.Logger();

        var thrown = Record.Exception(() =>
            logger.Log<string>(LogLevel.Error, default, "boom", null, (_, _) => throw new InvalidOperationException("formatter")));

        Assert.Null(thrown);
        Assert.Empty(capture.Drain());
        Assert.Equal(0, capture.Buffer.ReservedSlots);
        Assert.Equal(1, capture.Drops.CutPending()!.CaptureFailed);
        Assert.Equal(Noon.UtcDateTime, capture.Coverage.LastIncompleteAtUtc);

        logger.LogWarning("after");
        Assert.Equal("after", capture.Single().Message);
    }

    [Fact]
    public async Task Suppressed_DroppedSilently_NotCounted_OutsideCaptured()
    {
        using var capture = new Capture();
        var logger = capture.Logger();

        using (LogCaptureSuppression.Enter())
        {
            Assert.True(LogCaptureSuppression.IsActive);
            logger.LogError("inside");
            await Task.Run(() => logger.LogError("inside a task started in the scope"));
            using (LogCaptureSuppression.Enter())
            {
                logger.LogError("nested");
            }

            Assert.True(LogCaptureSuppression.IsActive);
            logger.LogError("after leaving the nested scope");
        }

        Assert.False(LogCaptureSuppression.IsActive);
        logger.LogError("outside");

        Assert.Equal("outside", capture.Single().Message);
        Assert.Equal(0, capture.Drops.PendingTotal);
        Assert.Equal(DateTime.MinValue, capture.Coverage.LastIncompleteAtUtc);
    }

    [Fact]
    public async Task Suppression_DoesNotLeakToAnotherExecutionContext()
    {
        using var capture = new Capture();
        var logger = capture.Logger();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using (LogCaptureSuppression.Enter())
            {
                entered.Set();
                release.Wait();
            }
        });

        entered.Wait();
        logger.LogWarning("unrelated flow");
        release.Set();
        await holder;

        Assert.Single(capture.Drain());
    }

    // --- coverage marker (3.3) ---

    [Fact]
    public void ErrorDrop_MarksCoverageIncomplete()
    {
        using var capture = new Capture(maxEntries: 1);
        var logger = capture.Logger();
        logger.LogInformation("fills");

        capture.Time.Advance(TimeSpan.FromMinutes(5));
        logger.LogError("dropped error");

        Assert.Equal(Noon.UtcDateTime.AddMinutes(5), capture.Coverage.LastIncompleteAtUtc);

        using var broke = new Capture(budget: 0);
        broke.Logger().LogCritical("over budget");
        Assert.Equal(Noon.UtcDateTime, broke.Coverage.LastIncompleteAtUtc);
    }

    [Fact]
    public void InfoDrop_DoesNotMark()
    {
        using var capture = new Capture(maxEntries: 1);
        var logger = capture.Logger();
        logger.LogInformation("fills");

        logger.LogInformation("dropped info");
        logger.LogWarning("dropped warning");

        Assert.Equal(2, capture.Drops.PendingTotal);
        Assert.Equal(DateTime.MinValue, capture.Coverage.LastIncompleteAtUtc);
    }

    // --- second line for the redacted TwitchLib events (O3, §4.2a) ---

    [Theory]
    [InlineData("LogParsingError")]
    [InlineData("LogUnaccountedFor")]
    [InlineData("LogLeavingChannel")]
    public void BypassedWrapper_TwitchClientParsingError_StoresTypesAndStacksOnly(string eventName)
    {
        using var capture = new Capture();
        var exception = Thrown(() => new InvalidOperationException("chat text EvilName", Thrown(() => new FormatException("inner GEHEIMTEXT"))));

        capture.Logger("TwitchLib.Client.TwitchClient").Log(LogLevel.Error, new EventId(3, eventName), "harmless", exception, (s, _) => s);

        var entry = capture.Single();
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
        Assert.Contains("System.InvalidOperationException", entry.Exception);
        Assert.Contains("System.FormatException", entry.Exception);
        Assert.Contains(nameof(Thrown), entry.Exception);
        Assert.DoesNotContain("EvilName", entry.Exception);
        Assert.DoesNotContain("GEHEIMTEXT", entry.Exception);
    }

    [Theory]
    [InlineData("TwitchLib.Client.TwitchClient", "OnMessageReceived")]
    [InlineData("EmotePurge.Worker.TwitchChatManager", "LogParsingError")]
    public void OtherEventsOrCategories_KeepExceptionMessages(string category, string eventName)
    {
        using var capture = new Capture();
        var exception = Thrown(() => new InvalidOperationException("diagnostic text"));

        capture.Logger(category).Log(LogLevel.Error, new EventId(3, eventName), "m", exception, (s, _) => s);

        Assert.Contains("diagnostic text", capture.Single().Exception);
    }

    // --- lifecycle ---

    [Fact]
    public async Task AfterClose_DropsSilently()
    {
        using var capture = new Capture();
        var logger = capture.Logger();
        logger.LogWarning("before");

        capture.Provider.Close();
        logger.LogError("after");
        capture.Provider.Close();

        Assert.False(logger.IsEnabled(LogLevel.Critical));
        Assert.Equal("before", capture.Single().Message);
        Assert.Equal(0, capture.Drops.PendingTotal);
        Assert.Equal(DateTime.MinValue, capture.Coverage.LastIncompleteAtUtc);
        await capture.Buffer.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dispose_ReachesClose()
    {
        using var capture = new Capture();
        var logger = capture.Logger();

        capture.Provider.Dispose();
        logger.LogError("after dispose");

        Assert.Empty(capture.Drain());
        Assert.Equal(0, capture.Drops.PendingTotal);
        Assert.True(capture.Buffer.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void CaptureIds_AreUniqueAndVersion7()
    {
        using var capture = new Capture();
        var logger = capture.Logger();

        for (var i = 0; i < 1000; i++)
        {
            logger.LogInformation("entry {Index}", i);
        }

        var entries = capture.Drain();
        Assert.Equal(1000, entries.Count);
        Assert.Equal(1000, entries.Select(e => e.CaptureId).Distinct().Count());
        Assert.All(entries, e => Assert.Equal(7, e.CaptureId.Version));
    }

    [Fact]
    public void ProviderAlias_IsLogCapture()
    {
        var alias = (ProviderAliasAttribute?)Attribute.GetCustomAttribute(typeof(LogCaptureProvider), typeof(ProviderAliasAttribute));

        Assert.Equal("LogCapture", alias?.Alias);
    }

    // --- options and identity ---

    [Fact]
    public void Options_Defaults()
    {
        var options = new LogCaptureOptions();

        Assert.Equal("LogCapture", LogCaptureOptions.SectionName);
        Assert.True(options.Enabled);
        Assert.Equal(20_000, options.DailyEntryBudget);
    }

    [Fact]
    public void Identity_EightHexInstanceIdAndUtcProcessStart()
    {
        var identity = new LogCaptureIdentity(LogSource.Worker);
        var other = new LogCaptureIdentity(LogSource.Api);

        Assert.Equal(LogSource.Worker, identity.Source);
        Assert.Matches("^[0-9a-f]{8}$", identity.InstanceId);
        Assert.NotEqual(identity.InstanceId, other.InstanceId);
        Assert.Equal(DateTimeKind.Utc, identity.ProcessStartedUtc.Kind);
        Assert.True(identity.ProcessStartedUtc <= DateTime.UtcNow);
        Assert.True(identity.ProcessStartedUtc > DateTime.UtcNow.AddDays(-30));
        Assert.Equal(0, identity.ProcessStartedUtc.Ticks % 10);
    }

    /// <summary>A LoggerFactory with only our provider, minimum level Trace.</summary>
    private sealed class Capture : IDisposable
    {
        public Capture(
            int maxEntries = LogCaptureBuffer.DefaultMaxEntries,
            long maxBytes = LogCaptureBuffer.DefaultMaxBytes,
            int budget = 20_000,
            IDictionary<string, string?>? configuration = null)
        {
            Buffer = new LogCaptureBuffer(maxEntries, maxBytes);
            Budget = new LogCaptureBudget(budget, Time);
            Provider = new LogCaptureProvider(Buffer, Budget, Drops, Coverage, Time);
            Factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                if (configuration is not null)
                {
                    builder.AddConfiguration(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build().GetSection("Logging"));
                }

                builder.AddProvider(Provider);
            });
        }

        public FakeTimeProvider Time { get; } = new(Noon);

        public LogCaptureBuffer Buffer { get; }

        public LogCaptureBudget Budget { get; }

        public LogCaptureDropCounters Drops { get; } = new();

        public LogCaptureCoverage Coverage { get; } = new();

        public LogCaptureProvider Provider { get; }

        public ILoggerFactory Factory { get; }

        public ILogger Logger(string category = "Test.Category") => Factory.CreateLogger(category);

        public List<CapturedLogEntry> Drain()
        {
            var entries = new List<CapturedLogEntry>();
            while (Buffer.Reader.TryRead(out var entry))
            {
                entries.Add(entry);
            }

            return entries;
        }

        public CapturedLogEntry Single() => Assert.Single(Drain());

        public void Dispose() => Factory.Dispose();
    }
}
