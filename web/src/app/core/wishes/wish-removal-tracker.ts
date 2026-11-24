import { HttpErrorResponse } from '@angular/common/http';
import { inject, Service, signal } from '@angular/core';
import { Observable, Subject } from 'rxjs';

import { apiErrorCode } from '../i18n/api-error';
import {
  DeleteRowEvidence,
  DeleteRunSettlement,
  SevenTvDeleteService,
  timeoutReportAttempt,
} from '../seven-tv/seven-tv-delete.service';
import { SkipReason } from '../seven-tv/seven-tv-run-engine';
import type { Wish } from './wish.model';
import { WishRemovalProgress, WishRemovalTarget } from './wish-removal';
import type { WishStartAbortReason } from './wish-run-start';
import { WishService } from './wish.service';

/** Where one wish's removal stands (plan 2.6). */
export type WishRemovalState =
  /** The start contract runs (click to start). */
  | { kind: 'starting' }
  /** The start contract stopped before any write. */
  | { kind: 'notStarted'; reason: WishStartAbortReason }
  /** The delete run works on the entry, or waits out 7TV's rate limit (`pauseSeconds`). */
  | { kind: 'running'; pauseSeconds: number | null }
  /** A check before the removal stopped it; no request went out for it. */
  | { kind: 'skipped'; reason: SkipReason }
  /** 7TV refused the removal. */
  | { kind: 'failed'; message: string }
  /** The run was cancelled before the removal went out, or it is known not to have landed. */
  | { kind: 'cancelled' }
  /** The answer got lost and the re-read could not tell; the next sync shows it. */
  | { kind: 'unknown' }
  /** Removed in 7TV; `complete-removal` is out. */
  | { kind: 'reporting' }
  /** Removed in 7TV and booked (200). */
  | { kind: 'booked' }
  /**
   * Removed in 7TV, but the Api booked nothing (409/404): the wish moved on meanwhile. Final.
   * `deleted` is set when the answer was 404 `wish_not_found`: a manager deleted the wish meanwhile (K7a).
   */
  | { kind: 'notBooked'; deleted?: true }
  /** Removed in 7TV; the report did not get through (network, 5xx, timeout) — `retry` sends it again. */
  | { kind: 'reportPending' };

/** A removal that reached an end state, for the page to reload its lists by. */
export interface WishRemovalEnd {
  readonly channelName: string;
  readonly wishId: number;
  readonly state: WishRemovalState;
}

/** States `prune` never drops: a removal under way or a report out or owed. */
const BUSY_KINDS: ReadonlySet<WishRemovalState['kind']> = new Set([
  'starting',
  'running',
  'reporting',
  'reportPending',
]);

/** Statuses in which a finished removal's line still says something about the row. */
const STILL_RELEVANT_STATUSES: ReadonlySet<string> = new Set(['active', 'presented', 'removed']);

interface TrackedRun {
  readonly key: string;
  settled: boolean;
}

interface OwedReport {
  readonly target: WishRemovalTarget;
  readonly evidence: DeleteRowEvidence;
}

/**
 * The outcome of a removal from a wish row, per channel and wish, and its booking with the Api
 * (#485, plan 2.6, Sol-P1-2, Sol-P2-7). Root, so it outlives the page: a run started on the wishes
 * page books its outcome after a navigation all the same.
 *
 * Per accepted run it holds the frozen target (`WishRemovalTarget`: channel, wish, slot, generation,
 * set) and waits for the run's one completion report. Its row `done` → **exactly one**
 * `complete-removal` with that target and the row's evidence (`acknowledged` when 7TV confirmed the
 * removal, `absent` when only the re-read found the entry gone, R20); every other outcome → none,
 * with the reason at the row. The answers (plan 2.1a): 200 is booked; 409 and 404 are final, never
 * sent again; anything else (network, 5xx, the 30 s attempt budget) leaves the report owed, and
 * `retry` sends the identical body — harmless, the Api answers a repeat with 200 (row 5).
 */
@Service()
export class WishRemovalTracker {
  private readonly wishService = inject(WishService);
  private readonly deleteService = inject(SevenTvDeleteService);

  private readonly states = signal<ReadonlyMap<string, WishRemovalState>>(new Map());
  private readonly runs = new Map<string, TrackedRun>();
  private readonly owed = new Map<string, OwedReport>();
  private readonly ends = new Subject<WishRemovalEnd>();

  /** Every removal that reached an end state (all but starting, running, reporting). */
  readonly ended: Observable<WishRemovalEnd> = this.ends.asObservable();

  /** The progress sink for one click on "Entfernen" of this wish. */
  progressFor(channelName: string, wishId: number): WishRemovalProgress {
    const key = stateKey(channelName, wishId);
    return {
      starting: () => this.setState(key, { kind: 'starting' }),
      notStarted: (reason) => this.end(key, channelName, wishId, { kind: 'notStarted', reason }),
      dismissed: () => this.setState(key, null),
      started: (runId) => {
        const known = this.runs.get(runId);
        if (known === undefined) {
          this.runs.set(runId, { key, settled: false });
          this.setState(key, { kind: 'running', pauseSeconds: null });
        }
      },
      settled: (target, settlement) => this.settle(key, target, settlement),
    };
  }

