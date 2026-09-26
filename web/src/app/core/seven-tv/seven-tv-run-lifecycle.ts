import { Signal, WritableSignal, computed, signal } from '@angular/core';

/**
 * Where one 7TV run stands, from its first mutation to its last report (#256, contract P6 of the
 * #254 spec, 11.1):
 * - `running` — the engine works through the run's queue.
 * - `settling` — the engine is done, but rows it could not confirm are being re-read (import only;
 *   a run without a re-read skips this phase).
 * - `reporting` — the outcome is final and at least one report to our Api has no end state yet.
 * - `closed` — every report the run opened has an end state (`succeeded | partial | failed`), or
 *   there was none to send. A resync is not a report and never holds a run open.
 */
export type RunPhase = 'running' | 'settling' | 'reporting' | 'closed';

/** What every run record carries, whatever the run does. `runId` is the identity: records are
 *  replaced by a new object on every change (so signals see the change), and every late answer
 *  finds its record by id, never by object reference. `destructive` is fixed at the start — a run
 *  with at least one row that removes something from a set. */
export interface RunRecordBase {
  readonly runId: string;
  readonly phase: RunPhase;
  readonly destructive: boolean;
}

/**
 * The run-bound lifecycle of one run service (#256): holds every run of the service that is not
 * closed yet, by `runId`, next to the one run the dock shows. A run completes on its own record —
 * its re-read, its reports, their answers — whether or not it is still shown; `detach()` (the
 * service's `reset()`) and a newer run only change what is shown.
 *
 * Operations, in the order a run meets them: `createRunId`/`open` (start, shown at once), `update`
 * (every change: result, phase, a report going `pending`, a report's answer), `detach` (display
 * only), `reshow` (a detached run whose report did not succeed, see the service). A record's report
 * states are fields of the record itself; the service tells this class which of them count through
 * `reportsPending`, and `update` closes a `reporting` run the moment none is pending any more.
 * `closed` is final: a retried report on a closed run never reopens it, so neither `isSettling`
 * nor `destructiveOpen` comes back.
 *
 * A closed record is dropped once no report of it is in flight and nothing shows it; until then it
 * stays reachable by id, so a late answer always lands on it. Plain class, not injectable: each
 * service owns one instance, like its `SevenTvRunEngine`.
 */
export class SevenTvRunLifecycle<TRun extends RunRecordBase> {
  /** The run the dock shows — the service exposes this as its own writable `run` signal. */
  readonly shown: WritableSignal<TRun | null> = signal<TRun | null>(null);

  private readonly records = signal<ReadonlyMap<string, TRun>>(new Map());

  /** True while any run of this service re-reads or reports — shown or not. */
  readonly isSettling: Signal<boolean> = computed(() =>
    [...this.records().values()].some(
      (run) => run.phase === 'settling' || run.phase === 'reporting',
    ),
  );

  /** True while any destructive run of this service is not closed — shown or not. */
  readonly destructiveOpen: Signal<boolean> = computed(() =>
    [...this.records().values()].some((run) => run.destructive && run.phase !== 'closed'),
  );

  private runCount = 0;

  /** `label` prefixes the ids this instance hands out; `reportsPending` says whether a record still
   *  waits for the answer of at least one report. */
  constructor(
    private readonly label: string,
    private readonly reportsPending: (run: TRun) => boolean,
  ) {}

  /** A fresh id for the next run — unique per instance, which is per service. */
  createRunId(): string {
    this.runCount += 1;
    return `${this.label}-${this.runCount}`;
  }

  /** Registers a started run and shows it. Whatever was shown before stays open on its own record. */
  open(run: TRun): void {
    this.records.update((records) => new Map(records).set(run.runId, run));
    this.shown.set(run);
  }

  /**
   * Undoes `open(run)` for a run whose engine never actually started (#256 review finding "open
   * before start"): a service calls `open` *before* asking its engine to start, precisely so a
   * synchronous `onComplete` cannot land on a run the lifecycle has not registered yet — but that
   * means a refused start (already running, empty queue, no token) has already been opened and
   * shown and must be taken back. Drops `runId`'s record and restores `previousShown` — whatever
   * was shown before `open`, which may be another run still settling its own report, never assumed
   * to be `null`.
   */
  discardUnstarted(runId: string, previousShown: TRun | null): void {
    this.records.update((records) => {
      const updated = new Map(records);
      updated.delete(runId);
      return updated;
    });
    if (this.shown()?.runId === runId) {
      this.shown.set(previousShown);
    }
  }

  /** The open record of `runId` — also the shown run's own record even after it closed and was
   *  dropped from the map (mirrors the lookup `update()` uses for its own `current`): a closed run
   *  is removed from `records` the moment nothing is pending any more, whether or not it is still
   *  shown, so a manual retry on the shown, already-`closed` run must still find it here. `null`
   *  only once neither the map nor `shown` holds it. */
  get(runId: string): TRun | null {
    const shownRun = this.shown();
    return this.records().get(runId) ?? (shownRun?.runId === runId ? shownRun : null);
  }

  isShown(runId: string): boolean {
    return this.shown()?.runId === runId;
  }

  /**
   * Replaces the record of `runId` with `change(record)` and returns the new record — also when the
   * change closed and dropped it, so the caller can still act on its final state. Mirrors the new
   * record into `shown` exactly when `runId` is the shown run. A `reporting` record without a
   * pending report comes back `closed`. `null` when no record of that id is held (closed, dropped,
   * and not shown).
   *
   * `closed` is a one-way door (#256 review finding): once `current.phase` is `closed`, `change`
   * may still patch any other field — a manual retry's report field, for one — but a phase it
   * returns is overridden back to `closed`. Without this a caller that carelessly copies `phase`
   * off a stale closure (or a future bug in a service) could reopen a run the arbiter and every
   * dock have already stopped watching.
   */
  update(runId: string, change: (run: TRun) => TRun): TRun | null {
    const shownRun = this.shown();
    const current = this.records().get(runId) ?? (shownRun?.runId === runId ? shownRun : null);
    if (current === null) {
      return null;
    }

    let next = change(current);
    if (current.phase === 'closed' && next.phase !== 'closed') {
      next = { ...next, phase: 'closed' };
    }
    if (next.phase === 'reporting' && !this.reportsPending(next)) {
      next = { ...next, phase: 'closed' };
    }

    const keep = next.phase !== 'closed' || this.reportsPending(next);
    this.records.update((records) => {
      const updated = new Map(records);
      if (keep) {
        updated.set(runId, next);
      } else {
        updated.delete(runId);
      }
      return updated;
    });
    if (shownRun?.runId === runId) {
      this.shown.set(next);
    }
    return next;
  }

  /** Stops showing the current run. The run itself goes on: it re-reads, reports and closes on its
   *  own record. */
  detach(): void {
    this.shown.set(null);
  }

  /** Shows `run` again when nothing is shown — the way back for a detached run whose report did not
   *  succeed, so its failure has a place with a retry. `false` when another run is shown. */
  reshow(run: TRun): boolean {
    const shownRun = this.shown();
    if (shownRun?.runId === run.runId) {
      return true;
    }
    if (shownRun !== null) {
      return false;
    }
    this.shown.set(run);
    return true;
  }
}
