import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, linkedSignal, signal, WritableSignal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { MonoTypeOperatorFunction, Observable, map, retry, throwError, timeout, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import { SyncDeletedInSetResponse } from './seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from './seven-tv-emote-set.service';
import {
  RunItemStatus,
  RunOperation,
  RunQueueEmote,
  RunQueueItem,
  RunResult,
  SevenTvRunEngine,
} from './seven-tv-run-engine';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { RunRecordBase, SevenTvRunLifecycle } from './seven-tv-run-lifecycle';
import { SevenTvTokenService } from './seven-tv-token.service';
import {
  SyncReportOutcome,
  SyncReportReason,
  SyncReportState,
  classifySyncInSetFailure,
  classifySyncInSetResponse,
  isChannelMismatch,
} from './sync-report-outcome';

/** Kept under its historical name — the engine's constant is the same value. */
export { RUN_DELAY_MS as DELETE_DELAY_MS } from './seven-tv-run-engine';
// Exported for the restore service, which reports its run with the identical policy.
export const MAX_AUTOMATIC_SYNC_RETRIES = 2;
// Multiplied by the attempt number, so the two automatic attempts land at 2s and 4s. Kept short on
// purpose: the deletions themselves are already done, the admin is waiting on a verdict, and a
// manual retry button covers the cases a short backoff cannot.
export const SYNC_RETRY_DELAY_MS = 2000;
/** Time budget of one attempt of a closing report (`sync-imported`, `sync-deleted`,
 *  `sync-restored`) — #256, Plan-256 Festlegung 15. A report that never answers would otherwise
 *  keep its run open for good: never `closed`, never closable, and for a destructive run the tab's
 *  unload guard armed forever. More generous than the 20 s re-read, because the report kicks off
 *  server-side resync steps. An attempt that runs out counts as a transient failure (see
 *  {@link timeoutReportAttempt}), so the same retries follow, then `failed`/`unavailable`. */
export const REPORT_TIMEOUT_MS = 30_000;

/** Ends one attempt of a closing report after {@link REPORT_TIMEOUT_MS} with a status-`0`
 *  `HttpErrorResponse` — the shape of a network failure, so the retry policy retries it and
 *  `classifySyncInSetFailure` reads it as `'unavailable'`. Placed *before* the retry operator, so
 *  every attempt gets its own budget. */
export function timeoutReportAttempt<T>(): MonoTypeOperatorFunction<T> {
  return timeout<T, Observable<never>>({
    first: REPORT_TIMEOUT_MS,
    with: () =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 0,
            statusText: `No answer within ${REPORT_TIMEOUT_MS} ms`,
          }),
      ),
  });
}

/** How long a confirmed delete that never became a run keeps `confirmedRunPending` set, so the
 *  panel's abort notice ("Nothing was deleted." plus the reason) can still be read after the host's
 *  dock lost every other reason to stay mounted. Longer than the restore/import services' 4 s
 *  duplicate notice: this one reports that an irreversible action the user explicitly confirmed did
 *  *not* happen, and it is two sentences rather than a count. Self-clearing for the same reason
 *  those are (docs/UI-Designsprache.md §4.5) — there is no run or queue for a dismiss button to
 *  attach to. */
export const ABORTED_DELETE_NOTICE_MS = 8000;

/** v4 dropped the `action` enum in favour of one field per operation; the emote travels inside the
 *  `EmoteSetEmoteId` input object rather than as a sibling argument, and variable types are `Id!`
 *  instead of `ObjectID!`. Removal has no alias, unlike `addEmote` in the import/restore services.
 *  One `removeEmote` takes every entry of the id. Shared with the import run's replace rows. */
export const REMOVE_EMOTE_MUTATION = `
  mutation RemoveEmote($setId: Id!, $emoteId: Id!) {
    emoteSets {
      emoteSet(id: $setId) {
        removeEmote(id: { emoteId: $emoteId }) {
          id
        }
      }
    }
  }
`;

/** The one thing that makes this run a *delete* — everything else lives in the engine. */
const REMOVE_OPERATION: RunOperation = {
  label: 'mass delete',
  buildRequest: (setId, emote) => ({
    query: REMOVE_EMOTE_MUTATION,
    variables: { setId, emoteId: emote.sevenTvEmoteId },
  }),
};

