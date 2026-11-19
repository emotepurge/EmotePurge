import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, firstValueFrom, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService, EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import {
  DeleteQueueEmote,
  DeleteRunSettlement,
  DeleteStartOptions,
  DeleteStartResult,
} from '../../core/seven-tv/seven-tv-delete.service';
import {
  EditableSetResolution,
  EmoteSetListResponse,
} from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import type { Wish, WishDetail } from '../../core/wishes/wish.model';
import { WishRemovalProgress } from '../../core/wishes/wish-removal';
import { WishService } from '../../core/wishes/wish.service';
import { WishRemovalConfirmData } from './wish-removal-confirm-dialog';
import { WishRemovalFlowDeps, startWishRemovalFlow } from './wish-removal-flow';

const CHANNEL = 'olaf';
const SET_A = 'set-a';
const SET_B = 'set-b';
const TOKEN = 'token-1';
const ADDED = '2026-11-02T10:00:00.123456Z';

function wish(over: Partial<Wish> = {}): Wish {
  return {
    id: 7,
    sevenTvEmoteId: '01EMOTE',
    emoteName: 'KEKW',
    alias: 'kekw2',
    source: 'manual',
    status: 'active',
    statusReason: null,
    requesterLogin: null,
    durationDays: 14,
    createdAtUtc: '2026-11-01T10:00:00Z',
    approvedAtUtc: '2026-11-01T10:00:00Z',
    startsAtUtc: ADDED,
    startsAtAuthoritative: true,
    probationStartsAtUtc: null,
    expiresAtUtc: '2026-11-16T10:00:00Z',
    endedAtUtc: null,
    slotId: 'slot-1',
    slotAddedAtUtc: ADDED,
    imageUrl: null,
    linkOrigin: 'manual',
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

function push<T>(queue: Subject<T>[]): Observable<T> {
  const subject = new Subject<T>();
  queue.push(subject);
  return subject;
}

function answer<T>(queue: Subject<T>[], value: T): void {
  const subject = queue.shift();
  if (!subject) {
    throw new Error('Nothing is waiting');
  }
  subject.next(value);
  subject.complete();
}

const setList = (activeEmoteSetId: string): EmoteSetListResponse => ({
  activeEmoteSetId,
  sets: [],
});

describe('wish-removal-flow', () => {
  let sets: Subject<EmoteSetListResponse>[];
  let setRefresh: (boolean | undefined)[];
  let stored: Subject<EmoteSetStatus>[];
  let prechecks: Subject<EditableSetResolution>[];
  let wishes: Subject<WishDetail>[];
  let warnings: Subject<EmoteSetWarning>[];
  let confirmations: { data: WishRemovalConfirmData; answer: Subject<boolean> }[];
  let token: string | null;
  let arbiter: SevenTvRunArbiter;
  let startCheckPending: ReturnType<typeof signal<boolean>>;
  let startDeleteRun: ReturnType<typeof vi.fn>;
  let claim: { begin: number; end: number; clear: number };
  let progress: { [K in keyof WishRemovalProgress]: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    TestBed.configureTestingModule({});
    arbiter = TestBed.inject(SevenTvRunArbiter);
    sets = [];
    setRefresh = [];
    stored = [];
    prechecks = [];
    wishes = [];
    warnings = [];
    confirmations = [];
    token = TOKEN;
    startCheckPending = signal(false);
    arbiter.register({
      kind: 'delete',
      isRunning: signal(false),
      isSettling: signal(false),
      destructiveOpen: signal(false),
      startCheckPending,
    });
    startDeleteRun = vi.fn((): DeleteStartResult => ({ started: true, runId: 'run-1' }));
    claim = { begin: 0, end: 0, clear: 0 };
    progress = {
      starting: vi.fn(),
      notStarted: vi.fn(),
      dismissed: vi.fn(),
      started: vi.fn(),
      settled: vi.fn(),
    };
  });

  function deps(): WishRemovalFlowDeps {
    return {
      emoteSetService: {
        listForeignChannelEmoteSets: (_channel: string, options?: { refresh?: boolean }) => {
          setRefresh.push(options?.refresh);
          return push(sets);
        },
        resolveEditableSet: () => push(prechecks),
      } as unknown as SevenTvEmoteSetService,
      emoteAdminService: {
        getSetStatus: () => push(stored),
        getSetWarning: () => push(warnings),
      } as unknown as EmoteAdminService,
      wishService: { get: () => push(wishes) } as unknown as WishService,
      tokenService: { getToken: () => token } as SevenTvTokenService,
      arbiter,
      deleteService: {
        startDeleteRun,
        startCheckPending,
        beginConfirmedRun: () => claim.begin++,
        endConfirmedRun: () => claim.end++,
        clearConfirmedRun: () => claim.clear++,
      } as unknown as WishRemovalFlowDeps['deleteService'],
      openConfirmation: (data) => {
        const confirmed = new Subject<boolean>();
        confirmations.push({ data, answer: confirmed });
        return confirmed;
      },
    };
  }

  function start(target: Wish = wish()): void {
    startWishRemovalFlow(deps(), {
      channelName: CHANNEL,
      wish: target,
      caller: { alive: () => true, channelName: () => CHANNEL },
      promptToken: () => of(true),
      progress: progress as unknown as WishRemovalProgress,
    });
  }

  /** Click read, stored set, pre-check (with a set name), then the confirmation opens. */
  function toConfirmation(setName?: string): void {
    answer(sets, setList(SET_A));
    answer(stored, { activeEmoteSetId: SET_A } as EmoteSetStatus);
    answer(prechecks, {
      status: 'editable',
      target: { ownerTwitchChannelId: 'owner-1', ...(setName ? { setName } : {}) } as never,
    });
  }

  function confirm(): void {
    answer(
      confirmations.map((entry) => entry.answer),
      true,
    );
    confirmations.shift();
  }

  /** The row and options the run was started with. */
  function startedWith(): { setId: string; rows: DeleteQueueEmote[]; options: DeleteStartOptions } {
    expect(startDeleteRun).toHaveBeenCalledOnce();
    const [setId, , rows, , , , options] = startDeleteRun.mock.calls[0];
    return { setId, rows, options };
  }

  it('removes exactly the frozen entry: slot, alias, the date verbatim, the frozen set, bound to the wish', () => {
    start();
    toConfirmation('Herbst');
    confirm();
    answer(sets, setList(SET_A));
    answer(wishes, wish() as WishDetail);

    const { setId, rows } = startedWith();
    expect(setId).toBe(SET_A);
    expect(startDeleteRun.mock.calls[0]).toEqual([
      SET_A,
      CHANNEL,
      expect.any(Array),
      CHANNEL,
      'owner-1',
      undefined,
      expect.any(Object),
    ]);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      sevenTvEmoteId: '01EMOTE',
      name: 'kekw2',
      entry: { slotId: 'slot-1', alias: 'kekw2', addedAt: ADDED, cardKey: 'wish-7' },
      wishBinding: { activeEmoteSetId: SET_A, token: TOKEN },
    });
    expect(progress.started).toHaveBeenCalledExactlyOnceWith('run-1', {
      channelName: CHANNEL,
      wishId: 7,
      slotId: 'slot-1',
      slotAddedAtUtc: ADDED,
      emoteSetId: SET_A,
    });
    expect(claim).toEqual({ begin: 1, end: 1, clear: 0 });
    expect(startCheckPending()).toBe(false);
  });

  it('builds the row from the wish frozen at the click, not from the fresh read', () => {
    start();
    toConfirmation();
    confirm();
    answer(sets, setList(SET_A));
    // Same slot and generation, so the start goes ahead; its other fields are never read.
    answer(wishes, wish({ alias: 'renamed', emoteName: 'Other' }) as WishDetail);

    expect(startedWith().rows[0]).toMatchObject({
      name: 'kekw2',
      entry: { alias: 'kekw2', addedAt: ADDED },
    });
  });

  it('names alias, the date of the generation and the set in the confirmation, the set id without a name', () => {
    start();
    toConfirmation('Herbst');
    expect(confirmations[0].data).toMatchObject({
      alias: 'kekw2',
      addedAtUtc: ADDED,
      setName: 'Herbst',
    });
    expect(confirmations[0].data.warningLoading()).toBe(true);
    answer(warnings, {
      available: true,
      isOwnSet: false,
      otherTrackedChannelsSharingSet: [],
      otherModeratedChannelsSharingSet: [],
    });
    expect(confirmations[0].data.warningLoading()).toBe(false);
    expect(confirmations[0].data.warning()?.isOwnSet).toBe(false);
    answer(
      confirmations.map((entry) => entry.answer),
      false,
    );

    start();
    toConfirmation();
    expect(confirmations[1].data.setName).toBe(SET_A);
  });

  it('starts nothing for a dismissed confirmation and frees the dock claim and the start check', () => {
    start();
    toConfirmation();
    expect(startCheckPending()).toBe(true);
    expect(claim.begin).toBe(1);
    answer(
      confirmations.map((entry) => entry.answer),
      false,
    );

    expect(startDeleteRun).not.toHaveBeenCalled();
    expect(progress.dismissed).toHaveBeenCalledOnce();
    expect(claim).toEqual({ begin: 1, end: 0, clear: 1 });
    expect(startCheckPending()).toBe(false);
    expect(sets).toHaveLength(0);
  });

  it.each([
    ['before', 'wish first'],
    ['after', 'set first'],
  ])(
    'starts nothing when the fresh wish shows a newer generation of the same slot and alias (answer %s the set)',
    (_label, order) => {
      start();
      toConfirmation();
      confirm();
      const newer = wish({ slotAddedAtUtc: '2026-11-03T08:00:00Z' }) as WishDetail;
      if (order === 'wish first') {
        answer(wishes, newer);
        answer(sets, setList(SET_A));
      } else {
        answer(sets, setList(SET_A));
        answer(wishes, newer);
      }

      expect(startDeleteRun).not.toHaveBeenCalled();
      expect(progress.notStarted).toHaveBeenCalledExactlyOnceWith('wishChanged');
      expect(claim.end).toBe(1);
      expect(startCheckPending()).toBe(false);
    },
  );

  it('says why when the delete run refuses to start', () => {
    startDeleteRun.mockReturnValue({ started: false });
    start();
    toConfirmation();
    confirm();
    answer(sets, setList(SET_A));
    answer(wishes, wish() as WishDetail);

    expect(progress.started).not.toHaveBeenCalled();
    expect(progress.notStarted).toHaveBeenCalledExactlyOnceWith('busy');
    expect(startCheckPending()).toBe(false);
  });

  it.each([
    ['no slot', { slotId: null }],
    ['no generation', { slotAddedAtUtc: null }],
    ['a provisional date', { startsAtAuthoritative: false }],
    ['an unusable date', { slotAddedAtUtc: 'yesterday' }],
  ])('starts nothing at all for a wish with %s', (_label, change) => {
    start(wish(change));

    expect(progress.starting).not.toHaveBeenCalled();
    expect(sets).toHaveLength(0);
    expect(startCheckPending()).toBe(false);
  });

  it('hands the run’s completion report on together with the frozen target', () => {
    start();
    toConfirmation();
    confirm();
    answer(sets, setList(SET_A));
    answer(wishes, wish() as WishDetail);
    const settlement = { runId: 'run-1' } as DeleteRunSettlement;

    startedWith().options.onSettled?.(settlement);

    expect(progress.settled).toHaveBeenCalledExactlyOnceWith(
      expect.objectContaining({ wishId: 7, slotAddedAtUtc: ADDED, emoteSetId: SET_A }),
      settlement,
    );
  });

  describe('the binding’s check of set and token', () => {
    async function checkOfStartedRow(): Promise<() => Promise<string | null>> {
      start();
      toConfirmation();
      confirm();
      answer(sets, setList(SET_A));
      answer(wishes, wish() as WishDetail);
      const binding = startedWith().rows[0].wishBinding!;
      return () => firstValueFrom(binding.verifySetAndToken());
    }

    it('reads the active set past the cache and passes when it is still the frozen one', async () => {
      const check = await checkOfStartedRow();
      setRefresh.length = 0;
      const verdict = check();
      answer(sets, setList(SET_A));

      expect(await verdict).toBeNull();
      expect(setRefresh).toEqual([true]);
    });

    it('reports another active set, a throttled read and a gone token', async () => {
      const check = await checkOfStartedRow();

      const switched = check();
      answer(sets, setList(SET_B));
      expect(await switched).toBe('setChanged');

      const throttled = check();
      const pending = sets.shift()!;
      pending.error(new HttpErrorResponse({ status: 429 }));
      expect(await throttled).toBe('setUnverified');

      token = null;
      expect(await check()).toBe('tokenChanged');
    });
  });
});
