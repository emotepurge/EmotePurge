import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { LegalService } from './legal.service';

/** Matches any URL, so `router.navigateByUrl(...)` below always completes with a `NavigationEnd`
 *  even though nothing in this spec ever mounts a `RouterOutlet` to render it into. */
@Component({ template: '' })
class Blank {}

describe('LegalService', () => {
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: '**', component: Blank }]),
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches availability once on construction and starts both flags false', () => {
    const service = TestBed.inject(LegalService);

    expect(service.imprintAvailable()).toBe(false);
    expect(service.privacyAvailable()).toBe(false);

    const req = httpMock.expectOne('/api/legal/availability');
    expect(req.request.method).toBe('GET');
    req.flush({ imprintAvailable: true, privacyAvailable: false });

    expect(service.imprintAvailable()).toBe(true);
    expect(service.privacyAvailable()).toBe(false);
  });

  it('falls back to both flags false when the availability request errors', () => {
    const service = TestBed.inject(LegalService);

    httpMock
      .expectOne('/api/legal/availability')
      .flush(null, { status: 500, statusText: 'Server Error' });

    expect(service.imprintAvailable()).toBe(false);
    expect(service.privacyAvailable()).toBe(false);
  });

  it('hasAnyDocument is true as soon as either flag is', () => {
    const service = TestBed.inject(LegalService);

    httpMock
      .expectOne('/api/legal/availability')
      .flush({ imprintAvailable: false, privacyAvailable: true });

    expect(service.hasAnyDocument()).toBe(true);
  });

  it('hasAnyDocument stays false when neither document is configured', () => {
    const service = TestBed.inject(LegalService);

    httpMock
      .expectOne('/api/legal/availability')
      .flush({ imprintAvailable: false, privacyAvailable: false });

    expect(service.hasAnyDocument()).toBe(false);
  });

  it('getDocument requests the given kind and language', () => {
    const service = TestBed.inject(LegalService);
    httpMock
      .expectOne('/api/legal/availability')
      .flush({ imprintAvailable: true, privacyAvailable: true });

    let result: { html: string; isGermanFallback: boolean } | undefined;
    service.getDocument('privacy', 'en').subscribe((response) => (result = response));

    const req = httpMock.expectOne('/api/legal/privacy/en');
    expect(req.request.method).toBe('GET');
    req.flush({ html: '<h1>Privacy</h1>', isGermanFallback: true });

    expect(result).toEqual({ html: '<h1>Privacy</h1>', isGermanFallback: true });
  });

  // Codex Sol review of #247 (P2): a failed availability fetch must not hide both footer links for
  // the rest of the session — it has to get another chance, paced by the visitor's own navigation
  // rather than a timer.
  describe('retry after a failed availability fetch', () => {
    it('retries the request on the next completed navigation after a failure', async () => {
      const service = TestBed.inject(LegalService);
      httpMock
        .expectOne('/api/legal/availability')
        .flush(null, { status: 429, statusText: 'Too Many Requests' });
      expect(service.imprintAvailable()).toBe(false);

      await router.navigateByUrl('/somewhere');

      httpMock
        .expectOne('/api/legal/availability')
        .flush({ imprintAvailable: true, privacyAvailable: false });

      expect(service.imprintAvailable()).toBe(true);
    });

    it('keeps retrying on further navigations as long as the retry itself fails', async () => {
      const service = TestBed.inject(LegalService);
      httpMock
        .expectOne('/api/legal/availability')
        .flush(null, { status: 500, statusText: 'Server Error' });

      await router.navigateByUrl('/first');
      httpMock
        .expectOne('/api/legal/availability')
        .flush(null, { status: 500, statusText: 'Server Error' });

      await router.navigateByUrl('/second');
      httpMock
        .expectOne('/api/legal/availability')
        .flush({ imprintAvailable: false, privacyAvailable: true });

      expect(service.privacyAvailable()).toBe(true);
    });

    it('does not re-request on navigation once a fetch has already succeeded', async () => {
      TestBed.inject(LegalService);
      httpMock
        .expectOne('/api/legal/availability')
        .flush({ imprintAvailable: true, privacyAvailable: true });

      await router.navigateByUrl('/somewhere');

      // httpMock.verify() in afterEach fails on any request left open — there must be none, since
      // a second one here would mean the service keeps re-fetching configuration that already
      // loaded successfully and cannot change again before a restart.
    });
  });
});
