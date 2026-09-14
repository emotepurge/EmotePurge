import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { signal } from '@angular/core';

import { LanguageService } from '../../core/i18n/language.service';
import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { ForeignEmoteGrid } from './foreign-emote-grid';

// Only the keys this component translates.
const DE_TRANSLATIONS = {
  import: {
    clearSelection: 'Auswahl aufheben',
    animated: 'animiert',
    foreignChannel: {
      empty: 'Das aktive 7TV-Set dieses Kanals hat keine Emotes.',
      truncated: 'Nur ein Teil des Sets konnte geladen werden ({{ loaded }} von {{ totalCount }}).',
      selectedCount: '{{ count }} ausgewählt',
      grid: { ariaLabel: 'Emote-Auswahl' },
      sort: {
        label: 'Sortieren nach',
        none: 'Set-Reihenfolge',
        topAllTime: '7TV-Score (gesamt)',
        trending: '7TV-Score (Trend)',
        noScore: 'kein Wert',
        scoreHint:
          'Die Zahl auf jeder Kachel ist 7TVs netzwerkweiter Vergleichswert für dieses eine Emote — kein Maß dafür, wie oft es in diesem Kanal benutzt wird.',
      },
    },
    // Stand-ins for `emptyMessageKey`/`truncatedMessageKey`/`scoreHintKey` (Task 7, spec E12):
    // arbitrary keys that only need to exist in this test's translation map to prove the grid
    // renders whatever key it is handed, not real production i18n — no entry belongs in
    // `web/public/i18n/*.json` for this task (that file is owned by a later task).
    leaderboard: {
      empty: 'Die 7TV-Bestenliste ist leer.',
      truncated: 'Nur ein Teil der Bestenliste geladen ({{ loaded }} von {{ totalCount }}).',
      scoreHint: 'Eigener Bestenlisten-Hinweistext.',
    },
  },
};

/** jsdom has no ResizeObserver, and the CDK viewport reads it during init too. */
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

function row(overrides: Partial<ForeignEmoteRow> = {}): ForeignEmoteRow {
  return {
    sevenTvEmoteId: 'e1',
    name: 'catJAM',
    defaultName: 'catJAM',
    imageUrl: 'https://cdn.7tv.app/e1/4x.webp',
    topAllTime: null,
    trending: null,
    ...overrides,
  };
}

