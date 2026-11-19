namespace EmotePurge.Core.Entities;

/// <summary>
/// A channel-point reward a manager has taught Emote Purge (K6): redemptions carrying its
/// <c>custom-reward-id</c> tag become wishes. At most <see cref="WishLimits.MaxRewardsPerChannel"/> per channel.
/// The chat delivers no title, so <see cref="Label"/> is the manager's own name for it.
/// </summary>
public class ChannelWishReward
{
    public long Id { get; set; }

    public string ChannelId { get; set; } = string.Empty;
    public Channel? Channel { get; set; }

    /// <summary>The value of Twitch's <c>custom-reward-id</c> tag; unique per channel. Never leaves the Api.</summary>
    public string RewardId { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Pre-fills <see cref="Wish.DurationDays"/> of the wishes this reward creates.</summary>
    public int DefaultDurationDays { get; set; } = WishLimits.DefaultDurationDays;

    /// <summary>False pauses the reward without forgetting it: its redemptions are discarded.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The last redemption the capture saw; shown as "last seen".</summary>
    public DateTime? LastRedemptionSeenAtUtc { get; set; }
}
