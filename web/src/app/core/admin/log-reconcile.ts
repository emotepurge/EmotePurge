import { LogEntry, LogKeyPage, LogRangeQuery } from './admin-logs.model';

/**
 * The live reconcile of the log list (spec §8.3): instead of trusting commit times or notifications,
 * the page periodically compares the ids in its loaded interval `[from_L, now)` with the server's keys
 * and fetches what is missing. Everything here is pure except the scheduler, which owns one timer.
 */

/** The server's span limit for any range, the reconcile's `[from_L, request time)` included. */
const MAX_SPAN_MS = 8 * 24 * 60 * 60 * 1000;
/** More missing entries than this ask for a reload instead of a silent gap. */
const MAX_MISSING = 500;
/** `GET /logs?ids=` takes at most this many ids per request. */
const IDS_PER_REQUEST = 200;
/** Idle gap between the end of one reconcile and the start of the next. */
const MIN_GAP_MS = 5000;

const INSTANT = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:\d{2})$/;

export type ReconcilePreflight = { kind: 'request' } | { kind: 'skip' } | { kind: 'reload' };

export type ReconcilePlan =
  { kind: 'none' } | { kind: 'fetch'; idBatches: number[][] } | { kind: 'reload' };

/**
 * `loaded` and `incoming` as one list, newest first by `(timestampUtc, id)` — the server's order — with
 * each id once. Late entries land at their sorted position, also in the middle of loaded pages. On a
 * duplicate the loaded object wins: entries never change server-side, and keeping the reference keeps
 * whatever the page hangs on it (expanded details) stable.
 */
export function mergeLogEntries(
  loaded: readonly LogEntry[],
  incoming: readonly LogEntry[],
): LogEntry[] {
  const byId = new Map<number, LogEntry>();
  for (const entry of [...loaded, ...incoming]) {
    if (!byId.has(entry.id)) {
      byId.set(entry.id, entry);
    }
  }
  return [...byId.values()].sort(newestFirst);
}

/**
 * `from_L`, the start of the loaded interval: the range start when nothing is loaded (R7) or when
 * pagination has reached its end — otherwise an entry committed late and older than every loaded one
 * would never be fetched — and the oldest loaded timestamp while more pages exist (those older entries
 * arrive by loading more). Returned exactly as the server sent it.
 */
export function reconcileFromUtc(
  loaded: readonly LogEntry[],
  rangeFromUtc: string,
  nextCursor: string | null,
): string {
  if (loaded.length === 0 || nextCursor === null) {
    return rangeFromUtc;
  }
  return loaded.reduce((oldest, e) => (newestFirst(e, oldest) > 0 ? e : oldest)).timestampUtc;
}

/**
 * Decides before every `getKeys` whether to ask at all. An absolute range has no live mode: `skip`. A page
 * left open on `7d` slowly pushes `[from_L, now)` past the server's 8-day limit, which would answer
 * `400 invalid_log_range` and pause the page; past that point this says `reload` without a request
 * ("too many new entries — reload the list", L3). `nowMs` is injected; the function is pure.
 */
export function preflightReconcile(
  fromL: string,
  nowMs: number,
  range: LogRangeQuery,
): ReconcilePreflight {
  if (!('range' in range)) {
    return { kind: 'skip' };
  }
  return nowMs - Date.parse(fromL) > MAX_SPAN_MS ? { kind: 'reload' } : { kind: 'request' };
}

/**
 * Compares the server's keys with the loaded ids. A truncated key list (more than 2,500) or more than
 * 500 missing entries mean `reload`; otherwise the missing ids, newest first, in batches of at most 200 —
 * at most three. Loaded ids the server no longer lists (retention) are left alone until the next reload.
 */
export function planReconcile(keys: LogKeyPage, loadedIds: ReadonlySet<number>): ReconcilePlan {
  if (keys.truncated) {
    return { kind: 'reload' };
  }
  const missing = keys.entries.map((k) => k.id).filter((id) => !loadedIds.has(id));
  if (missing.length === 0) {
    return { kind: 'none' };
  }
  if (missing.length > MAX_MISSING) {
    return { kind: 'reload' };
  }
  const idBatches: number[][] = [];
  for (let i = 0; i < missing.length; i += IDS_PER_REQUEST) {
    idBatches.push(missing.slice(i, i + IDS_PER_REQUEST));
  }
  return { kind: 'fetch', idBatches };
}

/**
 * Paces reconciles: at most one runs at a time, at least 5,000 ms pass between the end of one and the
 * start of the next, and every trigger that arrives while one runs or during that gap collapses into
 * exactly one trailing run. `pause()` drops every trigger — and a queued trailing run — until
 * `resume()`; a run already in flight finishes. `resume()` does not run by itself; the caller triggers
 * when it wants one. `pause()` is also the teardown: it clears the timer.
 *
 * `run` is expected to handle its own errors (the page pauses itself after a failed automatic fetch);
 * a rejection is swallowed here only so it cannot wedge the scheduler.
 */
export class LogReconcileScheduler {
  private running = false;
  private paused = false;
  private trailing = false;
  private lastEndMs = Number.NEGATIVE_INFINITY;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly now: () => number,
    private readonly run: () => Promise<void>,
  ) {}

  trigger(): void {
    if (this.paused) {
      return;
    }
    if (this.running) {
      this.trailing = true;
      return;
    }
    const waitMs = this.lastEndMs + MIN_GAP_MS - this.now();
    if (waitMs <= 0) {
      this.clearTimer();
      this.start();
      return;
    }
    this.trailing = true;
    this.arm(waitMs);
  }

  pause(): void {
    this.paused = true;
    this.trailing = false;
    this.clearTimer();
  }

  resume(): void {
    this.paused = false;
  }

  private start(): void {
    this.trailing = false;
    this.running = true;
    let result: Promise<void>;
    try {
      result = this.run();
    } catch {
      result = Promise.resolve();
    }
    void result.catch(() => undefined).then(() => this.finish());
  }

  private finish(): void {
    this.running = false;
    this.lastEndMs = this.now();
    if (this.trailing && !this.paused) {
      this.arm(MIN_GAP_MS);
    }
  }

  private arm(delayMs: number): void {
    if (this.timer !== null) {
      return;
    }
    this.timer = setTimeout(() => {
      this.timer = null;
      if (this.trailing && !this.paused && !this.running) {
        this.start();
      }
    }, delayMs);
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}

/** Sort comparator, newest first by `(timestampUtc, id)`. Compares at full wire precision (up to 100 ns):
 *  `Date.parse` stops at milliseconds, which would let the id decide between two entries of the same
 *  millisecond and disagree with the server's order. */
function newestFirst(a: LogEntry, b: LogEntry): number {
  const [aMs, aFrac] = instantKey(a.timestampUtc);
  const [bMs, bFrac] = instantKey(b.timestampUtc);
  return bMs - aMs || bFrac - aFrac || b.id - a.id;
}

/** Whole seconds as epoch milliseconds plus the fraction in 100 ns ticks. */
function instantKey(iso: string): [number, number] {
  const match = INSTANT.exec(iso);
  if (match === null) {
    return [Date.parse(iso), 0];
  }
  const [, seconds, fraction = '', zone] = match;
  return [Date.parse(seconds + zone), Number(fraction.padEnd(7, '0').slice(0, 7))];
}
