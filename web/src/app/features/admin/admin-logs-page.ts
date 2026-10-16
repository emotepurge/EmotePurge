import { Dialog } from '@angular/cdk/dialog';
import { BreakpointObserver } from '@angular/cdk/layout';
import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  afterRenderEffect,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import {
  EMPTY,
  Observable,
  Subscription,
  catchError,
  concat,
  finalize,
  map,
  skip,
  switchMap,
  tap,
  toArray,
} from 'rxjs';

import {
  LogEntry,
  LogFilterApiError,
  LogPage,
  LogRangePreset,
  LogRangeQuery,
  LogStatus,
  LogSummary,
  SavedLogFilter,
  isLogCaptureComplete,
  isLogFilterApiError,
} from '../../core/admin/admin-logs.model';
import { AdminLogsService } from '../../core/admin/admin-logs.service';
import {
  appendFilterTerm,
  filterErrorSelection,
  normalizeFilterWhitespace,
} from '../../core/admin/log-filter-text';
import {
  LogReconcileScheduler,
  mergeLogEntries,
  planReconcile,
  preflightReconcile,
  reconcileFromUtc,
} from '../../core/admin/log-reconcile';
import { apiErrorCode, apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { pluralKey } from '../../core/i18n/plural';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { LiveStatus, LiveUpdateService } from '../../core/live/live-update.service';
import { pollWhileVisible } from '../../core/live/poll-while-visible';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import { listQueryState } from '../../core/routing/list-query-state';
import { TimeHistogram } from '../../shared/charts/time-histogram';
import { Button } from '../../shared/ui/button';
import { openConfirmDialog } from '../../shared/ui/confirm-dialog';
import { EmptyState } from '../../shared/ui/empty-state';
import { HealthMarker, HealthTone } from '../../shared/ui/health-marker';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { Popover } from '../../shared/ui/popover';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { AdminLogFacets } from './admin-log-facets';
import { AdminLogFilterChips } from './admin-log-filter-chips';
import { mapLogFilterSaveError, openLogFilterDialog } from './admin-log-filter-dialog';
import { openLogFacetsDialog } from './admin-log-facets-dialog';
import { AdminLogList, formatLogLine } from './admin-log-list';

/** The loaded list stops here and asks for a narrower range or filter (spec §9.2, A25). */
export const LOG_LIST_LIMIT = 2000;
/** "Sichtbare kopieren" copies at most this many loaded rows. */
export const LOG_COPY_LIMIT = 500;
/** The server's filter-length limit; a longer one is not sent (spec §7.1). */
export const LOG_FILTER_MAX_LENGTH = 512;

const PRESETS: readonly LogRangePreset[] = ['1h', '6h', '24h', '7d'];
const DEFAULT_PRESET: LogRangePreset = '24h';
const DESKTOP_QUERY = '(min-width: 64rem)';
const COPY_FEEDBACK_MS = 2500;

/** The periodic reconcile; the hint, the tab's return and the stream's (re)opening only make it sooner
 *  (spec §8.3, A36). */
const LOG_RECONCILE_INTERVAL_MS = 30_000;
/** The summary (histogram, facets) reloads at most this often in live mode (spec §8.3). */
const SUMMARY_MIN_INTERVAL_MS = 30_000;
/** How long an inserted row stays marked (spec §9.4). */
const HIGHLIGHT_MS = 2000;

/** What the live status marker in the filter bar says: the stream's own state, why live is off, or
 *  that a source has stopped capturing — then "Live" would claim a completeness the list lacks. */
type LiveState = LiveStatus | 'paused' | 'reload' | 'absolute' | 'capture';

const LIVE_TONES: Record<LiveState, HealthTone> = {
  idle: 'idle',
  connecting: 'idle',
  open: 'ok',
  closed: 'danger',
  paused: 'warning',
  reload: 'warning',
  absolute: 'idle',
  capture: 'warning',
};

/**
 * "Oben" (spec §9.4): the first row is in view below the sticky bars **and** no row is unfolded. Only
 * then may new entries go into the list; otherwise they wait behind "n neue". An empty list counts as
 * the top — there is nothing to push away. All numbers are viewport pixels (`getBoundingClientRect`).
 */
export function isLogListAtTop(
  firstRowTop: number | null,
  stickyBottom: number,
  viewportHeight: number,
  expanded: boolean,
): boolean {
  if (expanded) {
    return false;
  }
  if (firstRowTop === null) {
    return true;
  }
  // One pixel of slack for sub-pixel layout where the row meets the bar's edge.
  return firstRowTop >= stickyBottom - 1 && firstRowTop < viewportHeight;
}

/** What the `range`/`from`/`to` URL parameters say. An empty `range` means the default preset. */
export type ParsedLogRange =
  | { kind: 'preset'; range: LogRangePreset }
  | { kind: 'absolute'; from: string; to: string }
  | { kind: 'invalid' };

/**
 * Reads the three range parameters. Anything the server would reject as a mixed or malformed request is
 * `invalid` here, so a broken deep link shows a notice and sends nothing instead of looping on a `400`.
 * The span and future limits stay the server's call — its `invalid_log_range` shows as a notice too.
 */
export function parseLogRange(range: string, from: string, to: string): ParsedLogRange {
  const hasAbsolute = from !== '' || to !== '';
  if (!hasAbsolute) {
    if (range === '') {
      return { kind: 'preset', range: DEFAULT_PRESET };
    }
    return (PRESETS as readonly string[]).includes(range)
      ? { kind: 'preset', range: range as LogRangePreset }
      : { kind: 'invalid' };
  }
  if (range !== '' || from === '' || to === '') {
    return { kind: 'invalid' };
  }
  const fromMs = Date.parse(from);
  const toMs = Date.parse(to);
  return Number.isFinite(fromMs) && Number.isFinite(toMs) && fromMs < toMs
    ? { kind: 'absolute', from, to }
    : { kind: 'invalid' };
}

interface LogRequest {
  q: string;
  range: LogRangeQuery;
}

interface Banner {
  key: string;
  params: Record<string, string | number>;
}

const PRESET_SECONDS: Record<LogRangePreset, number> = {
  '1h': 3600,
  '6h': 6 * 3600,
  '24h': 86400,
  '7d': 7 * 86400,
};

/** The bounds a request itself names (absolute) or resolves to now (relative), for answers without any. */
function boundsOf(range: LogRangeQuery): { from: string; to: string } | null {
  if ('from' in range) {
    return { from: range.from, to: range.to };
  }
  const to = Date.now();
  return {
    from: new Date(to - PRESET_SECONDS[range.range] * 1000).toISOString(),
    to: new Date(to).toISOString(),
  };
}

/** `yyyy-MM-dd HH:mm` of an ISO instant, cut from the string (it is UTC already). */
function utcMinute(instant: string): string {
  return instant.slice(0, 16).replace('T', ' ');
}

/**
 * The bounds of an absolute range as its label shows them, in UTC: to the minute, or to the second
 * when the range is shorter than a minute or a bound falls between two minutes — a narrowed 10 s
 * bucket would otherwise read "11:59 bis 11:59".
 */
export function absoluteRangeLabelBounds(from: string, to: string): { from: string; to: string } {
  const fromMs = Date.parse(from);
  const toMs = Date.parse(to);
  const byMinute = toMs - fromMs >= 60_000 && fromMs % 60_000 === 0 && toMs % 60_000 === 0;
  const format = (ms: number) =>
    new Date(ms)
      .toISOString()
      .slice(0, byMinute ? 16 : 19)
      .replace('T', ' ');
  return { from: format(fromMs), to: format(toMs) };
}

/**
 * `/admin/logs`: filter bar, time histogram, facets and the log list, with every state the spec names
 * (§9.7). The URL (`?q=&range=` or `?q=&from=&to=`) is the source of truth for query and range; the
 * filter applies on Enter or "Anwenden", facet and bar clicks apply at once.
 *
 * Seams for what comes next: {@link applyQuery} is the one way a new filter enters (saved-filter chips),
 * {@link entries} and {@link list} expose what is loaded and whether a row is unfolded, and
 * {@link pendingNew} feeds the reserved "n neue" slot.
 *
 * Live mode (spec §8.3, §9.4), relative ranges only: a `logs.appended` hint, the tab's return, the admin
 * stream's (re)opening and a 30 s tick each ask {@link LogReconcileScheduler} for a reconcile — one at a
 * time, 5 s apart. A reconcile compares the server's keys of the loaded interval with what is loaded and
 * fetches the missing entries by id. At the top they go into the list (highlighted, one announcement);
 * otherwise they wait behind "n neue". Any failed automatic fetch pauses live mode until a load the
 * reader asked for succeeds; a key list the client cannot reconcile asks for a reload instead.
 */
@Component({
  selector: 'app-admin-logs-page',
  imports: [
    AdminLogFacets,
    AdminLogFilterChips,
    AdminLogList,
    Button,
    EmptyState,
    HealthMarker,
    NoticeBanner,
    Popover,
    SegmentedControl,
    SkeletonRows,
    TimeHistogram,
    TranslocoPipe,
  ],
  template: `
    <div class="flex flex-col gap-4">
      <!-- One polite message per insert ("3 neue Einträge"), never one per row (spec §9.4). -->
      <p class="sr-only" role="status" aria-live="polite" data-testid="log-live-announcement">
        {{ announcement() }}
      </p>
      <!-- Sticky below header (top-14) + admin tabs (h-10): top-24 (design doc §8.5). -->
      <div #stickyBar class="app-sticky-bar top-24 flex flex-col gap-2 py-2">
        <form class="flex flex-wrap items-end gap-2" (submit)="onSubmit($event)">
          <div class="flex min-w-0 flex-1 basis-64 flex-col gap-1">
            <label for="admin-log-filter" class="text-xs font-medium text-fg-muted">{{
              'admin.logs.filter.label' | transloco
            }}</label>
            <input
              #filterInput
              id="admin-log-filter"
              type="text"
              class="app-input-sm w-full font-mono"
              autocomplete="off"
              spellcheck="false"
              [value]="draft()"
              (input)="onDraftInput($event)"
              [placeholder]="'admin.logs.filter.placeholder' | transloco"
              [attr.aria-invalid]="filterError() ? 'true' : null"
              [attr.aria-describedby]="filterError() ? 'admin-log-filter-error' : null"
            />
          </div>
          <!-- 44 px targets below lg (spec §9.3); the desktop keeps the dense toolbar size. -->
          <button type="submit" appButton="primary" class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0">
            {{ 'admin.logs.filter.apply' | transloco }}
          </button>
          <div class="relative" data-popover-anchor>
            <button
              type="button"
              appButton="outline"
              class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
              [attr.aria-expanded]="helpOpen()"
              [attr.aria-label]="'admin.logs.filter.help' | transloco"
              (click)="helpOpen.set(!helpOpen())"
            >
              <span aria-hidden="true">?</span>
            </button>
            @if (helpOpen()) {
              <app-popover
                width="w-96"
                align="end"
                [ariaLabel]="'admin.logs.filter.help' | transloco"
                (closed)="helpOpen.set(false)"
              >
                <div class="flex flex-col gap-2 p-4 text-xs text-fg-body">
                  <p>{{ 'admin.logs.help.intro' | transloco }}</p>
                  <h3 class="font-medium text-fg-secondary">
                    {{ 'admin.logs.help.keysTitle' | transloco }}
                  </h3>
                  <ul class="flex flex-col gap-0.5">
                    @for (key of helpKeys; track key) {
                      <li>{{ 'admin.logs.help.keys.' + key | transloco }}</li>
                    }
                  </ul>
                  <h3 class="font-medium text-fg-secondary">
                    {{ 'admin.logs.help.examplesTitle' | transloco }}
                  </h3>
                  <ul class="flex flex-col gap-2">
                    @for (example of helpExamples; track example.id) {
                      <li>
                        <code class="font-mono break-words">{{ example.filter }}</code>
                        <span class="block text-fg-muted">{{
                          'admin.logs.help.examples.' + example.id | transloco
                        }}</span>
                      </li>
                    }
                  </ul>
                </div>
              </app-popover>
            }
          </div>
          @if (!isDesktop()) {
            <button type="button" appButton="outline" buttonSize="lg" (click)="openFilterDialog()">
              {{ 'admin.logs.filter.open' | transloco }}
            </button>
          }
        </form>
        @if (filterError(); as error) {
          <p id="admin-log-filter-error" class="text-sm text-danger-fg">
            {{ 'admin.logs.filterErrors.' + error.reason | transloco }}
          </p>
        }

        <div class="flex flex-wrap items-center gap-2">
          @if (isDesktop()) {
            <app-segmented-control
              [options]="rangeOptions"
              [value]="rangeValue()"
              (valueChange)="setRange($event)"
              [ariaLabel]="'admin.logs.range.label' | transloco"
            />
          }
          @if (absoluteText(); as text) {
            <span class="text-xs text-fg-muted tabular-nums">{{ text }}</span>
          }
          @if (backRange(); as range) {
            <button
              type="button"
              appButton="outline"
              class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
              data-focus-handoff="back"
              (click)="goBack()"
            >
              {{ 'admin.logs.range.backTo' | transloco: { range: range } }}
            </button>
          }
          <!-- The live line never wraps and never changes size with the live state: the marker may
               truncate, and the one action slot is reserved at a fixed width. Below lg it is a line of
               its own (the Filter button sits in the form row), so nothing else can push it over. -->
          <div
            class="flex min-w-0 basis-full flex-nowrap items-center justify-between gap-3 lg:ml-auto lg:basis-auto lg:justify-end"
            data-testid="log-live-line"
          >
            <div class="min-w-0 overflow-hidden whitespace-nowrap" [title]="liveLabel()">
              <app-health-marker [tone]="liveTone()" [label]="liveLabel()" />
            </div>
            <!-- One slot for "n neue ↑" and, while the stream is closed, "Neu laden" (spec §9.4); held
                 rows win, since inserting them is a fresh look as well. Reserved even when empty. -->
            <span class="w-28 shrink-0 text-right" data-testid="log-new-slot">
              <button
                type="button"
                class="inline-flex min-h-11 items-center px-2 text-sm text-accent-fg underline-offset-2 hover:underline lg:min-h-8"
                [class.invisible]="slotAction() === null"
                [attr.aria-hidden]="slotAction() === null ? 'true' : null"
                [tabindex]="slotAction() === null ? -1 : 0"
                data-focus-handoff="slot"
                [attr.aria-label]="
                  slotAction() === 'reload'
                    ? null
                    : (pendingAriaKey() | transloco: { count: pendingNew() })
                "
                (click)="onSlot()"
              >
                @if (slotAction() === 'reload') {
                  {{ 'admin.logs.states.reload' | transloco }}
                } @else {
                  {{ 'admin.logs.newEntries.label' | transloco: { count: pendingNew() } }}
                }
              </button>
            </span>
          </div>
        </div>
      </div>

      @if (isDesktop()) {
        <app-admin-log-filter-chips
          [filters]="savedFilters() ?? []"
          [currentQ]="appliedQ()"
          [loadFailed]="savedFiltersFailed()"
          (selected)="applyQuery({ q: $event.filter })"
          (saveRequested)="saveCurrentFilter()"
          (editRequested)="editSavedFilter($event)"
          (overwriteRequested)="overwriteSavedFilter($event)"
          (deleteRequested)="deleteSavedFilter($event)"
          (retryRequested)="loadSavedFilters()"
        />
      }
      @if (savedFilterError(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }

      @if (rangeInvalid()) {
        <app-notice-banner variant="error">{{
          'admin.logs.range.invalid' | transloco
        }}</app-notice-banner>
      }

      @if (summary(); as data) {
        <app-time-histogram
          [bucketStartsUtc]="data.bucketStartsUtc"
          [info]="data.counts.info"
          [warn]="data.counts.warn"
          [error]="data.counts.error"
          [bucketSeconds]="data.bucketSeconds"
          [processStarts]="histogramStarts()"
          [lastBucketPartial]="isRelative()"
          [compact]="!isDesktop()"
          [label]="'admin.logs.histogramLabel' | transloco"
          (bucketSelected)="selectBucket($event.fromUtc, $event.toUtc)"
        />
      } @else if (summaryLoading()) {
        <!-- As tall as the histogram it stands in for (spec §9.7): on the desktop caption, 128 px plot,
             reading line and legend, compact the 32 px strip and the reading line, each with the 8 px
             gaps of its space-y-2 stack — the sr-only text alternative closes that stack, so the last
             visible block keeps a gap too (208 px, 64 px). The E2E "first load" cases measure both. -->
        <div
          class="app-skeleton w-full"
          [class.h-52]="isDesktop()"
          [class.h-16]="!isDesktop()"
          role="status"
          data-testid="log-histogram-skeleton"
          [attr.aria-label]="'common.loading' | transloco"
        ></div>
      }

      <div class="grid gap-6 lg:grid-cols-[14rem_minmax(0,1fr)]">
        @if (isDesktop()) {
          <aside class="min-w-0" [attr.aria-label]="'admin.logs.facets.title' | transloco">
            @if (summary(); as data) {
              <app-admin-log-facets [summary]="data" (termAdded)="addTerm($event)" />
            } @else if (summaryLoading()) {
              <app-skeleton-rows [count]="4" />
            }
          </aside>
        }

        <section class="flex min-w-0 flex-col gap-3" [attr.aria-busy]="listLoading()">
          @if (queryErrorKey(); as key) {
            <app-notice-banner variant="error">
              <span>{{ key | transloco }}</span>
              @if (hasStaleData()) {
                <span> {{ 'admin.logs.states.stale' | transloco }}</span>
              }
              <button
                notice-action
                type="button"
                appButton="outline"
                class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
                (click)="reload()"
              >
                {{ 'admin.logs.states.retry' | transloco }}
              </button>
            </app-notice-banner>
          }
          @if (paused()) {
            <app-notice-banner variant="warning">
              <span>{{ 'admin.logs.live.pausedNotice' | transloco }}</span>
              <button
                notice-action
                type="button"
                appButton="outline"
                class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
                (click)="reload()"
              >
                {{ 'admin.logs.states.retry' | transloco }}
              </button>
            </app-notice-banner>
          }
          @if (tooManyNew()) {
            <app-notice-banner variant="info">
              <span>{{ 'admin.logs.live.tooMany' | transloco }}</span>
              <button
                notice-action
                type="button"
                appButton="outline"
                class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
                (click)="reload()"
              >
                {{ 'admin.logs.live.reloadList' | transloco }}
              </button>
            </app-notice-banner>
          }
          @if (dropBanner(); as drop) {
            <app-notice-banner variant="warning">
              {{ drop }}
              <button
                notice-action
                type="button"
                appButton="outline"
                class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
                (click)="applyQuery({ q: 'cat:LogCapture' })"
              >
                {{ 'admin.logs.states.dropsShow' | transloco }}
              </button>
            </app-notice-banner>
          }
          @for (banner of captureBanners(); track $index) {
            <app-notice-banner variant="warning">{{
              'admin.logs.states.' + banner.key | transloco: banner.params
            }}</app-notice-banner>
          }

          @if (entries(); as rows) {
            @if (rows.length === 0) {
              @if (!listLoading() && !queryErrorKey()) {
                <app-empty-state [title]="'admin.logs.states.empty' | transloco">
                  @if (canWiden()) {
                    <button
                      type="button"
                      appButton="outline"
                      data-focus-handoff="widen"
                      (click)="setRange('7d')"
                    >
                      {{ 'admin.logs.range.extend' | transloco }}
                    </button>
                  }
                </app-empty-state>
              }
            } @else {
              <div class="flex flex-wrap items-center justify-between gap-2">
                <p class="text-xs text-fg-muted tabular-nums">
                  @if (loadedTotal(); as total) {
                    {{ 'admin.logs.list.loaded' | transloco: { shown: rows.length, total: total } }}
                  } @else {
                    {{ 'admin.logs.list.loadedCount' | transloco: { shown: rows.length } }}
                  }
                </p>
                <div class="flex items-center gap-2">
                  @if (copyFeedback(); as feedback) {
                    <span class="text-xs text-fg-muted" role="status">{{ feedback }}</span>
                  }
                  <button
                    type="button"
                    appButton="outline"
                    class="min-h-11 min-w-11 lg:min-h-0 lg:min-w-0"
                    (click)="copyVisible()"
                  >
                    {{ 'admin.logs.list.copyVisible' | transloco }}
                  </button>
                </div>
              </div>
              <app-admin-log-list
                [entries]="rows"
                [highlighted]="highlighted()"
                [processStarts]="summary()?.processStarts ?? []"
                (termAdded)="addTerm($event)"
              />
              @if (moreError()) {
                <p class="flex flex-wrap items-center gap-3 text-sm text-danger-fg" role="alert">
                  {{ 'admin.logs.list.loadMoreFailed' | transloco }}
                  <button type="button" appButton="outline" (click)="loadMore()">
                    {{ 'admin.logs.states.retry' | transloco }}
                  </button>
                </p>
              }
              @if (moreLoading()) {
                <p class="text-xs text-fg-muted" role="status">
                  {{ 'admin.logs.list.loadingMore' | transloco }}
                </p>
              }
              @if (limitReached()) {
                <p class="text-sm text-fg-secondary" data-testid="log-limit">
                  {{ 'admin.logs.list.limit' | transloco: { count: rows.length } }}
                </p>
              } @else if (cursor()) {
                <div #sentinel class="h-px" aria-hidden="true"></div>
              } @else {
                <p class="text-xs text-fg-muted">{{ 'admin.logs.list.end' | transloco }}</p>
              }
            }
          } @else if (listLoading()) {
            <app-skeleton-rows [count]="8" />
          }
        </section>
      </div>
    </div>
  `,
})
export class AdminLogsPage {
  private readonly service = inject(AdminLogsService);
  private readonly dialog = inject(Dialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly languageService = inject(LanguageService);
  private readonly transloco = inject(TranslocoService);
  private readonly breakpoints = inject(BreakpointObserver);
  private readonly liveUpdate = inject(LiveUpdateService);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly query = listQueryState({ q: '', range: '', from: '', to: '' });

  private readonly filterInput = viewChild<ElementRef<HTMLInputElement>>('filterInput');
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');
  private readonly stickyBar = viewChild<ElementRef<HTMLElement>>('stickyBar');
  protected readonly list = viewChild(AdminLogList);

  protected readonly helpKeys = [
    'level',
    'source',
    'cat',
    'event',
    'instance',
    'uptime',
    'has',
    'other',
    'free',
  ];
  protected readonly helpExamples = [
    { id: 'one', filter: 'level>=warn cat:TwitchChatManager' },
    { id: 'two', filter: 'source:worker has:exception -channel:handofblood' },
    { id: 'three', filter: '"Fehler:"' },
  ];

  protected readonly rangeOptions: SegmentedControlOption[] = PRESETS.map((value) => ({
    value,
    labelKey: `admin.logs.range.${value}`,
  }));

  protected readonly isDesktop = toSignal(
    this.breakpoints.observe(DESKTOP_QUERY).pipe(map((state) => state.matches)),
    { initialValue: this.breakpoints.isMatched(DESKTOP_QUERY) },
  );

  // --- URL state ------------------------------------------------------------------------------

  /** The applied filter (what the URL says). */
  protected readonly appliedQ = computed(() => this.query.params().q);
  /** What is typed in the field; reaches the URL only on Enter or "Anwenden" (A23). */
  protected readonly draft = signal(this.query.params().q);

  private readonly parsedRange = computed(() => {
    const { range, from, to } = this.query.params();
    return parseLogRange(range, from, to);
  });
  protected readonly rangeInvalid = computed(() => this.parsedRange().kind === 'invalid');
  protected readonly isRelative = computed(() => this.parsedRange().kind === 'preset');
  protected readonly rangeValue = computed(() => {
    const parsed = this.parsedRange();
    return parsed.kind === 'preset' ? parsed.range : '';
  });
  protected readonly absoluteText = computed(() => {
    const parsed = this.parsedRange();
    if (parsed.kind !== 'absolute') {
      return null;
    }
    this.languageService.lang();
    return this.transloco.translate(
      'admin.logs.range.absolute',
      absoluteRangeLabelBounds(parsed.from, parsed.to),
    );
  });

  /** The relative preset a bucket click narrowed away from, for "Zurück zu 24 h". */
  private readonly returnRange = signal<LogRangePreset | null>(null);
  protected readonly backRange = computed(() => {
    const preset = this.returnRange();
    if (!preset || this.parsedRange().kind !== 'absolute') {
      return null;
    }
    this.languageService.lang();
    return this.transloco.translate(`admin.logs.range.${preset}`);
  });

  /** What to load, or null when the URL cannot be queried (broken range, filter too long). */
  private readonly request = computed<LogRequest | null>(() => {
    const parsed = this.parsedRange();
    const q = this.appliedQ();
    if (parsed.kind === 'invalid' || q.length > LOG_FILTER_MAX_LENGTH) {
      return null;
    }
    return {
      q,
      range:
        parsed.kind === 'preset' ? { range: parsed.range } : { from: parsed.from, to: parsed.to },
    };
  });

  // --- Loaded state ---------------------------------------------------------------------------

  /** Loaded rows, newest first; `null` until the first page (or after an invalid filter). */
  readonly entries = signal<LogEntry[] | null>(null);
  protected readonly summary = signal<LogSummary | null>(null);
  protected readonly status = signal<LogStatus | null>(null);
  protected readonly cursor = signal<string | null>(null);
  /** The exact bounds of the first page; paging sends them back as an absolute range (R4). */
  private frozen: { from: string; to: string } | null = null;

  protected readonly listLoading = signal(false);
  protected readonly summaryLoading = signal(false);
  protected readonly moreLoading = signal(false);
  /** The last "load more" failed; the loaded rows are fresh, only the next page is missing. */
  protected readonly moreError = signal(false);
  protected readonly queryError = signal<HttpErrorResponse | null>(null);
  private readonly serverFilterError = signal<LogFilterApiError | null>(null);

  /** The saved filters in the server's order; `null` until the first answer. */
  protected readonly savedFilters = signal<SavedLogFilter[] | null>(null);
  protected readonly savedFiltersFailed = signal(false);
  /** Translation key of the last failed overwrite or delete, shown as a banner. */
  protected readonly savedFilterError = signal<string | null>(null);

  /** New entries the live mode holds back while the reader is not at the top (spec §9.4). */
  private readonly held = signal<LogEntry[]>([]);
  /** How many entries wait behind the reserved "n neue" slot. */
  readonly pendingNew = computed(() => this.held().length);
  /** Rows inserted by the live mode in the last two seconds. */
  protected readonly highlighted = signal<ReadonlySet<number>>(new Set());
  /** The polite live-region text, one message per insert. */
  protected readonly announcement = signal('');
  /** A failed automatic fetch stopped every automatic fetch until a load the reader asked for succeeds. */
  protected readonly paused = signal(false);
  /** The loaded interval cannot be reconciled any more (too many new entries, span beyond 8 days). */
  protected readonly tooManyNew = signal(false);

  private readonly visible = pageVisible();
  private readonly scheduler = new LogReconcileScheduler(
    () => Date.now(),
    () => this.reconcile(),
  );
  private readonly highlightTimers = new Set<ReturnType<typeof setTimeout>>();

  protected readonly liveState = computed<LiveState>(() => {
    if (this.parsedRange().kind === 'absolute') {
      return 'absolute';
    }
    if (this.paused()) {
      return 'paused';
    }
    // No reconcile runs while the hint asks for a reload, so the marker must not claim "Live".
    if (this.tooManyNew()) {
      return 'reload';
    }
    const stream = this.liveUpdate.status();
    // The stream may be fine while a source writes nothing: its banner explains, the marker must
    // not say "Live" over it (spec §9.7).
    const capturing = this.status()?.sources.every((source) => source.state === 'healthy') ?? true;
    return stream === 'open' && !capturing ? 'capture' : stream;
  });
  protected readonly liveTone = computed(() => LIVE_TONES[this.liveState()]);
  protected readonly liveLabel = computed(() => {
    this.languageService.lang();
    return this.transloco.translate(`admin.logs.live.${this.liveState()}`);
  });
  /** What the one reserved slot offers: held rows first, else "Neu laden" for a closed stream. */
  protected readonly slotAction = computed<'pending' | 'reload' | null>(() => {
    if (this.pendingNew() > 0) {
      return 'pending';
    }
    return this.liveState() === 'closed' ? 'reload' : null;
  });
  protected readonly pendingAriaKey = computed(() =>
    pluralKey(this.pendingNew(), 'admin.logs.newEntries.aria'),
  );

  protected readonly copyFeedback = signal<string | null>(null);
  protected readonly helpOpen = signal(false);

  private readonly reloadTick = signal(0);
  private pageSub?: Subscription;
  private summarySub?: Subscription;
  /**
   * The current load's first page, held back until its summary and the capture status have answered
   * too (either way): the histogram, the drop banner and the capture banners then arrive together with
   * the rows instead of pushing rendered rows down (spec §9.7, B8 "no jumps").
   */
  private pendingPage: { page: LogPage; range: LogRangeQuery } | null = null;
  private summarySettled = true;
  private statusSettled = true;
  private moreSub?: Subscription;
  private statusSub?: Subscription;
  private overwriting = false;
  private copyTimer?: ReturnType<typeof setTimeout>;
  private reconcileSub?: Subscription;
  private lastSummaryAtMs = Number.NEGATIVE_INFINITY;
  /** Rows went into the list since the summary last loaded, so its counts are behind. */
  private summaryBehind = false;
  /** The summary answers the current request (it may be the previous one's, kept as stale). */
  private readonly summaryCurrent = signal(false);
  /** Live rows inserted since the summary was counted. */
  private readonly insertedSinceSummary = signal(0);

  /** "n von total": the summary's total plus what live mode inserted since; `null` when the summary
   *  shown belongs to an earlier request and its total would describe a different filter. */
  protected readonly loadedTotal = computed(() => {
    const summary = this.summary();
    if (!summary || !this.summaryCurrent()) {
      return null;
    }
    return Math.max(this.entries()?.length ?? 0, summary.total + this.insertedSinceSummary());
  });

  protected readonly clientFilterError = computed<LogFilterApiError | null>(() => {
    const q = this.appliedQ();
    return q.length > LOG_FILTER_MAX_LENGTH
      ? {
          errorCode: 'invalid_log_filter',
          reason: 'too_long',
          position: LOG_FILTER_MAX_LENGTH,
          length: q.length - LOG_FILTER_MAX_LENGTH,
        }
      : null;
  });
  protected readonly filterError = computed(
    () => this.clientFilterError() ?? this.serverFilterError(),
  );

  protected readonly limitReached = computed(
    () => (this.entries()?.length ?? 0) >= LOG_LIST_LIMIT && this.cursor() !== null,
  );

  protected readonly hasStaleData = computed(
    () => this.entries() !== null || this.summary() !== null,
  );

  protected readonly queryErrorKey = computed(() => {
    const error = this.queryError();
    if (!error) {
      return null;
    }
    if (error.status === 503) {
      return apiErrorCode(error) === 'log_query_timeout'
        ? 'admin.logs.states.timeout'
        : 'admin.logs.states.busy';
    }
    return apiErrorTranslationKey(error);
  });

  protected readonly canWiden = computed(() => {
    const parsed = this.parsedRange();
    return parsed.kind === 'absolute' || (parsed.kind === 'preset' && parsed.range !== '7d');
  });

  /** "Auf 7 Tage erweitern" is on screen: an empty, settled answer on a range shorter than 7 days. */
  private readonly widenOffered = computed(
    () =>
      this.entries()?.length === 0 &&
      !this.listLoading() &&
      this.queryErrorKey() === null &&
      this.canWiden(),
  );

  protected readonly histogramStarts = computed(() =>
    (this.summary()?.processStarts ?? []).map((p) => ({
      source: p.source,
      startedUtc: p.startedUtc,
    })),
  );

  protected readonly dropBanner = computed(() => {
    const dropped = this.summary()?.dropped;
    if (!dropped || dropped.total <= 0) {
      return null;
    }
    this.languageService.lang();
    const reasons = (
      ['queueFull', 'budget', 'writeUnconfirmed', 'captureFailed', 'reentrant', 'rejected'] as const
    )
      .filter((reason) => dropped[reason] > 0)
      .map((reason) =>
        this.transloco.translate(`admin.logs.states.drops.${reason}`, { count: dropped[reason] }),
      )
      .join(', ');
    return this.transloco.translate(pluralKey(dropped.total, 'admin.logs.states.dropsLead'), {
      count: dropped.total,
      reasons,
    });
  });

  /** Coverage gaps inside the shown range, a source that stopped writing, and a late retention run. */
  protected readonly captureBanners = computed<Banner[]>(() => {
    const status = this.status();
    if (!status) {
      return [];
    }
    const banners: Banner[] = [];
    if (!isLogCaptureComplete(status)) {
      const range = this.summary();
      for (const gap of status.coverage24h.incompleteSince) {
        const at = Date.parse(gap.lastIncompleteAtUtc);
        if (range && at >= Date.parse(range.rangeFromUtc) && at <= Date.parse(range.rangeToUtc)) {
          banners.push({
            key: 'coverage',
            params: { source: gap.source, time: utcMinute(gap.lastIncompleteAtUtc) },
          });
        }
      }
    }
    for (const source of status.sources) {
      if (source.state === 'stale') {
        banners.push({
          key: 'stalled',
          params: { source: source.source, time: (source.lastFlushUtc ?? '').slice(11, 16) },
        });
      } else if (source.state === 'disabled' || source.state === 'missing') {
        banners.push({ key: source.state, params: { source: source.source } });
      }
    }
    if (status.retention.overdue) {
      banners.push({
        key: 'overdue',
        params: { date: status.retention.oldestEntryUtc?.slice(0, 10) ?? '—' },
      });
    }
    return banners;
  });

  constructor() {
    this.loadSavedFilters();
    this.destroyRef.onDestroy(() => {
      this.scheduler.pause();
      this.reconcileSub?.unsubscribe();
      this.cancelLoads();
      this.statusSub?.unsubscribe();
      clearTimeout(this.copyTimer);
      this.highlightTimers.forEach((timer) => clearTimeout(timer));
    });

    // Reconcile triggers (spec §8.3). The 30 s tick runs whatever the stream does — the hint only makes
    // the reconcile sooner, it is never what makes the list correct. pollWhileVisible also ticks once
    // when the tab comes back; its very first tick is skipped because the page is loading anyway.
    // The same tick refreshes the capture status, so an open page learns that a source stopped
    // writing; `/logs/status` is cached 30 s on the server, so this tick is its natural pace.
    liveEvents(ADMIN_LIVE_URL, [LIVE_EVENT_TYPES.logsAppended]).subscribe(() =>
      this.requestReconcile(),
    );
    pollWhileVisible(LOG_RECONCILE_INTERVAL_MS, this.visible)
      .pipe(skip(this.visible() ? 1 : 0))
      .subscribe(() => {
        this.loadStatus();
        this.requestReconcile();
      });
    effect(() => {
      if (this.liveUpdate.status() === 'open') {
        untracked(() => this.requestReconcile());
      }
    });

    // "Zurück zu 24 h", "Auf 7 Tage erweitern" and the slot's "Neu laden" vanish as a result of their
    // own action (or, for the slot, of the stream reopening). The focus would fall to <body>; hand it
    // to the range control (the filter field below lg) or, from the slot, to the first row. This runs
    // before the view drops the control, while it still holds the focus.
    effect(() => {
      const gone = {
        back: this.backRange() === null,
        widen: !this.widenOffered(),
        slot: this.slotAction() === null,
      };
      untracked(() => {
        const active = this.document.activeElement;
        const handoff = this.host.nativeElement.contains(active)
          ? active?.closest('[data-focus-handoff]')?.getAttribute('data-focus-handoff')
          : null;
        if (handoff === 'back' || handoff === 'widen' || handoff === 'slot') {
          if (gone[handoff]) {
            this.handFocus(handoff === 'slot' ? 'list' : 'range');
          }
        }
      });
    });

    // URL -> field, so a chip, a facet or the browser's back button shows in the input.
    effect(() => {
      const applied = this.appliedQ();
      untracked(() => this.draft.set(applied));
    });

    // One load per query, range or explicit reload; the previous answers stay until the new ones land.
    effect(() => {
      const request = this.request();
      this.reloadTick();
      untracked(() => this.load(request));
    });

    // An invalid filter marks the offending spot in the field, as the field-error pattern asks.
    afterRenderEffect(() => {
      const error = this.filterError();
      const input = this.filterInput()?.nativeElement;
      if (!error || !input || untracked(this.draft) !== untracked(this.appliedQ)) {
        return;
      }
      const { start, end } = filterErrorSelection(error);
      input.focus();
      input.setSelectionRange(start, end);
    });

    // The sentinel loads the next 100 rows; re-observing after each page catches a sentinel that is
    // still in view (an observer only fires on a change).
    afterRenderEffect((onCleanup) => {
      this.entries();
      const element = this.sentinel()?.nativeElement;
      if (!element || typeof IntersectionObserver === 'undefined') {
        return;
      }
      const observer = new IntersectionObserver(
        (hits) => {
          if (hits.some((hit) => hit.isIntersecting)) {
            this.loadMore();
          }
        },
        { rootMargin: '400px' },
      );
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });
  }

  // --- Actions --------------------------------------------------------------------------------

  /** Applies a new filter (and optionally a new range) — the one entry point for chips and links. */
  applyQuery(change: { q: string }): void {
    if (change.q === this.appliedQ()) {
      this.reload();
      return;
    }
    this.query.setParams({ q: change.q });
  }

  reload(): void {
    this.reloadTick.update((n) => n + 1);
  }

  protected onDraftInput(event: Event): void {
    this.draft.set((event.target as HTMLInputElement).value);
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    this.applyQuery({ q: normalizeFilterWhitespace(this.draft()) });
  }

  protected addTerm(term: string): void {
    this.applyQuery({ q: appendFilterTerm(this.appliedQ(), term) });
  }

  protected setRange(value: string): void {
    if (!(PRESETS as readonly string[]).includes(value)) {
      return;
    }
    this.returnRange.set(null);
    this.query.setParams({ range: value === DEFAULT_PRESET ? '' : value, from: '', to: '' });
  }

  protected selectBucket(fromUtc: string, toUtc: string): void {
    const parsed = this.parsedRange();
    if (parsed.kind === 'preset') {
      this.returnRange.set(parsed.range);
    }
    this.query.setParams({ range: '', from: fromUtc, to: toUtc });
  }

  protected goBack(): void {
    const preset = this.returnRange();
    if (preset) {
      this.setRange(preset);
    }
  }

  protected onSlot(): void {
    if (this.slotAction() === 'reload') {
      this.reload();
    } else {
      this.showPending();
    }
  }

  /** "n neue ↑": back to the top, then in with what waited. Focus moves to the first row, since the
   *  slot itself turns inert. */
  protected showPending(): void {
    const rows = this.held();
    if (rows.length === 0) {
      return;
    }
    this.document.defaultView?.scrollTo({ top: 0 });
    this.insert(rows, true);
  }

  protected openFilterDialog(): void {
    openLogFacetsDialog(this.dialog, {
      rangeOptions: this.rangeOptions,
      rangeValue: this.rangeValue,
      backRange: this.backRange,
      summary: this.summary,
      setRange: (value) => this.setRange(value),
      goBack: () => this.goBack(),
      addTerm: (term) => this.addTerm(term),
      savedFilters: computed(() => this.savedFilters() ?? []),
      currentQ: this.appliedQ,
      savedFiltersFailed: this.savedFiltersFailed,
      selectFilter: (filter) => this.applyQuery({ q: filter.filter }),
      saveCurrent: () => this.saveCurrentFilter(),
      editFilter: (filter) => this.editSavedFilter(filter),
      overwriteFilter: (filter) => this.overwriteSavedFilter(filter),
      deleteFilter: (filter) => this.deleteSavedFilter(filter),
      retryFilters: () => this.loadSavedFilters(),
    });
  }

  protected copyVisible(): void {
    const rows = (this.entries() ?? []).slice(0, LOG_COPY_LIMIT);
    const text = rows.map((entry) => formatLogLine(entry)).join('\n');
    const capped = (this.entries()?.length ?? 0) > LOG_COPY_LIMIT;
    // Without a clipboard API (an insecure origin such as a plain-http LAN host) say so, too.
    const write = navigator.clipboard?.writeText(text) ?? Promise.reject(new Error('no clipboard'));
    void write.then(
      () =>
        this.showCopyFeedback(
          this.transloco.translate(
            capped ? 'admin.logs.list.copyVisibleCapped' : 'admin.logs.list.copyVisibleDone',
            { count: rows.length },
          ),
        ),
      () => this.showCopyFeedback(this.transloco.translate('admin.logs.list.copyFailed')),
    );
  }

  protected loadMore(): void {
    const request = this.request();
    const cursor = this.cursor();
    const frozen = this.frozen;
    if (
      !request ||
      !cursor ||
      !frozen ||
      this.moreLoading() ||
      this.listLoading() ||
      this.limitReached()
    ) {
      return;
    }
    this.moreLoading.set(true);
    this.moreError.set(false);
    this.moreSub = this.service
      .getPage(request.q, { from: frozen.from, to: frozen.to }, cursor)
      .subscribe({
        next: (page) => {
          this.moreLoading.set(false);
          this.cursor.set(page.nextCursor);
          // Merged, not appended: a live insert may already sit at the old end of the list.
          this.entries.update((current) => mergeLogEntries(current ?? [], page.entries));
        },
        error: () => {
          this.moreLoading.set(false);
          this.moreError.set(true);
        },
      });
  }

  // --- Saved filters --------------------------------------------------------------------------

  protected loadSavedFilters(): void {
    this.savedFiltersFailed.set(false);
    this.service
      .listFilters()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (filters) => this.savedFilters.set(filters),
        error: () => this.savedFiltersFailed.set(true),
      });
  }

  protected saveCurrentFilter(): void {
    this.savedFilterError.set(null);
    openLogFilterDialog(this.dialog, { filter: null, initialFilter: this.appliedQ() })
      .closed.pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((saved) => {
        if (!saved) {
          return;
        }
        // After a failed first load the bar holds nothing to append to: ask the server instead.
        if (this.savedFilters() === null) {
          this.loadSavedFilters();
        } else {
          this.savedFilters.update((list) => [...(list ?? []), saved]);
        }
      });
  }

  protected editSavedFilter(item: SavedLogFilter): void {
    this.savedFilterError.set(null);
    openLogFilterDialog(this.dialog, {
      filter: item,
      initialFilter: '',
      onVanished: () => this.loadSavedFilters(),
    })
      .closed.pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((saved) => {
        if (saved) {
          this.replaceSavedFilter(saved);
        }
      });
  }

  protected overwriteSavedFilter(item: SavedLogFilter): void {
    this.savedFilterError.set(null);
    if (this.overwriting) {
      return;
    }
    this.overwriting = true;
    this.service
      .updateFilter(item.id, item.name, this.appliedQ())
      .pipe(
        finalize(() => (this.overwriting = false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (saved) => this.replaceSavedFilter(saved),
        error: (error: HttpErrorResponse) => this.failSavedFilterAction(error),
      });
  }

  protected deleteSavedFilter(item: SavedLogFilter): void {
    this.savedFilterError.set(null);
    openConfirmDialog(this.dialog, {
      message: this.transloco.translate('admin.logs.savedFilters.deleteConfirm', {
        name: item.name,
      }),
      confirmLabel: this.transloco.translate('admin.logs.savedFilters.delete'),
    })
      .closed.pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((confirmed) => {
        if (!confirmed) {
          return;
        }
        this.service
          .deleteFilter(item.id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () =>
              this.savedFilters.update((list) => (list ?? []).filter((f) => f.id !== item.id)),
            error: (error: HttpErrorResponse) => this.failSavedFilterAction(error),
          });
      });
  }

  private replaceSavedFilter(saved: SavedLogFilter): void {
    this.savedFilters.update((list) => (list ?? []).map((f) => (f.id === saved.id ? saved : f)));
  }

  /** A refused overwrite or delete has no field to sit on, so it becomes a banner; a filter that has
   *  vanished meanwhile also means the bar is out of date. */
  private failSavedFilterAction(error: HttpErrorResponse): void {
    const mapped = mapLogFilterSaveError(error);
    this.savedFilterError.set(
      mapped.place === 'filter'
        ? `admin.logs.filterErrors.${mapped.error.reason}`
        : mapped.messageKey,
    );
    if (apiErrorCode(error) === 'log_filter_not_found') {
      this.loadSavedFilters();
    }
  }

  // --- Loading --------------------------------------------------------------------------------

  private load(request: LogRequest | null): void {
    this.cancelLoads();
    // A reconcile of the previous answer must not land on the new one.
    this.reconcileSub?.unsubscribe();
    this.held.set([]);
    this.tooManyNew.set(false);
    this.queryError.set(null);
    this.serverFilterError.set(null);
    this.statusSettled = false;
    this.loadStatus();
    this.moreError.set(false);
    if (!request) {
      // Nothing may sit under the invalid-range or filter-length notice.
      this.entries.set(null);
      this.summary.set(null);
      this.cursor.set(null);
      this.frozen = null;
      this.listLoading.set(false);
      this.summaryLoading.set(false);
      return;
    }
    this.listLoading.set(true);
    this.summaryLoading.set(true);
    this.summarySettled = false;
    this.summaryCurrent.set(false);

    this.pageSub = this.service.getPage(request.q, request.range).subscribe({
      next: (page) => {
        this.pendingPage = { page, range: request.range };
        this.applyPendingPage();
      },
      error: (error: unknown) => this.onLoadError(error, 'list'),
    });
    this.summarySub = this.service.getSummary(request.q, request.range).subscribe({
      next: (summary) => {
        this.summaryLoading.set(false);
        this.summary.set(summary);
        this.lastSummaryAtMs = Date.now();
        this.summaryBehind = false;
        this.summaryCurrent.set(true);
        this.insertedSinceSummary.set(0);
        this.summarySettled = true;
        this.applyPendingPage();
      },
      error: (error: unknown) => {
        this.summarySettled = true;
        this.onLoadError(error, 'summary');
        this.applyPendingPage();
      },
    });
  }

  private applyPendingPage(): void {
    const pending = this.pendingPage;
    if (!pending || !this.summarySettled || !this.statusSettled) {
      return;
    }
    this.pendingPage = null;
    this.onFirstPage(pending.page, pending.range);
  }

  private onFirstPage(page: LogPage, range: LogRangeQuery): void {
    this.listLoading.set(false);
    this.entries.set(page.entries);
    this.frozen =
      page.rangeFromUtc && page.rangeToUtc
        ? { from: page.rangeFromUtc, to: page.rangeToUtc }
        : boundsOf(range);
    // Without bounds to page on there is no next page: show the end, not a dead sentinel.
    this.cursor.set(this.frozen ? page.nextCursor : null);
    // A load the reader asked for went through: live mode may fetch on its own again (S1).
    if (this.paused()) {
      this.paused.set(false);
      this.scheduler.resume();
    }
  }

  // --- Live mode ------------------------------------------------------------------------------

  /** Live only on a relative range with something loaded, on a visible tab, and not paused. */
  private canReconcile(): boolean {
    const request = this.request();
    return (
      request !== null &&
      'range' in request.range &&
      this.entries() !== null &&
      this.frozen !== null &&
      !this.listLoading() &&
      this.visible() &&
      !this.paused() &&
      !this.tooManyNew()
    );
  }

  private requestReconcile(): void {
    if (this.canReconcile()) {
      this.scheduler.trigger();
    }
  }

  /** One reconcile for the scheduler; resolves when it ended, was cancelled or did nothing. */
  private reconcile(): Promise<void> {
    return new Promise<void>((resolve) => {
      this.reconcileSub?.unsubscribe();
      this.reconcileSub = this.reconcile$()
        .pipe(finalize(() => resolve()))
        .subscribe();
    });
  }

  private reconcile$(): Observable<unknown> {
    const request = this.request();
    const loaded = this.entries();
    const frozen = this.frozen;
    if (!this.canReconcile() || !request || !loaded || !frozen) {
      return EMPTY;
    }
    const fromL = reconcileFromUtc(loaded, frozen.from, this.cursor());
    // Asked before every key query: past the 8-day span the server would answer 400 (L3).
    const preflight = preflightReconcile(fromL, Date.now(), request.range);
    if (preflight.kind !== 'request') {
      if (preflight.kind === 'reload') {
        this.tooManyNew.set(true);
      }
      return EMPTY;
    }
    return this.service.getKeys(request.q, fromL).pipe(
      catchError((error: unknown) => {
        // Clock skew at the 8-day edge: the server's "range too long" means reload, not failure.
        if (isInvalidRange(error)) {
          this.tooManyNew.set(true);
        } else {
          this.pause();
        }
        return EMPTY;
      }),
      switchMap((keys) => {
        const known = new Set([...(this.entries() ?? []), ...this.held()].map((e) => e.id));
        const plan = planReconcile(keys, known);
        if (plan.kind === 'reload') {
          this.tooManyNew.set(true);
          return EMPTY;
        }
        const batches = plan.kind === 'fetch' ? plan.idBatches : [];
        return concat(...batches.map((ids) => this.service.getByIds(ids))).pipe(
          toArray(),
          tap((pages) => this.receive(pages.flatMap((p) => p.entries))),
          switchMap(() => this.refreshSummary$(request)),
          catchError(() => {
            this.pause();
            return EMPTY;
          }),
        );
      }),
    );
  }

  /** Into the list at the top, behind "n neue" otherwise — together with what already waited. */
  private receive(incoming: LogEntry[]): void {
    const rows = mergeLogEntries(this.held(), incoming);
    if (rows.length === 0) {
      return;
    }
    if (this.atTop()) {
      // The reader may still sit on "n neue ↑", which turns inert now: take the focus along.
      this.insert(rows, this.slotHasFocus());
    } else {
      this.held.set(rows);
    }
  }

  private insert(rows: LogEntry[], moveFocus: boolean): void {
    this.entries.update((current) => mergeLogEntries(current ?? [], rows));
    this.held.set([]);
    this.summaryBehind = true;
    this.insertedSinceSummary.update((n) => n + rows.length);
    this.announce(
      this.transloco.translate(pluralKey(rows.length, 'admin.logs.live.inserted'), {
        count: rows.length,
      }),
    );
    if (moveFocus) {
      afterNextRender(
        () => this.host.nativeElement.querySelector<HTMLElement>('[data-log-id] button')?.focus(),
        { injector: this.injector },
      );
    }
    const ids = rows.map((row) => row.id);
    this.highlighted.update((current) => new Set([...current, ...ids]));
    const timer = setTimeout(() => {
      this.highlightTimers.delete(timer);
      this.highlighted.update((current) => {
        const next = new Set(current);
        ids.forEach((id) => next.delete(id));
        return next;
      });
    }, HIGHLIGHT_MS);
    this.highlightTimers.add(timer);
  }

  /** The histogram and facets follow inserted rows, at most once per 30 s. */
  private refreshSummary$(request: LogRequest): Observable<unknown> {
    if (!this.summaryBehind || Date.now() - this.lastSummaryAtMs < SUMMARY_MIN_INTERVAL_MS) {
      return EMPTY;
    }
    return this.service.getSummary(request.q, request.range).pipe(
      tap((summary) => {
        this.summary.set(summary);
        this.lastSummaryAtMs = Date.now();
        this.summaryBehind = false;
        this.insertedSinceSummary.set(0);
      }),
    );
  }

  private pause(): void {
    this.paused.set(true);
    this.scheduler.pause();
  }

  /**
   * One polite message per insert, also when two inserts read the same ("1 neuer Eintrag" twice):
   * a live region only speaks on a change, so it is emptied first and refilled after that render.
   */
  private announce(text: string): void {
    this.announcement.set('');
    afterNextRender(() => this.announcement.set(text), { injector: this.injector });
  }

  /** Focuses the selected range (or, without the range control, the filter field) or the first row. */
  private handFocus(target: 'range' | 'list'): void {
    afterNextRender(
      () => {
        const root = this.host.nativeElement;
        const preferred =
          target === 'range'
            ? root.querySelector<HTMLElement>('app-segmented-control [aria-checked="true"]')
            : root.querySelector<HTMLElement>('[data-log-id] button');
        (preferred ?? this.filterInput()?.nativeElement)?.focus();
      },
      { injector: this.injector },
    );
  }

  private slotHasFocus(): boolean {
    const active = this.document.activeElement;
    return (
      active !== null &&
      (this.host.nativeElement.querySelector('[data-testid="log-new-slot"]')?.contains(active) ??
        false)
    );
  }

  private atTop(): boolean {
    const first = this.host.nativeElement.querySelector<HTMLElement>('[data-log-id]');
    const bar = this.stickyBar()?.nativeElement;
    return isLogListAtTop(
      first ? first.getBoundingClientRect().top : null,
      bar ? bar.getBoundingClientRect().bottom : 0,
      this.document.defaultView?.innerHeight ?? 0,
      this.list()?.hasExpanded() ?? false,
    );
  }

  private onLoadError(error: unknown, source: 'list' | 'summary'): void {
    if (source === 'list') {
      this.listLoading.set(false);
    } else {
      this.summaryLoading.set(false);
    }
    if (!(error instanceof HttpErrorResponse)) {
      return;
    }
    if (error.status === 400 && isLogFilterApiError(error.error)) {
      // Never applied partially: nothing of the old answer stays under a filter that is not valid.
      this.serverFilterError.set(error.error);
      this.entries.set(null);
      this.summary.set(null);
      this.cursor.set(null);
      this.cancelLoads();
      this.listLoading.set(false);
      this.summaryLoading.set(false);
      return;
    }
    this.queryError.set(error);
  }

  private loadStatus(): void {
    this.statusSub?.unsubscribe();
    this.statusSub = this.service
      .getStatus()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (status) => {
          this.status.set(status);
          this.onStatusSettled();
        },
        // The banners are advisory; a failed status call must not look like a failed page.
        error: () => this.onStatusSettled(),
      });
  }

  /** Any status answer releases a held first page — also the tick's, which replaced the load's call. */
  private onStatusSettled(): void {
    this.statusSettled = true;
    this.applyPendingPage();
  }

  private cancelLoads(): void {
    this.pageSub?.unsubscribe();
    this.summarySub?.unsubscribe();
    this.pendingPage = null;
    this.moreSub?.unsubscribe();
    this.moreLoading.set(false);
  }

  private showCopyFeedback(text: string): void {
    this.copyFeedback.set(text);
    clearTimeout(this.copyTimer);
    this.copyTimer = setTimeout(() => this.copyFeedback.set(null), COPY_FEEDBACK_MS);
  }
}

function isInvalidRange(error: unknown): boolean {
  return (
    error instanceof HttpErrorResponse &&
    error.status === 400 &&
    apiErrorCode(error) === 'invalid_log_range'
  );
}
