export interface SlotProjection {
  projected: number;
  capacity: number;
  overflow: boolean;
}

/**
 * Projects a target set's occupied slots forward by a pending run's *net* change — the restore and
 * import confirm dialogs' "you'll have N / capacity" line. `null` when 7TV reports no usable
 * capacity (`null` or `<= 0`): without a real denominator there is nothing to project.
 *
 * `delta` is a net delta, not a raw add count: `delta = addCount − removedEntryCount`
 * (`conflict-resolution.ts`'s `summarizeTransferPlan`, AK 21). A plain restore or an import with no
 * name-conflict resolutions passes its add count unchanged, since `removedEntryCount` is always `0`
 * there — this signature does not change for them. Once a run can carry a `replace` decision, the
 * three shapes this can take are: a single rename is `+1` (an ADD with no matching REMOVE); an
 * ordinary replace is `0` (its own ADD cancels the one entry its own REMOVE takes); a replace whose
 * target holds more than one live entry for its id — a #74 duplicate, or a named alias plus an
 * aliasless sibling — goes negative, because the REMOVE clears every entry at once while the ADD
 * only ever puts one back.
 *
 * Not a replacement for `shared/emotes/slot-budget.ts`: that one models a *removal* and clamps the
 * result into range, because a pending delete can never push occupancy below zero. This one models a
 * net change that can itself be negative, where going over capacity is exactly the case worth
 * flagging — clamping it away would hide the one thing this function exists to report (`overflow`).
 */
export function projectSlots(
  occupied: number,
  capacity: number | null,
  delta: number,
): SlotProjection | null {
  if (capacity === null || capacity <= 0) {
    return null;
  }

  const projected = occupied + delta;
  return { projected, capacity, overflow: projected > capacity };
}
