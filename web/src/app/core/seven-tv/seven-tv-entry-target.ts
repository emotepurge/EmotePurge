/**
 * The one set entry a delete or restore row addresses (slot mass delete, #365 child 8): an emote id
 * alone does not name it, because one id can sit in a set several times under different aliases,
 * each entry with its own add date (its generation).
 */
export interface SevenTvEntryTarget {
  /** The usage slot the entry belongs to. Absent for an entry that has none (a card of the
   *  unslotted class): the report then names the entry by alias and date only. */
  slotId?: string;
  /** The alias the entry sits under; the only thing a slot removal sends to pick the entry. */
  alias: string;
  /** The entry's add date as the wire carried it, passed on **verbatim** — never reformatted, so
   *  what the report sends is the very string the server compares against. Valid per
   *  {@link parseStrictEntryDate}. */
  addedAt: string;
  /** Identity of the card the row came from; forms the queue key `${sevenTvEmoteId}#${cardKey}`
   *  and leads a finished row back to its card. */
  cardKey: string;
}

const ENTRY_DATE_PATTERN =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?(?:Z|[+-](\d{2}):(\d{2}))$/;

function daysInMonth(year: number, month: number): number {
  if (month === 2) {
    return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0 ? 29 : 28;
  }
  return [4, 6, 9, 11].includes(month) ? 30 : 31;
}

/**
 * Strict check of an entry's add date, **before** any `ADD`/`REMOVE` goes out: the Api binds
 * `AddedAt` as a `DateTimeOffset`, and a value it rejects makes the closing report fail with
 * `400 invalid_request_body` after 7TV was already changed — a retry never repairs that. `Date.parse`
 * is far too lenient for this (`"0"` and `2026-02-31T…` both parse).
 *
 * Valid is ISO 8601 with date, time and an explicit zone (`Z` or `±hh:mm`), real calendar fields
 * (month lengths, leap years), time fields in range, any number of fraction digits.
 *
 * @returns the very same string when valid, `null` otherwise — never a reformatted copy.
 */
export function parseStrictEntryDate(value: unknown): string | null {
  if (typeof value !== 'string') {
    return null;
  }
  const match = ENTRY_DATE_PATTERN.exec(value);
  if (!match) {
    return null;
  }
  const [year, month, day, hour, minute, second] = match.slice(1, 7).map(Number);
  const offsetHour = match[7] === undefined ? 0 : Number(match[7]);
  const offsetMinute = match[8] === undefined ? 0 : Number(match[8]);
  const valid =
    year >= 1 &&
    month >= 1 &&
    month <= 12 &&
    day >= 1 &&
    day <= daysInMonth(year, month) &&
    hour <= 23 &&
    minute <= 59 &&
    second <= 59 &&
    offsetHour <= 23 &&
    offsetMinute <= 59;
  return valid ? value : null;
}
