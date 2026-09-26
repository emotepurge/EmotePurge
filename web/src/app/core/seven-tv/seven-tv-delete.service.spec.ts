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
  DeleteRunInfo,
  REPORT_TIMEOUT_MS,
  SevenTvDeleteService,
} from './seven-tv-delete.service';
import { SyncDeletedInSetResponse } from './seven-tv-emote-set.model';
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
const SYNC_ENDPOINT = '/api/seventv/emote-sets/set-1/sync-deleted';
const SYNC_ENDPOINT_SET_2 = '/api/seventv/emote-sets/set-2/sync-deleted';

/** A set-centric `sync-deleted` answer (spec 5.3) — paper only by default. */
function deletedAnswer(
  overrides: Partial<SyncDeletedInSetResponse> = {},
): SyncDeletedInSetResponse {
  return {
    reportedCount: 1,
    channels: [],
    unresolvedChannel: null,
    resyncTriggered: [],
    ...overrides,
  };
}

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
    service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    return httpMock.expectOne(SYNC_ENDPOINT);
  }

  /** A first report that failed for good is followed by the client's fallback resync of the
   *  expected channel (addendum N1) — flushed here where a case is about something else. */
  function flushFallbackResync() {
    httpMock
      .expectOne('/api/channels/sensitron/resync')
      .flush(null, { status: 202, statusText: 'Accepted' });
  }

  describe('sync report', () => {
    it('reports success only when the backend archived everything', () => {
      runOneDeleteToSyncRequest().flush(
        deletedAnswer({
          channels: [{ channelName: 'sensitron', archivedCount: 1, notFoundIds: [] }],
          resyncTriggered: ['sensitron'],
        }),
      );

      expect(service.syncReport()).toBe('succeeded');
      expect(service.syncReportReason()).toBeNull();
    });

    // AK 15, F8: a touched channel short of the reported ids is partial/shortfall.
    it('treats an under-count as partial/shortfall — all ids in notFoundIds used to look like success', () => {
      runOneDeleteToSyncRequest().flush(
        deletedAnswer({
          channels: [{ channelName: 'sensitron', archivedCount: 0, notFoundIds: ['7tv-1'] }],
        }),
      );

      expect(service.syncReport()).toBe('partial');
      expect(service.syncReportReason()).toBe('shortfall');
    });

    // AK 15, E18: the expected channel was not hit because it is tracked but currently active
    // under a different set (stale active set) — partial/channelMismatchActiveSetDiffers (#255:
    // kept apart from the notTracked case below, they read differently to a user).
    it('treats an unresolved expected channel with reason activeSetDiffers as partial/channelMismatchActiveSetDiffers', () => {
      runOneDeleteToSyncRequest().flush(
        deletedAnswer({
          unresolvedChannel: { channelName: 'sensitron', reason: 'activeSetDiffers' },
          resyncTriggered: ['sensitron'],
        }),
      );

      expect(service.syncReport()).toBe('partial');
      expect(service.syncReportReason()).toBe('channelMismatchActiveSetDiffers');
    });

    // AK 15, E18: the expected channel was not hit because EmotePurge does not currently track it
    // at all — partial/channelMismatchNotTracked.
    it('treats an unresolved expected channel with reason notTracked as partial/channelMismatchNotTracked', () => {
      runOneDeleteToSyncRequest().flush(
        deletedAnswer({
          unresolvedChannel: { channelName: 'sensitron', reason: 'notTracked' },
        }),
      );

      expect(service.syncReport()).toBe('partial');
      expect(service.syncReportReason()).toBe('channelMismatchNotTracked');
    });

    // addendum N4, AK 40: nothing a retry could improve — the service refuses it, no request, for
    // either channel-mismatch reason.
    it.each(['activeSetDiffers', 'notTracked'] as const)(
      'refuses a manual retry of a report that ended partial/channelMismatch (%s)',
      (reason) => {
        runOneDeleteToSyncRequest().flush(
          deletedAnswer({
            unresolvedChannel: { channelName: 'sensitron', reason },
            resyncTriggered: reason === 'activeSetDiffers' ? ['sensitron'] : [],
          }),
        );

        service.retrySyncReport();

        httpMock.expectNone(SYNC_ENDPOINT);
        expect(service.syncReport()).toBe('partial');
      },
    );

    it('still allows a manual retry of a report that ended partial/shortfall', () => {
      runOneDeleteToSyncRequest().flush(
        deletedAnswer({
          channels: [{ channelName: 'sensitron', archivedCount: 0, notFoundIds: ['7tv-1'] }],
        }),
      );
      expect(service.syncReportReason()).toBe('shortfall');

      service.retrySyncReport();

      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    // AK 15: a revoked right is final — no automatic retry, failed/forbidden.
    it('reads a 403 as failed/forbidden without retrying', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('forbidden');
      vi.advanceTimersByTime(10_000);
      httpMock.expectNone(SYNC_ENDPOINT);
    });

    // AK 15, #224: a vanished set must never read as succeeded.
    it('reads a 404 after the retries as failed/setNotFound, never succeeded', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 404, statusText: 'Not Found' });
      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 404, statusText: 'Not Found' });
      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 404, statusText: 'Not Found' });
      flushFallbackResync();

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('setNotFound');
    });

    it('reads a 503 or a network failure after the retries as failed/unavailable', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 503, statusText: 'Unavailable' });
      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 429, statusText: 'Too Many' });
      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_ENDPOINT).error(new ProgressEvent('error'));
      flushFallbackResync();

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('unavailable');
    });

    it('retries a transient failure and succeeds on the second attempt', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 429, statusText: 'Too Many Requests' });
      expect(service.syncReport()).toBe('pending');

      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      expect(service.syncReport()).toBe('succeeded');
    });

    it('gives up after the automatic retries are exhausted', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 500, statusText: 'Server Error' });

      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 500, statusText: 'Server Error' });

      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 500, statusText: 'Server Error' });
      flushFallbackResync();

      expect(service.syncReport()).toBe('failed');
    });

    // #256 P2-1 (Plan-256 review): a malformed 200 answer makes `classifySyncInSetResponse` throw
    // inside the `map` ahead of `retry` — this proves that throw is retried exactly like an HTTP
    // failure (the comment beside that `map` call explains why: a plain `TypeError`, not an
    // `HttpErrorResponse`, so `retry`'s 401/403 check never matches it) and, once the retries are
    // exhausted, still reaches an end state rather than leaving the run `reporting` forever.
    it('retries a malformed 200 answer that makes the classification throw, and closes the run once the retries are exhausted', () => {
      runOneDeleteToSyncRequest().flush(null);

      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null);

      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_ENDPOINT).flush(null);
      flushFallbackResync();

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('other');
      expect(service.run()?.phase).toBe('closed');
      expect(service.destructiveOpen()).toBe(false);
    });

    // #256 P2-2 (Plan-256 review, Festlegung 15): a report that never answers must not keep its run
    // open for good — same contract and constant as the import's own version of this test
    // (seven-tv-import.service.spec.ts).
    it('gives up a report without an answer after REPORT_TIMEOUT_MS per attempt and closes the run', () => {
      const firstAttempt = runOneDeleteToSyncRequest();
      vi.advanceTimersByTime(REPORT_TIMEOUT_MS - 1);
      expect(firstAttempt.cancelled).toBe(false);
      expect(service.run()?.phase).toBe('reporting');
      vi.advanceTimersByTime(1);
      expect(firstAttempt.cancelled).toBe(true);

      vi.advanceTimersByTime(2000);
      const secondAttempt = httpMock.expectOne(SYNC_ENDPOINT);
      vi.advanceTimersByTime(REPORT_TIMEOUT_MS);
      expect(secondAttempt.cancelled).toBe(true);

      vi.advanceTimersByTime(4000);
      const thirdAttempt = httpMock.expectOne(SYNC_ENDPOINT);
      vi.advanceTimersByTime(REPORT_TIMEOUT_MS);
      expect(thirdAttempt.cancelled).toBe(true);
      flushFallbackResync();

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('unavailable');
      expect(service.run()?.phase).toBe('closed');
      expect(service.destructiveOpen()).toBe(false);
    });

    it('does not retry a 401 — an expired session cannot be fixed by waiting', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });
      flushFallbackResync();

      expect(service.syncReport()).toBe('failed');
      vi.advanceTimersByTime(10_000);
      httpMock.verify(); // no further attempt was made
    });

    it('retrySyncReport() re-sends the same ids after a failure', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });
      flushFallbackResync();

      service.retrySyncReport();

      const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(retryReq.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1'],
        expectedChannelName: 'sensitron',
      });
      retryReq.flush(deletedAnswer());

      expect(service.syncReport()).toBe('succeeded');
    });

    // addendum N1, AK 36: a report that fails for good never reached the backend's resync stage,
    // so the client resyncs the expected channel (the page's, for its active set) itself — once,
    // after the first report only, and without a dock line of its own.
    describe('fallback resync after a report that failed for good', () => {
      const RESYNC_ENDPOINT = '/api/channels/sensitron/resync';

      it('resyncs the expected channel once the report has failed after its retries', () => {
        runOneDeleteToSyncRequest().flush(null, { status: 503, statusText: 'Unavailable' });
        vi.advanceTimersByTime(2000);
        httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 503, statusText: 'Unavailable' });
        httpMock.expectNone(RESYNC_ENDPOINT);
        vi.advanceTimersByTime(4000);
        httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 503, statusText: 'Unavailable' });

        expect(service.syncReport()).toBe('failed');
        httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      });

      it('sends no resync for a set without an expected channel', () => {
        service.startDelete('set-1', 'sensitron', [EMOTES[0]], null);
        httpMock.expectOne(GQL_ENDPOINT).flush({});
        vi.advanceTimersByTime(DELETE_DELAY_MS);
        httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 403, statusText: 'Forbidden' });

        expect(service.syncReport()).toBe('failed');
        httpMock.expectNone((request) => request.url.endsWith('/resync'));
      });

      it('sends no resync after a report that answered', () => {
        runOneDeleteToSyncRequest().flush(
          deletedAnswer({
            unresolvedChannel: { channelName: 'sensitron', reason: 'activeSetDiffers' },
          }),
        );

        httpMock.expectNone((request) => request.url.endsWith('/resync'));
      });

      it('sends no second resync for a manual retry that fails again', () => {
        runOneDeleteToSyncRequest().flush(null, { status: 403, statusText: 'Forbidden' });
        httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

        service.retrySyncReport();
        httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 403, statusText: 'Forbidden' });

        expect(service.syncReport()).toBe('failed');
        httpMock.expectNone((request) => request.url.endsWith('/resync'));
      });

      it('keeps a cooldown answer to itself — the delete dock has no resync line', () => {
        runOneDeleteToSyncRequest().flush(null, { status: 403, statusText: 'Forbidden' });
        httpMock
          .expectOne(RESYNC_ENDPOINT)
          .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });

        expect(service.syncReport()).toBe('failed');
        expect(service.syncReportReason()).toBe('forbidden');
      });
    });

    it('reset() clears the sync report and its reason as well', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });
      flushFallbackResync();
      expect(service.syncReportReason()).toBe('other');

      service.reset();

      expect(service.syncReport()).toBe('idle');
      expect(service.syncReportReason()).toBeNull();
    });
  });

  // #256, Plan-256 Festlegungen 3, 6, 13: the run-bound lifecycle. `run`/`isSettling`/
  // `destructiveOpen` are the lifecycle's own signals; the report keeps going on the run's own
  // record whether or not the dock shows it.
  describe('#256 run lifecycle', () => {
    it('holds destructiveOpen from startDelete to closed — every delete row is destructive', () => {
      expect(service.destructiveOpen()).toBe(false);

      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
      expect(service.destructiveOpen()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      expect(service.destructiveOpen()).toBe(true); // reporting — not closed yet

      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
      expect(service.destructiveOpen()).toBe(false);
    });

    it('isSettling is true while the report is out, false once it closes', () => {
      expect(service.isSettling()).toBe(false);
      const syncReq = runOneDeleteToSyncRequest();
      expect(service.isSettling()).toBe(true);

      syncReq.flush(deletedAnswer());
      expect(service.isSettling()).toBe(false);
    });

    // Codex-Befund 1 on the plan: `reset()` during `running` must not cancel the engine — a REMOVE
    // already in flight when the display detaches can still be confirmed by 7TV afterwards, and the
    // run must still report it, even though nothing shows it any more.
    it('reset() while running lets the engine finish and still reports a REMOVE that was in flight', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
      const inFlightReq = httpMock.expectOne(GQL_ENDPOINT);

      service.reset();

      expect(service.run()).toBeNull(); // detached at once
      expect(service.isRunning()).toBe(true); // the engine itself was not touched
      expect(service.destructiveOpen()).toBe(true); // the run is still open, just not shown

      // 7TV confirms the request that was in flight when reset() was called.
      inFlightReq.flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);

      // The confirmed removals are reported all the same, on the run's own record.
      const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(syncReq.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1', '7tv-2'],
        expectedChannelName: 'sensitron',
      });
      syncReq.flush(deletedAnswer());

      expect(service.run()).toBeNull(); // still nothing shown — a success needs no reshow
      expect(service.destructiveOpen()).toBe(false);
    });

    // Festlegung 13, Codex-Befund 2: a channel switch mid-report must not drop a run whose report
    // could still fail — only a `closed` run follows resetIfChannelChanged's old "engine stopped"
    // rule.
    it('resetIfChannelChanged() during reporting leaves the run shown until it closes', () => {
      const syncReq = runOneDeleteToSyncRequest();

      service.resetIfChannelChanged('other-channel');
      expect(service.run()).not.toBeNull();
      expect(service.syncReport()).toBe('pending');

      syncReq.flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();

      // Ended failed — stays visible with its reason and a retry, not swept away mid-report.
      expect(service.run()).not.toBeNull();
      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('forbidden');

      // Only now, once closed, does a channel switch actually reset it.
      service.resetIfChannelChanged('other-channel');
      expect(service.run()).toBeNull();
    });

    // Festlegung 13: a programmatic reset() detaches a run whose report has not answered yet; if
    // that report then does not succeed, the run shows itself again so its reason and retry stay
    // reachable — unless something else is shown by then, in which case the failure only reaches
    // the console.
    it('shows a detached run again once its report fails, and lets a retry send from there', () => {
      const syncReq = runOneDeleteToSyncRequest();
      service.reset();
      expect(service.run()).toBeNull();

      syncReq.flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();

      expect(service.run()).not.toBeNull();
      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('forbidden');

      service.retrySyncReport();
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    it('does not reshow a failed report once a newer run is shown, and logs it instead', () => {
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      const syncReq = runOneDeleteToSyncRequest();
      service.reset();

      service.startDelete('set-2', 'other-channel', [EMOTES[1]], 'other-channel');
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      const run2SyncReq = httpMock.expectOne(SYNC_ENDPOINT_SET_2);
      expect(service.isRunning()).toBe(false); // run 2's engine work is done, only its report is out
      expect(service.run()?.setId).toBe('set-2');

      syncReq.flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();

      expect(service.run()?.setId).toBe('set-2'); // run 1's failure did not take the dock back
      expect(warnSpy).toHaveBeenCalledWith(
        '[EmotePurge] 7TV delete report of a run no longer shown did not succeed',
        expect.objectContaining({ state: 'failed', reason: 'forbidden' }),
      );

      run2SyncReq.flush(deletedAnswer());
    });
  });

  // A closed run's own record — never goes through the engine, so it leaves `queue()` untouched;
  // only stands in for whatever the dock already shows when a *second* start is refused, below.
  const PREVIOUS_CLOSED_RUN: DeleteRunInfo = {
    runId: 'delete-previous',
    phase: 'closed',
    destructive: true,
    channelName: 'sensitron',
    expectedChannelName: 'sensitron',
    setId: 'set-2',
    result: { doneKeys: ['7tv-9'], items: [], startedAt: 0, finishedAt: 1 },
    syncReport: 'succeeded',
    syncReportReason: null,
  };

  it('does nothing without a stored token', () => {
    // #256 P3-1 (Plan-256 review, "open() vor start()"): a previous, already-closed run is shown
    // on the dock when this refused start comes in. `startDelete` opens its own record *before*
    // asking the engine to start (#256 review finding) — a refused start must take that record back
    // (`discardUnstarted`) rather than leave it dangling in the lifecycle's map, or this run's
    // `destructive: true` would leak into `destructiveOpen` forever and the previous run's dock
    // would be silently replaced.
    service.run.set(PREVIOUS_CLOSED_RUN);
    tokenService.clearToken();

    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

    expect(service.isRunning()).toBe(false);
    expect(service.queue()).toEqual([]);
    expect(service.destructiveOpen()).toBe(false);
    expect(service.isSettling()).toBe(false);
    expect(service.run()).toBe(PREVIOUS_CLOSED_RUN);
  });

  it('does nothing when the emote list is empty', () => {
    // #256 P3-1 — same reasoning as the no-token case above.
    service.run.set(PREVIOUS_CLOSED_RUN);

    service.startDelete('set-1', 'sensitron', [], 'sensitron');

    expect(service.isRunning()).toBe(false);
    expect(service.destructiveOpen()).toBe(false);
    expect(service.isSettling()).toBe(false);
    expect(service.run()).toBe(PREVIOUS_CLOSED_RUN);
  });

  it('keys every queue row by its 7TV id', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

    expect(service.queue().map((item) => item.key)).toEqual(['7tv-1', '7tv-2']);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
  });

  it('deletes emotes sequentially with a delay, then syncs the archived ids', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

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

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      expectedChannelName: 'sensitron',
    });
    syncReq.flush(deletedAnswer());
  });

  it('marks a GraphQL error response as failed and continues the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'emote not found' }] });

    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toBe('emote not found');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    // Only the successful one gets synced back to Postgres.
    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-2'],
      expectedChannelName: 'sensitron',
    });
    syncReq.flush(deletedAnswer());
  });

  describe('7TV rate limiting', () => {
    it('waits out the reported reset and retries the same emote instead of failing it', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');

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
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    it('re-paces the rest of the run from the reported quota', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

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
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    it('gives up on an emote after the retries are exhausted, without stalling the queue', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');

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
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');

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
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    it('reports the achieved rate at the end of a run — the only way we learn 7TVs real quota', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

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
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);

      // A 90s rate-limit pause pushes the retry more than a minute past the first two requests.
      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(90));
      vi.advanceTimersByTime(90_500);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      // Re-paced to (275ms elapsed + 90s reset) / 100 * 1.1 ≈ 993ms.
      vi.advanceTimersByTime(993);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      expect(console.info).toHaveBeenCalledWith(
        '[EmotePurge] 7TV mass delete finished',
        expect.objectContaining({ requestsSent: 3, peakRequestsPer60s: 2, rateLimitHits: 1 }),
      );
    });

    it('cancel() during a rate-limit pause stops the run and clears the countdown', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
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
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(tokenService.hasToken()).toBe(false);
    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toContain('Token ungültig');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
  });

  it('cancel() marks pending/in-progress items as cancelled and stops the run', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
    httpMock.expectOne(GQL_ENDPOINT).flush({});

    // Second item is now 'pending', waiting out the inter-request delay — cancel before it fires.
    service.cancel();

    expect(service.isRunning()).toBe(false);
    expect(service.queue().map((i) => i.status)).toEqual(['done', 'cancelled']);

    // Only the already-done emote gets synced back.
    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      expectedChannelName: 'sensitron',
    });
    syncReq.flush(deletedAnswer());

    // Advancing time afterwards must not fire the second (cancelled) request.
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('reset() clears the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    service.cancel();
    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

    service.reset();

    expect(service.queue()).toEqual([]);
  });

  it('reset() also drops the retry state — retrySyncReport() afterwards sends nothing', () => {
    runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });
    flushFallbackResync();

    service.reset();
    service.retrySyncReport();

    httpMock.expectNone(SYNC_ENDPOINT);
  });

  it('resetIfChannelChanged() clears a finished run when entering another channel', () => {
    runOneDeleteToSyncRequest().flush(deletedAnswer());

    service.resetIfChannelChanged('other-channel');

    expect(service.queue()).toEqual([]);
    expect(service.syncReport()).toBe('idle');
  });

  it('resetIfChannelChanged() keeps a finished run in its own channel', () => {
    runOneDeleteToSyncRequest().flush(deletedAnswer());

    service.resetIfChannelChanged('sensitron');

    expect(service.queue()).toHaveLength(1);
    expect(service.syncReport()).toBe('succeeded');
  });

  it('resetIfChannelChanged() leaves a running run alone, even for another channel', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

    service.resetIfChannelChanged('other-channel');

    expect(service.isRunning()).toBe(true);
    expect(service.queue()).toHaveLength(2);

    // Drain the run so afterEach's httpMock.verify() stays green.
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
  });

  // R15 (#72, T12): finish() flips isRunning() to false *before* the closing sync-deleted call
  // resolves, so a second run can legitimately start while the first one's report is still in
  // flight. Its late answer must not land on the second run's state.
  it('discards a late sync-deleted answer from a superseded run without touching the new one', () => {
    const staleSyncReq = runOneDeleteToSyncRequest();
    expect(service.isRunning()).toBe(false); // finish() already flipped this before the follow-up

    // A second run starts, for a different channel, before run 1's answer comes back.
    service.startDelete('set-2', 'other-channel', [EMOTES[1]], 'other-channel');
    expect(service.isRunning()).toBe(true);
    expect(service.syncReport()).toBe('idle'); // run 2's own state, reset at start
    expect(service.lastRun()).toBeNull(); // run 2 has not finished yet

    // Run 1's late answer resolves successfully — even so, it must not resurrect run 1's outcome.
    staleSyncReq.flush(deletedAnswer());
    expect(service.syncReport()).toBe('idle');
    expect(service.lastRun()).toBeNull();
    // #256 P3-4 (Plan-256 review): run 1's late answer is still verbucht on *its own* record and
    // closes it (Plan-256 Festlegung 2, identity by `runId`) — `isSettling`/`destructiveOpen` here
    // read run 2's own state, not a stale leftover of run 1's: run 2 is still `running` (not yet
    // reporting) but destructive, so `isSettling` is false and `destructiveOpen` is true.
    expect(service.isSettling()).toBe(false);
    expect(service.destructiveOpen()).toBe(true);

    // Run 2 finishes normally afterwards — the guard must not have swallowed its own terminal
    // flank along with the stale one.
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT_SET_2).flush(deletedAnswer());

    expect(service.syncReport()).toBe('succeeded');
    expect(service.lastRun()?.channelName).toBe('other-channel');
    expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-2']);

    // A retry now must send run 2's ids to run 2's channel and set, never run 1's.
    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_ENDPOINT_SET_2);
    expect(retryReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-2'],
      expectedChannelName: 'other-channel',
    });
    retryReq.flush(deletedAnswer());
  });

  // Spec #200, 7.2 / AK 68: a set-view row may have no local emote at all — it runs, is reported by
  // its 7TV id, and keeps `emoteId` absent on the item the protocol is written from.
  it('runs a row without an emoteId and reports it by its 7TV id', () => {
    service.startDelete(
      'set-1',
      'sensitron',
      [{ sevenTvEmoteId: '7tv-live', name: 'LiveOnly' }],
      'sensitron',
    );

    expect(service.queue()[0].key).toBe('7tv-live');
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-live'],
      expectedChannelName: 'sensitron',
    });
    syncReq.flush(deletedAnswer());
    expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-live']);
    expect(service.lastRun()?.result.items[0].emoteId).toBeUndefined();
  });

  // AK 71: the set is frozen into the run record when the run starts. Whatever the page chooses
  // afterwards (modelled here by a refused second start naming another set), the first report and
  // the manual retry both name the run's own set.
  it('reports and retries with the set id frozen at the start of the run', () => {
    service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');
    service.startDelete('set-2', 'sensitron', [EMOTES[1]], 'sensitron');
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    // The set is in the route now: the report is addressed to set-1, never to set-2.
    const firstReport = httpMock.expectOne(SYNC_ENDPOINT);
    httpMock.expectNone(SYNC_ENDPOINT_SET_2);
    firstReport.flush(null, { status: 401, statusText: 'Unauthorized' });
    flushFallbackResync();
    expect(service.lastRun()?.setId).toBe('set-1');

    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(retryReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      expectedChannelName: 'sensitron',
    });
    retryReq.flush(deletedAnswer());
  });

  // Spec 5.1, E3: the legacy `{ emoteIds }` body stays valid on the server for old tabs, but this
  // client never sends it — not even when every row carries a Guid.
  it('sends only the set-centric body form, never the legacy emoteIds', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(Object.keys(syncReq.request.body).sort()).toEqual([
      'expectedChannelName',
      'sevenTvEmoteIds',
    ]);
    expect(syncReq.request.body.emoteIds).toBeUndefined();
    syncReq.flush(deletedAnswer());
  });

  // Sonde 5, branch A (spec 7.2, AK 68): one REMOVE without an alias takes both entries of a #74
  // duplicate, so the cell is one queue row and one request — carrying both aliases for the
  // protocol.
  it('runs a duplicate cell as one row with one REMOVE and keeps both aliases', () => {
    service.startDelete(
      'set-1',
      'sensitron',
      [
        {
          emoteId: 'internal-1',
          sevenTvEmoteId: '7tv-1',
          name: 'PogU',
          aliases: ['PogU', 'PogU2'],
        },
      ],
      'sensitron',
    );

    expect(service.queue()).toHaveLength(1);
    expect(service.queue()[0].key).toBe('7tv-1');
    expect(service.queue()[0].aliases).toEqual(['PogU', 'PogU2']);

    const removeReq = httpMock.expectOne(GQL_ENDPOINT);
    expect(removeReq.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
    removeReq.flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);

    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-1']);
  });

  // Spec 4.6 point 21 / 6.5: a delete from a set that is not the page's active one expects no
  // channel, and the server's paper-only answer (`channels: []`) is a plain success, not partial.
  it('reports a non-active set with no expected channel and reads the paper-only answer as succeeded', () => {
    service.startDelete('set-2', 'sensitron', [EMOTES[0]], null);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT_SET_2);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      expectedChannelName: null,
    });
    syncReq.flush(deletedAnswer());

    expect(service.syncReport()).toBe('succeeded');
    expect(service.syncReportReason()).toBeNull();
    // The delete has no resync of its own (spec 6.5).
    httpMock.expectNone((request) => request.url.endsWith('/resync'));
    // The run record keeps the page's channel for the protocol, whatever it expected.
    expect(service.lastRun()?.channelName).toBe('sensitron');
  });

  it('does not start a second run while one is already in progress', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
    const firstQueueLength = service.queue().length;

    service.startDelete('set-2', 'other-channel', [EMOTES[0]], 'other-channel');

    expect(service.queue()).toHaveLength(firstQueueLength);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
  });

  // The arbiter (#70, Task 4) has no lock of its own — it reads the signals this service registers
  // with it (#256: isRunning, isSettling, destructiveOpen), so these three cases pin the invariants a hand-kept tryAcquire/release could not have
  // guaranteed (see R1 in docs/DECISIONS.md): the derived state can never outlive the run it
  // describes, not even across cancel() or a start the engine itself refused.
  describe('run arbiter', () => {
    let arbiter: SevenTvRunArbiter;

    beforeEach(() => {
      arbiter = TestBed.inject(SevenTvRunArbiter);
    });

    it('reports "delete" as the active run while this service runs, then null once it ends', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');

      expect(arbiter.activeRun()).toBe('delete');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      expect(arbiter.activeRun()).toBeNull();
    });

    it('clears the active run once a run ended by cancel() has had its report answered', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      service.cancel();

      // #256 (contract P2): the confirmed row is still being reported — the arbiter counts that
      // settling window as busy, and frees up only once the report has an end state.
      expect(service.isRunning()).toBe(false);
      expect(arbiter.activeRun()).toBe('delete');

      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      expect(arbiter.activeRun()).toBeNull();
    });

    it('leaves no active run when the engine refuses the start for a cleared token', () => {
      tokenService.clearToken();

      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');

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
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron');
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      service.endConfirmedRun();

      // The run carries the dock by itself from here (isRunning/queue), so holding the claim any
      // longer would only keep the dock open past the run's own summary.
      expect(service.confirmedRunPending()).toBe(false);

      service.cancel();
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
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

    it('is dropped outright by clearConfirmedRun, without a notice window', () => {
      service.beginConfirmedRun();

      service.clearConfirmedRun();

      expect(service.confirmedRunPending()).toBe(false);
    });

    it('cancels a notice window already running when clearConfirmedRun comes in', () => {
      service.beginConfirmedRun();
      service.endConfirmedRun();

      service.clearConfirmedRun();

      expect(service.confirmedRunPending()).toBe(false);
      vi.advanceTimersByTime(ABORTED_DELETE_NOTICE_MS);
      expect(service.confirmedRunPending()).toBe(false);
    });

    it('is dropped by reset(), window and all — the user dismissed the dock it belonged to', () => {
      service.beginConfirmedRun();
      service.endConfirmedRun();

      service.reset();

      expect(service.confirmedRunPending()).toBe(false);
      vi.advanceTimersByTime(ABORTED_DELETE_NOTICE_MS);
      expect(service.confirmedRunPending()).toBe(false);
    });

    it('cannot be re-armed by a release that arrives after the claim was dropped elsewhere', () => {
      // The read of a confirmed delete is still out when reset() or a channel change drops the
      // claim; its eventual endConfirmedRun must not open a notice window on a dock that now
      // belongs to something else.
      service.beginConfirmedRun();
      service.clearConfirmedRun();

      service.endConfirmedRun();

      expect(service.confirmedRunPending()).toBe(false);
      vi.advanceTimersByTime(ABORTED_DELETE_NOTICE_MS);
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
