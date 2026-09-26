import { RunItemStatus } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
// Type-only: `seven-tv-undo.service.ts` is the one core module that builds this file's protocol,
// so it is the natural owner of `UndoRunInfo`/`UndoRunItem`/`UndoRunItemStatus` — mirrors
// `transfer-run-export.ts` importing `ImportRunItem` from `seven-tv-import.service`.
import type {
  UndoRunInfo,
  UndoRunItem,
  UndoRunItemStatus,
} from '../../core/seven-tv/seven-tv-undo.service';
import { UndoSkipReason } from '../../core/seven-tv/undo-plan';
import { CsvColumn, toCsv } from './csv';
import { ExportEnvelope, buildEnvelope } from './export-envelope';
import { sanitizeFilenamePart } from './file-download';
import { RestoreFileTarget, RestoreRow } from './purge-run-export';
import { readEnvelope } from './read-envelope';
import { UndoCandidate, UndoSourceFileInfo } from './transfer-run-export';

/**
 * The transfer-undo protocol (#254): the paper trail of undoing a `replace` — REMOVE the source
 * emote from the target set, then re-ADD whatever entries that `replace` took from the target. One
 * envelope kind, two stages (`meta.stage`), same shape as `transfer-run` (#230):
 * `planned` is the back-out file, written from a *live* read of the target set right before the
 * undo's first `REMOVE`; `finished` is the result protocol, written after the run settles.
 *
 * A grounding principle behind every recovery file in this codebase: **a file restores what its own
 * run removed, never what some other run removed.** The transfer-run `planned` file already restores
 * the *target* a replace is about to clear (via `parseTransferRunForRestore`); it does not also
 * restore the *source* — that is this file's job, in the opposite direction, for the run that removes
 * the source again.
 *
 * Deliberately its own row-shape version (`TRANSFER_UNDO_FORMAT_VERSION`), independent of both
 * `EXPORT_FORMAT_VERSION` and `TRANSFER_RUN_FORMAT_VERSION` — same reasoning as those two: the *row*
 * shape is this kind's own contract, and no version bump anywhere else ties into it.
 *
 * Explicitly **not** an import source (`import-source-parser.ts` rejects `kind === 'transfer-undo'`
 * by name, same as `transfer-run`) and explicitly **not** an undo source either
 * (`parseTransferRunForUndo` refuses a `transfer-undo` file as `wrongKind`, spec F7) — there is no
 * undo of an undo; the way back from one is a fresh transfer. Both stages *are* restore sources
 * (`parseTransferUndoForRestore` below), the same way a transfer-run file is.
 */
export const TRANSFER_UNDO_FORMAT_VERSION = 1;

/** One target entry a `full` undo row asked 7TV to add back — `added` is whether that step's ADD was
 *  confirmed, independent of the row's own final status (an `unknown` ADD whose mutation went through
 *  server-side is still `added: true`, same rule `TransferRunRemovedTarget.confirmed` uses). Always
 *  `false` in the `planned` stage: nothing has run yet. `alias` is never `null` here — a target entry
 *  without an alias is sent (and shown) under the emote's own default name from the file (E21), so an
 *  undo never re-adds an entry it cannot name. */
export interface TransferUndoRestoredTargetEntry {
  alias: string;
  added: boolean;
}

/** What one undo row's ADDs restore on the target. */
export interface TransferUndoRestoredTarget {
  sevenTvEmoteId: string;
  /** The target's 7TV default name at the time of the replace this row undoes (from the file, not a
   *  live lookup — the target may not even exist in the set once this reads it, F15). */
  defaultName: string | null;
  entries: TransferUndoRestoredTargetEntry[];
}

/** What one `full` undo row's REMOVE takes off the source — `null` for an `addOnly` row, which never
 *  issues a REMOVE at all. */
export interface TransferUndoRemovedSource {
  /** The source's own live entries at the moment this row's REMOVE ran (`planned`: the dialog's
   *  read; `finished`: the *last* read this row's own prior-to-REMOVE check made, spec F13) — never
   *  an aliasless entry (the source a replace collided on always carries the named `alias` this row
   *  restores under). */
  entries: { alias: string }[];
  /** Whether 7TV confirmed the REMOVE — directly, or through a later re-read — independent of the
   *  row's own final status. Always `false` in the `planned` stage. */
  confirmed: boolean;
}

