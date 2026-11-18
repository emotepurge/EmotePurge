import { describe, expect, it } from 'vitest';

import {
  normalizeRequesterLogin,
  parseEmoteInput,
  parseProbationLeadDays,
  parseWishDurationDays,
  validateWishAlias,
} from './wish-input';

const HEX = '60ae958e229664e8667aea38';
const ULID = '01FDGSXVJG0004RTSM3QHRP7QN';

describe('parseEmoteInput', () => {
  it.each([
    [`https://7tv.app/emotes/${HEX}`, HEX],
    [`https://7tv.app/emotes/${HEX}/`, HEX],
    [`https://7tv.app/emotes/${HEX}?ref=chat`, HEX],
    [`https://7tv.app/emotes/${HEX}/?ref=chat#x`, HEX],
    [`  https://7tv.app/emotes/${HEX}  `, HEX],
    [`http://7tv.app/emotes/${HEX}`, HEX],
    [`7tv.app/emotes/${HEX}`, HEX],
    [`https://www.7tv.app/emotes/${HEX}`, HEX],
    [`https://old.7tv.app/emotes/${HEX}`, HEX],
    [`https://7tv.gg/emotes/${HEX}`, HEX],
    [`https://7TV.app/Emotes/${HEX}`, HEX],
    [`https://7tv.app/emotes/${ULID}`, ULID],
    [HEX, HEX],
    [`  ${HEX}\n`, HEX],
    [HEX.toUpperCase(), HEX.toUpperCase()],
    [ULID, ULID],
  ])('accepts %j', (input, id) => {
    expect(parseEmoteInput(input)).toEqual({ ok: true, id });
  });

  it.each([
    ['KEKW'],
    ['OMEGALUL'],
    ['0123456789abcdef'],
    [`${HEX}0`],
    [HEX.slice(1)],
    ['zzzzzzzzzzzzzzzzzzzzzzzz'],
    [`${ULID}A`],
    ['01FDGSXVJG0004RTSM3QHRP7QI'], // "I" is not Crockford base32
    [`https://7tv.app/emotes/KEKW`],
    [`https://7tv.app/emotes/`],
    [`https://7tv.app/emotes`],
    [`https://7tv.app/${HEX}`],
    [`https://7tv.app/users/${HEX}`],
    [`https://7tv.app/emotes/${HEX}/extra`],
    [`https://7tv.app/emote-sets/${HEX}`],
    [`https://example.com/emotes/${HEX}`],
    [`https://7tv.app.evil.example/emotes/${HEX}`],
    [`https://evil.example/7tv.app/emotes/${HEX}`],
    [`ftp://7tv.app/emotes/${HEX}`],
    [`https://7tv.app//emotes/${HEX}`],
    ['not a link at all'],
    [`${HEX} ${HEX}`],
  ])('rejects %j as invalid', (input) => {
    expect(parseEmoteInput(input)).toEqual({ ok: false, reason: 'invalid' });
  });

  it('tells empty from invalid', () => {
    expect(parseEmoteInput('')).toEqual({ ok: false, reason: 'empty' });
    expect(parseEmoteInput('   \n')).toEqual({ ok: false, reason: 'empty' });
  });
});

describe('normalizeRequesterLogin', () => {
  it('treats empty input as not given', () => {
    expect(normalizeRequesterLogin('')).toEqual({ kind: 'none' });
    expect(normalizeRequesterLogin('   ')).toEqual({ kind: 'none' });
    expect(normalizeRequesterLogin(' @ ')).toEqual({ kind: 'none' });
  });

  it('trims, drops one leading @ and lower-cases', () => {
    expect(normalizeRequesterLogin('  @HandOfBlood ')).toEqual({
      kind: 'ok',
      login: 'handofblood',
    });
    expect(normalizeRequesterLogin('Some_User1')).toEqual({ kind: 'ok', login: 'some_user1' });
  });

  it('drops only one @', () => {
    expect(normalizeRequesterLogin('@@abcd')).toEqual({ kind: 'invalid' });
  });

  it.each([
    [3, false],
    [4, true],
    [25, true],
    [26, false],
  ])('length %i -> valid %s', (length, valid) => {
    const result = normalizeRequesterLogin('a'.repeat(length));
    expect(result.kind).toBe(valid ? 'ok' : 'invalid');
  });

  it('rejects characters outside the server pattern', () => {
    for (const bad of ['ab cd', 'ab-cd', 'äbcd', 'ab.cd', 'ab​cd']) {
      expect(normalizeRequesterLogin(bad), bad).toEqual({ kind: 'invalid' });
    }
  });
});

