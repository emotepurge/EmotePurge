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
  /** Whether `names`/`addCount` above are not guaranteed to be exact (operator decision 2026-09-25,
   *  #255; widened #255 P2, Codex review) — true in either of two cases: the open-time duplicate
   *  check's own 7TV read failed outright (`available: false` from `loadRestoreConfirmPreview`,
   *  which fails open, the same as `filterAlreadyPresentForRestore` itself), or it succeeded but
   *  only saw part of the target set (`available: true`, `complete: false` — the 10-page runaway
   *  guard, or a `totalCount` mismatch, see `SevenTvSetEntries.complete`). In both cases `names`/
   *  `addCount` still reflect real filtering against whatever the read did see — they are not the
   *  unfiltered input — the dialog just cannot vouch for them being the *whole* answer, so it
   *  phrases the count as an upper bound ("up to N") instead of claiming an exact number it does not
   *  have — never a smaller, possibly wrong one, and never silence about the uncertainty either. */
  countIsUpperBound: boolean;
  /** Live view of the set status, so the capacity line pops in once the check answers.
   *  null = unknown (no capacity reported) — then no projection line is shown at all. */
  slots: Signal<{ occupied: number; capacity: number } | null>;
  /** The set the run re-adds into (spec #200, 8.8) — falls back to the set id itself when unnamed,
   *  same convention as the delete confirmation's `setName`. */
  setName: string;
  /** Whether `setName` is the channel's currently active 7TV set — gates the "this set is not
   *  currently active" addition (spec 8.8), together with `trackedChannelName` below: an
   *  untracked target never shows that line, whatever this says (spec 4.3, point 6). */
  isActiveSet: boolean;
  /** The set's resolved 7TV id (spec E21) — shown so the confirmation names exactly the set the
   *  pre-check verified, never a value read straight from an unvalidated file. */
  emoteSetId: string;
  /** The set's owner, always shown (spec 4.3, point 6) — already carries its own display fallback
   *  (never blank), same convention as `EditableSetTarget.ownerDisplayName`. */
  ownerDisplayName: string;
  /** The target's tracked channel, or `null` for an untracked target (spec 6.1) — gates both the
   *  channel line and the "not currently active" line: neither is shown for an untracked target. */
  trackedChannelName: string | null;
  /** Whether the target is a different set than the one the host page has selected — including a
   *  different set of the *same* channel, and any page with no selected set at all (spec E21). Own
   *  hint line, shown after the "not active" line, before the emote names. */
  foreignToView: boolean;
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
      <!-- Line order is contract (spec 4.3, point 6; task brief): set, set id, owner, channel
           (tracked only), "not active" (tracked and not active only), foreign-to-view hint, then
           the emote names/projection/history note below. -->
      <p class="text-sm text-fg-secondary">
        {{ 'restore.confirmSetLine' | transloco: { setName: data.setName } }}
      </p>
      <p class="text-sm text-fg-secondary">
        {{ 'restore.confirmSetIdLine' | transloco: { emoteSetId: data.emoteSetId } }}
      </p>
      <p class="text-sm text-fg-secondary">
        {{ 'restore.confirmOwnerLine' | transloco: { ownerDisplayName: data.ownerDisplayName } }}
      </p>
      @if (data.trackedChannelName !== null) {
        <p class="text-sm text-fg-secondary">
          {{ 'restore.confirmChannelLine' | transloco: { channelName: data.trackedChannelName } }}
        </p>
      }
      @if (data.trackedChannelName !== null && !data.isActiveSet) {
        <p class="text-sm text-fg-secondary">
          {{ 'restore.confirmSetNotActive' | transloco }}
        </p>
      }
      @if (data.foreignToView) {
        <app-notice-banner variant="info">
          {{ 'restore.confirmForeignToView' | transloco }}
        </app-notice-banner>
      }
      <app-name-preview-list [names]="data.names" />

      @if (projection(); as slots) {
        @if (slots.overflow) {
          <app-notice-banner variant="warning">
            <span class="flex flex-col gap-1">
              <span>
                {{
                  capacityProjectionKey
                    | transloco: { projected: slots.projected, capacity: slots.capacity }
                }}
              </span>
              <span>{{ capacityWarningKey | transloco }}</span>
            </span>
          </app-notice-banner>
        } @else {
          <p class="text-sm text-fg-muted">
            {{
              capacityProjectionKey
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

  /** #255: the "up to N" family once the open-time check could not verify the count — never mixed
   *  with the plain family, so a translator can never see one language's title claim certainty the
   *  other one hedges. */
  protected readonly titleKey = pluralKey(
    this.data.names.length,
    this.data.countIsUpperBound ? 'restore.confirmTitleUpTo' : 'restore.confirmTitle',
  );

  /** Same hedge as `titleKey`, for the capacity line below — `data` never changes after the dialog
   *  is created, so a plain field is enough, same reasoning as `titleKey`. */
  protected readonly capacityProjectionKey = this.data.countIsUpperBound
    ? 'restore.capacityProjectionUpTo'
    : 'restore.capacityProjection';

  /** #255 P3(9): the overflow warning itself hedges the same way once the projected count is only
   *  an upper bound — "this exceeds capacity" is a claim the check never verified; "this could"
   *  is the honest one. */
  protected readonly capacityWarningKey = this.data.countIsUpperBound
    ? 'restore.capacityWarningUpTo'
    : 'restore.capacityWarning';

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
