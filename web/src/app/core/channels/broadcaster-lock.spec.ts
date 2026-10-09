import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';

import { readBroadcasterLock } from './broadcaster-lock';

function failure(status: number, error: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error });
}

describe('readBroadcasterLock', () => {
  it('returns the lock time of an admin 409', () => {
    const lock = readBroadcasterLock(
      failure(409, {
        errorCode: 'channel_locked_by_broadcaster',
        lockedAtUtc: '2026-10-01T10:00:00Z',
      }),
    );
    expect(lock).toEqual({ lockedAtUtc: '2026-10-01T10:00:00Z' });
  });

  it('returns null for the final 403 a moderator gets for the same code', () => {
    expect(
      readBroadcasterLock(failure(403, { errorCode: 'channel_locked_by_broadcaster' })),
    ).toBeNull();
  });

  it('returns null for other 409 codes, a missing time and non-HTTP errors', () => {
    expect(readBroadcasterLock(failure(409, { errorCode: 'channel_capacity_reached' }))).toBeNull();
    expect(
      readBroadcasterLock(failure(409, { errorCode: 'channel_locked_by_broadcaster' })),
    ).toBeNull();
    expect(readBroadcasterLock(failure(409, null))).toBeNull();
    expect(readBroadcasterLock(new Error('boom'))).toBeNull();
  });
});
