import { HttpErrorResponse } from '@angular/common/http';
import { Subject } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import type { Wish, WishDetail, WishUsage } from '../../core/wishes/wish.model';
import {
  usageIsDue,
  usageSeries,
  usageWindowLabel,
  WISH_USAGE_RETRY_MS,
  WISH_USAGE_TTL_MS,
  WishUsageStore,
  wishUsageKey,
} from './wish-usage';

function wish(overrides: Partial<Wish> = {}): Wish {
  return {
    id: 7,
    sevenTvEmoteId: 'emote7',
    emoteName: 'name7',
    alias: 'alias7',
    source: 'manual',
    status: 'active',
    statusReason: null,
    requesterLogin: null,
    durationDays: 14,
    createdAtUtc: '2026-11-01T10:00:00Z',
    approvedAtUtc: '2026-11-01T10:00:00Z',
    startsAtUtc: '2026-11-02T10:00:00Z',
    startsAtAuthoritative: true,
    probationStartsAtUtc: null,
    expiresAtUtc: '2026-12-16T10:00:00Z',
    endedAtUtc: null,
    slotId: 'slot-7',
    slotAddedAtUtc: '2026-11-02T10:00:00Z',
    imageUrl: null,
    linkOrigin: 'auto',
    extensionCount: 0,
    permanent: false,
    botOwned: false,
    botWritePending: false,
    botNextAttemptAtUtc: null,
    removalQueuedAtUtc: null,
    mode: 'manual',
    ...overrides,
  };
}

function usage(total: number, overrides: Partial<WishUsage> = {}): WishUsage {
  return {
    windowFromUtc: '2026-11-11T12:00:00Z',
    windowToUtc: '2026-11-18T12:00:00Z',
    total,
    perDayMean: total / 7,
    rank: 2,
    rankOf: 9,
    coverageGap: false,
    daily: [
      { date: '2026-11-16', useCount: total - 1 },
      { date: '2026-11-17', useCount: 1 },
      { date: '2026-11-18', useCount: 0 },
    ],
    ...overrides,
  };
}

describe('usage key and due rule', () => {
  it('changes with every part of the key, and only with those', () => {
    const base = wishUsageKey(wish());
    expect(wishUsageKey(wish({ status: 'presented' }))).not.toBe(base);
    expect(wishUsageKey(wish({ expiresAtUtc: '2026-12-17T10:00:00Z' }))).not.toBe(base);
    expect(wishUsageKey(wish({ slotId: 'slot-8' }))).not.toBe(base);
    expect(wishUsageKey(wish({ slotAddedAtUtc: '2026-11-02T10:00:00.5Z' }))).not.toBe(base);
    expect(wishUsageKey(wish({ alias: 'other', extensionCount: 3 }))).toBe(base);
  });

  it('is due without an entry, for another key and from the expiry on', () => {
    const entry = { key: 'k', validUntil: 1000 };
    expect(usageIsDue(undefined, 'k', 0)).toBe(true);
    expect(usageIsDue(entry, 'other', 0)).toBe(true);
    expect(usageIsDue(entry, 'k', 999)).toBe(false);
    expect(usageIsDue(entry, 'k', 1000)).toBe(true);
  });
});

describe('window label and series', () => {
  it('names the window from its bounds: the start when it opens there, else the length', () => {
    const u = usage(5);
    expect(usageWindowLabel(u, '2026-11-11T12:00:00Z')).toEqual({ kind: 'sinceStart' });
    expect(usageWindowLabel(u, '2026-10-01T00:00:00Z')).toEqual({ kind: 'lastDays', days: 7 });
    expect(usageWindowLabel(u, null)).toEqual({ kind: 'lastDays', days: 7 });
  });

  it('hands the line exactly usage.daily, and none for one or two points', () => {
    const u = usage(5);
    expect(usageSeries(u)).toEqual(u.daily);
    expect(usageSeries(usage(5, { daily: u.daily?.slice(0, 2) }))).toBeNull();
    expect(usageSeries(usage(5, { daily: undefined }))).toBeNull();
    expect(usageSeries(usage(5, { daily: [] }))).toBeNull();
  });
});

