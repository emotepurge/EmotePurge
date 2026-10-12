import { describe, expect, it } from 'vitest';

import { AdminDashboardLoad } from '../../core/admin/admin.model';
import { buildLoadCharts, LoadChartContext } from './admin-load-charts';

const STARTS = ['2026-10-11T11:00:00Z', '2026-10-11T12:00:00Z'];
const ctx: LoadChartContext = {
  locale: 'de-DE',
  translate: (key, params) => (params ? `${key} ${JSON.stringify(params)}` : key),
};

function columns(names: string[], value: unknown = null): Record<string, unknown[]> {
  return Object.fromEntries(names.map((name) => [name, STARTS.map(() => value)]));
}

const PROCESS = [
  'processCpuP95',
  'processCpuMax',
  'cgroupCpuP95',
  'cgroupCpuMax',
  'cgroupCpuQuotaCores',
  'cgroupCpuQuotaShareP95',
  'cgroupCpuQuotaShareMax',
  'cgroupMemoryP95Bytes',
  'cgroupMemoryMaxBytes',
  'cgroupMemoryLimitBytes',
  'cgroupMemoryShareP95',
  'cgroupMemoryShareMax',
  'sampleCount',
];

function load(
  kind: 'raw' | 'hourly',
  overrides: { api?: Record<string, unknown>; host?: Record<string, unknown>; swap?: unknown } = {},
): AdminDashboardLoad {
  return {
    available: true,
    range: kind === 'raw' ? '24h' : '7d',
    sourceKind: kind,
    bucketSeconds: kind === 'raw' ? 300 : 3600,
    bucketStartsUtc: STARTS,
    host: {
      ...columns([
        'cpuBusyP95',
        'cpuBusyMax',
        'memoryUsedP95Bytes',
        'memoryUsedMaxBytes',
        'memoryTotalBytes',
        'loadAverage1P95',
        'loadAverage1Max',
        'provenance',
        'cpuValueSource',
        'memoryValueSource',
        'loadAverageValueSource',
        'apiSampleCount',
        'workerSampleCount',
        'apiCpuBusyMean',
        'workerCpuBusyMean',
        'apiMemoryUsedMeanBytes',
        'workerMemoryUsedMeanBytes',
        'apiLoadAverage1Mean',
        'workerLoadAverage1Mean',
      ]),
      ...overrides.host,
    },
    api: { ...columns(PROCESS), ...overrides.api },
    worker: columns(PROCESS),
    workload: {
      workloadUnit: kind === 'raw' ? 'perMinute' : 'perBucket',
      ...columns([
        'activeChannelsMax',
        'liveChannelsMax',
        'chatLines',
        'emoteMatches',
        'workloadHoursWithData',
      ]),
      workloadIntervalSeconds: [299.87, null],
    },
    swap: overrides.swap ?? null,
  } as unknown as AdminDashboardLoad;
}

const chart = (l: AdminDashboardLoad, id: string) =>
  buildLoadCharts(l, ctx).find((c) => c.id === id)!;
const noteKeys = (l: AdminDashboardLoad, id: string) => chart(l, id).notes.map((n) => n.key);

describe('buildLoadCharts', () => {
  it('always yields the same eleven charts, whatever the source', () => {
    const raw = buildLoadCharts(load('raw'), ctx).map((c) => c.id);
    expect(raw).toHaveLength(11);
    expect(buildLoadCharts(load('hourly'), ctx).map((c) => c.id)).toEqual(raw);
  });

  describe('swap', () => {
    const swap = {
      lastSwapUsedBytes: 1_048_576,
      lastSwapTotalBytes: 2_097_152,
      observedAtUtc: '2026-10-11T12:00:00Z',
    };

    it('is a note on the host memory chart in raw ranges when swap exists', () => {
      expect(noteKeys(load('raw', { swap }), 'host-memory')).toContain('swap');
    });

    it('is not shown without swap, with a zero total, or in hourly ranges', () => {
      expect(noteKeys(load('raw'), 'host-memory')).not.toContain('swap');
      expect(
        noteKeys(load('raw', { swap: { ...swap, lastSwapTotalBytes: 0 } }), 'host-memory'),
      ).not.toContain('swap');
      expect(noteKeys(load('hourly', { swap }), 'host-memory')).not.toContain('swap');
    });
  });

  describe('limits that were removed', () => {
    it('does not keep showing a limit from an older bucket once the newest sampled bucket has none', () => {
      const api = {
        sampleCount: [5, 5],
        cgroupMemoryLimitBytes: [536_870_912, null],
        cgroupCpuQuotaCores: [1, null],
      };
      const l = load('raw', { api });

      expect(noteKeys(l, 'cgroup-memory-api')).toContain('memoryLimitNone');
      expect(chart(l, 'cgroup-cpu').notes[0].key).toBe('cpuQuotaNone');
    });

    it('shows the limit of the newest bucket that has samples, skipping trailing empty buckets', () => {
      const api = {
        sampleCount: [5, null],
        cgroupMemoryLimitBytes: [536_870_912, null],
      };

      expect(noteKeys(load('raw', { api }), 'cgroup-memory-api')).toContain('memoryLimit');
    });

    it('shows the host RAM total of the newest sampled bucket only', () => {
      const host = { apiSampleCount: [3, 3], memoryTotalBytes: [8_000_000_000, null] };

      expect(noteKeys(load('raw', { host }), 'host-memory')).toContain('memoryTotalNone');
    });
  });

  it('names the maxima of process and container series in the reading text', () => {
    const api = { sampleCount: [5, 5], processCpuMax: [0.5, 0.75] };
    const raw = chart(load('raw', { api }), 'process-cpu').details[0];
    const hourly = chart(load('hourly', { api }), 'process-cpu').details[0];

    expect(raw).toContain('admin.dashboard.load.detail.max.raw');
    expect(hourly).toContain('admin.dashboard.load.detail.max.hourly');
  });

  it('formats the interval seconds with the locale, not as a raw float', () => {
    const detail = chart(load('raw'), 'chatLines').details[0];

    expect(detail).toContain('299,9');
    expect(detail).not.toContain('299.87');
  });
});
