import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, Signal, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import {
  ownershipCheckUnavailable,
  sharedSetWarningOf,
} from '../../shared/seven-tv/delete-confirm-dialog';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';

/** What the removal is bound to (plan 2.6), and the shared-set check, live while the dialog is open. */
export interface WishRemovalConfirmData {
  /** The alias the entry sits under — the only thing the removal sends to pick the entry. */
  alias: string;
  /** The generation the removal is bound to, `slotAddedAtUtc` as the wish carries it. */
  addedAtUtc: string;
  /** The set's name, or its id when the pre-check named none. */
  setName: string;
  warning: Signal<EmoteSetWarning | null>;
  warningLoading: Signal<boolean>;
}

/**
 * The last screen before a wished emote's entry leaves the active 7TV set (#485, plan 2.6). It names
 * exactly what the run is bound to — the alias, the add date of that generation and the set — and
 * carries the delete dialog's shared-set warning. Closes with `true` on "Entfernen".
 */
@Component({
  selector: 'app-wish-removal-confirm-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'wishes.remove.confirm.title' | transloco">
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
        <dt class="text-fg-secondary">{{ 'wishes.remove.confirm.alias' | transloco }}</dt>
        <dd class="min-w-0 font-medium break-words">{{ data.alias }}</dd>
        <dt class="text-fg-secondary">{{ 'wishes.remove.confirm.addedAt' | transloco }}</dt>
        <dd>{{ addedAt() }}</dd>
        <dt class="text-fg-secondary">{{ 'wishes.remove.confirm.set' | transloco }}</dt>
        <dd class="min-w-0 break-words">{{ data.setName }}</dd>
      </dl>
      <p class="text-sm text-fg-secondary">{{ 'wishes.remove.confirm.intro' | transloco }}</p>

      @if (sharedSetWarning(); as warning) {
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
      } @else if (checkUnavailable()) {
        <app-notice-banner variant="warning">
          {{ 'massDelete.ownershipCheckUnavailable' | transloco }}
        </app-notice-banner>
      }

      <p class="text-xs text-fg-muted">{{ 'massDelete.undetectableChannelsNotice' | transloco }}</p>

      <!-- Why the button waits, stated rather than left to the greyed-out button (§10). -->
      @if (data.warningLoading()) {
        <p dialog-actions id="wish-removal-confirm-hint" class="mr-auto text-xs text-fg-muted">
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
        [attr.aria-describedby]="data.warningLoading() ? 'wish-removal-confirm-hint' : null"
        (click)="dialogRef.close(true)"
      >
        {{ 'wishes.remove.confirm.submit' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class WishRemovalConfirmDialog {
  protected readonly data = inject<WishRemovalConfirmData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
  private readonly languageService = inject(LanguageService);

  /** The generation's date and time in the reader's language and zone. */
  protected readonly addedAt = computed(() =>
    new Date(this.data.addedAtUtc).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'medium',
      timeStyle: 'short',
    }),
  );

  protected readonly sharedSetWarning = computed(() => sharedSetWarningOf(this.data.warning()));
  protected readonly checkUnavailable = computed(() =>
    ownershipCheckUnavailable(this.data.warning()),
  );
}

export function openWishRemovalConfirmDialog(
  dialog: Dialog,
  data: WishRemovalConfirmData,
): DialogRef<boolean> {
  return openAppDialog<boolean, WishRemovalConfirmData>(dialog, WishRemovalConfirmDialog, { data });
}
