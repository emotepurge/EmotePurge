/**
 * Mirror of `GET /api/channels/{channel}/usage-stats/import-coverage` (spec 2026-10-09, §5.6): which
 * days of a set were filled from a chat archive. All date fields are `yyyy-MM-dd`; they are null and
 * `sources`/`intervals` empty when nothing is imported for the scope.
 */
export interface ImportCoverage {
  /** The set the answer is for; null = every set; "" = the channel has no active set yet. */
  emoteSetId: string | null;
  /** Distinct archives the covered days came from, as imported — not as configured now. */
  sources: ImportCoverageSource[];
  importedFrom: string | null;
  /** EXCLUSIVE: the last covered day + 1. The caption shows the day before it. */
  importedTo: string | null;
  hasGaps: boolean;
  /** Start of the covered stretch that ends exactly at the counting start; null when none does. */
  contiguousFrom: string | null;
  intervals: ImportCoverageInterval[];
}

export interface ImportCoverageSource {
  name: string;
  url: string;
}

export interface ImportCoverageInterval {
  from: string;
  /** Exclusive. */
  to: string;
  archiveHost: string;
}

/** The set a coverage read is about: the page's chosen set, the channel's active set, or every set. */
export type ImportCoverageScope =
  | { readonly kind: 'active' }
  | { readonly kind: 'all' }
  | { readonly kind: 'set'; readonly emoteSetId: string };

/**
 * The caption's wording, decided from the coverage alone. `null` = the plain tracked-since sentence
 * stays. The sentence is `lead` + one link per source + `tail`; the gaps variant only changes the tail
 * (", with gaps.") because the link sits between the two halves.
 */
export interface ImportCaption {
  leadKey: string;
  tailKey: string;
}

/** Disclosure is unconditional (B7, D34): any imported day for the viewed scope earns the sentence. */
export function importCaptionFor(coverage: ImportCoverage | null): ImportCaption | null {
  if (coverage === null || coverage.importedFrom === null || coverage.importedTo === null) {
    return null;
  }
  return {
    leadKey: 'usageStats.trackedSinceWithImport',
    tailKey: coverage.hasGaps
      ? 'usageStats.trackedSinceWithImportGaps'
      : 'usageStats.trackedSinceWithImportEnd',
  };
}

/** `importedTo` is exclusive on the wire; the caption names the last imported day itself. */
export function importedToInclusive(coverage: ImportCoverage): string | null {
  if (coverage.importedTo === null) {
    return null;
  }
  const [year, month, day] = coverage.importedTo.split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1, day - 1));
  return date.toISOString().slice(0, 10);
}

/**
 * Where reliable counting starts for the before-tracking warning and the trend (D34/D49): the start
 * of the covered stretch that reaches the counting start, and only then. A rejoin gap (`trackedSince`
 * later than the imported end) or no covered day next to the counting start leaves the live start in
 * charge, as does an unreadable coverage.
 *
 * Both sides are UTC dates: `importedTo` is the server's `DateOnly`, `trackedSinceDate` the first ten
 * characters of the UTC `trackedSince` timestamp.
 */
export function coverageStartFor(
  coverage: ImportCoverage | null,
  trackedSinceDate: string | null,
): string | null {
  if (
    coverage !== null &&
    trackedSinceDate !== null &&
    coverage.contiguousFrom !== null &&
    coverage.importedTo === trackedSinceDate
  ) {
    return coverage.contiguousFrom;
  }
  return trackedSinceDate;
}

/**
 * The earliest day "all time" may start at: the viewed set's first imported day when it lies before
 * the tracking start, otherwise the tracking start itself (operator decision 2026-10-09). `coverage`
 * is null while unknown or unreadable, which reads exactly like "nothing imported".
 */
export function allTimeEarliestFor(
  coverage: ImportCoverage | null,
  trackedSinceDate: string | null,
): string | null {
  if (trackedSinceDate === null) {
    return null;
  }
  const importedFrom = coverage?.importedFrom ?? null;
  return importedFrom !== null && importedFrom < trackedSinceDate ? importedFrom : trackedSinceDate;
}

/**
 * True when imported days lie before `startDate` (the counting start the warning names): the stretch
 * before it is then patchy rather than empty, and the warning must not say nothing was counted.
 */
export function hasImportedDaysBefore(
  coverage: ImportCoverage | null,
  startDate: string | null,
): boolean {
  const importedFrom = coverage?.importedFrom ?? null;
  return importedFrom !== null && startDate !== null && importedFrom < startDate;
}

/**
 * The first day live days can be known for (#366), or `null` = no clipping. Live days (`ChannelLiveDay`)
 * come from our own Helix poll and exist only from the tracking start; an imported day carries chat
 * counts and no live information. A live-day statement over a range that contains imported days must
 * therefore start at the tracking start and say so — otherwise one recorded live day reads as "the
 * channel was live on one day in six months" under a curve that spans the import.
 *
 * Clipping applies only when the shown range [from, to] (both inclusive) contains at least one
 * imported day of the viewed scope; otherwise every live-day statement stays exactly as it was. The
 * boundary is the tracking start date, or — when the set status could not be read — the first day after
 * the imports (`importedTo`, exclusive on the wire). A boundary at or before `from` clips nothing.
 *
 * With gaps the individual intervals decide, so a range lying wholly in a gap between two imports is
 * not treated as imported. All values are `yyyy-MM-dd` UTC dates.
 */
export function liveKnownFromFor(
  coverage: ImportCoverage | null,
  trackedSinceDate: string | null,
  from: string,
  to: string,
): string | null {
  if (coverage === null || coverage.importedFrom === null || coverage.importedTo === null) {
    return null;
  }
  const spans =
    coverage.intervals.length > 0
      ? coverage.intervals.map((interval) => ({ from: interval.from, to: interval.to }))
      : [{ from: coverage.importedFrom, to: coverage.importedTo }];
  // `span.to` is exclusive: the range touches the span when its first day is before it.
  const importedInRange = spans.some((span) => span.from <= to && span.to > from);
  if (!importedInRange) {
    return null;
  }
  const boundary = trackedSinceDate ?? coverage.importedTo;
  return boundary > from ? boundary : null;
}
