import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { EmoteUsageSeries } from './usage-stat.model';
import { UsageStatService } from './usage-stat.service';

describe('UsageStatService', () => {
  let service: UsageStatService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(UsageStatService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('getTotals GETs the totals endpoint with from/to query params and no emoteSetId when null', () => {
    service.getTotals('sensitron', '2026-07-01', '2026-07-28', null).subscribe();

    const req = httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/sensitron/usage-stats/totals' &&
        r.params.get('from') === '2026-07-01' &&
        r.params.get('to') === '2026-07-28' &&
        r.params.get('emoteSetId') === null,
    );
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('getTotals sends emoteSetId when a set is explicitly selected', () => {
    service.getTotals('sensitron', '2026-07-01', '2026-07-28', 'set-halloween').subscribe();

    const req = httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/sensitron/usage-stats/totals' &&
        r.params.get('emoteSetId') === 'set-halloween',
    );
    req.flush([]);
  });

  const SERIES: EmoteUsageSeries = {
    emoteId: 'e1',
    emoteName: 'PogU',
    from: '2026-07-01',
    to: '2026-07-28',
    totalUseCount: 0,
    firstUsedDate: null,
    lastUsedDate: null,
    days: [],
    liveDays: [],
  };

  it('getDailySeries GETs the daily endpoint with emoteId/from/to query params', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', null).subscribe();

    const req = httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/sensitron/usage-stats/daily' &&
        r.params.get('emoteId') === 'e1' &&
        r.params.get('from') === '2026-07-01' &&
        r.params.get('to') === '2026-07-28' &&
        r.params.get('emoteSetId') === null,
    );
    expect(req.request.method).toBe('GET');
    req.flush(SERIES);
  });

  it('serves a second identical getDailySeries call from the cache', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', null).subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);

    let replayed: EmoteUsageSeries | undefined;
    service
      .getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', null)
      .subscribe((series) => (replayed = series));

    // No second request — httpMock.verify() in afterEach would flag one.
    expect(replayed?.emoteName).toBe('PogU');
  });

  it('does not serve a getDailySeries call for a different emoteSetId from another set’s cache (AK 64)', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', 'set-a').subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);

    // Same channel, emote and range, a DIFFERENT set — must hit the network again, not the cache
    // entry keyed by 'set-a'.
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', 'set-b').subscribe();
    httpMock
      .expectOne(
        (r) =>
          r.url === '/api/channels/sensitron/usage-stats/daily' &&
          r.params.get('emoteSetId') === 'set-b',
      )
      .flush(SERIES);
  });

  it('clearSeriesCache forces a fresh request', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', null).subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);

    service.clearSeriesCache();
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', null).subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);
  });

  it('does not cache a failed request', () => {
    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', null).subscribe({
      error: () => undefined,
    });
    httpMock
      .expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily')
      .flush({ errorCode: 'unexpected_error' }, { status: 500, statusText: 'Server Error' });

    service.getDailySeries('sensitron', 'e1', '2026-07-01', '2026-07-28', null).subscribe();
    httpMock.expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/daily').flush(SERIES);
  });

  it('getChannelSeries GETs the series endpoint with from/to and no emoteSetId when null', () => {
    service.getChannelSeries('sensitron', '2026-07-01', '2026-07-28', null).subscribe();

    const req = httpMock.expectOne(
      (r) =>
        r.url === '/api/channels/sensitron/usage-stats/series' &&
        r.params.get('from') === '2026-07-01' &&
        r.params.get('to') === '2026-07-28' &&
        r.params.get('emoteSetId') === null,
    );
    req.flush({ from: '2026-07-01', to: '2026-07-28', liveDays: [], emotes: [] });
  });

  it('does not serve a getChannelSeries call for a different emoteSetId from another set’s cache (AK 64)', () => {
    service.getChannelSeries('sensitron', '2026-07-01', '2026-07-28', 'set-a').subscribe();
    httpMock
      .expectOne((r) => r.url === '/api/channels/sensitron/usage-stats/series')
      .flush({ from: '2026-07-01', to: '2026-07-28', liveDays: [], emotes: [] });

    // Same channel and range, a DIFFERENT set — two sets over the same range are two requests, not
    // one cached answer served twice.
    service.getChannelSeries('sensitron', '2026-07-01', '2026-07-28', 'set-b').subscribe();
    httpMock
      .expectOne(
        (r) =>
          r.url === '/api/channels/sensitron/usage-stats/series' &&
          r.params.get('emoteSetId') === 'set-b',
      )
      .flush({ from: '2026-07-01', to: '2026-07-28', liveDays: [], emotes: [] });
  });
});
