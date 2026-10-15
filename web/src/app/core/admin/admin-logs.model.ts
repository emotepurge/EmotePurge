/**
 * Wire types of the admin log viewer (`/api/admin/logs*` and `/api/admin/log-filters*`), mirroring
 * `AdminLogEndpoints.cs`. Every instant is an ISO-8601 UTC string as the server sends it — with up to
 * seven fractional digits and trailing zeros dropped — and is never rebuilt on the client: frozen bounds
 * and reconcile starts go back to the server byte for byte. Nullable fields arrive as `null`, not
 * missing.
 */

/** Wire tokens of `LogViewerTokens.Level`; `crit` is Critical. Debug and Trace are never stored. */
export type LogLevelKey = 'info' | 'warn' | 'error' | 'crit';

/** Wire tokens of `LogViewerTokens.Source`. */
export type LogSourceKey = 'api' | 'worker';

export type LogRangePreset = '1h' | '6h' | '24h' | '7d';

/** Either a relative preset (its end is the request time) or an absolute pair, never both — the server
 *  answers a mixed request with `400 invalid_log_range`. */
export type LogRangeQuery = { range: LogRangePreset } | { from: string; to: string };

/** A captured property value: the writer stores strings, numbers, booleans and short collections. */
export type LogPropertyValue =
  | string
  | number
  | boolean
  | null
  | readonly LogPropertyValue[]
  | { readonly [key: string]: LogPropertyValue };

/** One entry as lists show it. The exception text is only in {@link LogEntryDetail}. */
export interface LogEntry {
  id: number;
  timestampUtc: string;
  level: LogLevelKey;
  source: LogSourceKey;
  instanceId: string;
  category: string;
  eventId: number | null;
  eventName: string | null;
  message: string;
  hasException: boolean;
  exceptionType: string | null;
  /** Always an object; `{}` when the entry has none. */
  properties: Readonly<Record<string, LogPropertyValue>>;
  /** A field was cut at its capture limit. */
  truncated: boolean;
}

/** `GET /api/admin/logs/{id}`. */
export interface LogEntryDetail extends LogEntry {
  exception: string | null;
  processStartedUtc: string;
  /** Whole seconds from the process start to the entry. */
  uptimeSeconds: number;
}

/**
 * `GET /api/admin/logs` with a range, and the targeted reload `?ids=`. A range page carries the exact,
 * unsnapped bounds it ran on, which the client freezes and sends back as `from`/`to` with the cursor.
 * The `ids=` answer has no range: both bounds are `null`, `nextCursor` is `null` and `truncated` is
 * `false`.
 */
export interface LogPage {
  entries: LogEntry[];
  nextCursor: string | null;
  rangeFromUtc: string | null;
  rangeToUtc: string | null;
  truncated: boolean;
}

export interface LogEntryKey {
  id: number;
  timestampUtc: string;
}

/** `GET /api/admin/logs?keysOnly=true` — at most 2,500 keys newest first; `truncated` when more matched. */
export interface LogKeyPage {
  entries: LogEntryKey[];
  nextCursor: null;
  rangeFromUtc: string;
  rangeToUtc: string;
  truncated: boolean;
}

export interface LogFacetValue<T extends string = string> {
  value: T;
  count: number;
}

export interface LogCategoryFacetValue {
  value: string;
  /** The last segment after a dot. */
  short: string;
  count: number;
}

/**
 * Drop totals of the synthetic capture reports in the range, independent of the filter.
 * `writeUnconfirmed` entries are only possibly lost — they may be in the list as well.
 */
export interface LogDropTotals {
  total: number;
  queueFull: number;
  budget: number;
  writeUnconfirmed: number;
  captureFailed: number;
  reentrant: number;
  rejected: number;
}

export interface LogProcessStart {
  source: LogSourceKey;
  instanceId: string;
  startedUtc: string;
}

/** `GET /api/admin/logs/summary`. `rangeFromUtc`/`rangeToUtc` are exact; the bucket bounds are snapped
 *  and only for the histogram. `counts.error` holds Error and Critical. */
export interface LogSummary {
  rangeFromUtc: string;
  rangeToUtc: string;
  bucketFromUtc: string;
  bucketToUtc: string;
  bucketSeconds: number;
  bucketStartsUtc: string[];
  counts: { info: number[]; warn: number[]; error: number[] };
  total: number;
  facets: {
    source: LogFacetValue<LogSourceKey>[];
    level: LogFacetValue<LogLevelKey>[];
    category: LogCategoryFacetValue[];
    categoryOther: number;
  };
  dropped: LogDropTotals;
  processStarts: LogProcessStart[];
}

export type LogSourceState = 'healthy' | 'stale' | 'disabled' | 'missing';

/** Liveness of one source from its newest instance; the instance fields are `null` when `missing`. */
export interface LogSourceStatus {
  source: LogSourceKey;
  state: LogSourceState;
  instanceId: string | null;
  startedUtc: string | null;
  lastFlushUtc: string | null;
}

export interface LogIncompleteSince {
  source: LogSourceKey;
  lastIncompleteAtUtc: string;
}

/**
 * `GET /api/admin/logs/status` (30 s server cache). Liveness (`sources`, always api and worker) and
 * coverage (`coverage24h`) are separate on purpose — see {@link isLogCaptureComplete} for the one
 * condition under which an error count may be called complete.
 */
export interface LogStatus {
  generatedAtUtc: string;
  sources: LogSourceStatus[];
  coverage24h: {
    complete: boolean;
    /** A lower bound when `complete` is false. */
    recordedErrors: number;
    incompleteSince: LogIncompleteSince[];
  };
  retention: { oldestEntryUtc: string | null; overdue: boolean };
}

/** `GET/POST/PUT /api/admin/log-filters`. */
export interface SavedLogFilter {
  id: number;
  name: string;
  filter: string;
  sortOrder: number;
}

/** The closed `reason` vocabulary of an invalid filter (`LogFilterErrorReasons`); translated under
 *  `admin.logs.filterErrors.<reason>`. */
export type LogFilterErrorReason =
  | 'unterminated_quote'
  | 'empty_term'
  | 'empty_value'
  | 'invalid_operator'
  | 'invalid_level'
  | 'invalid_source'
  | 'invalid_instance'
  | 'invalid_duration'
  | 'invalid_value'
  | 'too_long'
  | 'too_many_terms'
  | 'too_many_values';

/** Body of `400 invalid_log_filter`. `position`/`length` are UTF-16 indices into the submitted filter —
 *  the same unit as JavaScript string indices. */
export interface LogFilterApiError {
  errorCode: 'invalid_log_filter';
  reason: LogFilterErrorReason;
  position: number;
  length: number;
}

/** Narrows an `HttpErrorResponse.error` body to {@link LogFilterApiError}. */
export function isLogFilterApiError(body: unknown): body is LogFilterApiError {
  if (typeof body !== 'object' || body === null) {
    return false;
  }
  const candidate = body as Partial<Record<keyof LogFilterApiError, unknown>>;
  return (
    candidate.errorCode === 'invalid_log_filter' &&
    typeof candidate.reason === 'string' &&
    typeof candidate.position === 'number' &&
    typeof candidate.length === 'number'
  );
}

/**
 * True only when both sources are `healthy` **and** the 24 h coverage is complete (spec §11). Neither
 * alone is enough: a healthy heartbeat says nothing about an error dropped an hour ago, and a disabled
 * source's gap marker is never bumped, so its coverage can look complete while nothing is captured.
 */
export function isLogCaptureComplete(status: LogStatus): boolean {
  return status.coverage24h.complete && status.sources.every((s) => s.state === 'healthy');
}
