import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, Signal, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NamePreviewList } from '../ui/name-preview-list';
import { NoticeBanner } from '../ui/notice-banner';

/** Live view onto the host panel's state: the warning check finishes while the dialog is open,
 *  so the dialog reads the panel's signals instead of taking a snapshot. */
export interface DeleteConfirmDialogData {
  emotes: Signal<string[]>;
  warning: Signal<EmoteSetWarning | null>;
  warningLoading: Signal<boolean>;
}

/**
 * The last screen before emotes leave 7TV for good. Focus trap, Escape, backdrop click and
 * aria-modal come from the CDK container; closes with `true` when the user confirms the run.
 *
 * Colour here means *this run is unusual*, nothing else. The two notes at the bottom are true of
 * every delete, so they are quiet; the shared-set finding is true of some, so it is a banner. The
 * old version said all four in amber or red at once and the one that mattered had to compete.
 */
@Component({
  selector: 'app-delete-confirm-dialog',
  imports: [Button, DialogShell, NamePreviewList, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey() | transloco: { count: data.emotes().length }">
      <app-name-preview-list [names]="data.emotes()" />

      @if (hasSharedSetWarning(); as warning) {
        <app-notice-banner variant="error">
          <span class="flex flex-col gap-1">
            <span class="font-medium">{{ 'massDelete.sharedSetWarningTitle' | transloco }}</span>
            @if (!warning.isOwnSet) {
              <span>{{ 'massDelete.notOwnSet' | transloco }}</span>
            }
            @if (warning.otherTrackedChannelsSharingSet.length > 0) {
              <span>
                {{
                  'massDelete.knownAffected'
                    | transloco: { list: warning.otherTrackedChannelsSharingSet.join(', ') }
                }}
              </span>
            }
            @if (warning.otherModeratedChannelsSharingSet.length > 0) {
              <span>
                {{
                  'massDelete.moderatedAffected'
                    | transloco: { list: warning.otherModeratedChannelsSharingSet.join(', ') }
                }}
              </span>
            }
          </span>
        </app-notice-banner>
      } @else if (ownershipCheckUnavailable()) {
        <app-notice-banner variant="warning">
          {{ 'massDelete.ownershipCheckUnavailable' | transloco }}
        </app-notice-banner>
      }

      <div class="flex flex-col gap-1">
        <p class="text-sm text-fg-secondary">{{ 'massDelete.irreversibleNotice' | transloco }}</p>
        <p class="text-xs text-fg-muted">
          {{ 'massDelete.undetectableChannelsNotice' | transloco }}
        </p>
      </div>

      <!-- Why the confirm button is locked, stated rather than left to the greyed-out button —
           same rule as TypedConfirmDialog's retype hint, and it replaces the old loading line. -->
      @if (data.warningLoading()) {
        <p dialog-actions id="delete-confirm-hint" class="mr-auto text-xs text-fg-muted">
          {{ 'massDelete.checkingSharedSets' | transloco }}
        </p>
      }
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
        appButton="danger-solid"
        buttonSize="lg"
        class="disabled:cursor-not-allowed"
        [disabled]="data.warningLoading()"
        [attr.aria-describedby]="data.warningLoading() ? 'delete-confirm-hint' : null"
        (click)="dialogRef.close(true)"
      >
        {{ 'massDelete.startDelete' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class DeleteConfirmDialog {
  protected readonly data = inject<DeleteConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);

  protected readonly titleKey = computed(() =>
    pluralKey(this.data.emotes().length, 'massDelete.confirmTitle'),
  );

  // Only surface the alarming (red) block when the check actually *ran* and found something to
  // flag — `available: false` means the check itself failed, not that the set was confirmed
  // foreign; conflating the two produced a guaranteed false alarm on every network hiccup.
  protected readonly hasSharedSetWarning = computed<EmoteSetWarning | null>(() => {
    const w = this.data.warning();
    if (!w?.available) {
      return null;
    }
    const flagged =
      !w.isOwnSet ||
      w.otherTrackedChannelsSharingSet.length > 0 ||
      w.otherModeratedChannelsSharingSet.length > 0;
    return flagged ? w : null;
  });

  // Separate, neutrally-worded (amber, not red) notice for "we couldn't tell" — distinct from
  // the red "we checked and it's shared" case above.
  protected readonly ownershipCheckUnavailable = computed(() => {
    const w = this.data.warning();
    return w !== null && !w.available;
  });
}

export function openDeleteConfirmDialog(
  dialog: Dialog,
  data: DeleteConfirmDialogData,
): DialogRef<boolean> {
  return openAppDialog<boolean, DeleteConfirmDialogData>(dialog, DeleteConfirmDialog, { data });
}
