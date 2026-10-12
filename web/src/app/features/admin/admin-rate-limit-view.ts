import {
  RateLimitPolicySnapshot,
  RateLimitProviderCounters,
  RateLimitTelemetrySnapshot,
  RateLimitTelemetryTrend,
} from '../../core/admin/admin.model';

/** One policy that rejected something: the exception the section exists to show. */
export interface PolicyExceptionRow {
  name: string;
  /** Rejected in the last minute: still happening, listed before everything else. */
  acute: boolean;
  rejectedLastMinute: number;
  rejectedLast24Hours: number;
  acceptedLast24Hours: number;
  /**
   * Rejected share of all policy decisions in 24 h, in percent. `null` when there were no
   * decisions at all ("no requests"): there is nothing to divide, and 0 % would claim a measurement.
   * This is the aggregate over all partitions, never the fill level of a bucket.
   */
  sharePercent: number | null;
}

/** One provider/call-source pair that answered with HTTP 429. Counts only: no quota to divide by. */
export interface ProviderExceptionRow {
  providerName: string;
  callSource: string;
  acute: boolean;
  rateLimitedLastMinute: number;
  rateLimitedLast24Hours: number;
}

export interface TrendSeriesSpec {
  id: string;
  kind: 'local' | 'provider';
  providerName?: string;
  callSource?: string;
  values: number[];
}

/** Acute (rejected within the last minute) first, then by 24 h count descending, then by name. */
function byUrgency<T>(
  acute: (row: T) => boolean,
  count24h: (row: T) => number,
  name: (row: T) => string,
): (a: T, b: T) => number {
  return (a, b) =>
    Number(acute(b)) - Number(acute(a)) ||
    count24h(b) - count24h(a) ||
    name(a).localeCompare(name(b));
}

export function policyExceptions(
  snapshot: RateLimitTelemetrySnapshot | undefined,
): PolicyExceptionRow[] {
  // With the counter store unreachable every count is a fabricated 0 — no rows, not zero rows.
  if (!snapshot?.telemetryAvailable) {
    return [];
  }
  return snapshot.policies
    .filter((p) => p.rejectedLastMinute > 0 || p.rejectedLast24Hours > 0)
    .map((p) => toPolicyRow(p))
    .sort(
      byUrgency(
        (r) => r.acute,
        (r) => r.rejectedLast24Hours,
        (r) => r.name,
      ),
    );
}

function toPolicyRow(policy: RateLimitPolicySnapshot): PolicyExceptionRow {
  const decisions = policy.acceptedLast24Hours + policy.rejectedLast24Hours;
  return {
    name: policy.name,
    acute: policy.rejectedLastMinute > 0,
    rejectedLastMinute: policy.rejectedLastMinute,
    rejectedLast24Hours: policy.rejectedLast24Hours,
    acceptedLast24Hours: policy.acceptedLast24Hours,
    sharePercent: decisions > 0 ? (policy.rejectedLast24Hours / decisions) * 100 : null,
  };
}

export function providerExceptions(
  snapshot: RateLimitTelemetrySnapshot | undefined,
): ProviderExceptionRow[] {
  if (!snapshot?.telemetryAvailable) {
    return [];
  }
  return snapshot.providers
    .filter((p) => p.rateLimitedLastMinute > 0 || p.rateLimitedLast24Hours > 0)
    .map((p: RateLimitProviderCounters) => ({
      providerName: p.providerName,
      callSource: p.callSource,
      acute: p.rateLimitedLastMinute > 0,
      rateLimitedLastMinute: p.rateLimitedLastMinute,
      rateLimitedLast24Hours: p.rateLimitedLast24Hours,
    }))
    .sort(
      byUrgency(
        (r) => r.acute,
        (r) => r.rateLimitedLast24Hours,
        (r) => r.providerName + r.callSource,
      ),
    );
}

/**
 * The series of the 24 h trend chart: the aggregated local rejections always, plus one separate
 * series per provider/call-source dimension that was rate-limited in at least one minute of the
 * window. A dimension without a single 429 is left out — a flat zero line per quiet provider would
 * be noise next to the one that matters.
 */
export function trendSeriesSpecs(trend: RateLimitTelemetryTrend | undefined): TrendSeriesSpec[] {
  if (!trend?.telemetryAvailable || trend.minuteStartsUtc.length === 0) {
    return [];
  }
  const specs: TrendSeriesSpec[] = [{ id: 'local', kind: 'local', values: trend.localRejections }];
  for (const provider of trend.providers) {
    if (provider.rateLimited.some((n) => n > 0)) {
      specs.push({
        id: `provider:${provider.providerName}|${provider.callSource}`,
        kind: 'provider',
        providerName: provider.providerName,
        callSource: provider.callSource,
        values: provider.rateLimited,
      });
    }
  }
  return specs;
}
