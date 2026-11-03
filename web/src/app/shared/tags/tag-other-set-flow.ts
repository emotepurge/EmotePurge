import { pluralKey } from '../../core/i18n/plural';
import { startImportFlow } from '../seven-tv/import-flow';
import { ImportTargetChoice } from '../seven-tv/import-target-dialog';
import {
  TagRunFlowDeps,
  TagRunRequest,
  clearNotice,
  raiseNotice,
  readTagEntries,
  readTagRunLive,
  startTagPlayInFlow,
  tellFeedback,
} from './tag-play-in-flow';
import { buildTagImportSource, partitionTagPlayIn, playInCandidates } from './tag-play-in';
import { TagRunNotice } from './tag-run-notice-sink';

/*
 * "Add to another set" (#328): a one-shot add of a tag's emotes into any set the user may edit,
 * chosen in the import target picker. Decided by the operator on 2026-11-02: no tracking of other
 * sets, so the run records nothing about the tag.
 *
 * What this flow deliberately does **not** do, against the active-set play-in
 * (`tag-play-in-flow.ts`):
 *
 * - **No registration, no placement report.** Both exist to judge later "Remove from active set" proposals
 *   against leave observations the sync records for the active set only; a set it does not watch
 *   would only collect placements that go stale unnoticed. The run therefore starts the ordinary
 *   import flow **without a tag hook** (`target.tag` stays unset, so `SevenTvImportService` reports
 *   no placements); the report that does go out is the plain `emotes.syncImported`.
 * - **No active-set requirement and no set guard.** The entries are read for the picked set (the
 *   backend accepts any set id; `inSet` is `null` for a non-active one), and the pre-check of the
 *   write right is the picker's `editable` flag plus 7TV itself.
 *
 * **The pin is what keeps the writes on the picked set** (`pinSetId`): without it a tracked
 * account's active set takes the loader's `trackedActive` fast path, which drops the chosen id and
 * loads whichever set is active by then — the partition would describe the picked set while the
 * writes went to another, and with no hook no set guard stops that.
 *
 * Picking the tag channel's current active set is not this flow: that is the ordinary play-in, whose
 * placements must stay correct (`routeTagIntoChosenSet`).
 */

/**
 * Routes the picker's answer: the tag channel's current active set runs the existing play-in
 * (registration, placements), any other set — including another account's active set — this flow.
 * The comparison reads the host's live view of the active set at the moment the picker closes.
 *
 * The play-in is taken only when "Add to active set" would itself be usable (`playInUsable`): an empty
 * play-in reports an activation, which would mark the tag as played in with no dialog and no choice
 * by the person. Otherwise the active set is told to be complete as it is, with no request at all.
 * `entryCount` is the tag's entry count, for the feedback when nothing is marked.
 */
export function routeTagIntoChosenSet(
  deps: TagRunFlowDeps,
  request: TagRunRequest,
  choice: ImportTargetChoice,
  active: { playInUsable: boolean; entryCount: number },
): void {
  if (choice.emoteSetId === request.activeEmoteSetId()) {
    if (active.playInUsable) {
      startTagPlayInFlow(deps, request);
      return;
    }
    // As both real entry points do: a banner (and its retry) of an earlier attempt must not stay
    // next to this feedback.
    clearNotice(request);
    const marking = request.markedIds?.length ?? 0;
    const count = marking > 0 ? marking : active.entryCount;
    tellFeedback(
      request,
      pluralKey(
        count,
        marking > 0 ? 'tags.feedback.otherSetAllMarkedPresent' : 'tags.feedback.otherSetAllPresent',
      ),
      { count, tag: request.tag.name, set: choice.setName },
    );
    return;
  }
  startTagOtherSetFlow(deps, request, choice);
}

/**
 * Steps: the tag's entries for the picked set → the live read of it → the grid marking narrows the
 * candidates (`playInCandidates`) → what the set lacks (`partitionTagPlayIn`) → the ordinary import
 * flow, whose confirm dialog names the target and what gets added. Every block ends `pending` and
 * leaves a notice; `pending` also ends at the hand-over to the import flow, which reports nothing
 * back for a dismissed dialog or a refused start.
 */
export function startTagOtherSetFlow(
  deps: TagRunFlowDeps,
  request: TagRunRequest,
  choice: ImportTargetChoice,
): void {
  const targetSetId = choice.emoteSetId;
  const restart = (): void => startTagOtherSetFlow(deps, request, choice);
  const block = (notice: TagRunNotice): void => {
    request.pending.set(false);
    raiseNotice(request, notice);
  };
  clearNotice(request);
  request.pending.set(true);

  readTagEntries(deps, request, targetSetId, block, restart, (entries) => {
    // The entries must describe the set that was picked; the active-set requirement of the
    // play-in does not apply.
    if (entries.emoteSetId !== targetSetId) {
      block({ key: 'tags.errors.otherSetChanged' });
      return;
    }
    readTagRunLive(deps, targetSetId, block, restart, (live) => {
      const marking = request.markedIds ?? [];
      const candidates = playInCandidates(entries.entries, marking);
      if (marking.length > 0 && candidates.length === 0) {
        block({ key: 'tags.errors.markedGone' });
        return;
      }
      const partition = partitionTagPlayIn(candidates, live);
      if (partition.toAdd.length === 0) {
        request.pending.set(false);
        request.onRunCommitted?.();
        const count = candidates.length;
        tellFeedback(
          request,
          pluralKey(
            count,
            marking.length > 0
              ? 'tags.feedback.otherSetAllMarkedPresent'
              : 'tags.feedback.otherSetAllPresent',
          ),
          { count, tag: request.tag.name, set: choice.setName },
        );
        return;
      }
      request.pending.set(false);
      startImportFlow(
        deps,
        buildTagImportSource(partition, request.tag, request.channelName),
        { kind: 'chosen', choice, pinSetId: true },
        undefined,
        { onStarted: () => request.onRunCommitted?.() },
      );
    });
  });
}
