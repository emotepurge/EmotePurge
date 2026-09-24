export type LegalBackTarget =
  | { readonly kind: 'back' }
  | { readonly kind: 'fallback'; readonly link: string; readonly labelKey: string };

/**
 * `LegalPage`'s one navigation control (fix for a gap in issue #247): a fixed "back to home" link
 * stranded a logged-in visitor who opened the imprint from inside the app (e.g. the footer on a
 * channel page) on the public landing page instead of back where they came from.
 *
 * Pure and exhaustive over both inputs so the decision is unit-testable without mounting
 * `LegalPage`, `Router`, or `Location`:
 * - An in-app previous page always wins over login state — the visitor came from somewhere in
 *   THIS app, and "Back" returns them there whether or not they are signed in. `hasPreviousPage`
 *   already guarantees (see `NavigationHistoryService`) that going back stays inside the app.
 * - No in-app previous page (fresh tab, reload, external link — the page was the session's entry
 *   point) falls back to a fixed destination: a logged-in visitor's own home (the overview, which
 *   `homeGuard` resolves '/' to) rather than the public landing page they have already passed;
 *   an anonymous visitor still goes to the landing page, unchanged from before this fix.
 */
export function resolveLegalBackTarget(
  hasPreviousPage: boolean,
  isLoggedIn: boolean,
): LegalBackTarget {
  if (hasPreviousPage) {
    return { kind: 'back' };
  }

  return isLoggedIn
    ? { kind: 'fallback', link: '/', labelKey: 'nav.overview' }
    : { kind: 'fallback', link: '/welcome', labelKey: 'legal.back' };
}
