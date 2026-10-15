using System.Collections.Concurrent;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Logging;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// The capture sink. <c>Log()</c> never performs I/O, never waits and never throws: it admits an entry in a
/// fixed order before any expensive work (spec §5.3), builds it with the bounded rendering, and offers it to
/// the <see cref="LogCaptureBuffer"/> without blocking. Everything that is not admitted is counted in
/// <see cref="LogCaptureDropCounters"/> (except the silent suppression scope and the state after
/// <see cref="Close"/>); a dropped entry of level Error or above also raises the
/// <see cref="LogCaptureCoverage"/> marker.
/// <para>
/// Floor and exclusions are fixed per category when the logger is created (§5.2): never below
/// Information; <c>Microsoft.EntityFrameworkCore.Database.Command</c>, <c>Npgsql</c> and <c>Npgsql.*</c>
/// only from Warning; the capture's own categories (<c>EmotePurge.Infrastructure.Logging.*</c>) never.
/// Configuration under the alias (<c>Logging:LogCapture:LogLevel:&lt;Category&gt;</c>) is applied by the
/// <see cref="LoggerFactory"/> before this logger is reached, so it can raise a category but never lower
/// it below these rules.
/// </para>
/// It takes no <see cref="ILogger"/> itself: the provider is created by the logger factory (DI cycle).
/// </summary>
[ProviderAlias("LogCapture")]
public sealed class LogCaptureProvider : ILoggerProvider, ISupportExternalScope
{
    internal const int MaxCategoryLength = 256;
    internal const int MaxEventNameLength = 128;
    internal const string TwitchClientCategory = "TwitchLib.Client.TwitchClient";

    private const string OwnCategoryRoot = "EmotePurge.Infrastructure.Logging";
    private const string EfCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";
    private const string NpgsqlCategoryRoot = "Npgsql";
    private const string RequestPathScopeKey = "RequestPath";
    private const string TraceIdScopeKey = "TraceId";

    // The formatted message is cut to this many characters before scrubbing; that bounds the regex cost
    // and is still twice the stored length, so scrubbing never sees a secret split by the final cut.
    private const int MessageScrubWindow = 2 * LogEntryRendering.MaxMessageLength;

    // Set while this thread formats or renders an entry inside the sink (admission step 3): a log call
    // arriving on the same thread meanwhile (a formatter or value that logs itself) is a re-entry.
    [ThreadStatic]
    private static bool _capturing;

    private readonly LogCaptureBuffer _buffer;
    private readonly LogCaptureBudget _budget;
    private readonly LogCaptureDropCounters _drops;
    private readonly LogCaptureCoverage _coverage;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CaptureLogger> _loggers = new(StringComparer.Ordinal);

    private IExternalScopeProvider? _scopeProvider;
    private volatile bool _closed;

