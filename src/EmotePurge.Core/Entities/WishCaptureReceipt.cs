namespace EmotePurge.Core.Entities;

/// <summary>
/// The content-free proof that the capture has decided an IRC message (K6 dedupe): key
/// (<see cref="ChannelId"/>, <see cref="IrcMessageId"/>), no text, no login, no user id. Written for a
/// candidate and for a wish alike, attached to no wish, so neither confirming a learning session nor an
/// account deletion lets the same line create a second wish. Removed after
/// <see cref="WishLimits.CaptureReceiptRetention"/>.
/// </summary>
public class WishCaptureReceipt
{
    public string ChannelId { get; set; } = string.Empty;
    public Channel? Channel { get; set; }

    /// <summary>Twitch's <c>id</c> tag of the message.</summary>
    public string IrcMessageId { get; set; } = string.Empty;

    public DateTime SeenAtUtc { get; set; }
}
