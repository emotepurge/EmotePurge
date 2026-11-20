namespace EmotePurge.Core.Entities;

/// <summary>
/// A released automatic link (K6 <c>unlink</c>): the reconcile never links this wish to this slot again.
/// Deliberately without a foreign key to the slot, so the exclusion outlives an archived or deleted slot.
/// </summary>
public class WishSlotExclusion
{
    public long WishId { get; set; }
    public Wish? Wish { get; set; }

    public string SlotId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}
