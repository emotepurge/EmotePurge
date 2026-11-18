namespace EmotePurge.Core.Entities;

/// <summary>
/// The values of <see cref="Wish.Status"/>. Strings, not an enum: the value is persisted and shipped to
/// the frontend as-is, so the text is the contract (same reasoning as <see cref="AuditActions"/>).
/// </summary>
public static class WishStatus
{
    /// <summary>Captured in chat, waiting for a match or approval. Built in K6; no producer before that.</summary>
    public const string Pending = "pending";

    /// <summary>Approved, the entry is not in the set yet.</summary>
    public const string Approved = "approved";

    /// <summary>The bot wrote, the authoritative <c>addedAt</c> is still missing.</summary>
    public const string Unconfirmed = "unconfirmed";

    /// <summary>The bot's add was rejected by 7TV.</summary>
    public const string Failed = "failed";

    /// <summary>The entry is in the set, the clock runs.</summary>
    public const string Active = "active";

    /// <summary>Needs a human decision; the reason is in <see cref="Wish.StatusReason"/>.</summary>
    public const string Presented = "presented";

    /// <summary>Approval refused (K6). Ended.</summary>
    public const string Rejected = "rejected";

    /// <summary>Stayed pending until the expiry window ran out (K6). Ended.</summary>
    public const string Expired = "expired";

    /// <summary>Removed through Emote Purge. Ended.</summary>
    public const string Removed = "removed";

    /// <summary>Removed outside Emote Purge. Ended, with a way back inside the adoption window.</summary>
    public const string RemovedExternally = "removed_externally";

    /// <summary>The entry stays for good, the wish is closed. Ended.</summary>
    public const string Kept = "kept";

    /// <summary>Ended without any action in 7TV.</summary>
    public const string Closed = "closed";

    /// <summary>Folded into a running wish as an extension (K6), or the younger of two running wishes in a channel merge. Ended.</summary>
    public const string Merged = "merged";

    /// <summary>Every value, in the order of the lifecycle table.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Pending, Approved, Unconfirmed, Failed, Active, Presented,
        Rejected, Expired, Removed, RemovedExternally, Kept, Closed, Merged,
    ];

    /// <summary>
    /// The states a wish counts as running in: at most one per channel and 7TV emote id (E13, partial
    /// unique index).
    /// </summary>
    public static readonly IReadOnlySet<string> Running =
        new HashSet<string>(StringComparer.Ordinal) { Approved, Unconfirmed, Active, Presented };

    /// <summary>The terminal states (<see cref="Wish.EndedAtUtc"/> is set while a wish is in one).</summary>
    public static readonly IReadOnlySet<string> Ended =
        new HashSet<string>(StringComparer.Ordinal) { Rejected, Expired, Removed, RemovedExternally, Kept, Closed, Merged };

    public static bool IsRunning(string status) => Running.Contains(status);

    public static bool IsEnded(string status) => Ended.Contains(status);
}

/// <summary>The values of <see cref="Wish.StatusReason"/>.</summary>
public static class WishStatusReason
{
    // presented
    public const string Expired = "expired";
    public const string Protected = "protected";
    public const string CoverageGap = "coverage_gap";
    public const string NotBotOwned = "not_bot_owned";
    public const string OwnershipLost = "ownership_lost";
    public const string Unconfirmed = "unconfirmed";
    public const string BotUnavailable = "bot_unavailable";
    public const string AlreadyInSet = "already_in_set";
    public const string RepairReview = "repair_review";

    // failed
    public const string AliasConflict = "alias_conflict";
    public const string SetFull = "set_full";
    public const string EmoteGone = "emote_gone";
    public const string NotEditor = "not_editor";
    public const string Unknown = "unknown";

    // pending hints (not a state of their own; K6). AlreadyInSet is shared with presented.
    public const string AmbiguousMatch = "ambiguous_match";
    public const string AlreadyRunning = "already_running";
    public const string Unparsed = "unparsed";

    public static readonly IReadOnlyList<string> Presented =
        [Expired, Protected, CoverageGap, NotBotOwned, OwnershipLost, Unconfirmed, BotUnavailable, AlreadyInSet, RepairReview];

    public static readonly IReadOnlyList<string> Failed = [AliasConflict, SetFull, EmoteGone, NotEditor, Unknown];

    public static readonly IReadOnlyList<string> PendingHints = [AmbiguousMatch, AlreadyInSet, AlreadyRunning, Unparsed];
}

/// <summary>The values of <see cref="Wish.Source"/>.</summary>
public static class WishSource
{
    public const string Irc = "irc";
    public const string Manual = "manual";
    public const string Adopted = "adopted";
}

/// <summary>The values of <see cref="Wish.LinkOrigin"/>: how the link to the slot came about.</summary>
public static class WishLinkOrigin
{
    public const string Auto = "auto";
    public const string Adopted = "adopted";
    public const string Bot = "bot";
}

/// <summary>
/// The boundaries every wish layer shares: validation (Api), the matcher and the reconcile
/// (Infrastructure) and the channel settings read these instead of repeating the numbers.
/// </summary>
public static class WishLimits
{
    public const int DurationDaysMin = 1;
    public const int DurationDaysMax = 365;

    public const int ProbationLeadDaysMin = 1;
    public const int ProbationLeadDaysMax = 30;

    /// <summary>Defaults of <see cref="ChannelWishSettings"/>; they also apply when the row is missing.</summary>
    public const int DefaultDurationDays = 30;
    public const int DefaultProbationLeadDays = 7;

    /// <summary>The protection threshold must be greater than this; null means protection off.</summary>
    public const decimal ProtectionThresholdExclusiveMin = 0m;

    public const int AliasMaxLength = 100;
    public const int EmoteNameMaxLength = 100;
    public const int RequesterLoginMaxLength = 25;
    public const int SevenTvEmoteIdMaxLength = 32;

    /// <summary>E10: how long after an external removal a returning slot is still taken over.</summary>
    public static readonly TimeSpan AdoptionWindow = TimeSpan.FromHours(6);

    /// <summary>
    /// Look-back before <see cref="Wish.EndedAtUtc"/> for a returning slot of an externally removed
    /// wish, absorbing 7TV timestamps that lie before our own observation (plan finding 6, F1 variant c).
    /// </summary>
    public static readonly TimeSpan ReturnLookBack = TimeSpan.FromMinutes(30);
}
