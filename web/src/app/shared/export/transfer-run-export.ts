import { ImportOrigin } from '../../core/seven-tv/import-source';
import { isLeaderboardSort } from '../../core/seven-tv/leaderboard.model';
import { ImportRunItem } from '../../core/seven-tv/seven-tv-import.service';
import { RunItemStatus } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { TransferPlan, TransferRow, TransferRowTarget } from '../../core/seven-tv/transfer-plan';
import { CsvColumn, toCsv } from './csv';
import { ExportEnvelope, buildEnvelope } from './export-envelope';
import { sanitizeFilenamePart } from './file-download';
import { RestoreFileTarget, RestoreRow } from './purge-run-export';
import { readEnvelope } from './read-envelope';

/**
 * The transfer-run protocol (#230): the paper trail of an import run that carries a `replace`
 * row — one envelope kind, two stages (`meta.stage`). `planned` is the back-out file, written from a
 * *live* read of the target set right before the first `REMOVE`; `finished` is the result protocol,
 * written after the run settles. Both name only *target* entries a `replace` row touches — never a
 * source `add`/`renameSource`/`adoptSourceName` row, which nothing removes.
 *
 * Deliberately its own row-shape version (`TRANSFER_RUN_FORMAT_VERSION`), independent of
 * `EXPORT_FORMAT_VERSION` — same reasoning as `PurgeRunProtocol`'s own version: the *row* shape is
 * this kind's own contract, and a shared envelope version would tie unrelated `kind`s together.
 *
 * Explicitly **not** an import source (`import-source-parser.ts` rejects `kind === 'transfer-run'`
 * by name) — its rows are 7TV mutations already applied or about to be, not an emote list to copy
 * from. Both stages *are* restore sources: `parseTransferRunForRestore` below turns the removed
 * target entries back into restore rows, read through the same file step as a purge-run protocol.
 *
 * Both stages are also an undo source (#254): `parseTransferRunForUndo` below turns a `replace`
 * row's *source* — the emote this file's own run removed from the set — into an {@link UndoCandidate},
 * the mirror image of the restore rows above.
 */
export const TRANSFER_RUN_FORMAT_VERSION = 1;

/** One entry a replace row's target id held — `{ alias: string | null }`, `null` for the one
 *  aliasless entry an id can carry alongside its named ones (K5, spec §37/§38). This is what tells a
 *  restore reader that entry came back without an alias (7TV falls back to the emote's own default
 *  name), the same rule `ADD_EMOTE_MUTATION`'s own comment in `seven-tv-import.service.ts` states. */
export interface TransferRunRemovedTargetEntry {
  alias: string | null;
}

/** What one `replace` row's REMOVE touches — present only on a `replace` row, `null` on every other
 *  action (`add`, `renameSource`, `adoptSourceName` remove nothing). */
export interface TransferRunRemovedTarget {
  sevenTvEmoteId: string;
  /** Every entry the target id held — one per named alias, plus one `{ alias: null }` when the id
   *  also carried an aliasless entry. What a restore re-adds, one `ADD` per entry. */
  entries: TransferRunRemovedTargetEntry[];
  /** The named entries alone, for the CSV reader (a `|`-joined single cell there). */
  aliases: string[];
  /** The target's 7TV default name, or `null` while unknown — the only name an aliasless entry has
   *  (see `TransferRowTarget.defaultName`'s own doc for why). */
  defaultName: string | null;
  /** Whether 7TV confirmed the REMOVE — directly, or through the one re-read after a lost answer —
   *  independent of the row's own final status (an `unknown` ADD whose REMOVE went through is still
   *  `confirmed: true`). Always `false` in the `planned` stage: nothing has run yet. */
  confirmed: boolean;
}

