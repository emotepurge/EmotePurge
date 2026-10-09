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