/** A target entry a `full` row could not restore, or an `addOnly` row's entry it skipped — a name a
 *  third id holds (`targetNameTaken`) or a `null` entry whose file carries no `defaultName` to add it
 *  back under (`targetNameUnverifiable`). Orthogonal to `status` (E23): a row can be `partial` with
 *  omissions while every entry it *did* attempt succeeded. `alias` is `null` exactly for a
 *  `targetNameUnverifiable` entry: it is omitted *because* the file has no name for it (E21) — there
 *  is nothing honest to invent, so `alias` stays `null` rather than a made-up placeholder. A
 *  `targetNameTaken` entry always has a name (a third id cannot hold a name nobody wrote down). */
export interface TransferUndoOmittedEntry {
  alias: string | null;
  reason: 'targetNameTaken' | 'targetNameUnverifiable';
}

/** A row that actually ran (or was queued to) — as opposed to one skipped before a mode was ever
 *  decided for it (see {@link TransferUndoSkippedRow}). Kept in both stages under `kind: 'executed'`
 *  so a reader can tell the two apart without knowing which stage it holds. */
export interface TransferUndoExecutedRow {
  kind: 'executed';
  mode: 'full' | 'addOnly';
  sourceSevenTvEmoteId: string;
  sourceName: string;
  /** The alias the source is restored under — the same alias the original replace moved onto the
   *  target. */
  alias: string;
  /** `null` for an `addOnly` row — nothing was ever removed from the source for it to restore. */
  removedSource: TransferUndoRemovedSource | null;
  restoredTarget: TransferUndoRestoredTarget;
  /** Whether the *candidate this row came from* was a proven removal at the time it was read (F17) —
   *  carried through unchanged from {@link UndoCandidate.provenance}. */
  provenance: 'confirmed' | 'unproven';
  /** Only ever non-empty on an `addOnly` row (E23) — a `full` row with any omission never ran at all
   *  (E8's alles-oder-nichts rule), so it has nothing to list here. */
  omittedEntries: TransferUndoOmittedEntry[];
  /** Only ever non-empty on an `addOnly` row (E20) — a foreign entry on the target sits next to what
   *  this row restores, untouched. */
  notes: 'targetHasForeignEntries'[];
  /** `'partial'` stands in for `'done'` only — `failed`/`unknown`/`cancelled` are never overwritten
   *  by an omission (F19). `'pending'` for every row in the `planned` stage. */
  status: RunItemStatus | 'partial';
  failedStep: number | null;
  completedSteps: number;
  /** `null` in `planned`. In `finished`, 7TV's own raw text for a step that actually failed a
   *  mutation — but a row the recheck skipped before a REMOVE (`skippedDrift`/`recheckUnavailable`)
   *  never called 7TV for that step, so this is the translated UI text of `undo.errors.${reason}`
   *  instead (`toExecutedInput`'s `sevenTvErrorMessage ?? errorMessage`, same convention as
   *  `ImportRunItem`); `skippedReason` below already carries that reason untranslated. */
  errorMessage: string | null;
  /** Set only for a row the run itself skipped mid-flight (a drift the per-REMOVE recheck found, or
   *  the recheck becoming unavailable) — `null` otherwise, including for every `planned` row. A
   *  candidate skipped *before* it ever became a row (in the dialog, the pre-run recheck, or the
   *  origin lock) is a {@link TransferUndoSkippedRow} instead, never this. */
  skippedReason: UndoSkipReason | null;
}

/**
 * A candidate the run never turned into an executed row at all — refused as `duplicateInFile` by the
 * classification's own step 0 or the service's own lock (`undo-plan.ts`, `seven-tv-undo.service.ts`),
 * the confirm dialog (`nothingToDo`, `sourceUnderOtherName`, `targetNameTaken`, …), the pre-run
 * freshness recheck (`skippedDrift`), or the service's own second origin check
 * (`skippedUnproven`) — spec 17 K4. Cannot carry `mode`/`restoredTarget`/`removedSource`/`status`/
 * `completedSteps`: none of those were ever decided for it. Never appears in the `planned` stage — a
 * back-out file names only what its run is actually about to touch (spec 6.4).
 */
