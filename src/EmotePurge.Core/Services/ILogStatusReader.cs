using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// Cached read of the log capture status (spec sections 8.1 and 11): 30 s TTL with single-flight, a failed
/// read is never cached, and every fill runs in a scope of its own inside the capture's suppression scope,
/// so the Api route only has to authorize and map. Per-instance state, like the other dashboard readers.
/// </summary>
public interface ILogStatusReader
{
    Task<LogCaptureStatusView> ReadAsync(CancellationToken ct);
}

/// <summary>
/// What <c>GET /api/admin/logs/status</c> reports: liveness per source (<see cref="Sources"/>, always exactly
/// api and worker), the 24 h error coverage kept apart from it, and the retention state.
/// </summary>
public sealed record LogCaptureStatusView(
    DateTime GeneratedAtUtc,
    IReadOnlyList<LogSourceStatus> Sources,
    LogCoverage Coverage24h,
    LogRetentionStatus Retention);

/// <summary>Liveness of one source, taken from the row of its newest process instance (<c>null</c> fields when missing).</summary>
public sealed record LogSourceStatus(
    LogSource Source,
    LogSourceState State,
    string? InstanceId,
    DateTime? StartedUtc,
    DateTime? LastFlushUtc);

public enum LogSourceState
{
    /// <summary>Fresh heartbeat, capture on.</summary>
    Healthy,

    /// <summary>The newest instance has not flushed for more than 5 minutes.</summary>
    Stale,

    /// <summary>Fresh heartbeat, but the process runs with capture switched off.</summary>
    Disabled,

    /// <summary>No status row for the source (cold start, restore without status data, expired row).</summary>
    Missing,
}

/// <summary>
/// Error coverage of the last 24 hours. <see cref="Complete"/> is true only when both sources have a row and
/// the gap marker of each newest instance is older than 24 h; <see cref="RecordedErrors"/> is a lower bound
/// when it is not complete. Independent of liveness.
/// </summary>
public sealed record LogCoverage(
    bool Complete,
    int RecordedErrors,
    IReadOnlyList<LogIncompleteSince> IncompleteSince);

public sealed record LogIncompleteSince(LogSource Source, DateTime LastIncompleteAtUtc);

/// <summary><see cref="Overdue"/>: the oldest entry is older than 7 days + 2 hours.</summary>
public sealed record LogRetentionStatus(DateTime? OldestEntryUtc, bool Overdue);
