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
 * - **A marking on the tags page grid takes precedence** (operator decision 2026-10-05): with at least
 *   one marked entry, the proposal is the marking — every marked emote in the set is proposed, own
 *   placement or not, played in or not — and every unmarked one is not (`notMarked`). One another
 *   active tag still needs stays unticked with that reason either way: `heldBy` wins over
 *   `notMarked` too, because the confirm-time re-read treats every holder the dialog showed as known,
 *   so a holder must never hide behind another reason. A marked id that is not in the set is no row,
 *   like any other entry not in the set.
 * - The snapshot carries **all** of the tag's own placements, visible in the set or not (6.4/3): the
 *   ones no longer in the set are not rows, but they belong to the report so the server can drop
 *   them, and each carries the revision it was read at so the server touches only that placement.
 * - `aliases` come straight from the live read (empty for an aliasless-only id). The queue the run
 *   deletes from is built later by `toDeleteQueueEmotes`, which adds the aliasless fallback — this
 *   file has no alias rule of its own.
 */
/** `placed`: this tag's own placement. `heldBy`: another active tag still needs it. `alreadyPresent`:
 *  in the set before this (played-in) tag's play-in. `tagged`: an emote of a tag that is not played
 *  in — proposed for its tagging alone, with no further line. `notMarked`: the grid had a marking and
 *  this emote was not part of it. */
export type TagRemovalReason = 'placed' | 'heldBy' | 'alreadyPresent' | 'tagged' | 'notMarked';

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
  /** Whether the proposal follows a grid marking — the dialog's sentence above the list says so. */
  fromMarking: boolean;
  /** How many rows are marked (marked entries in the live set) — 0 without a marking. The dialog's
   *  sentence compares it with the proposed count: a held marked row is a row, but not proposed. */
  markedInSetCount: number;
}

/** `tagActive`: the entry read found an activation of the tag in the set (`activationOperationId
 *  !== null`) — the same read the snapshot comes from, never the page's older summary. `markedIds`:
 *  the grid's marking as it stood at the click; empty (or absent) means there was none. */
export function proposeTagRemoval(
  entries: readonly EmoteTagEntry[],
  live: SevenTvSetEntries,
  tagActive: boolean,
  markedIds: readonly string[] = [],
): TagRemovalProposal {
  const marked = markedIds.length > 0 ? new Set(markedIds) : null;
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
    rows.push(toRow(entry, isOwn, tagActive, marked, live));
  }

  const markedInSetCount =
    marked === null ? 0 : rows.filter((row) => marked.has(row.sevenTvEmoteId)).length;
  return {
    rows,
    notInSetCount,
    snapshot,
    ownInLiveIds,
    tagActive,
    fromMarking: marked !== null,
    markedInSetCount,
  };
}

function toRow(
  entry: EmoteTagEntry,
  isOwn: boolean,
  tagActive: boolean,
  marked: ReadonlySet<string> | null,
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
    placedAtUtc: isOwn ? entry.placedAtUtc : null,
  };
  // Held comes first on every path, marked or not: an emote another active tag needs (through a
  // placement or an entry alone) stays unticked and names that tag — the re-read treats every shown
  // holder as known. Every other tag with a valid placement is active (inactive => no placement) and
  // has an entry (the foreign key), so it is among `heldByActiveTags`; merged anyway, so a read that
  // broke that rule still withholds the tick (propose less).
  const holders = mergeRefs(entry.heldByActiveTags, entry.placedByOtherTags);
  if (holders.length > 0) {
    return { ...base, checked: false, reason: 'heldBy', heldBy: holders };
  }
  if (marked !== null && !marked.has(id)) {
    return { ...base, checked: false, reason: 'notMarked', heldBy: [] };
  }
  if (isOwn) {
    return { ...base, checked: true, reason: 'placed', heldBy: [] };
  }
  // A played-in tag proposes only its own placements, unless the person marked this one; the fact
  // that the tag did not play it in stays on the row either way. A tag that is not played in
  // proposes it for its tagging alone.
  return tagActive
    ? { ...base, checked: marked !== null, reason: 'alreadyPresent', heldBy: [] }
    : { ...base, checked: true, reason: 'tagged', heldBy: [] };
}

/** `first` in its order, then whatever of `second` it lacks (by id). */
function mergeRefs(first: readonly EmoteTagRef[], second: readonly EmoteTagRef[]): EmoteTagRef[] {
  const ids = new Set(first.map((ref) => ref.id));
  return [...first, ...second.filter((ref) => !ids.has(ref.id))];
}
