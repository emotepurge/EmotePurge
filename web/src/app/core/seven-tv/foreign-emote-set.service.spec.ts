import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import { ForeignEmoteSetService } from './foreign-emote-set.service';

function response(overrides: Partial<ForeignEmoteSetResponse> = {}): ForeignEmoteSetResponse {
  return {
    channelName: 'handofblood',
    sevenTvUserId: 'user1',
    emoteSetId: 'set1',
    totalCount: 1,
    truncated: false,
    emotes: [
      {
        sevenTvEmoteId: 'e1',
        name: 'catJAM',
        defaultName: 'catJAM',
        imageUrl: 'https://cdn.7tv.app/e1/4x.webp',
        topAllTime: 12,
        trending: null,
      },
    ],
    ...overrides,
  };
}

describe('ForeignEmoteSetService', () => {
  let service: ForeignEmoteSetService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ForeignEmoteSetService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('request shape', () => {
    it('GETs the per-channel emotes endpoint with no query string when refresh is not requested', () => {
      service.load('handofblood').subscribe();

      const req = httpMock.expectOne('/api/seventv/channels/handofblood/emotes');
      expect(req.request.method).toBe('GET');
      expect(req.request.params.keys()).toEqual([]);
      req.flush(response());
    });

    it('omits refresh from the query string when explicitly passed as false', () => {
      // A caller that hands { refresh: false } must not accidentally bypass the backend's cache.
      service.load('handofblood', { refresh: false }).subscribe();

      const req = httpMock.expectOne('/api/seventv/channels/handofblood/emotes');
      expect(req.request.params.has('refresh')).toBe(false);
      req.flush(response());
    });

    it('appends refresh=true only when the cache bypass is explicitly requested', () => {
      service.load('handofblood', { refresh: true }).subscribe();

      const req = httpMock.expectOne(
        (candidate) =>
          candidate.url === '/api/seventv/channels/handofblood/emotes' &&
          candidate.params.get('refresh') === 'true',
      );
      expect(req.request.params.keys()).toEqual(['refresh']);
      req.flush(response());
    });
  });

  describe('channel name normalization (rule 9)', () => {
    it('lower-cases a mixed-case Twitch login before it reaches the URL', () => {
      service.load('HandOfBlood').subscribe();

      httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(response());
    });

    it('trims surrounding whitespace before it reaches the URL', () => {
      service.load('  handofblood  ').subscribe();

      httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(response());
    });

    it('normalizes the same way whether or not a refresh is requested', () => {
      service.load('HandOfBlood', { refresh: true }).subscribe();

      httpMock
        .expectOne(
          (candidate) =>
            candidate.url === '/api/seventv/channels/handofblood/emotes' &&
            candidate.params.get('refresh') === 'true',
        )
        .flush(response());
    });
  });

  describe('response and error passthrough', () => {
    it('hands the response body through unchanged, including a null trending score', () => {
      let result: ForeignEmoteSetResponse | undefined;
      service.load('handofblood').subscribe((r) => (result = r));

      const payload = response({ truncated: true, totalCount: 1500 });
      httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(payload);

      expect(result).toEqual(payload);
    });

    it('propagates a backend error response to the caller instead of swallowing it', () => {
      // Error mapping is deliberately left to the caller via apiErrorTranslationKey (per the class
      // doc comment) — this only proves the service does not intercept or translate the error itself.
      let error: HttpErrorResponse | undefined;
      service.load('handofblood').subscribe({
        error: (e: HttpErrorResponse) => (error = e),
      });

      httpMock
        .expectOne('/api/seventv/channels/handofblood/emotes')
        .flush(
          { errorCode: 'foreign_channel_seventv_unavailable' },
          { status: 503, statusText: 'Service Unavailable' },
        );

      expect(error?.status).toBe(503);
      expect(error?.error).toEqual({ errorCode: 'foreign_channel_seventv_unavailable' });
    });
  });
});
