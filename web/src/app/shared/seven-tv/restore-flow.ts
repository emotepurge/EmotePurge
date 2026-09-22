import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { signal } from '@angular/core';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { DeleteQueueEmote } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { PurgeRunRow } from '../export/purge-run-export';
import { filterAlreadyPresent } from './already-present-filter';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

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
 * this was extracted from: restoring an already-validated purge-run protocol has no read-only
 * preview step worth protecting the token prompt's ordering against — do not "align" this with
 * the import flow.
 *
 * `channelName` and `setId` are the values frozen at the moment the caller chose this file/run —
 * the flow never reads them from a live signal, so a channel switch while a dialog of this chain
 * is still open cannot change what gets restored.
 */
export function startRestoreFlow(
  deps: RestoreFlowDeps,
  channelName: string,
  setId: string,
  rows: PurgeRunRow[],
): void {
  const openConfirm = (): void => {
    // Live slot view, same pattern as the delete confirm's shared-set warning. The signal is born
    // here, next to its one subscription and its one reader — openConfirm runs at most once per
    // flow, so there is nothing to reset it from.
    const slots = signal<{ occupied: number; capacity: number } | null>(null);
    deps.emoteAdminService.getSetStatus(channelName).subscribe({
      next: (status) =>
        slots.set(
          status.capacity === null
            ? null
            : { occupied: status.occupiedSlots, capacity: status.capacity },
        ),
      error: () => slots.set(null),
    });

    const data: RestoreConfirmDialogData = {
      names: rows.map((row) => row.name),
      slots: slots.asReadonly(),
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
      // alias, so a #74 duplicate comes back under both of its names.
      const emotes: DeleteQueueEmote[] = rows.map((row) => ({
        emoteId: row.emoteId ?? undefined,
        sevenTvEmoteId: row.sevenTvEmoteId,
        name: row.name,
        aliases: row.aliases,
      }));
      // #149/T5: a restore never had any duplicate protection at all — filter it fresh, right here,
      // against the target set's current contents, read from 7TV itself rather than our database
      // (see `filterAlreadyPresent`'s doc — asking our own mirror is exactly wrong for restore,
      // which runs *because* something already went wrong and our mirror may still be stale) for
      // why this sits at confirm-time rather than dialog-open-time and for the residual race it
      // does not close.
      filterAlreadyPresent(deps.httpClient, setId, emotes).subscribe(
        ({ rows: toRestore, skipped, available }) => {
          // #149 P2 review fix: the arbiter check above ran *before* this fetch, outside the
          // mutual-exclusion contract (design doc §4.3) it is meant to enforce — another run can
          // start in that window. Re-checked here, right before the only remaining call that
          // actually starts anything; silent on a block for the same reason as the check above,
          // the run that got there first is already visible in the dock.
          if (deps.arbiter.activeRun() !== null) {
            return;
          }
          deps.restoreService.startRestore(setId, channelName, toRestore, skipped, available);
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
