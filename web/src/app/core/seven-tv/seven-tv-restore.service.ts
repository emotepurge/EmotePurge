import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, linkedSignal, signal, WritableSignal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { map, retry, throwError, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import {
  DeleteQueueEmote,
  MAX_AUTOMATIC_SYNC_RETRIES,
  SYNC_RETRY_DELAY_MS,
  timeoutReportAttempt,
} from './seven-tv-delete.service';
import { SyncRestoredInSetResponse } from './seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from './seven-tv-emote-set.service';
import { RunOperation, RunQueueEmote, RunResult, SevenTvRunEngine } from './seven-tv-run-engine';
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

/** Same shape as the delete's REMOVE, with `addEmote` and the alias to restore under. `alias`
 *  restores the chat alias the emote had at delete time — without it 7TV falls back to the emote's
 *  default name, which for renamed emotes would not be the one the chat knows. `null` is that
 *  fallback on purpose: it is how an entry that had no alias comes back as one. It travels *inside*
 *  the `EmoteSetEmoteId` input object, not as a sibling argument — v4's `addEmote` field replaces
 *  v3's single `emotes(action: ADD, name:)` mutation with one field per operation (see
 *  docs/DECISIONS.md, #149). */
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

/** The `ADD` of one restore run. The alias each queue row sends is looked up by its key in
 *  `aliasByKey` (built together with the queue by `toRestoreQueue`, so every key is in it) rather
 *  than read from the row's `name`, which for an entry without an alias is only its display name. */
function addOperation(aliasByKey: ReadonlyMap<string, string | null>): RunOperation {
  return {
    label: 'restore',
    buildRequest: (setId, emote) => ({
      query: ADD_EMOTE_MUTATION,
      variables: {
        setId,
        emoteId: emote.sevenTvEmoteId,
        alias: aliasByKey.get(emote.key),
      },
    }),
  };
}

/** One row a restore run re-adds: a `DeleteQueueEmote` whose aliases may include `null` — an entry
 *  without an alias, which only a transfer-run file records (`RestoreRow` in `purge-run-export.ts`).
 *  `defaultName` is what the queue shows for that entry; without it, the 7TV id. */
export interface RestoreQueueEmote extends Omit<DeleteQueueEmote, 'aliases'> {
  aliases?: readonly (string | null)[];
  defaultName?: string | null;
}

// #149 P2 (independent review): how long `duplicateNoticePending` stays true after a `startRestore`
// call that had something to report. Same 4000 ms convention as every other transient status in
// this app (docs/UI-Designsprache.md §4.5). See the identical constant in
// `seven-tv-import.service.ts` for why this lives on the service rather than on a page.
const DUPLICATE_NOTICE_MS = 4000;

/** Outcome of the closing resync trigger. 'cooldown' is not a failure: the per-channel cooldown
 *  (429) means a sync just ran or is about to — the periodic worker heals the view within its
 *  60s tick either way. `'backendTriggered'` (restore only, active set only since #255, spec
 *  6.4/F15) means the report's own answer named the channel in `resyncTriggered`: the backend
 *  already started the resync, so no request of ours went out — the dock says "being re-synced"
 *  all the same. The import never takes this value; its resync skips such a channel and stays
 *  `'idle'`. */
export type ResyncTriggerState =
  'idle' | 'pending' | 'succeeded' | 'cooldown' | 'failed' | 'backendTriggered';

/**
 * Where a restore run goes and whom it tells (spec 6.4, E12, E13, E18) — the first parameter of
 * `startRestore`. The caller derives it from the resolved target, the service never re-derives
 * anything from it:
 *
 * - `setId` — the 7TV set every `ADD`, the report and every retry name.
 * - `expectedChannelName` — the tracked channel the report expects to touch: the target account's
 *   tracked channel when the target is its *active* set, otherwise `null` (E18).
 * - `resyncChannelName` — the tracked channel of a *non-active* set, otherwise `null`; display only
 *   since the operator decision 2026-09-25 (#255) — `RestoreProgressSection`'s target line reads it
 *   to name the channel, but `resyncAfterReport` no longer does. Before #255 this also named the
 *   one case no backend resync covered and triggered the client's own resync for it (former E12) —
 *   dropped because that resync only ever reloaded the channel's *active* set view, never the
 *   non-active set the run actually wrote to (design doc §18 addendum, 2026-09-25).
 * - `hostChannelName` — the channel of the page the run was started on; only
 *   `resetIfChannelChanged` compares against it (F7).
 * - `setName`, `ownerOrChannelLabel` — display only, never compared (the dock's target line, 4.4
 *   point 12).
 */
export interface RestoreStartTarget {
  setId: string;
  expectedChannelName: string | null;
  resyncChannelName: string | null;
  hostChannelName: string;
  setName: string;
  ownerOrChannelLabel: string;
}

/**
 * One restore run, from the moment it starts to the moment its closing report reaches an end state
 * (#256, `SevenTvRunLifecycle`): `running → reporting → closed` on its own record — a restore never
 * re-reads, so it never sees `settling`. `runId` is the identity a late answer or a manual retry
 * finds it by; the record is replaced by a new object on every change, never mutated (see the
 * identical note on `DeleteRunInfo` in `seven-tv-delete.service.ts` and on `ImportRunInfo` in
 * `seven-tv-import.service.ts`). `destructive` is always `false`: a restore only ever `ADD`s, so it
 * never arms the tab's unload guard (Plan-256 Festlegung 6).
 */
export interface RestoreRunInfo extends RunRecordBase {
  /** The set the run re-adds into, frozen when it starts (spec #200, 7.2, AK 71) — the report and
   *  every retry name this set. */
  targetSetId: string;
  /** See `RestoreStartTarget.expectedChannelName` — sent with every report and retry. */
  expectedChannelName: string | null;
  /** See `RestoreStartTarget.resyncChannelName` — display only since #255, read by
   *  `RestoreProgressSection`'s target line, never by `resyncAfterReport`. */
  resyncChannelName: string | null;
  /** See `RestoreStartTarget.hostChannelName`. */
  hostChannelName: string;
  /** Display only (the dock's target line). */
  setName: string;
  /** Display only (the dock's target line): the tracked channel or the owner's display name. */
  ownerOrChannelLabel: string;
  /** `null` while the run is in flight; set once the engine reports the run complete. */
  result: RunResult | null;
  /** This run's `sync-restored` report — `SevenTvRestoreService.syncReport` projects it for the
   *  shown run. */
  syncReport: SyncReportState;
  /** Projected by `syncReportReason`. */
  syncReportReason: SyncReportReason | null;
  /** Projected by `resyncTrigger`. Not a report (#256, Plan-256 Festlegung 4): never holds the run
   *  open. */
  resyncTrigger: ResyncTriggerState;
}

/**
 * The restore half of A6: re-adds emotes to the 7TV set, in the browser, over the same run engine
 * (pacing, backoff, token) as the delete — ADD draws tickets from the same `emote_set_change`
 * bucket. Zero-knowledge holds: the write token never leaves the browser.
 *
 * A finished run reports itself to the set-centric `sync-restored` (spec 5.1, 6.4): the
 * bookkeeping call that un-archives the rows of every tracked channel whose active set this is
 * and — the reason it exists at all — writes the `emotes.syncRestored` audit entry, for any
 * target, tracked or not. The backend resyncs every channel it touched (E17) and says so in
 * `resyncTriggered`; a **successful** report never makes this service trigger a resync of its own —
 * for the target's active set the backend's own coverage (E17) is unconditional, so only the dock's
 * `'backendTriggered'` display depends on whether the answer happens to name it, and a non-active
 * tracked target gets no client resync at all any more (operator decision 2026-09-25, #255, same as
 * the import, `seven-tv-import.service.ts:657-671`). Only the **first** report of a run that fails
 * for good, and only for the active set, makes the client stand in with its own resync of
 * `expectedChannelName`, since the backend never reached its own resync stage then (addendum N1,
 * AK 36); the cooldown (F15) absorbs a duplicate against a resync the backend or an earlier run
 * already triggered.
 *
 * Every run completes run-bound (#256, `SevenTvRunLifecycle`): `running → reporting → closed` on
 * its own record, whether or not the dock still shows it. `reset()` and a newer run only change
 * what is shown. `isSettling` and `destructiveOpen` look across every open run of this service —
 * `destructiveOpen` is always `false` here, since a restore never removes anything.
 */
@Injectable({ providedIn: 'root' })
export class SevenTvRestoreService {
  private readonly channelService = inject(ChannelService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);

  /** Own engine instance — see the identical note in SevenTvDeleteService. */
  private readonly engine = new SevenTvRunEngine(
    inject(HttpClient),
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  /** Every open run of this service, by id, plus the one the dock shows (#256). */
  private readonly lifecycle = new SevenTvRunLifecycle<RestoreRunInfo>(
    'restore',
    (run) => run.syncReport === 'pending',
  );

  /** The run this service is showing — in flight (`result === null`) or finished. Its record is
   *  replaced by a new object on every change; `runId` is its identity. Writable because specs
   *  drive the dock through it; production code only writes through the lifecycle. */
  readonly run: WritableSignal<RestoreRunInfo | null> = this.lifecycle.shown;

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** True while any run of this service reports — shown or not (#256, contract P1). A restore never
   *  re-reads, so this is exactly "reporting", never "settling". */
  readonly isSettling = this.lifecycle.isSettling;

  /** Always `false`: no restore row is destructive (#256, contract P3; Plan-256 Festlegung 6). Kept
   *  as a real projection of the lifecycle, not a literal, so the contract holds even if that ever
   *  changes. */
  readonly destructiveOpen = this.lifecycle.destructiveOpen;

  /** State of the shown run's closing sync-restored call — same contract as the delete's
   *  syncReport. `linkedSignal` projection of the shown record (#256, Plan-256 Festlegung 14):
   *  writable so specs can drive a dock directly; production code never writes it, only the
   *  record. */
  readonly syncReport = linkedSignal<SyncReportState>(() => this.run()?.syncReport ?? 'idle');

  /** Why `syncReport` is `'failed'`/`'partial'` (spec E23), `null` otherwise — the dock shows it as
   *  its own line under the report notice. Projection, like `syncReport`. */
  readonly syncReportReason = linkedSignal<SyncReportReason | null>(
    () => this.run()?.syncReportReason ?? null,
  );

  readonly resyncTrigger = linkedSignal<ResyncTriggerState>(
    () => this.run()?.resyncTrigger ?? 'idle',
  );

  /** How many `ADD`s — one per alias of a protocol row, counted per alias since the 2026-09-22
   *  "middle rule" (`filterAlreadyPresentForRestore`) — the caller's pre-run duplicate check
   *  (#149/T5, `already-present-filter.ts`) dropped before ever calling `startRestore` — surfaced so a run where every row was already
   *  present is not a silent no-op. Set unconditionally, even when the engine then refuses to start
   *  (an empty `emotes` list, e.g. because everything was a duplicate) — that case is exactly the
   *  one this exists to make visible. */
  readonly skippedDuplicates = signal(0);

  /** How many `ADD`s the same pre-run check dropped because a *different* emote now holds that
   *  alias in the target set (`filterAlreadyPresentForRestore`, rule 4) — shown apart from
   *  `skippedDuplicates`, so "skipped" is never read as "was already there". Set unconditionally,
   *  like `skippedDuplicates`, and part of the same transient notice. */
  readonly skippedNameTaken = signal(0);

  /** Whether the caller's pre-run duplicate check (#149/T5, `already-present-filter.ts`) actually
   *  ran — `false` means its fetch failed, so `emotes` passed through unfiltered and an undetected
   *  duplicate is possible in this run. Same vocabulary as `AlreadyPresentFilterResult.available`;
   *  see that type's doc for why a failed check must not read as a clean `skippedDuplicates: 0`.
   *  Defaults to `true` so existing callers/tests that omit it keep reading as "checked, nothing to
   *  skip". */
  readonly duplicateCheckAvailable = signal(true);

  /** #149 P2 (independent review): whether the notice built from the three signals above should
   *  currently be shown — true for `DUPLICATE_NOTICE_MS` after any `startRestore` call that had
   *  something to report (a skip count above 0, or `!duplicateCheckAvailable`), including a refused
   *  (all-duplicates) call. `dockVisible()` (`usage-stats-page.ts`, via `action-dock.ts`) treats this
   *  exactly like an active restore, which is what lets `RestoreProgressSection` mount at all in
   *  that refused case (moved out of `MassDeletePanel` in #253/T9) — without it the section's own
   *  gate (`isRunning() || queue().length > 0`) would never fire, since a refused call leaves both
   *  false, and the notice that is the run's *only* outcome would be unreachable. Self-clearing
   *  rather than requiring a manual dismiss for the same reason `usage-stats-page`'s
   *  `selectionPrunedFeedback` is (design doc §4.5): a refused call has no run/queue for a dismiss
   *  button to attach to, and a persistent flag would otherwise be able to sit next to an unrelated
   *  *later* run's details with nothing to clear it. */
  readonly duplicateNoticePending = signal(false);

  /** Whether a restore's shared open-time pre-check chain (`resolveEditableSet`, then the open-time
   *  duplicate check) is out right now, from *either* of the two entry points a restore can start
   *  from — `ImportTrigger`'s restore-file door (`startRestoreFlow`, `restore-flow.ts`) or
   *  `MassDeletePanel`'s restore button (`openRestoreConfirm`). Root-level and shared on purpose
   *  (#255 P2, Codex review): the two entries used to keep separate, component-local pending flags,
   *  which guarded each button against a second click on *itself* but left the other entry's button
   *  fully enabled while the first's read was still out — both mount together on the usage-stats
   *  page (`usage-stats-page.html`), so a click there while the other's pre-check chain was in
   *  flight could open a second confirmation stacked on the first, with a duplicate
   *  `app-dialog-title` id. Both entries now read and set this same signal instead of a field of
   *  their own — see `ImportTrigger.restorePreviewPending`/`RestoreFlowDeps.previewPending` and
   *  `MassDeletePanel.restoreConfirmPending`, both of which alias this signal rather than holding
   *  their own. Exposed writable (not `.asReadonly()`), like `RestoreFlowDeps.previewPending`
   *  already was before this fix: both call sites are the ones setting it, this only moves *where*
   *  the shared instance lives. */
  readonly restorePreCheckPending: WritableSignal<boolean> = signal(false);

  private duplicateNoticeTimeout: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    // The one run-service → arbiter edge (#256, contract P4): the arbiter derives "busy" and the
    // tab's unload guard from these three signals; it does not know this service otherwise.
    inject(SevenTvRunArbiter).register({
      kind: 'restore',
      isRunning: this.isRunning,
      isSettling: this.isSettling,
      destructiveOpen: this.destructiveOpen,
    });
  }

  /** `target` is where the run goes and whom it tells — see `RestoreStartTarget`; frozen into the
   *  run record here and never read again from the caller. `skippedDuplicates` is the caller's own
   *  count from filtering `emotes` *before* this call — this method does no filtering of its own
   *  (see `already-present-filter.ts`, which every current caller runs first). Defaults to 0 so
   *  callers/tests that pass only two arguments are unaffected. `duplicateCheckAvailable` mirrors
   *  the same call's `available` and defaults to `true` for the same reason; `skippedNameTaken` is
   *  the same call's name-taken count, default 0. A run that starts is shown at once; the run shown
   *  before it goes on to close on its own record (#256). */
  startRestore(
    target: RestoreStartTarget,
    emotes: readonly RestoreQueueEmote[],
    skippedDuplicates = 0,
    duplicateCheckAvailable = true,
    skippedNameTaken = 0,
  ): void {
    this.skippedDuplicates.set(skippedDuplicates);
    this.duplicateCheckAvailable.set(duplicateCheckAvailable);
    this.skippedNameTaken.set(skippedNameTaken);
    this.showDuplicateNotice(
      skippedDuplicates > 0 || skippedNameTaken > 0 || !duplicateCheckAvailable,
    );
    const previousShown = this.lifecycle.shown();
    const runId = this.lifecycle.createRunId();
    const started: RestoreRunInfo = {
      runId,
      phase: 'running',
      destructive: false,
      targetSetId: target.setId,
      expectedChannelName: target.expectedChannelName,
      resyncChannelName: target.resyncChannelName,
      hostChannelName: target.hostChannelName,
      setName: target.setName,
      ownerOrChannelLabel: target.ownerOrChannelLabel,
      result: null,
      syncReport: 'idle',
      syncReportReason: null,
      resyncTrigger: 'idle',
    };
    const { queue, aliasByKey } = toRestoreQueue(emotes);
    // Opened *before* the engine is asked to start — see the identical note in
    // SevenTvDeleteService.startDelete (#256 review finding: a synchronous `onComplete` must always
    // find its record already registered).
    this.lifecycle.open(started);
    const engineStarted = this.engine.start(
      target.setId,
      queue,
      addOperation(aliasByKey),
      (result) => this.onRunComplete(runId, result),
    );
    if (!engineStarted) {
      // Refused (already running, empty list, no token) — leave the counts above (an all-skipped
      // restore is a legitimate "refused" case whose counts the caller still needs to see), but
      // take the just-opened record back and restore whatever was shown before it.
      this.lifecycle.discardUnstarted(runId, previousShown);
    }
  }

  cancel(): void {
    this.engine.cancel();
  }

  reset(): void {
    if (!this.engine.isRunning()) {
      this.engine.reset();
    }
    this.lifecycle.detach();
    this.skippedDuplicates.set(0);
    this.skippedNameTaken.set(0);
    this.duplicateCheckAvailable.set(true);
    this.showDuplicateNotice(false);
  }

  /** Same page-follows-user reasoning as the delete service's counterpart — compared against the
   *  channel of the page the run was started on (`hostChannelName`, F7/E13), never against the
   *  target. Since #256 (Plan-256 Festlegung 13) this only fires for a `closed` run: a run still
   *  reporting follows the user for the few seconds until its report reaches an end state —
   *  dropping it mid-report would leave a later `failed` with no dock to show it and no retry to
   *  reach it (Codex-Befund 2). */
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

  /** Manual retry for the closing report — the 7TV re-adds are long done, so this only re-sends
   *  the bookkeeping call, never the resync (that followed the first report already). Safe to
   *  repeat: ids already un-archived still count as restored. Set, expected channel *and* keys come
   *  from the same record, so a retry can never mix one run's ids with another's target or with a
   *  set chosen after the run started (R15, AK 71). A retry on a closed run does not reopen it
   *  (#256): it is a new report on a closed run, and neither the arbiter nor the unload guard sees
   *  it. */
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

    this.reportRestored(current.runId, current.result);
  }

  /** Turns the engine's snapshot into the run's outcome, always on the run's own record (#256:
   *  there is no early return for a run that is no longer shown; its confirmed adds are reported
   *  all the same). `phase` and `syncReport` move together in one update so the lifecycle's
   *  auto-close guard never sees a `reporting` record whose report has not been marked `pending`
   *  yet — a run with nothing to report goes straight to `closed`. */
  private onRunComplete(runId: string, result: RunResult): void {
    const reportsRestored = result.doneKeys.length > 0;
    const updated = this.lifecycle.update(runId, (run) => ({
      ...run,
      result,
      phase: 'reporting',
      syncReport: reportsRestored ? 'pending' : run.syncReport,
      syncReportReason: reportsRestored ? null : run.syncReportReason,
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

    if (reportsRestored) {
      // The expected channel is frozen at start and never re-read from the record after this: the
      // resync helpers below take it as a plain parameter rather than looking the record up again,
      // because a superseded, already-`closed` run can be pruned from the lifecycle's map by the
      // time its resync answers — the resync itself is still owed to 7TV's state regardless
      // (see `resyncAfterReport`'s own doc).
      const expectedChannelName = updated.expectedChannelName;
      this.reportRestored(runId, result, (resyncTriggered) =>
        this.resyncAfterReport(runId, expectedChannelName, resyncTriggered),
      );
    }
  }

  /** `afterReport` runs once the report has settled either way — with the answer's
   *  `resyncTriggered` on success, with `null` once it has failed for good (the backend never
   *  reached its resync stage then, addendum N1). It runs even for a superseded run: the report and
   *  the resync are owed to 7TV's state, not to what the dock shows.
   *
   *  Patches the record to `syncReport: 'pending'` first (#256 P2, Plan-256-Robustheit review) —
   *  same fix and same reason as the identical line in `SevenTvDeleteService.reportDeleted`: a
   *  manual `retrySyncReport()` call needs the record marked `'pending'` before its request goes
   *  out, or the retry button stays up for a second click and a `closed`-but-pending record has
   *  nothing to keep it in the lifecycle's map for a "Close" clicked mid-retry. `closed` itself
   *  stays untouched — the lifecycle's one-way door does that. */
  private reportRestored(
    runId: string,
    result: RunResult,
    afterReport?: (resyncTriggered: readonly string[] | null) => void,
  ): void {
    const run = this.patchRun(runId, { syncReport: 'pending', syncReportReason: null });
    if (run === null) {
      // Unreachable in practice: called right after the update that put the run into `reporting`,
      // or from a manual retry that just read the record — kept as a guard, not a silent no-op.
      return;
    }
    const sevenTvEmoteIds = doneSevenTvEmoteIds(result);

    this.emoteSetService
      .reportRestoredInSet(run.targetSetId, {
        sevenTvEmoteIds,
        expectedChannelName: run.expectedChannelName,
      })
      .pipe(
        timeoutReportAttempt(),
        // The threeway reading (`map`, not inside `next:`) lives ahead of `retry` so a malformed
        // 200 answer that makes `classifySyncInSetResponse` throw ends the report like any other
        // transient failure — an uncaught throw inside a `next:` callback would otherwise leave
        // this run `reporting` forever, never `closed` (#256 review finding, same fix as the
        // delete's and the import's own `sync-deleted`). `retry`'s `delay` below then also retries
        // this throw (a plain `TypeError`, not an `HttpErrorResponse` — its `status` reads
        // `undefined`, so neither branch of the 401/403 check matches): uncritical, because a
        // retried `sync-restored` call is idempotent either way.
        map((answer: SyncRestoredInSetResponse) => ({
          outcome: classifySyncInSetResponse(answer, sevenTvEmoteIds.length),
          resyncTriggered: answer.resyncTriggered,
        })),
        // Same policy as the delete's report: waiting can fix a 429/5xx, not a 401/403.
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
          // The threeway reading lives in one place for all three services (F8, E23, AK 15) —
          // never an `>=` of our own here.
          this.endReport(runId, outcome);
          afterReport?.(resyncTriggered);
        },
        error: (error: HttpErrorResponse) => {
          // A 404 (the set is gone) ends in 'failed'/'setNotFound' — never in 'succeeded' (#224).
          this.endReport(runId, classifySyncInSetFailure(error.status));
          afterReport?.(null);
        },
      });
  }

  /** F15, AK 21/27, spec 6.4 and 4.4 point 11, operator decision 2026-09-25 (#255): a resync of our
   *  own only ever for the target's **active** set (`expectedChannelName`) — a non-active tracked
   *  target no longer gets a client resync at all, whatever the report's answer says or whether it
   *  succeeded or failed for good. This replaces the former E12 (docs/superpowers/specs/
   *  2026-09-24-restore-pro-set-253-design.md, §18 addendum, 2026-09-25): a non-active set's
   *  channel resync only ever pulled the channel's *active* set view, never the set this run
   *  actually wrote to — a request that could succeed while confirming nothing the user cares
   *  about, exactly the reasoning the import's own `sendFollowUp` already followed
   *  (`seven-tv-import.service.ts:657-671`). `resyncChannelName` still names a non-active tracked
   *  target's channel, but only for `RestoreProgressSection`'s target-line label — never read here
   *  any more.
   *
   *  For the active set: the dock says "being re-synced" (`'backendTriggered'`) without a request
   *  of ours whenever the answer already names the expected channel in `resyncTriggered` (also for
   *  an unresolved expected channel, `activeSetDiffers`). Otherwise `resyncTrigger` simply stays
   *  `'idle'` — no request, no resync line — because the backend's own resync (E17) covers the
   *  active set unconditionally regardless of whether it happens to be visible in `resyncTriggered`;
   *  unlike the removed non-active case, the client was never the one this success path depended on.
   *  `resyncTriggered === null` is a report that failed for good (addendum N1, AK 36) — any status,
   *  or a network error, after the retries; only then, because the backend never reached its resync
   *  stage at all, does the client stand in for it with `expectedChannelName`, and only after the
   *  *first* report of a run (a manual retry passes no `afterReport`); the cooldown absorbs a
   *  duplicate.
   *
   *  Takes `expectedChannelName` as a parameter rather than re-reading it off the record (#256): a
   *  superseded run that already ended `closed` can be pruned from the lifecycle's map by the time
   *  this runs, but the resync it triggers is still owed to 7TV's state, not to whether anything
   *  still shows the run — see `onRunComplete`. Whatever state this writes back onto the record via
   *  `patchRun` below is a no-op once the record is gone, same as it was silently ignored under the
   *  old per-object `applyIfCurrent` guard for a run nothing shows any more. */
  private resyncAfterReport(
    runId: string,
    expectedChannelName: string | null,
    resyncTriggered: readonly string[] | null,
  ): void {
    if (expectedChannelName === null) {
      // Untracked, or a non-active tracked target (#255) — nothing of ours resyncs either way.
      return;
    }
    if (resyncTriggered === null) {
      // N1 fallback: the report failed for good, so the backend never reached its own resync —
      // active set only, same as the rest of this method.
      this.triggerResync(runId, expectedChannelName);
      return;
    }
    if (includesChannel(resyncTriggered, expectedChannelName)) {
      this.patchRun(runId, { resyncTrigger: 'backendTriggered' });
    }
    // Not named: the backend's own resync (E17) covers the active set unconditionally regardless —
    // nothing for the client to trigger itself, unlike the non-active case #255 removed. Stays
    // `'idle'`.
  }

  private triggerResync(runId: string, channelName: string): void {
    this.patchRun(runId, { resyncTrigger: 'pending' });
    this.channelService.resync(channelName).subscribe({
      next: () => this.patchRun(runId, { resyncTrigger: 'succeeded' }),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.patchRun(runId, { resyncTrigger: error.status === 429 ? 'cooldown' : 'failed' }),
    });
  }

  /** Writes a report's end state onto its run's record — which closes the run once it was the last
   *  (only) report out — and brings a run nobody shows back onto the dock when that end state is
   *  not a success (Plan-256 Festlegung 13): a failed or partial report needs a place with its
   *  reason and a retry. Shown again only when nothing else is shown and the engine is free;
   *  otherwise the failure stays on the record and in the console.
   *
   *  `reshow` is attempted before `showFinishedRows` and rolled back with `detach()` if that then
   *  refuses (#256 review finding, same fix as the delete's `endReport`). */
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
    console.warn('[EmotePurge] 7TV restore report of a run no longer shown did not succeed', {
      runId,
      state: outcome.state,
      reason: outcome.reason,
    });
  }

  /** Merges `patch` into the record of `runId` — see `SevenTvRunLifecycle.update`. */
  private patchRun(runId: string, patch: Partial<RestoreRunInfo>): RestoreRunInfo | null {
    return this.lifecycle.update(runId, (run) => ({ ...run, ...patch }));
  }

  /** #149 P2: `hasSomethingToReport` clears any earlier timer first — a second call within
   *  `DUPLICATE_NOTICE_MS` of the first must not let the first timer's clear race the new one and
   *  hide a still-current notice out from under it. */
  private showDuplicateNotice(hasSomethingToReport: boolean): void {
    clearTimeout(this.duplicateNoticeTimeout);
    if (!hasSomethingToReport) {
      this.duplicateNoticePending.set(false);
      return;
    }
    this.duplicateNoticePending.set(true);
    this.duplicateNoticeTimeout = setTimeout(
      () => this.duplicateNoticePending.set(false),
      DUPLICATE_NOTICE_MS,
    );
  }
}

/** One queue row per alias (spec #200, 7.2, Sonde 5 branch A): 7TV accepts the same emote twice
 *  under two aliases, and restoring a #74 duplicate cell takes one `ADD` each, so the restore is
 *  the one run keyed `${sevenTvEmoteId}#${alias}`. `name` carries the alias the `ADD` sends. A row
 *  without `aliases` (an old protocol, a vote-page run) restores under its `name`. A key seen
 *  twice is dropped: the engine updates status per key, and a second identical `ADD` could only
 *  collide with the first.
 *
 *  A `null` alias — an entry without one — is keyed `${sevenTvEmoteId}#` (empty suffix): still
 *  unique, because 7TV holds at most one aliasless entry per id and no named alias is empty. Its
 *  `ADD` sends `alias: null` (`aliasByKey`), and its queue row shows the emote's default name, or
 *  its 7TV id while that is unknown. */
function toRestoreQueue(emotes: readonly RestoreQueueEmote[]): {
  queue: RunQueueEmote[];
  aliasByKey: Map<string, string | null>;
} {
  const rows = new Map<string, RunQueueEmote>();
  const aliasByKey = new Map<string, string | null>();
  for (const emote of emotes) {
    const aliases = emote.aliases && emote.aliases.length > 0 ? emote.aliases : [emote.name];
    for (const alias of aliases) {
      const key = `${emote.sevenTvEmoteId}#${alias ?? ''}`;
      if (!rows.has(key)) {
        rows.set(key, {
          key,
          emoteId: emote.emoteId,
          sevenTvEmoteId: emote.sevenTvEmoteId,
          name: alias ?? (emote.defaultName || emote.sevenTvEmoteId),
        });
        aliasByKey.set(key, alias);
      }
    }
  }
  return { queue: [...rows.values()], aliasByKey };
}

/** Whether `channelName` is among the channels a report's answer says the backend resynced —
 *  case-insensitive, since the backend answers with normalized names. */
function includesChannel(channels: readonly string[], channelName: string): boolean {
  const normalized = channelName.toLowerCase();
  return channels.some((channel) => channel.toLowerCase() === normalized);
}

/** The 7TV ids a restore run finished, read off its `doneKeys` — once each, even when two aliases
 *  of one emote came back (spec #200, AK 69: two `ADD`s, one id in the report). */
function doneSevenTvEmoteIds(result: RunResult): string[] {
  const done = new Set(result.doneKeys);
  const ids = result.items.filter((item) => done.has(item.key)).map((item) => item.sevenTvEmoteId);
  return [...new Set(ids)];
}
