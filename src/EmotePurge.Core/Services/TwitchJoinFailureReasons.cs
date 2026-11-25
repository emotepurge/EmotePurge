namespace EmotePurge.Core.Services;

/// <summary>
/// Why the worker's IRC JOIN for a channel has not been confirmed, as a stable, language-neutral
/// code (Regel 7). Plain strings rather than an enum so the JSON wire value is exactly the value
/// named here, independent of serializer settings — same reasoning as
/// <see cref="SevenTvSyncFailureReasons"/>.
/// <para>
/// Mirrored in the admin roster card; every code needs an
/// <c>admin.roster.joinFailure.reason.&lt;code&gt;</c> key in <b>both</b> locale files.
/// </para>
/// </summary>
public static class TwitchJoinFailureReasons
{
    /// <summary>
    /// Twitch never answered the JOIN (TwitchLib's join timer expired). The only reason that is
    /// not a concrete server statement, and the one a later attempt may replace a concrete reason with.
    /// </summary>
    public const string NoConfirmation = "noConfirmation";

    /// <summary>Twitch answered the JOIN with <c>msg_channel_suspended</c>.</summary>
    public const string ChannelSuspended = "channelSuspended";

    /// <summary>Twitch answered with <c>msg_concurrent_channel_limit_reached</c>: the account is over its chatroom cap.</summary>
    public const string ConcurrentChannelLimit = "concurrentChannelLimit";

    /// <summary>Twitch answered with <c>msg_banned</c>: the account may not join this channel.</summary>
    public const string Banned = "banned";
}
