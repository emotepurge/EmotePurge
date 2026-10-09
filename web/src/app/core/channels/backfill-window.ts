import { AppLang } from '../i18n/language.service';
import { toLocale } from '../i18n/locale';
import { BackfillCoverageInterval, BackfillRun } from './backfill.model';

/** The day before an exclusive upper bound (`windowTo` is the day counting started). */
export function lastDayOf(exclusiveEnd: string): string {
  const date = new Date(`${exclusiveEnd}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() - 1);
  return date.toISOString().slice(0, 10);
}

/** An ISO calendar day (`YYYY-MM-DD`, UTC) in the reader's language. */
export function formatIsoDay(isoDay: string, lang: AppLang): string {
  return new Date(`${isoDay}T00:00:00Z`).toLocaleDateString(toLocale(lang), {
    timeZone: 'UTC',
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  });
}

/** An ISO instant in the reader's language and local time zone. */
export function formatInstant(iso: string, lang: AppLang): string {
  return new Date(iso).toLocaleString(toLocale(lang), { dateStyle: 'short', timeStyle: 'short' });
}

/** What a run was told to count against: the set's name, or its id while no name is known. */
export function backfillSetLabel(run: Pick<BackfillRun, 'emoteSetName' | 'emoteSetId'>): string {
  return run.emoteSetName ?? run.emoteSetId;
}

/** A stretch of already imported days that a new run would count against another set. */
export interface ReplacedInterval {
  /** First replaced day, inclusive. */
  from: string;
  /** Exclusive, like the server's coverage and window bounds. */
  to: string;
  emoteSetId: string;
  emoteSetName: string | null;
}

/**
 * The coverage a run for `setId` over `[windowFrom, windowTo)` would replace: the intervals held by
 * any other set, clipped to the window (OD-A) and merged where one set's stretches meet. Intervals
 * of the chosen set are re-imported in place, not replaced from the reader's point of view; ones
 * that only touch the window (`to === windowFrom`) or lie outside it are not part of it. ISO days
 * compare correctly as strings.
 */
export function replacedIntervals(
  coverage: readonly BackfillCoverageInterval[],
  windowFrom: string,
  windowTo: string,
  setId: string,
): ReplacedInterval[] {
  const clipped = coverage
    .filter((interval) => interval.emoteSetId !== setId)
    .map((interval) => ({
      from: interval.from > windowFrom ? interval.from : windowFrom,
      to: interval.to < windowTo ? interval.to : windowTo,
      emoteSetId: interval.emoteSetId,
      emoteSetName: interval.emoteSetName,
    }))
    .filter((interval) => interval.from < interval.to)
    .sort((a, b) => a.from.localeCompare(b.from));
  // The server splits coverage by set and by archive host; the host is irrelevant here, so
  // back-to-back stretches of one set read as one sentence.
  const merged: ReplacedInterval[] = [];
  for (const interval of clipped) {
    const last = merged.at(-1);
    if (last && last.emoteSetId === interval.emoteSetId && last.to === interval.from) {
      last.to = interval.to;
      last.emoteSetName = last.emoteSetName ?? interval.emoteSetName;
    } else {
      merged.push({ ...interval });
    }
  }
  return merged;
}
