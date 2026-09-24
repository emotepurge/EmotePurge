using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure and TwitchLib-free like BotChatterDetector/TwitchWatchdogPolicy — the chatter id arrives as
// a plain BCL value, IConfiguration is the only external dependency. Never throws: IsExcluded runs
// in TwitchChatManager.OnMessageReceived, the hot path (see the class comment on
// ExcludedChatterFilter).
public class ExcludedChatterFilterTests
{
    [Fact]
    public void IsExcluded_AccountIdFromScalarConfig_ReturnsTrue()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds"] = "555555555"
        });

        Assert.True(filter.IsExcluded("555555555"));
    }

    [Fact]
    public void IsExcluded_AccountIdFromIndexedArrayConfig_ReturnsTrue()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds:0"] = "555555555",
            ["Twitch:ExcludedChatterIds:1"] = "666666666"
        });

        Assert.True(filter.IsExcluded("555555555"));
        Assert.True(filter.IsExcluded("666666666"));
    }

    [Fact]
    public void IsExcluded_ScalarConfigWinsOverIndexedArray()
    {
        // Same rule as BotChatterDetector: a non-empty scalar is read instead of the indexed keys,
        // never merged with them — see ReadExcludedChatterIds's comment.
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds"] = "555555555",
            ["Twitch:ExcludedChatterIds:0"] = "666666666"
        });

        Assert.True(filter.IsExcluded("555555555"));
        Assert.False(filter.IsExcluded("666666666"));
    }

    [Fact]
    public void IsExcluded_UnknownAccountId_ReturnsFalse()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds"] = "555555555"
        });

        Assert.False(filter.IsExcluded("123456789"));
    }

    [Fact]
    public void IsExcluded_NullChatterId_ReturnsFalse_NoException()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds"] = "555555555"
        });

        var result = filter.IsExcluded(chatterId: null);

        Assert.False(result);
    }

    [Fact]
    public void IsExcluded_EmptyChatterId_ReturnsFalse_NoException()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds"] = "555555555"
        });

        var result = filter.IsExcluded(chatterId: "");

        Assert.False(result);
    }

    [Fact]
    public void IsExcluded_ConfigMissing_NothingIsExcluded()
    {
        var filter = new ExcludedChatterFilter(new ConfigurationBuilder().Build(), NullLogger<ExcludedChatterFilter>.Instance);

        Assert.False(filter.IsExcluded("555555555"));
        Assert.False(filter.IsExcluded(null));
    }

    [Fact]
    public void IsExcluded_ConfigEmpty_NothingIsExcluded()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds"] = ""
        });

        Assert.False(filter.IsExcluded("555555555"));
    }

    [Fact]
    public void IsExcluded_ConfigOnlyCommas_NothingIsExcluded()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds"] = ",,,"
        });

        Assert.False(filter.IsExcluded("123456789"));
    }

    [Fact]
    public void IsExcluded_ConfigValueWithSurroundingWhitespace_IsRecognized()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Twitch:ExcludedChatterIds"] = "  555555555 , 666666666  "
        });

        Assert.True(filter.IsExcluded("555555555"));
        Assert.True(filter.IsExcluded("666666666"));
    }

    [Fact]
    public void Constructor_LogsOnlyTheCount_NeverAnId()
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory();
        loggerFactory.AddProvider(provider);
        var logger = loggerFactory.CreateLogger<ExcludedChatterFilter>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Twitch:ExcludedChatterIds"] = "555555555, 666666666"
            })
            .Build();

        _ = new ExcludedChatterFilter(configuration, logger);

        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("2", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("555555555", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("666666666", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ConfigEmpty_LogsZero()
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory();
        loggerFactory.AddProvider(provider);
        var logger = loggerFactory.CreateLogger<ExcludedChatterFilter>();

        _ = new ExcludedChatterFilter(new ConfigurationBuilder().Build(), logger);

        var entry = Assert.Single(provider.Entries);
        Assert.Contains("0", entry.Message, StringComparison.Ordinal);
    }

    private static ExcludedChatterFilter CreateFilter(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ExcludedChatterFilter(configuration, NullLogger<ExcludedChatterFilter>.Instance);
    }

    // Same shape as RedactingTwitchClientLoggerFactoryTests.CapturingLoggerProvider — kept local to
    // this file rather than shared, since it is a two-line test double, not a reusable fixture.
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
