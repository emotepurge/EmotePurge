namespace EmotePurge.Core.Services;

/// <summary>
/// The endpoint-side facade of the admin dashboard (#381): short per-instance caches with
/// single-flight in front of the activity query and the rate-limit telemetry reads, so that a handful
/// of open dashboards polling every minute cost one database or Redis read per window, not one each.
/// </summary>
/// <remarks>
/// A failed read is never cached; the exception reaches every caller waiting on that read and the next
/// call starts afresh. The caches are per Api instance by design — no shared state, no invalidation.
/// </remarks>
public interface IAdminDashboardReader
{
    /// <summary>Daily live-channel counts for the range; cached 60 s per range.</summary>
    Task<AdminDashboardActivity> GetActivityAsync(
        AdminDashboardActivityRange range,
        CancellationToken cancellationToken = default);

    /// <summary>The rate-limit counter snapshot; cached 30 s.</summary>
    Task<RateLimitTelemetrySnapshot> GetRateLimitSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>The 24-hour per-minute rate-limit trend; cached 30 s.</summary>
    Task<RateLimitTelemetryTrend> GetRateLimitTrendAsync(CancellationToken cancellationToken = default);
}
