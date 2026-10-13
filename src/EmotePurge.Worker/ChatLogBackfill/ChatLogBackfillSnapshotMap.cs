using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.ChatLogBackfill;

/// <summary>
/// What a backfill run counts against, derived once from its persisted snapshot (spec 4.3, slot plan
/// #375): the coalesced alias → slot map, the entry-day gate per slot and the emote of every slot. Pure
/// — no I/O. One snapshot row per slot, so two aliases of one emote are two slots and count apart.
/// <para>
/// <see cref="Rebind"/> is the worker's half of E9: when a block comes back
/// <see cref="ChatLogBackfillBlockResult.SnapshotChanged"/>, a settle has rebound snapshot rows to other
/// slots. A row keeps its <c>(EmoteId, Name)</c> through that — the settle only changes its slot — so the
/// old row of an aggregate's slot finds the row's new slot in the re-read snapshot.
/// </para>
/// </summary>
public sealed class ChatLogBackfillSnapshotMap
{
    private readonly Dictionary<string, ChatLogBackfillSnapshotEmote> _rowBySlotId;
    private readonly Dictionary<(string EmoteId, string Name), string> _slotIdByRow;
    private readonly HashSet<(string EmoteId, string Name)> _ambiguousRows;

    private ChatLogBackfillSnapshotMap(IReadOnlyList<ChatLogBackfillSnapshotEmote> rows)
    {
        // The snapshot's own order (EmoteId, then SlotId, ordinal) decides which slot an alias that
        // appears twice counts for — the first, as before the slot key (spec 4.3).
        var map = EmoteNameMatching.Coalesce(rows.Select(r => new KeyValuePair<string, string>(r.Name, r.SlotId)));
        NameToSlotId = map.NameToId;
        AmbiguousNames = map.AmbiguousNames;

        _rowBySlotId = new Dictionary<string, ChatLogBackfillSnapshotEmote>(StringComparer.Ordinal);
        _slotIdByRow = [];
        _ambiguousRows = [];
        var addedToSetDay = new Dictionary<string, DateOnly?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            _rowBySlotId.TryAdd(row.SlotId, row);
            addedToSetDay.TryAdd(row.SlotId, row.AddedToSetDay);
            if (!_slotIdByRow.TryAdd((row.EmoteId, row.Name), row.SlotId))
            {
                _ambiguousRows.Add((row.EmoteId, row.Name));
            }
        }

        AddedToSetDay = addedToSetDay;
    }

    /// <summary>Alias → slot id, the first slot per alias (spec 4.3).</summary>
    public IReadOnlyDictionary<string, string> NameToSlotId { get; }

    /// <summary>Aliases that appear on more than one snapshot row.</summary>
    public IReadOnlySet<string> AmbiguousNames { get; }

    /// <summary>Per slot id, the day its entry joined the chosen set; a missing key or <c>null</c> means no gate.</summary>
    public IReadOnlyDictionary<string, DateOnly?> AddedToSetDay { get; }

    public static ChatLogBackfillSnapshotMap From(IReadOnlyList<ChatLogBackfillSnapshotEmote> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new ChatLogBackfillSnapshotMap(rows);
    }

    /// <summary>The emote of a snapshot slot; <c>null</c> for a slot the snapshot does not hold.</summary>
    public string? EmoteIdOf(string slotId) => _rowBySlotId.TryGetValue(slotId, out var row) ? row.EmoteId : null;

    /// <summary>
    /// Moves <paramref name="rows"/>, counted against <paramref name="previous"/>, onto the slots
    /// <paramref name="current"/> holds for the same snapshot rows: each aggregate's slot leads to its old
    /// row, the row's <c>(EmoteId, Name)</c> to the row in <paramref name="current"/>, and that row's slot
    /// replaces the aggregate's — a no-op for a row whose slot did not change. Fails — the run cannot
    /// follow the change — when an aggregate's slot was never a row of <paramref name="previous"/> under
    /// its emote, when its row is gone from <paramref name="current"/> or not unique there, when no row of
    /// the block changed its slot (the server saw a change the snapshot does not show), or when two
    /// aggregates land on one slot and day.
    /// </summary>
    public static ChatLogBackfillRebindResult Rebind(
        IReadOnlyList<ChatLogBackfillAggregate> rows, ChatLogBackfillSnapshotMap previous, ChatLogBackfillSnapshotMap current)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var rebound = new List<ChatLogBackfillAggregate>(rows.Count);
        var moved = 0;
        foreach (var row in rows)
        {
            if (!previous._rowBySlotId.TryGetValue(row.SlotId, out var old) || !string.Equals(old.EmoteId, row.EmoteId, StringComparison.Ordinal))
            {
                return ChatLogBackfillRebindResult.Failed("an aggregate was not counted against a row of the snapshot");
            }

            var key = (old.EmoteId, old.Name);
            if (current._ambiguousRows.Contains(key) || !current._slotIdByRow.TryGetValue(key, out var slotId))
            {
                return ChatLogBackfillRebindResult.Failed("a snapshot row of the block is gone or no longer unique");
            }

            if (!string.Equals(slotId, row.SlotId, StringComparison.Ordinal))
            {
                moved++;
            }

            rebound.Add(row with { SlotId = slotId });
        }

        if (moved == 0)
        {
            return ChatLogBackfillRebindResult.Failed("no snapshot row of the block changed its slot");
        }

        var cells = new HashSet<(string, DateOnly)>();
        if (rebound.Any(r => !cells.Add((r.SlotId, r.Date))))
        {
            return ChatLogBackfillRebindResult.Failed("two aggregates landed on one slot and day");
        }

        return new ChatLogBackfillRebindResult(rebound, moved, null);
    }
}

/// <summary>
/// <see cref="ChatLogBackfillSnapshotMap.Rebind"/>'s answer: the moved aggregates (in their original
/// order) and how many changed their slot, or <see cref="Failure"/> with <see cref="Rows"/> <c>null</c>.
/// </summary>
public sealed record ChatLogBackfillRebindResult(IReadOnlyList<ChatLogBackfillAggregate>? Rows, int Moved, string? Failure)
{
    public static ChatLogBackfillRebindResult Failed(string reason) => new(null, 0, reason);
}
