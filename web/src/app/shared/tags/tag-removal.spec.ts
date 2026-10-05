import { describe, expect, it } from 'vitest';

import type { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import type { EmoteTagEntry } from '../../core/tags/emote-tag.model';
import { proposeTagRemoval } from './tag-removal';

function entry(id: string, patch: Partial<EmoteTagEntry> = {}): EmoteTagEntry {
  return {
    sevenTvEmoteId: id,
    alias: `alias-${id}`,
    imageUrl: '',
    inSet: null,
    currentName: null,
    placedByThisTag: false,
    placedAtUtc: null,
    placementOperationId: null,
    heldByActiveTags: [],
    placedByOtherTags: [],
    ...patch,
  };
}

function own(id: string, patch: Partial<EmoteTagEntry> = {}): EmoteTagEntry {
  return entry(id, {
    placedByThisTag: true,
    placedAtUtc: '2026-10-01T10:00:00Z',
    placementOperationId: `op-${id}`,
    ...patch,
  });
}

function live(map: Record<string, string[]>, aliasless: string[] = []): SevenTvSetEntries {
  return {
    aliasesById: new Map(Object.entries(map)),
    aliaslessIds: new Set(aliasless),
  } as unknown as SevenTvSetEntries;
}

const T2 = { id: 2, name: 'Other' };

describe('proposeTagRemoval', () => {
  it('case 1: an entry not in the set is not listed but counted', () => {
    const p = proposeTagRemoval([own('a'), entry('b')], live({ a: ['x'] }));
    expect(p.rows.map((r) => r.sevenTvEmoteId)).toEqual(['a']);
    expect(p.notInSetCount).toBe(1);
  });

  it('case 2: own placement, held by nobody -> checked, placed, with date', () => {
    const [row] = proposeTagRemoval([own('a')], live({ a: ['x'] })).rows;
    expect(row).toMatchObject({
      checked: true,
      reason: 'placed',
      heldBy: [],
      placedAtUtc: '2026-10-01T10:00:00Z',
    });
  });

  it('case 3: own placement, held by an active tag -> unchecked, heldBy, with date', () => {
    const [row] = proposeTagRemoval(
      [own('a', { heldByActiveTags: [T2] })],
      live({ a: ['x'] }),
    ).rows;
    expect(row).toMatchObject({
      checked: false,
      reason: 'heldBy',
      heldBy: [T2],
      placedAtUtc: '2026-10-01T10:00:00Z',
    });
  });

  it('case 4: not own, placed by another tag -> unchecked, heldBy that tag, no date', () => {
    const [row] = proposeTagRemoval(
      [entry('a', { placedByOtherTags: [T2] })],
      live({ a: ['x'] }),
    ).rows;
    expect(row).toMatchObject({
      checked: false,
      reason: 'heldBy',
      heldBy: [T2],
      placedAtUtc: null,
    });
  });

  it('case 5: not own, nobody placed it -> unchecked, alreadyPresent', () => {
    const [row] = proposeTagRemoval([entry('a')], live({ a: ['x'] })).rows;
    expect(row).toMatchObject({
      checked: false,
      reason: 'alreadyPresent',
      heldBy: [],
      placedAtUtc: null,
    });
  });

  it('keeps entry order and treats an aliasless-only id as in the set', () => {
    const p = proposeTagRemoval([own('c'), own('a'), own('b')], live({ a: ['x'] }, ['b']));
    expect(p.rows.map((r) => r.sevenTvEmoteId)).toEqual(['a', 'b']);
    expect(p.notInSetCount).toBe(1);
  });

  it('snapshot holds every own placement with its revision, also those not in the set', () => {
    const p = proposeTagRemoval([own('a'), own('gone'), entry('c')], live({ a: ['x'], c: ['y'] }));
    expect(p.snapshot).toEqual([
      { sevenTvEmoteId: 'a', placementOperationId: 'op-a' },
      { sevenTvEmoteId: 'gone', placementOperationId: 'op-gone' },
    ]);
  });

  it('takes aliases from the live read', () => {
    const [row] = proposeTagRemoval([own('a')], live({ a: ['one', 'two'] })).rows;
    expect(row.aliases).toEqual(['one', 'two']);
  });

  it('ownInLiveIds are the own placements that are in the set (held ones included)', () => {
    const p = proposeTagRemoval(
      [own('a'), own('b', { heldByActiveTags: [T2] }), own('gone'), entry('d')],
      live({ a: ['x'], b: ['y'], d: ['z'] }),
    );
    expect(p.ownInLiveIds).toEqual(['a', 'b']);
  });
});
