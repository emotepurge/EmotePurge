/**
 * Ordering and grouping for the "Recently added" sort of the usage page.
 *
 * Moderators drop throwaway emotes into the set during a stream and want to take them out again
 * afterwards. The question is "what came in on that day", not "how much is it used", so the sheet
 * drops its usage bands in this mode and groups by the calendar day an emote was added instead.
 * The day is the viewer's local one — a stream that runs past midnight UTC is still one evening.
 */

/** A group of rows added on one local calendar day, or the trailing group with no known date. */
export interface AddedDayGroup<T> {
  /** `yyyy-MM-dd` of the local day, or {@link UNKNOWN_ADDED_DAY} for rows without a date. */
  key: string;
  items: T[];
}

export const UNKNOWN_ADDED_DAY = 'unknown';

/** The shape of a row this module can date: the two fields that can carry an add date. */
export interface AddDated {
  /**
   * The date the Api holds for the row: a slot row's own add date; an emote-mode row's oldest own
   * slot (or first-seen) date. `null` = unknown.
   */
  firstSeenAt: string | null;
  /** The viewed set's own 7TV add date of a live card in a non-active view; wins over the rest. */
  setAddedAt?: string;
  /** The 7TV add date of a per-entry live row that has no counted row (so no `firstSeenAt`). */
  liveAddedAt?: string;
}

/**
 * When the row entered the shown set, in epoch milliseconds, or `null` when no date is known.
 *
 * Preference: the viewed set's own 7TV date (`setAddedAt`, non-active views), then `firstSeenAt`
 * (the Api moves a slot row's date forward on a re-add; a provisional value is still a usable
 * observation time), then `liveAddedAt` of a per-entry live row, which has no `firstSeenAt`.
 */
export function addedAtOf(row: AddDated): number | null {
  for (const candidate of [row.setAddedAt, row.firstSeenAt, row.liveAddedAt]) {
    if (candidate) {
      const parsed = Date.parse(candidate);
      if (!Number.isNaN(parsed)) {
        return parsed;
      }
    }
  }
  return null;
}

/** `yyyy-MM-dd` of the instant in the runtime's local time zone. */
export function localDayKey(epochMs: number): string {
  const date = new Date(epochMs);
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

/** The group a row belongs to — also what the inspector names for a single row. */
export function addedDayKeyOf(row: AddDated): string {
  const at = addedAtOf(row);
  return at === null ? UNKNOWN_ADDED_DAY : localDayKey(at);
}

/**
 * Splits `items` into local-day groups, `desc` = newest day first. Inside a group the add time runs
 * in the same direction, the name (ascending, whichever the direction) breaks a tie. Rows without a
 * date form one trailing group in name order — last in both directions, because "unknown" is not
 * the oldest nor the newest, it is outside the question.
 */
export function groupByAddedDay<T extends AddDated>(
  items: readonly T[],
  direction: 'asc' | 'desc',
  name: (item: T) => string,
): AddedDayGroup<T>[] {
  const factor = direction === 'desc' ? -1 : 1;
  const byName = (a: T, b: T) => name(a).localeCompare(name(b), undefined, { sensitivity: 'base' });

  const dated: { item: T; at: number }[] = [];
  const unknown: T[] = [];
  for (const item of items) {
    const at = addedAtOf(item);
    if (at === null) {
      unknown.push(item);
    } else {
      dated.push({ item, at });
    }
  }

  dated.sort((a, b) => factor * (a.at - b.at) || byName(a.item, b.item));

  const groups: AddedDayGroup<T>[] = [];
  for (const { item, at } of dated) {
    const key = localDayKey(at);
    const last = groups[groups.length - 1];
    if (last?.key === key) {
      last.items.push(item);
    } else {
      groups.push({ key, items: [item] });
    }
  }

  if (unknown.length > 0) {
    groups.push({ key: UNKNOWN_ADDED_DAY, items: unknown.sort(byName) });
  }
  return groups;
}

export type AddedDayKind = 'today' | 'yesterday' | 'day';

/**
 * Whether a local-day key is `now`'s day, the day before it, or neither. Compared as calendar days
 * built from `now`'s local components, so month and year boundaries need no special case.
 */
export function addedDayKind(dayKey: string, now: Date): AddedDayKind {
  if (dayKey === localDayKey(now.getTime())) {
    return 'today';
  }
  const yesterday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1);
  return dayKey === localDayKey(yesterday.getTime()) ? 'yesterday' : 'day';
}
