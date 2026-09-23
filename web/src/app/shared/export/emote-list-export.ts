import { ImportRow } from '../../core/seven-tv/import-source';
import { ExportEnvelope, buildEnvelope } from './export-envelope';
import { ExportScope } from './export-dialog';
import { sanitizeFilenamePart } from './file-download';

/**
 * The fourth envelope kind (#72, K3): a plain list of emotes to copy into another channel's set,
 * downloadable from the usage-stats grid and re-importable through the same file path as the
 * purge-run protocol and a usage export. Deliberately thin — unlike `PurgeRunRow` it carries no
 * `emoteId` (Regel 8: that guid is internal to *this* channel's rows and means nothing to whatever
 * channel the file is later imported into) and no usage figures, only the identity a 7TV ADD
 * mutation needs.
 */

// Same three fields as `ImportRow`: since #230 the written file carries the emote's image URL
// alongside id and name, taken from the same source the rows came from (`EmoteListItem.imageUrl`
// for the channel grid, `EmoteUsageTotal.imageUrl` for the usage-export purpose that builds this
// envelope too) — never derived from `sevenTvEmoteId`. `null` only for a row whose source itself
// had none.
export type EmoteListRow = Pick<ImportRow, 'sevenTvEmoteId' | 'name' | 'imageUrl'>;

export interface EmoteListMeta {
  sourceEmoteSetId: string;
  rowCount: number;
  scope: ExportScope;
}

export type EmoteListProtocol = ExportEnvelope<EmoteListRow, EmoteListMeta>;

export function buildEmoteListEnvelope(input: {
  channelName: string;
  emoteSetId: string;
  scope: ExportScope;
  rows: ImportRow[];
}): EmoteListProtocol {
  return buildEnvelope({
    kind: 'emote-list',
    channelName: input.channelName,
    withheld: [],
    meta: {
      sourceEmoteSetId: input.emoteSetId,
      rowCount: input.rows.length,
      scope: input.scope,
    },
    rows: input.rows.map((row) => ({
      sevenTvEmoteId: row.sevenTvEmoteId,
      name: row.name,
      imageUrl: row.imageUrl,
    })),
  });
}

export function emoteListJson(envelope: EmoteListProtocol): string {
  return JSON.stringify(envelope, null, 2);
}

export function emoteListFilename(channelName: string, exportedAt: string): string {
  // yyyy-mm-dd of the export — unlike the purge protocol, an emote list is not produced several
  // times a day, and the browser numbers filename collisions on its own anyway.
  const stamp = exportedAt.slice(0, 10);
  return `emotepurge_${sanitizeFilenamePart(channelName)}_emote-list_${stamp}.json`;
}