/** The public input contract for a delete/restore run — deliberately its own interface, not an
 *  alias of the engine's `RunQueueEmote`: a caller here never has to think about a queue `key`.
 *  `startDelete`/`startRestore` mint it themselves from the 7TV id (spec #200, 7.2), so the run's
 *  identity never depends on whether the row has a local `Emote.Id`. */
export interface DeleteQueueEmote {
  /** Local `Emote.Id` — optional (spec #200, 7.2): a live member of a non-active set may never
   *  have had a row here. Only ever written into the purge protocol; the run does not read it. */
  emoteId?: string;
  sevenTvEmoteId: string;
  name: string;
  /** Every alias the emote sits under in the set — two for a #74 duplicate cell. The delete
   *  records them in the protocol; the restore sends one `ADD` per alias. Omitted means `[name]`. */
  aliases?: readonly string[];
}
/** Historical aliases — the panel and both host pages import these names. */
export type DeleteItemStatus = RunItemStatus;
export type DeleteQueueItem = RunQueueItem;

/**
 * One delete run, from the moment it starts to the moment its closing report reaches an end state
 * (#256, `SevenTvRunLifecycle`): `running → reporting → closed` on its own record — a delete never
 * re-reads, so it never sees `settling`. `runId` is the identity a late answer or a manual retry
 * finds it by; the record is replaced by a new object on every change, never mutated. `destructive`
 * is always `true` (Plan-256 Festlegung 6): every delete row removes something from the set, so a
 * delete run arms the tab's unload guard from `startDelete` until `closed`, the same way an
 * import's `replace` plan does.
 */
export interface DeleteRunInfo extends RunRecordBase {
  /** The channel of the page the run was started on — the purge protocol's envelope
   *  `channelName`, its filename and `resetIfChannelChanged` read it (spec 6.5). No longer the
   *  addressee of the report: that is the set (`setId`), with `expectedChannelName` beside it. */
  channelName: string;
  /** The tracked channel the report expects to touch (spec 4.6 point 21, E18): the page's channel
   *  when the run's set is its active one, otherwise `null`. Frozen with the run, sent with the
   *  report and every retry, and the channel of the fallback resync after a first report that
   *  failed for good (addendum N1). */
  expectedChannelName: string | null;
  /** The set the run removes from, frozen when it starts (spec #200, 7.2, AK 71): the first
   *  report and every retry name this set, whatever the page's set dropdown shows by then. */
  setId: string;
  /** `null` while the run is in flight; set once the engine reports the run complete. */
  result: RunResult | null;
  /** This run's `sync-deleted` report — `SevenTvDeleteService.syncReport` projects it for the
   *  shown run. */
  syncReport: SyncReportState;
  /** Projected by `syncReportReason`. */
  syncReportReason: SyncReportReason | null;
}

@Injectable({ providedIn: 'root' })
export class SevenTvDeleteService {
  private readonly channelService = inject(ChannelService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);

