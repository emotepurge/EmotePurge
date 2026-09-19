import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';

/** What gets exported: the visible (filtered + sorted) list, or the current grid selection. */
export type ExportScope = 'visible' | 'selection';

/**
 * One radio in the option group. `id` is opaque to the dialog — the caller is the one that
 * switches on it. `labelKey` renders as the first line, `hintKey` (when present) as a second,
 * quieter line beneath it, both inside the `<label>` and therefore both part of the accessible
 * name (§7.4 — house pattern, see "(Kanal muss erst beitreten)" in `import-target-dialog.ts`).
 */
export interface ExportDialogOption {
  readonly id: string;
  readonly labelKey: string;
  readonly hintKey?: string;
}

export interface ExportDialogData<TId extends string = string> {
  /** Rows of the visible list — the default export, so the count is part of the confirmation. */
  rowCount: number;
  /** True when the visible list is a filtered subset; renders the "filtered" hint line. */
  filtered: boolean;
  /**
   * Size of the page's grid selection — three states, each with exactly one dialog behaviour:
   *  - `null` — this caller has no grid-selection concept at all (the voting ballot already is
   *    the subset; the purge protocol is always the whole run). Neither the scope radiogroup nor
   *    the "no selection" hint renders.
   *  - `0` — a real, currently-empty selection. No radiogroup (a zero-row export is never a valid
   *    answer), but the hint explaining that the visible list will be used instead.
   *  - `> 0` — radiogroup shown, no hint.
   */
  selectionCount: number | null;
  /**
   * Pre-resolved Transloco keys explaining *why* columns will be missing (secret ballot,
   * manager-only usage). This dialog is the one place that explanation can live — the file itself
   * cannot carry it.
   */
  noticeKeys: readonly string[];
  /** Legend of the option radiogroup — 'export.formatLabel' for the two unchanged callers,
   *  'export.purposeLabel' for usage-stats' purpose-sorted list. */
  optionsLegendKey: string;
  /** Display order is the radio order; `options[0]` is the preselection — no second default
   *  concept lives anywhere else in this dialog. */
  options: readonly (ExportDialogOption & { id: TId })[];
}

export interface ExportChoice<TId extends string = string> {
  optionId: TId;
  scope: ExportScope;
}

/**
 * The CSV/JSON pair the dialog used to hardwire, now a caller-supplied constant so the wording
 * stays in one place for the two callers that keep it (voting export, delete protocol export).
 */
export const FORMAT_EXPORT_OPTIONS: readonly ExportDialogOption[] = [
  { id: 'csv', labelKey: 'export.formatCsv' },
  { id: 'json', labelKey: 'export.formatJson' },
];

/**
 * Closes with the chosen option id + scope, or `undefined` on cancel/Escape/backdrop.
 *
 * The option list used to be hardwired CSV/JSON. It is caller-supplied now (§7.4): the dialog
 * treats `data.options` as opaque and pre-selects `options[0]`, which keeps "CSV first" for the
 * two callers that still choose a format while letting usage-stats offer a purpose-sorted list
 * instead. It was a radio group before this change too, matching the scope choice directly above
 * it, and the footer states one action.
 */
