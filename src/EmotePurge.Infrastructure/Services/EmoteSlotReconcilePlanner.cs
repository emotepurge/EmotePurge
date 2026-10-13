using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The pure decision half of the slot-aware REST reconcile (spec §6.2 steps 2–8, §6.3; plan #372 E10).
/// <see cref="SevenTvSyncService"/> takes the snapshot behind its row locks, asks this class for a
/// <see cref="SlotReconcilePlan"/> per emote and applies it to its tracked entities. No I/O, no clock, no
/// DbContext — everything the rules read is a parameter.
/// <para>
/// <b>Input contract.</b> Per live <c>SevenTvEmoteId</c> group <i>G</i>: the group's live entries in v3
/// order (name + the v4 overlay date for <c>(Id, Name)</c>, <c>null</c> = undated, D11 e); <b>all</b>
/// slots of the emote (own slots and every unsettled seed, read after the lock); the ids of the
/// unsettled seeds whose usage rows belong to the active set (one <c>UsageStats</c> query, plan 2.2
/// step 6 — never derived from <c>IsArchived</c>, #371 handover); the emote's name and whether it was
/// active <b>before</b> step 1 of this pass (only for <see cref="SlotReconcilePlan.InventoryChanged"/>);
/// and <c>now</c> for archive stamps.
/// </para>
/// <para>
/// <b>Output contract.</b> <see cref="SlotReconcilePlan.UpdatedSlots"/> are the final column values of
/// existing slots that actually change (apply with <see cref="SlotState.ApplyTo"/>, which also stamps
/// <c>LastSyncedAt</c>); <see cref="SlotReconcilePlan.CreatedSlots"/> are new slots with their ids
/// (<see cref="SlotState.ToNewEntity"/>); <see cref="SlotReconcilePlan.UsageRebinds"/> move a settled
/// seed's active-set usage rows onto another slot (only rows with <c>EmoteSetId</c> = active set);
/// <see cref="SlotReconcilePlan.Derivation"/> is the D13 value for <c>Emote.Name</c>/<c>FirstSeenAt</c>
/// (<c>null</c> = leave both as they are). The emote row itself (step 1: create, un-archive, image,
/// <c>LastEnteredSetAtUtc</c>, <c>IsPlaceholder</c>, leave detection) stays with the service; with the
/// flag on, step 1 must not rename the emote — the name is derived here.
/// </para>
/// <para>
/// Name equality is ordinal and case-sensitive (U14); dates compare by ticks. A name tie in an "oldest alias"
/// ordering (D13, the settle candidate) is broken in UTF-8 byte order (<see cref="BaseSlotRule.Utf8Order"/>,
/// the order of D2; #403), which differs from UTF-16 ordinal for supplementary characters.
/// </para>
/// </summary>
internal static class EmoteSlotReconcilePlanner
{
    /// <summary>
    /// Plans one live group (spec §6.2 steps 2–8). Steps in this order: dedup (step 1, "first (Id, Name)
    /// wins"), deferral check (§6.3), date identity (step 3), settle (step 2, needs step 3's result for
    /// its target search, E11), resumption D7 (step 4), name binding (step 5), new slots (step 6),
    /// archive (step 7), derivation (step 8).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="liveEntries"/> is empty — an emote without live
    /// entry goes through <see cref="PlanAbsentEmote"/>.</exception>
    public static SlotReconcilePlan PlanLiveGroup(
        IReadOnlyList<SlotLiveEntry> liveEntries,
        IReadOnlyList<SlotState> slots,
        IReadOnlySet<string> activeSetSeedIds,
        string emoteName,
        bool emoteWasActive,
        DateTime now)
    {
        // Plan 2.3 step 1: a duplicate (Id, Name) pair is one entry; the first one counts.
        var entries = liveEntries.DistinctBy(e => e.Name, StringComparer.Ordinal).ToList();
        if (entries.Count == 0)
        {
            throw new ArgumentException("A live group needs at least one entry.", nameof(liveEntries));
        }

        // E7: own slots = everything but unsettled seeds. Seeds of other sets ("foreign") are not even
        // copied into the working set: nothing below can reach them (§6.2 step 2, §6.3 last paragraph,
        // #371 handover: no ArchivedAt stamp from step 7, never a D7 candidate, never shown).
        var own = slots.Where(s => !s.SeedUnsettled).Select(s => new WorkingSlot(s)).ToList();
        var activeSetSeeds = slots
            .Where(s => s.SeedUnsettled && activeSetSeedIds.Contains(s.Id))
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => new WorkingSlot(s))
            .ToList();

        // §6.3 / D12 "wait instead of guess": with a seed of the active set, a multi-entry group needs a
        // date on every entry to know which entry is the oldest. Deferred = only step 1 applies: no slot
        // is created, archived, re-keyed or renamed and the emote keeps its name and date (plan 2.3 step
        // 2, 2.4). Without such a seed a missing date is plain D11 (e) below.
        if (activeSetSeeds.Count > 0 && entries.Count >= 2 && entries.Any(e => e.AddedAt is null))
        {
            return SlotReconcilePlan.Deferred;
        }

        var virtualSlotVisible = emoteWasActive && !own.Any(s => !s.Original.IsArchived);
        var existing = own.Concat(activeSetSeeds).ToList();
        var bindings = new WorkingSlot?[entries.Count];

        BindByDate(entries, own, bindings);

        var rebinds = new List<SlotUsageRebind>();
        if (activeSetSeeds.Count > 0)
        {
            Settle(entries, own, activeSetSeeds, bindings, rebinds);
        }

        ResumeArchivedSlots(entries, own, bindings);

        for (var i = 0; i < entries.Count; i++)
        {
            bindings[i] ??= BindByName(entries[i], own, bindings);
        }

        // Step 6 (§6.2): every entry still unexplained is a new slot. Dated → authoritative; undated →
        // provisional with AddedAt = null (never "now", never "new").
        var created = new List<WorkingSlot>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (bindings[i] is null)
            {
                var entry = entries[i];
                var slot = WorkingSlot.New(entry.Name, entry.AddedAt);
                created.Add(slot);
                bindings[i] = slot;
            }
        }

        // Step 7 (§6.2): every active own slot no entry explained is gone from the set. Unsettled seeds
        // are not in `own` (E7), so a foreign seed copied active by M1 keeps IsArchived = false and gets
        // no ArchivedAt; a seed settled in place is bound.
        var boundSlots = bindings.ToHashSet();
        foreach (var slot in own.Where(s => !s.IsArchived && !boundSlots.Contains(s)))
        {
            slot.IsArchived = true;
            slot.ArchivedAt = now;
        }

        var derivation = Derive(own.Concat(created).Select(s => s.ToState()));

        var changed = InventoryChanged(existing.Concat(created), emoteName, emoteWasActive, virtualSlotVisible)
            || (derivation is not null && !string.Equals(derivation.Name, emoteName, StringComparison.Ordinal));

        return new SlotReconcilePlan(
            SettleDeferred: false,
            UpdatedSlots: existing
                .Where(s => s.IsModified)
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => s.ToState())
                .ToList(),
            CreatedSlots: created.ConvertAll(s => s.ToState()),
            UsageRebinds: rebinds,
            Derivation: derivation,
            InventoryChanged: changed);
    }

    /// <summary>
    /// Plans an emote of the channel without any live entry (spec §6.2 "after all groups"): every active
    /// own slot is archived with <paramref name="now"/>, also under an emote that is already archived
    /// (delta pull before this tick). Unsettled seeds of any set stay untouched (plan 2.4: a seed of the
    /// active set without live entry waits for its emote's return; E7 for the others). The emote keeps its
    /// last name and date (D13), and the result never reports an inventory change: archiving slots under
    /// an archived emote is invisible (E18), and the emote's own archive is the service's change.
    /// </summary>
    public static SlotReconcilePlan PlanAbsentEmote(IReadOnlyList<SlotState> slots, DateTime now)
    {
        var updated = slots
            .Where(s => !s.SeedUnsettled && !s.IsArchived)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s with { IsArchived = true, ArchivedAt = now })
            .ToList();
        return new SlotReconcilePlan(false, updated, [], [], null, false);
    }

    /// <summary>
    /// Step 3 (§6.2, D4, D16, §6.5): dated entries bind to own <b>authoritative</b> slots of the same
    /// date, active or archived. Per date <i>t</i>: exactly one live entry and exactly one slot with
    /// <i>t</i> are the same set entry, whatever the names (a rename, §6.5); otherwise — a timestamp tie
    /// on either side — only equally named pairs bind (D4 tie rule, AK 8), so a rename inside a tie
    /// group reads as remove + add. Runs once over all entries before the settle (the tie count must see
    /// the whole live list, not what the settle leaves over).
    /// </summary>
    private static void BindByDate(List<SlotLiveEntry> entries, List<WorkingSlot> own, WorkingSlot?[] bindings)
    {
        var datedGroups = Enumerable.Range(0, entries.Count)
            .Where(i => entries[i].AddedAt is not null)
            .GroupBy(i => entries[i].AddedAt!.Value);

        foreach (var group in datedGroups)
        {
            var indexes = group.ToList();
            var slotsAtDate = own
                .Where(s => s.AddedAtFromRest && s.AddedAt == group.Key)
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .ToList();

            if (indexes.Count == 1 && slotsAtDate.Count == 1)
            {
                bindings[indexes[0]] = BindByDateTo(slotsAtDate[0], entries[indexes[0]]);
                continue;
            }

            foreach (var i in indexes)
            {
                var match = slotsAtDate.FirstOrDefault(s =>
                    string.Equals(s.Name, entries[i].Name, StringComparison.Ordinal) && !bindings.Contains(s));
                if (match is not null)
                {
                    bindings[i] = BindByDateTo(match, entries[i]);
                }
            }
        }
    }

    /// <summary>
    /// Step 2 (§6.3, D12, plan 2.4 E11–E13): settles the unsettled seed(s) of the active set. The target
    /// candidate is the only entry (|G| = 1) or the oldest one (|G| ≥ 2, all dated — the deferral case
    /// has returned before; tie → smallest name in UTF-8 byte order, as D2).
    /// <para>
    /// <b>Target exists</b> (E11): the candidate binds to an own slot by step 3 (already computed) or by
    /// step 5 → the seed's active-set rows move onto that slot and the seed becomes an empty settled
    /// slot: marker off, <c>IsArchived = true</c>, <c>ArchivedAt = null</c>, name and date kept (E12: never
    /// an own entry, never a D7 candidate). Covers the #371 edge case "seed next to a REST slot".
    /// </para>
    /// <para>
    /// <b>Otherwise</b> the first seed (Id ordinal) is re-keyed in place onto the candidate (name, date,
    /// authoritative iff dated), marker off, active; it is the candidate's slot from here on. Every
    /// further seed of the active set then finds that slot as its existing target (plan 2.4).
    /// </para>
    /// <para>
    /// The target search deliberately looks at steps 3 and 5 only, not at D7 (plan E11 names exactly
    /// these, spec D12 speaks of "the same authoritative key"). An archived own slot the candidate could
    /// resume therefore stays archived, and the seed's history continues under the candidate — the
    /// conservative reading, since both histories are kept either way. The candidate's step-5 binding is
    /// committed here already; that is equivalent to binding it in step 5, because step 4 only claims
    /// archived slots and step 5 binds by name, and names are distinct within <i>G</i>.
    /// </para>
    /// </summary>
    private static void Settle(
        List<SlotLiveEntry> entries,
        List<WorkingSlot> own,
        List<WorkingSlot> activeSetSeeds,
        WorkingSlot?[] bindings,
        List<SlotUsageRebind> rebinds)
    {
        var candidate = entries.Count == 1
            ? 0
            : Enumerable.Range(0, entries.Count)
                .OrderBy(i => entries[i].AddedAt!.Value)
                .ThenBy(i => entries[i].Name, BaseSlotRule.Utf8Order)
                .First();

        var target = bindings[candidate] ?? BindByName(entries[candidate], own, bindings);
        var remaining = activeSetSeeds;
        if (target is null)
        {
            var seed = activeSetSeeds[0];
            var entry = entries[candidate];
            seed.Name = entry.Name;
            seed.AddedAt = entry.AddedAt;
            seed.AddedAtFromRest = entry.AddedAt is not null;
            seed.IsArchived = false;
            seed.ArchivedAt = null;
            seed.SeedUnsettled = false;
            // From here on an own slot like any other (working state of steps 4–8).
            own.Add(seed);
            target = seed;
            remaining = activeSetSeeds.Skip(1).ToList();
        }

        bindings[candidate] = target;
        foreach (var seed in remaining)
        {
            rebinds.Add(new SlotUsageRebind(seed.Id, target.Id));
            seed.SeedUnsettled = false;
            seed.IsArchived = true;
            seed.ArchivedAt = null;
        }
    }

    /// <summary>
    /// Step 4, resumption (D7, AK 47–50): after every date match, a still unbound <b>dated</b> entry takes
    /// over the most recently archived own slot of the same name — but only when no active own slot in
    /// the working state carries that name (slots bound in step 3 under their new names, active slots
    /// not explained yet, a seed settled in place). Candidates need <c>ArchivedAt != null</c> (really
    /// archived; excludes backfill slots "never in" and emptied seeds, E12) and are never unsettled seeds
    /// (E8, they are not in <paramref name="own"/>). Choice: <c>ArchivedAt</c> descending, then <c>Id</c>
    /// ordinal ascending. The slot keeps its id and history and is re-keyed onto the entry's date.
    /// Undated entries never resume (plan §3: a return during an overlay outage is a new provisional slot).
    /// </summary>
    private static void ResumeArchivedSlots(List<SlotLiveEntry> entries, List<WorkingSlot> own, WorkingSlot?[] bindings)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (bindings[i] is not null || entry.AddedAt is null)
            {
                continue;
            }

            if (own.Any(s => !s.IsArchived && string.Equals(s.Name, entry.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            var resumed = own
                .Where(s => s.ArchivedAt is not null
                    && string.Equals(s.Name, entry.Name, StringComparison.Ordinal)
                    && !bindings.Contains(s))
                .OrderByDescending(s => s.ArchivedAt)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (resumed is null)
            {
                continue;
            }

            resumed.AddedAt = entry.AddedAt;
            resumed.AddedAtFromRest = true;
            resumed.IsArchived = false;
            resumed.ArchivedAt = null;
            bindings[i] = resumed;
        }
    }

    /// <summary>
    /// Step 5, name binding (D11): an entry binds to an unbound active <b>provisional</b> own slot of the
    /// same name; a dated entry authorizes that slot's date (D14). An undated entry may also bind to an
    /// active authoritative own slot of the same name and never changes a slot's date — so an overlay
    /// outage without rename keeps every slot, date and the derived <c>FirstSeenAt</c> (plan 2.3 step 8).
    /// Two active own slots of one name should not exist (7TV keeps aliases unique per set); if they do,
    /// the choice is deterministic: provisional first, then <c>AddedAt</c> ascending (null last), then
    /// <c>Id</c> ordinal. Returns the bound slot or <c>null</c>.
    /// </summary>
    private static WorkingSlot? BindByName(SlotLiveEntry entry, List<WorkingSlot> own, WorkingSlot?[] bindings)
    {
        var slot = own
            .Where(s => !s.IsArchived
                && string.Equals(s.Name, entry.Name, StringComparison.Ordinal)
                && (!s.AddedAtFromRest || entry.AddedAt is null)
                && !bindings.Contains(s))
            .OrderBy(s => s.AddedAtFromRest)
            .ThenBy(s => s.AddedAt is null)
            .ThenBy(s => s.AddedAt)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (slot is not null && entry.AddedAt is not null)
        {
            slot.AddedAt = entry.AddedAt;
            slot.AddedAtFromRest = true;
        }

        return slot;
    }

    private static WorkingSlot BindByDateTo(WorkingSlot slot, SlotLiveEntry entry)
    {
        slot.Name = entry.Name;
        slot.IsArchived = false;
        slot.ArchivedAt = null;
        return slot;
    }

    /// <summary>
    /// Step 8, D13 strictly (plan 2.3 step 8, replaces E9): the active own slot with the smallest
    /// <c>AddedAt</c> (null last, then name in UTF-8 byte order like D2, then id ordinal) gives <c>Emote.Name</c> and
    /// <c>Emote.FirstSeenAt</c> — the latter also when it is <c>null</c>: a replacement never inherits
    /// the departed slot's date. Without an active own slot nothing is derived.
    /// </summary>
    internal static EmoteNameDerivation? Derive(IEnumerable<SlotState> resultingOwn)
    {
        var first = resultingOwn
            .Where(s => !s.IsArchived)
            .OrderBy(s => s.AddedAt is null)
            .ThenBy(s => s.AddedAt)
            .ThenBy(s => s.Name, BaseSlotRule.Utf8Order)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return first is null ? null : new EmoteNameDerivation(first.Name, first.AddedAt);
    }

    /// <summary>
    /// E18: <c>changed</c> measures the visible inventory (what the slot readers of plan 2.5 show). A
    /// slot is visible when it is an own slot (no unsettled seed) and not archived. Counted: a slot
    /// becoming visible (created, un-archived, resumed, seed settled in place), a rename of a slot that
    /// stays visible, and a visible slot being archived — the last only under an emote that was active,
    /// since slots under an archived emote are not shown. Not counted: date authorization, marker
    /// removal, usage rebinding, an emptied seed. And not the one slot that exactly replaces the virtual
    /// slot (E14: an active emote without an active own slot shows one slot under <c>Emote.Name</c>) —
    /// generalized from "created" to any slot becoming visible under that name, so the first pass after
    /// switching the flag on (also after on → off → on) does not fire <c>channel.synced</c> per channel.
    /// The derived name change is added by the caller of this helper.
    /// </summary>
    private static bool InventoryChanged(
        IEnumerable<WorkingSlot> slots, string emoteName, bool emoteWasActive, bool virtualSlotVisible)
    {
        var virtualSlotAvailable = virtualSlotVisible;
        foreach (var slot in slots)
        {
            var wasVisible = slot.Original is { SeedUnsettled: false, IsArchived: false };
            var isVisible = !slot.SeedUnsettled && !slot.IsArchived;

            if (!wasVisible && isVisible)
            {
                if (virtualSlotAvailable && string.Equals(slot.Name, emoteName, StringComparison.Ordinal))
                {
                    virtualSlotAvailable = false;
                    continue;
                }

                return true;
            }

            if (wasVisible && isVisible && !string.Equals(slot.Name, slot.Original.Name, StringComparison.Ordinal))
            {
                return true;
            }

            if (wasVisible && !isVisible && emoteWasActive)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Mutable working copy of one slot while the steps run.</summary>
    private sealed class WorkingSlot
    {
        public WorkingSlot(SlotState original)
        {
            Original = original;
            Id = original.Id;
            Name = original.Name;
            AddedAt = original.AddedAt;
            AddedAtFromRest = original.AddedAtFromRest;
            IsArchived = original.IsArchived;
            ArchivedAt = original.ArchivedAt;
            SeedUnsettled = original.SeedUnsettled;
        }

        public SlotState Original { get; }
        public string Id { get; }
        public string Name { get; set; }
        public DateTime? AddedAt { get; set; }
        public bool AddedAtFromRest { get; set; }
        public bool IsArchived { get; set; }
        public DateTime? ArchivedAt { get; set; }
        public bool SeedUnsettled { get; set; }

        // Ticks comparison on the dates (DateTime ==), so a Kind difference alone is no change.
        public bool IsModified => ToState() != Original;

        public static WorkingSlot New(string name, DateTime? addedAt)
        {
            // A "virtual original" that is archived and not a seed: never visible before, so a new slot
            // always counts as becoming visible.
            var original = new SlotState(Guid.NewGuid().ToString(), name, addedAt, addedAt is not null, true, null, false);
            return new WorkingSlot(original) { IsArchived = false };
        }

        public SlotState ToState() => new(Id, Name, AddedAt, AddedAtFromRest, IsArchived, ArchivedAt, SeedUnsettled);
    }
}

/// <summary>One live set entry of an emote: alias and the v4 overlay date for <c>(Id, Name)</c>, <c>null</c> = undated.</summary>
internal readonly record struct SlotLiveEntry(string Name, DateTime? AddedAt);

/// <summary>
/// The columns of an <see cref="EmoteSlot"/> the reconcile decides on, as an immutable value: the input
/// snapshot (<see cref="From"/>) and the planned final state (<see cref="ApplyTo"/>,
/// <see cref="ToNewEntity"/>).
/// </summary>
internal sealed record SlotState(
    string Id,
    string Name,
    DateTime? AddedAt,
    bool AddedAtFromRest,
    bool IsArchived,
    DateTime? ArchivedAt,
    bool SeedUnsettled)
{
    public static SlotState From(EmoteSlot slot) =>
        new(slot.Id, slot.Name, slot.AddedAt, slot.AddedAtFromRest, slot.IsArchived, slot.ArchivedAt, slot.SeedUnsettled);

    /// <summary>
    /// Writes the planned values onto the tracked slot of the same id and stamps <c>LastSyncedAt</c>
    /// (spec §4.1: stamped on creation, rename, archive and date authorization — a planned update is
    /// always one of these, so an unchanged slot is never in the plan and keeps its stamp).
    /// </summary>
    public void ApplyTo(EmoteSlot slot, DateTime now)
    {
        if (!string.Equals(slot.Id, Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Planned state for slot {Id} applied to slot {slot.Id}.");
        }

        slot.Name = Name;
        slot.AddedAt = AddedAt;
        slot.AddedAtFromRest = AddedAtFromRest;
        slot.IsArchived = IsArchived;
        slot.ArchivedAt = ArchivedAt;
        slot.SeedUnsettled = SeedUnsettled;
        slot.LastSyncedAt = now;
    }

    public EmoteSlot ToNewEntity(string emoteId, DateTime now)
    {
        var slot = new EmoteSlot { Id = Id, EmoteId = emoteId };
        ApplyTo(slot, now);
        return slot;
    }
}

/// <summary>Move the usage rows of the active set from a settled seed onto <paramref name="ToSlotId"/> (§6.3 "target exists").</summary>
internal sealed record SlotUsageRebind(string FromSeedId, string ToSlotId);

/// <summary>D13: the values for <c>Emote.Name</c> and <c>Emote.FirstSeenAt</c>; <c>FirstSeenAt</c> may be <c>null</c>.</summary>
internal sealed record EmoteNameDerivation(string Name, DateTime? FirstSeenAt);

/// <summary>
/// What <see cref="EmoteSlotReconcilePlanner"/> decided for one emote; see the planner's summary for how
/// the service applies it. <see cref="SettleDeferred"/> = the group waits for v4 dates (§6.3) and every
/// other member is empty; the service counts these for the per-channel "settle deferred" line.
/// <see cref="InventoryChanged"/> covers the slot inventory and the derived name (E18); emote-level
/// changes of step 1 (created, un-archived, archived, image) are the service's to add.
/// </summary>
internal sealed record SlotReconcilePlan(
    bool SettleDeferred,
    IReadOnlyList<SlotState> UpdatedSlots,
    IReadOnlyList<SlotState> CreatedSlots,
    IReadOnlyList<SlotUsageRebind> UsageRebinds,
    EmoteNameDerivation? Derivation,
    bool InventoryChanged)
{
    public static SlotReconcilePlan Deferred { get; } = new(true, [], [], [], null, false);
}
