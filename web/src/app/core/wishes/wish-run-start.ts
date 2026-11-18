import { WritableSignal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Observable,
  Subscription,
  catchError,
  concatMap,
  defaultIfEmpty,
  defer,
  forkJoin,
  map,
  of,
  take,
  timeout,
} from 'rxjs';

import { EmoteAdminService } from '../emotes/emote-admin.service';
import { SevenTvEmoteSetService } from '../seven-tv/seven-tv-emote-set.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../seven-tv/seven-tv-token.service';
import type { Wish } from './wish.model';
import { WishService } from './wish.service';

/*
 * The start contract of every 7TV run from a wish row (#485, plan 2.5; Sol-P2-3, Sol-R2-P2-3,
 * Sol-R3), shared by the add (T8, `WishAddService`) and the removal (T9). It knows no page and no
 * dialog: everything it needs is handed in (the `DeleteFlowDeps` pattern), the token prompt and an
 * optional confirmation included.
 *
 * Phases: (1) freeze at the click: the channel, the wish, and the channel's active set from the
 * authoritative read, which must agree with the set the last sync stored; (2) prepare: token (or the
 * prompt), the shared pre-check, the caller's confirmation; (3a) read the active set and the wish
 * again, both reads end before anything is decided; (3b) decide and take over in one synchronous
 * step (`decideWishStart`), whose caller starts the run in the same step. `startCheckPending` stands
 * from the click to the last exit, so every other 7TV start trigger of the app is locked meanwhile.
 *
 * The authoritative read of the active set is `GET /api/seventv/channels/{c}/emote-sets` with
 * `refresh=true` (T0a): 7TV's own `style.activeEmoteSetId`, past the Api's 60 s cache — never
 * `…/emotes/active-set`, which is the set the last Postgres sync stored. The route counts against
 * the `ForeignEmoteLookup` budget (10 per user and minute, shared with the foreign set preview and
 * the target picker; plan R17): a start costs two reads (click and 3a), each retry one more. A
 * throttled or failed read is "not shown", and nothing is started or sent.
 */

/** Time budget of every read of the start and of the check before a send. A hung request is a
 *  failed read (fail-closed), the same 20 s every other pre-run read of the 7TV runs has. */
export const WISH_START_READ_TIMEOUT_MS = 20_000;

/** Why a wish run did not start, or why a send was skipped; each one is a sentence of its own. */
export type WishStartAbortReason =
  | 'pageLeft'
  | 'busy'
  | 'tokenChanged'
  | 'setChanged'
  | 'setSwitched'
  | 'setUnverified'
  | 'noActiveSet'
  | 'wishChanged'
  | 'wishUnverified'
  | 'targetNotEditable'
  | 'targetNotSelectable'
  | 'targetCheckUnavailable';

/** The sentence of each reason. Every one of them stands behind `WISH_START_NOTHING_CHANGED_KEY`. */
export const WISH_START_ABORT_KEY: Record<WishStartAbortReason, string> = {
  pageLeft: 'wishes.start.pageLeft',
  busy: 'wishes.start.busy',
  tokenChanged: 'wishes.start.tokenChanged',
  setChanged: 'wishes.start.setChanged',
  setSwitched: 'wishes.start.setSwitched',
  setUnverified: 'wishes.start.setUnverified',
  noActiveSet: 'wishes.start.noActiveSet',
  wishChanged: 'wishes.start.wishChanged',
  wishUnverified: 'wishes.start.wishUnverified',
  targetNotEditable: 'massDelete.errors.targetNotEditable',
  targetNotSelectable: 'massDelete.errors.targetNotSelectable',
  targetCheckUnavailable: 'massDelete.errors.targetCheckUnavailable',
};

/** "Es wurde nichts geändert." — the lead of every abort and skip. */
export const WISH_START_NOTHING_CHANGED_KEY = 'wishes.start.nothingChanged';

/** Which run the start is for; it decides what "the wish is unchanged" means (step 3b v). */
export type WishStartPurpose = 'add' | 'remove';

/** The authoritative read of the active set: an id, no active set at all, or not shown. */
export type ActiveSetRead =
  { kind: 'ok'; activeEmoteSetId: string } | { kind: 'none' } | { kind: 'unavailable' };

/** The fresh read of the wish: the wish, gone (404), or not shown. */
export type WishRead = { kind: 'ok'; wish: Wish } | { kind: 'gone' } | { kind: 'unavailable' };

