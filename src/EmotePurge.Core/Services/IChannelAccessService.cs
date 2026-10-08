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
}
