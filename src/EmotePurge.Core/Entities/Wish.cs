namespace EmotePurge.Core.Entities;

/// <summary>
/// A wish: one channel's request for a 7TV emote to be in the set for a limited time. Status values are
/// the <see cref="WishStatus"/> constants; every change goes through <c>WishTransitions</c>. The row
/// stores no moderator identity — who did what lives only in the audit log.
/// </summary>
public class Wish
{
    public long Id { get; set; }

    public string ChannelId { get; set; } = string.Empty;
    public Channel? Channel { get; set; }

    /// <summary>The 7TV id, not <see cref="Emote.Id"/> (rule 8).</summary>
    public string SevenTvEmoteId { get; set; } = string.Empty;

    /// <summary>Default name at 7TV when the wish was entered; display only.</summary>
    public string EmoteName { get; set; } = string.Empty;

    /// <summary>The wanted respectively current name in the set.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>One of <see cref="WishSource"/>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>One of <see cref="WishStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>One of <see cref="WishStatusReason"/>; set for <c>presented</c> and <c>failed</c>.</summary>
    public string? StatusReason { get; set; }

    public DateTime StatusChangedAtUtc { get; set; }

    /// <summary>The redeemer; empty for a manual entry.</summary>
    public string? RequesterTwitchUserId { get; set; }
    public string? RequesterLogin { get; set; }

    public int DurationDays { get; set; } = WishLimits.DefaultDurationDays;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }

    /// <summary>7TV's <c>addedAt</c> of the entry.</summary>
    public DateTime? StartsAtUtc { get; set; }

    /// <summary>False while the slot's <c>AddedAtFromRest</c> is false.</summary>
    public bool StartsAtAuthoritative { get; set; }

    /// <summary>Start plus duration plus extensions.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Set in every ended state; the clock of the retention.</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>The current slot. Kept in every ended state; SET NULL when the slot is deleted.</summary>
    public string? SlotId { get; set; }
    public EmoteSlot? Slot { get; set; }

    public int ExtensionCount { get; set; }

    /// <summary>"Add again, permanently": the reconcile then ends in <c>kept</c> instead of <c>active</c>.</summary>
    public bool Permanent { get; set; }

    public long? MergedIntoWishId { get; set; }

    public bool BotOwned { get; set; }
    public DateTime? BotOwnedAddedAtUtc { get; set; }
    public DateTime? BotWriteAtUtc { get; set; }
    public int BotAttemptCount { get; set; }
    public DateTime? BotNextAttemptAtUtc { get; set; }

    public DateTime? RemovalQueuedAtUtc { get; set; }
    public int? RemovalQueuedVersion { get; set; }

    /// <summary>Counts every manager action and every ownership change (E25).</summary>
    public int DecisionVersion { get; set; }

    // The four columns below go beyond the spec's table 6.1; see the DECISIONS entry "Wish emotes,
    // child 1: backend core" for the reasons.

    /// <summary>One of <see cref="WishLinkOrigin"/>; null while no slot was ever linked.</summary>
    public string? LinkOrigin { get; set; }

    /// <summary>The linked slot's <c>AddedAt</c> at the last match: the generation of the link.</summary>
    public DateTime? SlotAddedAtUtc { get; set; }

    /// <summary>The last reconcile that saw the slot active in this generation (written at most every 30 minutes).</summary>
    public DateTime? SlotConfirmedAtUtc { get; set; }

    /// <summary>Set on the first entry into <c>active</c>; never changed or cleared afterwards.</summary>
    public DateTime? FirstActivatedAtUtc { get; set; }

    // Chat capture (K6). Filled only for Source = irc; RequestText and RequestedName are requester data.

    /// <summary>The learned reward this wish was redeemed with; SET NULL when the reward is forgotten.</summary>
    public long? RewardId { get; set; }
    public ChannelWishReward? Reward { get; set; }

    /// <summary>Twitch's <c>id</c> tag of the redemption message; unique per channel while set.</summary>
    public string? IrcMessageId { get; set; }

    /// <summary>The redemption's text, as typed by the viewer.</summary>
    public string? RequestText { get; set; }

    /// <summary>The emote name the parser read from <see cref="RequestText"/>, if any.</summary>
    public string? RequestedName { get; set; }

    /// <summary>One of <see cref="WishMatchKind"/>; null for wishes not captured in chat.</summary>
    public string? MatchKind { get; set; }

    /// <summary>
    /// Whether the wish names a 7TV emote. An open wish (<c>pending</c>, and the ended <c>rejected</c> and
    /// <c>expired</c>) may carry the empty string as a placeholder in <see cref="SevenTvEmoteId"/>,
    /// <see cref="EmoteName"/> and <see cref="Alias"/> (CHECK <c>CK_Wishes_EmotePlaceholder</c>); every other
    /// state has all three. Every lookup or comparison by emote id asks here first. Not mapped; a query
    /// compares <c>SevenTvEmoteId != ""</c> instead.
    /// </summary>
    public bool HasEmote => SevenTvEmoteId.Length > 0;
}
