import { pluralKey } from '../../core/i18n/plural';
import { EmoteDailyUsage } from '../../core/usage-stats/usage-stat.model';

/**
 * Everything the usage sparkline needs between the API response and the rendered chart: filling the
 * sparse `/usage-stats/daily` response into the dense day array the chart draws from, the polyline
 * and live-band geometry, and the caption key that names what the bands mean. The caption lives here
 * rather than in the sidecar or the drilldown dialog because both render the same bands and must say
 * the same thing about them — a copy in each would drift the moment one of them changed.
 */

export interface SparklinePoint {
  /** ISO date, `yyyy-MM-dd`. */
  date: string;
  useCount: number;
}

const DAY_MS = 86_400_000;

/**
 * Fills [from, to] day by day; missing days become 0. Days outside the range are ignored — the
 * server clips the range already, this just refuses to trust it. An invalid or inverted range
 * yields `[]`.
 */
export function fillDailySeries(
  days: readonly EmoteDailyUsage[],
  from: string,
  to: string,
): SparklinePoint[] {
  const byDate = new Map(days.map((day) => [day.date, day.useCount]));
  return rangeDates(from, to).map((date) => ({ date, useCount: byDate.get(date) ?? 0 }));
}

/**
 * The same, for the batch endpoint's `[dayOffset, useCount]` pairs. Offsets index the dense array
 * directly, so this needs no date parsing per day at all — which is the point of the encoding.
 * Offsets outside the range are dropped rather than trusted, same stance as `fillDailySeries`.
 */
export function fillOffsetSeries(
  days: readonly (readonly number[])[],
  from: string,
  to: string,
): SparklinePoint[] {
  const points = rangeDates(from, to).map((date) => ({ date, useCount: 0 }));
  for (const [offset, useCount] of days) {
    if (offset >= 0 && offset < points.length) {
      points[offset].useCount = useCount;
    }
  }
  return points;
}

/**
 * ISO dates for day offsets counted from `from` — the batch endpoint carries its live days that
 * way, while `liveBands` speaks dates like the rest of this file. Converted once per response
 * rather than per emote inspected.
 */
export function offsetsToDates(offsets: readonly number[], from: string): string[] {
  const start = Date.parse(`${from}T00:00:00Z`);
  if (Number.isNaN(start)) {
    return [];
  }
  return offsets.map((offset) => new Date(start + offset * DAY_MS).toISOString().slice(0, 10));
}

/**
 * SVG polyline points in a 0..width / 0..height viewBox, y inverted (0 at the bottom edge). The
 * maximum is clamped to >= 1 so an all-zero series draws a flat baseline instead of dividing by
 * zero. A single point renders as a full-width flat line — one dot would be invisible.
 *
 * `drawFrom` (ISO day) is the first day the line may speak for: everything before it is left
 * undrawn rather than drawn as zero, because a baseline over days the emote did not exist on reads
 * as "unused" and is the opposite verdict. A day carrying usage is never left out, see
 * `firstDrawableIndex`. The x mapping keeps counting from the start of the array regardless, so the
 * curve stays aligned with the live bands, which span the whole range.
 */
export function toPolylinePoints(
  points: readonly SparklinePoint[],
  width: number,
  height: number,
  drawFrom?: string,
): string {
  if (points.length === 0) {
    return '';
  }

  const firstVisible = firstDrawableIndex(points, drawFrom);
  if (firstVisible >= points.length) {
    return '';
  }

  const visible = points.slice(firstVisible);
  const max = Math.max(1, ...visible.map((point) => point.useCount));
  const yOf = (useCount: number) => round(height - (useCount / max) * height);

  if (points.length === 1) {
    const y = yOf(points[0].useCount);
    return `0,${y} ${round(width)},${y}`;
  }

  const stepX = width / (points.length - 1);

  // A polyline with one coordinate draws nothing at all, so a single visible day gets a stub. It is
  // always the trailing half-step `[width - stepX/2, width]`: `visible` runs to the end of the
  // array, so `visible.length === 1` means the last day and `x === width`. That is exactly the span
  // `liveBands` gives that same day — never the full width, which is what the branch above does for
  // a one-day range and what would re-state the whole span here. The clamps stay as insurance
  // against a caller passing an array this reasoning does not hold for.
  if (visible.length === 1) {
    const x = firstVisible * stepX;
    const y = yOf(visible[0].useCount);
    return `${round(Math.max(0, x - stepX / 2))},${y} ${round(Math.min(width, x + stepX / 2))},${y}`;
  }

  return visible
    .map((point, index) => `${round((firstVisible + index) * stepX)},${yOf(point.useCount)}`)
    .join(' ');
}

/** One background band of consecutive live days, in viewBox x-units. */
export interface LiveBand {
  x: number;
  width: number;
}

/**
 * Groups the live days into full-height background bands under the polyline (A10). Each day owns
 * the half-step around its x position — the same x mapping as `toPolylinePoints` — so a band spans
 * from half a step before its first live day to half a step after its last, clamped to the
 * viewBox. Days not present in `points` are ignored; no live days (older ranges predate the
 * coverage data) simply means no bands, never a statement about being offline.
 */
export function liveBands(
  points: readonly SparklinePoint[],
  liveDays: readonly string[],
  width: number,
): LiveBand[] {
  if (points.length === 0 || liveDays.length === 0) {
    return [];
  }

  const live = new Set(liveDays);
  if (points.length === 1) {
    return live.has(points[0].date) ? [{ x: 0, width: round(width) }] : [];
  }

  const stepX = width / (points.length - 1);
  const bands: LiveBand[] = [];
  let runStart: number | null = null;
  // One index past the end, so a run that touches the last day is flushed like any other.
  for (let index = 0; index <= points.length; index++) {
    const isLive = index < points.length && live.has(points[index].date);
    if (isLive && runStart === null) {
      runStart = index;
    } else if (!isLive && runStart !== null) {
      const from = Math.max(0, (runStart - 0.5) * stepX);
      const to = Math.min(width, (index - 0.5) * stepX);
      bands.push({ x: round(from), width: round(to - from) });
      runStart = null;
    }
  }
  return bands;
}

