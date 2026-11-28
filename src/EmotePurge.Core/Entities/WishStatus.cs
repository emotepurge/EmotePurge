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

    /// <summary>
    /// The states in which a wish may carry no emote yet: <see cref="Wish.SevenTvEmoteId"/>,
    /// <see cref="Wish.EmoteName"/> and <see cref="Wish.Alias"/> may hold the empty placeholder here and only
    /// here (K6, CHECK <c>CK_Wishes_EmotePlaceholder</c>). Every transition out of <see cref="Pending"/> into
    /// another state fills all three in the same update.
    /// </summary>
    public static readonly IReadOnlySet<string> EmotePlaceholderAllowed =
        new HashSet<string>(StringComparer.Ordinal) { Pending, Rejected, Expired };

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

    /// <summary>The start is only provisional (EventAPI date) when the expiry falls due; never a reason for a bot removal (plan finding 21).</summary>
    public const string UnconfirmedStart = "unconfirmed_start";
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
        [Expired, Protected, CoverageGap, NotBotOwned, OwnershipLost, Unconfirmed, BotUnavailable, UnconfirmedStart, AlreadyInSet, RepairReview];

    public static readonly IReadOnlyList<string> Failed = [AliasConflict, SetFull, EmoteGone, NotEditor, Unknown];

    public static readonly IReadOnlyList<string> PendingHints = [AmbiguousMatch, AlreadyInSet, AlreadyRunning, Unparsed];
}

/// <summary>
/// The values of <see cref="ChannelWishSettings.BotSuspendedReason"/> and <see cref="ChannelWishSettings.BotActivationError"/>
/// (#487 plan 2.5b, 2.8, 2.9, Revision 6b K6b-8). Field values, not error codes: <c>GET …/wishes/bot</c> passes them on
/// unchanged and the frontend translates them (K5). <see cref="NotEditor"/> is the same text as
/// <see cref="WishStatusReason.NotEditor"/>, the reason of a failed wish.
/// </summary>
public static class WishBotChannelReasons
{
    /// <summary>The bot is not an editor with "manage emote sets" in the channel (suspension and activation error).</summary>
    public const string NotEditor = "not_editor";

    /// <summary>The channel is no longer covered by the pilot list or an activator who is admin by id (suspension).</summary>
    public const string NotListed = "not_listed";

    /// <summary>An invitation of the bot is open and not accepted yet (suspension of an activated channel).</summary>
    public const string InvitationPending = "invitation_pending";

    /// <summary>The bot account is another 7TV account than the one the managers consented to (suspension and activation error).</summary>
    public const string BotAccountChanged = "bot_account_changed";

    /// <summary>The bot could not accept the invitation for this request (activation error).</summary>
    public const string InvitationNotAccepted = "invitation_not_accepted";

    /// <summary>The request was cancelled while an acceptance attempt was open; its effect is still unknown (activation error).</summary>
    public const string InvitationAcceptUnresolved = "invitation_accept_unresolved";

    /// <summary>An acceptance sent before a cancellation took effect after it: the bot is an editor without consent (activation error).</summary>
    public const string InvitationAcceptedAfterAbort = "invitation_accepted_after_abort";

    /// <summary>The four values of <see cref="ChannelWishSettings.BotSuspendedReason"/>.</summary>
    public static readonly IReadOnlyList<string> Suspended = [NotEditor, NotListed, InvitationPending, BotAccountChanged];

    /// <summary>The five values of <see cref="ChannelWishSettings.BotActivationError"/>.</summary>
    public static readonly IReadOnlyList<string> ActivationErrors =
        [NotEditor, InvitationNotAccepted, BotAccountChanged, InvitationAcceptUnresolved, InvitationAcceptedAfterAbort];
}

/// <summary>The values of <see cref="Wish.Source"/>.</summary>
public static class WishSource
{
    public const string Irc = "irc";
    public const string Manual = "manual";
    public const string Adopted = "adopted";
}

