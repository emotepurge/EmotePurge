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
import {
  ChartUnit,
  formatBucketTime,
  formatUnit,
  provenanceSources,
} from '../../shared/charts/chart-series';
import { LineChart, LineChartSeries } from '../../shared/charts/line-chart';
import { EmptyState } from '../../shared/ui/empty-state';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SegmentedControl, SegmentedControlOption } from '../../shared/ui/segmented-control';
import { SkeletonSections } from '../../shared/ui/skeleton-sections';

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

interface NoteLine {
  key: string;
  params?: Record<string, unknown>;
}

interface LoadChart {
  id: string;
  titleKey: string;
  titleParams: Record<string, unknown>;
  unit: ChartUnit;
  series: LineChartSeries[];
  details: (string | null)[];
  notes: NoteLine[];
}

type SourceName = 'api' | 'worker';

function pollIntervalMs(range: AdminDashboardLoadRange): number {
  return range === '1h' || range === '24h' ? POLL_TICK_MS : LONG_RANGE_INTERVAL_MS;
}

function lastKnown(values: readonly (number | null)[]): number | null {
  for (let i = values.length - 1; i >= 0; i--) {
    if (values[i] !== null && Number.isFinite(values[i])) {
      return values[i];
    }
  }
  return null;
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
    return load && load.available ? this.buildCharts(load) : [];
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
    this.probeResource.reload();
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

  private buildCharts(load: AdminDashboardLoad): LoadChart[] {
    const raw = load.sourceKind === 'raw';
    const kind = raw ? 'raw' : 'hourly';
    const stat = this.transloco.translate(`admin.dashboard.load.stat.${kind}`);
    const p95Key = `admin.dashboard.load.series.p95.${kind}`;
    const maxKey = `admin.dashboard.load.series.max.${kind}`;
    const { host, api, worker, workload } = load;
    const bucketHours = load.bucketSeconds / HOUR_SECONDS;

    const hostSeries = (p95: (number | null)[], max: (number | null)[], id: string) => [
      { id: `${id}-p95`, labelKey: p95Key, values: p95 },
      { id: `${id}-max`, labelKey: maxKey, values: max, emphasis: 'marks' as const },
    ];
    const bySource = (id: string, pick: (s: typeof api) => (number | null)[]) => [
      { id: `${id}-api`, labelKey: 'admin.dashboard.load.series.api', values: pick(api) },
      { id: `${id}-worker`, labelKey: 'admin.dashboard.load.series.worker', values: pick(worker) },
    ];
    const processDetails = () =>
      load.bucketStartsUtc.map((_, i) =>
        this.listText('detail.samples', [
          ['api', api.sampleCount[i]],
          ['worker', worker.sampleCount[i]],
        ]),
      );
    const cgroupMemory = (source: SourceName): LoadChart => {
      const series = source === 'api' ? api : worker;
      const limit = lastKnown(series.cgroupMemoryLimitBytes);
      const share = raw ? lastKnown(series.cgroupMemoryShareP95) : null;
      return {
        id: `cgroup-memory-${source}`,
        titleKey: 'admin.dashboard.load.charts.cgroupMemory',
        titleParams: { source: this.sourceName(source), stat },
        unit: 'bytes',
        series: [
          { id: `cgroup-memory-${source}`, labelKey: p95Key, values: series.cgroupMemoryP95Bytes },
        ],
        details: load.bucketStartsUtc.map((_, i) =>
          this.listText('detail.samples', [[source, series.sampleCount[i]]]),
        ),
        notes: [
          limit === null
            ? { key: 'memoryLimitNone' }
            : { key: 'memoryLimit', params: { limit: this.format(limit, 'bytes') } },
          ...(share === null
            ? []
            : [{ key: 'memoryShare', params: { percent: this.format(share, 'percent') } }]),
        ],
      };
    };
    const quotaNotes = (): NoteLine[] =>
      (['api', 'worker'] as const).map((source) => {
        const quota = lastKnown((source === 'api' ? api : worker).cgroupCpuQuotaCores);
        return quota === null
          ? { key: 'cpuQuotaNone', params: { source: this.sourceName(source) } }
          : {
              key: 'cpuQuota',
              params: { source: this.sourceName(source), cores: this.format(quota, 'cores') },
            };
      });
    const workloadChart = (
      id: 'chatLines' | 'emoteMatches',
      values: (number | null)[],
    ): LoadChart => ({
      id,
      titleKey: `admin.dashboard.load.charts.${id}.${workload.workloadUnit}`,
      titleParams: { hours: new Intl.NumberFormat(this.locale()).format(bucketHours) },
      unit: 'count',
      series: [{ id, labelKey: `admin.dashboard.load.series.${id}`, values }],
      details: load.bucketStartsUtc.map((_, i) => {
        const seconds = workload.workloadIntervalSeconds[i];
        const hours = workload.workloadHoursWithData[i];
        if (workload.workloadUnit === 'perMinute') {
          return seconds === null || seconds === undefined
            ? null
            : this.transloco.translate('admin.dashboard.load.detail.intervalSeconds', { seconds });
        }
        return hours === null || hours === undefined
          ? null
          : this.transloco.translate('admin.dashboard.load.detail.hoursWithData', { hours });
      }),
      notes: [],
    });

    const lastIndexWithMemory = [...host.memoryUsedP95Bytes.keys()]
      .reverse()
      .find((i) => host.memoryUsedP95Bytes[i] !== null && host.memoryTotalBytes[i] !== null);
    const memoryTotal = lastKnown(host.memoryTotalBytes);
    // The share is derived here from the raw used/total pair of one bucket; only for a finite,
    // known total (spec: percent only against a finite bound), and never from hourly rows.
    const memoryShare =
      raw &&
      lastIndexWithMemory !== undefined &&
      (host.memoryTotalBytes[lastIndexWithMemory] ?? 0) > 0
        ? (100 * (host.memoryUsedP95Bytes[lastIndexWithMemory] as number)) /
          (host.memoryTotalBytes[lastIndexWithMemory] as number)
        : null;

    return [
      {
        id: 'host-cpu',
        titleKey: 'admin.dashboard.load.charts.hostCpu',
        titleParams: { stat },
        unit: 'percent',
        series: hostSeries(host.cpuBusyP95, host.cpuBusyMax, 'host-cpu'),
        details: this.hostDetails(load, 'cpu', 'percent'),
        notes: [],
      },
      {
        id: 'host-memory',
        titleKey: 'admin.dashboard.load.charts.hostMemory',
        titleParams: { stat },
        unit: 'bytes',
        series: hostSeries(host.memoryUsedP95Bytes, host.memoryUsedMaxBytes, 'host-memory'),
        details: this.hostDetails(load, 'memory', 'bytes'),
        notes: [
          memoryTotal === null
            ? { key: 'memoryTotalNone' }
            : { key: 'memoryTotal', params: { total: this.format(memoryTotal, 'bytes') } },
          ...(memoryShare === null
            ? []
            : [
                {
                  key: 'memoryShareHost',
                  params: { percent: this.format(memoryShare, 'percent') },
                },
              ]),
        ],
      },
      {
        id: 'host-load',
        titleKey: 'admin.dashboard.load.charts.hostLoad',
        titleParams: { stat },
        unit: 'tasks',
        series: hostSeries(host.loadAverage1P95, host.loadAverage1Max, 'host-load'),
        details: this.hostDetails(load, 'load', 'tasks'),
        notes: [],
      },
      {
        id: 'process-cpu',
        titleKey: 'admin.dashboard.load.charts.processCpu',
        titleParams: { stat },
        unit: 'cores',
        series: bySource('process-cpu', (s) => s.processCpuP95),
        details: processDetails(),
        notes: [],
      },
      {
        id: 'cgroup-cpu',
        titleKey: 'admin.dashboard.load.charts.cgroupCpu',
        titleParams: { stat },
        unit: 'cores',
        series: bySource('cgroup-cpu', (s) => s.cgroupCpuP95),
        details: processDetails(),
        notes: quotaNotes(),
      },
      {
        // Stays in every range so the chart count never changes; hourly rows carry no share.
        id: 'cgroup-cpu-share',
        titleKey: 'admin.dashboard.load.charts.cgroupCpuShare',
        titleParams: { stat },
        unit: 'percent',
        series: bySource('cgroup-cpu-share', (s) => s.cgroupCpuQuotaShareP95),
        details: processDetails(),
        notes: raw ? [] : [{ key: 'quotaShareHourly' }, ...quotaNotes()],
      },
      cgroupMemory('api'),
      cgroupMemory('worker'),
      {
        id: 'channels',
        titleKey: 'admin.dashboard.load.charts.channels',
        titleParams: {},
        unit: 'count',
        series: [
          {
            id: 'channels-active',
            labelKey: 'admin.dashboard.load.series.activeChannels',
            values: workload.activeChannelsMax,
          },
          {
            id: 'channels-live',
            labelKey: 'admin.dashboard.load.series.liveChannels',
            values: workload.liveChannelsMax,
          },
        ],
        details: [],
        notes: [],
      },
      workloadChart('chatLines', workload.chatLines),
      workloadChart('emoteMatches', workload.emoteMatches),
    ];
  }

  /** Sources, the source of the shown value, sample counts and per-source means of one host bucket. */
  private hostDetails(
    load: AdminDashboardLoad,
    metric: 'cpu' | 'memory' | 'load',
    unit: ChartUnit,
  ): (string | null)[] {
    const host = load.host;
    const winners =
      metric === 'cpu'
        ? host.cpuValueSource
        : metric === 'memory'
          ? host.memoryValueSource
          : host.loadAverageValueSource;
    const [apiMean, workerMean] =
      metric === 'cpu'
        ? [host.apiCpuBusyMean, host.workerCpuBusyMean]
        : metric === 'memory'
          ? [host.apiMemoryUsedMeanBytes, host.workerMemoryUsedMeanBytes]
          : [host.apiLoadAverage1Mean, host.workerLoadAverage1Mean];

    return load.bucketStartsUtc.map((_, i) => {
      const parts: string[] = [];
      const sources = provenanceSources(host.provenance[i] ?? null);
      if (sources.length > 0) {
        parts.push(
          this.transloco.translate('admin.dashboard.load.detail.sources', {
            list: sources.map((source) => this.sourceName(source)).join(', '),
          }),
        );
      }
      const winner = winners[i];
      if (winner) {
        parts.push(
          this.transloco.translate('admin.dashboard.load.detail.valueFrom', {
            source: this.sourceName(winner === 'A' ? 'api' : 'worker'),
          }),
        );
      }
      const counts = this.listText('detail.samples', [
        ['api', host.apiSampleCount[i]],
        ['worker', host.workerSampleCount[i]],
      ]);
      if (counts) {
        parts.push(counts);
      }
      const means = this.listText(
        'detail.means',
        [
          ['api', apiMean[i]],
          ['worker', workerMean[i]],
        ],
        unit,
      );
      if (means) {
        parts.push(means);
      }
      return parts.length > 0 ? parts.join(' · ') : null;
    });
  }

  /** "Api 120, Worker 118" under a translated lead-in; null when no source has a value. */
  private listText(
    key: string,
    entries: [SourceName, number | null | undefined][],
    unit?: ChartUnit,
  ): string | null {
    const list = entries
      .filter((entry): entry is [SourceName, number] => entry[1] !== null && entry[1] !== undefined)
      .map(
        ([source, value]) =>
          `${this.sourceName(source)} ${unit ? this.format(value, unit) : new Intl.NumberFormat(this.locale()).format(value)}`,
      );
    return list.length > 0
      ? this.transloco.translate(`admin.dashboard.load.${key}`, { list: list.join(', ') })
      : null;
  }

  private sourceName(source: SourceName): string {
    return this.transloco.translate(`admin.dashboard.load.source.${source}`);
  }

  private format(value: number, unit: ChartUnit): string {
    const word =
      unit === 'cores' || unit === 'tasks'
        ? this.transloco.translate(`admin.dashboard.chart.unitWord.${unit}`)
        : '';
    return formatUnit(value, unit, this.locale(), word);
  }

  private formatDateTime(iso: string): string {
    return new Date(iso).toLocaleString(this.locale(), { dateStyle: 'short', timeStyle: 'medium' });
  }

  private locale(): string {
    return toLocale(this.languageService.lang());
  }
}
