import { HttpClient } from '@angular/common/http';
import { inject, Service, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import { concatMap, map, Observable, of } from 'rxjs';

import { EmoteAdminService } from '../emotes/emote-admin.service';
import { ADD_EMOTE_MUTATION } from '../seven-tv/seven-tv-add-emote';
import { SevenTvEmoteSetService } from '../seven-tv/seven-tv-emote-set.service';
import { SevenTvRunArbiter } from '../seven-tv/seven-tv-run-arbiter';
import {
  RunFailure,
  RunOperation,
  RunResult,
  SevenTvRunEngine,
  StepGate,
} from '../seven-tv/seven-tv-run-engine';
import { SevenTvTokenService } from '../seven-tv/seven-tv-token.service';
import type { Wish } from './wish.model';
import {
  FrozenWishStart,
  readWish,
  runWishStart,
  verifySetAndToken,
  WISH_START_ABORT_KEY,
  WishRunStartDeps,
  WishStartAbortReason,
  WishStartCaller,
  wishDeviation,
} from './wish-run-start';
import { WishService } from './wish.service';

/** `extensions.status` of 7TV's GraphQL rejection for a name already taken in the set. */
const NAME_TAKEN_GQL_STATUS = 409;

/** Where one wish add stands, per wish (plan 2.5a). */
export type WishAddState =
  /** Phases 1 to 3 of the start contract run (click to start). */
  | { kind: 'starting' }
  /** The ADD is out, or the engine waits out 7TV's rate limit (`pauseSeconds`). */
  | { kind: 'running'; pauseSeconds: number | null }
  /** 7TV confirmed it; the next sync takes the wish over. Nothing is reported to the Api. */
  | { kind: 'added' }
  /** 7TV refused it: a taken name, a token 7TV did not accept, or the engine's own text. */
  | { kind: 'rejected'; reason: 'nameTaken' | 'tokenInvalid' | 'other'; message: string }
  /** The answer got lost; the next sync shows whether the emote is in the set. No retry. */
  | { kind: 'unknown' }
  /** The start contract stopped before any write. */
  | { kind: 'notStarted'; reason: WishStartAbortReason }
  /** The check before a send stopped the run; no (further) write went out. */
  | { kind: 'skipped'; reason: WishStartAbortReason };

export interface WishAddRequest {
  /** The page's channel, already normalized. */
  channelName: string;
  wish: Wish;
  caller: WishStartCaller;
  /** Opens the token prompt; `true` once a token is there. */
  promptToken(): Observable<boolean>;
  /** Called once, when the add reached its end state (anything but `starting`/`running`). */
  onEnd?(state: WishAddState): void;
}

/**
 * "In 7TV hinzufügen" from an `approved` wish row (#485, plan 2.5a): a run of exactly one row on an
 * engine of its own, registered with the arbiter as the run kind `wish`. The start follows the start
 * contract (`runWishStart`) without a confirmation dialog — the add destroys nothing, and the wish is
 * the approval. The row's result lives here, per channel and wish, and survives navigation.
 *
 * **Before every send** (plan 2.5, table "vor jedem Senden"; the engine's `beforeStep` hook): the
 * token is the one the start was confirmed with, the authoritative read (with `refresh`) still shows
 * the frozen set, and `GET /{id}` still shows the wish `approved` with the same emote and alias. The
 * first send follows step 3b in the same synchronous step, so it rests on the 3a reads that step
 * decided on (no time lies between them) and checks only the token again; every retry after a
 * 429 reads both afresh. Any deviation, a failed or throttled read, a gone or other token: the row is
 * skipped and nothing (more) is sent.
 *
 * After a confirmed ADD nothing goes to the Api (spec K3): the sync sees the entry and links the wish.
 */
@Service()
export class WishAddService {
  private readonly http = inject(HttpClient);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly translocoService = inject(TranslocoService);
  private readonly arbiter = inject(SevenTvRunArbiter);
  private readonly startDeps: WishRunStartDeps = {
    emoteSetService: inject(SevenTvEmoteSetService),
    emoteAdminService: inject(EmoteAdminService),
    wishService: inject(WishService),
    tokenService: this.tokenService,
    arbiter: this.arbiter,
  };

  private readonly engine = new SevenTvRunEngine(
    this.http,
    this.tokenService,
    this.translocoService,
  );

  /** Phases 1 to 3 of a start are out (plan 2.5); registered with the arbiter. */
  readonly startCheckPending = signal(false);

  readonly isRunning = this.engine.isRunning;

  private readonly states = signal<ReadonlyMap<string, WishAddState>>(new Map());

  /** The wish whose run the engine works right now, for its pause countdown. */
  private readonly runningKey = signal<string | null>(null);

  constructor() {
    this.arbiter.register({
      kind: 'wish',
      isRunning: this.engine.isRunning,
      isSettling: signal(false).asReadonly(),
      destructiveOpen: signal(false).asReadonly(),
      startCheckPending: this.startCheckPending,
    });
  }

  /**
   * The add state of this wish, `null` when there is none to show. A state only holds while the wish
   * is `approved`: once the sync linked it (or anyone changed it), "added" has said what it had to.
   */
  stateFor(channelName: string, wish: Pick<Wish, 'id' | 'status'>): WishAddState | null {
    if (wish.status !== 'approved') {
      return null;
    }
    const key = stateKey(channelName, wish.id);
    const state = this.states().get(key) ?? null;
    if (state?.kind === 'running' && this.runningKey() === key) {
      return { kind: 'running', pauseSeconds: this.engine.rateLimitPauseSeconds() };
    }
    return state;
  }

  /** Drops the finished states of the given wishes that are no longer `approved`, so one that
   *  returns to `approved` later does not show an old result. A start or run in progress stays. */
  prune(channelName: string, wishes: readonly Pick<Wish, 'id' | 'status'>[]): void {
    const current = this.states();
    const drop = wishes
      .filter((wish) => wish.status !== 'approved')
      .map((wish) => stateKey(channelName, wish.id))
      .filter((key) => {
        const state = current.get(key);
        return state !== undefined && state.kind !== 'starting' && state.kind !== 'running';
      });
    if (drop.length === 0) {
      return;
    }
    const next = new Map(current);
    drop.forEach((key) => next.delete(key));
    this.states.set(next);
  }

  /** Starts the add of `request.wish` along the start contract. Every outcome lands in `stateFor`. */
  start(request: WishAddRequest): void {
    const key = stateKey(request.channelName, request.wish.id);
    const end = (state: WishAddState) => {
      this.setState(key, state);
      request.onEnd?.(state);
    };
    this.setState(key, { kind: 'starting' });
    runWishStart(
      this.startDeps,
      {
        purpose: 'add',
        ownKind: 'wish',
        channelName: request.channelName,
        wish: request.wish,
        caller: request.caller,
        startCheckPending: this.startCheckPending,
        promptToken: () => request.promptToken(),
      },
      {
        start: (frozen) => this.startRun(key, frozen, end),
        aborted: (reason) => end({ kind: 'notStarted', reason }),
        // A dismissed prompt is no outcome: the row shows nothing, as before the click.
        dismissed: () => this.setState(key, null),
      },
    );
  }

  private startRun(
    key: string,
    frozen: FrozenWishStart,
    end: (state: WishAddState) => void,
  ): boolean {
    let skipReason: WishStartAbortReason | null = null;
    // The engine hands a failed step's details only to `abortOn`; the row keeps just the text.
    let failure: RunFailure | null = null;
    // The first `beforeStep` call happens inside `engine.start`, i.e. inside step 3b itself; only
    // that one may rest on the 3a reads. Any later call (a retry) reads afresh.
    let inStartStep = true;
    const operation: RunOperation = {
      label: 'wish add',
      buildRequest: (setId, emote) => ({
        mutation: ADD_EMOTE_MUTATION,
        variables: { setId, emoteId: emote.sevenTvEmoteId, alias: frozen.wish.alias },
      }),
      // A lost answer may still have landed: `unknown`, and the next sync shows it.
      transportLossIsUnknown: true,
      abortOn: (failed) => {
        failure = failed;
        return false;
      },
      beforeStep: () => {
        const check$: Observable<WishStartAbortReason | null> = inStartStep
          ? of(this.tokenService.getToken() === frozen.token ? null : 'tokenChanged')
          : this.verifyBeforeSend(frozen);
        inStartStep = false;
        return check$.pipe(
          map((reason): StepGate => {
            if (reason === null) {
              return { kind: 'proceed' };
            }
            skipReason = reason;
            return {
              kind: 'skip',
              errorMessage: this.translocoService.translate(WISH_START_ABORT_KEY[reason]),
            };
          }),
        );
      },
    };
    this.setState(key, { kind: 'running', pauseSeconds: null });
    this.runningKey.set(key);
    const started = this.engine.start(
      frozen.activeEmoteSetId,
      [
        {
          key: `wish-${frozen.wish.id}`,
          sevenTvEmoteId: frozen.wish.sevenTvEmoteId,
          name: frozen.wish.alias,
        },
      ],
      operation,
      (result) => {
        this.runningKey.set(null);
        end(outcomeOf(result, skipReason, failure));
      },
    );
    inStartStep = false;
    if (!started) {
      this.runningKey.set(null);
    }
    return started;
  }

  /** The full check before a retry: token and set (shared with the removal), then the wish. */
  private verifyBeforeSend(frozen: FrozenWishStart): Observable<WishStartAbortReason | null> {
    return verifySetAndToken(this.startDeps, frozen).pipe(
      concatMap((reason) =>
        reason !== null
          ? of(reason)
          : readWish(this.startDeps.wishService, frozen.channelName, frozen.wish.id).pipe(
              map((read): WishStartAbortReason | null =>
                this.tokenService.getToken() !== frozen.token
                  ? 'tokenChanged'
                  : wishDeviation('add', frozen.wish, read),
              ),
            ),
      ),
    );
  }

  private setState(key: string, state: WishAddState | null): void {
    const next = new Map(this.states());
    if (state === null) {
      next.delete(key);
    } else {
      next.set(key, state);
    }
    this.states.set(next);
  }
}

function stateKey(channelName: string, wishId: number): string {
  return `${channelName}#${wishId}`;
}

/** The end state of the one row of a finished run. */
function outcomeOf(
  result: RunResult,
  skipReason: WishStartAbortReason | null,
  failure: RunFailure | null,
): WishAddState {
  const row = result.items[0];
  switch (row?.status) {
    case 'done':
      return { kind: 'added' };
    case 'unknown':
      return { kind: 'unknown' };
    case 'failed':
      return rejection(row.errorMessage ?? '', failure);
    default:
      // `cancelled`: only the check before a send ends the row so (there is no cancel here).
      return { kind: 'skipped', reason: skipReason ?? 'setUnverified' };
  }
}

/** Only what the engine tells apart for sure (plan 2.5a): a taken name (GraphQL status 409) and a
 *  token 7TV refused (HTTP 401/403, which already cleared it). Everything else shows its text. */
function rejection(message: string, failure: RunFailure | null): WishAddState {
  if (failure?.gqlStatus === NAME_TAKEN_GQL_STATUS) {
    return { kind: 'rejected', reason: 'nameTaken', message };
  }
  if (failure?.httpStatus === 401 || failure?.httpStatus === 403) {
    return { kind: 'rejected', reason: 'tokenInvalid', message };
  }
  return { kind: 'rejected', reason: 'other', message };
}
