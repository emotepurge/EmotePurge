import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ImportCoverage } from '../../core/usage/import-coverage.model';
import { ALL_EMOTE_SETS, EmoteUsageSeries } from '../../core/usage-stats/usage-stat.model';
import { UsageStatService } from '../../core/usage-stats/usage-stat.service';
import { VoteType } from '../../core/voting/vote-session.model';
import { EmoteDrilldownData, EmoteDrilldownDialog } from './emote-drilldown-dialog';

const IMAGE_URL = 'https://cdn.7tv.app/emote/aaa/4x_static.webp';

function data(overrides: Partial<EmoteDrilldownData> = {}): EmoteDrilldownData {
  return {
    channelName: 'sensitron',
    from: '2026-01-15',
    to: '2026-01-21',
    emoteId: 'emote-1',
    sevenTvEmoteId: '01FFWH9WV80000JT8GHDKHJNZC',
    emoteName: 'Kappa',
    imageUrl: IMAGE_URL,
    ...overrides,
  };
}

function series(overrides: Partial<EmoteUsageSeries> = {}): EmoteUsageSeries {
  return {
    emoteId: 'emote-1',
    emoteName: 'Kappa',
    from: '2026-01-15',
    to: '2026-01-21',
    totalUseCount: 20,
    firstUsedDate: null,
    lastUsedDate: null,
    days: [],
    liveDays: [],
    ...overrides,
  };
}

