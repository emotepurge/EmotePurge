import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { retry, throwError, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import {
  DeleteQueueEmote,
  MAX_AUTOMATIC_SYNC_RETRIES,
  SYNC_RETRY_DELAY_MS,
} from './seven-tv-delete.service';
import { SyncRestoredInSetResponse } from './seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from './seven-tv-emote-set.service';
import { RunOperation, RunQueueEmote, RunResult, SevenTvRunEngine } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';
import {
  SyncReportOutcome,
  SyncReportReason,
  SyncReportState,
  classifySyncInSetFailure,
  classifySyncInSetResponse,
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
 *  60s tick either way. `'backendTriggered'` (restore only, spec 6.4/E12/F15) means the report's
 *  own answer named the channel in `resyncTriggered`: the backend already started the resync, so
 *  no request of ours went out — the dock says "being re-synced" all the same. The import never
 *  takes this value; its resync skips such a channel and stays `'idle'`. */
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
 * - `resyncChannelName` — the tracked channel whose resync the client itself may trigger: set only
 *   for a *non-active* set of a tracked channel, otherwise `null` (E12). No backend resync covers
 *   that case, and it is what reloads the non-active set's member list (spec #200, 8.3).
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
 * One restore run, from the moment it starts to the moment both closing calls are done (spec E13).
 * Everything the two asynchronous follow-ups need hangs off *this* object, never off a field next
 * to the service (R15, #72, T12) — see the identical note on `DeleteRunInfo` in
 * `seven-tv-delete.service.ts` and on `ImportRunInfo` in `seven-tv-import.service.ts`. A restore has
 * *two* callbacks racing a superseded run (`sync-restored` and `resync`), each checked
 * independently: one settling first must not stop the other's guard from applying.
 */
export interface RestoreRunInfo {
  /** The set the run re-adds into, frozen when it starts (spec #200, 7.2, AK 71) — the report and
   *  every retry name this set. */
  targetSetId: string;
  /** See `RestoreStartTarget.expectedChannelName` — sent with every report and retry. */
  expectedChannelName: string | null;
  /** See `RestoreStartTarget.resyncChannelName`. */
  resyncChannelName: string | null;
  /** See `RestoreStartTarget.hostChannelName`. */
  hostChannelName: string;
  /** Display only (the dock's target line). */
  setName: string;
  /** Display only (the dock's target line): the tracked channel or the owner's display name. */
  ownerOrChannelLabel: string;
  /** `null` while the run is in flight; set once the engine reports the run complete. */
  result: RunResult | null;
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
 * `resyncTriggered`; this service only follows up with its own resync for a non-active set of a
 * tracked channel (E12), and only once the report has answered, so a channel is never resynced
 * twice for one report (F15, AK 27). A first report that fails for good gets a resync of
 * `resyncChannelName ?? expectedChannelName` instead, since the backend never reached its own
 * (Nachtrag N1, AK 36).
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

  /** The run every asynchronous follow-up is bound to (R15). */
  private readonly runState = signal<RestoreRunInfo | null>(null);

  /** The run this service is showing — in flight (`result === null`) or finished. Read-only view,
   *  for the dock's target line (`setName`, `ownerOrChannelLabel`). */
  readonly run = this.runState.asReadonly();

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** State of the closing sync-restored call — same contract as the delete's syncReport. */
  readonly syncReport = signal<SyncReportState>('idle');

  /** Why `syncReport` is `'failed'`/`'partial'` (spec E23), `null` otherwise — the dock shows it as
   *  its own line under the report notice. */
  readonly syncReportReason = signal<SyncReportReason | null>(null);

  readonly resyncTrigger = signal<ResyncTriggerState>('idle');

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

  private duplicateNoticeTimeout: ReturnType<typeof setTimeout> | undefined;

  /** `target` is where the run goes and whom it tells — see `RestoreStartTarget`; frozen into the
   *  run record here and never read again from the caller. `skippedDuplicates` is the caller's own
   *  count from filtering `emotes` *before* this call — this method does no filtering of its own
   *  (see `already-present-filter.ts`, which every current caller runs first). Defaults to 0 so
   *  callers/tests that pass only two arguments are unaffected. `duplicateCheckAvailable` mirrors
   *  the same call's `available` and defaults to `true` for the same reason; `skippedNameTaken` is
   *  the same call's name-taken count, default 0. */
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
    const started: RestoreRunInfo = {
      targetSetId: target.setId,
      expectedChannelName: target.expectedChannelName,
      resyncChannelName: target.resyncChannelName,
      hostChannelName: target.hostChannelName,
      setName: target.setName,
      ownerOrChannelLabel: target.ownerOrChannelLabel,
      result: null,
    };
    const { queue, aliasByKey } = toRestoreQueue(emotes);
    const engineStarted = this.engine.start(
      target.setId,
      queue,
      addOperation(aliasByKey),
      (result) => this.onRunComplete(started, result),
    );
    if (!engineStarted) {
      // Refused (already running, empty list, no token) — leave every signal as it was, except
      // skippedDuplicates, skippedNameTaken and duplicateCheckAvailable above: an all-skipped
      // restore is a legitimate "refused" case whose counts (and whether they are even
      // trustworthy) the caller still needs to see.
      return;
    }
    this.runState.set(started);
    this.syncReport.set('idle');
    this.syncReportReason.set(null);
    this.resyncTrigger.set('idle');
  }

  cancel(): void {
    this.engine.cancel();
  }

  reset(): void {
    this.engine.reset();
    this.syncReport.set('idle');
    this.syncReportReason.set(null);
    this.resyncTrigger.set('idle');
    this.skippedDuplicates.set(0);
    this.skippedNameTaken.set(0);
    this.duplicateCheckAvailable.set(true);
    this.showDuplicateNotice(false);
    this.runState.set(null);
  }

  /** Same page-follows-user reasoning as the delete service's counterpart — compared against the
   *  channel of the page the run was started on (`hostChannelName`, F7/E13), never against the
   *  target: the layout calling this only knows its own page's channel, and a run into another
   *  channel's set or an untracked set still belongs to the page it was started from (AK 20). A
   *  running run is left alone, and a finished one's report has gone out regardless. */
  resetIfChannelChanged(pageChannelName: string): void {
    const current = this.runState();
    if (this.isRunning() || current === null || current.hostChannelName === pageChannelName) {
      return;
    }
    this.reset();
  }

  /** Manual retry for the closing report — the 7TV re-adds are long done, so this only re-sends
   *  the bookkeeping call, never the resync (that followed the first report already). Safe to
   *  repeat: ids already un-archived still count as restored. Set, expected channel *and* keys come
   *  from the same record, so a retry can never mix one run's ids with another's target or with a
   *  set chosen after the run started (R15, AK 71). */
  retrySyncReport(): void {
    const current = this.runState();
    if (
      this.syncReport() === 'pending' ||
      !current?.result ||
      current.result.doneKeys.length === 0
    ) {
      return;
    }

    this.reportRestored(current, current.result);
  }

  private onRunComplete(started: RestoreRunInfo, result: RunResult): void {
    if (this.runState() !== started) {
      // Only reachable via reset()/resetIfChannelChanged() during the run: the shown run is not
      // this one any more, so neither its result nor its bookkeeping belong on screen.
      return;
    }

    const finished: RestoreRunInfo = { ...started, result };
    this.runState.set(finished);

    if (result.doneKeys.length === 0) {
      return;
    }
    // The report first, the resync after it (spec 6.4, F15): only the report's answer says whether
    // the backend already resynced the channel, and a second resync of ours would merely run into
    // the cooldown.
    this.reportRestored(finished, result, (resyncTriggered) =>
      this.resyncAfterReport(finished, resyncTriggered),
    );
  }

  /** `afterReport` runs once the report has settled either way — with the answer's
   *  `resyncTriggered` on success, with `null` once it has failed for good (the backend never
   *  reached its resync stage then, Nachtrag N1). It runs even for a superseded run: the report and
   *  the resync are owed to 7TV's state, not to what the dock shows; only the *state* each writes is
   *  guarded (`applyIfCurrent`). */
  private reportRestored(
    run: RestoreRunInfo,
    result: RunResult,
    afterReport?: (resyncTriggered: readonly string[] | null) => void,
  ): void {
    this.syncReport.set('pending');
    this.syncReportReason.set(null);
    const sevenTvEmoteIds = doneSevenTvEmoteIds(result);

    this.emoteSetService
      .reportRestoredInSet(run.targetSetId, {
        sevenTvEmoteIds,
        expectedChannelName: run.expectedChannelName,
      })
      .pipe(
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
        next: (answer: SyncRestoredInSetResponse) => {
          // The threeway reading lives in one place for all three services (F8, E23, AK 15) —
          // never an `>=` of our own here.
          this.applyIfCurrent(run, () =>
            this.applyReportOutcome(classifySyncInSetResponse(answer, sevenTvEmoteIds.length)),
          );
          afterReport?.(answer.resyncTriggered);
        },
        error: (error: HttpErrorResponse) => {
          // A 404 (the set is gone) ends in 'failed'/'setNotFound' — never in 'succeeded' (#224).
          this.applyIfCurrent(run, () =>
            this.applyReportOutcome(classifySyncInSetFailure(error.status)),
          );
          afterReport?.(null);
        },
      });
  }

  /** E12, F15, AK 21/27, spec 6.4 and 4.4 point 11: a resync of our own only for a non-active set
   *  of a tracked channel (`resyncChannelName`), and only when the report's answer does not already
   *  name that channel in `resyncTriggered`. Whenever the answer names the channel this run is
   *  about — `resyncChannelName`, or for an active set its `expectedChannelName` (also when it came
   *  back unresolved, `activeSetDiffers`) — the dock says "being re-synced" (`'backendTriggered'`)
   *  without a request of ours. An active set whose channel is not named (the cooldown was not
   *  acquired, F15, or `notTracked`) and an untracked target (nothing to resync) leave
   *  `resyncTrigger` on `'idle'`: no request, no resync line.
   *
   *  `resyncTriggered === null` is a report that failed for good (Nachtrag N1, AK 36) — any status,
   *  or a network error, after the retries. The backend never reached its resync stage, so the
   *  client stands in for it with `resyncChannelName ?? expectedChannelName`; the cooldown absorbs a
   *  duplicate. Only after the first report of a run: a manual retry passes no `afterReport`. */
  private resyncAfterReport(run: RestoreRunInfo, resyncTriggered: readonly string[] | null): void {
    if (resyncTriggered === null) {
      const fallbackChannel = run.resyncChannelName ?? run.expectedChannelName;
      if (fallbackChannel !== null) {
        this.triggerResync(run, fallbackChannel);
      }
      return;
    }
    const channelName = run.resyncChannelName;
    if (channelName === null) {
      const expected = run.expectedChannelName;
      if (expected !== null && includesChannel(resyncTriggered, expected)) {
        this.applyIfCurrent(run, () => this.resyncTrigger.set('backendTriggered'));
      }
      return;
    }
    if (includesChannel(resyncTriggered, channelName)) {
      this.applyIfCurrent(run, () => this.resyncTrigger.set('backendTriggered'));
      return;
    }
    this.triggerResync(run, channelName);
  }

  private triggerResync(run: RestoreRunInfo, channelName: string): void {
    this.applyIfCurrent(run, () => this.resyncTrigger.set('pending'));
    this.channelService.resync(channelName).subscribe({
      next: () => this.applyIfCurrent(run, () => this.resyncTrigger.set('succeeded')),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.applyIfCurrent(run, () =>
          this.resyncTrigger.set(error.status === 429 ? 'cooldown' : 'failed'),
        ),
    });
  }

  private applyReportOutcome(outcome: SyncReportOutcome): void {
    this.syncReport.set(outcome.state);
    this.syncReportReason.set(outcome.reason);
  }

  /** The R15 guard in one place: an answer that belongs to a superseded run is dropped silently —
   *  no error state, nothing written. The run it belongs to is not on screen any more, and the one
   *  that is must not inherit its outcome. Shared by both closing calls (`sync-restored`, `resync`)
   *  — each checks independently, so one settling does not gate the other. */
  private applyIfCurrent(run: RestoreRunInfo, apply: () => void): void {
    if (this.runState() !== run) {
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
