using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Api.Tests;

// Everything a console line could show, plus the scopes the framework opens around a request.
internal sealed class TraceCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<Line> _lines = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<Line> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    public sealed record Line(string Category, LogLevel Level, string Message, string? Exception, string Rest)
    {
        public string AllText => string.Join('\n', Category, Message, Exception ?? string.Empty, Rest);
    }

    private sealed class Logger(TraceCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var rest = new StringBuilder();
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                {
                    rest.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
                }
            }

            owner._scopes.ForEachScope(
                (scope, builder) =>
                {
                    builder.Append(scope).Append('\n');
                    if (scope is IEnumerable<KeyValuePair<string, object?>> scopeValues)
                    {
                        foreach (var pair in scopeValues)
                        {
                            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
                        }
                    }
                },
                rest);
            owner._lines.Enqueue(new Line(category, logLevel, formatter(state, exception), exception?.ToString(), rest.ToString()));
        }
    }
}
