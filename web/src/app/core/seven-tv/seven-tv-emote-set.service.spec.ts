import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import { EmoteSetListResponse, EmoteSetTargetsResponse } from './seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from './seven-tv-emote-set.service';

function listResponse(overrides: Partial<EmoteSetListResponse> = {}): EmoteSetListResponse {
  return {
    activeEmoteSetId: 'set-active',
    sets: [
      {
        id: 'set-active',
        name: 'Main',
        capacity: 250,
        kind: 'NORMAL',
        isActive: true,
        isPersonal: false,
        ownerDisplayName: 'HandOfBlood',
        observations: [],
      },
    ],
    ...overrides,
  };
}

function targetsResponse(
  overrides: Partial<EmoteSetTargetsResponse> = {},
): EmoteSetTargetsResponse {
  return {
    accounts: [
      {
        twitchChannelId: '1',
        twitchLogin: 'handofblood',
        isOwnAccount: true,
        trackedChannelName: 'handofblood',
        activeEmoteSetId: 'set-active',
        sets: [
          {
            id: 'set-active',
            name: 'Main',
            capacity: 250,
            kind: 'NORMAL',
            isActive: true,
            isPersonal: false,
            ownerDisplayName: 'HandOfBlood',
          },
        ],
        setsUnavailable: false,
      },
    ],
    sevenTvUnavailable: false,
    ...overrides,
  };
}

function previewResponse(
  overrides: Partial<ForeignEmoteSetResponse> = {},
): ForeignEmoteSetResponse {
  return {
    channelName: 'handofblood',
    sevenTvUserId: null,
    emoteSetId: 'set-halloween',
    emoteSetName: 'Halloween',
    capacity: 250,
    totalCount: 1,
    truncated: false,
    emotes: [],
    ...overrides,
  };
}

