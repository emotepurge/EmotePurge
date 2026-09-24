namespace EmotePurge.Core.Services;

/// <summary>
/// The retention periods decided on 2026-09-23 (#243/#244), as the one place in the code where the
/// numbers stand. They are constants, not configuration, on purpose: the privacy policy states them
/// (#247), and an environment variable that silently changed one would turn that text into a lie.
/// What is configurable is only whether the job writes and how often it runs (<c>Retention:*</c>).
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
}
