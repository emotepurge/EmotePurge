import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, Observable, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  AdminDashboardHostSeries,
  AdminDashboardLoad,
  AdminDashboardLoadRange,
  AdminDashboardProcessSeries,
} from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { AdminLoadSection } from './admin-load-section';

const EN = {
  admin: {
    dashboard: {
      chart: {
        noData: 'no data',
        noRange: 'none',
        legend: { sum: 's', max: 'm' },
        incomplete: 'i',
        incompleteShort: 'i',
        cursorHint: '{{ title }}',
        summary: '{{ label }}',
        utc: 'UTC',
        unitWord: { cores: 'cores', tasks: 'tasks' },
        unit: {
          count: 'count',
          percent: 'percent',
          bytes: 'bytes',
          cores: 'cores',
          tasks: 'tasks',
        },
      },
      load: {
        title: 'Load and work',
        stale: 'Stale since {{ at }}',
        dataThrough: 'Data through {{ at }}',
        shownRange: 'Still showing: {{ range }}',
        range: { label: 'Range', '1h': '1 h', '24h': '24 h', '7d': '7 d', '13mo': '13 mo' },
        stat: { raw: 'P95', hourly: 'highest hourly P95' },
        charts: {
          hostCpu: 'Host CPU ({{ stat }})',
          hostMemory: 'Host RAM ({{ stat }})',
          chatLines: {
            perMinute: 'Chat lines per minute',
            perBucket: 'Chat lines per {{ hours }} h',
          },
        },
        note: {
          memoryShareHost: 'RAM share {{ percent }}',
          memoryTotal: 'Total {{ total }}',
          quotaShareHourly: 'No quota share in hourly values',
        },
        empty: {
          waitingTitle: 'Waiting',
          waitingDescription: 'Waiting for first samples',
          rangeTitle: 'Empty range',
          sinceTitle: 'Empty range with history',
          sinceDescription: 'No samples for {{ range }}; hourly from {{ since }}',
        },
        coverage: { hours: 'Only {{ count }} hours', days: 'Only {{ count }} days' },
        detail: { sources: 'sources: {{ list }}', valueFrom: 'from {{ source }}' },
        source: { api: 'Api', worker: 'Worker' },
      },
    },
  },
  errors: { status: { server: 'Server error' } },
};

const NO_SERIES = (n: number): (number | null)[] => Array<number | null>(n).fill(null);
const nums = (...values: (number | null)[]) => values;

function process(n: number, overrides: Partial<AdminDashboardProcessSeries> = {}) {
  const none = NO_SERIES(n);
  return {
    processCpuP95: [...none],
    processCpuMax: [...none],
    cgroupCpuP95: [...none],
    cgroupCpuMax: [...none],
    cgroupCpuQuotaCores: [...none],
    cgroupCpuQuotaShareP95: [...none],
    cgroupCpuQuotaShareMax: [...none],
    cgroupMemoryP95Bytes: [...none],
    cgroupMemoryMaxBytes: [...none],
    cgroupMemoryLimitBytes: [...none],
    cgroupMemoryShareP95: [...none],
    cgroupMemoryShareMax: [...none],
    sampleCount: [...none],
    ...overrides,
  } satisfies AdminDashboardProcessSeries;
}

