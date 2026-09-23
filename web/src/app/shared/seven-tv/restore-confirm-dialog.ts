import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, Signal, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NamePreviewList } from '../ui/name-preview-list';
import { NoticeBanner } from '../ui/notice-banner';
import { projectSlots } from './slot-projection';

export interface RestoreConfirmDialogData {
  /** Names of the emotes about to be re-added — the preview list, capped like the delete's. One
   *  entry per row of the source (protocol, transfer-run file or finished delete run), not one per
   *  `ADD`: a #74 duplicate cell is one row here even though it restores under two aliases (see
   *  `addCount`). A row whose only entry has no alias is listed under the emote's default name
   *  (`RestoreRow.name`). */
  names: readonly string[];
  /** How many `ADD` mutations the run will actually send — spec #200, 7.2: a #74 duplicate cell
   *  restores under both of its aliases, so it counts as two here even though `names` lists it
   *  once; an entry without an alias is one `ADD` too. This, not `names.length`, is what the
   *  capacity projection below is computed against; using the row count instead would understate
   *  the projection by one slot per duplicate and could silently miss the overflow warning. */
  addCount: number;
  /** Live view of the set status, so the capacity line pops in once the check answers.
   *  null = unknown (no capacity reported) — then no projection line is shown at all. */
  slots: Signal<{ occupied: number; capacity: number } | null>;
  /** The set the run re-adds into (spec #200, 8.8) — falls back to the set id itself when unnamed,
   *  same convention as the delete confirmation's `setName`. */
  setName: string;
  /** Whether `setName` is the channel's currently active 7TV set — gates the "this set is not
   *  currently active" addition (spec 8.8). */
  isActiveSet: boolean;
}

/**
 * The restore counterpart of DeleteConfirmDialog. Same two-tier *shape* as the destructive
 * convention (outline trigger → solid executor), but deliberately not its *colour*: restoring is
 * constructive, `danger` would be a false statement — the executor is `primary`.
 * Warns (never blocks) when the set would overflow: 7TV is the authority on its own capacity.
 */
@Component({
  selector: 'app-restore-confirm-dialog',
  imports: [Button, DialogShell, NamePreviewList, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey | transloco: { count: data.names.length }">
      <p class="text-sm text-fg-secondary">
        {{ 'restore.confirmSetLine' | transloco: { setName: data.setName } }}
      </p>
      @if (!data.isActiveSet) {
        <p class="text-sm text-fg-secondary">
          {{ 'restore.confirmSetNotActive' | transloco }}
        </p>
      }
      <app-name-preview-list [names]="data.names" />

      @if (projection(); as slots) {
        @if (slots.overflow) {
          <app-notice-banner variant="warning">
            <span class="flex flex-col gap-1">
              <span>
                {{
                  'restore.capacityProjection'
                    | transloco: { projected: slots.projected, capacity: slots.capacity }
                }}
              </span>
              <span>{{ 'restore.capacityWarning' | transloco }}</span>
            </span>
          </app-notice-banner>
        } @else {
          <p class="text-sm text-fg-muted">
            {{
              'restore.capacityProjection'
                | transloco: { projected: slots.projected, capacity: slots.capacity }
            }}
          </p>
        }
      }
      <p class="text-xs text-fg-muted">{{ 'restore.historyNote' | transloco }}</p>

      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close(false)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        (click)="dialogRef.close(true)"
      >
        {{ 'restore.confirmExecute' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class RestoreConfirmDialog {
  protected readonly data = inject<RestoreConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);

  protected readonly titleKey = pluralKey(this.data.names.length, 'restore.confirmTitle');

  protected readonly projection = computed(() => {
    const slots = this.data.slots();
    if (!slots) {
      return null;
    }
    // spec #200, 7.2: projected against the number of ADDs, not the number of rows — a #74
    // duplicate cell is one row in `names` but two ADDs (one per alias).
    return projectSlots(slots.occupied, slots.capacity, this.data.addCount);
  });
}

export function openRestoreConfirmDialog(
  dialog: Dialog,
  data: RestoreConfirmDialogData,
): DialogRef<boolean> {
  return openAppDialog<boolean, RestoreConfirmDialogData>(dialog, RestoreConfirmDialog, { data });
}
