import { describe, expect, it } from 'vitest';

import {
  addedAtOf,
  addedDayKeyOf,
  groupByAddedDay,
  localDayKey,
  UNKNOWN_ADDED_DAY,
} from './added-day-groups';

interface Row {
  name: string;
  firstSeenAt: string | null;
  liveAddedAt?: string;
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

  it('dates a re-added emote by its latest entry, so it sits in the newer group only', () => {
    // The Api moves firstSeenAt forward on a re-add; the row therefore carries one date, the newest.
    const groups = run([row('readded', at(10, 16, 8)), row('kept', at(10, 12))]);

    expect(groups.map((g) => g.key)).toEqual(['2026-10-16', '2026-10-12']);
    expect(names(groups[0].items)).toEqual(['readded']);
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

  it('keeps rows without counts: dating looks at the date alone', () => {
    // A freshly added emote with no counts yet is exactly what the sort is for; the grouping must
    // not know about counts at all.
    const groups = run([row('fresh', at(10, 16), undefined)]);
    expect(names(groups[0].items)).toEqual(['fresh']);
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
