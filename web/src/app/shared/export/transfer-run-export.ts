import { ImportOrigin } from '../../core/seven-tv/import-source';
import { ImportRunItem } from '../../core/seven-tv/seven-tv-import.service';
import { RunItemStatus } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { TransferPlan, TransferRow, TransferRowTarget } from '../../core/seven-tv/transfer-plan';
import { CsvColumn, toCsv } from './csv';
import { ExportEnvelope, buildEnvelope } from './export-envelope';
import { sanitizeFilenamePart } from './file-download';

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
 * from. Loading either stage back through the restore path is not wired up yet.
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
  /** `null` for an untracked target (spec 8.6, T2.6) — the envelope's own `channelName` carries
   *  `''` in that case (Frage 6, entschieden), this field keeps the honest `null`. */
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
 *  it right now — the `planned` stage's own source of truth (Codex-Finding 1: from the *read*, not
 *  from the preview the row was decided against). */
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
 * (the live set read, not the preview the row was decided against — Codex-Finding 1) and named by
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
 * row's own final status (Runde 2, Finding 1: an `unknown` ADD whose REMOVE went through still
 * counts as a confirmed removal).
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
 *  target (Grenzfall: an untracked target without an owner display name still names the file by its
 *  set id). */
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
