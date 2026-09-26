import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { DestroyRef, WritableSignal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, finalize, timeout } from 'rxjs';

import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvSetEntries, loadSevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { SevenTvUndoService, UndoRunTarget } from '../../core/seven-tv/seven-tv-undo.service';
import { UndoCandidate, UndoSourceFileInfo } from '../../core/seven-tv/undo-candidate';
import {
  UndoPlanRow,
  UndoSkippedRow,
  classifyUndoRows,
  diffUndoPlans,
  undoLiveCounterpart,
} from '../../core/seven-tv/undo-plan';
import { FileImportResult } from './file-import-step';
import { LIVE_READ_TIMEOUT_MS } from './recovery-file-gate';
import { ResolvedRestoreTarget } from './restore-flow';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';
import { UndoConfirmOutcome, openUndoConfirmDialog } from './undo-confirm-dialog';

/** What `FileImportStep` reports when the user picks "undo the replacements" at the switch. */
export type TransferUndoFileResult = Extract<FileImportResult, { kind: 'transfer-undo' }>;

/**
 * Everything the undo flow needs, handed in rather than injected — same reasoning as
 * `RestoreFlowDeps` (see there): the flow opens dialogs from `shared/` and drives a service from
 * `core/`, and holds no state between calls. The confirm dialog injects its own collaborators (the
 * slot preview's), so they are not listed here.
 */
export interface UndoFlowDeps {
  dialog: Dialog;
  /** For the flow's own two tokenless 7TV set reads — the first read (spec 17 K3) and the freshness
   *  check right before the start (E14). */
  httpClient: HttpClient;
  tokenService: SevenTvTokenService;
  undoService: SevenTvUndoService;
  arbiter: SevenTvRunArbiter;
  /** `true` from the moment the first read goes out until it has answered (or failed, or timed
   *  out, or the caller was torn down) — the caller folds it into whatever button opens this flow,
   *  so a second click cannot start a second flow whose dialog would stack on the first. The flow
   *  also refuses to start while it is `true`. */
  firstReadPending: WritableSignal<boolean>;
  /** The caller's own teardown: both reads are dropped with it, so a late answer can neither open a
   *  dialog nobody sees nor start a run nobody confirmed any more. */
  destroyRef: DestroyRef;
}

/**
 * The undo's run target (spec 6.5) from the target the file step's pre-check resolved: the
 * restore's derivation (`restoreStartTarget`) without `resyncChannelName` — a non-active target gets
 * no client resync any more (E16, #255) — plus the three fields the `finished` protocol needs
 * (`trackedChannelName`, `ownerDisplayName`, `sourceFile`). `ownerDisplayName` is the very value
 * the confirm dialog writes into the recovery file as `targetOwnerDisplayName`, so both files of one
 * undo name the same owner.
 */
export function undoRunTarget(
  target: ResolvedRestoreTarget,
  sourceFile: UndoSourceFileInfo,
): UndoRunTarget {
  const channel = target.trackedChannelName;
  return {
    setId: target.emoteSetId,
    expectedChannelName: channel !== null && target.isActiveSet ? channel : null,
    hostChannelName: target.hostChannelName,
    setName: target.setName,
    ownerOrChannelLabel: channel ?? target.ownerDisplayName,
    trackedChannelName: channel,
    ownerDisplayName: target.ownerDisplayName,
    sourceFile,
  };
}

