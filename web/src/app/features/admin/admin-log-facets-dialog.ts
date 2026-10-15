import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, Signal, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { LogSummary } from '../../core/admin/admin-logs.model';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { AdminLogFacets } from './admin-log-facets';

/**
 * Live handles into the page: the dialog edits the page's own state, so a facet click applies at once
 * (spec §9.1) and the counts behind the dialog follow the new filter. Signals and callbacks rather than
 * a snapshot for that reason. The saved-filter chips join this list when they exist.
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
}

/** The phone form of the filter bar's secondary controls: time range and facets (spec §9.3). */
@Component({
  selector: 'app-admin-log-facets-dialog',
  imports: [AdminLogFacets, Button, DialogShell, SegmentedControl, TranslocoPipe],
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
            <button type="button" appButton="outline" (click)="data.goBack()">
              {{ 'admin.logs.range.backTo' | transloco: { range: range } }}
            </button>
          }
        </div>
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
}

export function openLogFacetsDialog(dialog: Dialog, data: LogFacetsDialogData): DialogRef<void> {
  return openAppDialog<void, LogFacetsDialogData>(dialog, AdminLogFacetsDialog, { data });
}
