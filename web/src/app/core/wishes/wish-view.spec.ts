import { describe, expect, it } from 'vitest';

import type { Wish } from './wish.model';
import {
  canRemove,
  endedDate,
  groupWishes,
  isConfirmedAction,
  isDue,
  isProbation,
  isSevenTvAction,
  needsAction,
  remainingTime,
  timeInSet,
  wishActions,
  type WishAction,
  type WishRuleInput,
} from './wish-view';

const NOW = new Date('2026-11-18T12:00:00Z');

function rule(over: Partial<WishRuleInput>): WishRuleInput {
  return {
    status: 'active',
    statusReason: null,
    slotId: 'slot-1',
    slotAddedAtUtc: '2026-11-01T00:00:00Z',
    expiresAtUtc: '2026-12-01T00:00:00Z',
    mode: 'manual',
    ...over,
  };
}

function wish(over: Partial<Wish>): Wish {
  return {
    id: 1,
    sevenTvEmoteId: 'e1',
    emoteName: 'KEKW',
    alias: 'KEKW',
    source: 'irc',
    status: 'active',
    statusReason: null,
    requesterLogin: null,
    durationDays: 30,
    createdAtUtc: '2026-11-01T00:00:00Z',
    approvedAtUtc: null,
    startsAtUtc: '2026-11-01T00:00:00Z',
    startsAtAuthoritative: true,
    probationStartsAtUtc: null,
    expiresAtUtc: '2026-12-01T00:00:00Z',
    endedAtUtc: null,
    slotId: 'slot-1',
    slotAddedAtUtc: '2026-11-01T00:00:00Z',
    imageUrl: null,
    linkOrigin: null,
    extensionCount: 0,
    permanent: false,
    botOwned: false,
    botWritePending: false,
    botNextAttemptAtUtc: null,
    removalQueuedAtUtc: null,
    mode: 'manual',
    ...over,
  };
}