/**
 * Confirms and starts one undo of a transfer-run file's replacements (spec 4.2 point 5, 6.3):
 * **arbiter → token → first read → confirmation → arbiter → token → freshness check → start.**
 *
 * - **Arbiter first.** Busy (a run of any kind running, or finishing its reports) ⇒ the arbiter's
 *   own transient notice (`noteRefusedStart('undo')`), no token prompt, no request. The flow has no
 *   lock of its own and asks nothing else about another run's state (E22, AK 32).
 * - **Token before the confirmation** (E13): between saving the recovery file and "Starten" there
 *   is no prompt, so the window for drift stays small.
 * - **The first read belongs to the flow** (spec 17 K3): one tokenless read of the target, bounded
 *   like every other live read. Classified against it, nothing to run ⇒ no dialog: the undo
 *   service's transient notice names every skipped candidate with its reason (AK 5, 21) — through
 *   a `startUndo` call with no runnable row, which starts nothing. A failed or incomplete read ⇒ the
 *   dialog opens in its error state (`initialRead: null`) and releases nothing until "Ziel neu
 *   laden" brings a complete one (AK 7). Otherwise the dialog opens on that read and only reads
 *   again on "Ziel neu laden".
 * - **After the confirmation** — a falsy result is a cancel — the arbiter again (another run can
 *   have started while the dialog was open), the token again (a run's privilege abort can have
 *   cleared it; a new prompt then leads back to the arbiter), then the freshness check (E14): a new
 *   read, the whole file classified again, the confirmed rows compared against it
 *   (`diffUndoPlans`). What drifted is skipped as `skippedDrift` with its live counterpart; a failed
 *   or incomplete read lets no row with a REMOVE run (`recheckUnavailable`), while the ADD-only rows
 *   run as confirmed (AK 8). The arbiter and the token are checked once more right before the start,
 *   because that read took time.
 * - **The start** passes the fresh rows, every skipped candidate in file order, and the dialog's
 *   confirmation flag unchanged — the service applies the origin lock itself (spec 17 K2) and shows
 *   its own notice for what it skipped; this flow shows none of its own.
 *
 * `result` is the value frozen when the file step emitted it; nothing here re-reads a live signal.
 */
export function startUndoFlow(deps: UndoFlowDeps, result: TransferUndoFileResult): void {
  if (refusedByArbiter(deps) || deps.firstReadPending()) {
    return;
  }
  withToken(deps, () => readAndConfirm(deps, result));
}

/** The first read (spec 17 K3) and what follows from it: a notice, or the confirm dialog. */
function readAndConfirm(deps: UndoFlowDeps, result: TransferUndoFileResult): void {
  if (deps.firstReadPending()) {
    return;
  }
  deps.firstReadPending.set(true);
  readTarget(deps, result.target, () => deps.firstReadPending.set(false)).subscribe({
    next: (read) => openConfirmation(deps, result, read.complete ? read : null, Date.now()),
    error: () => openConfirmation(deps, result, null, Date.now()),
  });
}

function openConfirmation(
  deps: UndoFlowDeps,
  result: TransferUndoFileResult,
  initialRead: SevenTvSetEntries | null,
  initialReadAt: number,
): void {
  if (initialRead !== null) {
    const plan = classifyUndoRows(result.candidates, initialRead);
    if (plan.rows.length === 0) {
      // Nothing to confirm: the service's own notice says why, and nothing starts.
      deps.undoService.startUndo(
        undoRunTarget(result.target, result.sourceFile),
        [],
        plan.skipped,
        false,
      );
      return;
    }
  }
  openUndoConfirmDialog(deps.dialog, {
    candidates: result.candidates,
    target: result.target,
    sourceFile: result.sourceFile,
    initialRead,
    initialReadAt,
  }).closed.subscribe((outcome) => {
    if (outcome) {
      confirmStart(deps, result, outcome);
    }
  });
}

/** Everything between a confirmed dialog and the service: arbiter, token, freshness check. */
function confirmStart(
  deps: UndoFlowDeps,
  result: TransferUndoFileResult,
  outcome: UndoConfirmOutcome,
): void {
  if (refusedByArbiter(deps)) {
    return;
  }
  withToken(deps, () => {
    // Re-entered after a fresh prompt: the arbiter may have changed meanwhile.
    if (refusedByArbiter(deps)) {
      return;
    }
    readTarget(deps, result.target).subscribe({
      next: (fresh) => startChecked(deps, result, outcome, fresh.complete ? fresh : null),
      error: () => startChecked(deps, result, outcome, null),
    });
  });
}

