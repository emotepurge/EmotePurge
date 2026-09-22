import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ABORTED_DELETE_NOTICE_MS,
  DELETE_DELAY_MS,
  DeleteQueueEmote,
  SevenTvDeleteService,
} from './seven-tv-delete.service';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { SevenTvTokenService } from './seven-tv-token.service';

// Only the keys this service actually translates — not the full app translation file.
const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig oder abgelaufen — bitte neues 7TV-Token eintragen.',
      rateLimited: 'Zu viele Anfragen an 7TV (Rate Limit) — später erneut versuchen.',
      networkError: 'Keine Verbindung zu 7TV möglich (Netzwerkfehler).',
      genericStatus: '7TV-Fehler (Status {{ status }}).',
      rateLimitedGaveUp:
        '7TV-Rate-Limit auch nach mehreren Wartezyklen aktiv — Emote übersprungen.',
    },
  },
};

/** 7TV rejects a rate-limited mutation with HTTP 200 and the details inside `errors[0].extensions`
 *  — the response headers themselves are not CORS-exposed, so this payload is all a browser gets. */
function rateLimitResponse(resetSeconds: number, limit: number | null = 100) {
  const headers: Record<string, string> = {
    'x-ratelimit-emote_set_change-remaining': '0',
    'x-ratelimit-emote_set_change-reset': String(resetSeconds),
    'x-ratelimit-emote_set_change-used': '101',
  };
  if (limit !== null) {
    headers['x-ratelimit-emote_set_change-limit'] = String(limit);
  }
  return {
    errors: [
      {
        message: 'RATE_LIMIT_EXCEEDED rate limit exceeded',
        extensions: { code: 'RATE_LIMIT_EXCEEDED', status: 429, headers },
      },
    ],
  };
}

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';
const SYNC_ENDPOINT = '/api/channels/sensitron/emotes/sync-deleted';