/// <summary>The values of <see cref="Wish.MatchKind"/>: what the parser read from a captured redemption text (K6).</summary>
public static class WishMatchKind
{
    /// <summary>A 7TV emote link or id; the wish carries the id.</summary>
    public const string Link = "link";

    /// <summary>A bare name; the wish carries <see cref="Wish.RequestedName"/>, no id.</summary>
    public const string Name = "name";

    /// <summary>Nothing usable; the wish carries the hint <c>unparsed</c>.</summary>
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [Link, Name, None];
}

/// <summary>The values of <see cref="WishCaptureGap.Reason"/> (K6).</summary>
public static class WishCaptureGapReason
{
    public const string Disconnect = "disconnect";
    public const string WorkerRestart = "worker_restart";
    public const string NotJoined = "not_joined";

    public static readonly IReadOnlyList<string> All = [Disconnect, WorkerRestart, NotJoined];
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

    // Chat capture (K6, plan #486 2.8).

    /// <summary>Learned rewards per channel.</summary>
    public const int MaxRewardsPerChannel = 10;

    /// <summary>Open (<c>pending</c>) wishes per channel; above it a redemption is counted and discarded.</summary>
    public const int MaxPendingPerChannel = 200;

    /// <summary>Bounds and default of <c>Wishes:Capture:MaxPendingPerRequester</c> (open wishes per viewer and channel).</summary>
    public const int MaxPendingPerRequesterMin = 1;
    public const int MaxPendingPerRequesterMax = 50;
    public const int DefaultMaxPendingPerRequester = 3;

    /// <summary>Bounds and default of <see cref="ChannelWishSettings.PendingExpiryDays"/>.</summary>
    public const int PendingExpiryDaysMin = 1;
    public const int PendingExpiryDaysMax = 90;
    public const int DefaultPendingExpiryDays = 14;

    public const int RewardLabelMaxLength = 50;

    /// <summary>A captured redemption text, and the learning candidate's text.</summary>
    public const int RequestTextMaxLength = 500;

    public const int RequestedNameMaxLength = 100;

    /// <summary>Twitch's <c>custom-reward-id</c> tag.</summary>
    public const int RewardIdMaxLength = 64;

    /// <summary>Twitch's <c>id</c> tag of a message.</summary>
    public const int IrcMessageIdMaxLength = 64;

    /// <summary>E10: how long after an external removal a returning slot is still taken over.</summary>
    public static readonly TimeSpan AdoptionWindow = TimeSpan.FromHours(6);

    /// <summary>
    /// Look-back before <see cref="Wish.EndedAtUtc"/> for a returning slot of an externally removed
    /// wish, absorbing 7TV timestamps that lie before our own observation (plan finding 6, F1 variant c).
    /// </summary>
    public static readonly TimeSpan ReturnLookBack = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a due wish with only a provisional start is held back by the expiry run (waiting for the
    /// authoritative 7TV date), counted from the expiry and from the channel's last full sync.
    /// </summary>
    public static readonly TimeSpan ProvisionalStartGrace = TimeSpan.FromHours(24);

    // Chat capture durations (K6).

    /// <summary>How long a learning session waits for its candidate and the manager's confirmation.</summary>
    public static readonly TimeSpan LearningWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long a dedupe receipt (<see cref="WishCaptureReceipt"/>) is kept.</summary>
    public static readonly TimeSpan CaptureReceiptRetention = TimeSpan.FromDays(7);

    /// <summary>How far back the capture gaps are shown.</summary>
    public static readonly TimeSpan CaptureGapDisplayWindow = TimeSpan.FromDays(14);

    /// <summary>Shorter stretches without capture are not stored.</summary>
    public static readonly TimeSpan MinCaptureGap = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the bot confirms a write before the deadline step presents it (#487 plan 2.6 point 5, R2-7): anchor plus
    /// 16 minutes, one minute of slack for the fifth read. The anchor is the write, after <c>recheck</c> the recheck.
    /// </summary>
    public static readonly TimeSpan BotConfirmWindow = TimeSpan.FromMinutes(16);
}
