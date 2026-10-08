import type { ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import type { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import type { EmoteTagEntry } from '../../core/tags/emote-tag.model';

/*
 * The pure half of a tag play-in (#201 T-C, spec 7.1/4): which of a tag's entries still have to be
 * added to the set, and which the set already holds.
 *
 * "Is in the set" asks the live read — `aliasesById.has(id) || aliaslessIds.has(id)` — and counts
 * an id that sits **only** under aliasless entries as present (spec 3.4): 7TV does not dedupe ADDs
 * by id, so an ADD for an id that is there under any entry, named or not, would become a second
 * entry that a later REMOVE takes along with the first. There is deliberately **no fallback** to
 * the server's `inSet` flag on the entry (E28): that flag comes from the database, which can lag
 * the live set, and a half-known list must never decide what gets written. An entry the live read
 * does not show is added, whatever `inSet` says. Ids compare ordinally (plain string equality).
 */
export interface TagPlayInPartition {
  /** Entries not in the set, in the tag's entry order, shaped as the import's rows. */
  toAdd: ImportRow[];
  /** Ids already in the set (under any alias), in entry order — they get no placement. */
  alreadyInSetIds: string[];
}

export function partitionTagPlayIn(
  entries: readonly EmoteTagEntry[],
  live: SevenTvSetEntries,
): TagPlayInPartition {
  const toAdd: ImportRow[] = [];
  const alreadyInSetIds: string[] = [];
  for (const entry of entries) {
    const id = entry.sevenTvEmoteId;
    if (live.aliasesById.has(id) || live.aliaslessIds.has(id)) {
      alreadyInSetIds.push(id);
    } else {
      toAdd.push({ sevenTvEmoteId: id, name: entry.alias, imageUrl: entry.imageUrl });
    }
  }
  return { toAdd, alreadyInSetIds };
}

/**
 * The entries a play-in considers (operator decision 2026-10-05, mirroring the clear-out): with a
 * grid marking only the marked ones, in the tag's entry order; without one (empty or absent) all of
 * them. Which of those still have to be added is the live read's call (`partitionTagPlayIn`), never
 * the page's `inSet` flag the marking was counted against. A marked id the tag no longer has an entry
 * for drops out.
 */
export function playInCandidates(
  entries: readonly EmoteTagEntry[],
  markedIds: readonly string[] | undefined,
): readonly EmoteTagEntry[] {
  if (markedIds === undefined || markedIds.length === 0) {
    return entries;
  }
  const marked = new Set(markedIds);
  return entries.filter((entry) => marked.has(entry.sevenTvEmoteId));
}

/**
 * How many of the marked entries the host page shows as not in the set — the number "Ins Set holen"
 * carries with a marking. Read off the page's own entry list (`inSet`), so it is a label, not a
 * decision: what is added is the live read's call. An entry with no known state (`inSet: null`) is not
 * known to be present and counts as missing, as the tag-level gate does with `inSetCount: null`.
 */
export function markedMissingCount(
  entries: readonly EmoteTagEntry[],
  markedIds: readonly string[],
): number {
  if (markedIds.length === 0) {
    return 0;
  }
  const marked = new Set(markedIds);
  return entries.filter((entry) => marked.has(entry.sevenTvEmoteId) && entry.inSet !== true).length;
}

/**
 * How many of the marked entries the host page shows as in the set — the number "Aus dem Set
 * entfernen" carries with a marking, the mirror of {@link markedMissingCount}. A label, not a
 * decision: the clear-out proposes what the live read finds in the set. An entry with no known
 * state (`inSet: null`) is not known to be present and does not count.
 */
export function markedInSetCount(
  entries: readonly EmoteTagEntry[],
  markedIds: readonly string[],
): number {
  if (markedIds.length === 0) {
    return 0;
  }
  const marked = new Set(markedIds);
  return entries.filter((entry) => marked.has(entry.sevenTvEmoteId) && entry.inSet === true).length;
}

/** The import source of a tag play-in: the rows to add, tagged with the `tag` origin. Nothing is
 *  collapsed or discarded here — a tag's entries are unique by id already. */
export function buildTagImportSource(
  partition: TagPlayInPartition,
  tag: { id: number; name: string },
  channelName: string,
): ImportSource {
  return {
    origin: {
      kind: 'tag',
      tagId: tag.id,
      tagName: tag.name,
      channelName,
      alreadyInSetCount: partition.alreadyInSetIds.length,
    },
    rows: partition.toAdd,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}
