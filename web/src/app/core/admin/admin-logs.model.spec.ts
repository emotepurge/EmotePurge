import { describe, expect, it } from 'vitest';

import {
  isLogCaptureComplete,
  isLogFilterApiError,
  LogSourceState,
  LogStatus,
} from './admin-logs.model';

function status(api: LogSourceState, worker: LogSourceState, complete: boolean): LogStatus {
  const source = (name: 'api' | 'worker', state: LogSourceState) => ({
    source: name,
    state,
    instanceId: state === 'missing' ? null : 'a1b2c3d4',
    startedUtc: state === 'missing' ? null : '2026-10-13T00:00:00Z',
    lastFlushUtc: state === 'missing' ? null : '2026-10-14T11:59:30Z',
  });
  return {
    generatedAtUtc: '2026-10-14T12:00:00Z',
    sources: [source('api', api), source('worker', worker)],
    coverage24h: { complete, recordedErrors: 2, incompleteSince: [] },
    retention: { oldestEntryUtc: null, overdue: false },
  };
}

describe('isLogCaptureComplete', () => {
  it('is true only when both sources are healthy and the coverage is complete', () => {
    expect(isLogCaptureComplete(status('healthy', 'healthy', true))).toBe(true);
  });

  it('is false for a healthy pair whose 24 h coverage is incomplete', () => {
    expect(isLogCaptureComplete(status('healthy', 'healthy', false))).toBe(false);
  });

  it.each<LogSourceState>(['stale', 'disabled', 'missing'])(
    'is false when one source is %s even though the coverage marker says complete',
    (state) => {
      expect(isLogCaptureComplete(status('healthy', state, true))).toBe(false);
      expect(isLogCaptureComplete(status(state, 'healthy', true))).toBe(false);
    },
  );
});

describe('isLogFilterApiError', () => {
  it('accepts the invalid_log_filter body', () => {
    expect(
      isLogFilterApiError({
        errorCode: 'invalid_log_filter',
        reason: 'invalid_level',
        position: 6,
        length: 5,
      }),
    ).toBe(true);
  });

  it.each([
    null,
    'invalid_log_filter',
    { errorCode: 'invalid_log_range' },
    { errorCode: 'invalid_log_filter', reason: 'invalid_level' },
    { errorCode: 'invalid_log_filter', reason: 'x', position: '6', length: 5 },
  ])('rejects %j', (body) => {
    expect(isLogFilterApiError(body)).toBe(false);
  });
});
