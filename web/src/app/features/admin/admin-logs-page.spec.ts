import { BreakpointObserver } from '@angular/cdk/layout';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { DebugElement } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import en from '../../../../public/i18n/en.json';
import { LogEntry, LogPage, LogStatus, LogSummary } from '../../core/admin/admin-logs.model';
import { TimeHistogram } from '../../shared/charts/time-histogram';
import { AdminLogsPage, LOG_LIST_LIMIT, parseLogRange } from './admin-logs-page';

function entry(id: number): LogEntry {
  return {
    id,
    timestampUtc: '2026-10-14T12:00:00Z',
    level: 'info',
    source: 'worker',
    instanceId: 'a1b2c3d4',
    category: 'EmotePurge.Worker.X',
    eventId: null,
    eventName: null,
    message: `message ${id}`,
    hasException: false,
    exceptionType: null,
    properties: {},
    truncated: false,
  };
}

function page(from: number, count: number, nextCursor: string | null): LogPage {
  return {
    entries: Array.from({ length: count }, (_, i) => entry(from - i)),
    nextCursor,
    rangeFromUtc: '2026-10-13T12:00:00Z',
    rangeToUtc: '2026-10-14T12:00:00Z',
    truncated: false,
  };
}

function summary(overrides: Partial<LogSummary> = {}): LogSummary {
  return {
    rangeFromUtc: '2026-10-13T12:00:00Z',
    rangeToUtc: '2026-10-14T12:00:00Z',
    bucketFromUtc: '2026-10-13T12:00:00Z',
    bucketToUtc: '2026-10-14T12:00:00Z',
    bucketSeconds: 900,
    bucketStartsUtc: ['2026-10-13T12:00:00Z', '2026-10-13T12:15:00Z'],
    counts: { info: [1, 2], warn: [0, 1], error: [0, 0] },
    total: 4,
    facets: {
      source: [
        { value: 'api', count: 1 },
        { value: 'worker', count: 3 },
      ],
      level: [{ value: 'info', count: 3 }],
      category: [],
      categoryOther: 0,
    },
    dropped: {
      total: 0,
      queueFull: 0,
      budget: 0,
      writeUnconfirmed: 0,
      captureFailed: 0,
      reentrant: 0,
      rejected: 0,
    },
    processStarts: [],
    ...overrides,
  };
}

function healthyStatus(overrides: Partial<LogStatus> = {}): LogStatus {
  const source = (name: 'api' | 'worker') => ({
    source: name,
    state: 'healthy' as const,
    instanceId: 'a1b2c3d4',
    startedUtc: '2026-10-14T08:00:00Z',
    lastFlushUtc: '2026-10-14T11:59:30Z',
  });
  return {
    generatedAtUtc: '2026-10-14T12:00:00Z',
    sources: [source('api'), source('worker')],
    coverage24h: { complete: true, recordedErrors: 0, incompleteSince: [] },
    retention: { oldestEntryUtc: '2026-10-08T12:00:00Z', overdue: false },
    ...overrides,
  };
}

/** Collects the observers the sentinel creates, so a test can scroll without a layout engine. */
class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = [];
  disconnected = false;
  constructor(private readonly callback: (hits: { isIntersecting: boolean }[]) => void) {
    FakeIntersectionObserver.instances.push(this);
  }
  observe(): void {
    /* no-op */
  }
  disconnect(): void {
    this.disconnected = true;
  }
  fire(): void {
    this.callback([{ isIntersecting: true }]);
  }
}

