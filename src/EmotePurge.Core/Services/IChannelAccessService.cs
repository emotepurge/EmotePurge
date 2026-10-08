namespace EmotePurge.Core.Services;

// AccessToken is the cookie-claim token, null once its claimed expiry has passed. Consumers must
// not use it directly for Helix calls — ITwitchUserTokenService takes the whole principal and
// serves the claim token while valid, then falls back to the server-side refresh flow.
public record TwitchPrincipalInfo(string TwitchUserId, string TwitchLogin, string? AccessToken);

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
}
