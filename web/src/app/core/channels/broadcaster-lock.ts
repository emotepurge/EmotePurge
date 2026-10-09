import { HttpErrorResponse } from '@angular/common/http';

/** The error code both the 409 (admin) and the 403 (everyone else) of a locked join carry. */
const BROADCASTER_LOCK_CODE = 'channel_locked_by_broadcaster';

/**
 * Reads the broadcaster lock off a failed join. Only the admin's **409** with a `lockedAtUtc` is a
 * lock the caller may lift after confirming; the 403 a moderator gets for the same code is final and
 * returns null here, so it flows on to the ordinary message mapping instead of opening a dialog.
 */
export function readBroadcasterLock(error: unknown): { lockedAtUtc: string } | null {
  if (!(error instanceof HttpErrorResponse) || error.status !== 409) {
    return null;
  }
  const body = error.error as { errorCode?: unknown; lockedAtUtc?: unknown } | null;
  if (body?.errorCode !== BROADCASTER_LOCK_CODE || typeof body.lockedAtUtc !== 'string') {
    return null;
  }
  return { lockedAtUtc: body.lockedAtUtc };
}
