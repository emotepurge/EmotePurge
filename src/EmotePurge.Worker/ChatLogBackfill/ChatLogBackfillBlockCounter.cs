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
/// <see cref="EmoteNameMatching"/> (split on spaces, ordinal, one hit per emote and message). What it
/// adds is the block boundary (a message dated outside the block is dropped) and the entry-day gate of
/// the chosen set (a hit before the emote entered that set is not counted, B9). The excluded-chatter
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
    private readonly IReadOnlyDictionary<string, string> _nameToId;
    private readonly IReadOnlyDictionary<string, DateOnly?> _addedToSetDay;
    private readonly Func<string?, IReadOnlyList<KeyValuePair<string, string>>?, bool> _isBot;
    private readonly Dictionary<(string EmoteId, DateOnly Day), Buckets> _cells = [];
    private readonly HashSet<string> _matched = new(StringComparer.Ordinal);

    /// <param name="block">The days this instance counts.</param>
    /// <param name="nameToId">The coalesced alias → emote id map of the run's snapshot (spec 4.3).</param>
    /// <param name="addedToSetDay">
    /// Per emote id, the day it entered the chosen set; a missing key or <c>null</c> means no gate.
    /// </param>
    /// <param name="isBot">The live bot classifier (<see cref="IBotChatterDetector.IsBot"/>).</param>
    public ChatLogBackfillBlockCounter(
        ChatLogBackfillBlock block,
        IReadOnlyDictionary<string, string> nameToId,
        IReadOnlyDictionary<string, DateOnly?> addedToSetDay,
        Func<string?, IReadOnlyList<KeyValuePair<string, string>>?, bool> isBot)
    {
        ArgumentNullException.ThrowIfNull(nameToId);
        ArgumentNullException.ThrowIfNull(addedToSetDay);
        ArgumentNullException.ThrowIfNull(isBot);

        _block = block;
        _nameToId = nameToId;
        _addedToSetDay = addedToSetDay;
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

        // 3. One hit per emote and message, gated by the emote's entry day into the chosen set.
        _matched.Clear();
        EmoteNameMatching.MatchEmoteIds(message.Text, _nameToId, _matched);
        foreach (var emoteId in _matched)
        {
            if (_addedToSetDay.TryGetValue(emoteId, out var addedDay) && addedDay is { } entered && day < entered)
            {
                continue;
            }

            var key = (emoteId, day);
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
    /// One aggregate per counted <c>(emote, day)</c> cell, ordered by emote id (ordinal) and day so the
    /// same input always yields the same list.
    /// </summary>
    public IReadOnlyList<ChatLogBackfillAggregate> Aggregates() =>
        _cells
            .OrderBy(c => c.Key.EmoteId, StringComparer.Ordinal)
            .ThenBy(c => c.Key.Day)
            .Select(c => new ChatLogBackfillAggregate(c.Key.EmoteId, c.Key.Day, c.Value.Human, c.Value.Bot, c.Value.SharedChat))
            .ToList();

    private readonly record struct Buckets(int Human, int Bot, int SharedChat);
}
