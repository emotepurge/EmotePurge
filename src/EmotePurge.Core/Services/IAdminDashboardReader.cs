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

    /// <summary>
    /// The system-load series for the range; cached 30 s for <c>1h</c>/<c>24h</c>, 60 s for longer ranges.
    /// Rebuilds run one at a time across all ranges; when the wait for the turn exceeds 5 s the answer is
    /// <see cref="AdminDashboardLoadRead.Busy"/> and no rebuild was started.
    /// </summary>
    Task<AdminDashboardLoadRead> GetLoadAsync(
        AdminDashboardLoadRange range,
        CancellationToken cancellationToken = default);

    /// <summary>The rate-limit counter snapshot; cached 30 s.</summary>
    Task<RateLimitTelemetrySnapshot> GetRateLimitSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>The 24-hour per-minute rate-limit trend; cached 30 s.</summary>
    Task<RateLimitTelemetryTrend> GetRateLimitTrendAsync(CancellationToken cancellationToken = default);
}

/// <summary>The facade's answer for a load read: the series, or a busy marker with the suggested retry delay.</summary>
public sealed record AdminDashboardLoadRead(AdminDashboardLoad? Load, bool Busy, int RetryAfterSeconds)
{
    public static AdminDashboardLoadRead Ok(AdminDashboardLoad load) => new(load, false, 0);

    public static AdminDashboardLoadRead BusyFor(int retryAfterSeconds) => new(null, true, retryAfterSeconds);
}
