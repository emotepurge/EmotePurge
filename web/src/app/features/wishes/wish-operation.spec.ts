import { describe, expect, it } from 'vitest';

import type { WishAddState } from '../../core/wishes/wish-add.service';
import type { WishRemovalState } from '../../core/wishes/wish-removal-tracker';
import {
  WISH_START_ABORT_KEY,
  WISH_START_NOTHING_CHANGED_KEY,
  type WishStartAbortReason,
} from '../../core/wishes/wish-run-start';
import {
  type WishOperationTone,
  wishAddOperationView,
  wishRemovalOperationView,
} from './wish-operation';

const ABORT_REASONS = Object.keys(WISH_START_ABORT_KEY) as WishStartAbortReason[];

describe('wishAddOperationView', () => {
  it('shows nothing without a state', () => {
    expect(wishAddOperationView(null)).toBeNull();
  });

  const cases: readonly [string, WishAddState, WishOperationTone, string][] = [
    ['starting', { kind: 'starting' }, 'progress', 'wishes.add.starting'],
    ['running', { kind: 'running', pauseSeconds: null }, 'progress', 'wishes.add.running'],
    ['added', { kind: 'added' }, 'done', 'wishes.add.added'],
    ['unknown', { kind: 'unknown' }, 'problem', 'wishes.add.unknown'],
    [
      'rejected nameTaken',
      { kind: 'rejected', reason: 'nameTaken', message: 'm' },
      'problem',
      'wishes.add.nameTaken',
    ],
    [
      'rejected tokenInvalid',
      { kind: 'rejected', reason: 'tokenInvalid', message: 'm' },
      'problem',
      'wishes.add.tokenInvalid',
    ],
  ];

  it.each(cases)('%s: tone, key, no lead, no retry', (_name, state, tone, key) => {
    const view = wishAddOperationView(state);
    expect(view).toMatchObject({ tone, key, leadKey: null });
    expect(view?.retryKey).toBeUndefined();
    expect(view?.params).toBeUndefined();
  });

  it('a pause carries its seconds as the paused sentence', () => {
    const view = wishAddOperationView({ kind: 'running', pauseSeconds: 7 });
    expect(view).toMatchObject({
      tone: 'progress',
      key: 'wishes.add.paused',
      params: { seconds: 7 },
    });
  });

  it('a pause of zero seconds is still a pause, not the plain running line', () => {
    const view = wishAddOperationView({ kind: 'running', pauseSeconds: 0 });
    expect(view?.key).toBe('wishes.add.paused');
    expect(view?.params).toEqual({ seconds: 0 });
  });

  it('another rejection passes the engine message through', () => {
    const view = wishAddOperationView({ kind: 'rejected', reason: 'other', message: 'boom' });
    expect(view).toMatchObject({
      tone: 'problem',
      key: 'wishes.add.rejected',
      params: { message: 'boom' },
    });
  });

  it.each(['notStarted', 'skipped'] as const)(
    '%s takes its sentence from the abort reason behind the nothing-changed lead',
    (kind) => {
      for (const reason of ABORT_REASONS) {
        const view = wishAddOperationView({ kind, reason });
        expect(view).toMatchObject({
          tone: 'problem',
          leadKey: WISH_START_NOTHING_CHANGED_KEY,
          key: WISH_START_ABORT_KEY[reason],
        });
        expect(view?.retryKey).toBeUndefined();
      }
    },
  );
});

