using System.Collections.Concurrent;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public class EmoteUsageCounter : IEmoteUsageCounter
{
    private ConcurrentDictionary<UsageCounterKey, EmoteUsageCounts> _counts = new();

    // The TArg overload of AddOrUpdate is used deliberately: a plain closure over `category` would
    // allocate on every call, and this runs once per matched emote per chat message. Passing
    // `category` as the factory argument keeps both lambdas static, so Increment allocates nothing
    // beyond the dictionary's own first insert per key — UsageCategory is a value type and does
    // not box across the generic TArg.
    public void Increment(string emoteId, string emoteSetId, UsageCategory category)
        => _counts.AddOrUpdate(
            new UsageCounterKey(emoteId, emoteSetId),
            static (_, cat) => cat switch
            {
                UsageCategory.Bot => new EmoteUsageCounts(Human: 0, Bot: 1, SharedChat: 0),
                UsageCategory.SharedChat => new EmoteUsageCounts(Human: 0, Bot: 0, SharedChat: 1),
                _ => new EmoteUsageCounts(Human: 1, Bot: 0, SharedChat: 0),
            },
            static (_, current, cat) => cat switch
            {
                UsageCategory.Bot => current with { Bot = current.Bot + 1 },
                UsageCategory.SharedChat => current with { SharedChat = current.SharedChat + 1 },
                _ => current with { Human = current.Human + 1 },
            },
            category);

    public void Merge(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> counts)
    {
        foreach (var (key, addition) in counts)
        {
            // Same TArg pattern as Increment, for the same reason: `addition` travels as the
            // factory argument instead of being captured by a closure.
            _counts.AddOrUpdate(
                key,
                static (_, added) => added,
                static (_, current, added) => new EmoteUsageCounts(
                    current.Human + added.Human,
                    current.Bot + added.Bot,
                    current.SharedChat + added.SharedChat),
                addition);
        }
    }

    public IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> DrainAndReset()
        => Interlocked.Exchange(ref _counts, new ConcurrentDictionary<UsageCounterKey, EmoteUsageCounts>());

    // Volatile.Read because DrainAndReset swaps the whole dictionary out from under concurrent
    // readers; without it this could observe a stale reference.
    public int PendingEmoteCount => Volatile.Read(ref _counts).Count;
}
