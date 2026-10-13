import { describe, expect, it } from 'vitest';

import { isValidEntryGeneration, parseStrictEntryDate } from './seven-tv-entry-target';

describe('parseStrictEntryDate', () => {
  // The Api binds `AddedAt` as a DateTimeOffset: every accepted value must survive that binding.
  it.each([
    '2026-10-13T08:00:14.902Z',
    '2026-10-13T08:00:14Z',
    '2026-10-13T08:00:14.902123+02:00',
    '2026-10-13T08:00:14-05:30',
    '2024-02-29T12:00:00Z',
    // The widest zones DateTimeOffset takes.
    '2026-10-13T08:00:14+14:00',
    '2026-10-13T08:00:14-14:00',
    // Sixteen fraction digits — the most System.Text.Json reads.
    '2026-10-13T08:00:14.1234567890123456Z',
    // The ends of DateTimeOffset's range in UTC.
    '0001-01-01T00:00:00Z',
    '0001-01-01T01:00:00+01:00',
    '9999-12-31T23:59:59.9999999Z',
    '9999-12-31T22:59:59.9999999-01:00',
  ])('accepts %s', (value) => {
    expect(parseStrictEntryDate(value)).toBe(value);
  });

  // `Date.parse` takes several of these; the report endpoint rejects them with a 400 after 7TV was
  // already changed.
  it.each([
    ['a bare number', '0'],
    ['a day that does not exist in the month', '2026-02-31T00:00:00.000Z'],
    ['a leap day in a common year', '2025-02-29T00:00:00Z'],
    ['no zone', '2026-10-13T08:00:14'],
    ['a date without a time', '2026-10-13'],
    ['hour 25', '2026-10-13T25:00:00Z'],
    ['month 13', '2026-13-01T00:00:00Z'],
    ['minute 60', '2026-10-13T08:60:00Z'],
    ['a zone offset without minutes', '2026-10-13T08:00:14+02'],
    ['an empty string', ''],
    ['a surrounding blank', ' 2026-10-13T08:00:14Z'],
    ['a zone beyond +14:00', '2026-10-13T08:00:14+15:00'],
    ['a zone of 14 hours and some minutes', '2026-10-13T08:00:14+14:30'],
    ['a zone of minute 60', '2026-10-13T08:00:14+05:60'],
    ['seventeen fraction digits', '2026-10-13T08:00:14.12345678901234567Z'],
    ['an instant before 0001-01-01 in UTC', '0001-01-01T00:59:59.9999999+01:00'],
    ['an instant after 9999-12-31 in UTC', '9999-12-31T23:00:00-01:00'],
  ])('rejects %s', (_label, value) => {
    expect(parseStrictEntryDate(value)).toBeNull();
  });

  it('rejects what is not a string', () => {
    expect(parseStrictEntryDate(null)).toBeNull();
    expect(parseStrictEntryDate(undefined)).toBeNull();
    expect(parseStrictEntryDate(1_760_000_000_000)).toBeNull();
  });

  it('hands the very same string back, never a reformatted one', () => {
    const wire = '2026-10-13T08:00:14.902123+02:00';

    expect(parseStrictEntryDate(wire)).toBe(wire);
    expect(parseStrictEntryDate(wire)).not.toBe(new Date(wire).toISOString());
  });
});

describe('isValidEntryGeneration', () => {
  const valid = { slotId: 'slot-1', alias: 'hob44', addedAt: '2026-10-13T08:00:14.902Z' };

  it('accepts a target with a slot and one without', () => {
    expect(isValidEntryGeneration(valid)).toBe(true);
    expect(isValidEntryGeneration({ alias: 'hob44', addedAt: valid.addedAt })).toBe(true);
  });

  it.each([
    ['an add date the report endpoint would refuse', { ...valid, addedAt: '2026-02-31T00:00:00Z' }],
    ['an empty slot id', { ...valid, slotId: '' }],
    ['an empty alias', { ...valid, alias: '' }],
  ])('rejects %s', (_label, entry) => {
    expect(isValidEntryGeneration(entry)).toBe(false);
  });
});