export interface TransferRunRow {
  action: TransferRow['action'];
  /** The source row's own name, even when `alias` differs from it (a `renameSource` row) — without
   *  it a rename cannot be reconstructed from the file alone. */
  sourceName: string;
  /** The alias the row's `ADD`/`UPDATE` writes (or would write, in the `planned` stage). */
  alias: string;
  sevenTvEmoteId: string;
  /** `'pending'` for every row in the `planned` stage; the row's real outcome in `finished`. */
  status: RunItemStatus;
  failedStep: number | null;
  /** `null` in `planned`; 7TV's own raw text in `finished` (`sevenTvErrorMessage ?? errorMessage`
   *  — the same rule the run-progress panel and the dock use to show 7TV's own words). */
  errorMessage: string | null;
  /** Only ever set on a `replace` row. */
  removedTarget: TransferRunRemovedTarget | null;
}

interface TransferRunMetaBase {
  targetEmoteSetId: string;
  /** `null` for an untracked target (spec 8.6) — the envelope's own `channelName` carries `''` in
   *  that case, but this field keeps the honest `null`. */
  targetChannelName: string | null;
  targetOwnerDisplayName: string | null;
  origin: ImportOrigin;
}

export interface TransferRunCountsPlanned {
  /** Every row the plan will run — add, renameSource, replace and adoptSourceName rows alike. */
  planned: number;
  /** The `replace` rows among them — the REMOVEs this run is about to issue. */
  removals: number;
}

export interface TransferRunCountsFinished {
  requested: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  /** Confirmed REMOVEs (`removedTarget.confirmed`), whatever each row's own final status — mirrors
   *  `ImportRunInfo.removedCount`. */
  removed: number;
  unknown: number;
}

export interface TransferRunMetaPlanned extends TransferRunMetaBase {
  stage: 'planned';
  /** ISO timestamp of the live read this file was built from. */
  verifiedAt: string;
  counts: TransferRunCountsPlanned;
}

export interface TransferRunMetaFinished extends TransferRunMetaBase {
  stage: 'finished';
  startedAt: string;
  finishedAt: string;
  counts: TransferRunCountsFinished;
}

export type TransferRunMeta = TransferRunMetaPlanned | TransferRunMetaFinished;

/** The `planned` stage — the back-out file, built from a live read (`buildTransferPlanRecord`). */
export type TransferPlanRecord = ExportEnvelope<TransferRunRow, TransferRunMetaPlanned>;

/** The `finished` stage — the result protocol, built from a settled run (`buildTransferRunProtocol`). */
export type TransferRunProtocol = ExportEnvelope<TransferRunRow, TransferRunMetaFinished>;

/** A `TransferRowTarget`-shaped view of one target id as the live read (`SevenTvSetEntries`) sees
 *  it right now — the `planned` stage's own source of truth: from the *read*, not from the preview
 *  the row was decided against. */
function liveRowTarget(
  targetId: string,
  entries: SevenTvSetEntries,
  defaultNameById: ReadonlyMap<string, string>,
): TransferRowTarget {
  return {
    sevenTvEmoteId: targetId,
    aliases: [...(entries.aliasesById.get(targetId) ?? [])],
    hasAliaslessEntry: entries.aliaslessIds.has(targetId),
    defaultName: defaultNameById.get(targetId) ?? null,
  };
}

function entriesFromTarget(target: TransferRowTarget): TransferRunRemovedTargetEntry[] {
  return [
    ...target.aliases.map((alias): TransferRunRemovedTargetEntry => ({ alias })),
    ...(target.hasAliaslessEntry ? [{ alias: null }] : []),
  ];
}

function removedTargetFromRowTarget(
  target: TransferRowTarget,
  confirmed: boolean,
): TransferRunRemovedTarget {
  return {
    sevenTvEmoteId: target.sevenTvEmoteId,
    entries: entriesFromTarget(target),
    aliases: [...target.aliases],
    defaultName: target.defaultName,
    confirmed,
  };
}

