namespace EmotePurge.Api.Health;

/// <summary>
/// The ceilings the admin roster reports against. Twitch's rule and our own admission cap
/// (<c>Channels:MaxActiveChannels</c>) are separate numbers on purpose: each ships with its own
/// field and the UI labels the provenance rather than presenting either as the other.
/// </summary>
internal static class WorkerCapacity
{
    /// <summary>
    /// Twitch's documented cap on how many chatrooms one account may be joined to at a time, in
    /// force since 2024-05-15 (dev.twitch.tv/docs/chat). Channels where the account is broadcaster
    /// or moderator are exempt, which ours never is. A verified bot account lifts it.
    /// </summary>
    public const int TwitchConcurrentChannelLimit = 100;
}
