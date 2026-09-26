import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import {
  Injectable,
  Signal,
  WritableSignal,
  computed,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { Observable, catchError, map, of, retry, throwError, timeout, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import {
  MAX_AUTOMATIC_SYNC_RETRIES,
  REMOVE_EMOTE_MUTATION,
  SYNC_RETRY_DELAY_MS,
  timeoutReportAttempt,
} from './seven-tv-delete.service';
import { SyncDeletedInSetResponse, SyncRestoredInSetResponse } from './seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from './seven-tv-emote-set.service';
import { ResyncTriggerState } from './seven-tv-restore.service';
import {
  RunFailure,
  RunItemStatus,
  RunOperation,
  RunQueueEmote,
  RunQueueItem,
  RunResult,
  SevenTvRunEngine,
  StepGate,
} from './seven-tv-run-engine';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { RunRecordBase, SevenTvRunLifecycle } from './seven-tv-run-lifecycle';
import { SevenTvSetEntries, loadSevenTvSetEntries } from './seven-tv-set-entries';
import { SevenTvTokenService } from './seven-tv-token.service';
import {
  SyncReportOutcome,
  SyncReportReason,
  SyncReportState,
  classifySyncInSetFailure,
  classifySyncInSetResponse,
  isChannelMismatch,
} from './sync-report-outcome';
import { UndoCandidate, UndoSourceFileInfo } from './undo-candidate';
import {
  UNDO_SKIP_REASONS,
  UndoNote,
  UndoOmittedEntry,
  UndoPlanRow,
  UndoSkipReason,
  UndoSkippedRow,
  classifyUndoRow,
  sameClassification,
} from './undo-plan';

/** The ADD every undo step after the REMOVE sends — the same mutation the restore and the import
 *  use (copied, not shared, like theirs). The undo **always** sets `$alias` (spec E21): an entry the
 *  file names without an alias goes back under the file's `defaultName`, never under whatever 7TV's
 *  default name is today. */
const ADD_EMOTE_MUTATION = `
  mutation AddEmote($setId: Id!, $emoteId: Id!, $alias: String) {
    emoteSets {
      emoteSet(id: $setId) {
        addEmote(id: { emoteId: $emoteId, alias: $alias }) {
          id
        }
      }
    }
  }
`;

/** Time budget of the read before one REMOVE attempt (spec E19). `loadSevenTvSetEntries` has no
 *  deadline of its own and the engine gives a `beforeStep` hook none either — the same 20 s the
 *  import's re-read allows. A read that runs out is a read failure (fail-closed). */
export const RECHECK_READ_TIMEOUT_MS = 20_000;

/** Time budget of the one re-read after a run with an unanswered step (spec 4.6) — the import's
 *  `SETTLE_READ_TIMEOUT_MS`. */
export const UNDO_SETTLE_READ_TIMEOUT_MS = 20_000;

/** After this many failed reads in a row, every remaining `full` row is skipped without another
 *  read (spec E19, 4.4 point 10a); `addOnly` rows keep running. */
export const MAX_CONSECUTIVE_RECHECK_FAILURES = 3;

/** How long `noticePending` stays true after a `startUndo` call that skipped something — the same
 *  4000 ms transient notice as the restore's and the import's (`docs/UI-Designsprache.md` §4.5). */
export const UNDO_NOTICE_MS = 4000;

const PROCEED: StepGate = { kind: 'proceed' };

/**
 * Where an undo run goes and whom it tells (spec 6.5) — the restore's `RestoreStartTarget`
 * **without** `resyncChannelName`, because no client resync of a non-active target exists any more
 * (E16, #255). Frozen into the run record, never re-read from the caller:
 *
 * - `setId` — the set every REMOVE/ADD, both reports and every retry name.
 * - `expectedChannelName` — the tracked channel when the target is its *active* set, else `null`
 *   (#253 E18); sent with both reports, the channel of the N1 fallback resync.
 * - `hostChannelName` — the page's channel; only `resetIfChannelChanged` compares against it.
 * - `setName`, `ownerOrChannelLabel` — display only (the dock's target line).
 *
 * Three fields beyond spec 6.5, all for the `finished` protocol (`buildUndoRunProtocol`), which
 * needs them and which nothing else could hand the service later (#254 T4 report):
 * - `trackedChannelName` — the target account's tracked channel, active set or not (`null` when
 *   untracked): the protocol's `meta.targetChannelName` and its filename part.
 * - `ownerDisplayName` — the protocol's `meta.targetOwnerDisplayName`.
 * - `sourceFile` — the transfer-run file this undo reverses, the protocol's `meta.undoneFile` (F6).
 */
export interface UndoRunTarget {
  setId: string;
  expectedChannelName: string | null;
  hostChannelName: string;
  setName: string;
  ownerOrChannelLabel: string;
  trackedChannelName: string | null;
  ownerDisplayName: string | null;
  sourceFile: UndoSourceFileInfo;
}

/** The two reasons the run itself skips a row, both through the recheck before a REMOVE (E19). */
export type UndoInRunSkipReason = Extract<UndoSkipReason, 'skippedDrift' | 'recheckUnavailable'>;

/** What the recheck before a `full` row's REMOVE recorded for that row — written by the hook before
 *  it answers the engine, idempotently: a second attempt of the same REMOVE (after a rate-limit
 *  pause) overwrites the first, so the **last** read before the REMOVE wins (F13). */
export interface UndoRowRecheck {
  /** `null` once a read let the REMOVE go ahead. */
  skippedReason: UndoInRunSkipReason | null;
  /** The source's named entries in the last read that answered — kept from an earlier attempt when
   *  a later read failed. */
  sourceEntriesAtRemove: { alias: string }[];
}

/** A row's status as the undo shows it: the engine's, or `partial` — which only ever replaces a
 *  `done` of an `addOnly` row that left entries out (E23, F19). */
export type UndoRunItemStatus = RunItemStatus | 'partial';

/**
 * One row of an undo run: the engine's queue row (whose `status` stays the engine's own — a
 * `partial` row is `done` there, which is the projection a `RunProgressPanel` counts, plan
 * Festlegung 5) plus the plan row it runs and what the run learned about it.
 *
 * `errorMessage` is the text to display; where the settled result replaces it with an undo reason
 * (`undo.errors.*`), the engine's own text moves to `sevenTvErrorMessage` — same convention as
 * `ImportRunItem`; a protocol writes `sevenTvErrorMessage ?? errorMessage`.
 */
export interface UndoRunItem extends RunQueueItem {
  candidate: UndoCandidate;
  mode: 'full' | 'addOnly';
  /** The ADDs of this row, in step order; never `null`, never empty (E21). */
  adds: { alias: string }[];
  provenance: UndoCandidate['provenance'];
  /** Only ever non-empty on an `addOnly` row; orthogonal to the status (E23). */
  omittedEntries: UndoOmittedEntry[];
  /** Only ever non-empty on an `addOnly` row (E20). */
  notes: UndoNote[];
  undoStatus: UndoRunItemStatus;
  /** Set only for a row the recheck skipped (`status: 'cancelled'`), `null` otherwise. */
  skippedReason: UndoInRunSkipReason | null;
  /** `null` for `addOnly`. For `full`: the source's entries in the last read before the REMOVE
   *  (F13) — or, before any read ran for the row, the entries the stamped plan verified: exactly
   *  `{ alias }`, the only source a `full` row is ever classified from. */
  sourceEntriesAtRemove: { alias: string }[] | null;
  sevenTvErrorMessage?: string | null;
}

/** The engine's `RunResult` with the undo's rows — `doneKeys` stays the engine's (`status ===
 *  'done'`, a `partial` row included), so the page's settle watcher can read it like any run's. */
export interface UndoRunResult extends RunResult {
  items: UndoRunItem[];
}

/** `'pending'` from the start until the outcome is final (the re-read of `unknown` rows included),
 *  `'settled'` once it is — nothing is reported before (spec 4.4 point 12). Moves in lockstep with
 *  the phase, like the import's. */
export type UndoSettlement = 'pending' | 'settled';

/**
 * One undo run, from `startUndo` to the end state of its last report (#256,
 * `SevenTvRunLifecycle`): `running → settling (re-read) → reporting → closed`, on its own record,
 * whether or not the dock still shows it. `destructive` is fixed at the start: at least one `full`
 * row **after** the service's own origin lock.
 */
export interface UndoRunInfo extends RunRecordBase {
  targetSetId: string;
  expectedChannelName: string | null;
  hostChannelName: string;
  setName: string;
  ownerOrChannelLabel: string;
  trackedChannelName: string | null;
  ownerDisplayName: string | null;
  sourceFile: UndoSourceFileInfo;
  /** Whether the dialog's confirmation for an unproven file was given (spec 17 K2) — the paper trail
   *  in the protocol's `meta`. */
  acknowledgedUnproven: boolean;
  /** The rows the run executes, in queue order — what `startUndo` got, minus what its own locks
   *  took out. */
  rows: readonly UndoPlanRow[];
  /** Every candidate that never became a row: what the caller skipped (dialog, freshness check)
   *  plus what the service's own locks took out (`skippedUnproven`, `duplicateInFile`) — the
   *  protocol's `kind: 'skipped'` rows (spec 17 K4). */
  skipped: readonly UndoSkippedRow[];
  /** What the recheck before each `full` row's REMOVE found, by queue key (see `UndoRowRecheck`). */
  recheck: Readonly<Record<string, UndoRowRecheck>>;
  settlement: UndoSettlement;
  /** `null` while the run is in flight; the engine's snapshot, then the settled outcome. */
  result: UndoRunResult | null;
  /** `sync-deleted` — the source ids of every `full` row whose REMOVE 7TV confirmed. */
  removalReport: SyncReportState;
  removalReportReason: SyncReportReason | null;
  /** `sync-restored` — the target ids of every row with at least one confirmed ADD. */
  restoreReport: SyncReportState;
  restoreReportReason: SyncReportReason | null;
  /** Never a report: never holds the run open (Plan-256 Festlegung 4). `backendTriggered` once
   *  either answer names `expectedChannelName`; `pending`/`succeeded`/`cooldown`/`failed` only from
   *  the N1 fallback. */
  resyncTrigger: ResyncTriggerState;
  abortedForPrivileges: boolean;
  protocolSaved: boolean;
}

/**
 * The dock's counters for one run (spec 4.7), derived from its rows and its skipped candidates —
 * see `summarizeUndoRun`. Row statuses are counted once each: `done`, `partialRows`, `failed`,
 * `unknownCount` and `cancelled` are disjoint, and a row the recheck skipped counts in
 * `skippedInRun` (and under its reason in `skippedByReason`), **not** in `cancelled` — so no skipped
 * row is counted twice.
 */
export interface UndoRunSummary {
  done: number;
  partialRows: number;
  failed: number;
  unknownCount: number;
  /** Rows the engine cancelled (user cancel, privilege abort) — without the recheck's skips. */
  cancelled: number;
  /** Rows the recheck before their REMOVE skipped (`skippedDrift`, `recheckUnavailable`). */
  skippedInRun: number;
  /** Confirmed REMOVEs — `full` rows with `completedSteps >= 1`, whatever they ended as. */
  removedCount: number;
  /** Confirmed ADDs, per entry. */
  restoredCount: number;
  /** `unknown` rows whose REMOVE stayed unanswered (`failedStep === 0`) — recorded only in the
   *  undo's back-out file (spec 11.2). */
  unknownRemovalCount: number;
  /** `full` rows that ended `failed` after their REMOVE: source gone, target not fully back (E9). */
  gapCount: number;
  /** Omitted entries over every row, whatever it ended as (E23). */
  omittedEntryCount: number;
  /** `addOnly` rows that ran next to a foreign target entry (E20). */
  foreignNotedRows: number;
  /** Every skipped candidate by reason: the ones that never became a row plus `skippedInRun`. */
  skippedByReason: Record<UndoSkipReason, number>;
}

type UndoReportKind = 'removal' | 'restore';

/** Per-run state the operation and the settlement share, never exposed. */
interface UndoRunContext {
  rowsByKey: ReadonlyMap<string, UndoPlanRow>;
  /** Rows whose failure reached `abortOn` — a real rejection. A `failed` row missing here was
   *  failed by a `cancel()` after a confirmed step (the engine's mid-row rule). */
  rejectedKeys: Set<string>;
  /** Failed reads before a REMOVE since the last read that answered (spec E19). */
  consecutiveReadFailures: number;
}

/**
 * The fourth destructive 7TV run (#254): undoes a replace from its transfer-run file — per `full`
 * row the REMOVE of the source, then an ADD per missing target entry; per `addOnly` row only the
 * ADDs — over its own `SevenTvRunEngine` (pacing, backoff, token) like the delete, the restore and
 * the import. Zero-knowledge holds: the write token never leaves the browser.
 *
 * What it adds to the engine:
 * - **A recheck before every REMOVE attempt** (E19) through the engine's `beforeStep` hook: a fresh,
 *   complete, tokenless read and the classification of exactly that row again; the REMOVE goes out
 *   only when the row classifies as stamped. Anything else skips the row with a reason, and a read
 *   that fails skips it too (fail-closed); after three failed reads in a row every further `full`
 *   row is skipped without reading. ADD steps and `addOnly` rows are never gated.
 * - **A second origin lock** (spec 17 K2): without `acknowledgedUnproven`, a `full` row from an
 *   unproven (`planned`) file never becomes a queue row, whatever the caller handed in.
 * - **Settling after the run** (spec 4.4 point 12, 4.6): a run with an `unknown` row is re-read once
 *   and each such row cleared up by the operation of its step (E24); `partial` replaces `done` only
 *   (E23, F19).
 * - **Two reports, in order** (spec 4.5, F8): `sync-deleted` for every confirmed REMOVE, then
 *   `sync-restored` for every target with a confirmed ADD — no client resync on success (E16), the
 *   N1 fallback only when every report the run sent failed for good.
 *
 * Settling, the reports and the notice are copied from `SevenTvImportService` (spec 6.5 allows it;
 * extracting them is #256 point 5/6), minus that service's former early return (F20): every run
 * completes run-bound on `SevenTvRunLifecycle` — `reset()` and a newer run only change what is
 * shown, and a report of a detached run lands on its own record.
 *
 * Registers with the `SevenTvRunArbiter` as `'undo'`; it holds no start lock of its own — whether a
 * run may start is the caller's question to the arbiter (spec 11.1, E22).
 */
@Injectable({ providedIn: 'root' })
export class SevenTvUndoService {
  private readonly channelService = inject(ChannelService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  private readonly httpClient = inject(HttpClient);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly translocoService = inject(TranslocoService);

  /** Own engine instance — see the identical note in SevenTvDeleteService. */
  private readonly engine = new SevenTvRunEngine(
    this.httpClient,
    this.tokenService,
    this.translocoService,
  );

  /** Every open run of this service, by id, plus the one the dock shows (#256). */
  private readonly lifecycle = new SevenTvRunLifecycle<UndoRunInfo>(
    'undo',
    (run) => run.removalReport === 'pending' || run.restoreReport === 'pending',
  );

  /** The run this service shows — in flight (`result === null`) or finished. Writable because specs
   *  drive the dock through it; production code only writes through the lifecycle. */
  readonly run: WritableSignal<UndoRunInfo | null> = this.lifecycle.shown;

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;

  /** True while any run of this service re-reads or waits for a report — shown or not (#256,
   *  contract P1). */
  readonly isSettling = this.lifecycle.isSettling;

  /** True while any run with a `full` row is not closed — running, re-reading or reporting, shown
   *  or not (#256, contract P3; AK 15). A pure `addOnly` run never arms it. */
  readonly destructiveOpen = this.lifecycle.destructiveOpen;

  /** The rows to show: the engine's live queue with the shown run's plan rows while it is in
   *  flight, the shown run's own (settled) result afterwards — never the engine's queue of a newer
   *  run. */
  readonly items: Signal<UndoRunItem[]> = computed(() => {
    const shown = this.run();
    if (shown === null) {
      return [];
    }
    return shown.result?.items ?? toUndoItems(this.engine.queue(), shown);
  });

  /** Like the engine's `progress`, except that a row the recheck skipped counts as finished — it
   *  was processed, it just sent nothing. A row the user's `cancel()` stopped does not, as in every
   *  other run. */
  readonly progress: Signal<{ finished: number; total: number }> = computed(() => {
    const recheck = this.run()?.recheck ?? {};
    const queue = this.engine.queue();
    const finished = queue.filter(
      (item) =>
        item.status === 'done' ||
        item.status === 'failed' ||
        item.status === 'unknown' ||
        (item.status === 'cancelled' && (recheck[item.key]?.skippedReason ?? null) !== null),
    ).length;
    return { finished, total: queue.length };
  });

  /** The dock's counters for the shown run (spec 4.7) — see `UndoRunSummary`. */
  readonly summary: Signal<UndoRunSummary> = computed(() =>
    summarizeUndoRun(this.items(), this.run()?.skipped ?? []),
  );

  // The signals below project the shown run's record (#256, Plan-256 Festlegung 14): `linkedSignal`,
  // so they follow `run()` and stay writable for specs that drive a dock directly.

  readonly settlement = linkedSignal<UndoSettlement | null>(() => this.run()?.settlement ?? null);

  readonly removalReport = linkedSignal<SyncReportState>(() => this.run()?.removalReport ?? 'idle');

  readonly removalReportReason = linkedSignal<SyncReportReason | null>(
    () => this.run()?.removalReportReason ?? null,
  );

  readonly restoreReport = linkedSignal<SyncReportState>(() => this.run()?.restoreReport ?? 'idle');

  readonly restoreReportReason = linkedSignal<SyncReportReason | null>(
    () => this.run()?.restoreReportReason ?? null,
  );

  readonly resyncTrigger = linkedSignal<ResyncTriggerState>(
    () => this.run()?.resyncTrigger ?? 'idle',
  );

  /** True once the shown run gave up for missing 7TV write privileges (spec 4.9 point 23). */
  readonly abortedForPrivileges = linkedSignal<boolean>(
    () => this.run()?.abortedForPrivileges ?? false,
  );

  /** Whether the shown run's `finished` protocol was downloaded — the silent `protocolNotSaved`
   *  hint stays until it was (AK 18). */
  readonly protocolSaved = linkedSignal<boolean>(() => this.run()?.protocolSaved ?? false);

  /** True for `UNDO_NOTICE_MS` after a `startUndo` call that skipped at least one candidate —
   *  including a call where nothing runs at all (spec 6.3: "everything skipped" is a transient
   *  notice, no dialog, no run). Lets the dock mount for a call that leaves no run behind. */
  readonly noticePending = signal(false);

  /** The skipped candidates of the last `startUndo` call — what the notice names, by reason. */
  readonly noticeSkipped = signal<readonly UndoSkippedRow[]>([]);

  private noticeTimeout: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    // The one run-service → arbiter edge (#256, contract P4): the arbiter derives "busy" and the
    // tab's unload guard from these three signals; it does not know this service otherwise.
    inject(SevenTvRunArbiter).register({
      kind: 'undo',
      isRunning: this.isRunning,
      isSettling: this.isSettling,
      destructiveOpen: this.destructiveOpen,
    });
  }

  /**
   * Starts an undo run (spec 17 K2 signature). `runnable` are the rows the confirm dialog and the
   * freshness check let through (`UndoPlanRow`s as classified); `skipped` every candidate they left
   * out, with its reason — kept for the protocol and the notice. `acknowledgedUnproven` is the
   * dialog's confirmation for an unproven file.
   *
   * **Second origin lock, before the queue exists:** without `acknowledgedUnproven`, every `full`
   * row whose provenance is `unproven` (on the row or on its candidate) moves to `skipped` as
   * `skippedUnproven` — no queue row, no REMOVE, whatever the caller sent. A source id that occurs
   * in more than one runnable row moves there as `duplicateInFile` (the queue is keyed by it; the
   * classification already excludes this, the service does not rely on it).
   *
   * Nothing left to run ⇒ no run starts; the skipped candidates show as the transient notice.
   * Checking the arbiter is the caller's job. A run that starts is shown at once; the run shown
   * before goes on to close on its own record (#256).
   */
  startUndo(
    target: UndoRunTarget,
    runnable: readonly UndoPlanRow[],
    skipped: readonly UndoSkippedRow[],
    acknowledgedUnproven: boolean,
  ): void {
    const { rows, lockedOut } = applyServiceLocks(runnable, acknowledgedUnproven);
    const allSkipped = [...skipped, ...lockedOut];
    this.showNotice(allSkipped);

    const previousShown = this.lifecycle.shown();
    const runId = this.lifecycle.createRunId();
    const started: UndoRunInfo = {
      runId,
      phase: 'running',
      destructive: rows.some((row) => row.mode === 'full'),
      targetSetId: target.setId,
      expectedChannelName: target.expectedChannelName,
      hostChannelName: target.hostChannelName,
      setName: target.setName,
      ownerOrChannelLabel: target.ownerOrChannelLabel,
      trackedChannelName: target.trackedChannelName,
      ownerDisplayName: target.ownerDisplayName,
      sourceFile: target.sourceFile,
      acknowledgedUnproven,
      rows,
      skipped: allSkipped,
      recheck: {},
      settlement: 'pending',
      result: null,
      removalReport: 'idle',
      removalReportReason: null,
      restoreReport: 'idle',
      restoreReportReason: null,
      resyncTrigger: 'idle',
      abortedForPrivileges: false,
      protocolSaved: false,
    };
    const context: UndoRunContext = {
      rowsByKey: new Map(rows.map((row) => [queueKey(row), row])),
      rejectedKeys: new Set(),
      consecutiveReadFailures: 0,
    };

    // Opened *before* the engine is asked to start (#256 review finding) — see the identical note
    // in SevenTvImportService.startImport.
    this.lifecycle.open(started);
    const engineStarted = this.engine.start(
      target.setId,
      rows.map(toQueueEmote),
      this.createOperation(runId, context),
      (result) => this.onRunComplete(runId, context, result),
    );
    if (!engineStarted) {
      // Refused (nothing to run, engine busy, no token) — take the record back and show whatever
      // was shown before; the notice above stays.
      this.lifecycle.discardUnstarted(runId, previousShown);
    }
  }

  /** Stops the run. A row whose request is in flight ends `unknown` (the operation asks for it,
   *  spec 4.4 point 11), a row stopped after its REMOVE `failed` with `cancelledMidRow`, the rest
   *  `cancelled`; a row whose recheck read is still out ends `cancelled` — no request was sent. */
  cancel(): void {
    this.engine.cancel();
  }

  /** Clears what the dock shows — and only that (#256, spec 6.5, AK 39). The shown run goes on on
   *  its own record: in flight to its end, its re-read, both reports and their answers. The engine's
   *  queue belongs to a run in flight until `finish()` built its result from it, so it is cleared
   *  then (`onRunComplete`), not here. */
  reset(): void {
    if (!this.engine.isRunning()) {
      this.engine.reset();
    }
    this.lifecycle.detach();
    this.showNotice([]);
  }

  /** Same page-follows-user rule as the restore's: only a `closed` run is dropped when the page's
   *  channel is no longer the one the run was started on — a run still running or reporting stays
   *  and completes (Plan-256 Festlegung 13). */
  resetIfChannelChanged(pageChannelName: string): void {
    const current = this.run();
    if (
      current === null ||
      current.phase !== 'closed' ||
      current.hostChannelName === pageChannelName
    ) {
      return;
    }
    this.reset();
  }

  /** Records on the shown run that its `finished` protocol was downloaded. */
  markProtocolSaved(): void {
    const shown = this.run();
    if (shown !== null) {
      this.lifecycle.update(shown.runId, (run) => ({ ...run, protocolSaved: true }));
    }
  }

  /** Manual retry of the shown run's `sync-deleted` (addendum N4): refused while it is pending, for
   *  either channel-mismatch reason (recorded, and for `activeSetDiffers` its resync already runs),
   *  before the run settled and when it confirmed no REMOVE. Never reopens a closed run and never
   *  triggers the N1 fallback. */
  retryRemovalReport(): void {
    this.retryReport('removal');
  }

  /** Manual retry of the shown run's `sync-restored` — same rules as `retryRemovalReport`. */
  retryRestoreReport(): void {
    this.retryReport('restore');
  }

  /** The operation of one run: per row, `full` ⇒ REMOVE of the source, then an ADD per entry;
   *  `addOnly` ⇒ the ADDs. Every lost answer is `unknown` (E10); a privilege failure aborts (E13). */
  private createOperation(runId: string, context: UndoRunContext): RunOperation {
    const rowOf = (emote: RunQueueEmote): UndoPlanRow => planRowOf(context.rowsByKey, emote.key);
    return {
      label: 'undo',
      transportLossIsUnknown: true,
      stepCount: (emote) => stepCountOf(rowOf(emote)),
      buildRequest: (setId, emote, step) => buildUndoRequest(setId, rowOf(emote), step),
      // Gated by the row's mode, not by the step number: step 0 of an `addOnly` row is an ADD.
      beforeStep: (setId, emote, step) => {
        const row = rowOf(emote);
        return row.mode === 'full' && step === 0
          ? this.recheckBeforeRemove(runId, context, setId, row)
          : of(PROCEED);
      },
      abortOn: (failure) => this.onStepFailed(runId, context, failure),
    };
  }

  /**
   * The recheck before one REMOVE attempt (E19): a fresh `loadSevenTvSetEntries` per call — never
   * a shared or earlier read, whose accumulators would come back `complete: false` — with its own
   * deadline. Never throws or errors: every failure becomes a `recheckUnavailable` skip, so the
   * failure counter, the row's reason and the translated text are always this service's own.
   */
  private recheckBeforeRemove(
    runId: string,
    context: UndoRunContext,
    setId: string,
    row: UndoPlanRow,
  ): Observable<StepGate> {
    if (context.consecutiveReadFailures >= MAX_CONSECUTIVE_RECHECK_FAILURES) {
      return of(this.skipRow(runId, row, 'recheckUnavailable', null));
    }
    return loadSevenTvSetEntries(this.httpClient, setId).pipe(
      timeout(RECHECK_READ_TIMEOUT_MS),
      map((read) => this.decideRecheck(runId, context, row, read)),
      catchError(() => of(this.failRecheck(runId, context, row))),
    );
  }

  /** An incomplete read is a failed read — checked *before* the classification, which refuses one. */
  private decideRecheck(
    runId: string,
    context: UndoRunContext,
    row: UndoPlanRow,
    read: SevenTvSetEntries,
  ): StepGate {
    if (!read.complete) {
      return this.failRecheck(runId, context, row);
    }
    context.consecutiveReadFailures = 0;
    const sourceEntries = (read.aliasesById.get(row.candidate.sourceSevenTvEmoteId) ?? []).map(
      (alias) => ({ alias }),
    );
    if (sameClassification(row, classifyUndoRow(row.candidate, read))) {
      this.recordRecheck(runId, row, null, sourceEntries);
      return PROCEED;
    }
    return this.skipRow(runId, row, 'skippedDrift', sourceEntries);
  }

  private failRecheck(runId: string, context: UndoRunContext, row: UndoPlanRow): StepGate {
    context.consecutiveReadFailures += 1;
    return this.skipRow(runId, row, 'recheckUnavailable', null);
  }

  /** Records the skip on the run's record, then answers the engine — `null` entries keep what an
   *  earlier read of the same row recorded. */
  private skipRow(
    runId: string,
    row: UndoPlanRow,
    reason: UndoInRunSkipReason,
    sourceEntries: { alias: string }[] | null,
  ): StepGate {
    this.recordRecheck(runId, row, reason, sourceEntries);
    return { kind: 'skip', errorMessage: this.translocoService.translate(`undo.errors.${reason}`) };
  }

  private recordRecheck(
    runId: string,
    row: UndoPlanRow,
    skippedReason: UndoInRunSkipReason | null,
    sourceEntries: { alias: string }[] | null,
  ): void {
    const key = queueKey(row);
    this.lifecycle.update(runId, (run) => {
      const previous = run.recheck[key]?.sourceEntriesAtRemove ?? stampedSourceEntries(row);
      return {
        ...run,
        recheck: {
          ...run.recheck,
          [key]: { skippedReason, sourceEntriesAtRemove: sourceEntries ?? previous },
        },
      };
    });
  }

  /** `abortOn`: remembers which row this failure belongs to — the one `failed` row not yet
   *  recorded, since the engine calls this right after setting it — and aborts for a missing
   *  privilege, clearing the token for `LACKING_PRIVILEGES` too (the engine already clears it for a
   *  401/403; spec 4.9 point 23). */
  private onStepFailed(runId: string, context: UndoRunContext, failure: RunFailure): boolean {
    const row = this.engine
      .queue()
      .find((item) => item.status === 'failed' && !context.rejectedKeys.has(item.key));
    if (row) {
      context.rejectedKeys.add(row.key);
    }
    const abort = abortsForMissingPrivileges(failure);
    if (abort) {
      if (failure.errorCode === 'LACKING_PRIVILEGES') {
        this.tokenService.clearToken();
      }
      this.lifecycle.update(runId, (run) => ({ ...run, abortedForPrivileges: true }));
    }
    return abort;
  }

  /**
   * Turns the engine's snapshot into the run's outcome, always on the run's own record — there is
   * deliberately no early return for a run nobody shows any more (F20, AK 39). Without an
   * `unknown` row the run settles at once; with one it is `settling` while the target set is read
   * once (`UNDO_SETTLE_READ_TIMEOUT_MS`), and a read that fails, runs out or comes back incomplete
   * leaves those rows `unknown`.
   */
  private onRunComplete(runId: string, context: UndoRunContext, result: RunResult): void {
    const current = this.lifecycle.get(runId);
    if (current === null) {
      // Unreachable: a run is only ever dropped once it is closed, and it cannot close before this.
      return;
    }
    const snapshot: UndoRunResult = {
      ...result,
      items: toUndoItems(result.items, current),
    };
    const hasUnknown = snapshot.items.some((item) => item.status === 'unknown');
    this.lifecycle.update(runId, (run) => ({
      ...run,
      result: snapshot,
      phase: hasUnknown ? 'settling' : run.phase,
    }));
    if (!this.lifecycle.isShown(runId)) {
      // A run `reset()` detached while in flight has left its queue on the engine until now.
      this.engine.reset();
    }

    if (!hasUnknown) {
      this.settleRun(runId, context, null);
      return;
    }
    loadSevenTvSetEntries(this.httpClient, current.targetSetId)
      .pipe(
        timeout(UNDO_SETTLE_READ_TIMEOUT_MS),
        catchError(() => of(null)),
      )
      .subscribe((entries) => this.settleRun(runId, context, entries));
  }

  /** Publishes the settled outcome and opens both reports in the same update — the record goes to
   *  `reporting` (or straight to `closed` with nothing to report) without a moment in which it
   *  looks closed with a report still to come. */
  private settleRun(
    runId: string,
    context: UndoRunContext,
    entries: SevenTvSetEntries | null,
  ): void {
    const snapshot = this.lifecycle.get(runId)?.result ?? null;
    if (snapshot === null) {
      return;
    }
    const translate = (key: string): string => this.translocoService.translate(key);
    const result = settleUndoResult(snapshot, context.rejectedKeys, entries, translate);
    const removed = removedSourceIds(result.items);
    const restored = restoredTargetIds(result.items);
    const settled = this.lifecycle.update(runId, (run) => ({
      ...run,
      result,
      settlement: 'settled',
      phase: 'reporting',
      removalReport: removed.length > 0 ? 'pending' : run.removalReport,
      removalReportReason: removed.length > 0 ? null : run.removalReportReason,
      restoreReport: restored.length > 0 ? 'pending' : run.restoreReport,
      restoreReportReason: restored.length > 0 ? null : run.restoreReportReason,
    }));
    if (settled !== null) {
      this.sendFollowUp(settled, removed, restored);
    }
  }

  /**
   * Both reports of a settled run, `sync-deleted` first and `sync-restored` only once it has an
   * end state (F8: the removal is the destructive fact, its paper trail first); an empty id list
   * sends nothing. No resync of the client's own on an answer (E16): the dock says
   * `backendTriggered` as soon as either answer names `expectedChannelName`. The N1 fallback
   * resyncs `expectedChannelName` once, only when **every** report this run sent failed for good —
   * a run with one report and that one failed included, since then no report reached the backend's
   * resync stage either; a non-active or untracked target (`null`) never gets one.
   */
  private sendFollowUp(run: UndoRunInfo, removed: string[], restored: string[]): void {
    const expected = run.expectedChannelName;
    const answers: (readonly string[] | null)[] = [];
    const afterReport = (resyncTriggered: readonly string[] | null): void => {
      answers.push(resyncTriggered);
      if (
        expected !== null &&
        resyncTriggered !== null &&
        includesChannel(resyncTriggered, expected)
      ) {
        this.patchRun(run.runId, { resyncTrigger: 'backendTriggered' });
      }
    };
    const afterLast = (): void => {
      if (expected !== null && answers.length > 0 && answers.every((answer) => answer === null)) {
        this.triggerResync(run.runId, expected);
      }
    };
    const sendRestored = (): void => {
      if (restored.length === 0) {
        afterLast();
        return;
      }
      this.sendReport(run.runId, 'restore', restored, (resyncTriggered) => {
        afterReport(resyncTriggered);
        afterLast();
      });
    };

    if (removed.length === 0) {
      sendRestored();
      return;
    }
    this.sendReport(run.runId, 'removal', removed, (resyncTriggered) => {
      afterReport(resyncTriggered);
      sendRestored();
    });
  }

  /**
   * One report (`sync-deleted` or `sync-restored`) of a run — the set-centric route, never a
   * channel-bound one (AK 12). Patches the record to `pending` first so a manual retry's button
   * goes away and a closed-but-pending record stays reachable (#256). Classification (`map`) sits
   * before `retry`, one `timeoutReportAttempt` per attempt: every report reaches an end state.
   * `afterReport` gets the answer's `resyncTriggered`, or `null` once it failed for good.
   */
  private sendReport(
    runId: string,
    kind: UndoReportKind,
    sevenTvEmoteIds: string[],
    afterReport?: (resyncTriggered: readonly string[] | null) => void,
  ): void {
    const run = this.patchRun(runId, reportPatch(kind, 'pending', null));
    if (run === null) {
      return;
    }
    const body = { sevenTvEmoteIds, expectedChannelName: run.expectedChannelName };
    const request$: Observable<SyncDeletedInSetResponse | SyncRestoredInSetResponse> =
      kind === 'removal'
        ? this.emoteSetService.reportDeletedInSet(run.targetSetId, body)
        : this.emoteSetService.reportRestoredInSet(run.targetSetId, body);

    request$
      .pipe(
        timeoutReportAttempt(),
        map((answer) => ({
          outcome: classifySyncInSetResponse(answer, sevenTvEmoteIds.length),
          resyncTriggered: answer.resyncTriggered,
        })),
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        next: ({ outcome, resyncTriggered }) => {
          this.endReport(runId, kind, outcome);
          afterReport?.(resyncTriggered);
        },
        error: (error: HttpErrorResponse) => {
          this.endReport(runId, kind, classifySyncInSetFailure(error.status));
          afterReport?.(null);
        },
      });
  }

  /** Writes a report's end state onto its run — which closes it once nothing is pending — and shows
   *  a detached run again when that end state is not a success, so its reason and retry have a
   *  place (Plan-256 Festlegung 13; `reshow` before `showFinishedRows`, rolled back if that refuses —
   *  same as the other three services). */
  private endReport(runId: string, kind: UndoReportKind, outcome: SyncReportOutcome): void {
    const run = this.patchRun(runId, reportPatch(kind, outcome.state, outcome.reason));
    if (run === null || outcome.state === 'succeeded' || this.lifecycle.isShown(runId)) {
      return;
    }
    if (run.result !== null && !this.engine.isRunning() && this.lifecycle.reshow(run)) {
      if (this.engine.showFinishedRows(run.result.items)) {
        return;
      }
      this.lifecycle.detach();
    }
    console.warn('[EmotePurge] 7TV undo report of a run no longer shown did not succeed', {
      runId,
      report: kind,
      state: outcome.state,
      reason: outcome.reason,
    });
  }

  private retryReport(kind: UndoReportKind): void {
    const current = this.run();
    if (current === null || current.settlement !== 'settled' || current.result === null) {
      return;
    }
    const state = kind === 'removal' ? current.removalReport : current.restoreReport;
    const reason = kind === 'removal' ? current.removalReportReason : current.restoreReportReason;
    if (state === 'pending' || isChannelMismatch(reason)) {
      return;
    }
    const ids =
      kind === 'removal'
        ? removedSourceIds(current.result.items)
        : restoredTargetIds(current.result.items);
    if (ids.length > 0) {
      this.sendReport(current.runId, kind, ids);
    }
  }

  /** The N1 fallback's resync — same states as the restore's. */
  private triggerResync(runId: string, channelName: string): void {
    this.patchRun(runId, { resyncTrigger: 'pending' });
    this.channelService.resync(channelName).subscribe({
      next: () => this.patchRun(runId, { resyncTrigger: 'succeeded' }),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run.
        this.patchRun(runId, { resyncTrigger: error.status === 429 ? 'cooldown' : 'failed' }),
    });
  }

  private patchRun(runId: string, patch: Partial<UndoRunInfo>): UndoRunInfo | null {
    return this.lifecycle.update(runId, (run) => ({ ...run, ...patch }));
  }

  /** Sets the notice for `skipped` — or clears it when there is nothing to name. A second call
   *  within the window restarts it (same rule as the restore's `showDuplicateNotice`). */
  private showNotice(skipped: readonly UndoSkippedRow[]): void {
    clearTimeout(this.noticeTimeout);
    this.noticeSkipped.set(skipped);
    if (skipped.length === 0) {
      this.noticePending.set(false);
      return;
    }
    this.noticePending.set(true);
    this.noticeTimeout = setTimeout(() => this.noticePending.set(false), UNDO_NOTICE_MS);
  }
}

/** The dock's counters (spec 4.7) over a run's rows and its skipped candidates — see
 *  `UndoRunSummary` for what is counted where. */
export function summarizeUndoRun(
  items: readonly UndoRunItem[],
  skipped: readonly UndoSkippedRow[],
): UndoRunSummary {
  const summary: UndoRunSummary = {
    done: 0,
    partialRows: 0,
    failed: 0,
    unknownCount: 0,
    cancelled: 0,
    skippedInRun: 0,
    removedCount: 0,
    restoredCount: 0,
    unknownRemovalCount: 0,
    gapCount: 0,
    omittedEntryCount: 0,
    foreignNotedRows: 0,
    skippedByReason: Object.fromEntries(UNDO_SKIP_REASONS.map((reason) => [reason, 0])) as Record<
      UndoSkipReason,
      number
    >,
  };
  for (const row of skipped) {
    summary.skippedByReason[row.reason] += 1;
  }
  for (const item of items) {
    countStatus(summary, item);
    countEffects(summary, item);
  }
  return summary;
}

function countStatus(summary: UndoRunSummary, item: UndoRunItem): void {
  switch (item.undoStatus) {
    case 'done':
      summary.done += 1;
      return;
    case 'partial':
      summary.partialRows += 1;
      return;
    case 'failed':
      summary.failed += 1;
      return;
    case 'unknown':
      summary.unknownCount += 1;
      return;
    case 'cancelled':
      if (item.skippedReason === null) {
        summary.cancelled += 1;
      } else {
        summary.skippedInRun += 1;
        summary.skippedByReason[item.skippedReason] += 1;
      }
      return;
    default:
      // pending / in-progress: still running, counted nowhere yet.
      return;
  }
}

function countEffects(summary: UndoRunSummary, item: UndoRunItem): void {
  const removed = item.mode === 'full' && item.completedSteps >= 1;
  if (removed) {
    summary.removedCount += 1;
  }
  summary.restoredCount += confirmedAdds(item);
  if (item.status === 'unknown' && item.mode === 'full' && item.failedStep === 0) {
    summary.unknownRemovalCount += 1;
  }
  if (removed && item.status === 'failed') {
    summary.gapCount += 1;
  }
  summary.omittedEntryCount += item.omittedEntries.length;
  if (item.mode === 'addOnly' && item.notes.includes('targetHasForeignEntries')) {
    summary.foreignNotedRows += 1;
  }
}

/** Copied from `SevenTvImportService` (module-private there): 401/403 from the HTTP layer, or v4's
 *  `LACKING_PRIVILEGES` over HTTP 200. Every other failure is a row failure; the run goes on. */
function abortsForMissingPrivileges(failure: RunFailure): boolean {
  return (
    failure.httpStatus === 401 ||
    failure.httpStatus === 403 ||
    failure.errorCode === 'LACKING_PRIVILEGES'
  );
}

/**
 * The service's own locks, before the queue exists (spec 17 K2): a `full` row whose provenance is
 * `unproven` — on the row or on its candidate, fail-closed — runs only with `acknowledgedUnproven`;
 * a source id that occurs twice is the queue key twice, so every row carrying it is left out. What
 * a lock takes out carries no live counterpart: the service holds no read.
 *
 * Defence in depth beyond a duplicate source id (Codex review, T4 follow-up): the classification's
 * own step 0 (`duplicateInFileCheck` in `undo-plan.ts`) already refuses a candidate whose source is
 * its own target, or whose source id is another candidate's target id — but `runnable` here is
 * whatever the caller hands `startUndo` (spec 17 K2: the dialog's effective plan after its own
 * origin lock), not necessarily something that went through that check on this exact set. A row
 * whose REMOVE would take the very id another row's ADD is about to restore to (or its own) is
 * locked out the same way, as `duplicateInFile` — one row's REMOVE must never pull the emote
 * another row (or itself) is restoring.
 */
function applyServiceLocks(
  runnable: readonly UndoPlanRow[],
  acknowledgedUnproven: boolean,
): { rows: UndoPlanRow[]; lockedOut: UndoSkippedRow[] } {
  const keyCount = new Map<string, number>();
  for (const row of runnable) {
    keyCount.set(queueKey(row), (keyCount.get(queueKey(row)) ?? 0) + 1);
  }
  const targetIds = new Set(runnable.map((row) => row.candidate.target.sevenTvEmoteId));
  const rows: UndoPlanRow[] = [];
  const lockedOut: UndoSkippedRow[] = [];
  for (const row of runnable) {
    const unproven = row.provenance === 'unproven' || row.candidate.provenance === 'unproven';
    const crossLinked = targetIds.has(row.candidate.sourceSevenTvEmoteId);
    if (row.mode === 'full' && unproven && !acknowledgedUnproven) {
      lockedOut.push(lockedOutRow(row, 'skippedUnproven'));
    } else if ((keyCount.get(queueKey(row)) ?? 0) > 1 || crossLinked) {
      lockedOut.push(lockedOutRow(row, 'duplicateInFile'));
    } else {
      rows.push(row);
    }
  }
  return { rows, lockedOut };
}

function lockedOutRow(row: UndoPlanRow, reason: UndoSkipReason): UndoSkippedRow {
  return {
    candidate: row.candidate,
    reason,
    live: { sourceEntries: [], targetEntries: [] },
    omittedEntries: [],
  };
}

/** The queue key of a row: the source id (spec 4.4 point 10, like the import). */
function queueKey(row: UndoPlanRow): string {
  return row.candidate.sourceSevenTvEmoteId;
}

function toQueueEmote(row: UndoPlanRow): RunQueueEmote {
  return {
    key: queueKey(row),
    sevenTvEmoteId: row.candidate.sourceSevenTvEmoteId,
    name: row.candidate.alias,
  };
}

function planRowOf(rowsByKey: ReadonlyMap<string, UndoPlanRow>, key: string): UndoPlanRow {
  const row = rowsByKey.get(key);
  if (row === undefined) {
    throw new Error(`No undo plan row for queue key ${key}.`);
  }
  return row;
}

/** Derived from the ADD list rather than read off `row.stepCount`, so the steps the engine runs can
 *  never disagree with the requests `buildUndoRequest` can build. */
function stepCountOf(row: Pick<UndoPlanRow, 'mode' | 'adds'>): number {
  return row.mode === 'full' ? 1 + row.adds.length : row.adds.length;
}

/** The request for one step (E6): step 0 of a `full` row is the REMOVE of the source, every other
 *  step the ADD of one target entry under its explicit alias (E21). */
function buildUndoRequest(
  setId: string,
  row: UndoPlanRow,
  step: number,
): { query: string; variables: Record<string, unknown> } {
  if (row.mode === 'full' && step === 0) {
    return {
      query: REMOVE_EMOTE_MUTATION,
      variables: { setId, emoteId: row.candidate.sourceSevenTvEmoteId },
    };
  }
  const add = row.adds[addIndexOf(row.mode, step)];
  return {
    query: ADD_EMOTE_MUTATION,
    variables: { setId, emoteId: row.candidate.target.sevenTvEmoteId, alias: add.alias },
  };
}

/** Which entry of `adds` step `step` sends — by the operation of the step, not its number (E24). */
function addIndexOf(mode: UndoPlanRow['mode'], step: number): number {
  return mode === 'full' ? step - 1 : step;
}

/** A `full` row's source entries before any recheck read ran for it: what the stamped plan
 *  verified (`{ alias }` exactly — nothing else classifies as `full`). */
function stampedSourceEntries(row: Pick<UndoPlanRow, 'candidate'>): { alias: string }[] {
  return [{ alias: row.candidate.alias }];
}

/** The engine's rows with the run's plan rows and recheck findings attached. Total: a row the plan
 *  does not know is left out rather than thrown on (a throw in a computed breaks the panel). */
function toUndoItems(queue: readonly RunQueueItem[], run: UndoRunInfo): UndoRunItem[] {
  const rowsByKey = new Map(run.rows.map((row) => [queueKey(row), row]));
  return queue.flatMap((item) => {
    const row = rowsByKey.get(item.key);
    return row === undefined ? [] : [toUndoItem(item, row, run.recheck[item.key])];
  });
}

function toUndoItem(
  item: RunQueueItem,
  row: UndoPlanRow,
  recheck: UndoRowRecheck | undefined,
): UndoRunItem {
  return {
    ...item,
    candidate: row.candidate,
    mode: row.mode,
    adds: row.adds,
    provenance: row.provenance,
    omittedEntries: row.omittedEntries,
    notes: row.notes,
    undoStatus: item.status,
    skippedReason: item.status === 'cancelled' ? (recheck?.skippedReason ?? null) : null,
    sourceEntriesAtRemove:
      row.mode === 'full' ? (recheck?.sourceEntriesAtRemove ?? stampedSourceEntries(row)) : null,
  };
}

/**
 * The settled outcome (spec 4.6): every `unknown` row cleared up against `entries` where the read
 * allows it, every `failed` row given its undo reason, `partial` set, `doneKeys` recomputed. Works
 * on copies. `entries` is `null` when nothing was `unknown` or the read failed; an incomplete read
 * counts as none.
 */
function settleUndoResult(
  snapshot: UndoRunResult,
  rejectedKeys: ReadonlySet<string>,
  entries: SevenTvSetEntries | null,
  translate: (key: string) => string,
): UndoRunResult {
  const readable = entries?.complete === true ? entries : null;
  const items = snapshot.items.map((item) =>
    withUndoStatus(settleItem(item, rejectedKeys, readable, translate)),
  );
  return {
    ...snapshot,
    items,
    doneKeys: items.filter((item) => item.status === 'done').map((item) => item.key),
  };
}

function settleItem(
  item: UndoRunItem,
  rejectedKeys: ReadonlySet<string>,
  readable: SevenTvSetEntries | null,
  translate: (key: string) => string,
): UndoRunItem {
  if (item.status === 'unknown') {
    return readable === null ? item : settleUnknownRow(item, readable, translate);
  }
  if (item.status === 'failed') {
    return withFailureReason(item, rejectedKeys.has(item.key), translate);
  }
  return item;
}

/** E23/F19: `partial` replaces a `done` of an `addOnly` row with omitted entries — and nothing
 *  else; `failed`, `unknown` and `cancelled` keep their status with the omissions next to it. */
function withUndoStatus(item: UndoRunItem): UndoRunItem {
  const partial =
    item.status === 'done' && item.mode === 'addOnly' && item.omittedEntries.length > 0;
  return { ...item, undoStatus: partial ? 'partial' : item.status };
}

/** A `full` row that failed after its REMOVE left a gap: a rejected step says so as
 *  `removedButNotRestored`, a `cancel()` after the REMOVE (never passed to `abortOn`) as
 *  `cancelledMidRow`. Every other failure keeps the engine's text. */
function withFailureReason(
  item: UndoRunItem,
  rejected: boolean,
  translate: (key: string) => string,
): UndoRunItem {
  if (item.mode !== 'full' || item.completedSteps < 1) {
    return item;
  }
  return withReason(
    item,
    translate(rejected ? 'undo.errors.removedButNotRestored' : 'undo.errors.cancelledMidRow'),
  );
}

/** Spec 4.6 by the operation of the unanswered step (E24): step 0 of a `full` row is the REMOVE,
 *  every other step an ADD. `completedSteps` is only ever raised. */
function settleUnknownRow(
  item: UndoRunItem,
  read: SevenTvSetEntries,
  translate: (key: string) => string,
): UndoRunItem {
  const step = item.failedStep ?? 0;
  if (item.mode === 'full' && step === 0) {
    return settleUnknownRemove(item, read, translate);
  }
  return settleUnknownAdd(item, read, step, translate);
}

function settleUnknownRemove(
  item: UndoRunItem,
  read: SevenTvSetEntries,
  translate: (key: string) => string,
): UndoRunItem {
  const sourceEntries = liveEntries(read, item.candidate.sourceSevenTvEmoteId);
  if (sourceEntries.length === 0) {
    // Applied: the source is gone, and the ADDs never ran — a gap, and a confirmed REMOVE.
    return withReason(
      {
        ...item,
        status: 'failed',
        completedSteps: Math.max(item.completedSteps, 1),
        failedStep: 1,
      },
      translate('undo.errors.removedButNotRestored'),
    );
  }
  if (sourceEntries.length === 1 && sourceEntries[0] === item.candidate.alias) {
    // Not applied: the source still stands exactly as the plan found it.
    return withReason(
      { ...item, status: 'failed', failedStep: 0 },
      translate('undo.errors.unknownOutcome'),
    );
  }
  return item;
}

function settleUnknownAdd(
  item: UndoRunItem,
  read: SevenTvSetEntries,
  step: number,
  translate: (key: string) => string,
): UndoRunItem {
  const add = item.adds[addIndexOf(item.mode, step)];
  if (add === undefined) {
    return item;
  }
  const targetAliases = read.aliasesById.get(item.candidate.target.sevenTvEmoteId) ?? [];
  if (targetAliases.includes(add.alias)) {
    const completedSteps = Math.max(item.completedSteps, step + 1);
    if (completedSteps >= stepCountOf(item)) {
      return { ...item, status: 'done', completedSteps, failedStep: null, errorMessage: undefined };
    }
    // Confirmed, but the ADDs after it never ran.
    return withReason(
      { ...item, status: 'failed', completedSteps, failedStep: completedSteps },
      translate(gapReasonKey(item.mode, completedSteps)),
    );
  }
  return withReason(
    { ...item, status: 'failed', failedStep: step },
    translate(gapReasonKey(item.mode, item.completedSteps)),
  );
}

/** Why a row is short of its target after the re-read: a `full` row lost its source
 *  (`removedButNotRestored`), an `addOnly` row that confirmed some ADDs but not all is incomplete,
 *  and one that confirmed none was simply not applied. */
function gapReasonKey(mode: UndoPlanRow['mode'], completedSteps: number): string {
  if (mode === 'full') {
    return 'undo.errors.removedButNotRestored';
  }
  return completedSteps >= 1 ? 'undo.errors.restoreIncomplete' : 'undo.errors.unknownOutcome';
}

/** Replaces the displayed text with an undo reason and keeps what the engine had. */
function withReason(item: UndoRunItem, reason: string): UndoRunItem {
  return { ...item, errorMessage: reason, sevenTvErrorMessage: item.errorMessage ?? null };
}

/** `entriesOf(id)` of spec 4.3: the named aliases, then `null` once for an aliasless entry. */
function liveEntries(read: SevenTvSetEntries, id: string): (string | null)[] {
  const entries: (string | null)[] = [...(read.aliasesById.get(id) ?? [])];
  if (read.aliaslessIds.has(id)) {
    entries.push(null);
  }
  return entries;
}

/** The ADDs of `item` 7TV confirmed: on a `full` row every step after the REMOVE. */
function confirmedAdds(item: UndoRunItem): number {
  return item.mode === 'full' ? Math.max(0, item.completedSteps - 1) : item.completedSteps;
}

/** Source ids of every `full` row whose REMOVE 7TV confirmed, whatever the row ended as — what
 *  `sync-deleted` names (spec 4.5 point 13, 16). Never an `addOnly` row (E24). */
function removedSourceIds(items: readonly UndoRunItem[]): string[] {
  const ids = items
    .filter((item) => item.mode === 'full' && item.completedSteps >= 1)
    .map((item) => item.candidate.sourceSevenTvEmoteId);
  return [...new Set(ids)];
}

/** Target ids of every row with at least one confirmed ADD — what `sync-restored` names (spec 4.5
 *  point 14). */
function restoredTargetIds(items: readonly UndoRunItem[]): string[] {
  const ids = items
    .filter((item) => confirmedAdds(item) >= 1)
    .map((item) => item.candidate.target.sevenTvEmoteId);
  return [...new Set(ids)];
}

function reportPatch(
  kind: UndoReportKind,
  state: SyncReportState,
  reason: SyncReportReason | null,
): Partial<UndoRunInfo> {
  return kind === 'removal'
    ? { removalReport: state, removalReportReason: reason }
    : { restoreReport: state, restoreReportReason: reason };
}

/** Whether `channelName` is among the channels an answer says the backend resynced —
 *  case-insensitive, since the backend answers with normalized names. */
function includesChannel(channels: readonly string[], channelName: string): boolean {
  const normalized = channelName.toLowerCase();
  return channels.some((channel) => channel.toLowerCase() === normalized);
}