describe('AdminLogsPage', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let httpMock: HttpTestingController;
  let desktop: boolean;

  beforeEach(() => {
    desktop = true;
    FakeIntersectionObserver.instances = [];
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'admin/logs', component: AdminLogsPage }]),
        provideLocationMocks(),
        {
          provide: BreakpointObserver,
          useValue: {
            observe: () => of({ matches: desktop, breakpoints: {} }),
            isMatched: () => desktop,
          },
        },
      ],
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    httpMock?.verify();
  });

  const root = () => harness.fixture.nativeElement as HTMLElement;

  async function open(url: string): Promise<void> {
    await firstValueFrom(TestBed.inject(TranslocoService).load('en'));
    harness = await RouterTestingHarness.create();
    router = TestBed.inject(Router);
    router.setUpLocationChangeListener();
    httpMock = TestBed.inject(HttpTestingController);
    await harness.navigateByUrl(url, AdminLogsPage);
    harness.detectChanges();
  }

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    harness.detectChanges();
  }

  const listRequest = (match: (r: TestRequest['request']) => boolean = () => true) =>
    httpMock.expectOne(
      (r) =>
        r.url === '/api/admin/logs' &&
        !r.params.has('keysOnly') &&
        !r.params.has('ids') &&
        match(r),
    );
  const summaryRequest = (match: (r: TestRequest['request']) => boolean = () => true) =>
    httpMock.expectOne((r) => r.url === '/api/admin/logs/summary' && match(r));
  const statusRequest = () => httpMock.expectOne('/api/admin/logs/status');

  async function openAndLoad(
    url: string,
    options: { status?: LogStatus; page?: LogPage; summary?: LogSummary } = {},
  ): Promise<void> {
    await open(url);
    statusRequest().flush(options.status ?? healthyStatus());
    listRequest().flush(options.page ?? page(4, 4, null));
    summaryRequest().flush(options.summary ?? summary());
    await settle();
  }

  const input = () => root().querySelector<HTMLInputElement>('#admin-log-filter')!;
  const buttonNamed = (name: string) =>
    Array.from(root().querySelectorAll('button')).find(
      (b) => b.getAttribute('aria-label') === name || b.textContent?.trim() === name,
    )!;

  describe('parseLogRange', () => {
    it('reads the default, a preset and an absolute pair', () => {
      expect(parseLogRange('', '', '')).toEqual({ kind: 'preset', range: '24h' });
      expect(parseLogRange('7d', '', '')).toEqual({ kind: 'preset', range: '7d' });
      expect(parseLogRange('', '2026-10-14T10:00:00Z', '2026-10-14T11:00:00Z')).toEqual({
        kind: 'absolute',
        from: '2026-10-14T10:00:00Z',
        to: '2026-10-14T11:00:00Z',
      });
    });

    it('rejects an unknown preset, a half pair, a mixed request and an inverted pair', () => {
      expect(parseLogRange('2d', '', '').kind).toBe('invalid');
      expect(parseLogRange('', '2026-10-14T10:00:00Z', '').kind).toBe('invalid');
      expect(parseLogRange('7d', '2026-10-14T10:00:00Z', '2026-10-14T11:00:00Z').kind).toBe(
        'invalid',
      );
      expect(parseLogRange('', '2026-10-14T11:00:00Z', '2026-10-14T10:00:00Z').kind).toBe(
        'invalid',
      );
      expect(parseLogRange('', 'yesterday', 'today').kind).toBe('invalid');
    });
  });

  it('opens a deep link prefiltered: the filter is in the field and in both requests', async () => {
    await open('/admin/logs?q=level%3E%3Dwarn%20cat%3ATwitchChatManager&range=7d');
    statusRequest().flush(healthyStatus());
    const list = listRequest((r) => r.params.get('range') === '7d');
    expect(list.request.params.get('q')).toBe('level>=warn cat:TwitchChatManager');
    list.flush(page(4, 4, null));
    summaryRequest((r) => r.params.get('q') === 'level>=warn cat:TwitchChatManager').flush(
      summary(),
    );
    await settle();

    expect(input().value).toBe('level>=warn cat:TwitchChatManager');
  });

  it('applies the typed filter on submit, not while typing', async () => {
    await openAndLoad('/admin/logs');

    input().value = 'source:api';
    input().dispatchEvent(new Event('input'));
    await settle();
    httpMock.expectNone((r) => r.url.startsWith('/api/admin/logs'));

    root().querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
    statusRequest().flush(healthyStatus());
    listRequest((r) => r.params.get('q') === 'source:api').flush(page(4, 4, null));
    summaryRequest((r) => r.params.get('q') === 'source:api').flush(summary());
    await settle();

    expect(router.url).toContain('q=source:api');
  });

  it('appends a facet term, then an exclusion, to the applied filter', async () => {
    await openAndLoad('/admin/logs?q=level%3Eerror');

    buttonNamed('Add worker (3) to the filter').click();
    await settle();
    statusRequest().flush(healthyStatus());
    listRequest((r) => r.params.get('q') === 'level>error source:worker').flush(page(4, 4, null));
    summaryRequest().flush(summary());
    await settle();

    buttonNamed('Exclude api').click();
    await settle();
    statusRequest().flush(healthyStatus());
    listRequest((r) => r.params.get('q') === 'level>error source:worker -source:api').flush(
      page(4, 4, null),
    );
    summaryRequest().flush(summary());
  });

  it('narrows to a clicked bucket and offers the way back to the previous relative range', async () => {
    await openAndLoad('/admin/logs');
    expect(buttonNamed('Back to 24 h')).toBeUndefined();

    const histogram: DebugElement = harness.fixture.debugElement.query(By.directive(TimeHistogram));
    histogram.triggerEventHandler('bucketSelected', {
      fromUtc: '2026-10-13T12:15:00Z',
      toUtc: '2026-10-13T12:30:00Z',
    });
    await settle();
    statusRequest().flush(healthyStatus());
    const narrowed = listRequest((r) => r.params.get('from') === '2026-10-13T12:15:00Z');
    expect(narrowed.request.params.get('to')).toBe('2026-10-13T12:30:00Z');
    expect(narrowed.request.params.has('range')).toBe(false);
    narrowed.flush(page(4, 4, null));
    summaryRequest().flush(summary());
    await settle();

    expect(router.url).toContain('from=2026-10-13T12:15:00Z');
    buttonNamed('Back to 24 h').click();
    await settle();
    statusRequest().flush(healthyStatus());
    const back = listRequest((r) => r.params.get('range') === '24h');
    expect(back.request.params.has('from')).toBe(false);
    back.flush(page(4, 4, null));
    summaryRequest().flush(summary());
    await settle();

    expect(router.url).not.toContain('from=');
    expect(buttonNamed('Back to 24 h')).toBeUndefined();
  });

  it('keeps loading in steps of the cursor on the frozen bounds and stops at 2,000 rows', async () => {
    await openAndLoad('/admin/logs', { page: page(2000, 100, 'c1') });

    let loaded = 100;
    let cursor = 1;
    while (loaded < LOG_LIST_LIMIT) {
      const observer = FakeIntersectionObserver.instances.filter((o) => !o.disconnected).at(-1)!;
      observer.fire();
      const more = listRequest((r) => r.params.get('before') === `c${cursor}`);
      // Paging sends the first answer's exact bounds as an absolute range, never the preset.
      expect(more.request.params.get('from')).toBe('2026-10-13T12:00:00Z');
      expect(more.request.params.get('to')).toBe('2026-10-14T12:00:00Z');
      expect(more.request.params.has('range')).toBe(false);
      loaded += 100;
      cursor += 1;
      more.flush(page(2000 - loaded + 100, 100, `c${cursor}`));
      await settle();
    }

    expect(root().querySelectorAll('[data-log-id]').length).toBe(LOG_LIST_LIMIT);
    expect(root().querySelector('[data-testid="log-limit"]')?.textContent).toContain(
      'narrow the time range or the filter',
    );
    // No sentinel left to observe: nothing can ask for the 2,001st row.
    expect(FakeIntersectionObserver.instances.every((o) => o.disconnected)).toBe(true);
  });

  it('clears rows and histogram when the URL turns invalid after a valid load', async () => {
    await openAndLoad('/admin/logs');
    expect(root().querySelectorAll('[data-log-id]').length).toBe(4);

    await router.navigateByUrl('/admin/logs?range=2d');
    await settle();
    statusRequest().flush(healthyStatus());

    expect(root().querySelectorAll('[data-log-id]').length).toBe(0);
    expect(root().querySelector('app-time-histogram')).toBeNull();
    expect(root().textContent).toContain('The time range in the address is not valid');
  });

  it('reports a failed next page without the stale sentence and retries that page', async () => {
    await openAndLoad('/admin/logs', { page: page(200, 100, 'c1') });

    FakeIntersectionObserver.instances
      .filter((o) => !o.disconnected)
      .at(-1)!
      .fire();
    listRequest((r) => r.params.get('before') === 'c1').flush(null, {
      status: 500,
      statusText: 'x',
    });
    await settle();

    expect(root().textContent).toContain('More entries could not be loaded.');
    expect(root().textContent).not.toContain('out of date');
    expect(root().querySelectorAll('[data-log-id]').length).toBe(100);

    buttonNamed('Try again').click();
    const retry = listRequest((r) => r.params.get('before') === 'c1');
    expect(retry.request.params.get('from')).toBe('2026-10-13T12:00:00Z');
    retry.flush(page(100, 100, null));
    await settle();

    expect(root().querySelectorAll('[data-log-id]').length).toBe(200);
    expect(root().textContent).not.toContain('More entries could not be loaded.');
  });

  it('pages on the request bounds when the first answer names none', async () => {
    await openAndLoad('/admin/logs?from=2026-10-14T10%3A00%3A00Z&to=2026-10-14T11%3A00%3A00Z', {
      page: { ...page(200, 100, 'c1'), rangeFromUtc: null, rangeToUtc: null },
    });

    FakeIntersectionObserver.instances
      .filter((o) => !o.disconnected)
      .at(-1)!
      .fire();
    const more = listRequest((r) => r.params.get('before') === 'c1');
    expect(more.request.params.get('from')).toBe('2026-10-14T10:00:00Z');
    expect(more.request.params.get('to')).toBe('2026-10-14T11:00:00Z');
    more.flush(page(100, 100, null));
  });

  describe('a broken deep link', () => {
    it.each([
      ['an unknown preset', '/admin/logs?range=2d'],
      ['from without to', '/admin/logs?from=2026-10-14T10%3A00%3A00Z'],
    ])('%s shows a notice, sends no query and leaves the range control usable', async (_n, url) => {
      await open(url);
      statusRequest().flush(healthyStatus());
      await settle();

      httpMock.expectNone(
        (r) => r.url === '/api/admin/logs' || r.url === '/api/admin/logs/summary',
      );
      expect(root().textContent).toContain('The time range in the address is not valid');

      buttonNamed('6 h').click();
      await settle();
      statusRequest().flush(healthyStatus());
      listRequest((r) => r.params.get('range') === '6h').flush(page(4, 4, null));
      summaryRequest().flush(summary());
      await settle();
      expect(root().textContent).not.toContain('The time range in the address is not valid');
    });

    it('a filter over 512 characters is marked in the field and not sent', async () => {
      await open(`/admin/logs?q=${'a'.repeat(513)}`);
      statusRequest().flush(healthyStatus());
      await settle();

      httpMock.expectNone(
        (r) => r.url === '/api/admin/logs' || r.url === '/api/admin/logs/summary',
      );
      expect(input().getAttribute('aria-invalid')).toBe('true');
      const describedBy = input().getAttribute('aria-describedby')!;
      expect(root().querySelector(`#${describedBy}`)?.textContent).toContain('too long');
      expect(input().selectionStart).toBe(512);
      expect(input().selectionEnd).toBe(513);
    });
  });

  it('marks the place an invalid filter fails at and never shows the old rows under it', async () => {
    await openAndLoad('/admin/logs');

    input().value = 'level:debug';
    input().dispatchEvent(new Event('input'));
    root().querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
    statusRequest().flush(healthyStatus());
    listRequest().flush(
      { errorCode: 'invalid_log_filter', reason: 'invalid_level', position: 6, length: 5 },
      { status: 400, statusText: 'Bad Request' },
    );
    // The first failure cancels the sibling request: nothing of this query is kept.
    expect(summaryRequest().cancelled).toBe(true);
    await settle();

    expect(input().getAttribute('aria-invalid')).toBe('true');
    const message = root().querySelector(`#${input().getAttribute('aria-describedby')}`);
    expect(message?.textContent).toContain('Invalid level');
    expect([input().selectionStart, input().selectionEnd]).toEqual([6, 11]);
    expect(root().querySelectorAll('[data-log-id]').length).toBe(0);
  });

  it('keeps the last answer on screen under a timeout banner that says it is stale', async () => {
    await openAndLoad('/admin/logs');

    buttonNamed('7 days').click();
    await settle();
    statusRequest().flush(healthyStatus());
    listRequest().flush({ errorCode: 'log_query_timeout' }, { status: 503, statusText: 'x' });
    summaryRequest().flush({ errorCode: 'log_query_timeout' }, { status: 503, statusText: 'x' });
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain(
      'Query too expensive — narrow the time range or the filter',
    );
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('out of date');
    expect(root().querySelectorAll('[data-log-id]').length).toBe(4);
  });

  it('lists every drop reason above zero, rejected included, and keeps the "possibly"', async () => {
    await openAndLoad('/admin/logs', {
      summary: summary({
        dropped: {
          total: 9,
          queueFull: 4,
          budget: 0,
          writeUnconfirmed: 3,
          captureFailed: 0,
          reentrant: 0,
          rejected: 2,
        },
      }),
    });

    const text = root().textContent ?? '';
    expect(text).toContain('9 entries were not, or possibly not, stored');
    expect(text).toContain('Buffer full: 4');
    expect(text).toContain('Write not confirmed: 3');
    expect(text).toContain('Rejected: 2');
    expect(text).not.toContain('Daily budget');
  });

  it('shows a banner per source that is not healthy, and the overdue retention', async () => {
    const status = healthyStatus({
      sources: [
        {
          source: 'api',
          state: 'stale',
          instanceId: 'x',
          startedUtc: '2026-10-14T08:00:00Z',
          lastFlushUtc: '2026-10-14T11:20:41Z',
        },
        {
          source: 'worker',
          state: 'missing',
          instanceId: null,
          startedUtc: null,
          lastFlushUtc: null,
        },
      ],
      retention: { oldestEntryUtc: '2026-10-05T01:00:00Z', overdue: true },
    });
    await openAndLoad('/admin/logs', { status });

    const text = root().textContent ?? '';
    expect(text).toContain('capture of api has written nothing since 11:20 UTC');
    expect(text).toContain('capture of worker has never reported');
    expect(text).toContain('older than 7 days are still present (oldest: 2026-10-05)');
  });

  it('warns about a coverage gap only when it falls inside the shown range', async () => {
    const status = healthyStatus({
      coverage24h: {
        complete: false,
        recordedErrors: 2,
        incompleteSince: [
          { source: 'api', lastIncompleteAtUtc: '2026-10-14T03:30:00Z' },
          { source: 'worker', lastIncompleteAtUtc: '2026-10-01T03:30:00Z' },
        ],
      },
    });
    await openAndLoad('/admin/logs', { status });

    const text = root().textContent ?? '';
    expect(text).toContain('capture of api was not gapless at 2026-10-14 03:30 UTC');
    expect(text).not.toContain('capture of worker was not gapless');
  });

  it('offers to widen an empty result to 7 days', async () => {
    await openAndLoad('/admin/logs', { page: page(0, 0, null) });

    expect(root().textContent).toContain('No entries for this filter in the time range');
    buttonNamed('Widen to 7 days').click();
    await settle();
    statusRequest().flush(healthyStatus());
    listRequest((r) => r.params.get('range') === '7d').flush(page(0, 0, null));
    summaryRequest().flush(summary());
  });

  it('reserves the "new entries" slot at zero and keeps it out of the accessibility tree', async () => {
    await openAndLoad('/admin/logs');

    const slot = root().querySelector('[data-testid="log-new-slot"] button')!;
    expect(slot.classList.contains('invisible')).toBe(true);
    expect(slot.getAttribute('aria-hidden')).toBe('true');
    expect(slot.getAttribute('tabindex')).toBe('-1');
  });

  it('puts the time range behind the Filter button below the desktop breakpoint', async () => {
    desktop = false;
    await openAndLoad('/admin/logs');

    expect(root().querySelector('[role="radiogroup"]')).toBeNull();
    expect(buttonNamed('Filters')).toBeDefined();
    expect(root().querySelector('[aria-label="Facets"]')).toBeNull();
  });
});
