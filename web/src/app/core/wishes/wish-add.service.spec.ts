import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
  TestRequest,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, Observable, of, Subject, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../emotes/emote-admin.service';
import { EmoteSetStatus } from '../emotes/emote-set-status.model';
import { EditableSetResolution, EmoteSetListResponse } from '../seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../seven-tv/seven-tv-emote-set.service';
import { flushApplied } from '../seven-tv/seven-tv-mutation.testing';
import { SevenTvRunArbiter } from '../seven-tv/seven-tv-run-arbiter';
import { RUN_DELAY_MS } from '../seven-tv/seven-tv-run-engine';
import { SevenTvTokenService } from '../seven-tv/seven-tv-token.service';
import type { Wish, WishDetail } from './wish.model';
import { WishAddRequest, WishAddService, WishAddState } from './wish-add.service';
import { WishService } from './wish.service';

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';
const CHANNEL = 'olaf';
const SET_A = 'set-a';
const SET_B = 'set-b';
const RATE_LIMIT_RESET_SECONDS = 2;
const RATE_LIMIT_WAIT_MS = RATE_LIMIT_RESET_SECONDS * 1000 + 500;

function wish(over: Partial<Wish> = {}): Wish {
  return {
    id: 7,
    sevenTvEmoteId: '01EMOTE',
    emoteName: 'KEKW',
    alias: 'KEKW',
    source: 'manual',
    status: 'approved',
    statusReason: null,
    requesterLogin: null,
    durationDays: 14,
    createdAtUtc: '2026-11-01T10:00:00Z',
    approvedAtUtc: '2026-11-01T10:00:00Z',
    startsAtUtc: null,
    startsAtAuthoritative: false,
    probationStartsAtUtc: null,
    expiresAtUtc: null,
    endedAtUtc: null,
    slotId: null,
    slotAddedAtUtc: null,
    imageUrl: null,
    linkOrigin: null,
    extensionCount: 0,
    permanent: false,
    botOwned: false,
    botWritePending: false,
    botNextAttemptAtUtc: null,
    removalQueuedAtUtc: null,
    mode: 'manual',
    ...over,
  };
}

function rateLimitResponse() {
  return {
    errors: [
      {
        message: 'RATE_LIMIT_EXCEEDED rate limit exceeded',
        extensions: {
          code: 'RATE_LIMIT_EXCEEDED',
          status: 429,
          headers: {
            'x-ratelimit-emote_set_change-remaining': '0',
            'x-ratelimit-emote_set_change-reset': String(RATE_LIMIT_RESET_SECONDS),
            'x-ratelimit-emote_set_change-limit': '100',
          },
        },
      },
    ],
  };
}

/** The Api side of a start as it stands right now; each read answers from it at once. */
class Api {
  liveSet: string | HttpErrorResponse = SET_A;
  storedSet = SET_A;
  liveWish: Wish | HttpErrorResponse = wish();

  readonly emoteSetService = {
    listForeignChannelEmoteSets: vi.fn(
      (_channel: string, options?: { refresh?: boolean }): Observable<EmoteSetListResponse> => {
        if (options?.refresh !== true) {
          throw new Error('The wish add reads the set list with refresh only');
        }
        const live = this.liveSet;
        return typeof live === 'string'
          ? of({ activeEmoteSetId: live, sets: [] })
          : throwError(() => live);
      },
    ),
    resolveEditableSet: vi.fn((): Observable<EditableSetResolution> =>
      of({ status: 'editable', target: { ownerTwitchChannelId: null } as never }),
    ),
  };
  readonly emoteAdminService = {
    getSetStatus: vi.fn((): Observable<EmoteSetStatus> =>
      of({ activeEmoteSetId: this.storedSet } as EmoteSetStatus),
    ),
  };
  readonly wishService = {
    get: vi.fn((): Observable<WishDetail> =>
      this.liveWish instanceof HttpErrorResponse
        ? throwError(() => this.liveWish)
        : of({ ...this.liveWish, usage: null }),
    ),
  };

  calls(): number {
    return (
      this.emoteSetService.listForeignChannelEmoteSets.mock.calls.length +
      this.emoteSetService.resolveEditableSet.mock.calls.length +
      this.emoteAdminService.getSetStatus.mock.calls.length +
      this.wishService.get.mock.calls.length
    );
  }
}

