namespace EmotePurge.Core.Services;

/// <summary>
/// Verdict on whether the worker is receiving chat from every channel it is meant to be in (#516).
/// Only <see cref="Failing"/> and <see cref="Unavailable"/> alarm.
/// </summary>
public enum ChannelJoinHealthStatus
{
    /// <summary>Nothing alarming: every wished join is confirmed, within its grace, or an excluded rejection.</summary>
    Healthy,

    /// <summary>No usable roster yet and the Api process is younger than the roster TTL: a cold start, not a fault.</summary>
    Starting,

    /// <summary>The verdict cannot be reached (roster missing or stale, legacy schema, truncated, or the database read failed).</summary>
    Unavailable,

    /// <summary>At least one channel has waited longer than the grace period and counts for the alarm.</summary>
    Failing
}

/// <summary>An active channel and the moment it was last meant to start being tracked.</summary>
/// <param name="ChannelName">Normalized channel name.</param>
/// <param name="ActivatedAtUtc"><c>TrackingResumedAt ?? CreatedAt</c>.</param>
public sealed record ActiveChannelActivation(string ChannelName, DateTime ActivatedAtUtc);

/// <summary>An IRC-wished channel whose JOIN is not confirmed, within its grace or not.</summary>
/// <param name="Overdue">Waited longer than the grace period.</param>
/// <param name="CountsForAlarm">Overdue and not a rejection Twitch stated outright (suspended, banned).</param>
public sealed record ChannelJoinHealthEntry(
    string ChannelName,
    int UnconfirmedSeconds,
    string? Reason,
    bool Overdue,
    bool CountsForAlarm);

/// <summary>An active channel in the database that the worker holds no IRC wish for.</summary>
public sealed record ChannelJoinHealthMissingIntent(string ChannelName, int WaitingSeconds, bool Overdue);

/// <summary>
/// The evaluation result. <see cref="Entries"/> and <see cref="MissingIntent"/> are empty unless the
/// status is <see cref="ChannelJoinHealthStatus.Healthy"/> or <see cref="ChannelJoinHealthStatus.Failing"/>.
/// </summary>
/// <param name="AlarmingCount">Entries that count for the alarm plus overdue missing-intent channels.</param>
/// <param name="ExcludedCount">Overdue entries that do not count (suspended, banned).</param>
public sealed record ChannelJoinHealthReport(
    ChannelJoinHealthStatus Status,
    int GraceSeconds,
    IReadOnlyList<ChannelJoinHealthEntry> Entries,
    IReadOnlyList<ChannelJoinHealthMissingIntent> MissingIntent,
    int AlarmingCount,
    int ExcludedCount);

/// <summary>
/// Judges the worker roster's per-channel join state. The whole decision lives here; callers only
/// map it (HTTP status, admin card).
/// </summary>
public interface IChannelJoinHealthService
{
    /// <summary>
    /// Uncached evaluation of an already-read roster (null = no roster). Reads the active channel list
    /// itself; a failing read makes the result <see cref="ChannelJoinHealthStatus.Unavailable"/>.
    /// Throws only <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<ChannelJoinHealthReport> EvaluateAsync(WorkerRosterSnapshot? snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// The public decision: true for <see cref="ChannelJoinHealthStatus.Failing"/> and
    /// <see cref="ChannelJoinHealthStatus.Unavailable"/>. Cached for 5 s behind a shared single-flight
    /// fill with its own 2 s deadline; the "unavailable" outcome is cached like any other.
    /// </summary>
    Task<bool> IsAlarmingAsync(CancellationToken cancellationToken = default);
}
