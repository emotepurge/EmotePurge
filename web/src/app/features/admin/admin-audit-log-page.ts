import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AdminService } from '../../core/admin/admin.service';
import { AuditLogEntry } from '../../core/audit/audit.model';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { PagedResult } from '../../core/models/paged-result.model';
import { listQueryState } from '../../core/routing/list-query-state';
import { ACTION_KEYS, CHANNELLESS_ACTIONS } from '../../shared/audit/audit-actions';
import { AuditLogList } from '../../shared/audit/audit-log-list';
import { toAuditRows } from '../../shared/audit/audit-row';
import { Pager } from '../../shared/pagination/pager';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';

const PAGE_SIZE = 25;

/** Long enough to swallow keystrokes of one word, short enough to still feel live. */
const FILTER_DEBOUNCE_MS = 300;

const EMPTY_PAGE: PagedResult<AuditLogEntry> = {
  items: [],
  page: 1,
  pageSize: PAGE_SIZE,
  totalCount: 0,
  totalPages: 0,
};

/**
 * The audit log: who did what, when, to which channel. Read-only by construction — entries are
 * written by the services that perform the actions, inside those actions' own transactions, and
 * nothing in the UI can create, edit or delete one.
 */
@Component({
  selector: 'app-admin-audit-log-page',
  imports: [
    AuditLogList,
    Button,
    EmptyState,
    NoticeBanner,
    Pager,
    SegmentedControl,
    SkeletonRows,
    TranslocoPipe,
  ],
  template: `
    <div class="flex flex-col gap-4">
      <!-- The heading doubles as the pager's scroll/focus target (§8.4): tabindex="-1" lets it take
           focus without becoming a tab stop, scroll-mt-24 clears the two sticky layers above it
           (shell header h-14 + admin tabs h-10, §8.5) so it does not land behind them. -->
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 #resultsTop tabindex="-1" class="scroll-mt-24 text-lg font-semibold">
          {{ 'admin.audit.title' | transloco }}
        </h2>
        <button
          type="button"
          appButton="outline"
          [disabled]="isLoading()"
          (click)="reload()"
          [title]="'admin.audit.refreshTitle' | transloco"
        >
          {{ 'admin.audit.refresh' | transloco }}
        </button>
      </header>

      <!-- Sticky below header (top-14) + admin tabs (h-10): top-24. py-2 gives the blur a surface
           instead of a hard edge against the rows scrolling through underneath (design doc §8.5). -->
      <div class="app-sticky-bar top-24 flex flex-col gap-3 py-2">
        <app-segmented-control
          [options]="actionOptions"
          [value]="actionFilter()"
          (valueChange)="onActionFilterChange($event)"
          [ariaLabel]="'admin.audit.filter.actionLabel' | transloco"
        />
        <!-- Filter-toolbar fields: no visible label by design, aria-label + title instead (§5.2).
             Live filtering per keystroke like the emote grid; requests are debounced in the class. -->
        <div class="flex flex-wrap items-center gap-2">
          <input
            type="text"
            class="app-input-sm w-44 disabled:cursor-not-allowed disabled:opacity-50"
            [value]="channelFilter()"
            (input)="onChannelFilterInput($event)"
            [disabled]="channelFilterDisabled()"
            [placeholder]="'admin.audit.filter.channelPlaceholder' | transloco"
            [attr.aria-label]="'admin.audit.filter.channelLabel' | transloco"
            [title]="
              (channelFilterDisabled()
                ? 'admin.audit.filter.channelNotApplicable'
                : 'admin.audit.filter.channelLabel'
              ) | transloco
            "
          />
          <input
            type="text"
            class="app-input-sm w-44"
            [value]="actorFilter()"
            (input)="onActorFilterInput($event)"
            [placeholder]="'admin.audit.filter.actorPlaceholder' | transloco"
            [attr.aria-label]="'admin.audit.filter.actorLabel' | transloco"
            [title]="'admin.audit.filter.actorLabel' | transloco"
          />
          @if (hasActiveFilters()) {
            <button type="button" appButton="outline" (click)="resetFilters()">
              {{ 'admin.audit.filter.reset' | transloco }}
            </button>
          }
        </div>
      </div>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      }

      @if (isLoading()) {
        <app-skeleton-rows [count]="5" />
      } @else if (rows().length === 0) {
        @if (hasActiveFilters()) {
          <app-empty-state
            [title]="'admin.audit.filter.noMatches' | transloco"
            [description]="'admin.audit.filter.noMatchesHint' | transloco"
          >
            <button type="button" appButton="outline" (click)="resetFilters()">
              {{ 'admin.audit.filter.reset' | transloco }}
            </button>
          </app-empty-state>
        } @else {
          <app-empty-state
            [title]="'admin.audit.empty' | transloco"
            [description]="'admin.audit.emptyHint' | transloco"
          />
        }
      } @else {
        <app-audit-log-list [rows]="rows()" />

        <app-pager
          [page]="page()"
          [totalPages]="totalPages()"
          [scrollTarget]="resultsTop"
          (pageChange)="onPageChange($event)"
        />
      }
    </div>
  `,
})
export class AdminAuditLogPage {
  private readonly adminService = inject(AdminService);
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);

  // Page *and* all three filters live in the URL: a restored "page 3" without the filter that made
  // page 3 mean anything is worse than useless (core/routing/list-query-state.ts).
  private readonly query = listQueryState({ action: '', channel: '', actor: '' });

  protected readonly page = this.query.page;

  // The action segments are clicks, so they go straight into the URL. The two text filters keep a
  // local draft updated per keystroke — same feel as the emote grid's filter — and reach the URL
  // only once the typing settles; the URL value is then also what the request reads.
  protected readonly actionFilter = computed(() => this.query.params().action);
  protected readonly channelFilter = this.query.textFilter('channel', FILTER_DEBOUNCE_MS);
  protected readonly actorFilter = this.query.textFilter('actor', FILTER_DEBOUNCE_MS);

  /** True while an action without a channel dimension is selected — the channel input is disabled
   *  then, and a previously typed channel value has already been cleared by the action handler. */
  protected readonly channelFilterDisabled = computed(() =>
    CHANNELLESS_ACTIONS.has(this.actionFilter()),
  );

  /** "All" plus one segment per known action, reusing the row labels — the filter can only ever
   *  offer what this build can name. */
  protected readonly actionOptions: SegmentedControlOption[] = [
    { value: '', labelKey: 'admin.audit.filter.all' },
    ...Object.entries(ACTION_KEYS).map(([value, labelKey]) => ({ value, labelKey })),
  ];

  protected readonly hasActiveFilters = computed(
    () => this.actionFilter() !== '' || this.channelFilter() !== '' || this.actorFilter() !== '',
  );

  // Same rxResource shape as vote-session-list-page.ts: a navigation that moves `page` or a filter is
  // the whole reload trigger, no hand-written effect. Reading the URL rather than the individual
  // signals is what dropped the old "short-circuit the stale channel" workaround — see
  // onActionFilterChange.
  private readonly auditLogResource = rxResource({
    params: () => ({
      page: this.page(),
      ...this.query.params(),
    }),
    stream: ({ params }) =>
      this.adminService.listAuditLog(params.page, PAGE_SIZE, {
        action: params.action || undefined,
        channel: params.channel || undefined,
        actor: params.actor || undefined,
      }),
    defaultValue: EMPTY_PAGE,
  });

  protected readonly isLoading = computed(() => this.auditLogResource.isLoading());
  protected readonly totalPages = computed(() => this.auditLogResource.value().totalPages);

  // Reads `lang()` so a language switch re-formats the timestamps: LOCALE_ID is fixed at bootstrap
  // and cannot follow one (same reasoning as the other two admin pages).
  protected readonly rows = computed(() =>
    toAuditRows(this.auditLogResource.value().items, toLocale(this.languageService.lang()), (key) =>
      this.translocoService.translate(key),
    ),
  );

  protected readonly errorMessage = computed(() => {
    const error = this.auditLogResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  protected reload(): void {
    this.auditLogResource.reload();
  }

  protected onPageChange(newPage: number): void {
    this.query.goToPage(newPage);
  }

  // The jump back to page 1 is no longer repeated per handler — `setParams` does it for every filter
  // change, for the same reason as before: the old page number belongs to the old result set.
  protected onActionFilterChange(action: string): void {
    // Action and channel move in *one* call. A stale channel combined with a channel-less action
    // could only ever match nothing; clearing it here is what makes disabling the input honest, and
    // `setParams` clears the input's pending draft along with the URL — a channel typed inside the
    // debounce window would otherwise come back 300 ms later.
    this.query.setParams(CHANNELLESS_ACTIONS.has(action) ? { action, channel: '' } : { action });
  }

  protected onChannelFilterInput(event: Event): void {
    this.channelFilter.set((event.target as HTMLInputElement).value);
  }

  protected onActorFilterInput(event: Event): void {
    this.actorFilter.set((event.target as HTMLInputElement).value);
  }

  protected resetFilters(): void {
    this.query.setParams({ action: '', channel: '', actor: '' });
  }
}
