import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { Observable } from 'rxjs';

import type { SparklinePoint } from '../../shared/emotes/usage-series';
import type { Wish, WishDetail, WishUsage } from '../../core/wishes/wish.model';

/** How long a loaded usage value stands (plan 2.4, Sol-R2-P2-6): it hangs on the clock, not the key. */
export const WISH_USAGE_TTL_MS = 5 * 60_000;

/** After a failed reload the old value stays and the next attempt is due after this long. */
export const WISH_USAGE_RETRY_MS = 60_000;

/** A line needs a shape: with one or two points only the numbers speak. */
const MIN_POINTS_FOR_LINE = 3;

/** What the row may show of a wish's usage. */
export interface WishUsageBlock {
  /** `limited`: the server answered 429, the row says "later". `none` is never handed to a row. */
  readonly state: 'ready' | 'limited';
  readonly usage: WishUsage | null;
}

interface UsageEntry {
  /** The wish key this value was loaded for; it counts only while the wish still has this key. */
  readonly key: string;
  readonly usage: WishUsage | null;
  readonly state: 'ready' | 'none' | 'limited';
  /** Epoch ms from which the value is due for a reload. */
  readonly validUntil: number;
}

type KeyedWish = Pick<Wish, 'id' | 'status' | 'expiresAtUtc' | 'slotId' | 'slotAddedAtUtc'>;

/** The identity a loaded usage value is valid for (plan 2.4); a change reloads at once. */
export function wishUsageKey(wish: KeyedWish): string {
  return JSON.stringify([
    wish.id,
    wish.status,
    wish.expiresAtUtc,
    wish.slotId,
    wish.slotAddedAtUtc,
  ]);
}

/** Whether a row with `key` needs a fetch now: nothing held, another key, or the value ran out. */
export function usageIsDue(
  entry: Pick<UsageEntry, 'key' | 'validUntil'> | undefined,
  key: string,
  nowMs: number,
): boolean {
  return entry === undefined || entry.key !== key || nowMs >= entry.validUntil;
}

/** The window the numbers describe, named from its bounds and never hard-wired (plan T5). */
export type UsageWindowLabel = { kind: 'sinceStart' } | { kind: 'lastDays'; days: number };

export function usageWindowLabel(
  usage: Pick<WishUsage, 'windowFromUtc' | 'windowToUtc'>,
  startsAtUtc: string | null,
): UsageWindowLabel {
  const from = Date.parse(usage.windowFromUtc);
  if (startsAtUtc !== null && Math.abs(Date.parse(startsAtUtc) - from) < 1000) {
    return { kind: 'sinceStart' };
  }
  const days = Math.round((Date.parse(usage.windowToUtc) - from) / 86_400_000);
  return { kind: 'lastDays', days: Math.max(1, days) };
}

/** Exactly `usage.daily`, the series of the window the numbers are computed over; `null` = no line. */
export function usageSeries(usage: WishUsage): SparklinePoint[] | null {
  const daily = usage.daily;
  if (!daily || daily.length < MIN_POINTS_FOR_LINE) {
    return null;
  }
  return daily.map((day) => ({ date: day.date, useCount: day.useCount }));
}

/**
 * Holds the usage of the visible open/running rows (plan 2.4, T5): one `GET /{id}` per row, only
 * once the row is in view and the window is visible, each value for at most five minutes and only
 * for the key it was loaded with. A value that runs out, a changed key, a reopened stream and a
 * window that becomes visible again each lead to a reload of the *visible* rows; an invisible row
 * loads when it becomes visible. While a reload is on the way, and after one that failed, the old
 * value stays (no flicker). Only the youngest request per row counts.
 */
export class WishUsageStore {
  private readonly entries = signal<ReadonlyMap<number, UsageEntry>>(new Map());
  private readonly visible = new Map<number, Wish>();
  private readonly inflight = new Map<number, { key: string; seq: number }>();
  private seq = 0;
  private generation = 0;

  constructor(
    private readonly fetch: (id: number) => Observable<WishDetail>,
    private readonly clock: () => number,
    /** False while the window is hidden: nothing is fetched then, (d) catches up on return. */
    private readonly active: () => boolean,
  ) {}

