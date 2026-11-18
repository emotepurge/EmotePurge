import { describe, expect, it } from 'vitest';

import { wishReasonKey, wishRemainingPart, wishStatusKey, wishStatusTone } from './wish-labels';

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
