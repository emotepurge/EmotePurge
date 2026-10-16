namespace EmotePurge.Core.Services;

/// <summary>
/// Retention periods as the one place in the code where the numbers stand. Periods for personal data
/// mirror the privacy policy (#247); system-load samples are non-personal operational telemetry and
/// have separate periods. They are constants, not configuration, so an environment variable cannot
/// silently change the retention contract. What is configurable is only whether the job writes and
/// how often it runs (<c>Retention:*</c>).
/// </summary>
/// <remarks>
/// "Twelve months" is 365 days — a fixed span, not a calendar month count, so a cutoff is one
/// subtraction and does not move with leap years or month lengths. Every period is measured
/// exclusively: a row exactly one period old is not due yet.
/// </remarks>
public static class RetentionPolicy
{
    /// <summary>
    /// Encrypted Twitch tokens are cleared 30 days after the user was last active
    /// (<c>max(LastLogin, LastSeenAtUtc)</c>). Safe by construction: the session cookie slides over
    /// 14 days, so a user unseen for 30 days has no valid session left that could use the token.
    /// </summary>
    public static readonly TimeSpan TwitchTokens = TimeSpan.FromDays(30);

    /// <summary>
    /// A user account is deleted twelve months after the user was last active
    /// (<c>max(LastLogin, LastSeenAtUtc)</c>), through the account deletion path.
    /// </summary>
    public static readonly TimeSpan InactiveAccount = TimeSpan.FromDays(365);

    /// <summary>
    /// An ended vote session is deleted, with its votes and ballot, twelve months after it ended
    /// (<c>EndedAt</c>, falling back to <c>StartedAt</c>). No totals are kept. Open sessions never.
    /// </summary>
    public static readonly TimeSpan EndedVoteSession = TimeSpan.FromDays(365);

    /// <summary>An audit log entry is deleted twelve months after it occurred (<c>OccurredAtUtc</c>).</summary>
    public static readonly TimeSpan AuditLogEntry = TimeSpan.FromDays(365);

    /// <summary>
    /// A channel the bot left is deleted with its whole history 180 days after it was deactivated
    /// (<c>DeactivatedAtUtc</c>). Active channels and their statistics stay.
    /// </summary>
    public static readonly TimeSpan DeactivatedChannel = TimeSpan.FromDays(180);

    /// <summary>
    /// A finished chat-log backfill run (completed, failed or cancelled) is deleted 365 days after it
    /// finished (<c>FinishedAtUtc</c>), with its snapshot rows. Coverage days and imported usage rows
    /// stay (they are statistics, not the request record).
    /// </summary>
    public static readonly TimeSpan ChatLogBackfillRun = TimeSpan.FromDays(365);

    /// <summary>
    /// Raw samples remain for seven days and until their own and any immediate successor hour has rolled up.
    /// </summary>
    public static readonly TimeSpan SystemLoadSample = TimeSpan.FromDays(7);

    /// <summary>
    /// A captured log entry (<c>LogEntry.TimestampUtc</c>) is deleted after seven days; the heartbeat row
    /// of a process instance (<c>LogCaptureStatus.LastFlushUtc</c>) follows the same period. Saved log
    /// filters are configuration, not captured data, and are never expired. Application logs are not
    /// meant to carry personal data; the period is the operational window of the admin log viewer.
    /// </summary>
    public static readonly TimeSpan LogEntry = TimeSpan.FromDays(7);

    /// <summary>Hourly system-load summaries remain for thirteen calendar months.</summary>
    public const int SystemLoadHourlyMonths = 13;
}
