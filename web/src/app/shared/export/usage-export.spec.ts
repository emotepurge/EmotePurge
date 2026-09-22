import { describe, expect, it } from 'vitest';

import { EmoteUsageTotalDto } from '../../core/usage-stats/usage-stat.model';
import {
  UsageExportInput,
  UsageExportSourceRow,
  usageCsv,
  usageExportFilename,
  usageJson,
} from './usage-export';

function usageRow(overrides: Partial<EmoteUsageTotalDto> = {}): EmoteUsageTotalDto {
  return {
    emoteId: 'guid-1',
    emoteName: 'PogU',
    sevenTvEmoteId: '01ABC',
    imageUrl: 'https://cdn.7tv.app/x',
    totalUseCount: 42,
    lastUsedDate: '2026-08-01',
    previousWindowUseCount: 12,
    firstSeenAt: '2026-06-01T00:00:00Z',
    isArchived: false,
    nameTwinEmoteSetIds: [],
    ...overrides,
  };
}

/** Same shape as `usageRow`, but built directly against `UsageExportSourceRow` (the merged,
 *  null-tolerant `EmoteUsageTotal` view — spec 7.1) rather than the DB-only `EmoteUsageTotalDto`,
 *  which types `totalUseCount`/`previousWindowUseCount` as plain `number`. Needed for a null-count
 *  row (E17, AK 65): a non-active set's live member with no `UsageStat` at all. */
function usageSourceRow(overrides: Partial<UsageExportSourceRow> = {}): UsageExportSourceRow {
  return {
    emoteName: 'PogU',
    sevenTvEmoteId: '01ABC',
    totalUseCount: 42,
    previousWindowUseCount: 12,
    lastUsedDate: '2026-08-01',
    firstSeenAt: '2026-06-01T00:00:00Z',
    ...overrides,
  };
}

function input(overrides: Partial<UsageExportInput> = {}): UsageExportInput {
  return {
    channelName: 'sensitron',
    emoteSetId: 'set-01ABCDEF',
    emoteSetName: 'Main',
    from: '2026-07-01',
    to: '2026-08-01',
    rows: [usageRow()],
    scope: 'visible',
    filtered: false,
    trendFor: () => 'rising',
    ...overrides,
  };
}

describe('usageExportFilename', () => {
  it('carries channel, set and range, sanitizing the channel casing', () => {
    expect(usageExportFilename(input({ channelName: 'HandOfBlood' }), 'csv')).toBe(
      'emotepurge_handofblood_usage_ABCDEF_2026-07-01_2026-08-01.csv',
    );
  });

  it('names the set by its last six characters, so two set exports of the same channel never collide (AK 65)', () => {
    expect(usageExportFilename(input({ emoteSetId: 'set-halloween-99' }), 'json')).toBe(
      'emotepurge_sensitron_usage_een-99_2026-07-01_2026-08-01.json',
    );
  });

  it('omits the set segment entirely when there is no set (a channel with no active/selected set)', () => {
    expect(usageExportFilename(input({ emoteSetId: null }), 'csv')).toBe(
      'emotepurge_sensitron_usage_2026-07-01_2026-08-01.csv',
    );
  });
});

describe('usageCsv', () => {
  it('emits every column with the trend as a language-neutral token', () => {
    const csv = usageCsv(input());
    const [header, row] = csv.replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(header).toBe(
      'emote_name,seven_tv_emote_id,total_use_count,previous_window_use_count,last_used_date,first_seen_at,trend',
    );
    expect(row).toBe('PogU,01ABC,42,12,2026-08-01,2026-06-01T00:00:00Z,rising');
  });

  it('renders a never-used emote with empty last_used_date, not zero', () => {
    const csv = usageCsv(input({ rows: [usageRow({ lastUsedDate: null, totalUseCount: 0 })] }));
    const row = csv.replace(/^﻿/, '').trimEnd().split('\r\n')[1];
    expect(row).toBe('PogU,01ABC,0,12,,2026-06-01T00:00:00Z,rising');
  });

  it('renders a null-count row (no counts under the shown set, E17) as empty cells and an unknown trend, never 0 (AK 65)', () => {
    const csv = usageCsv(
      input({
        rows: [usageSourceRow({ totalUseCount: null, previousWindowUseCount: null })],
        trendFor: () => 'unknown',
      }),
    );
    const row = csv.replace(/^﻿/, '').trimEnd().split('\r\n')[1];
    expect(row).toBe('PogU,01ABC,,,2026-08-01,2026-06-01T00:00:00Z,unknown');
  });
});

describe('usageJson', () => {
  it('wraps the rows in the envelope with nothing withheld', () => {
    const parsed = JSON.parse(usageJson(input({ filtered: true })));
    expect(parsed.source).toBe('emotepurge');
    expect(parsed.kind).toBe('usage');
    expect(parsed.channelName).toBe('sensitron');
    expect(parsed.withheld).toEqual([]);
    expect(parsed.meta).toMatchObject({
      emoteSetId: 'set-01ABCDEF',
      emoteSetName: 'Main',
      from: '2026-07-01',
      to: '2026-08-01',
      rowCount: 1,
      scope: 'visible',
      filtered: true,
    });
    expect(parsed.rows[0]).toEqual({
      emoteName: 'PogU',
      sevenTvEmoteId: '01ABC',
      totalUseCount: 42,
      previousWindowUseCount: 12,
      lastUsedDate: '2026-08-01',
      firstSeenAt: '2026-06-01T00:00:00Z',
      trend: 'rising',
    });
  });

  it('records a selection export as such in the meta', () => {
    const parsed = JSON.parse(usageJson(input({ scope: 'selection' })));
    expect(parsed.meta.scope).toBe('selection');
  });

  it('names the set in the meta, or nulls both fields when there is none (AK 65)', () => {
    const parsed = JSON.parse(usageJson(input({ emoteSetId: null, emoteSetName: null })));
    expect(parsed.meta.emoteSetId).toBeNull();
    expect(parsed.meta.emoteSetName).toBeNull();
  });

  it('serializes a null-count row as JSON null, never 0, with an unknown trend (E17, AK 65)', () => {
    const parsed = JSON.parse(
      usageJson(
        input({
          rows: [usageSourceRow({ totalUseCount: null, previousWindowUseCount: null })],
          trendFor: () => 'unknown',
        }),
      ),
    );
    expect(parsed.rows[0]).toEqual({
      emoteName: 'PogU',
      sevenTvEmoteId: '01ABC',
      totalUseCount: null,
      previousWindowUseCount: null,
      lastUsedDate: '2026-08-01',
      firstSeenAt: '2026-06-01T00:00:00Z',
      trend: 'unknown',
    });
  });
});
