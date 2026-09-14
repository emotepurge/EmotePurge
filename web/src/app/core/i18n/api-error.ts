import { HttpErrorResponse } from '@angular/common/http';

// Mirrors src/EmotePurge.Api/Validation/ApiErrorCodes.cs — the API returns only these stable,
// language-neutral codes (never prose), so translation happens exactly once, here.
//
// Exported so api-error-locales.spec.ts can assert that both locale files carry every entry. That
// mirror used to be maintained by hand and had already drifted: no_health_data and
// health_data_unreadable existed server-side and in neither locale, and silently fell back to the
// generic status message.
export const KNOWN_API_ERROR_CODES = new Set([
  'invalid_channel_name',
  'invalid_channel_or_session_id',
  'emote_ids_empty',
  'emote_ids_invalid',
  'invalid_date_format',
  'from_after_to',
  'range_too_large',
  'invalid_oauth_state',
  'twitch_token_exchange_failed',
  'twitch_user_info_unavailable',
  'vote_session_title_empty',
  'vote_session_roles_empty',
  'vips_not_supported',
  'started_at_in_future',
  'channel_not_joined',
  'emote_id_empty',
  'invalid_vote_type',
  'invalid_source_kind',
  'channel_not_found',
  'channel_not_on_twitch',
  'foreign_channel_twitch_unavailable',
  'foreign_channel_no_seventv_account',
  'foreign_channel_no_active_emote_set',
  'foreign_channel_seventv_unavailable',
  'invalid_leaderboard_sort',
  'vote_session_not_found',
  'vote_session_ended',
  'emote_not_eligible',
  'resync_cooldown_active',
  'rate_limit_exceeded',
  'live_stream_unavailable',
  'live_stream_quota_exhausted',
  'unexpected_error',
  'no_health_data',
  'health_data_unreadable',
]);

/**
 * The last-resort key {@link apiErrorTranslationKey} falls back to for an HTTP status it has no
 * specific message for. Exported so callers that also have to handle *non*-HTTP failures (a resource
 * whose stream threw something that is not an `HttpErrorResponse`) can land on the same message
 * instead of inventing a second generic key.
 */
export const GENERIC_ERROR_TRANSLATION_KEY = 'errors.generic';

/**
 * Resolves an HTTP error from the EmotePurge API to a translation key: `errors.api.<code>` for a
 * recognized `errorCode` body, otherwise a message derived from the status code.
 *
 * The status fallback exists because a large share of real failures carry no body at all — the four
 * authorization endpoint filters answer with a bare `Forbid()`, a dropped connection has status 0.
 * All of those used to collapse into "Etwas ist schiefgelaufen. Bitte versuch es erneut.", which
 * told a freshly promoted moderator to retry an action that could not succeed until the mod-role
 * cache expired.
 *
 * Our own rate limiter left the same gap until 2026-08-29 and now sends `rate_limit_exceeded` with a
 * `Retry-After`. `errors.status.rateLimited` therefore stays, but from here on it covers the 429s
 * that did *not* come from this API — nginx, Cloudflare, a proxy in between. Both keys carry the
 * same sentence on purpose: which service throttled is a diagnostic fact, not a different
 * instruction to the user.
 */
export function apiErrorTranslationKey(error: HttpErrorResponse): string {
  const code = (error.error as { errorCode?: string } | null)?.errorCode;
  if (code && KNOWN_API_ERROR_CODES.has(code)) {
    return `errors.api.${code}`;
  }
  return statusTranslationKey(error.status);
}

function statusTranslationKey(status: number): string {
  // 0 is Angular's marker for "the request never reached a server" (offline, DNS, CORS, aborted).
  if (status === 0) {
    return 'errors.status.offline';
  }
  if (status >= 500) {
    return 'errors.status.server';
  }
  switch (status) {
    case 401:
      return 'errors.status.unauthorized';
    case 403:
      return 'errors.status.forbidden';
    case 404:
      return 'errors.status.notFound';
    case 409:
      return 'errors.status.conflict';
    case 429:
      return 'errors.status.rateLimited';
    default:
      return GENERIC_ERROR_TRANSLATION_KEY;
  }
}
