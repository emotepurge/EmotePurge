import { describe, expect, it } from 'vitest';

import { ForeignEmoteRow } from '../seven-tv/foreign-emote-set.model';
import { mergeSetView } from './merge-set-view';
import { EmoteUsageTotal, EmoteUsageTotalDto } from './usage-stat.model';

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
    slotId: null,
    firstSeenAtFromRest: false,
    siblingNames: [],
    firstSeenAtShared: false,
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
        slotId: null,
        slotKey: '01BBB',
        siblingNames: [],
        firstSeenAtFromRest: false,
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

/** A counted slot-mode row with an authoritative date (plan 377, 2.4 step 2). */
function slotDto(
  slotId: string,
  emoteName: string,
  overrides: Partial<EmoteUsageTotalDto> = {},
): EmoteUsageTotalDto {
  return dto({
    slotId,
    emoteName,
    sevenTvEmoteId: '01SLOT',
    firstSeenAt: '2026-10-01T12:34:56.780Z',
    firstSeenAtFromRest: true,
    ...overrides,
  });
}

/** A live entry of the slot fixtures' emote. */
function slotEntry(name: string, addedAt: string | null | undefined): ForeignEmoteRow {
  return liveRow({ sevenTvEmoteId: '01SLOT', name, defaultName: 'Base', addedAt });
}

const T = '2026-10-01T12:34:56.780Z';
const T_SHORT = '2026-10-01T12:34:56.78Z';
const LATER = '2026-10-05T08:00:00.000Z';

function byName(rows: readonly EmoteUsageTotal[], name: string): EmoteUsageTotal {
  const row = rows.find((candidate) => candidate.emoteName === name);
  if (!row) {
    throw new Error(`no row named ${name}`);
  }
  return row;
}

