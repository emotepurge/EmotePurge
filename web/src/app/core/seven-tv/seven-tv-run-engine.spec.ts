import { HttpClient } from '@angular/common/http';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  RUN_DELAY_MS,
  RunOperation,
  RunQueueEmote,
  RunResult,
  SevenTvRunEngine,
} from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

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

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';

const TEST_OPERATION: RunOperation = {
  label: 'test run',
  buildRequest: (setId, emote) => ({
    query: 'mutation Test',
    variables: { setId, emoteId: emote.sevenTvEmoteId },
  }),
};

const EMOTES: RunQueueEmote[] = [
  { key: 'internal-1', emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { key: 'internal-2', emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

function rateLimitResponse(resetSeconds: number) {
  return {
    errors: [
      {
        message: 'RATE_LIMIT_EXCEEDED rate limit exceeded',
        extensions: {
          code: 'RATE_LIMIT_EXCEEDED',
          status: 429,
          headers: {
            'x-ratelimit-emote_set_change-remaining': '0',
            'x-ratelimit-emote_set_change-reset': String(resetSeconds),
            'x-ratelimit-emote_set_change-limit': '100',
          },
        },
      },
    ],
  };
}

describe('SevenTvRunEngine', () => {
  let engine: SevenTvRunEngine;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;
  let results: RunResult[];

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
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
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    tokenService = TestBed.inject(SevenTvTokenService);
    engine = new SevenTvRunEngine(
      TestBed.inject(HttpClient),
      tokenService,
      TestBed.inject(TranslocoService),
    );
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
    results = [];
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function start(
    emotes: RunQueueEmote[] = EMOTES,
    operation: RunOperation = TEST_OPERATION,
  ): boolean {
    return engine.start('set-1', emotes, operation, (result) => results.push(result));
  }

  it('refuses to start without a token and reports it to the caller', () => {
    tokenService.clearToken();
    expect(start()).toBe(false);
    expect(engine.isRunning()).toBe(false);
    expect(engine.queue()).toEqual([]);
  });

  it('refuses to start on an empty list', () => {
    expect(start([])).toBe(false);
  });

  it('runs the queue sequentially through the operation and completes with the done ids', () => {
    expect(start()).toBe(true);
    expect(engine.queue().map((item) => item.status)).toEqual(['in-progress', 'pending']);

    const req1 = httpMock.expectOne(GQL_ENDPOINT);
    expect(req1.request.headers.get('Authorization')).toBe('Bearer write-token');
    expect(req1.request.body).toEqual({
      query: 'mutation Test',
      variables: { setId: 'set-1', emoteId: '7tv-1' },
    });
    req1.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'boom' }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(engine.isRunning()).toBe(false);
    expect(results).toHaveLength(1);
    expect(results[0].doneKeys).toEqual(['internal-1']);
    expect(results[0].items.map((item) => item.status)).toEqual(['done', 'failed']);
    expect(results[0].finishedAt).toBeGreaterThanOrEqual(results[0].startedAt);
  });

  it('carries a row without an emoteId through the queue and reports its key', () => {
    const emote: RunQueueEmote = { key: 'import-1', sevenTvEmoteId: '7tv-9', name: 'PogU' };
    expect(start([emote])).toBe(true);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(engine.isRunning()).toBe(false);
    expect(results[0].doneKeys).toEqual(['import-1']);
  });

  it('matches a queue key exactly, never as a prefix', () => {
    const emotes: RunQueueEmote[] = [
      { key: 'abc', emoteId: 'internal-abc', sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { key: 'abcd', emoteId: 'internal-abcd', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];
    expect(start(emotes)).toBe(true);

    httpMock.expectOne(GQL_ENDPOINT).flush({});

    expect(engine.queue()[0].status).toBe('done');
    expect(engine.queue()[1].status).toBe('pending');

    // Drain the rest so httpMock.verify() stays green.
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
  });

  it('lists every finished row in doneKeys, whether or not it carries an emoteId', () => {
    const emotes: RunQueueEmote[] = [
      { key: 'k1', emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { key: 'k2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];
    expect(start(emotes)).toBe(true);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(results[0].doneKeys).toEqual(['k1', 'k2']);
  });

  // Spec #200, E2/AK 67–68: the Guid list beside doneKeys is gone — keys are the one identity a
  // finished run reports, and they are whatever the calling service minted (7TV ids for a delete).
  it('reports doneKeys as the only identity of a finished run — no Guid list beside it', () => {
    const emotes: RunQueueEmote[] = [
      { key: '7tv-1', emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { key: '7tv-2', emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];
    expect(start(emotes)).toBe(true);

    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'boom' }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(results[0].doneKeys).toEqual(['7tv-2']);
    expect(Object.keys(results[0]).sort()).toEqual([
      'doneKeys',
      'finishedAt',
      'items',
      'startedAt',
    ]);
  });

  it('runs a delete-shaped row without an emoteId like any other and keeps its aliases on the item', () => {
    const emote: RunQueueEmote = {
      key: '7tv-9',
      sevenTvEmoteId: '7tv-9',
      name: 'PogU',
      aliases: ['PogU', 'PogU2'],
    };
    expect(start([emote])).toBe(true);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(results[0].doneKeys).toEqual(['7tv-9']);
    expect(results[0].items[0].emoteId).toBeUndefined();
    expect(results[0].items[0].aliases).toEqual(['PogU', 'PogU2']);
    expect(results[0].items[0].status).toBe('done');
  });

  it('waits out a GQL rate limit and retries instead of failing the emote', () => {
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(30));

    expect(engine.queue()[0].status).toBe('in-progress');
    expect(engine.rateLimitPauseSeconds()).toBe(31);

    vi.advanceTimersByTime(30_500);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    // Generously past the re-paced inter-request delay (~330ms after the 100-per-30s answer).
    vi.advanceTimersByTime(1000);

    expect(engine.queue()[0].status).toBe('done');
    expect(results[0].doneKeys).toEqual(['internal-1']);
  });

  it('backs off a bare HTTP 429 for a full window', () => {
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 429, statusText: 'Too Many Requests' });

    expect(engine.rateLimitPauseSeconds()).toBe(60);
    vi.advanceTimersByTime(60_000);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(engine.queue()[0].status).toBe('done');
  });

  it('backs off a degenerate rate-limit answer for a full window when extensions.headers is missing', () => {
    // Safety net for the plan-149 §0.3 parity claim: v4 uses the same ApiError serializer as v3, but
    // nothing guarantees every rejection carries the header mirror. isRateLimitError only looks at
    // extensions.code/status, so this must still be recognised as a rate limit and fall back to the
    // blind 60s wait — never fall through to readRateLimitInfo silently defaulting everything to
    // null and being (mis-)treated as an ordinary row failure.
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({
      errors: [
        {
          message: 'RATE_LIMIT_EXCEEDED rate limit exceeded',
          extensions: { code: 'RATE_LIMIT_EXCEEDED' },
        },
      ],
    });

    expect(engine.queue()[0].status).toBe('in-progress');
    expect(engine.rateLimitPauseSeconds()).toBe(60);

    vi.advanceTimersByTime(60_000);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(engine.queue()[0].status).toBe('done');
  });

  it('clears the token on 401 so the UI falls back to the prompt', () => {
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 401, statusText: 'Unauthorized' });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(tokenService.hasToken()).toBe(false);
    expect(engine.queue()[0].status).toBe('failed');
    expect(engine.queue()[0].errorMessage).toContain('Token ungültig');
  });

  it('cancel() marks the rest cancelled, keeps terminal states and still completes the run', () => {
    start();
    httpMock.expectOne(GQL_ENDPOINT).flush({});

    engine.cancel();

    expect(engine.isRunning()).toBe(false);
    expect(engine.queue().map((item) => item.status)).toEqual(['done', 'cancelled']);
    expect(results).toHaveLength(1);
    expect(results[0].doneKeys).toEqual(['internal-1']);

    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('cancel() outside a run does nothing', () => {
    engine.cancel();
    expect(results).toEqual([]);
  });

  it('logs the closing measurement under the operation label', () => {
    start([EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(console.info).toHaveBeenCalledWith(
      '[EmotePurge] 7TV test run finished',
      expect.objectContaining({ requested: 1, succeeded: 1 }),
    );
  });

  describe('abortOn', () => {
    it('aborts synchronously when the hook returns true, cancelling the rest without waiting', () => {
      const emotes: RunQueueEmote[] = [
        { key: 'k1', emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
        { key: 'k2', emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
        { key: 'k3', emoteId: 'internal-3', sevenTvEmoteId: '7tv-3', name: 'FeelsBadMan' },
      ];
      const operation: RunOperation = { ...TEST_OPERATION, abortOn: () => true };
      expect(start(emotes, operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'boom' }] });

      // Abort lands synchronously on the failing row — no RUN_DELAY_MS wait, not even for the
      // finish() that follows.
      expect(engine.queue().map((item) => item.status)).toEqual(['done', 'failed', 'cancelled']);
      expect(engine.isRunning()).toBe(false);
      expect(results).toHaveLength(1);
      expect(results[0].doneKeys).toEqual(['k1']);
      expect(engine.rateLimitPauseSeconds()).toBeNull();

      // No further request even after the queue's own pacing delay would otherwise have elapsed.
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('hook returning false leaves the run identical to one without a hook', () => {
      const operation: RunOperation = { ...TEST_OPERATION, abortOn: () => false };
      expect(start(EMOTES, operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'boom' }] });
      vi.advanceTimersByTime(RUN_DELAY_MS);

      expect(engine.isRunning()).toBe(false);
      expect(engine.queue().map((item) => item.status)).toEqual(['done', 'failed']);
      expect(results).toHaveLength(1);
      expect(results[0].doneKeys).toEqual(['internal-1']);
    });

    it('gives the hook the raw GQL message, a null httpStatus and a null errorCode for a GQL-level rejection without extensions', () => {
      const abortOn = vi.fn().mockReturnValue(false);
      const operation: RunOperation = { ...TEST_OPERATION, abortOn };
      expect(start([EMOTES[0]], operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'insufficient privileges' }] });

      expect(abortOn).toHaveBeenCalledExactlyOnceWith({
        message: 'insufficient privileges',
        httpStatus: null,
        errorCode: null,
      });
    });

    it('passes extensions.code through to the hook for a structured GQL rejection', () => {
      // The v4 shape the migration exists for: a missing-permission mutation answers with HTTP 200
      // and extensions.code = LACKING_PRIVILEGES, no httpStatus to match on at all — the caller's
      // abortsForMissingPrivileges (a different task) reads this field, not the message text.
      const abortOn = vi.fn().mockReturnValue(false);
      const operation: RunOperation = { ...TEST_OPERATION, abortOn };
      expect(start([EMOTES[0]], operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({
        errors: [
          {
            message: 'LACKING_PRIVILEGES you are not an editor for this user',
            extensions: { code: 'LACKING_PRIVILEGES' },
          },
        ],
      });

      expect(abortOn).toHaveBeenCalledExactlyOnceWith({
        message: 'LACKING_PRIVILEGES you are not an editor for this user',
        httpStatus: null,
        errorCode: 'LACKING_PRIVILEGES',
      });
    });

    it('gives the hook the translated text, the HTTP status and a null errorCode for a transport failure, after the token is cleared', () => {
      const abortOn = vi.fn().mockReturnValue(false);
      const operation: RunOperation = { ...TEST_OPERATION, abortOn };
      expect(start([EMOTES[0]], operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 403, statusText: 'Forbidden' });

      expect(tokenService.hasToken()).toBe(false);
      expect(abortOn).toHaveBeenCalledExactlyOnceWith({
        message: 'Token ungültig oder abgelaufen — bitte neues 7TV-Token eintragen.',
        httpStatus: 403,
        errorCode: null,
      });
    });

    it('passes httpStatus 0 and a null errorCode for a network error', () => {
      const abortOn = vi.fn().mockReturnValue(false);
      const operation: RunOperation = { ...TEST_OPERATION, abortOn };
      expect(start([EMOTES[0]], operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush(null, { status: 0, statusText: 'Unknown Error' });

      expect(abortOn).toHaveBeenCalledExactlyOnceWith({
        message: 'Keine Verbindung zu 7TV möglich (Netzwerkfehler).',
        httpStatus: 0,
        errorCode: null,
      });
    });

    it('skips the hook for a successful row and for a rate-limit retry, calling it once after the give-up', () => {
      const abortOn = vi.fn().mockReturnValue(false);
      const operation: RunOperation = { ...TEST_OPERATION, abortOn };
      expect(start([EMOTES[0], EMOTES[1]], operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      expect(abortOn).not.toHaveBeenCalled();

      // Keep rate-limiting the second row until runWithBackoff exhausts its retries and gives up.
      for (let attempt = 0; engine.queue()[1].status !== 'failed'; attempt++) {
        if (attempt > 10) {
          throw new Error('rate-limit retries did not exhaust within 10 attempts');
        }
        httpMock.expectOne(GQL_ENDPOINT).flush(rateLimitResponse(1));
        vi.advanceTimersByTime(2000);
      }

      expect(abortOn).toHaveBeenCalledExactlyOnceWith({
        message: '7TV-Rate-Limit auch nach mehreren Wartezyklen aktiv — Emote übersprungen.',
        httpStatus: null,
        errorCode: null,
      });
    });

    it("calls onComplete exactly once when the abort happens on the run's last row", () => {
      const operation: RunOperation = { ...TEST_OPERATION, abortOn: () => true };
      expect(start([EMOTES[0]], operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'boom' }] });

      expect(engine.isRunning()).toBe(false);
      expect(engine.queue()[0].status).toBe('failed');
      expect(results).toHaveLength(1);

      // If the abort and the chain's own end-of-queue completion both fired finish(), this would
      // be 2 — the trap this test exists to catch.
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectNone(GQL_ENDPOINT);
      expect(results).toHaveLength(1);
    });

    it('treats a throwing hook as false and reports it via console.error, letting the run continue', () => {
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      const abortOn = vi.fn().mockImplementation(() => {
        throw new Error('boom from abortOn');
      });
      const operation: RunOperation = { ...TEST_OPERATION, abortOn };
      expect(start(EMOTES, operation)).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'boom' }] });
      vi.advanceTimersByTime(RUN_DELAY_MS);

      expect(engine.isRunning()).toBe(false);
      expect(engine.queue().map((item) => item.status)).toEqual(['done', 'failed']);
      expect(console.error).toHaveBeenCalledTimes(1);
      expect(results).toHaveLength(1);
    });
  });
});
