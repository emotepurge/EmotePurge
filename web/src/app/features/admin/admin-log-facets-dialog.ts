import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import {
  Component,
  ElementRef,
  Injector,
  Signal,
  afterNextRender,
  effect,
  inject,
  untracked,
} from '@angular/core';
import { take } from 'rxjs';
import { TranslocoPipe } from '@jsverse/transloco';

import { LogSummary, SavedLogFilter } from '../../core/admin/admin-logs.model';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { AdminLogFacets } from './admin-log-facets';
import { AdminLogFilterChips } from './admin-log-filter-chips';

/**
 * Live handles into the page: the dialog edits the page's own state, so a facet click applies at once
 * (spec §9.1) and the counts behind the dialog follow the new filter. Signals and callbacks rather than
 * a snapshot for that reason. The saved-filter chips stay on the page in both layouts: their create and
 * edit dialogs must not open out of this one (one dialog at a time, design language §7).
 */
export interface LogFacetsDialogData {
  rangeOptions: SegmentedControlOption[];
  rangeValue: Signal<string>;
  /** "Back to 24 h" label parameters, or null when there is nothing to go back to. */
  backRange: Signal<string | null>;
  summary: Signal<LogSummary | null>;
  setRange(value: string): void;
  goBack(): void;
  addTerm(term: string): void;
  /** The saved-filter bar lives in this sheet on a phone (spec §9.3). Applying a chip closes the sheet;
   *  every other action closes it first and runs only afterwards, so dialogs never stack. */
  savedFilters: Signal<readonly SavedLogFilter[]>;
  currentQ: Signal<string>;
  savedFiltersFailed: Signal<boolean>;
  selectFilter(filter: SavedLogFilter): void;
  saveCurrent(): void;
  editFilter(filter: SavedLogFilter): void;
  overwriteFilter(filter: SavedLogFilter): void;
  deleteFilter(filter: SavedLogFilter): void;
  retryFilters(): void;
}

/** The phone form of the filter bar's secondary controls: time range and facets (spec §9.3). */
@Component({
  selector: 'app-admin-log-facets-dialog',
  imports: [
    AdminLogFacets,
    AdminLogFilterChips,
    Button,
    DialogShell,
    SegmentedControl,
    TranslocoPipe,
  ],
  template: `
    <app-dialog-shell [dialogTitle]="'admin.logs.filter.dialogTitle' | transloco">
      <div class="flex flex-col gap-4">
        <div class="flex flex-col gap-2">
          <app-segmented-control
            size="lg"
            [options]="data.rangeOptions"
            [value]="data.rangeValue()"
            (valueChange)="data.setRange($event)"
            [ariaLabel]="'admin.logs.range.label' | transloco"
          />
          @if (data.backRange(); as range) {
            <button
              type="button"
              appButton="outline"
              buttonSize="lg"
              data-focus-handoff="back"
              (click)="data.goBack()"
            >
              {{ 'admin.logs.range.backTo' | transloco: { range: range } }}
            </button>
          }
        </div>
        <app-admin-log-filter-chips
          [filters]="data.savedFilters()"
          [currentQ]="data.currentQ()"
          [loadFailed]="data.savedFiltersFailed()"
          (selected)="closeThen(() => data.selectFilter($event))"
          (saveRequested)="closeThen(() => data.saveCurrent())"
          (editRequested)="closeThen(() => data.editFilter($event))"
          (overwriteRequested)="closeThen(() => data.overwriteFilter($event))"
          (deleteRequested)="closeThen(() => data.deleteFilter($event))"
          (retryRequested)="data.retryFilters()"
        />
        @if (data.summary(); as summary) {
          <app-admin-log-facets [summary]="summary" (termAdded)="data.addTerm($event)" />
        }
      </div>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        (click)="ref.close()"
      >
        {{ 'admin.logs.filter.done' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class AdminLogFacetsDialog {
  protected readonly data = inject<LogFacetsDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<void>>(DialogRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  constructor() {
    // "Zurück zu 24 h" vanishes with its own click; the focus goes to the range it restored instead
    // of falling out of the sheet. Runs before the view drops the button, while it still has focus.
    effect(() => {
      if (this.data.backRange() !== null) {
        return;
      }
      untracked(() => {
        const active = this.host.nativeElement.ownerDocument.activeElement;
        if (
          active?.closest('[data-focus-handoff="back"]') &&
          this.host.nativeElement.contains(active)
        ) {
          afterNextRender(
            () =>
              this.host.nativeElement
                .querySelector<HTMLElement>('app-segmented-control [aria-checked="true"]')
                ?.focus(),
            { injector: this.injector },
          );
        }
      });
    });
  }

  /** Closes the sheet, then runs `action` once it is gone — the next dialog must not open over it. */
  protected closeThen(action: () => void): void {
    this.ref.closed.pipe(take(1)).subscribe(action);
    this.ref.close();
  }
}

export function openLogFacetsDialog(dialog: Dialog, data: LogFacetsDialogData): DialogRef<void> {
  return openAppDialog<void, LogFacetsDialogData>(dialog, AdminLogFacetsDialog, { data });
}
