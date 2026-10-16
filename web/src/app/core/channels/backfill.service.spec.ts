import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { BackfillCoverageInterval } from './backfill.model';
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

  it('posts the confirmed coverage to the removal route, without the set names', () => {
    const coverage: BackfillCoverageInterval[] = [
      {
        from: '2026-04-08',
        to: '2026-07-08',
        emoteSetId: 'set-halloween',
        emoteSetName: 'Halloween',
        archiveHost: 'logs.cyex.app',
      },
      {
        from: '2026-07-08',
        to: '2026-10-08',
        emoteSetId: 'set-normal',
        emoteSetName: null,
        archiveHost: 'logs.cyex.app',
      },
    ];

    service.removeImported('HandOfBlood', coverage).subscribe();

    const request = httpMock.expectOne('/api/channels/handofblood/backfill/imported/remove');
    expect(request.request.method).toBe('POST');
    // toStrictEqual: a key sent as `null` or `undefined` would fail here, not only a wrong value.
    expect(request.request.body).toStrictEqual({
      expectedCoverage: [
        {
          from: '2026-04-08',
          to: '2026-07-08',
          emoteSetId: 'set-halloween',
          archiveHost: 'logs.cyex.app',
        },
        {
          from: '2026-07-08',
          to: '2026-10-08',
          emoteSetId: 'set-normal',
          archiveHost: 'logs.cyex.app',
        },
      ],
    });
    request.flush({ removedUsageRows: 12, removedCoverageDays: 183 });
  });
});
