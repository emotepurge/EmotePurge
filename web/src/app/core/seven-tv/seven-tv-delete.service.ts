import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { retry, throwError, timer } from 'rxjs';

import { EmoteAdminService, SyncDeletedResult } from '../emotes/emote-admin.service';
import {
  RunItemStatus,
  RunOperation,
  RunQueueEmote,
  RunQueueItem,
  RunResult,
  SevenTvRunEngine,
} from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

/** Kept under its historical name — the engine's constant is the same value. */
export { RUN_DELAY_MS as DELETE_DELAY_MS } from './seven-tv-run-engine';
// Exported for the restore service, which reports its run with the identical policy.
export const MAX_AUTOMATIC_SYNC_RETRIES = 2;
// Multiplied by the attempt number, so the two automatic attempts land at 2s and 4s. Kept short on
// purpose: the deletions themselves are already done, the admin is waiting on a verdict, and a
// manual retry button covers the cases a short backoff cannot.
export const SYNC_RETRY_DELAY_MS = 2000;

/** v4 dropped the `action` enum in favour of one field per operation; the emote travels inside the
 *  `EmoteSetEmoteId` input object rather than as a sibling argument, and variable types are `Id!`
 *  instead of `ObjectID!`. Removal has no alias, unlike `addEmote` in the import/restore services. */
const REMOVE_EMOTE_MUTATION = `
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

/** Outcome of reporting the finished run back to our own API (not to 7TV).
 *  'partial' means the call succeeded but the backend archived fewer emotes than we reported. */
export type SyncReportState = 'idle' | 'pending' | 'succeeded' | 'partial' | 'failed';

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
  channelName: string;
  /** The set the run removes from, frozen when it starts (spec #200, 7.2, AK 71): the first
   *  report and every retry name this set, whatever the page's set dropdown shows by then. */
  setId: string;
  /** `null` while the run is in flight; set once the engine reports the run complete. */
  result: RunResult | null;
}

@Injectable({ providedIn: 'root' })
export class SevenTvDeleteService {
  private readonly emoteAdminService = inject(EmoteAdminService);

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

  /** The finished run, kept for the summary/protocol UI (A6). Cleared on reset() — once the panel
   *  is dismissed, the downloaded protocol file is the only remaining artifact, by design. */
  readonly lastRun = signal<{ setId: string; channelName: string; result: RunResult } | null>(null);

  startDelete(setId: string, channelName: string, emotes: DeleteQueueEmote[]): void {
    const started: DeleteRunInfo = { channelName, setId, result: null };
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
    this.run = null;
    this.lastRun.set(null);
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
   *  Channel, set *and* keys come from the same record, so a retry can never mix one run's ids
   *  with another's channel or with a set chosen after the run started (R15, AK 71). */
  retrySyncReport(): void {
    const current = this.run;
    if (
      this.syncReport() === 'pending' ||
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
      this.reportDeleted(finished, result);
    }
  }

  private reportDeleted(run: DeleteRunInfo, result: RunResult): void {
    this.syncReport.set('pending');
    // A delete run's keys are its 7TV ids (see toDeleteQueue) — one per cell, unique in the run.
    const sevenTvEmoteIds = result.doneKeys;

    this.emoteAdminService
      .syncDeleted(run.channelName, { emoteSetId: run.setId, sevenTvEmoteIds })
      .pipe(
        // A 429 is the realistic case: sync-deleted shares a rate-limit budget with other calls, and
        // a swallowed 429 used to look exactly like success. A 401 (session expired during a long
        // run) cannot be fixed by waiting, so it is not retried.
        retry({
          count: MAX_AUTOMATIC_SYNC_RETRIES,
          delay: (error: HttpErrorResponse, attempt) =>
            error.status === 401 || error.status === 403
              ? throwError(() => error)
              : timer(SYNC_RETRY_DELAY_MS * attempt),
        }),
      )
      .subscribe({
        next: (answer: SyncDeletedResult) =>
          // notFoundIds covers ids the backend could not archive (unknown, foreign channel). All of
          // them coming back is indistinguishable from success in the raw numbers, which is why the
          // result is evaluated at all instead of being discarded. A non-active set is paper only
          // (spec #200, 6.6): `archivedCount` is 0 by design there, the audit entry is the success.
          this.applyIfCurrent(run, () =>
            this.syncReport.set(
              answer.targetIsActiveSetOfChannel === false ||
                answer.archivedCount >= sevenTvEmoteIds.length
                ? 'succeeded'
                : 'partial',
            ),
          ),
        error: () => this.applyIfCurrent(run, () => this.syncReport.set('failed')),
      });
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
