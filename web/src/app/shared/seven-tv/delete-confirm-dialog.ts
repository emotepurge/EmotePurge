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
 *  so the dialog reads the panel's signals instead of taking a snapshot.
 *
 *  The two name lists are live for a second reason, and it is load-bearing rather than incidental:
 *  a pushed reload can prune the host's selection while this dialog is open, and the last screen
 *  before an irreversible write must name what will actually be deleted. `MassDeletePanel` reads
 *  the very same signals again, synchronously, in the `closed` callback (operator decision
 *  2026-09-22), so what the confirmation last showed and what the run deletes are the same list by
 *  construction. Whoever changes these two to plain values must move that snapshot with them. */
export interface DeleteConfirmDialogData {
  /** The visible names — same capped preview as before (Konzept "Auswahl überlebt Suche und
   *  Filter" 2.1: they are on screen already, so the 50-name cap costs nothing here). */
  emotes: Signal<string[]>;
  /** Names the host page's current filter hides right now. Shown in their own, uncapped block —
   *  this is the safety-relevant half: every target the user cannot currently see must still be
   *  identifiable by name right before the irreversible run. */
  hiddenEmotes: Signal<string[]>;
  warning: Signal<EmoteSetWarning | null>;
  warningLoading: Signal<boolean>;
  /** The set the run deletes from (spec #200, 8.8) — the page's *selected* set, not necessarily
   *  the active one. Named here so the confirmation never leaves it to the reader to remember
   *  which set the dropdown showed when the dialog opened. Falls back to the set id itself when
   *  the host page's set list has not (or no longer) named it, the same convention every other
   *  unnamed-set reader in this app uses. A plain value, not a signal: the panel reads it once,
   *  right before opening the dialog — the very same read the panel freezes into the `frozenSetId`
   *  it later compares its live `setId()` input against in `startDelete`. That comparison, not this
   *  dialog changing under it, is what catches a set switch behind an open dialog: the panel aborts
   *  visibly (`abortedByLock`) when the two disagree at confirm time, whether the switch is still in
   *  progress (an active host lock) or has already settled (#200 K5 finding A). */
  setName: string;
  /** Whether `setName` is the channel's currently active 7TV set — gates the "this set is not
   *  currently active" addition (spec 8.8). */
  isActiveSet: boolean;
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
    <app-dialog-shell [dialogTitle]="titleKey() | transloco: { count: totalCount() }">
      <!-- Names the set right under the title (spec 8.8) — a duplicate cell (#74, two aliases,
           one REMOVE) is still exactly one entry in data.emotes() (mass-delete-panel.ts builds
           one DeletableEmote per cell), so it reads as one deletion here too, in no group of its
           own (8.9 is gone). -->
      <p class="text-sm text-fg-secondary">
        {{ 'massDelete.confirmSetLine' | transloco: { setName: data.setName } }}
      </p>
      @if (!data.isActiveSet) {
        <p class="text-sm text-fg-secondary">
          {{ 'massDelete.confirmSetNotActive' | transloco }}
        </p>
      }
      <app-name-preview-list [names]="data.emotes()" />

      <!-- Uncapped on purpose (Konzept "Auswahl überlebt Suche und Filter" 2.1): the 50-name cap
           above is fine because those names are on screen already, but a target the current
           filter is hiding must stay identifiable by name right up to this irreversible action —
           reducing it to a bare number here would be exactly the S2-16 safety gap this dialog
           exists to close.

           Deliberately a plain paragraph, not a live region. The dialog moves focus into itself
           and is read out as a whole on open, so there is nothing here for a role="status" to add:
           the block is created together with the dialog and could never announce its own arrival
           (docs/UI-Designsprache.md §4.5), and the one thing it could still fire on — the count
           changing under an open dialog — would read this sentence alone while the uncapped name
           list beside it and the total in the title changed silently, i.e. a fragment of the
           change. It would also make a second status region next to the amber
           "ownershipCheckUnavailable" banner in the same dialog, which §4.5 names as its own
           source of error. Its place in the reading order between the two lists is what carries
           it. -->
      @if (data.hiddenEmotes().length > 0) {
        <div class="flex flex-col gap-2">
          <p class="text-sm text-fg-secondary">
            {{ hiddenByFilterKey() | transloco: { count: data.hiddenEmotes().length } }}
          </p>
          <app-name-preview-list [names]="data.hiddenEmotes()" [cap]="null" />
        </div>
      }

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
           same rule as TypedConfirmDialog's retype hint, and it replaces the old loading line.
           Two reasons share the slot now; see confirmLockReasonKey for which one wins. -->
      @if (confirmLockReasonKey(); as reasonKey) {
        <p dialog-actions id="delete-confirm-hint" class="mr-auto text-xs text-fg-muted">
          {{ reasonKey | transloco }}
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
        [disabled]="confirmLockReasonKey() !== null"
        [attr.aria-describedby]="confirmLockReasonKey() !== null ? 'delete-confirm-hint' : null"
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

  // The title counts every marked emote, hidden ones included — only the body's two lists split
  // by visibility (Konzept "Auswahl überlebt Suche und Filter" 2.1).
  protected readonly totalCount = computed(
    () => this.data.emotes().length + this.data.hiddenEmotes().length,
  );

  protected readonly titleKey = computed(() =>
    pluralKey(this.totalCount(), 'massDelete.confirmTitle'),
  );

  protected readonly hiddenByFilterKey = computed(() =>
    pluralKey(this.data.hiddenEmotes().length, 'massDelete.hiddenByFilter'),
  );

  /**
   * Why the destructive button is locked, or `null` when nothing locks it — rendered into the
   * `#delete-confirm-hint` slot and pointed at by `aria-describedby`, because a disabled control
   * explains itself (docs/UI-Designsprache.md §10).
   *
   * The emptied selection is the newer of the two and takes precedence: the lists here are live
   * (see `DeleteConfirmDialogData`), so a pushed reload behind the open modal can prune the
   * selection to nothing, and this dialog would then offer an enabled red "start deleting" button
   * over the title "delete 0 emotes" and two empty lists. The panel refuses such a confirmation
   * anyway (`massDelete.selectionGoneDuringConfirm`), but the last screen before an irreversible
   * write must not invite a click it is going to reject — and between the two reasons, "there is
   * nothing left" is the final one while "still checking" is merely not-yet.
   */
  protected readonly confirmLockReasonKey = computed<string | null>(() => {
    if (this.totalCount() === 0) {
      return 'massDelete.confirmSelectionEmpty';
    }
    return this.data.warningLoading() ? 'massDelete.checkingSharedSets' : null;
  });

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
