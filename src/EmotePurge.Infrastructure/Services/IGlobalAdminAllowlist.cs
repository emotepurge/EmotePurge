using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The configured set of global admins. Resolved by the immutable Twitch user id; the legacy
/// login-based list only applies while no id list is configured (transitional fallback).
/// </summary>
public interface IGlobalAdminAllowlist
{
    bool IsAdmin(TwitchPrincipalInfo principal);

    /// <summary>
    /// True only when the principal's Twitch id is on the configured id list — never through the
    /// login fallback. For the rights that must hang on the immutable id (#245: lifting a
    /// broadcaster's re-add lock).
    /// </summary>
    bool IsAdminById(TwitchPrincipalInfo principal);
}