function transferRunRowFromPlanRow(
  row: TransferRow,
  entries: SevenTvSetEntries,
  defaultNameById: ReadonlyMap<string, string>,
): TransferRunRow {
  return {
    action: row.action,
    sourceName: row.source.name,
    alias: row.alias,
    sevenTvEmoteId: row.source.sevenTvEmoteId,
    status: 'pending',
    failedStep: null,
    errorMessage: null,
    removedTarget:
      row.action === 'replace'
        ? removedTargetFromRowTarget(
            liveRowTarget(row.target.sevenTvEmoteId, entries, defaultNameById),
            false,
          )
        : null,
  };
}

function transferRunRowFromRunItem(item: ImportRunItem): TransferRunRow {
  const row = item.transfer;
  return {
    action: row.action,
    sourceName: row.source.name,
    alias: row.alias,
    sevenTvEmoteId: row.source.sevenTvEmoteId,
    status: item.status,
    failedStep: item.failedStep,
    errorMessage: item.sevenTvErrorMessage ?? item.errorMessage ?? null,
    removedTarget:
      row.action === 'replace'
        ? removedTargetFromRowTarget(row.target, item.completedSteps >= 1)
        : null,
  };
}

/**
 * The `planned` stage: every row of `plan`, each `replace` row's target read fresh from `entries`
 * (the live set read, not the preview the row was decided against) and named by
 * `defaultNameById` (`seven-tv-set-entries.ts`'s own field). Every row `status: 'pending'`,
 * `failedStep: null`, `errorMessage: null` — nothing has run yet.
 */
export function buildTransferPlanRecord(input: {
  targetEmoteSetId: string;
  targetChannelName: string | null;
  targetOwnerDisplayName: string | null;
  origin: ImportOrigin;
  /** Epoch ms of the live read this file is built from. */
  verifiedAt: number;
  plan: TransferPlan;
  entries: SevenTvSetEntries;
  defaultNameById: ReadonlyMap<string, string>;
}): TransferPlanRecord {
  const removals = input.plan.rows.filter((row) => row.action === 'replace').length;
  const envelope = buildEnvelope<TransferRunRow, TransferRunMetaPlanned>({
    kind: 'transfer-run',
    channelName: input.targetChannelName ?? '',
    withheld: [],
    meta: {
      stage: 'planned',
      targetEmoteSetId: input.targetEmoteSetId,
      targetChannelName: input.targetChannelName,
      targetOwnerDisplayName: input.targetOwnerDisplayName,
      origin: input.origin,
      verifiedAt: new Date(input.verifiedAt).toISOString(),
      counts: { planned: input.plan.rows.length, removals },
    },
    rows: input.plan.rows.map((row) =>
      transferRunRowFromPlanRow(row, input.entries, input.defaultNameById),
    ),
  });
  return { ...envelope, formatVersion: TRANSFER_RUN_FORMAT_VERSION };
}

/**
 * The `finished` stage: every row of the settled run, **unfiltered** (F3 — `failed`, `cancelled` and
 * `unknown` rows included, same rule the purge-run protocol follows). A `replace` row's
 * `removedTarget` comes from its own plan row's frozen target (`item.transfer.target`), not a fresh
 * read — there is none at this point — and `confirmed` is `completedSteps >= 1`, independent of the
 * row's own final status: an `unknown` ADD whose REMOVE went through still counts as a confirmed
 * removal.
 */
