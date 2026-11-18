import { HttpErrorResponse } from '@angular/common/http';
import { signal, WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, of, Subject, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../emotes/emote-admin.service';
import { EmoteSetStatus } from '../emotes/emote-set-status.model';
import { EditableSetResolution, EmoteSetListResponse } from '../seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../seven-tv/seven-tv-emote-set.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../seven-tv/seven-tv-token.service';
import type { Wish, WishDetail } from './wish.model';
import {
  decideWishStart,
  FrozenWishStart,
  runWishStart,
  verifySetAndToken,
  WISH_START_READ_TIMEOUT_MS,
  WishRunStartDeps,
  WishRunStartHooks,
  WishRunStartRequest,
  WishStartAbortReason,
  WishStartDecisionInput,
} from './wish-run-start';
import { WishService } from './wish.service';

const CHANNEL = 'olaf';
const SET_A = 'set-a';
const SET_B = 'set-b';
const TOKEN = 'token-1';

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

function setList(activeEmoteSetId: string): EmoteSetListResponse {
  return { activeEmoteSetId, sets: [] };
}

const EDITABLE: EditableSetResolution = {
  status: 'editable',
  target: { ownerTwitchChannelId: 'owner-1' } as never,
};

/** Every read of a start as a queue of answers the case releases by hand. */
class Reads {
  readonly sets: Subject<EmoteSetListResponse>[] = [];
  readonly setRefresh: (boolean | undefined)[] = [];
  readonly stored: Subject<EmoteSetStatus>[] = [];
  readonly prechecks: Subject<EditableSetResolution>[] = [];
  readonly wishes: Subject<WishDetail>[] = [];

  readonly emoteSetService = {
    listForeignChannelEmoteSets: vi.fn(
      (_channel: string, options?: { refresh?: boolean }): Observable<EmoteSetListResponse> => {
        this.setRefresh.push(options?.refresh);
        return push(this.sets);
      },
    ),
    resolveEditableSet: vi.fn((): Observable<EditableSetResolution> => push(this.prechecks)),
  };
  readonly emoteAdminService = {
    getSetStatus: vi.fn((): Observable<EmoteSetStatus> => push(this.stored)),
  };
  readonly wishService = { get: vi.fn((): Observable<WishDetail> => push(this.wishes)) };
}

function push<T>(queue: Subject<T>[]): Observable<T> {
  const subject = new Subject<T>();
  queue.push(subject);
  return subject;
}

function answer<T>(queue: Subject<T>[], value: T): void {
  const subject = queue.shift();
  if (!subject) {
    throw new Error('No read is waiting');
  }
  subject.next(value);
  subject.complete();
}

function fail<T>(queue: Subject<T>[], error: unknown): void {
  const subject = queue.shift();
  if (!subject) {
    throw new Error('No read is waiting');
  }
  subject.error(error);
}

describe('wish-run-start', () => {
  let reads: Reads;
  let arbiter: SevenTvRunArbiter;
  let token: string | null;
  let tokenService: Pick<SevenTvTokenService, 'getToken'>;
  let startCheckPending: WritableSignal<boolean>;
  let alive: boolean;
  let callerChannel: string;
  let hooks: {
    start: ReturnType<typeof vi.fn>;
    aborted: ReturnType<typeof vi.fn>;
    dismissed: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    TestBed.configureTestingModule({});
    arbiter = TestBed.inject(SevenTvRunArbiter);
    reads = new Reads();
    token = TOKEN;
    tokenService = { getToken: () => token };
    startCheckPending = signal(false);
    arbiter.register({
      kind: 'wish',
      isRunning: signal(false),
      isSettling: signal(false),
      destructiveOpen: signal(false),
      startCheckPending,
    });
    alive = true;
    callerChannel = CHANNEL;
    hooks = { start: vi.fn(() => true), aborted: vi.fn(), dismissed: vi.fn() };
  });

  afterEach(() => vi.useRealTimers());

  function deps(): WishRunStartDeps {
    return {
      emoteSetService: reads.emoteSetService as unknown as SevenTvEmoteSetService,
      emoteAdminService: reads.emoteAdminService as unknown as EmoteAdminService,
      wishService: reads.wishService as unknown as WishService,
      tokenService: tokenService as SevenTvTokenService,
      arbiter,
    };
  }

  function request(over: Partial<WishRunStartRequest> = {}): WishRunStartRequest {
    return {
      purpose: 'add',
      ownKind: 'wish',
      channelName: CHANNEL,
      wish: wish(),
      caller: { alive: () => alive, channelName: () => callerChannel },
      startCheckPending,
      promptToken: () => of(true),
      ...over,
    };
  }

  function start(over: Partial<WishRunStartRequest> = {}): void {
    runWishStart(deps(), request(over), hooks as unknown as WishRunStartHooks);
  }

  /** Phase 1 and the pre-check answer as the happy path; leaves the case in 3a. */
  function throughPhaseTwo(): void {
    answer(reads.sets, setList(SET_A));
    answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);
    answer(reads.prechecks, EDITABLE);
  }

  function abortedWith(reason: WishStartAbortReason): void {
    expect(hooks.aborted).toHaveBeenCalledExactlyOnceWith(reason);
    expect(hooks.start).not.toHaveBeenCalled();
    expect(startCheckPending()).toBe(false);
  }

  /** A participant of another kind whose signals a case sets by hand. */
  function otherRun(kind: SevenTvRunKind = 'import') {
    const participant = {
      kind,
      isRunning: signal(false),
      isSettling: signal(false),
      destructiveOpen: signal(false),
      startCheckPending: signal(false),
    };
    arbiter.register(participant);
    return participant;
  }

  describe('the authoritative read of the active set', () => {
    it('reads the set list with refresh at the click and in 3a, never from the cache', () => {
      start();
      throughPhaseTwo();

      answer(reads.sets, setList(SET_A));
      answer(reads.wishes, wish() as WishDetail);

      expect(reads.setRefresh).toEqual([true, true]);
      expect(hooks.start).toHaveBeenCalledOnce();
    });

    it('freezes the set of the authoritative read and sends nothing when the stored one differs', () => {
      start();
      answer(reads.sets, setList(SET_B));
      answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);

      abortedWith('setSwitched');
      expect(reads.prechecks).toHaveLength(0);
    });

    it('treats a throttled read at the click as no start', () => {
      start();
      fail(reads.sets, new HttpErrorResponse({ status: 429 }));
      answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);

      abortedWith('setUnverified');
    });

    it('starts nothing for a channel without an active set', () => {
      start();
      answer(reads.sets, setList(''));
      answer(reads.stored, { activeEmoteSetId: '' } as EmoteSetStatus);

      abortedWith('noActiveSet');
    });

    it('treats a read that never answers as failed once the time budget ran out', () => {
      vi.useFakeTimers();
      start();
      answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);

      vi.advanceTimersByTime(WISH_START_READ_TIMEOUT_MS);

      abortedWith('setUnverified');
    });
  });

  describe('phases 1 and 2', () => {
    it('refuses at once, without a read, while the arbiter locks the start', () => {
      const other = otherRun();
      other.isRunning.set(true);

      start();

      abortedWith('busy');
      expect(reads.sets).toHaveLength(0);
    });

    it('holds startCheckPending from the click until the run started, then releases it', () => {
      start();
      expect(startCheckPending()).toBe(true);
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));
      expect(startCheckPending()).toBe(true);

      hooks.start.mockImplementation(() => {
        expect(startCheckPending()).toBe(true);
        return true;
      });
      answer(reads.wishes, wish() as WishDetail);

      expect(hooks.start).toHaveBeenCalledOnce();
      expect(startCheckPending()).toBe(false);
    });

    it('opens the token prompt without a token and freezes the token it left behind', () => {
      token = null;
      const prompt = new Subject<boolean>();
      start({ promptToken: () => prompt });
      answer(reads.sets, setList(SET_A));
      answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);
      expect(reads.prechecks).toHaveLength(0);

      token = 'token-2';
      prompt.next(true);
      answer(reads.prechecks, EDITABLE);
      answer(reads.sets, setList(SET_A));
      answer(reads.wishes, wish() as WishDetail);

      expect(hooks.start.mock.calls[0][0]).toMatchObject({ token: 'token-2' });
    });

    it('ends without a reason and without a read when the prompt is dismissed', () => {
      token = null;
      start({ promptToken: () => of(false) });
      answer(reads.sets, setList(SET_A));
      answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);

      expect(hooks.dismissed).toHaveBeenCalledOnce();
      expect(hooks.aborted).not.toHaveBeenCalled();
      expect(reads.prechecks).toHaveLength(0);
      expect(startCheckPending()).toBe(false);
    });

    it('stops on the pre-check with its reason, "cannot be checked" included', () => {
      start();
      answer(reads.sets, setList(SET_A));
      answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);
      fail(reads.prechecks, new HttpErrorResponse({ status: 503 }));

      abortedWith('targetCheckUnavailable');
    });

    it('ends without a reason when the confirmation is dismissed, and reads nothing more', () => {
      start({ confirm: () => of(false) });
      throughPhaseTwo();

      expect(hooks.dismissed).toHaveBeenCalledOnce();
      expect(reads.sets).toHaveLength(0);
      expect(reads.wishes).toHaveLength(0);
      expect(startCheckPending()).toBe(false);
    });

    it('gives way to a confirmed run of another kind that started while the pre-check was out', () => {
      const other = otherRun('delete');
      start();
      answer(reads.sets, setList(SET_A));
      answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);

      other.isRunning.set(true);
      answer(reads.prechecks, EDITABLE);
      answer(reads.sets, setList(SET_A));
      answer(reads.wishes, wish() as WishDetail);

      abortedWith('busy');
    });
  });

  describe('3a and 3b', () => {
    it('decides only once both reads ended, in either order', () => {
      start();
      throughPhaseTwo();

      answer(reads.wishes, wish() as WishDetail);
      expect(hooks.start).not.toHaveBeenCalled();
      answer(reads.sets, setList(SET_A));

      expect(hooks.start).toHaveBeenCalledOnce();
    });

    it('aborts after the delayed last read when the page was left meanwhile', () => {
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));

      alive = false;
      answer(reads.wishes, wish() as WishDetail);

      abortedWith('pageLeft');
    });

    it('aborts when the page shows another channel by the time the last read answers', () => {
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));

      callerChannel = 'someone-else';
      answer(reads.wishes, wish() as WishDetail);

      abortedWith('pageLeft');
    });

    it('aborts when a run of another kind started while the last read hung', () => {
      const other = otherRun('restore');
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));

      other.isSettling.set(true);
      answer(reads.wishes, wish() as WishDetail);

      abortedWith('busy');
    });

    it('gives way to the start check of another kind that is making its own last read', () => {
      const other = otherRun('undo');
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));

      other.startCheckPending.set(true);
      answer(reads.wishes, wish() as WishDetail);

      abortedWith('busy');
    });

    it('aborts on a set switch only the refresh read shows', () => {
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_B));
      answer(reads.wishes, wish() as WishDetail);

      abortedWith('setChanged');
    });

    it('aborts when the token was changed while the last reads were out', () => {
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));

      token = 'token-2';
      answer(reads.wishes, wish() as WishDetail);

      abortedWith('tokenChanged');
    });

    it('aborts when the wish was closed meanwhile', () => {
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));
      answer(reads.wishes, wish({ status: 'closed' }) as WishDetail);

      abortedWith('wishChanged');
    });

    it('aborts when the wish was renamed meanwhile', () => {
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));
      answer(reads.wishes, wish({ alias: 'KEKWait' }) as WishDetail);

      abortedWith('wishChanged');
    });

    it('aborts on a failed wish read (fail-closed) and on a wish that is gone', () => {
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));
      fail(reads.wishes, new HttpErrorResponse({ status: 503 }));
      abortedWith('wishUnverified');

      hooks.aborted.mockClear();
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));
      fail(reads.wishes, new HttpErrorResponse({ status: 404 }));
      abortedWith('wishChanged');
    });

    it('aborts when the fresh set read fails', () => {
      start();
      throughPhaseTwo();
      fail(reads.sets, new HttpErrorResponse({ status: 503 }));
      answer(reads.wishes, wish() as WishDetail);

      abortedWith('setUnverified');
    });

    it('starts in the same step with exactly the frozen values', () => {
      start({ wish: wish({ alias: 'KEKW', sevenTvEmoteId: '01EMOTE' }) });
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));
      answer(reads.wishes, wish() as WishDetail);

      expect(hooks.start).toHaveBeenCalledExactlyOnceWith({
        purpose: 'add',
        channelName: CHANNEL,
        wish: {
          id: 7,
          status: 'approved',
          sevenTvEmoteId: '01EMOTE',
          alias: 'KEKW',
          slotId: null,
          slotAddedAtUtc: null,
        },
        activeEmoteSetId: SET_A,
        ownerTwitchChannelId: 'owner-1',
        token: TOKEN,
      } satisfies FrozenWishStart);
      expect(hooks.aborted).not.toHaveBeenCalled();
    });

    it('reports busy when the engine refuses the start it was asked for', () => {
      hooks.start.mockReturnValue(false);
      start();
      throughPhaseTwo();
      answer(reads.sets, setList(SET_A));
      answer(reads.wishes, wish() as WishDetail);

      expect(hooks.aborted).toHaveBeenCalledExactlyOnceWith('busy');
      expect(startCheckPending()).toBe(false);
    });
  });

  describe('decideWishStart for the removal', () => {
    const held = wish({
      status: 'active',
      slotId: 'slot-1',
      slotAddedAtUtc: '2026-11-02T10:00:00.123456Z',
      startsAtAuthoritative: true,
    });

    function decide(fresh: Wish): ReturnType<typeof decideWishStart> {
      const input: WishStartDecisionInput = {
        frozen: {
          purpose: 'remove',
          channelName: CHANNEL,
          wish: held,
          activeEmoteSetId: SET_A,
          ownerTwitchChannelId: null,
          token: TOKEN,
        },
        caller: { alive: () => true, channelName: () => CHANNEL },
        tokenService,
        arbiter,
        ownKind: 'delete',
        setRead: { kind: 'ok', activeEmoteSetId: SET_A },
        wishRead: { kind: 'ok', wish: fresh },
      };
      return decideWishStart(input);
    }

    it('starts for the same slot and generation, presented included', () => {
      expect(decide(held)).toEqual({ kind: 'start' });
      expect(decide({ ...held, status: 'presented' })).toEqual({ kind: 'start' });
    });

    it('aborts for another slot, another generation, an unconfirmed date or another state', () => {
      expect(decide({ ...held, slotId: 'slot-2' })).toEqual({
        kind: 'abort',
        reason: 'wishChanged',
      });
      expect(decide({ ...held, slotAddedAtUtc: '2026-11-03T10:00:00Z' })).toEqual({
        kind: 'abort',
        reason: 'wishChanged',
      });
      expect(decide({ ...held, startsAtAuthoritative: false })).toEqual({
        kind: 'abort',
        reason: 'wishChanged',
      });
      expect(decide({ ...held, status: 'removed' })).toEqual({
        kind: 'abort',
        reason: 'wishChanged',
      });
    });
  });

  describe('verifySetAndToken (before every send)', () => {
    const frozen = { channelName: CHANNEL, activeEmoteSetId: SET_A, token: TOKEN };

    function verify(): (WishStartAbortReason | null)[] {
      const results: (WishStartAbortReason | null)[] = [];
      verifySetAndToken(deps(), frozen).subscribe((result) => results.push(result));
      return results;
    }

    it('sends for the same token and the frozen set, read with refresh', () => {
      const results = verify();
      answer(reads.sets, setList(SET_A));

      expect(results).toEqual([null]);
      expect(reads.setRefresh).toEqual([true]);
    });

    it('skips without a read when the token is gone or another one', () => {
      token = null;
      expect(verify()).toEqual(['tokenChanged']);
      token = 'token-2';
      expect(verify()).toEqual(['tokenChanged']);
      expect(reads.sets).toHaveLength(0);
    });

    it('skips when the token goes while the read is out', () => {
      const results = verify();
      token = null;
      answer(reads.sets, setList(SET_A));

      expect(results).toEqual(['tokenChanged']);
    });

    it('skips for another active set and for a read that fails or is throttled', () => {
      const switched = verify();
      answer(reads.sets, setList(SET_B));
      expect(switched).toEqual(['setChanged']);

      const throttled = verify();
      fail(reads.sets, new HttpErrorResponse({ status: 429 }));
      expect(throttled).toEqual(['setUnverified']);
    });
  });

  it('never errors on an unexpected throw inside a read stub', () => {
    reads.emoteSetService.listForeignChannelEmoteSets.mockImplementationOnce(() =>
      throwError(() => new Error('boom')),
    );
    start();
    answer(reads.stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);

    abortedWith('setUnverified');
  });
});
