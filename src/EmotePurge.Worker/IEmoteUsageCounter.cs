using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public interface IEmoteUsageCounter
{
    // emoteSetId is the 7TV set the match cache had active for the channel when this hit was
    // matched (spec 2026-09-20, section 5) — part of the buffering key, not metadata alongside it.
    void Increment(string emoteId, string emoteSetId, UsageCategory category);

    // Puts a failed flush batch back into the counter. DrainAndReset empties it before the flush is
    // even attempted, so without this every failed flush is a guaranteed total loss of that window.
    void Merge(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> counts);

    IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> DrainAndReset();

    // Number of *distinct (EmoteId, EmoteSetId) keys* currently buffered, not the sum of their hit
    // counts — the sum would mean walking the whole dictionary on every health publish, while the
    // entry count is a cheap O(1)-ish read. As a health signal the two say the same thing: a number
    // that keeps growing across publishes means the flush is not draining. Unchanged by the
    // human/bot split and by the shared-chat split: an emote seen only from bots, or only from
    // foreign rooms, still counts as one buffered key. The same emote observed under two different
    // sets (a cache swap between two messages) counts as two.
    int PendingEmoteCount { get; }
}
