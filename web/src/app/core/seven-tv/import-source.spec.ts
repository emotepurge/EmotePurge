import { describe, expect, it } from 'vitest';

import {
  ImportRow,
  dedupeImportRows,
  importOriginLeaderboardSort,
  importOriginSourceChannelName,
} from './import-source';

describe('dedupeImportRows', () => {
  it('collapses a duplicate sevenTvEmoteId and keeps the first occurrence', () => {
    const rows: ImportRow[] = [
      { sevenTvEmoteId: 'a1', name: 'PogU' },
      { sevenTvEmoteId: 'a2', name: 'Kappa' },
      { sevenTvEmoteId: 'a1', name: 'PogU-again' },
    ];

    const result = dedupeImportRows(rows);

    expect(result.duplicatesCollapsed).toBe(1);
    expect(result.rows).toEqual([
      { sevenTvEmoteId: 'a1', name: 'PogU' },
      { sevenTvEmoteId: 'a2', name: 'Kappa' },
    ]);
  });

  it('reports zero collapsed and preserves order when there are no duplicates', () => {
    const rows: ImportRow[] = [
      { sevenTvEmoteId: 'a1', name: 'PogU' },
      { sevenTvEmoteId: 'a2', name: 'Kappa' },
      { sevenTvEmoteId: 'a3', name: 'monkaS' },
    ];

    const result = dedupeImportRows(rows);

    expect(result.duplicatesCollapsed).toBe(0);
    expect(result.rows).toEqual(rows);
  });

  it('returns an empty result for an empty input', () => {
    const result = dedupeImportRows([]);

    expect(result.rows).toEqual([]);
    expect(result.duplicatesCollapsed).toBe(0);
  });
});

describe('importOriginSourceChannelName', () => {
  it('names the channel for a tracked-channel origin', () => {
    expect(importOriginSourceChannelName({ kind: 'channel', channelName: 'brudivoeller_tv' })).toBe(
      'brudivoeller_tv',
    );
  });

  it('names the channel for a foreign 7TV origin too', () => {
    // The whole reason this function exists: the wire body is built from its answer, and a `null`
    // here is answered with a 400 *after* the 7TV mutations have run — emotes copied, provenance
    // gone (spec F6).
    expect(
      importOriginSourceChannelName({ kind: 'seventv-channel', channelName: 'handofblood' }),
    ).toBe('handofblood');
  });

  it('sends no channel for a file origin even when the file names one', () => {
    expect(
      importOriginSourceChannelName({
        kind: 'file',
        fileName: 'emotes.json',
        exportedAt: '2026-09-05T10:00:00Z',
        channelName: 'brudivoeller_tv',
        envelopeKind: 'emote-list',
      }),
    ).toBeNull();
  });

  it('sends no channel for a leaderboard origin — it has no source channel at all', () => {
    expect(
      importOriginSourceChannelName({ kind: 'seventv-leaderboard', sortBy: 'TOP_ALL_TIME' }),
    ).toBeNull();
  });
});

describe('importOriginLeaderboardSort', () => {
  it('names the sort for a leaderboard origin', () => {
    expect(
      importOriginLeaderboardSort({ kind: 'seventv-leaderboard', sortBy: 'TRENDING_DAILY' }),
    ).toBe('TRENDING_DAILY');
    expect(
      importOriginLeaderboardSort({ kind: 'seventv-leaderboard', sortBy: 'TOP_ALL_TIME' }),
    ).toBe('TOP_ALL_TIME');
  });

  it('sends no sort for the three origins that are not a leaderboard pick', () => {
    expect(
      importOriginLeaderboardSort({ kind: 'channel', channelName: 'brudivoeller_tv' }),
    ).toBeNull();
    expect(
      importOriginLeaderboardSort({ kind: 'seventv-channel', channelName: 'handofblood' }),
    ).toBeNull();
    expect(
      importOriginLeaderboardSort({
        kind: 'file',
        fileName: 'emotes.json',
        exportedAt: '2026-09-05T10:00:00Z',
        channelName: 'brudivoeller_tv',
        envelopeKind: 'emote-list',
      }),
    ).toBeNull();
  });
});
