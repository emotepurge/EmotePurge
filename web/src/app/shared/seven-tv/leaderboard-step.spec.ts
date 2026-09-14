import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { By } from '@angular/platform-browser';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LanguageService } from '../../core/i18n/language.service';
import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { LeaderboardSort, SevenTvLeaderboardResponse } from '../../core/seven-tv/leaderboard.model';
import { ForeignEmoteGrid } from './foreign-emote-grid';
import { LeaderboardStep } from './leaderboard-step';

const DE_TRANSLATIONS = {
  import: {
    clearSelection: 'Auswahl aufheben',
    foreignChannel: {
      retry: 'Erneut versuchen',
      selectedCount: '{{ count }} ausgewählt',
      grid: { ariaLabel: 'Emote-Auswahl' },
      sort: { label: 'Sortieren nach', none: 'Set-Reihenfolge' },
    },
    leaderboard: {
      title: 'Aus 7TVs Bestenliste importieren',
      sortLabel: 'Liste',
      sort: { TRENDING_DAILY: 'Trend heute', TOP_ALL_TIME: 'Top insgesamt' },
      empty: '7TV liefert für diese Liste gerade keine Einträge.',
      truncated: {
        TRENDING_DAILY: 'Tagesliste: {{ loaded }} von {{ totalCount }}.',
        TOP_ALL_TIME: 'Rangfolge: {{ loaded }}, insgesamt {{ totalCount }}.',
      },
      scoreHint: 'Die Reihenfolge ist 7TVs eigene.',
    },
  },
  errors: {
    api: { foreign_channel_seventv_unavailable: '7TV ist gerade nicht erreichbar.' },
  },
};