describe('EmoteDrilldownDialog', () => {
  let fixture: ComponentFixture<EmoteDrilldownDialog>;
  let component: EmoteDrilldownDialog;
  let dialogData: EmoteDrilldownData;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        EmoteDrilldownDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        // Resolved when the component is constructed, so a test may shape the data first —
        // same pattern as delete-confirm-dialog.spec.ts.
        { provide: DIALOG_DATA, useFactory: () => dialogData },
        {
          provide: DialogRef,
          useValue: { close: () => undefined } satisfies Pick<DialogRef<void>, 'close'>,
        },
      ],
    }).compileComponents();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  /** Stubs `UsageStatService.getDailySeries()` directly — what matters for these decisions is the
   *  series answer (or failure) the constructor's subscription receives, not the cache key or the
   *  wire request, both already covered by usage-stat.service.spec.ts. */
  function render(
    dialogInput: EmoteDrilldownData,
    seriesResult: Observable<EmoteUsageSeries>,
  ): void {
    dialogData = dialogInput;
    vi.spyOn(TestBed.inject(UsageStatService), 'getDailySeries').mockReturnValue(seriesResult);
    fixture = TestBed.createComponent(EmoteDrilldownDialog);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  describe('trend guard', () => {
    it('is unknown before the series has loaded, even with both trend inputs present', () => {
      // `of()` with no arguments completes without ever emitting — `series()` stays null, which is
      // the guard's own first condition (`!series`), independent of trackedSince/previousWindowUseCount.
      render(data({ trackedSince: '2025-01-01T00:00:00Z', previousWindowUseCount: 5 }), of());

      expect(component['series']()).toBeNull();
      expect(component['trend']()).toBe('unknown');
    });

    it('is unknown once loaded when trackedSince is missing', () => {
      render(
        data({ trackedSince: null, previousWindowUseCount: 5 }),
        of(series({ totalUseCount: 20 })),
      );

      expect(component['series']()).not.toBeNull();
      expect(component['trend']()).toBe('unknown');
    });

    it('is unknown once loaded when previousWindowUseCount is missing', () => {
      render(
        data({ trackedSince: '2025-01-01T00:00:00Z', previousWindowUseCount: undefined }),
        of(series({ totalUseCount: 20 })),
      );

      expect(component['trend']()).toBe('unknown');
    });

    it('delegates to the real trend calculation once both inputs and the series are present', () => {
      // trackedSince far enough back and no firstSeenAt to clear usageTrend's own guards — this
      // only pins that the guard here forwards to usageTrend with real inputs, not the branches
      // of usageTrend itself (covered by emote-context.spec.ts).
      render(
        data({
          from: '2026-01-15',
          to: '2026-01-21',
          trackedSince: '2025-01-01T00:00:00Z',
          previousWindowUseCount: 5,
          firstSeenAt: null,
        }),
        of(series({ from: '2026-01-15', to: '2026-01-21', totalUseCount: 20 })),
      );

      // (20 - 5) / 5 = 3.0, well past usageTrend's rising threshold — proof the guard actually
      // delegated instead of returning 'unknown' by default.
      expect(component['trend']()).toBe('rising');
    });
  });

  describe('live days with imported days in the range (#366)', () => {
    // The dialog decides against its own range; the imports end 2026-08-10 (exclusive).
    const live = ['2026-08-05', '2026-08-12', '2026-08-13'];
    const imported: ImportCoverage = {
      emoteSetId: 'SET',
      sources: [],
      importedFrom: '2026-06-01',
      importedTo: '2026-08-10',
      hasGaps: false,
      contiguousFrom: '2026-06-01',
      intervals: [],
    };
    const range = { from: '2026-07-01', to: '2026-08-20' };
    const wide = series({ ...range, liveDays: live });

    it('counts every live day and keeps the plain wording without coverage', () => {
      render(data(range), of(wide));

      expect(component['liveDays']()).toEqual(live);
      expect(component['liveKey']()).toBe('usageStats.chart.unusedOnLiveDays.other');
    });

    it('counts only the live days since the boundary and chooses the dated wording', () => {
      render(
        data({ ...range, importCoverage: imported, trackedSinceDate: '2026-08-10' }),
        of(wide),
      );

      expect(component['liveDays']()).toEqual(['2026-08-12', '2026-08-13']);
      expect(component['liveKey']()).toBe('usageStats.chart.since.unusedOnLiveDays.other');
    });

    it('falls back to the first day after the imports without a tracking start', () => {
      render(data({ ...range, importCoverage: imported }), of(wide));

      expect(component['liveDays']()).toEqual(['2026-08-12', '2026-08-13']);
    });

    it('does not clip a range without imported days', () => {
      render(data({ from: '2026-08-11', to: '2026-08-20', importCoverage: imported }), of(wide));

      expect(component['liveDays']()).toEqual(live);
    });

    it('says there is no live information when clipping leaves no live day', () => {
      render(
        data({ ...range, importCoverage: imported, trackedSinceDate: '2026-08-10' }),
        of(series({ ...range, liveDays: ['2026-08-05'] })),
      );

      expect(component['liveKey']()).toBe('usageStats.chart.noLiveInfoImported');
    });
  });

  describe('myVoteKey switch', () => {
    it('reports keep', () => {
      render(
        data({
          vote: { keepVotes: 3, deleteVotes: 1, score: 2, myVote: VoteType.Keep },
        }),
        of(series()),
      );

      expect(component['myVoteKey']()).toBe('usageStats.drilldown.myVoteKeep');
    });

    it('reports delete', () => {
      render(
        data({
          vote: { keepVotes: 3, deleteVotes: 1, score: 2, myVote: VoteType.Delete },
        }),
        of(series()),
      );

      expect(component['myVoteKey']()).toBe('usageStats.drilldown.myVoteDelete');
    });

    it('reports none when the viewer has not voted', () => {
      render(
        data({
          vote: { keepVotes: 3, deleteVotes: 1, score: 2, myVote: null },
        }),
        of(series()),
      );

      expect(component['myVoteKey']()).toBe('usageStats.drilldown.myVoteNone');
    });

    it("still reports the viewer's own vote on a secret ballot — only the tallies are withheld, not myVote", () => {
      render(
        data({
          vote: { keepVotes: null, deleteVotes: null, score: null, myVote: VoteType.Keep },
        }),
        of(series()),
      );

      expect(component['myVoteKey']()).toBe('usageStats.drilldown.myVoteKeep');
    });
  });

  describe('7TV page link', () => {
    function link(): HTMLAnchorElement {
      return fixture.nativeElement.querySelector('a[target="_blank"]') as HTMLAnchorElement;
    }

    it('points at the emote page on 7TV and opens in a new tab without leaking the opener', () => {
      render(data(), of(series()));

      expect(link().getAttribute('href')).toBe('https://7tv.app/emotes/01FFWH9WV80000JT8GHDKHJNZC');
      expect(link().rel.split(' ')).toEqual(expect.arrayContaining(['noopener', 'noreferrer']));
    });

    it('marks the close button, not the link, as the initial focus target', () => {
      render(data(), of(series()));

      const initial = fixture.nativeElement.querySelector('[cdkFocusInitial]') as HTMLElement;
      expect(initial.tagName).toBe('BUTTON');
      expect(link().hasAttribute('cdkFocusInitial')).toBe(false);
    });

    it('names the destination first and says it opens in a new tab, hiding the arrow glyph', () => {
      render(data(), of(series()));

      // The test transloco has no translations, so the keys stand in for the wording.
      expect(link().textContent?.trim()).toMatch(
        /^usageStats\.drilldown\.openOnSevenTv.*common\.opensInNewTab$/s,
      );
      expect(link().querySelector('[aria-hidden="true"]')?.textContent).toContain('↗');
    });
  });

  describe('constructor error branch', () => {
    it('sets no error and stores the series on success', () => {
      render(data(), of(series({ totalUseCount: 42 })));

      expect(component['errorKey']()).toBeNull();
      expect(component['series']()?.totalUseCount).toBe(42);
    });

    it('translates an HttpErrorResponse via the shared api-error mapping', () => {
      render(
        data(),
        throwError(
          () => new HttpErrorResponse({ status: 404, error: { errorCode: 'channel_not_found' } }),
        ),
      );

      expect(component['errorKey']()).toBe('errors.api.channel_not_found');
      expect(component['series']()).toBeNull();
    });

    it('falls back to the generic load-failed key for a non-HTTP error', () => {
      render(
        data(),
        throwError(() => new Error('boom')),
      );

      expect(component['errorKey']()).toBe('usageStats.errors.loadFailed');
      expect(component['series']()).toBeNull();
    });
  });
  describe('set scope (spec #200, 7.2, AK 64)', () => {
    it('asks for the series of the set frozen into its data', () => {
      render(data({ emoteSetId: 'set-halloween' }), of());

      expect(TestBed.inject(UsageStatService).getDailySeries).toHaveBeenCalledWith(
        'sensitron',
        'emote-1',
        '2026-01-15',
        '2026-01-21',
        'set-halloween',
      );
    });

    it("asks for the channel's active set when its data carries no set", () => {
      render(data(), of());

      expect(TestBed.inject(UsageStatService).getDailySeries).toHaveBeenCalledWith(
        'sensitron',
        'emote-1',
        '2026-01-15',
        '2026-01-21',
        null,
      );
    });

    it('asks for every set when its data says so (a null-session on the vote page)', () => {
      render(data({ emoteSetId: ALL_EMOTE_SETS }), of());

      expect(TestBed.inject(UsageStatService).getDailySeries).toHaveBeenCalledWith(
        'sensitron',
        'emote-1',
        '2026-01-15',
        '2026-01-21',
        ALL_EMOTE_SETS,
      );
    });
  });
});
