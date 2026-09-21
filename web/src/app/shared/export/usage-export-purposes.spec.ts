import { describe, expect, it } from 'vitest';

import { EmoteUsageTotalDto } from '../../core/usage-stats/usage-stat.model';
import { CSV_MIME } from './csv';
import { JSON_MIME } from './export-envelope';
import {
  ExportPurposeId,
  UsageExportPurposeScope,
  buildUsageExportPurposeDownload,
  usageExportPurposeOptions,
} from './usage-export-purposes';

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

function scope(overrides: Partial<UsageExportPurposeScope> = {}): UsageExportPurposeScope {
  return {
    channelName: 'sensitron',
    emoteSetId: 'set-1',
    emoteSetName: 'Main',
    from: '2026-07-01',
    to: '2026-08-01',
    filtered: false,
    rows: [usageRow()],
    scope: 'visible',
    trendFor: () => 'rising',
    ...overrides,
  };
}

describe('usageExportPurposeOptions', () => {
  it('offers only the two usage purposes when the emote list is not offerable', () => {
    const options = usageExportPurposeOptions(false);
    expect(options.map((option) => option.id)).toEqual(['usage-csv', 'usage-json']);
  });

  it('appends the emote list last, in the fixed order, when offerable', () => {
    const options = usageExportPurposeOptions(true);
    expect(options.map((option) => option.id)).toEqual(['usage-csv', 'usage-json', 'emote-list']);
  });

  it('carries the exact i18n keys the dialog renders', () => {
    const options = usageExportPurposeOptions(true);
    expect(options).toEqual([
      { id: 'usage-csv', labelKey: 'export.purposeAnalyse', hintKey: 'export.purposeAnalyseHint' },
      {
        id: 'usage-json',
        labelKey: 'export.purposeProcess',
        hintKey: 'export.purposeProcessHint',
      },
      {
        id: 'emote-list',
        labelKey: 'export.purposeReimport',
        hintKey: 'export.purposeReimportHint',
      },
    ]);
  });
});

describe('buildUsageExportPurposeDownload — usage-csv', () => {
  it('produces the CSV filename, CSV content and CSV mime type', () => {
    const download = buildUsageExportPurposeDownload('usage-csv', scope());

    expect(download).not.toBeNull();
    expect(download?.filename).toBe('emotepurge_sensitron_usage_set-1_2026-07-01_2026-08-01.csv');
    expect(download?.mimeType).toBe(CSV_MIME);
    const [header, row] = (download?.content ?? '').replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(header).toBe(
      'emote_name,seven_tv_emote_id,total_use_count,previous_window_use_count,last_used_date,first_seen_at,trend',
    );
    expect(row).toBe('PogU,01ABC,42,12,2026-08-01,2026-06-01T00:00:00Z,rising');
  });

  it('omits the set segment when the capture carried no set (AK 65)', () => {
    const download = buildUsageExportPurposeDownload('usage-csv', scope({ emoteSetId: null }));
    expect(download?.filename).toBe('emotepurge_sensitron_usage_2026-07-01_2026-08-01.csv');
  });
});

describe('buildUsageExportPurposeDownload — usage-json', () => {
  it('produces the JSON filename, an envelope with nothing withheld and the JSON mime type', () => {
    const download = buildUsageExportPurposeDownload('usage-json', scope({ filtered: true }));

    expect(download).not.toBeNull();
    expect(download?.filename).toBe('emotepurge_sensitron_usage_set-1_2026-07-01_2026-08-01.json');
    expect(download?.mimeType).toBe(JSON_MIME);
    const parsed = JSON.parse(download?.content ?? '{}');
    expect(parsed.kind).toBe('usage');
    expect(parsed.withheld).toEqual([]);
    expect(parsed.meta).toMatchObject({
      emoteSetId: 'set-1',
      emoteSetName: 'Main',
      from: '2026-07-01',
      to: '2026-08-01',
      rowCount: 1,
      scope: 'visible',
      filtered: true,
    });
    expect(parsed.rows[0]).toMatchObject({
      emoteName: 'PogU',
      sevenTvEmoteId: '01ABC',
      trend: 'rising',
    });
  });
});

describe('buildUsageExportPurposeDownload — emote-list', () => {
  it('dedupes rows, carries the captured set id and scope into the envelope', () => {
    const rows = [
      usageRow({ sevenTvEmoteId: '01ABC', emoteName: 'PogU' }),
      usageRow({ sevenTvEmoteId: '01DEF', emoteName: 'Kappa' }),
      usageRow({ sevenTvEmoteId: '01ABC', emoteName: 'PogU-duplicate' }),
    ];
    const download = buildUsageExportPurposeDownload(
      'emote-list',
      scope({ emoteSetId: 'set-42', scope: 'selection', rows }),
    );

    expect(download).not.toBeNull();
    expect(download?.mimeType).toBe(JSON_MIME);
    expect(download?.filename).toMatch(/^emotepurge_sensitron_emote-list_\d{4}-\d{2}-\d{2}\.json$/);
    const parsed = JSON.parse(download?.content ?? '{}');
    expect(parsed.kind).toBe('emote-list');
    expect(parsed.meta).toMatchObject({
      sourceEmoteSetId: 'set-42',
      rowCount: 2,
      scope: 'selection',
    });
    expect(parsed.rows).toEqual([
      { sevenTvEmoteId: '01ABC', name: 'PogU' },
      { sevenTvEmoteId: '01DEF', name: 'Kappa' },
    ]);
  });

  it('returns null — the unreachable case — when no set id was captured', () => {
    const download = buildUsageExportPurposeDownload('emote-list', scope({ emoteSetId: null }));
    expect(download).toBeNull();
  });
});

describe('buildUsageExportPurposeDownload — exhaustiveness', () => {
  it('throws on a purpose id outside the known union, rather than silently returning nothing', () => {
    // ExportPurposeId is a closed union — reaching the `default` branch is impossible through the
    // public type, so the only way in is a deliberately bogus cast, the same way a malformed
    // dialog choice from outside TypeScript's own guarantees would arrive.
    expect(() =>
      buildUsageExportPurposeDownload('bogus' as unknown as ExportPurposeId, scope()),
    ).toThrow('Unhandled export purpose: bogus');
  });
});
