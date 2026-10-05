import { describe, expect, it } from 'vitest';

import type { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import type { EmoteTagEntry } from '../../core/tags/emote-tag.model';
import { buildTagImportSource, partitionTagPlayIn } from './tag-play-in';

function entry(id: string, patch: Partial<EmoteTagEntry> = {}): EmoteTagEntry {
  return {
    sevenTvEmoteId: id,
    alias: `alias-${id}`,
    imageUrl: `https://cdn.example/${id}/4x.webp`,
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

function live(aliased: string[], aliasless: string[] = []): SevenTvSetEntries {
  return {
    aliasesById: new Map([
      ...aliased.map((id): [string, string[]] => [id, [`n-${id}`]]),
      ...aliasless.map((id): [string, string[]] => [id, []]),
    ]),
    aliaslessIds: new Set(aliasless),
  } as unknown as SevenTvSetEntries;
}

describe('partitionTagPlayIn', () => {
  it('splits against aliasesById and aliaslessIds, keeping entry order', () => {
    const entries = [entry('a'), entry('b'), entry('c'), entry('d'), entry('e')];
    const p = partitionTagPlayIn(entries, live(['b'], ['d']));
    expect(p.toAdd.map((r) => r.sevenTvEmoteId)).toEqual(['a', 'c', 'e']);
    expect(p.alreadyInSetIds).toEqual(['b', 'd']);
  });

  it('counts an id held only under an aliasless entry (aliaslessIds alone) as in the set', () => {
    const l = {
      aliasesById: new Map(),
      aliaslessIds: new Set(['x']),
    } as unknown as SevenTvSetEntries;
    const p = partitionTagPlayIn([entry('x')], l);
    expect(p.toAdd).toEqual([]);
    expect(p.alreadyInSetIds).toEqual(['x']);
  });

  it('maps a row as name = alias and the entry image', () => {
    const p = partitionTagPlayIn([entry('a')], live([]));
    expect(p.toAdd).toEqual([
      { sevenTvEmoteId: 'a', name: 'alias-a', imageUrl: 'https://cdn.example/a/4x.webp' },
    ]);
  });

  it('yields an empty toAdd when everything is already in the set', () => {
    const p = partitionTagPlayIn([entry('a'), entry('b')], live(['a', 'b']));
    expect(p.toAdd).toEqual([]);
    expect(p.alreadyInSetIds).toEqual(['a', 'b']);
  });

  it('adds an entry flagged inSet that the live read does not show (no fallback)', () => {
    const p = partitionTagPlayIn([entry('a', { inSet: true })], live([]));
    expect(p.toAdd.map((r) => r.sevenTvEmoteId)).toEqual(['a']);
    expect(p.alreadyInSetIds).toEqual([]);
  });
});

describe('buildTagImportSource', () => {
  it('builds the tag origin with alreadyInSetCount and nothing collapsed', () => {
    const p = partitionTagPlayIn([entry('a'), entry('b'), entry('c')], live(['b', 'c']));
    const source = buildTagImportSource(p, { id: 7, name: 'Stronghold' }, 'somechannel');
    expect(source.origin).toEqual({
      kind: 'tag',
      tagId: 7,
      tagName: 'Stronghold',
      channelName: 'somechannel',
      alreadyInSetCount: 2,
    });
    expect(source.rows).toBe(p.toAdd);
    expect(source.duplicatesCollapsed).toBe(0);
    expect(source.discardedRows).toBe(0);
  });
});
