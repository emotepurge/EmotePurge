using System.Collections;

namespace EmotePurge.Worker;

/// <summary>
/// Wraps the <see cref="ILoggerFactory"/> handed to <c>TwitchLib.Client.TwitchClient</c> so that
/// its own internal logging — which we do not write and cannot change — cannot put a raw,
/// unredacted IRC line into our log sink (#246).
/// <para>
/// TwitchLib's read loop hands the <em>entire</em> line, chat text and identifying tags included,
/// to two of its own <c>LoggerMessage</c>-generated calls on category
/// <c>TwitchLib.Client.TwitchClient</c> — decompiled from TwitchLib.Client 4.0.1
/// (<c>~/.nuget/packages/twitchlib.client/4.0.1/lib/net10.0/TwitchLib.Client.dll</c>,
/// <c>TwitchLib.Client.Extensions.LogExtensions</c>):
/// <code>
/// [LoggerMessage(LogLevel.Error, "Unexpected error during message parsing, message: {message}")]
/// public static void LogParsingError(this ILogger&lt;TwitchClient&gt; logger, string message, Exception ex);
///
/// [LoggerMessage(LogLevel.Warning, "Unaccounted for: {ircString} (please create a TwitchLib GitHub issue :P)")]
/// public static void LogUnaccountedFor(this ILogger&lt;TwitchClient&gt; logger, string ircString);
/// </code>
/// Both are at or above the Worker's configured minimum level for this category (Information,
/// <c>appsettings.json</c>), so both reach the sink unless intercepted — <c>LogParsingError</c> is
/// the one #246 names explicitly, <c>LogUnaccountedFor</c> is the same class of leak found while
/// implementing it (an unrecognised IRC line is logged whole too) and handled the same way.
/// </para>
/// <para>
/// Matched by <see cref="EventId.Name"/> rather than the numeric id — the id is a hash Roslyn's
/// <c>LoggerMessage</c> source generator derives from the method, stable across patch releases in
/// practice but not a documented contract; the name is the generated method name and just as
/// specific within this single category. Every other log call on this category — and everything on
/// any other category the wrapped factory is asked for, including
/// <c>TwitchLib.Communication.Clients.WebSocketClient</c> — passes through unchanged: the signal
/// (that a parse failed, that a line was unaccounted for, and everything else this category logs)
/// must survive, only the payload of these two specific calls is replaced.
/// </para>
/// <para>
/// Deliberately not solved by raising this category's minimum level: <c>TwitchLib.Client.TwitchClient</c>
/// also logs connection-relevant events (e.g. <c>LogReconnecting</c>) that must keep reaching the
/// sink at their own level.
/// </para>
/// </summary>
public sealed class RedactingTwitchClientLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    private const string TwitchClientCategory = "TwitchLib.Client.TwitchClient";

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public ILogger CreateLogger(string categoryName)
    {
        var logger = inner.CreateLogger(categoryName);
        return categoryName == TwitchClientCategory ? new RedactingLogger(logger) : logger;
    }

    public void Dispose() => inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (TryRedact(eventId, state, out var redactedMessage))
            {
                inner.Log(logLevel, eventId, new RedactedState(redactedMessage), exception,
                    static (redacted, _) => redacted.Message);
                return;
            }

            inner.Log(logLevel, eventId, state, exception, formatter);
        }

        private static bool TryRedact<TState>(EventId eventId, TState state, out string redactedMessage)
        {
            var rawLineParameterName = eventId.Name switch
            {
                "LogParsingError" => "message",
                "LogUnaccountedFor" => "ircString",
                _ => null,
            };

            if (rawLineParameterName is null || state is not IReadOnlyList<KeyValuePair<string, object>> parameters)
            {
                redactedMessage = string.Empty;
                return false;
            }

            var rawLine = FindNamedParameter(parameters, rawLineParameterName) as string;
            var redactedLine = TwitchLibRawLineRedaction.Redact(rawLine);
            redactedMessage = $"TwitchLib.{eventId.Name} (redacted, #246): {redactedLine}";
            return true;
        }

        private static object? FindNamedParameter(IReadOnlyList<KeyValuePair<string, object>> parameters, string name)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Key == name)
                {
                    return parameter.Value;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Minimal <c>ILogger</c> state carrying only the already-redacted message, shaped like a
    /// structured-logging state (one <c>{OriginalFormat}</c> entry) so sinks that special-case that
    /// interface still get something sensible instead of falling back to <c>ToString()</c> alone.
    /// </summary>
    private sealed class RedactedState(string message) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public string Message { get; } = message;

        public int Count => 1;

        public KeyValuePair<string, object?> this[int index] => index == 0
            ? new KeyValuePair<string, object?>("{OriginalFormat}", Message)
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return this[0];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => Message;
    }
}