export function buildTransferRunProtocol(input: {
  targetEmoteSetId: string;
  targetChannelName: string | null;
  targetOwnerDisplayName: string | null;
  origin: ImportOrigin;
  startedAt: number;
  finishedAt: number;
  items: readonly ImportRunItem[];
}): TransferRunProtocol {
  const rows = input.items.map(transferRunRowFromRunItem);
  const statuses = input.items.map((item) => item.status);
  const removed = rows.filter((row) => row.removedTarget?.confirmed === true).length;
  const unknown = statuses.filter((status) => status === 'unknown').length;
  const envelope = buildEnvelope<TransferRunRow, TransferRunMetaFinished>({
    kind: 'transfer-run',
    channelName: input.targetChannelName ?? '',
    withheld: [],
    meta: {
      stage: 'finished',
      targetEmoteSetId: input.targetEmoteSetId,
      targetChannelName: input.targetChannelName,
      targetOwnerDisplayName: input.targetOwnerDisplayName,
      origin: input.origin,
      startedAt: new Date(input.startedAt).toISOString(),
      finishedAt: new Date(input.finishedAt).toISOString(),
      counts: {
        requested: statuses.length,
        succeeded: statuses.filter((status) => status === 'done').length,
        failed: statuses.filter((status) => status === 'failed').length,
        cancelled: statuses.filter((status) => status === 'cancelled').length,
        removed,
        unknown,
      },
    },
    rows,
  });
  return { ...envelope, formatVersion: TRANSFER_RUN_FORMAT_VERSION };
}

export function transferRunJson(protocol: TransferPlanRecord | TransferRunProtocol): string {
  return JSON.stringify(protocol, null, 2);
}

/** CSV only exists for the `finished` stage (the `planned` back-out file is JSON-only — one click,
 *  one file, nothing to choose). */
export function transferRunCsv(protocol: TransferRunProtocol): string {
  const columns: CsvColumn<TransferRunRow>[] = [
    { header: 'action', value: (row) => row.action },
    { header: 'source_name', value: (row) => row.sourceName },
    { header: 'alias', value: (row) => row.alias },
    { header: 'seven_tv_emote_id', value: (row) => row.sevenTvEmoteId },
    { header: 'status', value: (row) => row.status },
    { header: 'failed_step', value: (row) => row.failedStep },
    { header: 'error_message', value: (row) => row.errorMessage },
    {
      header: 'removed_seven_tv_emote_id',
      value: (row) => row.removedTarget?.sevenTvEmoteId ?? null,
    },
    {
      header: 'removed_aliases',
      value: (row) => (row.removedTarget ? row.removedTarget.aliases.join('|') : null),
    },
    {
      header: 'removed_aliasless_entry',
      value: (row) =>
        row.removedTarget
          ? String(row.removedTarget.entries.some((entry) => entry.alias === null))
          : null,
    },
    {
      header: 'removed_confirmed',
      value: (row) => (row.removedTarget ? String(row.removedTarget.confirmed) : null),
    },
  ];
  return toCsv(protocol.rows, columns);
}

/** The `planned` stage's filename — always JSON, and its own `-transfer-plan-` suffix so it never
 *  overwrites the `finished` stage's file of the same run when both sit in the same download
 *  folder. `channelOrSetLabel` is the target channel name, or the target set id for an untracked
 *  target (an untracked target without an owner display name still names the file by its set id).
 */
export function transferPlanFilename(channelOrSetLabel: string, verifiedAt: string): string {
  const stamp = verifiedAt.slice(0, 16).replace('T', '-').replace(':', '');
  return `emotepurge_${sanitizeFilenamePart(channelOrSetLabel)}_transfer-plan_${stamp}.json`;
}

/** The `finished` stage's filename — same `channelOrSetLabel` convention as `transferPlanFilename`. */
export function transferRunFilename(
  channelOrSetLabel: string,
  finishedAt: string,
  ext: 'csv' | 'json',
): string {
  const stamp = finishedAt.slice(0, 16).replace('T', '-').replace(':', '');
  return `emotepurge_${sanitizeFilenamePart(channelOrSetLabel)}_transfer_${stamp}.${ext}`;
}

/**
 * One target entry as an undo candidate names it — `alias: null` for the one entry that came back
 * without one, same shape as a purge/transfer-run restore row's own entries.
 */
export interface UndoCandidateTargetEntry {
  alias: string | null;
}

