import { describe, expect, it } from 'vitest';

import {
  countChannelStates,
  filterChannels,
  matchesChannelSearch,
  toChannelStateFilter,
} from './admin-channel-filter';

type Row = Parameters<typeof filterChannels>[0][number];

const row = (
  channelName: string,
  isBotActive: boolean,
  liveState: 'live' | 'offline' | 'unknown',
): Row => ({ channelName, isBotActive, liveState });

const FLEET = [
  row('HandOfBlood', true, 'live'),
  row('alpha', true, 'offline'),
  row('beta', true, 'unknown'),
  row('gone', false, 'live'),
  row('quiet', false, 'unknown'),
];

const names = (rows: Row[]) => rows.map((r) => r.channelName);

describe('filterChannels', () => {
  it('lists every channel under all, including active ones of unknown live state', () => {
    expect(names(filterChannels(FLEET, 'all', ''))).toHaveLength(5);
  });

  it('puts only active live channels under live', () => {
    expect(names(filterChannels(FLEET, 'live', ''))).toEqual(['HandOfBlood']);
  });

  it('puts only active offline channels under offline, never unknown ones', () => {
    expect(names(filterChannels(FLEET, 'offline', ''))).toEqual(['alpha']);
  });

  it('puts every non-active channel under inactive, whatever its live state', () => {
    expect(names(filterChannels(FLEET, 'inactive', ''))).toEqual(['gone', 'quiet']);
  });

  it('combines state and search', () => {
    expect(names(filterChannels(FLEET, 'inactive', 'qu'))).toEqual(['quiet']);
    expect(names(filterChannels(FLEET, 'live', 'alpha'))).toEqual([]);
  });

  it('matches case-insensitively and ignores surrounding whitespace', () => {
    expect(names(filterChannels(FLEET, 'all', '  HAND '))).toEqual(['HandOfBlood']);
    expect(matchesChannelSearch('handofblood', '   ')).toBe(true);
  });
});

describe('countChannelStates', () => {
  it('counts over all channels, independent of any search', () => {
    expect(countChannelStates(FLEET)).toEqual({ all: 5, live: 1, offline: 1, inactive: 2 });
  });

  it('reports live and offline as unknown when no active channel has a live state', () => {
    const counts = countChannelStates([row('a', true, 'unknown'), row('b', false, 'unknown')]);
    expect(counts).toEqual({ all: 2, live: null, offline: null, inactive: 1 });
  });

  it('keeps a true zero when nothing is active at all', () => {
    expect(countChannelStates([row('b', false, 'unknown')])).toEqual({
      all: 1,
      live: 0,
      offline: 0,
      inactive: 1,
    });
  });

  it('keeps a real zero when the live state is known but nothing matches', () => {
    const counts = countChannelStates([row('a', true, 'offline')]);
    expect(counts.live).toBe(0);
    expect(counts.offline).toBe(1);
  });
});

describe('toChannelStateFilter', () => {
  it.each(['all', 'live', 'offline', 'inactive'])('accepts %s', (state) => {
    expect(toChannelStateFilter(state)).toBe(state);
  });

  it.each(['', 'bogus', 'LIVE', null, undefined])('falls back to all for %s', (raw) => {
    expect(toChannelStateFilter(raw)).toBe('all');
  });
});
