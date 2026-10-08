import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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

  it('clears the whole selection and emits an empty array', () => {
    // The emission is the point: host steps see the selection only through selectionChange.
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

  // K3 follow-up fix: a host (ForeignChannelStep) can now serve a switch straight from its own
  // cache, reusing this same grid instance instead of destroying and recreating it — the
  // recreate used to be what cleared a stale selection. These three cases pin the replacement
  // mechanism directly, without a host in the loop.

  it('clears the selection and emits [] when a different emotes array arrives', () => {
    const a = row({ sevenTvEmoteId: 'a' });
    const b = row({ sevenTvEmoteId: 'b' });
    render([a]);
    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    const emitted: ForeignEmoteRow[][] = [];
    component.selectionChange.subscribe((rows) => emitted.push(rows));

    fixture.componentRef.setInput('emotes', [b]);
    fixture.detectChanges();

    expect(component['selection'].selectedKeys()).toEqual([]);
    expect(emitted).toEqual([[]]);
  });

  it("emits nothing on the component's own first render — only a later change to `emotes` counts", () => {
    const a = row({ sevenTvEmoteId: 'a' });
    const emitted: ForeignEmoteRow[][] = [];
    // Subscribed before the very first `detectChanges()`/effect flush, so a spurious emission on
    // construction (rather than on an actual list change) would show up here.
    component.selectionChange.subscribe((rows) => emitted.push(rows));

    render([a]);

    expect(emitted).toEqual([]);
  });

  it('does not clear the selection when the very same emotes array reference is set again', () => {
    const a = row({ sevenTvEmoteId: 'a' });
    const emotes = [a];
    render(emotes);
    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    expect(component['selection'].selectedKeys()).toEqual(['a']);

    const emitted: ForeignEmoteRow[][] = [];
    component.selectionChange.subscribe((rows) => emitted.push(rows));

    // Angular's signal input never becomes dirty for an `Object.is`-equal value — the effect does
    // not even re-run, so this is not merely "the clear happened to be a no-op".
    fixture.componentRef.setInput('emotes', emotes);
    fixture.detectChanges();

    expect(component['selection'].selectedKeys()).toEqual(['a']);
    expect(emitted).toEqual([]);
  });

  it('shows the clear-selection button only while at least one emote is selected', () => {
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

  // The button unmounts on its own click; focus must not fall to <body> inside the modal.
  it('moves focus to the grid when the clear-selection button removes itself', () => {
    const a = row({ sevenTvEmoteId: 'a' });
    render([a]);
    component['onCellClick'](a, { shiftKey: false } as MouseEvent);
    fixture.detectChanges();
    const clearButton = () =>
      Array.from(host.querySelectorAll('button')).find(
        (candidate) => candidate.textContent?.trim() === 'Auswahl aufheben',
      );

    clearButton()!.focus();
    clearButton()!.click();
    fixture.detectChanges();

    expect(clearButton()).toBeUndefined();
    expect(document.activeElement).toBe(host.querySelector('[role="group"]'));
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
  // itself is markup and, per rule 12, not the subject of a test.
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

  describe('rendered cells', () => {
    const animatedA = row({
      sevenTvEmoteId: 'a',
      name: 'AnimA',
      defaultName: 'AnimA',
      imageUrl: 'https://cdn.7tv.app/emote/a/4x_static.webp',
    });
    const animatedB = row({
      sevenTvEmoteId: 'b',
      name: 'AnimB',
      defaultName: 'AnimB',
      imageUrl: 'https://cdn.7tv.app/emote/b/4x_static.webp',
    });
    const still = row({
      sevenTvEmoteId: 's',
      name: 'Still',
      defaultName: 'Still',
      imageUrl: 'https://cdn.7tv.app/emote/s/4x.webp',
    });

    function cells(): HTMLButtonElement[] {
      return [...host.querySelectorAll<HTMLButtonElement>('[role="group"] button[aria-pressed]')];
    }

    function cell(name: string): HTMLButtonElement {
      const found = cells().find((candidate) => candidate.getAttribute('aria-label') === name);
      if (!found) {
        throw new Error(`no rendered cell named ${name}`);
      }
      return found;
    }

    function grid(): HTMLElement {
      return host.querySelector<HTMLElement>('[role="group"]')!;
    }

    function sources(): string[] {
      return [...host.querySelectorAll('img')].map((img) => img.getAttribute('src') ?? '');
    }

    // An animated emote's still is a `_static` url; any other variant of its id is the animation.
    function requestsAnimation(id: string): boolean {
      return sources().some((src) => new RegExp(`/emote/${id}/\\dx\\.webp$`).test(src));
    }

    function fire(target: EventTarget, type: string): void {
      target.dispatchEvent(new Event(type));
      fixture.detectChanges();
    }

    function advance(ms: number): void {
      vi.advanceTimersByTime(ms);
      fixture.detectChanges();
    }

    // jsdom has no layout: the viewport measures 0 px and renders the rows its buffer covers, one
    // cell each, on an animation frame. Let real frames pass before faking the dwell timers.
    beforeEach(async () => {
      render([animatedA, animatedB, still]);
      for (let attempt = 0; attempt < 50 && cells().length < 3; attempt++) {
        await new Promise((resolve) => setTimeout(resolve, 10));
        await fixture.whenStable();
      }
      expect(cells()).toHaveLength(3);
      vi.useFakeTimers();
    });

    afterEach(() => vi.useRealTimers());

    // The play marker in the corner is aria-hidden; the name is what marks the cell.
    it('names animated cells as animated, and only those', () => {
      expect(cells().map((candidate) => candidate.getAttribute('aria-label'))).toEqual([
        'AnimA, animiert',
        'AnimB, animiert',
        'Still',
      ]);
    });

    it('fetches no animation while no cell is hovered', () => {
      advance(1000);

      expect(requestsAnimation('a') || requestsAnimation('b')).toBe(false);
    });

    it('fetches the animation of the hovered animated cell after the dwell, and of no other', () => {
      fire(cell('AnimA, animiert'), 'mouseenter');
      advance(199);
      expect(requestsAnimation('a')).toBe(false);

      advance(1);
      expect(requestsAnimation('a')).toBe(true);
      expect(requestsAnimation('b')).toBe(false);
    });

    it('moves playback with the pointer rather than adding a second one', () => {
      fire(cell('AnimA, animiert'), 'mouseenter');
      advance(200);
      fire(cell('AnimB, animiert'), 'mouseenter');
      advance(200);

      expect(requestsAnimation('a')).toBe(false);
      expect(requestsAnimation('b')).toBe(true);
    });

    it('requests nothing for a hovered still', () => {
      const before = sources();

      fire(cell('Still'), 'mouseenter');
      advance(1000);

      expect(sources()).toEqual(before);
    });

    it('stops once the pointer or the focus leaves the cell', () => {
      const a = cell('AnimA, animiert');
      fire(a, 'mouseenter');
      advance(200);
      fire(a, 'mouseleave');
      expect(requestsAnimation('a')).toBe(false);

      a.focus();
      advance(200);
      expect(requestsAnimation('a')).toBe(true);
      a.blur();
      fixture.detectChanges();
      expect(requestsAnimation('a')).toBe(false);
    });

    it('does not stop a cell when a different cell loses focus', () => {
      fire(cell('AnimA, animiert'), 'mouseenter');
      fire(cell('AnimB, animiert'), 'blur');
      advance(200);

      expect(requestsAnimation('a')).toBe(true);
    });

    it('stops what the pointer started when the pointer leaves the grid, not what focus started', () => {
      fire(cell('AnimA, animiert'), 'mouseenter');
      advance(200);
      fire(grid(), 'mouseleave');
      expect(requestsAnimation('a')).toBe(false);

      cell('AnimA, animiert').focus();
      advance(200);
      fire(grid(), 'mouseleave');
      expect(requestsAnimation('a')).toBe(true);
    });

    // A cell recycled under a resting pointer fires no mouseleave; without this its hover would
    // survive and fetch the animation as soon as the cell rendered again.
    it('stops pointer playback when the viewport scrolls, but keeps a cell keyboard focus holds', () => {
      const viewport = fixture.debugElement.query(By.directive(CdkVirtualScrollViewport))
        .nativeElement as HTMLElement;

      fire(cell('AnimA, animiert'), 'mouseenter');
      fire(viewport, 'scroll');
      advance(1000);
      expect(requestsAnimation('a')).toBe(false);

      cell('AnimA, animiert').focus();
      advance(200);
      fire(viewport, 'scroll');
      expect(requestsAnimation('a')).toBe(true);
    });

    // A click focuses the cell in Chrome and Firefox, so focus holds it once the pointer moves on.
    it('keeps a clicked cell playing after the pointer leaves it, until focus leaves', () => {
      const a = cell('AnimA, animiert');
      fire(a, 'mouseenter');
      a.focus();
      a.click();
      advance(200);
      expect(requestsAnimation('a')).toBe(true);

      fire(a, 'mouseleave');
      expect(requestsAnimation('a')).toBe(true);
      fire(grid(), 'mouseleave');
      expect(requestsAnimation('a')).toBe(true);

      a.blur();
      fixture.detectChanges();
      expect(requestsAnimation('a')).toBe(false);
    });

    it('lets the pointer take over from a focused cell and hands playback back when it leaves', () => {
      cell('AnimA, animiert').focus();
      advance(200);
      expect(requestsAnimation('a')).toBe(true);

      const b = cell('AnimB, animiert');
      fire(b, 'mouseenter');
      advance(200);
      expect(requestsAnimation('a')).toBe(false);
      expect(requestsAnimation('b')).toBe(true);

      fire(b, 'mouseleave');
      expect(requestsAnimation('b')).toBe(false);
      advance(199);
      expect(requestsAnimation('a')).toBe(false);
      advance(1);
      expect(requestsAnimation('a')).toBe(true);
    });

    it('hands playback back to the focused cell when the viewport scrolls', () => {
      const viewport = fixture.debugElement.query(By.directive(CdkVirtualScrollViewport))
        .nativeElement as HTMLElement;
      cell('AnimA, animiert').focus();
      fire(cell('AnimB, animiert'), 'mouseenter');
      advance(200);
      expect(requestsAnimation('b')).toBe(true);

      fire(viewport, 'scroll');
      advance(200);
      expect(cells()).toHaveLength(3);
      expect(requestsAnimation('b')).toBe(false);
      expect(requestsAnimation('a')).toBe(true);
    });

    it('keeps playing the cell the pointer rests on when that cell loses focus', () => {
      const a = cell('AnimA, animiert');
      fire(a, 'mouseenter');
      a.focus();
      advance(200);

      a.blur();
      fixture.detectChanges();
      advance(200);
      expect(requestsAnimation('a')).toBe(true);
    });

    // A click focuses the cell in Chrome and Firefox, and virtualisation removes a focused cell
    // without a blur. Neither may leave its key behind to play the cell when it renders again.
    it('stops a clicked cell once it leaves the rendered rows, and does not play it when it renders again', async () => {
      const many = Array.from({ length: 30 }, (_, i) =>
        row({
          sevenTvEmoteId: `r${i}`,
          name: `Row${i}`,
          defaultName: `Row${i}`,
          imageUrl: `https://cdn.7tv.app/emote/r${i}/4x_static.webp`,
        }),
      );
      const viewportDebug = fixture.debugElement.query(By.directive(CdkVirtualScrollViewport));
      const viewport = viewportDebug.componentInstance as CdkVirtualScrollViewport;
      let scrollOffset = 0;
      vi.spyOn(viewport, 'measureScrollOffset').mockImplementation(() => scrollOffset);
      // jsdom has no layout, so a scroll is the offset the viewport measures plus a scroll event.
      // The CDK recomputes its range on a real animation frame, which fake timers do not reach, so
      // checkViewportSize() runs that recomputation instead. It re-renders after a microtask.
      const scrollTo = async (offset: number) => {
        scrollOffset = offset;
        fire(viewportDebug.nativeElement, 'scroll');
        viewport.checkViewportSize();
        await Promise.resolve();
        advance(16);
      };
      const isRendered = () =>
        cells().some((each) => each.getAttribute('aria-label') === 'Row0, animiert');

      render(many);
      advance(16);
      const first = cell('Row0, animiert');
      fire(first, 'mouseenter');
      first.focus();
      first.click();
      fixture.detectChanges();
      advance(200);
      expect(requestsAnimation('r0')).toBe(true);

      // Still rendered: focus keeps it playing through the scroll.
      await scrollTo(40);
      expect(isRendered()).toBe(true);
      expect(requestsAnimation('r0')).toBe(true);

      await scrollTo(2000);
      expect(isRendered()).toBe(false);

      await scrollTo(0);
      expect(isRendered()).toBe(true);
      advance(1000);
      expect(requestsAnimation('r0')).toBe(false);
    });

    it('hides the cell still only once the hovered animation has painted', () => {
      fire(cell('AnimA, animiert'), 'mouseenter');
      advance(200);
      expect(component['stillHidden'](animatedA)).toBe(false);

      const overlay = [...host.querySelectorAll('img')].find((img) =>
        /\/emote\/a\/\dx\.webp$/.test(img.getAttribute('src') ?? ''),
      )!;
      fire(overlay, 'load');
      fixture.detectChanges();
      expect(component['stillHidden'](animatedA)).toBe(true);

      // Coming back to the cell has to earn the reveal again.
      fire(cell('AnimB, animiert'), 'mouseenter');
      fire(cell('AnimA, animiert'), 'mouseenter');
      expect(component['stillHidden'](animatedA)).toBe(false);
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
    expect(component['selection'].selectedKeys()).toHaveLength(1500);

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
