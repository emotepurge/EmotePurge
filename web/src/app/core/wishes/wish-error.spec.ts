import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';

import { apiErrorTranslationKey } from '../i18n/api-error';
import { wishErrorKey, type WishErrorContext } from './wish-error';

const CONTEXTS: WishErrorContext[] = ['create', 'adopt', 'update', 're-add', 'action'];

function failure(status: number, body?: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('wishErrorKey', () => {
  it.each(CONTEXTS)('maps emote_ids_invalid to the wish text in context %s', (context) => {
    expect(wishErrorKey(failure(400, { errorCode: 'emote_ids_invalid' }), context)).toBe(
      'wishes.errors.emote_ids_invalid',
    );
  });

  it('maps invalid_request_body to the start-date text only while adopting', () => {
    const error = failure(400, { errorCode: 'invalid_request_body' });
    expect(wishErrorKey(error, 'adopt')).toBe('wishes.errors.adopt_start_invalid');
    for (const context of CONTEXTS.filter((c) => c !== 'adopt')) {
      expect(wishErrorKey(error, context), context).toBe(apiErrorTranslationKey(error));
    }
    expect(apiErrorTranslationKey(error)).toBe('errors.api.invalid_request_body');
  });

  it.each([
    ['wish_not_found', 404],
    ['wish_state_conflict', 409],
    ['wish_emote_already_in_set', 409],
    ['wish_alias_invalid', 400],
    ['wishes_disabled', 404],
    ['an_unknown_code', 400],
  ])('leaves %s to the general mapping', (errorCode, status) => {
    const error = failure(status, { errorCode });
    for (const context of CONTEXTS) {
      expect(wishErrorKey(error, context)).toBe(apiErrorTranslationKey(error));
    }
  });

  it.each([0, 401, 403, 429, 500, 503])(
    'leaves a body-less status %i to the general mapping',
    (status) => {
      const error = failure(status);
      for (const context of CONTEXTS) {
        expect(wishErrorKey(error, context)).toBe(apiErrorTranslationKey(error));
      }
    },
  );

  it('leaves a non-object body to the general mapping', () => {
    const error = failure(502, '<html>bad gateway</html>');
    expect(wishErrorKey(error, 'adopt')).toBe(apiErrorTranslationKey(error));
  });
});
