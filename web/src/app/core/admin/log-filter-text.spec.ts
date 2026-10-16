import { describe, expect, it } from 'vitest';

import {
  appendFilterTerm,
  filterErrorSelection,
  filterTerm,
  filterValueFits,
  normalizeFilterWhitespace,
  quoteFilterValue,
} from './log-filter-text';

describe('quoteFilterValue', () => {
  // `x*` and `C\temp` stay bare: `*` cannot be escaped anyway, and a backslash is only special before `"`.
  it.each([
    'TwitchChatManager',
    'EmotePurge.Worker.Worker',
    'handofblood',
    'a1b2c3d4',
    'x*',
    'C\\temp',
  ])('leaves a plain word unquoted: %s', (value) => {
    expect(quoteFilterValue(value)).toBe(value);
  });

  it.each([
    ['two words', '"two words"'],
    ['C:', '"C:"'],
    ['tab\there', '"tab\there"'],
    ['a,b', '"a,b"'],
    ['http://x', '"http://x"'],
    ['a<b', '"a<b"'],
    ['a>b', '"a>b"'],
    ['-leading', '"-leading"'],
    ['', '""'],
  ])('quotes %j as %s', (value, expected) => {
    expect(quoteFilterValue(value)).toBe(expected);
  });

  it('escapes a double quote as \\" and leaves other backslashes literal', () => {
    expect(quoteFilterValue('say "hi"')).toBe('"say \\"hi\\""');
    expect(quoteFilterValue('a\\"b')).toBe('"a\\\\"b"');
    expect(quoteFilterValue('C:\\my temp')).toBe('"C:\\my temp"');
  });

  it('keeps a trailing backslash out of the closing quote by widening it with *', () => {
    // `"a b\"` would read as an escaped quote and never terminate; the grammar has no way to escape a
    // backslash, so the closest valid term is the superset `"a b\*"`.
    expect(quoteFilterValue('a b\\')).toBe('"a b\\*"');
  });
});

describe('filterTerm', () => {
  it('builds key:value and -key:value', () => {
    expect(filterTerm('source', 'worker', false)).toBe('source:worker');
    expect(filterTerm('cat', 'TwitchChatManager', true)).toBe('-cat:TwitchChatManager');
  });

  it('quotes the value when needed', () => {
    expect(filterTerm('channel', 'hand of blood', false)).toBe('channel:"hand of blood"');
    expect(filterTerm('path', '/a,b', true)).toBe('-path:"/a,b"');
  });
});

describe('filterValueFits', () => {
  it('accepts a value up to the server limit of 200 characters and refuses a longer one', () => {
    expect(filterValueFits('x'.repeat(200))).toBe(true);
    expect(filterValueFits('x'.repeat(201))).toBe(false);
    // Quotes and escapes are not part of the value the parser measures.
    expect(filterValueFits(`${'"'.repeat(100)} ${'y'.repeat(99)}`)).toBe(true);
  });

  it('counts the * a quoted value ending in a backslash is widened with', () => {
    // 199 characters plus the *: still 200.
    expect(filterValueFits(`a b${'c'.repeat(195)}\\`)).toBe(true);
    expect(filterValueFits(`a b${'c'.repeat(196)}\\`)).toBe(false);
    // Bare, a trailing backslash is literal and widens nothing.
    expect(filterValueFits(`${'c'.repeat(199)}\\`)).toBe(true);
  });
});

describe('appendFilterTerm', () => {
  it('appends to an empty or blank filter without a separator', () => {
    expect(appendFilterTerm('', 'source:api')).toBe('source:api');
    expect(appendFilterTerm('   ', 'source:api')).toBe('source:api');
  });

  it('appends with one space after trailing whitespace', () => {
    expect(appendFilterTerm('level>=warn', 'source:api')).toBe('level>=warn source:api');
    expect(appendFilterTerm('level>=warn  ', 'source:api')).toBe('level>=warn source:api');
  });

  it('does not append a term that is already present as a whole term', () => {
    expect(appendFilterTerm('level>=warn source:api', 'source:api')).toBe('level>=warn source:api');
    expect(appendFilterTerm('channel:"a b" x', 'channel:"a b"')).toBe('channel:"a b" x');
  });

  it('appends when the term only occurs as part of another term', () => {
    expect(appendFilterTerm('-source:api', 'source:api')).toBe('-source:api source:api');
    expect(appendFilterTerm('source:api,worker', 'source:api')).toBe(
      'source:api,worker source:api',
    );
    expect(appendFilterTerm('"source:api now"', 'source:api')).toBe('"source:api now" source:api');
  });

  it('treats whitespace inside a quoted value as part of the term', () => {
    expect(appendFilterTerm('cat:"x y"', 'y"')).toBe('cat:"x y" y"');
    expect(appendFilterTerm('msg:"a \\" b" c', 'b"')).toBe('msg:"a \\" b" c b"');
  });
});

describe('normalizeFilterWhitespace', () => {
  it('trims and collapses whitespace between terms', () => {
    expect(normalizeFilterWhitespace('  level>=error  \t source:api\n')).toBe(
      'level>=error source:api',
    );
  });

  it('keeps whitespace inside quotes', () => {
    expect(normalizeFilterWhitespace(' -"angestoßen (Quelle"   -" gejoint." ')).toBe(
      '-"angestoßen (Quelle" -" gejoint."',
    );
    expect(normalizeFilterWhitespace('cat:"a  b",c   x')).toBe('cat:"a  b",c x');
  });

  it('maps a blank filter to the empty string', () => {
    expect(normalizeFilterWhitespace(' \t ')).toBe('');
  });
});

describe('filterErrorSelection', () => {
  it('selects position to position + length', () => {
    expect(
      filterErrorSelection({
        errorCode: 'invalid_log_filter',
        reason: 'invalid_level',
        position: 6,
        length: 5,
      }),
    ).toEqual({ start: 6, end: 11 });
  });

  it('uses the server indices unchanged after an emoji (both sides count UTF-16 code units)', () => {
    const q = '😀 level:debug';
    // The C# parser reports position 9: the emoji is a surrogate pair, two code units.
    const selection = filterErrorSelection({
      errorCode: 'invalid_log_filter',
      reason: 'invalid_level',
      position: 9,
      length: 5,
    });

    expect(q.slice(selection.start, selection.end)).toBe('debug');
  });

  it('never returns a negative or inverted selection', () => {
    expect(
      filterErrorSelection({
        errorCode: 'invalid_log_filter',
        reason: 'too_long',
        position: -1,
        length: -3,
      }),
    ).toEqual({ start: 0, end: 0 });
  });
});
