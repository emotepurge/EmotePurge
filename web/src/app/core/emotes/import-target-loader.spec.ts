import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { SevenTvEmoteSetService } from '../seven-tv/seven-tv-emote-set.service';
import { EmoteAdminService } from './emote-admin.service';
import { ImportTargetLoadState, loadImportTarget } from './import-target-loader';

const STATUS_URL = '/api/channels/sensitron/emotes/active-set';
const EMOTES_URL = '/api/channels/sensitron/emotes';
const WARNING_URL = '/api/channels/sensitron/emotes/set-warning';
const LIVE_URL = '/api/seventv/channels/sensitron/emotes';

const READY_STATUS = {
  activeEmoteSetId: 'set-1',
  capacity: 1000,
  occupiedSlots: 847,
  trackedSince: '2026-06-12T09:14:00Z',
  syncFailureReason: null,
  lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
  botsExcludedSince: null,
};

const READY_WARNING = {
  available: true,
  isOwnSet: true,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

const UNAVAILABLE_WARNING = {
  available: false,
  isOwnSet: false,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

const LIVE_PREVIEW = {
  channelName: 'sensitron',
  sevenTvUserId: null,
  emoteSetId: 'set-halloween',
  emoteSetName: 'Halloween',
  capacity: 500,
  totalCount: 338,
  truncated: false,
  emotes: [
    {
      sevenTvEmoteId: '7tv-1',
      name: 'SpookyU',
      imageUrl: 'https://cdn.7tv.app/emote/7tv-1/2x.webp',
    },
  ],
};

describe('loadImportTarget', () => {
  let emoteAdminService: EmoteAdminService;
  let emoteSetService: SevenTvEmoteSetService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    emoteAdminService = TestBed.inject(EmoteAdminService);
    emoteSetService = TestBed.inject(SevenTvEmoteSetService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('trackedActive — the "today" path (AK 36: no other request)', () => {
    it('emits ready with all fields once all three requests succeed, never synchronously', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedActive',
        channelName: 'sensitron',
      }).subscribe((value) => (result = value));

      // The dialog opens on `loading` and fills in later — the loader must not resolve before any
      // response has come back, so it must not be observable synchronously right after subscribe.
      expect(result).toBeUndefined();

      httpMock.expectOne(STATUS_URL).flush(READY_STATUS);
      httpMock.expectOne(EMOTES_URL).flush({
        emotes: [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }],
      });
      httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

      expect(result).toEqual({
        status: 'ready',
        setId: 'set-1',
        setName: null,
        occupiedSlots: 847,
        capacity: 1000,
        syncFailureReason: null,
        emotes: [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }],
        warning: READY_WARNING,
      });
    });

    it('emits ready with an unavailable warning when only getSetWarning fails', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedActive',
        channelName: 'sensitron',
      }).subscribe((value) => (result = value));

      httpMock.expectOne(STATUS_URL).flush(READY_STATUS);
      httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
      httpMock.expectOne(WARNING_URL).flush('boom', { status: 500, statusText: 'Server Error' });

      expect(result).toEqual({
        status: 'ready',
        setId: 'set-1',
        setName: null,
        occupiedSlots: 847,
        capacity: 1000,
        syncFailureReason: null,
        emotes: [],
        warning: UNAVAILABLE_WARNING,
      });
    });

    it('emits no-set when getSetStatus 404s', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedActive',
        channelName: 'sensitron',
      }).subscribe((value) => (result = value));

      httpMock.expectOne(STATUS_URL).flush('not found', { status: 404, statusText: 'Not Found' });
      httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
      httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

      expect(result).toEqual({ status: 'no-set' });
    });

    it('emits failed when listEmotes fails with a non-404 status', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedActive',
        channelName: 'sensitron',
      }).subscribe((value) => (result = value));

      httpMock.expectOne(STATUS_URL).flush(READY_STATUS);
      httpMock.expectOne(EMOTES_URL).flush('boom', { status: 500, statusText: 'Server Error' });
      httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

      expect(result).toEqual({ status: 'failed' });
    });

    it('emits no-set when getSetStatus succeeds with an empty active set id', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedActive',
        channelName: 'sensitron',
      }).subscribe((value) => (result = value));

      httpMock.expectOne(STATUS_URL).flush({
        activeEmoteSetId: '',
        capacity: null,
        occupiedSlots: 0,
        trackedSince: '2026-06-12T09:14:00Z',
        syncFailureReason: null,
        lastSyncAttemptAtUtc: null,
        botsExcludedSince: null,
      });
      httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
      httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

      expect(result).toEqual({ status: 'no-set' });
    });

    it('prefers no-set over failed when listEmotes 404s and getSetStatus fails with a 500', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedActive',
        channelName: 'sensitron',
      }).subscribe((value) => (result = value));

      httpMock.expectOne(STATUS_URL).flush('boom', { status: 500, statusText: 'Server Error' });
      httpMock.expectOne(EMOTES_URL).flush('not found', { status: 404, statusText: 'Not Found' });
      httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

      expect(result).toEqual({ status: 'no-set' });
    });

    it('emits a single failed value and completes without erroring when all three requests fail', () => {
      let nextCount = 0;
      let result: ImportTargetLoadState | undefined;
      let completed = false;
      let erroredValue: unknown;

      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedActive',
        channelName: 'sensitron',
      }).subscribe({
        next: (value) => {
          nextCount++;
          result = value;
        },
        error: (error) => {
          erroredValue = error;
        },
        complete: () => {
          completed = true;
        },
      });

      httpMock.expectOne(STATUS_URL).flush('boom', { status: 500, statusText: 'Server Error' });
      httpMock.expectOne(EMOTES_URL).flush('boom', { status: 500, statusText: 'Server Error' });
      httpMock.expectOne(WARNING_URL).flush('boom', { status: 500, statusText: 'Server Error' });

      expect(nextCount).toBe(1);
      expect(result).toEqual({ status: 'failed' });
      expect(completed).toBe(true);
      expect(erroredValue).toBeUndefined();
    });

    it('supports a retry by calling it again, with no state to clear first', () => {
      const results: ImportTargetLoadState[] = [];
      const selection = { kind: 'trackedActive' as const, channelName: 'sensitron' };

      loadImportTarget(emoteAdminService, emoteSetService, selection).subscribe((value) =>
        results.push(value),
      );
      httpMock.expectOne(STATUS_URL).flush('boom', { status: 500, statusText: 'Server Error' });
      httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
      httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

      expect(results).toEqual([{ status: 'failed' }]);

      loadImportTarget(emoteAdminService, emoteSetService, selection).subscribe((value) =>
        results.push(value),
      );
      httpMock.expectOne(STATUS_URL).flush(READY_STATUS);
      httpMock.expectOne(EMOTES_URL).flush({ emotes: [] });
      httpMock.expectOne(WARNING_URL).flush(READY_WARNING);

      expect(results[1]).toEqual({
        status: 'ready',
        setId: 'set-1',
        setName: null,
        occupiedSlots: 847,
        capacity: 1000,
        syncFailureReason: null,
        emotes: [],
        warning: READY_WARNING,
      });
    });
  });

  describe('trackedSet — a tracked channel, not its active set (spec F5, AK 36)', () => {
    it('reads occupancy, capacity and the set name from the live list, not EmoteSetStatus', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedSet',
        channelName: 'sensitron',
        emoteSetId: 'set-halloween',
      }).subscribe((value) => (result = value));

      // No EmoteSetStatus / plain listEmotes call at all for this class — only the live route.
      const req = httpMock.expectOne(
        (candidate) =>
          candidate.url === LIVE_URL && candidate.params.get('emoteSetId') === 'set-halloween',
      );
      req.flush(LIVE_PREVIEW);
      httpMock
        .expectOne(
          (candidate) =>
            candidate.url === WARNING_URL && candidate.params.get('emoteSetId') === 'set-halloween',
        )
        .flush(READY_WARNING);

      expect(result).toEqual({
        status: 'ready',
        setId: 'set-halloween',
        setName: 'Halloween',
        occupiedSlots: 338,
        capacity: 500,
        syncFailureReason: null,
        emotes: [
          {
            sevenTvEmoteId: '7tv-1',
            name: 'SpookyU',
            imageUrl: 'https://cdn.7tv.app/emote/7tv-1/2x.webp',
          },
        ],
        warning: READY_WARNING,
      });
    });

    it('carries the imageUrl of each live emote through untouched', () => {
      // import-target-loader.ts used to build a fresh { sevenTvEmoteId, name } pair here and
      // discard everything else the live route sent — the confirm dialog's side-by-side
      // source/target preview needs the image too.
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedSet',
        channelName: 'sensitron',
        emoteSetId: 'set-halloween',
      }).subscribe((value) => (result = value));

      httpMock
        .expectOne((candidate) => candidate.url === LIVE_URL)
        .flush({
          ...LIVE_PREVIEW,
          emotes: [
            {
              sevenTvEmoteId: '7tv-9',
              name: 'PumpkinPog',
              imageUrl: 'https://cdn.7tv.app/emote/7tv-9/4x.webp',
            },
          ],
        });
      httpMock.expectOne((candidate) => candidate.url === WARNING_URL).flush(READY_WARNING);

      expect(result).toMatchObject({
        status: 'ready',
        emotes: [
          {
            sevenTvEmoteId: '7tv-9',
            name: 'PumpkinPog',
            imageUrl: 'https://cdn.7tv.app/emote/7tv-9/4x.webp',
          },
        ],
      });
    });

    it('treats a truncated live list as failed, not as a smaller-but-usable answer', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedSet',
        channelName: 'sensitron',
        emoteSetId: 'set-halloween',
      }).subscribe((value) => (result = value));

      httpMock
        .expectOne((candidate) => candidate.url === LIVE_URL)
        .flush({ ...LIVE_PREVIEW, truncated: true });
      httpMock.expectOne((candidate) => candidate.url === WARNING_URL).flush(READY_WARNING);

      expect(result).toEqual({ status: 'failed' });
    });

    it('emits no-set when the live list 404s (7TV no longer knows this set)', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedSet',
        channelName: 'sensitron',
        emoteSetId: 'set-halloween',
      }).subscribe((value) => (result = value));

      httpMock
        .expectOne((candidate) => candidate.url === LIVE_URL)
        .flush('not found', { status: 404, statusText: 'Not Found' });
      httpMock.expectOne((candidate) => candidate.url === WARNING_URL).flush(READY_WARNING);

      expect(result).toEqual({ status: 'no-set' });
    });

    it('emits failed on a genuine transport error from the live list', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'trackedSet',
        channelName: 'sensitron',
        emoteSetId: 'set-halloween',
      }).subscribe((value) => (result = value));

      httpMock
        .expectOne((candidate) => candidate.url === LIVE_URL)
        .flush('boom', { status: 500, statusText: 'Server Error' });
      httpMock.expectOne((candidate) => candidate.url === WARNING_URL).flush(READY_WARNING);

      expect(result).toEqual({ status: 'failed' });
    });
  });

  describe('untrackedSet — an account with no channel at all (spec 8.6, AK 36)', () => {
    it('never checks set-warning — the unavailable fallback costs no request', () => {
      let result: ImportTargetLoadState | undefined;
      loadImportTarget(emoteAdminService, emoteSetService, {
        kind: 'untrackedSet',
        channelName: 'sensitron',
        emoteSetId: 'set-halloween',
      }).subscribe((value) => (result = value));

      httpMock.expectOne((candidate) => candidate.url === LIVE_URL).flush(LIVE_PREVIEW);
      // httpMock.verify() in afterEach fails the test outright if a set-warning request is
      // outstanding — this is deliberately not asserted a second time here.

      expect(result).toEqual({
        status: 'ready',
        setId: 'set-halloween',
        setName: 'Halloween',
        occupiedSlots: 338,
        capacity: 500,
        syncFailureReason: null,
        emotes: [
          {
            sevenTvEmoteId: '7tv-1',
            name: 'SpookyU',
            imageUrl: 'https://cdn.7tv.app/emote/7tv-1/2x.webp',
          },
        ],
        warning: UNAVAILABLE_WARNING,
      });
    });
  });
});
