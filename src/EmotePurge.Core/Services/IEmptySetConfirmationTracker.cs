namespace EmotePurge.Core.Services;

/// <summary>
/// Decides when a 7TV answer of "this set holds zero emotes" may be believed although the channel is
/// known to have active emotes. One bad answer from 7TV would archive the whole channel and empty the
/// match cache, so a zero for the same set has to be reported by consecutive syncs that are spread
/// out in time before it is accepted. "Consecutive" means consecutive observations: the caller resets
/// the streak on every other outcome (a non-empty answer, a failed lookup, an unusable response, a
/// live push, a v4 contradiction, the channel leaving the roster). Keyed by the normalized channel
/// name, the key the match cache uses too. The state is in memory only: a process restart merely
/// delays an acceptance by the confirmation count.
/// </summary>
public interface IEmptySetConfirmationTracker
{
    /// <summary>
    /// Records one zero-emote answer for <paramref name="channelName"/> and <paramref name="emoteSetId"/>
    /// and says whether it may now be accepted. A zero that follows the previous counted zero too
    /// closely does not count, so a burst of syncs (JOIN, RESYNC, EventAPI follow-ups) cannot
    /// confirm itself. An answer for a different set restarts the streak.
    /// </summary>
    EmptySetVerdict ObserveZero(string channelName, string emoteSetId);

    /// <summary>
    /// Forgets the streak of <paramref name="channelName"/>. Called on every observation that is not
    /// a counted zero, after an accepted zero, and when the channel leaves the worker's roster.
    /// </summary>
    void Reset(string channelName);
}

/// <summary>
/// <paramref name="Accept"/> is true once <paramref name="Streak"/> reached
/// <paramref name="Required"/>. <paramref name="Counted"/> is false when the observation was too
/// close to the previous one and left the streak unchanged.
/// </summary>
public readonly record struct EmptySetVerdict(bool Accept, int Streak, int Required, bool Counted);