  /** Own engine instance (not a shared singleton), so `isRunning` can never mean "the *other*
   *  service is busy". All pacing/backoff/token mechanics live there — see SevenTvRunEngine. */
  private readonly engine = new SevenTvRunEngine(
    inject(HttpClient),
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  /** Every open run of this service, by id, plus the one the dock shows (#256). */
  private readonly lifecycle = new SevenTvRunLifecycle<DeleteRunInfo>(
    'delete',
    (run) => run.syncReport === 'pending',
  );

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** The run this service is currently showing — in flight (`result === null`) or finished. Its
   *  record is replaced by a new object on every change; `runId` is its identity. Writable because
   *  specs drive the dock through it; production code only writes through the lifecycle. */
  readonly run: WritableSignal<DeleteRunInfo | null> = this.lifecycle.shown;

  /** True while any run of this service reports — shown or not (#256, contract P1). A delete never
   *  re-reads, so this is exactly "reporting", never "settling". */
  readonly isSettling = this.lifecycle.isSettling;

  /** True while any run of this service is not `closed` — every delete row is destructive (Plan-256
   *  Festlegung 6), so this holds from `startDelete` to `closed`, shown or not (#256, contract P3).
   *  The arbiter's unload guard (the union over every run service) reads it: until the run
   *  closes, its report has not reached an end state, and closing the tab could lose it. */
  readonly destructiveOpen = this.lifecycle.destructiveOpen;

  /** State of the shown run's closing sync-deleted call. `linkedSignal` projection of the shown
   *  record (#256, Plan-256 Festlegung 14) — writable so specs can drive a dock directly; production
   *  code never writes it, only the record. Consumers must wait for a terminal value before
   *  optimistically removing rows: 'failed'/'partial' means the backend does not (fully) know about
   *  the deletion yet, so filtering the list client-side would show a state that isn't real. */
  readonly syncReport = linkedSignal<SyncReportState>(() => this.run()?.syncReport ?? 'idle');

  /** Why `syncReport` is `'failed'`/`'partial'` (spec E23), `null` otherwise — the dock shows it as
   *  its own line under the report notice. Projection, like `syncReport`. */
  readonly syncReportReason = linkedSignal<SyncReportReason | null>(
    () => this.run()?.syncReportReason ?? null,
  );

  /** The finished run, kept for the summary/protocol UI (A6) — unchanged shape for
   *  `mass-delete-panel.ts`/`usage-stats-page.ts` (#256 Naht 2.4). Projection of `run()`: `null`
   *  while a run is in flight or nothing is shown, the frozen `{setId, channelName, result}` once
   *  the shown run has a result — `result` keeps the identity `onRunComplete` gave it across every
   *  later report patch, which is what `usage-stats-page.ts`'s `watchRunSettle` dedupes on. */
  readonly lastRun = linkedSignal<{ setId: string; channelName: string; result: RunResult } | null>(
    () => {
      const shown = this.run();
      return shown === null || shown.result === null
        ? null
        : { setId: shown.setId, channelName: shown.channelName, result: shown.result };
    },
  );

  /**
   * A delete the user is deciding on, or has decided on, that is not (yet) a run: `MassDeletePanel`
   * has the confirmation open, its pre-run live alias read is out, or that read has just ended in an
   * abort whose notice is the only outcome there is to show. None of those show up in
   * `isRunning`/`queue`, which is the problem this exists to solve — the host dock's own gate
   * (`action-dock.ts`, `usage-stats-page.ts`'s `dockVisible`) counts marked items and shown panels,
   * so a pushed reload that prunes every marked key unmounts the dock and takes `MassDeletePanel`
   * down with it. The CDK dialog is opened without a `viewContainerRef`, so it survives that and the
   * user still clicks Delete — against a destroyed panel, which by contract starts nothing
   * (`abortReasonBeforeStart`) and has no view left to say so on: no `REMOVE` sent, no notice,
   * nothing. The dock treats this claim exactly like an in-flight run, the same role
   * `duplicateNoticePending` plays for a fully-refused restore/import.
   *
   * The claim is therefore taken when the **confirmation opens**, not when the read starts: the
   * window that must be survived begins with the modal, and the no-read branch has no read to hang
   * it on at all.
   *
   * Written only through `beginConfirmedRun`/`endConfirmedRun`/`clearConfirmedRun` below.
   */
  readonly confirmedRunPending = signal(false);

  private confirmedRunTimeout: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    // The one run-service → arbiter edge (#256, contract P4): the arbiter derives "busy" and the
    // tab's unload guard from these three signals; it does not know this service otherwise.
    inject(SevenTvRunArbiter).register({
      kind: 'delete',
      isRunning: this.isRunning,
      isSettling: this.isSettling,
      destructiveOpen: this.destructiveOpen,
    });
  }

  /** The delete confirmation is open — hold the dock (and the panel inside it) until one of the two
   *  releases below. Every exit of the confirmation has to reach one of them. */
  beginConfirmedRun(): void {
    clearTimeout(this.confirmedRunTimeout);
    this.confirmedRunPending.set(true);
  }

  /**
   * The confirmed delete has been attempted and was either started or aborted. A started run carries
   * the dock by itself from here (`isRunning`/`queue`), so the claim is dropped at once; an abort has
   * nothing but its notice, so the claim is held for `ABORTED_DELETE_NOTICE_MS` and then dropped.
   * Asking `isRunning()` rather than taking the answer as a parameter keeps every caller from having
   * to agree on what "started" means.
   */
  endConfirmedRun(): void {
    clearTimeout(this.confirmedRunTimeout);
    // Only a claim that is still held may be extended into a notice window. Something else can have
    // dropped it from under this delete while its read was out — `reset()`, or the workspace
    // switching channel — and in both cases the dock it belonged to is gone; re-arming a timer here
    // would pin an empty one somewhere the aborted delete never belonged.
    if (!this.confirmedRunPending()) {
      return;
    }
    if (this.isRunning()) {
      this.confirmedRunPending.set(false);
      return;
    }
    this.confirmedRunTimeout = setTimeout(
      () => this.confirmedRunPending.set(false),
      ABORTED_DELETE_NOTICE_MS,
    );
  }