describe('ForeignEmoteGrid', () => {
  let fixture: ComponentFixture<ForeignEmoteGrid>;
  let component: ForeignEmoteGrid;
  let host: HTMLElement;

  beforeEach(async () => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

    await TestBed.configureTestingModule({
      imports: [
        ForeignEmoteGrid,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: LanguageService,
          useValue: { lang: signal('de') } as unknown as LanguageService,
        },
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    fixture = TestBed.createComponent(ForeignEmoteGrid);
    component = fixture.componentInstance;
    host = fixture.nativeElement;
  });

  function render(
    emotes: ForeignEmoteRow[],
    truncated = false,
    totalCount: number | null = null,
  ): void {
    fixture.componentRef.setInput('emotes', emotes);
    fixture.componentRef.setInput('truncated', truncated);
    fixture.componentRef.setInput('totalCount', totalCount);
    fixture.detectChanges();
  }

  it('shows the empty-set message and no controls when the source set has no emotes', () => {
    render([]);

    expect(host.textContent).toContain('Das aktive 7TV-Set dieses Kanals hat keine Emotes.');
    expect(host.querySelector('[role="group"]')).toBeNull();
  });

  it('exposes the grid as an accessible group', () => {
    render([row()]);

    const group = host.querySelector('[role="group"]');
    expect(group).not.toBeNull();
    expect(group?.getAttribute('aria-label')).toBe('Emote-Auswahl');
  });

  it('does not preselect a score sort — the set order is what the sort control shows', () => {
    render([row()]);

    expect(component['sortMode']()).toBe('none');
    const select = host.querySelector('select');
    expect(select?.value).toBe('none');
  });

  it('offers the sort as a labelled choice, not as a tab bar over different lists', () => {
    // The operator read the old segmented control as a statement about the *list* and concluded the
    // grid was showing 7TV's global emotes. It is a sort over this one channel's set, and the
    // control has to say so: a real <label for> in front of a real select, no radiogroup that looks
    // like tabs.
    render([row()]);

    const select = host.querySelector('select');
    const label = host.querySelector('label');
    expect(host.querySelector('[role="radiogroup"]')).toBeNull();
    expect(select?.getAttribute('id')).toBeTruthy();
    expect(label?.getAttribute('for')).toBe(select?.getAttribute('id'));
    expect(label?.textContent?.trim()).toBe('Sortieren nach');
  });

  it('explains what the score on a tile means only while a score sort is active', () => {
    render([row()]);
    const hint = () =>
      Array.from(host.querySelectorAll('p')).some((p) =>
        p.textContent?.includes('netzwerkweiter Vergleichswert'),
      );

    expect(hint()).toBe(false);

    component['sortMode'].set('topAllTime');
    fixture.detectChanges();

    expect(hint()).toBe(true);
  });

  it('toggles a single row on click and emits the updated selection', () => {
    const a = row({ sevenTvEmoteId: 'a' });
    const b = row({ sevenTvEmoteId: 'b' });
    render([a, b]);

    const emitted: ForeignEmoteRow[][] = [];
    component.selectionChange.subscribe((rows) => emitted.push(rows));

    component['onCellClick'](a, { shiftKey: false } as MouseEvent);

    expect(component['selection'].isSelected(a)).toBe(true);
    expect(component['selection'].isSelected(b)).toBe(false);
    expect(emitted).toEqual([[a]]);

    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].isSelected(a)).toBe(false);
    expect(emitted.at(-1)).toEqual([]);
  });

  it('clears the whole selection and emits an empty array (#166)', () => {
    // The emission is the point of the test: neither host step reads ListSelection directly, both
    // learn about a cleared selection only through selectionChange (see clearSelection()'s doc).
    const a = row({ sevenTvEmoteId: 'a' });
    const b = row({ sevenTvEmoteId: 'b' });
    render([a, b]);

    const emitted: ForeignEmoteRow[][] = [];
    component.selectionChange.subscribe((rows) => emitted.push(rows));

    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    component['onCellClick'](b, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'b']);

    component['clearSelection']();

    expect(component['selection'].selectedKeys()).toEqual([]);
    expect(emitted.at(-1)).toEqual([]);
  });

  it('shows the clear-selection button only while at least one emote is selected (#166)', () => {
    const a = row({ sevenTvEmoteId: 'a' });
    render([a]);
    const clearButton = () =>
      Array.from(host.querySelectorAll('button')).find(
        (candidate) => candidate.textContent?.trim() === 'Auswahl aufheben',
      );

    expect(clearButton()).toBeUndefined();

    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    fixture.detectChanges();
    expect(clearButton()).not.toBeUndefined();

    clearButton()?.click();
    fixture.detectChanges();
    expect(clearButton()).toBeUndefined();
    expect(component['selection'].selectedKeys()).toEqual([]);
  });

  it('selects a contiguous range on a shift-click, only the selected rows appear in toAdd', () => {
    const rows = ['a', 'b', 'c', 'd', 'e'].map((id) => row({ sevenTvEmoteId: id }));
    render(rows);

    component['onCellClick'](rows[0], { shiftKey: false } as MouseEvent);
    component['onCellClick'](rows[3], { shiftKey: true } as MouseEvent);

    expect(component['selection'].selectedKeys().sort()).toEqual(['a', 'b', 'c', 'd']);
    // 'e' was never part of the range — the whole point of a per-row selection (spec F4).
    expect(component['selection'].isSelected(rows[4])).toBe(false);
  });

  it('keeps a selection intact across a score-sort change', () => {
    const a = row({ sevenTvEmoteId: 'a', topAllTime: 5 });
    const b = row({ sevenTvEmoteId: 'b', topAllTime: 50 });
    render([a, b]);

    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    component['sortMode'].set('topAllTime');
    fixture.detectChanges();

    // Still selected, and still resolves to the very same row object even though the display
    // order flipped — ListSelection is keyed, not positional (spec F4 / list-selection.ts).
    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(component['selection'].isSelected(a)).toBe(true);
    expect(component['selection'].selectedItems()).toEqual([a]);
  });

  it('announces a truncated load without silently dropping it (spec F3/AK5)', () => {
    render([row(), row({ sevenTvEmoteId: 'e2' })], true, 10);

    const status = host.querySelector('[role="status"]');
    expect(status).not.toBeNull();
    expect(status?.textContent).toContain('2');
    expect(status?.textContent).toContain('10');
  });

  // The visible name line under each sprite prints the alias alone (64 px truncates most names);
  // this label is what the tile carries as its accessible name AND as its mouse tooltip, so it is
  // where the divergence between alias and global default name is actually readable. The line
  // itself is markup and, per rule 12, not the subject of a test — the virtual viewport renders no
  // cells under jsdom anyway.
  it('labels a cell with the set alias, and additionally the default name when it differs', () => {
    const aliased = row({ name: 'PogChamp2', defaultName: 'PogChamp' });
    const plain = row({ sevenTvEmoteId: 'e2', name: 'catJAM', defaultName: 'catJAM' });

    expect(component['cellLabel'](aliased)).toBe('PogChamp2 (PogChamp)');
    expect(component['cellLabel'](plain)).toBe('catJAM');
  });

  // The play marker in the corner is aria-hidden, so the label is the only place a screen reader
  // learns that an emote is animated.
  it('says "animated" in the label of an animated emote, and only there', () => {
    const animated = row({ imageUrl: 'https://cdn.7tv.app/e1/4x_static.webp' });
    const still = row({ sevenTvEmoteId: 'e2', imageUrl: 'https://cdn.7tv.app/e2/4x.webp' });

    expect(component['cellLabel'](animated)).toBe('catJAM, animiert');
    expect(component['cellLabel'](still)).toBe('catJAM');
  });

  it('puts "animated" after the names and before the active score', () => {
    render([row()]);
    component['sortMode'].set('topAllTime');
    fixture.detectChanges();

    expect(
      component['cellLabel'](
        row({
          name: 'PogChamp2',
          defaultName: 'PogChamp',
          imageUrl: 'https://cdn.7tv.app/e1/4x_static.webp',
          topAllTime: 12400,
        }),
      ),
    ).toBe('PogChamp2 (PogChamp), animiert, 7TV-Score (gesamt): 12,4k');
  });

  describe('hover playback', () => {
    const animatedA = row({
      sevenTvEmoteId: 'a',
      imageUrl: 'https://cdn.7tv.app/a/4x_static.webp',
    });
    const animatedB = row({
      sevenTvEmoteId: 'b',
      imageUrl: 'https://cdn.7tv.app/b/4x_static.webp',
    });
    const still = row({ sevenTvEmoteId: 's', imageUrl: 'https://cdn.7tv.app/s/4x.webp' });

    beforeEach(() => render([animatedA, animatedB, still]));

    // The whole design: one animated sprite in the grid, not one per cell — so nothing plays until
    // a cell is pointed at or focused.
    it('plays nothing before any cell is hovered', () => {
      expect(
        [animatedA, animatedB, still].some((emote) => component['playsAnimation'](emote)),
      ).toBe(false);
    });

    it('plays only the hovered animated emote', () => {
      component['onCellEnter'](animatedA);

      expect(component['playsAnimation'](animatedA)).toBe(true);
      expect(component['playsAnimation'](animatedB)).toBe(false);
      expect(component['playsAnimation'](still)).toBe(false);
    });

    it('moves playback with the pointer rather than adding a second one', () => {
      component['onCellEnter'](animatedA);
      component['onCellEnter'](animatedB);

      expect(component['playsAnimation'](animatedA)).toBe(false);
      expect(component['playsAnimation'](animatedB)).toBe(true);
    });

    // A still has no animation to fetch: mounting the animated sprite on it would be pointless.
    it('plays nothing for a hovered still', () => {
      component['onCellEnter'](still);

      expect(component['playsAnimation'](still)).toBe(false);
    });

    // mouseleave and blur share the handler.
    it('stops once the pointer or the focus leaves the cell', () => {
      component['onCellEnter'](animatedA);
      component['onCellLeave'](animatedA);

      expect(component['playsAnimation'](animatedA)).toBe(false);
    });

    it('does not stop a cell when a different cell loses focus', () => {
      component['onCellEnter'](animatedA);
      component['onCellLeave'](animatedB);

      expect(component['playsAnimation'](animatedA)).toBe(true);
    });

    // The cell keeps its own still underneath; hiding it before the animation has painted would
    // blank the cell, keeping it visible afterwards would show it through the animation.
    it('hides the cell still only once the hovered animation has painted', () => {
      const hidden = () =>
        component['stillSpriteClass'](animatedA).split(' ').includes('invisible');
      component['onCellEnter'](animatedA);
      expect(hidden()).toBe(false);

      component['revealedKey'].set('a');
      expect(hidden()).toBe(true);

      component['onCellEnter'](animatedB);
      component['onCellEnter'](animatedA);
      expect(hidden()).toBe(false);
    });
  });

  it('names the active score in the cell label — an aria-label replaces the tile text', () => {
    // The number printed on the tile is exactly what the user is sorting by, and an explicit
    // aria-label wipes the descendant text out of the accessibility tree: without this it does not
    // exist for a screen reader. It is announced under the sort control's own label, which is what
    // gives a bare number its meaning without claiming a unit for it.
    render([row()]);
    component['sortMode'].set('topAllTime');
    fixture.detectChanges();

    expect(component['cellLabel'](row({ name: 'catJAM', topAllTime: 12400 }))).toBe(
      'catJAM, 7TV-Score (gesamt): 12,4k',
    );
  });

  it('says the missing score as a word, where the tile only has room for a dash', () => {
    render([row()]);
    component['sortMode'].set('trending');
    fixture.detectChanges();

    expect(component['cellLabel'](row({ name: 'catJAM', trending: null }))).toBe(
      'catJAM, 7TV-Score (Trend): kein Wert',
    );
    // The tile itself keeps the typographic placeholder — it has 64 px, not a sentence.
    expect(component['scoreBadge'](row({ trending: null }))).toBe('–');
  });

  it('formats the active 7TV-global score compactly, and a missing score as a dash', () => {
    render([row()]);
    component['sortMode'].set('topAllTime');
    fixture.detectChanges();

    expect(component['scoreBadge'](row({ topAllTime: 12400 }))).toBe('12,4k');
    expect(component['scoreBadge'](row({ topAllTime: 42 }))).toBe('42');
    expect(component['scoreBadge'](row({ topAllTime: null }))).toBe('–');
  });

  // The six cases below cover `forcedSortMode` and the three caption inputs (spec E12/F2, AK 19,
  // 20, 23). None of them touch a case above — the render() helper and every existing `it` are
  // untouched, since a caller that never sets these four new inputs must see today's behaviour.

  it('with a forcedSortMode, renders no sort select but still shows the score explainer', () => {
    fixture.componentRef.setInput('emotes', [row()]);
    fixture.componentRef.setInput('forcedSortMode', 'topAllTime');
    fixture.detectChanges();

    expect(host.querySelector('select')).toBeNull();
    expect(host.querySelector('label')).toBeNull();
    expect(
      Array.from(host.querySelectorAll('p')).some((p) =>
        p.textContent?.includes('netzwerkweiter Vergleichswert'),
      ),
    ).toBe(true);
  });

  it('drives the score tile and each cell aria-label from the forced mode, leaving the internal sortMode at its default', () => {
    fixture.componentRef.setInput('emotes', [row()]);
    fixture.componentRef.setInput('forcedSortMode', 'trending');
    fixture.detectChanges();

    // The grid never had a select to change this from — a forced mode bypasses it entirely
    // rather than setting it (spec F2's "one signal drives four things").
    expect(component['sortMode']()).toBe('none');
    expect(component['scoreBadge'](row({ trending: 12400 }))).toBe('12,4k');
    expect(component['cellLabel'](row({ name: 'catJAM', trending: 12400 }))).toBe(
      'catJAM, 7TV-Score (Trend): 12,4k',
    );
  });

  it('orders sortedEmotes by the forced score field (AK 19), independent of the unset select', () => {
    const a = row({ sevenTvEmoteId: 'a', topAllTime: 5 });
    const b = row({ sevenTvEmoteId: 'b', topAllTime: 50 });
    fixture.componentRef.setInput('emotes', [a, b]);
    fixture.componentRef.setInput('forcedSortMode', 'topAllTime');
    fixture.detectChanges();

    expect(component['sortedEmotes']()).toEqual([b, a]);
  });

  it('lets the three caption inputs override the rendered message text (AK 20)', () => {
    fixture.componentRef.setInput('emotes', []);
    fixture.componentRef.setInput('truncated', true);
    fixture.componentRef.setInput('totalCount', 5);
    fixture.componentRef.setInput('emptyMessageKey', 'import.leaderboard.empty');
    fixture.componentRef.setInput('truncatedMessageKey', 'import.leaderboard.truncated');
    fixture.detectChanges();

    expect(host.textContent).toContain('Die 7TV-Bestenliste ist leer.');
    expect(host.textContent).toContain('Nur ein Teil der Bestenliste geladen (0 von 5).');
    expect(host.textContent).not.toContain('Das aktive 7TV-Set dieses Kanals hat keine Emotes.');

    // Switch to the populated view to reach the score hint, the third overridable caption — it
    // only renders while a score sort is active (spec F2), which the empty view above never is.
    fixture.componentRef.setInput('emotes', [row()]);
    fixture.componentRef.setInput('truncated', false);
    fixture.componentRef.setInput('forcedSortMode', 'topAllTime');
    fixture.componentRef.setInput('scoreHintKey', 'import.leaderboard.scoreHint');
    fixture.detectChanges();

    expect(host.textContent).toContain('Eigener Bestenlisten-Hinweistext.');
  });

  it('defaults the three caption inputs to the current foreignChannel keys when left unset (AK 23)', () => {
    render([]);

    expect(component.emptyMessageKey()).toBe('import.foreignChannel.empty');
    expect(component.truncatedMessageKey()).toBe('import.foreignChannel.truncated');
    expect(component.scoreHintKey()).toBe('import.foreignChannel.sort.scoreHint');
  });

  it('gives two grid instances different sort-select ids (AK 20)', () => {
    // Set this suite's own fixture first: `detectChanges()` under zoneless CD ticks the whole
    // `ApplicationRef`, not just the fixture it was called on — creating and detecting `other`
    // below before this one had its required `emotes` input set would trip NG0950 on `component`.
    render([row()]);

    const other = TestBed.createComponent(ForeignEmoteGrid);
    other.componentRef.setInput('emotes', [row()]);
    other.detectChanges();

    const ownId = component['sortSelectId'];
    const otherId = other.componentInstance['sortSelectId'];
    expect(ownId).not.toBe(otherId);
    expect(host.querySelector('select')?.getAttribute('id')).toBe(ownId);
  });

  it('formats the truncation-notice and selected-count parameters with locale grouping, and reacts to a language switch (Regel 14)', () => {
    // 7TV's "top overall" leaderboard reported 1,372,094 total entries live — six digits is the
    // shape the observed defect actually had, not a synthetic edge case.
    const many = Array.from({ length: 1500 }, (_, i) => row({ sevenTvEmoteId: `e${i}` }));
    render(many, true, 1372094);

    component['selection'].onRowClick(many[0], { shiftKey: false } as MouseEvent);
    component['selection'].onRowClick(many[1499], { shiftKey: true } as MouseEvent);
    expect(component['selection'].selectedKeys().length).toBe(1500);

    expect(component['truncatedNoticeParams']()).toEqual({
      loaded: '1.500',
      totalCount: '1.372.094',
    });
    expect(component['selectedCountParams']()).toEqual({ count: '1.500' });

    const languageService = TestBed.inject(LanguageService);
    (languageService.lang as unknown as { set: (value: string) => void }).set('en');

    expect(component['truncatedNoticeParams']()).toEqual({
      loaded: '1,500',
      totalCount: '1,372,094',
    });
    expect(component['selectedCountParams']()).toEqual({ count: '1,500' });
  });
});
