using Microsoft.Extensions.Logging;
using Xunit;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// Verifies the decorator against the real <see cref="LoggerMessage.Define{T1}"/> API — the same
/// mechanism TwitchLib.Client's Roslyn-generated <c>LogExtensions</c> uses internally (decompiled
/// and quoted in full on <see cref="RedactingTwitchClientLoggerFactory"/>) — rather than a hand-built
/// fake <c>TState</c>. That keeps the test honest about the actual shape .NET's logging
/// infrastructure hands to <see cref="ILogger.Log{TState}"/>: a state object implementing
/// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object&gt;&gt;</c> with one entry per named
/// placeholder plus <c>{OriginalFormat}</c>.
/// </summary>
public class RedactingTwitchClientLoggerFactoryTests
{
    private const string TwitchClientCategory = "TwitchLib.Client.TwitchClient";

    // Pinned from the decompiled TwitchLib.Client 4.0.1 LogExtensions (see the class-level remarks
    // on RedactingTwitchClientLoggerFactory) — not invented here.
    private static readonly EventId ParsingErrorEventId = new(932391387, "LogParsingError");
    private static readonly EventId UnaccountedForEventId = new(1006327295, "LogUnaccountedFor");
    private static readonly EventId ReconnectingEventId = new(1646549738, "LogReconnecting");

    // Same generator, same FNV-1a hash of the method name that yields the three ids above.
    private static readonly EventId LeavingChannelEventId = new(1270440230, "LogLeavingChannel");

    private static readonly Action<ILogger, string, Exception?> LogParsingErrorCallback =
        LoggerMessage.Define<string>(LogLevel.Error, ParsingErrorEventId, "Unexpected error during message parsing, message: {message}");

    private static readonly Action<ILogger, string, Exception?> LogUnaccountedForCallback =
        LoggerMessage.Define<string>(LogLevel.Warning, UnaccountedForEventId, "Unaccounted for: {ircString} (please create a TwitchLib GitHub issue :P)");

    private static readonly Action<ILogger, string, Exception?> LogLeavingChannelCallback =
        LoggerMessage.Define<string>(LogLevel.Information, LeavingChannelEventId, "Leaving channel: {channel}");

    private static readonly Action<ILogger, Exception?> LogReconnectingCallback =
        LoggerMessage.Define(LogLevel.Information, ReconnectingEventId, "Reconnecting to Twitch");

    [Fact]
    public void ParsingError_OnTwitchClientCategory_RawLineNeverReachesSink()
    {
        var (provider, redactingFactory) = CreateFactory();
        var logger = redactingFactory.CreateLogger(TwitchClientCategory);
        var rawLine = "@display-name=TestUser1;login=testuser1;user-id=333333333 "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :some real chat text";

        LogParsingErrorCallback(logger, rawLine, new InvalidOperationException("boom"));

        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(ParsingErrorEventId, entry.EventId);
        Assert.NotNull(entry.Exception);
        Assert.DoesNotContain("TestUser1", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("testuser1", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("333333333", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("some real chat text", entry.Message, StringComparison.Ordinal);
        Assert.Contains("command=PRIVMSG", entry.Message, StringComparison.Ordinal);
        Assert.Contains("tagKeys=[display-name,login,user-id]", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnaccountedFor_OnTwitchClientCategory_RawLineNeverReachesSink()
    {
        var (provider, redactingFactory) = CreateFactory();
        var logger = redactingFactory.CreateLogger(TwitchClientCategory);
        var rawLine = "@user-id=444444444 :chatter!chatter@chatter.tmi.twitch.tv WHISPER target :secret dm text";

        LogUnaccountedForCallback(logger, rawLine, null);

        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(UnaccountedForEventId, entry.EventId);
        Assert.DoesNotContain("444444444", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret dm text", entry.Message, StringComparison.Ordinal);
        Assert.Contains("command=WHISPER", entry.Message, StringComparison.Ordinal);
    }

    // Fourth Codex review of the block list: the worker parts a channel whose Twitch id is on the
    // excluded-channel list right after the identity reconcile deactivates it, and TwitchLib's own
    // "Leaving channel" line would name it. The event stays at its level; only the login goes.
    [Fact]
    public void LeavingChannel_OnTwitchClientCategory_KeepsTheEventButWithholdsTheChannel()
    {
        var (provider, redactingFactory) = CreateFactory();
        var logger = redactingFactory.CreateLogger(TwitchClientCategory);

        LogLeavingChannelCallback(logger, "blockedchannel_test", null);

        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(LeavingChannelEventId, entry.EventId);
        Assert.Contains("Leaving channel", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("blockedchannel_test", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherLogCallsOnTwitchClientCategory_PassThroughUnchanged()
    {
        var (provider, redactingFactory) = CreateFactory();
        var logger = redactingFactory.CreateLogger(TwitchClientCategory);

        LogReconnectingCallback(logger, null);

        var entry = Assert.Single(provider.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(ReconnectingEventId, entry.EventId);
        Assert.Equal("Reconnecting to Twitch", entry.Message);
    }

    [Fact]
    public void ParsingErrorEventId_OnADifferentCategory_PassesThroughUnredacted()
    {
        // The category gate, not the event id alone, decides — a coincidental id collision on an
        // unrelated logger must not trigger redaction.
        var (provider, redactingFactory) = CreateFactory();
        var logger = redactingFactory.CreateLogger("TwitchLib.Communication.Clients.WebSocketClient");
        var rawLine = "raw text that must survive on an unrelated category";

        LogParsingErrorCallback(logger, rawLine, new InvalidOperationException("boom"));

        var entry = Assert.Single(provider.Entries);
        Assert.Contains(rawLine, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddProviderAndDispose_DelegateToInnerFactory()
    {
        using var innerFactory = new LoggerFactory();
        var redactingFactory = new RedactingTwitchClientLoggerFactory(innerFactory);
        var provider = new CapturingLoggerProvider();

        redactingFactory.AddProvider(provider);
        var logger = redactingFactory.CreateLogger(TwitchClientCategory);
        LogReconnectingCallback(logger, null);

        Assert.Single(provider.Entries);
    }

    private static (CapturingLoggerProvider Provider, RedactingTwitchClientLoggerFactory Factory) CreateFactory()
    {
        var provider = new CapturingLoggerProvider();
        var innerFactory = new LoggerFactory();
        innerFactory.AddProvider(provider);
        return (provider, new RedactingTwitchClientLoggerFactory(innerFactory));
    }

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
                owner.Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
}
