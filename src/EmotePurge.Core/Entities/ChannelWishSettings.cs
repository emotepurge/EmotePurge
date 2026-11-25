namespace EmotePurge.Core.Entities;

/// <summary>
/// Per-channel wish settings, 1:1 with <see cref="Channel"/> (key = <see cref="ChannelId"/>). The row
/// appears with the first write; while it is missing the defaults of <see cref="WishLimits"/> apply.
/// A change affects future wishes and extensions only, never running deadlines.
/// </summary>
public class ChannelWishSettings
{
    public string ChannelId { get; set; } = string.Empty;
    public Channel? Channel { get; set; }

    public int DefaultDurationDays { get; set; } = WishLimits.DefaultDurationDays;
    public int ProbationLeadDays { get; set; } = WishLimits.DefaultProbationLeadDays;

    /// <summary>Uses per day above which a wish is protected; null = protection off, otherwise &gt; 0.</summary>
    public decimal? ProtectionThresholdPerDay { get; set; }

    public bool BotModeEnabled { get; set; }
    public string? BotModeActivatedByTwitchUserId { get; set; }
    public string? BotModeActivatedByLogin { get; set; }
    public DateTime? BotModeActivatedAtUtc { get; set; }
    public DateTime? BotEditorCheckedAtUtc { get; set; }

    /// <summary>The bot account's <c>IdentityVersion</c> at the editor check; null = never checked against one.</summary>
    public int? BotEditorCheckedIdentityVersion { get; set; }

    /// <summary><c>not_editor</c> or <c>not_listed</c>; set means the channel runs in the manual flow.</summary>
    public string? BotSuspendedReason { get; set; }

    public DateTime? BotActivationRequestedAtUtc { get; set; }
    public string? BotActivationRequestedByTwitchUserId { get; set; }
    public string? BotActivationRequestedByLogin { get; set; }

    /// <summary><c>not_editor</c> after a failed check; cleared by the next activate.</summary>
    public string? BotActivationError { get; set; }

    /// <summary>
    /// The 7TV user id of the bot account the managers consented to (plan 2.5b, 2.9): set by <c>activate</c> from the
    /// current bot account, kept by the confirmation. Every bot write requires it to equal the bot account's id; another
    /// account suspends the channel (<c>bot_account_changed</c>). Cleared by a new activation, which sets it afresh.
    /// </summary>
    public string? BotConsentSevenTvUserId { get; set; }

    /// <summary>
    /// When the worker is about to send an invitation acceptance, written durably before sending (own transaction);
    /// null = no attempt open. Resolved only through a read of the editor state, also after a DELETE or the kill switch.
    /// </summary>
    public DateTime? BotInvitationAttemptAtUtc { get; set; }

    /// <summary>
    /// The earliest moment of the next invitation attempt, persisted per channel so it survives a runner change and a
    /// restart; bound to the bot id of <see cref="BotConsentSevenTvUserId"/>. Null = no wait.
    /// </summary>
    public DateTime? BotInvitationRetryAtUtc { get; set; }

    /// <summary>
    /// Days a captured wish may stay <c>pending</c> before it expires (K6, 1 to 90). Carries a database default
    /// as well, because an image older than K6 keeps inserting settings rows without naming the column.
    /// </summary>
    public int PendingExpiryDays { get; set; } = WishLimits.DefaultPendingExpiryDays;
}
