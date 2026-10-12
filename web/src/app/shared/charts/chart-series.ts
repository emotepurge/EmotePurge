/**
 * Pure geometry, scale and formatting helpers behind `app-line-chart`. Kept free of Angular so the
 * decisions that matter — where a line breaks, which ticks a scale gets, how a unit reads — are
 * testable without rendering anything.
 */

export type ChartUnit = 'cores' | 'bytes' | 'percent' | 'tasks' | 'count';

/** Plot area and value domain a series is projected into. X is index-based: bucket `i` of `count`. */
export interface LineGeometry {
  width: number;
  height: number;
  yMin: number;
  yMax: number;
  /** Number of buckets on the x axis; defaults to the series length. Pass it to keep x positions
   *  stable when a series is split into a complete and a partial part. */
  count?: number;
}

export interface SeriesSummary {
  /** Smallest known value, or null when nothing is known. */
  min: number | null;
  max: number | null;
  /** Last known value (the last bucket that is not null), not necessarily the last bucket. */
  last: number | null;
  /** Number of buckets with a known value. */
  known: number;
}

const MAX_TICKS = 50;
const BYTES_PER_MIB = 1024 ** 2;
const BYTES_PER_GIB = 1024 ** 3;

/** X position of bucket `index`; a single bucket sits in the middle. */
export function xAt(index: number, count: number, width: number): number {
  return count <= 1 ? width / 2 : (index / (count - 1)) * width;
}

/** Y position of `value`, growing downwards like SVG does; a flat domain maps to the middle. */
export function yAt(value: number, yMin: number, yMax: number, height: number): number {
  if (yMax === yMin) {
    return height / 2;
  }
  return height - ((value - yMin) / (yMax - yMin)) * height;
}

/**
 * SVG path data for a series. Every run of known values is its own `M … L …` sub-path: a `null`
 * (unknown) ends the run and the next known value starts a new one, so the line never bridges a gap
 * and never interpolates across it. A run of one value is drawn as a zero-length segment, which
 * renders a dot with round line caps instead of vanishing. All-null input yields an empty string.
 */
export function toLinePath(values: readonly (number | null)[], geometry: LineGeometry): string {
  const count = geometry.count ?? values.length;
  const parts: string[] = [];
  let run: string[] = [];

  const flush = () => {
    if (run.length === 1) {
      run.push(run[0].replace('M', 'L'));
    }
    if (run.length > 0) {
      parts.push(run.join(' '));
    }
    run = [];
  };

  values.forEach((value, index) => {
    if (value === null || !Number.isFinite(value)) {
      flush();
      return;
    }
    const x = round(xAt(index, count, geometry.width));
    const y = round(yAt(value, geometry.yMin, geometry.yMax, geometry.height));
    run.push(`${run.length === 0 ? 'M' : 'L'}${x} ${y}`);
  });
  flush();

  return parts.join(' ');
}

/**
 * Round tick values for the axis [min, max]: a 1/2/5 × 10^n step, ticks starting at the multiple of
 * the step at or below `min` and ending at or above `max`, so the data always lies inside the first
 * and last tick. `count` is the wanted number of ticks, not a guarantee; `minStep` stops a scale
 * of whole things (counts) from getting fractional ticks that would round to duplicates. Degenerate input (a flat or
 * inverted range) yields a small range around the value rather than a single tick.
 */
export function niceTicks(min: number, max: number, count: number, minStep = 0): number[] {
  if (!Number.isFinite(min) || !Number.isFinite(max)) {
    return [];
  }
  if (max < min) {
    [min, max] = [max, min];
  }
  if (max === min) {
    max = min === 0 ? 1 : min + Math.abs(min) * 0.5;
    if (min !== 0) {
      min = min - Math.abs(min) * 0.5;
    }
  }

  const intervals = Math.max(1, count - 1);
  const rawStep = (max - min) / intervals;
  const magnitude = 10 ** Math.floor(Math.log10(rawStep));
  const fraction = rawStep / magnitude;
  const niceFraction = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
  const step = Math.max(niceFraction * magnitude, minStep);

  const first = Math.floor(min / step + 1e-9) * step;
  const ticks: number[] = [];
  // Capped: at huge magnitudes `value += step` stops changing the value and would never reach max.
  for (let i = 0, value = first; i < MAX_TICKS; i++, value += step) {
    ticks.push(roundTo(value, step));
    if (value >= max - step * 1e-9) {
      break;
    }
  }
  return ticks;
}

/**
 * A value with its unit, readable on an axis and in the reading line. Cores keep two decimals,
 * percent one (a value of 12.5 means 12.5 %), bytes switch between MiB and GiB at 1 GiB, tasks and
 * counts are shown with the precision their magnitude needs. `locale` only picks separators;
 * `unitWord` is the translated word for cores and tasks, which have no symbol.
 */
