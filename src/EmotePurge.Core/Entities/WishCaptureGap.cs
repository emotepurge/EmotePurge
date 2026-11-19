namespace EmotePurge.Core.Entities;

/// <summary>
/// A stretch in which the chat capture of a channel was not listening (K6): an approximation with about a
/// minute's resolution, never a measurement. Only stretches of at least
/// <see cref="WishLimits.MinCaptureGap"/> are stored.
/// </summary>
public class WishCaptureGap
{
    public long Id { get; set; }

    public string ChannelId { get; set; } = string.Empty;
    public Channel? Channel { get; set; }

    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }

    /// <summary>One of <see cref="WishCaptureGapReason"/>.</summary>
    public string Reason { get; set; } = string.Empty;
}