const EMOTES: DeleteQueueEmote[] = [
  { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

describe('SevenTvDeleteService', () => {
  let service: SevenTvDeleteService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    // The service reports every run's measured rate here — silenced so the suite stays readable,
    // asserted in the measurement test below.
    vi.spyOn(console, 'info').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    // Translations load asynchronously even with the synchronous TestingLoader — without this,
    // a translate() call in the same tick as a test's assertions would still see no data loaded
    // yet and fall back to returning the raw key.
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    service = TestBed.inject(SevenTvDeleteService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  // Drives one emote all the way through the 7TV queue and returns the closing sync-deleted request,
  // which is what the sync-report tests below are actually about.
  function runOneDeleteToSyncRequest() {
    service.startDelete('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    return httpMock.expectOne(SYNC_ENDPOINT);
  }

  describe('sync report', () => {
    it('reports success only when the backend archived everything', () => {
      runOneDeleteToSyncRequest().flush({ archivedCount: 1, notFoundIds: [] });

      expect(service.syncReport()).toBe('succeeded');
    });

    it('treats an under-count as partial — all ids in notFoundIds used to look like success', () => {
      runOneDeleteToSyncRequest().flush({
        archivedCount: 0,
        notFoundIds: ['7tv-1'],
        targetIsActiveSetOfChannel: true,
      });

      expect(service.syncReport()).toBe('partial');
    });

    it('retries a transient failure and succeeds on the second attempt', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 429, statusText: 'Too Many Requests' });
      expect(service.syncReport()).toBe('pending');

      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 1, notFoundIds: [] });

      expect(service.syncReport()).toBe('succeeded');
    });

    it('gives up after the automatic retries are exhausted', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 500, statusText: 'Server Error' });

      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 500, statusText: 'Server Error' });

      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 500, statusText: 'Server Error' });

      expect(service.syncReport()).toBe('failed');
    });

    it('does not retry a 401 — an expired session cannot be fixed by waiting', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(service.syncReport()).toBe('failed');
      vi.advanceTimersByTime(10_000);
      httpMock.verify(); // no further attempt was made
    });

    it('retrySyncReport() re-sends the same ids after a failure', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });

      service.retrySyncReport();

      const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(retryReq.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['7tv-1'] });
      retryReq.flush({ archivedCount: 1, notFoundIds: [] });

      expect(service.syncReport()).toBe('succeeded');
    });

    it('reset() clears the sync report as well', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });

      service.reset();

      expect(service.syncReport()).toBe('idle');
    });
  });

  it('does nothing without a stored token', () => {
    tokenService.clearToken();

    service.startDelete('set-1', 'sensitron', EMOTES);

    expect(service.isRunning()).toBe(false);
    expect(service.queue()).toEqual([]);
  });

  it('does nothing when the emote list is empty', () => {
    service.startDelete('set-1', 'sensitron', []);

    expect(service.isRunning()).toBe(false);
  });

  it('keys every queue row by its 7TV id', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    expect(service.queue().map((item) => item.key)).toEqual(['7tv-1', '7tv-2']);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 2, notFoundIds: [] });
  });

  it('deletes emotes sequentially with a delay, then syncs the archived ids', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    expect(service.isRunning()).toBe(true);
    expect(service.queue().map((i) => i.status)).toEqual(['in-progress', 'pending']);

    const req1 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req1.request.headers.get('Authorization')).toBe('Bearer write-token');
    expect(req1.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
    req1.flush({});

    expect(service.queue()[0].status).toBe('done');
    expect(service.queue()[1].status).toBe('pending');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    expect(service.queue()[1].status).toBe('in-progress');

    const req2 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req2.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-2' });
    req2.flush({});

    vi.advanceTimersByTime(DELETE_DELAY_MS);

    expect(service.isRunning()).toBe(false);
    expect(service.progress()).toEqual({ finished: 2, total: 2 });

    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({
      emoteSetId: 'set-1',
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
    });
    syncReq.flush({ archivedCount: 2, notFoundIds: [] });
  });

  it('marks a GraphQL error response as failed and continues the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'emote not found' }] });

    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toBe('emote not found');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    // Only the successful one gets synced back to Postgres.
    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['7tv-2'] });
    syncReq.flush({ archivedCount: 1, notFoundIds: [] });
  });

  describe('7TV rate limiting', () => {
    it('waits out the reported reset and retries the same emote instead of failing it', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]]);

      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(30));

      // Still in flight, not burnt — and the pause is visible rather than looking like a hang.
      expect(service.queue()[0].status).toBe('in-progress');
      expect(service.rateLimitPauseSeconds()).toBe(31);
      httpMock.expectNone(GQL_ENDPOINT);

      vi.advanceTimersByTime(30_500);

      const retryReq = httpMock.expectOne(GQL_ENDPOINT);
      expect(retryReq.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
      retryReq.flush({});

      expect(service.queue()[0].status).toBe('done');
      expect(service.rateLimitPauseSeconds()).toBeNull();

      vi.advanceTimersByTime(330);
      httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 1, notFoundIds: [] });
    });

    it('re-paces the rest of the run from the reported quota', () => {
      service.startDelete('set-1', 'sensitron', EMOTES);

      // limit 100 over a window of 0ms elapsed + 30s remaining => 300ms/request, +10% margin => 330ms.
      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(30, 100));
      vi.advanceTimersByTime(30_500);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      // The original 275ms pace is no longer in effect.
      vi.advanceTimersByTime(275);
      httpMock.expectNone(GQL_ENDPOINT);

      vi.advanceTimersByTime(55);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      vi.advanceTimersByTime(330);
      httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 2, notFoundIds: [] });
    });

    it('gives up on an emote after the retries are exhausted, without stalling the queue', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]]);

      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(1));
      for (let attempt = 0; attempt < 5; attempt++) {
        vi.advanceTimersByTime(1500);
        httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(1));
      }

      expect(service.queue()[0].status).toBe('failed');
      expect(service.queue()[0].errorMessage).toContain('übersprungen');
      expect(service.rateLimitPauseSeconds()).toBeNull();
    });

    it('backs off a bare HTTP 429 for a full window without mis-learning a pace from it', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]]);

      httpMock
        .expectOne(GQL_ENDPOINT)
        .flush(null, { status: 429, statusText: 'Too Many Requests' });

      expect(service.queue()[0].status).toBe('in-progress');
      expect(service.rateLimitPauseSeconds()).toBe(60);

      vi.advanceTimersByTime(60_000);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      expect(service.queue()[0].status).toBe('done');
      // A headerless rejection carries no quota, so the starting pace must survive it.
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 1, notFoundIds: [] });
    });

    it('reports the achieved rate at the end of a run — the only way we learn 7TVs real quota', () => {
      service.startDelete('set-1', 'sensitron', EMOTES);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 2, notFoundIds: [] });

      expect(console.info).toHaveBeenCalledWith(
        '[EmotePurge] 7TV mass delete finished',
        expect.objectContaining({
          requested: 2,
          succeeded: 2,
          requestsSent: 2,
          // Both requests fall inside one 60s span, so the busiest window holds both.
          peakRequestsPer60s: 2,
          rateLimitHits: 0,
        }),
      );
    });

    it('counts only the busiest 60s span, not the whole run', () => {
      service.startDelete('set-1', 'sensitron', EMOTES);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);

      // A 90s rate-limit pause pushes the retry more than a minute past the first two requests.
      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(90));
      vi.advanceTimersByTime(90_500);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      // Re-paced to (275ms elapsed + 90s reset) / 100 * 1.1 ≈ 993ms.
      vi.advanceTimersByTime(993);
      httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 2, notFoundIds: [] });

      expect(console.info).toHaveBeenCalledWith(
        '[EmotePurge] 7TV mass delete finished',
        expect.objectContaining({ requestsSent: 3, peakRequestsPer60s: 2, rateLimitHits: 1 }),
      );
    });

    it('cancel() during a rate-limit pause stops the run and clears the countdown', () => {
      service.startDelete('set-1', 'sensitron', EMOTES);
      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(30));

      service.cancel();

      expect(service.isRunning()).toBe(false);
      expect(service.rateLimitPauseSeconds()).toBeNull();
      expect(service.queue().map((i) => i.status)).toEqual(['cancelled', 'cancelled']);

      vi.advanceTimersByTime(60_000);
      httpMock.expectNone(GQL_ENDPOINT);
    });
  });

  it('clears the stored token and reports a friendly message on 401', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(tokenService.hasToken()).toBe(false);
    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toContain('Token ungültig');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock
      .expectOne('/api/channels/sensitron/emotes/sync-deleted')
      .flush({ archivedCount: 1, notFoundIds: [] });
  });

  it('cancel() marks pending/in-progress items as cancelled and stops the run', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    httpMock.expectOne(GQL_ENDPOINT).flush({});

    // Second item is now 'pending', waiting out the inter-request delay — cancel before it fires.
    service.cancel();

    expect(service.isRunning()).toBe(false);
    expect(service.queue().map((i) => i.status)).toEqual(['done', 'cancelled']);

    // Only the already-done emote gets synced back.
    const syncReq = httpMock.expectOne('/api/channels/sensitron/emotes/sync-deleted');
    expect(syncReq.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['7tv-1'] });
    syncReq.flush({ archivedCount: 1, notFoundIds: [] });

    // Advancing time afterwards must not fire the second (cancelled) request.
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('reset() clears the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    service.cancel();
    httpMock
      .expectOne('/api/channels/sensitron/emotes/sync-deleted')
      .flush({ archivedCount: 1, notFoundIds: [] });

    service.reset();

    expect(service.queue()).toEqual([]);
  });

  it('reset() also drops the retry state — retrySyncReport() afterwards sends nothing', () => {
    runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });

    service.reset();
    service.retrySyncReport();

    httpMock.expectNone(SYNC_ENDPOINT);
  });

  it('resetIfChannelChanged() clears a finished run when entering another channel', () => {
    runOneDeleteToSyncRequest().flush({ archivedCount: 1, notFoundIds: [] });

    service.resetIfChannelChanged('other-channel');

    expect(service.queue()).toEqual([]);
    expect(service.syncReport()).toBe('idle');
  });

  it('resetIfChannelChanged() keeps a finished run in its own channel', () => {
    runOneDeleteToSyncRequest().flush({ archivedCount: 1, notFoundIds: [] });

    service.resetIfChannelChanged('sensitron');

    expect(service.queue()).toHaveLength(1);
    expect(service.syncReport()).toBe('succeeded');
  });

  it('resetIfChannelChanged() leaves a running run alone, even for another channel', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);

    service.resetIfChannelChanged('other-channel');

    expect(service.isRunning()).toBe(true);
    expect(service.queue()).toHaveLength(2);

    // Drain the run so afterEach's httpMock.verify() stays green.
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 2, notFoundIds: [] });
  });

  // R15 (#72, T12): finish() flips isRunning() to false *before* the closing sync-deleted call
  // resolves, so a second run can legitimately start while the first one's report is still in
  // flight. Its late answer must not land on the second run's state.
  it('discards a late sync-deleted answer from a superseded run without touching the new one', () => {
    const staleSyncReq = runOneDeleteToSyncRequest();
    expect(service.isRunning()).toBe(false); // finish() already flipped this before the follow-up

    // A second run starts, for a different channel, before run 1's answer comes back.
    service.startDelete('set-2', 'other-channel', [EMOTES[1]]);
    expect(service.isRunning()).toBe(true);
    expect(service.syncReport()).toBe('idle'); // run 2's own state, reset at start
    expect(service.lastRun()).toBeNull(); // run 2 has not finished yet

    // Run 1's late answer resolves successfully — even so, it must not resurrect run 1's outcome.
    staleSyncReq.flush({ archivedCount: 1, notFoundIds: [] });
    expect(service.syncReport()).toBe('idle');
    expect(service.lastRun()).toBeNull();

    // Run 2 finishes normally afterwards — the guard must not have swallowed its own terminal
    // flank along with the stale one.
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock
      .expectOne('/api/channels/other-channel/emotes/sync-deleted')
      .flush({ archivedCount: 1, notFoundIds: [] });

    expect(service.syncReport()).toBe('succeeded');
    expect(service.lastRun()?.channelName).toBe('other-channel');
    expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-2']);

    // A retry now must send run 2's ids to run 2's channel and set, never run 1's.
    service.retrySyncReport();
    const retryReq = httpMock.expectOne('/api/channels/other-channel/emotes/sync-deleted');
    expect(retryReq.request.body).toEqual({ emoteSetId: 'set-2', sevenTvEmoteIds: ['7tv-2'] });
    retryReq.flush({ archivedCount: 1, notFoundIds: [] });
  });

  // Spec #200, 7.2 / AK 68: a set-view row may have no local emote at all — it runs, is reported by
  // its 7TV id, and keeps `emoteId` absent on the item the protocol is written from.
  it('runs a row without an emoteId and reports it by its 7TV id', () => {
    service.startDelete('set-1', 'sensitron', [{ sevenTvEmoteId: '7tv-live', name: 'LiveOnly' }]);

    expect(service.queue()[0].key).toBe('7tv-live');
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['7tv-live'] });
    syncReq.flush({ archivedCount: 1, notFoundIds: [] });
    expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-live']);
    expect(service.lastRun()?.result.items[0].emoteId).toBeUndefined();
  });

  // AK 71: the set is frozen into the run record when the run starts. Whatever the page chooses
  // afterwards (modelled here by a refused second start naming another set), the first report and
  // the manual retry both name the run's own set.
  it('reports and retries with the set id frozen at the start of the run', () => {
    service.startDelete('set-1', 'sensitron', [EMOTES[0]]);
    service.startDelete('set-2', 'sensitron', [EMOTES[1]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const firstReport = httpMock.expectOne(SYNC_ENDPOINT);
    expect(firstReport.request.body.emoteSetId).toBe('set-1');
    firstReport.flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(service.lastRun()?.setId).toBe('set-1');

    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(retryReq.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['7tv-1'] });
    retryReq.flush({ archivedCount: 1, notFoundIds: [] });
  });

  // Spec 6.6, E3: the legacy `{ emoteIds }` body stays valid on the server for old tabs, but this
  // client never sends it — not even when every row carries a Guid.
  it('sends only the set-scoped body form, never the legacy emoteIds', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(Object.keys(syncReq.request.body).sort()).toEqual(['emoteSetId', 'sevenTvEmoteIds']);
    expect(syncReq.request.body.emoteIds).toBeUndefined();
    syncReq.flush({ archivedCount: 2, notFoundIds: [] });
  });

  // Sonde 5, branch A (spec 7.2, AK 68): one REMOVE without an alias takes both entries of a #74
  // duplicate, so the cell is one queue row and one request — carrying both aliases for the
  // protocol.
  it('runs a duplicate cell as one row with one REMOVE and keeps both aliases', () => {
    service.startDelete('set-1', 'sensitron', [
      { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] },
    ]);

    expect(service.queue()).toHaveLength(1);
    expect(service.queue()[0].key).toBe('7tv-1');
    expect(service.queue()[0].aliases).toEqual(['PogU', 'PogU2']);

    const removeReq = httpMock.expectOne(GQL_ENDPOINT);
    expect(removeReq.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
    removeReq.flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);

    httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 1, notFoundIds: [] });
    expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-1']);
  });

  // Spec 6.6: a report for a set that is not the channel's active one is paper only — the server
  // archives nothing by design, so `archivedCount: 0` there is not a shortfall.
  it('treats a paper-only answer for a non-active set as succeeded, not partial', () => {
    runOneDeleteToSyncRequest().flush({
      archivedCount: 0,
      notFoundIds: [],
      targetIsActiveSetOfChannel: false,
    });

    expect(service.syncReport()).toBe('succeeded');
  });

  it('does not start a second run while one is already in progress', () => {
    service.startDelete('set-1', 'sensitron', EMOTES);
    const firstQueueLength = service.queue().length;

    service.startDelete('set-2', 'other-channel', [EMOTES[0]]);

    expect(service.queue()).toHaveLength(firstQueueLength);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock
      .expectOne('/api/channels/sensitron/emotes/sync-deleted')
      .flush({ archivedCount: 2, notFoundIds: [] });
  });

  // The arbiter (#70, Task 4) has no lock of its own — it reads this service's own isRunning
  // signal, so these three cases pin the invariants a hand-kept tryAcquire/release could not have
  // guaranteed (see R1 in docs/DECISIONS.md): the derived state can never outlive the run it
  // describes, not even across cancel() or a start the engine itself refused.
  describe('run arbiter', () => {
    let arbiter: SevenTvRunArbiter;

    beforeEach(() => {
      arbiter = TestBed.inject(SevenTvRunArbiter);
    });

    it('reports "delete" as the active run while this service runs, then null once it ends', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]]);

      expect(arbiter.activeRun()).toBe('delete');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock
        .expectOne('/api/channels/sensitron/emotes/sync-deleted')
        .flush({ archivedCount: 1, notFoundIds: [] });

      expect(arbiter.activeRun()).toBeNull();
    });

    it('clears the active run once cancel() ends it', () => {
      service.startDelete('set-1', 'sensitron', EMOTES);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      service.cancel();

      expect(arbiter.activeRun()).toBeNull();

      // Drain the closing sync-deleted call so afterEach's httpMock.verify() stays green.
      httpMock
        .expectOne('/api/channels/sensitron/emotes/sync-deleted')
        .flush({ archivedCount: 1, notFoundIds: [] });
    });

    it('leaves no active run when the engine refuses the start for a cleared token', () => {
      tokenService.clearToken();

      service.startDelete('set-1', 'sensitron', EMOTES);

      expect(service.isRunning()).toBe(false);
      expect(arbiter.activeRun()).toBeNull();
    });
  });

  /**
   * Codex P3, K5 fix round 2: a confirmed delete is invisible to the host dock between the
   * confirmation and the first `REMOVE` — `MassDeletePanel` is reading the set's live aliases, and
   * neither `isRunning` nor `queue` says anything yet. A pushed reload that prunes every marked key
   * in that window used to unmount the dock, destroy the panel and turn the confirmed delete into a
   * silent no-op. This is the claim the panel holds across that window; `action-dock.spec.ts` pins
   * what the dock does with it.
   */
  describe('the dock claim on a confirmed delete that is not a run yet', () => {
    it('is raised by beginConfirmedRun', () => {
      expect(service.confirmedRunPending()).toBe(false);

      service.beginConfirmedRun();

      expect(service.confirmedRunPending()).toBe(true);
    });

    it('is dropped at once once the confirmed delete actually became a run', () => {
      service.beginConfirmedRun();
      service.startDelete('set-1', 'sensitron', EMOTES);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      service.endConfirmedRun();

      // The run carries the dock by itself from here (isRunning/queue), so holding the claim any
      // longer would only keep the dock open past the run's own summary.
      expect(service.confirmedRunPending()).toBe(false);

      service.cancel();
      httpMock.expectOne(SYNC_ENDPOINT).flush({ archivedCount: 1, notFoundIds: [] });
    });

    it('is held for the abort notice when no run started, and clears itself afterwards', () => {
      service.beginConfirmedRun();

      service.endConfirmedRun();

      expect(service.confirmedRunPending()).toBe(true);
      vi.advanceTimersByTime(ABORTED_DELETE_NOTICE_MS - 1);
      expect(service.confirmedRunPending()).toBe(true);
      vi.advanceTimersByTime(1);
      expect(service.confirmedRunPending()).toBe(false);
    });

    it('does not let a first notice window expire onto a second confirmed delete', () => {
      service.beginConfirmedRun();
      service.endConfirmedRun();
      vi.advanceTimersByTime(ABORTED_DELETE_NOTICE_MS - 1);

      service.beginConfirmedRun();
      vi.advanceTimersByTime(1);

      expect(service.confirmedRunPending()).toBe(true);
    });
  });
});
