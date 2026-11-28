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

    /// <summary>
    /// The same id-only answer for a bare Twitch user id, for callers without a principal (#487 plan 2.3, K6b-14: the
    /// worker and the per-channel bot state, asked about <c>BotModeActivatedByTwitchUserId</c>). Never the login list.
    /// </summary>
    bool IsAdminById(string twitchUserId);
}
