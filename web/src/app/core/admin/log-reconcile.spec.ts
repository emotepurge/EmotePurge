import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { LogEntry, LogKeyPage } from './admin-logs.model';
import {
  LogReconcileScheduler,
  mergeLogEntries,
  planReconcile,
  preflightReconcile,
  reconcileFromUtc,
} from './log-reconcile';

function entry(id: number, timestampUtc: string): LogEntry {
  return {
    id,
    timestampUtc,
    level: 'info',
    source: 'worker',
    instanceId: 'a1b2c3d4',
    category: 'EmotePurge.Worker.Worker',
    eventId: null,
    eventName: null,
    message: `entry ${id}`,
    hasException: false,
    exceptionType: null,
    properties: {},
    truncated: false,
  };
}

function keyPage(ids: number[], truncated = false): LogKeyPage {
  return {
    entries: ids.map((id) => ({ id, timestampUtc: '2026-10-14T12:00:00Z' })),
    nextCursor: null,
    rangeFromUtc: '2026-10-14T11:00:00Z',
    rangeToUtc: '2026-10-14T12:00:05Z',
    truncated,
  };
}

const ids = (entries: readonly LogEntry[]) => entries.map((e) => e.id);
const range = (from: number, count: number) => Array.from({ length: count }, (_, i) => from + i);

describe('mergeLogEntries', () => {
  it('inserts late entries at their sorted position in the middle of loaded pages', () => {
    const loaded = [
      entry(50, '2026-10-14T12:00:05Z'),
      entry(40, '2026-10-14T12:00:03Z'),
      entry(30, '2026-10-14T12:00:01Z'),
    ];
    const incoming = [entry(60, '2026-10-14T12:00:02Z'), entry(70, '2026-10-14T12:00:06Z')];

    expect(ids(mergeLogEntries(loaded, incoming))).toEqual([70, 50, 40, 60, 30]);
  });

  it('dedupes by id and keeps the loaded object', () => {
    const loaded = [entry(2, '2026-10-14T12:00:02Z'), entry(1, '2026-10-14T12:00:01Z')];
    const again = { ...entry(2, '2026-10-14T12:00:02Z'), message: 'refetched' };

    const merged = mergeLogEntries(loaded, [again, entry(3, '2026-10-14T12:00:03Z')]);

    expect(ids(merged)).toEqual([3, 2, 1]);
    expect(merged[1]).toBe(loaded[0]);
  });

  it('breaks timestamp ties by id descending', () => {
    const at = '2026-10-14T12:00:00.25Z';
    expect(ids(mergeLogEntries([entry(5, at)], [entry(9, at), entry(7, at)]))).toEqual([9, 7, 5]);
  });

  it('orders by the instant, not the string: trailing zeros are dropped on the wire', () => {
    const merged = mergeLogEntries(
      [entry(1, '2026-10-14T12:00:00Z')],
      [entry(2, '2026-10-14T12:00:00.5Z'), entry(3, '2026-10-14T11:59:59.9Z')],
    );
    expect(ids(merged)).toEqual([2, 1, 3]);
  });

  it('keeps sub-millisecond order, which Date.parse would flatten into an id tie-break', () => {
    // Same millisecond: id 1 is 0.5 µs newer than id 2, so it comes first despite the lower id.
    const merged = mergeLogEntries(
      [entry(2, '2026-10-14T12:00:00.000001Z')],
      [entry(1, '2026-10-14T12:00:00.0000015Z')],
    );
    expect(ids(merged)).toEqual([1, 2]);
  });

  it('does not mutate its inputs', () => {
    const loaded = [entry(1, '2026-10-14T12:00:01Z')];
    const incoming = [entry(2, '2026-10-14T12:00:02Z')];

    mergeLogEntries(loaded, incoming);

    expect(ids(loaded)).toEqual([1]);
    expect(ids(incoming)).toEqual([2]);
  });
});

describe('reconcileFromUtc', () => {
  const rangeFrom = '2026-10-13T12:00:00.1234567Z';
  const loaded = [entry(9, '2026-10-14T12:00:00Z'), entry(8, '2026-10-14T10:00:00.000042Z')];

  it('is the range start when nothing is loaded (R7)', () => {
    expect(reconcileFromUtc([], rangeFrom, 'cursor')).toBe(rangeFrom);
  });

  it('is the range start once pagination has reached its end', () => {
    expect(reconcileFromUtc(loaded, rangeFrom, null)).toBe(rangeFrom);
  });

  it('is the oldest loaded timestamp, byte for byte, while more pages exist', () => {
    expect(reconcileFromUtc(loaded, rangeFrom, 'cursor')).toBe('2026-10-14T10:00:00.000042Z');
  });
});

