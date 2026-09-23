import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { retry, throwError, timer } from 'rxjs';

import { ChannelService } from '../channels/channel.service';
import { EmoteAdminService, SyncRestoredResult } from '../emotes/emote-admin.service';
import {
  DeleteQueueEmote,
  MAX_AUTOMATIC_SYNC_RETRIES,
  SYNC_RETRY_DELAY_MS,
  SyncReportState,
} from './seven-tv-delete.service';
import { RunOperation, RunQueueEmote, RunResult, SevenTvRunEngine } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

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
 *  60s tick either way. */
export type ResyncTriggerState = 'idle' | 'pending' | 'succeeded' | 'cooldown' | 'failed';

/**
 * One restore run, from the moment it starts to the moment both closing calls are done. Everything
 * the two asynchronous follow-ups need hangs off *this* object, never off a field next to the
 * service (R15, #72, T12) — see the identical note on `DeleteRunInfo` in
 * `seven-tv-delete.service.ts` and on `ImportRunInfo` in `seven-tv-import.service.ts`. A restore has
 * *two* callbacks racing a superseded run (`sync-restored` and `resync`), each checked
 * independently: one settling first must not stop the other's guard from applying.
 */
interface RestoreRunInfo {
  channelName: string;
  /** The set the run re-adds into, frozen when it starts (spec #200, 7.2, AK 71) — the report and
   *  every retry name this set. */
  setId: string;
  /** `null` while the run is in flight; set once the engine reports the run complete. */
  result: RunResult | null;
}

/**
 * The restore half of A6: re-adds emotes to the 7TV set, in the browser, over the same run engine
 * (pacing, backoff, token) as the delete — ADD draws tickets from the same `emote_set_change`
 * bucket. Zero-knowledge holds: the write token never leaves the browser.
 *
 * A finished run reports itself twice, for two different reasons. `sync-restored` is the
 * bookkeeping call (mirror of the delete's `sync-deleted`): it un-archives the rows and — the
 * reason it exists at all — writes the `emotes.syncRestored` audit entry; before it, a restore
 * only ever appeared in the log as an anonymous `channel.resync`, or under the resync cooldown
 * not at all (user decision 2026-08-02, revising the original "no sync-restored endpoint" call).
 * The A8 resync trigger stays on top as reconciliation against 7TV as the authority — aliases the
 * run could not restore, and anything else that drifted.
 */
@Injectable({ providedIn: 'root' })
export class SevenTvRestoreService {
  private readonly channelService = inject(ChannelService);
  private readonly emoteAdminService = inject(EmoteAdminService);

  /** Own engine instance — see the identical note in SevenTvDeleteService. */
  private readonly engine = new SevenTvRunEngine(
    inject(HttpClient),
    inject(SevenTvTokenService),
    inject(TranslocoService),
  );

  /** The run every asynchronous follow-up is bound to (R15). */
  private run: RestoreRunInfo | null = null;

  readonly queue = this.engine.queue;
  readonly isRunning = this.engine.isRunning;
  readonly rateLimitPauseSeconds = this.engine.rateLimitPauseSeconds;
  readonly progress = this.engine.progress;

  /** State of the closing sync-restored call — same contract as the delete's syncReport. */
  readonly syncReport = signal<SyncReportState>('idle');

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
   *  exactly like an active restore, which is what lets `MassDeletePanel` mount at all in that
   *  refused case — without it the panel's own gate (`isRunning() || queue().length > 0`) would
   *  never fire, since a refused call leaves both false, and the notice that is the run's *only*
   *  outcome would be unreachable. Self-clearing rather than requiring a manual dismiss for the same
   *  reason `usage-stats-page`'s `selectionPrunedFeedback` is (design doc §4.5): a refused call has
   *  no run/queue for a dismiss button to attach to, and a persistent flag would otherwise be able
   *  to sit next to an unrelated *later* run's details with nothing to clear it. */
  readonly duplicateNoticePending = signal(false);

  private duplicateNoticeTimeout: ReturnType<typeof setTimeout> | undefined;

