/**
 * Input checks of the wish forms (#481, K3): the emote link or id, the requester login, the
 * durations and the alias. Pure; the server re-checks everything, these only keep an obviously
 * invalid request from being sent and name the reason.
 */

export const WISH_DURATION_DAYS_MIN = 1;
export const WISH_DURATION_DAYS_MAX = 365;
export const WISH_PROBATION_LEAD_DAYS_MIN = 1;
export const WISH_PROBATION_LEAD_DAYS_MAX = 30;
export const WISH_ALIAS_MAX_LENGTH = 100;

/** 7TV hands out two id forms: a 24-character hex ObjectID and a 26-character ULID (Crockford base32). */
const OBJECT_ID = /^[0-9a-f]{24}$/i;
const ULID = /^[0-9A-HJKMNP-TV-Z]{26}$/i;
const SEVEN_TV_HOST = /^(?:www\.|old\.)?7tv\.(?:app|gg)$/i;
const SEVEN_TV_HOST_PREFIX = /^(?:www\.|old\.)?7tv\.(?:app|gg)\//i;
const LOGIN = /^[a-z0-9_]{4,25}$/;
const WHOLE_NUMBER = /^\d+$/;
const ALIAS_FORBIDDEN = /[\p{Cc}\p{White_Space}]/u;

export type EmoteInputResult =
  { ok: true; id: string } | { ok: false; reason: 'empty' | 'invalid' };

/**
 * An emote page link (`https://7tv.app/emotes/<id>`, with or without scheme, trailing slash or
 * query; the alternative domain too) or a bare id. A bare id counts only in the two forms 7TV
 * issues, so a name such as "KEKW" is never read as an id. The id keeps the case it was given.
 */
export function parseEmoteInput(raw: string): EmoteInputResult {
  const text = raw.trim();
  if (text === '') {
    return { ok: false, reason: 'empty' };
  }
  if (isSevenTvId(text)) {
    return { ok: true, id: text };
  }
  const id = idFromLink(text);
  return id === null ? { ok: false, reason: 'invalid' } : { ok: true, id };
}

function isSevenTvId(value: string): boolean {
  return OBJECT_ID.test(value) || ULID.test(value);
}

function idFromLink(text: string): string | null {
  const candidate = SEVEN_TV_HOST_PREFIX.test(text) ? `https://${text}` : text;
  let url: URL;
  try {
    url = new URL(candidate);
  } catch {
    return null;
  }
  if (
    (url.protocol !== 'https:' && url.protocol !== 'http:') ||
    !SEVEN_TV_HOST.test(url.hostname)
  ) {
    return null;
  }
  const segments = url.pathname.split('/').filter((segment, index, all) => {
    // Only the empty segments of a leading and a trailing slash are tolerated.
    return segment !== '' || (index !== 0 && index !== all.length - 1);
  });
  if (segments.length !== 2 || segments[0].toLowerCase() !== 'emotes') {
    return null;
  }
  return isSevenTvId(segments[1]) ? segments[1] : null;
}

export type RequesterLoginResult =
  { kind: 'none' } | { kind: 'ok'; login: string } | { kind: 'invalid' };

/** Trim, drop one leading "@", lower-case, then the server's pattern. Empty means "not given". */
export function normalizeRequesterLogin(raw: string): RequesterLoginResult {
  let text = raw.trim();
  if (text.startsWith('@')) {
    text = text.slice(1);
  }
  text = text.toLowerCase();
  if (text === '') {
    return { kind: 'none' };
  }
  return LOGIN.test(text) ? { kind: 'ok', login: text } : { kind: 'invalid' };
}

export type WholeDaysResult =
  { ok: true; value: number } | { ok: false; reason: 'empty' | 'not_integer' | 'out_of_range' };

/** A whole number of days within `[min, max]`, from a text field (`'1.5'`, `'1e2'` and `'-3'` are not) or a number. */
export function parseWholeDays(
  raw: string | number | null | undefined,
  min: number,
  max: number,
): WholeDaysResult {
  if (raw === null || raw === undefined) {
    return { ok: false, reason: 'empty' };
  }
  let value: number;
  if (typeof raw === 'number') {
    if (!Number.isInteger(raw)) {
      return { ok: false, reason: 'not_integer' };
    }
    value = raw;
  } else {
    const text = raw.trim();
    if (text === '') {
      return { ok: false, reason: 'empty' };
    }
    if (!WHOLE_NUMBER.test(text)) {
      return { ok: false, reason: 'not_integer' };
    }
    value = Number(text);
  }
  return value >= min && value <= max ? { ok: true, value } : { ok: false, reason: 'out_of_range' };
}

/** Duration of a wish, 1 to 365 days. */
export function parseWishDurationDays(raw: string | number | null | undefined): WholeDaysResult {
  return parseWholeDays(raw, WISH_DURATION_DAYS_MIN, WISH_DURATION_DAYS_MAX);
}

/** Probation lead of the channel settings, 1 to 30 days. */
export function parseProbationLeadDays(raw: string | number | null | undefined): WholeDaysResult {
  return parseWholeDays(raw, WISH_PROBATION_LEAD_DAYS_MIN, WISH_PROBATION_LEAD_DAYS_MAX);
}

export type WishAliasResult =
  { ok: true; alias: string } | { ok: false; reason: 'empty' | 'too_long' | 'forbidden_char' };

/**
 * An alias for a wish: 1 to 100 code points, no whitespace and no control characters. This is the
 * server's wish rule, not the import's 7TV blocklist (`isNameRejectedBySevenTv`), which tests other
 * characters; the text is not trimmed, a space inside it is an error, not something to fix silently.
 */
export function validateWishAlias(alias: string): WishAliasResult {
  if (alias.length === 0) {
    return { ok: false, reason: 'empty' };
  }
  if ([...alias].length > WISH_ALIAS_MAX_LENGTH) {
    return { ok: false, reason: 'too_long' };
  }
  return ALIAS_FORBIDDEN.test(alias)
    ? { ok: false, reason: 'forbidden_char' }
    : { ok: true, alias };
}
