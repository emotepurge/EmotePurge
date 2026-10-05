import { describe, expect, it } from 'vitest';

import { RunResult } from './seven-tv-run-engine';
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
    expect(deriveTagKeptIds(tag, result([]))).toEqual(['X', 'Y', 'A', 'B', 'C']);
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
