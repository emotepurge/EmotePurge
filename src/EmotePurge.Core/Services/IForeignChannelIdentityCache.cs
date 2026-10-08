namespace EmotePurge.Core.Services;

/// <summary>
/// A short-lived cache of one Helix login-to-Twitch-id resolution, scoped to the K3 source-set list
/// route (<c>ForeignEmoteSetService.GetForeignEmoteSetListAsync</c>, spec 2026-09-20 K3 review,
/// finding P3-1). That method's own Helix call sits ahead of <c>ISevenTvEmoteSetListService</c>,
/// which already caches, coalesces, breaks and budgets everything downstream of the Twitch id — so
/// without this cache, reopening the picker for the same channel pays a fresh Helix request and a
/// request-budget permit on every call, even while the list itself would have answered from its own
/// cache, or while its breaker is open and no upstream request would have helped anyway.
/// <para>
/// Only <see cref="TwitchUserLookupStatus.Found"/> is ever held. A login Twitch does not know, or a
/// Helix call that failed outright, is not cached at all: a positive resolution stays true for as
/// long as the channel exists, while a negative one may be a channel that registered moments ago or
/// a transient Helix hiccup, and holding either would make that channel look wrong for the whole
/// shelf-life — a correctness risk the modest saving does not justify (unlike the *downstream* list
/// cache, which already has to hold negative outcomes for its own guard chain to work at all).
/// </para>
/// </summary>
public interface IForeignChannelIdentityCache
{
    /// <summary>Fail-open: a Redis outage reads as a miss, never as an exception.</summary>
    Task<string?> TryGetTwitchUserIdAsync(string normalizedChannelName, CancellationToken cancellationToken = default);

    /// <summary>Fail-open: a lost write only costs the next request a live Helix lookup.</summary>
    Task SetTwitchUserIdAsync(string normalizedChannelName, string twitchUserId, CancellationToken cancellationToken = default);
}