export interface TransferUndoSkippedRow {
  kind: 'skipped';
  sourceSevenTvEmoteId: string;
  sourceName: string;
  alias: string;
  targetSevenTvEmoteId: string;
  provenance: 'confirmed' | 'unproven';
  skippedReason: UndoSkipReason;
}

export type TransferUndoRow = TransferUndoExecutedRow | TransferUndoSkippedRow;

interface TransferUndoMetaBase {
  targetEmoteSetId: string;
  targetChannelName: string | null;
  targetOwnerDisplayName: string | null;
  /** Provenance of the transfer-run file this undo reverses (F6) — lets a human follow the paper
   *  trail from the undo back to the transfer it undoes. */
  undoneFile: UndoSourceFileInfo;
  /** Whether the person confirmed an unproven (`planned`-sourced) `full` row before it ran — the
   *  Herkunftssperre's own paper trail (spec 17 K2). `false` when every runnable row was already
   *  `provenance: 'confirmed'`, or when the run was `addOnly`-only. */
  acknowledgedUnproven: boolean;
}

export interface TransferUndoCountsPlanned {
  /** Rows this plan will run — `full` and `addOnly` alike. */
  planned: number;
  /** The `full` rows among them — the REMOVEs this run is about to issue. */
  removals: number;
  /** Every ADD across every row. */
  additions: number;
}

export interface TransferUndoCountsFinished {
  /** Rows that actually ran — never counts a {@link TransferUndoSkippedRow} (spec 17 K4).
   *  `succeeded + failed + cancelled + unknown + partial` always sums to this. */
  requested: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  unknown: number;
  /** An otherwise-`done` `addOnly` row an omission kept from being complete (F19) — its own bucket,
   *  disjoint from the four above, so the five sub-counters add up to `requested` exactly. */
  partial: number;
  /** Confirmed REMOVEs (`removedSource.confirmed`). */
  removed: number;
  /** Confirmed ADDs, across every row. */
  added: number;
  /** Candidates the run never turned into a row at all. */
  skipped: number;
}

export interface TransferUndoMetaPlanned extends TransferUndoMetaBase {
  stage: 'planned';
  /** ISO timestamp of the live read this file was built from. */
  verifiedAt: string;
  counts: TransferUndoCountsPlanned;
}

export interface TransferUndoMetaFinished extends TransferUndoMetaBase {
  stage: 'finished';
  startedAt: string;
  finishedAt: string;
  counts: TransferUndoCountsFinished;
}

export type TransferUndoMeta = TransferUndoMetaPlanned | TransferUndoMetaFinished;

/** The `planned` stage — the back-out file, built from a live read (`buildTransferUndoPlanRecord`).
 *  Only ever holds {@link TransferUndoExecutedRow}s (spec 6.4: a back-out file names only what its
 *  run is about to touch) — typed as the full row union anyway so both stages, and the restore
 *  parser below, share one row type. */
export type TransferUndoPlanRecord = ExportEnvelope<TransferUndoRow, TransferUndoMetaPlanned>;

/** The `finished` stage — the result protocol, built from a settled run
 *  (`buildTransferUndoProtocol`). */
export type TransferUndoProtocol = ExportEnvelope<TransferUndoRow, TransferUndoMetaFinished>;

/** Fields a `buildTransferUndoPlanRecord`/`buildTransferUndoProtocol` caller hands in for *every*
 *  row, regardless of mode: the classified candidate and the ADDs it asks for (or asked for).
 *  `adds`/`omittedEntries`/`notes` are exactly `UndoPlanRow`'s/`UndoRunItem`'s own fields of the same
 *  name (#254 T2/T4) — passed straight through, not re-derived here; this module only ever turns
 *  them into a file row. */
interface TransferUndoRowInputBase {
  candidate: UndoCandidate;
  /** The ADDs this row asks the target for — always a non-empty, non-null alias (E21); a `full` row
   *  with zero ADDs cannot happen (F11). */
  adds: { alias: string }[];
  /** Only ever non-empty for an `addOnly` row. */
  omittedEntries: TransferUndoOmittedEntry[];
  /** Only ever non-empty for an `addOnly` row. */
  notes: 'targetHasForeignEntries'[];
}

