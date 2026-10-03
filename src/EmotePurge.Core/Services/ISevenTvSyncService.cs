using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

public interface ISevenTvSyncService
{
    /// <summary>
    /// Resolves and fully reconciles a channel's active 7TV emote set against Postgres.
    /// Returns the resolved set id plus the channel's 7TV account id (for EventAPI subscriptions),
    /// or null if the channel has no 7TV account/emote set. <see cref="SevenTvSyncResult.HasChanges"/>
    /// tells the caller whether anything actually changed, so unattended callers can stay silent on
    /// a no-op resync.
    /// <para>
    /// Anything the caller keys on the channel afterwards — an EventAPI subscription, a
    /// <c>channel.synced</c> publish — belongs on <see cref="SevenTvSyncResult.ChannelName"/> and not
    /// on <paramref name="channelName"/>: the sync re-reads its row under the row gate, so a rename
    /// committed while this call was queued means the two differ (issue #60).
    /// </para>
    /// </summary>
    Task<SevenTvSyncResult?> SyncChannelAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds the channel's match cache from Postgres when it is empty, and does nothing else: no
    /// 7TV or Twitch call, no database write, no failure recording. Takes the same gates and the same
    /// excluded-channel check as <see cref="SyncChannelAsync"/>. Returns nothing: an unknown or vanished
    /// (merged) row is a silent no-op rather than an error, and so is a cache that already holds
    /// names. An excluded row is not silent: as in the sync, its cache entry is removed and a Debug
    /// line is written. Lets boot recovery make a channel count from its join without waiting for the
    /// 7TV round trip of its sync.
    /// </summary>
    Task WarmChannelAsync(string channelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one 7TV EventAPI dispatch delta to a single channel, under the same per-channel
    /// gate the full resync uses, followed by a full match-cache reload. Applies nothing when
    /// <paramref name="emoteSetId"/> is no longer the channel's active set (a still-live
    /// subscription on a set the channel has switched away from) or when the delta would wipe a
    /// non-empty channel (implausible; see the empty-set guard in SyncChannelAsync).
    /// Callers — not this method — run the follow-up full resync those outcomes ask for:
    /// SyncChannelAsync takes the same non-reentrant gate and would deadlock if called from here.
    /// <para>
    /// <see cref="SevenTvDeltaOutcome.Applied"/> is the "something really changed" signal (it is
    /// returned only after a persisted write) — that is what makes the caller publish
    /// <c>channel.synced</c>; every other outcome wrote nothing.
    /// </para>
    /// <para>
    /// As with <see cref="SyncChannelAsync"/>, the follow-up belongs on
    /// <see cref="SevenTvDeltaResult.ChannelName"/> rather than on <paramref name="channelName"/>
    /// wherever that is non-null (issue #60).
    /// </para>
    /// </summary>
    Task<SevenTvDeltaResult> ApplyEmoteSetUpdateAsync(
        string channelName,
        string emoteSetId,
        SevenTvEmoteSetDelta delta,
        CancellationToken cancellationToken = default);
}
