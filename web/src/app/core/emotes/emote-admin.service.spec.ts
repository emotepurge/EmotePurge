import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { EmoteAdminService, SyncDeletedResult } from './emote-admin.service';
import { EmoteListItem } from './emote-list-item.model';
import { EmoteSetStatus } from './emote-set-status.model';

describe('EmoteAdminService', () => {
  let service: EmoteAdminService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(EmoteAdminService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('syncDeleted POSTs the set and its 7TV ids to the sync-deleted endpoint', () => {
    service
      .syncDeleted('sensitron', { emoteSetId: 'set-1', sevenTvEmoteIds: ['a', 'b'] })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['a', 'b'] });
    req.flush({ archivedCount: 2, notFoundIds: [], targetIsActiveSetOfChannel: true });
  });

  it('syncRestored POSTs the set and its 7TV ids to the sync-restored endpoint', () => {
    service
      .syncRestored('sensitron', { emoteSetId: 'set-1', sevenTvEmoteIds: ['a', 'b'] })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-restored');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['a', 'b'] });
    req.flush({ restoredCount: 2, notFoundIds: [], targetIsActiveSetOfChannel: true });
  });

  // Spec #200, 6.6: a report for a non-active set is paper only — the flag is what tells the
  // caller that `archivedCount: 0` is the expected answer, so it must reach it unchanged.
  it('syncDeleted passes targetIsActiveSetOfChannel through', () => {
    let answer: SyncDeletedResult | undefined;
    service
      .syncDeleted('sensitron', { emoteSetId: 'set-2', sevenTvEmoteIds: ['a'] })
      .subscribe((value) => (answer = value));

    httpMock
      .expectOne('/api/channels/sensitron/emotes/sync-deleted')
      .flush({ archivedCount: 0, notFoundIds: [], targetIsActiveSetOfChannel: false });

    expect(answer).toEqual({
      archivedCount: 0,
      notFoundIds: [],
      targetIsActiveSetOfChannel: false,
    });
  });

  // E3: the server keeps the legacy `{ emoteIds }` body for tabs opened before the deploy; this
  // client never sends it, whatever object it is handed.
  it('never sends the legacy emoteIds field, even when the caller passes one along', () => {
    const body = { emoteSetId: 'set-1', sevenTvEmoteIds: ['a'], emoteIds: ['guid-a'] };
    service.syncRestored('sensitron', body).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-restored');
    expect(req.request.body.emoteIds).toBeUndefined();
    req.flush({ restoredCount: 1, notFoundIds: [], targetIsActiveSetOfChannel: true });
  });

  it('getSetWarning GETs the set-warning endpoint', () => {
    service.getSetWarning('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/set-warning');
    expect(req.request.method).toBe('GET');
    req.flush({
      available: true,
      isOwnSet: true,
      otherTrackedChannelsSharingSet: [],
      otherModeratedChannelsSharingSet: [],
    });
  });

  it('getSetStatus GETs the active-set endpoint', () => {
    let status: EmoteSetStatus | undefined;
    service.getSetStatus('sensitron').subscribe((value) => (status = value));

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/active-set');
    expect(req.request.method).toBe('GET');
    req.flush({
      activeEmoteSetId: 'set-1',
      capacity: 1000,
      occupiedSlots: 847,
      trackedSince: '2026-06-12T09:14:00Z',
      syncFailureReason: null,
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
      botsExcludedSince: '2026-09-01',
      sharedChatSeparatedSince: '2026-09-07',
    });

    expect(status).toEqual({
      activeEmoteSetId: 'set-1',
      capacity: 1000,
      occupiedSlots: 847,
      trackedSince: '2026-06-12T09:14:00Z',
      syncFailureReason: null,
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
      botsExcludedSince: '2026-09-01',
      sharedChatSeparatedSince: '2026-09-07',
    });
  });

  it('getSetStatus passes a sync failure reason through untranslated', () => {
    // The code must reach the page verbatim: translation happens exactly once, in the template
    // (Regel 7), and a service that mapped it to prose here would put German into the model.
    let status: EmoteSetStatus | undefined;
    service.getSetStatus('sensitron').subscribe((value) => (status = value));

    httpMock.expectOne('/api/channels/sensitron/emotes/active-set').flush({
      activeEmoteSetId: '',
      capacity: null,
      occupiedSlots: 0,
      trackedSince: '2026-06-12T09:14:00Z',
      syncFailureReason: 'no_active_emote_set',
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
      botsExcludedSince: null,
    });

    expect(status?.syncFailureReason).toBe('no_active_emote_set');
  });

  it('listEmotes GETs the emotes endpoint and unwraps the response', () => {
    let result: EmoteListItem[] | undefined;
    service.listEmotes('sensitron').subscribe((value) => (result = value));

    const req = httpMock.expectOne('/api/channels/sensitron/emotes');
    expect(req.request.method).toBe('GET');
    req.flush({
      emotes: [
        { sevenTvEmoteId: '7tv-1', name: 'ApuDrums' },
        { sevenTvEmoteId: '7tv-2', name: 'PogChamp' },
      ],
    });

    expect(result).toEqual([
      { sevenTvEmoteId: '7tv-1', name: 'ApuDrums' },
      { sevenTvEmoteId: '7tv-2', name: 'PogChamp' },
    ]);
  });

  it('syncImported POSTs the exact body to the sync-imported endpoint', () => {
    service
      .syncImported('sensitron', {
        sevenTvEmoteIds: ['7tv-1', '7tv-2'],
        sourceChannelName: 'other-channel',
        sourceKind: 'channel',
        leaderboardSort: null,
        targetEmoteSetId: 'set-1',
      })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-imported');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: 'other-channel',
      sourceKind: 'channel',
      leaderboardSort: null,
      targetEmoteSetId: 'set-1',
    });
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('syncImported sends an explicit null sourceChannelName for a file import', () => {
    service
      .syncImported('sensitron', {
        sevenTvEmoteIds: ['7tv-1'],
        sourceChannelName: null,
        sourceKind: 'file',
        leaderboardSort: null,
        targetEmoteSetId: 'set-1',
      })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-imported');
    expect(req.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      sourceChannelName: null,
      sourceKind: 'file',
      leaderboardSort: null,
      targetEmoteSetId: 'set-1',
    });
    expect('sourceChannelName' in req.request.body).toBe(true);
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('syncImported sends the leaderboard sort and no source channel for a leaderboard import', () => {
    service
      .syncImported('sensitron', {
        sevenTvEmoteIds: ['7tv-1'],
        sourceChannelName: null,
        sourceKind: 'seventv-leaderboard',
        leaderboardSort: 'TOP_ALL_TIME',
        targetEmoteSetId: 'set-1',
      })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-imported');
    expect(req.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      sourceChannelName: null,
      sourceKind: 'seventv-leaderboard',
      leaderboardSort: 'TOP_ALL_TIME',
      targetEmoteSetId: 'set-1',
    });
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  // AK 44: this client always knows the set it just wrote into by the time it reports — every
  // syncImported call carries it, active target or not, tracked or not (the server keeps the field
  // optional forever for an older client, spec 6.7/E5 — that leniency is not this client's excuse).
  it('always sends targetEmoteSetId, even for a target that is not the channel active set', () => {
    service
      .syncImported('sensitron', {
        sevenTvEmoteIds: ['7tv-1'],
        sourceChannelName: 'sensitron',
        sourceKind: 'channel',
        leaderboardSort: null,
        targetEmoteSetId: 'set-halloween',
      })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-imported');
    expect(req.request.body.targetEmoteSetId).toBe('set-halloween');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('getSetWarning GETs the same set-warning URL with no query when emoteSetId is omitted', () => {
    // AK 36's "no other request" guarantee for the tracked-active path depends on this: passing no
    // emoteSetId must not append an empty query string that the loader's existing tests would fail
    // to match.
    service.getSetWarning('sensitron').subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/set-warning');
    expect(req.request.params.has('emoteSetId')).toBe(false);
    req.flush({
      available: true,
      isOwnSet: true,
      otherTrackedChannelsSharingSet: [],
      otherModeratedChannelsSharingSet: [],
    });
  });

  it('getSetWarning appends emoteSetId when checking a specific, possibly non-active set', () => {
    service.getSetWarning('sensitron', 'set-halloween').subscribe();

    const req = httpMock.expectOne(
      (candidate) =>
        candidate.url === '/api/channels/sensitron/emotes/set-warning' &&
        candidate.params.get('emoteSetId') === 'set-halloween',
    );
    req.flush({
      available: true,
      isOwnSet: true,
      otherTrackedChannelsSharingSet: [],
      otherModeratedChannelsSharingSet: [],
    });
  });
});