/** One runnable row for the `planned` stage — everything `buildTransferUndoPlanRecord` needs beyond
 *  the live read it restores `removedSource.entries` from. Not discriminated on `mode` the way
 *  {@link TransferUndoExecutedInput} is: the `planned` builder derives `removedSource.entries` itself
 *  from the live read it is given, so there is no `sourceEntriesAtRemove` field here to make
 *  conditionally required. */
export type TransferUndoRunnableInput = TransferUndoRowInputBase & { mode: 'full' | 'addOnly' };

/** The statuses a settled run's row can actually stop at. Deliberately excludes `pending`/
 *  `in-progress` — those only ever apply to a row still queued or in flight, and the `finished`
 *  builder only ever sees rows a run has already settled. Narrowing this, rather than reusing the
 *  broader `RunItemStatus | 'partial'` the file row type itself carries (a `planned` row genuinely
 *  is `'pending'`), is what makes `TransferUndoCountsFinished`'s "the five sub-counters sum to
 *  `requested`" true by construction rather than by convention: these five values are exactly its
 *  five buckets, so every {@link TransferUndoExecutedInput} lands in exactly one. */
export type TransferUndoSettledStatus = 'done' | 'failed' | 'cancelled' | 'unknown' | 'partial';

interface TransferUndoExecutedInputBase extends TransferUndoRowInputBase {
  status: TransferUndoSettledStatus;
  failedStep: number | null;
  completedSteps: number;
  errorMessage: string | null;
  skippedReason: UndoSkipReason | null;
}

/**
 * One row of a settled run, for the `finished` stage. `completedSteps` drives both
 * `removedSource.confirmed` (`>= 1`) and each ADD's own `entries[].added` — step 0 is the REMOVE on
 * a `full` row (absent on `addOnly`), so ADD *i* (0-based) is confirmed once `completedSteps` passes
 * it.
 *
 * Discriminated on `mode` (spec 6.4: `removedSource` is `null` only for `addOnly`) so a `full` row
 * cannot be built without `sourceEntriesAtRemove` — the *last* read this row's own pre-REMOVE
 * recheck made (F13) — even one whose REMOVE never confirmed: the recheck always reads *something*
 * before attempting the mutation, so this is never `null` for a `full` row, and a caller that has
 * nothing to put there has a bug, not an honest gap. An `addOnly` row never rechecks or removes
 * anything, so its `sourceEntriesAtRemove` is always `null`, not merely optional.
 */
export type TransferUndoExecutedInput =
  | (TransferUndoExecutedInputBase & {
      mode: 'full';
      sourceEntriesAtRemove: { alias: string }[];
    })
  | (TransferUndoExecutedInputBase & { mode: 'addOnly'; sourceEntriesAtRemove: null });

/** One candidate the run never turned into a row — see {@link TransferUndoSkippedRow}. */
export interface TransferUndoSkippedInput {
  candidate: UndoCandidate;
  skippedReason: UndoSkipReason;
}

function transferUndoExecutedRow(
  input: TransferUndoRowInputBase & { mode: 'full' | 'addOnly' },
  removedSourceEntries: { alias: string }[] | null,
  completedSteps: number,
  status: RunItemStatus | 'partial',
  failedStep: number | null,
  errorMessage: string | null,
  skippedReason: UndoSkipReason | null,
): TransferUndoExecutedRow {
  const { candidate, mode, adds, omittedEntries, notes } = input;
  // Step 0 is the REMOVE on a `full` row; `addOnly` skips straight to its ADDs at step 0.
  const addStepOffset = mode === 'full' ? 1 : 0;
  return {
    kind: 'executed',
    mode,
    sourceSevenTvEmoteId: candidate.sourceSevenTvEmoteId,
    sourceName: candidate.sourceName,
    alias: candidate.alias,
    // A `full` row always carries `removedSource` (spec 6.4: `null` is only ever valid for
    // `addOnly`) — `removedSourceEntries ?? []` rather than falling through to `null` if a caller
    // ever passed one, so a bug upstream shows up as an empty entries list, not a `full` row that
    // silently looks like it never removed anything at all.
    removedSource:
      mode === 'full'
        ? { entries: removedSourceEntries ?? [], confirmed: completedSteps >= 1 }
        : null,
    restoredTarget: {
      sevenTvEmoteId: candidate.target.sevenTvEmoteId,
      defaultName: candidate.target.defaultName,
      entries: adds.map((add, index) => ({
        alias: add.alias,
        added: completedSteps >= addStepOffset + index + 1,
      })),
    },
    provenance: candidate.provenance,
    omittedEntries,
    notes,
    status,
    failedStep,
    completedSteps,
    errorMessage,
    skippedReason,
  };
}