  /** `skippedDuplicates` is the caller's own count from filtering `emotes` *before* this call —
   *  this method does no filtering of its own (see `already-present-filter.ts`, which every current
   *  caller runs first). Defaults to 0 so existing callers/tests that pass only three arguments are
   *  unaffected. `duplicateCheckAvailable` mirrors the same call's `available` and defaults to
   *  `true` for the same reason; `skippedNameTaken` is the same call's name-taken count, default 0. */
  startRestore(
    setId: string,
    channelName: string,
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
    const started: RestoreRunInfo = { channelName, setId, result: null };
    const { queue, aliasByKey } = toRestoreQueue(emotes);
    const engineStarted = this.engine.start(setId, queue, addOperation(aliasByKey), (result) =>
      this.onRunComplete(started, result),
    );
    if (!engineStarted) {
      // Refused (already running, empty list, no token) — leave every signal as it was, except
      // skippedDuplicates, skippedNameTaken and duplicateCheckAvailable above: an all-skipped
      // restore is a legitimate "refused" case whose counts (and whether they are even
      // trustworthy) the caller still needs to see.
      return;
    }
    this.run = started;
    this.syncReport.set('idle');
    this.resyncTrigger.set('idle');
  }

  cancel(): void {
    this.engine.cancel();
  }

  reset(): void {
    this.engine.reset();
    this.syncReport.set('idle');
    this.resyncTrigger.set('idle');
    this.skippedDuplicates.set(0);
    this.skippedNameTaken.set(0);
    this.duplicateCheckAvailable.set(true);
    this.showDuplicateNotice(false);
    this.run = null;
  }

  /** Same page-follows-user reasoning as the delete service's counterpart. */
  resetIfChannelChanged(channelName: string): void {
    if (this.isRunning() || this.run === null || this.run.channelName === channelName) {
      return;
    }
    this.reset();
  }

  /** Manual retry for the closing report — the 7TV re-adds are long done, so this only re-sends
   *  the bookkeeping call. Safe to repeat: ids already un-archived still count as restored.
   *  Channel, set *and* keys come from the same record, so a retry can never mix one run's ids with
   *  another's channel or with a set chosen after the run started (R15, AK 71). */
  retrySyncReport(): void {
    const current = this.run;
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
    if (this.run !== started) {
      // Only reachable via reset()/resetIfChannelChanged() during the run: the shown run is not
      // this one any more, so neither its result nor its bookkeeping belong on screen.
      return;
    }

    const finished: RestoreRunInfo = { ...started, result };
    this.run = finished;

    if (result.doneKeys.length === 0) {
      return;
    }
    // Deliberately both, in parallel: the report is bookkeeping + audit trail for exactly these
    // ids, the resync is reconciliation against 7TV as the authority. Neither replaces the other.
    this.reportRestored(finished, result);
    this.resyncTrigger.set('pending');
    this.channelService.resync(finished.channelName).subscribe({
      next: () => this.applyIfCurrent(finished, () => this.resyncTrigger.set('succeeded')),
      error: (error: HttpErrorResponse) =>
        // 429 = the per-channel cooldown: a sync just ran or will run — "coming on its own",
        // reported as such rather than as an error.
        this.applyIfCurrent(finished, () =>
          this.resyncTrigger.set(error.status === 429 ? 'cooldown' : 'failed'),
        ),
    });
  }

  private reportRestored(run: RestoreRunInfo, result: RunResult): void {
    this.syncReport.set('pending');
    const sevenTvEmoteIds = doneSevenTvEmoteIds(result);

    this.emoteAdminService
      .syncRestored(run.channelName, { emoteSetId: run.setId, sevenTvEmoteIds })
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
        next: (answer: SyncRestoredResult) =>
          // Paper only for a non-active set (spec #200, 6.6) — `restoredCount` is 0 by design there.
          this.applyIfCurrent(run, () =>
            this.syncReport.set(
              answer.targetIsActiveSetOfChannel === false ||
                answer.restoredCount >= sevenTvEmoteIds.length
                ? 'succeeded'
                : 'partial',
            ),
          ),
        error: () => this.applyIfCurrent(run, () => this.syncReport.set('failed')),
      });
  }

  /** The R15 guard in one place: an answer that belongs to a superseded run is dropped silently —
   *  no error state, nothing written. The run it belongs to is not on screen any more, and the one
   *  that is must not inherit its outcome. Shared by both closing calls (`sync-restored`, `resync`)
   *  — each checks independently, so one settling does not gate the other. */
  private applyIfCurrent(run: RestoreRunInfo, apply: () => void): void {
    if (this.run !== run) {
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

/** The 7TV ids a restore run finished, read off its `doneKeys` — once each, even when two aliases
 *  of one emote came back (spec #200, AK 69: two `ADD`s, one id in the report). */
function doneSevenTvEmoteIds(result: RunResult): string[] {
  const done = new Set(result.doneKeys);
  const ids = result.items.filter((item) => done.has(item.key)).map((item) => item.sevenTvEmoteId);
  return [...new Set(ids)];
}