    internal LogCaptureProvider(
        LogCaptureBuffer buffer,
        LogCaptureBudget budget,
        LogCaptureDropCounters drops,
        LogCaptureCoverage coverage,
        TimeProvider time)
    {
        _buffer = buffer;
        _budget = budget;
        _drops = drops;
        _coverage = coverage;
        _time = time;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (name, provider) => new CaptureLogger(provider, name), this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose() => Close();

    /// <summary>
    /// From now on every log call is discarded silently, without a drop count; the buffer is completed so
    /// its reader ends once it has taken what is left. Idempotent.
    /// </summary>
    internal void Close()
    {
        _closed = true;
        _buffer.Complete();
    }

    /// <summary>The lowest level captured for a category; <see cref="LogLevel.None"/> means never.</summary>
    internal static LogLevel MinimumLevelFor(string category)
    {
        if (IsSameOrChild(category, OwnCategoryRoot))
        {
            return LogLevel.None;
        }

        if (IsSameOrChild(category, EfCommandCategory) || IsSameOrChild(category, NpgsqlCategoryRoot))
        {
            return LogLevel.Warning;
        }

        return LogLevel.Information;
    }

    private void Capture<TState>(CaptureLogger logger, LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var level = (LogEntryLevel)(short)logLevel;
        var now = DateTime.MinValue;
        var slotHeld = false;
        var capturing = false;
        try
        {
            now = _time.GetUtcNow().UtcDateTime;
            if (_capturing)
            {
                Drop(LogDropReason.Reentrant, level, now);
                return;
            }

            if (!_budget.TryAdmit(level))
            {
                Drop(LogDropReason.Budget, level, now);
                return;
            }

            if (!_buffer.TryReserveSlot())
            {
                Drop(LogDropReason.QueueFull, level, now);
                return;
            }

            slotHeld = true;
            _capturing = capturing = true;

            var entry = Build(logger, level, now, eventId, state, exception, formatter);
            var bytes = 2L * (entry.Message.Length + (entry.Exception?.Length ?? 0) + (entry.PropertiesJson?.Length ?? 0));

            // TryCommit owns the slot from here: it releases it itself when it rejects the entry.
            slotHeld = false;
            if (!_buffer.TryCommit(entry, bytes))
            {
                Drop(LogDropReason.QueueFull, level, now);
            }
        }
        catch (Exception)
        {
            try
            {
                if (slotHeld)
                {
                    _buffer.ReleaseSlot();
                }

                Drop(LogDropReason.CaptureFailed, level, now == DateTime.MinValue ? DateTime.UtcNow : now);
            }
            catch (Exception)
            {
                // Log() never throws; a failure while counting the failure is lost.
            }
        }
        finally
        {
            if (capturing)
            {
                _capturing = false;
            }
        }
    }

    private CapturedLogEntry Build<TState>(CaptureLogger logger, LogEntryLevel level, DateTime now, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var captureId = Guid.CreateVersion7(new DateTimeOffset(now, TimeSpan.Zero));
        var (message, truncated) = PrepareMessage(formatter(state, exception) ?? string.Empty);
        truncated |= logger.CategoryTruncated;

        string? exceptionType = null;
        string? exceptionText = null;
        if (exception is not null)
        {
            // Second line behind the Worker's redacting wrapper (O3, §4.2a): if that wrapper was bypassed,
            // the exception of these TwitchLib events still contributes only types and stack traces.
            var withhold = logger.IsTwitchClient && IsWithheldTwitchEvent(eventId.Name) && exception is not WithheldMessageException;
            var rendered = LogEntryRendering.RenderException(exception, withhold);
            exceptionType = rendered.Type;
            exceptionText = rendered.Text;
            truncated |= rendered.Truncated;
        }

        var (requestPath, traceId) = ReadScopes();
        var properties = LogEntryRendering.RenderProperties(state, requestPath, traceId);
        truncated |= properties.Truncated;

        string? eventName = null;
        if (!string.IsNullOrEmpty(eventId.Name))
        {
            eventName = LogEntryRendering.NormalizeForPostgres(LogEntryRendering.CutSafely(eventId.Name, MaxEventNameLength));
            truncated |= eventId.Name.Length > MaxEventNameLength;
        }

        return new CapturedLogEntry(
            captureId,
            now,
            level,
            logger.StoredCategory,
            eventId.Id == 0 ? null : eventId.Id,
            eventName,
            message,
            exceptionType,
            exceptionText,
            properties.Json,
            truncated,
            IsSynthetic: false);
    }

    private (string? RequestPath, string? TraceId) ReadScopes()
    {
        var scopes = _scopeProvider;
        if (scopes is null)
        {
            return (null, null);
        }

        var collector = new ScopeCollector();
        scopes.ForEachScope(static (scope, target) => target.Visit(scope), collector);
        return (collector.RequestPath, collector.TraceId);
    }

    private void Drop(LogDropReason reason, LogEntryLevel level, DateTime now)
    {
        if (_closed)
        {
            return;
        }

        _drops.Record(reason, now);
        if (level >= LogEntryLevel.Error)
        {
            _coverage.MarkIncomplete(now);
        }
    }

    /// <summary>
    /// Scrubs the formatted message before the cut (scrubbing after it could leave an unrecognizable token
    /// fragment), cuts it to the stored length and normalizes it. The property JSON and the exception text
    /// are scrubbed inside the rendering and must not be scrubbed again.
    /// </summary>
    private static (string Text, bool Truncated) PrepareMessage(string formatted)
    {
        var preCut = formatted.Length > MessageScrubWindow;
        var scrubbed = SecretScrubber.Scrub(preCut ? LogEntryRendering.CutSafely(formatted, MessageScrubWindow) : formatted);
        var (text, truncated) = LogEntryRendering.TruncateMessage(scrubbed);
        if (preCut && !truncated)
        {
            // Scrubbing shortened a pre-cut text below the limit; it is still incomplete.
            text += LogEntryRendering.TruncatedMarker;
            truncated = true;
        }

        return (LogEntryRendering.NormalizeForPostgres(text), truncated);
    }

    private static bool IsWithheldTwitchEvent(string? eventName) =>
        eventName is "LogParsingError" or "LogUnaccountedFor" or "LogLeavingChannel";

    private static bool IsSameOrChild(string category, string root) =>
        category.StartsWith(root, StringComparison.Ordinal)
        && (category.Length == root.Length || category[root.Length] == '.');

    private sealed class CaptureLogger : ILogger
    {
        private readonly LogCaptureProvider _provider;
        private readonly LogLevel _minimum;

        public CaptureLogger(LogCaptureProvider provider, string category)
        {
            _provider = provider;
            _minimum = MinimumLevelFor(category);
            StoredCategory = LogEntryRendering.NormalizeForPostgres(LogEntryRendering.CutSafely(category, MaxCategoryLength));
            CategoryTruncated = category.Length > MaxCategoryLength;
            IsTwitchClient = string.Equals(category, TwitchClientCategory, StringComparison.Ordinal);
        }

        public string StoredCategory { get; }

        public bool CategoryTruncated { get; }

        public bool IsTwitchClient { get; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => _provider._scopeProvider?.Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= _minimum && logLevel != LogLevel.None && !_provider._closed;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Admission steps 1 (level and category) and 2 (silent suppression scope); 3–5 follow in Capture.
            if (!IsEnabled(logLevel) || LogCaptureSuppression.IsActive)
            {
                return;
            }

            _provider.Capture(this, logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>Takes <c>RequestPath</c> and <c>TraceId</c> from list-shaped scopes, reading at most 17 indices, never past <c>Count</c>.</summary>
    private sealed class ScopeCollector
    {
        public string? RequestPath { get; private set; }

        public string? TraceId { get; private set; }

        public void Visit(object? scope)
        {
            if (scope is not IReadOnlyList<KeyValuePair<string, object?>> list)
            {
                return;
            }

            try
            {
                var count = Math.Min(list.Count, LogEntryRendering.MaxStateIndices);
                for (var i = 0; i < count; i++)
                {
                    var (key, value) = list[i];
                    if (RequestPath is null && value is string path && string.Equals(key, RequestPathScopeKey, StringComparison.OrdinalIgnoreCase))
                    {
                        RequestPath = path;
                    }
                    else if (TraceId is null && value is string traceId && string.Equals(key, TraceIdScopeKey, StringComparison.OrdinalIgnoreCase))
                    {
                        TraceId = traceId;
                    }
                }
            }
            catch (Exception)
            {
                // A scope that throws while being read contributes nothing; the entry is still captured.
            }
        }
    }
}
