import { describe, expect, it } from 'vitest';

import { EmoteUsageTotalDto } from '../../core/usage-stats/usage-stat.model';
import { UsageExportInput, usageCsv, usageExportFilename, usageJson } from './usage-export';

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

function input(overrides: Partial<UsageExportInput> = {}): UsageExportInput {
  return {
    channelName: 'sensitron',
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
  it('carries channel and range, sanitizing the channel casing', () => {
    expect(usageExportFilename(input({ channelName: 'HandOfBlood' }), 'csv')).toBe(
      'emotepurge_handofblood_usage_2026-07-01_2026-08-01.csv',
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
});

describe('usageJson', () => {
  it('wraps the rows in the envelope with nothing withheld', () => {
    const parsed = JSON.parse(usageJson(input({ filtered: true })));
    expect(parsed.source).toBe('emotepurge');
    expect(parsed.kind).toBe('usage');
    expect(parsed.channelName).toBe('sensitron');
    expect(parsed.withheld).toEqual([]);
    expect(parsed.meta).toMatchObject({
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
});
