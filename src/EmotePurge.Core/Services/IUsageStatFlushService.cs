namespace EmotePurge.Core.Services;

/// <summary>
/// A trio of usage counts for one emote, split first by which room the message came from and,
/// within the channel's own room, by who triggered it.
/// </summary>
/// <param name="Human">
/// What <c>UsageStat.UseCount</c> has always meant and now means exclusively: chat messages from
/// the channel's own room, from chatters that <c>IBotChatterDetector</c> did not classify as a
/// bot. "Own room" — see <see cref="SharedChat"/>.
/// </param>
/// <param name="Bot">
/// What lands in <c>UsageStat.BotUseCount</c>: chat messages from the channel's own room, from a
/// recognized bot account. Kept, not dropped — see the DECISIONS entry for 2026-09-01 on why a
/// misclassification in either direction stays reparable.
/// </param>
/// <param name="SharedChat">
/// What lands in <c>UsageStat.SharedChatUseCount</c>: every message mirrored in from a foreign
/// room during a Twitch Shared Chat session, bots included, plus messages whose room cannot be
/// determined — see the DECISIONS entry for 2026-09-06 (#73) for both the room rule and why an
/// undeterminable message is folded in here rather than getting its own category. The room
/// question is decided before the human/bot question: a foreign-room message is always
/// <see cref="SharedChat"/>, never <see cref="Human"/> or <see cref="Bot"/>, regardless of who
/// sent it.
/// </param>
public readonly record struct EmoteUsageCounts(int Human, int Bot, int SharedChat);

/// <summary>
/// The buffering/flush pipeline's key: a slot together with the 7TV set the match cache had
/// active when the chat message that produced the hit was matched (spec 2026-09-20, section 5;
/// emote slots spec 6.1). Shared by <c>EmotePurge.Worker</c> (buffering) and
/// <c>EmotePurge.Infrastructure</c> (flush), so it lives here rather than in either project alone.
/// <para>
/// <c>SlotId</c> is whatever the match map handed out for the matched name: an <c>EmoteSlots.Id</c>,
/// or — with the slot reconcile flag off, for an emote whose (emote, set) pair has no slot yet — an
/// opaque pending key that only the flush can resolve (plan #373, E4). Everything outside
/// Infrastructure treats both the same: an opaque string.
/// </para>
/// </summary>
public readonly record struct UsageCounterKey(string SlotId, string EmoteSetId);

/// <summary>
/// What one flush did (plan #373, E9). <paramref name="WrittenChannels"/> are the distinct normalized
/// names of the channels whose usage was actually written, by either phase — the input alone cannot
/// answer that, since keys of meanwhile-deleted slots or emotes are dropped; callers announce them,
/// it is not an error signal. <paramref name="Requeued"/> are the keys the flush could not resolve
/// this time, with their counts, to be put back and tried again by the caller; empty when everything
/// landed or was dropped. A key is in exactly one of three states: written, dropped (gone, never
/// requeued) or requeued.
/// </summary>
public sealed record UsageFlushResult(
    IReadOnlyCollection<string> WrittenChannels,
    IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> Requeued)
{
    /// <summary>Nothing written, nothing requeued.</summary>
    public static UsageFlushResult Empty { get; } = new([], new Dictionary<UsageCounterKey, EmoteUsageCounts>());
}

public interface IUsageStatFlushService
{
    /// <summary>
    /// Upserts a drained snapshot of in-memory usage counts (keyed by slot and the observed set →
    /// (human, bot, shared chat)) into today's UTC UsageStat rows, in two phases: first every key that
    /// names an existing slot, then — in a transaction of its own — every pending key, resolved to the
    /// base slot of its (emote, set) pair.
    /// </summary>
    /// <returns>
    /// The written channels and the keys to requeue (see <see cref="UsageFlushResult"/>). Throws only
    /// when the first phase fails, and then nothing was written: the caller puts the whole batch back.
    /// A failure of the second phase never throws, because the first phase is committed by then —
    /// its keys come back in <see cref="UsageFlushResult.Requeued"/> instead.
    /// </returns>
    Task<UsageFlushResult> FlushAsync(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> usageCounts, CancellationToken cancellationToken = default);
}
