import { HttpErrorResponse } from '@angular/common/http';

import { apiErrorCode, apiErrorTranslationKey } from '../i18n/api-error';

/** Which wish operation failed; only `adopt` changes how a generic 400 reads. */
export type WishErrorContext = 'create' | 'adopt' | 'update' | 're-add' | 'action';

export const WISH_EMOTE_IDS_INVALID_KEY = 'wishes.errors.emote_ids_invalid';
export const WISH_ADOPT_START_INVALID_KEY = 'wishes.errors.adopt_start_invalid';

/**
 * The translation key for a failed wish request. The generic mapping, except for two codes whose
 * general text names the wrong thing here: `emote_ids_invalid` (the general text speaks of selected
 * emotes, a wish form has a link field) and `invalid_request_body` while adopting (the only way that
 * body is rejected there is a start date the server refuses). Everything else, including a status
 * without a body, goes unchanged to `apiErrorTranslationKey`.
 */
export function wishErrorKey(error: HttpErrorResponse, context: WishErrorContext): string {
  const code = apiErrorCode(error);
  if (code === 'emote_ids_invalid') {
    return WISH_EMOTE_IDS_INVALID_KEY;
  }
  if (code === 'invalid_request_body' && context === 'adopt') {
    return WISH_ADOPT_START_INVALID_KEY;
  }
  return apiErrorTranslationKey(error);
}
