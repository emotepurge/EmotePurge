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
