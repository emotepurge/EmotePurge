import { inject, Service, signal } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';

/**
 * Tracks whether the current page was reached by an in-app navigation — i.e. whether a completed
 * `NavigationEnd` happened in this browser tab before the one for the current page — as opposed to
 * the current page being the entry point of the session (a fresh tab, a reload, or an external
 * link landing directly on it).
 *
 * Provided in root and injected eagerly from `App` (`app.ts`, the one component rendered on every
 * route including the pages outside the app shell), not lazily from whichever page first needs it:
 * a `providedIn: 'root'` service is otherwise only constructed on first injection, and by the time
 * a lazily-loaded page such as `LegalPage` injects it for itself, that page's OWN `NavigationEnd`
 * may already have fired — indistinguishable from "no previous page" unless something was already
 * listening before it.
 *
 * Counts navigations rather than comparing URLs deliberately: nothing here needs to know WHICH
 * page came before, only whether one did, and going by `router.events` rather than
 * `history.length` matters for that. `history.length` also counts pages outside this app (an
 * external referrer, an earlier tab session), which would make a `Location.back()` gated on it
 * leave the app — the one consumer of `hasPreviousPage()`, `legal-back-target.ts`, relies on this
 * signal being true only when the previous browser-history entry is itself a page THIS app pushed.
 */
@Service()
export class NavigationHistoryService {
  private readonly router = inject(Router);

  private navigationCount = 0;

  /** False for the very first completed navigation of the session, true from the second one on. */
  readonly hasPreviousPage = signal(false);

  constructor() {
    this.router.events
      .pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd))
      .subscribe(() => {
        this.navigationCount += 1;
        if (this.navigationCount > 1) {
          this.hasPreviousPage.set(true);
        }
      });
  }
}
