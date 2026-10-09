using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Auth;

/// <summary>
/// The one place that decides whether a caller looks like a channel's broadcaster (#245), shared by
/// both broadcaster filters and the <c>/permissions</c> handler so they cannot drift. Pure: it reads
/// the stored row and the claims, nothing external.
/// </summary>
internal static class BroadcasterOwnership
{
    /// <summary>
    /// What the filter lets through: a row without a stored Twitch id (the service proves ownership
    /// live against Helix) or a row whose id equals the caller's. Ordinal — ids are opaque digits.
    /// </summary>
    public static bool MayAttempt(Channel channel, TwitchPrincipalInfo principal) =>
        channel.TwitchChannelId is null
        || string.Equals(channel.TwitchChannelId, principal.TwitchUserId, StringComparison.Ordinal);

    /// <summary>
    /// Whether the UI should offer the purge, and who may read its data summary: an id match, or — only
    /// for an id-less row — a match of the caller's current login. The login branch never authorizes
    /// the purge itself (its service proves the login live); it does decide the summary, whose numbers
    /// are what the purge button would show this same caller.
    /// </summary>
    public static bool CanPurge(Channel? channel, TwitchPrincipalInfo principal)
    {
        if (channel is null)
        {
            return false;
        }

        if (channel.TwitchChannelId is not null)
        {
            return string.Equals(channel.TwitchChannelId, principal.TwitchUserId, StringComparison.Ordinal);
        }

        return string.Equals(
            channel.ChannelName, ChannelName.Normalize(principal.TwitchLogin), StringComparison.Ordinal);
    }
}