describe('wishRemovalOperationView', () => {
  it('shows nothing without a state', () => {
    expect(wishRemovalOperationView(null)).toBeNull();
  });

  const cases: readonly [WishRemovalState['kind'], WishRemovalState, WishOperationTone, string][] =
    [
      ['starting', { kind: 'starting' }, 'progress', 'wishes.remove.starting'],
      ['running', { kind: 'running', pauseSeconds: null }, 'progress', 'wishes.remove.running'],
      ['cancelled', { kind: 'cancelled' }, 'progress', 'wishes.remove.cancelled'],
      ['reporting', { kind: 'reporting' }, 'progress', 'wishes.remove.reporting'],
      ['unknown', { kind: 'unknown' }, 'problem', 'wishes.remove.unknown'],
      ['booked', { kind: 'booked' }, 'done', 'wishes.remove.booked'],
      ['notBooked', { kind: 'notBooked' }, 'problem', 'wishes.remove.notBooked'],
    ];

  it.each(cases)('%s: tone, key, no lead, no retry', (_kind, state, tone, key) => {
    const view = wishRemovalOperationView(state);
    expect(view).toMatchObject({ tone, key, leadKey: null });
    expect(view?.retryKey).toBeUndefined();
    expect(view?.params).toBeUndefined();
  });

  it('a pause carries its seconds as the paused sentence', () => {
    const view = wishRemovalOperationView({ kind: 'running', pauseSeconds: 12 });
    expect(view).toMatchObject({
      tone: 'progress',
      key: 'wishes.remove.paused',
      params: { seconds: 12 },
    });
  });

  it('only the owed report offers a retry, and it is a problem', () => {
    const view = wishRemovalOperationView({ kind: 'reportPending' });
    expect(view?.tone).toBe('problem');
    expect(view?.retryKey).toBeTruthy();
    const others = [
      ...cases.map(([, state]) => state),
      { kind: 'failed', message: 'm' } as const,
      { kind: 'skipped', reason: { key: 'massDelete.memberRead.unavailable' } } as const,
      ...ABORT_REASONS.map((reason) => ({ kind: 'notStarted', reason }) as const),
    ] satisfies WishRemovalState[];
    for (const state of others) {
      expect(wishRemovalOperationView(state)?.retryKey).toBeUndefined();
    }
  });

  it('notBooked after a deletion says so with its own text', () => {
    expect(wishRemovalOperationView({ kind: 'notBooked', deleted: true })).toMatchObject({
      tone: 'problem',
      key: 'wishes.remove.notBookedDeleted',
      spokenByPage: true,
    });
    expect(wishRemovalOperationView({ kind: 'notBooked' })?.key).toBe('wishes.remove.notBooked');
  });

  it('the final states booked and notBooked offer no retry', () => {
    expect(wishRemovalOperationView({ kind: 'booked' })?.retryKey).toBeUndefined();
    expect(wishRemovalOperationView({ kind: 'notBooked' })?.retryKey).toBeUndefined();
  });

  it('notStarted takes its sentence from the abort reason behind the nothing-changed lead', () => {
    for (const reason of ABORT_REASONS) {
      expect(wishRemovalOperationView({ kind: 'notStarted', reason })).toMatchObject({
        tone: 'problem',
        leadKey: WISH_START_NOTHING_CHANGED_KEY,
        key: WISH_START_ABORT_KEY[reason],
      });
    }
  });

  it('skipped takes the key and params of its SkipReason behind a nothing-removed lead', () => {
    const view = wishRemovalOperationView({
      kind: 'skipped',
      reason: { key: 'usageStats.delete.renamedSinceLoad', params: { name: 'KEKW' } },
    });
    expect(view).toMatchObject({
      tone: 'problem',
      key: 'usageStats.delete.renamedSinceLoad',
      params: { name: 'KEKW' },
    });
    expect(view?.leadKey).toBeTruthy();
    expect(view?.leadKey).not.toBe(WISH_START_NOTHING_CHANGED_KEY);
  });

  it('skipped without params leaves params out', () => {
    const view = wishRemovalOperationView({
      kind: 'skipped',
      reason: { key: 'massDelete.memberRead.truncated' },
    });
    expect(view?.key).toBe('massDelete.memberRead.truncated');
    expect(view && 'params' in view).toBe(false);
  });

  it('failed passes the message through behind the nothing-removed lead', () => {
    const view = wishRemovalOperationView({ kind: 'failed', message: 'refused' });
    expect(view).toMatchObject({ tone: 'problem', params: { message: 'refused' } });
    expect(view?.leadKey).toBeTruthy();
  });
});
