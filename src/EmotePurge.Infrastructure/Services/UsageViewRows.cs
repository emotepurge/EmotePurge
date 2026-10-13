namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The row formation of the slot-aware usage readers (emote-slots plan #376, sections 2.1–2.3): which
/// rows <c>/totals</c> and <c>/series</c> show for one channel and one requested set X, in which mode, under
/// which name and date, and whose aggregates each row carries. Both endpoints build their rows here, so a
/// card and its curve can never disagree. No I/O, no clock, no DbContext — everything the rules read is a
/// parameter; the caller aggregates <c>UsageStats</c> per slot and folds them along
/// <see cref="UsageViewRow.AggregateSlotIds"/>.
/// <para>
/// <b>Input contract.</b> <c>emotes</c>: every emote of the channel, archived ones included (the base set is
/// narrowed here). <c>slots</c>: every <c>EmoteSlot</c> of those emotes — own slots and unsettled seeds
/// alike; a slot of an emote that is not in <c>emotes</c> is ignored. <c>seedIdsOfSet</c>: the ids of the
/// seeds of X (<c>SetSeeds.SeedsOfSet</c>, plan 2.1). <c>slotIdsWithRowsInSet</c>: the ids of the slots that
/// carry at least one <c>UsageStats</c> row with <c>EmoteSetId = X</c>, without a date bound and whatever
/// the row's <c>UseCount</c> — the per-slot form of today's "has at least one count under this set" test
/// (spec E16). <c>isActiveView</c>: X is the channel's active set.
/// </para>
/// <para>
/// <b>Output contract.</b> Rows grouped by emote, emotes in ordinal <c>Id</c> order, the rows of one emote in
/// the D2 order of their slots (plan 2.4: the <c>/series</c> entry order; <c>/totals</c> re-sorts by its
/// total). Every emote of the base set has at least one row (plan 2.2); no other emote has any.
/// </para>
/// <para>
/// <b>Invariant</b> (R2): <see cref="UsageViewRow.SlotId"/> is set exactly when the row is a slot-mode row,
/// and then <see cref="UsageViewRow.AggregateSlotIds"/> is exactly that one slot — a row with a slot id
/// shows that slot's numbers and nothing else, which is what <c>/daily?slotId=</c> answers. An emote-mode
/// row carries all of its emote's slots with rows in X: the emote's whole count in X, today's number.
/// </para>
/// </summary>
internal static class UsageViewRows
{
    /// <summary>
    /// Builds the view rows of one channel for one requested set; see the type summary for the contracts.
    /// </summary>
    public static IReadOnlyList<UsageViewRow> Build(
        IReadOnlyList<UsageViewEmote> emotes,
        IReadOnlyList<UsageViewSlot> slots,
        IReadOnlySet<string> seedIdsOfSet,
        IReadOnlySet<string> slotIdsWithRowsInSet,
        bool isActiveView)
    {
        var slotsByEmote = slots
            .GroupBy(s => s.EmoteId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(s => new BaseSlotCandidate(s.Id, s.Name, s.AddedAt), BaseSlotRule.D2Order).ToList(),
                StringComparer.Ordinal);

        var rows = new List<UsageViewRow>();
        foreach (var emote in emotes.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var emoteSlots = slotsByEmote.GetValueOrDefault(emote.Id) ?? [];
            var slotsWithRows = emoteSlots
                .Where(s => slotIdsWithRowsInSet.Contains(s.Id) || seedIdsOfSet.Contains(s.Id))
                .Select(s => s.Id)
                .ToList();

            // Plan 2.2 base set, unchanged from today: the active view is every unarchived emote, zero-filled;
            // a non-active view is every emote with at least one row under X, archived ones included (E16/E23).
            var inBaseSet = isActiveView ? !emote.IsArchived : slotsWithRows.Count > 0;
            if (!inBaseSet)
            {
                continue;
            }

            if (IsEmoteMode(emoteSlots, seedIdsOfSet, isActiveView))
            {
                rows.Add(EmoteModeRow(emote, slotsWithRows));
                continue;
            }

            // Plan 2.2 table, slot mode. Own slot = not an unsettled seed (plan 2.1, E7). Active view: one row
            // per active own slot, zero-filled; an archived own slot never shows there, even with rows in X —
            // its history returns on the same card when D7 resumes it (D1/D7). Non-active view: one row per own
            // slot with rows in X, archived or not; a slot without rows in X (an emptied one too) has no row.
            var shown = emoteSlots
                .Where(s => !s.SeedUnsettled
                            && (isActiveView ? !s.IsArchived : slotIdsWithRowsInSet.Contains(s.Id)))
                .ToList();

            // Defensive, unreachable with a consistent snapshot: in a non-active view a base-set emote whose
            // rows in X sit on no own slot only has them on a seed of X, which is emote mode above. Should the
            // snapshot disagree (a settle between the caller's queries), the emote keeps its whole count in
            // one emote-mode row rather than vanishing; the invariant (R2) still holds.
            if (shown.Count == 0)
            {
                rows.Add(EmoteModeRow(emote, slotsWithRows));
                continue;
            }

            foreach (var slot in shown)
            {
                rows.Add(new UsageViewRow(
                    emote.Id,
                    slot.Id,
                    slot.Name,
                    // Plan 2.2 wire table: the slot's own date; null stays "unknown", never "new".
                    slot.AddedAt,
                    slot.AddedAtFromRest,
                    // Plan 2.2 wire table: slot or emote archived; in the active view both are unarchived by
                    // the filters above, so this is always false there.
                    slot.IsArchived || emote.IsArchived,
                    SiblingNames(slot, shown),
                    [slot.Id],
                    SharesAuthoritativeDate(slot, emoteSlots)));
            }
        }

        return rows;
    }

    /// <summary>
    /// Plan 2.1 "Modus je Emote" (R2): emote mode when the emote has a seed of X — its rows in X are not
    /// settled onto a slot yet (E7: a seed does not count; its name may be stale, its date is null) — or, in
    /// the active view only, when it has no active own slot (E14: the virtual slot, read as the emote-wide
    /// row from before slots). A seed of another set and a row-less seed are not seeds of X and decide nothing.
    /// </summary>
    private static bool IsEmoteMode(List<UsageViewSlot> emoteSlots, IReadOnlySet<string> seedIdsOfSet, bool isActiveView)
    {
        if (emoteSlots.Any(s => seedIdsOfSet.Contains(s.Id)))
        {
            return true;
        }

        return isActiveView && !emoteSlots.Any(s => !s.SeedUnsettled && !s.IsArchived);
    }

    /// <summary>
    /// Plan #377 2.12 (F6) <c>firstSeenAtShared</c>: a slot with an authoritative date shares it when another
    /// own slot of the emote (<paramref name="emoteSlots"/> is the whole list — archived slots included, shown
    /// or not) carries the same stored instant, as <c>EmoteSlotReconcilePlanner.BindByDate</c> compares it.
    /// An unsettled seed has no date identity (E7) and never counts; a provisional or undated slot has none
    /// either, so it is never flagged.
    /// </summary>
    private static bool SharesAuthoritativeDate(UsageViewSlot slot, List<UsageViewSlot> emoteSlots) =>
        slot is { AddedAtFromRest: true, AddedAt: not null }
        && emoteSlots.Any(o => !string.Equals(o.Id, slot.Id, StringComparison.Ordinal)
                               && !o.SeedUnsettled
                               && o.AddedAtFromRest
                               && o.AddedAt == slot.AddedAt);

    /// <summary>
    /// R1/R2: exactly the emote-wide row from before slots — no slot id, the emote's name and
    /// <c>FirstSeenAt</c>, never "from REST", no siblings, <c>IsArchived</c> from the emote (always false
    /// in the active view, whose base set is unarchived) — carrying every slot of the emote with rows in
    /// X, so its numbers are the emote's whole count in X.
    /// </summary>
    private static UsageViewRow EmoteModeRow(UsageViewEmote emote, List<string> slotsWithRows) =>
        new(emote.Id, null, emote.Name, emote.FirstSeenAt, false, emote.IsArchived, [], slotsWithRows, false);

    /// <summary>
    /// Plan 2.2 <c>siblingNames</c>: the names of the other rows of the same emote in this view, in the D2
    /// order of their slots (<paramref name="shown"/> already is), ordinal distinct, without the row's own name.
    /// </summary>
    private static List<string> SiblingNames(UsageViewSlot self, List<UsageViewSlot> shown) =>
        shown
            .Where(s => !string.Equals(s.Id, self.Id, StringComparison.Ordinal)
                        && !string.Equals(s.Name, self.Name, StringComparison.Ordinal))
            .Select(s => s.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>An emote as <see cref="UsageViewRows"/> sees it.</summary>
internal sealed record UsageViewEmote(string Id, string Name, DateTime? FirstSeenAt, bool IsArchived);

/// <summary>An <c>EmoteSlot</c> as <see cref="UsageViewRows"/> sees it.</summary>
internal sealed record UsageViewSlot(
    string Id,
    string EmoteId,
    string Name,
    DateTime? AddedAt,
    bool AddedAtFromRest,
    bool IsArchived,
    bool SeedUnsettled);

/// <summary>
/// One row of the usage view (plan 2.2/2.3). <see cref="SlotId"/> <c>null</c> = emote mode. The display
/// fields are final (<see cref="Name"/> is the wire's <c>emoteName</c>/<c>name</c>, <see cref="FirstSeenAt"/>
/// the wire's <c>firstSeenAt</c>); <see cref="AggregateSlotIds"/> are the slots whose usage rows in X the
/// row's numbers are the sum of — possibly none (a zero-filled row). <see cref="FirstSeenAtShared"/> is the
/// wire's <c>firstSeenAtShared</c> (plan #377 2.12); only <c>/totals</c> sends it.
/// </summary>
internal sealed record UsageViewRow(
    string EmoteId,
    string? SlotId,
    string Name,
    DateTime? FirstSeenAt,
    bool FirstSeenAtFromRest,
    bool IsArchived,
    IReadOnlyList<string> SiblingNames,
    IReadOnlyList<string> AggregateSlotIds,
    bool FirstSeenAtShared);
