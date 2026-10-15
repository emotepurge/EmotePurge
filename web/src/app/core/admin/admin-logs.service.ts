import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  LogEntryDetail,
  LogKeyPage,
  LogPage,
  LogRangeQuery,
  LogStatus,
  LogSummary,
  SavedLogFilter,
} from './admin-logs.model';

const LOGS_URL = '/api/admin/logs';
const FILTERS_URL = '/api/admin/log-filters';

/**
 * The client of the admin log viewer (`/api/admin/logs*`, `/api/admin/log-filters*`). Global-admin-only
 * server-side like every `/api/admin` route.
 *
 * Instants go on the wire exactly as the server sent them: a relative first page answers with exact
 * bounds, the page freezes them and loads more with `{ from, to }` (R4), and the reconcile start is a
 * server timestamp. Re-serializing through `Date` would cut them to milliseconds and shift the bounds.
 *
 * Errors are left to the caller: `400 invalid_log_filter` carries `reason`/`position`/`length`
 * (`isLogFilterApiError`), `400 invalid_log_range`, and `503` either with `log_query_timeout` or — from
 * the concurrency gate — with no body and `Retry-After: 5`.
 */
@Service()
export class AdminLogsService {
  private readonly http = inject(HttpClient);

  /** One page, newest first. `before` is the previous page's `nextCursor`; the server clamps `limit`
   *  to 1–200 and defaults it to 100. */
  getPage(q: string, range: LogRangeQuery, before?: string, limit?: number): Observable<LogPage> {
    let params = withRange(withFilter(new HttpParams(), q), range);
    if (before) {
      params = params.set('before', before);
    }
    if (limit !== undefined) {
      params = params.set('limit', limit);
    }
    return this.http.get<LogPage>(LOGS_URL, { params });
  }

  /** Reconcile keys from `fromUtc` to the request time (no `to`, no preset — the server rejects a
   *  preset next to `keysOnly`). At most 2,500 keys; `truncated` when more matched. */
  getKeys(q: string, fromUtc: string): Observable<LogKeyPage> {
    const params = withFilter(new HttpParams(), q).set('from', fromUtc).set('keysOnly', true);
    return this.http.get<LogKeyPage>(LOGS_URL, { params });
  }

  /** Targeted reload of up to 200 entries by id — no range and no filter (the ids come from the
   *  filtered key query). Ids that no longer exist are simply absent. Both range bounds are `null`. */
  getByIds(ids: readonly number[]): Observable<LogPage> {
    return this.http.get<LogPage>(LOGS_URL, { params: { ids: ids.join(',') } });
  }

  /** One entry with its exception text; `404 log_entry_not_found` once retention removed it. */
  getEntry(id: number): Observable<LogEntryDetail> {
    return this.http.get<LogEntryDetail>(`${LOGS_URL}/${id}`);
  }

  getSummary(q: string, range: LogRangeQuery): Observable<LogSummary> {
    const params = withRange(withFilter(new HttpParams(), q), range);
    return this.http.get<LogSummary>(`${LOGS_URL}/summary`, { params });
  }

  getStatus(): Observable<LogStatus> {
    return this.http.get<LogStatus>(`${LOGS_URL}/status`);
  }

  listFilters(): Observable<SavedLogFilter[]> {
    return this.http.get<SavedLogFilter[]>(FILTERS_URL);
  }

  createFilter(name: string, filter: string): Observable<SavedLogFilter> {
    return this.http.post<SavedLogFilter>(FILTERS_URL, { name, filter });
  }

  updateFilter(id: number, name: string, filter: string): Observable<SavedLogFilter> {
    return this.http.put<SavedLogFilter>(`${FILTERS_URL}/${id}`, { name, filter });
  }

  deleteFilter(id: number): Observable<void> {
    return this.http.delete<void>(`${FILTERS_URL}/${id}`);
  }
}

/** A blank filter means "everything" server-side; leaving it out keeps the URL clean. */
function withFilter(params: HttpParams, q: string): HttpParams {
  return q.trim() === '' ? params : params.set('q', q);
}

function withRange(params: HttpParams, range: LogRangeQuery): HttpParams {
  return 'range' in range
    ? params.set('range', range.range)
    : params.set('from', range.from).set('to', range.to);
}
