import { signal } from '@angular/core';
import { Observable, map } from 'rxjs';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import {
  DeleteQueueEmote,
  SevenTvDeleteService,
  WishBindingSkipReason,
} from '../../core/seven-tv/seven-tv-delete.service';
import type { Wish } from '../../core/wishes/wish.model';
import {
  WishRemovalProgress,
  WishRemovalTarget,
  wishRemovalBlockKey,
} from '../../core/wishes/wish-removal';
import {
  FrozenWishStart,
  WishRunStartDeps,
  WishStartAbortReason,
  WishStartCaller,
  readWish,
  runWishStart,
  verifySetAndToken,
  wishDeviation,
} from '../../core/wishes/wish-run-start';
import { WishRemovalConfirmData } from './wish-removal-confirm-dialog';

/*
 * "Entfernen" from a wish row (#485, plan 2.6, T9): the start contract of plan 2.5 (`runWishStart`,
 * T8), then the existing delete run with exactly one entry row — so the run reports `sync-deleted`
 * (audit `emotes.syncDeleted`) and the dock offers its protocol and "Wiederherstellen", like every
 * delete run (operator decision, plan R2).
 *
 * **The target comes from the frozen wish alone (Sol-P1-1):** the entry row's slot, alias and add
 * date are the wish's `slotId`, `alias` and `slotAddedAtUtc` as frozen at the click — the date
 * verbatim, as the DTO carried it —, the set is the frozen active set. No list, no usage total and
 * no read of the page supplies any of it.
 *
 * **Before every attempt** the row carries the wish binding of the delete run (plan 2.6): the
 * confirmed token, the frozen active set, the start contract's own "set and token" check
 * (`verifySetAndToken`, always with `refresh`) and, since #500 (E46, plan 2.7), a fresh read of the
 * wish compared with the frozen one (`wishDeviation`), so a colleague's keep, delete or adopt during
 * a rate-limit pause stops the retry.
 *
 * The confirm-time refusal of the delete chain (`confirmTimeRefusal`: a run holding the arbiter, a
 * token gone) is part of step 3b here (`decideWishStart` ii and iii, the token compared for
 * equality, not presence), so the flow does not call it a second time. The dock claim follows the
 * delete chain: taken when the confirmation opens, cleared when nothing was confirmed, ended after
 * an attempt. `SevenTvDeleteService.startCheckPending` stands from the click until the run holds the
 * arbiter or the start ended.
 */

/** Everything the flow needs, handed in (the `DeleteFlowDeps` pattern). */
export interface WishRemovalFlowDeps extends WishRunStartDeps {
  deleteService: Pick<
    SevenTvDeleteService,
    | 'startDeleteRun'
    | 'startCheckPending'
    | 'beginConfirmedRun'
    | 'endConfirmedRun'
    | 'clearConfirmedRun'
  >;
  /** Opens the confirmation; `true` when the user chose "Entfernen". */
  openConfirmation(data: WishRemovalConfirmData): Observable<boolean>;
}

export interface WishRemovalRequest {
  /** The page's channel, already normalized. */
  channelName: string;
  wish: Wish;
  caller: WishStartCaller;
  /** Opens the token prompt; `true` once a token is there. */
  promptToken(): Observable<boolean>;
  progress: WishRemovalProgress;
}

/** Starts the removal of `request.wish`. A wish whose removal is locked starts nothing. */
export function startWishRemovalFlow(deps: WishRemovalFlowDeps, request: WishRemovalRequest): void {
  if (wishRemovalBlockKey(request.wish) !== null) {
    return;
  }
  const { progress } = request;
  progress.starting();
  runWishStart(
    deps,
    {
      purpose: 'remove',
      ownKind: 'delete',
      channelName: request.channelName,
      wish: request.wish,
      caller: request.caller,
      startCheckPending: deps.deleteService.startCheckPending,
      promptToken: () => request.promptToken(),
      confirm: (frozen) => confirmRemoval(deps, frozen),
    },
    {
      start: (frozen) => startRun(deps, request, frozen),
      aborted: (reason: WishStartAbortReason) => {
        // An attempt that ended without a run: the claim keeps its notice window, as in the
        // delete chain (a claim never taken is left alone by `endConfirmedRun`).
        deps.deleteService.endConfirmedRun();
        progress.notStarted(reason);
      },
      dismissed: () => {
        deps.deleteService.clearConfirmedRun();
        progress.dismissed();
      },
    },
  );
}

