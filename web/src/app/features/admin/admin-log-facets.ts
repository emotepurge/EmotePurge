import { Component, computed, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { LogCategoryFacetValue, LogLevelKey, LogSummary } from '../../core/admin/admin-logs.model';
import { filterTerm } from '../../core/admin/log-filter-text';

/**
 * What a category facet puts into the filter: the short name when no other facet value shares it,
 * otherwise the full name — `cat:SevenTvEventClient` is a suffix match and would also hit a sibling
 * with the same last segment (spec §9.2).
 */
export function categoryFilterValue(
  facet: LogCategoryFacetValue,
  all: readonly LogCategoryFacetValue[],
): string {
  const sharing = all.filter((other) => other.short === facet.short).length;
  return sharing === 1 ? facet.short : facet.value;
}

/**
 * The facet column of `/admin/logs`: source, level and category with their counts. Every value has two
 * actions — the value itself adds `key:value`, a small button next to it adds `-key:value`. Facets only
 * ever append terms (spec §7.4); the page decides what to do with the term.
 */
@Component({
  selector: 'app-admin-log-facets',
  imports: [TranslocoPipe],
  template: `
    <div class="flex flex-col gap-5" data-testid="log-facets">
      @for (group of groups(); track group.key) {
        @if (group.values.length > 0) {
          <section class="flex flex-col gap-1">
            <h3 class="text-xs font-medium tracking-wide text-fg-muted uppercase">
              {{ 'admin.logs.facets.' + group.key | transloco }}
            </h3>
            <!-- 44 px targets in the phone sheet, 36 px in the desktop column (spec §9.3). -->
            <ul class="flex flex-col">
              @for (item of group.values; track item.filterValue) {
                <li class="flex items-center gap-1">
                  <button
                    type="button"
                    class="flex min-h-11 min-w-0 flex-1 items-center justify-between gap-2 rounded px-2 text-left text-sm text-fg-body hover:bg-surface-inset lg:min-h-9"
                    [title]="item.title"
                    [attr.aria-label]="
                      'admin.logs.facets.include'
                        | transloco: { value: item.label + ' (' + item.count + ')' }
                    "
                    (click)="termAdded.emit(item.include)"
                  >
                    <span class="truncate">{{ item.label }}</span>
                    <span class="shrink-0 text-xs text-fg-muted tabular-nums">{{
                      item.count
                    }}</span>
                  </button>
                  <button
                    type="button"
                    class="flex min-h-11 min-w-11 items-center justify-center rounded text-sm text-fg-muted hover:bg-surface-inset hover:text-fg-body lg:min-h-9 lg:min-w-9"
                    [attr.aria-label]="
                      'admin.logs.facets.exclude' | transloco: { value: item.label }
                    "
                    (click)="termAdded.emit(item.exclude)"
                  >
                    <span aria-hidden="true">−</span>
                  </button>
                </li>
              }
            </ul>
          </section>
        }
      }
      @if (summary().facets.categoryOther > 0) {
        <p class="px-2 text-xs text-fg-muted">
          {{ 'admin.logs.facets.other' | transloco: { count: summary().facets.categoryOther } }}
        </p>
      }
    </div>
  `,
})
export class AdminLogFacets {
  readonly summary = input.required<LogSummary>();

  /** A complete filter term (`key:value` or `-key:value`) the reader asked to append. */
  readonly termAdded = output<string>();

  protected readonly groups = computed(() => {
    const { facets } = this.summary();
    const toItem = (
      key: string,
      filterValue: string,
      label: string,
      count: number,
      title = '',
    ) => ({
      filterValue,
      label,
      count,
      title: title || label,
      include: filterTerm(key, filterValue, false),
      exclude: filterTerm(key, filterValue, true),
    });
    return [
      {
        key: 'source',
        values: facets.source.map((f) => toItem('source', f.value, f.value, f.count)),
      },
      {
        key: 'level',
        values: facets.level.map((f) => toItem('level', f.value, levelLabel(f.value), f.count)),
      },
      {
        key: 'category',
        values: facets.category.map((f) =>
          toItem('cat', categoryFilterValue(f, facets.category), f.short, f.count, f.value),
        ),
      },
    ];
  });
}

/** Level names are the log levels' own (English) names in both languages, like the log content. */
export const LOG_LEVEL_NAMES: Record<LogLevelKey, string> = {
  info: 'Information',
  warn: 'Warning',
  error: 'Error',
  crit: 'Critical',
};

function levelLabel(level: LogLevelKey): string {
  return LOG_LEVEL_NAMES[level];
}
