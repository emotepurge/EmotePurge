import { describe, expect, it } from 'vitest';

import { RunItemStatus, RunQueueItem, RunResult } from './seven-tv-run-engine';
import { deriveTagKeptIds } from './tag-run-settlement';

function result(doneKeys: string[]): RunResult {
  return { doneKeys, items: [], startedAt: 0, finishedAt: 1 };
}

const tag = { checkedOwnIds: ['A', 'B', 'C'], uncheckedOwnIds: ['X', 'Y'] };

describe('deriveTagKeptIds', () => {
  it('keeps every own placement when no run happened', () => {
    expect(deriveTagKeptIds(tag, null)).toEqual(['X', 'Y', 'A', 'B', 'C']);
  });

  it('keeps the unticked ones and the ticked ones that were not removed', () => {
    expect(deriveTagKeptIds(tag, result(['A', 'C']))).toEqual(['X', 'Y', 'B']);
  });

  it('keeps only the unticked ones when every ticked one was removed', () => {
    expect(deriveTagKeptIds(tag, result(['A', 'B', 'C']))).toEqual(['X', 'Y']);
  });

  it('treats a ticked id with no done row as kept, whatever its other status', () => {
    const row = (key: string, status: RunItemStatus): RunQueueItem => ({
      key,
      sevenTvEmoteId: key,
      name: key,
      status,
      completedSteps: 0,
      failedStep: null,
    });
    const run: RunResult = {
      doneKeys: [],
      items: [row('A', 'failed'), row('B', 'cancelled'), row('C', 'unknown')],
      startedAt: 0,
      finishedAt: 1,
    };
    expect(deriveTagKeptIds(tag, run)).toEqual(['X', 'Y', 'A', 'B', 'C']);
    // 'D' has no run row at all; 'A' failed, 'B' was cancelled, 'C' is unknown.
    expect(
      deriveTagKeptIds(
        { checkedOwnIds: ['A', 'B', 'C', 'D', 'E'], uncheckedOwnIds: [] },
        {
          ...run,
          doneKeys: ['E'],
        },
      ),
    ).toEqual(['A', 'B', 'C', 'D']);
  });

  it('lists unticked first, then ticked in context order, without duplicates', () => {
    expect(
      deriveTagKeptIds({ checkedOwnIds: ['B', 'A', 'X'], uncheckedOwnIds: ['X', 'Y'] }, result([])),
    ).toEqual(['X', 'Y', 'B', 'A']);
  });

  it('does not keep a removed id that the run reports but the context never ticked', () => {
    expect(deriveTagKeptIds(tag, result(['Z', 'A', 'B', 'C']))).toEqual(['X', 'Y']);
  });
});
