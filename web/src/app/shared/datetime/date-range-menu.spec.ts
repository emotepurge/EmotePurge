import { describe, expect, it } from 'vitest';

import {
  MAX_RANGE_DAYS,
  allTimeStart,
  dateRangePresetOptions,
  daysAgo,
  setObservedRange,
  toIsoDate,
} from './date-range-menu';

/** What the API actually rejects: `toDate.DayNumber - fromDate.DayNumber > 366` → RangeTooLarge. */
const API_MAX_RANGE_DAYS = 366;

function spanInDays(from: string): number {
  return Math.round(
    (Date.parse(`${toIsoDate(new Date())}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) /
      86_400_000,
  );
}

describe('allTimeStart', () => {
  it('reaches back to the channel’s own starting point when that is inside the window', () => {
    const trackedSince = toIsoDate(daysAgo(20));

    expect(allTimeStart(trackedSince)).toBe(trackedSince);
  });

  it('falls back to the widest accepted span while the starting point is unknown', () => {
    // The first render happens before the set-status response lands, so `earliest` is null exactly
    // once per page open — and must still produce a request the API answers.
    expect(allTimeStart(null)).toBe(toIsoDate(daysAgo(MAX_RANGE_DAYS)));
  });

  it('clamps a starting point older than the API allows instead of sending a 400', () => {
    // Cannot happen yet (tracking began 2026-07), which is why it is pinned here: the day a channel
    // has been tracked for over a year, "all time" must degrade to the widest answerable range
    // rather than turn the default page load into RangeTooLarge.
    expect(allTimeStart('2020-01-01')).toBe(toIsoDate(daysAgo(MAX_RANGE_DAYS)));
  });

  it('never asks for a wider span than the endpoint accepts', () => {
    for (const earliest of [null, '2020-01-01', toIsoDate(daysAgo(5))]) {
      expect(spanInDays(allTimeStart(earliest))).toBeLessThanOrEqual(API_MAX_RANGE_DAYS);
    }
  });
});

describe("the 'set-observed' preset (spec #200, 8.5, AK 61)", () => {
  it('is offered only when the chosen set has at least one observation interval', () => {
    const withoutInterval = setObservedRange([], '2026-10-20');
    const withInterval = setObservedRange(
      [{ fromUtc: '2026-10-01T00:00:00Z', toUtc: null }],
      '2026-10-20',
    );

    expect(withoutInterval).toBeNull();
    expect(
      dateRangePresetOptions(withoutInterval !== null).map((option) => option.value),
    ).not.toContain('set-observed');
    expect(dateRangePresetOptions(withInterval !== null).map((option) => option.value)).toEqual([
      '0',
      '7',
      '30',
      'all',
      'set-observed',
      'custom',
    ]);
  });

  it('selects the youngest interval, open end meaning today, whatever order the intervals arrive in', () => {
    const intervals = [
      { fromUtc: '2026-10-01T18:00:00Z', toUtc: null },
      { fromUtc: '2026-07-20T09:00:00Z', toUtc: '2026-08-02T12:00:00Z' },
    ];

    expect(setObservedRange(intervals, '2026-10-20')).toEqual({
      from: '2026-10-01',
      to: '2026-10-20',
    });
    expect(setObservedRange([...intervals].reverse(), '2026-10-20')).toEqual({
      from: '2026-10-01',
      to: '2026-10-20',
    });
    // A closed youngest interval ends where it ended, not today.
    expect(
      setObservedRange([{ fromUtc: '2026-07-20T09:00:00Z', toUtc: '2026-08-02T12:00:00Z' }]),
    ).toEqual({ from: '2026-07-20', to: '2026-08-02' });
  });
});
