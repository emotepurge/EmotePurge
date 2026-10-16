using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// Thread-safe stand-in for the console provider: keeps, per log call, the category, the level, the
/// rendered message, <c>exception.ToString()</c> and the string form of every structured state value —
/// everything a console line could show. Registered next to the capture sink so a test can assert
/// that neither of them sees something.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLine> _lines = new();

    public IReadOnlyList<CapturedLine> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    public sealed record CapturedLine(string Category, LogLevel Level, EventId EventId, string Message, string? Exception, IReadOnlyList<string> StateValues)
    {
        /// <summary>Everything the line could print, joined, for "does not contain" assertions.</summary>
        public string AllText => string.Join('\n', [Category, Message, Exception ?? string.Empty, .. StateValues]);
    }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new List<string>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    values.Add($"{list[i].Key}={list[i].Value}");
                }
            }

            provider._lines.Enqueue(new CapturedLine(category, logLevel, eventId, formatter(state, exception), exception?.ToString(), values));
        }
    }
}