describe('WishUsageStore', () => {
  let now: number;
  let active: boolean;
  let fetches: { id: number; answer: Subject<WishDetail> }[];
  let store: WishUsageStore;

  beforeEach(() => {
    now = 1_000_000;
    active = true;
    fetches = [];
    store = new WishUsageStore(
      (id) => {
        const answer = new Subject<WishDetail>();
        fetches.push({ id, answer });
        return answer;
      },
      () => now,
      () => active,
    );
  });

  function answer(index: number, value: WishUsage | null, w: Wish = wish()): void {
    fetches[index].answer.next({ ...w, usage: value });
  }

  it('loads nothing until the row is visible, then once', () => {
    const w = wish();
    store.sync([w]);
    store.tick();
    expect(fetches).toHaveLength(0);

    store.setVisible(w, true);
    store.setVisible(w, true);
    expect(fetches).toHaveLength(1);
    expect(fetches[0].id).toBe(7);
  });

  it('shows the numbers and keeps asking nothing inside the five minutes', () => {
    const w = wish();
    store.setVisible(w, true);
    answer(0, usage(10));
    expect(store.blockFor(w)?.usage?.total).toBe(10);

    now += WISH_USAGE_TTL_MS - 1;
    store.tick();
    store.sync([w]);
    expect(fetches).toHaveLength(1);
  });

  it('reloads a visible row after the expiry and shows the new numbers (same key)', () => {
    const w = wish();
    store.setVisible(w, true);
    answer(0, usage(10));

    now += WISH_USAGE_TTL_MS;
    store.tick();
    expect(fetches).toHaveLength(2);
    // Until the answer arrives the old value stands.
    expect(store.blockFor(w)?.usage?.total).toBe(10);
    answer(1, usage(25));
    expect(store.blockFor(w)?.usage?.total).toBe(25);
  });

  it('does not reload an invisible row after the expiry, but when it becomes visible', () => {
    const w = wish();
    store.setVisible(w, true);
    answer(0, usage(10));
    store.setVisible(w, false);

    now += WISH_USAGE_TTL_MS;
    store.tick();
    expect(fetches).toHaveLength(1);

    store.setVisible(w, true);
    expect(fetches).toHaveLength(2);
  });

  it('loads at once for another key, even only for another slotAddedAtUtc, and hides the old value', () => {
    const w = wish();
    store.setVisible(w, true);
    answer(0, usage(10));

    const moved = wish({ slotAddedAtUtc: '2026-11-03T10:00:00Z' });
    expect(store.blockFor(moved)).toBeNull();
    store.sync([moved]);
    expect(fetches).toHaveLength(2);
    answer(1, usage(3), moved);
    expect(store.blockFor(moved)?.usage?.total).toBe(3);
    expect(store.blockFor(w)).toBeNull();
  });

  it('lets every held value run out on expireAll and reloads only the visible rows', () => {
    const seen = wish({ id: 1 });
    const unseen = wish({ id: 2, slotId: 'slot-2' });
    store.setVisible(seen, true);
    store.setVisible(unseen, true);
    answer(0, usage(1), seen);
    answer(1, usage(2), unseen);
    store.setVisible(unseen, false);

    store.expireAll();
    expect(fetches.map((f) => f.id)).toEqual([1, 2, 1]);

    store.setVisible(unseen, true);
    expect(fetches.map((f) => f.id)).toEqual([1, 2, 1, 2]);
  });

  it('fetches nothing while the window is hidden and catches up on expireAll', () => {
    const w = wish();
    active = false;
    store.setVisible(w, true);
    expect(fetches).toHaveLength(0);

    active = true;
    store.expireAll();
    expect(fetches).toHaveLength(1);
  });

  it('keeps the old value while reloading and when the reload fails, and tries again soon', () => {
    const w = wish();
    store.setVisible(w, true);
    answer(0, usage(10));
    now += WISH_USAGE_TTL_MS;
    store.tick();

    fetches[1].answer.error(new HttpErrorResponse({ status: 500 }));
    expect(store.blockFor(w)?.usage?.total).toBe(10);

    store.tick();
    expect(fetches).toHaveLength(2);
    now += WISH_USAGE_RETRY_MS;
    store.tick();
    expect(fetches).toHaveLength(3);
  });

  it('shows nothing for a 404 and does not ask again inside the lifetime', () => {
    const w = wish();
    store.setVisible(w, true);
    fetches[0].answer.error(new HttpErrorResponse({ status: 404 }));

    expect(store.blockFor(w)).toBeNull();
    store.tick();
    expect(fetches).toHaveLength(1);
  });

  it('says "later" for a 429 and does not hammer', () => {
    const w = wish();
    store.setVisible(w, true);
    fetches[0].answer.error(new HttpErrorResponse({ status: 429 }));

    expect(store.blockFor(w)).toEqual({ state: 'limited', usage: null });
    store.tick();
    store.sync([w]);
    expect(fetches).toHaveLength(1);
  });

  it('counts only the youngest request of a row', () => {
    const w = wish();
    store.setVisible(w, true);
    const moved = wish({ slotId: 'slot-9' });
    store.sync([moved]);
    expect(fetches).toHaveLength(2);

    answer(1, usage(30), moved);
    answer(0, usage(99), w);
    expect(store.blockFor(moved)?.usage?.total).toBe(30);
  });

  it('forgets rows that left the screen and drops answers after a reset', () => {
    const w = wish();
    store.setVisible(w, true);
    store.sync([]);
    now += WISH_USAGE_TTL_MS;
    store.tick();
    expect(fetches).toHaveLength(1);

    store.reset();
    answer(0, usage(5));
    expect(store.blockFor(w)).toBeNull();
  });

  it('takes the usage of a loaded detail without a request, with the same lifetime', () => {
    const w = wish();
    store.seed(w, usage(8));
    store.setVisible(w, true);
    expect(fetches).toHaveLength(0);
    expect(store.blockFor(w)?.usage?.total).toBe(8);

    now += WISH_USAGE_TTL_MS;
    store.tick();
    expect(fetches).toHaveLength(1);
  });

  it('does not use the line and the numbers from different sources', () => {
    const w = wish();
    store.setVisible(w, true);
    const u = usage(12);
    answer(0, u);
    const block = store.blockFor(w);
    expect(block?.usage).toBe(u);
    expect(usageSeries(block?.usage as WishUsage)).toEqual(u.daily);
  });
});