function startChecked(
  deps: UndoFlowDeps,
  result: TransferUndoFileResult,
  outcome: UndoConfirmOutcome,
  fresh: SevenTvSetEntries | null,
): void {
  if (refusedByArbiter(deps)) {
    return;
  }
  if (!deps.tokenService.hasToken()) {
    // Cleared while the read was out — ask again, and check everything again after it.
    confirmStart(deps, result, outcome);
    return;
  }
  const checked = freshnessCheck(result.candidates, outcome, fresh);
  deps.undoService.startUndo(
    undoRunTarget(result.target, result.sourceFile),
    checked.runnable,
    inFileOrder(result.candidates, [...outcome.skipped, ...checked.skipped]),
    outcome.acknowledgedUnproven,
  );
}

/**
 * E14's second check point: the confirmed rows against a read taken right before the start. Never
 * classifies an incomplete read (`fresh` is `null` for a failed or incomplete one): then every row
 * that would REMOVE is skipped as `recheckUnavailable` — described by the read the dialog confirmed
 * — and the ADD-only rows run as confirmed. Otherwise the whole file is classified again (so the
 * duplicate check sees every candidate, as it did in the dialog) and only rows whose classification
 * is unchanged run, in their fresh form; the rest are `skippedDrift`, described by the fresh read.
 */
function freshnessCheck(
  candidates: readonly UndoCandidate[],
  outcome: UndoConfirmOutcome,
  fresh: SevenTvSetEntries | null,
): { runnable: UndoPlanRow[]; skipped: UndoSkippedRow[] } {
  if (fresh === null) {
    return {
      runnable: outcome.runnable.filter((row) => row.mode === 'addOnly'),
      skipped: outcome.runnable
        .filter((row) => row.mode === 'full')
        .map((row) => skippedRow(row.candidate, 'recheckUnavailable', outcome.read)),
    };
  }
  const diff = diffUndoPlans({ rows: outcome.runnable }, classifyUndoRows([...candidates], fresh));
  return {
    runnable: diff.runnable,
    skipped: diff.drifted.map((row) => skippedRow(row.candidate, 'skippedDrift', fresh)),
  };
}

function skippedRow(
  candidate: UndoCandidate,
  reason: 'skippedDrift' | 'recheckUnavailable',
  read: SevenTvSetEntries,
): UndoSkippedRow {
  return { candidate, reason, live: undoLiveCounterpart(candidate, read), omittedEntries: [] };
}

/** Skipped rows from the dialog and from the freshness check, merged back into the file's order —
 *  the order the notice and the protocol list them in. A row whose candidate is not among
 *  `candidates` (not expected) keeps its place at the end. */
function inFileOrder(
  candidates: readonly UndoCandidate[],
  skipped: readonly UndoSkippedRow[],
): UndoSkippedRow[] {
  const position = new Map(candidates.map((candidate, index) => [candidate, index]));
  const at = (row: UndoSkippedRow) => position.get(row.candidate) ?? candidates.length;
  return [...skipped].sort((a, b) => at(a) - at(b));
}

/** One live read of the target, bounded and dropped with the caller. */
function readTarget(
  deps: UndoFlowDeps,
  target: ResolvedRestoreTarget,
  onSettled: () => void = () => undefined,
): Observable<SevenTvSetEntries> {
  return loadSevenTvSetEntries(deps.httpClient, target.emoteSetId).pipe(
    timeout(LIVE_READ_TIMEOUT_MS),
    takeUntilDestroyed(deps.destroyRef),
    finalize(onSettled),
  );
}

/** Whether another run holds the arbiter — noted as the arbiter's transient notice if so. */
function refusedByArbiter(deps: UndoFlowDeps): boolean {
  if (deps.arbiter.activeRun() === null) {
    return false;
  }
  deps.arbiter.noteRefusedStart('undo');
  return true;
}

/** Runs `next` with a stored token, prompting for one first if there is none; a cancelled prompt
 *  ends the flow. */
function withToken(deps: UndoFlowDeps, next: () => void): void {
  if (deps.tokenService.hasToken()) {
    next();
    return;
  }
  openSevenTvTokenPromptDialog(deps.dialog).closed.subscribe((saved) => {
    if (saved === true) {
      next();
    }
  });
}
