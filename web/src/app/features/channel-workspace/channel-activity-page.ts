import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AuditLogEntry } from '../../core/audit/audit.model';
import { ChannelAuditService } from '../../core/channels/channel-audit.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { PagedResult } from '../../core/models/paged-result.model';
import { listQueryState } from '../../core/routing/list-query-state';
import { ACTION_KEYS, CHANNEL_SCOPED_ACTIONS } from '../../shared/audit/audit-actions';
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
 * The channel's own activity feed: who joined or left, who created, ended or deleted a vote session,
 * who reported emotes as deleted, who triggered a resync. Same rows as the global-admin log, scoped
 * to one channel — and only for its management team, because the rows name people.
 *
 * Read-only by construction: entries are written by the services that perform the actions, and
 * nothing here can create, edit or delete one.
 */
@Component({
  selector: 'app-channel-activity-page',
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
           (shell header h-14 + workspace tabs h-10, §8.5) so it does not land behind them. -->
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 #resultsTop tabindex="-1" class="scroll-mt-24 text-lg font-semibold">
          {{ 'channelWorkspace.activity.title' | transloco }}
        </h2>
        <button
          type="button"
          appButton="outline"
          [disabled]="isLoading()"
          (click)="reload()"
          [title]="'channelWorkspace.activity.refreshTitle' | transloco"
        >
          {{ 'channelWorkspace.activity.refresh' | transloco }}
        </button>
      </header>

      <!-- Sticky below the shell header (top-14) + the workspace tabs (h-10): top-24, the same
           contract the admin log follows. py-2 gives the blur a surface instead of a hard edge. -->
      <div class="app-sticky-bar top-24 flex flex-col gap-3 py-2">
        <app-segmented-control
          [options]="actionOptions"
          [value]="actionFilter()"
          (valueChange)="onActionFilterChange($event)"
          [ariaLabel]="'channelWorkspace.activity.filter.actionLabel' | transloco"
        />
        <!-- Filter-toolbar field: no visible label by design, aria-label + title instead (§5.2). -->
        <div class="flex flex-wrap items-center gap-2">
          <input
            type="text"
            class="app-input-sm w-44"
            [value]="actorFilter()"
            (input)="onActorFilterInput($event)"
            [placeholder]="'channelWorkspace.activity.filter.actorPlaceholder' | transloco"
            [attr.aria-label]="'channelWorkspace.activity.filter.actorLabel' | transloco"
            [title]="'channelWorkspace.activity.filter.actorLabel' | transloco"
          />
          @if (hasActiveFilters()) {
            <button type="button" appButton="outline" (click)="resetFilters()">
              {{ 'channelWorkspace.activity.filter.reset' | transloco }}
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
            [title]="'channelWorkspace.activity.filter.noMatches' | transloco"
            [description]="'channelWorkspace.activity.filter.noMatchesHint' | transloco"
          >
            <button type="button" appButton="outline" (click)="resetFilters()">
              {{ 'channelWorkspace.activity.filter.reset' | transloco }}
            </button>
          </app-empty-state>
        } @else {
          <app-empty-state
            [title]="'channelWorkspace.activity.empty' | transloco"
            [description]="'channelWorkspace.activity.emptyHint' | transloco"
          />
        }
      } @else {
        <!-- showChannel off: every row here carries this channel, and the link would point at the
             page the reader is already on. -->
        <app-audit-log-list [rows]="rows()" [showChannel]="false" />

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
export class ChannelActivityPage {
  readonly channelName = input.required<string>();

  private readonly channelAuditService = inject(ChannelAuditService);
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);

  // Page *and* both filters live in the URL, same as the global admin log: a restored "page 3"
  // without the filter that made page 3 mean anything is worse than useless (core/routing).
  private readonly query = listQueryState({ action: '', actor: '' });

  protected readonly page = this.query.page;

  protected readonly actionFilter = computed(() => this.query.params().action);
  protected readonly actorFilter = this.query.textFilter('actor', FILTER_DEBOUNCE_MS);

  /**
   * "All" plus one segment per action a channel's log can actually contain — built from
   * `CHANNEL_SCOPED_ACTIONS`, not from the full table: the two user-scoped actions are global and
   * would be segments that can only ever return nothing.
   */
  protected readonly actionOptions: SegmentedControlOption[] = [
    { value: '', labelKey: 'channelWorkspace.activity.filter.all' },
    ...CHANNEL_SCOPED_ACTIONS.map((value) => ({ value, labelKey: ACTION_KEYS[value] })),
  ];

  protected readonly hasActiveFilters = computed(
    () => this.actionFilter() !== '' || this.actorFilter() !== '',
  );

  // Reading the required channelName() input inside `params` rather than in the constructor is what
  // keeps this off NG0950: rxResource evaluates it lazily, after the route inputs are applied.
  private readonly auditLogResource = rxResource({
    params: () => ({
      channel: this.channelName(),
      page: this.page(),
      ...this.query.params(),
    }),
    stream: ({ params }) =>
      this.channelAuditService.listAuditLog(params.channel, params.page, PAGE_SIZE, {
        action: params.action || undefined,
        actor: params.actor || undefined,
      }),
    defaultValue: EMPTY_PAGE,
  });

  protected readonly isLoading = computed(() => this.auditLogResource.isLoading());
  protected readonly totalPages = computed(() => this.auditLogResource.value().totalPages);

  // Reads `lang()` so a language switch re-formats the timestamps: LOCALE_ID is fixed at bootstrap
  // and cannot follow one.
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
    this.query.setParams({ action });
  }

  protected onActorFilterInput(event: Event): void {
    this.actorFilter.set((event.target as HTMLInputElement).value);
  }

  protected resetFilters(): void {
    this.query.setParams({ action: '', actor: '' });
  }
}
