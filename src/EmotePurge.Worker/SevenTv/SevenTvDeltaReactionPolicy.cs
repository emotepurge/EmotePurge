using EmotePurge.Core.SevenTv;

namespace EmotePurge.Worker.SevenTv;

/// <summary>What the EventAPI client does after one delta was applied to one channel.</summary>
public enum SevenTvDeltaReaction
{
    /// <summary>Nothing changed; stay silent.</summary>
    None,

    /// <summary>The delta persisted a write: announce <c>channel.synced</c>.</summary>
    PublishSynced,

    /// <summary>Fall back to a full REST resync of the channel, adopting its result.</summary>
    ResyncAdoptResult,

    /// <summary>The channel is gone: drop its subscription entry and request a sync.</summary>
    RemoveSubscription
}

/// <summary>
/// Pure mapping from a delta outcome to the client's follow-up. Kept apart from
/// <c>SevenTvEventClient</c> (a transport class, verified live) so the decision is testable.
/// </summary>
public static class SevenTvDeltaReactionPolicy
{
    /// <summary>
    /// Unknown (future) outcomes fall back to a resync: it is the only reaction that is always safe,
    /// and it converges the channel on whatever the delta could not express.
    /// </summary>
    public static SevenTvDeltaReaction Decide(SevenTvDeltaOutcome outcome) => outcome switch
    {
        SevenTvDeltaOutcome.Applied => SevenTvDeltaReaction.PublishSynced,
        SevenTvDeltaOutcome.NoChange => SevenTvDeltaReaction.None,
        SevenTvDeltaOutcome.SetNotActive => SevenTvDeltaReaction.ResyncAdoptResult,
        SevenTvDeltaOutcome.ImplausibleSkipped => SevenTvDeltaReaction.ResyncAdoptResult,
        SevenTvDeltaOutcome.AmbiguousDelta => SevenTvDeltaReaction.ResyncAdoptResult,
        SevenTvDeltaOutcome.ChannelUnknown => SevenTvDeltaReaction.RemoveSubscription,
        _ => SevenTvDeltaReaction.ResyncAdoptResult
    };
}
