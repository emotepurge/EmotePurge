import { describe, expect, it } from 'vitest';

import { BackfillCoverageInterval } from './backfill.model';
import { backfillSetLabel, lastDayOf, replacedIntervals } from './backfill-window';

function interval(
  from: string,
  to: string,
  emoteSetId: string,
  emoteSetName: string | null = null,
): BackfillCoverageInterval {
  return { from, to, emoteSetId, emoteSetName, archiveHost: 'logs.cyex.app' };
}

describe('lastDayOf', () => {
  it('turns the exclusive window end into the last imported day, across month and year ends', () => {
    expect(lastDayOf('2026-10-08')).toBe('2026-10-07');
    expect(lastDayOf('2026-10-01')).toBe('2026-09-30');
    expect(lastDayOf('2027-01-01')).toBe('2026-12-31');
  });
});

describe('backfillSetLabel', () => {
  it('prefers the name and falls back to the id', () => {
    expect(backfillSetLabel({ emoteSetId: 'abc', emoteSetName: 'Halloween' })).toBe('Halloween');
    expect(backfillSetLabel({ emoteSetId: 'abc', emoteSetName: null })).toBe('abc');
  });
});

describe('replacedIntervals', () => {
  const FROM = '2026-07-08';
  const TO = '2026-10-08';

  it('is empty without coverage', () => {
    expect(replacedIntervals([], FROM, TO, 'a')).toEqual([]);
  });

  it('leaves out intervals of the chosen set', () => {
    expect(replacedIntervals([interval('2026-08-01', '2026-09-01', 'a')], FROM, TO, 'a')).toEqual(
      [],
    );
  });

  it('names an interval of another set inside the window as it is', () => {
    expect(
      replacedIntervals([interval('2026-08-01', '2026-09-01', 'b', 'Halloween')], FROM, TO, 'a'),
    ).toEqual([
      { from: '2026-08-01', to: '2026-09-01', emoteSetId: 'b', emoteSetName: 'Halloween' },
    ]);
  });

  it('clips an interval that reaches over either window edge', () => {
    const result = replacedIntervals(
      [interval('2026-04-08', '2026-08-01', 'b'), interval('2026-09-20', '2026-10-08', 'c')],
      FROM,
      TO,
      'a',
    );

    expect(result.map((r) => [r.from, r.to])).toEqual([
      ['2026-07-08', '2026-08-01'],
      ['2026-09-20', '2026-10-08'],
    ]);
  });

  it('ignores intervals that only touch the window or lie outside it', () => {
    expect(
      replacedIntervals(
        [interval('2026-04-08', FROM, 'b'), interval(TO, '2026-11-01', 'b')],
        FROM,
        TO,
        'a',
      ),
    ).toEqual([]);
  });

  it('keeps adjacent intervals of different sets apart and sorts them by start', () => {
    const result = replacedIntervals(
      [interval('2026-09-01', '2026-09-15', 'c'), interval('2026-08-01', '2026-09-01', 'b')],
      FROM,
      TO,
      'a',
    );

    expect(result.map((r) => r.emoteSetId)).toEqual(['b', 'c']);
  });
});
