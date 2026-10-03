import { Component, computed, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';

/** Beyond this the list stops being readable and starts being a wall — the tail is counted instead.
 *  Exported so a caller that formats the same kind of "these names" list as a single line of text
 *  instead of a scrollable `<ul>` (`MassDeletePanel`'s missing-row abort reason, #227 P2-c) reuses
 *  the identical threshold rather than picking its own. */
export const PREVIEW_CAP = 50;

/**
 * The list of names a dialog is about to act on (delete, restore).
 *
 * Both confirm dialogs had built this themselves, down to the same cap, the same overflow line and
 * the same translation key — but not the same data contract, so neither could use the other's.
 *
 * Ruled rows rather than the old `space-y-1`: this is the one list inside the dialog family, and the
 * design language's list recipe (§2.1) is what makes fifty names scannable. Full-bleed (`-mx-6`
 * against the shell's `p-6`) so the rules read as bands across the dialog, not as a boxed table.
 */
@Component({
  selector: 'app-name-preview-list',
  imports: [TranslocoPipe],
  template: `
    <ul
      class="-mx-6 max-h-48 divide-y divide-border overflow-y-auto border-y border-border text-sm"
    >
      @for (name of preview(); track name) {
        <li class="px-6 py-1.5 text-fg-secondary">{{ name }}</li>
      }
      @if (remaining() > 0) {
        <li class="px-6 py-1.5 text-fg-muted">
          {{ andMoreKey() | transloco: { count: remaining() } }}
        </li>
      }
    </ul>
  `,
})
export class NamePreviewList {
  readonly names = input.required<readonly string[]>();

  /** `null` shows every name — the delete-confirm dialog's hidden-by-filter block (Konzept
   *  "Auswahl überlebt Suche und Filter" 2.1) is the reason: those names are the safety-relevant
   *  part of that dialog and must never fall behind the "n weitere" tail before an irreversible
   *  action. The `-mx-6 max-h-48 overflow-y-auto` wrapper already scrolls rather than growing the
   *  dialog, so lifting the cap does not need any layout change of its own. */
  readonly cap = input<number | null>(PREVIEW_CAP);

  protected readonly preview = computed(() => {
    const cap = this.cap();
    return cap === null ? this.names() : this.names().slice(0, cap);
  });
  protected readonly remaining = computed(() => this.names().length - this.preview().length);
  protected readonly andMoreKey = computed(() => pluralKey(this.remaining(), 'common.andMore'));
}
