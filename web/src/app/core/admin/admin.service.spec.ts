import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  AdminChannelDetail,
  AdminChannelsResult,
  AdminHealth,
  AdminRoster,
  AdminUser,
  RateLimitTelemetrySnapshot,
} from './admin.model';
import { AdminService } from './admin.service';
import { AuditLogEntry } from '../audit/audit.model';
import { PagedResult } from '../models/paged-result.model';

describe('AdminService', () => {
  let service: AdminService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AdminService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('getHealth GETs /api/admin/health', () => {
    const health: AdminHealth = {
      snapshotAvailable: true,
      status: 'connected',
      isConnected: true,
      lastMessageReceivedUtc: '2026-07-31T12:00:00Z',
      connectAttemptedUtc: '2026-07-31T11:00:00Z',
      secondsSinceLastMessage: 12,
      sevenTv: {
        status: 'connected',
        enabled: true,
        connected: true,
        lastFrameUtc: '2026-07-31T12:00:00Z',
        lastDispatchUtc: '2026-07-31T11:59:00Z',
        connectAttemptedUtc: '2026-07-31T11:00:00Z',
        secondsSinceLastFrame: 5,
        desiredChannelCount: 7,
        desiredSubscriptionCount: 14,
        unacknowledgedCount: 0,
        subscriptionLimit: 500,
        resyncIntervalSeconds: 60,
      },
      flush: {
        consecutiveFailures: 0,
        lastSuccessUtc: '2026-07-31T11:59:30Z',
        lastRowCount: 42,
        pendingEmoteCount: 3,
      },
      worker: {
        instanceId: 'a1b2c3d4',
        processStartedUtc: '2026-07-31T09:00:00Z',
      },
    };

    let result: AdminHealth | undefined;
    service.getHealth().subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/health');
    expect(req.request.method).toBe('GET');
    req.flush(health);

    expect(result).toEqual(health);
  });

  it('passes through a snapshot-less response (worker gone: every detail null)', () => {
    // The Api answers with the same shape rather than a shorter error body, so the page renders one
    // layout — this pins that the service does not massage it into something else.
    let result: AdminHealth | undefined;
    service.getHealth().subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/health').flush({
      snapshotAvailable: false,
      status: 'unknown',
      isConnected: false,
      lastMessageReceivedUtc: null,
      connectAttemptedUtc: null,
      secondsSinceLastMessage: null,
      sevenTv: {
        status: 'unknown',
        enabled: false,
        connected: false,
        lastFrameUtc: null,
        lastDispatchUtc: null,
        connectAttemptedUtc: null,
        secondsSinceLastFrame: null,
        desiredChannelCount: null,
        desiredSubscriptionCount: null,
        unacknowledgedCount: null,
        subscriptionLimit: 500,
        resyncIntervalSeconds: null,
      },
      flush: {
        consecutiveFailures: null,
        lastSuccessUtc: null,
        lastRowCount: null,
        pendingEmoteCount: null,
      },
      worker: {
        instanceId: null,
        processStartedUtc: null,
      },
    });

    expect(result?.snapshotAvailable).toBe(false);
    expect(result?.status).toBe('unknown');
    expect(result?.sevenTv.subscriptionLimit).toBe(500);
  });

  it('getRoster GETs /api/admin/roster', () => {
    let result: AdminRoster | undefined;
    service.getRoster().subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/roster');
    expect(req.request.method).toBe('GET');
    req.flush({
      snapshotAvailable: true,
      trackedChannelCount: 3,
      ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 20 },
      generatedAtUtc: '2026-08-01T12:00:00Z',
      ageSeconds: 12,
      bootRecoveryCompleted: true,
      truncated: false,
      rosterChannelCount: 3,
      ircConfirmedCount: 2,
      sevenTvAcknowledgedCount: 3,
      missingFromIrc: ['sensitron'],
      missingFromIrcTotal: 1,
    });

    expect(result?.ircConfirmedCount).toBe(2);
    expect(result?.missingFromIrc).toEqual(['sensitron']);
    expect(result?.ceilings.twitchJoinBudgetChannels).toBe(20);
  });

  it('getRoster passes a snapshot-less response through without inventing zeros', () => {
    // The key expired or the worker never started. Counts stay absent rather than becoming 0 —
    // "the worker is gone" and "the worker is up and has joined nothing" are opposite diagnoses.
    let result: AdminRoster | undefined;
    service.getRoster().subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/roster').flush({
      snapshotAvailable: false,
      trackedChannelCount: 3,
      ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 20 },
    });

    expect(result?.snapshotAvailable).toBe(false);
    expect(result?.ircConfirmedCount).toBeUndefined();
    expect(result?.trackedChannelCount).toBe(3);
  });

  it('getChannel GETs the encoded drilldown route', () => {
    let result: AdminChannelDetail | undefined;
    service.getChannel('HandOf Blood').subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/channels/HandOf%20Blood');
    expect(req.request.method).toBe('GET');
    req.flush({
      channel: {
        channelName: 'handofblood',
        twitchChannelId: '4711',
        isBotActive: true,
        createdAt: '2026-01-01T00:00:00Z',
        emoteCount: 903,
        archivedEmoteCount: 17,
        activeVoteSessionCount: 1,
        voteSessionCount: 4,
        lastSyncedAtUtc: '2026-08-01T11:59:00Z',
        lastInventoryChangeUtc: '2026-05-01T09:00:00Z',
        activeEmoteSetId: '01HSET',
        activeEmoteSetCapacity: 1000,
        lastSyncFailureReason: 'no_active_emote_set',
        lastSyncAttemptAtUtc: '2026-08-01T12:00:00Z',
        trackingResumedAt: null,
        liveState: 'unknown',
      },
      roster: {
        available: true,
        ageSeconds: 30,
        bootRecoveryCompleted: true,
        workerInstanceId: 'a1b2c3d4',
        channel: null,
      },
    });

    // available: true with channel: null is the finding — the worker published a roster and this
    // channel is not in it. The service must not smooth that into "roster unavailable".
    expect(result?.roster.available).toBe(true);
    expect(result?.roster.channel).toBeNull();
    expect(result?.channel.lastSyncedAtUtc).not.toBe(result?.channel.lastInventoryChangeUtc);
    expect(result?.channel.lastSyncFailureReason).toBe('no_active_emote_set');
  });

  it('listChannels GETs /api/admin/channels', () => {
    const channelsResult: AdminChannelsResult = {
      channels: [
        {
          channelName: 'handofblood',
          twitchChannelId: '4711',
          isBotActive: true,
          createdAt: '2026-01-01T00:00:00Z',
          emoteCount: 903,
          archivedEmoteCount: 17,
          activeVoteSessionCount: 1,
          voteSessionCount: 4,
          lastSyncedAtUtc: '2026-07-31T11:55:00Z',
          lastInventoryChangeUtc: '2026-07-31T11:00:00Z',
          activeEmoteSetId: '01HSET',
          activeEmoteSetCapacity: 1000,
          lastSyncFailureReason: null,
          lastSyncAttemptAtUtc: '2026-07-31T11:55:00Z',
          trackingResumedAt: null,
          liveState: 'live',
        },
      ],
      livePolledAtUtc: '2026-08-03T18:00:00Z',
    };

    let result: AdminChannelsResult | undefined;
    service.listChannels().subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/channels');
    expect(req.request.method).toBe('GET');
    req.flush(channelsResult);

    expect(result).toEqual(channelsResult);
  });

  it('listChannels passes a never-synced channel through unchanged (nulls stay nulls)', () => {
    // A freshly joined channel has no emote rows, so lastSyncedAtUtc is null — the service must not
    // coerce that into a date, because "never synced" must stay distinguishable in the UI. Same for
    // livePolledAtUtc: no snapshot means "no statement", not the epoch.
    let result: AdminChannelsResult | undefined;
    service.listChannels().subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/channels').flush({
      channels: [
        {
          channelName: 'freshchannel',
          twitchChannelId: null,
          isBotActive: false,
          createdAt: '2026-07-31T10:00:00Z',
          emoteCount: 0,
          archivedEmoteCount: 0,
          activeVoteSessionCount: 0,
          voteSessionCount: 0,
          lastSyncedAtUtc: null,
          lastInventoryChangeUtc: null,
          activeEmoteSetId: null,
          activeEmoteSetCapacity: null,
          lastSyncFailureReason: null,
          lastSyncAttemptAtUtc: null,
          trackingResumedAt: null,
          liveState: 'unknown',
        },
      ],
      livePolledAtUtc: null,
    });

    expect(result?.channels[0].lastSyncedAtUtc).toBeNull();
    expect(result?.channels[0].twitchChannelId).toBeNull();
    expect(result?.livePolledAtUtc).toBeNull();
  });

  it('listUsers GETs /api/admin/users with paging params', () => {
    const users: AdminUser[] = [
      {
        twitchUserId: '4711',
        twitchUsername: 'handofblood',
        displayName: 'HandOfBlood',
        lastLogin: '2026-07-31T12:00:00Z',
        sessionsValidFromUtc: null,
        hasRefreshToken: true,
        twitchAccessTokenExpiresAtUtc: '2026-07-31T16:00:00Z',
        twitchTokenScopes: 'user:read:email',
      },
    ];
    const page: PagedResult<AdminUser> = {
      items: users,
      page: 2,
      pageSize: 25,
      totalCount: 30,
      totalPages: 2,
    };

    let result: PagedResult<AdminUser> | undefined;
    service.listUsers(2, 25).subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/users?page=2&pageSize=25');
    expect(req.request.method).toBe('GET');
    req.flush(page);

    expect(result).toEqual(page);
    // Derived status only — pinning that no token-looking field sneaks into the contract.
    expect(result?.items[0].hasRefreshToken).toBe(true);
    expect('twitchRefreshToken' in result!.items[0]).toBe(false);
  });

  it('revokeSessions POSTs to the user-scoped revoke endpoint with an empty body', () => {
    let completed = false;
    service.revokeSessions('4711').subscribe(() => (completed = true));

    const req = httpMock.expectOne('/api/admin/users/4711/revoke-sessions');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    req.flush(null);

    expect(completed).toBe(true);
  });

  it('deleteUser DELETEs the user-scoped account-deletion endpoint', () => {
    let completed = false;
    service.deleteUser('4711').subscribe(() => (completed = true));

    const req = httpMock.expectOne('/api/admin/users/4711');
    expect(req.request.method).toBe('DELETE');
    req.flush(null, { status: 204, statusText: 'No Content' });

    expect(completed).toBe(true);
  });

  it('deleteUser encodes the Twitch id into the path', () => {
    // Twitch ids are digits only in practice, but the id is still user-derived data reaching a URL
    // segment — same reasoning as resyncChannel's channel-name encoding below.
    service.deleteUser('47 11').subscribe();

    httpMock
      .expectOne('/api/admin/users/47%2011')
      .flush(null, { status: 204, statusText: 'No Content' });
  });

  it('resyncChannel POSTs to the channel-scoped resync endpoint with an empty body', () => {
    let completed = false;
    service.resyncChannel('handofblood').subscribe(() => (completed = true));

    const req = httpMock.expectOne('/api/admin/channels/handofblood/resync');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    // 202 with no body: "accepted", not "synced" — the service must complete on that all the same.
    req.flush(null, { status: 202, statusText: 'Accepted' });

    expect(completed).toBe(true);
  });

  it('resyncChannel encodes the channel name into the path', () => {
    // Channel names are normalized before they reach here, but the segment is still user-derived —
    // encoding it is what keeps a stray character from re-shaping the URL.
    service.resyncChannel('hand/of blood').subscribe();

    httpMock
      .expectOne('/api/admin/channels/hand%2Fof%20blood/resync')
      .flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('invalidateRoleCache POSTs and passes the removed-entry count through', () => {
    let result: { removedEntries: number } | undefined;
    service.invalidateRoleCache('4711').subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/users/4711/invalidate-role-cache');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    req.flush({ removedEntries: 3 });

    // The count is the action's only visible outcome, so it must survive untouched — including 0,
    // which means "nothing was cached", not "nothing happened".
    expect(result).toEqual({ removedEntries: 3 });
  });

  it('invalidateRoleCache passes a zero count through unchanged', () => {
    let result: { removedEntries: number } | undefined;
    service.invalidateRoleCache('4711').subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/users/4711/invalidate-role-cache').flush({ removedEntries: 0 });

    expect(result?.removedEntries).toBe(0);
  });

  it('listAuditLog GETs /api/admin/audit-log with paging params', () => {
    const entries: AuditLogEntry[] = [
      {
        id: 2,
        occurredAtUtc: '2026-07-31T12:00:00Z',
        actorLogin: 'sensitron',
        action: 'channel.purge',
        channelName: 'handofblood',
        targetType: null,
        targetId: null,
        detail: null,
      },
    ];
    const page: PagedResult<AuditLogEntry> = {
      items: entries,
      page: 2,
      pageSize: 25,
      totalCount: 30,
      totalPages: 2,
    };

    let result: PagedResult<AuditLogEntry> | undefined;
    service.listAuditLog(2, 25).subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/audit-log?page=2&pageSize=25');
    expect(req.request.method).toBe('GET');
    req.flush(page);

    expect(result).toEqual(page);
  });

  it('listAuditLog defaults to the first page with pageSize 25', () => {
    service.listAuditLog().subscribe();

    httpMock.expectOne('/api/admin/audit-log?page=1&pageSize=25').flush({
      items: [],
      page: 1,
      pageSize: 25,
      totalCount: 0,
      totalPages: 0,
    });
  });

  it('listAuditLog appends only the filter params that are set', () => {
    service.listAuditLog(1, 25, { action: 'channel.purge', channel: 'handofblood' }).subscribe();

    // No `actor` param: an unset filter field must not appear as an empty query param, otherwise
    // the backend would treat "" as a filter value and the URL stops being cache-comparable.
    httpMock
      .expectOne('/api/admin/audit-log?page=1&pageSize=25&action=channel.purge&channel=handofblood')
      .flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });
  });

  it('listAuditLog trims text filters and drops blank ones', () => {
    service.listAuditLog(1, 25, { channel: '  HandOfBlood  ', actor: '   ' }).subscribe();

    // Trimming here keeps the URL stable; case-normalization stays server-side next to Regel 9.
    httpMock
      .expectOne('/api/admin/audit-log?page=1&pageSize=25&channel=HandOfBlood')
      .flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });
  });

  it('getRateLimits GETs /api/admin/rate-limits', () => {
    const snapshot: RateLimitTelemetrySnapshot = {
      telemetryAvailable: true,
      policies: [
        {
          name: 'InteractiveRead',
          type: 'token-bucket',
          capacity: 300,
          tokensPerPeriod: 5,
          replenishmentPeriodSeconds: 1,
          windowSeconds: null,
          partition: 'twitch-user',
          queueLimit: 0,
          acceptedLastMinute: 42,
          rejectedLastMinute: 0,
          acceptedLast24Hours: 5000,
          rejectedLast24Hours: 3,
        },
        {
          name: 'ChannelResync',
          type: 'fixed-window',
          capacity: 5,
          tokensPerPeriod: null,
          replenishmentPeriodSeconds: null,
          windowSeconds: 60,
          partition: 'twitch-user',
          queueLimit: 0,
          acceptedLastMinute: 1,
          rejectedLastMinute: 0,
          acceptedLast24Hours: 12,
          rejectedLast24Hours: 0,
        },
      ],
      lastLocalRejection: {
        observedAtUtc: '2026-08-30T12:00:00Z',
        httpMethod: 'POST',
        routeTemplate: '/api/vote-sessions/{sessionId}/votes',
        policyName: 'Voting',
        partition: 'user:4711+session:99',
        retryAfterSeconds: 12,
      },
      caches: [
        {
          cacheName: 'moderated-channels',
          hitsLastMinute: 30,
          missesLastMinute: 1,
          hitsLast24Hours: 4000,
          missesLast24Hours: 50,
        },
      ],
      providers: [
        {
          providerName: 'twitch',
          callSource: 'twitch-helix',
          requestsLastMinute: 8,
          requestsLast24Hours: 900,
          rateLimitedLastMinute: 0,
          rateLimitedLast24Hours: 0,
          lastRetryAfterSeconds: null,
          lastRateLimitedAtUtc: null,
          lastHeaderSample: {
            observedAtUtc: '2026-08-30T11:59:00Z',
            limit: '800',
            remaining: '750',
            reset: '1725019200',
          },
        },
      ],
    };

    let result: RateLimitTelemetrySnapshot | undefined;
    service.getRateLimits().subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/rate-limits');
    expect(req.request.method).toBe('GET');
    req.flush(snapshot);

    expect(result).toEqual(snapshot);
  });

  it('getRateLimits passes a degraded response through without inventing numbers', () => {
    // telemetryAvailable: false means the counter store could not be reached — every count in
    // this shape is a fabricated 0 from the endpoint's `?? 0` fallback, not a measured zero. The
    // service must not massage that away; the page decides how to render it.
    let result: RateLimitTelemetrySnapshot | undefined;
    service.getRateLimits().subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/rate-limits').flush({
      telemetryAvailable: false,
      policies: [
        {
          name: 'InteractiveRead',
          type: 'token-bucket',
          capacity: 300,
          tokensPerPeriod: 5,
          replenishmentPeriodSeconds: 1,
          windowSeconds: null,
          partition: 'twitch-user',
          queueLimit: 0,
          acceptedLastMinute: 0,
          rejectedLastMinute: 0,
          acceptedLast24Hours: 0,
          rejectedLast24Hours: 0,
        },
      ],
      lastLocalRejection: null,
      caches: [],
      providers: [],
    });

    expect(result?.telemetryAvailable).toBe(false);
    expect(result?.caches).toEqual([]);
    expect(result?.providers).toEqual([]);
    expect(result?.policies[0].acceptedLastMinute).toBe(0);
  });

  it('hands the whitelisted detail through as the server projected it', () => {
    // The raw jsonb column never reaches a client: the server reduces it to a closed set of kinds
    // (AuditLogQueryService.ProjectDetail), so neither this service nor the page has to guess what
    // an unknown shape means — or risk rendering something no one meant for this audience.
    let result: PagedResult<AuditLogEntry> | undefined;
    service.listAuditLog(1, 25).subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/audit-log?page=1&pageSize=25').flush({
      items: [
        {
          id: 1,
          occurredAtUtc: '2026-07-31T11:00:00Z',
          actorLogin: 'sensitron',
          action: 'emotes.syncDeleted',
          channelName: 'handofblood',
          targetType: null,
          targetId: null,
          detail: { kind: 'emoteCount', count: 12, text: null },
        },
      ],
      page: 1,
      pageSize: 25,
      totalCount: 1,
      totalPages: 1,
    });

    expect(result?.items[0].detail).toEqual({ kind: 'emoteCount', count: 12, text: null });
  });
});
