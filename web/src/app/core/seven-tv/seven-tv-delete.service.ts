import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { retry, throwError, timer } from 'rxjs';

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
 * One delete run, from the moment it starts to the moment its closing report is done. Everything
 * the asynchronous follow-up needs hangs off *this* object, never off a field next to the service
 * (R15, #72, T12): the engine sets `isRunning` back to `false` inside `finish()`, i.e. *before*
 * `onRunComplete` fires the asynchronous `sync-deleted` call, and the arbiter derives "a run is
 * active" from exactly that signal — so a second delete can legitimately start while the first
 * one's report is still in flight. With the channel in one field and the reported ids in another, a
 * late answer (or a manual retry) of run 1 could be applied to run 2's channel. Bound to the
 * record, a late answer is simply no longer `this.run` and is dropped — see the identical note on
 * `ImportRunInfo` in `seven-tv-import.service.ts`.
 */
interface DeleteRunInfo {
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

  /** The run every asynchronous follow-up is bound to (R15) — not the same thing as `lastRun`,
   *  which stays `null` for as long as this is in flight and only mirrors it once `result` lands. */
  private run: DeleteRunInfo | null = null;

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** State of the closing sync-deleted call. Consumers must wait for a terminal value before
   *  optimistically removing rows: 'failed'/'partial' means the backend does not (fully) know about
   *  the deletion yet, so filtering the list client-side would show a state that isn't real. */
  readonly syncReport = signal<SyncReportState>('idle');

  /** Why `syncReport` is `'failed'`/`'partial'` (spec E23), `null` otherwise — the dock shows it as
   *  its own line under the report notice. */
  readonly syncReportReason = signal<SyncReportReason | null>(null);

  /** The finished run, kept for the summary/protocol UI (A6). Cleared on reset() — once the panel
   *  is dismissed, the downloaded protocol file is the only remaining artifact, by design. */
  readonly lastRun = signal<{ setId: string; channelName: string; result: RunResult } | null>(null);

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
   *  otherwise (spec 6.5) — the caller knows which, this service does not. */
  startDelete(
    setId: string,
    channelName: string,
    emotes: DeleteQueueEmote[],
    expectedChannelName: string | null,
  ): void {
    const started: DeleteRunInfo = { channelName, expectedChannelName, setId, result: null };
    const engineStarted = this.engine.start(
      setId,
      toDeleteQueue(emotes),
      REMOVE_OPERATION,
      (result) => this.onRunComplete(started, result),
    );
    if (!engineStarted) {
      // Refused (already running, empty list, no token) — leave every signal as it was.
      return;
    }
    this.run = started;
    this.syncReport.set('idle');
    this.syncReportReason.set(null);
    this.lastRun.set(null);
  }

  cancel(): void {
    this.engine.cancel();
  }

  /** Clears the panel after the admin has acknowledged a finished/cancelled run. Also drops the
   *  run record: with the panel gone there is nothing left to retry against, and any answer still
   *  in flight for it is no longer `this.run` (R15). */
  reset(): void {
    this.engine.reset();
    this.syncReport.set('idle');
    this.syncReportReason.set(null);
    this.run = null;
    this.lastRun.set(null);
    // The restore service clears its own transient notice flag here for the same reason: whatever
    // this dock was still holding open, the user has dismissed it.
    this.clearConfirmedRun();
  }

  /** The panel is a root-service singleton, so a finished run used to follow the user into the
   *  next channel's workspace, still showing the previous channel's counts. A *running* run is
   *  deliberately left alone — hiding it would be worse than showing it on the wrong page, and it
   *  still needs its channel for the closing sync call. */
  resetIfChannelChanged(channelName: string): void {
    if (this.isRunning() || this.run === null || this.run.channelName === channelName) {
      return;
    }
    this.reset();
  }

  /** Manual retry for the closing report. The 7TV deletions are long done at this point, so this
   *  only re-sends the bookkeeping call — safe to repeat, ids already archived still count.
   *  Set, expected channel *and* keys come from the same record, so a retry can never mix one
   *  run's ids with another's target or with a set chosen after the run started (R15, AK 71). */
  retrySyncReport(): void {
    const current = this.run;
    if (
      this.syncReport() === 'pending' ||
      // addendum N4, AK 40: either channel-mismatch reason is recorded and, for
      // activeSetDiffers, its resync already runs — a retry could only write the same mismatch
      // again.
      isChannelMismatch(this.syncReportReason()) ||
      !current?.result ||
      current.result.doneKeys.length === 0
    ) {
      return;
    }

    this.reportDeleted(current, current.result);
  }

  private onRunComplete(started: DeleteRunInfo, result: RunResult): void {
    if (this.run !== started) {
      // Only reachable via reset()/resetIfChannelChanged() during the run: the shown run is not
      // this one any more, so neither its result nor its bookkeeping belong on screen.
      return;
    }

    // A new object rather than a mutation, so consumers of `run` reading it back via `lastRun`
    // actually see the result. From here on this is the record the follow-up is bound to.
    const finished: DeleteRunInfo = { ...started, result };
    this.run = finished;
    this.lastRun.set({ setId: finished.setId, channelName: finished.channelName, result });

    if (result.doneKeys.length > 0) {
      this.reportDeleted(finished, result, () => this.fallbackResync(finished));
    }
  }

  /** No resync of its own on an answer (spec 6.5): the backend resyncs every channel the report
   *  touched (E17), and the page lives off the resulting `channel.synced`. `afterFailure` runs once
   *  the report has failed for good — only the first report of a run passes one, never a manual
   *  retry (addendum N1). */
  private reportDeleted(run: DeleteRunInfo, result: RunResult, afterFailure?: () => void): void {
    this.syncReport.set('pending');
    this.syncReportReason.set(null);
    // A delete run's keys are its 7TV ids (see toDeleteQueue) — one per cell, unique in the run.
    const sevenTvEmoteIds = result.doneKeys;

    this.emoteSetService
      .reportDeletedInSet(run.setId, {
        sevenTvEmoteIds,
        expectedChannelName: run.expectedChannelName,
      })
      .pipe(
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
        next: (answer: SyncDeletedInSetResponse) =>
          this.applyIfCurrent(run, () =>
            this.applyReportOutcome(classifySyncInSetResponse(answer, sevenTvEmoteIds.length)),
          ),
        // A 404 (the set is gone) ends in 'failed'/'setNotFound' — never in 'succeeded' (#224).
        error: (error: HttpErrorResponse) => {
          this.applyIfCurrent(run, () =>
            this.applyReportOutcome(classifySyncInSetFailure(error.status)),
          );
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

  private applyReportOutcome(outcome: SyncReportOutcome): void {
    this.syncReport.set(outcome.state);
    this.syncReportReason.set(outcome.reason);
  }

  /** The R15 guard in one place: an answer that belongs to a superseded run is dropped silently —
   *  no error state, nothing written. The run it belongs to is not on screen any more, and the one
   *  that is must not inherit its outcome. */
  private applyIfCurrent(run: DeleteRunInfo, apply: () => void): void {
    if (this.run !== run) {
      return;
    }
    apply();
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
