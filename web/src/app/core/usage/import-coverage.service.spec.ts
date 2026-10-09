import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ImportCoverageService } from './import-coverage.service';

describe('ImportCoverageService', () => {
  let service: ImportCoverageService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ImportCoverageService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('asks for a concrete set by its id on the normalized channel', () => {
    service.getCoverage('HandOfBlood', { kind: 'set', emoteSetId: '01HQSET' }).subscribe();

    const request = httpMock.expectOne(
      (r) => r.url === '/api/channels/handofblood/usage-stats/import-coverage',
    );
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('emoteSetId')).toBe('01HQSET');
    request.flush({});
  });

  it('spells every set as emoteSetId=all, not as setScope', () => {
    service.getCoverage('sensitron', { kind: 'all' }).subscribe();

    const request = httpMock.expectOne((r) => r.url.endsWith('/import-coverage'));
    expect(request.request.params.get('emoteSetId')).toBe('all');
    expect(request.request.params.has('setScope')).toBe(false);
    request.flush({});
  });

  it('omits the parameter for the active set so the server resolves it', () => {
    service.getCoverage('sensitron', { kind: 'active' }).subscribe();

    const request = httpMock.expectOne((r) => r.url.endsWith('/import-coverage'));
    expect(request.request.params.keys()).toEqual([]);
    request.flush({});
  });
});
