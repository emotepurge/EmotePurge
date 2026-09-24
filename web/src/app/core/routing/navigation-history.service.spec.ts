import { Location } from '@angular/common';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanActivateFn, NavigationEnd, provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { firstValueFrom } from 'rxjs';
import { filter } from 'rxjs/operators';
import { beforeEach, describe, expect, it } from 'vitest';

import { NavigationHistoryService } from './navigation-history.service';

@Component({ template: '' })
class PageA {}

@Component({ template: '' })
class PageB {}

@Component({ template: '' })
class PageRedirectTarget {}

const redirectToBGuard: CanActivateFn = () => TestBed.inject(Router).createUrlTree(['/b']);

/** Resolves once the next NavigationEnd fires — used to await a popstate-triggered navigation,
 * which (unlike `harness.navigateByUrl`) does not return a promise of its own. */
function nextNavigationEnd(router: Router): Promise<void> {
  return firstValueFrom(router.events.pipe(filter((event) => event instanceof NavigationEnd))).then(
    () => undefined,
  );
}

describe('NavigationHistoryService', () => {
  let harness: RouterTestingHarness;
  let service: NavigationHistoryService;
  let router: Router;
  let location: Location;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'a', component: PageA },
          { path: 'b', component: PageB },
          // Simulates homeGuard-style redirects (e.g. '/' -> '/welcome' for an anonymous visitor):
          // a canActivate guard that returns a UrlTree instead of activating this route.
          { path: 'redirect-to-b', component: PageRedirectTarget, canActivate: [redirectToBGuard] },
        ]),
      ],
    });

    // Injected BEFORE the first navigation, mirroring App's eager injection in production (see the
    // service's own doc comment) — it has to already be listening when the harness performs its
    // first navigateByUrl, exactly like it has to already be listening before a lazily-loaded
    // page's own first NavigationEnd.
    service = TestBed.inject(NavigationHistoryService);
    router = TestBed.inject(Router);
    location = TestBed.inject(Location);
    harness = await RouterTestingHarness.create();
  });

  it('reports no previous page for the very first in-app navigation of the session', async () => {
    await harness.navigateByUrl('/a', PageA);

    expect(service.hasPreviousPage()).toBe(false);
  });

  it('reports a previous page once a second in-app navigation completes', async () => {
    await harness.navigateByUrl('/a', PageA);
    await harness.navigateByUrl('/b', PageB);

    expect(service.hasPreviousPage()).toBe(true);
  });

  it('keeps reporting a previous page for every navigation after the second', async () => {
    await harness.navigateByUrl('/a', PageA);
    await harness.navigateByUrl('/b', PageB);
    await harness.navigateByUrl('/a', PageA);

    expect(service.hasPreviousPage()).toBe(true);
  });

  it('does not count a replaceUrl navigation as moving to a new entry', async () => {
    await harness.navigateByUrl('/a', PageA);

    await router.navigateByUrl('/b', { replaceUrl: true });

    expect(service.hasPreviousPage()).toBe(false);
  });

  it('does not create a phantom entry for a guard redirect — one popstate reaches the origin page', async () => {
    // '/a' plays the role of the session's origin page; 'redirect-to-b' plays homeGuard's '/', which
    // redirects an anonymous visitor to '/welcome' instead of activating.
    await harness.navigateByUrl('/a', PageA);
    await harness.navigateByUrl('/redirect-to-b', PageB);
    expect(service.hasPreviousPage()).toBe(true);

    // If the cancelled, superseded first attempt at 'redirect-to-b' had ALSO pushed a history entry
    // (a "phantom" one), a single popstate would land on it rather than on '/a'.
    router.setUpLocationChangeListener();
    const backToOrigin = nextNavigationEnd(router);
    location.back();
    await backToOrigin;

    expect(service.hasPreviousPage()).toBe(false);
  });

  it('reproduces the Codex-flagged scenario: direct load, fallback navigation, then browser Back returns to the very first entry', async () => {
    router.setUpLocationChangeListener();

    // '/a' stands in for a direct load of /imprint: the session's first and, at this point, only
    // history entry.
    await harness.navigateByUrl('/a', PageA);
    expect(service.hasPreviousPage()).toBe(false);

    // '/b' stands in for clicking the fallback link to /welcome.
    await harness.navigateByUrl('/b', PageB);
    expect(service.hasPreviousPage()).toBe(true);

    // The browser's own Back button: this lands on '/a' again, which is STILL this tab's very
    // first history entry — a lifetime count of completed navigations would wrongly report "has a
    // previous page" here (this was the Codex P2 finding); tracking the active entry's position
    // must not.
    const backToFirst = nextNavigationEnd(router);
    location.back();
    await backToFirst;

    expect(service.hasPreviousPage()).toBe(false);

    // Forward again lands back on '/b', which DOES have a previous entry.
    const forwardAgain = nextNavigationEnd(router);
    location.forward();
    await forwardAgain;

    expect(service.hasPreviousPage()).toBe(true);
  });
});