/**
 * The busiest day; on a tie the earliest wins. `null` for an empty or all-zero series. `drawFrom`
 * excludes the days the curve does not draw, so the axis label and the line describe the same days —
 * it cannot change the answer on its own, because every day it skips is by definition unused and an
 * unused day never wins a peak. It stays here so the label provably reads the drawn days.
 */
export function seriesPeak(
  points: readonly SparklinePoint[],
  drawFrom?: string,
): { useCount: number; date: string } | null {
  let peak: SparklinePoint | null = null;
  for (let index = firstDrawableIndex(points, drawFrom); index < points.length; index++) {
    const point = points[index];
    if (point.useCount > (peak?.useCount ?? 0)) {
      peak = point;
    }
  }
  return peak ? { useCount: peak.useCount, date: peak.date } : null;
}

/**
 * How many of the days the emote could have been used on the stream was live, and on how many of
 * those it went unused. Counting starts at `firstDrawableIndex`: a live day before the emote entered
 * the set is not a day it could have been used — unless it was used, which settles the question
 * against `drawFrom`. Without `drawFrom` the whole range counts, which is the honest reading when
 * 7TV reported no date for the emote.
 *
 * The denominator is live days and never the length of the range — a missing ChannelLiveDay row
 * means "no data", never "offline", so "of 13 days" would state an absence nobody measured.
 */
export function liveDayCoverage(
  points: readonly SparklinePoint[],
  liveDays: readonly string[],
  drawFrom?: string,
): { live: number; unused: number } {
  const live = new Set(liveDays);
  let liveCount = 0;
  let unused = 0;
  for (let index = firstDrawableIndex(points, drawFrom); index < points.length; index++) {
    const point = points[index];
    if (!live.has(point.date)) {
      continue;
    }
    liveCount++;
    if (point.useCount === 0) {
      unused++;
    }
  }
  return { live: liveCount, unused };
}

/**
 * The transloco key for the line under the curve, or `null` when it must stay silent. Three forms,
 * because the honest sentence differs: some live days went unused, all of them were used, or none of
 * the live days fall inside the emote's lifetime — and in that last case the green bands are still
 * on screen and still need naming.
 *
 * It lives here rather than in either component because the sidecar and the drilldown dialog have to
 * say the same thing; two copies of this decision would drift the moment somebody touched one.
 */
export function liveDayCaptionKey(
  coverage: { live: number; unused: number },
  hasLiveDays: boolean,
  clipped = false,
): string | null {
  if (coverage.live === 0) {
    if (hasLiveDays) {
      return clipped ? 'usageStats.chart.liveLegendSince' : 'usageStats.chart.liveLegend';
    }
    // Imported days in the range and no live day left after clipping: the curve is not silent about
    // them — it says why there is nothing green.
    return clipped ? 'usageStats.chart.noLiveInfoImported' : null;
  }
  const base = coverage.unused === 0 ? 'usedOnAllLiveDays' : 'unusedOnLiveDays';
  return pluralKey(coverage.live, `usageStats.chart.${clipped ? 'since.' : ''}${base}`);
}

/**
 * The live days that count when imported days share the range (#366): those from `liveKnownFrom` on.
 * `null` = no clipping, the list comes back untouched. Plain string comparison, as everywhere here.
 */
export function liveDaysFrom(
  liveDays: readonly string[],
  liveKnownFrom: string | null,
): readonly string[] {
  return liveKnownFrom === null ? liveDays : liveDays.filter((day) => day >= liveKnownFrom);
}

/**
 * First index the curve may speak for. Days before `drawFrom` stay silent — but never a day that
 * carries usage: a day the emote was used on is by definition a day it existed, and 7TV moves a
 * re-added emote's `addedAt` forward past its own surviving history (see
 * SevenTvSyncService.UpsertEmote, which un-archives the same row and keeps its UsageStats). Hiding
 * measured counts is the worse false statement.
 *
 * Shared by all three readers of `drawFrom` so they cannot disagree about which days the curve, its
 * axis label and its live-day line are talking about.
 *
 * Returns `points.length` when no day qualifies, so a slice from it yields nothing.
 */
function firstDrawableIndex(points: readonly SparklinePoint[], drawFrom?: string): number {
  if (!drawFrom) {
    return 0;
  }
  // Tolerate a full ISO timestamp: `'2026-07-03' >= '2026-07-03T10:00:00Z'` is false, which would
  // silently drop the emote's own first day. Beyond that, plain string comparison: for `yyyy-MM-dd`
  // the lexicographic order is the chronological one, and the rest of this file counts UTC days the
  // same way.
  const day = drawFrom.slice(0, 10);
  const index = points.findIndex((point) => point.date >= day || point.useCount > 0);
  return index === -1 ? points.length : index;
}

/** Every ISO date in [from, to], inclusive. Empty for an invalid or inverted range. */
function rangeDates(from: string, to: string): string[] {
  const start = Date.parse(`${from}T00:00:00Z`);
  const end = Date.parse(`${to}T00:00:00Z`);
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) {
    return [];
  }

  const dates: string[] = [];
  for (let time = start; time <= end; time += DAY_MS) {
    dates.push(new Date(time).toISOString().slice(0, 10));
  }
  return dates;
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}
