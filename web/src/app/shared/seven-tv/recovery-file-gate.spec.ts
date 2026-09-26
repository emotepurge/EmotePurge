import { DestroyRef, WritableSignal, signal } from '@angular/core';
import { Observable, Subject, of } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  LIVE_READ_TIMEOUT_MS,
  RecoveryFileGate,
  RecoveryFileSave,
  RecoveryFileVerification,
} from './recovery-file-gate';

/** The smallest plan, drift, target and read the gate can be driven with — it only ever compares
 *  plans by identity and hands the rest through. */
interface TestPlan {
  label: string;
  stamped?: boolean;
}
interface TestDrift {
  key: string;
}
interface TestTarget {
  setId: string;
}
interface TestRead {
  occupied: number;
  complete: boolean;
  verification: RecoveryFileVerification<TestDrift>;
}

const CLEAN: RecoveryFileVerification<TestDrift> = { available: true, drifted: [] };

interface Harness {
  gate: RecoveryFileGate<TestPlan, TestDrift, TestTarget, TestRead>;
  plan: WritableSignal<TestPlan | null>;
  reloads: WritableSignal<number>;
  reads: Subject<TestRead>[];
  saved: RecoveryFileSave<TestTarget, TestPlan, TestRead>[];
  drifts: (readonly TestDrift[])[];
  /** The gate's notice as `onDrift` saw it, one entry per call. */
  noticeDuringDrift: unknown[];
  observed: TestRead[];
  destroy: () => void;
  refuseSave: () => void;
  /** Makes the named callback throw from now on. */
  breakCallback: (name: 'verify' | 'stamp' | 'onLiveRead' | 'onDrift' | 'isComplete') => void;
}

interface GateOptions {
  initialRead?: TestRead | null;
  /** Replaces the Subject-backed read, e.g. with a source that emits more than once. */
  read?: () => Observable<TestRead>;
  verifyRead?: () => Observable<TestRead>;
}

const TARGET: TestTarget = { setId: 'set-1' };

function createGate(initialPlan: TestPlan, options: GateOptions = {}): Harness {
  const plan = signal<TestPlan | null>(initialPlan);
  const reloads = signal(0);
  const reads: Subject<TestRead>[] = [];
  const saved: RecoveryFileSave<TestTarget, TestPlan, TestRead>[] = [];
  const drifts: (readonly TestDrift[])[] = [];
  const noticeDuringDrift: unknown[] = [];
  const observed: TestRead[] = [];
  const onDestroy: (() => void)[] = [];
  const broken = new Set<string>();
  const failIf = (name: string) => {
    if (broken.has(name)) {
      throw new Error(`${name} broke`);
    }
  };
  let saveRefused = false;
  const destroyRef = {
    onDestroy: (callback: () => void) => {
      onDestroy.push(callback);
      return () => undefined;
    },
  } as unknown as DestroyRef;

  const gate: RecoveryFileGate<TestPlan, TestDrift, TestTarget, TestRead> = new RecoveryFileGate<
    TestPlan,
    TestDrift,
    TestTarget,
    TestRead
  >({
    plan,
    noticeResetSource: reloads,
    destroyRef,
    initialRead: options.initialRead,
    verifyRead: options.verifyRead,
    read:
      options.read ??
      (() => {
        const read = new Subject<TestRead>();
        reads.push(read);
        return read;
      }),
    isComplete: (read) => {
      failIf('isComplete');
      return read.complete;
    },
    verify: (read) => {
      failIf('verify');
      return read.verification;
    },
    stamp: (current) => {
      failIf('stamp');
      return { ...current, stamped: true };
    },
    save: (file) => {
      if (saveRefused) {
        throw new Error('download refused');
      }
      saved.push(file);
    },
    onDrift: (drifted) => {
      noticeDuringDrift.push(gate.notice());
      failIf('onDrift');
      drifts.push(drifted);
    },
    onLiveRead: (read) => {
      failIf('onLiveRead');
      observed.push(read);
    },
  });

  return {
    gate,
    plan,
    reloads,
    reads,
    saved,
    drifts,
    noticeDuringDrift,
    observed,
    destroy: () => onDestroy.forEach((callback) => callback()),
    refuseSave: () => {
      saveRefused = true;
    },
    breakCallback: (name) => broken.add(name),
  };
}

