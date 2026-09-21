import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

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
