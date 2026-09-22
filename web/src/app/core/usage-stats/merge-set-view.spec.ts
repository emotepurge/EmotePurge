import { describe, expect, it } from 'vitest';

import { ForeignEmoteRow } from '../seven-tv/foreign-emote-set.model';
import { mergeSetView } from './merge-set-view';
import { EmoteUsageTotalDto } from './usage-stat.model';

function dto(overrides: Partial<EmoteUsageTotalDto> = {}): EmoteUsageTotalDto {
  return {
    emoteId: 'emote-guid-1',
    emoteName: 'PeepoHappy',
    sevenTvEmoteId: '01AAA',
    imageUrl: 'https://cdn.example/peepo.webp',
    totalUseCount: 42,
    lastUsedDate: '2026-09-01',
    previousWindowUseCount: 10,
    firstSeenAt: '2026-08-01T00:00:00Z',
    isArchived: false,
    nameTwinEmoteSetIds: [],
    ...overrides,
  };
}

function liveRow(overrides: Partial<ForeignEmoteRow> = {}): ForeignEmoteRow {
  return {
    sevenTvEmoteId: '01AAA',
    name: 'PeepoHappy',
    defaultName: 'PeepoHappy',
    imageUrl: 'https://cdn.example/peepo.webp',
    topAllTime: null,
    trending: null,
    ...overrides,
  };
}

describe('mergeSetView', () => {
  it('marks a live member without a counted row as membership live with null payload (Klasse 3)', () => {
    const result = mergeSetView(
      [],
      [liveRow({ sevenTvEmoteId: '01BBB', name: 'NeverCounted' })],
      false,
    );

    expect(result).toEqual([
      {
        emoteId: null,
        emoteName: 'NeverCounted',
        sevenTvEmoteId: '01BBB',
        imageUrl: 'https://cdn.example/peepo.webp',
        totalUseCount: null,
        lastUsedDate: null,
        previousWindowUseCount: null,
        firstSeenAt: null,
        membership: 'live',
        slotCount: 1,
        aliases: ['NeverCounted'],
        nameTwinEmoteSetIds: [],
      },
    ]);
  });

  it('marks a counted row with no matching live member as left', () => {
    const result = mergeSetView([dto({ sevenTvEmoteId: '01CCC' })], [], false);

    expect(result).toHaveLength(1);
    expect(result[0].membership).toBe('left');
    expect(result[0].slotCount).toBe(1);
  });

  it('groups two live entries of the same sevenTvEmoteId into one cell with slotCount 2 and both aliases', () => {
    const result = mergeSetView(
      [dto({ sevenTvEmoteId: '01DDD' })],
      [
        liveRow({ sevenTvEmoteId: '01DDD', name: 'AliasOne' }),
        liveRow({ sevenTvEmoteId: '01DDD', name: 'AliasTwo' }),
      ],
      false,
    );

    expect(result).toHaveLength(1);
    expect(result[0].membership).toBe('live');
    expect(result[0].slotCount).toBe(2);
    expect(result[0].aliases).toEqual(['AliasOne', 'AliasTwo']);
  });

  it('reports every row as live with slotCount 1 in the active view, ignoring live entirely', () => {
    const result = mergeSetView(
      [dto({ sevenTvEmoteId: '01EEE' }), dto({ sevenTvEmoteId: '01FFF', emoteName: 'Second' })],
      null,
      true,
    );

    expect(result).toHaveLength(2);
    expect(result.every((row) => row.membership === 'live')).toBe(true);
    expect(result.every((row) => row.slotCount === 1)).toBe(true);
  });

  it('sets aliases to [emoteName] for a row with no live entry', () => {
    const result = mergeSetView([dto({ emoteName: 'LonelyEmote' })], [], false);

    expect(result[0].aliases).toEqual(['LonelyEmote']);
  });

  it('passes nameTwinEmoteSetIds through from the counted row', () => {
    const result = mergeSetView(
      [dto({ sevenTvEmoteId: '01GGG', nameTwinEmoteSetIds: ['01HALLOWEEN'] })],
      [liveRow({ sevenTvEmoteId: '01GGG' })],
      false,
    );

    expect(result[0].nameTwinEmoteSetIds).toEqual(['01HALLOWEEN']);
  });

  it('keeps output order a stable function of input: totals order first, then live-only appended in encounter order', () => {
    const result = mergeSetView(
      [
        dto({ sevenTvEmoteId: '01ZZZ', emoteName: 'RowB' }),
        dto({ sevenTvEmoteId: '01AAA', emoteName: 'RowA' }),
      ],
      [
        liveRow({ sevenTvEmoteId: '01YYY', name: 'LiveOnlyFirst' }),
        liveRow({ sevenTvEmoteId: '01AAA', name: 'RowA' }),
        liveRow({ sevenTvEmoteId: '01XXX', name: 'LiveOnlySecond' }),
      ],
      false,
    );

    expect(result.map((row) => row.sevenTvEmoteId)).toEqual(['01ZZZ', '01AAA', '01YYY', '01XXX']);
  });

  it('returns an empty list for empty inputs', () => {
    expect(mergeSetView([], [], false)).toEqual([]);
    expect(mergeSetView([], null, true)).toEqual([]);
  });
});
