import { Dialog } from '@angular/cdk/dialog';
import { BreakpointObserver } from '@angular/cdk/layout';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  ElementRef,
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
import { Subscription, map } from 'rxjs';

import {
  LogEntry,
  LogFilterApiError,
  LogPage,
  LogRangePreset,
  LogRangeQuery,
  LogStatus,
  LogSummary,
  isLogCaptureComplete,
  isLogFilterApiError,
} from '../../core/admin/admin-logs.model';
import { AdminLogsService } from '../../core/admin/admin-logs.service';
import {
  appendFilterTerm,
  filterErrorSelection,
  normalizeFilterWhitespace,
} from '../../core/admin/log-filter-text';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { listQueryState } from '../../core/routing/list-query-state';
import { TimeHistogram } from '../../shared/charts/time-histogram';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { Popover } from '../../shared/ui/popover';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { AdminLogFacets } from './admin-log-facets';
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
 * `/admin/logs`: filter bar, time histogram, facets and the log list, with every state the spec names
 * (§9.7). The URL (`?q=&range=` or `?q=&from=&to=`) is the source of truth for query and range; the
 * filter applies on Enter or "Anwenden", facet and bar clicks apply at once.
 *
 * Seams for what comes next: {@link applyQuery} is the one way a new filter enters (saved-filter chips),
 * {@link entries} and {@link list} expose what is loaded and whether a row is unfolded, and
 * {@link pendingNew} feeds the reserved "n neue" slot (live mode). This page loads once per query and
 * never polls.
 */