/** The fields of the wish the start freezes at the click; the write targets only these. */
export type FrozenWish = Pick<
  Wish,
  'id' | 'status' | 'sevenTvEmoteId' | 'alias' | 'slotId' | 'slotAddedAtUtc'
>;

/** Everything a started run works with, frozen at the click and after the token step. */
export interface FrozenWishStart {
  readonly purpose: WishStartPurpose;
  readonly channelName: string;
  readonly wish: FrozenWish;
  /** The active set from the authoritative read at the click. */
  readonly activeEmoteSetId: string;
  /** That set's name as the shared pre-check listed it; absent when it named none. Only for what a
   *  confirmation shows (T9), never compared. */
  readonly activeEmoteSetName?: string;
  /** The owner the shared pre-check resolved (owner-hint design 3.6), for a run that reports it. */
  readonly ownerTwitchChannelId: string | null;
  /** The 7TV token the start was confirmed with. Kept in memory only, never logged. */
  readonly token: string;
}

/** The page that started the run: whether it still lives and which channel it shows. */
export interface WishStartCaller {
  alive(): boolean;
  channelName(): string;
}

export interface WishRunStartDeps {
  emoteSetService: SevenTvEmoteSetService;
  /** Only for the stored active set (`getSetStatus`), compared at the click. */
  emoteAdminService: EmoteAdminService;
  wishService: WishService;
  tokenService: SevenTvTokenService;
  arbiter: SevenTvRunArbiter;
}

export interface WishRunStartRequest {
  purpose: WishStartPurpose;
  /** The run kind the start becomes; another kind's start check makes it give way (3b iii). */
  ownKind: SevenTvRunKind;
  /** The page's channel, already normalized. */
  channelName: string;
  wish: Wish;
  caller: WishStartCaller;
  /** The run service's own start-check flag, registered with the arbiter. */
  startCheckPending: WritableSignal<boolean>;
  /** Opens the token prompt; `true` once a token is there. */
  promptToken(): Observable<boolean>;
  /** The caller's confirmation (the removal's dialog); `false` ends the start without a reason. */
  confirm?(frozen: FrozenWishStart): Observable<boolean>;
}

export interface WishRunStartHooks {
  /** Called inside step 3b, synchronously: start the run now. `false` if the engine refused. */
  start(frozen: FrozenWishStart): boolean;
  /** The start ended without a write, with its reason. */
  aborted(reason: WishStartAbortReason): void;
  /** The user dismissed the token prompt or the confirmation; nothing to say. */
  dismissed(): void;
}

/** Everything step 3b decides on; the two reads have already ended. */
export interface WishStartDecisionInput {
  frozen: FrozenWishStart;
  caller: WishStartCaller;
  tokenService: Pick<SevenTvTokenService, 'getToken'>;
  arbiter: Pick<SevenTvRunArbiter, 'activeClaim' | 'foreignStartPending'>;
  ownKind: SevenTvRunKind;
  setRead: ActiveSetRead;
  wishRead: WishRead;
}

export type WishStartDecision = { kind: 'start' } | { kind: 'abort'; reason: WishStartAbortReason };

/**
 * Step 3b: one synchronous decision over reads that have already ended, in the order of plan 2.5 —
 * (i) the caller lives and shows the same channel; (ii) the token is still the confirmed one; (iii)
 * the arbiter is free: no claim, and no start check of another kind; (iv) the read active set is
 * the frozen one; (v) the read wish is in the allowed state and unchanged in the frozen fields. The
 * first deviation is the answer. The caller starts the run in the same step on `start`, with no
 * `await`, timer or subscription in between.
 */
export function decideWishStart(input: WishStartDecisionInput): WishStartDecision {
  const { frozen } = input;
  if (!input.caller.alive() || input.caller.channelName() !== frozen.channelName) {
    return abort('pageLeft');
  }
  if (input.tokenService.getToken() !== frozen.token) {
    return abort('tokenChanged');
  }
  if (input.arbiter.activeClaim() !== null || input.arbiter.foreignStartPending(input.ownKind)) {
    return abort('busy');
  }
  const setReason = activeSetDeviation(input.setRead, frozen.activeEmoteSetId);
  if (setReason !== null) {
    return abort(setReason);
  }
  const wishReason = wishDeviation(frozen.purpose, frozen.wish, input.wishRead);
  if (wishReason !== null) {
    return abort(wishReason);
  }
  return { kind: 'start' };
}

