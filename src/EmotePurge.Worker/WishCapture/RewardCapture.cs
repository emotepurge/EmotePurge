using EmotePurge.Core.Chat;
using EmotePurge.Core.Services;
using TwitchLib.Client.Models;

namespace EmotePurge.Worker.WishCapture;

/// <summary>
/// The capture branch of <c>TwitchChatManager.OnMessageReceived</c>, pulled out of the manager so the TwitchLib
/// contract tests run the very same field reads the handler runs (AK48) without a client or a fake. Entered only when
/// <see cref="ChatMessageRoute.Observe"/> says so, i.e. for a line that carries <c>custom-reward-id</c>.
/// </summary>
public static class RewardCapture
{
    /// <summary>
    /// The observation one parsed line yields, or <c>null</c>. Reads only typed <see cref="ChatMessage"/> properties
    /// — TwitchLib.Client 4.0.1 types <c>custom-reward-id</c> as <see cref="ChatMessage.CustomRewardId"/>, so it never
    /// appears in <c>UndocumentedTags</c> (plan W1) — plus the Shared Chat markers, which are untyped and come through
    /// <see cref="SharedChatRule.FromTags"/>.
    /// </summary>
    public static RewardObservation? Read(ChatMessage message)
    {
        var origin = SharedChatRule.FromTags(message.RoomId, message.UndocumentedTags);
        return RewardRedemptionParser.Parse(
            message.CustomRewardId,
            origin,
            message.RoomId,
            message.Id,
            message.UserId,
            message.Username,
            message.Message,
            message.TmiSent.UtcDateTime);
    }

    /// <summary>
    /// Reads the observation and hands it to <paramref name="sink"/> without waiting. Never throws: an exception here
    /// would hit TwitchLib's event chain, so it is counted (<see cref="WorkerStats.RecordRewardCaptureFault"/>) and
    /// swallowed, and the line's emote counting runs on unchanged (AK49). Never logs — not the text, not the login,
    /// not the user id (AK64).
    /// </summary>
    /// <param name="message">The line, as TwitchLib parsed it.</param>
    /// <param name="snapshotIsEmpty">The channel's emote snapshot was empty when the handler read it; a queued
    /// observation is then counted as such, the live proof for AK67 (Sol-R2-P2-9).</param>
    /// <param name="sink">Where the observation goes.</param>
    /// <param name="stats">The worker's counters.</param>
    public static void Capture(ChatMessage message, bool snapshotIsEmpty, IRewardObservationSink sink, WorkerStats stats)
    {
        try
        {
            var observation = Read(message);
            if (observation is not null && sink.TryEnqueue(observation) && snapshotIsEmpty)
            {
                stats.RecordRewardObservationAtEmptySnapshot();
            }
        }
        catch (Exception)
        {
            // Deliberately every exception: the read loop must never see one from this branch (plan 2.1).
            stats.RecordRewardCaptureFault();
        }
    }
}
