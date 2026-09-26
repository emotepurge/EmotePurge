import { describe, expect, it } from 'vitest';

import { RunRecordBase, SevenTvRunLifecycle } from './seven-tv-run-lifecycle';

/** The smallest record the building block can hold: the base plus one report state, which is all
 *  `reportsPending` needs to decide when a `reporting` run closes. */
interface TestRun extends RunRecordBase {
  report: 'idle' | 'pending' | 'succeeded' | 'failed';
  label: string;
}

function createLifecycle(): SevenTvRunLifecycle<TestRun> {
  return new SevenTvRunLifecycle<TestRun>('test', (run) => run.report === 'pending');
}

function openRun(
  lifecycle: SevenTvRunLifecycle<TestRun>,
  overrides: Partial<TestRun> = {},
): TestRun {
  const run: TestRun = {
    runId: lifecycle.createRunId(),
    phase: 'running',
    destructive: false,
    report: 'idle',
    label: 'a',
    ...overrides,
  };
  lifecycle.open(run);
  return run;
}

describe('SevenTvRunLifecycle', () => {
  it('opens a run as the shown one and hands out a fresh id per run', () => {
    const lifecycle = createLifecycle();

    const first = openRun(lifecycle);
    const second = openRun(lifecycle);

    expect(first.runId).not.toBe(second.runId);
    expect(lifecycle.shown()).toBe(second);
    expect(lifecycle.get(first.runId)).toBe(first);
    expect(lifecycle.isShown(first.runId)).toBe(false);
    expect(lifecycle.isShown(second.runId)).toBe(true);
  });

  it('patches a record by id and mirrors the new object into the display only while shown', () => {
    const lifecycle = createLifecycle();
    const first = openRun(lifecycle);

    const patched = lifecycle.update(first.runId, (run) => ({ ...run, label: 'b' }));

    expect(patched).not.toBe(first);
    expect(patched?.label).toBe('b');
    expect(lifecycle.shown()).toBe(patched);

    const second = openRun(lifecycle);
    lifecycle.update(first.runId, (run) => ({ ...run, label: 'c' }));

    expect(lifecycle.get(first.runId)?.label).toBe('c');
    expect(lifecycle.shown()).toBe(second);
  });

  it('keeps a record identified by its id after every replacement of the object', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle);

    lifecycle.update(run.runId, (current) => ({ ...current, label: 'b' }));
    lifecycle.update(run.runId, (current) => ({ ...current, label: 'c' }));

    expect(lifecycle.get(run.runId)?.label).toBe('c');
    expect(lifecycle.isShown(run.runId)).toBe(true);
  });

  it('returns null for an id it does not hold, and changes nothing', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle);

    expect(lifecycle.update('test-99', (current) => ({ ...current, label: 'x' }))).toBeNull();
    expect(lifecycle.shown()).toBe(run);
  });

  it('is settling while a run settles or reports, and not while it runs or once it is closed', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle);
    expect(lifecycle.isSettling()).toBe(false);

    lifecycle.update(run.runId, (current) => ({ ...current, phase: 'settling' }));
    expect(lifecycle.isSettling()).toBe(true);

    lifecycle.update(run.runId, (current) => ({
      ...current,
      phase: 'reporting',
      report: 'pending',
    }));
    expect(lifecycle.isSettling()).toBe(true);

    lifecycle.update(run.runId, (current) => ({ ...current, report: 'succeeded' }));
    expect(lifecycle.get(run.runId)).toBeNull();
    expect(lifecycle.shown()?.phase).toBe('closed');
    expect(lifecycle.isSettling()).toBe(false);
  });

  it('closes a reporting run only once no report is pending any more', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle);

    const reporting = lifecycle.update(run.runId, (current) => ({
      ...current,
      phase: 'reporting',
      report: 'pending',
    }));
    expect(reporting?.phase).toBe('reporting');

    const failed = lifecycle.update(run.runId, (current) => ({ ...current, report: 'failed' }));
    // A report that ends failed is an end state too: the run closes all the same.
    expect(failed?.phase).toBe('closed');
  });

  it('closes a run that reaches reporting with nothing to report without ever stopping there', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle);

    const settled = lifecycle.update(run.runId, (current) => ({ ...current, phase: 'reporting' }));

    expect(settled?.phase).toBe('closed');
    expect(lifecycle.isSettling()).toBe(false);
  });

  it('never reopens a closed run for a retried report', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle, { destructive: true });
    lifecycle.update(run.runId, (current) => ({ ...current, phase: 'reporting' }));
    expect(lifecycle.shown()?.phase).toBe('closed');

    const retried = lifecycle.update(run.runId, (current) => ({ ...current, report: 'pending' }));

    expect(retried?.phase).toBe('closed');
    expect(lifecycle.isSettling()).toBe(false);
    expect(lifecycle.destructiveOpen()).toBe(false);

    const answered = lifecycle.update(run.runId, (current) => ({ ...current, report: 'failed' }));
    expect(answered?.phase).toBe('closed');
  });

  it('holds destructiveOpen from start to close for a destructive run, never for another one', () => {
    const lifecycle = createLifecycle();
    const plain = openRun(lifecycle);
    expect(lifecycle.destructiveOpen()).toBe(false);

    const destructive = openRun(lifecycle, { destructive: true });
    expect(lifecycle.destructiveOpen()).toBe(true);

    lifecycle.update(destructive.runId, (current) => ({
      ...current,
      phase: 'reporting',
      report: 'pending',
    }));
    expect(lifecycle.destructiveOpen()).toBe(true);

    lifecycle.update(destructive.runId, (current) => ({ ...current, report: 'succeeded' }));
    expect(lifecycle.destructiveOpen()).toBe(false);
    expect(lifecycle.get(plain.runId)).not.toBeNull();
  });

  it('derives isSettling and destructiveOpen over every open run, not over the shown one', () => {
    const lifecycle = createLifecycle();
    const first = openRun(lifecycle, { destructive: true });
    lifecycle.update(first.runId, (current) => ({
      ...current,
      phase: 'reporting',
      report: 'pending',
    }));

    openRun(lifecycle);
    expect(lifecycle.isShown(first.runId)).toBe(false);
    expect(lifecycle.isSettling()).toBe(true);
    expect(lifecycle.destructiveOpen()).toBe(true);

    lifecycle.update(first.runId, (current) => ({ ...current, report: 'succeeded' }));
    expect(lifecycle.isSettling()).toBe(false);
    expect(lifecycle.destructiveOpen()).toBe(false);
  });

  it('detaches the display without closing the run behind it', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle, { destructive: true });

    lifecycle.detach();

    expect(lifecycle.shown()).toBeNull();
    expect(lifecycle.get(run.runId)).toBe(run);
    expect(lifecycle.destructiveOpen()).toBe(true);

    const reporting = lifecycle.update(run.runId, (current) => ({
      ...current,
      phase: 'reporting',
      report: 'pending',
    }));
    expect(lifecycle.shown()).toBeNull();
    expect(lifecycle.isSettling()).toBe(true);
    expect(reporting?.phase).toBe('reporting');
  });

  it('drops a closed run nobody shows once its reports are answered, but keeps its last record', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle);
    lifecycle.update(run.runId, (current) => ({
      ...current,
      phase: 'reporting',
      report: 'pending',
    }));
    lifecycle.detach();

    const closed = lifecycle.update(run.runId, (current) => ({ ...current, report: 'failed' }));

    // The caller still gets the final record, to decide whether it has to be shown again.
    expect(closed).toMatchObject({ phase: 'closed', report: 'failed' });
    expect(lifecycle.get(run.runId)).toBeNull();
  });

  it('shows a detached run again when nothing else is shown', () => {
    const lifecycle = createLifecycle();
    const run = openRun(lifecycle);
    lifecycle.detach();
    const closed = lifecycle.update(run.runId, (current) => ({
      ...current,
      phase: 'reporting',
      report: 'failed',
    }));
    if (closed === null) {
      throw new Error('record expected');
    }

    expect(lifecycle.reshow(closed)).toBe(true);
    expect(lifecycle.shown()).toBe(closed);
    // Reachable through the display from now on, so a retry of the shown run still finds it.
    expect(lifecycle.update(run.runId, (current) => ({ ...current, label: 'z' }))?.label).toBe('z');
  });

  it('does not show a detached run again over another run that is shown', () => {
    const lifecycle = createLifecycle();
    const first = openRun(lifecycle);
    const second = openRun(lifecycle);
    const closed = lifecycle.update(first.runId, (current) => ({
      ...current,
      phase: 'reporting',
      report: 'failed',
    }));
    if (closed === null) {
      throw new Error('record expected');
    }

    expect(lifecycle.reshow(closed)).toBe(false);
    expect(lifecycle.shown()).toBe(second);
  });
});
