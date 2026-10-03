namespace EmotePurge.Core.Services;

/// <summary>
/// Decides when a 7TV answer of "this set holds zero emotes" may be believed although the channel is
/// known to have active emotes. One bad answer from 7TV would archive the whole channel and empty the
/// match cache, so a zero for the same set has to be reported repeatedly, by syncs that are spread
/// out in time, before it is accepted. The state is in memory only: a process restart merely
/// delays an acceptance by the confirmation count.
/// </summary>
public interface IEmptySetConfirmationTracker
{
    /// <summary>
    /// Records one zero-emote answer for <paramref name="channelId"/> and <paramref name="emoteSetId"/>
    /// and says whether it may now be accepted. A zero that follows the previous counted zero too
    /// closely does not count, so a burst of syncs (JOIN, RESYNC, EventAPI follow-ups) cannot
    /// confirm itself. An answer for a different set restarts the streak.
    /// </summary>
    EmptySetVerdict ObserveZero(string channelId, string emoteSetId);

    /// <summary>Forgets the streak: call on any non-empty answer and after an accepted zero.</summary>
    void Reset(string channelId);
}

/// <summary>
/// <paramref name="Accept"/> is true once <paramref name="Streak"/> reached
/// <paramref name="Required"/>. <paramref name="Counted"/> is false when the observation was too
/// close to the previous one and left the streak unchanged.
/// </summary>
public readonly record struct EmptySetVerdict(bool Accept, int Streak, int Required, bool Counted);
