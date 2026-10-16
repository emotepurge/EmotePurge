import { provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, Subject, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import en from '../../../../public/i18n/en.json';
import { LogSourceState, LogStatus } from '../../core/admin/admin-logs.model';
import { AdminLogsService } from '../../core/admin/admin-logs.service';
import { AdminLogErrorsRow } from './admin-log-errors-row';

const T = en.admin.monitoring.logErrors;

function status(overrides: {
  api?: LogSourceState;
  worker?: LogSourceState;
  complete?: boolean;
  recordedErrors?: number;
  incompleteSince?: LogStatus['coverage24h']['incompleteSince'];
  overdue?: boolean;
  workerLastFlushUtc?: string | null;
}): LogStatus {
  const source = (name: 'api' | 'worker', state: LogSourceState, lastFlushUtc: string | null) => ({
    source: name,
    state,
    instanceId: state === 'missing' ? null : 'abc',
    startedUtc: state === 'missing' ? null : '2026-10-14T08:00:00Z',
    lastFlushUtc,
  });
  return {
    generatedAtUtc: '2026-10-14T12:00:00Z',
    sources: [
      source('api', overrides.api ?? 'healthy', '2026-10-14T11:59:30Z'),
      source(
        'worker',
        overrides.worker ?? 'healthy',
        overrides.workerLastFlushUtc === undefined
          ? '2026-10-14T11:59:30Z'
          : overrides.workerLastFlushUtc,
      ),
    ],
    coverage24h: {
      complete: overrides.complete ?? true,
      recordedErrors: overrides.recordedErrors ?? 0,
      incompleteSince: overrides.incompleteSince ?? [],
    },
    retention: { oldestEntryUtc: '2026-10-08T12:00:00Z', overdue: overrides.overdue ?? false },
  };
}

describe('AdminLogErrorsRow', () => {
  let fixture: ComponentFixture<AdminLogErrorsRow>;
  let logs: AdminLogsService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        AdminLogErrorsRow,
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [provideHttpClient(), provideRouter([])],
    });
    logs = TestBed.inject(AdminLogsService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  function render(stream: Observable<LogStatus>): HTMLElement {
    vi.spyOn(logs, 'getStatus').mockReturnValue(stream);
    fixture = TestBed.createComponent(AdminLogErrorsRow);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const value = (root: HTMLElement) =>
    root.querySelector('[data-testid="admin-log-errors-value"]')?.textContent?.trim();
  const reasons = (root: HTMLElement) =>
    Array.from(root.querySelectorAll('[data-testid="admin-log-errors-reason"]')).map((e) =>
      e.textContent?.trim(),
    );

  describe('complete capture', () => {
    it('says "none" for zero errors', () => {
      const root = render(of(status({})));

      expect(value(root)).toBe(T.none);
      expect(reasons(root)).toEqual([]);
    });

    it('shows the count for errors, without a lower-bound qualifier', () => {
      const root = render(of(status({ recordedErrors: 3 })));

      expect(value(root)).toBe('3');
    });
  });

  describe('incomplete capture never claims "none"', () => {
    const cases: [string, Parameters<typeof status>[0]][] = [
      ['a missing source', { worker: 'missing' }],
      ['a disabled source', { worker: 'disabled' }],
      ['a stale source', { worker: 'stale' }],
      ['an incomplete 24 h coverage', { complete: false }],
    ];

    it.each(cases)('0 errors with %s reads "0 recorded, incomplete"', (_name, overrides) => {
      const root = render(of(status(overrides)));

      expect(value(root)).toBe(T.zeroIncomplete);
      expect(root.textContent).not.toContain(T.none);
    });

    it.each(cases)('3 errors with %s reads "at least 3"', (_name, overrides) => {
      const root = render(of(status({ ...overrides, recordedErrors: 3 })));

      expect(value(root)).toBe('at least 3');
    });

    it('names a stalled source with its last flush in UTC', () => {
      const root = render(
        of(status({ worker: 'stale', workerLastFlushUtc: '2026-10-14T11:41:00Z' })),
      );

      expect(reasons(root)).toEqual(['Log capture worker stalled since 11:41 UTC']);
    });

    it('names a disabled and a missing source', () => {
      expect(reasons(render(of(status({ worker: 'disabled' }))))).toEqual([
        'Log capture worker switched off',
      ]);
      fixture.destroy();
      expect(reasons(render(of(status({ api: 'missing' }))))).toEqual([
        'Log capture api no status report',
      ]);
    });

    it('names the coverage gap of a source whose heartbeat is healthy', () => {
      const root = render(
        of(
          status({
            complete: false,
            incompleteSince: [{ source: 'api', lastIncompleteAtUtc: '2026-10-14T09:30:00Z' }],
          }),
        ),
      );

      expect(reasons(root)).toHaveLength(1);
      expect(reasons(root)[0]).toMatch(/^Capture of api incomplete since .*09:30 UTC$/);
    });

    it('does not repeat a gap for a source that is already explained by its state', () => {
      const root = render(
        of(
          status({
            worker: 'stale',
            complete: false,
            incompleteSince: [{ source: 'worker', lastIncompleteAtUtc: '2026-10-14T09:30:00Z' }],
          }),
        ),
      );

      expect(reasons(root)).toHaveLength(1);
    });
  });

  it('adds the overdue retention reason to a complete count as well', () => {
    const root = render(of(status({ overdue: true })));

    expect(value(root)).toBe(T.none);
    expect(reasons(root)).toEqual([T.reasons.retentionOverdue]);
  });

  it('links to the saved "all errors" filter over 24 hours', () => {
    const root = render(of(status({})));

    const link = root.querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/admin/logs?q=level%3E%3Derror&range=24h');
  });

  it('names the link with its context, without the arrow', () => {
    const root = render(of(status({})));

    const link = root.querySelector('a') as HTMLAnchorElement;
    expect(link.textContent?.replace(/\s+/g, ' ').trim()).toContain('View');
    const clone = link.cloneNode(true) as HTMLElement;
    clone.querySelectorAll('[aria-hidden="true"]').forEach((e) => e.remove());
    expect(clone.textContent?.replace(/\s+/g, ' ').trim()).toBe(`View ${T.label}`);
  });

  it('does not print "since —" for a stale source without a last flush', () => {
    const root = render(of(status({ worker: 'stale', workerLastFlushUtc: null })));

    expect(reasons(root)).toEqual(['Log capture worker no status report']);
  });

  it('shows a loading status instead of a value until the request answers', async () => {
    const pending = new Subject<LogStatus>();
    const root = render(pending);

    expect(root.querySelector('[role="status"]')).not.toBeNull();
    expect(value(root)).toBeUndefined();

    pending.next(status({}));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.querySelector('[role="status"]')).toBeNull();
    expect(value(root)).toBe(T.none);
  });

  it('says "not available" on a load error and still links to the logs', () => {
    const root = render(throwError(() => new Error('boom')));

    expect(
      root.querySelector('[data-testid="admin-log-errors-unavailable"]')?.textContent,
    ).toContain(T.unavailable);
    expect(value(root)).toBeUndefined();
    expect(root.querySelector('a')).not.toBeNull();
  });

  it('requests the status again on reload()', () => {
    render(of(status({})));
    expect(logs.getStatus).toHaveBeenCalledTimes(1);

    fixture.componentInstance.reload();
    fixture.detectChanges();

    expect(logs.getStatus).toHaveBeenCalledTimes(2);
  });
});
