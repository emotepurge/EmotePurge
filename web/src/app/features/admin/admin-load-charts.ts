import { AdminDashboardLoad, AdminDashboardProcessSeries } from '../../core/admin/admin.model';
import {
  ChartUnit,
  formatBucketTime,
  formatUnit,
  provenanceSources,
} from '../../shared/charts/chart-series';
import { LineChartSeries } from '../../shared/charts/line-chart';

const HOUR_SECONDS = 3_600;

/** What the mapping needs from the outside: a translator and the locale for numbers and times. */
export interface LoadChartContext {
  translate: (key: string, params?: Record<string, unknown>) => string;
  locale: string;
}

export interface LoadChartNote {
  key: string;
  params?: Record<string, unknown>;
}

export interface LoadChart {
  id: string;
  titleKey: string;
  titleParams: Record<string, unknown>;
  unit: ChartUnit;
  series: LineChartSeries[];
  details: (string | null)[];
  notes: LoadChartNote[];
}

type SourceName = 'api' | 'worker';
type Column = readonly (number | null)[];

/**
 * The value in the newest bucket that has samples of its own, even if that value is unknown. A
 * limit that disappeared must not keep showing as "last measured" from an older bucket.
 */
function latestWithSamples(
  values: Column,
  sampleCounts: readonly Column[],
): { found: boolean; value: number | null } {
  for (let i = values.length - 1; i >= 0; i--) {
    if (sampleCounts.some((counts) => (counts[i] ?? 0) > 0)) {
      const value = values[i];
      return { found: true, value: value !== null && Number.isFinite(value) ? value : null };
    }
  }
  return { found: false, value: null };
}

function lastKnown(values: Column): number | null {
  for (let i = values.length - 1; i >= 0; i--) {
    if (values[i] !== null && Number.isFinite(values[i])) {
      return values[i];
    }
  }
  return null;
}

/**
 * Maps one complete load answer to the eleven fixed charts with their notes and per-bucket reading
 * text. Pure: texts come through `ctx`, so the range only changes titles and values, never the set.
 */