@Component({
  selector: 'app-export-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'export.title' | transloco">
      @if (data.selectionCount !== null) {
        @if (data.selectionCount > 0) {
          <div
            class="flex flex-wrap gap-4 text-sm text-fg-secondary"
            role="radiogroup"
            [attr.aria-label]="'export.scopeLabel' | transloco"
          >
            <label class="flex items-center gap-2 py-1">
              <input
                type="radio"
                class="h-4 w-4 accent-accent-solid"
                name="export-scope"
                [disabled]="data.rowCount === 0"
                [checked]="scope() === 'visible'"
                (change)="scope.set('visible')"
              />
              {{ 'export.scopeVisible' | transloco: { count: data.rowCount } }}
            </label>
            <label class="flex items-center gap-2 py-1">
              <input
                type="radio"
                class="h-4 w-4 accent-accent-solid"
                name="export-scope"
                [checked]="scope() === 'selection'"
                (change)="scope.set('selection')"
              />
              {{ 'export.scopeSelection' | transloco: { count: data.selectionCount } }}
            </label>
          </div>
        } @else {
          <p class="text-xs text-fg-muted">{{ 'export.scopeNoSelectionHint' | transloco }}</p>
        }
      }

      <div
        class="flex flex-wrap gap-4 text-sm text-fg-secondary"
        role="radiogroup"
        [attr.aria-label]="data.optionsLegendKey | transloco"
      >
        @for (option of data.options; track option.id) {
          <label class="flex items-start gap-2 py-1">
            <input
              type="radio"
              class="h-4 w-4 accent-accent-solid"
              name="export-option"
              [checked]="optionId() === option.id"
              (change)="optionId.set(option.id)"
            />
            <span class="flex flex-col">
              <span>{{ option.labelKey | transloco }}</span>
              @if (option.hintKey) {
                <span class="text-xs text-fg-muted">{{ option.hintKey | transloco }}</span>
              }
            </span>
          </label>
        }
      </div>

      <div class="flex flex-col gap-1">
        <p class="text-sm text-fg-secondary">
          {{ rowCountKey() | transloco: { count: exportRowCount() } }}
        </p>
        @if (data.filtered && scope() === 'visible') {
          <p class="text-xs text-fg-muted">{{ 'export.filteredHint' | transloco }}</p>
        }
      </div>

      @for (noticeKey of data.noticeKeys; track noticeKey) {
        <app-notice-banner variant="info">{{ noticeKey | transloco }}</app-notice-banner>
      }

      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close()"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        [disabled]="exportRowCount() === 0"
        (click)="submit()"
      >
        {{ 'export.submit' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class ExportDialog {
  protected readonly data = inject<ExportDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ExportChoice | undefined>>(DialogRef);

  // Defaults to the visible list even when a selection exists: the selection also drives
  // mass-delete and vote-session creation, and an export must never silently narrow to it.
  // Exception (Konzept "Auswahl überlebt Suche und Filter" nachtrag, 2026-09-19): a filter can
  // leave the visible list empty while the selection survives it, and defaulting to an empty
  // scope would preselect a radio this dialog then has to disable on the very same render. The
  // rule stays "prefer visible" — this only breaks the tie when visible has nothing to offer.
  protected readonly scope = signal<ExportScope>(
    this.data.rowCount === 0 && this.data.selectionCount !== null && this.data.selectionCount > 0
      ? 'selection'
      : 'visible',
  );
  // options[0] is the preselection (E1) — no second default concept lives here. This keeps "CSV
  // first" for the two callers that pass FORMAT_EXPORT_OPTIONS without the dialog knowing what a
  // "format" is.
  protected readonly optionId = signal<string>(this.data.options[0].id);

  // Doubles as the submit button's lock (see the template): a scope resolving to zero rows would
  // write an empty file, and the "visible" radio being disabled at rowCount === 0 only stops that
  // scope from being *chosen* going forward — this also covers the state right after render, before
  // any click has happened, and the null-selectionCount branch this dialog already treats as
  // unreachable.
  protected readonly exportRowCount = computed(() => {
    if (this.scope() !== 'selection') {
      return this.data.rowCount;
    }
    const selectionCount = this.data.selectionCount;
    if (selectionCount === null) {
      // Unreachable: scope can only become 'selection' via a click inside the radiogroup, and
      // that group only ever renders (see the template) when data.selectionCount is a number
      // greater than 0 — never null. Narrowed honestly rather than cast or defaulted with `??`.
      return this.data.rowCount;
    }
    return selectionCount;
  });

  protected readonly rowCountKey = computed(() =>
    pluralKey(this.exportRowCount(), 'export.rowCount'),
  );

  protected submit(): void {
    this.dialogRef.close({ optionId: this.optionId(), scope: this.scope() });
  }
}

export function openExportDialog<TId extends string>(
  dialog: Dialog,
  data: ExportDialogData<TId>,
): DialogRef<ExportChoice<TId> | undefined> {
  return openAppDialog<ExportChoice<TId> | undefined, ExportDialogData<TId>>(dialog, ExportDialog, {
    data,
  });
}
