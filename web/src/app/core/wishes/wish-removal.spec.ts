import { describe, expect, it } from 'vitest';

import { wishRemovalBlockKey } from './wish-removal';

const HELD = {
  slotId: 'slot-1',
  slotAddedAtUtc: '2026-11-02T10:00:00.123456Z',
  startsAtAuthoritative: true,
  alias: 'KEKW',
};

describe('wishRemovalBlockKey', () => {
  it('lets a wish with a confirmed, valid generation start', () => {
    expect(wishRemovalBlockKey(HELD)).toBeNull();
  });

  it.each([
    ['no slot', { slotId: null }, 'wishes.remove.blocked.noEntry'],
    ['no generation', { slotAddedAtUtc: null }, 'wishes.remove.blocked.noEntry'],
    [
      'a date 7TV has not confirmed',
      { startsAtAuthoritative: false },
      'wishes.remove.blocked.provisional',
    ],
    [
      'a date the report could not carry',
      { slotAddedAtUtc: '2026-02-31T10:00:00Z' },
      'wishes.remove.blocked.invalid',
    ],
    ['an empty alias', { alias: '' }, 'wishes.remove.blocked.invalid'],
  ])('locks the removal with its reason for %s', (_label, change, key) => {
    expect(wishRemovalBlockKey({ ...HELD, ...change })).toBe(key);
  });
});
