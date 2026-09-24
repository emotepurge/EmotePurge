import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { signal } from '@angular/core';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import {
  RestoreQueueEmote,
  RestoreStartTarget,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { RestoreRow } from '../export/purge-run-export';
import { filterAlreadyPresentForRestore } from './already-present-filter';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/**
 * Everything a restore run needs to know where it goes, whom it tells, what its confirmation
 * shows, and whether that differs from the page it was started on (spec 6.1) — the shared
 * pre-check's outcome (`EditableSetTarget`, spec 6.2) plus the two fields only a caller attached to
 * a page can supply:
 *
 * - `hostChannelName` — the channel of the page the run was started on (E13); only
 *   `resetIfChannelChanged` compares against it (F7), never the target itself.
 * - `hostSelectedSetId` — the page's *selected* set, `null` when it has none; the confirmation's
 *   foreign-to-view hint compares this against `emoteSetId` (E21) — a different set of the *same*
 *   channel and a page with no selection both count as foreign.
 *
 * Produced by `resolveEditableSet` plus these two fields today (`MassDeletePanel`'s
 * `openRestoreConfirm`); `FileImportStep` becomes the file-based producer once T5 wires it up (2.5
 * of the plan) — until then `ImportTrigger` builds an interim one from the page's frozen values.
 */
export interface ResolvedRestoreTarget {
  emoteSetId: string;
  setName: string;
  ownerDisplayName: string;
  twitchLogin: string;
  trackedChannelName: string | null;
  isActiveSet: boolean;
  hostChannelName: string;
  hostSelectedSetId: string | null;
}

/**
 * Everything the flow needs, handed in rather than injected — same reasoning as `ImportFlowDeps`
 * in `import-flow.ts` (see there): the flow opens dialogs, which live in `shared/`, while the
 * services it drives live in `core/`, and `core/` may not import from `shared/`. A service here
 * would therefore have to sit in `shared/` and be provided there, for a piece of code that holds
 * no state between calls; a function that takes its collaborators keeps the dependency edges
 * pointing the one legal way and lets every entry point pass its own injected instances.
 */
export interface RestoreFlowDeps {
  dialog: Dialog;
  emoteAdminService: EmoteAdminService;
  /** The live slot preview for a non-active target set (spec #200, 8.3/7.2, K5) — read only when
   *  `isActiveSet` is false; the active set keeps using `emoteAdminService.getSetStatus`'s
   *  cheaper, non-7TV-rate-limited path (same fork `loadImportTarget` already uses, AK 36). */
  emoteSetService: SevenTvEmoteSetService;
  /** Only for `filterAlreadyPresent`'s direct read against 7TV (#149 P1 fix) — every other read in
   *  this flow goes through `emoteAdminService`. */
  httpClient: HttpClient;
  tokenService: SevenTvTokenService;
  restoreService: SevenTvRestoreService;
  arbiter: SevenTvRunArbiter;
}

/**
 * Confirms and starts one restore run: 7TV token → confirmation with a live slot preview → run.
 *
 * **The token prompt comes before the confirmation**, unlike the import flow, which asks only
 * after the confirmation (see the note on that in `startImportFlow`). Unchanged from the panel
 * this was extracted from: restoring an already-validated restore file has no read-only
 * preview step worth protecting the token prompt's ordering against — do not "align" this with
 * the import flow.
 *
 * `target` is the value frozen at the moment the caller resolved it — the flow never re-reads it
 * from a live signal, so a channel or set switch while a dialog of this chain is still open cannot
 * change what gets restored or where (spec #200, 8.8, carried over to `ResolvedRestoreTarget`).
 */
export function startRestoreFlow(
  deps: RestoreFlowDeps,
  target: ResolvedRestoreTarget,
  rows: readonly RestoreRow[],
): void {
  const openConfirm = (): void => {
    // Live slot view, same pattern as the delete confirm's shared-set warning. The signal is born
    // here, next to its one subscription and its one reader — openConfirm runs at most once per
    // flow, so there is nothing to reset it from.
    const slots = signal<{ occupied: number; capacity: number } | null>(null);
    // Slot-preview fork (spec 4.3, point 8 — same fork `loadImportTarget` already uses): a
    // tracked, *active* target reads the cheap, non-7TV-rate-limited status; anything else — a
    // non-active set of a tracked channel, or an untracked target — reads the live per-set preview
    // instead, keyed by the tracked channel when there is one, otherwise the account's own
    // `twitchLogin` (the active-set status endpoint has no set-scoped or untracked form at all).
    if (target.trackedChannelName !== null && target.isActiveSet) {
      deps.emoteAdminService.getSetStatus(target.trackedChannelName).subscribe({
        next: (status) =>
          slots.set(
            status.capacity === null
              ? null
              : { occupied: status.occupiedSlots, capacity: status.capacity },
          ),
        error: () => slots.set(null),
      });
    } else {
      deps.emoteSetService
        .loadEmoteSetPreview(target.trackedChannelName ?? target.twitchLogin, target.emoteSetId)
        .subscribe({
          next: (preview) =>
            slots.set(
              preview.capacity === null
                ? null
                : { occupied: preview.totalCount, capacity: preview.capacity },
            ),
          error: () => slots.set(null),
        });
    }

    // spec #200, 7.2: the projection is against ADDs, not rows — a #74 duplicate cell's row
    // carries every alias it sat under and restores once per alias. An entry without an alias
    // (`null`, from a transfer-run file) is an ADD like any other.
    const addCount = rows.reduce((sum, row) => sum + row.aliases.length, 0);

    const data: RestoreConfirmDialogData = {
      names: rows.map((row) => row.name),
      addCount,
      slots: slots.asReadonly(),
      setName: target.setName,
      isActiveSet: target.isActiveSet,
      emoteSetId: target.emoteSetId,
      ownerDisplayName: target.ownerDisplayName,
      trackedChannelName: target.trackedChannelName,
      // Spec E21: a different set of the page's own channel, and a page with no selected set at
      // all, both count as foreign — never a channel comparison.
      foreignToView: target.emoteSetId !== target.hostSelectedSetId,
    };
    openRestoreConfirmDialog(deps.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      // The engine only refuses *its own* second run; a delete or import running elsewhere is
      // invisible to it, so the cross-kind check happens here — silently, because the progress of
      // that other run is already on screen and saying it twice would be the louder mistake.
      if (deps.arbiter.activeRun() !== null) {
        return;
      }
      // `emoteId` is `null` for a row that never had a local emote (spec #200, 7.2) — the restore
      // does not need it. `aliases` goes through whole: the restore service sends one `ADD` per
      // alias, so a #74 duplicate comes back under both of its names, and a `null` alias as an
      // `ADD` without one. `defaultName` names that aliasless queue row.
      const emotes: RestoreQueueEmote[] = rows.map((row) => ({
        emoteId: row.emoteId ?? undefined,
        sevenTvEmoteId: row.sevenTvEmoteId,
        name: row.name,
        aliases: row.aliases,
        defaultName: row.defaultName,
      }));
      // #149/T5: a restore never had any duplicate protection at all — filter it fresh, right here,
      // against the target set's current contents, read from 7TV itself rather than our database
      // (see `filterAlreadyPresent`'s doc — asking our own mirror is exactly wrong for restore,
      // which runs *because* something already went wrong and our mirror may still be stale) for
      // why this sits at confirm-time rather than dialog-open-time and for the residual race it
      // does not close. Per alias, not per id (operator decision 2026-09-22): a row whose id is
      // present only under some of its own aliases re-adds just the missing ones, and an alias
      // another emote now holds is left out rather than sent into a certain name conflict — see
      // `filterAlreadyPresentForRestore`.
      filterAlreadyPresentForRestore(deps.httpClient, target.emoteSetId, emotes).subscribe(
        ({ rows: toRestore, skipped, skippedNameTaken, available }) => {
          // #149 P2 review fix: the arbiter check above ran *before* this fetch, outside the
          // mutual-exclusion contract (design doc §4.3) it is meant to enforce — another run can
          // start in that window. Re-checked here, right before the only remaining call that
          // actually starts anything; silent on a block for the same reason as the check above,
          // the run that got there first is already visible in the dock.
          if (deps.arbiter.activeRun() !== null) {
            return;
          }
          deps.restoreService.startRestore(
            restoreStartTarget(target),
            toRestore,
            skipped,
            available,
            skippedNameTaken,
          );
        },
      );
    });
  };

  if (deps.tokenService.hasToken()) {
    openConfirm();
    return;
  }
  openSevenTvTokenPromptDialog(deps.dialog).closed.subscribe((saved) => {
    if (saved === true) {
      openConfirm();
    }
  });
}

