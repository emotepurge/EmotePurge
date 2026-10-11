import { describe, expect, it } from 'vitest';

import {
  RateLimitPolicySnapshot,
  RateLimitProviderCounters,
  RateLimitTelemetrySnapshot,
  RateLimitTelemetryTrend,
} from '../../core/admin/admin.model';
import { policyExceptions, providerExceptions, trendSeriesSpecs } from './admin-rate-limit-view';

function policy(overrides: Partial<RateLimitPolicySnapshot>): RateLimitPolicySnapshot {
  return {
    name: 'Voting',
    type: 'token-bucket',
    capacity: 40,
    tokensPerPeriod: 40,
    replenishmentPeriodSeconds: 60,
    windowSeconds: null,
    partition: 'per-user',
    queueLimit: 0,
    acceptedLastMinute: 0,
    rejectedLastMinute: 0,
    acceptedLast24Hours: 0,
    rejectedLast24Hours: 0,
    ...overrides,
  };
}

function provider(overrides: Partial<RateLimitProviderCounters>): RateLimitProviderCounters {
  return {
    providerName: 'seventv',
    callSource: 'sync',
    requestsLastMinute: 0,
    requestsLast24Hours: 0,
    rateLimitedLastMinute: 0,
    rateLimitedLast24Hours: 0,
    lastRetryAfterSeconds: null,
    lastRateLimitedAtUtc: null,
    lastHeaderSample: null,
    ...overrides,
  };
}

function snapshot(overrides: Partial<RateLimitTelemetrySnapshot>): RateLimitTelemetrySnapshot {
  return {
    telemetryAvailable: true,
    policies: [],
    lastLocalRejection: null,
    caches: [],
    providers: [],
    ...overrides,
  };
}

function trend(overrides: Partial<RateLimitTelemetryTrend>): RateLimitTelemetryTrend {
  return {
    telemetryAvailable: true,
    fromUtc: '2026-10-10T12:00:00Z',
    toUtc: '2026-10-11T12:00:00Z',
    minuteStartsUtc: ['2026-10-11T11:58:00Z', '2026-10-11T11:59:00Z'],
    localRejections: [0, 1],
    providers: [],
    ...overrides,
  };
}

describe('policyExceptions', () => {
  it('lists only policies with a rejection, acute ones first, then by 24 h rejections descending', () => {
    const rows = policyExceptions(
      snapshot({
        policies: [
          policy({ name: 'Quiet', acceptedLast24Hours: 500 }),
          policy({ name: 'Big', rejectedLast24Hours: 90 }),
          policy({ name: 'Small', rejectedLast24Hours: 5 }),
          policy({ name: 'Acute', rejectedLastMinute: 1, rejectedLast24Hours: 2 }),
        ],
      }),
    );

    expect(rows.map((r) => r.name)).toEqual(['Acute', 'Big', 'Small']);
    expect(rows[0].acute).toBe(true);
    expect(rows[1].acute).toBe(false);
  });

  it('breaks ties by name so the order is stable between polls', () => {
    const rows = policyExceptions(
      snapshot({
        policies: [
          policy({ name: 'B', rejectedLast24Hours: 3 }),
          policy({ name: 'A', rejectedLast24Hours: 3 }),
        ],
      }),
    );

    expect(rows.map((r) => r.name)).toEqual(['A', 'B']);
  });

  it('computes the rejected share of all decisions', () => {
    const [row] = policyExceptions(
      snapshot({ policies: [policy({ acceptedLast24Hours: 75, rejectedLast24Hours: 25 })] }),
    );

    expect(row.sharePercent).toBe(25);
  });

  it('reports "no requests" (null share) instead of dividing when there were no decisions', () => {
    const [row] = policyExceptions(snapshot({ policies: [policy({ rejectedLastMinute: 1 })] }));

    expect(row.sharePercent).toBeNull();
  });

  it('yields nothing without telemetry, because the counts are fabricated zeros then', () => {
    expect(
      policyExceptions(
        snapshot({ telemetryAvailable: false, policies: [policy({ rejectedLast24Hours: 9 })] }),
      ),
    ).toEqual([]);
    expect(policyExceptions(undefined)).toEqual([]);
  });
});

describe('providerExceptions', () => {
  it('lists only rate-limited dimensions, acute first, then by 24 h count', () => {
    const rows = providerExceptions(
      snapshot({
        providers: [
          provider({ providerName: 'twitch', callSource: 'api', requestsLast24Hours: 900 }),
          provider({ providerName: 'seventv', callSource: 'sync', rateLimitedLast24Hours: 40 }),
          provider({
            providerName: 'seventv',
            callSource: 'search',
            rateLimitedLastMinute: 1,
            rateLimitedLast24Hours: 3,
          }),
        ],
      }),
    );

    expect(rows.map((r) => r.callSource)).toEqual(['search', 'sync']);
  });
});

describe('trendSeriesSpecs', () => {
  it('gives the local series plus one separate series per rejecting dimension', () => {
    const specs = trendSeriesSpecs(
      trend({
        providers: [
          { providerName: 'seventv', callSource: 'sync', rateLimited: [0, 2] },
          { providerName: 'seventv', callSource: 'search', rateLimited: [1, 0] },
          { providerName: 'twitch', callSource: 'api', rateLimited: [3, 3] },
          { providerName: 'twitch', callSource: 'helix', rateLimited: [0, 0] },
        ],
      }),
    );

    expect(specs).toHaveLength(4);
    expect(new Set(specs.map((s) => s.id)).size).toBe(4);
    expect(specs[0].kind).toBe('local');
    expect(specs.map((s) => s.callSource).filter(Boolean)).toEqual(['sync', 'search', 'api']);
    expect(specs.some((s) => s.callSource === 'helix')).toBe(false);
  });

  it('is only the local series when no provider was rate-limited', () => {
    const specs = trendSeriesSpecs(
      trend({ providers: [{ providerName: 'twitch', callSource: 'api', rateLimited: [0, 0] }] }),
    );

    expect(specs.map((s) => s.kind)).toEqual(['local']);
  });

  it('draws nothing when telemetry is unavailable or the window is empty', () => {
    expect(trendSeriesSpecs(trend({ telemetryAvailable: false }))).toEqual([]);
    expect(trendSeriesSpecs(trend({ minuteStartsUtc: [], localRejections: [] }))).toEqual([]);
    expect(trendSeriesSpecs(undefined)).toEqual([]);
  });
});
