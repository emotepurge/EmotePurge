import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import {
  EditableSetResolution,
  EmoteSetListResponse,
  EmoteSetTargetsResponse,
  SyncDeletedInSetResponse,
  SyncRestoredInSetResponse,
} from './seven-tv-emote-set.model';
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
        sevenTvUserId: 'user-1',
        sets: [
          {
            id: 'set-active',
            name: 'Main',
            capacity: 250,
            kind: 'NORMAL',
            isActive: true,
            isPersonal: false,
            ownerDisplayName: 'HandOfBlood',
            ownerSevenTvUserId: 'user-1',
            editable: true,
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

  // #253/T4, spec 4.2/6.2, E19: the one shared pre-check every first mutation into a 7TV set runs
  // (restore's file step, a delete confirmation, a replace start) — four outcomes, read from the
  // same 60 s-cached target list the picker itself reads (AK 3-6).
  describe('resolveEditableSet — 4.2/6.2, E19', () => {
    it('resolves a found, NORMAL, editable set to status "editable" with the resolved target', () => {
      let result: EditableSetResolution | undefined;
      service.resolveEditableSet('set-active').subscribe((r) => (result = r));

      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(targetsResponse());

      expect(result).toEqual({
        status: 'editable',
        target: {
          emoteSetId: 'set-active',
          setName: 'Main',
          ownerDisplayName: 'HandOfBlood',
          twitchLogin: 'handofblood',
          trackedChannelName: 'handofblood',
          isActiveSet: true,
        },
      });
    });

    it('resolves a found set with kind !== NORMAL to "notSelectable", even when it is editable', () => {
      let result: EditableSetResolution | undefined;
      service.resolveEditableSet('set-global').subscribe((r) => (result = r));

      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(
        targetsResponse({
          accounts: [
            {
              twitchChannelId: '1',
              twitchLogin: 'handofblood',
              isOwnAccount: true,
              trackedChannelName: 'handofblood',
              activeEmoteSetId: 'set-active',
              sevenTvUserId: 'user-1',
              setsUnavailable: false,
              sets: [
                {
                  id: 'set-global',
                  name: 'Global',
                  capacity: null,
                  kind: 'GLOBAL',
                  isActive: false,
                  isPersonal: false,
                  ownerDisplayName: null,
                  ownerSevenTvUserId: 'user-1',
                  editable: true,
                },
              ],
            },
          ],
        }),
      );

      expect(result).toEqual({ status: 'notSelectable' });
    });

    it('resolves a set that is in no account\'s list, with the list otherwise complete, to "notEditable"', () => {
      let result: EditableSetResolution | undefined;
      service.resolveEditableSet('set-unknown').subscribe((r) => (result = r));

      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(targetsResponse());

      expect(result).toEqual({ status: 'notEditable' });
    });

    it('resolves a not-found set to "unavailable" when the list itself was incomplete (sevenTvUnavailable)', () => {
      let result: EditableSetResolution | undefined;
      service.resolveEditableSet('set-unknown').subscribe((r) => (result = r));

      httpMock
        .expectOne('/api/seventv/me/emote-set-targets')
        .flush(targetsResponse({ sevenTvUnavailable: true }));

      expect(result).toEqual({ status: 'unavailable' });
    });

    it('resolves a found, NORMAL set with editable false to "notEditable" (AK 3)', () => {
      let result: EditableSetResolution | undefined;
      service.resolveEditableSet('set-active').subscribe((r) => (result = r));

      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(
        targetsResponse({
          accounts: [
            {
              twitchChannelId: '1',
              twitchLogin: 'handofblood',
              isOwnAccount: true,
              trackedChannelName: 'handofblood',
              activeEmoteSetId: 'set-active',
              sevenTvUserId: 'user-1',
              setsUnavailable: false,
              sets: [
                {
                  id: 'set-active',
                  name: 'Main',
                  capacity: 250,
                  kind: 'NORMAL',
                  isActive: true,
                  isPersonal: false,
                  ownerDisplayName: 'HandOfBlood',
                  ownerSevenTvUserId: 'user-1',
                  editable: false,
                },
              ],
            },
          ],
        }),
      );

      expect(result).toEqual({ status: 'notEditable' });
    });

    it('resolves a not-found set to "unavailable" when the owning account\'s own list was unreadable (setsUnavailable, AK 4)', () => {
      let result: EditableSetResolution | undefined;
      service.resolveEditableSet('set-unknown').subscribe((r) => (result = r));

      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(
        targetsResponse({
          sevenTvUnavailable: false,
          accounts: [
            {
              twitchChannelId: '1',
              twitchLogin: 'handofblood',
              isOwnAccount: true,
              trackedChannelName: 'handofblood',
              activeEmoteSetId: 'set-active',
              sevenTvUserId: 'user-1',
              setsUnavailable: true,
              sets: [],
            },
          ],
        }),
      );

      expect(result).toEqual({ status: 'unavailable' });
    });

    it('serves a second resolveEditableSet call within 60 s from the cache — no second request goes out', () => {
      service.resolveEditableSet('set-active').subscribe();
      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(targetsResponse());

      let result: EditableSetResolution | undefined;
      service.resolveEditableSet('set-active').subscribe((r) => (result = r));

      expect(result).toMatchObject({ status: 'editable' });
      httpMock.expectNone('/api/seventv/me/emote-set-targets');
    });

    it('never caches a failed load — a retry after a 429 always asks again', () => {
      service.resolveEditableSet('set-active').subscribe({ error: () => undefined });
      httpMock
        .expectOne('/api/seventv/me/emote-set-targets')
        .flush({ errorCode: 'rate_limited' }, { status: 429, statusText: 'Too Many Requests' });

      service.resolveEditableSet('set-active').subscribe();
      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(targetsResponse());
    });
  });

  // AK 6: refresh explicitly bypasses the 60 s copy resolveEditableSet and the picker share.
  describe('loadCachedEmoteSetTargets — F3, E19 client-side cache', () => {
    it('a refresh:true call bypasses a warm cache entry and fetches again', () => {
      service.loadCachedEmoteSetTargets().subscribe();
      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(targetsResponse());

      service.loadCachedEmoteSetTargets({ refresh: true }).subscribe();
      httpMock.expectOne('/api/seventv/me/emote-set-targets').flush(targetsResponse());
    });
  });

  // AK 7-8: the two set-centric closing reports (spec 5.1) — the delete/restore/import services
  // (T7) call these instead of the retired channel-bound EmoteAdminService methods.
  describe('reportDeletedInSet — 5.1/6.4/6.5 set-centric bookkeeping', () => {
    it('POSTs sevenTvEmoteIds and expectedChannelName to the set-scoped sync-deleted route', () => {
      let result: SyncDeletedInSetResponse | undefined;
      service
        .reportDeletedInSet('set-x', {
          sevenTvEmoteIds: ['7tv-1', '7tv-2'],
          expectedChannelName: 'handofblood',
        })
        .subscribe((r) => (result = r));

      const req = httpMock.expectOne('/api/seventv/emote-sets/set-x/sync-deleted');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1', '7tv-2'],
        expectedChannelName: 'handofblood',
      });

      const payload: SyncDeletedInSetResponse = {
        reportedCount: 2,
        channels: [{ channelName: 'handofblood', archivedCount: 2, notFoundIds: [] }],
        unresolvedChannel: null,
        resyncTriggered: [],
      };
      req.flush(payload);

      expect(result).toEqual(payload);
    });
  });

  describe('reportRestoredInSet — 5.1/6.4 set-centric bookkeeping', () => {
    it('POSTs sevenTvEmoteIds and expectedChannelName to the set-scoped sync-restored route', () => {
      let result: SyncRestoredInSetResponse | undefined;
      service
        .reportRestoredInSet('set-x', { sevenTvEmoteIds: ['7tv-1'], expectedChannelName: null })
        .subscribe((r) => (result = r));

      const req = httpMock.expectOne('/api/seventv/emote-sets/set-x/sync-restored');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ sevenTvEmoteIds: ['7tv-1'], expectedChannelName: null });

      const payload: SyncRestoredInSetResponse = {
        reportedCount: 1,
        channels: [],
        unresolvedChannel: null,
        resyncTriggered: [],
      };
      req.flush(payload);

      expect(result).toEqual(payload);
    });
  });
});
