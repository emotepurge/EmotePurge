import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import {
  TagRemovalConfirmDialogData,
  TagRemovalConfirmResult,
  openTagRemovalConfirmDialog,
} from './tag-removal-confirm-dialog';
import type { TagRemovalRow } from './tag-removal';

const DE = {
  common: { cancel: 'Abbrechen' },
  massDelete: {
    confirmSetLine: 'Aus dem Set „{{ setName }}“.',
    checkingSharedSets: 'Prüfe geteilte Sets…',
    sharedSetWarningTitle: 'Achtung: geteiltes Set.',
    notOwnSet: 'Das Set gehört nicht diesem Channel.',
    knownAffected: 'Betroffen: {{ list }}',
    moderatedAffected: 'Moderiert: {{ list }}',
    ownershipCheckUnavailable: 'Besitz nicht prüfbar.',
    irreversibleNotice: 'Nicht rückgängig zu machen.',
    undetectableChannelsNotice: 'Fremde Channels nicht erkennbar.',
  },
  tags: {
    removalDialog: {
      title: '{{tag}} ausräumen',
      summary: {
        remove: { one: '{{count}} wird entfernt', other: '{{count}} werden entfernt' },
        keep: { one: '{{count}} bleibt', other: '{{count}} bleiben' },
      },
      proposedHeading: 'Vorgeschlagen',
      notProposedHeading: 'Nicht vorgeschlagen',
      placedAt: 'eingespielt am {{date}}',
      reason: {
        alreadyPresent: 'war schon vorher im Set',
        heldBy: 'wird noch von {{tag}} gebraucht',
      },
      notInSet: { one: '{{count}} weiterer nicht im Set', other: '{{count}} weitere nicht im Set' },
      nothingToDelete: 'Es wird nichts bei 7TV gelöscht.',
      confirm: 'Ausräumen',
    },
  },
};

function row(id: string, over: Partial<TagRemovalRow> = {}): TagRemovalRow {
  return {
    sevenTvEmoteId: id,
    aliases: [`name-${id}`],
    displayName: `name-${id}`,
    imageUrl: null,
    checked: true,
    reason: 'placed',
    heldBy: [],
    placedAtUtc: '2026-10-01T10:00:00Z',
    ...over,
  };
}

