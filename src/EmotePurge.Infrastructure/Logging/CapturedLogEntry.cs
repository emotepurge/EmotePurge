using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// One entry as admitted by the sink, or built by the writer for a drop report (<c>IsSynthetic</c>). Every
/// string is already bounded, scrubbed and normalized for Postgres. Source, instance id and process start
/// come from <see cref="LogCaptureIdentity"/> when the writer stores the entry.
/// </summary>
internal sealed record CapturedLogEntry(
    Guid CaptureId,
    DateTime TimestampUtc,
    LogEntryLevel Level,
    string Category,
    int? EventId,
    string? EventName,
    string Message,
    string? ExceptionType,
    string? Exception,
    string? PropertiesJson,
    bool Truncated,
    bool IsSynthetic);