/**
 * One `replace` row's REMOVE, read back out as something a #254 undo might reverse: the source
 * emote to bring back, and the target entries it once held. `provenance` says how much trust the
 * *file itself* — as opposed to a live re-check — puts in the row (F17, Codex-Befund 2): a
 * `finished` file only ever names a row 7TV actually confirmed the REMOVE for, so its candidates are
 * `'confirmed'`; a `planned` file is written *before* the first REMOVE and proves nothing about
 * whether its run ever started, so every one of its candidates is `'unproven'` until a human
 * confirms the source shown really is the one that vanished (spec 17 K1/K2, the undo confirm
 * dialog's checkbox). Classification (#254 T2) reads this through unchanged; it is not something
 * the live check can derive.
 */
export interface UndoCandidate {
  sourceSevenTvEmoteId: string;
  sourceName: string;
  /** The alias the row's ADD wrote onto the target — the name a REMOVE-then-ADD undo pair moves
   *  back to the source. */
  alias: string;
  fileStatus: RunItemStatus | 'pending';
  target: {
    sevenTvEmoteId: string;
    entries: UndoCandidateTargetEntry[];
    defaultName: string | null;
  };
  provenance: 'confirmed' | 'unproven';
}

/**
 * Where an undo candidate's own transfer-run file came from — carried into the `transfer-undo`
 * file's `meta.undoneFile` (F6) so a human reading the undo's paper trail can follow the chain back
 * to the transfer it reverses, the same way `TransferRunRow.sourceName` lets a restore reconstruct a
 * rename. `verifiedAt`/`finishedAt` mirror whichever of `TransferRunMetaPlanned`/`Finished` the file
 * actually was — only one of the two is ever non-null. `origin` is `null` when the file's own
 * `meta.origin` is missing or not a recognizable {@link ImportOrigin} — untrusted JSON from a file,
 * validated rather than cast (see {@link readImportOrigin}), so a corrupted or hand-edited field
 * shows up here as an honest "unknown" instead of throwing later wherever this gets displayed or
 * re-serialized.
 */
export interface UndoSourceFileInfo {
  stage: TransferRunMeta['stage'];
  exportedAt: string;
  verifiedAt: string | null;
  finishedAt: string | null;
  origin: ImportOrigin | null;
}

/** Every {@link RunItemStatus} value, for validating an untrusted `status` field rather than casting
 *  it — a file can claim any string there. */
const KNOWN_RUN_ITEM_STATUSES: ReadonlySet<string> = new Set<RunItemStatus>([
  'pending',
  'in-progress',
  'done',
  'failed',
  'cancelled',
  'unknown',
]);

/**
 * Validates an untrusted `meta.origin` value as an {@link ImportOrigin} — there is no existing
 * general-purpose reader for it (the one other place that builds one, `import-source-parser.ts`,
 * only ever constructs a `kind: 'file'` origin from fields it already trusts). `null` for anything
 * that is not an object, has an unrecognized `kind`, or is missing a field its `kind` requires —
 * fail closed rather than pass a shape-mismatched value on to a caller that reads it as if it were
 * one of the four real variants.
 */
function readImportOrigin(value: unknown): ImportOrigin | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const origin = value as Record<string, unknown>;
  const kind = origin['kind'];
  switch (kind) {
    case 'channel':
    case 'seventv-channel':
      return typeof origin['channelName'] === 'string'
        ? { kind, channelName: origin['channelName'] }
        : null;
    case 'file': {
      const fileName = origin['fileName'];
      const exportedAt = origin['exportedAt'];
      const channelName = origin['channelName'];
      const envelopeKind = origin['envelopeKind'];
      if (typeof fileName !== 'string') {
        return null;
      }
      if (exportedAt !== null && typeof exportedAt !== 'string') {
        return null;
      }
      if (channelName !== null && typeof channelName !== 'string') {
        return null;
      }
      if (envelopeKind !== 'emote-list' && envelopeKind !== 'usage') {
        return null;
      }
      return { kind: 'file', fileName, exportedAt, channelName, envelopeKind };
    }
    case 'seventv-leaderboard':
      return typeof origin['sortBy'] === 'string' && isLeaderboardSort(origin['sortBy'])
        ? { kind: 'seventv-leaderboard', sortBy: origin['sortBy'] }
        : null;
    default:
      return null;
  }
}