/** Derives the restore service's `RestoreStartTarget` from the resolved target (spec 6.4):
 *  `expectedChannelName` is the tracked channel only when the target is its *active* set (E18);
 *  `resyncChannelName` is the tracked channel only when it is *not* (E12), the one case no backend
 *  resync covers; an untracked target sends neither. `ownerOrChannelLabel` prefers the tracked
 *  channel, falling back to the owner's display name for an untracked target — display only
 *  (the dock's target line), never compared. Exported: `MassDeletePanel`'s own restore-confirm
 *  chain (spec E16) needs the identical derivation and stays its own chain rather than folding
 *  into `startRestoreFlow` — its rows come from a finished delete run's `RunQueueItem`s, not from
 *  a file's `RestoreRow`s, so the two `filterAlreadyPresentForRestore`/`startRestore` call sites
 *  are not otherwise byte-identical (task brief, Plan-253 §6 Nr. 3). */
export function restoreStartTarget(target: ResolvedRestoreTarget): RestoreStartTarget {
  const channel = target.trackedChannelName;
  return {
    setId: target.emoteSetId,
    expectedChannelName: channel !== null && target.isActiveSet ? channel : null,
    resyncChannelName: channel !== null && !target.isActiveSet ? channel : null,
    hostChannelName: target.hostChannelName,
    setName: target.setName,
    ownerOrChannelLabel: channel ?? target.ownerDisplayName,
  };
}
