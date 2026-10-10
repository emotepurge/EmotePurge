import { describe, expect, it } from 'vitest';

import {
  fillDailySeries,
  fillOffsetSeries,
  liveBands,
  liveDayCaptionKey,
  liveDayCoverage,
  liveDaysFrom,
  offsetsToDates,
  seriesPeak,
  toPolylinePoints,
} from './usage-series';

describe('fillDailySeries', () => {
  it('zero-fills the gaps of a sparse series', () => {
    const points = fillDailySeries(
      [
        { date: '2026-07-01', useCount: 3 },
        { date: '2026-07-03', useCount: 5 },
      ],
      '2026-07-01',
      '2026-07-04',
    );
    expect(points).toEqual([
      { date: '2026-07-01', useCount: 3 },
      { date: '2026-07-02', useCount: 0 },
      { date: '2026-07-03', useCount: 5 },
      { date: '2026-07-04', useCount: 0 },
    ]);
  });

  it('handles a single-day range', () => {
    expect(
      fillDailySeries([{ date: '2026-07-01', useCount: 2 }], '2026-07-01', '2026-07-01'),
    ).toEqual([{ date: '2026-07-01', useCount: 2 }]);
  });

  it('ignores days outside the range', () => {
    const points = fillDailySeries(
      [
        { date: '2026-06-30', useCount: 99 },
        { date: '2026-07-02', useCount: 1 },
      ],
      '2026-07-01',
      '2026-07-02',
    );
    expect(points).toEqual([
      { date: '2026-07-01', useCount: 0 },
      { date: '2026-07-02', useCount: 1 },
    ]);
  });

  it('returns [] for an inverted or invalid range', () => {
    expect(fillDailySeries([], '2026-07-05', '2026-07-01')).toEqual([]);
    expect(fillDailySeries([], 'nonsense', '2026-07-01')).toEqual([]);
  });
});

describe('fillOffsetSeries', () => {
  it('places each pair at its offset and zero-fills the rest', () => {
    const points = fillOffsetSeries(
      [
        [0, 3],
        [2, 5],
      ],
      '2026-07-01',
      '2026-07-04',
    );
    // Identical output to fillDailySeries on the same data — the two encodings are the same series.
    expect(points).toEqual([
      { date: '2026-07-01', useCount: 3 },
      { date: '2026-07-02', useCount: 0 },
      { date: '2026-07-03', useCount: 5 },
      { date: '2026-07-04', useCount: 0 },
    ]);
  });

  it('drops offsets outside the range rather than growing the array', () => {
    const points = fillOffsetSeries(
      [
        [-1, 99],
        [1, 4],
        [7, 99],
      ],
      '2026-07-01',
      '2026-07-02',
    );
    expect(points).toEqual([
      { date: '2026-07-01', useCount: 0 },
      { date: '2026-07-02', useCount: 4 },
    ]);
  });

  it('returns [] for an inverted or invalid range', () => {
    expect(fillOffsetSeries([[0, 1]], '2026-07-05', '2026-07-01')).toEqual([]);
    expect(fillOffsetSeries([[0, 1]], 'nonsense', '2026-07-01')).toEqual([]);
  });
});

describe('offsetsToDates', () => {
  it('counts days from the range start, across a month boundary', () => {
    expect(offsetsToDates([0, 1, 31], '2026-07-31')).toEqual([
      '2026-07-31',
      '2026-08-01',
      '2026-08-31',
    ]);
  });

  it('returns [] for an invalid start date', () => {
    expect(offsetsToDates([0], 'nonsense')).toEqual([]);
  });
});

