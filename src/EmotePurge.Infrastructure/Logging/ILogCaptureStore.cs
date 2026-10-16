using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Logging;

/// <summary>
/// The capture writer's own data path into Postgres (recursion layer 1): raw ADO.NET on a dedicated data
/// source without a logger factory, never <c>AppDbContext</c>. Every call is one statement.
/// </summary>
internal interface ILogCaptureStore
{
    /// <summary>
    /// Inserts a batch in one statement; an entry whose <c>CaptureId</c> is already stored is skipped, so
    /// repeating a batch after a lost acknowledgement writes nothing twice.
    /// </summary>
    Task InsertAsync(IReadOnlyList<CapturedLogEntry> batch, CancellationToken ct);

    /// <summary>Upserts this instance's heartbeat row; the stored coverage marker only ever rises.</summary>
    Task UpsertStatusAsync(LogCaptureStatusWrite status, CancellationToken ct);

    /// <summary>The status row of the newest other instance of <paramref name="source"/> (by process start), or <c>null</c>.</summary>
    Task<LogCaptureStatusWrite?> ReadLatestOtherInstanceAsync(LogSource source, string ownInstanceId, CancellationToken ct);
}

/// <summary>One row of <c>LogCaptureStatus</c> as the writer and the disabled-mode heartbeat write it.</summary>
internal sealed record LogCaptureStatusWrite(
    LogSource Source,
    string InstanceId,
    DateTime ProcessStartedUtc,
    DateTime LastFlushUtc,
    bool CaptureEnabled,
    DateTime LastIncompleteAtUtc,
    DateTime? CleanShutdownAtUtc);