export type TransferRunUndoParseResult =
  | {
      ok: true;
      candidates: UndoCandidate[];
      stage: TransferRunMeta['stage'];
      target: RestoreFileTarget;
      sourceFile: UndoSourceFileInfo;
    }
  /** `errorKey` is a Transloco key (restore.import.errors.*), never finished prose. */
  | { ok: false; errorKey: string };

/**
 * Reads either stage of a transfer-run file as undo candidates (#254, spec 6.1) — the mirror image
 * of {@link parseTransferRunForRestore}: that function turns a `replace` row's removed *target* back
 * into something a restore re-adds, this one turns the same row's *source* (the emote a replace took
 * off this set) into something an undo removes again and gives back to whatever the target held.
 *
 * Selection follows E2: `planned` offers **every** `replace` row (the run may never have started, so
 * nothing is filtered on `confirmed` — see {@link UndoCandidate.provenance}); `finished` offers only
 * rows whose REMOVE 7TV actually confirmed (`removedTarget.confirmed === true`), whatever the row's
 * own final status — the same "confirmed, not done" rule the restore parser uses, so an `unknown` row
 * whose REMOVE went through is still a candidate and a `failed@0` row (REMOVE itself never happened)
 * is not.
 *
 * A `transfer-undo` file itself is refused here with `wrongKind` (F7) — there is no undo of an undo;
 * the way back from one is a fresh transfer, not this parser.
 */
