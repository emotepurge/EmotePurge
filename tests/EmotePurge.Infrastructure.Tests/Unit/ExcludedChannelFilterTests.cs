using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// Container-free: IConfiguration is the only external dependency, same shape as
// ExcludedChatterFilterTests in EmotePurge.Worker.Tests (the per-chatter counterpart of this class)
// and BotChatterDetector's own parsing tests.
public class ExcludedChannelFilterTests
{
    [Fact]
    public void IsExcluded_ChannelIdFromScalarConfig_ReturnsTrue()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Channels:ExcludedChannelIds"] = "990001"
        });

        Assert.True(filter.IsExcluded("990001"));
    }

    [Fact]
    public void IsExcluded_ChannelIdFromIndexedArrayConfig_ReturnsTrue()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Channels:ExcludedChannelIds:0"] = "990001",
            ["Channels:ExcludedChannelIds:1"] = "990002"
        });

        Assert.True(filter.IsExcluded("990001"));
        Assert.True(filter.IsExcluded("990002"));
    }

    [Fact]
    public void IsExcluded_ScalarConfigWinsOverIndexedArray()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Channels:ExcludedChannelIds"] = "990001",
            ["Channels:ExcludedChannelIds:0"] = "990002"
        });

        Assert.True(filter.IsExcluded("990001"));
        Assert.False(filter.IsExcluded("990002"));
    }

    [Fact]
    public void IsExcluded_UnknownChannelId_ReturnsFalse()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Channels:ExcludedChannelIds"] = "990001"
        });

        Assert.False(filter.IsExcluded("123456789"));
    }

    [Fact]
    public void IsExcluded_NullOrEmptyChannelId_ReturnsFalse_NoException()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Channels:ExcludedChannelIds"] = "990001"
        });

        Assert.False(filter.IsExcluded(null));
        Assert.False(filter.IsExcluded(""));
    }

    [Fact]
    public void IsExcluded_ConfigMissing_NothingIsExcluded()
    {
        var filter = new ExcludedChannelFilter(new ConfigurationBuilder().Build(), NullLogger<ExcludedChannelFilter>.Instance);

        Assert.False(filter.IsExcluded("990001"));
        Assert.False(filter.IsExcluded(null));
    }

    [Fact]
    public void IsExcluded_ConfigEmpty_NothingIsExcluded()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Channels:ExcludedChannelIds"] = ""
        });

        Assert.False(filter.IsExcluded("990001"));
    }

    [Fact]
    public void IsExcluded_ConfigOnlyCommas_NothingIsExcluded()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Channels:ExcludedChannelIds"] = ",,,"
        });

        Assert.False(filter.IsExcluded("990001"));
    }

    [Fact]
    public void IsExcluded_ConfigValueWithSurroundingWhitespace_IsRecognized()
    {
        var filter = CreateFilter(new Dictionary<string, string?>
        {
            ["Channels:ExcludedChannelIds"] = "  990001 , 990002  "
        });

        Assert.True(filter.IsExcluded("990001"));
        Assert.True(filter.IsExcluded("990002"));
    }

    [Fact]
    public void Constructor_LogsOnlyTheCount_NeverAnId()
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory();
        loggerFactory.AddProvider(provider);
        var logger = loggerFactory.CreateLogger<ExcludedChannelFilter>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Channels:ExcludedChannelIds"] = "990001, 990002"
            })
            .Build();

        _ = new ExcludedChannelFilter(configuration, logger);

        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("2", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("990001", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("990002", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ConfigEmpty_LogsZero()
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory();
        loggerFactory.AddProvider(provider);
        var logger = loggerFactory.CreateLogger<ExcludedChannelFilter>();

        _ = new ExcludedChannelFilter(new ConfigurationBuilder().Build(), logger);

        var entry = Assert.Single(provider.Entries);
        Assert.Contains("0", entry.Message, StringComparison.Ordinal);
    }

    private static ExcludedChannelFilter CreateFilter(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ExcludedChannelFilter(configuration, NullLogger<ExcludedChannelFilter>.Instance);
    }

    // Local rather than shared, same call as ExcludedChatterFilterTests's copy in Worker.Tests: a
    // two-line test double is not worth a shared fixture across two test projects.
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
