namespace EmotePurge.Core.Services;

/// <summary>
/// The 60 s Redis cache on a foreign channel's assembled emote set preview (spec 2026-09-09, E3):
/// the whole point is caching the finished <see cref="ForeignEmoteSet"/>, not 7TV's raw answer — the
/// resolution chain behind it is itself two upstream requests before the paginated set read even
/// starts, and re-running all of that on every "open the import dialog again" click is exactly what
/// this exists to avoid. Consumed only by the hardening decorator around
/// <see cref="IForeignEmoteSetService"/> (T2) — the base implementation never touches it.
/// </summary>
public interface IForeignEmoteSetCache
{
    /// <summary>Fail-open: a Redis outage reads as a miss, never as an exception.</summary>
    Task<ForeignEmoteSet?> TryGetAsync(string normalizedChannelName, CancellationToken cancellationToken = default);

    /// <summary>Fail-open: a lost write only costs the next request a live lookup.</summary>
    Task SetAsync(string normalizedChannelName, ForeignEmoteSet emoteSet, CancellationToken cancellationToken = default);

    /// <summary>
    /// The set-ID read mode's own key space (spec 2026-09-20, E12): a second namespace in the same
    /// cache, never the same key as <see cref="TryGetAsync"/>/<see cref="SetAsync"/> for the same
    /// channel — an entry read by set id must never be overwritten by, or overwrite, the entry for
    /// that channel's currently active set, and vice versa. Fail-open, same as
    /// <see cref="TryGetAsync"/>.
    /// </summary>
    Task<ForeignEmoteSet?> TryGetBySetIdAsync(string emoteSetId, CancellationToken cancellationToken = default);

    /// <summary>The set-ID key space's write side. Fail-open, same as <see cref="SetAsync"/>.</summary>
    Task SetBySetIdAsync(string emoteSetId, ForeignEmoteSet emoteSet, CancellationToken cancellationToken = default);
}
