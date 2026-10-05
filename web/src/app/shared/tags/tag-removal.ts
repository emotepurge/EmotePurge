import type { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import type {
  EmoteTagEntry,
  EmoteTagRef,
  TagPlacementSnapshotEntry,
} from '../../core/tags/emote-tag.model';

/**
 * The pure half of a tag clear-out (#201 T-C, spec 7.2/5): what the confirmation dialog proposes.
 *
 * - An id counts as in the set when `aliasesById.has(id) || aliaslessIds.has(id)` — also when it
 *   sits only under aliasless entries (spec 3.4): one REMOVE takes every entry of the id, so the
 *   emote is there to be removed even though the read cannot name it. No fallback to the entry's
 *   `inSet` flag (E28): only the live read decides.
 * - Every uncertainty tips towards "propose less": anything not clearly this tag's own, unheld
 *   placement starts unchecked, and the user can tick it either way.
 * - The snapshot carries **all** of the tag's own placements, visible in the set or not (6.4/3): the
 *   ones no longer in the set are not rows, but they belong to the report so the server can drop
 *   them, and each carries the revision it was read at so the server touches only that placement.
 * - `aliases` come straight from the live read (empty for an aliasless-only id). The queue the run
 *   deletes from is built later by `toDeleteQueueEmotes`, which adds the aliasless fallback — this
 *   file has no alias rule of its own.
 */
export type TagRemovalReason = 'placed' | 'heldBy' | 'alreadyPresent';

export interface TagRemovalRow {
  sevenTvEmoteId: string;
  aliases: string[];
  checked: boolean;
  reason: TagRemovalReason;
  /** The tags that still need the emote; empty unless `reason` is `heldBy`. */
  heldBy: EmoteTagRef[];
  /** When this tag placed it; `null` when the placement is not this tag's. */
  placedAtUtc: string | null;
}

export interface TagRemovalProposal {
  /** Entries that are in the set, in the tag's entry order. */
  rows: TagRemovalRow[];
  /** Entries not in the set — not listed, only counted. */
  notInSetCount: number;
  /** All of the tag's own placements (visible or not) with their read revision. */
  snapshot: TagPlacementSnapshotEntry[];
  /** Ids of the tag's own placements that are in the set; the flow derives the kept ids from it. */
  ownInLiveIds: string[];
}

export function proposeTagRemoval(
  entries: readonly EmoteTagEntry[],
  live: SevenTvSetEntries,
): TagRemovalProposal {
  const rows: TagRemovalRow[] = [];
  const snapshot: TagPlacementSnapshotEntry[] = [];
  const ownInLiveIds: string[] = [];
  let notInSetCount = 0;

  for (const entry of entries) {
    const id = entry.sevenTvEmoteId;
    const inLive = live.aliasesById.has(id) || live.aliaslessIds.has(id);
    if (entry.placedByThisTag && entry.placementOperationId !== null) {
      snapshot.push({ sevenTvEmoteId: id, placementOperationId: entry.placementOperationId });
    }
    if (!inLive) {
      notInSetCount++;
      continue;
    }
    if (entry.placedByThisTag) {
      ownInLiveIds.push(id);
    }
    rows.push(toRow(entry, live.aliasesById.get(id) ?? []));
  }

  return { rows, notInSetCount, snapshot, ownInLiveIds };
}

function toRow(entry: EmoteTagEntry, aliases: string[]): TagRemovalRow {
  const base = { sevenTvEmoteId: entry.sevenTvEmoteId, aliases: [...aliases] };
  if (entry.placedByThisTag) {
    const held = entry.heldByActiveTags.length > 0;
    return {
      ...base,
      checked: !held,
      reason: held ? 'heldBy' : 'placed',
      heldBy: held ? [...entry.heldByActiveTags] : [],
      placedAtUtc: entry.placedAtUtc,
    };
  }
  if (entry.placedByOtherTags.length > 0) {
    return {
      ...base,
      checked: false,
      reason: 'heldBy',
      heldBy: [...entry.placedByOtherTags],
      placedAtUtc: null,
    };
  }
  return { ...base, checked: false, reason: 'alreadyPresent', heldBy: [], placedAtUtc: null };
}
