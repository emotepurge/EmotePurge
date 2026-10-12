import { describe, expect, it } from 'vitest';

import {
  formatBucketTime,
  formatUnit,
  niceTicks,
  summarizeSeries,
  toLinePath,
} from './chart-series';

const GEOMETRY = { width: 100, height: 50, yMin: 0, yMax: 10 };

function subPaths(path: string): string[] {
  return path.split(/(?=M)/).map((part) => part.trim());
}

describe('toLinePath', () => {
  it('splits into two sub-paths at a null instead of bridging the gap', () => {
    const path = toLinePath([1, null, 2], GEOMETRY);

    expect(subPaths(path)).toHaveLength(2);
    expect(path).not.toContain('50 '); // the middle bucket (x = 50) is never visited
  });

  it('draws one continuous sub-path without gaps', () => {
    expect(subPaths(toLinePath([1, 2, 3], GEOMETRY))).toHaveLength(1);
  });

  it('yields an empty path when every value is unknown', () => {
    expect(toLinePath([null, null], GEOMETRY)).toBe('');
    expect(toLinePath([], GEOMETRY)).toBe('');
  });

  it('keeps an isolated known value visible as a zero-length segment', () => {
    const path = toLinePath([null, 5, null], GEOMETRY);

    expect(path).toBe('M50 25 L50 25');
  });

  it('places x by bucket index of the full count, so a split series stays aligned', () => {
    const path = toLinePath([null, null, 4], { ...GEOMETRY, count: 5 });

    expect(path.startsWith('M50 ')).toBe(true);
  });
});

describe('niceTicks', () => {
  it('returns round steps that enclose the data', () => {
    expect(niceTicks(0, 0.87, 4)).toEqual([0, 0.5, 1]);
  });

  it('starts at a multiple of the step below the minimum', () => {
    const ticks = niceTicks(3, 97, 5);

    expect(ticks[0]).toBeLessThanOrEqual(3);
    expect(ticks[ticks.length - 1]).toBeGreaterThanOrEqual(97);
    expect(ticks).toEqual([0, 50, 100]);
  });

  it('gives a flat range more than one tick', () => {
    expect(niceTicks(5, 5, 4).length).toBeGreaterThan(1);
    expect(niceTicks(0, 0, 4)).toEqual([0, 0.5, 1]);
  });

  it('keeps whole-number scales free of fractional ticks with a minimum step', () => {
    for (const [min, max] of [
      [0, 0],
      [0, 1],
    ]) {
      const ticks = niceTicks(min, max, 4, 1);
      const labels = ticks.map((tick) => formatUnit(tick, 'count', 'en'));

      expect(ticks).toEqual([0, 1]);
      expect(new Set(labels).size).toBe(labels.length);
    }
  });

  it('terminates at magnitudes where adding the step no longer changes the value', () => {
    expect(niceTicks(1e16, 1e16 + 2, 4).length).toBeLessThanOrEqual(50);
  });

  it('returns nothing for non-finite input', () => {
    expect(niceTicks(NaN, 1, 4)).toEqual([]);
  });
});

describe('formatUnit', () => {
  it('switches from MiB to GiB at one GiB', () => {
    expect(formatUnit(1024 ** 3 - 1, 'bytes', 'en')).toMatch(/MiB$/);
    expect(formatUnit(1024 ** 3, 'bytes', 'en')).toBe('1.00 GiB');
    expect(formatUnit(512 * 1024 ** 2, 'bytes', 'en')).toBe('512.0 MiB');
  });

  it('shows percent with one decimal and cores with two', () => {
    expect(formatUnit(12.34, 'percent', 'en')).toBe('12.3 %');
    expect(formatUnit(0.5, 'cores', 'en', 'cores')).toBe('0.50 cores');
  });

  it('uses the locale for separators and the German word for cores', () => {
    expect(formatUnit(1.5, 'cores', 'de', 'Kerne')).toBe('1,50 Kerne');
  });

  it('shows whole tasks without decimals and fractional ones with two', () => {
    expect(formatUnit(3, 'tasks', 'en', 'Tasks')).toBe('3 Tasks');
    expect(formatUnit(0.456, 'tasks', 'en', 'Tasks')).toBe('0.46 Tasks');
  });

  it('rounds counts to whole numbers', () => {
    expect(formatUnit(1234.6, 'count', 'en')).toBe('1,235');
  });
});

describe('summarizeSeries', () => {
  it('reports min, max, last known value and the count of known points', () => {
    expect(summarizeSeries([3, null, 1, 7, null])).toEqual({ min: 1, max: 7, last: 7, known: 3 });
  });

  it('takes the last known value, not the last bucket', () => {
    expect(summarizeSeries([2, 5, null]).last).toBe(5);
  });

  it('reports nothing known for an empty or all-null series', () => {
    expect(summarizeSeries([null, null])).toEqual({ min: null, max: null, last: null, known: 0 });
    expect(summarizeSeries([]).known).toBe(0);
  });
});

describe('formatBucketTime', () => {
  it('reads in UTC and says so for sub-day buckets', () => {
    const text = formatBucketTime('2026-10-11T23:30:00Z', 300, 'en');

    expect(text).toContain('UTC');
    expect(text).toContain('11:30');
  });

  it('shows the date only for day-wide buckets, without a time of day', () => {
    const text = formatBucketTime('2026-10-11T00:00:00Z', 86_400, 'en');

    expect(text).toContain('11');
    expect(text).not.toContain('UTC');
  });
});
