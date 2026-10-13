namespace EmotePurge.Core.Entities;

/// <summary>
/// The matching snapshot of a backfill run's chosen 7TV set, taken from 7TV by the Api at request
/// time: what the counter matches and from which day, so nothing that happens to the channel's live
/// emotes after the click changes what the run counts. Goes with its run (cascade).
/// <para>
/// <see cref="EmoteId"/> deliberately has <b>no foreign key</b> to <c>Emotes</c>: an emote hard-deleted
/// after the click must not break or silently shrink the snapshot mid-run; the aggregates of such an
/// emote are dropped at commit instead.
/// </para>
/// </summary>
public class ChatLogBackfillRunEmote
{
    public long RunId { get; set; }

    // Our Emote.Id (an internal Guid string) for (ChannelId, SevenTvEmoteId).
    public string EmoteId { get; set; } = string.Empty;

    // The 7TV ObjectID, for traceability.
    public string SevenTvEmoteId { get; set; } = string.Empty;

    // The emote's alias in the chosen set — what the counter matches; may differ from Emote.Name,
    // which is the active set's alias.
    public string Name { get; set; } = string.Empty;

    // UTC day of the set entry's addedAt from the same 7TV read; null when 7TV reported none = no gate.
    public DateOnly? AddedToSetDay { get; set; }

    // True iff this enqueue created the Emote row (creation provenance for the rollback cleanup). It
    // belongs to the emote, not to the slot: every snapshot row of one emote in a run carries the same
    // value, so a rollback collapse that keeps only one of them loses nothing.
    public bool CreatedRow { get; set; }

    // The slot this snapshot row counts for; with RunId the primary key since M3
    // (SwitchBackfillSnapshotToSlotKey, #375), so a run has one row per slot and may have several per
    // emote. Required. No foreign key, for the same reason as EmoteId.
    public string SlotId { get; set; } = string.Empty;

    // True iff this enqueue created the EmoteSlot (creation provenance for the rollback cleanup).
    public bool CreatedSlot { get; set; }

    public ChatLogBackfillRun Run { get; set; } = null!;
}
