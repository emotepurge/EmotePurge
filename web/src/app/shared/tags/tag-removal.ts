import type { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import type {
  EmoteTagEntry,
  EmoteTagRef,
  TagPlacementSnapshotEntry,
} from '../../core/tags/emote-tag.model';

/*
 * The pure half of a tag clear-out (#201 T-C, spec 7.2/5): what the confirmation dialog proposes.
 *
 * - An id counts as in the set when `aliasesById.has(id) || aliaslessIds.has(id)` — also when it
 *   sits only under aliasless entries (spec 3.4): one REMOVE takes every entry of the id, so the
 *   emote is there to be removed even though the read cannot name it. No fallback to the entry's
 *   `inSet` flag (E28): only the live read decides.
 * - Every uncertainty tips towards "propose less": for a tag played in to the set, anything not
 *   clearly this tag's own, unheld placement starts unchecked, and the user can tick it either way.
 * - A tag that is **not** played in (never, or cleared out before) has no placement to tell its own
 *   emotes from ones that were there before (operator decision 2026-10-05: a tag of emotes that
 *   were all in the set already must still be clearable). Its whole point is the person's tagging,
 *   so every emote of it in the set is proposed — except one another active tag still needs
 *   (`heldByActiveTags`, or a valid placement of another tag), which stays unchecked with that
 *   reason. "Not played in by this tag" is a statement about a play-in and does not apply.
 * - The snapshot carries **all** of the tag's own placements, visible in the set or not (6.4/3): the
 *   ones no longer in the set are not rows, but they belong to the report so the server can drop
 *   them, and each carries the revision it was read at so the server touches only that placement.
 * - `aliases` come straight from the live read (empty for an aliasless-only id). The queue the run
 *   deletes from is built later by `toDeleteQueueEmotes`, which adds the aliasless fallback — this
 *   file has no alias rule of its own.
 */
/** `placed`: this tag's own placement. `heldBy`: another active tag still needs it. `alreadyPresent`:
 *  in the set before this (played-in) tag's play-in. `tagged`: an emote of a tag that is not played
 *  in — proposed for its tagging alone, with no further line. */
export type TagRemovalReason = 'placed' | 'heldBy' | 'alreadyPresent' | 'tagged';

export interface TagRemovalRow {
  sevenTvEmoteId: string;
  aliases: string[];
  /**
   * What the row is called, never empty. Rule: the entry's current alias in the live set (the
   * first, if several); else the entry's current name, else the alias it was tagged under; else the
   * set's default name for the id (`live.defaultNameById`); else the id itself.
   */
  displayName: string;
  /** The entry's image (snapshot from tagging); `null` when it has none — the empty plate. */
  imageUrl: string | null;
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
  /** Whether the entry read found the tag played in to the set — the dialog words n = 0 by it. */
  tagActive: boolean;
}

/** `tagActive`: the entry read found an activation of the tag in the set (`activationOperationId
 *  !== null`) — the same read the snapshot comes from, never the page's older summary. */
export function proposeTagRemoval(
  entries: readonly EmoteTagEntry[],
  live: SevenTvSetEntries,
  tagActive: boolean,
): TagRemovalProposal {
  const rows: TagRemovalRow[] = [];
  const snapshot: TagPlacementSnapshotEntry[] = [];
  const ownInLiveIds: string[] = [];
  let notInSetCount = 0;

  for (const entry of entries) {
    const id = entry.sevenTvEmoteId;
    const inLive = live.aliasesById.has(id) || live.aliaslessIds.has(id);
    // An own placement without a revision counts as not own — in the snapshot, in `ownInLiveIds` and
    // in the row alike, so the lists agree. The server only reports valid placements, which always
    // carry a revision, so this is unreachable by contract; it fails safe (proposes less).
    const isOwn = entry.placedByThisTag && entry.placementOperationId !== null;
    if (isOwn) {
      snapshot.push({ sevenTvEmoteId: id, placementOperationId: entry.placementOperationId! });
    }
    if (!inLive) {
      notInSetCount++;
      continue;
    }
    if (isOwn) {
      ownInLiveIds.push(id);
    }
    rows.push(toRow(entry, isOwn, tagActive, live));
  }

  return { rows, notInSetCount, snapshot, ownInLiveIds, tagActive };
}

function toRow(
  entry: EmoteTagEntry,
  isOwn: boolean,
  tagActive: boolean,
  live: SevenTvSetEntries,
): TagRemovalRow {
  const id = entry.sevenTvEmoteId;
  const aliases = live.aliasesById.get(id) ?? [];
  const displayName =
    aliases[0] || entry.currentName || entry.alias || live.defaultNameById.get(id) || id;
  const base = {
    sevenTvEmoteId: id,
    aliases: [...aliases],
    displayName,
    imageUrl: entry.imageUrl || null,
  };
  if (isOwn) {
    const holders = mergeRefs(entry.heldByActiveTags, entry.placedByOtherTags);
    const held = holders.length > 0;
    return {
      ...base,
      checked: !held,
      reason: held ? 'heldBy' : 'placed',
      heldBy: holders,
      placedAtUtc: entry.placedAtUtc,
    };
  }
  if (!tagActive) {
    // Every other tag with a valid placement is active (inactive => no placement) and has an entry
    // (the foreign key), so it is among `heldByActiveTags`; merged anyway, so a read that broke that
    // rule still withholds the tick (propose less).
    const heldBy = mergeRefs(entry.heldByActiveTags, entry.placedByOtherTags);
    return heldBy.length > 0
      ? { ...base, checked: false, reason: 'heldBy', heldBy, placedAtUtc: null }
      : { ...base, checked: true, reason: 'tagged', heldBy: [], placedAtUtc: null };
  }
  // Played in: an emote another active tag needs through an entry alone (no placement) is held too,
  // so the dialog names that tag — the re-read treats every shown holder as known.
  const holders = mergeRefs(entry.heldByActiveTags, entry.placedByOtherTags);
  if (holders.length > 0) {
    return { ...base, checked: false, reason: 'heldBy', heldBy: holders, placedAtUtc: null };
  }
  return { ...base, checked: false, reason: 'alreadyPresent', heldBy: [], placedAtUtc: null };
}

/** `first` in its order, then whatever of `second` it lacks (by id). */
function mergeRefs(first: readonly EmoteTagRef[], second: readonly EmoteTagRef[]): EmoteTagRef[] {
  const ids = new Set(first.map((ref) => ref.id));
  return [...first, ...second.filter((ref) => !ids.has(ref.id))];
}
