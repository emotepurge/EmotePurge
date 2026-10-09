import { describe, expect, it } from 'vitest';

import {
  coverageStartFor,
  ImportCoverage,
  importCaptionFor,
  importedToInclusive,
} from './import-coverage.model';

function coverage(overrides: Partial<ImportCoverage> = {}): ImportCoverage {
  return {
    emoteSetId: 'SET',
    sources: [{ name: 'logs.cyex.app', url: 'https://logs.cyex.app/' }],
    importedFrom: '2026-04-08',
    importedTo: '2026-10-08',
    hasGaps: false,
    contiguousFrom: '2026-04-08',
    intervals: [],
    ...overrides,
  };
}

const EMPTY = coverage({
  emoteSetId: 'SET',
  sources: [],
  importedFrom: null,
  importedTo: null,
  contiguousFrom: null,
});

describe('importCaptionFor', () => {
  it('keeps the plain sentence when nothing is imported or the read is missing', () => {
    expect(importCaptionFor(null)).toBeNull();
    expect(importCaptionFor(EMPTY)).toBeNull();
  });

  it('closes a gap-free import with a plain full stop', () => {
    expect(importCaptionFor(coverage())).toEqual({
      leadKey: 'usageStats.trackedSinceWithImport',
      tailKey: 'usageStats.trackedSinceWithImportEnd',
    });
  });

  it('switches to the gaps variant whenever the covered days are not consecutive', () => {
    expect(importCaptionFor(coverage({ hasGaps: true }))?.tailKey).toBe(
      'usageStats.trackedSinceWithImportGaps',
    );
  });

  it('discloses coverage that does not reach the counting start', () => {
    const partial = coverage({ contiguousFrom: null, importedTo: '2026-07-01' });
    expect(importCaptionFor(partial)).not.toBeNull();
  });
});

describe('importedToInclusive', () => {
  it('names the day before the exclusive end', () => {
    expect(importedToInclusive(coverage({ importedTo: '2026-10-08' }))).toBe('2026-10-07');
  });

  it('crosses month and year boundaries', () => {
    expect(importedToInclusive(coverage({ importedTo: '2026-03-01' }))).toBe('2026-02-28');
    expect(importedToInclusive(coverage({ importedTo: '2027-01-01' }))).toBe('2026-12-31');
  });

  it('is null without an import', () => {
    expect(importedToInclusive(EMPTY)).toBeNull();
  });
});

describe('coverageStartFor', () => {
  it('moves the start to the contiguous stretch that reaches the counting start', () => {
    expect(coverageStartFor(coverage(), '2026-10-08')).toBe('2026-04-08');
  });

  it('keeps the older-gap suffix as the start (spec example: Apr 1-10 and Jun 1-Oct 7)', () => {
    const gapped = coverage({
      importedFrom: '2026-04-01',
      hasGaps: true,
      contiguousFrom: '2026-06-01',
    });
    expect(coverageStartFor(gapped, '2026-10-08')).toBe('2026-06-01');
  });

  it('leaves the live start when no covered day touches the counting start', () => {
    const detached = coverage({ contiguousFrom: null, importedTo: '2026-07-01' });
    expect(coverageStartFor(detached, '2026-10-08')).toBe('2026-10-08');
  });

  it('leaves the live start after a rejoin gap (tracking resumed after the imported end)', () => {
    expect(coverageStartFor(coverage(), '2026-10-20')).toBe('2026-10-20');
  });

  it('leaves the live start without a readable coverage', () => {
    expect(coverageStartFor(null, '2026-10-08')).toBe('2026-10-08');
    expect(coverageStartFor(EMPTY, '2026-10-08')).toBe('2026-10-08');
  });

  it('has no start at all while the tracking start is unknown', () => {
    expect(coverageStartFor(coverage(), null)).toBeNull();
  });
});
