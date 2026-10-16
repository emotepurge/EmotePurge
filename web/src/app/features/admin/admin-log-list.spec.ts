import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import en from '../../../../public/i18n/en.json';
import { LogEntry, LogEntryDetail } from '../../core/admin/admin-logs.model';
import {
  AdminLogList,
  formatLogLine,
  formatLogTime,
  formatUptime,
  shortCategory,
} from './admin-log-list';

function entry(id: number, overrides: Partial<LogEntry> = {}): LogEntry {
  return {
    id,
    timestampUtc: '2026-10-14T12:00:01.5Z',
    level: 'info',
    source: 'worker',
    instanceId: 'a1b2c3d4',
    category: 'EmotePurge.Worker.TwitchChatManager',
    eventId: null,
    eventName: null,
    message: `message ${id}`,
    hasException: false,
    exceptionType: null,
    properties: {},
    truncated: false,
    ...overrides,
  };
}

describe('log row formatting', () => {
  it('cuts HH:mm:ss.fff from the ISO string and pads the fraction', () => {
    expect(formatLogTime('2026-10-14T12:00:01Z')).toBe('12:00:01.000');
    expect(formatLogTime('2026-10-14T12:00:01.5Z')).toBe('12:00:01.500');
    expect(formatLogTime('2026-10-14T12:00:01.1234567Z')).toBe('12:00:01.123');
  });

  it('shortens a category to its last segment', () => {
    expect(shortCategory('EmotePurge.Worker.TwitchChatManager')).toBe('TwitchChatManager');
    expect(shortCategory('Plain')).toBe('Plain');
  });

  it('writes a docker-logs style line, with the exception below when there is one', () => {
    const e = entry(1, { level: 'error', message: 'boom' });
    expect(formatLogLine(e)).toBe(
      '2026-10-14T12:00:01.5Z error worker EmotePurge.Worker.TwitchChatManager: boom',
    );
    expect(formatLogLine(e, 'System.Exception: x')).toBe(
      '2026-10-14T12:00:01.5Z error worker EmotePurge.Worker.TwitchChatManager: boom\nSystem.Exception: x',
    );
  });

  it('words an uptime in language-neutral units', () => {
    expect(formatUptime(45)).toBe('45 s');
    expect(formatUptime(12 * 60 + 5)).toBe('12 min');
    expect(formatUptime(3 * 3600 + 5 * 60)).toBe('3 h 5 min');
    expect(formatUptime(2 * 86400 + 4 * 3600)).toBe('2 d 4 h');
  });
});