  /** Drops the claim at once, without the notice window `endConfirmedRun` grants: nothing was
   *  confirmed and nothing has to be read, so keeping an otherwise empty dock up for 8 s would be
   *  exactly the empty bar `actionDockHasContent` exists to prevent. Three cases: the dismissed
   *  confirmation, `reset()`, and the channel workspace moving to another channel — the last two
   *  because an abort notice belongs to the dock it was raised in and to no other. */
  clearConfirmedRun(): void {
    clearTimeout(this.confirmedRunTimeout);
    this.confirmedRunPending.set(false);
  }

  /** `expectedChannelName` is `channelName` when `setId` is the page's active set, `null`
   *  otherwise (spec 6.5) — the caller knows which, this service does not. A run that starts is
   *  shown at once; the run shown before it goes on to close on its own record (#256). */
  startDelete(
    setId: string,
    channelName: string,
    emotes: DeleteQueueEmote[],
    expectedChannelName: string | null,
  ): void {
    const previousShown = this.lifecycle.shown();
    const runId = this.lifecycle.createRunId();
    const started: DeleteRunInfo = {
      runId,
      phase: 'running',
      destructive: true,
      channelName,
      expectedChannelName,
      setId,
      result: null,
      syncReport: 'idle',
      syncReportReason: null,
    };
    // Opened *before* the engine is asked to start (#256 review finding): the engine answers
    // asynchronously in practice, but only this ordering guarantees that a synchronous
    // `onComplete` — however unlikely — always finds its record already registered, rather than
    // updating a run the lifecycle does not know about yet, which would then never close.
    this.lifecycle.open(started);
    const engineStarted = this.engine.start(
      setId,
      toDeleteQueue(emotes),
      REMOVE_OPERATION,
      (result) => this.onRunComplete(runId, result),
    );
    if (!engineStarted) {
      // Refused (already running, empty list, no token) — take the just-opened record back and
      // restore whatever was shown before it, which may be another run still settling its report.
      this.lifecycle.discardUnstarted(runId, previousShown);
    }
  }

  cancel(): void {
    this.engine.cancel();
  }

  /** Clears what the dock shows — and only that (#256, Plan-256 Festlegung 3). The shown run goes
   *  on on its own record: a run still in flight runs to its end (never cancelled here: a request
   *  7TV may already have applied must still be reported), and its report goes out and is answered.
   *  The engine's queue belongs to a run in flight until `finish()` has built its result from it,
   *  so it is cleared then (`onRunComplete`), not here. */
  reset(): void {
    if (!this.engine.isRunning()) {
      this.engine.reset();
    }
    this.lifecycle.detach();
    // The restore service clears its own transient notice flag here for the same reason: whatever
    // this dock was still holding open, the user has dismissed it.
    this.clearConfirmedRun();
  }

  /** The panel is a root-service singleton, so a finished run used to follow the user into the
   *  next channel's workspace, still showing the previous channel's counts. Since #256 (Plan-256
   *  Festlegung 13) this only fires for a `closed` run: a run still reporting follows the user for
   *  the few seconds until its report reaches an end state — dropping it mid-report would leave a
   *  later `failed` with no dock to show it and no retry to reach it (Codex-Befund 2). */
  resetIfChannelChanged(channelName: string): void {
    const current = this.run();
    if (current === null || current.phase !== 'closed' || current.channelName === channelName) {
      return;
    }
    this.reset();
  }

  /** Manual retry for the closing report. The 7TV deletions are long done at this point, so this
   *  only re-sends the bookkeeping call — safe to repeat, ids already archived still count.
   *  Set, expected channel *and* keys come from the same record, so a retry can never mix one
   *  run's ids with another's target or with a set chosen after the run started (R15, AK 71). A
   *  retry on a closed run does not reopen it (#256): it is a new report on a closed run, and
   *  neither the arbiter nor the unload guard sees it. */
  retrySyncReport(): void {
    const current = this.run();
    if (
      current === null ||
      current.syncReport === 'pending' ||
      // addendum N4, AK 40: either channel-mismatch reason is recorded and, for
      // activeSetDiffers, its resync already runs — a retry could only write the same mismatch
      // again.
      isChannelMismatch(current.syncReportReason) ||
      current.result === null ||
      current.result.doneKeys.length === 0
    ) {
      return;
    }

    this.reportDeleted(current.runId, current.result.doneKeys);
  }

