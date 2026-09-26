import { DestroyRef, WritableSignal, signal } from '@angular/core';
import { Subject } from 'rxjs';
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
  observed: TestRead[];
  destroy: () => void;
  refuseSave: () => void;
}

const TARGET: TestTarget = { setId: 'set-1' };

function createGate(initialPlan: TestPlan): Harness {
  const plan = signal<TestPlan | null>(initialPlan);
  const reloads = signal(0);
  const reads: Subject<TestRead>[] = [];
  const saved: RecoveryFileSave<TestTarget, TestPlan, TestRead>[] = [];
  const drifts: (readonly TestDrift[])[] = [];
  const observed: TestRead[] = [];
  const onDestroy: (() => void)[] = [];
  let saveRefused = false;
  const destroyRef = {
    onDestroy: (callback: () => void) => {
      onDestroy.push(callback);
      return () => undefined;
    },
  } as unknown as DestroyRef;

  const gate = new RecoveryFileGate<TestPlan, TestDrift, TestTarget, TestRead>({
    plan,
    noticeResetSource: reloads,
    destroyRef,
    read: () => {
      const read = new Subject<TestRead>();
      reads.push(read);
      return read;
    },
    verify: (read) => read.verification,
    stamp: (current) => ({ ...current, stamped: true }),
    save: (file) => {
      if (saveRefused) {
        throw new Error('download refused');
      }
      saved.push(file);
    },
    onDrift: (drifted) => drifts.push(drifted),
    onLiveRead: (read) => observed.push(read),
  });

  return {
    gate,
    plan,
    reloads,
    reads,
    saved,
    drifts,
    observed,
    destroy: () => onDestroy.forEach((callback) => callback()),
    refuseSave: () => {
      saveRefused = true;
    },
  };
}

function answer(read: Subject<TestRead> | undefined, value: Partial<TestRead> = {}): TestRead {
  const full: TestRead = { occupied: 7, verification: CLEAN, ...value };
  read?.next(full);
  read?.complete();
  return full;
}

describe('RecoveryFileGate', () => {
  afterEach(() => {
    vi.useRealTimers();
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

  it('settles a refused download on idle with saveFailed', () => {
    const plan = { label: 'a' };
    const harness = createGate(plan);
    harness.refuseSave();
    harness.gate.verifyAndSave(TARGET, plan);

    answer(harness.reads[0]);

    expect(harness.gate.state()).toEqual({ kind: 'idle' });
    expect(harness.gate.notice()).toEqual({ kind: 'saveFailed' });
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
});