describe('wishActions (the action table, AK35)', () => {
  interface Row {
    name: string;
    given: Partial<WishRuleInput>;
    expected: WishAction[];
  }

  const noSlot = { slotId: null, slotAddedAtUtc: null };
  const noExpiry = { expiresAtUtc: null };

  const table: Row[] = [
    { name: 'pending: neutral row', given: { status: 'pending' }, expected: [] },
    { name: 'pending, bot mode', given: { status: 'pending', mode: 'bot' }, expected: [] },

    {
      name: 'approved, manual',
      given: { status: 'approved', ...noSlot },
      expected: ['add7tv', 'adopt', 'edit', 'close'],
    },
    {
      name: 'approved, manual, with slot',
      given: { status: 'approved' },
      expected: ['add7tv', 'adopt', 'edit', 'close'],
    },
    {
      name: 'approved, bot mode: the bot works, nothing to do',
      given: { status: 'approved', mode: 'bot' },
      expected: [],
    },

    { name: 'unconfirmed: neutral row', given: { status: 'unconfirmed' }, expected: [] },

    {
      name: 'failed',
      given: { status: 'failed', ...noSlot },
      expected: ['retry', 'adopt', 'edit', 'close'],
    },
    {
      name: 'failed, with a reason',
      given: { status: 'failed', statusReason: 'set_full' },
      expected: ['retry', 'adopt', 'edit', 'close'],
    },

    { name: 'active', given: { status: 'active' }, expected: ['extend', 'keep', 'remove'] },
    {
      name: 'active, no expiry',
      given: { status: 'active', ...noExpiry },
      expected: ['keep', 'remove'],
    },
    {
      name: 'active, no slot',
      given: { status: 'active', ...noSlot },
      expected: ['extend', 'keep'],
    },
    {
      name: 'active, no expiry, no slot',
      given: { status: 'active', ...noExpiry, ...noSlot },
      expected: ['keep'],
    },

    {
      name: 'presented (expired), slot',
      given: { status: 'presented', statusReason: 'expired' },
      expected: ['extend', 'keep', 'remove'],
    },
    {
      name: 'presented (expired), no slot',
      given: { status: 'presented', statusReason: 'expired', ...noSlot },
      expected: ['extend', 'keep', 'close'],
    },
    {
      name: 'presented (expired), no slot, no expiry',
      given: { status: 'presented', statusReason: 'expired', ...noSlot, ...noExpiry },
      expected: ['keep', 'close'],
    },
    {
      name: 'presented (unconfirmed), slot',
      given: { status: 'presented', statusReason: 'unconfirmed' },
      expected: ['extend', 'keep', 'adopt', 'remove'],
    },
    {
      name: 'presented (unconfirmed), no slot',
      given: { status: 'presented', statusReason: 'unconfirmed', ...noSlot },
      expected: ['extend', 'keep', 'adopt', 'close'],
    },
    {
      name: 'presented (already_in_set), no slot, no expiry',
      given: { status: 'presented', statusReason: 'already_in_set', ...noSlot, ...noExpiry },
      expected: ['keep', 'adopt', 'close'],
    },
    {
      name: 'presented (protected), no slot',
      given: { status: 'presented', statusReason: 'protected', ...noSlot },
      expected: ['extend', 'keep', 'close'],
    },
    {
      name: 'presented, unknown reason changes nothing',
      given: { status: 'presented', statusReason: 'from_the_future' },
      expected: ['extend', 'keep', 'remove'],
    },
    {
      name: 'presented, no reason',
      given: { status: 'presented', statusReason: null, ...noSlot },
      expected: ['extend', 'keep', 'close'],
    },

    { name: 'removed', given: { status: 'removed' }, expected: ['reAdd'] },
    { name: 'removed_externally', given: { status: 'removed_externally' }, expected: ['reAdd'] },

    { name: 'kept', given: { status: 'kept' }, expected: [] },
    { name: 'closed', given: { status: 'closed' }, expected: [] },
    { name: 'rejected', given: { status: 'rejected' }, expected: [] },
    { name: 'expired', given: { status: 'expired' }, expected: [] },
    { name: 'merged', given: { status: 'merged' }, expected: [] },

    { name: 'unknown status', given: { status: 'quarantined' }, expected: [] },
    {
      name: 'unknown status with unknown reason',
      given: { status: 'quarantined', statusReason: 'x' },
      expected: [],
    },
  ];

  it.each(table)('$name', ({ given, expected }) => {
    expect(wishActions(rule(given))).toEqual(expected);
  });

  it('covers all thirteen known statuses', () => {
    const covered = new Set(table.map((row) => row.given.status));
    for (const status of [
      'pending',
      'approved',
      'unconfirmed',
      'failed',
      'active',
      'presented',
      'rejected',
      'expired',
      'removed',
      'removed_externally',
      'kept',
      'closed',
      'merged',
    ]) {
      expect(covered, status).toContain(status);
    }
  });

  it('never offers the destroying action before a building one', () => {
    for (const row of table) {
      const actions = wishActions(rule(row.given));
      const close = actions.indexOf('close');
      if (close >= 0) {
        expect(close, row.name).toBe(actions.length - 1);
      }
    }
  });

  it('marks exactly add7tv and remove as 7TV actions', () => {
    const all: WishAction[] = [
      'add7tv',
      'adopt',
      'edit',
      'close',
      'retry',
      'extend',
      'keep',
      'remove',
      'reAdd',
    ];
    expect(all.filter(isSevenTvAction)).toEqual(['add7tv', 'remove']);
  });

  it('asks for confirmation only for the two final actions', () => {
    const all: WishAction[] = [
      'add7tv',
      'adopt',
      'edit',
      'close',
      'retry',
      'extend',
      'keep',
      'remove',
      'reAdd',
    ];
    expect(all.filter(isConfirmedAction)).toEqual(['close', 'keep']);
  });
});

describe('needsAction', () => {
  it.each([
    ['pending', 'manual', true],
    ['pending', 'bot', true],
    ['failed', 'manual', true],
    ['presented', 'bot', true],
    ['approved', 'manual', true],
    ['approved', 'bot', false],
    ['active', 'manual', false],
    ['unconfirmed', 'manual', false],
    ['kept', 'manual', false],
    ['removed', 'manual', false],
    ['quarantined', 'manual', false],
  ])('%s in %s mode -> %s', (status, mode, expected) => {
    expect(needsAction({ status, mode })).toBe(expected);
  });
});