  /** Turns the engine's snapshot into the run's outcome, always on the run's own record (#256:
   *  there is no early return for a run that is no longer shown; its confirmed removals are
   *  reported all the same). `phase` and `syncReport` move together in one update so the
   *  lifecycle's auto-close guard never sees a `reporting` record whose report has not been marked
   *  `pending` yet — a run with nothing to report goes straight to `closed`. */
  private onRunComplete(runId: string, result: RunResult): void {
    const reportsDeleted = result.doneKeys.length > 0;
    const updated = this.lifecycle.update(runId, (run) => ({
      ...run,
      result,
      phase: 'reporting',
      syncReport: reportsDeleted ? 'pending' : run.syncReport,
      syncReportReason: reportsDeleted ? null : run.syncReportReason,
    }));
    if (!this.lifecycle.isShown(runId)) {
      // A run `reset()` detached while in flight has left its queue on the engine until now,
      // because `finish()` builds this very result from it; nothing shows that queue any more.
      this.engine.reset();
    }
    if (updated === null) {
      // Unreachable: a run is only ever dropped once it is closed, and it cannot close before this.
      return;
    }

    if (reportsDeleted) {
      this.reportDeleted(runId, result.doneKeys, () => this.fallbackResync(updated));
    }
  }

  /** No resync of its own on an answer (spec 6.5): the backend resyncs every channel the report
   *  touched (E17), and the page lives off the resulting `channel.synced`. `afterFailure` runs once
   *  the report has failed for good — only the first report of a run passes one, never a manual
   *  retry (addendum N1).
   *
   *  Patches the record to `syncReport: 'pending'` first (#256 P2, Plan-256-Robustheit review),
   *  the same way the import's `reportImported`/`reportRemoved` do — the first call after
   *  `onRunComplete` finds it already `'pending'` (redundant but harmless), but a manual
   *  `retrySyncReport()` call needs exactly this: without it, the record stayed on its previous
   *  end state (e.g. `'failed'`) for the whole time the retry's request was out, so the retry
   *  button stayed visible for a second click (a parallel, redundant report) and, once `closed`,
   *  the record had nothing pending to keep it in the lifecycle's map for a late answer to find —
   *  a "Close" clicked mid-retry then left the eventual answer with no record to land on: no
   *  reshow, no `console.warn`. `syncReportReason` is cleared too, so a stale reason does not
   *  flash next to the fresh `'pending'` state. `closed` itself is not reopened here — the
   *  lifecycle's one-way door leaves the phase alone, and `reportsPending` (`syncReport ===
   *  'pending'`) is what keeps a `closed`-but-pending record in the map until this new attempt
   *  also reaches an end state. */
  private reportDeleted(runId: string, sevenTvEmoteIds: string[], afterFailure?: () => void): void {
    const run = this.patchRun(runId, { syncReport: 'pending', syncReportReason: null });
    if (run === null) {
      // Unreachable in practice: called right after the update that put the run into `reporting`,
      // or from a manual retry that just read the record — kept as a guard, not a silent no-op.
      return;
    }

    this.emoteSetService
      .reportDeletedInSet(run.setId, {
        sevenTvEmoteIds,
        expectedChannelName: run.expectedChannelName,
      })
      .pipe(
        timeoutReportAttempt(),
        // The threeway reading (`map`, not inside `next:`) lives ahead of `retry` so a malformed
        // 200 answer that makes `classifySyncInSetResponse` throw ends the report like any other
        // transient failure — an uncaught throw inside a `next:` callback would otherwise leave
        // this run `reporting` forever, never `closed` (#256 review finding). `retry`'s `delay`
        // below then also retries this throw (a plain `TypeError`, not an `HttpErrorResponse` —
        // its `status` reads `undefined`, so neither branch of the 401/403 check matches):
        // uncritical, because a retried `sync-deleted` call is idempotent either way.
        map((answer: SyncDeletedInSetResponse) =>
          classifySyncInSetResponse(answer, sevenTvEmoteIds.length),
        ),
        // A 429 is the realistic case: sync-deleted shares a rate-limit budget with other calls, and
        // a swallowed 429 used to look exactly like success. A 401 (session expired during a long
        // run) or a 403 (the right to the set is gone) cannot be fixed by waiting, so neither is
        // retried.
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        // The threeway reading lives in one place for all three services (F8, E23, AK 15): a
        // channel short of the reported ids is 'partial'/'shortfall', a missed expected channel
        // 'partial'/'channelMismatchNotTracked' or 'partial'/'channelMismatchActiveSetDiffers'
        // (#255), a paper-only answer (`channels: []`) a plain success.
        next: (outcome) => this.endReport(runId, outcome),
        // A 404 (the set is gone) ends in 'failed'/'setNotFound' — never in 'succeeded' (#224).
        error: (error: HttpErrorResponse) => {
          this.endReport(runId, classifySyncInSetFailure(error.status));
          afterFailure?.();
        },
      });
  }

  /** addendum N1, AK 36: a report that failed for good (any status, or a network error, after the
   *  retries) never reached the backend's resync stage, so nothing would pull the page's rows until
   *  the worker's periodic resync. The client stands in for it — for `expectedChannelName` only: a
   *  non-active or untracked set has no channel the backend would have resynced either. No dock line
   *  (the delete never had one; the `syncFailed` notice is already up, and the visible effect is the
   *  resync's `channel.synced`), so its outcome — a 429 cooldown included — is deliberately
   *  swallowed. Runs for a superseded run too, like the restore's: it is owed to 7TV's state. */
  private fallbackResync(run: DeleteRunInfo): void {
    const channelName = run.expectedChannelName;
    if (channelName === null) {
      return;
    }
    this.channelService.resync(channelName).subscribe({ error: () => undefined });
  }

  /** Writes a report's end state onto its run's record — which closes the run once it was the last
   *  (only) report out — and brings a run nobody shows back onto the dock when that end state is
   *  not a success (Plan-256 Festlegung 13): a failed or partial report needs a place with its
   *  reason and a retry. Shown again only when nothing else is shown and the engine is free;
   *  otherwise the failure stays on the record and in the console.
   *
   *  `reshow` is attempted before `showFinishedRows` and rolled back with `detach()` if that then
   *  refuses (#256 review finding: reshowing must not leave the dock pointing at a run whose queue
   *  never actually reappeared) — never the other way round, which could otherwise push a
   *  to-be-rejected run's rows onto the engine's queue where an already-shown run would inherit them. */
  private endReport(runId: string, outcome: SyncReportOutcome): void {
    const run = this.patchRun(runId, {
      syncReport: outcome.state,
      syncReportReason: outcome.reason,
    });
    if (run === null || outcome.state === 'succeeded' || this.lifecycle.isShown(runId)) {
      return;
    }
    if (run.result !== null && !this.engine.isRunning() && this.lifecycle.reshow(run)) {
      if (this.engine.showFinishedRows(run.result.items)) {
        return;
      }
      this.lifecycle.detach();
    }
    console.warn('[EmotePurge] 7TV delete report of a run no longer shown did not succeed', {
      runId,
      state: outcome.state,
      reason: outcome.reason,
    });
  }

  /** Merges `patch` into the record of `runId` — see `SevenTvRunLifecycle.update`. */
  private patchRun(runId: string, patch: Partial<DeleteRunInfo>): DeleteRunInfo | null {
    return this.lifecycle.update(runId, (run) => ({ ...run, ...patch }));
  }
}

/** One queue row per 7TV id, keyed by it (spec #200, 7.2, Sonde 5 branch A): a #74 duplicate cell
 *  is one row, and its one `REMOVE` takes every entry. Should a caller hand in the same id twice,
 *  the rows merge — two rows with one key would make the engine's per-key status updates hit
 *  both, and the second `REMOVE` could only fail — keeping every alias for the protocol. */
function toDeleteQueue(emotes: readonly DeleteQueueEmote[]): RunQueueEmote[] {
  const rows = new Map<string, RunQueueEmote & { aliases: string[] }>();
  for (const emote of emotes) {
    const aliases = emote.aliases && emote.aliases.length > 0 ? emote.aliases : [emote.name];
    const existing = rows.get(emote.sevenTvEmoteId);
    if (existing) {
      existing.aliases.push(...aliases.filter((alias) => !existing.aliases.includes(alias)));
      existing.emoteId ??= emote.emoteId;
      continue;
    }
    rows.set(emote.sevenTvEmoteId, {
      key: emote.sevenTvEmoteId,
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.name,
      aliases: [...new Set(aliases)],
    });
  }
  return [...rows.values()];
}
