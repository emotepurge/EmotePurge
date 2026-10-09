import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ChannelPermissions, ChannelStatus, MyChannelsResult } from './channel.model';
import { ChannelService } from './channel.service';

const PERMISSIONS: ChannelPermissions = {
  canManage: false,
  canViewUsageStats: true,
  isGlobalAdmin: false,
  isTracked: true,
  isBotActive: false,
  tagRunsEnabled: false,
  canPurgeAsBroadcaster: false,
  chatLogBackfillEnabled: false,
};

describe('ChannelService', () => {
  let service: ChannelService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ChannelService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('getStatus GETs /api/channels/{channelName}', () => {
    const status: ChannelStatus = {
      channelId: '1',
      channelName: 'sensitron',
      isBotActive: true,
      activeEmoteSetId: 'set1',
    };

    let result: ChannelStatus | undefined;
    service.getStatus('sensitron').subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/channels/sensitron');
    expect(req.request.method).toBe('GET');
    req.flush(status);

    expect(result).toEqual(status);
  });

  it('getPermissions GETs /api/channels/{channelName}/permissions', () => {
    let result: ChannelPermissions | undefined;
    service.getPermissions('sensitron').subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/channels/sensitron/permissions');
    expect(req.request.method).toBe('GET');
    req.flush(PERMISSIONS);

    expect(result).toEqual(PERMISSIONS);
  });

  // The reason this cache exists: opening a channel asks three times (guard, workspace layout,
  // page resource) and each request costs a live Twitch/7TV role check plus one of 40 permits per
  // minute. Without the sharing below, ordinary clicking-around answers 429.
  describe('getPermissions caching', () => {
    it('serves concurrent readers from a single in-flight request', () => {
      const results: ChannelPermissions[] = [];
      service.getPermissions('sensitron').subscribe((r) => results.push(r));
      service.getPermissions('sensitron').subscribe((r) => results.push(r));

      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

      expect(results).toEqual([PERMISSIONS, PERMISSIONS]);
    });

    it('serves a later reader from the cache without a second request', () => {
      service.getPermissions('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

      let result: ChannelPermissions | undefined;
      service.getPermissions('sensitron').subscribe((r) => (result = r));

      expect(result).toEqual(PERMISSIONS);
      httpMock.expectNone('/api/channels/sensitron/permissions');
    });

    it('keys on the normalized name, so mixed-case navigation shares the entry (Regel 9)', () => {
      service.getPermissions('HandOfBlood').subscribe();
      httpMock.expectOne('/api/channels/HandOfBlood/permissions').flush(PERMISSIONS);

      let result: ChannelPermissions | undefined;
      service.getPermissions('handofblood').subscribe((r) => (result = r));

      expect(result).toEqual(PERMISSIONS);
      httpMock.expectNone('/api/channels/handofblood/permissions');
    });

    it('refetches once the entry has expired', () => {
      vi.useFakeTimers();
      try {
        service.getPermissions('sensitron').subscribe();
        httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

        vi.advanceTimersByTime(30_001);

        service.getPermissions('sensitron').subscribe();
        httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);
      } finally {
        vi.useRealTimers();
      }
    });

    // shareReplay replays a terminal error to every later subscriber and never re-subscribes the
    // source — a cached failure would lock the channel out for the whole TTL.
    it('does not cache a failure', () => {
      service.getPermissions('sensitron').subscribe({ error: () => undefined });
      httpMock
        .expectOne('/api/channels/sensitron/permissions')
        .flush(null, { status: 500, statusText: 'Server Error' });

      let result: ChannelPermissions | undefined;
      service.getPermissions('sensitron').subscribe((r) => (result = r));
      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

      expect(result).toEqual(PERMISSIONS);
    });

    it('invalidatePermissions() drops one channel, or all of them without a name', () => {
      service.getPermissions('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

      service.invalidatePermissions('sensitron');
      service.getPermissions('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

      service.invalidatePermissions();
      service.getPermissions('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);
    });

    it('join and leave invalidate the channel they change', () => {
      service.getPermissions('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

      service.join('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron/join').flush({});

      service.getPermissions('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

      service.leave('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron').flush(null);

      service.getPermissions('sensitron').subscribe();
      httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);
    });
  });

  it('join POSTs to /api/channels/{channelName}/join with an empty body', () => {
    service.join('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/join');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({});
  });

  it('join with liftBroadcasterLock sends the flag and the confirmed lock date verbatim', () => {
    // Verbatim: the server compares the date tick for tick, so it must not pass through Date.
    service
      .join('sensitron', {
        liftBroadcasterLock: { confirmedLockedAtUtc: '2026-10-01T10:00:00.123456Z' },
      })
      .subscribe();

    const req = httpMock.expectOne((r) => r.url === '/api/channels/sensitron/join');
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('liftBroadcasterLock')).toBe('true');
    expect(req.request.params.get('confirmedLockedAtUtc')).toBe('2026-10-01T10:00:00.123456Z');
    req.flush({});
  });

  it('purgeOwnData DELETEs /data with the expected Twitch id and drops the cached permissions', () => {
    service.getPermissions('sensitron').subscribe();
    httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);

    service.purgeOwnData('sensitron', '123').subscribe();
    const req = httpMock.expectOne('/api/channels/sensitron/data?expectedTwitchUserId=123');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    service.getPermissions('sensitron').subscribe();
    httpMock.expectOne('/api/channels/sensitron/permissions').flush(PERMISSIONS);
  });

  it('getDataSummary GETs /api/channels/{channelName}/data-summary', () => {
    let result: unknown;
    service.getDataSummary('sensitron').subscribe((summary) => (result = summary));

    const req = httpMock.expectOne('/api/channels/sensitron/data-summary');
    expect(req.request.method).toBe('GET');
    const body = { emoteCount: 1, voteSessionCount: 2, liveDayCount: 3, tagCount: 4 };
    req.flush(body);
    expect(result).toEqual(body);
  });

  it('leave DELETEs /api/channels/{channelName}', () => {
    service.leave('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  it('purge DELETEs /api/channels/{channelName}/purge', () => {
    // Distinct from leave(): same verb, different path — a regression that dropped the /purge
    // suffix would silently downgrade an irreversible purge into a harmless deactivation.
    service.purge('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/purge');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  it('listMine GETs /api/channels/mine', () => {
    const result: MyChannelsResult = {
      helixUnavailable: false,
      reauthRequired: false,
      sevenTvUnavailable: false,
      channels: [],
      livePolledAtUtc: null,
    };
    service.listMine().subscribe();

    const req = httpMock.expectOne('/api/channels/mine');
    expect(req.request.method).toBe('GET');
    req.flush(result);
  });
});