export function formatUnit(value: number, unit: ChartUnit, locale: string, unitWord = ''): string {
  const number = (digits: number) =>
    new Intl.NumberFormat(locale, {
      minimumFractionDigits: digits,
      maximumFractionDigits: digits,
    }).format(value);

  switch (unit) {
    case 'cores':
      return `${number(2)} ${unitWord}`.trim();
    case 'percent':
      return `${number(1)} %`;
    case 'bytes':
      return Math.abs(value) >= BYTES_PER_GIB
        ? `${new Intl.NumberFormat(locale, { maximumFractionDigits: 2, minimumFractionDigits: 2 }).format(value / BYTES_PER_GIB)} GiB`
        : `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1, minimumFractionDigits: 1 }).format(value / BYTES_PER_MIB)} MiB`;
    case 'tasks':
      return `${number(Number.isInteger(value) ? 0 : 2)} ${unitWord}`.trim();
    case 'count':
      return new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(value);
  }
}

/** Min, max, last known value and number of known buckets — the text alternative's raw material. */
export function summarizeSeries(values: readonly (number | null)[]): SeriesSummary {
  let min: number | null = null;
  let max: number | null = null;
  let last: number | null = null;
  let known = 0;

  for (const value of values) {
    if (value === null || !Number.isFinite(value)) {
      continue;
    }
    known++;
    last = value;
    min = min === null ? value : Math.min(min, value);
    max = max === null ? value : Math.max(max, value);
  }

  return { min, max, last, known };
}

/**
 * A bucket start for reading, always in UTC. Buckets of a day or wider show the date only; finer
 * ones add the time and say "UTC" so nobody reads it as local time.
 */
export function formatBucketTime(iso: string, bucketSeconds: number, locale: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  if (bucketSeconds >= 86_400) {
    return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(date);
  }
  const text = new Intl.DateTimeFormat(locale, {
    dateStyle: 'short',
    timeStyle: 'short',
    timeZone: 'UTC',
  }).format(date);
  return `${text} UTC`;
}

/** Shorter form for the x axis: date for wide buckets, time of day for narrow ones. */
export function formatAxisTime(iso: string, bucketSeconds: number, locale: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  const options: Intl.DateTimeFormatOptions =
    bucketSeconds >= 86_400
      ? { day: '2-digit', month: '2-digit', timeZone: 'UTC' }
      : bucketSeconds >= 3_600 * 6
        ? { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', timeZone: 'UTC' }
        : { hour: '2-digit', minute: '2-digit', timeZone: 'UTC' };
  return new Intl.DateTimeFormat(locale, options).format(date);
}

/** The processes a host provenance code (`A`, `W`, `AW`) names, in a fixed order; unknown codes name none. */
export function provenanceSources(code: string | null): ('api' | 'worker')[] {
  switch (code) {
    case 'A':
      return ['api'];
    case 'W':
      return ['worker'];
    case 'AW':
      return ['api', 'worker'];
    default:
      return [];
  }
}

/**
 * Sums a minute series into coarser buckets laid out as `bucketStartsUtc[0] + i * bucketSeconds`.
 * A bucket that starts before the first minute or after the end of the last one is `null`:
 * unknown, never a fabricated 0. The bucket the minutes end in (the running one) keeps its partial
 * sum. Minutes outside every bucket are ignored.
 */
export function sumMinutesIntoBuckets(
  minuteStartsUtc: readonly string[],
  values: readonly number[],
  bucketStartsUtc: readonly string[],
  bucketSeconds: number,
): (number | null)[] {
  if (bucketStartsUtc.length === 0 || minuteStartsUtc.length === 0 || bucketSeconds <= 0) {
    return bucketStartsUtc.map(() => null);
  }
  const first = Date.parse(bucketStartsUtc[0]);
  const bucketMs = bucketSeconds * 1000;
  const minutes = minuteStartsUtc.map((iso) => Date.parse(iso));
  const coveredFrom = Math.min(...minutes);
  const coveredTo = Math.max(...minutes) + 60_000;
  if (!Number.isFinite(first) || !Number.isFinite(coveredFrom)) {
    return bucketStartsUtc.map(() => null);
  }

  const sums = bucketStartsUtc.map(() => 0);
  minutes.forEach((minute, position) => {
    const index = Math.floor((minute - first) / bucketMs);
    if (index >= 0 && index < sums.length) {
      sums[index] += values[position] ?? 0;
    }
  });

  return sums.map((sum, index) => {
    const start = first + index * bucketMs;
    return start >= coveredFrom && start < coveredTo ? sum : null;
  });
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

/** Removes the floating-point dust a repeated `+= step` leaves (0.30000000000000004). */
function roundTo(value: number, step: number): number {
  const decimals = Math.max(0, -Math.floor(Math.log10(step)) + 1);
  return Number(value.toFixed(Math.min(decimals, 12)));
}
