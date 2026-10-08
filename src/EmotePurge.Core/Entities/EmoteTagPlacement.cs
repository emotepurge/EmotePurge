namespace EmotePurge.Core.Entities;

/// <summary>
/// "This tag brought this emote into this set" — as far as the browser reported it. Keyed by
/// <c>(TagId, SevenTvEmoteId, SevenTvEmoteSetId)</c>.
/// <para>
/// The composite foreign key <c>(TagId, SevenTvEmoteId)</c> to <see cref="EmoteTagEntry"/> (cascade) is
/// deliberate: the database itself then upholds "no placement without an entry" and "taking an emote
/// out of a tag takes its placements in every set with it", regardless of the locking discipline of
/// whichever caller writes. There is deliberately no foreign key to <see cref="EmoteTagOperation"/>:
/// it would force a delete order the sweep does not need.
/// </para>
/// <para>
/// <see cref="OperationId"/> is the placement's provenance and revision — the operation that last
/// created or transferred it, which a removal snapshot matches against. Whether the placement still holds is decided at read time by comparing
/// <see cref="EmoteSetLeaveObservation"/> against <see cref="RegisteredAtUtc"/>, never by deleting rows
/// in the sync.
/// </para>
/// </summary>
public class EmoteTagPlacement
{
    public long TagId { get; set; }

    /// <summary>The 7TV emote id (a 26-character ULID in practice; the column allows 32).</summary>
    public string SevenTvEmoteId { get; set; } = string.Empty;

    /// <summary>The 7TV emote set id (a 26-character ULID in practice; the column allows 32).</summary>
    public string SevenTvEmoteSetId { get; set; } = string.Empty;

    /// <summary>When the play-in report that last added the emote was applied. Survives a transfer.</summary>
    public DateTime PlacedAtUtc { get; set; }

    /// <summary>
    /// The operation that last created or transferred this placement (provenance and snapshot revision;
    /// not the validity anchor).
    /// </summary>
    public Guid OperationId { get; set; }

    /// <summary>
    /// The registration time of the operation that last wrote this placement — the play-in operation,
    /// or on a transfer the removal operation of the tag it was taken over from. This is the validity
    /// anchor: a leave observation strictly later than it means the placement no longer holds.
    /// </summary>
    /// <remarks>
    /// Deliberately a copy on the placement rather than a lookup through <see cref="OperationId"/>.
    /// Operations cascade with their tag, while a transfer points another tag's placement at the
    /// removal operation of the tag it came from; deleting that tag afterwards would otherwise leave
    /// the transferred placement without an anchor and silently invalidate it.
    /// </remarks>
    public DateTime RegisteredAtUtc { get; set; }

    public EmoteTag Tag { get; set; } = null!;
}
