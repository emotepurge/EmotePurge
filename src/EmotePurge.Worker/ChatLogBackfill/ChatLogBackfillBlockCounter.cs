using EmotePurge.Core.Chat;
using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.ChatLogBackfill;

/// <summary>
/// Counts one block of a backfill run (spec 4.4, D9): one instance per block attempt, fed message by
/// message from the archive callback, read once with <see cref="Aggregates"/>. Pure — no I/O, no
/// clock, no logger.
/// <para>
/// It counts the way the live path counts: the room rule is <see cref="SharedChatRule"/>, the
/// room-vs-bot precedence is <see cref="UsageCategoryRule"/>, and the name match is
/// <see cref="EmoteNameMatching"/> (split on spaces, ordinal, one hit per value and message). It counts
/// per slot of the run's snapshot (slot plan #375): the map's values are slot ids, so two aliases of
/// one emote in one message count once each. What it adds is the block boundary (a message dated
/// outside the block is dropped) and the entry-day gate of the chosen set, per slot (a hit before the
/// entry joined that set is not counted, B9). The excluded-chatter
/// gate is the caller's, applied before <see cref="Count"/> (same placement as the harness); the
/// message counter is the archive client's, not this class's (D47).
/// </para>
/// <para>
/// No chatter id is kept: the user id is read once for the bot decision and never stored, so nothing
/// this instance holds or returns can identify a chatter.
/// </para>
/// </summary>
public sealed class ChatLogBackfillBlockCounter
{
    private readonly ChatLogBackfillBlock _block;
    private readonly ChatLogBackfillSnapshotMap _snapshot;
    private readonly Func<string?, IReadOnlyList<KeyValuePair<string, string>>?, bool> _isBot;
    private readonly Dictionary<(string SlotId, DateOnly Day), Buckets> _cells = [];
    private readonly HashSet<string> _matched = new(StringComparer.Ordinal);

    /// <param name="block">The days this instance counts.</param>
    /// <param name="snapshot">The run's snapshot: alias → slot, the entry-day gate per slot, each slot's emote (spec 4.3).</param>
    /// <param name="isBot">The live bot classifier (<see cref="IBotChatterDetector.IsBot"/>).</param>
    public ChatLogBackfillBlockCounter(
        ChatLogBackfillBlock block,
        ChatLogBackfillSnapshotMap snapshot,
        Func<string?, IReadOnlyList<KeyValuePair<string, string>>?, bool> isBot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(isBot);

        _block = block;
        _snapshot = snapshot;
        _isBot = isBot;
    }

    /// <summary>Counts one archive message (spec 4.4, the exact rule in order).</summary>
    public void Count(ChatLogMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 1. Defensive block boundary: the archive bounds the range itself (probe 1), but a cell
        // outside the block would escape this block's delete and coverage.
        var day = DateOnly.FromDateTime(message.SentAtUtc);
        if (day < _block.From || day >= _block.ToExclusive)
        {
            return;
        }

        // 2. Room first, bot only for the own room — identical to ReplayDayCounter and the live path.
        var origin = SharedChatRule.Classify(message.RoomId, message.SourceRoomId, message.HasOtherSourceMarkers);
        var isBot = origin == MessageOrigin.Own && _isBot(message.UserId, message.Badges);
        var category = UsageCategoryRule.Resolve(origin, isBot);

        // 3. One hit per slot and message, gated by the slot's entry day into the chosen set.
        _matched.Clear();
        EmoteNameMatching.MatchEmoteIds(message.Text, _snapshot.NameToSlotId, _matched);
        foreach (var slotId in _matched)
        {
            if (_snapshot.AddedToSetDay.TryGetValue(slotId, out var addedDay) && addedDay is { } entered && day < entered)
            {
                continue;
            }

            var key = (slotId, day);
            var buckets = _cells.GetValueOrDefault(key);
            _cells[key] = category switch
            {
                UsageCategory.Human => buckets with { Human = buckets.Human + 1 },
                UsageCategory.Bot => buckets with { Bot = buckets.Bot + 1 },
                _ => buckets with { SharedChat = buckets.SharedChat + 1 },
            };
        }
    }

    /// <summary>
    /// One aggregate per counted <c>(slot, day)</c> cell with the slot's emote, ordered by slot id
    /// (ordinal) and day so the same input always yields the same list.
    /// </summary>
    public IReadOnlyList<ChatLogBackfillAggregate> Aggregates() =>
        _cells
            .OrderBy(c => c.Key.SlotId, StringComparer.Ordinal)
            .ThenBy(c => c.Key.Day)
            .Select(c => new ChatLogBackfillAggregate(
                c.Key.SlotId, _snapshot.EmoteIdOf(c.Key.SlotId)!, c.Key.Day, c.Value.Human, c.Value.Bot, c.Value.SharedChat))
            .ToList();

    private readonly record struct Buckets(int Human, int Bot, int SharedChat);
}
