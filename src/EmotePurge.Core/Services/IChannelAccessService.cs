namespace EmotePurge.Core.Services;

// AccessToken is the cookie-claim token, null once its claimed expiry has passed. Consumers must
// not use it directly for Helix calls — ITwitchUserTokenService takes the whole principal and
// serves the claim token while valid, then falls back to the server-side refresh flow.
public record TwitchPrincipalInfo(string TwitchUserId, string TwitchLogin, string? AccessToken);

/// <summary>
/// The answer of <see cref="IChannelAccessService.ResolveWishCaptureAccessAsync"/> (wish emotes K6, plan #486 2.4a):
/// the one channel row the question was decided on. <see cref="ChannelId"/> is carried on to every service call that
/// reads or changes K6 data, so a rename between the check and the query can never hit another row.
/// <see cref="IdConfirmed"/> is true only when the row carries a <see cref="TwitchChannelId"/> and the caller is
/// confirmed against exactly that id; a row without one is never confirmed.
/// </summary>
public sealed record WishCaptureAccess(string ChannelId, string? TwitchChannelId, bool IdConfirmed)
{
    /// <summary>False while the row has no Twitch id: nothing is learned or captured for it, nothing given out.</summary>
    public bool IsBound => TwitchChannelId is not null;
}

public interface IChannelAccessService
{
    Task<bool> CanManageChannelAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default);

    // Weaker than CanManageChannelAsync: additionally lets a channel's 7TV editors (per 7TV's own
    // editor_of relationship, not a Twitch role) view its usage stats — but NOT join/leave the bot,
    // manage vote sessions, or anything else CanManageChannelAsync gates.
    Task<bool> CanViewUsageStatsAsync(TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default);

    // Channel-independent check for the admin allowlist (Auth:AdminTwitchUserIds, login list as transitional fallback) — used by
    // endpoints that aren't scoped to a single channel, e.g. the admin "list all channels" overview.
    bool IsGlobalAdmin(TwitchPrincipalInfo principal);

    // Stricter than IsGlobalAdmin: true only for an admin on Auth:AdminTwitchUserIds, never through the
    // login fallback. Gates the one admin right that overrides a broadcaster's own decision — lifting
    // their re-add lock (#245) — since a login can be released and registered by someone else.
    bool IsGlobalAdminById(TwitchPrincipalInfo principal);

    /// <summary>
    /// Who may see and change K6 data of a channel (plan #486 2.4a, Sol-R2-P1-1): reads the one channel row of
    /// <paramref name="channelName"/> and decides on exactly that row. Confirmed "on the id" is a caller for a row that
    /// carries a <c>TwitchChannelId</c> and who is a global admin by id (<see cref="IsGlobalAdminById"/>), or whose
    /// Twitch id equals it (broadcaster), or whose moderated-channel list holds an entry with that
    /// <c>BroadcasterId</c>. The login plays no role anywhere, so a login Twitch has reassigned never opens the old
    /// row's data. A list that cannot be determined right now answers "not confirmed", as in the moderator check.
    /// Null when no channel row has that name.
    /// </summary>
    Task<WishCaptureAccess?> ResolveWishCaptureAccessAsync(
        TwitchPrincipalInfo principal, string channelName, CancellationToken cancellationToken = default);
}