describe('isProbation (AK36: from the field only)', () => {
  it('is true from the field on, inclusive', () => {
    const w = { status: 'active', probationStartsAtUtc: '2026-11-18T12:00:00Z' };
    expect(isProbation(w, NOW)).toBe(true);
    expect(isProbation(w, new Date('2026-11-18T11:59:59Z'))).toBe(false);
  });

  it('is false without the field, however close the expiry is', () => {
    expect(isProbation({ status: 'active', probationStartsAtUtc: null }, NOW)).toBe(false);
  });

  it('is false for other statuses and for an unparseable date', () => {
    expect(
      isProbation({ status: 'presented', probationStartsAtUtc: '2026-01-01T00:00:00Z' }, NOW),
    ).toBe(false);
    expect(isProbation({ status: 'active', probationStartsAtUtc: 'garbage' }, NOW)).toBe(false);
  });
});

describe('isDue', () => {
  it('is true for presented with any reason or none', () => {
    expect(isDue({ status: 'presented', expiresAtUtc: null }, NOW)).toBe(true);
  });

  it('treats an active wish expiring exactly now as due', () => {
    expect(isDue({ status: 'active', expiresAtUtc: NOW.toISOString() }, NOW)).toBe(true);
    expect(isDue({ status: 'active', expiresAtUtc: '2026-11-18T12:00:01Z' }, NOW)).toBe(false);
  });

  it('is false for active without expiry and for other statuses', () => {
    expect(isDue({ status: 'active', expiresAtUtc: null }, NOW)).toBe(false);
    expect(isDue({ status: 'kept', expiresAtUtc: '2020-01-01T00:00:00Z' }, NOW)).toBe(false);
  });
});

describe('remainingTime', () => {
  const at = (iso: string) => remainingTime(iso, NOW);

  it('has none without a usable expiry', () => {
    expect(remainingTime(null, NOW)).toBeNull();
    expect(remainingTime('garbage', NOW)).toBeNull();
  });

  it('counts a begun day as a day from one day up', () => {
    expect(at('2026-11-19T12:00:00Z')).toEqual({ kind: 'days', count: 1 });
    expect(at('2026-11-19T12:00:01Z')).toEqual({ kind: 'days', count: 2 });
    expect(at('2026-11-21T11:59:59Z')).toEqual({ kind: 'days', count: 3 });
    expect(at('2026-12-18T11:59:59Z')).toEqual({ kind: 'days', count: 30 });
    expect(at('2026-12-18T12:00:00Z')).toEqual({ kind: 'days', count: 30 });
  });

  it('counts hours below a day, at least one', () => {
    expect(at('2026-11-19T11:59:59Z')).toEqual({ kind: 'hours', count: 23 });
    expect(at('2026-11-18T15:00:00Z')).toEqual({ kind: 'hours', count: 3 });
    expect(at('2026-11-18T12:00:01Z')).toEqual({ kind: 'hours', count: 1 });
  });

  it('is expired today exactly at expiry and within the first day after it', () => {
    expect(at('2026-11-18T12:00:00Z')).toEqual({ kind: 'expired_today' });
    expect(at('2026-11-17T12:00:01Z')).toEqual({ kind: 'expired_today' });
  });

  it('counts the days since expiry from one day on', () => {
    expect(at('2026-11-17T12:00:00Z')).toEqual({ kind: 'expired_days', count: 1 });
    expect(at('2026-11-10T00:00:00Z')).toEqual({ kind: 'expired_days', count: 8 });
  });
});