describe('preflightReconcile', () => {
  const fromL = '2026-10-06T12:00:00.25Z';
  const fromMs = Date.parse(fromL);
  const eightDays = 8 * 24 * 60 * 60 * 1000;
  const live = { range: '7d' } as const;

  it('requests while the span from from_L to now stays within 8 days', () => {
    expect(preflightReconcile(fromL, fromMs + eightDays - 1000, live)).toEqual({ kind: 'request' });
    expect(preflightReconcile(fromL, fromMs + eightDays, live)).toEqual({ kind: 'request' });
  });

  it('asks for a reload without a request once the span passes 8 days (Review Focus 3, L3)', () => {
    expect(preflightReconcile(fromL, fromMs + eightDays + 1000, live)).toEqual({ kind: 'reload' });
  });

  it('skips an absolute range, which has no live mode', () => {
    expect(
      preflightReconcile(fromL, fromMs + 1000, { from: fromL, to: '2026-10-06T13:00:00Z' }),
    ).toEqual({ kind: 'skip' });
  });
});

describe('planReconcile', () => {
  it('does nothing when every key is loaded', () => {
    expect(planReconcile(keyPage([3, 2, 1]), new Set([1, 2, 3]))).toEqual({ kind: 'none' });
  });

  it('ignores loaded ids the server no longer has (retention)', () => {
    expect(planReconcile(keyPage([3]), new Set([1, 2, 3]))).toEqual({ kind: 'none' });
  });

  it('fetches only the missing ids, newest first, in batches of at most 200', () => {
    const plan = planReconcile(keyPage([...range(1, 450), 9001]), new Set([9001]));

    expect(plan.kind).toBe('fetch');
    if (plan.kind !== 'fetch') return;
    expect(plan.idBatches.map((b) => b.length)).toEqual([200, 200, 50]);
    expect(plan.idBatches.flat()).toEqual(range(1, 450));
  });

  it('still fetches exactly 500 missing ids in three requests', () => {
    const plan = planReconcile(keyPage(range(1, 500)), new Set());

    expect(plan).toEqual({
      kind: 'fetch',
      idBatches: [range(1, 200), range(201, 200), range(401, 100)],
    });
  });

  it('asks for a reload when more than 500 are missing', () => {
    expect(planReconcile(keyPage(range(1, 501)), new Set())).toEqual({ kind: 'reload' });
  });

  it('asks for a reload when the key list is truncated, even with nothing missing', () => {
    expect(planReconcile(keyPage([1], true), new Set([1]))).toEqual({ kind: 'reload' });
  });
});

describe('LogReconcileScheduler', () => {
  let runs: number;
  let active: number;
  let maxActive: number;
  let pending: (() => void)[];
  let scheduler: LogReconcileScheduler;

  beforeEach(() => {
    vi.useFakeTimers({ now: 0 });
    runs = 0;
    active = 0;
    maxActive = 0;
    pending = [];
    scheduler = new LogReconcileScheduler(
      () => Date.now(),
      () =>
        new Promise<void>((resolve) => {
          runs += 1;
          active += 1;
          maxActive = Math.max(maxActive, active);
          pending.push(() => {
            active -= 1;
            resolve();
          });
        }),
    );
  });

  afterEach(() => {
    scheduler.pause();
    vi.useRealTimers();
  });

  async function finishRun(): Promise<void> {
    pending.shift()?.();
    await vi.advanceTimersByTimeAsync(0);
  }

  it('runs the first trigger immediately', () => {
    scheduler.trigger();
    expect(runs).toBe(1);
  });

  it('never runs two at once and follows a busy period with exactly one trailing run', async () => {
    scheduler.trigger();
    scheduler.trigger();
    scheduler.trigger();
    expect(runs).toBe(1);

    await vi.advanceTimersByTimeAsync(1000);
    await finishRun();
    expect(runs).toBe(1);

    await vi.advanceTimersByTimeAsync(4999);
    expect(runs).toBe(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(runs).toBe(2);

    await finishRun();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(runs).toBe(2);
    expect(maxActive).toBe(1);
  });

  it('keeps 5,000 ms between two runs and collapses triggers in the gap into one', async () => {
    scheduler.trigger();
    await finishRun();

    for (let i = 0; i < 4; i++) {
      await vi.advanceTimersByTimeAsync(1000);
      scheduler.trigger();
    }
    expect(runs).toBe(1);

    await vi.advanceTimersByTimeAsync(1000);
    expect(runs).toBe(2);
    await finishRun();
    await vi.advanceTimersByTimeAsync(30_000);
    expect(runs).toBe(2);
  });

  it('runs a trigger after the gap at once', async () => {
    scheduler.trigger();
    await finishRun();
    await vi.advanceTimersByTimeAsync(5000);

    scheduler.trigger();

    expect(runs).toBe(2);
  });

  it('suppresses every trigger while paused, including a trailing run already queued', async () => {
    scheduler.trigger();
    scheduler.trigger();
    scheduler.pause();
    await finishRun();
    await vi.advanceTimersByTimeAsync(10_000);
    scheduler.trigger();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(runs).toBe(1);

    scheduler.resume();
    expect(runs).toBe(1);
    scheduler.trigger();
    expect(runs).toBe(2);
  });

  it('recovers from a run that rejects', async () => {
    const failing = new LogReconcileScheduler(
      () => Date.now(),
      () => {
        runs += 1;
        return Promise.reject(new Error('boom'));
      },
    );

    failing.trigger();
    await vi.advanceTimersByTimeAsync(5000);
    failing.trigger();

    expect(runs).toBe(2);
    failing.pause();
  });
});
