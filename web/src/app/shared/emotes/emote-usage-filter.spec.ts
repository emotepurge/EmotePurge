import { describe, expect, it } from 'vitest';

import { EmoteUsageFilter } from './emote-usage-filter';

interface Row {
  emoteName: string;
  totalUseCount: number | null;
  firstSeenAt?: string | null;
}

const NOW = new Date('2026-08-01T12:00:00Z');

const ROWS: Row[] = [
  { emoteName: 'peepoHappy', totalUseCount: 0 },
  { emoteName: 'peepoSad', totalUseCount: 5 },
  { emoteName: 'catJAM', totalUseCount: 120 },
];

describe('EmoteUsageFilter', () => {
  it('starts inactive and passes everything through', () => {
    const filter = new EmoteUsageFilter<Row>();

    expect(filter.isAnyActive()).toBe(false);
    expect(filter.apply(ROWS)).toEqual(ROWS);
  });

  it('filters by min/max count and bare-substring name query', () => {
    const filter = new EmoteUsageFilter<Row>();

    filter.setMinCount('1');
    expect(filter.apply(ROWS).map((row) => row.emoteName)).toEqual(['peepoSad', 'catJAM']);

    filter.setMaxCount('10');
    expect(filter.apply(ROWS).map((row) => row.emoteName)).toEqual(['peepoSad']);

    // A bare query is an unanchored substring match — only typing * or ? opts into glob anchoring.
    filter.setMinCount('');
    filter.setMaxCount('');
    filter.setNameFilter('peepo');
    expect(filter.apply(ROWS).map((row) => row.emoteName)).toEqual(['peepoHappy', 'peepoSad']);

    expect(filter.isAnyActive()).toBe(true);
  });

  it('reset() clears every filter', () => {
    // No onChange hook any more (Konzept "Auswahl überlebt Suche und Filter" 2026-09-18): a filter
    // change, reset() included, never touches a host's selection — the filter lost that job
    // entirely, it does not just stop announcing it.
    const filter = new EmoteUsageFilter<Row>();
    filter.setMinCount('1');
    filter.setNameFilter('cat');

    filter.reset();

    expect(filter.isAnyActive()).toBe(false);
    expect(filter.apply(ROWS)).toEqual(ROWS);
  });

  it('setRange() drives the 0/0 range that "never used" means, and clears back to unbounded', () => {
    const filter = new EmoteUsageFilter<Row>();

    filter.setRange(0, 0);
    expect(filter.isUnusedActive()).toBe(true);
    expect(filter.apply(ROWS).map((row) => row.emoteName)).toEqual(['peepoHappy']);

    // Re-applying the same range is idempotent — the menu is a radio group, so picking the selected
    // option again must not toggle it back off the way the old toggleUnused() did.
    filter.setRange(0, 0);
    expect(filter.isUnusedActive()).toBe(true);

    filter.setRange(null, null);
    expect(filter.isUnusedActive()).toBe(false);
    expect(filter.apply(ROWS)).toEqual(ROWS);
  });

  it('null usage never matches a usage bound — including "unused" — but passes without bounds', () => {
    const rows: Row[] = [
      { emoteName: 'withData', totalUseCount: 0 },
      { emoteName: 'withheld', totalUseCount: null },
    ];
    const filter = new EmoteUsageFilter<Row>();

    // No usage bounds set: null rows pass through untouched (voter view, name filter only).
    expect(filter.apply(rows)).toEqual(rows);

    // "Unused" means a confirmed 0 — withheld data must not masquerade as unused.
    filter.setRange(0, 0);
    expect(filter.apply(rows).map((row) => row.emoteName)).toEqual(['withData']);

    filter.setRange(null, null);
    filter.setMinCount('0');
    expect(filter.apply(rows).map((row) => row.emoteName)).toEqual(['withData']);
  });

  it('toggleHideObserved() hides only emotes still inside their observation period', () => {
    const rows: Row[] = [
      { emoteName: 'established', totalUseCount: 0, firstSeenAt: '2026-01-01T00:00:00Z' },
      { emoteName: 'justAdded', totalUseCount: 0, firstSeenAt: '2026-07-28T00:00:00Z' },
    ];
    const filter = new EmoteUsageFilter<Row>();

    filter.toggleHideObserved();
    expect(filter.isHideObservedActive()).toBe(true);
    expect(filter.apply(rows, NOW).map((row) => row.emoteName)).toEqual(['established']);

    filter.toggleHideObserved();
    expect(filter.apply(rows, NOW)).toEqual(rows);
  });

  it('never hides an emote whose entry date is unknown', () => {
    // Right after the column was introduced most rows have no date; hiding them would empty the
    // grid for a filter the user reads as "hide the new ones".
    const rows: Row[] = [
      { emoteName: 'noDate', totalUseCount: 0 },
      { emoteName: 'nullDate', totalUseCount: 0, firstSeenAt: null },
    ];
    const filter = new EmoteUsageFilter<Row>();

    filter.toggleHideObserved();

    expect(filter.apply(rows, NOW)).toEqual(rows);
  });

  it('counts the observation toggle as an active filter and clears it on reset', () => {
    const filter = new EmoteUsageFilter<Row>();

    filter.toggleHideObserved();
    expect(filter.isAnyActive()).toBe(true);

    filter.reset();
    expect(filter.isHideObservedActive()).toBe(false);
    expect(filter.isAnyActive()).toBe(false);
  });

  it('combines the observation toggle with the unused filter instead of overriding it', () => {
    const rows: Row[] = [
      { emoteName: 'oldUnused', totalUseCount: 0, firstSeenAt: '2026-01-01T00:00:00Z' },
      { emoteName: 'newUnused', totalUseCount: 0, firstSeenAt: '2026-07-28T00:00:00Z' },
      { emoteName: 'oldUsed', totalUseCount: 40, firstSeenAt: '2026-01-01T00:00:00Z' },
    ];
    const filter = new EmoteUsageFilter<Row>();

    filter.setRange(0, 0);
    // Fresh emotes stay visible in the plain unused list — the card badge explains them there.
    expect(filter.apply(rows, NOW).map((row) => row.emoteName)).toEqual(['oldUnused', 'newUnused']);

    filter.toggleHideObserved();
    expect(filter.apply(rows, NOW).map((row) => row.emoteName)).toEqual(['oldUnused']);
  });
});