function load(
  range: AdminDashboardLoadRange,
  overrides: Omit<Partial<AdminDashboardLoad>, 'host'> & {
    host?: Partial<AdminDashboardHostSeries>;
  } = {},
): AdminDashboardLoad {
  const hourly = range !== '1h' && range !== '24h';
  const n = 3;
  const none = NO_SERIES(n);
  const { host, ...rest } = overrides;
  return {
    available: true,
    range,
    sourceKind: hourly ? 'hourly' : 'raw',
    bucketSeconds: { '1h': 60, '24h': 300, '7d': 3600, '30d': 7200, '90d': 21600, '13mo': 86400 }[
      range
    ],
    fromUtc: '2026-10-11T10:00:00Z',
    toUtc: '2026-10-11T12:00:00Z',
    generatedAtUtc: '2026-10-11T12:00:00Z',
    dataThroughUtc: '2026-10-11T12:00:00Z',
    samplingIntervalSeconds: 30,
    bucketStartsUtc: ['2026-10-11T10:00:00Z', '2026-10-11T11:00:00Z', '2026-10-11T12:00:00Z'],
    host: {
      cpuBusyP95: nums(10, 12, 14),
      cpuBusyMax: nums(20, 22, 24),
      memoryUsedP95Bytes: nums(1_000, 2_000, 4_000),
      memoryUsedMaxBytes: nums(1_100, 2_100, 4_100),
      memoryTotalBytes: nums(8_000, 8_000, 8_000),
      loadAverage1P95: [...none],
      loadAverage1Max: [...none],
      provenance: ['AW', 'AW', 'W'],
      cpuValueSource: ['A', 'W', 'W'],
      memoryValueSource: [...none].map(() => null),
      loadAverageValueSource: [...none].map(() => null),
      apiSampleCount: nums(10, 10, 10),
      workerSampleCount: nums(10, 10, 10),
      apiCpuBusyMean: [...none],
      workerCpuBusyMean: [...none],
      apiMemoryUsedMeanBytes: [...none],
      workerMemoryUsedMeanBytes: [...none],
      apiLoadAverage1Mean: [...none],
      workerLoadAverage1Mean: [...none],
      ...host,
    },
    api: process(n),
    worker: process(n),
    workload: {
      workloadUnit: hourly ? 'perBucket' : 'perMinute',
      activeChannelsMax: nums(5, 5, 6),
      liveChannelsMax: nums(1, 2, 2),
      chatLines: nums(100, 200, 300),
      emoteMatches: [...none],
      workloadIntervalSeconds: [...none],
      workloadHoursWithData: [...none],
    },
    swap: null,
    ...rest,
  };
}

const EMPTY = (range: AdminDashboardLoadRange): AdminDashboardLoad => ({
  ...load(range),
  available: false,
  dataThroughUtc: null,
  bucketStartsUtc: [],
});

const serverError = () => throwError(() => new HttpErrorResponse({ status: 503 }));

