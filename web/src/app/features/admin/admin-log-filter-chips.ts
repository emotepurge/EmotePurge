import { Component, ElementRef, computed, inject, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { SavedLogFilter } from '../../core/admin/admin-logs.model';
import { normalizeFilterWhitespace } from '../../core/admin/log-filter-text';
import { Button } from '../../shared/ui/button';
import { Popover } from '../../shared/ui/popover';

/** A chip is active when its filter equals the applied `q` after whitespace normalization (spec §9.5). */
export function isLogFilterChipActive(chipFilter: string, q: string): boolean {
  return normalizeFilterWhitespace(chipFilter) === normalizeFilterWhitespace(q);
}

/** Why saving or overwriting with the current filter is not possible, or `null` when it is. */
export function currentFilterBlockedKey(q: string): string | null {
  return q.trim() === '' ? 'admin.logs.savedFilters.blockedEmptyFilter' : null;
}

/**
 * The saved-filter bar of `/admin/logs` (spec §9.5): one chip per filter, each with a small menu, then
 * "+ Filter speichern". Purely presentational — the page owns the data and the server calls.
 */
@Component({
  selector: 'app-admin-log-filter-chips',
  imports: [Button, Popover, TranslocoPipe],
  template: `
    <!-- Every button here is a 44 px target below lg (spec §9.3), where the bar lives in the Filter
         sheet; the desktop bar keeps the dense size. -->
    <div
      class="flex flex-wrap items-center gap-2"
      role="group"
      [attr.aria-label]="'admin.logs.savedFilters.label' | transloco"
    >
      @for (item of filters(); track item.id) {
        <span class="relative inline-flex items-center gap-1" data-popover-anchor>
          <button
            type="button"
            appButton="outline"
            class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
            [buttonPressed]="isActive(item)"
            [attr.aria-pressed]="isActive(item)"
            [attr.title]="item.filter"
            (click)="selected.emit(item)"
          >
            {{ item.name }}
          </button>
          <button
            type="button"
            appButton="outline"
            class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
            [attr.data-chip-menu]="item.id"
            aria-haspopup="dialog"
            [attr.aria-expanded]="openMenuId() === item.id"
            [attr.aria-label]="'admin.logs.savedFilters.menuFor' | transloco: { name: item.name }"
            (click)="toggleMenu(item.id)"
          >
            <span aria-hidden="true">⋯</span>
          </button>
          @if (openMenuId() === item.id) {
            <app-popover
              width="w-64"
              [ariaLabel]="'admin.logs.savedFilters.menuFor' | transloco: { name: item.name }"
              (closed)="closeMenu(true)"
            >
              <div class="flex flex-col py-1">
                <button
                  type="button"
                  [class]="rowClass + ' text-fg-body hover:bg-surface-inset'"
                  (click)="act(editRequested, item)"
                >
                  {{ 'admin.logs.savedFilters.edit' | transloco }}
                </button>
                <button
                  type="button"
                  [class]="rowClass + ' text-fg-body hover:bg-surface-inset'"
                  [disabled]="blockedKey() !== null"
                  (click)="act(overwriteRequested, item)"
                >
                  {{ 'admin.logs.savedFilters.overwrite' | transloco }}
                </button>
                @if (blockedKey(); as key) {
                  <p class="px-3 pb-1 text-xs text-fg-muted">{{ key | transloco }}</p>
                }
                <button
                  type="button"
                  [class]="rowClass + ' text-danger-fg hover:bg-danger-wash'"
                  (click)="act(deleteRequested, item)"
                >
                  {{ 'admin.logs.savedFilters.delete' | transloco }}
                </button>
              </div>
            </app-popover>
          }
        </span>
      }
      <button
        type="button"
        appButton="outline"
        class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
        [disabled]="blockedKey() !== null"
        [attr.aria-describedby]="blockedKey() !== null ? 'log-save-blocked-hint' : null"
        (click)="saveRequested.emit()"
      >
        {{ 'admin.logs.savedFilters.add' | transloco }}
      </button>
      @if (blockedKey(); as key) {
        <span id="log-save-blocked-hint" class="text-xs text-fg-muted">{{ key | transloco }}</span>
      }
      @if (loadFailed()) {
        <span class="flex items-center gap-2 text-sm text-danger-fg" role="alert">
          {{ 'admin.logs.savedFilters.loadFailed' | transloco }}
          <button
            type="button"
            appButton="outline"
            class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
            (click)="retryRequested.emit()"
          >
            {{ 'admin.logs.states.retry' | transloco }}
          </button>
        </span>
      }
    </div>
  `,
})
export class AdminLogFilterChips {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly filters = input.required<readonly SavedLogFilter[]>();
  /** The applied filter: decides which chip is active and whether "save" is possible. */
  readonly currentQ = input.required<string>();
  readonly loadFailed = input(false);

  readonly selected = output<SavedLogFilter>();
  readonly saveRequested = output<void>();
  readonly editRequested = output<SavedLogFilter>();
  readonly overwriteRequested = output<SavedLogFilter>();
  readonly deleteRequested = output<SavedLogFilter>();
  readonly retryRequested = output<void>();

  protected readonly rowClass =
    'flex min-h-11 w-full items-center px-3 text-left text-sm transition disabled:text-fg-disabled disabled:hover:bg-transparent lg:min-h-9';
  protected readonly openMenuId = signal<number | null>(null);
  protected readonly blockedKey = computed(() => currentFilterBlockedKey(this.currentQ()));

  protected isActive(item: SavedLogFilter): boolean {
    return isLogFilterChipActive(item.filter, this.currentQ());
  }

  protected toggleMenu(id: number): void {
    this.openMenuId.update((open) => (open === id ? null : id));
  }

  protected closeMenu(returnFocus: boolean): void {
    const id = this.openMenuId();
    this.openMenuId.set(null);
    if (returnFocus && id !== null) {
      this.host.nativeElement.querySelector<HTMLElement>(`[data-chip-menu="${id}"]`)?.focus();
    }
  }

  protected act(target: { emit(value: SavedLogFilter): void }, item: SavedLogFilter): void {
    this.closeMenu(false);
    target.emit(item);
  }
}