class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  unobserve(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

function row(id: string, name: string): ForeignEmoteRow {
  return {
    sevenTvEmoteId: id,
    name,
    defaultName: name,
    imageUrl: `https://cdn.7tv.app/${id}/4x.webp`,
    topAllTime: 10,
    trending: 5,
  };
}

function response(overrides: Partial<SevenTvLeaderboardResponse> = {}): SevenTvLeaderboardResponse {
  return {
    sortBy: 'TRENDING_DAILY',
    totalCount: 705,
    truncated: false,
    emotes: [row('e1', 'catJAM')],
    ...overrides,
  };
}

describe('LeaderboardStep', () => {
  let fixture: ComponentFixture<LeaderboardStep>;
  let component: LeaderboardStep;
  let host: HTMLElement;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

    await TestBed.configureTestingModule({
      imports: [
        LeaderboardStep,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: LanguageService,
          useValue: { lang: signal('de') } as unknown as LanguageService,
        },
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeaderboardStep);
    component = fixture.componentInstance;
    host = fixture.nativeElement;
    fixture.detectChanges();
  });

  /** The one request the step has in flight, asserted to be for the given list. */
  function expectRequest(sortBy: LeaderboardSort) {
    return httpMock.expectOne(
      (candidate) =>
        candidate.url === '/api/seventv/leaderboard' && candidate.params.get('sortBy') === sortBy,
    );
  }

  function sortSelect(): HTMLSelectElement {
    const select = host.querySelector('select');
    if (!select) {
      throw new Error('no sort select rendered');
    }
    return select;
  }

  function chooseSort(value: LeaderboardSort): void {
    const select = sortSelect();
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function grid(): ForeignEmoteGrid {
    return fixture.debugElement.query(By.directive(ForeignEmoteGrid))
      .componentInstance as ForeignEmoteGrid;
  }

  it("asks for today's trending list on the way in, without being told to", () => {
    // Entering the step is the whole trigger — there is no "load" button, because there is nothing
    // to type in first (spec §7).
    expectRequest('TRENDING_DAILY').flush(response());
    fixture.detectChanges();

    expect(host.querySelector('app-foreign-emote-grid')).not.toBeNull();
    expect(component.showsGrid()).toBe(true);
  });

  it('loads the other list on a sort change and forces the grid into its ranking', () => {
    expectRequest('TRENDING_DAILY').flush(response());
    fixture.detectChanges();
    expect(grid().forcedSortMode()).toBe('trending');

    chooseSort('TOP_ALL_TIME');
    expectRequest('TOP_ALL_TIME').flush(response({ sortBy: 'TOP_ALL_TIME' }));
    fixture.detectChanges();

    // The list IS the ordering here, so the grid is displayed in it and its own sort control is
    // suppressed (spec E12/F2).
    expect(grid().forcedSortMode()).toBe('topAllTime');
  });

  it('empties the selection visibly when the list is switched (E14)', () => {
    expectRequest('TRENDING_DAILY').flush(response());
    fixture.detectChanges();
    component['onSelectionChange']([row('e1', 'catJAM')]);
    fixture.detectChanges();
    const firstGrid = grid();
    expect(component.result()).not.toBeNull();

    chooseSort('TOP_ALL_TIME');

    // While the new list is in flight there is no grid at all — which is what takes the old
    // `ListSelection` with it, so no key of the old list can survive into the new one (#132/#133).
    expect(host.querySelector('app-foreign-emote-grid')).toBeNull();
    expect(component.result()).toBeNull();
    expect(component.showsGrid()).toBe(false);

    expectRequest('TOP_ALL_TIME').flush(response({ sortBy: 'TOP_ALL_TIME' }));
    fixture.detectChanges();

    // A different grid instance, i.e. a different selection: nothing is marked on the new list,
    // and the grid says so where the user can see it rather than only in the step's state.
    expect(grid()).not.toBe(firstGrid);
    expect(grid()['selection'].selectedKeys()).toEqual([]);
    expect(host.textContent).toContain('0 ausgewählt');
    expect(component.result()).toBeNull();
  });

  it('locks "Continue" again once the grid\'s clear-selection button empties the pick (#166)', () => {
    expectRequest('TRENDING_DAILY').flush(response());
    fixture.detectChanges();

    component['onSelectionChange']([row('e1', 'catJAM')]);
    fixture.detectChanges();
    expect(component.result()).not.toBeNull();

    // Drives it through the grid's own method, the same path the button in its template calls —
    // this step only ever learns about the selection through selectionChange (see that method's
    // doc comment on ForeignEmoteGrid).
    grid()['clearSelection']();
    fixture.detectChanges();

    expect(component.result()).toBeNull();
  });

  it('reports the picked rows together with the list they were picked off', () => {
    expectRequest('TRENDING_DAILY').flush(response({ sortBy: 'TOP_ALL_TIME' }));
    fixture.detectChanges();
    expect(component.result()).toBeNull();

    const picked = [row('e1', 'catJAM')];
    component['onSelectionChange'](picked);

    // The sort is the provenance of a leaderboard pick, so it travels with the rows (E2/E8) — and
    // it is the list the server says it answered with, not the select's own value.
    expect(component.result()).toEqual({ sortBy: 'TOP_ALL_TIME', rows: picked });
  });

  it('surfaces a failure and retries the same list', () => {
    expectRequest('TRENDING_DAILY').flush(
      { errorCode: 'foreign_channel_seventv_unavailable' },
      { status: 503, statusText: 'Service Unavailable' },
    );
    fixture.detectChanges();

    expect(host.textContent).toContain('7TV ist gerade nicht erreichbar.');
    expect(component.showsGrid()).toBe(false);

    const retry = Array.from(host.querySelectorAll('button')).find(
      (candidate) => candidate.textContent?.trim() === 'Erneut versuchen',
    );
    retry?.click();

    expectRequest('TRENDING_DAILY').flush(response());
  });

  it('says an empty list is empty, in the leaderboard’s own words', () => {
    expectRequest('TRENDING_DAILY').flush(response({ emotes: [], totalCount: 0 }));
    fixture.detectChanges();

    // Not the channel step's "this channel's set has no emotes" — the caption inputs exist because
    // those texts read wrong for a network-wide list (spec E12).
    expect(grid().emptyMessageKey()).toBe('import.leaderboard.empty');
    expect(host.textContent).toContain('7TV liefert für diese Liste gerade keine Einträge.');
  });

  it('names truncation differently per list, both with what was loaded and what exists', () => {
    // 500 of ~700 is most of the day's list; 500 of 1.37 million is a sample off the top. One
    // sentence true of both would have to be vague enough not to be read (spec §4, AK 23).
    expectRequest('TRENDING_DAILY').flush(response({ truncated: true, totalCount: 705 }));
    fixture.detectChanges();
    expect(grid().truncatedMessageKey()).toBe('import.leaderboard.truncated.TRENDING_DAILY');
    expect(host.textContent).toContain('Tagesliste: 1 von 705.');

    chooseSort('TOP_ALL_TIME');
    expectRequest('TOP_ALL_TIME').flush(
      response({ sortBy: 'TOP_ALL_TIME', truncated: true, totalCount: 1371890 }),
    );
    fixture.detectChanges();

    expect(grid().truncatedMessageKey()).toBe('import.leaderboard.truncated.TOP_ALL_TIME');
    // Locale-grouped, not the raw 1371890 — 7TV's own totals run into the millions, and the
    // truncation notice's counts are formatted for the active locale now.
    expect(host.textContent).toContain('Rangfolge: 1, insgesamt 1.371.890.');
  });

  it('puts the caret on the list chooser while the first list is still loading', () => {
    // The real ordering, and the only one that occurs: the dialog calls focusFirstControl() from
    // `afterNextRender`, i.e. right after the render that mounted this step — always before any
    // response can have arrived. Focusing *after* the flush would test a sequence that never
    // happens and would pass over a control that cannot take focus at the moment it is asked to.
    const pending = expectRequest('TRENDING_DAILY');

    component.focusFirstControl();

    expect(document.activeElement).toBe(sortSelect());

    // And it stays where it was put once the list lands — the select survives the state change.
    pending.flush(response());
    fixture.detectChanges();
    expect(document.activeElement).toBe(sortSelect());
  });

  it('marks the list chooser unavailable while loading without taking it out of the tab order', () => {
    // `disabled` would make it unreachable exactly when the dialog hands it the caret; `aria-disabled`
    // says the same thing to a screen reader while leaving the control focusable (WCAG AA).
    expect(sortSelect().getAttribute('aria-disabled')).toBe('true');
    expect(sortSelect().disabled).toBe(false);

    expectRequest('TRENDING_DAILY').flush(response());
    fixture.detectChanges();

    expect(sortSelect().getAttribute('aria-disabled')).toBe('false');
  });

  it('ignores a list change while a fetch is in flight, so no late answer can overwrite a newer one', () => {
    const pending = expectRequest('TRENDING_DAILY');

    chooseSort('TOP_ALL_TIME');

    // No second request exists to race the first — the chooser is announced as unavailable and
    // behaves that way, and the visible value is put back so it never names a list that is not the
    // one being fetched.
    httpMock.expectNone((candidate) => candidate.url === '/api/seventv/leaderboard');
    expect(sortSelect().value).toBe('TRENDING_DAILY');

    pending.flush(response());
    fixture.detectChanges();

    // What arrived is what the chooser names, and it is what the grid shows.
    expect(grid().forcedSortMode()).toBe('trending');
    expect(sortSelect().value).toBe('TRENDING_DAILY');

    // Once the fetch is done the chooser works again.
    chooseSort('TOP_ALL_TIME');
    expectRequest('TOP_ALL_TIME').flush(response({ sortBy: 'TOP_ALL_TIME' }));
    fixture.detectChanges();
    expect(grid().forcedSortMode()).toBe('topAllTime');
  });
});
