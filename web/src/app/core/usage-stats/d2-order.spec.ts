import { describe, expect, it } from 'vitest';

import { compareD2, compareUtf8 } from './d2-order';

const EMOJI = '\u{1F600}';
const TILDE = '～';

function byteOrder(a: string, b: string): number {
  const x = new TextEncoder().encode(a);
  const y = new TextEncoder().encode(b);
  for (let i = 0; i < Math.min(x.length, y.length); i++) {
    if (x[i] !== y[i]) {
      return Math.sign(x[i] - y[i]);
    }
  }
  return Math.sign(x.length - y.length);
}

describe('compareUtf8', () => {
  it('uses fixtures that really are what the pairs claim', () => {
    expect(EMOJI.codePointAt(0)).toBe(0x1f600);
    expect(EMOJI).toHaveLength(2);
    const prefixed = 'a\u{1F600}';
    expect(prefixed.codePointAt(1)).toBe(0x1f600);
    expect(prefixed).toHaveLength(3);
  });

  const pairs: [string, string][] = [
    ['abc', 'abd'],
    ['ab', 'abc'],
    ['Zebra', 'apple'],
    ['a\u{1F600}', 'a￿'],
    ['é', ''],
    [EMOJI, TILDE],
    ['', EMOJI],
    ['same', 'same'],
  ];

  it.each(pairs)('agrees with a byte comparison of the UTF-8 encoding: %j vs %j', (x, y) => {
    expect(Math.sign(compareUtf8(x, y))).toBe(byteOrder(x, y));
    expect(Math.sign(compareUtf8(y, x))).toBe(byteOrder(y, x));
  });

  it('differs from the JS < order exactly where a supplementary character meets U+E000-U+FFFF', () => {
    for (const [supplementary, bmp] of [
      [EMOJI, TILDE],
      ['a\u{1F600}', 'a￿'],
      [EMOJI, ''],
    ]) {
      expect(supplementary < bmp).toBe(true);
      expect(compareUtf8(supplementary, bmp)).toBeGreaterThan(0);
    }
  });

  it('reads a lone surrogate as U+FFFD, then breaks a scalar tie by UTF-16 ordinal, like the server', () => {
    // U+FFFD sorts after U+E000 (JS < says the opposite), and before a supplementary character.
    expect(compareUtf8('\uD800', '')).toBeGreaterThan(0);
    expect(compareUtf8('\uD800', EMOJI)).toBeLessThan(0);
    expect(compareUtf8('\uD800', '�')).toBeLessThan(0);
    expect(compareUtf8('�', '\uD800')).toBeGreaterThan(0);
    expect(compareUtf8('\uD800', '\uD800')).toBe(0);
  });
});

describe('compareD2', () => {
  it('ranks the date before the name, with unknown dates last', () => {
    const sorted = [
      { name: 'a', instant: null },
      { name: 'z', instant: 1 },
      { name: 'b', instant: 2 },
    ].sort(compareD2);
    expect(sorted.map((entry) => entry.name)).toEqual(['z', 'b', 'a']);
  });

  it('breaks a date tie, known or both unknown, by UTF-8 name order', () => {
    for (const instant of [5, null]) {
      const sorted = [
        { name: EMOJI, instant },
        { name: TILDE, instant },
      ].sort(compareD2);
      expect(sorted.map((entry) => entry.name)).toEqual([TILDE, EMOJI]);
    }
  });
});