export function parseTransferRunForUndo(text: string): TransferRunUndoParseResult {
  const read = readEnvelope(text);
  if (!read.ok) {
    return read;
  }
  // Untrusted JSON from a file — every field below is checked by hand, mirroring
  // `parseTransferRunForRestore`'s own cast.
  const envelope = read.envelope as unknown as Partial<TransferPlanRecord | TransferRunProtocol>;
  if (envelope.kind !== 'transfer-run') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  if (envelope.formatVersion !== TRANSFER_RUN_FORMAT_VERSION) {
    return { ok: false, errorKey: 'restore.import.errors.wrongVersion' };
  }
  const meta = envelope.meta as Partial<TransferRunMeta> | undefined;
  if (!meta || typeof meta !== 'object') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  const targetEmoteSetId = meta.targetEmoteSetId;
  if (typeof targetEmoteSetId !== 'string' || targetEmoteSetId.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  const stage = meta.stage;
  if ((stage !== 'planned' && stage !== 'finished') || !Array.isArray(envelope.rows)) {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }

  const candidates = (envelope.rows as unknown[]).flatMap((row) => {
    const candidate = readUndoCandidate(row, stage);
    return candidate ? [candidate] : [];
  });
  if (candidates.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.transferRunNoRows' };
  }

  // `meta` is `Partial<TransferRunMetaPlanned> | Partial<TransferRunMetaFinished>` at this point —
  // reading `verifiedAt`/`finishedAt` across that union needs the same untrusted-field cast the rest
  // of this file uses rather than an intersection type (which TS collapses to `never` over the two
  // stages' conflicting `stage` literal).
  const untypedMeta = meta as Record<string, unknown>;
  const rawVerifiedAt = untypedMeta['verifiedAt'];
  const rawFinishedAt = untypedMeta['finishedAt'];
  const sourceFile: UndoSourceFileInfo = {
    stage,
    exportedAt: typeof envelope.exportedAt === 'string' ? envelope.exportedAt : '',
    verifiedAt: stage === 'planned' && typeof rawVerifiedAt === 'string' ? rawVerifiedAt : null,
    finishedAt: stage === 'finished' && typeof rawFinishedAt === 'string' ? rawFinishedAt : null,
    origin: readImportOrigin(untypedMeta['origin']),
  };

  return { ok: true, candidates, stage, target: { emoteSetId: targetEmoteSetId }, sourceFile };
}

/** The undo candidate for one untrusted transfer-run row, or `null` when it names nothing an undo
 *  could reverse: not a `replace` row, no readable `removedTarget`, an empty own `alias`, no target
 *  entry to compare against (same "nothing to restore" rule the restore parser's own
 *  `readRemovedTarget` uses), or — in the `finished` stage — a REMOVE 7TV never confirmed. */
function readUndoCandidate(value: unknown, stage: TransferRunMeta['stage']): UndoCandidate | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const row = value as Record<string, unknown>;
  if (row['action'] !== 'replace') {
    return null;
  }
  const target = row['removedTarget'];
  if (typeof target !== 'object' || target === null) {
    return null;
  }
  const { sevenTvEmoteId, entries, aliases, defaultName, confirmed } = target as Record<
    string,
    unknown
  >;
  if (typeof sevenTvEmoteId !== 'string' || sevenTvEmoteId.length === 0) {
    return null;
  }
  if (stage === 'finished' && confirmed !== true) {
    return null;
  }
  const sourceSevenTvEmoteId = row['sevenTvEmoteId'];
  const sourceName = row['sourceName'];
  const alias = row['alias'];
  if (typeof sourceSevenTvEmoteId !== 'string' || sourceSevenTvEmoteId.length === 0) {
    return null;
  }
  if (typeof sourceName !== 'string' || typeof alias !== 'string' || alias.length === 0) {
    return null;
  }
  const status = row['status'];
  const fileStatus: RunItemStatus | 'pending' =
    typeof status === 'string' && KNOWN_RUN_ITEM_STATUSES.has(status)
      ? (status as RunItemStatus)
      : 'pending';
  const targetEntries = readEntryAliases(entries, aliases).map(
    (entryAlias): UndoCandidateTargetEntry => ({ alias: entryAlias }),
  );
  // Same rule `readRemovedTarget` (the restore parser) applies to its own `restoreAliases`: a
  // target with no readable entry at all is nothing an undo could restore anything back onto.
  if (targetEntries.length === 0) {
    return null;
  }
  const knownDefaultName =
    typeof defaultName === 'string' && defaultName.length > 0 ? defaultName : null;

  return {
    sourceSevenTvEmoteId,
    sourceName,
    alias,
    fileStatus,
    target: { sevenTvEmoteId, entries: targetEntries, defaultName: knownDefaultName },
    provenance: stage === 'planned' ? 'unproven' : 'confirmed',
  };
}

export type TransferRunRestoreParseResult =
  | { ok: true; rows: RestoreRow[]; stage: TransferRunMeta['stage']; target: RestoreFileTarget }
  /** `errorKey` is a Transloco key (restore.import.errors.*), never finished prose. */
  | { ok: false; errorKey: string };

/**
 * Reads either stage of a transfer-run file back as restore rows — the removed **target** entries
 * of its `replace` rows and nothing else (a source row's ADD is not something a restore undoes).
 *
 * Validated like a purge-run protocol (`parsePurgeRunProtocol`), with the same error keys: the kind,
 * this kind's own `formatVersion`, the `meta` shape and the rows. The target is the file's own
 * `meta.targetEmoteSetId`, returned as `target` rather than held against the page the file is read
 * on (spec #253, E1/E15) — whether the caller may write to it is the file step's target check. The
 * envelope's `channelName` is **not read at all** (F2): it holds `''` for an untracked target, and a
 * channel name is no part of the target anyway — the channel, the owner and whether the set is
 * active all come from the target list, never from the file.
 *
 * Which removed targets become rows depends on the stage: `planned` (the back-out file, written
 * before any REMOVE) offers **every** target the run was about to remove — whatever was never
 * removed is still in the set and falls out through the restore filter; `finished` (the result
 * protocol) offers only targets whose REMOVE 7TV confirmed (`confirmed === true`), whatever the
 * row's own final status. One row per target, one alias per entry (`null` for the entry without an
 * alias). A file that yields no row is refused with `transferRunNoRows`.
 */