describe('timeInSet and endedDate', () => {
  it('flags a provisional start', () => {
    expect(
      timeInSet({ startsAtUtc: '2026-11-01T00:00:00Z', startsAtAuthoritative: false }),
    ).toEqual({
      sinceUtc: '2026-11-01T00:00:00Z',
      provisional: true,
    });
    expect(
      timeInSet({ startsAtUtc: '2026-11-01T00:00:00Z', startsAtAuthoritative: true })?.provisional,
    ).toBe(false);
  });

  it('prefers the slot arrival over a later start', () => {
    expect(
      timeInSet({
        startsAtUtc: '2026-11-10T00:00:00Z',
        startsAtAuthoritative: true,
        slotAddedAtUtc: '2026-11-01T00:00:00Z',
      }),
    ).toEqual({ sinceUtc: '2026-11-01T00:00:00Z', provisional: false });
    expect(
      timeInSet({
        startsAtUtc: '2026-11-10T00:00:00Z',
        startsAtAuthoritative: true,
        slotAddedAtUtc: null,
      })?.sinceUtc,
    ).toBe('2026-11-10T00:00:00Z');
  });

  it('flags a slot arrival 7TV has not confirmed yet as provisional', () => {
    expect(
      timeInSet({
        startsAtUtc: '2026-11-10T00:00:00Z',
        startsAtAuthoritative: false,
        slotAddedAtUtc: '2026-11-01T00:00:00Z',
      }),
    ).toEqual({ sinceUtc: '2026-11-01T00:00:00Z', provisional: true });
  });

  it('has no time in the set without a start', () => {
    expect(timeInSet({ startsAtUtc: null, startsAtAuthoritative: true })).toBeNull();
  });

  it('shows the UTC day of the end', () => {
    expect(endedDate({ endedAtUtc: '2026-11-17T23:59:59.5Z' })).toBe('2026-11-17');
    expect(endedDate({ endedAtUtc: null })).toBeNull();
    expect(endedDate({ endedAtUtc: 'garbage' })).toBeNull();
  });
});

describe('canRemove', () => {
  const full = { slotId: 's', slotAddedAtUtc: '2026-11-01T00:00:00Z', startsAtAuthoritative: true };

  it('needs slot, generation and an authoritative start', () => {
    expect(canRemove(full)).toBe(true);
    expect(canRemove({ ...full, slotId: null })).toBe(false);
    expect(canRemove({ ...full, slotAddedAtUtc: null })).toBe(false);
    expect(canRemove({ ...full, startsAtAuthoritative: false })).toBe(false);
  });
});

describe('groupWishes (my-wishes, AK42)', () => {
  const item = (id: number, over: Partial<Wish>) => ({ id, wish: wish({ id, ...over }) });
  const ids = (list: { id: number }[]) => list.map((entry) => entry.id);

  it('splits in two and keeps the server order inside each group', () => {
    const items = [
      item(1, { status: 'active', probationStartsAtUtc: '2026-11-10T00:00:00Z' }),
      item(2, { status: 'presented' }),
      item(3, { status: 'failed' }),
      item(4, { status: 'active', probationStartsAtUtc: '2026-11-11T00:00:00Z' }),
      item(5, { status: 'approved', mode: 'manual' }),
    ];
    const groups = groupWishes(items, (entry) => entry.wish, NOW);
    expect(ids(groups.actionNeeded)).toEqual([2, 3, 5]);
    expect(ids(groups.probation)).toEqual([1, 4]);
  });

  it('keeps what fits no group, at the end of probation', () => {
    const items = [
      item(1, { status: 'quarantined' }),
      item(2, { status: 'active', probationStartsAtUtc: '2026-11-10T00:00:00Z' }),
      item(3, { status: 'active', probationStartsAtUtc: '2027-01-01T00:00:00Z' }),
      item(4, { status: 'approved', mode: 'bot' }),
    ];
    const groups = groupWishes(items, (entry) => entry.wish, NOW);
    expect(ids(groups.actionNeeded)).toEqual([]);
    expect(ids(groups.probation)).toEqual([2, 1, 3, 4]);
  });

  it('loses nothing and handles an empty list', () => {
    expect(groupWishes([], (entry: { wish: Wish }) => entry.wish, NOW)).toEqual({
      actionNeeded: [],
      probation: [],
    });
  });
});
