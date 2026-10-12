import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, linkedSignal, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { skip } from 'rxjs';

import { AdminService } from '../../core/admin/admin.service';
import { AdminDashboardLoad, AdminDashboardLoadRange } from '../../core/admin/admin.model';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pollWhileVisible } from '../../core/live/poll-while-visible';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import { formatBucketTime } from '../../shared/charts/chart-series';
import { LineChart } from '../../shared/charts/line-chart';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { SkeletonSections } from '../../shared/ui/skeleton-sections';
import { buildLoadCharts, LoadChart } from './admin-load-charts';

const RANGES: AdminDashboardLoadRange[] = ['1h', '24h', '7d', '30d', '90d', '13mo'];
const RANGE_OPTIONS: SegmentedControlOption[] = RANGES.map((value) => ({
  value,
  labelKey: `admin.dashboard.load.range.${value}`,
}));

const DEFAULT_RANGE: AdminDashboardLoadRange = '24h';
/** The widest hourly range: if it holds nothing, nothing holds anything. */
const PROBE_RANGE: AdminDashboardLoadRange = '13mo';

/** One shared timer tick; ranges from 7 days on only act on every fifth one (spec section 8). */
const POLL_TICK_MS = 60_000;
const LONG_RANGE_INTERVAL_MS = 300_000;
const TICK_TOLERANCE_MS = 5_000;
const HOUR_SECONDS = 3_600;
const DAY_SECONDS = 86_400;

/**
 * The x axis the rate-limit trend should use: its own minute axis while the load history has none
 * to offer, the load buckets for ranges up to 24 hours, or no series at all for longer ranges.
 */
export type TrendAxis =
  | { kind: 'own' }
  | { kind: 'load'; startsUtc: readonly string[]; bucketSeconds: number }
  | { kind: 'hidden' };

function pollIntervalMs(range: AdminDashboardLoadRange): number {
  return range === '1h' || range === '24h' ? POLL_TICK_MS : LONG_RANGE_INTERVAL_MS;
}

/**
 * "Load and work" of the admin monitoring page: eleven small multiples (a fixed set, whatever the
 * range) over one complete, server-bucketed range, polled while the tab is visible. The section owns
 * its resource, its range and its history probe; the page only reloads it and reads `trendAxis`.
 */
@Component({
  selector: 'app-admin-load-section',
  imports: [EmptyState, LineChart, NoticeBanner, SegmentedControl, SkeletonSections, TranslocoPipe],
  template: `
    <section class="flex flex-col gap-3" data-testid="admin-load">
      <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-2">
        <h3 class="text-base font-semibold">{{ 'admin.dashboard.load.title' | transloco }}</h3>
        @if (dataThroughText(); as through) {
          <p class="text-sm text-fg-body" data-testid="load-data-through">
            {{ 'admin.dashboard.load.dataThrough' | transloco: { at: through } }}
          </p>
        }
      </div>

      @if (errorKey(); as error) {
        <app-notice-banner variant="error" data-testid="load-error">
          {{ error | transloco }}
        </app-notice-banner>
      }
      @if (staleAtText(); as at) {
        <app-notice-banner variant="warning" data-testid="load-stale">
          {{ 'admin.dashboard.load.stale' | transloco: { at } }}
        </app-notice-banner>
      }

      @if (historyKnown()) {
        <app-segmented-control
          [options]="rangeOptions"
          [value]="range()"
          (valueChange)="onRangeChange($event)"
          [ariaLabel]="'admin.dashboard.load.range.label' | transloco"
        />
      }

      @if (data(); as load) {
        @if (rangeMismatch()) {
          <p class="text-xs text-fg-muted" data-testid="load-shown-range">
            {{
              'admin.dashboard.load.shownRange'
                | transloco: { range: 'admin.dashboard.load.range.' + load.range | transloco }
            }}
          </p>
        }
        @if (!load.available) {
          <app-empty-state
            data-testid="load-empty"
            [title]="emptyTitleKey() | transloco"
            [description]="emptyDescription()"
          />
        } @else {
          @if (coverageText(); as coverage) {
            <p class="text-xs text-fg-muted" data-testid="load-coverage">{{ coverage }}</p>
          }
          <div
            class="grid grid-cols-1 gap-x-6 gap-y-5 transition-opacity md:grid-cols-2"
            [class.opacity-50]="busy()"
            [attr.aria-busy]="busy()"
          >
            @for (chart of charts(); track chart.id) {
              <div
                class="flex min-w-0 flex-col gap-1"
                [attr.data-testid]="'load-chart-' + chart.id"
              >
                <h4 class="text-sm font-medium">
                  {{ chart.titleKey | transloco: chart.titleParams }}
                </h4>
                <app-line-chart
                  [series]="chart.series"
                  [xStartsUtc]="load.bucketStartsUtc"
                  [bucketSeconds]="load.bucketSeconds"
                  [unit]="chart.unit"
                  [partialLastBucket]="true"
                  [bucketDetails]="chart.details"
                  [title]="chart.titleKey | transloco: chart.titleParams"
                />
                @for (note of chart.notes; track $index) {
                  <p class="text-xs text-fg-muted">
                    {{ 'admin.dashboard.load.note.' + note.key | transloco: note.params }}
                  </p>
                }
              </div>
            }
          </div>
          <div class="flex flex-col gap-1 text-xs text-fg-muted">
            <p>{{ 'admin.dashboard.load.help.sharedHost' | transloco }}</p>
            @if (load.sourceKind === 'hourly') {
              <p>{{ 'admin.dashboard.load.help.hourly' | transloco }}</p>
            }
          </div>
        }
      } @else if (!errorKey()) {
        @if (loading()) {
          <app-skeleton-sections [count]="2" />
        }
      }
    </section>
  `,
})
export class AdminLoadSection {
  private readonly adminService = inject(AdminService);
  private readonly languageService = inject(LanguageService);
  private readonly transloco = inject(TranslocoService);

