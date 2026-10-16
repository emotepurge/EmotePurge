import { LogFilterApiError } from './admin-logs.model';

/**
 * String tools for the log filter field. The frontend deliberately does not parse the filter language —
 * the one parser is `LogFilterParser` on the server (spec §7.4) — so these only know enough of its
 * lexical rules to quote a value, split a string into whole terms and mark an error:
 *
 * - a term ends at whitespace; a key term is `[-]key op value{,value}` with `key` a letter followed by
 *   letters, digits, `_` or `.`, and `op` one of `:` `>=` `<=` `>` `<`;
 * - a `"` opens a quote only at the start of a value or of a free-text term; inside it `\"` is a quote
 *   and every other backslash is literal; there is no escape for `*` (wildcard) or for a backslash.
 */

/** The server's limit for a saved filter's name (trimmed length). */
export const LOG_FILTER_NAME_MAX_LENGTH = 60;

/** Anything here makes a bare word mean something else: whitespace or a comma ends it, a quote opens a
 *  quoted value, and `:`/`<`/`>` turn free text into a key term. `\u0085` is whitespace to .NET only. */
const NEEDS_QUOTES = /[\s\u0085,":<>]/u;
const KEY_START = /\p{L}/u;
const KEY_CHAR = /[\p{L}\p{Nd}_.]/u;
const WHITESPACE = /[\s\u0085]/u;

/**
 * The value as it has to be typed: bare when it is a plain word, otherwise in double quotes with `"`
 * escaped as `\"`. A leading `-` is quoted too, so the value never reads as a negation in free text.
 *
 * Two values cannot be written exactly, because the grammar has no escape for them: a `*` is always a
 * wildcard, and a quoted value cannot end in a backslash (`\"` would escape the closing quote). The
 * latter gets a `*` appended — the closest valid term, a superset that still matches the value itself.
 */
export function quoteFilterValue(value: string): string {
  if (value !== '' && !NEEDS_QUOTES.test(value) && !value.startsWith('-')) {
    return value;
  }
  const escaped = value.replaceAll('"', '\\"');
  return `"${escaped}${escaped.endsWith('\\') ? '*' : ''}"`;
}

/** `key:value`, or `-key:value` to exclude; the value is quoted as needed. */
export function filterTerm(key: string, value: string, exclude: boolean): string {
  return `${exclude ? '-' : ''}${key}:${quoteFilterValue(value)}`;
}

/**
 * Appends `term` unless it already occurs as a whole term — character for character, so `-source:api`
 * and `source:api,worker` do not count as `source:api`. Facet and property clicks only ever add terms
 * (spec §7.4); they never remove or rewrite one.
 */
export function appendFilterTerm(q: string, term: string): string {
  const terms = splitFilterTerms(q);
  if (terms.includes(term)) {
    return q;
  }
  return terms.length === 0 ? term : `${q.trimEnd()} ${term}`;
}

/** Trims and collapses the whitespace between terms; whitespace inside quotes is part of the value and
 *  stays. Two filters that normalize equally are the same filter (the active saved-filter chip). */
export function normalizeFilterWhitespace(q: string): string {
  return splitFilterTerms(q).join(' ');
}

/** The range to select in the field for a `400 invalid_log_filter`. The server's `position`/`length` are
 *  UTF-16 indices, the same unit as JavaScript string indices, so this is the identity apart from
 *  guarding against a negative or inverted range. */
export function filterErrorSelection(error: LogFilterApiError): { start: number; end: number } {
  const start = Math.max(0, error.position);
  return { start, end: Math.max(start, error.position + error.length) };
}

/** The whole terms of `q`, in order, exactly as written. Follows the parser's lexical structure but never
 *  fails: an unterminated quote runs to the end, and junk after a closing quote stays in its term. */
function splitFilterTerms(q: string): string[] {
  const terms: string[] = [];
  let i = 0;
  while (true) {
    while (i < q.length && WHITESPACE.test(q[i])) {
      i++;
    }
    if (i >= q.length) {
      return terms;
    }

    const start = i;
    if (q[i] === '-') {
      i++;
    }
    const valuesStart = keyTermValuesStart(q, i);
    i = valuesStart === null ? readFreeText(q, i) : readValueList(q, valuesStart);
    terms.push(q.slice(start, i));
  }
}

/** Where the value list of a key term starting at `i` begins, or `null` when `i` starts free text. */
function keyTermValuesStart(q: string, i: number): number | null {
  if (i >= q.length || !KEY_START.test(q[i])) {
    return null;
  }
  let end = i;
  while (end < q.length && KEY_CHAR.test(q[end])) {
    end++;
  }
  const op = q[end];
  if (op === ':') {
    return end + 1;
  }
  if (op === '<' || op === '>') {
    return q[end + 1] === '=' ? end + 2 : end + 1;
  }
  return null;
}

function readFreeText(q: string, i: number): number {
  if (q[i] === '"') {
    i = readQuoted(q, i);
  }
  while (i < q.length && !WHITESPACE.test(q[i])) {
    i++;
  }
  return i;
}

function readValueList(q: string, i: number): number {
  while (true) {
    if (q[i] === '"') {
      i = readQuoted(q, i);
    }
    while (i < q.length && !WHITESPACE.test(q[i]) && q[i] !== ',') {
      i++;
    }
    if (q[i] !== ',') {
      return i;
    }
    i++;
  }
}

/** `i` is on the opening quote; returns the index after the closing one (or the end of the string). */
function readQuoted(q: string, i: number): number {
  i++;
  while (i < q.length) {
    if (q[i] === '\\' && q[i + 1] === '"') {
      i += 2;
    } else if (q[i] === '"') {
      return i + 1;
    } else {
      i++;
    }
  }
  return i;
}