/** Whether a read of the active set still shows the frozen one: `null`, or why not. */
export function activeSetDeviation(
  read: ActiveSetRead,
  frozenSetId: string,
): WishStartAbortReason | null {
  switch (read.kind) {
    case 'ok':
      return read.activeEmoteSetId === frozenSetId ? null : 'setChanged';
    case 'none':
      return 'setChanged';
    default:
      return 'setUnverified';
  }
}

/**
 * Whether the fresh wish still allows the run: `null`, or why not. The add needs `approved` with the
 * same emote and alias; the removal `active` or `presented` with the same slot and the same
 * generation (the date compared as the string it came as), confirmed by 7TV.
 */
export function wishDeviation(
  purpose: WishStartPurpose,
  frozen: FrozenWish,
  read: WishRead,
): WishStartAbortReason | null {
  if (read.kind === 'gone') {
    return 'wishChanged';
  }
  if (read.kind === 'unavailable') {
    return 'wishUnverified';
  }
  const fresh = read.wish;
  if (fresh.id !== frozen.id || fresh.sevenTvEmoteId !== frozen.sevenTvEmoteId) {
    return 'wishChanged';
  }
  if (purpose === 'add') {
    return fresh.status === 'approved' && fresh.alias === frozen.alias ? null : 'wishChanged';
  }
  const stillHeld =
    (fresh.status === 'active' || fresh.status === 'presented') &&
    fresh.slotId !== null &&
    fresh.slotId === frozen.slotId &&
    fresh.slotAddedAtUtc !== null &&
    fresh.slotAddedAtUtc === frozen.slotAddedAtUtc &&
    fresh.startsAtAuthoritative;
  return stillHeld ? null : 'wishChanged';
}

/** The authoritative read of the channel's active set (always with `refresh`); never errors. */
export function readActiveSet(
  emoteSetService: Pick<SevenTvEmoteSetService, 'listForeignChannelEmoteSets'>,
  channelName: string,
): Observable<ActiveSetRead> {
  return defer(() =>
    emoteSetService.listForeignChannelEmoteSets(channelName, { refresh: true }),
  ).pipe(
    take(1),
    timeout(WISH_START_READ_TIMEOUT_MS),
    map((list): ActiveSetRead =>
      list.activeEmoteSetId
        ? { kind: 'ok', activeEmoteSetId: list.activeEmoteSetId }
        : { kind: 'none' },
    ),
    defaultIfEmpty<ActiveSetRead, ActiveSetRead>({ kind: 'unavailable' }),
    catchError(() => of<ActiveSetRead>({ kind: 'unavailable' })),
  );
}

/** The fresh read of the wish (`GET /{id}`); never errors. */
export function readWish(
  wishService: Pick<WishService, 'get'>,
  channelName: string,
  wishId: number,
): Observable<WishRead> {
  return defer(() => wishService.get(channelName, wishId)).pipe(
    take(1),
    timeout(WISH_START_READ_TIMEOUT_MS),
    map((wish): WishRead => ({ kind: 'ok', wish })),
    defaultIfEmpty<WishRead, WishRead>({ kind: 'unavailable' }),
    catchError((error: unknown) =>
      of<WishRead>(
        error instanceof HttpErrorResponse && error.status === 404
          ? { kind: 'gone' }
          : { kind: 'unavailable' },
      ),
    ),
  );
}

/**
 * The check before every send (plan 2.5, table "vor jedem Senden"), in the part the add and the
 * removal share: the token is still the confirmed one, and the authoritative read (with `refresh`)
 * still shows the frozen active set. The token is checked before the read (a gone token costs no
 * read) and again after it, since the read takes time. `null` means "send"; never errors.
 */
export function verifySetAndToken(
  deps: Pick<WishRunStartDeps, 'emoteSetService' | 'tokenService'>,
  frozen: Pick<FrozenWishStart, 'channelName' | 'activeEmoteSetId' | 'token'>,
): Observable<WishStartAbortReason | null> {
  return defer(() => {
    if (deps.tokenService.getToken() !== frozen.token) {
      return of<WishStartAbortReason | null>('tokenChanged');
    }
    return readActiveSet(deps.emoteSetService, frozen.channelName).pipe(
      map((read): WishStartAbortReason | null => {
        if (deps.tokenService.getToken() !== frozen.token) {
          return 'tokenChanged';
        }
        return activeSetDeviation(read, frozen.activeEmoteSetId);
      }),
    );
  });
}

