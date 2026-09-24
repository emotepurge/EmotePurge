import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import { NavigationHistoryService } from './navigation-history.service';

@Component({ template: '' })
class PageA {}

@Component({ template: '' })
class PageB {}

describe('NavigationHistoryService', () => {
  let harness: RouterTestingHarness;
  let service: NavigationHistoryService;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'a', component: PageA },
          { path: 'b', component: PageB },
        ]),
      ],
    });

    // Injected BEFORE the first navigation, mirroring App's eager injection in production (see the
    // service's own doc comment) — it has to already be listening when the harness performs its
    // first navigateByUrl, exactly like it has to already be listening before a lazily-loaded
    // page's own first NavigationEnd.
    service = TestBed.inject(NavigationHistoryService);
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
});
