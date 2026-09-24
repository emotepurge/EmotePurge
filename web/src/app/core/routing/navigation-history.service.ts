import { inject, Service, signal } from '@angular/core';
import { NavigationEnd, NavigationStart, Router } from '@angular/router';

/**
 * Tracks whether the ACTIVE browser-history entry — the one the tab is currently showing — is the
 * first one this app instance has ever navigated to, as opposed to the current page being the
 * entry point of the session (a fresh tab, a reload, or an external link landing directly on it).
 *
 * Provided in root and injected eagerly from `App` (`app.ts`, the one component rendered on every
 * route including the pages outside the app shell), not lazily from whichever page first needs it:
 * a `providedIn: 'root'` service is otherwise only constructed on first injection, and by the time
 * a lazily-loaded page such as `LegalPage` injects it for itself, that page's OWN `NavigationEnd`
 * may already have fired — indistinguishable from "no previous page" unless something was already
 * listening before it.
 *
 * Tracks the POSITION of the active entry, not a lifetime count of completed navigations (fixed
 * after a Codex review flagged the earlier count-based version as a P2): a visitor who opens
 * `/imprint` directly (entry 0), clicks the fallback link to `/welcome` (entry 1), then presses the
 * browser's own Back button lands back on `/imprint` — which is, again, this tab's very FIRST
 * history entry. A lifetime count would have kept counting (2 completed navigations, "has a
 * previous page") and offered a `Location.back()` that does nothing in a fresh tab, or leaves the
 * app to whatever opened it. Going by `router.events` rather than `history.length` still matters
 * for the reason below; what changed is COUNTING replaced by TRACKING.
 *
 * `history.length` is not an option to begin with — it also counts pages outside this app (an
 * external referrer, an earlier tab session), which would make a `Location.back()` gated on it
 * leave the app — the one consumer of `hasPreviousPage()`, `legal-back-target.ts`, relies on this
 * signal being true only when the previous browser-history entry is itself a page THIS app pushed.
 *
 * The mechanism, verified empirically against Angular 22's actual `Router`/`Location` event
 * sequence (a `RouterTestingHarness` probe, not just the source):
 * - The very first `NavigationStart` this instance ever sees (`currentIndex` still `null`) is
 *   entry 0, no matter its `navigationTrigger` — even the app's own initial navigation reports
 *   `'imperative'`, not some special "boot" value.
 * - An `'imperative'` trigger (`router.navigate()`/`navigateByUrl()`, including a `RouterLink`
 *   click) advances by one UNLESS the in-flight navigation's own extras say `replaceUrl` or
 *   `skipLocationChange` — read via `router.getCurrentNavigation()?.extras`, which is populated
 *   before `NavigationStart` fires and thus safe to read synchronously from this subscriber. A
 *   guard redirect (e.g. `homeGuard` sending `/` to `/welcome`) is NOT a special case here: the
 *   original, cancelled attempt never reaches `NavigationEnd` (so it never advances anything) and
 *   the redirect's OWN completing navigation carries `replaceUrl: false` — it performs a genuine
 *   `pushState`, confirmed against the fake `Navigation` entries list in a harness test — so a
 *   redirect still costs exactly the one advance that matches the one real entry it leaves behind.
 * - A `'popstate'` trigger (the browser's own Back/Forward) does not advance by a fixed amount at
 *   all — it carries `restoredState.navigationId`, the id of whichever earlier `NavigationEnd`
 *   pushed the entry the browser just moved to, and this service looks up the index THAT
 *   navigation was assigned when it completed. An id this instance never recorded (an entry from
 *   before this page load, e.g. the bfcache) is treated as entry 0 rather than guessed at.
 * - A `'hashchange'` trigger (fragment-only navigation) never changes which page is active, so the
 *   index does not move.
 * - A navigation that never reaches `NavigationEnd` (`NavigationCancel`, `NavigationError`,
 *   `NavigationSkipped`) never applies the index it computed at its own `NavigationStart` — the
 *   next real `NavigationStart` simply overwrites that pending value before anything reads it.
 */
@Service()
export class NavigationHistoryService {
  private readonly router = inject(Router);

  // Position of the active history entry in this app instance's own navigation sequence. `null`
  // until the first NavigationEnd of the session — see the class doc comment for why every branch
  // below treats that as its own case rather than falling out of the arithmetic.
  private currentIndex: number | null = null;

  // Computed at NavigationStart for whichever transition is currently in flight, applied to
  // currentIndex only if that same transition reaches NavigationEnd (see the class doc comment's
  // last bullet for why a cancelled/errored transition needs no explicit cleanup here).
  private pendingIndex: number | null = null;

  // navigationId -> the index it was assigned on completion. Filled on every NavigationEnd, read
  // back on a popstate NavigationStart via restoredState.navigationId.
  private readonly indexByNavigationId = new Map<number, number>();

  /** True unless the active history entry is the first one this app instance has navigated to. */
  readonly hasPreviousPage = signal(false);

  constructor() {
    this.router.events.subscribe((event) => {
      if (event instanceof NavigationStart) {
        this.pendingIndex = this.computeIndexFor(event);
      } else if (event instanceof NavigationEnd) {
        // pendingIndex is never null here: NavigationEnd only ever fires after a NavigationStart
        // for the same navigation id, and nothing between the two can clear it.
        this.currentIndex = this.pendingIndex as number;
        this.indexByNavigationId.set(event.id, this.currentIndex);
        this.hasPreviousPage.set(this.currentIndex > 0);
      }
    });
  }

  private computeIndexFor(event: NavigationStart): number {
    if (this.currentIndex === null) {
      return 0;
    }

    if (event.navigationTrigger === 'popstate') {
      const navigationId = event.restoredState?.navigationId;
      const restoredIndex =
        navigationId !== undefined ? this.indexByNavigationId.get(navigationId) : undefined;
      return restoredIndex ?? 0;
    }

    if (event.navigationTrigger === 'hashchange') {
      return this.currentIndex;
    }

    const extras = this.router.getCurrentNavigation()?.extras;
    const staysOnCurrentEntry = extras?.replaceUrl === true || extras?.skipLocationChange === true;
    return staysOnCurrentEntry ? this.currentIndex : this.currentIndex + 1;
  }
}
