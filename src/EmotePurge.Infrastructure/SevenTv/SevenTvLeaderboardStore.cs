using System.Collections.Concurrent;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// What one fill produced: the value to hand out, and how long it may be handed out for.
/// </summary>
/// <param name="Value">The assembled answer — a hit, an empty list, or a negatively cached failure.</param>
/// <param name="TimeToLive">
/// From <see cref="SevenTvLeaderboardTtlPolicy"/>. Counted from the moment the fill <i>finished</i>,
/// not from the moment it started, so a slow fill never hands out an answer that is already stale.
/// </param>
public readonly record struct SevenTvLeaderboardStoreFill<TValue>(TValue Value, TimeSpan TimeToLive);

/// <summary>
/// The in-process stock behind the 7TV leaderboard (spec 2026-09-13, section 6, "Der Vorrat —
/// Komposition, verbindlich"): a tiny keyed cache with a shelf-life and single-flight coalescing
/// built in, so that however many readers arrive for a key, at most one fill for it is ever running.
/// </summary>
/// <typeparam name="TValue">
/// Whatever the caller's fill produces. Generic on purpose: the stock is the mechanism that keeps
/// the upstream lid on, and it must be provable — and provably concurrent — without dragging in the
/// leaderboard service's own result types.
/// </typeparam>
/// <remarks>
/// <para>
/// <b>Why not <c>ForeignEmoteSetRequestCoalescer</c>.</b> That class is the right pattern with one
/// difference that decides everything here: it removes the entry in a <c>finally</c>, on the
/// perfectly good grounds that "a permanently cached 'in flight' entry would freeze every later
/// lookup on today's answer". A stock with a shelf-life must solve that differently — it keeps the
/// entry and lets time invalidate it.
/// </para>
/// <para>
/// <b>Replacement, never removal — and never a blind add-or-update.</b> An expired entry is swapped
/// with <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/> against the exact entry the reader
/// looked at. A naive add-or-update lets two readers who arrive at the same instant of expiry each
/// start a fill, which silently doubles the upstream lid; the loser of the swap therefore takes the
/// winner's entry rather than its own. Removal is never right either: a key whose fill failed would
/// be refilled on every click, and the failure shelf-life — the thing that makes the lid hold on a
/// bad afternoon — would never apply.
/// </para>
/// <para>
/// <b>A running entry never expires.</b> The expiry lives on the fill's completion, so an entry that
/// has not finished has no expiry at all and cannot be swapped out from under the readers already
/// waiting on it. (Where the expiry physically sits is implementation freedom per the spec; putting
/// it on the completion is what makes "the factory sets it when it finishes" true by construction
/// rather than by discipline, and removes any window in which a finished entry has no expiry yet.)
/// </para>
/// <para>
/// <b>A faulted or cancelled fill counts as expired immediately</b>, and is replaced by the next
/// reader like any other expired entry. Such a task can only come from the caller's own backstop —
/// an unexpected exception that skipped every result path — because every outcome the fill
/// <i>expects</i>, a rate limit included, is a value it returns, not an exception it throws.
/// </para>
/// <para>
/// <b>The fill is nobody's request.</b> It runs under <see cref="CancellationToken.None"/>, while
/// each reader waits on the shared task under its own token — the same split
/// <c>ForeignEmoteSetRequestCoalescer</c> documents. Without it, one browser navigating away would
/// cancel the fill for everyone else waiting on it, and worse, leave a cancelled entry behind that
/// the next reader has to pay for.
/// </para>
/// <para>
/// <b>No eviction.</b> The key space is fixed and tiny (two sort keys, a few hundred rows each), so
/// there is nothing to evict; an eviction policy would only add a second way for an entry to
/// disappear.
/// </para>
/// </remarks>
public sealed class SevenTvLeaderboardStore<TValue>(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // The value is the entry itself: reference equality is what TryUpdate compares, which is exactly
    // the "swap only if nobody beat me to it" check the expiry race needs.
    private readonly ConcurrentDictionary<string, Lazy<Task<Completion>>> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// How many keys the stock holds. It only ever grows to the number of distinct keys asked for —
    /// an expired, failed or cancelled entry is replaced in place, never dropped.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Hands out the stocked value for <paramref name="key"/>, filling it first if there is nothing
    /// for that key or what is there has passed its shelf-life.
    /// </summary>
    /// <param name="key">The stock key — one entry per key, pages are not part of it.</param>
    /// <param name="fill">
    /// Produces the value and its shelf-life. Invoked at most once per fill, always with
    /// <see cref="CancellationToken.None"/>: the work belongs to the stock, not to whoever happened
    /// to ask first.
    /// </param>
    /// <param name="callerCancellationToken">
    /// Cancels this caller's wait only. The fill keeps running for whoever else is waiting on it, and
    /// still stocks its result.
    /// </param>
    public Task<TValue> GetOrFillAsync(
        string key,
        Func<CancellationToken, Task<SevenTvLeaderboardStoreFill<TValue>>> fill,
        CancellationToken callerCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fill);

        while (true)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                // Lazy's default thread-safety mode means the fill runs at most once per entry, no
                // matter how many readers reach this line together.
                var work = entry.Value;

                if (!IsExpired(work, _timeProvider.GetUtcNow()))
                {
                    return AwaitAsync(work, callerCancellationToken);
                }

                var replacement = NewEntry(fill);
                if (_entries.TryUpdate(key, replacement, entry))
                {
                    return AwaitAsync(replacement.Value, callerCancellationToken);
                }

                // Somebody else replaced the same expired entry first. Their fill is the one that
                // runs; this reader loops, takes it, and drops its own replacement — which, never
                // having had its Value observed, cost nothing upstream.
                continue;
            }

            var created = NewEntry(fill);
            if (_entries.TryAdd(key, created))
            {
                // Whatever this fill produces is this reader's answer, failure included: an entry is
                // only ever judged expired by a *later* reader, never re-run inside the call that
                // installed it.
                return AwaitAsync(created.Value, callerCancellationToken);
            }
        }
    }

    private static bool IsExpired(Task<Completion> work, DateTimeOffset now)
    {
        if (!work.IsCompleted)
        {
            // Still filling: it has no expiry yet, and must not be replaced out from under its readers.
            return false;
        }

        // Faulted or cancelled: the caller's backstop produced it, and it is stale on arrival.
        return !work.IsCompletedSuccessfully || now >= work.Result.ExpiresAt;
    }

    private static async Task<TValue> AwaitAsync(Task<Completion> work, CancellationToken callerCancellationToken)
    {
        // WaitAsync rather than a plain await: it detaches this caller from the shared fill instead
        // of cancelling it.
        var completion = await work.WaitAsync(callerCancellationToken).ConfigureAwait(false);
        return completion.Value;
    }

    private Lazy<Task<Completion>> NewEntry(Func<CancellationToken, Task<SevenTvLeaderboardStoreFill<TValue>>> fill) =>
        new(() => RunAsync(fill));

    private async Task<Completion> RunAsync(Func<CancellationToken, Task<SevenTvLeaderboardStoreFill<TValue>>> fill)
    {
        var filled = await fill(CancellationToken.None).ConfigureAwait(false);

        return new Completion(filled.Value, _timeProvider.GetUtcNow() + filled.TimeToLive);
    }

    private readonly record struct Completion(TValue Value, DateTimeOffset ExpiresAt);
}
