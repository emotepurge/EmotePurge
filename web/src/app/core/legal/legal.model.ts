/** Route-segment vocabulary mirrored from `LegalEndpoints.TryParseKind` in the Api. */
export type LegalDocumentKind = 'imprint' | 'privacy';

/** Mirrors `LegalEndpoints.TryParseLanguage` — the app's own two supported locales (`AppLang`). */
export type LegalLanguage = 'de' | 'en';

/** GET /api/legal/availability. */
export interface LegalAvailabilityResponse {
  imprintAvailable: boolean;
  privacyAvailable: boolean;
}

/** GET /api/legal/{kind}/{language}. `html` is already-rendered Markdown, HTML disabled at render
 *  time server-side (Markdig `DisableHtml()`) — still bound through Angular's sanitizer on the
 *  frontend (LegalPage's `[innerHTML]`), never via `bypassSecurityTrustHtml`. */
export interface LegalDocumentResponse {
  html: string;
  isGermanFallback: boolean;
}