describe('AdminLogList', () => {
  let fixture: ComponentFixture<AdminLogList>;
  let httpMock: HttpTestingController;
  let terms: string[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        AdminLogList,
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('en'));
    httpMock = TestBed.inject(HttpTestingController);
    terms = [];
  });

  afterEach(() => httpMock.verify());

  function render(entries: LogEntry[], processStarts = []): HTMLElement {
    fixture = TestBed.createComponent(AdminLogList);
    fixture.componentRef.setInput('entries', entries);
    fixture.componentRef.setInput('processStarts', processStarts);
    fixture.componentInstance.termAdded.subscribe((t) => terms.push(t));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const rowButton = (root: HTMLElement, index = 0) =>
    root.querySelectorAll<HTMLButtonElement>('li > button[aria-expanded]')[index];

  it('puts a day separator between rows only where the UTC date changes', () => {
    const root = render([
      entry(3, { timestampUtc: '2026-10-14T00:00:05Z' }),
      entry(2, { timestampUtc: '2026-10-14T00:00:01Z' }),
      entry(1, { timestampUtc: '2026-10-13T23:59:59Z' }),
    ]);

    const days = Array.from(root.querySelectorAll('[data-testid="log-day"]'));
    expect(days.map((d) => d.textContent?.trim())).toEqual(['2026-10-13']);
    // The separator sits between row 2 and row 1.
    const items = Array.from(root.querySelectorAll('ul > li'));
    expect(items[2].getAttribute('data-testid')).toBe('log-day');
  });

  it('unfolds a row as a disclosure whose aria-controls names the details', () => {
    const root = render([entry(7)]);
    const button = rowButton(root);

    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(root.querySelector(`#${button.getAttribute('aria-controls')}`)).toBeNull();

    button.click();
    fixture.detectChanges();

    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(root.querySelector(`#${button.getAttribute('aria-controls')}`)).not.toBeNull();
    expect(fixture.componentInstance.hasExpanded()).toBe(true);

    button.click();
    fixture.detectChanges();
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(fixture.componentInstance.hasExpanded()).toBe(false);
  });

  it('stops counting an unfolded row once it has left the list, and forgets it', () => {
    const root = render([entry(8), entry(7)]);
    rowButton(root, 1).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.hasExpanded()).toBe(true);

    // A new filter's answer without row 7: nothing visible is unfolded any more.
    fixture.componentRef.setInput('entries', [entry(8)]);
    fixture.detectChanges();
    expect(fixture.componentInstance.hasExpanded()).toBe(false);

    // Coming back later, the row is folded like any other.
    fixture.componentRef.setInput('entries', [entry(8), entry(7)]);
    fixture.detectChanges();
    expect(rowButton(root, 1).getAttribute('aria-expanded')).toBe('false');
    expect(fixture.componentInstance.hasExpanded()).toBe(false);
  });

  it('asks again for an exception whose first load failed when the row is reopened', () => {
    const root = render([entry(9, { hasException: true })]);
    rowButton(root).click();
    fixture.detectChanges();
    httpMock.expectOne('/api/admin/logs/9').flush(null, { status: 503, statusText: 'Busy' });
    fixture.detectChanges();
    expect(root.textContent).toContain('The exception could not be loaded.');

    rowButton(root).click();
    fixture.detectChanges();
    rowButton(root).click();
    fixture.detectChanges();

    httpMock.expectOne('/api/admin/logs/9').flush({
      ...entry(9, { hasException: true }),
      exception: 'System.TimeoutException: late',
      processStartedUtc: '2026-10-14T11:00:00Z',
      uptimeSeconds: 60,
    } satisfies LogEntryDetail);
    fixture.detectChanges();
    expect(root.querySelector('pre')?.textContent).toContain('System.TimeoutException');
  });

  it('asks for no exception when a row without one is opened', () => {
    const root = render([entry(7)]);
    rowButton(root).click();
    fixture.detectChanges();

    httpMock.expectNone('/api/admin/logs/7');
  });

  it('loads the exception lazily behind a skeleton and shows it in a pre', () => {
    const root = render([entry(8, { hasException: true, level: 'error' })]);
    rowButton(root).click();
    fixture.detectChanges();

    expect(root.querySelector('.app-skeleton')).not.toBeNull();
    expect(root.querySelector('pre')).toBeNull();

    httpMock.expectOne('/api/admin/logs/8').flush({
      ...entry(8, { hasException: true }),
      exception: 'System.InvalidOperationException: nope',
      processStartedUtc: '2026-10-14T11:00:00Z',
      uptimeSeconds: 3601,
    } satisfies LogEntryDetail);
    fixture.detectChanges();

    expect(root.querySelector('.app-skeleton')).toBeNull();
    expect(root.querySelector('pre')?.textContent).toContain('System.InvalidOperationException');
    expect(root.textContent).toContain('1 h 0 min after start');
  });

  it('says so when the exception cannot be loaded', () => {
    const root = render([entry(9, { hasException: true })]);
    rowButton(root).click();
    fixture.detectChanges();

    httpMock.expectOne('/api/admin/logs/9').flush(null, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(root.textContent).toContain('The exception could not be loaded.');
  });

  it('words the time since start from the summary process starts without a request', () => {
    const root = render([entry(1, { timestampUtc: '2026-10-14T12:12:05Z' })], [
      { source: 'worker', instanceId: 'a1b2c3d4', startedUtc: '2026-10-14T12:00:00Z' },
    ] as never);
    rowButton(root).click();
    fixture.detectChanges();

    expect(root.textContent).toContain('12 min after start');
  });

  it('offers filter and exclude per property and emits the terms', () => {
    const root = render([
      entry(1, { properties: { channel: 'handofblood', note: 'two words', nested: { a: 1 } } }),
    ]);
    rowButton(root).click();
    fixture.detectChanges();

    const named = (name: string) =>
      Array.from(root.querySelectorAll('button')).find(
        (b) => b.getAttribute('aria-label') === name,
      )!;
    named('Add property channel with value handofblood to the filter').click();
    named('Exclude property channel with value handofblood').click();
    named('Add property note with value two words to the filter').click();

    expect(terms).toEqual(['channel:handofblood', '-channel:handofblood', 'note:"two words"']);
    // A nested value cannot be written in the filter language: no actions for it.
    expect(
      Array.from(root.querySelectorAll('button')).some((b) =>
        (b.getAttribute('aria-label') ?? '').includes('property nested'),
      ),
    ).toBe(false);
  });

  it('gives no actions to a property named like a built-in key', () => {
    const root = render([entry(1, { properties: { level: 'x' } })]);
    rowButton(root).click();
    fixture.detectChanges();

    expect(
      Array.from(root.querySelectorAll('button')).some((b) =>
        (b.getAttribute('aria-label') ?? '').includes('property level'),
      ),
    ).toBe(false);
  });
});