  /** What the row of `wish` shows: only a value that belongs to the wish's current key. */
  blockFor(wish: KeyedWish): WishUsageBlock | null {
    const entry = this.entries().get(wish.id);
    if (!entry || entry.key !== wishUsageKey(wish) || entry.state === 'none') {
      return null;
    }
    return { state: entry.state, usage: entry.usage };
  }

  /** The row came into view (or left it). */
  setVisible(wish: Wish, visible: boolean): void {
    if (visible) {
      this.visible.set(wish.id, wish);
      this.ensure(wish);
    } else {
      this.visible.delete(wish.id);
    }
  }

  /** The wishes now on screen: forget rows that are gone, hand the visible ones their newer state. */
  sync(displayed: readonly Wish[]): void {
    const byId = new Map(displayed.map((wish) => [wish.id, wish]));
    for (const id of [...this.visible.keys()]) {
      const wish = byId.get(id);
      if (wish) {
        this.visible.set(id, wish);
        this.ensure(wish);
      } else {
        this.visible.delete(id);
      }
    }
  }

  /** The clock tick: a visible row whose value ran out loads again. */
  tick(): void {
    this.visible.forEach((wish) => this.ensure(wish));
  }

  /** (c) and (d): every held value runs out at once; the visible rows load again. */
  expireAll(): void {
    this.entries.update((entries) => {
      const next = new Map<number, UsageEntry>();
      entries.forEach((entry, id) => next.set(id, { ...entry, validUntil: 0 }));
      return next;
    });
    this.inflight.clear();
    this.tick();
  }

  /** A value that came with a loaded wish (the pinned row's `GET /{id}`): no second request. */
  seed(wish: Wish, usage: WishUsage | null): void {
    this.inflight.delete(wish.id);
    this.store(wish.id, {
      key: wishUsageKey(wish),
      usage,
      state: usage ? 'ready' : 'none',
      validUntil: this.clock() + WISH_USAGE_TTL_MS,
    });
  }

  /** Another channel: nothing held, nothing visible, every answer on the way is outdated. */
  reset(): void {
    this.generation++;
    this.visible.clear();
    this.inflight.clear();
    this.entries.set(new Map());
  }

  private ensure(wish: Wish): void {
    if (!this.active()) {
      return;
    }
    const key = wishUsageKey(wish);
    if (this.inflight.get(wish.id)?.key === key) {
      return;
    }
    if (usageIsDue(this.entries().get(wish.id), key, this.clock())) {
      this.load(wish, key);
    }
  }

  private load(wish: Wish, key: string): void {
    const id = wish.id;
    const seq = ++this.seq;
    const generation = this.generation;
    this.inflight.set(id, { key, seq });
    const current = () => generation === this.generation && this.inflight.get(id)?.seq === seq;

    this.fetch(id).subscribe({
      next: (detail) => {
        if (!current()) {
          return;
        }
        this.inflight.delete(id);
        this.store(id, {
          key,
          usage: detail.usage,
          state: detail.usage ? 'ready' : 'none',
          validUntil: this.clock() + WISH_USAGE_TTL_MS,
        });
      },
      error: (error: unknown) => {
        if (!current()) {
          return;
        }
        this.inflight.delete(id);
        const status = error instanceof HttpErrorResponse ? error.status : 0;
        if (status === 404) {
          this.store(id, this.settled(key, null, 'none', WISH_USAGE_TTL_MS));
        } else if (status === 429) {
          this.store(id, this.settled(key, null, 'limited', WISH_USAGE_TTL_MS));
        } else {
          // The old value of the same key stays; the next attempt is due after a short while.
          const previous = this.entries().get(id);
          this.store(
            id,
            previous?.key === key
              ? { ...previous, validUntil: this.clock() + WISH_USAGE_RETRY_MS }
              : this.settled(key, null, 'none', WISH_USAGE_RETRY_MS),
          );
        }
      },
    });
  }

  private settled(
    key: string,
    usage: WishUsage | null,
    state: UsageEntry['state'],
    ttlMs: number,
  ): UsageEntry {
    return { key, usage, state, validUntil: this.clock() + ttlMs };
  }

  private store(id: number, entry: UsageEntry): void {
    this.entries.update((entries) => new Map(entries).set(id, entry));
  }
}
