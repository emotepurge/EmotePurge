import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { DuplicateEmoteName } from './duplicate-emote-name.model';
import { EmoteAdminService } from './emote-admin.service';
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

  it('syncDeleted POSTs emoteIds to the sync-deleted endpoint', () => {
    service.syncDeleted('sensitron', ['a', 'b']).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ emoteIds: ['a', 'b'] });
    req.flush({ archivedCount: 2, notFoundIds: [] });
  });

  it('syncRestored POSTs emoteIds to the sync-restored endpoint', () => {
    service.syncRestored('sensitron', ['a', 'b']).subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-restored');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ emoteIds: ['a', 'b'] });
    req.flush({ restoredCount: 2, notFoundIds: [] });
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

  it('getDuplicateNames GETs the duplicate-names endpoint', () => {
    let result: DuplicateEmoteName[] | undefined;
    service.getDuplicateNames('sensitron').subscribe((value) => (result = value));

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/duplicate-names');
    expect(req.request.method).toBe('GET');
    req.flush([
      {
        name: 'ApuDrums',
        emotes: [
          { emoteId: 'id-1', sevenTvEmoteId: '7tv-1', imageUrl: 'https://cdn/1.webp' },
          { emoteId: 'id-2', sevenTvEmoteId: '7tv-2', imageUrl: 'https://cdn/2.webp' },
        ],
      },
    ]);

    expect(result).toHaveLength(1);
    expect(result?.[0].name).toBe('ApuDrums');
    expect(result?.[0].emotes).toHaveLength(2);
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
      })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-imported');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: 'other-channel',
      sourceKind: 'channel',
      leaderboardSort: null,
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
      })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-imported');
    expect(req.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      sourceChannelName: null,
      sourceKind: 'file',
      leaderboardSort: null,
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
      })
      .subscribe();

    const req = httpMock.expectOne('/api/channels/sensitron/emotes/sync-imported');
    expect(req.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      sourceChannelName: null,
      sourceKind: 'seventv-leaderboard',
      leaderboardSort: 'TOP_ALL_TIME',
    });
    req.flush(null, { status: 204, statusText: 'No Content' });
  });
});
