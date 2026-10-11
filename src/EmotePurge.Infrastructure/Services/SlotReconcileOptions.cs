namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Bound from configuration section <c>SevenTv:SlotReconcile</c>; a plain POCO registered as a singleton,
/// the same shape as <see cref="EmoteTagOptions"/>: read once at startup, so a changed value takes effect
/// on the next restart (no hot reload). The single read point for the flag of the slot-aware reconcile
/// (emote-slots spec, child 2, plan 372 section 2.0).
/// </summary>
public sealed class SlotReconcileOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "SevenTv:SlotReconcile";

    /// <summary>
    /// Whether the REST reconcile, the v4 overlay and the slot readers work per slot. Default <c>false</c>:
    /// everything behaves exactly as before the slot work (emote-wide reconcile, overlay keyed by emote id,
    /// no writes to <c>EmoteSlots</c>). Read by api and worker; set both together and restart. Switched on
    /// in production only by the last child of epic #365.
    /// </summary>
    public bool Enabled { get; set; }
}
