/** An entry the D2 order ranks: its add instant (ms, `null` = unknown) and its name. */
export interface D2Entry {
  name: string;
  instant: number | null;
}

/**
 * Strings in UTF-8 byte order, the server's name order (`BaseSlotRule.Utf8Order`, Postgres `COLLATE "C"`).
 * Compares Unicode scalar values; a lone surrogate reads as U+FFFD and ties then fall back to UTF-16 ordinal, as on the server.
 */
export function compareUtf8(a: string, b: string): number {
  const left = a[Symbol.iterator]();
  const right = b[Symbol.iterator]();
  for (;;) {
    const x = left.next();
    const y = right.next();
    if (x.done || y.done) {
      if (x.done && y.done) {
        return ordinal(a, b);
      }
      return x.done ? -1 : 1;
    }
    const byScalar = scalar(x.value) - scalar(y.value);
    if (byScalar !== 0) {
      return byScalar;
    }
  }
}

/** D2 order: add instant ascending, unknown (`null`) last, then name in UTF-8 byte order. */
export function compareD2(a: D2Entry, b: D2Entry): number {
  if (a.instant !== b.instant) {
    if (a.instant === null) {
      return 1;
    }
    if (b.instant === null) {
      return -1;
    }
    return a.instant - b.instant;
  }
  return compareUtf8(a.name, b.name);
}

function scalar(character: string): number {
  const code = character.codePointAt(0) ?? 0;
  return code >= 0xd800 && code <= 0xdfff ? 0xfffd : code;
}

function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