  /** The removal state of this wish, `null` when there is none to show. */
  stateFor(channelName: string, wish: Pick<Wish, 'id'>): WishRemovalState | null {
    const state = this.states().get(stateKey(channelName, wish.id)) ?? null;
    if (state?.kind === 'running' && this.deleteService.isRunning()) {
      return { kind: 'running', pauseSeconds: this.deleteService.rateLimitPauseSeconds() };
    }
    return state;
  }

  /**
   * Whether this wish's removal is done in 7TV but its booking is out or owed — no second removal may
   * start over it. A removal still starting or running needs no such lock: it holds the arbiter,
   * which locks every 7TV action already (plan 2.7).
   */
  owesReport(channelName: string, wishId: number): boolean {
    const kind = this.states().get(stateKey(channelName, wishId))?.kind;
    return kind === 'reporting' || kind === 'reportPending';
  }

  /** Sends an owed report again, with the identical body; nothing for any other state. */
  retry(channelName: string, wishId: number): void {
    const key = stateKey(channelName, wishId);
    const report = this.owed.get(key);
    if (report !== undefined && this.states().get(key)?.kind === 'reportPending') {
      this.report(key, report);
    }
  }

  /** Drops the finished lines of the given wishes once they are no longer `active`, `presented` or
   *  `removed` (re-added, closed, …): the line no longer describes the row. A removal under way and
   *  an owed report always stay. */
  prune(channelName: string, wishes: readonly Pick<Wish, 'id' | 'status'>[]): void {
    const current = this.states();
    const drop = wishes
      .filter((wish) => !STILL_RELEVANT_STATUSES.has(wish.status))
      .map((wish) => stateKey(channelName, wish.id))
      .filter((key) => {
        const state = current.get(key);
        return state !== undefined && !BUSY_KINDS.has(state.kind);
      });
    if (drop.length === 0) {
      return;
    }
    const next = new Map(current);
    drop.forEach((key) => next.delete(key));
    this.states.set(next);
  }

  /** The run's one completion report: book a `done` row, show every other outcome. Once per run. */
  private settle(key: string, target: WishRemovalTarget, settlement: DeleteRunSettlement): void {
    const run = this.runs.get(settlement.runId);
    if (run?.settled) {
      return;
    }
    this.runs.set(settlement.runId, { key, settled: true });
    // The run has this one row; its key ends in the card key the flow gave it.
    const row =
      settlement.result.items.find((item) => item.key.endsWith(`#wish-${target.wishId}`)) ??
      settlement.result.items[0];
    const done = row !== undefined && settlement.result.doneKeys.includes(row.key);
    if (done) {
      this.report(key, {
        target,
        evidence: settlement.evidence.get(row.key) ?? 'acknowledged',
      });
      return;
    }
    this.end(key, target.channelName, target.wishId, outcomeOf(row));
  }

  private report(key: string, report: OwedReport): void {
    const { target } = report;
    this.owed.set(key, report);
    this.setState(key, { kind: 'reporting' });
    this.wishService
      .completeRemoval(target.channelName, target.wishId, {
        slotId: target.slotId,
        slotAddedAtUtc: target.slotAddedAtUtc,
        emoteSetId: target.emoteSetId,
        evidence: report.evidence,
      })
      .pipe(timeoutReportAttempt())
      .subscribe({
        next: () => {
          this.owed.delete(key);
          this.end(key, target.channelName, target.wishId, { kind: 'booked' });
        },
        error: (error: unknown) => {
          if (isFinalRefusal(error)) {
            this.owed.delete(key);
            this.end(
              key,
              target.channelName,
              target.wishId,
              isWishGone(error) ? { kind: 'notBooked', deleted: true } : { kind: 'notBooked' },
            );
            return;
          }
          this.end(key, target.channelName, target.wishId, { kind: 'reportPending' });
        },
      });
  }

  private end(key: string, channelName: string, wishId: number, state: WishRemovalState): void {
    this.setState(key, state);
    this.ends.next({ channelName, wishId, state });
  }

  private setState(key: string, state: WishRemovalState | null): void {
    const next = new Map(this.states());
    if (state === null) {
      next.delete(key);
    } else {
      next.set(key, state);
    }
    this.states.set(next);
  }
}

function stateKey(channelName: string, wishId: number): string {
  return `${channelName}#${wishId}`;
}

/** 409 and 404: this report books nothing and must not book anything (plan 2.1a). */
function isFinalRefusal(error: unknown): boolean {
  return error instanceof HttpErrorResponse && (error.status === 409 || error.status === 404);
}

/** 404 with `wish_not_found`: the wish itself is gone (deleted meanwhile), not just moved on. */
function isWishGone(error: unknown): boolean {
  return (
    error instanceof HttpErrorResponse &&
    error.status === 404 &&
    apiErrorCode(error) === 'wish_not_found'
  );
}

/** The end state of a row that is not `done`. */
function outcomeOf(
  row: DeleteRunSettlement['result']['items'][number] | undefined,
): WishRemovalState {
  switch (row?.status) {
    case 'cancelled':
      return row.skipReason === undefined
        ? { kind: 'cancelled' }
        : { kind: 'skipped', reason: row.skipReason };
    case 'failed':
      return { kind: 'failed', message: row.errorMessage ?? '' };
    default:
      // `unknown` after the re-read; a row that never ran cannot reach a settled run.
      return { kind: 'unknown' };
  }
}
