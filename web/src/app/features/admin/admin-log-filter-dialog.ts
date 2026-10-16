import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import {
  LogFilterApiError,
  SavedLogFilter,
  isLogFilterApiError,
} from '../../core/admin/admin-logs.model';
import { AdminLogsService } from '../../core/admin/admin-logs.service';
import { LOG_FILTER_NAME_MAX_LENGTH, filterErrorSelection } from '../../core/admin/log-filter-text';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';

export interface LogFilterDialogData {
  /** The filter being edited, or `null` for "save the current filter as a new one". */
  filter: SavedLogFilter | null;
  /** Prefill of the filter field for a new filter (the applied `q`). Ignored when editing. */
  initialFilter: string;
}

/** Where a failed save shows up: on a field, or on the dialog as a whole. */
export type LogFilterSaveError =
  | { place: 'name'; messageKey: string }
  | { place: 'filter'; error: LogFilterApiError }
  | { place: 'dialog'; messageKey: string };

/**
 * Maps a failed create/update to its place (spec §9.5): a taken or invalid name belongs to the name
 * field, an invalid filter to the filter field (with the position the server named), and everything
 * else — the 30-filter limit, a filter that has vanished meanwhile, a network error — to the dialog.
 */
export function mapLogFilterSaveError(error: HttpErrorResponse): LogFilterSaveError {
  const body = error.error as { errorCode?: string } | null;
  const code = body?.errorCode;
  if (code === 'log_filter_name_taken' || code === 'invalid_log_filter_name') {
    return { place: 'name', messageKey: apiErrorTranslationKey(error) };
  }
  if (isLogFilterApiError(error.error)) {
    return { place: 'filter', error: error.error };
  }
  return { place: 'dialog', messageKey: apiErrorTranslationKey(error) };
}

/** Why the save button is locked, as a translation key, or `null` when it is not. */
export function logFilterSaveBlockedKey(name: string, filter: string): string | null {
  if (filter.trim() === '') {
    return 'admin.logs.savedFilters.blockedEmptyFilter';
  }
  if (name.trim() === '') {
    return 'admin.logs.savedFilters.blockedEmptyName';
  }
  return null;
}

/**
 * Create or edit a saved log filter: a name and the filter string (spec §9.5). The dialog saves
 * itself — a server refusal has to land on the right field and the dialog must stay open — and
 * closes with the stored filter.
 */
@Component({
  selector: 'app-admin-log-filter-dialog',
  imports: [Button, DialogShell, TranslocoPipe],
  template: `
    <app-dialog-shell
      [dialogTitle]="
        (data.filter ? 'admin.logs.savedFilters.editTitle' : 'admin.logs.savedFilters.newTitle')
          | transloco
      "
    >
      <div class="flex flex-col gap-1">
        <label for="log-filter-name" class="text-xs font-medium text-fg-muted">{{
          'admin.logs.savedFilters.nameLabel' | transloco
        }}</label>
        <input
          id="log-filter-name"
          type="text"
          class="app-input"
          autocomplete="off"
          [attr.maxlength]="nameMaxLength"
          [value]="name()"
          (input)="onName($event)"
          [attr.aria-invalid]="nameError() ? 'true' : null"
          [attr.aria-describedby]="nameError() ? 'log-filter-name-error' : null"
        />
        @if (nameError(); as key) {
          <p id="log-filter-name-error" class="text-sm text-danger-fg">{{ key | transloco }}</p>
        }
      </div>
      <div class="flex flex-col gap-1">
        <label for="log-filter-filter" class="text-xs font-medium text-fg-muted">{{
          'admin.logs.savedFilters.filterLabel' | transloco
        }}</label>
        <input
          #filterInput
          id="log-filter-filter"
          type="text"
          class="app-input font-mono"
          autocomplete="off"
          spellcheck="false"
          [value]="filter()"
          (input)="onFilter($event)"
          [attr.aria-invalid]="filterError() ? 'true' : null"
          [attr.aria-describedby]="filterError() ? 'log-filter-filter-error' : null"
        />
        @if (filterError(); as error) {
          <p id="log-filter-filter-error" class="text-sm text-danger-fg">
            {{ 'admin.logs.filterErrors.' + error.reason | transloco }}
          </p>
        }
      </div>
      @if (dialogError(); as key) {
        <p class="text-sm text-danger-fg" role="alert">{{ key | transloco }}</p>
      }

      @if (blockedKey(); as key) {
        <p dialog-actions id="log-filter-blocked-hint" class="mr-auto text-xs text-fg-muted">
          {{ key | transloco }}
        </p>
      }
      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="ref.close(undefined)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        [disabled]="saving() || blockedKey() !== null"
        [attr.aria-describedby]="blockedKey() !== null ? 'log-filter-blocked-hint' : null"
        (click)="save()"
      >
        {{ 'admin.logs.savedFilters.save' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class AdminLogFilterDialog {
  protected readonly data = inject<LogFilterDialogData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<SavedLogFilter | undefined>>(DialogRef);
  private readonly service = inject(AdminLogsService);

  private readonly filterInput = viewChild<ElementRef<HTMLInputElement>>('filterInput');

  protected readonly nameMaxLength = LOG_FILTER_NAME_MAX_LENGTH;
  protected readonly name = signal(this.data.filter?.name ?? '');
  protected readonly filter = signal(this.data.filter?.filter ?? this.data.initialFilter);
  protected readonly saving = signal(false);
  protected readonly nameError = signal<string | null>(null);
  protected readonly filterError = signal<LogFilterApiError | null>(null);
  protected readonly dialogError = signal<string | null>(null);

  protected readonly blockedKey = computed(() =>
    logFilterSaveBlockedKey(this.name(), this.filter()),
  );

  constructor() {
    // Marks the offending spot of a refused filter, like the page's own filter field.
    afterRenderEffect(() => {
      const error = this.filterError();
      const input = this.filterInput()?.nativeElement;
      if (!error || !input) {
        return;
      }
      const { start, end } = filterErrorSelection(error);
      input.focus();
      input.setSelectionRange(start, end);
    });
  }

  protected onName(event: Event): void {
    this.name.set((event.target as HTMLInputElement).value);
    this.nameError.set(null);
    this.dialogError.set(null);
  }

  protected onFilter(event: Event): void {
    this.filter.set((event.target as HTMLInputElement).value);
    this.filterError.set(null);
    this.dialogError.set(null);
  }

  protected save(): void {
    if (untracked(this.blockedKey) !== null || this.saving()) {
      return;
    }
    const name = this.name().trim();
    const filter = this.filter();
    const existing = this.data.filter;
    this.saving.set(true);
    this.nameError.set(null);
    this.filterError.set(null);
    this.dialogError.set(null);
    const request = existing
      ? this.service.updateFilter(existing.id, name, filter)
      : this.service.createFilter(name, filter);
    request.subscribe({
      next: (saved) => this.ref.close(saved),
      error: (error: HttpErrorResponse) => {
        this.saving.set(false);
        const mapped = mapLogFilterSaveError(error);
        switch (mapped.place) {
          case 'name':
            this.nameError.set(mapped.messageKey);
            break;
          case 'filter':
            this.filterError.set(mapped.error);
            break;
          case 'dialog':
            this.dialogError.set(mapped.messageKey);
            break;
        }
      },
    });
  }
}

export function openLogFilterDialog(
  dialog: Dialog,
  data: LogFilterDialogData,
): DialogRef<SavedLogFilter | undefined> {
  return openAppDialog<SavedLogFilter | undefined, LogFilterDialogData>(
    dialog,
    AdminLogFilterDialog,
    { data },
  );
}
