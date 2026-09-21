namespace EmotePurge.Core.Services;

/// <summary>
/// Owns every write to <see cref="Entities.ChannelEmoteSetObservation"/> (spec section 4.3): no
/// caller touches the table directly, so the "at most one open interval per channel" invariant has
/// exactly one place that can break it, and the partial unique index is there only as the backstop
/// for a bug, not as the contract's primary enforcement.
/// </summary>
public interface IChannelEmoteSetObservationService
{
    /// <summary>
    /// The occasion "a successful <c>SyncChannelAsync</c>" (spec 4.3, rows 1 and 2). If no interval
    /// is currently open for <paramref name="channelId"/>, opens one for <paramref name="emoteSetId"/>
    /// — tracked only, so it rides whichever <c>SaveChangesAsync</c> the caller issues next, even one
    /// that also carries unrelated changes. This covers both a channel's first-ever sync and a
    /// rejoin onto a set the log last closed under the same id — the decision looks only at whether
    /// an interval is open right now, never at history, so it opens a fresh row even then.
    /// <para>
    /// If an interval is already open under a *different* set, closes it
    /// (<c>ClosedBy = ChannelEmoteSetObservationClosedBy.SetSwitch</c>) and opens the new one itself,
    /// in its own transaction with its own <c>SaveChangesAsync</c> calls, before returning — a set
    /// switch must not straddle the caller's unrelated pending changes, and it must never leave the
    /// channel with a closed interval and nothing open, or with two intervals open at once.
    /// </para>
    /// <para>If the open interval already carries this exact set, does nothing.</para>
    /// </summary>
    Task RecordObservedSetAsync(string channelId, string emoteSetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The occasions "leave", "rename" (at join and via the periodic identity reconcile) and "merge"
    /// (the surviving channel) — spec 4.3, rows 3–5. Closes the currently open interval for
    /// <paramref name="channelId"/> with <paramref name="closedBy"/>, if one is open — tracked only,
    /// so it rides the caller's own <c>SaveChangesAsync</c>. A no-op when nothing is open: every call
    /// site closes a channel that is expected to have an open interval, but enforcing that is not
    /// this method's job.
    /// </summary>
    Task CloseOpenIntervalAsync(string channelId, string closedBy, CancellationToken cancellationToken = default);
}