/** The confirmation with the shared-set warning; the dock claim is taken as it opens. */
function confirmRemoval(deps: WishRemovalFlowDeps, frozen: FrozenWishStart): Observable<boolean> {
  const warning = signal<EmoteSetWarning | null>(null);
  const warningLoading = signal(true);
  deps.emoteAdminService.getSetWarning(frozen.channelName, frozen.activeEmoteSetId).subscribe({
    next: (answer) => {
      warning.set(answer);
      warningLoading.set(false);
    },
    // Same fallback as the delete dialog's: "could not check", never a false "foreign set" alarm.
    error: () => {
      warning.set({
        available: false,
        isOwnSet: false,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      });
      warningLoading.set(false);
    },
  });
  deps.deleteService.beginConfirmedRun();
  return deps.openConfirmation({
    alias: frozen.wish.alias,
    // Non-null: the flow starts only for a wish with a generation (`wishRemovalBlockKey`).
    addedAtUtc: frozen.wish.slotAddedAtUtc ?? '',
    setName: frozen.activeEmoteSetName ?? frozen.activeEmoteSetId,
    warning: warning.asReadonly(),
    warningLoading: warningLoading.asReadonly(),
  });
}

/** Step 3b's start: one entry row from the frozen wish, bound to it; `false` if not started. */
function startRun(
  deps: WishRemovalFlowDeps,
  request: WishRemovalRequest,
  frozen: FrozenWishStart,
): boolean {
  try {
    const { wish } = frozen;
    // 3b (v) compared the fresh wish with these very fields, both non-null; kept as a guard.
    if (wish.slotId === null || wish.slotAddedAtUtc === null) {
      return false;
    }
    const target: WishRemovalTarget = {
      channelName: frozen.channelName,
      wishId: wish.id,
      slotId: wish.slotId,
      slotAddedAtUtc: wish.slotAddedAtUtc,
      emoteSetId: frozen.activeEmoteSetId,
    };
    const start = deps.deleteService.startDeleteRun(
      frozen.activeEmoteSetId,
      frozen.channelName,
      [wishEntryRow(deps, frozen, target)],
      // The frozen set is the channel's active one (3b iv), so its report expects this channel.
      frozen.channelName,
      frozen.ownerTwitchChannelId,
      undefined,
      { onSettled: (settlement) => request.progress.settled(target, settlement) },
    );
    if (start.started) {
      request.progress.started(start.runId, target);
    }
    return start.started;
  } finally {
    // `finally`, as in the delete chain: a leaked claim pins an empty dock.
    deps.deleteService.endConfirmedRun();
  }
}

/** The run's one row: the wish's entry by its frozen slot, alias and generation, with the binding. */
export function wishEntryRow(
  deps: Pick<WishRunStartDeps, 'emoteSetService' | 'tokenService' | 'wishService'>,
  frozen: FrozenWishStart,
  target: WishRemovalTarget,
): DeleteQueueEmote {
  return {
    sevenTvEmoteId: frozen.wish.sevenTvEmoteId,
    name: frozen.wish.alias,
    entry: {
      slotId: target.slotId,
      alias: frozen.wish.alias,
      addedAt: target.slotAddedAtUtc,
      cardKey: `wish-${target.wishId}`,
    },
    wishBinding: {
      activeEmoteSetId: target.emoteSetId,
      token: frozen.token,
      verifySetAndToken: () => verifySetAndToken(deps, frozen).pipe(map(bindingReason)),
      // The start's own read and comparison (3b v), with its deadline: 404, another state, slot or
      // generation is `wishChanged`; a failed, throttled or hung read is `wishUnverified`.
      verifyWish: () =>
        readWish(deps.wishService, frozen.channelName, frozen.wish.id).pipe(
          map((read) => wishBindingReason(wishDeviation('remove', frozen.wish, read))),
        ),
    },
  };
}

/** The start contract's reasons a "set and token" check can give, as the binding's own. */
function bindingReason(reason: WishStartAbortReason | null): WishBindingSkipReason | null {
  switch (reason) {
    case null:
      return null;
    case 'tokenChanged':
    case 'setChanged':
      return reason;
    default:
      return 'setUnverified';
  }
}

/** The start contract's reasons a check of the wish can give, as the binding's own. */
function wishBindingReason(reason: WishStartAbortReason | null): WishBindingSkipReason | null {
  switch (reason) {
    case null:
      return null;
    case 'wishChanged':
      return 'wishChanged';
    default:
      return 'wishUnverified';
  }
}
