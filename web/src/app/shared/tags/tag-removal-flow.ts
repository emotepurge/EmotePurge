import { signal } from '@angular/core';
import { Observable, map, of } from 'rxjs';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { DeleteTagContext, deriveTagKeptIds } from '../../core/seven-tv/tag-run-settlement';
import { confirmTimeRefusal, toDeleteQueueEmotes } from '../seven-tv/delete-flow';
import { openSevenTvTokenPromptDialog } from '../seven-tv/seven-tv-token-prompt-dialog';
import {
  PreparedTagRun,
  TagRunFlowDeps,
  TagRunRequest,
  prepareTagRun,
  sendTagReport,
} from './tag-play-in-flow';
import { TagRemovalProposal, proposeTagRemoval } from './tag-removal';
import { openTagRemovalConfirmDialog } from './tag-removal-confirm-dialog';

/*
 * The tag clear-out (#201 T-C, spec 7.2): from the click to the delete run, or — when nothing is
 * ticked — straight to the removal report.
 *
 * The shared steps (`prepareTagRun` in `tag-play-in-flow.ts`) come first, with the token prompt
 * between the registration and the live read: after the registration, so a user without write
 * rights hears that before being asked for a token (spec 7.2/3); before the read, so a cancelled
 * prompt spends no 7TV read. Then the proposal (`proposeTagRemoval`), the shared-set warning and
 * the dialog, which is preview and confirmation in one (E19).
 *
 * The confirm-time checks are the delete's own (F4): a set switch behind the open dialog, then
 * `confirmTimeRefusal` (a run that started meanwhile, a token a 401 cleared meanwhile) — the very
 * function the usage page's delete chain calls, so a refusal reads the same and, like there, does
 * not go through `noteRefusedStart`: this component's banner is its persistent explanation. The
 * queue comes from `toDeleteQueueEmotes`, with its aliasless fallback. The dock claim follows the
 * delete chain too: taken when the dialog opens, cleared when nothing was confirmed, ended after an
 * attempt.
 */

/**
 * Starts a tag clear-out. After the dialog:
 *
 * - **Nothing ticked (n = 0):** no run — nothing goes to 7TV, so there is no settlement to send the
 *   report. The flow sends it itself, with no removed ids and every own placement in the set as
 *   kept; that report is what deactivates the tag (E26: a clear-out always completes).
 * - **n > 0:** `SevenTvDeleteService.startDelete` with the ticked rows and the tag context; the
 *   run's settlement sends the removal report. `pending` ends at that hand-over (F35).
 */
export function startTagRemovalFlow(deps: TagRunFlowDeps, request: TagRunRequest): void {
  prepareTagRun(
    deps,
    request,
    {
      kind: 'removal',
      // The clear-out deletes, so it keeps the delete's own wording (`massDelete.errors.*`).
      targetBlockedKey: (resolution) => resolution.reasonKey,
      restart: () => startTagRemovalFlow(deps, request),
      beforeLiveRead: () => tokenAvailable(deps),
    },
    (prepared) => openConfirmation(deps, request, prepared),
  );
}

/** The confirmation's checked ids, split into the tag context's two own-placement lists: ticked own
 *  placements and unticked own ones, both from `ownInLiveIds`, in its order. */
export function splitOwnPlacements(
  proposal: Pick<TagRemovalProposal, 'ownInLiveIds'>,
  checkedIds: readonly string[],
): Pick<DeleteTagContext, 'checkedOwnIds' | 'uncheckedOwnIds'> {
  const checked = new Set(checkedIds);
  return {
    checkedOwnIds: proposal.ownInLiveIds.filter((id) => checked.has(id)),
    uncheckedOwnIds: proposal.ownInLiveIds.filter((id) => !checked.has(id)),
  };
}