const OWN_SET: EmoteSetWarning = {
  available: true,
  isOwnSet: true,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

describe('TagRemovalConfirmDialog', () => {
  let results: (TagRemovalConfirmResult | undefined)[];
  let ref: DialogRef<TagRemovalConfirmResult | undefined>;
  const warning = signal<EmoteSetWarning | null>(OWN_SET);
  const warningLoading = signal(false);

  beforeEach(async () => {
    results = [];
    warning.set(OWN_SET);
    warningLoading.set(false);
    await TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  afterEach(() => {
    ref?.close();
  });

  function open(
    rows: TagRemovalRow[],
    over: Partial<TagRemovalConfirmDialogData> = {},
  ): HTMLElement {
    const data: TagRemovalConfirmDialogData = {
      tagName: 'Stronghold',
      setName: 'Hauptset',
      proposal: { rows, notInSetCount: 0, snapshot: [], ownInLiveIds: [] },
      warning,
      warningLoading,
      ...over,
    };
    ref = openTagRemovalConfirmDialog(TestBed.inject(Dialog), data);
    ref.closed.subscribe((r) => results.push(r));
    return document.querySelector<HTMLElement>('app-tag-removal-confirm-dialog')!;
  }

  async function settle(): Promise<void> {
    TestBed.tick();
    await Promise.resolve();
    TestBed.tick();
  }

  const box = (host: HTMLElement, id: string) =>
    host.querySelector<HTMLInputElement>(`input[data-emote-id="${id}"]`)!;
  const button = (host: HTMLElement, label: string) =>
    Array.from(host.querySelectorAll('button')).find((b) => b.textContent?.trim() === label)!;
  const summary = (host: HTMLElement) => host.querySelector('[role="status"]')!.textContent!.trim();
  const rowIds = (host: HTMLElement) =>
    Array.from(host.querySelectorAll<HTMLInputElement>('input[data-emote-id]')).map(
      (i) => i.dataset['emoteId'],
    );

  it('names itself and carries the dialog role', async () => {
    const host = open([row('a')]);
    await settle();
    const pane = document.querySelector('[role="dialog"]')!;
    expect(pane.contains(host)).toBe(true);
    const labelled = document.getElementById(pane.getAttribute('aria-labelledby')!);
    expect(labelled?.textContent?.trim()).toBe('Stronghold ausräumen');
  });

  it('keeps the summary in step with the ticks', async () => {
    const host = open([row('a'), row('b')]);
    await settle();
    expect(summary(host)).toBe('2 werden entfernt, 0 bleiben');

    box(host, 'a').click();
    await settle();
    expect(summary(host)).toBe('1 wird entfernt, 1 bleibt');

    box(host, 'a').click();
    await settle();
    expect(summary(host)).toBe('2 werden entfernt, 0 bleiben');
  });

  it('groups rows by the proposal and keeps them in place when toggled', async () => {
    const host = open([
      row('held', { checked: false, reason: 'heldBy', heldBy: [{ id: 2, name: 'Raid' }] }),
      row('own'),
    ]);
    await settle();
    expect(rowIds(host)).toEqual(['own', 'held']);
    expect(Array.from(host.querySelectorAll('h3')).map((h) => h.textContent!.trim())).toEqual([
      'Vorgeschlagen',
      'Nicht vorgeschlagen',
    ]);
    expect(summary(host)).toBe('1 wird entfernt, 1 bleibt');

    box(host, 'own').click();
    await settle();
    expect(rowIds(host)).toEqual(['own', 'held']);
    expect(box(host, 'own').checked).toBe(false);
    expect(summary(host)).toBe('0 werden entfernt, 2 bleiben');

    box(host, 'held').click();
    await settle();
    expect(rowIds(host)).toEqual(['own', 'held']);
    expect(box(host, 'held').checked).toBe(true);
    expect(summary(host)).toBe('1 wird entfernt, 1 bleibt');
  });

  it('says why a row is not proposed', async () => {
    const host = open([
      row('held', {
        checked: false,
        reason: 'heldBy',
        placedAtUtc: null,
        heldBy: [
          { id: 2, name: 'Raid' },
          { id: 3, name: 'Duo' },
        ],
      }),
      row('pre', { checked: false, reason: 'alreadyPresent', placedAtUtc: null }),
    ]);
    await settle();
    const text = host.textContent!;
    expect(text).toContain('wird noch von Raid, Duo gebraucht');
    expect(text).toContain('war schon vorher im Set');
    expect(text).not.toContain('eingespielt am');
    expect(host.querySelector('[title="Raid, Duo"]')).not.toBeNull();
  });

  it('shows the placement date of a row this tag placed that another tag still holds', async () => {
    const host = open([
      row('held', { checked: false, reason: 'heldBy', heldBy: [{ id: 2, name: 'Raid' }] }),
    ]);
    await settle();
    expect(host.textContent).toContain('wird noch von Raid gebraucht');
    expect(host.textContent).toContain('eingespielt am');
  });

  it('shows the placement date of a ticked row', async () => {
    const host = open([row('a')]);
    await settle();
    expect(host.textContent).toMatch(/eingespielt am \d{1,2}[./]\d{1,2}[./]\d{2,4}/);
  });

  it('keeps the confirm button active at n = 0 and says nothing is deleted', async () => {
    const host = open([row('held', { checked: false, reason: 'alreadyPresent' })]);
    await settle();
    expect(summary(host)).toBe('0 werden entfernt, 1 bleibt');
    expect(button(host, 'Ausräumen').disabled).toBe(false);
    expect(host.textContent).toContain('Es wird nichts bei 7TV gelöscht.');

    button(host, 'Ausräumen').click();
    expect(results).toEqual([{ checkedIds: [] }]);
  });

  it('closes with the ticked ids in proposal order after toggling', async () => {
    const host = open([row('a'), row('b', { checked: false, reason: 'alreadyPresent' }), row('c')]);
    await settle();
    box(host, 'a').click();
    box(host, 'b').click();
    await settle();

    button(host, 'Ausräumen').click();
    expect(results).toEqual([{ checkedIds: ['b', 'c'] }]);
  });

  it('closes with undefined on Escape and on Cancel', async () => {
    open([row('a')]);
    await settle();
    document
      .querySelector('[role="dialog"]')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
    await settle();
    expect(results).toEqual([undefined]);

    results.length = 0;
    open([row('a')]);
    await settle();
    button(document.body, 'Abbrechen').click();
    expect(results).toEqual([undefined]);
  });

  it('shows the shared-set warning only when the set is not the channel own', async () => {
    const host = open([row('a')]);
    await settle();
    expect(host.querySelectorAll('[role="alert"]')).toHaveLength(0);

    warning.set({ ...OWN_SET, isOwnSet: false });
    await settle();
    const alert = host.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain('Achtung: geteiltes Set.');
    expect(alert?.textContent).toContain('Das Set gehört nicht diesem Channel.');
  });

  it('locks the confirm button only while the shared-set check runs', async () => {
    warningLoading.set(true);
    const host = open([row('a')]);
    await settle();
    expect(button(host, 'Ausräumen').disabled).toBe(true);
    warningLoading.set(false);
    await settle();
    expect(button(host, 'Ausräumen').disabled).toBe(false);
  });

  it('keeps exactly one status region, also while the ownership check is unavailable', async () => {
    warning.set({ ...OWN_SET, available: false });
    const host = open([row('a')]);
    await settle();
    expect(host.textContent).toContain('Besitz nicht prüfbar.');
    expect(host.querySelectorAll('[role="status"]')).toHaveLength(1);
  });

  describe('long lists', () => {
    let lastViewport: CdkVirtualScrollViewport | null = null;
    const many = (n: number) => Array.from({ length: n }, (_, i) => row(`r${i}`));

    /** jsdom has no layout: the viewport would measure 0 px and has no ResizeObserver. A fixed
     *  three-row viewport and a simulated scroll offset stand in for the browser, so rows far
     *  down the list are really absent from the DOM until `scrollToIndex` brings them in. */
    beforeEach(() => {
      vi.stubGlobal(
        'ResizeObserver',
        class {
          observe(): void {
            /* no-op */
          }
          unobserve(): void {
            /* no-op */
          }
          disconnect(): void {
            /* no-op */
          }
        },
      );
      let offset = 0;
      lastViewport = null;
      vi.spyOn(CdkVirtualScrollViewport.prototype, 'measureViewportSize').mockReturnValue(156);
      vi.spyOn(CdkVirtualScrollViewport.prototype, 'measureScrollOffset').mockImplementation(
        () => offset,
      );
      vi.spyOn(CdkVirtualScrollViewport.prototype, 'scrollToIndex').mockImplementation(function (
        this: CdkVirtualScrollViewport,
        index: number,
      ) {
        lastViewport = this;
        offset = index * 52;
        this.checkViewportSize();
      });
    });

    afterEach(() => {
      vi.restoreAllMocks();
      vi.unstubAllGlobals();
    });

    async function flush(): Promise<void> {
      await new Promise((resolve) => setTimeout(resolve));
      await settle();
    }

    const keydown = (host: HTMLElement, id: string, key: string) =>
      box(host, id).dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    const tabStops = (host: HTMLElement) =>
      Array.from(host.querySelectorAll<HTMLInputElement>('input[data-emote-id]'))
        .filter((i) => i.tabIndex === 0)
        .map((i) => i.dataset['emoteId']);

    it('makes only the first row a tab stop and moves it with the arrow keys', async () => {
      const host = open(many(60));
      await flush();
      expect(tabStops(host)).toEqual(['r0']);

      box(host, 'r0').focus();
      keydown(host, 'r0', 'ArrowDown');
      await flush();
      expect(document.activeElement).toBe(box(host, 'r1'));
      expect(tabStops(host)).toEqual(['r1']);

      keydown(host, 'r1', 'ArrowUp');
      await flush();
      expect(document.activeElement).toBe(box(host, 'r0'));
      expect(tabStops(host)).toEqual(['r0']);
    });

    it('reaches a row outside the rendered buffer with End', async () => {
      const host = open(many(60));
      await flush();
      expect(host.querySelector('input[data-emote-id="r59"]')).toBeNull();

      box(host, 'r0').focus();
      keydown(host, 'r0', 'End');
      await flush();
      expect(document.activeElement).toBe(box(host, 'r59'));
      expect(tabStops(host)).toEqual(['r59']);
    });

    it('hands the tab stop to the nearest rendered row once its own row is scrolled away', async () => {
      const host = open(many(60));
      await flush();
      box(host, 'r0').focus();
      keydown(host, 'r0', 'End');
      await flush();
      expect(tabStops(host)).toEqual(['r59']);

      // Scrolled back to the top by wheel or scrollbar: r59 leaves the DOM, and Tab into the list
      // must still find exactly one stop — the rendered row closest to where r59 sits.
      (document.activeElement as HTMLElement | null)?.blur();
      lastViewport!.scrollToIndex(0);
      await flush();
      expect(host.querySelector('input[data-emote-id="r59"]')).toBeNull();
      const rendered = rowIds(host);
      expect(tabStops(host)).toEqual([rendered[rendered.length - 1]]);

      // Back down: the chosen row is rendered again and is the stop once more.
      lastViewport!.scrollToIndex(61);
      await flush();
      expect(tabStops(host)).toEqual(['r59']);
    });

    it('does not move the toggled row out of its place in a long list', async () => {
      const host = open(many(60));
      await flush();
      box(host, 'r0').click();
      await flush();
      expect(box(host, 'r0').checked).toBe(false);
      expect(rowIds(host)[0]).toBe('r0');
      expect(summary(host)).toBe('59 werden entfernt, 1 bleibt');
    });
  });
});
