namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The pure binding half of the backfill enqueue (plan #375 2.2–2.4, D28): which slot each preview
/// entry of one emote binds to, built from the REST reconcile's own steps so both paths keep one notion
/// of slot identity. No I/O, no clock, no DbContext.
/// <para>
/// <b>Input contract.</b> Per <c>SevenTvEmoteId</c> group <i>G</i> of the previewed set X: the preview
/// entries in v3 order (name + v4 overlay date, <c>null</c> = undated; a duplicate name counts once, the
/// first); <b>all</b> slots of the emote read behind the emote row lock (archived ones and every
/// unsettled seed); and the ids of the unsettled seeds of X (<c>SetSeeds.SeedsOfSet</c>). Whether X is
/// the active set is the caller's choice of method, read under the channel lock.
/// </para>
/// <para>
/// <b>Output contract</b> (<see cref="BackfillSlotBindingPlan"/>). A rejected plan carries only its
/// reason; one rejection rejects the whole enqueue (409 <c>backfill_slots_unsettled</c>) and nothing is
/// written. Otherwise every deduplicated entry has a binding: an existing slot id, or <c>null</c> for a
/// new authoritative slot the caller creates through <see cref="ArchivedSlotUpsert"/> (only ever a dated
/// entry — undated entries without a slot are rejected, operator decisions R4/R7 (a), so the backfill
/// never creates a provisional slot). Two entries never bind one slot. <see cref="BackfillSlotBindingPlan.UpdatedSlots"/>
/// are the final values of existing slots that change; <see cref="BackfillSlotBindingPlan.UsageRebinds"/>
/// move a settled seed's rows <b>of X</b> and its snapshot rows onto the target (<see cref="SeedSettleWrites"/>).
/// </para>
/// <para>
/// <b>Identity, not membership (E2).</b> On an existing slot the plan changes only <c>Name</c>,
/// <c>AddedAt</c> and <c>AddedAtFromRest</c>; its <c>IsArchived</c>/<c>ArchivedAt</c> stay with the REST
/// reconcile, which binds the slots by their exact date on its next tick. The one exception is a seed of
/// X that this plan settles (marker and archive state, E4/R2). There is no step 7 and no D13 derivation.
/// </para>
/// </summary>
internal static partial class EmoteSlotReconcilePlanner
{
    /// <summary>
    /// Plan 2.2, the preview of the active set: steps 3 (date identity, also a rename, AK 27), 4 (D7: the
    /// archived candidate is only re-keyed onto the entry's date, AK 53) and 5 (D11 name binding: a dated
    /// entry authorizes a provisional slot's date — the push and restore slots; an undated entry may also
    /// bind an active authoritative slot) exactly as REST runs them; every dated entry still unbound is a
    /// new slot. Rejected when the emote has an unsettled seed of the active set (2.4 a — settling it is
    /// REST's) or an undated entry finds no slot by name (2.4 c).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="previewEntries"/> is empty.</exception>
    public static BackfillSlotBindingPlan PlanActivePreviewGroup(
        IReadOnlyList<SlotLiveEntry> previewEntries,
        IReadOnlyList<SlotState> slots,
        IReadOnlySet<string> activeSetSeedIds)
    {
        var entries = DistinctPreviewEntries(previewEntries);
        if (slots.Any(s => s.SeedUnsettled && activeSetSeedIds.Contains(s.Id)))
        {
            return BackfillSlotBindingPlan.Rejected(BackfillSlotBindingRejection.ActiveSetSeedUnsettled);
        }

        var own = slots.Where(s => !s.SeedUnsettled).Select(s => new WorkingSlot(s)).ToList();
        var existing = own.ToList();
        var bindings = new WorkingSlot?[entries.Count];

        BindByDate(entries, own, bindings);
        ResumeArchivedSlots(entries, own, bindings);
        for (var i = 0; i < entries.Count; i++)
        {
            bindings[i] ??= BindByName(entries[i], own, bindings);
        }

        if (Enumerable.Range(0, entries.Count).Any(i => bindings[i] is null && entries[i].AddedAt is null))
        {
            return BackfillSlotBindingPlan.Rejected(BackfillSlotBindingRejection.UndatedEntryWithoutBinding);
        }

        return BoundPreviewPlan(entries, existing, bindings, []);
    }

    /// <summary>
    /// Plan 2.3, the preview of a set X that is not the active one. Every entry must be dated (2.4 b/b',
    /// R7 (a)): an undated entry would need a provisional slot REST never finds again. Entries bind by
    /// exact authoritative date only (step 3, active or archived; no D7, no name binding — D28). A seed of
    /// X is settled with the date-only target selection (Sol-P2-2: never onto a same-named provisional
    /// slot of the active set): target exists → X's rows and the snapshot rows move onto it and the seed
    /// ends empty, archived without date (E12); otherwise the seed is re-keyed in place onto the oldest
    /// entry and stays archived, keeping its archive date or getting <paramref name="now"/> (R2). Every
    /// other entry still unbound is a new slot.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="previewEntries"/> is empty.</exception>
    public static BackfillSlotBindingPlan PlanInactivePreviewGroup(
        IReadOnlyList<SlotLiveEntry> previewEntries,
        IReadOnlyList<SlotState> slots,
        IReadOnlySet<string> previewSetSeedIds,
        DateTime now)
    {
        var entries = DistinctPreviewEntries(previewEntries);
        if (entries.Any(e => e.AddedAt is null))
        {
            return BackfillSlotBindingPlan.Rejected(BackfillSlotBindingRejection.UndatedEntryOutsideActiveSet);
        }

        var own = slots.Where(s => !s.SeedUnsettled).Select(s => new WorkingSlot(s)).ToList();
        var previewSetSeeds = slots
            .Where(s => s.SeedUnsettled && previewSetSeedIds.Contains(s.Id))
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => new WorkingSlot(s))
            .ToList();
        var existing = own.Concat(previewSetSeeds).ToList();
        var bindings = new WorkingSlot?[entries.Count];
        var rebinds = new List<SlotUsageRebind>();

        BindByDate(entries, own, bindings);
        if (previewSetSeeds.Count > 0)
        {
            var (candidate, target) = SelectSettleTarget(entries, own, bindings, SettleTargetSearch.DateOnly);
            SettleOnto(entries, candidate, target, own, previewSetSeeds, bindings, rebinds, SettledSeedMembership.OutsideActiveSet, now);
        }

        return BoundPreviewPlan(entries, existing, bindings, rebinds);
    }

    private static List<SlotLiveEntry> DistinctPreviewEntries(IReadOnlyList<SlotLiveEntry> previewEntries)
    {
        // Plan 2.1 [P]: no D36 dedup any more; only "a duplicate (Id, Name) pair is one entry, the first counts".
        var entries = previewEntries.DistinctBy(e => e.Name, StringComparer.Ordinal).ToList();
        if (entries.Count == 0)
        {
            throw new ArgumentException("A preview group needs at least one entry.", nameof(previewEntries));
        }

        return entries;
    }

    /// <summary>
    /// E2: the steps above ran on the REST working state, where a bound slot is in the set. Only a seed
    /// this plan settled keeps the archive state the settle gave it; every other existing slot goes back
    /// to its stored <c>IsArchived</c>/<c>ArchivedAt</c> before the changes are collected.
    /// </summary>
    private static BackfillSlotBindingPlan BoundPreviewPlan(
        List<SlotLiveEntry> entries, List<WorkingSlot> existing, WorkingSlot?[] bindings, List<SlotUsageRebind> rebinds)
    {
        foreach (var slot in existing.Where(s => !s.Original.SeedUnsettled))
        {
            slot.IsArchived = slot.Original.IsArchived;
            slot.ArchivedAt = slot.Original.ArchivedAt;
        }

        return new BackfillSlotBindingPlan(
            Rejection: null,
            Bindings: entries.Select((e, i) => new BackfillEntryBinding(e.Name, e.AddedAt, bindings[i]?.Id)).ToList(),
            UpdatedSlots: existing
                .Where(s => s.IsModified)
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => s.ToState())
                .ToList(),
            UsageRebinds: rebinds);
    }
}

