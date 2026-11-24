import { BotAccountState } from '../../core/admin/admin.model';

/** From this many days before `exp` the page warns. Matches the server's own warning window: the
 *  warning stays while the renewal has not worked, so it must not depend on anything else. */
export const BOT_TOKEN_EXPIRY_WARNING_DAYS = 7;

/** The text a global admin retypes to remove the stored token. A fixed word: the account has no
 *  name worth retyping, and the 7TV user id may not be known yet. */
export const BOT_TOKEN_DELETE_CONFIRM_TEXT = 'bot-account';

export type BotTokenExpiryNotice = 'expired' | 'soon' | null;

/** The status to show: the server's, except that a token whose `expiresAtUtc` is at or before `now`
 *  reads as `expired` even while the server still says `valid` (the worker that flips the status
 *  does not run while wishes are off). */
export function botTokenStatus(state: BotAccountState, now: Date = new Date()): string {
  if (state.tokenPresent && isPastExpiry(state, now)) {
    return 'expired';
  }
  return state.status;
}

/** `expired` once the status says so, `expiresAtUtc` is at or before now, or no whole day is left;
 *  `soon` inside the warning window. */
export function botTokenExpiryNotice(
  state: BotAccountState,
  now: Date = new Date(),
): BotTokenExpiryNotice {
  if (!state.tokenPresent) {
    return null;
  }
  if (botTokenStatus(state, now) === 'expired') {
    return 'expired';
  }
  if (state.expiresInDays !== null && state.expiresInDays <= BOT_TOKEN_EXPIRY_WARNING_DAYS) {
    return 'soon';
  }
  return null;
}

/** The send button is locked while the field is empty (whitespace only counts as empty) or a
 *  request is in flight. */
export function canSubmitBotToken(token: string, busy: boolean): boolean {
  return !busy && token.trim().length > 0;
}

function isPastExpiry(state: BotAccountState, now: Date): boolean {
  if (!state.expiresAtUtc) {
    return false;
  }
  const expires = Date.parse(state.expiresAtUtc);
  return !Number.isNaN(expires) && expires <= now.getTime();
}
