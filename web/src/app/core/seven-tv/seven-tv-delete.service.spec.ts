import { HttpRequest, provideHttpClient } from '@angular/common/http';
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
import { TagRemovalResult } from '../tags/emote-tag.model';
import { SyncDeletedInSetResponse } from './seven-tv-emote-set.model';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { CANCEL_SETTLE_GRACE_MS, SET_ENTRIES_READ_TIMEOUT_MS } from './seven-tv-run-settlement';
import { SevenTvTokenService } from './seven-tv-token.service';
import { flushApplied, flushWithoutResult } from './seven-tv-mutation.testing';
import { DeleteTagContext } from './tag-run-settlement';

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

/** The run's own `REMOVE` and its settling re-read both go to the one GQL endpoint — told apart by
 *  the query text, as in the import spec. */
function isRemove(request: HttpRequest<unknown>): boolean {
  return (
    request.url === GQL_ENDPOINT &&
    (request.body as { query: string }).query.includes('removeEmote(')
  );
}

function isSetRead(request: HttpRequest<unknown>): boolean {
  return (
    request.url === GQL_ENDPOINT &&
    (request.body as { query: string }).query.includes('emotes(page: $page, perPage: $perPage)')
  );
}

/** One page of the tokenless set read (`loadSevenTvSetEntries`) holding exactly `entries`. */
function setEntriesPage(entries: { id: string; alias: string | null }[]) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: entries.length,
            pageCount: 1,
            items: entries.map((entry) => ({ alias: entry.alias, emote: { id: entry.id } })),
          },
        },
      },
    },
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
  function runOneDeleteToSyncRequest(targetOwnerTwitchId: string | null = null) {
    service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron', targetOwnerTwitchId);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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

    // Owner-hint design 3.6: the pre-check's resolved owner id, frozen onto the run at start, rides
    // along on the report and survives a manual retry unchanged — never re-resolved by this service.
    it('carries the target owner hint on the report and on a manual retry', () => {
      const reportReq = runOneDeleteToSyncRequest('tw-owner');
      expect(reportReq.request.body.targetOwnerTwitchId).toBe('tw-owner');
      reportReq.flush(null, { status: 401, statusText: 'Unauthorized' });
      flushFallbackResync();

      service.retrySyncReport();
      const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(retryReq.request.body.targetOwnerTwitchId).toBe('tw-owner');
      retryReq.flush(deletedAnswer());
    });

    it('retrySyncReport() re-sends the same ids after a failure', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 401, statusText: 'Unauthorized' });
      flushFallbackResync();

      service.retrySyncReport();

      const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(retryReq.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1'],
        expectedChannelName: 'sensitron',
        targetOwnerTwitchId: null,
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
        service.startDelete('set-1', 'sensitron', [EMOTES[0]], null, null);
        flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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

      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
      expect(service.destructiveOpen()).toBe(true);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
      const inFlightReq = httpMock.expectOne(GQL_ENDPOINT);

      service.reset();

      expect(service.run()).toBeNull(); // detached at once
      expect(service.isRunning()).toBe(true); // the engine itself was not touched
      expect(service.destructiveOpen()).toBe(true); // the run is still open, just not shown

      // 7TV confirms the request that was in flight when reset() was called.
      flushApplied(inFlightReq);
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(DELETE_DELAY_MS);

      // The confirmed removals are reported all the same, on the run's own record.
      const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(syncReq.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1', '7tv-2'],
        expectedChannelName: 'sensitron',
        targetOwnerTwitchId: null,
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

      service.startDelete('set-2', 'other-channel', [EMOTES[1]], 'other-channel', null);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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

  // #256 P2 (Plan-256-Robustheit review, branch-review round): a manual retry used to leave the
  // record on its previous end state until the retry's own answer came in — the retry button stayed
  // up for a second, parallel report, and a `closed`-but-nothing-pending record could not survive a
  // "Close" click mid-retry. `reportDeleted` now patches the record to `pending` before sending,
  // mirroring the import's `reportImported`/`reportRemoved`.
  describe('#256 P2: a manual retry marks the record pending before sending', () => {
    it('retrySyncReport() patches the record to pending before the request goes out', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();
      expect(service.syncReport()).toBe('failed');

      service.retrySyncReport();

      // Neither 'failed' nor 'partial' — run-progress-panel's `syncReportFailed` computed reads
      // this and hides the retry button/reason line the moment it is not one of those two.
      expect(service.syncReport()).toBe('pending');
      expect(service.syncReportReason()).toBeNull();

      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    it('a second click while the retry is out sends nothing', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();

      service.retrySyncReport();
      const retryReq = httpMock.expectOne(SYNC_ENDPOINT);

      service.retrySyncReport(); // no-op: syncReport is already 'pending'
      httpMock.expectNone(SYNC_ENDPOINT);

      retryReq.flush(deletedAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    it('isSettling and destructiveOpen stay false while a closed run’s retry is out', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();
      expect(service.run()?.phase).toBe('closed');

      service.retrySyncReport();

      // `closed` is a one-way door (#256): the retry never reopens the phase, so neither signal —
      // both derived from the phase, not from `syncReport` — sees this run as busy again.
      expect(service.isSettling()).toBe(false);
      expect(service.destructiveOpen()).toBe(false);

      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    it('reshows a run whose manual retry fails after the dock was closed mid-retry', () => {
      runOneDeleteToSyncRequest().flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();

      service.retrySyncReport();
      const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
      service.reset();
      expect(service.run()).toBeNull();

      // Without the pending patch, this record had already left the lifecycle's map (`closed`,
      // nothing pending) the moment reset() detached it, and this answer would have found no
      // record at all — no reshow, no console.warn.
      retryReq.flush(null, { status: 403, statusText: 'Forbidden' });

      expect(service.run()).not.toBeNull();
      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('forbidden');
    });
  });

  // #256 P3 (Plan-256-Robustheit review, branch-review round): the success-path counterpart to
  // "reset() while running lets the engine finish and still reports a REMOVE that was in flight" —
  // this one detaches while the *report itself* (not the engine) is in flight, and the report
  // succeeds.
  it('#256 P3: reset() while the report is in flight still lands a successful answer on the record exactly once, closing isSettling only after', () => {
    const syncReq = runOneDeleteToSyncRequest();
    expect(service.isSettling()).toBe(true);

    service.reset();
    expect(service.run()).toBeNull();
    expect(service.isSettling()).toBe(true); // still open — the report has not answered yet

    syncReq.flush(deletedAnswer());

    // httpMock's own afterEach.verify() proves the answer landed exactly once (no leftover, no
    // second request); a success needs no reshow.
    expect(service.isSettling()).toBe(false);
    expect(service.run()).toBeNull();
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
    targetOwnerTwitchId: null,
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

    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

    expect(service.isRunning()).toBe(false);
    expect(service.queue()).toEqual([]);
    expect(service.destructiveOpen()).toBe(false);
    expect(service.isSettling()).toBe(false);
    expect(service.run()).toBe(PREVIOUS_CLOSED_RUN);
  });

  it('does nothing when the emote list is empty', () => {
    // #256 P3-1 — same reasoning as the no-token case above.
    service.run.set(PREVIOUS_CLOSED_RUN);

    service.startDelete('set-1', 'sensitron', [], 'sensitron', null);

    expect(service.isRunning()).toBe(false);
    expect(service.destructiveOpen()).toBe(false);
    expect(service.isSettling()).toBe(false);
    expect(service.run()).toBe(PREVIOUS_CLOSED_RUN);
  });

  it('keys every queue row by its 7TV id', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

    expect(service.queue().map((item) => item.key)).toEqual(['7tv-1', '7tv-2']);

    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
  });

  it('deletes emotes sequentially with a delay, then syncs the archived ids', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

    expect(service.isRunning()).toBe(true);
    expect(service.queue().map((i) => i.status)).toEqual(['in-progress', 'pending']);

    const req1 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req1.request.headers.get('Authorization')).toBe('Bearer write-token');
    expect(req1.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
    flushApplied(req1);

    expect(service.queue()[0].status).toBe('done');
    expect(service.queue()[1].status).toBe('pending');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    expect(service.queue()[1].status).toBe('in-progress');

    const req2 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req2.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-2' });
    flushApplied(req2);

    vi.advanceTimersByTime(DELETE_DELAY_MS);

    expect(service.isRunning()).toBe(false);
    expect(service.progress()).toEqual({ finished: 2, total: 2 });

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      expectedChannelName: 'sensitron',
      targetOwnerTwitchId: null,
    });
    syncReq.flush(deletedAnswer());
  });

  it('marks a GraphQL error response as failed and continues the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'emote not found' }] });

    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toBe('emote not found');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    // Only the successful one gets synced back to Postgres.
    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-2'],
      expectedChannelName: 'sensitron',
      targetOwnerTwitchId: null,
    });
    syncReq.flush(deletedAnswer());
  });

  describe('7TV rate limiting', () => {
    it('waits out the reported reset and retries the same emote instead of failing it', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron', null);

      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(30));

      // Still in flight, not burnt — and the pause is visible rather than looking like a hang.
      expect(service.queue()[0].status).toBe('in-progress');
      expect(service.rateLimitPauseSeconds()).toBe(31);
      httpMock.expectNone(GQL_ENDPOINT);

      vi.advanceTimersByTime(30_500);

      const retryReq = httpMock.expectOne(GQL_ENDPOINT);
      expect(retryReq.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
      flushApplied(retryReq);

      expect(service.queue()[0].status).toBe('done');
      expect(service.rateLimitPauseSeconds()).toBeNull();

      vi.advanceTimersByTime(330);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    it('re-paces the rest of the run from the reported quota', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

      // limit 100 over a window of 0ms elapsed + 30s remaining => 300ms/request, +10% margin => 330ms.
      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(30, 100));
      vi.advanceTimersByTime(30_500);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));

      // The original 275ms pace is no longer in effect.
      vi.advanceTimersByTime(275);
      httpMock.expectNone(GQL_ENDPOINT);

      vi.advanceTimersByTime(55);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));

      vi.advanceTimersByTime(330);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    it('gives up on an emote after the retries are exhausted, without stalling the queue', () => {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron', null);

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
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron', null);

      httpMock
        .expectOne(GQL_ENDPOINT)
        .flush(null, { status: 429, statusText: 'Too Many Requests' });

      expect(service.queue()[0].status).toBe('in-progress');
      expect(service.rateLimitPauseSeconds()).toBe(60);

      vi.advanceTimersByTime(60_000);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));

      expect(service.queue()[0].status).toBe('done');
      // A headerless rejection carries no quota, so the starting pace must survive it.
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    it('reports the achieved rate at the end of a run — the only way we learn 7TVs real quota', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(DELETE_DELAY_MS);

      // A 90s rate-limit pause pushes the retry more than a minute past the first two requests.
      httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(90));
      vi.advanceTimersByTime(90_500);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));

      // Re-paced to (275ms elapsed + 90s reset) / 100 * 1.1 ≈ 993ms.
      vi.advanceTimersByTime(993);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      expect(console.info).toHaveBeenCalledWith(
        '[EmotePurge] 7TV mass delete finished',
        expect.objectContaining({ requestsSent: 3, peakRequestsPer60s: 2, rateLimitHits: 1 }),
      );
    });

    it('cancel() during a rate-limit pause stops the run and clears the countdown', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
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
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(tokenService.hasToken()).toBe(false);
    expect(service.queue()[0].status).toBe('failed');
    expect(service.queue()[0].errorMessage).toContain('Token ungültig');

    vi.advanceTimersByTime(DELETE_DELAY_MS);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
  });

  it('cancel() marks pending/in-progress items as cancelled and stops the run', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));

    // Second item is now 'pending', waiting out the inter-request delay — cancel before it fires.
    service.cancel();

    expect(service.isRunning()).toBe(false);
    expect(service.queue().map((i) => i.status)).toEqual(['done', 'cancelled']);

    // Only the already-done emote gets synced back.
    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      expectedChannelName: 'sensitron',
      targetOwnerTwitchId: null,
    });
    syncReq.flush(deletedAnswer());

    // Advancing time afterwards must not fire the second (cancelled) request.
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('reset() clears the queue', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

    service.resetIfChannelChanged('other-channel');

    expect(service.isRunning()).toBe(true);
    expect(service.queue()).toHaveLength(2);

    // Drain the run so afterEach's httpMock.verify() stays green.
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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
    service.startDelete('set-2', 'other-channel', [EMOTES[1]], 'other-channel', null);
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
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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
      targetOwnerTwitchId: null,
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
      null,
    );

    expect(service.queue()[0].key).toBe('7tv-live');
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-live'],
      expectedChannelName: 'sensitron',
      targetOwnerTwitchId: null,
    });
    syncReq.flush(deletedAnswer());
    expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-live']);
    expect(service.lastRun()?.result.items[0].emoteId).toBeUndefined();
  });

  // AK 71: the set is frozen into the run record when the run starts. Whatever the page chooses
  // afterwards (modelled here by a refused second start naming another set), the first report and
  // the manual retry both name the run's own set.
  it('reports and retries with the set id frozen at the start of the run', () => {
    service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron', null);
    service.startDelete('set-2', 'sensitron', [EMOTES[1]], 'sensitron', null);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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
      targetOwnerTwitchId: null,
    });
    retryReq.flush(deletedAnswer());
  });

  // Spec 5.1, E3: the legacy `{ emoteIds }` body stays valid on the server for old tabs, but this
  // client never sends it — not even when every row carries a Guid.
  it('sends only the set-centric body form, never the legacy emoteIds', () => {
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
    expect(Object.keys(syncReq.request.body).sort()).toEqual([
      'expectedChannelName',
      'sevenTvEmoteIds',
      'targetOwnerTwitchId',
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
      null,
    );

    expect(service.queue()).toHaveLength(1);
    expect(service.queue()[0].key).toBe('7tv-1');
    expect(service.queue()[0].aliases).toEqual(['PogU', 'PogU2']);

    const removeReq = httpMock.expectOne(GQL_ENDPOINT);
    expect(removeReq.request.body.variables).toEqual({ setId: 'set-1', emoteId: '7tv-1' });
    flushApplied(removeReq);
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);

    httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-1']);
  });

  // Spec 4.6 point 21 / 6.5: a delete from a set that is not the page's active one expects no
  // channel, and the server's paper-only answer (`channels: []`) is a plain success, not partial.
  it('reports a non-active set with no expected channel and reads the paper-only answer as succeeded', () => {
    service.startDelete('set-2', 'sensitron', [EMOTES[0]], null, null);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    const syncReq = httpMock.expectOne(SYNC_ENDPOINT_SET_2);
    expect(syncReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      expectedChannelName: null,
      targetOwnerTwitchId: null,
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
    service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
    const firstQueueLength = service.queue().length;

    service.startDelete('set-2', 'other-channel', [EMOTES[0]], 'other-channel', null);

    expect(service.queue()).toHaveLength(firstQueueLength);

    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
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
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron', null);

      expect(arbiter.activeRun()).toBe('delete');

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      expect(arbiter.activeRun()).toBeNull();
    });

    it('clears the active run once a run ended by cancel() has had its report answered', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));

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

      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);

      expect(service.isRunning()).toBe(false);
      expect(arbiter.activeRun()).toBeNull();
    });
  });

  // #275 (Plan-275 Festlegungen 1, 2, 5, 6, 10–13, 19, 20): a REMOVE whose answer was lost, or that
  // a cancel aborted in flight, ends its row `unknown`; the run is then `settling` while the set is
  // read once, the read only ever confirms (id gone ⇒ `done`), and only the settled outcome is
  // published and reported.
  describe('#275 settling an unknown REMOVE', () => {
    const RESYNC_ENDPOINT = '/api/channels/sensitron/resync';
    const THREE_EMOTES: DeleteQueueEmote[] = [
      ...EMOTES,
      { emoteId: 'internal-3', sevenTvEmoteId: '7tv-3', name: 'OMEGALUL' },
    ];

    /** Starts a one-row run and cancels it while its REMOVE is in flight. */
    function cancelOneRowInFlight(expectedChannelName: string | null = 'sensitron') {
      service.startDelete('set-1', 'sensitron', [EMOTES[0]], expectedChannelName, null);
      const remove = httpMock.expectOne(isRemove);
      service.cancel();
      expect(remove.cancelled).toBe(true);
    }

    /** Lets the cancel's grace period run out and returns the one re-read it then sends. */
    function readAfterGrace() {
      vi.advanceTimersByTime(CANCEL_SETTLE_GRACE_MS);
      return httpMock.expectOne(isSetRead);
    }

    it('settles a REMOVE cancelled in flight: settling, no read before the grace period, then one read that confirms, then the report', () => {
      cancelOneRowInFlight();

      expect(service.isRunning()).toBe(false);
      expect(service.run()?.phase).toBe('settling');
      expect(service.isSettling()).toBe(true);
      expect(service.destructiveOpen()).toBe(true);
      // Festlegung 10: nothing is published before the settle.
      expect(service.run()?.result).toBeNull();
      expect(service.lastRun()).toBeNull();
      // Festlegung 11: the dock shows the engine's own snapshot meanwhile.
      expect(service.queue().map((item) => item.status)).toEqual(['unknown']);
      httpMock.expectNone(SYNC_ENDPOINT);

      vi.advanceTimersByTime(CANCEL_SETTLE_GRACE_MS - 1);
      httpMock.expectNone(isSetRead);
      vi.advanceTimersByTime(1);
      const read = httpMock.expectOne(isSetRead);
      expect(read.request.headers.has('Authorization')).toBe(false);
      expect(read.request.body.variables.id).toBe('set-1');
      read.flush(setEntriesPage([{ id: '7tv-2', alias: 'KEKW' }]));

      expect(service.run()?.phase).toBe('reporting');
      expect(service.queue()[0]).toMatchObject({ key: '7tv-1', status: 'done' });
      expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-1']);
      const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(syncReq.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1'],
        expectedChannelName: 'sensitron',
        targetOwnerTwitchId: null,
      });
      syncReq.flush(deletedAnswer());

      expect(service.run()?.phase).toBe('closed');
      expect(service.syncReport()).toBe('succeeded');
      expect(service.isSettling()).toBe(false);
      expect(service.destructiveOpen()).toBe(false);
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it('leaves the row unknown when the read still shows the id, reports nothing and resyncs the active set', () => {
      cancelOneRowInFlight();

      // Under another alias than the one deleted — still "the id is there", never a guess.
      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'SomeoneElse' }]));

      expect(service.run()?.phase).toBe('closed');
      expect(service.lastRun()?.result.items[0].status).toBe('unknown');
      expect(service.lastRun()?.result.doneKeys).toEqual([]);
      expect(service.syncReport()).toBe('idle');
      expect(service.destructiveOpen()).toBe(false);
      httpMock.expectNone(SYNC_ENDPOINT);
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('sends no resync for an unknown-only run on a set without an expected channel', () => {
      cancelOneRowInFlight(null);

      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));

      expect(service.run()?.phase).toBe('closed');
      expect(service.lastRun()?.result.items[0].status).toBe('unknown');
      httpMock.expectNone(SYNC_ENDPOINT);
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it.each([
      [
        'a failed read',
        (read: ReturnType<HttpTestingController['expectOne']>) =>
          read.error(new ProgressEvent('error')),
      ],
      [
        'a GraphQL-level rejection of the read',
        (read: ReturnType<HttpTestingController['expectOne']>) =>
          read.flush({ errors: [{ message: 'unavailable' }] }),
      ],
      [
        'an incomplete read',
        (read: ReturnType<HttpTestingController['expectOne']>) => {
          // The id is not on this page — but the read only vouches for part of the set.
          const partial = setEntriesPage([{ id: '7tv-2', alias: 'KEKW' }]);
          partial.data.emoteSets.emoteSet.emotes.totalCount = 7;
          read.flush(partial);
        },
      ],
    ])('treats %s like no read at all: the row stays unknown, D6 resync', (_label, answer) => {
      cancelOneRowInFlight();

      answer(readAfterGrace());

      expect(service.run()?.phase).toBe('closed');
      expect(service.lastRun()?.result.items[0].status).toBe('unknown');
      httpMock.expectNone(SYNC_ENDPOINT);
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('gives a read that never answers SET_ENTRIES_READ_TIMEOUT_MS, then settles without it', () => {
      cancelOneRowInFlight();
      const read = readAfterGrace();

      vi.advanceTimersByTime(SET_ENTRIES_READ_TIMEOUT_MS - 1);
      expect(service.run()?.phase).toBe('settling');
      expect(service.destructiveOpen()).toBe(true);
      vi.advanceTimersByTime(1);

      expect(read.cancelled).toBe(true);
      expect(service.run()?.phase).toBe('closed');
      expect(service.lastRun()?.result.items[0].status).toBe('unknown');
      httpMock.expectNone(SYNC_ENDPOINT);
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it.each([
      [
        'an HTTP 503',
        (remove: ReturnType<HttpTestingController['expectOne']>) =>
          remove.flush('boom', { status: 503, statusText: 'Unavailable' }),
      ],
      [
        'no answer at all (status 0)',
        (remove: ReturnType<HttpTestingController['expectOne']>) =>
          remove.error(new ProgressEvent('error')),
      ],
      // #285: a 200 that neither rejects nor confirms the mutation is as unclear as a lost answer.
      [
        'an HTTP 200 with an empty body',
        (remove: ReturnType<HttpTestingController['expectOne']>) => remove.flush(null),
      ],
      [
        'an HTTP 200 with neither data nor errors',
        (remove: ReturnType<HttpTestingController['expectOne']>) => remove.flush({}),
      ],
      [
        'an HTTP 200 whose data stops short of the removeEmote result',
        (remove: ReturnType<HttpTestingController['expectOne']>) => flushWithoutResult(remove),
      ],
    ])(
      'makes %s mid-run unknown, keeps the run going and reads right after it without a grace period',
      (_label, answer) => {
        service.startDelete('set-1', 'sensitron', THREE_EMOTES, 'sensitron', null);
        flushApplied(httpMock.expectOne(isRemove));
        vi.advanceTimersByTime(DELETE_DELAY_MS);
        answer(httpMock.expectOne(isRemove));
        expect(service.queue()[1].status).toBe('unknown');
        expect(service.isRunning()).toBe(true);
        vi.advanceTimersByTime(DELETE_DELAY_MS);
        flushApplied(httpMock.expectOne(isRemove));
        vi.advanceTimersByTime(DELETE_DELAY_MS);

        expect(service.run()?.phase).toBe('settling');
        // No grace period after a plain transport loss: the read is already out.
        httpMock.expectOne(isSetRead).flush(setEntriesPage([{ id: 'other', alias: 'Other' }]));

        // One report after the settle, with the union in queue order.
        const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
        expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1', '7tv-2', '7tv-3']);
        syncReq.flush(deletedAnswer());
        httpMock.expectNone((request) => request.url.endsWith('/resync'));
      },
    );

    it('reports only the confirmed rows of a mixed run and leaves the rest to the report’s backend resync — no client resync', () => {
      service.startDelete('set-1', 'sensitron', THREE_EMOTES, 'sensitron', null);
      flushApplied(httpMock.expectOne(isRemove));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(isRemove).flush('boom', { status: 502, statusText: 'Bad Gateway' });
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      flushApplied(httpMock.expectOne(isRemove));
      vi.advanceTimersByTime(DELETE_DELAY_MS);

      httpMock.expectOne(isSetRead).flush(setEntriesPage([{ id: '7tv-2', alias: 'KEKW' }]));

      expect(service.lastRun()?.result.items.map((item) => item.status)).toEqual([
        'done',
        'unknown',
        'done',
      ]);
      const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1', '7tv-3']);
      syncReq.flush(deletedAnswer({ reportedCount: 2, resyncTriggered: ['sensitron'] }));

      expect(service.run()?.phase).toBe('closed');
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it('settles a cancel in flight after already confirmed rows into one report, not two', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
      flushApplied(httpMock.expectOne(isRemove));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(isRemove);
      service.cancel();

      expect(service.queue().map((item) => item.status)).toEqual(['done', 'unknown']);
      httpMock.expectNone(SYNC_ENDPOINT); // the confirmed row waits for the settle as well

      readAfterGrace().flush(setEntriesPage([]));

      const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1', '7tv-2']);
      syncReq.flush(deletedAnswer({ reportedCount: 2 }));
    });

    it('also waits out the grace period when a cancel between rows ends a run with an older transport loss', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
      httpMock.expectOne(isRemove).flush('boom', { status: 500, statusText: 'Server Error' });
      service.cancel(); // between rows: nothing in flight

      expect(service.queue().map((item) => item.status)).toEqual(['unknown', 'cancelled']);
      httpMock.expectNone(isSetRead);

      readAfterGrace().flush(setEntriesPage([{ id: '7tv-2', alias: 'KEKW' }]));

      expect(httpMock.expectOne(SYNC_ENDPOINT).request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
    });

    it('sends no read for a cancel between rows without any unknown row', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
      flushApplied(httpMock.expectOne(isRemove));
      service.cancel();

      expect(service.queue().map((item) => item.status)).toEqual(['done', 'cancelled']);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      vi.advanceTimersByTime(CANCEL_SETTLE_GRACE_MS + SET_ENTRIES_READ_TIMEOUT_MS);
      httpMock.expectNone(isSetRead);
    });

    it('keeps the settled result referentially stable across the later report patches', () => {
      cancelOneRowInFlight();
      readAfterGrace().flush(setEntriesPage([]));
      const settled = service.lastRun()?.result;
      expect(settled).toBeDefined();

      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 401, statusText: 'Unauthorized' });
      flushFallbackResync();
      expect(service.lastRun()?.result).toBe(settled);

      service.retrySyncReport();
      const retryReq = httpMock.expectOne(SYNC_ENDPOINT);
      // The retry reads the settled doneKeys — the row the re-read confirmed is in it.
      expect(retryReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
      retryReq.flush(deletedAnswer());
      expect(service.lastRun()?.result).toBe(settled);
      expect(service.syncReport()).toBe('succeeded');
    });

    it('refuses a manual retry while the run is still settling', () => {
      cancelOneRowInFlight();

      service.retrySyncReport();

      httpMock.expectNone(SYNC_ENDPOINT);
      readAfterGrace().flush(setEntriesPage([]));
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    // Festlegung 19: reset() while settling only drops the display; the record settles, reports and
    // closes on its own, and a report that then fails shows the run again with its retry.
    it('reset() while settling detaches the display; the run still settles, reports, and a failed report comes back with its retry', () => {
      cancelOneRowInFlight();

      service.reset();

      expect(service.run()).toBeNull();
      expect(service.queue()).toEqual([]);
      expect(service.isSettling()).toBe(true);
      expect(service.destructiveOpen()).toBe(true);

      readAfterGrace().flush(setEntriesPage([]));
      expect(service.run()).toBeNull(); // settled on its own record, still not shown
      const syncReq = httpMock.expectOne(SYNC_ENDPOINT);
      expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
      syncReq.flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();

      expect(service.run()?.phase).toBe('closed');
      expect(service.syncReport()).toBe('failed');
      expect(service.queue().map((item) => item.status)).toEqual(['done']);
      expect(service.destructiveOpen()).toBe(false);

      service.retrySyncReport();
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    it('resetIfChannelChanged() leaves a settling run shown', () => {
      cancelOneRowInFlight();

      service.resetIfChannelChanged('other-channel');

      expect(service.run()?.phase).toBe('settling');
      readAfterGrace().flush(setEntriesPage([]));
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
    });

    // Festlegung 5: the cancel flag lives only for the synchronous span of `cancel()` — a later run
    // that ends on a plain transport loss must read at once, not inherit an earlier run's grace.
    it('does not carry a cancelled run’s grace period over to a later run that ends on a transport loss', () => {
      cancelOneRowInFlight();
      readAfterGrace().flush(setEntriesPage([]));
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());
      expect(service.run()?.phase).toBe('closed');

      service.startDelete('set-2', 'sensitron', [EMOTES[1]], 'sensitron', null);
      httpMock.expectOne(isRemove).flush('boom', { status: 503, statusText: 'Unavailable' });
      vi.advanceTimersByTime(DELETE_DELAY_MS);

      // Already out — no grace period for this run.
      httpMock.expectOne(isSetRead).flush(setEntriesPage([]));
      expect(httpMock.expectOne(SYNC_ENDPOINT_SET_2).request.body.sevenTvEmoteIds).toEqual([
        '7tv-2',
      ]);
    });

    // The settle works on the run's own record by runId: a newer run shown in the meantime keeps the
    // dock, and the superseded run still settles and reports on its own.
    it('settles and reports a detached run by its runId without touching a newer run shown meanwhile', () => {
      cancelOneRowInFlight();
      const runA = service.run()?.runId;
      service.reset();

      service.startDelete('set-2', 'sensitron', [EMOTES[1]], 'sensitron', null);
      const runB = service.run()?.runId;
      expect(runB).not.toBe(runA);
      const removeB = httpMock.expectOne(isRemove);

      const readA = readAfterGrace();
      expect(readA.request.body.variables.id).toBe('set-1');
      readA.flush(setEntriesPage([]));

      // B stays on the dock, with its own live queue.
      expect(service.run()?.runId).toBe(runB);
      expect(service.run()?.phase).toBe('running');
      expect(service.queue().map((item) => [item.key, item.status])).toEqual([
        ['7tv-2', 'in-progress'],
      ]);
      // A reports all the same, for its own set and id.
      const syncA = httpMock.expectOne(SYNC_ENDPOINT);
      expect(syncA.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
      syncA.flush(deletedAnswer());
      expect(service.run()?.runId).toBe(runB);

      flushApplied(removeB);
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(SYNC_ENDPOINT_SET_2).flush(deletedAnswer());
      expect(service.lastRun()?.result.doneKeys).toEqual(['7tv-2']);
    });

    it('resyncs exactly once when a mixed run’s report fails for good', () => {
      service.startDelete('set-1', 'sensitron', THREE_EMOTES, 'sensitron', null);
      flushApplied(httpMock.expectOne(isRemove));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(isRemove).flush('boom', { status: 502, statusText: 'Bad Gateway' });
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      flushApplied(httpMock.expectOne(isRemove));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne(isSetRead).flush(setEntriesPage([{ id: '7tv-2', alias: 'KEKW' }]));

      // A 403 is final — no automatic retry, straight to the N1 fallback.
      httpMock.expectOne(SYNC_ENDPOINT).flush(null, { status: 403, statusText: 'Forbidden' });

      expect(service.syncReport()).toBe('failed');
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      vi.advanceTimersByTime(10_000);
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it('keeps the arbiter busy and the unload guard armed through settling, until closed', () => {
      const arbiter = TestBed.inject(SevenTvRunArbiter);
      cancelOneRowInFlight();

      expect(arbiter.activeClaim()).toEqual({ kind: 'delete', phase: 'settling' });
      expect(arbiter.destructiveOpen()).toBe(true);

      const read = readAfterGrace();
      expect(arbiter.activeClaim()).toEqual({ kind: 'delete', phase: 'settling' });
      read.flush(setEntriesPage([]));

      // Reporting counts as settling for the arbiter too (#256, contract P2).
      expect(arbiter.activeRun()).toBe('delete');
      expect(arbiter.destructiveOpen()).toBe(true);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      expect(arbiter.activeClaim()).toBeNull();
      expect(arbiter.destructiveOpen()).toBe(false);
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
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', null);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));

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

  // #201 T-C: a tag removal reports a second time, to the tag's own endpoint.
  describe('tag removal report', () => {
    const TAG: DeleteTagContext = {
      tagId: 7,
      operationId: 'op-1',
      activationOperationId: 'act-1',
      snapshot: [
        { sevenTvEmoteId: '7tv-1', placementOperationId: 'p-1' },
        { sevenTvEmoteId: '7tv-2', placementOperationId: 'p-2' },
        { sevenTvEmoteId: '7tv-9', placementOperationId: 'p-9' },
      ],
      checkedOwnIds: ['7tv-1', '7tv-2'],
      uncheckedOwnIds: ['7tv-9'],
      channelName: 'sensitron',
    };
    const REMOVED = '/api/channels/sensitron/tags/7/placements/removed';

    function removalAnswer(overrides: Partial<TagRemovalResult> = {}): TagRemovalResult {
      return {
        replayed: false,
        deletedCount: 0,
        transferredCount: 0,
        droppedCount: 0,
        sweptCount: 0,
        deactivated: true,
        ...overrides,
      };
    }

    /** Both rows of `EMOTES`: the first `REMOVE` is applied, the second ends as given. */
    function runTwoRows(
      second: 'applied' | 'failed' = 'applied',
      tag: DeleteTagContext | null = TAG,
    ) {
      if (tag === null) {
        service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', 'tw-owner');
      } else {
        service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', 'tw-owner', tag);
      }
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      if (second === 'applied') {
        flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      } else {
        httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'emote not found' }] });
      }
      vi.advanceTimersByTime(DELETE_DELAY_MS);
    }

    function answerSync(): void {
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer({ reportedCount: 2 }));
    }

    it('stays idle without a tag, and the run closes without waiting for it', () => {
      runTwoRows('applied', null);
      answerSync();

      expect(service.run()?.tag).toBeUndefined();
      expect(service.tagRemovalReport()).toBe('idle');
      expect(service.run()?.phase).toBe('closed');
      httpMock.expectNone(REMOVED);
    });

    it('holds the run open while pending and closes it once the report succeeds', () => {
      runTwoRows();
      answerSync();

      expect(service.tagRemovalReport()).toBe('pending');
      expect(service.run()?.phase).toBe('reporting');
      expect(service.isSettling()).toBe(true);
      expect(service.destructiveOpen()).toBe(true);

      httpMock.expectOne(REMOVED).flush(removalAnswer());

      expect(service.tagRemovalReport()).toBe('succeeded');
      expect(service.run()?.phase).toBe('closed');
      expect(service.isSettling()).toBe(false);
    });

    it('holds the run open for the sync report too when the removal report answers first', () => {
      runTwoRows();
      httpMock.expectOne(REMOVED).flush(removalAnswer());

      expect(service.run()?.phase).toBe('reporting');
      answerSync();
      expect(service.run()?.phase).toBe('closed');
    });

    it('sends the done keys as removedIds, the registered operation, set, owner and snapshot', () => {
      runTwoRows();
      const report = httpMock.expectOne(REMOVED);
      answerSync();

      expect(report.request.method).toBe('POST');
      expect(report.request.body).toEqual({
        operationId: 'op-1',
        emoteSetId: 'set-1',
        targetOwnerTwitchId: 'tw-owner',
        activationOperationId: 'act-1',
        snapshot: TAG.snapshot,
        removedIds: ['7tv-1', '7tv-2'],
        keptIds: ['7tv-9'],
      });
      report.flush(removalAnswer());
    });

    it('derives keptIds from the run result: a ticked row that failed stays, the unticked always do', () => {
      runTwoRows('failed');
      const report = httpMock.expectOne(REMOVED);
      httpMock.expectOne(SYNC_ENDPOINT).flush(deletedAnswer());

      expect(report.request.body.removedIds).toEqual(['7tv-1']);
      expect(report.request.body.keptIds).toEqual(['7tv-9', '7tv-2']);
      report.flush(removalAnswer());
    });

    it('passes a null activation operation through unchanged', () => {
      runTwoRows('applied', { ...TAG, activationOperationId: null });
      const report = httpMock.expectOne(REMOVED);
      answerSync();

      expect(report.request.body.activationOperationId).toBeNull();
      report.flush(removalAnswer());
    });

    it('still reports, with nothing removed, after a cancel before the first row', () => {
      service.startDelete('set-1', 'sensitron', EMOTES, 'sensitron', 'tw-owner', TAG);
      const first = httpMock.expectOne(GQL_ENDPOINT);

      service.cancel();
      vi.advanceTimersByTime(CANCEL_SETTLE_GRACE_MS);
      httpMock.expectOne(isSetRead).flush(
        setEntriesPage([
          { id: '7tv-1', alias: 'PogU' },
          { id: '7tv-2', alias: 'KEKW' },
        ]),
      );

      expect(first.cancelled).toBe(true);
      const report = httpMock.expectOne(REMOVED);
      expect(report.request.body.removedIds).toEqual([]);
      expect(report.request.body.keptIds).toEqual(['7tv-9', '7tv-1', '7tv-2']);
      report.flush(removalAnswer());
      // The unknown row has nothing to report to sync-deleted, so the fallback resync stands in.
      flushFallbackResync();
      expect(service.run()?.phase).toBe('closed');
    });

    it('fails a 403 as forbidden without an automatic retry', () => {
      runTwoRows();
      answerSync();

      httpMock.expectOne(REMOVED).flush({}, { status: 403, statusText: 'Forbidden' });
      vi.advanceTimersByTime(60_000);

      httpMock.expectNone(REMOVED);
      expect(service.tagRemovalReport()).toBe('failed');
      expect(service.tagRemovalReportReason()).toBe('forbidden');
      expect(service.run()?.phase).toBe('closed');
    });

    it('retries a failed report with the same body, and treats a replay as a plain success', () => {
      runTwoRows();
      answerSync();
      const first = httpMock.expectOne(REMOVED);
      const firstBody: unknown = first.request.body;
      first.flush({}, { status: 403, statusText: 'Forbidden' });
      expect(service.tagRemovalReport()).toBe('failed');

      service.retryTagRemovalReport();

      expect(service.tagRemovalReport()).toBe('pending');
      const second = httpMock.expectOne(REMOVED);
      expect(second.request.body).toEqual(firstBody);
      // F34: a replay carries no outcome — success, nothing else read from it.
      second.flush(removalAnswer({ replayed: true, deactivated: false }));
      expect(service.tagRemovalReport()).toBe('succeeded');
      expect(service.tagRemovalReportReason()).toBeNull();
    });

    it('offers no retry for a run without a tag', () => {
      runTwoRows('applied', null);
      answerSync();

      service.retryTagRemovalReport();

      httpMock.expectNone(REMOVED);
      expect(service.tagRemovalReport()).toBe('idle');
    });
  });
});