describe('AdminLoadSection', () => {
  let fixture: ComponentFixture<AdminLoadSection>;
  let answers: Partial<Record<AdminDashboardLoadRange, () => Observable<AdminDashboardLoad>>>;
  let getLoad: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    answers = {};
    getLoad = vi.fn((range: AdminDashboardLoadRange) =>
      (answers[range] ?? (() => of(EMPTY(range))))(),
    );
    await TestBed.configureTestingModule({
      imports: [
        AdminLoadSection,
        TranslocoTestingModule.forRoot({
          langs: { en: EN },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [{ provide: AdminService, useValue: { getDashboardLoad: getLoad } }],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('en'));
  });

  afterEach(() => {
    vi.useRealTimers();
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
  });

  function mount(): HTMLElement {
    fixture = TestBed.createComponent(AdminLoadSection);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const byId = (root: HTMLElement, id: string) =>
    root.querySelector<HTMLElement>(`[data-testid="${id}"]`);
  const chartIds = (root: HTMLElement) =>
    [...root.querySelectorAll('[data-testid^="load-chart-"]')].map((el) =>
      el.getAttribute('data-testid'),
    );
  const radios = (root: HTMLElement) => [...root.querySelectorAll<HTMLElement>('[role="radio"]')];

  function choose(root: HTMLElement, index: number): void {
    radios(root)[index].click();
    fixture.detectChanges();
  }

  describe('charts', () => {
    it('draws the same eleven charts in every range and only retitles them', () => {
      answers['24h'] = () => of(load('24h'));
      answers['7d'] = () => of(load('7d'));
      answers['13mo'] = () => of(load('13mo'));
      const root = mount();
      const rawIds = chartIds(root);
      expect(rawIds).toHaveLength(11);
      expect(byId(root, 'load-chart-host-cpu')?.textContent).toContain('Host CPU (P95)');
      expect(byId(root, 'load-chart-chatLines')?.textContent).toContain('Chat lines per minute');

      choose(root, 2); // 7 d
      expect(chartIds(root)).toEqual(rawIds);
      expect(byId(root, 'load-chart-host-cpu')?.textContent).toContain(
        'Host CPU (highest hourly P95)',
      );
    });

    it('labels the stored interval counters by the real bucket width, never per hour or per minute', () => {
      answers['24h'] = () => of(load('24h'));
      answers['7d'] = () => of(load('7d'));
      answers['13mo'] = () => of(load('13mo'));
      const root = mount();
      choose(root, 2);
      expect(byId(root, 'load-chart-chatLines')?.textContent).toContain('Chat lines per 1 h');

      answers['7d'] = () => of(load('7d', { bucketSeconds: 21_600 }));
      fixture.componentInstance.reload();
      fixture.detectChanges();

      expect(byId(root, 'load-chart-chatLines')?.textContent).toContain('Chat lines per 6 h');
      expect(byId(root, 'load-chart-chatLines')?.textContent).not.toContain('per minute');
    });

    it('derives the host RAM share from the raw used/total pair, and only for raw ranges', () => {
      answers['24h'] = () => of(load('24h'));
      answers['7d'] = () => of(load('7d'));
      answers['13mo'] = () => of(load('13mo'));
      const root = mount();
      // 4 000 of 8 000 bytes at the latest bucket.
      expect(byId(root, 'load-chart-host-memory')?.textContent).toContain('RAM share 50.0 %');

      choose(root, 2);
      expect(byId(root, 'load-chart-host-memory')?.textContent).not.toContain('RAM share');
    });

    it('shows no RAM share against an unknown or zero total', () => {
      answers['24h'] = () => of(load('24h', { host: { memoryTotalBytes: [null, null, 0] } }));
      const root = mount();

      expect(byId(root, 'load-chart-host-memory')?.textContent).not.toContain('RAM share');
    });

    it('keeps the quota-share chart in hourly ranges and explains that it has no quota there', () => {
      answers['24h'] = () => of(load('24h'));
      answers['7d'] = () => of(load('7d'));
      answers['13mo'] = () => of(load('13mo'));
      const root = mount();
      expect(byId(root, 'load-chart-cgroup-cpu-share')?.textContent).not.toContain(
        'No quota share',
      );

      choose(root, 2);

      expect(byId(root, 'load-chart-cgroup-cpu-share')).not.toBeNull();
      expect(byId(root, 'load-chart-cgroup-cpu-share')?.textContent).toContain('No quota share');
    });

    it('reads sources and the winning source of a host bucket at the cursor', () => {
      answers['24h'] = () => of(load('24h'));
      const root = mount();
      const group = byId(root, 'load-chart-host-cpu')?.querySelector('[role="group"]');

      group?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
      fixture.detectChanges();

      const line = byId(root, 'load-chart-host-cpu')?.querySelector('[aria-live="polite"]');
      expect(line?.textContent).toContain('sources: Api, Worker');
      expect(line?.textContent).toContain('from Api');
    });
  });

  describe('empty state and the history probe', () => {
    it('waits for samples and offers no range menu while neither range holds any', () => {
      const root = mount();

      expect(byId(root, 'load-empty')?.textContent).toContain('Waiting for first samples');
      expect(radios(root)).toHaveLength(0);
      expect(chartIds(root)).toHaveLength(0);
      expect(getLoad).toHaveBeenCalledWith('13mo');
    });

    it('probes again on every poll while both ranges are empty', () => {
      mount();
      const probes = () => getLoad.mock.calls.filter(([range]) => range === '13mo').length;
      expect(probes()).toBe(1);

      fixture.componentInstance.reload();
      fixture.detectChanges();

      expect(probes()).toBe(2);
    });

    it('stops probing once the probe has found history', () => {
      answers['13mo'] = () => of(load('13mo'));
      mount();
      const probes = () => getLoad.mock.calls.filter(([range]) => range === '13mo').length;
      expect(probes()).toBe(1);

      fixture.componentInstance.reload();
      fixture.detectChanges();

      expect(probes()).toBe(1);
    });

    it('shows the menu and names the hourly history when only the 13-month probe has data', () => {
      answers['13mo'] = () =>
        of(load('13mo', { bucketStartsUtc: ['2026-09-01T00:00:00Z', '2026-09-02T00:00:00Z'] }));
      const root = mount();

      expect(radios(root)).toHaveLength(6);
      expect(byId(root, 'load-empty')?.textContent).toContain('hourly from');
      expect(byId(root, 'load-empty')?.textContent).toContain('24 h');
    });

    it('keeps the menu after the user lands on a range without samples', () => {
      answers['24h'] = () => of(load('24h'));
      const root = mount();
      expect(radios(root)).toHaveLength(6);

      choose(root, 0); // 1 h is empty

      expect(radios(root)).toHaveLength(6);
      expect(byId(root, 'load-empty')).not.toBeNull();
    });

    it('notes how little history a range holds when leading buckets are missing', () => {
      answers['24h'] = () =>
        of(
          load('24h', {
            fromUtc: '2026-10-10T12:00:00Z',
            bucketStartsUtc: [
              '2026-10-11T10:00:00Z',
              '2026-10-11T11:00:00Z',
              '2026-10-11T12:00:00Z',
            ],
          }),
        );
      const root = mount();

      expect(byId(root, 'load-coverage')?.textContent).toContain('Only 2 hours');
    });

    it('states the data cut-off of the shown range', () => {
      answers['24h'] = () => of(load('24h'));
      const root = mount();

      expect(byId(root, 'load-data-through')).not.toBeNull();
    });
  });

  describe('stale marking', () => {
    it('keeps the last answer on screen and marks it stale after a failed or busy read, until a read succeeds', () => {
      answers['24h'] = () => of(load('24h'));
      const root = mount();
      expect(byId(root, 'load-stale')).toBeNull();

      answers['24h'] = serverError;
      fixture.componentInstance.reload();
      fixture.detectChanges();

      expect(byId(root, 'load-stale')).not.toBeNull();
      expect(chartIds(root)).toHaveLength(11);

      answers['24h'] = () => of(load('24h'));
      fixture.componentInstance.reload();
      fixture.detectChanges();

      expect(byId(root, 'load-stale')).toBeNull();
      expect(chartIds(root)).toHaveLength(11);
    });

    it('names the failure instead of a stale mark when nothing ever loaded', () => {
      answers['24h'] = serverError;
      const root = mount();

      expect(byId(root, 'load-stale')).toBeNull();
      expect(byId(root, 'load-error')?.textContent).toContain('Server error');
    });

    it('keeps showing the earlier range, stale, when a range switch fails', () => {
      answers['24h'] = () => of(load('24h'));
      answers['7d'] = serverError;
      const root = mount();

      choose(root, 2);

      expect(byId(root, 'load-stale')).not.toBeNull();
      expect(byId(root, 'load-shown-range')?.textContent).toContain('24 h');
      expect(chartIds(root)).toHaveLength(11);
    });
  });

  describe('trend axis for the rate-limit series', () => {
    it('is the load axis up to 24 hours, hidden for longer ranges and the trend’s own without data', () => {
      const root = mount();
      expect(fixture.componentInstance.trendAxis().kind).toBe('own');

      answers['24h'] = () => of(load('24h'));
      answers['7d'] = () => of(load('7d'));
      answers['13mo'] = () => of(load('13mo'));
      fixture.componentInstance.reload();
      fixture.detectChanges();
      const axis = fixture.componentInstance.trendAxis();
      expect(axis).toMatchObject({ kind: 'load', bucketSeconds: 300 });

      choose(root, 2);
      expect(fixture.componentInstance.trendAxis().kind).toBe('hidden');
    });
  });

  describe('polling', () => {
    const setVisibility = (state: string) =>
      Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
    const loads = (range: AdminDashboardLoadRange) =>
      getLoad.mock.calls.filter(([r]) => r === range).length;

    it('reloads every minute for 24 hours but only every five minutes from 7 days on', () => {
      vi.useFakeTimers();
      answers['24h'] = () => of(load('24h'));
      answers['7d'] = () => of(load('7d'));
      answers['13mo'] = () => of(load('13mo'));
      const root = mount();
      expect(loads('24h')).toBe(1);

      vi.advanceTimersByTime(60_000);
      fixture.detectChanges();
      expect(loads('24h')).toBe(2);

      choose(root, 2);
      expect(loads('7d')).toBe(1);
      vi.advanceTimersByTime(4 * 60_000);
      fixture.detectChanges();
      expect(loads('7d')).toBe(1);
      vi.advanceTimersByTime(60_000);
      fixture.detectChanges();
      expect(loads('7d')).toBe(2);
    });

    it('reloads at once when the tab returns, whatever the range’s cadence', () => {
      vi.useFakeTimers();
      answers['24h'] = () => of(load('24h'));
      answers['7d'] = () => of(load('7d'));
      answers['13mo'] = () => of(load('13mo'));
      const root = mount();
      vi.advanceTimersByTime(1); // the skipped first tick of a page that opened visible
      choose(root, 2);
      expect(loads('7d')).toBe(1);

      setVisibility('hidden');
      document.dispatchEvent(new Event('visibilitychange'));
      TestBed.tick();
      fixture.detectChanges();
      vi.advanceTimersByTime(60_000);
      setVisibility('visible');
      document.dispatchEvent(new Event('visibilitychange'));
      TestBed.tick();
      fixture.detectChanges();
      vi.advanceTimersByTime(1);
      fixture.detectChanges();

      expect(loads('7d')).toBe(2);
    });
  });
});
