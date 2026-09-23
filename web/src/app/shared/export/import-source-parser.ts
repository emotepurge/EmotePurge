import {
  ImportOrigin,
  ImportRow,
  ImportSource,
  dedupeImportRows,
} from '../../core/seven-tv/import-source';
import { ExportEnvelope } from './export-envelope';

/**
 * Turns an already-read envelope (see `readEnvelope`) into an `ImportSource` (#72, K3) — the file
 * side of the import flow's two origins (the other being a channel's active set, built directly
 * from `EmoteListItem[]`). Accepts two envelope kinds: a dedicated `emote-list` export and, because
 * it already carries `sevenTvEmoteId` + `emoteName` per row, a `usage` export as well — a user who
 * downloaded their usage stats to keep a backup should not have to re-export just to copy from it.
 */
export type ParseImportSourceResult =
  { ok: true; source: ImportSource } | { ok: false; errorKey: string };

interface RawImportRow {
  sevenTvEmoteId?: unknown;
  name?: unknown;
  emoteName?: unknown;
}

export function parseImportSource(
  envelope: ExportEnvelope<unknown>,
  fileName: string,
): ParseImportSourceResult {
  const partial = envelope as Partial<ExportEnvelope<unknown>>;

  if (partial.kind === 'voting') {
    return { ok: false, errorKey: 'restore.import.errors.votingExport' };
  }
  // A transfer-run protocol (either stage, #230) is never an import source: its rows are 7TV
  // mutations already applied or about to be, not an emote list to copy from. Named explicitly
  // rather than falling into the generic wrongKind below, same reasoning as the voting export
  // above — the file dispatch tries `parsePurgeRunProtocol` first for `purge-run`, and is meant to
  // grow a dedicated restore branch for `transfer-run` too; this function only ever sees one of
  // these two kinds if that dispatch is bypassed, and answers with a reason even then.
  if (partial.kind === 'transfer-run') {
    return { ok: false, errorKey: 'restore.import.errors.transferRun' };
  }
  if (partial.kind !== 'emote-list' && partial.kind !== 'usage') {
    // Anything else — an unknown kind, or a purge-run protocol handed here by mistake (the file
    // dispatch decides which parser to call, so this should not happen, but `kind` is untrusted
    // input and deserves a named response rather than a crash either way).
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  if (partial.formatVersion !== 1) {
    return { ok: false, errorKey: 'restore.import.errors.wrongVersion' };
  }
  if (!Array.isArray(partial.rows)) {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }

  const rawRows = partial.rows;
  const nameField: keyof RawImportRow = partial.kind === 'usage' ? 'emoteName' : 'name';
  const validRows: ImportRow[] = [];
  for (const raw of rawRows) {
    const row = raw as RawImportRow | null | undefined;
    const id = row?.sevenTvEmoteId;
    const name = row?.[nameField];
    if (typeof id === 'string' && id.length > 0 && typeof name === 'string') {
      // A file never carries an image URL (`emote-list-export.ts` writes only id and name) — `null`
      // here is the honest answer, not a guess derived from the id (spec T1).
      validRows.push({ sevenTvEmoteId: id, name, imageUrl: null });
    }
  }

  // The row count the source claims to have, checked *before* dedup so a broken row and a
  // duplicate row are counted independently instead of one masking the other. Falls back to the
  // raw array length when `meta.rowCount` is absent or not a number.
  const meta = partial.meta as { rowCount?: unknown } | null | undefined;
  const expectedRowCount = typeof meta?.rowCount === 'number' ? meta.rowCount : rawRows.length;
  const discardedRows = expectedRowCount - validRows.length;

  const { rows, duplicatesCollapsed } = dedupeImportRows(validRows);
  if (rows.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.noRows' };
  }

  const origin: ImportOrigin = {
    kind: 'file',
    fileName,
    exportedAt: typeof partial.exportedAt === 'string' ? partial.exportedAt : null,
    channelName: typeof partial.channelName === 'string' ? partial.channelName : null,
    envelopeKind: partial.kind,
  };

  return { ok: true, source: { origin, rows, duplicatesCollapsed, discardedRows } };
}
