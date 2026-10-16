import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import {
  LogEntry,
  LogEntryDetail,
  LogKeyPage,
  LogPage,
  LogStatus,
  LogSummary,
  SavedLogFilter,
} from './admin-logs.model';
import { AdminLogsService } from './admin-logs.service';

function entry(id: number, timestampUtc: string): LogEntry {
  return {
    id,
    timestampUtc,
    level: 'info',
    source: 'api',
    instanceId: 'a1b2c3d4',
    category: 'EmotePurge.Api.Program',
    eventId: null,
    eventName: null,
    message: `entry ${id}`,
    hasException: false,
    exceptionType: null,
    properties: {},
    truncated: false,
  };
}

describe('AdminLogsService', () => {
  let service: AdminLogsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AdminLogsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('getPage', () => {
    it('sends the filter and a range preset, no cursor and no limit on the first page', () => {
      const page: LogPage = {
        entries: [entry(2, '2026-10-14T12:00:00.5Z')],
        nextCursor: 'cur',
        rangeFromUtc: '2026-10-13T12:00:00.1234567Z',
        rangeToUtc: '2026-10-14T12:00:00.1234567Z',
        truncated: false,
      };
      let result: LogPage | undefined;

      service.getPage('level>=warn cat:Foo', { range: '24h' }).subscribe((r) => (result = r));

      const req = httpMock.expectOne((r) => r.url === '/api/admin/logs');
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('q')).toBe('level>=warn cat:Foo');
      expect(req.request.params.get('range')).toBe('24h');
      expect(req.request.params.has('from')).toBe(false);
      expect(req.request.params.has('to')).toBe(false);
      expect(req.request.params.has('before')).toBe(false);
      expect(req.request.params.has('limit')).toBe(false);
      req.flush(page);
      expect(result).toEqual(page);
    });

    it('sends frozen exact bounds as from/to with the cursor and limit when loading more (R4)', () => {
      service
        .getPage(
          'source:worker',
          { from: '2026-10-13T12:00:00.1234567Z', to: '2026-10-14T12:00:00.1234567Z' },
          'opaque-cursor',
          100,
        )
        .subscribe();

      const req = httpMock.expectOne((r) => r.url === '/api/admin/logs');
      // Byte for byte: the client never re-serializes the server's instants.
      expect(req.request.params.get('from')).toBe('2026-10-13T12:00:00.1234567Z');
      expect(req.request.params.get('to')).toBe('2026-10-14T12:00:00.1234567Z');
      expect(req.request.params.has('range')).toBe(false);
      expect(req.request.params.get('before')).toBe('opaque-cursor');
      expect(req.request.params.get('limit')).toBe('100');
      req.flush({
        entries: [],
        nextCursor: null,
        rangeFromUtc: null,
        rangeToUtc: null,
        truncated: false,
      });
    });

    it('percent-encodes a plus sign so the server does not read it as a space', () => {
      service.getPage('"a+b" level>=warn', { range: '1h' }).subscribe();

      const req = httpMock.expectOne((r) => r.url === '/api/admin/logs');
      expect(req.request.urlWithParams).toContain('q=%22a%2Bb%22%20level%3E=warn');
      req.flush({
        entries: [],
        nextCursor: null,
        rangeFromUtc: null,
        rangeToUtc: null,
        truncated: false,
      });
    });

    it('leaves out a blank filter', () => {
      service.getPage('   ', { range: '1h' }).subscribe();

      const req = httpMock.expectOne((r) => r.url === '/api/admin/logs');
      expect(req.request.params.has('q')).toBe(false);
      req.flush({
        entries: [],
        nextCursor: null,
        rangeFromUtc: null,
        rangeToUtc: null,
        truncated: false,
      });
    });
  });

  it('getKeys asks for keys only from the loaded start, without a to and without a range (L2)', () => {
    const keys: LogKeyPage = {
      entries: [{ id: 9, timestampUtc: '2026-10-14T12:00:01Z' }],
      nextCursor: null,
      rangeFromUtc: '2026-10-14T11:00:00.000123Z',
      rangeToUtc: '2026-10-14T12:00:02Z',
      truncated: false,
    };
    let result: LogKeyPage | undefined;

    service.getKeys('level:error', '2026-10-14T11:00:00.000123Z').subscribe((r) => (result = r));

    const req = httpMock.expectOne((r) => r.url === '/api/admin/logs');
    expect(req.request.params.get('keysOnly')).toBe('true');
    expect(req.request.params.get('from')).toBe('2026-10-14T11:00:00.000123Z');
    expect(req.request.params.get('q')).toBe('level:error');
    expect(req.request.params.has('to')).toBe(false);
    expect(req.request.params.has('range')).toBe(false);
    expect(req.request.params.has('before')).toBe(false);
    expect(req.request.params.has('limit')).toBe(false);
    req.flush(keys);
    expect(result).toEqual(keys);
  });

  it('getByIds sends the ids comma-separated and nothing else', () => {
    let result: LogPage | undefined;

    service.getByIds([7, 3, 12]).subscribe((r) => (result = r));

    const req = httpMock.expectOne((r) => r.url === '/api/admin/logs');
    expect(req.request.params.keys()).toEqual(['ids']);
    expect(req.request.params.get('ids')).toBe('7,3,12');
    const page: LogPage = {
      entries: [entry(12, '2026-10-14T12:00:00Z')],
      nextCursor: null,
      rangeFromUtc: null,
      rangeToUtc: null,
      truncated: false,
    };
    req.flush(page);
    expect(result).toEqual(page);
  });

  it('getEntry GETs one entry with its exception', () => {
    const detail: LogEntryDetail = {
      ...entry(5, '2026-10-14T12:00:00Z'),
      hasException: true,
      exceptionType: 'System.InvalidOperationException',
      exception: 'System.InvalidOperationException: boom',
      processStartedUtc: '2026-10-14T11:00:00Z',
      uptimeSeconds: 3600,
    };
    let result: LogEntryDetail | undefined;

    service.getEntry(5).subscribe((r) => (result = r));

    const req = httpMock.expectOne('/api/admin/logs/5');
    expect(req.request.method).toBe('GET');
    req.flush(detail);
    expect(result).toEqual(detail);
  });

  it('getSummary sends the filter and the range like a page', () => {
    service
      .getSummary('has:exception', { from: '2026-10-14T10:00:00Z', to: '2026-10-14T10:15:00Z' })
      .subscribe();

    const req = httpMock.expectOne((r) => r.url === '/api/admin/logs/summary');
    expect(req.request.params.get('q')).toBe('has:exception');
    expect(req.request.params.get('from')).toBe('2026-10-14T10:00:00Z');
    expect(req.request.params.get('to')).toBe('2026-10-14T10:15:00Z');
    expect(req.request.params.has('range')).toBe(false);
    const summary: LogSummary = {
      rangeFromUtc: '2026-10-14T10:00:00Z',
      rangeToUtc: '2026-10-14T10:15:00Z',
      bucketFromUtc: '2026-10-14T10:00:00Z',
      bucketToUtc: '2026-10-14T10:15:00Z',
      bucketSeconds: 10,
      bucketStartsUtc: ['2026-10-14T10:00:00Z'],
      counts: { info: [1], warn: [0], error: [0] },
      total: 1,
      facets: {
        source: [{ value: 'api', count: 1 }],
        level: [{ value: 'info', count: 1 }],
        category: [{ value: 'A.B', short: 'B', count: 1 }],
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
    };
    req.flush(summary);
  });

  it('getStatus GETs the capture status', () => {
    const status: LogStatus = {
      generatedAtUtc: '2026-10-14T12:00:00Z',
      sources: [
        {
          source: 'api',
          state: 'healthy',
          instanceId: 'a1b2c3d4',
          startedUtc: '2026-10-14T11:00:00Z',
          lastFlushUtc: '2026-10-14T11:59:30Z',
        },
        {
          source: 'worker',
          state: 'missing',
          instanceId: null,
          startedUtc: null,
          lastFlushUtc: null,
        },
      ],
      coverage24h: { complete: false, recordedErrors: 0, incompleteSince: [] },
      retention: { oldestEntryUtc: null, overdue: false },
    };
    let result: LogStatus | undefined;

    service.getStatus().subscribe((r) => (result = r));

    httpMock.expectOne('/api/admin/logs/status').flush(status);
    expect(result).toEqual(status);
  });

  describe('saved filters', () => {
    const saved: SavedLogFilter = {
      id: 4,
      name: 'Alle Fehler',
      filter: 'level>=error',
      sortOrder: 3,
    };

    it('listFilters GETs the list', () => {
      let result: SavedLogFilter[] | undefined;
      service.listFilters().subscribe((r) => (result = r));

      const req = httpMock.expectOne('/api/admin/log-filters');
      expect(req.request.method).toBe('GET');
      req.flush([saved]);
      expect(result).toEqual([saved]);
    });

    it('createFilter POSTs name and filter', () => {
      let result: SavedLogFilter | undefined;
      service.createFilter('Alle Fehler', 'level>=error').subscribe((r) => (result = r));

      const req = httpMock.expectOne('/api/admin/log-filters');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ name: 'Alle Fehler', filter: 'level>=error' });
      req.flush(saved, { status: 201, statusText: 'Created' });
      expect(result).toEqual(saved);
    });

    it('updateFilter PUTs name and filter to the id', () => {
      service.updateFilter(4, 'Fehler', 'level>=error').subscribe();

      const req = httpMock.expectOne('/api/admin/log-filters/4');
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({ name: 'Fehler', filter: 'level>=error' });
      req.flush({ ...saved, name: 'Fehler' });
    });

    it('deleteFilter DELETEs the id', () => {
      let completed = false;
      service.deleteFilter(4).subscribe({ complete: () => (completed = true) });

      const req = httpMock.expectOne('/api/admin/log-filters/4');
      expect(req.request.method).toBe('DELETE');
      req.flush(null, { status: 204, statusText: 'No Content' });
      expect(completed).toBe(true);
    });
  });
});
