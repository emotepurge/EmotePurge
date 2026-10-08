using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The configured set of global admins. Resolved by the immutable Twitch user id; the legacy
/// login-based list only applies while no id list is configured (transitional fallback).
/// </summary>
public interface IGlobalAdminAllowlist
{
    bool IsAdmin(TwitchPrincipalInfo principal);
}
