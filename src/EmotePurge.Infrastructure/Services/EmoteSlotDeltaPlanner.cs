using EmotePurge.Core.SevenTv;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The pure decision half of an EventAPI <c>emote_set.update</c> dispatch (spec §7.2, D17; plan #374
/// section 4). <see cref="SevenTvSyncService"/> takes the snapshot behind its
/// row locks, asks this class for a <see cref="SlotDeltaPlan"/> and applies it to its tracked entities.
/// No I/O, no clock, no DbContext — everything the rules read is a parameter.
/// <para>
/// <b>Input contract.</b> The dispatch as parsed (<see cref="SevenTvEmoteSetDelta"/>: pushed entries with
/// the empty string for a missing name, renames and pulls with <c>null</c>); one
/// <see cref="SlotDeltaEmoteSnapshot"/> per existing emote row of a dispatch id (an id without one is
/// state S0, extra snapshots are ignored), each with <b>all</b> slots of the emote read after the lock;
/// the ids of the unsettled seeds whose usage rows belong to the active set (same query as the reconcile,
/// plan #374 3.2 step 2); and <c>now</c> for the date of a push slot and for archive stamps.
/// </para>
/// <para>
/// <b>Output contract.</b> One <see cref="SlotDeltaEmotePlan"/> per dispatch id. The dispatch is ambiguous
/// as soon as one emote is (E7): then the service writes no emote and no slot row, but still records
/// <see cref="SlotDeltaPlan.LeaveIds"/> (operator decision R5). Otherwise every plan is applied: the emote
/// action (<see cref="SlotDeltaEmoteAction"/>), the image, <see cref="SlotDeltaEmotePlan.UpdatedSlots"/>
/// via <see cref="SlotState.ApplyTo"/>, <see cref="SlotDeltaEmotePlan.CreatedSlots"/> via
/// <see cref="SlotState.ToNewEntity"/>, the D13 derivation (<c>null</c> = keep <c>Emote.Name</c> and
/// <c>FirstSeenAt</c>), then the leaves. The dispatch never renames the emote row itself (plan 3.3).
/// </para>
/// <para>
/// Every change is resolved against the <b>starting snapshot</b>, never against an intermediate state
/// (E6), so the plan does not depend on the order of the wire lists. All name comparisons are ordinal and
/// case-sensitive.
/// </para>
/// </summary>
internal static class EmoteSlotDeltaPlanner
{
    /// <summary>Plans one dispatch (plan #374 3.2 step 4): each emote independently, against the snapshot.</summary>
    /// <exception cref="ArgumentException">Two snapshots carry the same 7TV id.</exception>
    public static SlotDeltaPlan Plan(
        SevenTvEmoteSetDelta delta,
        IReadOnlyCollection<SlotDeltaEmoteSnapshot> snapshots,
        IReadOnlySet<string> activeSetSeedIds,
        DateTime now)
    {
        var byId = new Dictionary<string, SlotDeltaEmoteSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (!byId.TryAdd(snapshot.SevenTvEmoteId, snapshot))
            {
                throw new ArgumentException($"Two snapshots for 7TV emote {snapshot.SevenTvEmoteId}.", nameof(snapshots));
            }
        }

        // E6: equal entries within one kind are one entry. Names compare ordinally; for a push the empty
        // string stands for a missing name (parser contract), for renames and pulls null does.
        var pushes = delta.Pushed
            .DistinctBy(p => (p.Id, p.Name))
            .ToLookup(p => p.Id, StringComparer.Ordinal);
        var renames = delta.Updated
            .DistinctBy(r => (r.Id, r.OldName, r.NewName))
            .ToLookup(r => r.Id, StringComparer.Ordinal);
        var pulls = delta.Pulled
            .DistinctBy(p => (p.Id, p.Name))
            .ToLookup(p => p.Id, StringComparer.Ordinal);

        var ids = delta.Pushed.Select(p => p.Id)
            .Concat(delta.Updated.Select(r => r.Id))
            .Concat(delta.Pulled.Select(p => p.Id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var plans = ids
            .Select(id => PlanEmote(
                new EmoteChanges(id, pushes[id].ToList(), renames[id].ToList(), pulls[id].ToList()),
                byId.GetValueOrDefault(id),
                activeSetSeedIds,
                now))
            .ToList();
        return new SlotDeltaPlan(plans);
    }

    private static SlotDeltaEmotePlan PlanEmote(
        EmoteChanges changes,
        SlotDeltaEmoteSnapshot? snapshot,
        IReadOnlySet<string> activeSetSeedIds,
        DateTime now)
    {
        var context = new EmoteContext(changes, snapshot, now);

        // State H (§7.2, D12, AK 20): an unsettled seed of the active set waits for REST's settle; any
        // change to its emote is ambiguous, ahead of every other rule.
        if (snapshot is not null && snapshot.Slots.Any(s => s.SeedUnsettled && activeSetSeedIds.Contains(s.Id)))
        {
            return context.Ambiguous(SlotDeltaAmbiguity.SeedOfActiveSet);
        }

        // E6: per id only one kind — any number of pushes, any number of pulls, or exactly one rename.
        // Every mix is undecidable from the dispatch alone (e.g. archived A, push B + pull A: did the
        // emote leave the set in between?).
        var kinds = (changes.Pushes.Count > 0 ? 1 : 0) + (changes.Renames.Count > 0 ? 1 : 0) + (changes.Pulls.Count > 0 ? 1 : 0);
        if (kinds > 1 || changes.Renames.Count > 1)
        {
            return context.Ambiguous(SlotDeltaAmbiguity.MixedChangeKinds);
        }

        if (changes.Pushes.Count > 0)
        {
            return PlanPushes(context);
        }

        return changes.Renames.Count > 0 ? PlanRename(context) : PlanPulls(context);
    }

    /// <summary>The <c>pushed</c> rows of plan 4.2 (spec §7.2, D17, D7, D11 b).</summary>
    private static SlotDeltaEmotePlan PlanPushes(EmoteContext context)
    {
        var names = context.Changes.Pushes.Select(p => p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        // §7.2 "value.name fehlt" → ambiguous.
        if (names.Any(string.IsNullOrEmpty))
        {
            return context.Ambiguous(SlotDeltaAmbiguity.NameMissing);
        }

        switch (context.State)
        {
            case EmoteState.Missing:
                // S0: create the emote, one new provisional slot per pushed name.
                return context.Applied(SlotDeltaEmoteAction.Create, [], NewPushSlots(names, context.Now), observeLeave: false);

            case EmoteState.Archived:
                // E4: active own slots under an archived emote are a stale state (left from before the
                // slot work, or a sync error) — REST has to sort it out, nothing is guessed on top of it.
                if (context.ActiveOwn.Count > 0)
                {
                    return context.Ambiguous(SlotDeltaAmbiguity.StaleSlotsUnderArchivedEmote);
                }

                // AK 19 / E3: a return under an archived name is D7's, which needs the REST date.
                if (names.Any(context.HasResumptionCandidate))
                {
                    return context.Ambiguous(SlotDeltaAmbiguity.ArchivedNamesake);
                }

                // A return under a new name: un-archive, new provisional slot(s) (D4: a new entry).
                return context.Applied(SlotDeltaEmoteAction.Unarchive, [], NewPushSlots(names, context.Now), observeLeave: false);

            case EmoteState.Virtual:
                // E5 (Sol-P2-2): every push in the virtual state is ambiguous. The D7 candidate check
                // runs first so the log names the stronger reason (restore report without entry).
                return context.Ambiguous(names.Any(context.HasResumptionCandidate)
                    ? SlotDeltaAmbiguity.ArchivedNamesake
                    : SlotDeltaAmbiguity.VirtualSlot);

            default:
                // Orchestrator 12.10. (from T2): a pushed name carried by two active own slots meets a state
                // that contradicts 7TV's unique aliases per set — stale or inconsistent, REST decides.
                if (names.Any(context.HasDuplicateActiveName))
                {
                    return context.Ambiguous(SlotDeltaAmbiguity.DuplicateActiveName);
                }

                // S2. AK 19: an active own slot of the name means REST was faster → no-op for that name.
                // Only for the remaining names the D7 candidate check applies (D7 never resumes over an
                // active slot of the same name); a candidate → ambiguous, else a new provisional slot.
                var unexplained = names
                    .Where(n => !context.ActiveOwn.Any(s => string.Equals(s.Name, n, StringComparison.Ordinal)))
                    .ToList();
                if (unexplained.Any(context.HasResumptionCandidate))
                {
                    return context.Ambiguous(SlotDeltaAmbiguity.ArchivedNamesake);
                }

                return context.Applied(SlotDeltaEmoteAction.None, [], NewPushSlots(unexplained, context.Now), observeLeave: false);
        }
    }

    /// <summary>The <c>updated</c> rows of plan 4.2 (spec §7.2, D17, E10, E12).</summary>
    private static SlotDeltaEmotePlan PlanRename(EmoteContext context)
    {
        var rename = context.Changes.Renames[0];
        if (string.IsNullOrEmpty(rename.OldName) || string.IsNullOrEmpty(rename.NewName))
        {
            return context.Ambiguous(SlotDeltaAmbiguity.NameMissing);
        }

        // S0, S1, S3: no active own slot the rename could hang on.
        if (context.State != EmoteState.ActiveWithSlots)
        {
            return context.Ambiguous(SlotDeltaAmbiguity.RenameSourceMissing);
        }

        // E10 / operator decision R4 (Sol-P1-1): in place only with exactly one active own slot. With two
        // or more, 7TV's quirk (value.name = the first slot's name) is only detectable while our sibling
        // names are fresh; REST resolves the rename through the date identity instead (§6.5).
        if (context.ActiveOwn.Count >= 2)
        {
            return context.Ambiguous(SlotDeltaAmbiguity.RenameWithSiblings);
        }

        var slot = context.ActiveOwn[0];
        if (!string.Equals(slot.Name, rename.OldName, StringComparison.Ordinal))
        {
            return context.Ambiguous(SlotDeltaAmbiguity.RenameSourceMissing);
        }

        if (string.Equals(rename.OldName, rename.NewName, StringComparison.Ordinal))
        {
            // E10: o == n → only the image.
            return context.Applied(SlotDeltaEmoteAction.None, [], [], observeLeave: false, isRename: true);
        }

        // E12 (Sol-P2-1): the authoritative unique index (EmoteId, AddedAt, Name) also covers archived
        // slots, so a D4 tie can leave an archived B(t) next to the active A(t); renaming A → B would
        // violate it. A provisional slot is outside the index and needs no check.
        if (slot.AddedAtFromRest
            && context.Own.Any(s => !string.Equals(s.Id, slot.Id, StringComparison.Ordinal)
                && s.AddedAtFromRest
                && s.AddedAt == slot.AddedAt
                && string.Equals(s.Name, rename.NewName, StringComparison.Ordinal)))
        {
            return context.Ambiguous(SlotDeltaAmbiguity.AuthoritativeKeyCollision);
        }

        // In place: same slot id, AddedAt and date origin unchanged (probe S1: rename-proof).
        return context.Applied(SlotDeltaEmoteAction.None, [slot with { Name = rename.NewName }], [], observeLeave: false, isRename: true);
    }

    /// <summary>The <c>pulled</c> rows of plan 4.2 (spec §7.2, D17, E5, E8, operator decision R1).</summary>
    private static SlotDeltaEmotePlan PlanPulls(EmoteContext context)
    {
        var pulls = context.Changes.Pulls;
        switch (context.State)
        {
            case EmoteState.Missing:
            case EmoteState.Archived:
                // E8: nothing to write, the leave is observed (today's "already archived"; a pull of an
                // id without a row is observed as before, F5). Stale active slots under an archived emote
                // stay for REST (plan section 5).
                return context.Applied(SlotDeltaEmoteAction.None, [], [], observeLeave: true);

            case EmoteState.Virtual:
                // E5: no slot exists, so archiving the emote miscounts nothing — if the pull names the
                // virtual slot (no name, or Emote.Name). Any other name is ambiguous.
                if (pulls.All(p => p.Name is null || string.Equals(p.Name, context.Snapshot!.Name, StringComparison.Ordinal)))
                {
                    return context.Applied(SlotDeltaEmoteAction.Archive, [], [], observeLeave: true);
                }

                return context.Ambiguous(SlotDeltaAmbiguity.VirtualSlot);

            default:
                return PlanActivePulls(context);
        }
    }

    /// <summary>
    /// S2 pulls. E6: all pulls of the id resolve against the snapshot; their targets are a set (a nameless
    /// and a named pull of the one active slot archive it once). "Last slot" = no active own slot is left
    /// after all of them.
    /// </summary>
    private static SlotDeltaEmotePlan PlanActivePulls(EmoteContext context)
    {
        var targets = new HashSet<SlotState>();

        // §7.2 / AK 20: a nameless pull is unambiguous only with exactly one active own slot.
        if (context.Changes.Pulls.Any(p => p.Name is null))
        {
            if (context.ActiveOwn.Count != 1)
            {
                return context.Ambiguous(SlotDeltaAmbiguity.NamelessPullWithSiblings);
            }

            targets.Add(context.ActiveOwn[0]);
        }

        var names = context.Changes.Pulls.Select(p => p.Name).OfType<string>().ToList();

        // Orchestrator 12.10. (from T2): 7TV keeps aliases unique per set, so two active own slots of a
        // pulled name are a stale or inconsistent state — REST decides which one left instead of a guess
        // (same principle as R1, R4, E12). Checked over all names before R1, so the reason code does not
        // depend on the wire order.
        if (names.Any(context.HasDuplicateActiveName))
        {
            return context.Ambiguous(SlotDeltaAmbiguity.DuplicateActiveName);
        }

        foreach (var name in names)
        {
            var target = context.ActiveOwn.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

            // Operator decision R1 (F1): a named pull without an active own slot of that name, while the
            // emote is active → ambiguous.
            if (target is null)
            {
                return context.Ambiguous(SlotDeltaAmbiguity.PullTargetMissing);
            }

            targets.Add(target);
        }

        var archived = targets
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s with { IsArchived = true, ArchivedAt = context.Now })
            .ToList();

        // §7.2 / D17: the last active own slot gone → archive the emote and observe the leave; otherwise
        // the emote stays active, no leave, D13 derives anew (AK 19).
        var lastSlotGone = context.ActiveOwn.All(targets.Contains);
        return context.Applied(
            lastSlotGone ? SlotDeltaEmoteAction.Archive : SlotDeltaEmoteAction.None,
            archived,
            [],
            observeLeave: lastSlotGone);
    }

    // D11 b: a push slot is provisional, dated "now" (the dispatch carries no added_at, F7).
    private static List<SlotState> NewPushSlots(IEnumerable<string> names, DateTime now) =>
        names.Select(n => new SlotState(Guid.NewGuid().ToString(), n, now, false, false, null, false)).ToList();

    private enum EmoteState
    {
        /// <summary>S0: no emote row.</summary>
        Missing,

        /// <summary>S1: emote row archived.</summary>
        Archived,

        /// <summary>S2: emote active with at least one active own slot.</summary>
        ActiveWithSlots,

        /// <summary>S3: emote active without an active own slot (virtual slot under <c>Emote.Name</c>, plan #372 E14).</summary>
        Virtual,
    }

    private sealed record EmoteChanges(
        string Id,
        IReadOnlyList<SevenTvEmote> Pushes,
        IReadOnlyList<SevenTvEmoteRename> Renames,
        IReadOnlyList<SevenTvPulledEntry> Pulls);

    /// <summary>The snapshot of one emote as the rules read it, and the two ways a plan ends.</summary>
    private sealed class EmoteContext
    {
        public EmoteContext(EmoteChanges changes, SlotDeltaEmoteSnapshot? snapshot, DateTime now)
        {
            Changes = changes;
            Snapshot = snapshot;
            Now = now;

            // Plan #372 E7 / #374 4.1: own slots = every slot but unsettled seeds. Foreign seeds are never
            // read, changed or counted.
            Own = snapshot?.Slots.Where(s => !s.SeedUnsettled).ToList() ?? [];
            ActiveOwn = Own.Where(s => !s.IsArchived).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            State = snapshot switch
            {
                null => EmoteState.Missing,
                { IsArchived: true } => EmoteState.Archived,
                _ when ActiveOwn.Count > 0 => EmoteState.ActiveWithSlots,
                _ => EmoteState.Virtual,
            };
        }

        public EmoteChanges Changes { get; }
        public SlotDeltaEmoteSnapshot? Snapshot { get; }
        public DateTime Now { get; }
        public List<SlotState> Own { get; }
        public List<SlotState> ActiveOwn { get; }
        public EmoteState State { get; }

        private bool WasActive => Snapshot is { IsArchived: false };

        /// <summary>
        /// E3: a D7 candidate is an own archived slot of that name with <c>ArchivedAt</c> set — exactly the
        /// slots REST §6.2 step 4 can resume. A slot archived without a stamp (backfill slot "never in",
        /// emptied seed, plan #372 E12) is none. If a later child changes D7's candidates, this follows.
        /// </summary>
        public bool HasResumptionCandidate(string name) =>
            Own.Any(s => s.IsArchived && s.ArchivedAt is not null && string.Equals(s.Name, name, StringComparison.Ordinal));

        /// <summary>Two or more active own slots carry <paramref name="name"/> (contradicts 7TV's unique aliases).</summary>
        public bool HasDuplicateActiveName(string name) =>
            ActiveOwn.Count(s => string.Equals(s.Name, name, StringComparison.Ordinal)) >= 2;

        public SlotDeltaEmotePlan Ambiguous(string reason) =>
            new(Changes.Id, reason, SlotDeltaEmoteAction.None, null, [], [], null, WasActive, WasActive, false);

        public SlotDeltaEmotePlan Applied(
            SlotDeltaEmoteAction action,
            IReadOnlyList<SlotState> updated,
            IReadOnlyList<SlotState> created,
            bool observeLeave,
            bool isRename = false)
        {
            var writesSlots = updated.Count > 0 || created.Count > 0;

            // Plan 3.3: Emote.Name/FirstSeenAt follow D13 over the active own slots after the change — the
            // reconcile's own derivation. Only when a slot changes: a no-op stays a no-op even if the row
            // disagrees with its slots (that is REST's to correct, as it is today).
            EmoteNameDerivation? derivation = null;
            if (writesSlots)
            {
                var updatedById = updated.ToDictionary(s => s.Id, StringComparer.Ordinal);
                var resulting = Own.Select(s => updatedById.GetValueOrDefault(s.Id) ?? s).Concat(created);
                derivation = EmoteSlotReconcilePlanner.Derive(resulting);
            }

            var imageUrl = PlanImage(action, writesSlots || isRename);
            var isActiveAfter = action switch
            {
                SlotDeltaEmoteAction.Create or SlotDeltaEmoteAction.Unarchive => true,
                SlotDeltaEmoteAction.Archive => false,
                _ => WasActive,
            };
            return new SlotDeltaEmotePlan(
                Changes.Id, null, action, imageUrl, updated, created.OrderBy(s => s.Name, StringComparer.Ordinal).ToList(),
                derivation, WasActive, isActiveAfter, observeLeave);
        }

        /// <summary>
        /// Plan 3.3 "Bild": from the change, never an empty URL over a known one. Only pushes and renames
        /// carry an image; when several do, the ordinally smallest non-empty URL wins so the result does not
        /// depend on the wire order (they are the same emote's image). A new row always gets a value; an
        /// existing row only when the plan touches it (a no-op push stays a no-op, AK 19) or for a rename
        /// (E10: o == n → only the image).
        /// </summary>
        private string? PlanImage(SlotDeltaEmoteAction action, bool touched)
        {
            var candidate = Changes.Pushes.Select(p => p.ImageUrl)
                .Concat(Changes.Renames.Select(r => r.ImageUrl))
                .Where(u => !string.IsNullOrEmpty(u))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();

            if (action == SlotDeltaEmoteAction.Create)
            {
                return candidate ?? string.Empty;
            }

            if (candidate is null || (action == SlotDeltaEmoteAction.None && !touched) || action == SlotDeltaEmoteAction.Archive)
            {
                return null;
            }

            return string.Equals(candidate, Snapshot!.ImageUrl, StringComparison.Ordinal) ? null : candidate;
        }
    }
}

/// <summary>
/// The columns of one existing emote row of a dispatch id the rules read, plus <b>all</b> its slots (own
/// slots and every unsettled seed, read after the lock).
/// </summary>
internal sealed record SlotDeltaEmoteSnapshot(
    string SevenTvEmoteId,
    string Name,
    string ImageUrl,
    bool IsArchived,
    IReadOnlyList<SlotState> Slots);

/// <summary>What happens to the emote row itself (plan #374 3.3).</summary>
internal enum SlotDeltaEmoteAction
{
    /// <summary>The row keeps its archive state (image and derivation may still change).</summary>
    None,

    /// <summary>S0 push: a new row, <c>LastEnteredSetAtUtc</c> now, name and <c>FirstSeenAt</c> from the derivation.</summary>
    Create,

    /// <summary>S1 push: <c>IsArchived = false</c>, <c>ArchivedAt = null</c>, <c>LastEnteredSetAtUtc</c> now, <c>IsPlaceholder = false</c>.</summary>
    Unarchive,

    /// <summary>Last active slot pulled (or S3 pull): <c>IsArchived</c>, <c>ArchivedAt</c> now, <c>IsPlaceholder = false</c> (D37).</summary>
    Archive,
}

/// <summary>
/// The reason codes of an ambiguous emote (plan #374 4.3), English and free of alias names so they can go
/// into the information log line as they are.
/// </summary>
internal static class SlotDeltaAmbiguity
{
    /// <summary>State H: the emote carries an unsettled seed of the active set (D12, AK 20).</summary>
    public const string SeedOfActiveSet = "seed-of-active-set";

    /// <summary>A push without <c>value.name</c>, a rename without old or new name.</summary>
    public const string NameMissing = "name-missing";

    /// <summary>A push under the name of a D7 candidate (AK 19, E3).</summary>
    public const string ArchivedNamesake = "archived-namesake";

    /// <summary>A push onto an archived emote that still has active own slots (E4).</summary>
    public const string StaleSlotsUnderArchivedEmote = "stale-slots-under-archived-emote";

    /// <summary>A push in the virtual state, or a pull of another name than <c>Emote.Name</c> there (E5).</summary>
    public const string VirtualSlot = "virtual-slot";

    /// <summary>A rename whose old name is not the one active own slot, or without any active own slot.</summary>
    public const string RenameSourceMissing = "rename-source-missing";

    /// <summary>A rename of an emote with two or more active own slots (E10, operator decision R4).</summary>
    public const string RenameWithSiblings = "rename-with-siblings";

    /// <summary>
    /// A rename that would violate the authoritative slot index (E12); the service also answers a slot key
    /// conflict at save time with this code (plan 3.4).
    /// </summary>
    public const string AuthoritativeKeyCollision = "authoritative-key-collision";

    /// <summary>A pull without a name while the emote has two or more active own slots (AK 20).</summary>
    public const string NamelessPullWithSiblings = "nameless-pull-with-siblings";

    /// <summary>A named pull without an active own slot of that name on an active emote (operator decision R1).</summary>
    public const string PullTargetMissing = "pull-target-missing";

    /// <summary>Two change kinds, or two renames, for one id in one dispatch (E6).</summary>
    public const string MixedChangeKinds = "mixed-change-kinds";

    /// <summary>
    /// A pushed or pulled name carried by two or more active own slots — a state that contradicts 7TV's
    /// unique aliases per set (Orchestrator 12.10., from T2). A rename meets it as <see cref="RenameWithSiblings"/>,
    /// a nameless pull as <see cref="NamelessPullWithSiblings"/>.
    /// </summary>
    public const string DuplicateActiveName = "duplicate-active-name";
}

/// <summary>
/// The decision for one dispatch id. <see cref="AmbiguityReason"/> <c>null</c> = applicable; an ambiguous
/// plan carries nothing else (<see cref="IsActiveAfter"/> = <see cref="WasActive"/>, no leave).
/// <see cref="ImageUrl"/> is the value to write, <c>null</c> = keep (for <see cref="SlotDeltaEmoteAction.Create"/>
/// always set, possibly empty). <see cref="Derivation"/> is set only when a slot changes, and <c>null</c> then
/// still means "no active own slot left, keep name and date" (D13).
/// </summary>
internal sealed record SlotDeltaEmotePlan(
    string SevenTvEmoteId,
    string? AmbiguityReason,
    SlotDeltaEmoteAction EmoteAction,
    string? ImageUrl,
    IReadOnlyList<SlotState> UpdatedSlots,
    IReadOnlyList<SlotState> CreatedSlots,
    EmoteNameDerivation? Derivation,
    bool WasActive,
    bool IsActiveAfter,
    bool ObserveLeave)
{
    public bool IsAmbiguous => AmbiguityReason is not null;

    /// <summary>True when applying this plan changes the emote row or a slot (plan 3.2 step 8, "Applied").</summary>
    public bool WritesInventory =>
        EmoteAction != SlotDeltaEmoteAction.None || ImageUrl is not null || UpdatedSlots.Count > 0 || CreatedSlots.Count > 0;
}

/// <summary>The decision for one dispatch; see <see cref="EmoteSlotDeltaPlanner"/> for how the service applies it.</summary>
internal sealed record SlotDeltaPlan(IReadOnlyList<SlotDeltaEmotePlan> Emotes)
{
    /// <summary>E7: one ambiguous emote makes the whole dispatch ambiguous for the inventory.</summary>
    public bool IsAmbiguous => Emotes.Any(e => e.IsAmbiguous);

    /// <summary>The ambiguous emotes with their reason codes, ordinal by 7TV id, for the log line (4.3).</summary>
    public IReadOnlyList<SlotDeltaEmotePlan> Ambiguities => Emotes.Where(e => e.IsAmbiguous).ToList();

    /// <summary>
    /// E8, and E7 / operator decision R5 when the dispatch is ambiguous: the pulled ids whose own plan is
    /// unambiguous and ends without an active slot (last slot gone, already archived, no row, S3 pull of
    /// <c>Emote.Name</c>). The same set in both cases; ordinal.
    /// </summary>
    public IReadOnlyList<string> LeaveIds => Emotes.Where(e => !e.IsAmbiguous && e.ObserveLeave).Select(e => e.SevenTvEmoteId).ToList();

    /// <summary>Applied vs NoChange (plan 3.2 step 8): false for an ambiguous dispatch; a leave alone is no change.</summary>
    public bool HasInventoryChanges => !IsAmbiguous && Emotes.Any(e => e.WritesInventory);

    /// <summary>
    /// Input for the plausibility guard (plan 3.2 step 6): how the number of active emote rows of the channel
    /// changes when the plan is applied. Only meaningful for an unambiguous dispatch (an ambiguous one writes
    /// nothing and is not checked).
    /// </summary>
    public int ActiveEmoteCountChange => Emotes.Sum(e => (e.IsActiveAfter ? 1 : 0) - (e.WasActive ? 1 : 0));
}
