import { describe, expect, it } from 'vitest';

import { EmoteSetSummary } from '../../core/seven-tv/seven-tv-emote-set.model';
import { selectableEmoteSets } from './emote-set-menu';

function set(overrides: Partial<EmoteSetSummary> = {}): EmoteSetSummary {
  return {
    id: 'set-1',
    name: 'Hauptset',
    capacity: 250,
    kind: 'NORMAL',
    isActive: true,
    isPersonal: false,
    ownerDisplayName: 'Owner',
    observations: [],
    ...overrides,
  };
}

describe('selectableEmoteSets', () => {
  it('keeps NORMAL sets', () => {
    const normal = set({ id: 'set-1', kind: 'NORMAL' });

    expect(selectableEmoteSets([normal])).toEqual([normal]);
  });

  it('hides a PERSONAL set entirely rather than showing it disabled (decision 2026-09-21)', () => {
    const personal = set({ id: 'set-2', kind: 'PERSONAL', isPersonal: true });

    expect(selectableEmoteSets([personal])).toEqual([]);
  });

  it('hides GLOBAL and SPECIAL sets too — this dropdown offers only NORMAL, unlike the source/target pickers', () => {
    const global = set({ id: 'set-3', kind: 'GLOBAL' });
    const special = set({ id: 'set-4', kind: 'SPECIAL' });

    expect(selectableEmoteSets([global, special])).toEqual([]);
  });

  it('keeps ordinal input order, filtering out only the non-NORMAL entries', () => {
    const a = set({ id: 'a', name: 'A', kind: 'NORMAL' });
    const personal = set({ id: 'p', kind: 'PERSONAL', isPersonal: true });
    const b = set({ id: 'b', name: 'B', kind: 'NORMAL' });

    expect(selectableEmoteSets([a, personal, b])).toEqual([a, b]);
  });

  it('returns an empty list for an account with nothing but non-NORMAL sets', () => {
    expect(selectableEmoteSets([set({ kind: 'PERSONAL', isPersonal: true })])).toEqual([]);
  });

  it('returns an empty list for an empty input', () => {
    expect(selectableEmoteSets([])).toEqual([]);
  });
});