function transferUndoSkippedRow(input: TransferUndoSkippedInput): TransferUndoSkippedRow {
  return {
    kind: 'skipped',
    sourceSevenTvEmoteId: input.candidate.sourceSevenTvEmoteId,
    sourceName: input.candidate.sourceName,
    alias: input.candidate.alias,
    targetSevenTvEmoteId: input.candidate.target.sevenTvEmoteId,
    provenance: input.candidate.provenance,
    skippedReason: input.skippedReason,
  };
}

/**
 * The `planned` stage: every *runnable* row (never a skipped candidate — spec 6.4: a back-out file
 * names only what its run is about to touch), each `full` row's `removedSource.entries` read fresh
 * from `read` (the live set read the confirm dialog made, not a stale preview) — the source's own
 * named entries at that moment, so a stale second alias the classifier never saw is not silently
 * dropped from the recovery file even though the run itself would still miss it (F13's own residual
 * gap is the moment *between* this read and the actual REMOVE, not this file). Every row
 * `status: 'pending'`, `completedSteps: 0`, every ADD `added: false` — nothing has run yet.
 */
export function buildTransferUndoPlanRecord(input: {
  targetEmoteSetId: string;
  targetChannelName: string | null;
  targetOwnerDisplayName: string | null;
  sourceFile: UndoSourceFileInfo;
  /** Epoch ms of the live read this file is built from. */
  verifiedAt: number;
  acknowledgedUnproven: boolean;
  read: SevenTvSetEntries;
  rows: TransferUndoRunnableInput[];
}): TransferUndoPlanRecord {
  const removals = input.rows.filter((row) => row.mode === 'full').length;
  const additions = input.rows.reduce((sum, row) => sum + row.adds.length, 0);
  const envelope = buildEnvelope<TransferUndoRow, TransferUndoMetaPlanned>({
    kind: 'transfer-undo',
    channelName: input.targetChannelName ?? '',
    withheld: [],
    meta: {
      stage: 'planned',
      targetEmoteSetId: input.targetEmoteSetId,
      targetChannelName: input.targetChannelName,
      targetOwnerDisplayName: input.targetOwnerDisplayName,
      undoneFile: input.sourceFile,
      acknowledgedUnproven: input.acknowledgedUnproven,
      verifiedAt: new Date(input.verifiedAt).toISOString(),
      counts: { planned: input.rows.length, removals, additions },
    },
    rows: input.rows.map((row) =>
      transferUndoExecutedRow(
        row,
        row.mode === 'full'
          ? (input.read.aliasesById.get(row.candidate.sourceSevenTvEmoteId) ?? []).map((alias) => ({
              alias,
            }))
          : null,
        0,
        'pending',
        null,
        null,
        null,
      ),
    ),
  });
  return { ...envelope, formatVersion: TRANSFER_UNDO_FORMAT_VERSION };
}

/**
 * The `finished` stage: every candidate of the run, **unfiltered** — executed rows (`done`, `failed`,
 * `cancelled`, `unknown`, and a row the per-REMOVE recheck skipped mid-flight, all still
 * `kind: 'executed'`) alongside every candidate the run never turned into a row at all
 * (`kind: 'skipped'`, spec 17 K4). `counts.requested` counts only the executed rows; `counts.skipped`
 * is the skipped ones on top — the two are never conflated. `counts.succeeded` + `.failed` +
 * `.cancelled` + `.unknown` + `.partial` always sums to `counts.requested`: every executed row's
 * `status` lands in exactly one of those five buckets, never more than one.
 */