function openConfirmation(
  deps: TagRunFlowDeps,
  request: TagRunRequest,
  prepared: PreparedTagRun,
): void {
  const proposal = proposeTagRemoval(prepared.entries.entries, prepared.live);
  const warning = signal<EmoteSetWarning | null>(null);
  const warningLoading = signal(true);
  deps.emoteAdminService.getSetWarning(request.channelName, prepared.frozenSetId).subscribe({
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

  // Held from the moment the dialog opens, as in `delete-flow.ts`: the dialog outlives the view it
  // was opened from, and the claim keeps the host's dock (and its delete section) up meanwhile.
  deps.deleteService.beginConfirmedRun();
  openTagRemovalConfirmDialog(deps.dialog, {
    tagName: request.tag.name,
    setName: request.setName ?? prepared.frozenSetId,
    isActiveSet: true,
    proposal,
    warning: warning.asReadonly(),
    warningLoading: warningLoading.asReadonly(),
  }).closed.subscribe((result) => {
    if (result === undefined) {
      deps.deleteService.clearConfirmedRun();
      request.pending.set(false);
      return;
    }
    confirm(deps, request, prepared, proposal, result.checkedIds);
  });
}

function confirm(
  deps: TagRunFlowDeps,
  request: TagRunRequest,
  prepared: PreparedTagRun,
  proposal: TagRemovalProposal,
  checkedIds: readonly string[],
): void {
  const abort = (leadKey: string, key: string, params?: Record<string, unknown>): void => {
    request.notice.set({ leadKey, key, ...(params === undefined ? {} : { params }) });
    // An attempt that ended in an abort: the claim keeps its notice window, as in the delete chain.
    deps.deleteService.endConfirmedRun();
    request.pending.set(false);
  };
  // The set the dialog showed must still be the active one (spec 7.2/7).
  if (request.activeEmoteSetId() !== prepared.frozenSetId) {
    abort('massDelete.abortedByLock', 'massDelete.setChangedDuringConfirm');
    return;
  }
  const refusal = confirmTimeRefusal(deps);
  if (refusal !== undefined) {
    abort(refusal.leadKey, refusal.reasonKey, refusal.reasonParams);
    return;
  }

  const tagContext: DeleteTagContext = {
    tagId: request.tag.id,
    operationId: prepared.operationId,
    activationOperationId: prepared.entries.activationOperationId,
    snapshot: proposal.snapshot,
    ...splitOwnPlacements(proposal, checkedIds),
    channelName: request.channelName,
  };
  const checked = new Set(checkedIds);
  const checkedRows = proposal.rows.filter((row) => checked.has(row.sevenTvEmoteId));

  if (checkedRows.length === 0) {
    // No run, so nothing for the dock to hold.
    deps.deleteService.clearConfirmedRun();
    sendTagReport(
      request,
      () =>
        deps.tagService.reportRemoval(request.channelName, request.tag.id, {
          operationId: prepared.operationId,
          emoteSetId: prepared.frozenSetId,
          targetOwnerTwitchId: prepared.ownerTwitchChannelId,
          activationOperationId: tagContext.activationOperationId,
          snapshot: tagContext.snapshot,
          removedIds: [],
          // No run: every own placement in the set stays (`deriveTagKeptIds` without a result).
          keptIds: deriveTagKeptIds(tagContext, null),
        }),
      // Never from the answer: a replay carries no outcome (F34).
      () => request.onFeedback('tags.feedback.removedNothing', { tag: request.tag.name }),
    );
    return;
  }

  const queue = toDeleteQueueEmotes(
    checkedRows.map((row) => ({
      sevenTvEmoteId: row.sevenTvEmoteId,
      name: row.displayName,
      hidden: false,
    })),
    prepared.live,
  );
  try {
    deps.deleteService.startDelete(
      prepared.frozenSetId,
      request.channelName,
      queue,
      // The frozen set is the active one (checked above), so its report expects this channel.
      request.channelName,
      prepared.ownerTwitchChannelId,
      tagContext,
    );
  } finally {
    // `finally`, as in the delete chain: a leaked claim pins an empty dock.
    deps.deleteService.endConfirmedRun();
    request.pending.set(false);
  }
}

/** The token prompt before the dialog, as `MassDeletePanel.openConfirm` asks it: `true` when a
 *  token is stored or was just saved. */
function tokenAvailable(deps: TagRunFlowDeps): Observable<boolean> {
  if (deps.tokenService.hasToken()) {
    return of(true);
  }
  return openSevenTvTokenPromptDialog(deps.dialog).closed.pipe(map((saved) => saved === true));
}
