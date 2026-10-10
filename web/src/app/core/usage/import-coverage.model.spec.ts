import { describe, expect, it } from 'vitest';

import {
  allTimeEarliestFor,
  coverageStartFor,
  hasImportedDaysBefore,
  ImportCoverage,
  importCaptionFor,
  importedToInclusive,
  liveDaysCaptionBaseKey,
  LIVE_POLL_START,
  liveKnownFromFor,
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

describe('allTimeEarliestFor', () => {
  it('starts at the first imported day when it lies before the tracking start', () => {
    expect(allTimeEarliestFor(coverage(), '2026-10-08')).toBe('2026-04-08');
  });

  it('keeps the tracking start without imports, without a coverage, or for a later import', () => {
    expect(allTimeEarliestFor(EMPTY, '2026-10-08')).toBe('2026-10-08');
    expect(allTimeEarliestFor(null, '2026-10-08')).toBe('2026-10-08');
    expect(allTimeEarliestFor(coverage({ importedFrom: '2026-10-09' }), '2026-10-08')).toBe(
      '2026-10-08',
    );
  });

  it('has no start while the tracking start is unknown', () => {
    expect(allTimeEarliestFor(coverage(), null)).toBeNull();
  });
});

describe('hasImportedDaysBefore', () => {
  it('is true only when an imported day precedes the given start', () => {
    expect(hasImportedDaysBefore(coverage({ importedFrom: '2026-07-09' }), '2026-10-08')).toBe(
      true,
    );
    expect(hasImportedDaysBefore(coverage({ importedFrom: '2026-04-08' }), '2026-04-08')).toBe(
      false,
    );
    expect(hasImportedDaysBefore(EMPTY, '2026-10-08')).toBe(false);
    expect(hasImportedDaysBefore(null, '2026-10-08')).toBe(false);
    expect(hasImportedDaysBefore(coverage(), null)).toBe(false);
  });
});

describe('liveKnownFromFor', () => {
  // Imports 2026-04-08 .. 2026-10-07 (importedTo exclusive), counting since 2026-10-08.
  const SINCE = '2026-10-08';

  it('does not clip without a coverage, without imported days, or when the read failed', () => {
    expect(liveKnownFromFor(null, SINCE, '2026-04-01', '2026-10-09')).toBeNull();
    expect(liveKnownFromFor(EMPTY, SINCE, '2026-04-01', '2026-10-09')).toBeNull();
  });

  it('clips to the tracking start when the range contains imported days', () => {
    expect(liveKnownFromFor(coverage(), SINCE, '2026-04-08', '2026-10-09')).toBe(SINCE);
    expect(liveKnownFromFor(coverage(), SINCE, '2026-09-01', '2026-10-09')).toBe(SINCE);
  });

  it('does not clip a range that starts at or after the tracking start', () => {
    // importedTo is exclusive: the last imported day is 2026-10-07, the range starts the next day.
    expect(liveKnownFromFor(coverage(), SINCE, '2026-10-08', '2026-10-20')).toBeNull();
    expect(liveKnownFromFor(coverage(), SINCE, '2026-10-12', '2026-10-20')).toBeNull();
  });

  it('does not clip a range that ends before the first imported day', () => {
    expect(liveKnownFromFor(coverage(), SINCE, '2026-03-01', '2026-04-07')).toBeNull();
  });

  it('clips a range lying entirely inside the imports to a boundary after it', () => {
    expect(liveKnownFromFor(coverage(), SINCE, '2026-05-01', '2026-05-31')).toBe(SINCE);
  });

  it('uses the first day after the imports when the tracking start is unknown', () => {
    expect(liveKnownFromFor(coverage(), null, '2026-04-08', '2026-10-09')).toBe('2026-10-08');
  });

  it('lets the intervals decide when the imports have gaps', () => {
    const gappy = coverage({
      hasGaps: true,
      intervals: [
        { from: '2026-04-08', to: '2026-05-01', archiveHost: 'logs.cyex.app' },
        { from: '2026-09-01', to: '2026-10-08', archiveHost: 'logs.cyex.app' },
      ],
    });
    // Wholly inside the gap: nothing imported is shown, nothing is clipped.
    expect(liveKnownFromFor(gappy, SINCE, '2026-06-01', '2026-08-31')).toBeNull();
    expect(liveKnownFromFor(gappy, SINCE, '2026-04-20', '2026-06-01')).toBe(SINCE);
    expect(liveKnownFromFor(gappy, SINCE, '2026-08-01', '2026-09-01')).toBe(SINCE);
  });

  it('raises the boundary to the live poll start for a channel tracked since before it', () => {
    expect(LIVE_POLL_START).toBe('2026-08-03');
    expect(liveKnownFromFor(coverage(), '2026-07-20', '2026-04-08', '2026-10-09')).toBe(
      LIVE_POLL_START,
    );
    expect(
      liveKnownFromFor(coverage({ importedTo: '2026-07-20' }), null, '2026-04-08', '2026-07-20'),
    ).toBe(LIVE_POLL_START);
    // A range already starting at or after the poll start has nothing to clip.
    expect(liveKnownFromFor(coverage(), '2026-07-20', '2026-08-03', '2026-10-09')).toBeNull();
  });
});

describe('liveDaysCaptionBaseKey', () => {
  it('stays silent without a live day', () => {
    expect(liveDaysCaptionBaseKey(0, '2026-10-08', '2026-10-08')).toBeNull();
    expect(liveDaysCaptionBaseKey(0, null, null)).toBeNull();
  });

  it('keeps the plain sentence when nothing is clipped', () => {
    expect(liveDaysCaptionBaseKey(3, null, '2026-10-08')).toBe('usageStats.liveDaysInRange');
  });

  it('drops the date when it is the one the first sentence already names', () => {
    expect(liveDaysCaptionBaseKey(3, '2026-10-08', '2026-10-08')).toBe(
      'usageStats.liveDaysInRangeFromStart',
    );
  });

  it('names the date when it differs, e.g. clamped to the poll start or without a tracking start', () => {
    expect(liveDaysCaptionBaseKey(3, '2026-08-03', '2026-07-20')).toBe(
      'usageStats.liveDaysInRangeSince',
    );
    expect(liveDaysCaptionBaseKey(3, '2026-10-08', null)).toBe('usageStats.liveDaysInRangeSince');
  });
});