export function buildTransferUndoProtocol(input: {
  targetEmoteSetId: string;
  targetChannelName: string | null;
  targetOwnerDisplayName: string | null;
  sourceFile: UndoSourceFileInfo;
  startedAt: number;
  finishedAt: number;
  acknowledgedUnproven: boolean;
  executed: TransferUndoExecutedInput[];
  skipped: TransferUndoSkippedInput[];
}): TransferUndoProtocol {
  const executedRows = input.executed.map((item) =>
    // `item.mode === 'full'` narrows `item.sourceEntriesAtRemove` to the non-null branch of the
    // TransferUndoExecutedInput union — an `addOnly` item's is always `null` by that same union.
    transferUndoExecutedRow(
      item,
      item.mode === 'full' ? item.sourceEntriesAtRemove : null,
      item.completedSteps,
      item.status,
      item.failedStep,
      item.errorMessage,
      item.skippedReason,
    ),
  );
  const skippedRows = input.skipped.map(transferUndoSkippedRow);
  const statuses = executedRows.map((row) => row.status);
  const removed = executedRows.filter((row) => row.removedSource?.confirmed === true).length;
  const added = executedRows.reduce(
    (sum, row) => sum + row.restoredTarget.entries.filter((entry) => entry.added).length,
    0,
  );

  const envelope = buildEnvelope<TransferUndoRow, TransferUndoMetaFinished>({
    kind: 'transfer-undo',
    channelName: input.targetChannelName ?? '',
    withheld: [],
    meta: {
      stage: 'finished',
      targetEmoteSetId: input.targetEmoteSetId,
      targetChannelName: input.targetChannelName,
      targetOwnerDisplayName: input.targetOwnerDisplayName,
      undoneFile: input.sourceFile,
      acknowledgedUnproven: input.acknowledgedUnproven,
      startedAt: new Date(input.startedAt).toISOString(),
      finishedAt: new Date(input.finishedAt).toISOString(),
      counts: {
        requested: statuses.length,
        succeeded: statuses.filter((status) => status === 'done').length,
        failed: statuses.filter((status) => status === 'failed').length,
        cancelled: statuses.filter((status) => status === 'cancelled').length,
        unknown: statuses.filter((status) => status === 'unknown').length,
        partial: statuses.filter((status) => status === 'partial').length,
        removed,
        added,
        skipped: skippedRows.length,
      },
    },
    rows: [...executedRows, ...skippedRows],
  });
  return { ...envelope, formatVersion: TRANSFER_UNDO_FORMAT_VERSION };
}

/**
 * The `finished` protocol of a settled run (spec 6.4, 17 K4) — every row it executed, as
 * `kind: 'executed'` (a row the recheck skipped included, `cancelled` with its reason), and every
 * candidate that never became a row, as `kind: 'skipped'`. `null` until the run is settled. What the
 * dock's download (T7) serializes with `transferUndoJson`/`transferUndoCsv`.
 *
 * Moved out of `SevenTvUndoService` (#254 layering fix): the mapping from the service's own
 * `UndoRunInfo`/`UndoRunItem` shapes to this file's `TransferUndoExecutedInput`/
 * `TransferUndoSkippedInput` belongs next to the protocol it builds, not inside `core/`, which may
 * not import from `shared/`.
 */
export function buildUndoRunProtocol(run: UndoRunInfo): TransferUndoProtocol | null {
  if (run.settlement !== 'settled' || run.result === null) {
    return null;
  }
  return buildTransferUndoProtocol({
    targetEmoteSetId: run.targetSetId,
    targetChannelName: run.trackedChannelName,
    targetOwnerDisplayName: run.ownerDisplayName,
    sourceFile: run.sourceFile,
    startedAt: run.result.startedAt,
    finishedAt: run.result.finishedAt,
    acknowledgedUnproven: run.acknowledgedUnproven,
    executed: run.result.items.map(toExecutedInput),
    skipped: run.skipped.map((row) => ({ candidate: row.candidate, skippedReason: row.reason })),
  });
}

/** One settled row as the `finished` protocol's input — `full` rows always with their source
 *  entries (F13), `addOnly` rows with `null`. A settled row is never `pending`/`in-progress`; one
 *  that somehow is counts as `cancelled`. */
