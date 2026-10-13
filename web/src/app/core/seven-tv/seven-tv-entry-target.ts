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

/**
 * The part of a {@link SevenTvEntryTarget} that names the entry and its generation — all a run row
 * needs once it is queued: the removal sends the alias and binds to the date, the report names
 * slot, alias and date. A restore row carries only this (Plan-378 4.7): it re-adds the entry under
 * its alias and reports the slot and the **original** add date of the generation that was removed,
 * but has no card of its own — a row read from a purge protocol file never had one in this tab.
 */
export type SevenTvEntryGeneration = Omit<SevenTvEntryTarget, 'cardKey'>;

/** At most sixteen fraction digits — the most System.Text.Json reads (it keeps the first seven,
 *  `DateTimeOffset`'s 100 ns ticks, and rejects a seventeenth). Not capped at seven: the server
 *  reads a nanosecond date (nine digits) fine, and a date refused here drops its card out of every
 *  run. */
const ENTRY_DATE_PATTERN =
  /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,16})?(?:Z|([+-])(\d{2}):(\d{2}))$/;

/** The largest zone offset `DateTimeOffset` accepts, in minutes (±14:00). */
const MAX_OFFSET_MINUTES = 14 * 60;

const SECONDS_PER_DAY = 24 * 60 * 60;

function daysInMonth(year: number, month: number): number {
  if (month === 2) {
    return (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0 ? 29 : 28;
  }
  return [4, 6, 9, 11].includes(month) ? 30 : 31;
}

/**
 * Strict check of an entry's add date, **before** any `ADD`/`REMOVE` goes out: the Api binds
 * `AddedAt` as a `DateTimeOffset` (System.Text.Json), and a value it rejects makes the closing
 * report fail with `400 invalid_request_body` after 7TV was already changed — a retry never repairs
 * that. `Date.parse` is far too lenient for this (`"0"` and `2026-02-31T…` both parse).
 *
 * Valid is ISO 8601 with date, time and an explicit zone, exactly where System.Text.Json reads it
 * (measured against .NET 10's `Utf8JsonReader.GetDateTimeOffset`): real calendar fields (month
 * lengths, leap years), time fields in range, at most sixteen fraction digits, a zone of `Z` or
 * `±hh:mm` no further out than ±14:00, and a UTC instant inside `DateTimeOffset`'s range
 * (`0001-01-01T00:00:00Z` to `9999-12-31T23:59:59.9999999Z` — `0001-01-01T00:00:00+01:00` lies
 * before it).
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
  const offsetSign = match[7] === '-' ? -1 : 1;
  const offsetHour = match[8] === undefined ? 0 : Number(match[8]);
  const offsetMinute = match[9] === undefined ? 0 : Number(match[9]);
  const offsetMinutes = offsetSign * (offsetHour * 60 + offsetMinute);
  const fieldsValid =
    year >= 1 &&
    month >= 1 &&
    month <= 12 &&
    day >= 1 &&
    day <= daysInMonth(year, month) &&
    hour <= 23 &&
    minute <= 59 &&
    second <= 59 &&
    offsetMinute <= 59 &&
    Math.abs(offsetMinutes) <= MAX_OFFSET_MINUTES;
  if (!fieldsValid) {
    return null;
  }
  // Only the first and the last day of the range can push the UTC instant out of it. Whole seconds
  // suffice: the offset is whole minutes, and a fraction below one second never crosses the bound.
  const secondOfDay = hour * 3600 + minute * 60 + second;
  const utcSecondOfDay = secondOfDay - offsetMinutes * 60;
  const beforeMin = year === 1 && month === 1 && day === 1 && utcSecondOfDay < 0;
  const afterMax = year === 9999 && month === 12 && day === 31 && utcSecondOfDay >= SECONDS_PER_DAY;
  return beforeMin || afterMax ? null : value;
}

/**
 * Whether `entry` can be reported as it stands: a non-empty alias, a slot id that is either absent
 * or non-empty, and an add date that passes {@link parseStrictEntryDate}. The restore checks every
 * target with it right before queueing (Plan-378 4.3, T-6) — a target that fails would let an `ADD`
 * change 7TV and then have its report refused, so its row never reaches the queue.
 */
export function isValidEntryGeneration(entry: SevenTvEntryGeneration): boolean {
  return (
    typeof entry.alias === 'string' &&
    entry.alias.length > 0 &&
    (entry.slotId === undefined || (typeof entry.slotId === 'string' && entry.slotId.length > 0)) &&
    parseStrictEntryDate(entry.addedAt) !== null
  );
}