export function buildLoadCharts(load: AdminDashboardLoad, ctx: LoadChartContext): LoadChart[] {
  const raw = load.sourceKind === 'raw';
  const kind = raw ? 'raw' : 'hourly';
  const stat = ctx.translate(`admin.dashboard.load.stat.${kind}`);
  const p95Key = `admin.dashboard.load.series.p95.${kind}`;
  const maxKey = `admin.dashboard.load.series.max.${kind}`;
  const { host, api, worker, workload } = load;
  const bucketHours = load.bucketSeconds / HOUR_SECONDS;
  const number = new Intl.NumberFormat(ctx.locale);
  const sourceName = (source: SourceName) => ctx.translate(`admin.dashboard.load.source.${source}`);
  const format = (value: number, unit: ChartUnit) => {
    const word =
      unit === 'cores' || unit === 'tasks'
        ? ctx.translate(`admin.dashboard.chart.unitWord.${unit}`)
        : '';
    return formatUnit(value, unit, ctx.locale, word);
  };
  /** "Api 120, Worker 118" under a translated lead-in; null when no source has a value. */
  const listText = (
    key: string,
    entries: [SourceName, number | null | undefined][],
    unit?: ChartUnit,
  ): string | null => {
    const list = entries
      .filter((entry): entry is [SourceName, number] => entry[1] !== null && entry[1] !== undefined)
      .map(
        ([source, value]) =>
          `${sourceName(source)} ${unit ? format(value, unit) : number.format(value)}`,
      );
    return list.length > 0
      ? ctx.translate(`admin.dashboard.load.${key}`, { list: list.join(', ') })
      : null;
  };
  const join = (parts: (string | null)[]) => {
    const present = parts.filter((part): part is string => part !== null);
    return present.length > 0 ? present.join(' · ') : null;
  };
  const maxText = (i: number, unit: ChartUnit, pick: (s: AdminDashboardProcessSeries) => Column) =>
    listText(
      `detail.max.${kind}`,
      [
        ['api', pick(api)[i]],
        ['worker', pick(worker)[i]],
      ],
      unit,
    );
  const samplesText = (i: number) =>
    listText('detail.samples', [
      ['api', api.sampleCount[i]],
      ['worker', worker.sampleCount[i]],
    ]);

  const hostSeries = (p95: (number | null)[], max: (number | null)[], id: string) => [
    { id: `${id}-p95`, labelKey: p95Key, values: p95 },
    { id: `${id}-max`, labelKey: maxKey, values: max, emphasis: 'marks' as const },
  ];
  const bySource = (id: string, pick: (s: AdminDashboardProcessSeries) => (number | null)[]) => [
    { id: `${id}-api`, labelKey: 'admin.dashboard.load.series.api', values: pick(api) },
    { id: `${id}-worker`, labelKey: 'admin.dashboard.load.series.worker', values: pick(worker) },
  ];
  const processDetails = (unit: ChartUnit, pick: (s: AdminDashboardProcessSeries) => Column) =>
    load.bucketStartsUtc.map((_, i) => join([samplesText(i), maxText(i, unit, pick)]));

  const hostDetails = (metric: 'cpu' | 'memory' | 'load', unit: ChartUnit) => {
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
      const sources = provenanceSources(host.provenance[i] ?? null);
      const winner = winners[i];
      return join([
        sources.length > 0
          ? ctx.translate('admin.dashboard.load.detail.sources', {
              list: sources.map(sourceName).join(', '),
            })
          : null,
        winner
          ? ctx.translate('admin.dashboard.load.detail.valueFrom', {
              source: sourceName(winner === 'A' ? 'api' : 'worker'),
            })
          : null,
        listText('detail.samples', [
          ['api', host.apiSampleCount[i]],
          ['worker', host.workerSampleCount[i]],
        ]),
        listText(
          'detail.means',
          [
            ['api', apiMean[i]],
            ['worker', workerMean[i]],
          ],
          unit,
        ),
      ]);
    });
  };

  const cgroupMemory = (source: SourceName): LoadChart => {
    const series = source === 'api' ? api : worker;
    const limit = latestWithSamples(series.cgroupMemoryLimitBytes, [series.sampleCount]);
    const share = raw ? lastKnown(series.cgroupMemoryShareP95) : null;
    return {
      id: `cgroup-memory-${source}`,
      titleKey: 'admin.dashboard.load.charts.cgroupMemory',
      titleParams: { source: sourceName(source), stat },
      unit: 'bytes',
      series: [
        { id: `cgroup-memory-${source}`, labelKey: p95Key, values: series.cgroupMemoryP95Bytes },
      ],
      details: load.bucketStartsUtc.map((_, i) =>
        join([
          listText('detail.samples', [[source, series.sampleCount[i]]]),
          listText('detail.max.' + kind, [[source, series.cgroupMemoryMaxBytes[i]]], 'bytes'),
        ]),
      ),
      notes: [
        limit.value === null
          ? { key: 'memoryLimitNone' }
          : { key: 'memoryLimit', params: { limit: format(limit.value, 'bytes') } },
        ...(share === null
          ? []
          : [{ key: 'memoryShare', params: { percent: format(share, 'percent') } }]),
      ],
    };
  };
  const quotaNotes = (): LoadChartNote[] =>
    (['api', 'worker'] as const).map((source) => {
      const series = source === 'api' ? api : worker;
      const quota = latestWithSamples(series.cgroupCpuQuotaCores, [series.sampleCount]).value;
      return quota === null
        ? { key: 'cpuQuotaNone', params: { source: sourceName(source) } }
        : {
            key: 'cpuQuota',
            params: { source: sourceName(source), cores: format(quota, 'cores') },
          };
    });
  const workloadChart = (
    id: 'chatLines' | 'emoteMatches',
    values: (number | null)[],
  ): LoadChart => ({
    id,
    titleKey: `admin.dashboard.load.charts.${id}.${workload.workloadUnit}`,
    titleParams: { hours: number.format(bucketHours) },
    unit: 'count',
    series: [{ id, labelKey: `admin.dashboard.load.series.${id}`, values }],
    details: load.bucketStartsUtc.map((_, i) => {
      if (workload.workloadUnit === 'perMinute') {
        const seconds = workload.workloadIntervalSeconds[i];
        return seconds === null || seconds === undefined
          ? null
          : ctx.translate('admin.dashboard.load.detail.intervalSeconds', {
              seconds: new Intl.NumberFormat(ctx.locale, { maximumFractionDigits: 1 }).format(
                seconds,
              ),
            });
      }
      const hours = workload.workloadHoursWithData[i];
      return hours === null || hours === undefined
        ? null
        : ctx.translate('admin.dashboard.load.detail.hoursWithData', { hours });
    }),
    notes: [],
  });

  // Host RAM total and share: from the newest bucket that has host samples; the share is derived
  // from the raw used/total pair of one bucket and only against a finite, known total.
  const hostSamples = [host.apiSampleCount, host.workerSampleCount];
  const total = latestWithSamples(host.memoryTotalBytes, hostSamples).value;
  const pairIndex = [...host.memoryUsedP95Bytes.keys()]
    .reverse()
    .find((i) => host.memoryUsedP95Bytes[i] !== null && (host.memoryTotalBytes[i] ?? 0) > 0);
  const memoryShare =
    raw && pairIndex !== undefined
      ? (100 * (host.memoryUsedP95Bytes[pairIndex] as number)) /
        (host.memoryTotalBytes[pairIndex] as number)
      : null;
  const swap = raw && load.swap && load.swap.lastSwapTotalBytes > 0 ? load.swap : null;

  return [
    {
      id: 'host-cpu',
      titleKey: 'admin.dashboard.load.charts.hostCpu',
      titleParams: { stat },
      unit: 'percent',
      series: hostSeries(host.cpuBusyP95, host.cpuBusyMax, 'host-cpu'),
      details: hostDetails('cpu', 'percent'),
      notes: [],
    },
    {
      id: 'host-memory',
      titleKey: 'admin.dashboard.load.charts.hostMemory',
      titleParams: { stat },
      unit: 'bytes',
      series: hostSeries(host.memoryUsedP95Bytes, host.memoryUsedMaxBytes, 'host-memory'),
      details: hostDetails('memory', 'bytes'),
      notes: [
        total === null
          ? { key: 'memoryTotalNone' }
          : { key: 'memoryTotal', params: { total: format(total, 'bytes') } },
        ...(memoryShare === null
          ? []
          : [{ key: 'memoryShareHost', params: { percent: format(memoryShare, 'percent') } }]),
        ...(swap
          ? [
              {
                key: 'swap',
                params: {
                  at: formatBucketTime(swap.observedAtUtc, 1, ctx.locale),
                  used: format(swap.lastSwapUsedBytes, 'bytes'),
                  total: format(swap.lastSwapTotalBytes, 'bytes'),
                },
              },
            ]
          : []),
      ],
    },
    {
      id: 'host-load',
      titleKey: 'admin.dashboard.load.charts.hostLoad',
      titleParams: { stat },
      unit: 'tasks',
      series: hostSeries(host.loadAverage1P95, host.loadAverage1Max, 'host-load'),
      details: hostDetails('load', 'tasks'),
      notes: [],
    },
    {
      id: 'process-cpu',
      titleKey: 'admin.dashboard.load.charts.processCpu',
      titleParams: { stat },
      unit: 'cores',
      series: bySource('process-cpu', (s) => s.processCpuP95),
      details: processDetails('cores', (s) => s.processCpuMax),
      notes: [],
    },
    {
      id: 'cgroup-cpu',
      titleKey: 'admin.dashboard.load.charts.cgroupCpu',
      titleParams: { stat },
      unit: 'cores',
      series: bySource('cgroup-cpu', (s) => s.cgroupCpuP95),
      details: processDetails('cores', (s) => s.cgroupCpuMax),
      notes: quotaNotes(),
    },
    {
      // Stays in every range so the chart count never changes; hourly rows carry no share.
      id: 'cgroup-cpu-share',
      titleKey: 'admin.dashboard.load.charts.cgroupCpuShare',
      titleParams: { stat },
      unit: 'percent',
      series: bySource('cgroup-cpu-share', (s) => s.cgroupCpuQuotaShareP95),
      details: processDetails('percent', (s) => s.cgroupCpuQuotaShareMax),
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