function answer(read: Subject<TestRead> | undefined, value: Partial<TestRead> = {}): TestRead {
  const full: TestRead = { occupied: 7, complete: true, verification: CLEAN, ...value };
  read?.next(full);
  read?.complete();
  return full;
}

describe('RecoveryFileGate', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('starts idle, without a notice and without reading', () => {
    const { gate, reads } = createGate({ label: 'a' });

    expect(gate.state()).toEqual({ kind: 'idle' });
    expect(gate.isVerifying()).toBe(false);
    expect(gate.notice()).toBeNull();
    expect(reads).toHaveLength(0);
  });

  it('reads once per call and is verifying until the read answers', () => {
    const plan = { label: 'a' };
    const { gate, reads } = createGate(plan);

    gate.verifyAndSave(TARGET, plan);

    expect(reads).toHaveLength(1);
    expect(gate.state()).toEqual({ kind: 'verifying', plan });
    expect(gate.isVerifying()).toBe(true);
  });

  it('saves the stamped plan with the read and its verification time, then is saved', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-09-26T10:00:00Z'));
    const plan = { label: 'a' };
    const { gate, reads, saved } = createGate(plan);

    gate.verifyAndSave(TARGET, plan);
    const read = answer(reads[0]);

    expect(saved).toEqual([
      {
        target: TARGET,
        stampedPlan: { label: 'a', stamped: true },
        read,
        verifiedAt: Date.parse('2026-09-26T10:00:00Z'),
      },
    ]);
    expect(gate.state()).toEqual({
      kind: 'saved',
      plan,
      stampedPlan: { label: 'a', stamped: true },
    });
    expect(gate.notice()).toBeNull();
  });

  it('reads a saved state as idle once the plan changed, and offers no stamped plan any more', () => {
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.gate.verifyAndSave(TARGET, plan);
    answer(harness.reads[0]);

    harness.plan.set({ label: 'b' });

    expect(harness.gate.state()).toEqual({ kind: 'idle' });
  });

  it('drops the answer of a read whose plan changed while it ran: idle, no save, no observer, no notice', () => {
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.gate.verifyAndSave(TARGET, plan);

    harness.plan.set({ label: 'b' });
    expect(harness.gate.state()).toEqual({ kind: 'idle' });
    answer(harness.reads[0]);
    // Back on the old plan object: the raw state settled to idle, it is not still verifying.
    harness.plan.set(plan);

    expect(harness.gate.state()).toEqual({ kind: 'idle' });
    expect(harness.saved).toEqual([]);
    expect(harness.observed).toEqual([]);
    expect(harness.gate.notice()).toBeNull();
  });

  it('settles a failed read on idle with readFailed while its plan is still current', () => {
    const plan = { label: 'a' };
    const { gate, reads, saved } = createGate(plan);
    gate.verifyAndSave(TARGET, plan);

    reads[0]?.error(new Error('network'));

    expect(gate.state()).toEqual({ kind: 'idle' });
    expect(gate.notice()).toEqual({ kind: 'readFailed' });
    expect(saved).toEqual([]);
  });

  it('names nothing for a failed read whose plan is gone by then', () => {
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.gate.verifyAndSave(TARGET, plan);

    harness.plan.set({ label: 'b' });
    harness.reads[0]?.error(new Error('network'));
    harness.plan.set(plan);

    expect(harness.gate.state()).toEqual({ kind: 'idle' });
    expect(harness.gate.notice()).toBeNull();
  });

  it('treats a read that outlasts the timeout as failed', () => {
    vi.useFakeTimers();
    const plan = { label: 'a' };
    const { gate, reads } = createGate(plan);
    gate.verifyAndSave(TARGET, plan);

    vi.advanceTimersByTime(LIVE_READ_TIMEOUT_MS - 1);
    expect(gate.isVerifying()).toBe(true);
    vi.advanceTimersByTime(1);

    expect(gate.state()).toEqual({ kind: 'idle' });
    expect(gate.notice()).toEqual({ kind: 'readFailed' });
    expect(reads[0]?.observed).toBe(false);
  });

  it('releases nothing on an unavailable verification, but the observer still sees the read', () => {
    const plan = { label: 'a' };
    const { gate, reads, saved, observed } = createGate(plan);
    gate.verifyAndSave(TARGET, plan);

    const read = answer(reads[0], { verification: { available: false } });

    expect(gate.state()).toEqual({ kind: 'idle' });
    expect(gate.notice()).toEqual({ kind: 'readFailed' });
    expect(saved).toEqual([]);
    expect(observed).toEqual([read]);
  });

  it('hands a drift to the consumer, sets the drifted notice and saves nothing', () => {
    const plan = { label: 'a' };
    const { gate, reads, saved, drifts, observed } = createGate(plan);
    gate.verifyAndSave(TARGET, plan);

    const drifted = [{ key: 'x' }, { key: 'y' }];
    const read = answer(reads[0], { verification: { available: true, drifted } });

    expect(drifts).toEqual([drifted]);
    expect(gate.notice()).toEqual({ kind: 'drifted', drifted });
    expect(gate.state()).toEqual({ kind: 'idle' });
    expect(saved).toEqual([]);
    expect(observed).toEqual([read]);
  });

  it('settles a refused download on idle with saveFailed, and logs why', () => {
    const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.refuseSave();
    harness.gate.verifyAndSave(TARGET, plan);

    answer(harness.reads[0]);

    expect(harness.gate.state()).toEqual({ kind: 'idle' });
    expect(harness.gate.notice()).toEqual({ kind: 'saveFailed' });
    expect(logged).toHaveBeenCalledTimes(1);
  });

  for (const name of ['onLiveRead', 'verify', 'stamp', 'onDrift'] as const) {
    it(`fails closed when ${name} throws on an answer: idle with readFailed, nothing saved`, () => {
      const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined);
      const plan = { label: 'a' };
      const harness = createGate(plan);
      harness.breakCallback(name);
      harness.gate.verifyAndSave(TARGET, plan);

      answer(
        harness.reads[0],
        name === 'onDrift' ? { verification: { available: true, drifted: [{ key: 'x' }] } } : {},
      );

      expect(harness.gate.state()).toEqual({ kind: 'idle' });
      expect(harness.gate.notice()).toEqual({ kind: 'readFailed' });
      expect(harness.saved).toEqual([]);
      expect(logged).toHaveBeenCalledTimes(1);
    });
  }

  it('calls onDrift before the drifted notice is set', () => {
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.gate.verifyAndSave(TARGET, plan);

    answer(harness.reads[0], { verification: { available: true, drifted: [{ key: 'x' }] } });

    expect(harness.noticeDuringDrift).toEqual([null]);
    expect(harness.gate.notice()).toEqual({ kind: 'drifted', drifted: [{ key: 'x' }] });
  });

  it('handles at most one answer per read, even from a source that emits twice', () => {
    const plan = { label: 'a' };
    const first: TestRead = { occupied: 1, complete: true, verification: CLEAN };
    const second: TestRead = { occupied: 2, complete: true, verification: CLEAN };
    const harness = createGate(plan, { read: () => of(first, second) });

    harness.gate.verifyAndSave(TARGET, plan);

    expect(harness.observed).toEqual([first]);
    expect(harness.saved.map((file) => file.read)).toEqual([first]);
  });

  it('lets only the newest read answer: a second call cancels the first, whose late answer changes nothing', () => {
    const plan = { label: 'a' };
    const { gate, reads, saved, observed } = createGate(plan);
    gate.verifyAndSave(TARGET, plan);
    gate.verifyAndSave(TARGET, plan);

    expect(reads).toHaveLength(2);
    expect(reads[0]?.observed).toBe(false);
    answer(reads[0]);
    expect(saved).toEqual([]);
    expect(observed).toEqual([]);
    expect(gate.isVerifying()).toBe(true);

    answer(reads[1]);
    expect(saved).toHaveLength(1);
    expect(gate.state().kind).toBe('saved');
  });

  it('lets a failing older read change nothing once a newer read runs', () => {
    const plan = { label: 'a' };
    const { gate, reads } = createGate(plan);
    gate.verifyAndSave(TARGET, plan);
    gate.verifyAndSave(TARGET, plan);

    reads[0]?.error(new Error('network'));

    expect(gate.notice()).toBeNull();
    expect(gate.isVerifying()).toBe(true);
  });

  it('clears a notice when a new read starts and whenever the reset source changes', () => {
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.gate.verifyAndSave(TARGET, plan);
    harness.reads[0]?.error(new Error('network'));
    expect(harness.gate.notice()).toEqual({ kind: 'readFailed' });

    harness.gate.verifyAndSave(TARGET, plan);
    expect(harness.gate.notice()).toBeNull();
    harness.reads[1]?.error(new Error('network'));
    expect(harness.gate.notice()).toEqual({ kind: 'readFailed' });

    harness.reloads.set(1);
    expect(harness.gate.notice()).toBeNull();
  });

  it('keeps the notice across a plan change that is not a reload', () => {
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.gate.verifyAndSave(TARGET, plan);
    answer(harness.reads[0], { verification: { available: true, drifted: [{ key: 'x' }] } });

    // The consumer's own drift handling changes the plan; the banner must survive that.
    harness.plan.set({ label: 'b' });

    expect(harness.gate.notice()).toEqual({ kind: 'drifted', drifted: [{ key: 'x' }] });
  });

  it('cancels the read in flight on destroy and runs no callback afterwards', () => {
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.gate.verifyAndSave(TARGET, plan);

    harness.destroy();
    expect(harness.reads[0]?.observed).toBe(false);
    answer(harness.reads[0]);
    harness.gate.verifyAndSave(TARGET, plan);

    expect(harness.reads).toHaveLength(1);
    expect(harness.saved).toEqual([]);
    expect(harness.observed).toEqual([]);
    expect(harness.gate.notice()).toBeNull();
  });

  it('shows each live answer of the current plan to the observer exactly once', () => {
    const plan = { label: 'a' };
    const { gate, reads, observed } = createGate(plan);

    gate.verifyAndSave(TARGET, plan);
    const first = answer(reads[0], { occupied: 3, verification: { available: false } });
    gate.verifyAndSave(TARGET, plan);
    const second = answer(reads[1], { occupied: 4 });

    expect(observed).toEqual([first, second]);
  });

  describe('reload', () => {
    it('starts from the initial read, empties lastRead while it runs, and publishes a complete answer', () => {
      const initial: TestRead = { occupied: 1, complete: true, verification: CLEAN };
      const plan = { label: 'a' };
      const harness = createGate(plan, { initialRead: initial });
      expect(harness.gate.lastRead()).toBe(initial);

      harness.gate.reload(TARGET);
      expect(harness.gate.isReloading()).toBe(true);
      expect(harness.gate.lastRead()).toBeNull();
      const read = answer(harness.reads[0], { occupied: 9 });

      expect(harness.gate.lastRead()).toBe(read);
      expect(harness.gate.isReloading()).toBe(false);
      expect(harness.observed).toEqual([read]);
      expect(harness.gate.notice()).toBeNull();
    });

    it('leaves verifyAndSave on verifyRead when one is given — the held read, no second request', () => {
      const held: TestRead = { occupied: 3, complete: true, verification: CLEAN };
      const plan = { label: 'a' };
      const harness = createGate(plan, { initialRead: held, verifyRead: () => of(held) });

      harness.gate.verifyAndSave(TARGET, plan);

      expect(harness.reads).toHaveLength(0);
      expect(harness.saved.map((file) => file.read)).toEqual([held]);
      expect(harness.gate.lastRead()).toBe(held);
      harness.gate.reload(TARGET);
      expect(harness.reads).toHaveLength(1);
    });

    it('never saves and never enters verifying or saved — a reload after saved reads idle', () => {
      const plan = { label: 'a' };
      const harness = createGate(plan);
      harness.gate.verifyAndSave(TARGET, plan);
      answer(harness.reads[0]);
      expect(harness.gate.state().kind).toBe('saved');

      harness.gate.reload(TARGET);
      expect(harness.gate.state()).toEqual({ kind: 'idle' });
      answer(harness.reads[1]);

      expect(harness.gate.state()).toEqual({ kind: 'idle' });
      expect(harness.saved).toHaveLength(1);
    });

    it('lets only the newest read answer: a newer reload cancels the older one', () => {
      const plan = { label: 'a' };
      const harness = createGate(plan);
      harness.gate.reload(TARGET);
      harness.gate.reload(TARGET);

      expect(harness.reads[0]?.observed).toBe(false);
      answer(harness.reads[0], { occupied: 1 });
      expect(harness.gate.lastRead()).toBeNull();
      expect(harness.gate.isReloading()).toBe(true);

      const newest = answer(harness.reads[1], { occupied: 2 });
      expect(harness.gate.lastRead()).toBe(newest);
      expect(harness.observed).toEqual([newest]);
    });

    it('is cancelled by a verifyAndSave started after it, and cancels one started before it', () => {
      const plan = { label: 'a' };
      const harness = createGate(plan);
      harness.gate.reload(TARGET);
      harness.gate.verifyAndSave(TARGET, plan);
      expect(harness.reads[0]?.observed).toBe(false);
      expect(harness.gate.isReloading()).toBe(false);

      harness.gate.reload(TARGET);
      expect(harness.reads[1]?.observed).toBe(false);
      expect(harness.gate.isVerifying()).toBe(false);
      answer(harness.reads[1]);
      expect(harness.saved).toEqual([]);
    });

    it('fails an incomplete read with readFailed and leaves lastRead empty', () => {
      const initial: TestRead = { occupied: 1, complete: true, verification: CLEAN };
      const harness = createGate({ label: 'a' }, { initialRead: initial });
      harness.gate.reload(TARGET);

      answer(harness.reads[0], { complete: false });

      expect(harness.gate.notice()).toEqual({ kind: 'readFailed' });
      expect(harness.gate.lastRead()).toBeNull();
      expect(harness.gate.isReloading()).toBe(false);
      expect(harness.observed).toEqual([]);
    });

    it('fails an erroring read with readFailed', () => {
      const harness = createGate({ label: 'a' });
      harness.gate.reload(TARGET);

      harness.reads[0]?.error(new Error('network'));

      expect(harness.gate.notice()).toEqual({ kind: 'readFailed' });
      expect(harness.gate.lastRead()).toBeNull();
      expect(harness.gate.isReloading()).toBe(false);
    });

    it('fails a read that outlasts the timeout', () => {
      vi.useFakeTimers();
      const harness = createGate({ label: 'a' });
      harness.gate.reload(TARGET);

      vi.advanceTimersByTime(LIVE_READ_TIMEOUT_MS);

      expect(harness.gate.notice()).toEqual({ kind: 'readFailed' });
      expect(harness.gate.isReloading()).toBe(false);
      expect(harness.reads[0]?.observed).toBe(false);
    });

    it('fails closed when onLiveRead throws on a reload', () => {
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      const harness = createGate({ label: 'a' });
      harness.breakCallback('onLiveRead');
      harness.gate.reload(TARGET);

      answer(harness.reads[0]);

      expect(harness.gate.notice()).toEqual({ kind: 'readFailed' });
      expect(harness.gate.lastRead()).toBeNull();
    });

    it('lands a synchronously answering read on the new state', () => {
      const read: TestRead = { occupied: 5, complete: true, verification: CLEAN };
      const harness = createGate({ label: 'a' }, { read: () => of(read) });

      harness.gate.reload(TARGET);

      expect(harness.gate.lastRead()).toBe(read);
      expect(harness.gate.isReloading()).toBe(false);
    });

    it('cancels on destroy and does nothing afterwards', () => {
      const harness = createGate({ label: 'a' });
      harness.gate.reload(TARGET);

      harness.destroy();
      expect(harness.reads[0]?.observed).toBe(false);
      answer(harness.reads[0]);
      harness.gate.reload(TARGET);

      expect(harness.reads).toHaveLength(1);
      expect(harness.gate.lastRead()).toBeNull();
      expect(harness.observed).toEqual([]);
    });
  });
});