describe('SevenTvEmoteSetService', () => {
  let service: SevenTvEmoteSetService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(SevenTvEmoteSetService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('listChannelEmoteSets — 6.1', () => {
    it('GETs the tracked-channel set list, normalizing the channel name (rule 9)', () => {
      let result: EmoteSetListResponse | undefined;
      service.listChannelEmoteSets('HandOfBlood').subscribe((r) => (result = r));

      const req = httpMock.expectOne('/api/channels/handofblood/emote-sets');
      expect(req.request.method).toBe('GET');
      const payload = listResponse();
      req.flush(payload);

      expect(result).toEqual(payload);
    });
  });

  describe('listEmoteSetTargets — 6.2', () => {
    it('GETs the picker offer list with no channel name in the route', () => {
      let result: EmoteSetTargetsResponse | undefined;
      service.listEmoteSetTargets().subscribe((r) => (result = r));

      const req = httpMock.expectOne('/api/seventv/me/emote-set-targets');
      expect(req.request.method).toBe('GET');
      const payload = targetsResponse();
      req.flush(payload);

      expect(result).toEqual(payload);
    });
  });

  describe('listForeignChannelEmoteSets — 6.3', () => {
    it('GETs the foreign-channel set list under /api/seventv/, normalizing the channel name', () => {
      let result: EmoteSetListResponse | undefined;
      service.listForeignChannelEmoteSets('HandOfBlood').subscribe((r) => (result = r));

      const req = httpMock.expectOne('/api/seventv/channels/handofblood/emote-sets');
      expect(req.request.method).toBe('GET');
      const payload = listResponse();
      req.flush(payload);

      expect(result).toEqual(payload);
    });
  });

  describe('loadEmoteSetPreview — 6.4 set-ID mode', () => {
    it('appends emoteSetId to the existing preview endpoint, without refresh by default', () => {
      let result: ForeignEmoteSetResponse | undefined;
      service.loadEmoteSetPreview('HandOfBlood', 'set-halloween').subscribe((r) => (result = r));

      const req = httpMock.expectOne(
        (candidate) =>
          candidate.url === '/api/seventv/channels/handofblood/emotes' &&
          candidate.params.get('emoteSetId') === 'set-halloween',
      );
      expect(req.request.params.has('refresh')).toBe(false);
      const payload = previewResponse();
      req.flush(payload);

      expect(result).toEqual(payload);
    });

    it('adds refresh=true only when explicitly requested, alongside emoteSetId', () => {
      service.loadEmoteSetPreview('handofblood', 'set-halloween', { refresh: true }).subscribe();

      const req = httpMock.expectOne(
        (candidate) =>
          candidate.url === '/api/seventv/channels/handofblood/emotes' &&
          candidate.params.get('emoteSetId') === 'set-halloween' &&
          candidate.params.get('refresh') === 'true',
      );
      req.flush(previewResponse());
    });
  });

  // K4 fix round (2026-09-22): a fast A→B→A switch on the usage page hit 429 on the shared
  // ForeignEmoteLookup limiter even though the backend's own 60 s cache sat behind it — a cache hit
  // there still spends a permit. loadCachedEmoteSetPreview mirrors that TTL client-side, for this
  // one caller (K4's usage-stats page) only; K3/K2 keep calling the plain loadEmoteSetPreview above.
  describe('loadCachedEmoteSetPreview — K4 client-side cache (2026-09-22)', () => {
    afterEach(() => {
      vi.useRealTimers();
    });

    it('serves a fresh entry from the cache without a second request', () => {
      service.loadCachedEmoteSetPreview('handofblood', 'set-halloween').subscribe();
      const payload = previewResponse();
      httpMock
        .expectOne(
          (candidate) =>
            candidate.url === '/api/seventv/channels/handofblood/emotes' &&
            candidate.params.get('emoteSetId') === 'set-halloween',
        )
        .flush(payload);

      let result: ForeignEmoteSetResponse | undefined;
      service
        .loadCachedEmoteSetPreview('handofblood', 'set-halloween')
        .subscribe((r) => (result = r));

      expect(result).toEqual(payload);
      httpMock.expectNone(
        (candidate) =>
          candidate.url === '/api/seventv/channels/handofblood/emotes' &&
          candidate.params.get('emoteSetId') === 'set-halloween',
      );
    });

    it('refetches once the entry has expired', () => {
      vi.useFakeTimers();
      service.loadCachedEmoteSetPreview('handofblood', 'set-halloween').subscribe();
      httpMock
        .expectOne((candidate) => candidate.params.get('emoteSetId') === 'set-halloween')
        .flush(previewResponse());

      vi.advanceTimersByTime(60_001);

      service.loadCachedEmoteSetPreview('handofblood', 'set-halloween').subscribe();
      httpMock
        .expectOne((candidate) => candidate.params.get('emoteSetId') === 'set-halloween')
        .flush(previewResponse());
    });

    it('a refresh:true call bypasses a warm entry, fetches, and replaces it for the next plain read', () => {
      service.loadCachedEmoteSetPreview('handofblood', 'set-halloween').subscribe();
      httpMock
        .expectOne((candidate) => candidate.params.get('emoteSetId') === 'set-halloween')
        .flush(previewResponse({ totalCount: 1 }));

      service
        .loadCachedEmoteSetPreview('handofblood', 'set-halloween', { refresh: true })
        .subscribe();
      const refreshReq = httpMock.expectOne(
        (candidate) =>
          candidate.params.get('emoteSetId') === 'set-halloween' &&
          candidate.params.get('refresh') === 'true',
      );
      const refreshed = previewResponse({ totalCount: 2 });
      refreshReq.flush(refreshed);

      let result: ForeignEmoteSetResponse | undefined;
      service
        .loadCachedEmoteSetPreview('handofblood', 'set-halloween')
        .subscribe((r) => (result = r));

      expect(result).toEqual(refreshed);
      httpMock.expectNone(
        (candidate) =>
          candidate.url === '/api/seventv/channels/handofblood/emotes' &&
          candidate.params.get('refresh') !== 'true',
      );
    });

    it('never caches an error — a retry after a 429 always asks again', () => {
      service
        .loadCachedEmoteSetPreview('handofblood', 'set-halloween')
        .subscribe({ error: () => undefined });
      httpMock
        .expectOne((candidate) => candidate.params.get('emoteSetId') === 'set-halloween')
        .flush({ errorCode: 'rate_limited' }, { status: 429, statusText: 'Too Many Requests' });

      service.loadCachedEmoteSetPreview('handofblood', 'set-halloween').subscribe();
      httpMock
        .expectOne((candidate) => candidate.params.get('emoteSetId') === 'set-halloween')
        .flush(previewResponse());
    });

    it('keys separately per emote set within the same channel', () => {
      service.loadCachedEmoteSetPreview('handofblood', 'set-a').subscribe();
      httpMock
        .expectOne((candidate) => candidate.params.get('emoteSetId') === 'set-a')
        .flush(previewResponse({ emoteSetId: 'set-a' }));

      service.loadCachedEmoteSetPreview('handofblood', 'set-b').subscribe();
      httpMock
        .expectOne((candidate) => candidate.params.get('emoteSetId') === 'set-b')
        .flush(previewResponse({ emoteSetId: 'set-b' }));
    });

    it('keys separately per channel for the same emote set id, and normalizes the channel name (rule 9)', () => {
      service.loadCachedEmoteSetPreview('HandOfBlood', 'set-shared').subscribe();
      httpMock
        .expectOne(
          (candidate) =>
            candidate.url === '/api/seventv/channels/handofblood/emotes' &&
            candidate.params.get('emoteSetId') === 'set-shared',
        )
        .flush(previewResponse({ channelName: 'handofblood', emoteSetId: 'set-shared' }));

      // Same emote set id, a different channel — must not be served from handofblood's entry.
      service.loadCachedEmoteSetPreview('otherchannel', 'set-shared').subscribe();
      httpMock
        .expectOne(
          (candidate) =>
            candidate.url === '/api/seventv/channels/otherchannel/emotes' &&
            candidate.params.get('emoteSetId') === 'set-shared',
        )
        .flush(previewResponse({ channelName: 'otherchannel', emoteSetId: 'set-shared' }));

      // Mixed-case navigation back to the first channel still hits the cached entry.
      let result: ForeignEmoteSetResponse | undefined;
      service.loadCachedEmoteSetPreview('handofblood', 'set-shared').subscribe((r) => (result = r));
      expect(result?.channelName).toBe('handofblood');
      httpMock.expectNone(
        (candidate) =>
          candidate.url === '/api/seventv/channels/handofblood/emotes' &&
          candidate.params.get('emoteSetId') === 'set-shared',
      );
    });
  });

  // 6.7/T2.6: the closing report for a copy into an *untracked* account's set — no channel name in
  // the route, and no `targetEmoteSetId` in the body (the route already names the set).
  describe('reportImportedToSet — 6.7 set-centric endpoint', () => {
    it('POSTs the body as-is, without a targetEmoteSetId, to the set-scoped route', () => {
      let completed = false;
      service
        .reportImportedToSet('set-u', {
          sevenTvEmoteIds: ['7tv-1', '7tv-2'],
          sourceChannelName: 'handofblood',
          sourceKind: 'channel',
          leaderboardSort: null,
        })
        .subscribe(() => (completed = true));

      const req = httpMock.expectOne('/api/seventv/emote-sets/set-u/sync-imported');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1', '7tv-2'],
        sourceChannelName: 'handofblood',
        sourceKind: 'channel',
        leaderboardSort: null,
      });
      req.flush(null, { status: 204, statusText: 'No Content' });

      expect(completed).toBe(true);
    });
  });
});
