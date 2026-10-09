import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { BackfillService } from './backfill.service';

describe('BackfillService', () => {
  let service: BackfillService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(BackfillService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('reads the status of the normalized channel name', () => {
    service.getStatus('HandOfBlood').subscribe();

    const request = httpMock.expectOne('/api/channels/handofblood/backfill');
    expect(request.request.method).toBe('GET');
    request.flush({});
  });

  it('posts exactly the picked set id and months as a JSON number', () => {
    service.start('sensitron', '01HQSETID', 3).subscribe();

    const request = httpMock.expectOne('/api/channels/sensitron/backfill');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ emoteSetId: '01HQSETID', months: 3 });
    expect(typeof request.request.body.months).toBe('number');
    request.flush({});
  });

  it('cancels with a DELETE on the same route', () => {
    service.cancel('sensitron').subscribe();

    const request = httpMock.expectOne('/api/channels/sensitron/backfill');
    expect(request.request.method).toBe('DELETE');
    request.flush(null, { status: 204, statusText: 'No Content' });
  });
});
