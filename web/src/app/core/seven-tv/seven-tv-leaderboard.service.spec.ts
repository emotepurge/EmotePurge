import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { SevenTvLeaderboardResponse } from './leaderboard.model';
import { SevenTvLeaderboardService } from './seven-tv-leaderboard.service';

function response(overrides: Partial<SevenTvLeaderboardResponse> = {}): SevenTvLeaderboardResponse {
  return {
    sortBy: 'TRENDING_DAILY',
    totalCount: 705,
    truncated: true,
    emotes: [
      {
        sevenTvEmoteId: 'e1',
        name: 'catJAM',
        defaultName: 'catJAM',
        imageUrl: 'https://cdn.7tv.app/e1/4x.webp',
        topAllTime: 42,
        trending: 7,
      },
    ],
    ...overrides,
  };
}

describe('SevenTvLeaderboardService', () => {
  let service: SevenTvLeaderboardService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(SevenTvLeaderboardService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('GETs the requested list and hands the response through unchanged', () => {
    let result: SevenTvLeaderboardResponse | undefined;
    service.load('TOP_ALL_TIME').subscribe((r) => (result = r));

    const req = httpMock.expectOne(
      (candidate) =>
        candidate.url === '/api/seventv/leaderboard' &&
        candidate.params.get('sortBy') === 'TOP_ALL_TIME',
    );
    expect(req.request.method).toBe('GET');
    const payload = response({ sortBy: 'TOP_ALL_TIME' });
    req.flush(payload);

    expect(result).toEqual(payload);
  });

  it('sends nothing but sortBy — no page, no perPage, no refresh', () => {
    // The whole safety argument of this source is that a client cannot make the server call 7TV
    // (spec §4, "Bypass-Freiheit"): a `refresh` here would be one upstream request per click.
    service.load('TRENDING_DAILY').subscribe();

    const req = httpMock.expectOne((candidate) => candidate.url === '/api/seventv/leaderboard');
    expect(req.request.params.keys()).toEqual(['sortBy']);
    req.flush(response());
  });
});