function toExecutedInput(item: UndoRunItem): TransferUndoExecutedInput {
  const base = {
    candidate: item.candidate,
    adds: item.adds,
    omittedEntries: item.omittedEntries,
    notes: item.notes,
    status: settledStatus(item.undoStatus),
    failedStep: item.failedStep,
    completedSteps: item.completedSteps,
    errorMessage: item.sevenTvErrorMessage ?? item.errorMessage ?? null,
    skippedReason: item.skippedReason,
  };
  return item.mode === 'full'
    ? {
        ...base,
        mode: 'full',
        sourceEntriesAtRemove: item.sourceEntriesAtRemove ?? [{ alias: item.candidate.alias }],
      }
    : { ...base, mode: 'addOnly', sourceEntriesAtRemove: null };
}

function settledStatus(status: UndoRunItemStatus): TransferUndoSettledStatus {
  return status === 'pending' || status === 'in-progress' ? 'cancelled' : status;
}

export function transferUndoJson(protocol: TransferUndoPlanRecord | TransferUndoProtocol): string {
  return JSON.stringify(protocol, null, 2);
}

/** CSV only exists for the `finished` stage, same reasoning as `transferRunCsv`: the `planned`
 *  back-out file is JSON-only. A `kind: 'skipped'` row leaves every column but `kind`, `source_name`,
 *  `alias`, `source_seven_tv_emote_id`, `target_seven_tv_emote_id` and `skipped_reason` empty — it
 *  never had a `mode`, a REMOVE or an ADD to report on. `omitted_entries` is `alias:reason` pairs,
 *  `|`-joined for a row with more than one — an entry with no name (`targetNameUnverifiable`, whose
 *  `alias` is `null`) writes the alias half empty rather than inventing one, e.g. `:targetNameUnverifiable`. */
export function transferUndoCsv(protocol: TransferUndoProtocol): string {
  const columns: CsvColumn<TransferUndoRow>[] = [
    { header: 'kind', value: (row) => row.kind },
    { header: 'mode', value: (row) => (row.kind === 'executed' ? row.mode : null) },
    { header: 'source_name', value: (row) => row.sourceName },
    { header: 'alias', value: (row) => row.alias },
    { header: 'source_seven_tv_emote_id', value: (row) => row.sourceSevenTvEmoteId },
    {
      header: 'removed_confirmed',
      value: (row) =>
        row.kind === 'executed' && row.removedSource ? String(row.removedSource.confirmed) : null,
    },
    {
      header: 'target_seven_tv_emote_id',
      value: (row) =>
        row.kind === 'executed' ? row.restoredTarget.sevenTvEmoteId : row.targetSevenTvEmoteId,
    },
    {
      header: 'target_default_name',
      value: (row) => (row.kind === 'executed' ? row.restoredTarget.defaultName : null),
    },
    {
      header: 'target_aliases',
      value: (row) =>
        row.kind === 'executed'
          ? row.restoredTarget.entries.map((entry) => entry.alias).join('|')
          : null,
    },
    {
      header: 'target_added',
      value: (row) =>
        row.kind === 'executed'
          ? row.restoredTarget.entries.map((entry) => String(entry.added)).join('|')
          : null,
    },
    { header: 'status', value: (row) => (row.kind === 'executed' ? row.status : null) },
    { header: 'failed_step', value: (row) => (row.kind === 'executed' ? row.failedStep : null) },
    {
      header: 'error_message',
      value: (row) => (row.kind === 'executed' ? row.errorMessage : null),
    },
    {
      header: 'omitted_entries',
      value: (row) =>
        row.kind === 'executed'
          ? row.omittedEntries.map((entry) => `${entry.alias ?? ''}:${entry.reason}`).join('|')
          : null,
    },
    // Both row kinds carry `skippedReason` under the same name — no branch needed, unlike every
    // other column above, which only the executed row shape has.
    { header: 'skipped_reason', value: (row) => row.skippedReason },
  ];
  return toCsv(protocol.rows, columns);
}

/** The `planned` stage's filename — always JSON, its own `-transfer-undo-plan-` suffix so it never
 *  collides with the `finished` stage's file, or with a transfer-run file of the same run. */
export function transferUndoPlanFilename(channelOrSetLabel: string, verifiedAt: string): string {
  const stamp = verifiedAt.slice(0, 16).replace('T', '-').replace(':', '');
  return `emotepurge_${sanitizeFilenamePart(channelOrSetLabel)}_transfer-undo-plan_${stamp}.json`;
}

