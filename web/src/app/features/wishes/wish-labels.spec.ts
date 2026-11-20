import { describe, expect, it } from 'vitest';

import {
  wishDurationPart,
  wishPendingAgePart,
  wishReasonKey,
  wishRemainingPart,
  wishStatusKey,
  wishStatusTone,
} from './wish-labels';

const NOW = new Date('2026-11-18T12:00:00Z');
const DAY = 86_400_000;
const at = (offsetMs: number) => new Date(NOW.getTime() + offsetMs).toISOString();

describe('wishStatusKey / wishStatusTone', () => {
  it('names every known status and reads an unknown one as "unknown"', () => {
    expect(wishStatusKey({ status: 'removed_externally' })).toBe(
      'wishes.status.removed_externally',
    );
    expect(wishStatusKey({ status: 'something_new' })).toBe('wishes.status.unknown');
  });

  it('colours only the two states that ask for a decision', () => {
    expect(wishStatusTone({ status: 'presented' })).toBe('warning');
    expect(wishStatusTone({ status: 'failed' })).toBe('danger');
    expect(wishStatusTone({ status: 'active' })).toBe('neutral');
    expect(wishStatusTone({ status: 'something_new' })).toBe('neutral');
  });
});

describe('wishReasonKey', () => {
  it('explains the two known reasons and gives every other one the neutral line', () => {
    expect(wishReasonKey({ status: 'presented', statusReason: 'expired' })).toBe(
      'wishes.reason.expired',
    );
    expect(wishReasonKey({ status: 'failed', statusReason: 'unconfirmed_start' })).toBe(
      'wishes.reason.unconfirmed_start',
    );
    expect(wishReasonKey({ status: 'presented', statusReason: 'already_in_set' })).toBe(
      'wishes.reason.other',
    );
  });

  it('says the four hints of an open wish and nothing for an unknown or missing one', () => {
    for (const hint of ['unparsed', 'ambiguous_match', 'already_in_set', 'already_running']) {
      expect(wishReasonKey({ status: 'pending', statusReason: hint })).toBe(
        `wishes.pending.hint.${hint}`,
      );
    }
    expect(wishReasonKey({ status: 'pending', statusReason: 'something_new' })).toBeNull();
    expect(wishReasonKey({ status: 'pending', statusReason: null })).toBeNull();
  });

  it('has no line without a reason or outside presented and failed', () => {
    expect(wishReasonKey({ status: 'presented', statusReason: null })).toBeNull();
    expect(wishReasonKey({ status: 'active', statusReason: 'expired' })).toBeNull();
  });
});

describe('wishRemainingPart', () => {
  it('picks the plural key and carries the count', () => {
    expect(
      wishRemainingPart({ status: 'active', permanent: false, expiresAtUtc: at(3 * DAY) }, NOW),
    ).toEqual({
      key: 'wishes.row.remaining.days.other',
      params: { kind: 'days', count: 3 },
    });
    expect(
      wishRemainingPart(
        { status: 'presented', permanent: false, expiresAtUtc: at(-2 * DAY - 1000) },
        NOW,
      )?.key,
    ).toBe('wishes.row.remaining.expiredDays.other');
    expect(
      wishRemainingPart({ status: 'active', permanent: false, expiresAtUtc: at(-1000) }, NOW),
    ).toEqual({ key: 'wishes.row.remaining.expiredToday' });
  });

  it('shows nothing for a permanent wish, other states or a missing expiry', () => {
    expect(
      wishRemainingPart({ status: 'active', permanent: true, expiresAtUtc: at(DAY) }, NOW),
    ).toBeNull();
    expect(
      wishRemainingPart({ status: 'closed', permanent: false, expiresAtUtc: at(DAY) }, NOW),
    ).toBeNull();
    expect(
      wishRemainingPart({ status: 'active', permanent: false, expiresAtUtc: null }, NOW),
    ).toBeNull();
  });
});

describe('wishDurationPart', () => {
  it('names the run time as is while never extended', () => {
    expect(wishDurationPart({ permanent: false, durationDays: 7, extensionCount: 0 })).toEqual({
      key: 'wishes.row.duration.other',
      params: { count: 7 },
    });
  });

  it('names it the initial run time once extended', () => {
    expect(wishDurationPart({ permanent: false, durationDays: 1, extensionCount: 2 })).toEqual({
      key: 'wishes.row.initialDuration.one',
      params: { count: 1 },
    });
  });

  it('has none for a permanent wish', () => {
    expect(wishDurationPart({ permanent: true, durationDays: 7, extensionCount: 0 })).toBeNull();
  });
});

describe('wishPendingAgePart', () => {
  it('counts whole days since the redemption, "today" within the first one', () => {
    expect(wishPendingAgePart({ status: 'pending', createdAtUtc: at(-DAY + 1000) }, NOW)).toEqual({
      key: 'wishes.pending.age.today',
    });
    expect(wishPendingAgePart({ status: 'pending', createdAtUtc: at(-DAY) }, NOW)).toEqual({
      key: 'wishes.pending.age.days.one',
      params: { count: 1 },
    });
    expect(wishPendingAgePart({ status: 'pending', createdAtUtc: at(-3 * DAY - 5) }, NOW)).toEqual({
      key: 'wishes.pending.age.days.other',
      params: { count: 3 },
    });
  });

  it('is only for open wishes with a usable date', () => {
    expect(wishPendingAgePart({ status: 'approved', createdAtUtc: at(-DAY) }, NOW)).toBeNull();
    expect(wishPendingAgePart({ status: 'pending', createdAtUtc: 'garbage' }, NOW)).toBeNull();
  });
});
