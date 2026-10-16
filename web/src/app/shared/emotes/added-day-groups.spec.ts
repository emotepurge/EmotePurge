import { describe, expect, it } from 'vitest';

import {
  addedAtOf,
  addedDayKind,
  addedDayKeyOf,
  groupByAddedDay,
  localDayKey,
  UNKNOWN_ADDED_DAY,
} from './added-day-groups';

interface Row {
  name: string;
  firstSeenAt: string | null;
  liveAddedAt?: string;
  setAddedAt?: string;
}

// Built from local components so the expectations hold in any time zone the suite runs in.
const at = (month: number, day: number, hour = 12, minute = 0): string =>
  new Date(2026, month - 1, day, hour, minute).toISOString();

const row = (name: string, firstSeenAt: string | null, liveAddedAt?: string): Row => ({
  name,
  firstSeenAt,
  liveAddedAt,
});

const names = (rows: readonly Row[]) => rows.map((r) => r.name);
const run = (rows: Row[], direction: 'asc' | 'desc' = 'desc') =>
  groupByAddedDay(rows, direction, (r) => r.name);

describe('addedAtOf', () => {
  it('reads firstSeenAt, falling back to the per-entry live date', () => {
    expect(addedAtOf(row('a', at(10, 15)))).toBe(Date.parse(at(10, 15)));
    expect(addedAtOf(row('a', null, at(10, 14)))).toBe(Date.parse(at(10, 14)));
  });

  it('prefers the viewed set\u2019s own date over firstSeenAt', () => {
    const rowWithBoth = { ...row('a', at(10, 1)), setAddedAt: at(10, 15) };
    expect(addedAtOf(rowWithBoth)).toBe(Date.parse(at(10, 15)));
    expect(addedDayKeyOf(rowWithBoth)).toBe('2026-10-15');
  });

  it('treats a missing or unparseable date as unknown', () => {
    expect(addedAtOf(row('a', null))).toBeNull();
    expect(addedAtOf(row('a', 'not a date'))).toBeNull();
  });
});

describe('groupByAddedDay', () => {
  it('lists the newest day first, and the newest emote first inside it', () => {
    const groups = run([
      row('old', at(10, 12)),
      row('morning', at(10, 16, 9)),
      row('evening', at(10, 16, 21)),
      row('yesterday', at(10, 15)),
    ]);

    expect(groups.map((g) => g.key)).toEqual(['2026-10-16', '2026-10-15', '2026-10-12']);
    expect(names(groups[0].items)).toEqual(['evening', 'morning']);
  });

  it('reverses both the days and the order inside a day when ascending', () => {
    const groups = run(
      [row('morning', at(10, 16, 9)), row('evening', at(10, 16, 21)), row('old', at(10, 12))],
      'asc',
    );

    expect(groups.map((g) => g.key)).toEqual(['2026-10-12', '2026-10-16']);
    expect(names(groups[1].items)).toEqual(['morning', 'evening']);
  });

  it('puts rows without a date into a trailing group in both directions, in name order', () => {
    const rows = [row('zed', null), row('dated', at(10, 15)), row('abe', null)];

    for (const direction of ['desc', 'asc'] as const) {
      const groups = run(rows, direction);
      expect(groups[groups.length - 1].key).toBe(UNKNOWN_ADDED_DAY);
      expect(names(groups[groups.length - 1].items)).toEqual(['abe', 'zed']);
    }
  });

  it('has no unknown group when every row is dated', () => {
    expect(run([row('a', at(10, 15))]).map((g) => g.key)).toEqual(['2026-10-15']);
  });

  it('splits at local midnight rather than at a UTC day boundary', () => {
    const groups = run([row('justBefore', at(10, 15, 23, 59)), row('justAfter', at(10, 16, 0, 1))]);

    expect(groups.map((g) => g.key)).toEqual(['2026-10-16', '2026-10-15']);
    expect(names(groups[0].items)).toEqual(['justAfter']);
  });

  it('breaks a tie of identical instants by name, ascending in either direction', () => {
    const same = at(10, 15, 10);
    for (const direction of ['desc', 'asc'] as const) {
      const groups = run([row('Pog', same), row('catJAM', same), row('Bedge', same)], direction);
      expect(names(groups[0].items)).toEqual(['Bedge', 'catJAM', 'Pog']);
    }
  });

  it('dates a per-entry live row by its live add date', () => {
    const groups = run([row('live', null, at(10, 14))]);
    expect(groups.map((g) => g.key)).toEqual(['2026-10-14']);
  });

  it('returns no groups for no rows', () => {
    expect(run([])).toEqual([]);
  });
});

describe('addedDayKeyOf', () => {
  it('names the group a single row belongs to', () => {
    expect(addedDayKeyOf(row('a', at(10, 15, 23, 30)))).toBe('2026-10-15');
    expect(addedDayKeyOf(row('a', null))).toBe(UNKNOWN_ADDED_DAY);
  });
});

describe('localDayKey', () => {
  it('zero-pads month and day', () => {
    expect(localDayKey(new Date(2026, 0, 5, 12).getTime())).toBe('2026-01-05');
  });
});

describe('addedDayKind', () => {
  const noon = (y: number, m: number, d: number) => new Date(y, m - 1, d, 12);

  it('tells today, yesterday and any other day apart', () => {
    const now = noon(2026, 10, 16);
    expect(addedDayKind('2026-10-16', now)).toBe('today');
    expect(addedDayKind('2026-10-15', now)).toBe('yesterday');
    expect(addedDayKind('2026-10-14', now)).toBe('day');
    expect(addedDayKind('2026-10-17', now)).toBe('day');
  });

  it('finds yesterday across a month boundary', () => {
    expect(addedDayKind('2026-09-30', noon(2026, 10, 1))).toBe('yesterday');
    expect(addedDayKind('2026-02-28', noon(2026, 3, 1))).toBe('yesterday');
  });

  it('finds yesterday across a year boundary', () => {
    expect(addedDayKind('2025-12-31', noon(2026, 1, 1))).toBe('yesterday');
    expect(addedDayKind('2026-01-01', noon(2026, 1, 1))).toBe('today');
  });

  it('judges by the local day, not the instant: just after midnight is still a new day', () => {
    const justAfterMidnight = new Date(2026, 9, 16, 0, 1);
    expect(addedDayKind('2026-10-15', justAfterMidnight)).toBe('yesterday');
  });
});