/** The `finished` stage's filename — same `channelOrSetLabel` convention as `transferUndoPlanFilename`. */
export function transferUndoFilename(
  channelOrSetLabel: string,
  finishedAt: string,
  ext: 'csv' | 'json',
): string {
  const stamp = finishedAt.slice(0, 16).replace('T', '-').replace(':', '');
  return `emotepurge_${sanitizeFilenamePart(channelOrSetLabel)}_transfer-undo_${stamp}.${ext}`;
}

export type TransferUndoRestoreParseResult =
  | { ok: true; rows: RestoreRow[]; stage: TransferUndoMeta['stage']; target: RestoreFileTarget }
  /** `errorKey` is a Transloco key (restore.import.errors.*), never finished prose. */
  | { ok: false; errorKey: string };

/**
 * Reads either stage of a transfer-undo file back as restore rows (E12) — the **source** emotes it
 * removed, under their own alias: `planned` offers every `full` row (whatever ran or not — the run
 * this file describes may never have started), `finished` offers only rows whose REMOVE 7TV confirmed
 * (`removedSource.confirmed === true`). A `kind: 'skipped'` row is excluded outright, in both stages —
 * nothing was ever removed for it. A row without a recognized `kind` fails the *whole* file as
 * `wrongKind` rather than being silently dropped: this row shape has no legacy reader to stay lenient
 * for (F7), so an unrecognized `kind` means the file is not one of ours, full stop.
 */
export function parseTransferUndoForRestore(text: string): TransferUndoRestoreParseResult {
  const read = readEnvelope(text);
  if (!read.ok) {
    return read;
  }
  // Untrusted JSON from a file — every field below is checked by hand, mirroring
  // `parseTransferRunForRestore`'s own cast.
  const envelope = read.envelope as unknown as Partial<
    TransferUndoPlanRecord | TransferUndoProtocol
  >;
  if (envelope.kind !== 'transfer-undo') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  if (envelope.formatVersion !== TRANSFER_UNDO_FORMAT_VERSION) {
    return { ok: false, errorKey: 'restore.import.errors.wrongVersion' };
  }
  const meta = envelope.meta as Partial<TransferUndoMeta> | undefined;
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

  const rawRows = envelope.rows as unknown[];
  for (const raw of rawRows) {
    const kind =
      typeof raw === 'object' && raw !== null
        ? (raw as Record<string, unknown>)['kind']
        : undefined;
    if (kind !== 'executed' && kind !== 'skipped') {
      return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
    }
  }

  const rows = rawRows.flatMap((row) => {
    const restoreRow = readUndoSourceRow(row as Record<string, unknown>, stage);
    return restoreRow ? [restoreRow] : [];
  });
  if (rows.length === 0) {
    return { ok: false, errorKey: 'restore.import.errors.transferUndoNoRows' };
  }
  return { ok: true, rows, stage, target: { emoteSetId: targetEmoteSetId } };
}

/** The restore row for one untrusted `kind: 'executed'` file row, or `null` when it names no source
 *  to restore: a `kind: 'skipped'` row (nothing was removed), a row whose `mode` is not `full` (an
 *  `addOnly` row never issues a REMOVE, whatever a manipulated `removedSource` next to it might
 *  claim), or — in the `finished` stage — a REMOVE 7TV never confirmed. */
function readUndoSourceRow(
  row: Record<string, unknown>,
  stage: TransferUndoMeta['stage'],
): RestoreRow | null {
  if (row['kind'] !== 'executed' || row['mode'] !== 'full') {
    return null;
  }
  const sourceSevenTvEmoteId = row['sourceSevenTvEmoteId'];
  const sourceName = row['sourceName'];
  const alias = row['alias'];
  if (typeof sourceSevenTvEmoteId !== 'string' || sourceSevenTvEmoteId.length === 0) {
    return null;
  }
  if (typeof sourceName !== 'string' || typeof alias !== 'string' || alias.length === 0) {
    return null;
  }
  const removedSource = row['removedSource'];
  if (typeof removedSource !== 'object' || removedSource === null) {
    return null;
  }
  const { confirmed } = removedSource as Record<string, unknown>;
  if (stage === 'finished' && confirmed !== true) {
    return null;
  }
  return {
    emoteId: null,
    sevenTvEmoteId: sourceSevenTvEmoteId,
    name: sourceName,
    aliases: [alias],
    defaultName: null,
  };
}