  protected readonly rangeOptions = RANGE_OPTIONS;
  protected readonly range = signal<AdminDashboardLoadRange>(DEFAULT_RANGE);

  private readonly resource = rxResource({
    params: () => this.range(),
    stream: ({ params }) => this.adminService.getDashboardLoad(params),
  });

  /** The last complete answer; a range change or a failed reload keeps it on screen. */
  protected readonly data = linkedSignal<
    AdminDashboardLoad | undefined,
    AdminDashboardLoad | undefined
  >({
    source: () => (this.resource.hasValue() ? this.resource.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  /**
   * The newest read failure, held until a read succeeds (a retry in flight does not clear it).
   * A busy 503 is one of these: it carries no body and no message of its own.
   */
  private readonly failure = linkedSignal<string, unknown>({
    source: () => this.resource.status(),
    computation: (status, previous) =>
      status === 'error'
        ? this.resource.error()
        : status === 'resolved'
          ? null
          : (previous?.value ?? null),
  });

  /**
   * An empty answer for the selected range leads to one cheap extra read of the widest range: the
   * probe says whether any history exists at all, which decides the range menu and the empty text.
   */
  private readonly probeNeeded = computed(() => {
    const load = this.data();
    return load !== undefined && !load.available && load.range !== PROBE_RANGE;
  });

  private readonly probeResource = rxResource({
    params: () => (this.probeNeeded() ? PROBE_RANGE : undefined),
    stream: ({ params }) => this.adminService.getDashboardLoad(params),
  });

  private readonly probe = linkedSignal<
    AdminDashboardLoad | undefined,
    AdminDashboardLoad | undefined
  >({
    source: () => (this.probeResource.hasValue() ? this.probeResource.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  /** Once any range has shown measurements the range menu stays, even if the next range is empty. */
  protected readonly historyKnown = linkedSignal<boolean, boolean>({
    source: () => this.data()?.available === true || this.probe()?.available === true,
    computation: (seen, previous) => seen || (previous?.value ?? false),
  });

  protected readonly loading = computed(() => this.resource.status() === 'loading');
  protected readonly rangeMismatch = computed(() => {
    const load = this.data();
    return load !== undefined && load.range !== this.range();
  });
  protected readonly busy = computed(() => this.rangeMismatch() && this.loading());

  protected readonly errorKey = computed(() => {
    const failure = this.failure();
    return this.data() === undefined && failure instanceof HttpErrorResponse
      ? apiErrorTranslationKey(failure)
      : null;
  });

  /** When the last good answer was generated, shown only while a newer read is failing. */
  protected readonly staleAtText = computed(() => {
    const load = this.data();
    return this.failure() !== null && load !== undefined
      ? this.formatDateTime(load.generatedAtUtc)
      : null;
  });

  protected readonly dataThroughText = computed(() => {
    const through = this.data()?.dataThroughUtc;
    return through ? this.formatDateTime(through) : null;
  });

  protected readonly emptyTitleKey = computed(() => {
    if (this.probe()?.available) {
      return 'admin.dashboard.load.empty.sinceTitle';
    }
    return this.historyKnown()
      ? 'admin.dashboard.load.empty.rangeTitle'
      : 'admin.dashboard.load.empty.waitingTitle';
  });

  protected readonly emptyDescription = computed(() => {
    this.languageService.lang();
    const first = this.probe()?.bucketStartsUtc[0];
    if (!first) {
      return this.historyKnown()
        ? null
        : this.transloco.translate('admin.dashboard.load.empty.waitingDescription');
    }
    return this.transloco.translate('admin.dashboard.load.empty.sinceDescription', {
      range: this.transloco.translate(`admin.dashboard.load.range.${this.data()?.range}`),
      since: formatBucketTime(first, DAY_SECONDS, this.locale()),
    });
  });

  /** Leading buckets without a single observation are left out by the server: say how much there is. */
  protected readonly coverageText = computed(() => {
    const load = this.data();
    const first = load?.bucketStartsUtc[0];
    if (!load || !load.available || !first || !load.dataThroughUtc) {
      return null;
    }
    const missingMs = Date.parse(first) - Date.parse(load.fromUtc);
    if (!(missingMs >= load.bucketSeconds * 1000)) {
      return null;
    }
    const spanSeconds = (Date.parse(load.dataThroughUtc) - Date.parse(first)) / 1000;
    const days = spanSeconds >= 2 * DAY_SECONDS;
    return this.transloco.translate(
      days ? 'admin.dashboard.load.coverage.days' : 'admin.dashboard.load.coverage.hours',
      {
        count: Math.max(1, Math.round(spanSeconds / (days ? DAY_SECONDS : HOUR_SECONDS))),
        since: formatBucketTime(first, load.bucketSeconds, this.locale()),
      },
    );
  });

  /** What the page's rate-limit trend should draw on: see `TrendAxis`. */
  readonly trendAxis = computed<TrendAxis>(() => {
    const load = this.data();
    if (!load || !load.available) {
      return { kind: 'own' };
    }
    return load.range === '1h' || load.range === '24h'
      ? { kind: 'load', startsUtc: load.bucketStartsUtc, bucketSeconds: load.bucketSeconds }
      : { kind: 'hidden' };
  });

  protected readonly charts = computed<LoadChart[]>(() => {
    const load = this.data();
    this.languageService.lang();
    return load && load.available
      ? buildLoadCharts(load, {
          translate: (key, params) => this.transloco.translate(key, params),
          locale: this.locale(),
        })
      : [];
  });

  private lastRequestAt = Date.now();
  private forceNextTick = false;

  constructor() {
    // A tab that was hidden reloads on its return whatever the range's own cadence says.
    const visible = pageVisible();
    effect(() => {
      if (!visible()) {
        this.forceNextTick = true;
      }
    });
    // The first tick of a visible page is skipped: the resource has just loaded itself. A page that
    // opened hidden has not had that load, so its first tick (the return to visible) counts.
    pollWhileVisible(POLL_TICK_MS, visible)
      .pipe(skip(visible() ? 1 : 0))
      .subscribe(() => this.onTick());
  }

  /** Reloads the selected range, and the history probe while one is needed. */
  reload(): void {
    this.lastRequestAt = Date.now();
    this.resource.reload();
    // A positive probe answer is final enough: history does not vanish, so stop asking.
    if (!this.probe()?.available) {
      this.probeResource.reload();
    }
  }

  protected onRangeChange(value: string): void {
    const range = RANGES.find((candidate) => candidate === value);
    if (range) {
      this.lastRequestAt = Date.now();
      this.range.set(range);
    }
  }

  private onTick(): void {
    const due =
      this.forceNextTick ||
      Date.now() - this.lastRequestAt >= pollIntervalMs(this.range()) - TICK_TOLERANCE_MS;
    if (due) {
      this.forceNextTick = false;
      this.reload();
    }
  }

  private formatDateTime(iso: string): string {
    return formatBucketTime(iso, 1, this.locale());
  }

  private locale(): string {
    return toLocale(this.languageService.lang());
  }
}