describe('mergeSetView per slot (plan 377)', () => {
  it('keys a slot-mode row of the active view by its slotId and takes siblingNames from the DTO', () => {
    const result = mergeSetView(
      [
        slotDto('slot-ome', 'ome44', { siblingNames: ['hob44'] }),
        slotDto('slot-hob', 'hob44', { siblingNames: ['ome44'] }),
        dto({ sevenTvEmoteId: '01EMOTE', emoteName: 'Plain' }),
      ],
      null,
      true,
    );

    expect(result.map((row) => row.slotKey)).toEqual(['slot-ome', 'slot-hob', '01EMOTE']);
    expect(result.map((row) => row.slotId)).toEqual(['slot-ome', 'slot-hob', null]);
    expect(result.map((row) => row.siblingNames)).toEqual([['hob44'], ['ome44'], []]);
    expect(result[0].firstSeenAtFromRest).toBe(true);
    expect(result.every((row) => row.membership === 'live' && row.slotCount === 1)).toBe(true);
  });

  it('joins a dated slot row by instant even when the two sides spell the milliseconds differently', () => {
    const result = mergeSetView(
      [slotDto('slot-ome', 'ome44', { firstSeenAt: T, totalUseCount: 7 })],
      [slotEntry('ome44', T_SHORT)],
      false,
    );

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      slotKey: 'slot-ome',
      membership: 'live',
      totalUseCount: 7,
      slotCount: 1,
      aliases: ['ome44'],
      siblingNames: [],
    });
  });

  it('binds by name inside a group of two live entries sharing one instant', () => {
    const result = mergeSetView(
      [slotDto('slot-hob', 'hob44', { totalUseCount: 3, firstSeenAtShared: true })],
      [slotEntry('ome44', T), slotEntry('hob44', T)],
      false,
    );

    expect(byName(result, 'hob44')).toMatchObject({
      slotKey: 'slot-hob',
      membership: 'live',
      totalUseCount: 3,
    });
    expect(byName(result, 'ome44')).toMatchObject({
      emoteId: null,
      totalUseCount: null,
      membership: 'live',
    });
  });

  it('S2: never shows the other slot’s count when two dated rows share the instant of one live entry', () => {
    const totals = [
      slotDto('slot-a', 'A', { totalUseCount: 100, firstSeenAtShared: true }),
      slotDto('slot-b', 'B', { totalUseCount: 3, firstSeenAtShared: true }),
    ];
    const live = [slotEntry('B', T)];

    const result = mergeSetView(totals, live, false);

    expect(result).toHaveLength(2);
    expect(result.find((row) => row.slotKey === 'slot-b')).toMatchObject({
      membership: 'live',
      totalUseCount: 3,
    });
    expect(result.find((row) => row.slotKey === 'slot-a')).toMatchObject({
      membership: 'left',
      totalUseCount: 100,
    });
  });

  it('S2: gives the same bindings whatever order the DTOs come in', () => {
    const forward = mergeSetView(
      [
        slotDto('slot-a', 'A', { totalUseCount: 100, firstSeenAtShared: true }),
        slotDto('slot-b', 'B', { totalUseCount: 3, firstSeenAtShared: true }),
      ],
      [slotEntry('B', T)],
      false,
    );
    const reversed = mergeSetView(
      [
        slotDto('slot-b', 'B', { totalUseCount: 3, firstSeenAtShared: true }),
        slotDto('slot-a', 'A', { totalUseCount: 100, firstSeenAtShared: true }),
      ],
      [slotEntry('B', T)],
      false,
    );

    const summary = (rows: EmoteUsageTotal[]) =>
      rows.map((row) => [row.slotKey, row.membership, row.totalUseCount]).sort();
    expect(summary(reversed)).toEqual(summary(forward));
  });

  it('S2: binds nothing when a tie on both sides has only renamed members', () => {
    const result = mergeSetView(
      [
        slotDto('slot-a', 'A', { firstSeenAtShared: true }),
        slotDto('slot-b', 'B', { firstSeenAtShared: true }),
      ],
      [slotEntry('C', T), slotEntry('D', T)],
      false,
    );

    expect(result.filter((row) => row.slotId !== null).map((row) => row.membership)).toEqual([
      'left',
      'left',
    ]);
    const klasse3 = result.filter((row) => row.emoteId === null);
    expect(klasse3.map((row) => row.emoteName)).toEqual(['C', 'D']);
    expect(klasse3.every((row) => row.membership === 'live' && row.totalUseCount === null)).toBe(
      true,
    );
  });

  it('binds a single dated row to a single live entry of its instant regardless of the name (rename)', () => {
    const result = mergeSetView(
      [slotDto('slot-a', 'A', { totalUseCount: 9, firstSeenAtShared: false })],
      [slotEntry('C', T)],
      false,
    );

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      slotKey: 'slot-a',
      membership: 'live',
      emoteName: 'C',
      totalUseCount: 9,
      aliases: ['C'],
    });
  });

  it('F6: binds only by name when the Api flags the instant as shared with a sibling outside the view', () => {
    const result = mergeSetView(
      [slotDto('slot-a', 'A', { totalUseCount: 100, firstSeenAtShared: true })],
      [slotEntry('B', T)],
      false,
    );

    expect(result).toHaveLength(2);
    expect(result[0]).toMatchObject({
      slotKey: 'slot-a',
      membership: 'left',
      emoteName: 'A',
      totalUseCount: 100,
    });
    expect(result[1]).toMatchObject({
      emoteName: 'B',
      emoteId: null,
      totalUseCount: null,
      membership: 'live',
    });
  });

  it('F6: reads an absent firstSeenAtShared (older Api) as shared and binds only by name', () => {
    const olderDto: Partial<EmoteUsageTotalDto> = slotDto('slot-a', 'A', { totalUseCount: 100 });
    delete olderDto.firstSeenAtShared;

    const result = mergeSetView([olderDto as EmoteUsageTotalDto], [slotEntry('B', T)], false);

    expect(result[0]).toMatchObject({ slotKey: 'slot-a', membership: 'left', totalUseCount: 100 });
    expect(result[1]).toMatchObject({ emoteName: 'B', totalUseCount: null });
  });

  it('binds a provisional slot row to the live entry of its name, dated or not', () => {
    const result = mergeSetView(
      [
        slotDto('slot-p', 'prov', {
          firstSeenAt: null,
          firstSeenAtFromRest: false,
          totalUseCount: 4,
        }),
        slotDto('slot-q', 'prov2', {
          firstSeenAt: '2026-09-01T00:00:00Z',
          firstSeenAtFromRest: false,
        }),
      ],
      [slotEntry('prov', LATER), slotEntry('prov2', null)],
      false,
    );

    expect(result).toHaveLength(2);
    expect(result.map((row) => [row.slotKey, row.membership])).toEqual([
      ['slot-p', 'live'],
      ['slot-q', 'live'],
    ]);
    expect(result[0].totalUseCount).toBe(4);
  });

  it('binds a dated row without an instant match to an undated live entry of the same name', () => {
    const result = mergeSetView(
      [slotDto('slot-a', 'A', { totalUseCount: 5 })],
      [slotEntry('A', undefined)],
      false,
    );

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({ slotKey: 'slot-a', membership: 'live', totalUseCount: 5 });
  });

  it('leaves a dated row whose namesake entry is dated at another instant, and keeps its DTO siblings', () => {
    const result = mergeSetView(
      [slotDto('slot-a', 'A', { totalUseCount: 5, siblingNames: ['Z'] })],
      [slotEntry('A', LATER)],
      false,
    );

    expect(result).toHaveLength(2);
    expect(result[0]).toMatchObject({ slotKey: 'slot-a', membership: 'left', siblingNames: ['Z'] });
    expect(result[1]).toMatchObject({
      emoteName: 'A',
      emoteId: null,
      slotKey: `live:01SLOT#${LATER}`,
      membership: 'live',
    });
  });

  it('turns every uncounted entry into its own card in a slot response, keyed by instant or name', () => {
    const result = mergeSetView(
      [slotDto('slot-ome', 'ome44', { totalUseCount: 12 })],
      [
        slotEntry('zzUndated', null),
        slotEntry('hob44', LATER),
        slotEntry('ome44', T),
        slotEntry('aaUndated', undefined),
      ],
      false,
    );

    expect(result.map((row) => row.slotKey)).toEqual([
      'slot-ome',
      'live:01SLOT##zzUndated',
      `live:01SLOT#${LATER}`,
      'live:01SLOT##aaUndated',
    ]);
    const klasse3 = result.slice(1);
    expect(
      klasse3.every(
        (row) =>
          row.emoteId === null &&
          row.slotId === null &&
          row.totalUseCount === null &&
          row.firstSeenAt === null &&
          row.firstSeenAtFromRest === false &&
          row.slotCount === 1 &&
          row.aliases.length === 1 &&
          row.aliases[0] === row.emoteName,
      ),
    ).toBe(true);
    // D2 order: add date ascending, undated last, then name ordinal — never the row's own name.
    expect(byName(result, 'ome44').siblingNames).toEqual(['hob44', 'aaUndated', 'zzUndated']);
    expect(byName(result, 'hob44').siblingNames).toEqual(['ome44', 'aaUndated', 'zzUndated']);
    expect(byName(result, 'zzUndated').siblingNames).toEqual(['ome44', 'hob44', 'aaUndated']);
  });

  it('S1: gives two uncounted entries sharing one instant distinct, name-qualified keys', () => {
    const result = mergeSetView(
      [
        dto({
          sevenTvEmoteId: '01OTHER',
          emoteName: 'Other',
          slotId: 'slot-other',
          firstSeenAt: LATER,
          firstSeenAtFromRest: true,
        }),
      ],
      [
        liveRow({ sevenTvEmoteId: '01OTHER', name: 'Other', addedAt: LATER }),
        slotEntry('first', T),
        slotEntry('second', T),
      ],
      false,
    );

    expect(result.map((row) => row.slotKey)).toEqual([
      'slot-other',
      `live:01SLOT#${T}#first`,
      `live:01SLOT#${T}#second`,
    ]);
  });

  it('S1: keeps the name-qualified key of a tie partner when the other partner is bound', () => {
    const result = mergeSetView(
      [slotDto('slot-first', 'first', { firstSeenAtShared: true })],
      [slotEntry('first', T), slotEntry('second', T)],
      false,
    );

    expect(result.map((row) => row.slotKey)).toEqual(['slot-first', `live:01SLOT#${T}#second`]);
  });

  it('lets an emote-mode row in a slot response consume every entry of its id, with no Klasse 3 beside it', () => {
    const result = mergeSetView(
      [
        slotDto('slot-ome', 'ome44'),
        dto({ sevenTvEmoteId: '01SEED', emoteName: 'Seeded', totalUseCount: 20 }),
      ],
      [
        slotEntry('ome44', T),
        liveRow({ sevenTvEmoteId: '01SEED', name: 'SeedOne', addedAt: T }),
        liveRow({ sevenTvEmoteId: '01SEED', name: 'SeedTwo', addedAt: LATER }),
      ],
      false,
    );

    expect(result).toHaveLength(2);
    expect(result[1]).toMatchObject({
      slotKey: '01SEED',
      slotId: null,
      emoteName: 'SeedOne',
      membership: 'live',
      totalUseCount: 20,
      slotCount: 2,
      aliases: ['SeedOne', 'SeedTwo'],
      siblingNames: [],
    });
  });

  it('AK 35: keys every card of a mixed view uniquely', () => {
    const result = mergeSetView(
      [
        slotDto('slot-ome', 'ome44'),
        slotDto('slot-gone', 'gone', { firstSeenAt: LATER }),
        dto({ sevenTvEmoteId: '01SEED', emoteName: 'Seeded' }),
      ],
      [
        slotEntry('ome44', T),
        slotEntry('hob44', '2026-10-02T00:00:00.000Z'),
        slotEntry('tieOne', '2026-10-03T00:00:00.000Z'),
        slotEntry('tieTwo', '2026-10-03T00:00:00.000Z'),
        slotEntry('undated', null),
        liveRow({ sevenTvEmoteId: '01SEED', name: 'Seeded' }),
        liveRow({ sevenTvEmoteId: '01NEVER', name: 'NeverOne', addedAt: T }),
        liveRow({ sevenTvEmoteId: '01NEVER', name: 'NeverTwo', addedAt: T }),
      ],
      false,
    );

    const keys = result.map((row) => row.slotKey);
    expect(result).toHaveLength(9);
    expect(new Set(keys).size).toBe(keys.length);
    expect(result.every((row) => row.siblingNames.length === 0 || row.slotCount === 1)).toBe(true);
  });

  it('reads the slot fields of an older DTO without them as their off values', () => {
    const olderDto: Partial<EmoteUsageTotalDto> = dto({
      sevenTvEmoteId: '01OLD',
      emoteName: 'Old',
    });
    delete olderDto.slotId;
    delete olderDto.firstSeenAtFromRest;
    delete olderDto.siblingNames;
    delete olderDto.firstSeenAtShared;

    for (const [live, isActiveSet] of [
      [null, true],
      [[liveRow({ sevenTvEmoteId: '01OLD', name: 'Old' })], false],
    ] as const) {
      const [row] = mergeSetView([olderDto as EmoteUsageTotalDto], live, isActiveSet);
      expect(row).toMatchObject({
        slotId: null,
        slotKey: '01OLD',
        firstSeenAtFromRest: false,
        siblingNames: [],
        membership: 'live',
      });
    }
  });

  it('binds the same one of two unbound same-named slot rows whatever the order of totals', () => {
    const provisional = (slotId: string, useCount: number) =>
      slotDto(slotId, 'ome44', {
        firstSeenAt: null,
        firstSeenAtFromRest: false,
        totalUseCount: useCount,
      });
    const bound = (totals: EmoteUsageTotalDto[]) =>
      mergeSetView(totals, [slotEntry('ome44', LATER)], false)
        .filter((row) => row.membership === 'live' && row.slotId !== null)
        .map((row) => row.slotId);

    const a = provisional('slot-a', 1);
    const b = provisional('slot-b', 9);

    expect(bound([a, b])).toEqual(['slot-a']);
    expect(bound([b, a])).toEqual(['slot-a']);
  });
});