export function parseTransferRunForRestore(text: string): TransferRunRestoreParseResult {
  const read = readEnvelope(text);
  if (!read.ok) {
    return read;
  }
  // Untrusted JSON from a file — every field below is checked by hand, see the same cast in
  // `parsePurgeRunProtocol`.
  const envelope = read.envelope as unknown as Partial<TransferPlanRecord | TransferRunProtocol>;
  if (envelope.kind !== 'transfer-run') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  if (envelope.formatVersion !== TRANSFER_RUN_FORMAT_VERSION) {
    return { ok: false, errorKey: 'restore.import.errors.wrongVersion' };
  }
  const meta = envelope.meta as Partial<TransferRunMeta> | undefined;
  if (!meta || typeof meta !== 'object') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  const targetEmoteSetId = meta.targetEmoteSetId;
  if (typeof targetEmoteSetId !== 'string' || targetEmoteSetId.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  const stage = meta.stage;
  if ((stage !== 'planned' && stage !== 'finished') || !Array.isArray(envelope.rows)) {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }

  const rows = (envelope.rows as unknown[]).flatMap((row) => {
    const restoreRow = readRemovedTarget(row, stage);
    return restoreRow ? [restoreRow] : [];
  });
  if (rows.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.transferRunNoRows' };
  }
  return { ok: true, rows, stage, target: { emoteSetId: targetEmoteSetId } };
}

/** The restore row for one untrusted file row, or `null` when it names no target to restore: not a
 *  `replace` row, no readable `removedTarget`, or — in the `finished` stage — a REMOVE 7TV never
 *  confirmed. */
function readRemovedTarget(value: unknown, stage: TransferRunMeta['stage']): RestoreRow | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const row = value as Record<string, unknown>;
  if (row['action'] !== 'replace') {
    return null;
  }
  const target = row['removedTarget'];
  if (typeof target !== 'object' || target === null) {
    return null;
  }
  const { sevenTvEmoteId, entries, aliases, defaultName, confirmed } = target as Record<
    string,
    unknown
  >;
  if (typeof sevenTvEmoteId !== 'string' || sevenTvEmoteId.length === 0) {
    return null;
  }
  if (stage === 'finished' && confirmed !== true) {
    return null;
  }
  const restoreAliases = readEntryAliases(entries, aliases);
  if (restoreAliases.length === 0) {
    return null;
  }
  const knownDefaultName =
    typeof defaultName === 'string' && defaultName.length > 0 ? defaultName : null;
  const firstNamed = restoreAliases.find((alias): alias is string => alias !== null);
  return {
    emoteId: null,
    sevenTvEmoteId,
    name: firstNamed ?? knownDefaultName ?? sevenTvEmoteId,
    aliases: restoreAliases,
    defaultName: knownDefaultName,
  };
}

/** Every entry of a removed target as an alias to restore under — a non-empty string, or `null`
 *  for the one entry without an alias — each at most once. `entries` is the authority; the named
 *  `aliases` list (the CSV-facing subset) is only read when `entries` yields nothing, so a target
 *  is restored under the names the file does carry rather than dropped. */
function readEntryAliases(entries: unknown, aliases: unknown): (string | null)[] {
  const read: (string | null)[] = [];
  const add = (value: unknown): void => {
    const alias =
      value === null || (typeof value === 'string' && value.length > 0) ? value : undefined;
    if (alias !== undefined && !read.includes(alias)) {
      read.push(alias);
    }
  };
  if (Array.isArray(entries)) {
    for (const entry of entries as unknown[]) {
      if (typeof entry === 'object' && entry !== null && 'alias' in entry) {
        add((entry as { alias: unknown }).alias);
      }
    }
  }
  if (read.length === 0 && Array.isArray(aliases)) {
    for (const alias of aliases as unknown[]) {
      if (alias !== null) {
        add(alias);
      }
    }
  }
  return read;
}
