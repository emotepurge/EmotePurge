import { RunItemStatus, RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { CsvColumn, toCsv } from './csv';
import { ExportEnvelope, ExportKind, buildEnvelope } from './export-envelope';
import { sanitizeFilenamePart } from './file-download';
import { readEnvelope } from './read-envelope';

/**
 * The purge-run protocol (A6): the paper trail of a mass delete, downloadable as JSON/CSV and
 * re-importable as a restore list. The third `kind` of the shared export envelope — and the reason
 * that envelope carries `source`/`kind`/`formatVersion`: an imported file is validated against
 * them instead of trusted. Deliberately contains no token of any kind, only emote ids and names.
 */

/**
 * The purge-run protocol's own row-shape version — deliberately **not** a bump of
 * `EXPORT_FORMAT_VERSION` (`export-envelope.ts`), which every envelope `kind` shares and
 * `import-source-parser.ts` pins its own, unrelated reads to `1` for; bumping it would have
 * version-gated exports this row-shape change never touched. Bumped here instead (spec #200, K5
 * finding C) because the *row* shape genuinely changed with K5 — `emoteId` went from always a Guid
 * to `string | null`, and every row gained `aliases: string[]` — and a reader that predates that
 * change must refuse a file in the new shape rather than parse it silently short: unaware of
 * either field, it would drop every `emoteId: null` row outright and, for a restored duplicate
 * cell, only re-add one of its two aliases — no error, just fewer restores than the file actually
 * recorded, discovered only by whoever later expected the rest to still be there.
 * `parsePurgeRunProtocol` keeps accepting `1` alongside this version, so no file already on
 * someone's disk from before this change stops being readable.
 */
export const PURGE_RUN_FORMAT_VERSION = 2;

export interface PurgeRunRow {
  /** Local `Emote.Id`, or `null` for a set-view row that never had one (spec #200, 7.2). Kept for
   *  the paper trail only — restoring reads the 7TV id. */
  emoteId: string | null;
  sevenTvEmoteId: string;
  name: string;
  /** Every alias the emote sat under in the set when it was removed — two for a #74 duplicate
   *  cell, whose one `REMOVE` took both entries. The restore sends one `ADD` per alias. A protocol
   *  written before this field existed is read as `[name]`. */
  aliases: string[];
  status: RunItemStatus;
  errorMessage: string | null;
}

/**
 * One row a restore run re-adds, whatever file it came from — the restore flow's input
 * (`startRestoreFlow`). A `PurgeRunRow` is one as it stands (every alias a non-empty string); a
 * transfer-run file's removed target (`parseTransferRunForRestore` in `transfer-run-export.ts`) is
 * the only source of a `null` alias, which stands for an entry without an alias: its `ADD` sends no
 * alias and 7TV names the entry by the emote's default name. The purge-run file itself never holds
 * a `null` — this type widens only the in-memory row.
 */
export interface RestoreRow {
  emoteId: string | null;
  sevenTvEmoteId: string;
  /** What the confirmation lists this row under: its first named alias, else its default name,
   *  else its 7TV id. */
  name: string;
  /** One `ADD` per entry — a string restores under that alias, `null` restores without one. */
  aliases: readonly (string | null)[];
  /** The emote's 7TV default name when the source file knows it (only a transfer-run file does),
   *  shown for the queue row of an entry without an alias. */
  defaultName?: string | null;
}

export interface PurgeRunMeta {
  emoteSetId: string;
  /** ISO timestamps of the run itself. */
  startedAt: string;
  finishedAt: string;
  counts: { requested: number; succeeded: number; failed: number; cancelled: number };
}

export type PurgeRunProtocol = ExportEnvelope<PurgeRunRow, PurgeRunMeta>;

export function buildPurgeRunProtocol(input: {
  channelName: string;
  emoteSetId: string;
  startedAt: number;
  finishedAt: number;
  // Every row of the run, unfiltered (spec #200, F3): a row without a local `emoteId` is written
  // with `emoteId: null`, never dropped — the protocol is the only way back from a deletion, and a
  // missing row would make that emote's removal irreversible without anyone noticing. `counts`
  // below is derived from the same list, so it cannot drift from `rows`.
  items: readonly RunQueueItem[];
}): PurgeRunProtocol {
  const statuses = input.items.map((item) => item.status);
  const envelope = buildEnvelope({
    kind: 'purge-run',
    channelName: input.channelName,
    withheld: [],
    meta: {
      emoteSetId: input.emoteSetId,
      startedAt: new Date(input.startedAt).toISOString(),
      finishedAt: new Date(input.finishedAt).toISOString(),
      counts: {
        requested: statuses.length,
        succeeded: statuses.filter((status) => status === 'done').length,
        failed: statuses.filter((status) => status === 'failed').length,
        cancelled: statuses.filter((status) => status === 'cancelled').length,
      },
    },
    rows: input.items.map((item) => ({
      emoteId: item.emoteId ?? null,
      sevenTvEmoteId: item.sevenTvEmoteId,
      name: item.name,
      aliases: item.aliases && item.aliases.length > 0 ? [...item.aliases] : [item.name],
      status: item.status,
      errorMessage: item.errorMessage ?? null,
    })),
  });
  // Overrides the shared envelope's own EXPORT_FORMAT_VERSION (still 1) — see
  // PURGE_RUN_FORMAT_VERSION's doc above for why this kind versions independently.
  return { ...envelope, formatVersion: PURGE_RUN_FORMAT_VERSION };
}

export function purgeRunJson(protocol: PurgeRunProtocol): string {
  return JSON.stringify(protocol, null, 2);
}

export function purgeRunCsv(protocol: PurgeRunProtocol): string {
  const columns: CsvColumn<PurgeRunRow>[] = [
    { header: 'name', value: (row) => row.name },
    { header: 'seven_tv_emote_id', value: (row) => row.sevenTvEmoteId },
    { header: 'status', value: (row) => row.status },
    { header: 'error_message', value: (row) => row.errorMessage },
  ];
  return toCsv(protocol.rows, columns);
}

export function purgeRunFilename(
  channelName: string,
  finishedAt: string,
  ext: 'csv' | 'json',
): string {
  // yyyy-mm-dd-HHmm of the run end — a channel can run several purges a day.
  const stamp = finishedAt.slice(0, 16).replace('T', '-').replace(':', '');
  return `emotepurge_${sanitizeFilenamePart(channelName)}_purge_${stamp}.${ext}`;
}

/**
 * The other envelope kinds this parser can name explicitly, mapped to an error that says what the
 * file actually is. A usage export used to need its own entry here, but since #72 it is itself an
 * importable source (see `import-source-parser`) — the file dispatch tries that parser first, so a
 * usage export reaching *this* function at all would be unexpected, and the generic `wrongKind` is
 * an honest enough answer for it. Voting exports have no import path anywhere, hence the one entry
 * left.
 */
const FOREIGN_KIND_ERROR_KEYS: Partial<Record<ExportKind, string>> = {
  voting: 'restore.import.errors.votingExport',
};

/** The set a restore file names as its target — read from the file's `meta` and nothing else
 *  (spec #253, 6.1/E1). Untrusted until `resolveEditableSet` has found it in the target list; the
 *  file step never hands it on unchecked. */
export interface RestoreFileTarget {
  emoteSetId: string;
}

export type ProtocolParseResult =
  | {
      ok: true;
      rows: PurgeRunRow[];
      meta: PurgeRunMeta;
      channelName: string;
      target: RestoreFileTarget;
    }
  /** `errorKey` is a Transloco key (restore.import.errors.*), never finished prose. */
  | { ok: false; errorKey: string };

/**
 * Reads an uploaded protocol and returns the set it names (`meta.emoteSetId`) as its `target` —
 * the file decides where a restore goes, never the page it is read on (spec #253, E1/E15). There
 * is no comparison against a channel or a set here any more: a protocol of a set that has since
 * stopped being active (after a set switch) is the normal case, not a mistake. Whether the caller
 * may write to that set is the file step's own target check (`resolveEditableSet`), not this
 * parser's. A protocol without `meta.emoteSetId` is `wrongKind` — every protocol this app wrote
 * carries one, and there is deliberately no fallback that would guess a set (F1). Returns only rows
 * with `status: 'done'`: a failed delete means the emote never left the set, and re-adding it would
 * at best be a no-op, at worst an alias collision.
 *
 * Reads every protocol this app has ever written (spec #200, AK 69): `emoteId` as a Guid (before
 * K5), `null` (a row without a local emote), or missing; `aliases` present, or missing — an older
 * file, whose one alias is its `name`. The returned rows are normalised to today's shape. Accepts
 * `formatVersion` `1` (every file written before K5) and `PURGE_RUN_FORMAT_VERSION` (today's row
 * shape) — anything else is refused rather than parsed short (see that constant's doc).
 */
export function parsePurgeRunProtocol(text: string): ProtocolParseResult {
  const read = readEnvelope(text);
  if (!read.ok) {
    return read;
  }

  // `read.envelope` is `ExportEnvelope<unknown>` — its `meta` is an untyped `Record<string,
  // unknown>`, which structurally shares nothing with `PurgeRunMeta`'s required fields, so TS
  // refuses the direct cast. Going through `unknown` says out loud what every check below already
  // does: treat this as unverified JSON from a file and validate each field by hand.
  const envelope = read.envelope as unknown as Partial<PurgeRunProtocol>;
  if (envelope.kind !== 'purge-run') {
    // Ours, but the wrong export — `kind` is untrusted input, so an unknown value falls back.
    const foreign = envelope.kind ? FOREIGN_KIND_ERROR_KEYS[envelope.kind] : undefined;
    return { ok: false, errorKey: foreign ?? 'restore.import.errors.wrongKind' };
  }
  if (envelope.formatVersion !== 1 && envelope.formatVersion !== PURGE_RUN_FORMAT_VERSION) {
    return { ok: false, errorKey: 'restore.import.errors.wrongVersion' };
  }
  // The envelope's channel is carried through for the caller's own use, never compared — but it
  // still has to be a string for the result type to be honest about it.
  if (typeof envelope.channelName !== 'string') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  const meta = envelope.meta;
  if (!meta || typeof meta.emoteSetId !== 'string' || meta.emoteSetId.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  if (!Array.isArray(envelope.rows)) {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }

  const restorable = (envelope.rows as unknown[]).flatMap((row) => {
    const parsed = readProtocolRow(row);
    return parsed && parsed.status === 'done' ? [parsed] : [];
  });
  if (restorable.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.noRestorableRows' };
  }

  return {
    ok: true,
    rows: restorable,
    meta,
    channelName: envelope.channelName,
    target: { emoteSetId: meta.emoteSetId },
  };
}

/** One row of an untrusted protocol file, or `null` when it cannot be restored from. The 7TV id
 *  and the name are required; `emoteId` may be a string, `null` or absent (all read as today's
 *  `string | null`); `aliases` falls back to `[name]` when absent or not a non-empty list of
 *  non-empty strings — a restore under the row's own name beats dropping the row, which would
 *  leave its deletion without a way back. */
function readProtocolRow(value: unknown): PurgeRunRow | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const row = value as Record<string, unknown>;
  const { emoteId, sevenTvEmoteId, name, aliases, status, errorMessage } = row;
  if (typeof sevenTvEmoteId !== 'string' || sevenTvEmoteId.length === 0) {
    return null;
  }
  if (typeof name !== 'string') {
    return null;
  }
  if (emoteId !== undefined && emoteId !== null && typeof emoteId !== 'string') {
    return null;
  }
  const readAliases =
    Array.isArray(aliases) &&
    aliases.length > 0 &&
    aliases.every((alias) => typeof alias === 'string' && alias.length > 0)
      ? [...new Set(aliases as string[])]
      : [name];
  return {
    emoteId: typeof emoteId === 'string' ? emoteId : null,
    sevenTvEmoteId,
    name,
    aliases: readAliases,
    status: status as RunItemStatus,
    errorMessage: typeof errorMessage === 'string' ? errorMessage : null,
  };
}
