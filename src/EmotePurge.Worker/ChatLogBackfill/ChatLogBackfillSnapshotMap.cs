using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.ChatLogBackfill;

/// <summary>
/// What a backfill run counts against, derived once from its persisted snapshot (spec 4.3, slot plan
/// #375): the coalesced alias → slot map, the entry-day gate per slot and the emote of every slot. Pure
/// — no I/O. One snapshot row per slot, so two aliases of one emote are two slots and count apart.
/// </summary>
public sealed class ChatLogBackfillSnapshotMap
{
    private readonly Dictionary<string, ChatLogBackfillSnapshotEmote> _rowBySlotId;

    private ChatLogBackfillSnapshotMap(IReadOnlyList<ChatLogBackfillSnapshotEmote> rows)
    {
        // The snapshot's own order (EmoteId, then SlotId, ordinal) decides which slot an alias that
        // appears twice counts for — the first, as before the slot key (spec 4.3).
        var map = EmoteNameMatching.Coalesce(rows.Select(r => new KeyValuePair<string, string>(r.Name, r.SlotId)));
        NameToSlotId = map.NameToId;
        AmbiguousNames = map.AmbiguousNames;

        _rowBySlotId = new Dictionary<string, ChatLogBackfillSnapshotEmote>(StringComparer.Ordinal);
        var addedToSetDay = new Dictionary<string, DateOnly?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            _rowBySlotId.TryAdd(row.SlotId, row);
            addedToSetDay.TryAdd(row.SlotId, row.AddedToSetDay);
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
}
