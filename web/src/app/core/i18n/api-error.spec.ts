import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';

import { apiErrorCode, apiErrorTranslationKey } from './api-error';

function errorWith(status: number, body: unknown = null): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('apiErrorTranslationKey', () => {
  it('prefers a recognized errorCode over the status', () => {
    expect(apiErrorTranslationKey(errorWith(409, { errorCode: 'vote_session_ended' }))).toBe(
      'errors.api.vote_session_ended',
    );
  });

  it('falls back to the status for an unrecognized errorCode', () => {
    expect(apiErrorTranslationKey(errorWith(404, { errorCode: 'something_new' }))).toBe(
      'errors.status.notFound',
    );
  });

  it('renders the rate limiter’s own 429 through its error code', () => {
    // Since 2026-08-29 the limiter sends a body. The status fallback below still exists, and now
    // means specifically "a 429 that did not come from us" — nginx, Cloudflare, a proxy in between.
    expect(
      apiErrorTranslationKey(
        errorWith(429, { errorCode: 'rate_limit_exceeded', retryAfterSeconds: 42 }),
      ),
    ).toBe('errors.api.rate_limit_exceeded');
  });

  it('keeps the resync cooldown apart from the generic budget', () => {
    // Two different 429s with two different answers: "someone else's resync is running" versus
    // "your own minute is spent".
    expect(apiErrorTranslationKey(errorWith(429, { errorCode: 'resync_cooldown_active' }))).toBe(
      'errors.api.resync_cooldown_active',
    );
  });

  // The bodyless responses: all four authorization endpoint filters answer with a bare Forbid(),
  // Results.NotFound() without a body is used in three places, and the rate limiter sends nothing.
  it.each([
    [0, 'errors.status.offline'],
    [401, 'errors.status.unauthorized'],
    [403, 'errors.status.forbidden'],
    [404, 'errors.status.notFound'],
    [409, 'errors.status.conflict'],
    [429, 'errors.status.rateLimited'],
    [500, 'errors.status.server'],
    [503, 'errors.status.server'],
  ])('maps a bodyless %i to %s', (status, expected) => {
    expect(apiErrorTranslationKey(errorWith(status))).toBe(expected);
  });

  it('keeps errors.generic for statuses with no specific message', () => {
    expect(apiErrorTranslationKey(errorWith(418))).toBe('errors.generic');
  });
});

describe('apiErrorCode', () => {
  it('reads the errorCode of an API error body, known or not', () => {
    expect(apiErrorCode(errorWith(400, { errorCode: 'invalid_log_range' }))).toBe(
      'invalid_log_range',
    );
    expect(apiErrorCode(errorWith(409, { errorCode: 'something_new', extra: 1 }))).toBe(
      'something_new',
    );
  });

  it('is undefined without a body, for a foreign body and for a code that is not a string', () => {
    expect(apiErrorCode(errorWith(403))).toBeUndefined();
    expect(apiErrorCode(errorWith(0, new ProgressEvent('error')))).toBeUndefined();
    expect(apiErrorCode(errorWith(502, '<html>Bad gateway</html>'))).toBeUndefined();
    expect(apiErrorCode(errorWith(400, { errorCode: 42 }))).toBeUndefined();
  });
});