/**
 * Runs phases 1 to 3 for one click and, if nothing deviates, starts the run through `hooks.start`
 * inside step 3b. Refuses at once (no read) while the arbiter locks the start. Every exit releases
 * `startCheckPending`: a start leaves it only after the run holds the arbiter, so the lock never has
 * a gap. The pipeline is deliberately not bound to the caller's lifetime: a caller that is gone by
 * step 3b is an abort with its reason (`pageLeft`), not a silent drop.
 */
export function runWishStart(
  deps: WishRunStartDeps,
  request: WishRunStartRequest,
  hooks: WishRunStartHooks,
): Subscription {
  if (deps.arbiter.startLocked()) {
    hooks.aborted('busy');
    return Subscription.EMPTY;
  }
  const { channelName, wish } = request;
  const frozenWish: FrozenWish = {
    id: wish.id,
    status: wish.status,
    sevenTvEmoteId: wish.sevenTvEmoteId,
    alias: wish.alias,
    slotId: wish.slotId,
    slotAddedAtUtc: wish.slotAddedAtUtc,
  };
  request.startCheckPending.set(true);
  let settled = false;
  const finish = (exit: () => void) => {
    if (settled) {
      return;
    }
    settled = true;
    request.startCheckPending.set(false);
    exit();
  };

  type Step = { kind: 'abort'; reason: WishStartAbortReason } | { kind: 'dismissed' };
  const stop = (step: Step) =>
    finish(() => (step.kind === 'abort' ? hooks.aborted(step.reason) : hooks.dismissed()));

  return freezeActiveSet(deps, channelName)
    .pipe(
      concatMap((freeze) => {
        if (freeze.kind === 'abort') {
          return of<Step | FrozenWishStart>(freeze);
        }
        return ensureToken(deps, request).pipe(
          concatMap((token) => {
            if (token === null) {
              return of<Step | FrozenWishStart>({ kind: 'dismissed' });
            }
            return checkEditable(deps, freeze.activeEmoteSetId, channelName).pipe(
              map((check): Step | FrozenWishStart =>
                check.kind === 'abort'
                  ? check
                  : {
                      purpose: request.purpose,
                      channelName,
                      wish: frozenWish,
                      activeEmoteSetId: freeze.activeEmoteSetId,
                      ...(check.setName ? { activeEmoteSetName: check.setName } : {}),
                      ownerTwitchChannelId: check.ownerTwitchChannelId,
                      token,
                    },
              ),
            );
          }),
        );
      }),
      concatMap((prepared) => {
        if ('kind' in prepared) {
          return of<Step | FrozenWishStart>(prepared);
        }
        const confirmed$ = request.confirm ? request.confirm(prepared) : of(true);
        return confirmed$.pipe(
          take(1),
          defaultIfEmpty(false),
          catchError(() => of(false)),
          map((confirmed): Step | FrozenWishStart =>
            confirmed ? prepared : { kind: 'dismissed' },
          ),
        );
      }),
      concatMap((prepared): Observable<Step | FinalReads> => {
        if ('kind' in prepared) {
          return of(prepared);
        }
        // 3a: both reads run and end before anything is decided.
        return forkJoin({
          setRead: readActiveSet(deps.emoteSetService, channelName),
          wishRead: readWish(deps.wishService, channelName, wish.id),
        }).pipe(map((reads): FinalReads => ({ frozen: prepared, ...reads })));
      }),
    )
    .subscribe({
      next: (outcome) => {
        if ('kind' in outcome) {
          stop(outcome);
          return;
        }
        const { frozen: prepared, setRead, wishRead } = outcome;
        // 3b: one synchronous step from here to the run holding the arbiter.
        const decision = decideWishStart({
          frozen: prepared,
          caller: request.caller,
          tokenService: deps.tokenService,
          arbiter: deps.arbiter,
          ownKind: request.ownKind,
          setRead,
          wishRead,
        });
        if (decision.kind === 'abort') {
          stop(decision);
          return;
        }
        const started = hooks.start(prepared);
        finish(() => {
          if (!started) {
            hooks.aborted('busy');
          }
        });
      },
      error: () => stop({ kind: 'abort', reason: 'setUnverified' }),
      complete: () => stop({ kind: 'abort', reason: 'setUnverified' }),
    });
}