/// <summary>Why a backfill preview group cannot be bound yet (plan #375 2.4, all answered with 409 <c>backfill_slots_unsettled</c>).</summary>
internal enum BackfillSlotBindingRejection
{
    /// <summary>(a) Active preview: the emote has an unsettled seed of the active set; the next REST tick settles it.</summary>
    ActiveSetSeedUnsettled,

    /// <summary>(c) Active preview: an undated entry binds to no active slot of its name (R4, [A] to D11 d).</summary>
    UndatedEntryWithoutBinding,

    /// <summary>(b, b') Preview of another set: an entry is undated (R7 (a)); covers a seed of that set with an undated entry.</summary>
    UndatedEntryOutsideActiveSet,
}

/// <summary>
/// One preview entry and its slot: <see cref="SlotId"/> is an existing slot, or <c>null</c> for a new
/// authoritative archived slot keyed <c>(EmoteId, AddedAt, Name)</c> — then <see cref="AddedAt"/> is set.
/// </summary>
internal sealed record BackfillEntryBinding(string Name, DateTime? AddedAt, string? SlotId);

/// <summary>What <see cref="EmoteSlotReconcilePlanner"/> decided for one backfill preview group; see the backfill planner's summary.</summary>
internal sealed record BackfillSlotBindingPlan(
    BackfillSlotBindingRejection? Rejection,
    IReadOnlyList<BackfillEntryBinding> Bindings,
    IReadOnlyList<SlotState> UpdatedSlots,
    IReadOnlyList<SlotUsageRebind> UsageRebinds)
{
    public static BackfillSlotBindingPlan Rejected(BackfillSlotBindingRejection reason) => new(reason, [], [], []);
}
