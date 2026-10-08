using System.Collections.Concurrent;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// Single-flight coalescing for a 7TV read path: concurrent lookups for the same key share one
/// upstream call chain instead of each running it themselves.
/// </summary>
/// <typeparam name="TResult">
/// Whatever the shared chain produces. Generic on purpose, the same way
/// <see cref="SevenTvLeaderboardStore{TValue}"/> is: the coalescing is the mechanism, and a second
/// read path (the emote-set list, spec 2026-09-20, 6.1) needs exactly it without dragging in the
/// preview's own result type. Each closed type gets its own singleton, so two paths sharing this
/// class share no in-flight table.
/// </typeparam>
/// <remarks>
/// <para>
/// Process-wide singleton per closed type by design, like <c>ChannelSyncGate</c> — no external
/// dependency, no alternative implementation to swap in, so no interface either. The dictionary is
/// bounded by the number of keys with a lookup genuinely in flight at once, which given the
/// provider-wide concurrency budget (<see cref="ForeignEmoteSetProviderBudget.MaxConcurrent"/>) is
/// tiny.
/// </para>
/// <para>
/// <b>The shared work is nobody's request.</b> Cancellation is split in two: the shared execution
/// runs under whatever token <paramref name="upstream"/> closed over (the caller gives it one that
/// belongs to no caller), while each caller waits on the shared task under <i>its own</i> token. The
/// first caller through the door used to donate its request-cancellation token to the shared task, so
/// that one client navigating away — the browser aborting a request is entirely routine — cancelled
/// the lookup out from under every other caller waiting on it. A caller may abandon its own wait; it
/// may not abandon everyone else's work.
/// </para>
/// </remarks>
public class ForeignEmoteSetRequestCoalescer<TResult>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<TResult>>> _inFlight = new(StringComparer.Ordinal);

    /// <param name="key">
    /// What the callers must agree on to share a chain. Two closed types never collide here, so a
    /// key only has to be unique within its own path.
    /// </param>
    /// <param name="callerCancellationToken">
    /// Cancels only this caller's wait. The shared execution keeps running for whoever else is
    /// waiting on it (and still populates the cache, so the abandoned work is not wasted).
    /// </param>
    public Task<TResult> CoalesceAsync(
        string key,
        Func<Task<TResult>> upstream,
        CancellationToken callerCancellationToken = default)
    {
        // Lazy<T>'s default thread-safety mode means the factory below can run at most once even if
        // GetOrAdd races and constructs more than one Lazy instance for the same key — only the one
        // ConcurrentDictionary actually stores ever has its Value observed, and constructing a Lazy
        // does not itself invoke the delegate it wraps, so a discarded race loser costs nothing.
        var entry = _inFlight.GetOrAdd(
            key,
            k => new Lazy<Task<TResult>>(() => RunAsync(k, upstream)));

        // WaitAsync rather than a plain await: it detaches this caller from the shared task instead of
        // cancelling it.
        return entry.Value.WaitAsync(callerCancellationToken);
    }

    private async Task<TResult> RunAsync(string key, Func<Task<TResult>> upstream)
    {
        try
        {
            return await upstream();
        }
        finally
        {
            // Removed once finished, successfully or not — a permanently cached "in flight" entry
            // would freeze every later lookup for this channel on today's answer.
            _inFlight.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// The foreign-channel-import preview's closed instantiation (spec 2026-09-09, section 6), keyed on
/// the normalized channel name alone, regardless of <c>refresh</c> — a cache-miss lookup arriving
/// while a forced refresh for the same channel is already in flight can safely share that refresh's
/// outcome, and vice versa; both end up running the exact same chain.
/// </summary>
/// <remarks>
/// A named subclass rather than a bare <c>ForeignEmoteSetRequestCoalescer&lt;ForeignEmoteSetLookupResult&gt;</c>
/// so that the preview path's registration, its decorator and its tests keep naming the same type
/// they always did: closing the type generically was supposed to leave that path bit-for-bit
/// unchanged (spec 6.1, AK 94), and renaming it everywhere would have been a change.
/// </remarks>
public sealed class ForeignEmoteSetRequestCoalescer : ForeignEmoteSetRequestCoalescer<ForeignEmoteSetLookupResult>;