/** What phase 3a hands step 3b: the frozen start and the two reads, both ended. */
interface FinalReads {
  frozen: FrozenWishStart;
  setRead: ActiveSetRead;
  wishRead: WishRead;
}

type FreezeResult =
  { kind: 'ok'; activeEmoteSetId: string } | { kind: 'abort'; reason: WishStartAbortReason };

/**
 * Phase 1: the authoritative active set, which has to agree with the one the last sync stored. A
 * different one means 7TV switched sets and the sync is still to come: the wishes' slots then do not
 * belong to the active set, so nothing starts until that sync ran.
 */
function freezeActiveSet(deps: WishRunStartDeps, channelName: string): Observable<FreezeResult> {
  return forkJoin({
    authoritative: readActiveSet(deps.emoteSetService, channelName),
    stored: readStoredActiveSet(deps.emoteAdminService, channelName),
  }).pipe(
    map(({ authoritative, stored }): FreezeResult => {
      if (authoritative.kind === 'unavailable' || stored === undefined) {
        return { kind: 'abort', reason: 'setUnverified' };
      }
      if (authoritative.kind === 'none') {
        return { kind: 'abort', reason: 'noActiveSet' };
      }
      if (stored !== authoritative.activeEmoteSetId) {
        return { kind: 'abort', reason: 'setSwitched' };
      }
      return { kind: 'ok', activeEmoteSetId: authoritative.activeEmoteSetId };
    }),
  );
}

/** The set the last sync stored (`…/emotes/active-set`); `null` for none, `undefined` if not read. */
function readStoredActiveSet(
  emoteAdminService: Pick<EmoteAdminService, 'getSetStatus'>,
  channelName: string,
): Observable<string | null | undefined> {
  return defer(() => emoteAdminService.getSetStatus(channelName)).pipe(
    take(1),
    timeout(WISH_START_READ_TIMEOUT_MS),
    map((status): string | null => status.activeEmoteSetId || null),
    defaultIfEmpty<string | null | undefined, undefined>(undefined),
    catchError(() => of(undefined)),
  );
}

/** Phase 2, token: the stored one, or the prompt's; `null` when the user left without one. */
function ensureToken(
  deps: WishRunStartDeps,
  request: WishRunStartRequest,
): Observable<string | null> {
  const stored = deps.tokenService.getToken();
  if (stored !== null) {
    return of(stored);
  }
  return request.promptToken().pipe(
    take(1),
    defaultIfEmpty(false),
    catchError(() => of(false)),
    map(() => deps.tokenService.getToken()),
  );
}

type EditableCheck =
  | { kind: 'ok'; ownerTwitchChannelId: string | null; setName: string | undefined }
  | { kind: 'abort'; reason: WishStartAbortReason };

/** Phase 2, the shared pre-check: "cannot be checked" is not "not allowed", but both stop here. */
function checkEditable(
  deps: WishRunStartDeps,
  setId: string,
  channelName: string,
): Observable<EditableCheck> {
  return defer(() =>
    deps.emoteSetService.resolveEditableSet(setId, {
      twitchChannelId: null,
      twitchLogin: channelName,
    }),
  ).pipe(
    take(1),
    timeout(WISH_START_READ_TIMEOUT_MS),
    map((resolution): EditableCheck => {
      switch (resolution.status) {
        case 'editable':
          return {
            kind: 'ok',
            ownerTwitchChannelId: resolution.target.ownerTwitchChannelId,
            setName: resolution.target.setName,
          };
        case 'notEditable':
          return { kind: 'abort', reason: 'targetNotEditable' };
        case 'notSelectable':
          return { kind: 'abort', reason: 'targetNotSelectable' };
        default:
          return { kind: 'abort', reason: 'targetCheckUnavailable' };
      }
    }),
    defaultIfEmpty<EditableCheck, EditableCheck>({
      kind: 'abort',
      reason: 'targetCheckUnavailable',
    }),
    catchError(() => of<EditableCheck>({ kind: 'abort', reason: 'targetCheckUnavailable' })),
  );
}

function abort(reason: WishStartAbortReason): WishStartDecision {
  return { kind: 'abort', reason };
}
