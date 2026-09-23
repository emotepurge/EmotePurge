import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { LegalService } from './legal.service';

describe('LegalService', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
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
});