describe('WishAddService', () => {
  let service: WishAddService;
  let tokenService: SevenTvTokenService;
  let arbiter: SevenTvRunArbiter;
  let httpMock: HttpTestingController;
  let api: Api;
  let ended: WishAddState[];

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    vi.spyOn(console, 'info').mockImplementation(() => undefined);
    api = new Api();
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SevenTvEmoteSetService, useValue: api.emoteSetService },
        { provide: EmoteAdminService, useValue: api.emoteAdminService },
        { provide: WishService, useValue: api.wishService },
      ],
    });
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    service = TestBed.inject(WishAddService);
    tokenService = TestBed.inject(SevenTvTokenService);
    arbiter = TestBed.inject(SevenTvRunArbiter);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
    ended = [];
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function start(over: Partial<WishAddRequest> = {}): void {
    service.start({
      channelName: CHANNEL,
      wish: wish(),
      caller: { alive: () => true, channelName: () => CHANNEL },
      promptToken: () => of(false),
      onEnd: (state) => ended.push(state),
      ...over,
    });
  }

  function state(over: Partial<Wish> = {}): WishAddState | null {
    return service.stateFor(CHANNEL, wish(over));
  }

  function mutation(): TestRequest {
    return httpMock.expectOne(GQL_ENDPOINT);
  }

  /** The ADD answered with 429; the run waits out the pause, then checks again. */
  function rateLimitedOnce(): void {
    mutation().flush(rateLimitResponse());
    expect(state()).toEqual({ kind: 'running', pauseSeconds: RATE_LIMIT_RESET_SECONDS + 1 });
  }

  it('registers as the run kind wish and holds the arbiter while the ADD is out', () => {
    expect(arbiter.activeRun()).toBeNull();
    start();

    expect(arbiter.activeRun()).toBe('wish');
    expect(arbiter.startPending()).toBe(false);
    flushApplied(mutation());
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(arbiter.activeRun()).toBeNull();
    expect(arbiter.destructiveOpen()).toBe(false);
  });

  it('locks every start while its start reads are out, and frees it on an abort', () => {
    const read = new Subject<EmoteSetListResponse>();
    api.emoteSetService.listForeignChannelEmoteSets.mockReturnValueOnce(read);
    start();

    expect(state()).toEqual({ kind: 'starting' });
    expect(arbiter.startLocked()).toBe(true);
    expect(arbiter.activeRun()).toBeNull();

    read.error(new HttpErrorResponse({ status: 429 }));

    expect(arbiter.startLocked()).toBe(false);
    expect(ended).toEqual([{ kind: 'notStarted', reason: 'setUnverified' }]);
  });

  it('sends the ADD to the frozen set with the wish emote and alias, under the confirmed token', () => {
    api.liveWish = wish({ alias: 'KEKWait' });
    start({ wish: wish({ alias: 'KEKWait' }) });

    const request = mutation();
    expect(request.request.body.variables).toEqual({
      setId: SET_A,
      emoteId: '01EMOTE',
      alias: 'KEKWait',
    });
    expect(request.request.headers.get('Authorization')).toBe('Bearer write-token');
    flushApplied(request);
    vi.advanceTimersByTime(RUN_DELAY_MS);
  });

  it('ends "added" on success and calls the Api no more after the ADD', () => {
    start();
    const before = api.calls();

    flushApplied(mutation());
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(ended).toEqual([{ kind: 'added' }]);
    expect(state()).toEqual({ kind: 'added' });
    expect(api.calls()).toBe(before);
  });

  it('ends "rejected" with the name conflict on GraphQL status 409', () => {
    start();
    mutation().flush({ errors: [{ message: 'name taken', extensions: { status: 409 } }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(ended).toEqual([{ kind: 'rejected', reason: 'nameTaken', message: 'name taken' }]);
  });

  it('ends "rejected" for a token 7TV refused, which is cleared', () => {
    start();
    mutation().flush(null, { status: 401, statusText: 'Unauthorized' });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(ended[0]).toMatchObject({ kind: 'rejected', reason: 'tokenInvalid' });
    expect(tokenService.hasToken()).toBe(false);
  });

  it('shows the engine text for any other refusal', () => {
    start();
    mutation().flush({ errors: [{ message: 'LACKING_PRIVILEGES', extensions: {} }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(ended).toEqual([{ kind: 'rejected', reason: 'other', message: 'LACKING_PRIVILEGES' }]);
  });

  it('ends "unknown" when the answer got lost, without trying again', () => {
    start();
    mutation().error(new ProgressEvent('error'), { status: 0 });
    vi.advanceTimersByTime(RUN_DELAY_MS + RATE_LIMIT_WAIT_MS);

    expect(ended).toEqual([{ kind: 'unknown' }]);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('ends "notStarted" with the reason of the start contract, and sends nothing', () => {
    api.liveSet = SET_B;
    start();

    expect(ended).toEqual([{ kind: 'notStarted', reason: 'setSwitched' }]);
    httpMock.expectNone(GQL_ENDPOINT);
  });

  it('leaves no state when the token prompt is dismissed', () => {
    tokenService.clearToken();
    start({ promptToken: () => of(false) });

    expect(state()).toBeNull();
    expect(ended).toEqual([]);
    expect(arbiter.startLocked()).toBe(false);
  });

  describe('before every send, also after a 429', () => {
    it('sends exactly one retry when nothing changed, after reading set and wish afresh', () => {
      start();
      const setReads = api.emoteSetService.listForeignChannelEmoteSets.mock.calls.length;
      const wishReads = api.wishService.get.mock.calls.length;
      expect(setReads).toBe(2);
      rateLimitedOnce();

      vi.advanceTimersByTime(RATE_LIMIT_WAIT_MS);

      flushApplied(mutation());
      vi.advanceTimersByTime(RUN_DELAY_MS);
      expect(api.emoteSetService.listForeignChannelEmoteSets).toHaveBeenCalledTimes(setReads + 1);
      expect(api.wishService.get).toHaveBeenCalledTimes(wishReads + 1);
      expect(ended).toEqual([{ kind: 'added' }]);
    });

    it('sends no second ADD when the wish was changed during the pause', () => {
      start();
      rateLimitedOnce();

      api.liveWish = wish({ status: 'closed' });
      vi.advanceTimersByTime(RATE_LIMIT_WAIT_MS + RUN_DELAY_MS);

      httpMock.expectNone(GQL_ENDPOINT);
      expect(ended).toEqual([{ kind: 'skipped', reason: 'wishChanged' }]);
    });

    it('sends no second ADD when only the refresh read shows another active set', () => {
      start();
      rateLimitedOnce();

      api.liveSet = SET_B;
      vi.advanceTimersByTime(RATE_LIMIT_WAIT_MS + RUN_DELAY_MS);

      httpMock.expectNone(GQL_ENDPOINT);
      expect(ended).toEqual([{ kind: 'skipped', reason: 'setChanged' }]);
    });

    it('sends no second ADD, and reads nothing, when the token was removed during the pause', () => {
      start();
      rateLimitedOnce();
      const reads = api.calls();

      tokenService.clearToken();
      vi.advanceTimersByTime(RATE_LIMIT_WAIT_MS + RUN_DELAY_MS);

      httpMock.expectNone(GQL_ENDPOINT);
      expect(api.calls()).toBe(reads);
      expect(ended).toEqual([{ kind: 'skipped', reason: 'tokenChanged' }]);
    });

    it('sends no second ADD when another token was entered during the pause', () => {
      start();
      rateLimitedOnce();

      tokenService.setToken('other-token');
      vi.advanceTimersByTime(RATE_LIMIT_WAIT_MS + RUN_DELAY_MS);

      httpMock.expectNone(GQL_ENDPOINT);
      expect(ended).toEqual([{ kind: 'skipped', reason: 'tokenChanged' }]);
    });

    it('sends no second ADD when the set read fails or the wish read fails', () => {
      start();
      rateLimitedOnce();
      api.liveSet = new HttpErrorResponse({ status: 429 });
      vi.advanceTimersByTime(RATE_LIMIT_WAIT_MS + RUN_DELAY_MS);
      httpMock.expectNone(GQL_ENDPOINT);
      expect(ended).toEqual([{ kind: 'skipped', reason: 'setUnverified' }]);

      api.liveSet = SET_A;
      ended = [];
      start();
      rateLimitedOnce();
      api.liveWish = new HttpErrorResponse({ status: 503 });
      vi.advanceTimersByTime(RATE_LIMIT_WAIT_MS + RUN_DELAY_MS);
      httpMock.expectNone(GQL_ENDPOINT);
      expect(ended).toEqual([{ kind: 'skipped', reason: 'wishUnverified' }]);
    });
  });

  describe('the result at the row', () => {
    it('stays while the wish is approved and is gone once it is not', () => {
      start();
      flushApplied(mutation());
      vi.advanceTimersByTime(RUN_DELAY_MS);

      expect(state()).toEqual({ kind: 'added' });
      expect(state({ status: 'active' })).toBeNull();
      expect(service.stateFor('another-channel', wish())).toBeNull();
    });

    it('drops a finished result of a wish that left approved, so it does not come back', () => {
      start();
      flushApplied(mutation());
      vi.advanceTimersByTime(RUN_DELAY_MS);

      service.prune(CHANNEL, [wish({ status: 'active' })]);

      expect(state()).toBeNull();
    });

    it('keeps a run in progress through a prune, and finishes it when the wish turned active first', () => {
      start();
      service.prune(CHANNEL, [wish({ status: 'active' })]);
      expect(state()).toEqual({ kind: 'running', pauseSeconds: null });

      flushApplied(mutation());
      vi.advanceTimersByTime(RUN_DELAY_MS);

      expect(ended).toEqual([{ kind: 'added' }]);
      expect(state({ status: 'active' })).toBeNull();
    });
  });
});
