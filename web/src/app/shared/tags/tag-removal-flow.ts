import { signal } from '@angular/core';
import { Observable, map, of } from 'rxjs';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { timeoutReportAttempt } from '../../core/seven-tv/seven-tv-delete.service';
import { DeleteTagContext, deriveTagKeptIds } from '../../core/seven-tv/tag-run-settlement';
import { EmoteTagEntries, EmoteTagEntry } from '../../core/tags/emote-tag.model';
import { confirmTimeRefusal, toDeleteQueueEmotes } from '../seven-tv/delete-flow';
import { openSevenTvTokenPromptDialog } from '../seven-tv/seven-tv-token-prompt-dialog';
import {
  PreparedTagRun,
  TagRunFlowDeps,
  TagRunRequest,
  prepareTagRun,
  raiseNotice,
  sendTagReport,
  tellFeedback,
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
 * The confirm-time checks are the delete's own (docs/DECISIONS.md, #201 T-C, on the deviation from
 * the wording of spec 7.2/7): a set switch behind the open dialog, then
 * `confirmTimeRefusal` (a run that started meanwhile, a token a 401 cleared meanwhile) — the very
 * function the usage page's delete chain calls, so a refusal reads the same and, like there, does
 * not go through `noteRefusedStart`: this component's banner is its persistent explanation. The
 * queue comes from `toDeleteQueueEmotes`, with its aliasless fallback. The dock claim follows the
 * delete chain too: taken when the dialog opens, cleared when nothing was confirmed, ended after an
 * attempt. The set check reads `PreparedTagRun.activeEmoteSetId`, which a host torn down behind
 * the dialog has turned `null` — a set switch, so the confirm aborts (see `tag-play-in-flow.ts`),
 * and its notice goes to the page-level sink, since the host's own banner went with the host.
 * With nothing ticked the token half of the refusal is skipped (`requireToken: false`): that
 * confirm writes nothing to 7TV, only the report.
 *
 * **A second entry read at confirm time, but no second live read.** With something ticked, the
 * tag's entries are read again from our own API right before the delete starts
 * (`confirmTimeEntriesDrift`): the dialog can stay open for minutes, and a tag played in meanwhile
 * (another tab, another manager) may now need a ticked emote — for a tag that is not played in,
 * every emote of it in the set is ticked, so that window covers all of them (risk R4). Anything
 * the proposal no longer stands on — a new holder of a ticked row, a ticked entry gone from the
 * tag, another activation or snapshot of this tag, another set — aborts the whole clear-out with
 * nothing deleted; a failed read, or one that outlasts the reports' 30 s bound, aborts too (fail
 * closed). The person opens it again and sees the new state; no row is unticked behind their back.
 *
 * The 7TV set itself is not read again, unlike the delete chain's #227 read. The one live read
 * before the dialog supplies both the proposal and the aliases the queue and the protocol record
 * (spec 7.2/4), and the engine does not re-read before its `REMOVE`s either — so with a dialog left
 * open for minutes, those aliases can be minutes old. Accepted, for two reasons:
 *
 * - Another tag re-adding an emote of this one while the dialog was open puts that emote in
 *   **both** live reads; a fresh one would wave it through just the same. A *holder* appearing is
 *   what matters, and the entry read above catches it.
 * - The delete chain fails the whole batch when a ticked row is missing from 7TV (#227), because a
 *   partial run would record a protocol that no longer matches what its dialog showed. Here such a
 *   row simply ends `failed` in the run and lands in the report's kept ids — no extra deletion, no
 *   write beyond what was ticked, and the tag's bookkeeping stays true to what 7TV did.
 */

/**
 * Starts a tag clear-out. After the dialog:
 *
 * - **Nothing ticked (n = 0):** no run — nothing goes to 7TV, so there is no settlement to send the
 *   report. The flow sends it itself, with no removed ids and every own placement in the set as
 *   kept; that report is what deactivates the tag (E26: a clear-out always completes).
 * - **n > 0:** the tag's entries are read once more (`confirmTimeEntriesDrift`, see above); only
 *   when they still back the proposal, `SevenTvDeleteService.startDelete` with the ticked rows and
 *   the tag context; the run's settlement sends the removal report. `pending` ends at that
 *   hand-over: a run reports nothing back to this flow.
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

/**
 * Whether the entry read made at confirm time still backs what the dialog proposed — `null` when it
 * does. `setChanged`: the read no longer describes the frozen set, or that set is no longer the
 * active one. `stateChanged`: the tag's activation or its own placements (the snapshot) differ, a
 * ticked emote is no longer an entry of the tag, or a ticked emote has a holder it did not have when
 * the dialog opened — another active tag that needs it, or another tag's placement. A holder the
 * dialog already showed (`toRow` lists both holder kinds on every path) is no change: the person ticked that row knowingly.
 */
export function confirmTimeEntriesDrift(
  frozenSetId: string,
  before: EmoteTagEntries,
  now: EmoteTagEntries,
  checkedIds: readonly string[],
): 'setChanged' | 'stateChanged' | null {
  if (now.emoteSetId !== frozenSetId || !now.isActiveSet) {
    return 'setChanged';
  }
  if (now.activationOperationId !== before.activationOperationId) {
    return 'stateChanged';
  }
  if (ownSnapshotKey(before.entries) !== ownSnapshotKey(now.entries)) {
    return 'stateChanged';
  }
  const beforeById = new Map(before.entries.map((entry) => [entry.sevenTvEmoteId, entry]));
  const nowById = new Map(now.entries.map((entry) => [entry.sevenTvEmoteId, entry]));
  for (const id of checkedIds) {
    const fresh = nowById.get(id);
    if (fresh === undefined) {
      return 'stateChanged';
    }
    const knownHolders = holderIds(beforeById.get(id));
    if ([...holderIds(fresh)].some((holder) => !knownHolders.has(holder))) {
      return 'stateChanged';
    }
  }
  return null;
}

function openConfirmation(
  deps: TagRunFlowDeps,
  request: TagRunRequest,
  prepared: PreparedTagRun,
): void {
  const proposal = proposeTagRemoval(
    prepared.entries.entries,
    prepared.live,
    prepared.entries.activationOperationId !== null,
  );
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
    raiseNotice(request, { leadKey, key, ...(params === undefined ? {} : { params }) });
    // An attempt that ended in an abort: the claim keeps its notice window, as in the delete chain.
    deps.deleteService.endConfirmedRun();
    request.pending.set(false);
  };
  const checked = new Set(checkedIds);
  const checkedRows = proposal.rows.filter((row) => checked.has(row.sevenTvEmoteId));
  /** The confirm-time guards; `false` after an abort. Run again after the entry re-read, which
   *  takes a moment of its own. */
  const guardsPass = (): boolean => {
    // The set the dialog showed must still be the active one (spec 7.2/7) — and its host still
    // there: a torn-down host reads `null` here (`hostBoundActiveSet`), never its stale last id.
    if (prepared.activeEmoteSetId() !== prepared.frozenSetId) {
      abort('massDelete.abortedByLock', 'massDelete.setChangedDuringConfirm');
      return false;
    }
    // With nothing ticked nothing goes to 7TV, so a token a 401 cleared meanwhile does not matter;
    // a run holding the arbiter still does, as for any confirm.
    const refusal = confirmTimeRefusal(deps, { requireToken: checkedRows.length > 0 });
    if (refusal !== undefined) {
      abort(refusal.leadKey, refusal.reasonKey, refusal.reasonParams);
      return false;
    }
    return true;
  };
  if (!guardsPass()) {
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
      // Never from the answer: a replay carries no outcome (docs/DECISIONS.md, #201 T-C).
      () => tellFeedback(request, 'tags.feedback.removedNothing', { tag: request.tag.name }),
    );
    return;
  }

  // Fail closed: whatever keeps the re-read from confirming the proposal deletes nothing. Its
  // "Try again" opens the clear-out afresh — the new state, in a new dialog. Not bound to the host's
  // lifetime: a host torn down meanwhile is a set switch to the guards below, so the abort still
  // ends the claim and reaches the page-level sink.
  const restart = (): void => startTagRemovalFlow(deps, request);
  deps.tagService
    .listEntries(request.channelName, request.tag.id, prepared.frozenSetId)
    .pipe(
      timeoutReportAttempt(),
      // In a `map`, so a malformed answer that makes the comparison throw reaches `error` (fail
      // closed) instead of an unhandled throw in `next` that would leave the claim and `pending` set.
      map((fresh) =>
        confirmTimeEntriesDrift(
          prepared.frozenSetId,
          prepared.entries,
          fresh,
          checkedRows.map((row) => row.sevenTvEmoteId),
        ),
      ),
    )
    .subscribe({
      next: (drift) => {
        if (drift === 'setChanged') {
          abort('massDelete.abortedByLock', 'massDelete.setChangedDuringConfirm');
          return;
        }
        if (drift === 'stateChanged') {
          abortWithRetry('tags.errors.changedDuringConfirm');
          return;
        }
        if (guardsPass()) {
          startRun();
        }
      },
      error: () => abortWithRetry('tags.errors.entriesUnavailable'),
    });

  function abortWithRetry(key: string): void {
    raiseNotice(request, { leadKey: 'massDelete.nothingDeleted', key, retry: restart });
    deps.deleteService.endConfirmedRun();
    request.pending.set(false);
  }

  function startRun(): void {
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
}

/** The tag's own placements with their revisions, as one comparable string. */
function ownSnapshotKey(entries: readonly EmoteTagEntry[]): string {
  return entries
    .filter((entry) => entry.placedByThisTag)
    .map((entry) => `${entry.sevenTvEmoteId}:${entry.placementOperationId ?? ''}`)
    .sort()
    .join('\n');
}

/** Every other tag an entry names as needing it: active holders and other tags' placements. */
function holderIds(entry: EmoteTagEntry | undefined): ReadonlySet<number> {
  if (entry === undefined) {
    return new Set();
  }
  return new Set([...entry.heldByActiveTags, ...entry.placedByOtherTags].map((ref) => ref.id));
}

/** The token prompt before the dialog, as `MassDeletePanel.openConfirm` asks it: `true` when a
 *  token is stored or was just saved. */
function tokenAvailable(deps: TagRunFlowDeps): Observable<boolean> {
  if (deps.tokenService.hasToken()) {
    return of(true);
  }
  return openSevenTvTokenPromptDialog(deps.dialog).closed.pipe(map((saved) => saved === true));
}
