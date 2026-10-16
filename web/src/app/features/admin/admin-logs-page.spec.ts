import { Dialog } from '@angular/cdk/dialog';
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
import {
  LogEntry,
  LogPage,
  LogStatus,
  LogSummary,
  SavedLogFilter,
} from '../../core/admin/admin-logs.model';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { TimeHistogram } from '../../shared/charts/time-histogram';
import {
  AdminLogsPage,
  LOG_LIST_LIMIT,
  absoluteRangeLabelBounds,
  isLogListAtTop,
  parseLogRange,
} from './admin-logs-page';

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

/** jsdom ships no `EventSource`; this one lets a test open, close and push frames into the admin stream. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readyState = 1;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }
  close(): void {
    this.readyState = 2;
  }
}

function emitLive(type: string): void {
  for (const source of FakeEventSource.instances) {
    source.onmessage?.({ data: JSON.stringify({ type }), lastEventId: '' } as MessageEvent);
  }
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
  let savedFilters: SavedLogFilter[];
  let savedFiltersFail: boolean;
  let dialogOpen: (component: unknown, config: unknown) => { closed: unknown };

  beforeEach(() => {
    desktop = true;
    savedFilters = [];
    savedFiltersFail = false;
    dialogOpen = () => ({ closed: of(undefined) });
    FakeIntersectionObserver.instances = [];
    FakeEventSource.instances = [];
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
        { provide: Dialog, useValue: { open: (c: unknown, o: unknown) => dialogOpen(c, o) } },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
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
    vi.useRealTimers();
    Reflect.deleteProperty(document, 'visibilityState');
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
    const filtersRequest = httpMock.expectOne('/api/admin/log-filters');
    if (savedFiltersFail) {
      filtersRequest.error(new ProgressEvent('error'));
    } else {
      filtersRequest.flush(savedFilters);
    }
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

  describe('absoluteRangeLabelBounds', () => {
    it('labels a range of whole minutes to the minute', () => {
      expect(absoluteRangeLabelBounds('2026-10-13T12:15:00Z', '2026-10-13T12:30:00Z')).toEqual({
        from: '2026-10-13 12:15',
        to: '2026-10-13 12:30',
      });
    });

    it('labels a bucket shorter than a minute, or off the minute, to the second', () => {
      expect(absoluteRangeLabelBounds('2026-10-14T11:59:20Z', '2026-10-14T11:59:30Z')).toEqual({
        from: '2026-10-14 11:59:20',
        to: '2026-10-14 11:59:30',
      });
      expect(absoluteRangeLabelBounds('2026-10-14T11:58:30Z', '2026-10-14T12:00:00Z')).toEqual({
        from: '2026-10-14 11:58:30',
        to: '2026-10-14 12:00:00',
      });
    });

    it('states the bounds in UTC whatever offset the link carries', () => {
      expect(
        absoluteRangeLabelBounds('2026-10-14T14:00:00+02:00', '2026-10-14T15:00:00+02:00'),
      ).toEqual({ from: '2026-10-14 12:00', to: '2026-10-14 13:00' });
    });
  });

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
    buttonNamed('Back to 24 h').focus();
    buttonNamed('Back to 24 h').click();
    await settle();
    // The button is gone with its own click; the focus lands on the range it restored.
    expect(document.activeElement?.getAttribute('role')).toBe('radio');
    expect(document.activeElement?.textContent?.trim()).toBe('24 h');
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

  it('holds the first page until the summary and the status have answered, so nothing lands above it later', async () => {
    await open('/admin/logs');
    const rows = () => root().querySelectorAll('[data-log-id]').length;

    listRequest().flush(page(4, 4, null));
    await settle();
    expect(rows()).toBe(0);

    summaryRequest().flush(
      summary({
        dropped: {
          total: 1,
          queueFull: 1,
          budget: 0,
          writeUnconfirmed: 0,
          captureFailed: 0,
          reentrant: 0,
          rejected: 0,
        },
      }),
    );
    await settle();
    expect(rows()).toBe(0);

    statusRequest().flush(
      healthyStatus({ retention: { oldestEntryUtc: '2026-10-05T01:00:00Z', overdue: true } }),
    );
    await settle();
    expect(rows()).toBe(4);
    const text = root().textContent ?? '';
    expect(text).toContain('Buffer full: 1');
    expect(text).toContain('older than 7 days are still present');
  });

  it('shows the rows when the summary fails, together with the error', async () => {
    await open('/admin/logs');
    statusRequest().flush(healthyStatus());
    listRequest().flush(page(4, 4, null));
    await settle();
    expect(root().querySelectorAll('[data-log-id]').length).toBe(0);

    summaryRequest().flush({}, { status: 503, statusText: 'Busy' });
    await settle();

    expect(root().querySelectorAll('[data-log-id]').length).toBe(4);
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('busy');
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

  it('words a single dropped entry in the singular', async () => {
    await openAndLoad('/admin/logs', {
      summary: summary({
        dropped: {
          total: 1,
          queueFull: 1,
          budget: 0,
          writeUnconfirmed: 0,
          captureFailed: 0,
          reentrant: 0,
          rejected: 0,
        },
      }),
    });

    expect(root().textContent).toContain('1 entry was not, or possibly not, stored');
  });

  it('counts "n of total" against the current summary only', async () => {
    await openAndLoad('/admin/logs');
    expect(root().textContent).toContain('4 of 4 entries loaded');

    buttonNamed('7 days').click();
    await settle();
    statusRequest().flush(healthyStatus());
    listRequest().flush(page(3, 3, null));
    summaryRequest().flush({}, { status: 503, statusText: 'Busy' });
    await settle();

    // The histogram above still shows the earlier answer; its total belongs to another range.
    expect(root().textContent).toContain('3 entries loaded');
    expect(root().textContent).not.toContain('of 4 entries');
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
    buttonNamed('Widen to 7 days').focus();
    buttonNamed('Widen to 7 days').click();
    await settle();
    statusRequest().flush(healthyStatus());
    listRequest((r) => r.params.get('range') === '7d').flush(page(0, 0, null));
    summaryRequest().flush(summary());
    await settle();

    expect(buttonNamed('Widen to 7 days')).toBeUndefined();
    expect(document.activeElement?.getAttribute('role')).toBe('radio');
    expect(document.activeElement?.textContent?.trim()).toBe('7 days');
  });

  it('reserves the "new entries" slot at zero and keeps it out of the accessibility tree', async () => {
    await openAndLoad('/admin/logs');

    const slot = root().querySelector('[data-testid="log-new-slot"] button')!;
    expect(slot.classList.contains('invisible')).toBe(true);
    expect(slot.getAttribute('aria-hidden')).toBe('true');
    expect(slot.getAttribute('tabindex')).toBe('-1');
  });

  it('says so when "Copy visible" finds no clipboard', async () => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    try {
      await openAndLoad('/admin/logs');
      buttonNamed('Copy visible').click();
      await settle();

      expect(root().textContent).toContain('Copying is not possible');
    } finally {
      Reflect.deleteProperty(navigator, 'clipboard');
    }
  });

  it('puts the time range behind the Filter button below the desktop breakpoint', async () => {
    desktop = false;
    await openAndLoad('/admin/logs');

    expect(root().querySelector('[role="radiogroup"]')).toBeNull();
    expect(buttonNamed('Filters')).toBeDefined();
    expect(root().querySelector('[aria-label="Facets"]')).toBeNull();
  });

  describe('saved filter chips', () => {
    const SEEDS: SavedLogFilter[] = [
      { id: 1, name: 'Start nach Deploy', filter: 'instance:latest uptime<15m', sortOrder: 1 },
      { id: 4, name: 'Alle Fehler', filter: 'level>=error', sortOrder: 4 },
    ];
    const chip = (name: string) =>
      Array.from(root().querySelectorAll<HTMLButtonElement>('button[aria-pressed]')).find(
        (b) => b.textContent?.trim() === name,
      )!;

    beforeEach(() => {
      savedFilters = SEEDS;
    });

    function fakeDialog(result: unknown): { opened: unknown[] } {
      const opened: unknown[] = [];
      dialogOpen = (_component, config) => {
        opened.push(config);
        return { closed: of(result) };
      };
      return { opened };
    }

    it('shows the seeds, with "Alle Fehler" active for q=level>=error', async () => {
      await openAndLoad('/admin/logs?q=level%3E%3Derror');

      expect(chip('Alle Fehler').getAttribute('aria-pressed')).toBe('true');
      expect(chip('Start nach Deploy').getAttribute('aria-pressed')).toBe('false');
    });

    it('replaces q on a chip click and keeps the time range', async () => {
      await openAndLoad('/admin/logs?q=source%3Aapi&range=7d');

      chip('Start nach Deploy').click();
      await settle();
      statusRequest().flush(healthyStatus());
      const list = listRequest((r) => r.params.get('q') === 'instance:latest uptime<15m');
      expect(list.request.params.get('range')).toBe('7d');
      list.flush(page(4, 4, null));
      summaryRequest().flush(summary());
      await settle();

      expect(router.url).toContain('range=7d');
      expect(input().value).toBe('instance:latest uptime<15m');
    });

    it('keeps a stored filter the parser now rejects clickable and shows the filter error', async () => {
      savedFilters = [{ id: 7, name: 'Alt', filter: 'level:debug', sortOrder: 1 }];
      await openAndLoad('/admin/logs');

      chip('Alt').click();
      await settle();
      statusRequest().flush(healthyStatus());
      listRequest((r) => r.params.get('q') === 'level:debug').flush(
        { errorCode: 'invalid_log_filter', reason: 'invalid_level', position: 6, length: 5 },
        { status: 400, statusText: 'Bad Request' },
      );
      expect(summaryRequest().cancelled).toBe(true);
      await settle();

      expect(input().getAttribute('aria-invalid')).toBe('true');
      expect(root().textContent).toContain(en.admin.logs.filterErrors.invalid_level);
    });

    it('overwrites a saved filter with the applied filter under its own name', async () => {
      await openAndLoad('/admin/logs?q=level%3E%3Dwarn');

      root().querySelector<HTMLButtonElement>('[data-chip-menu="4"]')!.click();
      harness.detectChanges();
      buttonNamed('Overwrite with current filter').click();
      const put = httpMock.expectOne('/api/admin/log-filters/4');
      expect(put.request.method).toBe('PUT');
      expect(put.request.body).toEqual({ name: 'Alle Fehler', filter: 'level>=warn' });
      put.flush({ id: 4, name: 'Alle Fehler', filter: 'level>=warn', sortOrder: 4 });
      await settle();

      expect(chip('Alle Fehler').getAttribute('aria-pressed')).toBe('true');
    });

    it('shows a refused overwrite as a banner', async () => {
      await openAndLoad('/admin/logs?q=level%3E%3Dwarn');

      root().querySelector<HTMLButtonElement>('[data-chip-menu="4"]')!.click();
      harness.detectChanges();
      buttonNamed('Overwrite with current filter').click();
      httpMock
        .expectOne('/api/admin/log-filters/4')
        .flush({ errorCode: 'log_filter_not_found' }, { status: 404, statusText: 'Not Found' });
      httpMock.expectOne('/api/admin/log-filters').flush(SEEDS.slice(0, 1));
      await settle();

      expect(root().textContent).toContain('no longer exists');
      expect(root().textContent).not.toContain('Alle Fehler');
    });

    it('deletes only after the confirmation says yes', async () => {
      fakeDialog(false);
      await openAndLoad('/admin/logs');
      root().querySelector<HTMLButtonElement>('[data-chip-menu="4"]')!.click();
      harness.detectChanges();
      buttonNamed('Delete').click();
      await settle();
      httpMock.expectNone('/api/admin/log-filters/4');
      expect(chip('Alle Fehler')).toBeDefined();
    });

    it('removes the chip once the confirmed delete succeeds', async () => {
      const { opened } = fakeDialog(true);
      await openAndLoad('/admin/logs');
      root().querySelector<HTMLButtonElement>('[data-chip-menu="4"]')!.click();
      harness.detectChanges();
      buttonNamed('Delete').click();
      const del = httpMock.expectOne('/api/admin/log-filters/4');
      expect(del.request.method).toBe('DELETE');
      del.flush(null, { status: 204, statusText: 'No Content' });
      await settle();

      expect(JSON.stringify(opened)).toContain('Alle Fehler');
      expect(chip('Alle Fehler')).toBeUndefined();
    });

    it('adds a newly saved filter to the bar', async () => {
      fakeDialog({ id: 9, name: 'Neu', filter: 'source:api', sortOrder: 5 });
      await openAndLoad('/admin/logs?q=source%3Aapi');

      buttonNamed('+ Save filter').click();
      await settle();

      expect(chip('Neu').getAttribute('aria-pressed')).toBe('true');
    });

    it('opens the edit dialog on the chosen filter, applies the result and refreshes after a vanish', async () => {
      const edited = { id: 4, name: 'Fehler', filter: 'level>=error', sortOrder: 4 };
      const { opened } = fakeDialog(edited);
      await openAndLoad('/admin/logs');
      root().querySelector<HTMLButtonElement>('[data-chip-menu="4"]')!.click();
      harness.detectChanges();
      buttonNamed('Edit').click();
      await settle();

      const data = (opened[0] as { data: { filter: SavedLogFilter; onVanished(): void } }).data;
      expect(data.filter).toEqual(SEEDS[1]);
      expect(chip('Fehler')).toBeDefined();
      expect(chip('Alle Fehler')).toBeUndefined();

      data.onVanished();
      httpMock.expectOne('/api/admin/log-filters').flush(SEEDS);
      await settle();
      expect(chip('Alle Fehler')).toBeDefined();
    });

    it('reloads the list instead of appending when the first load had failed', async () => {
      savedFiltersFail = true;
      fakeDialog({ id: 9, name: 'Neu', filter: 'source:api', sortOrder: 5 });
      await openAndLoad('/admin/logs?q=source%3Aapi');
      expect(root().querySelector('[role="alert"]')).not.toBeNull();

      buttonNamed('+ Save filter').click();
      await settle();
      httpMock.expectOne('/api/admin/log-filters').flush([...SEEDS]);
      await settle();

      expect(chip('Alle Fehler')).toBeDefined();
      expect(chip('Neu')).toBeUndefined();
    });

    it('sends one PUT for a double activation of "overwrite"', async () => {
      await openAndLoad('/admin/logs?q=level%3E%3Dwarn');
      root().querySelector<HTMLButtonElement>('[data-chip-menu="4"]')!.click();
      harness.detectChanges();
      const overwrite = buttonNamed('Overwrite with current filter');
      overwrite.click();
      root().querySelector<HTMLButtonElement>('[data-chip-menu="4"]')!.click();
      harness.detectChanges();
      buttonNamed('Overwrite with current filter').click();

      const put = httpMock.expectOne('/api/admin/log-filters/4');
      put.flush({ id: 4, name: 'Alle Fehler', filter: 'level>=warn', sortOrder: 4 });
      await settle();
    });
  });

  describe('on a phone', () => {
    it('keeps the chip bar off the page and hands it to the Filter sheet', async () => {
      desktop = false;
      savedFilters = [{ id: 4, name: 'Alle Fehler', filter: 'level>=error', sortOrder: 4 }];
      const configs: { data: Record<string, unknown> }[] = [];
      dialogOpen = (_c, config) => {
        configs.push(config as { data: Record<string, unknown> });
        return { closed: of(undefined) };
      };
      await openAndLoad('/admin/logs');

      expect(root().querySelector('app-admin-log-filter-chips')).toBeNull();
      buttonNamed('Filters').click();
      const data = configs[0].data as {
        savedFilters(): SavedLogFilter[];
        selectFilter(f: SavedLogFilter): void;
      };
      expect(data.savedFilters().map((f) => f.name)).toEqual(['Alle Fehler']);

      data.selectFilter(data.savedFilters()[0]);
      await settle();
      statusRequest().flush(healthyStatus());
      listRequest((r) => r.params.get('q') === 'level>=error').flush(page(4, 4, null));
      summaryRequest().flush(summary());
    });
  });

  describe('isLogListAtTop', () => {
    it('is true while the first row sits in view below the sticky bar and nothing is unfolded', () => {
      expect(isLogListAtTop(300, 200, 900, false)).toBe(true);
      expect(isLogListAtTop(200, 200, 900, false)).toBe(true);
    });

    it('is false once the first row has slid under the sticky bar or out of view', () => {
      expect(isLogListAtTop(150, 200, 900, false)).toBe(false);
      expect(isLogListAtTop(950, 200, 900, false)).toBe(false);
    });

    it('is false while a row is unfolded, wherever the list stands', () => {
      expect(isLogListAtTop(300, 200, 900, true)).toBe(false);
    });

    it('treats an empty list as the top', () => {
      expect(isLogListAtTop(null, 200, 900, false)).toBe(true);
      expect(isLogListAtTop(null, 200, 900, true)).toBe(false);
    });
  });

  describe('live mode', () => {
    const at = (id: number, timestampUtc: string): LogEntry => ({ ...entry(id), timestampUtc });
    const loaded = () => page(4, 4, null).entries;
    const isKeys = (r: TestRequest['request']) =>
      r.url === '/api/admin/logs' && r.params.has('keysOnly');
    const keysRequest = () => httpMock.expectOne(isKeys);
    const noKeysRequest = () => httpMock.expectNone(isKeys);
    const idsRequest = () =>
      httpMock.expectOne((r) => r.url === '/api/admin/logs' && r.params.has('ids'));
    const rowIds = () =>
      Array.from(root().querySelectorAll('[data-log-id]')).map((li) =>
        Number(li.getAttribute('data-log-id')),
      );
    const announcement = () =>
      root().querySelector('[data-testid="log-live-announcement"]')?.textContent?.trim() ?? '';
    const marker = () => root().querySelector('app-health-marker')?.textContent?.trim();
    const slotButton = () =>
      root().querySelector<HTMLButtonElement>('[data-testid="log-new-slot"] button')!;
    const keyPage = (entries: LogEntry[], truncated = false) => ({
      entries: entries.map((e) => ({ id: e.id, timestampUtc: e.timestampUtc })),
      nextCursor: null,
      rangeFromUtc: '2026-10-13T12:00:00Z',
      rangeToUtc: '2026-10-14T12:00:05Z',
      truncated,
    });
    const idsPage = (entries: LogEntry[]): LogPage => ({
      entries,
      nextCursor: null,
      rangeFromUtc: null,
      rangeToUtc: null,
      truncated: false,
    });
    const startFakeDate = (iso: string) => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date(iso));
    };
    const advanceDate = (ms: number) => vi.setSystemTime(Date.now() + ms);

    /** One hint, the key query over the loaded interval, and the id fetch for what is missing. */
    async function reconcile(incoming: LogEntry[]): Promise<void> {
      emitLive('logs.appended');
      await settle();
      keysRequest().flush(keyPage([...incoming, ...loaded()]));
      if (incoming.length > 0) {
        idsRequest().flush(idsPage(incoming));
      }
      await settle();
    }

    it('inserts new entries at their sorted place at the top and announces them once', async () => {
      await openAndLoad('/admin/logs');

      emitLive('logs.appended');
      await settle();
      const keys = keysRequest();
      // Pagination is at its end, so the loaded interval starts at the range start (R7, 3.2).
      expect(keys.request.params.get('from')).toBe('2026-10-13T12:00:00Z');
      expect(keys.request.params.has('to')).toBe(false);
      const newest = at(11, '2026-10-14T12:00:01Z');
      const late = at(10, '2026-10-14T11:59:59Z');
      keys.flush(keyPage([newest, ...loaded(), late]));
      const ids = idsRequest();
      expect(ids.request.params.get('ids')).toBe('11,10');
      ids.flush(idsPage([newest, late]));
      await settle();

      expect(rowIds()).toEqual([11, 4, 3, 2, 1, 10]);
      expect(announcement()).toBe('2 new entries');
      // The total follows the inserted rows instead of waiting up to 30 s for the next summary.
      expect(root().textContent).toContain('6 of 6 entries loaded');
      expect(slotButton().getAttribute('aria-hidden')).toBe('true');
    });

    it('holds new entries behind "n new" while a row is unfolded and inserts them on a click', async () => {
      await openAndLoad('/admin/logs');
      const scrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
      root().querySelector<HTMLButtonElement>('[data-log-id] button')!.click();
      await settle();

      await reconcile([at(5, '2026-10-14T12:00:01Z')]);

      expect(rowIds()).toEqual([4, 3, 2, 1]);
      expect(announcement()).toBe('');
      expect(slotButton().getAttribute('aria-hidden')).toBeNull();
      expect(slotButton().textContent?.trim()).toBe('1 new ↑');

      slotButton().click();
      await settle();

      expect(scrollTo).toHaveBeenCalled();
      expect(rowIds()).toEqual([5, 4, 3, 2, 1]);
      expect(announcement()).toBe('1 new entry');
      expect(slotButton().getAttribute('aria-hidden')).toBe('true');
    });

    it('inserts at the top again once an unfolded row was filtered out of the list', async () => {
      await openAndLoad('/admin/logs');
      root().querySelector<HTMLButtonElement>('[data-log-id="4"] button')!.click();
      await settle();

      // The reader excludes what the unfolded row showed: the new answer no longer has row 4.
      input().value = '-channel:noisy';
      input().dispatchEvent(new Event('input'));
      root().querySelector('form')!.dispatchEvent(new Event('submit'));
      await settle();
      statusRequest().flush(healthyStatus());
      const remaining = page(3, 3, null);
      listRequest((r) => r.params.get('q') === '-channel:noisy').flush(remaining);
      summaryRequest().flush(summary());
      await settle();
      expect(rowIds()).toEqual([3, 2, 1]);

      emitLive('logs.appended');
      await settle();
      const fresh = at(5, '2026-10-14T12:00:01Z');
      keysRequest().flush(keyPage([fresh, ...remaining.entries]));
      idsRequest().flush(idsPage([fresh]));
      await settle();

      expect(rowIds()).toEqual([5, 3, 2, 1]);
      expect(announcement()).toBe('1 new entry');
      expect(slotButton().getAttribute('aria-hidden')).toBe('true');
    });

    it('holds new entries once the first row has scrolled under the sticky filter bar', async () => {
      await openAndLoad('/admin/logs');
      const first = root().querySelector('[data-log-id]')!;
      vi.spyOn(first, 'getBoundingClientRect').mockReturnValue({
        top: -40,
        bottom: -10,
      } as DOMRect);

      await reconcile([at(5, '2026-10-14T12:00:01Z')]);

      expect(rowIds()).toEqual([4, 3, 2, 1]);
      expect(slotButton().textContent?.trim()).toBe('1 new ↑');
    });

    it('pauses after a failed reconcile: later hints send nothing until a manual load succeeds', async () => {
      startFakeDate('2026-10-14T12:00:05Z');
      await openAndLoad('/admin/logs');

      emitLive('logs.appended');
      await settle();
      keysRequest().flush({}, { status: 500, statusText: 'x' });
      await settle();

      expect(marker()).toBe('Live paused');
      expect(root().textContent).toContain('Live paused after an error');

      advanceDate(60_000);
      emitLive('logs.appended');
      FakeEventSource.instances.forEach((source) => source.onopen?.());
      await settle();
      noKeysRequest();

      buttonNamed('Try again').click();
      await settle();
      statusRequest().flush(healthyStatus());
      listRequest().flush(page(4, 4, null));
      summaryRequest().flush(summary());
      await settle();
      expect(marker()).not.toBe('Live paused');

      advanceDate(6000);
      emitLive('logs.appended');
      await settle();
      keysRequest().flush(keyPage(loaded()));
    });

    it('pauses after a failed id fetch as well', async () => {
      await openAndLoad('/admin/logs');

      emitLive('logs.appended');
      await settle();
      keysRequest().flush(keyPage([at(5, '2026-10-14T12:00:01Z'), ...loaded()]));
      idsRequest().flush({}, { status: 503, statusText: 'x' });
      await settle();

      expect(marker()).toBe('Live paused');
      expect(rowIds()).toEqual([4, 3, 2, 1]);
    });

    it('treats invalid_log_range from the key query as "reload the list", not as a pause', async () => {
      await openAndLoad('/admin/logs');

      emitLive('logs.appended');
      await settle();
      keysRequest().flush(
        { errorCode: 'invalid_log_range' },
        { status: 400, statusText: 'Bad Request' },
      );
      await settle();

      expect(root().textContent).toContain('Too many new entries');
      // No reconcile runs any more, so the marker must not say "Live" either.
      expect(marker()).toBe('Reload needed');

      buttonNamed('Reload list').click();
      await settle();
      statusRequest().flush(healthyStatus());
      listRequest().flush(page(4, 4, null));
      summaryRequest().flush(summary());
      await settle();
      expect(root().textContent).not.toContain('Too many new entries');
    });

    it('reconcile_spanBeyondEightDays_showsReloadWithoutRequest', async () => {
      // from_L is the range start 2026-10-13T12:00:00Z; one second past 8 days the server would
      // answer 400 for the key query — the client must not ask (Review Focus 3).
      startFakeDate('2026-10-21T12:00:01Z');
      await openAndLoad('/admin/logs?range=7d');

      emitLive('logs.appended');
      FakeEventSource.instances.forEach((source) => source.onopen?.());
      await settle();

      noKeysRequest();
      expect(root().textContent).toContain('Too many new entries');
      expect(marker()).not.toBe('Live paused');
    });

    it('asks for a reload when the server truncates the keys, without fetching ids', async () => {
      await openAndLoad('/admin/logs');

      emitLive('logs.appended');
      await settle();
      keysRequest().flush(keyPage(loaded(), true));
      await settle();

      httpMock.expectNone((r) => r.params.has('ids'));
      expect(root().textContent).toContain('Too many new entries');
    });

    it('has no live mode on an absolute range: the marker says so and hints send nothing', async () => {
      await openAndLoad('/admin/logs?from=2026-10-14T10%3A00%3A00Z&to=2026-10-14T11%3A00%3A00Z');

      emitLive('logs.appended');
      await settle();

      noKeysRequest();
      expect(marker()).toBe('No live updates for a fixed range');
    });

    it('reconciles when the stream (re)opens and then shows it as live', async () => {
      await openAndLoad('/admin/logs');

      FakeEventSource.instances.forEach((source) => source.onopen?.());
      await settle();

      keysRequest().flush(keyPage(loaded()));
      await settle();
      expect(marker()).toBe('Live');
    });

    it('shows a closed stream in the marker and offers a reload', async () => {
      await openAndLoad('/admin/logs');

      FakeEventSource.instances.forEach((source) => {
        source.readyState = 2;
        source.onerror?.();
      });
      await settle();

      expect(marker()).toBe('Live stream disconnected');
      buttonNamed('Reload').click();
      await settle();
      statusRequest().flush(healthyStatus());
      listRequest().flush(page(4, 4, null));
      summaryRequest().flush(summary());
    });

    it('takes the focus off "Reload" when the stream reopens and the slot turns inert', async () => {
      await openAndLoad('/admin/logs');
      FakeEventSource.instances.forEach((source) => {
        source.readyState = 2;
        source.onerror?.();
      });
      await settle();
      slotButton().focus();
      expect(slotButton().textContent?.trim()).toBe('Reload');

      FakeEventSource.instances.forEach((source) => {
        source.readyState = 1;
        source.onopen?.();
      });
      await settle();
      keysRequest().flush(keyPage(loaded()));
      await settle();

      expect(slotButton().getAttribute('aria-hidden')).toBe('true');
      expect(document.activeElement).toBe(root().querySelector('[data-log-id] button'));
    });

    it('announces every insert, also two in a row with the same count', async () => {
      startFakeDate('2026-10-14T12:00:05Z');
      await openAndLoad('/admin/logs');
      const region = root().querySelector('[data-testid="log-live-announcement"]')!;
      const heard: string[] = [];
      const observer = new MutationObserver(() => heard.push(region.textContent?.trim() ?? ''));
      observer.observe(region, { childList: true, characterData: true, subtree: true });
      const first = at(5, '2026-10-14T12:00:01Z');
      const second = at(6, '2026-10-14T12:00:02Z');

      advanceDate(6000);
      await reconcile([first]);
      await settle();
      advanceDate(6000);
      emitLive('logs.appended');
      await settle();
      keysRequest().flush(keyPage([second, first, ...loaded()]));
      idsRequest().flush(idsPage([second]));
      await settle();
      await settle();
      await Promise.resolve();
      observer.disconnect();

      expect(rowIds()).toEqual([6, 5, 4, 3, 2, 1]);
      expect(heard.filter((text) => text !== '')).toEqual(['1 new entry', '1 new entry']);
    });

    it('takes the focus off "n new" when the held rows go in on their own', async () => {
      startFakeDate('2026-10-14T12:00:05Z');
      await openAndLoad('/admin/logs');
      const firstRowButton = () => root().querySelector<HTMLButtonElement>('[data-log-id] button')!;
      firstRowButton().click();
      await settle();
      await reconcile([at(5, '2026-10-14T12:00:01Z')]);
      expect(slotButton().textContent?.trim()).toBe('1 new ↑');

      // The reader folds the row again and tabs to the slot; the next reconcile finds the top.
      firstRowButton().click();
      await settle();
      slotButton().focus();
      expect(document.activeElement).toBe(slotButton());
      advanceDate(6000);
      emitLive('logs.appended');
      await settle();
      keysRequest().flush(keyPage([at(5, '2026-10-14T12:00:01Z'), ...loaded()]));
      await settle();
      await settle();

      expect(rowIds()).toEqual([5, 4, 3, 2, 1]);
      expect(slotButton().getAttribute('aria-hidden')).toBe('true');
      expect(document.activeElement).toBe(firstRowButton());
    });

    it('reconciles when the tab comes back, and not while it is hidden', async () => {
      let state: DocumentVisibilityState = 'visible';
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
      await openAndLoad('/admin/logs');

      state = 'hidden';
      document.dispatchEvent(new Event('visibilitychange'));
      await settle();
      emitLive('logs.appended');
      await settle();
      noKeysRequest();

      state = 'visible';
      document.dispatchEvent(new Event('visibilitychange'));
      await settle();
      await settle();
      statusRequest().flush(healthyStatus());
      keysRequest().flush(keyPage(loaded()));
    });

    it('reconciles every 30 s without any hint', async () => {
      let state: DocumentVisibilityState = 'visible';
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
      await openAndLoad('/admin/logs');
      // Restart the visibility-driven timer under fake timers: away and back.
      state = 'hidden';
      document.dispatchEvent(new Event('visibilitychange'));
      await settle();
      vi.useFakeTimers({
        toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
      });
      state = 'visible';
      document.dispatchEvent(new Event('visibilitychange'));
      harness.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      statusRequest().flush(healthyStatus());
      keysRequest().flush(keyPage(loaded()));

      await vi.advanceTimersByTimeAsync(29_000);
      noKeysRequest();
      await vi.advanceTimersByTimeAsync(1_000);
      statusRequest().flush(healthyStatus());
      keysRequest().flush(keyPage(loaded()));
      await vi.advanceTimersByTimeAsync(30_000);
      statusRequest().flush(healthyStatus());
      keysRequest().flush(keyPage(loaded()));
    });

    it('refreshes the capture status on the 30 s tick and stops calling a stalled source live', async () => {
      let state: DocumentVisibilityState = 'visible';
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
      await openAndLoad('/admin/logs');
      state = 'hidden';
      document.dispatchEvent(new Event('visibilitychange'));
      await settle();
      vi.useFakeTimers({
        toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
      });
      state = 'visible';
      document.dispatchEvent(new Event('visibilitychange'));
      harness.detectChanges();
      await vi.advanceTimersByTimeAsync(0);
      statusRequest().flush(healthyStatus());
      keysRequest().flush(keyPage(loaded()));
      FakeEventSource.instances.forEach((source) => source.onopen?.());
      await vi.advanceTimersByTimeAsync(5_000);
      keysRequest().flush(keyPage(loaded()));
      harness.detectChanges();
      expect(marker()).toBe('Live');
      expect(root().textContent).not.toContain('has written nothing since');

      // The worker stops writing; the next tick asks for the status once more — no faster timer.
      await vi.advanceTimersByTimeAsync(24_000);
      httpMock.expectNone('/api/admin/logs/status');
      await vi.advanceTimersByTimeAsync(1_000);
      statusRequest().flush(
        healthyStatus({
          sources: [
            {
              source: 'api',
              state: 'healthy',
              instanceId: 'a1b2c3d4',
              startedUtc: '2026-10-14T08:00:00Z',
              lastFlushUtc: '2026-10-14T11:59:30Z',
            },
            {
              source: 'worker',
              state: 'stale',
              instanceId: 'a1b2c3d4',
              startedUtc: '2026-10-14T08:00:00Z',
              lastFlushUtc: '2026-10-14T11:50:12Z',
            },
          ],
        }),
      );
      keysRequest().flush(keyPage(loaded()));
      harness.detectChanges();

      expect(root().textContent).toContain('capture of worker has written nothing since 11:50 UTC');
      expect(marker()).toBe('Capture incomplete');
    });

    it('refreshes the summary at most once per 30 s, and only after something was inserted', async () => {
      startFakeDate('2026-10-14T12:00:05Z');
      await openAndLoad('/admin/logs');

      advanceDate(6000);
      await reconcile([at(5, '2026-10-14T12:00:01Z')]);
      httpMock.expectNone('/api/admin/logs/summary');

      advanceDate(30_000);
      emitLive('logs.appended');
      await settle();
      keysRequest().flush(keyPage([at(5, '2026-10-14T12:00:01Z'), ...loaded()]));
      await settle();
      summaryRequest().flush(summary({ total: 5 }));
      await settle();

      advanceDate(6000);
      emitLive('logs.appended');
      await settle();
      keysRequest().flush(keyPage([at(5, '2026-10-14T12:00:01Z'), ...loaded()]));
      await settle();
      httpMock.expectNone('/api/admin/logs/summary');
    });
  });
});