describe('duration and lead', () => {
  it.each([
    [0, false],
    [1, true],
    [365, true],
    [366, false],
    [-1, false],
  ])('duration %i -> valid %s', (days, valid) => {
    expect(parseWishDurationDays(days).ok).toBe(valid);
    expect(parseWishDurationDays(String(days)).ok).toBe(valid);
  });

  it.each([
    [0, false],
    [1, true],
    [30, true],
    [31, false],
  ])('lead %i -> valid %s', (days, valid) => {
    expect(parseProbationLeadDays(days).ok).toBe(valid);
    expect(parseProbationLeadDays(String(days)).ok).toBe(valid);
  });

  it('names the reason', () => {
    expect(parseWishDurationDays(0)).toEqual({ ok: false, reason: 'out_of_range' });
    expect(parseWishDurationDays('366')).toEqual({ ok: false, reason: 'out_of_range' });
    expect(parseWishDurationDays(null)).toEqual({ ok: false, reason: 'empty' });
    expect(parseWishDurationDays(undefined)).toEqual({ ok: false, reason: 'empty' });
    expect(parseWishDurationDays('  ')).toEqual({ ok: false, reason: 'empty' });
  });

  it('rejects decimals and other non-integers', () => {
    for (const bad of ['1.5', '30.0', '1e2', '-3', '+3', '3 days', '0x10', '1,5']) {
      expect(parseWishDurationDays(bad), bad).toEqual({ ok: false, reason: 'not_integer' });
    }
    expect(parseWishDurationDays(1.5)).toEqual({ ok: false, reason: 'not_integer' });
    expect(parseWishDurationDays(Number.NaN)).toEqual({ ok: false, reason: 'not_integer' });
  });

  it('returns the number, trimmed text included', () => {
    expect(parseWishDurationDays(' 30 ')).toEqual({ ok: true, value: 30 });
    expect(parseProbationLeadDays(7)).toEqual({ ok: true, value: 7 });
  });
});

describe('validateWishAlias (code points)', () => {
  it('accepts 1 to 100 code points', () => {
    expect(validateWishAlias('K')).toEqual({ ok: true, alias: 'K' });
    expect(validateWishAlias('a'.repeat(100)).ok).toBe(true);
    expect(validateWishAlias('a'.repeat(101))).toEqual({ ok: false, reason: 'too_long' });
  });

  it('counts an emoji as one code point, not two UTF-16 units', () => {
    expect(validateWishAlias('😀'.repeat(100)).ok).toBe(true);
    expect(validateWishAlias('😀'.repeat(101))).toEqual({ ok: false, reason: 'too_long' });
  });

  it('rejects the empty alias', () => {
    expect(validateWishAlias('')).toEqual({ ok: false, reason: 'empty' });
  });

  it('rejects whitespace and control characters anywhere, without trimming', () => {
    for (const bad of [
      ' a',
      'a ',
      'a b',
      'a\tb',
      'a\nb',
      'a\u0000b',
      'a\u0085b',
      'a b',
      'a　b',
      ' ',
    ]) {
      expect(validateWishAlias(bad), JSON.stringify(bad)).toEqual({
        ok: false,
        reason: 'forbidden_char',
      });
    }
  });

  it('allows letters of any script and the characters 7TV itself allows', () => {
    for (const ok of ['Hänno', 'ß', 'KEKW', 'a_b-c.d', '(:']) {
      expect(validateWishAlias(ok).ok, ok).toBe(true);
    }
  });
});