@Component({
  selector: 'app-admin-logs-page',
  imports: [
    AdminLogFacets,
    AdminLogList,
    Button,
    EmptyState,
    NoticeBanner,
    Popover,
    SegmentedControl,
    SkeletonRows,
    TimeHistogram,
    TranslocoPipe,
  ],
  template: `
    <div class="flex flex-col gap-4">
      <!-- Sticky below header (top-14) + admin tabs (h-10): top-24 (design doc §8.5). -->
      <div class="app-sticky-bar top-24 flex flex-col gap-2 py-2">
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
          <button type="submit" appButton="primary">
            {{ 'admin.logs.filter.apply' | transloco }}
          </button>
          <div class="relative" data-popover-anchor>
            <button
              type="button"
              appButton="outline"
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
          } @else {
            <button type="button" appButton="outline" (click)="openFilterDialog()">
              {{ 'admin.logs.filter.open' | transloco }}
            </button>
          }
          @if (absoluteText(); as text) {
            <span class="text-xs text-fg-muted tabular-nums">{{ text }}</span>
          }
          @if (backRange(); as range) {
            <button type="button" appButton="outline" (click)="goBack()">
              {{ 'admin.logs.range.backTo' | transloco: { range: range } }}
            </button>
          }
          <!-- Reserved even at zero (invisible), so the bar never jumps when "n neue" appears. -->
          <span class="ml-auto min-w-24 text-right" data-testid="log-new-slot">
            <button
              type="button"
              class="text-sm text-accent-fg underline-offset-2 hover:underline"
              [class.invisible]="pendingNew() === 0"
              [attr.aria-hidden]="pendingNew() === 0 ? 'true' : null"
              [tabindex]="pendingNew() === 0 ? -1 : 0"
              [attr.aria-label]="'admin.logs.newEntries.aria' | transloco: { count: pendingNew() }"
              (click)="showPending()"
            >
              {{ 'admin.logs.newEntries.label' | transloco: { count: pendingNew() } }}
            </button>
          </span>
        </div>
      </div>

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
        <div
          class="app-skeleton h-32 w-full"
          role="status"
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
              <button notice-action type="button" appButton="outline" (click)="reload()">
                {{ 'admin.logs.states.retry' | transloco }}
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
                    <button type="button" appButton="outline" (click)="setRange('7d')">
                      {{ 'admin.logs.range.extend' | transloco }}
                    </button>
                  }
                </app-empty-state>
              }
            } @else {
              <div class="flex flex-wrap items-center justify-between gap-2">
                <p class="text-xs text-fg-muted tabular-nums">
                  {{
                    'admin.logs.list.loaded'
                      | transloco: { shown: rows.length, total: summary()?.total ?? rows.length }
                  }}
                </p>
                <div class="flex items-center gap-2">
                  @if (copyFeedback(); as feedback) {
                    <span class="text-xs text-fg-muted" role="status">{{ feedback }}</span>
                  }
                  <button type="button" appButton="outline" (click)="copyVisible()">
                    {{ 'admin.logs.list.copyVisible' | transloco }}
                  </button>
                </div>
              </div>
              <app-admin-log-list
                [entries]="rows"
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

  private readonly query = listQueryState({ q: '', range: '', from: '', to: '' });

  private readonly filterInput = viewChild<ElementRef<HTMLInputElement>>('filterInput');
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');
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
    return this.transloco.translate('admin.logs.range.absolute', {
      from: utcMinute(parsed.from),
      to: utcMinute(parsed.to),
    });
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

  /** Entries waiting behind the reserved "n neue" slot; fed by the live mode, zero until then. */
  readonly pendingNew = signal(0);

  protected readonly copyFeedback = signal<string | null>(null);
  protected readonly helpOpen = signal(false);

  private readonly reloadTick = signal(0);
  private pageSub?: Subscription;
  private summarySub?: Subscription;
  private moreSub?: Subscription;
  private statusSub?: Subscription;
  private copyTimer?: ReturnType<typeof setTimeout>;

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
    const code = (error.error as { errorCode?: string } | null)?.errorCode;
    if (error.status === 503) {
      return code === 'log_query_timeout' ? 'admin.logs.states.timeout' : 'admin.logs.states.busy';
    }
    return apiErrorTranslationKey(error);
  });

  protected readonly canWiden = computed(() => {
    const parsed = this.parsedRange();
    return parsed.kind === 'absolute' || (parsed.kind === 'preset' && parsed.range !== '7d');
  });

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
    return this.transloco.translate('admin.logs.states.dropsLead', {
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
    this.destroyRef.onDestroy(() => {
      this.cancelLoads();
      this.statusSub?.unsubscribe();
      clearTimeout(this.copyTimer);
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

  protected showPending(): void {
    // Live mode attaches here; with nothing pending the slot is inert.
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
    });
  }

  protected copyVisible(): void {
    const rows = (this.entries() ?? []).slice(0, LOG_COPY_LIMIT);
    const text = rows.map((entry) => formatLogLine(entry)).join('\n');
    const capped = (this.entries()?.length ?? 0) > LOG_COPY_LIMIT;
    navigator.clipboard?.writeText(text).then(
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
          this.entries.update((current) => {
            const seen = new Set((current ?? []).map((entry) => entry.id));
            return [...(current ?? []), ...page.entries.filter((entry) => !seen.has(entry.id))];
          });
        },
        error: () => {
          this.moreLoading.set(false);
          this.moreError.set(true);
        },
      });
  }

  // --- Loading --------------------------------------------------------------------------------

  private load(request: LogRequest | null): void {
    this.cancelLoads();
    this.queryError.set(null);
    this.serverFilterError.set(null);
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

    this.pageSub = this.service.getPage(request.q, request.range).subscribe({
      next: (page) => this.onFirstPage(page, request.range),
      error: (error: unknown) => this.onLoadError(error, 'list'),
    });
    this.summarySub = this.service.getSummary(request.q, request.range).subscribe({
      next: (summary) => {
        this.summaryLoading.set(false);
        this.summary.set(summary);
      },
      error: (error: unknown) => this.onLoadError(error, 'summary'),
    });
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
        next: (status) => this.status.set(status),
        // The banners are advisory; a failed status call must not look like a failed page.
        error: () => undefined,
      });
  }

  private cancelLoads(): void {
    this.pageSub?.unsubscribe();
    this.summarySub?.unsubscribe();
    this.moreSub?.unsubscribe();
    this.moreLoading.set(false);
  }

  private showCopyFeedback(text: string): void {
    this.copyFeedback.set(text);
    clearTimeout(this.copyTimer);
    this.copyTimer = setTimeout(() => this.copyFeedback.set(null), COPY_FEEDBACK_MS);
  }
}
