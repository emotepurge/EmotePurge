namespace EmotePurge.Core.Entities;

/// <summary>
/// One occupancy of a channel's 7TV set by an emote under one alias: the unit usage is counted and
/// shown against, so an emote that is renamed or re-added keeps one row per stay instead of
/// collapsing into <see cref="Emote"/>. The emote stays the owner (rule 8 lives there); a slot only
/// adds the alias and the stay's dates. Goes with its emote (cascade).
/// <para>
/// <see cref="SeedUnsettled"/> marks the slots the migration seeded, one per (emote, emote set) that
/// already had usage rows: their <see cref="Name"/> is only the emote's name at migration time, they
/// carry no 7TV date, and they do not count until a sync has settled them onto a set entry.
/// </para>
/// </summary>
public class EmoteSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EmoteId { get; set; } = string.Empty;

    // The alias as 7TV lists it in the set (ordinal). An attribute, not the identity.
    public string Name { get; set; } = string.Empty;

    // 7TV's added_at for the set entry, stored as delivered; a provisional UtcNow until REST has
    // reported one. Null means "unknown", never "new".
    public DateTime? AddedAt { get; set; }

    // True when AddedAt is the authoritative 7TV date and therefore the slot's identity; false when
    // it is provisional or null.
    public bool AddedAtFromRest { get; set; }

    // True while the slot is not (or no longer) in the active set. A slot created by the chat-log
    // backfill is archived with ArchivedAt = null: "was never in".
    public bool IsArchived { get; set; }

    // Stamped when an active slot is archived.
    public DateTime? ArchivedAt { get; set; }
    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;

    // True for a migration seed until a sync has settled it; the column itself stays after that.
    public bool SeedUnsettled { get; set; }

    public Emote Emote { get; set; } = null!;
}
