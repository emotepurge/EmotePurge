import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Service, inject, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { retry, throwError, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import { EmoteAdminService } from '../emotes/emote-admin.service';
import {
  ImportOrigin,
  ImportRow,
  importOriginLeaderboardSort,
  importOriginSourceChannelName,
} from './import-source';
import {
  MAX_AUTOMATIC_SYNC_RETRIES,
  SYNC_RETRY_DELAY_MS,
  SyncReportState,
} from './seven-tv-delete.service';
import { ResyncTriggerState } from './seven-tv-restore.service';
import { RunOperation, RunQueueEmote, RunResult, SevenTvRunEngine } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

// #149 P2 (independent review): how long `duplicateNoticePending` stays true after a `startImport`
// call that had something to report. Same 4000 ms convention as every other transient status in
// this app (docs/UI-Designsprache.md §4.5 — usage-stats-page's SELECTION_PRUNED_FEEDBACK_MS,
// channel-workspace-layout's RESYNC_FEEDBACK_MS). Lives here rather than on the page that renders
// it because the *visibility* of the page's own dock depends on this flag (see
// `dockVisible`/`action-dock.ts`) — a refused, all-duplicates run leaves no run/queue for the dock
// to mount on otherwise, which is exactly the bug this exists to fix.
const DUPLICATE_NOTICE_MS = 4000;

/** The same ADD the restore run uses — an import *is* an ADD, only with rows that come from
 *  somewhere else. `alias` carries the source alias so the copy keeps the name the source channel
 *  knew it by; without it 7TV would fall back to the emote's default name. It travels *inside* the
 *  `EmoteSetEmoteId` input object, not as a sibling argument — v4's `addEmote` field replaces v3's
 *  single `emotes(action: ADD, name:)` mutation with one field per operation (see docs/DECISIONS.md,
 *  #149). */
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

/**
 * Whether a failed row means "this token may not write this set at all". True for it stops the run:
 * every remaining row would fail identically, and 7TV counts each attempt against the rate-limit
 * bucket. Deliberately narrow — a rate-limit give-up (translated text, `null` status) and a network
 * error (status `0`) are row failures, not privilege failures, and the run keeps going for them.
 *
 * v4 reports a missing-permission mutation as HTTP 200 with `extensions.code = 'LACKING_PRIVILEGES'`
 * — a structured field, not a guess at their frontend's wording, which is why the `errorCode` check
 * below is the whole story now. The `httpStatus` check stays for an invalid/expired token: that still
 * fails at the HTTP layer with a real 401, whose body sits outside the GraphQL schema
 * (`{"status":"Unauthorized","error_code":1000,"error":"invalid session"}`, live-measured) and carries
 * no `extensions.code` to read.
 */
function abortsForMissingPrivileges(failure: {
  message: string;
  httpStatus: number | null;
  errorCode: string | null;
}): boolean {
  return (
    failure.httpStatus === 401 ||
    failure.httpStatus === 403 ||
    failure.errorCode === 'LACKING_PRIVILEGES'
  );
}

/**
 * One import run, from the moment it starts to the moment its bookkeeping is done. Everything the
 * closing calls need hangs off *this* object, never off a field next to the service (R15, #72):
 * the engine sets `isRunning` back to `false` inside `finish()`, i.e. *before* `onComplete` fires
 * the asynchronous follow-up, and the arbiter derives "a run is active" from exactly that signal —
 * so a second import can legitimately start while the first one's `sync-imported`/`resync` are
 * still in flight. With a target channel in one field and the reported keys in another, a late
 * answer (or a manual retry) of run 1 could be applied to run 2's target: emote rows pushed into a
 * third channel and an audit entry naming the wrong origin. Bound to the record, a late answer is
 * simply no longer `run()` and is dropped.
 */
export interface ImportRunInfo {
  targetChannelName: string;
  targetSetId: string;
  origin: ImportOrigin;
  /** `null` while the run is in flight; set once, when the engine reports the run complete. */
  result: RunResult | null;
}

/**
 * The import half of #38/K3: copies emotes into *another* channel's 7TV set, over the same run
 * engine (pacing, backoff, token, queue) as the delete and the restore — ADD draws tickets from the
 * same `emote_set_change` bucket. Zero-knowledge holds: the write token never leaves the browser.
 *
 * Two things separate it from `SevenTvRestoreService`, both deliberate:
 * - **No `resetIfChannelChanged`.** A restore always writes into the channel whose page you are on;
 *   an import writes into a *different* one on purpose, so resetting the run when the page follows
 *   the user would throw away the very run they started (R9).
 * - **The follow-up hangs off `run()`, not off loose fields** — see `ImportRunInfo`.
 *
 * It does not know the `SevenTvRunArbiter`, and does not report to it: the arbiter derives its
 * answer from this service's own `isRunning` signal (its third branch), exactly as it does for
 * delete and restore. Checking whether a run may start is the caller's job.
 */
@Service()
export class SevenTvImportService {
  private readonly channelService = inject(ChannelService);
  private readonly emoteAdminService = inject(EmoteAdminService);

  /** Own engine instance — see the identical note in SevenTvDeleteService. */
  private readonly engine = new SevenTvRunEngine(
    inject(HttpClient),
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  /** Not a module-level constant like the delete's and the restore's: `abortOn` writes this
   *  service's own signal, so the operation has to close over the instance. */
  private readonly addOperation: RunOperation = {
    label: 'import',
    buildRequest: (setId, emote) => ({
      query: ADD_EMOTE_MUTATION,
      variables: { setId, emoteId: emote.sevenTvEmoteId, alias: emote.name },
    }),
    abortOn: (failure) => {
      const abort = abortsForMissingPrivileges(failure);
      if (abort) {
        this.abortedForPrivileges.set(true);
      }
      return abort;
    },
  };

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** The run this service is currently showing — in flight (`result === null`) or finished. The
   *  identity of this object is what every asynchronous follow-up checks itself against. */
  readonly run = signal<ImportRunInfo | null>(null);

  /** State of the closing sync-imported call. Never 'partial': the endpoint answers 204 without a
   *  body, so there is no per-id outcome to compare against. */
  readonly syncReport = signal<SyncReportState>('idle');

  readonly resyncTrigger = signal<ResyncTriggerState>('idle');

  /** True once a row failed for missing 7TV write privileges and the run gave up because of it —
   *  the summary says so instead of listing every cancelled row as an ordinary failure. */
  readonly abortedForPrivileges = signal(false);

  /** How many rows the caller's fresh pre-run duplicate check (#149/T5, `already-present-filter.ts`,
   *  run from `import-flow.ts` right before this call) dropped on top of the dialog-time
   *  `buildImportPreview` filter — surfaced so a run where the fresh check caught everything is not
   *  a silent no-op. Set unconditionally, even when the engine then refuses to start. */
  readonly skippedDuplicates = signal(0);

  /** Whether the caller's fresh pre-send duplicate check (#149/T5, `already-present-filter.ts`)
   *  actually ran — `false` means its fetch failed, so `rows` passed through unfiltered and an
   *  undetected duplicate is possible in this run. Same vocabulary as
   *  `AlreadyPresentFilterResult.available`; see that type's doc for why a failed check must not
   *  read as a clean `skippedDuplicates: 0`. Defaults to `true` so existing callers/tests that omit
   *  it keep reading as "checked, nothing to skip". */
  readonly duplicateCheckAvailable = signal(true);

  /** #149 P2 (independent review): whether the notice built from the two signals above should
   *  currently be shown — true for `DUPLICATE_NOTICE_MS` after any `startImport` call that had
   *  something to report (`skippedDuplicates > 0 || !duplicateCheckAvailable`), including a refused
   *  (all-duplicates) call. `dockVisible()` (`usage-stats-page.ts`, via `action-dock.ts`) treats this
   *  exactly like an active run, which is what lets `import-progress-section` mount at all in that
   *  refused case — without it the section's own gate (`isRunning() || queue().length > 0`) would
   *  never fire, since a refused call leaves both false, and the notice that is the run's *only*
   *  outcome would be unreachable. Self-clearing rather than requiring a manual dismiss for the same
   *  reason `usage-stats-page`'s `selectionPrunedFeedback` is (design doc §4.5): a refused call has
   *  no run/queue for a dismiss button to attach to, and a persistent flag would otherwise be able
   *  to sit next to an unrelated *later* run's details with nothing to clear it. */
  readonly duplicateNoticePending = signal(false);

  private duplicateNoticeTimeout: ReturnType<typeof setTimeout> | undefined;

  /** `rows` are expected deduplicated (`dedupeImportRows`) and already filtered against the
   *  dialog-time target snapshot (`buildImportPreview`); this method does no filtering of its own.
   *  `skippedDuplicates` is the caller's own count from the *fresh* re-check it ran just before this
   *  call (see `already-present-filter.ts`) — defaults to 0 so existing callers/tests that pass only
   *  three arguments are unaffected. `duplicateCheckAvailable` mirrors the same call's `available`
   *  and defaults to `true` for the same reason. */
  startImport(
    target: { setId: string; channelName: string },
    origin: ImportOrigin,
    rows: ImportRow[],
    skippedDuplicates = 0,
    duplicateCheckAvailable = true,
  ): void {
    this.skippedDuplicates.set(skippedDuplicates);
    this.duplicateCheckAvailable.set(duplicateCheckAvailable);
    this.showDuplicateNotice(skippedDuplicates > 0 || !duplicateCheckAvailable);
    // The 7TV id is the only identity an imported row has — the emote does not exist in our
    // database yet, so there is no internal `emoteId` to mirror the key from.
    const queueEmotes: RunQueueEmote[] = rows.map((row) => ({
      key: row.sevenTvEmoteId,
      sevenTvEmoteId: row.sevenTvEmoteId,
      name: row.name,
    }));
    const started: ImportRunInfo = {
      targetChannelName: target.channelName,
      targetSetId: target.setId,
      origin,
      result: null,
    };

    if (
      !this.engine.start(target.setId, queueEmotes, this.addOperation, (result) =>
        this.onRunComplete(started, result),
      )
    ) {
      // Refused (already running, empty list, no token) — leave every signal as it was, except
      // skippedDuplicates and duplicateCheckAvailable above: an all-duplicates import is a
      // legitimate "refused" case whose count (and whether it is even trustworthy) the caller still
      // needs to see.
      return;
    }

    this.run.set(started);
    this.syncReport.set('idle');
    this.resyncTrigger.set('idle');
    this.abortedForPrivileges.set(false);
  }

  cancel(): void {
    this.engine.cancel();
  }

  reset(): void {
    this.engine.reset();
    this.run.set(null);
    this.syncReport.set('idle');
    this.resyncTrigger.set('idle');
    this.abortedForPrivileges.set(false);
    this.skippedDuplicates.set(0);
    this.duplicateCheckAvailable.set(true);
    this.showDuplicateNotice(false);
  }

  /** Manual retry for the closing report — the 7TV adds are long done, so this only re-sends the
   *  bookkeeping call. Safe to repeat: the endpoint only writes an audit entry. Target *and* keys
   *  come from the same record, so a retry can never mix one run's keys with another's channel. */
  retrySyncReport(): void {
    const current = this.run();
    if (
      this.syncReport() === 'pending' ||
      current === null ||
      current.result === null ||
      current.result.doneKeys.length === 0
    ) {
      return;
    }

    this.reportImported(current);
  }

  private onRunComplete(started: ImportRunInfo, result: RunResult): void {
    if (this.run() !== started) {
      // Only reachable via reset() during the run: the shown run is not this one any more, so
      // neither its result nor its bookkeeping belong on screen.
      return;
    }

    // A new object rather than a mutation, so consumers of `run()` actually see the result. From
    // here on this is the record the follow-up is bound to.
    const finished: ImportRunInfo = { ...started, result };
    this.run.set(finished);

    if (result.doneKeys.length === 0) {
      return;
    }

    // Deliberately both, in parallel: the report is the audit trail for exactly these ids, the
    // resync is what actually pulls the new emote rows in from 7TV. Neither replaces the other.
    this.reportImported(finished);
    this.resyncTrigger.set('pending');
    this.channelService.resync(finished.targetChannelName).subscribe({
      next: () => this.applyIfCurrent(finished, () => this.resyncTrigger.set('succeeded')),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.applyIfCurrent(finished, () =>
          this.resyncTrigger.set(error.status === 429 ? 'cooldown' : 'failed'),
        ),
    });
  }

  private reportImported(run: ImportRunInfo): void {
    const doneKeys = run.result?.doneKeys ?? [];
    this.syncReport.set('pending');

    this.emoteAdminService
      .syncImported(run.targetChannelName, {
        sevenTvEmoteIds: doneKeys,
        // Through the two exhaustive helpers, never through a `=== 'channel'`/`=== 'seventv-leaderboard'`
        // test: this call runs after the 7TV mutations, so a kind that silently loses its source name
        // or sort here is answered with a 400 when the emotes are already copied and the provenance is
        // unrecoverable (spec F6/F1). A file still sends `null` for the source channel even when it
        // names one, and every non-leaderboard origin sends `null` for the sort — both rules live in
        // the helpers, not here.
        sourceChannelName: importOriginSourceChannelName(run.origin),
        sourceKind: run.origin.kind,
        leaderboardSort: importOriginLeaderboardSort(run.origin),
      })
      .pipe(
        // Same policy as the delete's and the restore's report: waiting can fix a 429/5xx, not a
        // 401/403.
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        next: () => this.applyIfCurrent(run, () => this.syncReport.set('succeeded')),
        error: () => this.applyIfCurrent(run, () => this.syncReport.set('failed')),
      });
  }

  /** The R15 guard in one place: an answer that belongs to a superseded run is dropped silently —
   *  no error state, nothing written. The run it belongs to is not on screen any more, and the one
   *  that is must not inherit its outcome. */
  private applyIfCurrent(run: ImportRunInfo, apply: () => void): void {
    if (this.run() !== run) {
      return;
    }
    apply();
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
