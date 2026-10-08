namespace EmotePurge.Core.Services;

/// <summary>
/// The verdict of <see cref="ITrackedEmoteSetMembershipService.CheckAsync"/>. No payload: the caller
/// needs the decision only.
/// </summary>
public enum TrackedEmoteSetMembership
{
    /// <summary>The set is the channel's active set or a <c>NORMAL</c> set of its 7TV account.</summary>
    Member,

    /// <summary>
    /// The set does not belong to the channel — including a channel with no resolved Twitch identity
    /// and one whose Twitch connection has no 7TV account.
    /// </summary>
    NotMember,

    /// <summary>The channel is not tracked.</summary>
    ChannelNotFound,

    /// <summary>The channel's set list could not be read (7TV rate limit, outage, own budget).</summary>
    SevenTvUnavailable
}

/// <summary>
/// Proves that a 7TV emote set belongs to a tracked channel — the gate in front of the
/// tracked-channel set preview, so that route cannot be used to read arbitrary sets.
/// </summary>
public interface ITrackedEmoteSetMembershipService
{
    /// <summary>
    /// Fail-closed: anything short of a positive proof is not <see cref="TrackedEmoteSetMembership.Member"/>.
    /// The channel name is normalised here.
    /// </summary>
    Task<TrackedEmoteSetMembership> CheckAsync(string channelName, string emoteSetId, CancellationToken cancellationToken = default);
}