describe('toPolylinePoints', () => {
  it('spreads the points over the width and inverts the y axis', () => {
    const points = toPolylinePoints(
      [
        { date: '2026-07-01', useCount: 0 },
        { date: '2026-07-02', useCount: 10 },
        { date: '2026-07-03', useCount: 5 },
      ],
      100,
      40,
    );
    expect(points).toBe('0,40 50,0 100,20');
  });

  it('draws an all-zero series as the baseline without dividing by zero', () => {
    const points = toPolylinePoints(
      [
        { date: '2026-07-01', useCount: 0 },
        { date: '2026-07-02', useCount: 0 },
      ],
      100,
      40,
    );
    expect(points).toBe('0,40 100,40');
  });

  it('renders a single point as a full-width flat line', () => {
    expect(toPolylinePoints([{ date: '2026-07-01', useCount: 4 }], 100, 40)).toBe('0,0 100,0');
  });

  it('returns an empty string for no points', () => {
    expect(toPolylinePoints([], 100, 40)).toBe('');
  });

  it('leaves out the days before drawFrom without moving the rest', () => {
    const points = fillDailySeries(
      [
        { date: '2026-07-03', useCount: 10 },
        { date: '2026-07-04', useCount: 5 },
      ],
      '2026-07-01',
      '2026-07-05',
    );
    // 5 points over width 100 → stepX 25, so the 03. sits at x=50 with or without drawFrom. That is
    // the whole point: the curve must stay aligned with the live bands underneath it, which keep
    // spanning the full range.
    expect(toPolylinePoints(points, 100, 40, '2026-07-03')).toBe('50,0 75,20 100,40');
  });

  it('changes nothing when drawFrom lies before the range', () => {
    const points = fillDailySeries(
      [{ date: '2026-07-02', useCount: 8 }],
      '2026-07-01',
      '2026-07-03',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-06-01')).toBe(toPolylinePoints(points, 100, 40));
  });

  it('draws nothing when no day before the range end qualifies', () => {
    // Nothing drawn only because no day is left: every day is before drawFrom *and* unused. A day
    // with a count would survive — see the re-add test below.
    const points = fillDailySeries([], '2026-07-01', '2026-07-03');
    expect(toPolylinePoints(points, 100, 40, '2026-08-01')).toBe('');
  });

  it('draws a day the emote was used on even when drawFrom claims it did not exist yet', () => {
    // The re-added emote: 7TV moves `addedAt` forward to the latest set entry, while the very same
    // Emote row keeps its usage history (SevenTvSyncService.UpsertEmote un-archives in place). If
    // drawFrom won here, a curve of measured 90 uses would collapse into a flat stub — a false
    // "unused" on the page whose only job is deciding whether an emote may be deleted.
    const points = fillDailySeries(
      [{ date: '2026-07-01', useCount: 90 }],
      '2026-07-01',
      '2026-07-05',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-07-05')).toBe('0,0 25,40 50,40 75,40 100,40');
  });

  it('treats a full ISO timestamp for drawFrom like the bare day', () => {
    // Callers pass `firstSeenAt?.slice(0, 10)` today, but a timestamp slipping through must not
    // silently drop the emote's own first day: '2026-07-05' >= '2026-07-05T10:00:00Z' is false.
    const points = fillDailySeries([], '2026-07-01', '2026-07-05');
    expect(toPolylinePoints(points, 100, 40, '2026-07-05T10:00:00Z')).toBe(
      toPolylinePoints(points, 100, 40, '2026-07-05'),
    );
    expect(toPolylinePoints(points, 100, 40, '2026-07-05T10:00:00Z')).toBe('87.5,40 100,40');
  });

  it('scales y over the drawn days, and the drawn peak reaches the top edge', () => {
    // The scale rule the spec fixes: `max` comes from the drawn points, not the whole array, so the
    // y-axis label (seriesPeak, same days) and the curve height cannot disagree. Since no day with a
    // count is ever trimmed, the busiest day is always drawn and always lands on y = 0.
    const points = fillDailySeries(
      [
        { date: '2026-07-01', useCount: 90 },
        { date: '2026-07-04', useCount: 45 },
      ],
      '2026-07-01',
      '2026-07-05',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-07-04')).toBe('0,0 25,40 50,40 75,20 100,40');
    expect(seriesPeak(points, '2026-07-04')).toEqual({ useCount: 90, date: '2026-07-01' });
  });

  it('draws a single visible day as the trailing half-step, not the full width', () => {
    // The emote added today: one drawable day. `visible` runs to the end of the array, so a single
    // visible day is always the last one — the stub is the half-step [width - stepX/2, width], the
    // same span liveBands gives that day. The old single-point branch paints the full width, which
    // would claim the whole range again — the exact statement this change removes.
    const points = fillDailySeries(
      [{ date: '2026-07-05', useCount: 3 }],
      '2026-07-01',
      '2026-07-05',
    );
    expect(toPolylinePoints(points, 100, 40, '2026-07-05')).toBe('87.5,0 100,0');
  });

  it('keeps that trailing half-step on the baseline when the day is unused', () => {
    const points = fillDailySeries([], '2026-07-01', '2026-07-05');
    expect(toPolylinePoints(points, 100, 40, '2026-07-05')).toBe('87.5,40 100,40');
  });
});

describe('liveBands', () => {
  const week = fillDailySeries([], '2026-07-01', '2026-07-07');

  it('merges consecutive live days into one band spanning their half-steps', () => {
    // 7 points over width 100 → stepX 100/6. Days at index 1..2 → from 0.5 to 2.5 steps.
    const bands = liveBands(week, ['2026-07-02', '2026-07-03'], 100);
    expect(bands).toEqual([{ x: 8.33, width: 33.33 }]);
  });

  it('keeps separate runs as separate bands and clamps to the viewBox edges', () => {
    const bands = liveBands(week, ['2026-07-01', '2026-07-06', '2026-07-07'], 100);
    expect(bands).toEqual([
      { x: 0, width: 8.33 },
      { x: 75, width: 25 },
    ]);
  });

  it('ignores live days outside the rendered range', () => {
    expect(liveBands(week, ['2026-06-30', '2026-08-01'], 100)).toEqual([]);
  });

  it('marks a single-point range as one full-width band', () => {
    const day = fillDailySeries([], '2026-07-01', '2026-07-01');
    expect(liveBands(day, ['2026-07-01'], 100)).toEqual([{ x: 0, width: 100 }]);
    expect(liveBands(day, ['2026-07-02'], 100)).toEqual([]);
  });

  it('returns no bands without points or without live days', () => {
    expect(liveBands([], ['2026-07-01'], 100)).toEqual([]);
    expect(liveBands(week, [], 100)).toEqual([]);
  });
});

describe('seriesPeak', () => {
  it('finds the busiest day', () => {
    const peak = seriesPeak([
      { date: '2026-07-01', useCount: 3 },
      { date: '2026-07-02', useCount: 9 },
      { date: '2026-07-03', useCount: 4 },
    ]);
    expect(peak).toEqual({ useCount: 9, date: '2026-07-02' });
  });

  it('lets the earliest day win a tie', () => {
    const peak = seriesPeak([
      { date: '2026-07-01', useCount: 9 },
      { date: '2026-07-02', useCount: 9 },
    ]);
    expect(peak?.date).toBe('2026-07-01');
  });

  it('returns null for an empty or all-zero series', () => {
    expect(seriesPeak([])).toBeNull();
    expect(seriesPeak([{ date: '2026-07-01', useCount: 0 }])).toBeNull();
  });

  it('still names a day the emote was used on, even before drawFrom', () => {
    // The axis label reads exactly the days the curve draws, and the curve never drops a day with a
    // count (re-added emote: 7TV's addedAt moves forward past the surviving usage history). Naming
    // the 02. here would put "4x" on an axis whose line peaks at 90.
    const points = [
      { date: '2026-07-01', useCount: 90 },
      { date: '2026-07-02', useCount: 4 },
    ];
    expect(seriesPeak(points, '2026-07-02')).toEqual({ useCount: 90, date: '2026-07-01' });
  });

  it('skips the silent leading days, which are all unused anyway', () => {
    const points = [
      { date: '2026-07-01', useCount: 0 },
      { date: '2026-07-02', useCount: 4 },
    ];
    expect(seriesPeak(points, '2026-07-02')).toEqual({ useCount: 4, date: '2026-07-02' });
  });
});

describe('liveDayCoverage', () => {
  const week = fillDailySeries(
    [
      { date: '2026-07-02', useCount: 4 },
      { date: '2026-07-05', useCount: 1 },
    ],
    '2026-07-01',
    '2026-07-07',
  );
  // The same week without any usage before the 05., so drawFrom alone decides where counting starts.
  const quietWeek = fillDailySeries(
    [{ date: '2026-07-05', useCount: 1 }],
    '2026-07-01',
    '2026-07-07',
  );
  const emptyWeek = fillDailySeries([], '2026-07-01', '2026-07-07');
  const live = ['2026-07-01', '2026-07-02', '2026-07-05', '2026-07-06'];

  it('counts the live days the emote went unused on', () => {
    expect(liveDayCoverage(week, live)).toEqual({ live: 4, unused: 2 });
  });

  it('leaves out live days before the emote entered the set', () => {
    // The 01. and the 02. drop out of both numbers, not just the numerator: they are not days the
    // emote could have been used on, so they belong in neither.
    expect(liveDayCoverage(quietWeek, live, '2026-07-05')).toEqual({ live: 2, unused: 1 });
  });

  it('counts a live day the emote was used on, whatever drawFrom claims', () => {
    // The 02. carries 4 uses and is therefore a day the emote demonstrably existed on — a re-added
    // emote's addedAt sits after its own history. Dropping it would shrink a denominator that the
    // measurement itself proves belongs there, and turn "used" into "could not have been used".
    expect(liveDayCoverage(week, live, '2026-07-05')).toEqual({ live: 3, unused: 1 });
  });

  it('reports no live days when the emote arrived after the last of them', () => {
    expect(liveDayCoverage(emptyWeek, live, '2026-07-07')).toEqual({ live: 0, unused: 0 });
  });

  it('treats a full ISO timestamp for drawFrom like the bare day', () => {
    // Without the day slice, the emote's own first live day would fall out of the denominator:
    // '2026-07-05' >= '2026-07-05T10:00:00Z' is false.
    expect(liveDayCoverage(emptyWeek, live, '2026-07-05T10:00:00Z')).toEqual({
      live: 2,
      unused: 2,
    });
  });

  it('ignores live days outside the rendered range', () => {
    expect(liveDayCoverage(week, ['2026-06-30', '2026-08-01'])).toEqual({ live: 0, unused: 0 });
  });

  it('reports nothing without live days', () => {
    expect(liveDayCoverage(week, [])).toEqual({ live: 0, unused: 0 });
  });
});

describe('liveDayCaptionKey', () => {
  it('names the live days the emote went unused on', () => {
    expect(liveDayCaptionKey({ live: 12, unused: 9 }, true)).toBe(
      'usageStats.chart.unusedOnLiveDays.other',
    );
  });

  it('drops the "1 of 1" wording for a single live day', () => {
    expect(liveDayCaptionKey({ live: 1, unused: 1 }, true)).toBe(
      'usageStats.chart.unusedOnLiveDays.one',
    );
  });

  it('states the positive case rather than "0 unused"', () => {
    expect(liveDayCaptionKey({ live: 12, unused: 0 }, true)).toBe(
      'usageStats.chart.usedOnAllLiveDays.other',
    );
    expect(liveDayCaptionKey({ live: 1, unused: 0 }, true)).toBe(
      'usageStats.chart.usedOnAllLiveDays.one',
    );
  });

  it('falls back to naming the bands when none of them fall inside the emote lifetime', () => {
    // The bands span the whole width regardless of when the emote arrived, so without this form the
    // green would stand on screen unexplained — in the very case where it is least obvious.
    expect(liveDayCaptionKey({ live: 0, unused: 0 }, true)).toBe('usageStats.chart.liveLegend');
  });

  it('stays silent when there are no live days at all', () => {
    expect(liveDayCaptionKey({ live: 0, unused: 0 }, false)).toBeNull();
  });
});

describe('liveDayCaptionKey with imported days in the range', () => {
  it('uses the "since" wording for the per-emote counts', () => {
    expect(liveDayCaptionKey({ live: 1, unused: 1 }, true, true)).toBe(
      'usageStats.chart.since.unusedOnLiveDays.one',
    );
    expect(liveDayCaptionKey({ live: 3, unused: 1 }, true, true)).toBe(
      'usageStats.chart.since.unusedOnLiveDays.other',
    );
    expect(liveDayCaptionKey({ live: 3, unused: 0 }, true, true)).toBe(
      'usageStats.chart.since.usedOnAllLiveDays.other',
    );
  });

  it('names the date on the bands legend too', () => {
    expect(liveDayCaptionKey({ live: 0, unused: 0 }, true, true)).toBe(
      'usageStats.chart.liveLegendSince',
    );
  });

  it('says there is no live information when clipping left no live day', () => {
    expect(liveDayCaptionKey({ live: 0, unused: 0 }, false, true)).toBe(
      'usageStats.chart.noLiveInfoImported',
    );
  });

  it('is unchanged without clipping', () => {
    expect(liveDayCaptionKey({ live: 3, unused: 1 }, true, false)).toBe(
      'usageStats.chart.unusedOnLiveDays.other',
    );
    expect(liveDayCaptionKey({ live: 0, unused: 0 }, false, false)).toBeNull();
  });
});

describe('liveDaysFrom', () => {
  const live = ['2026-05-02', '2026-10-08', '2026-10-09'];

  it('returns the list untouched without a boundary', () => {
    expect(liveDaysFrom(live, null)).toBe(live);
  });

  it('drops the days before the boundary and keeps the boundary day', () => {
    expect(liveDaysFrom(live, '2026-10-08')).toEqual(['2026-10-08', '2026-10-09']);
  });
});
