import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

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
    confirmSetNotActive: 'Dieses Set ist gerade nicht aktiv.',
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
      summary: '{{removeCount}} werden entfernt, {{keepCount}} bleiben',
      proposedHeading: 'Wird entfernt',
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
      isActiveSet: true,
      proposal: { rows, notInSetCount: 0, snapshot: [], ownInLiveIds: [] },
      warning,
      warningLoading,
      ...over,
    };
    ref = openTagRemovalConfirmDialog(TestBed.inject(Dialog), data);
    ref.closed.subscribe((r) => results.push(r));
    const el = ref.componentInstance as unknown as object;
    expect(el).toBeTruthy();
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
    expect(pane).toBeTruthy();
    const labelled = document.getElementById(pane.getAttribute('aria-labelledby')!);
    expect(labelled?.textContent?.trim()).toBe('Stronghold ausräumen');
    expect(host).toBeTruthy();
  });

  it('keeps the summary in step with the ticks', async () => {
    const host = open([row('a'), row('b')]);
    await settle();
    expect(summary(host)).toBe('2 werden entfernt, 0 bleiben');

    box(host, 'a').click();
    await settle();
    expect(summary(host)).toBe('1 werden entfernt, 1 bleiben');

    box(host, 'a').click();
    await settle();
    expect(summary(host)).toBe('2 werden entfernt, 0 bleiben');
  });

  it('lists ticked rows before the not-proposed ones, and moves a row when it is toggled', async () => {
    const host = open([
      row('held', { checked: false, reason: 'heldBy', heldBy: [{ id: 2, name: 'Raid' }] }),
      row('own'),
    ]);
    await settle();
    expect(rowIds(host)).toEqual(['own', 'held']);

    box(host, 'own').click();
    await settle();
    expect(rowIds(host)).toEqual(['held', 'own']);

    box(host, 'own').click();
    await settle();
    box(host, 'held').click();
    await settle();
    expect(rowIds(host)).toEqual(['held', 'own']);
    expect(box(host, 'held').checked).toBe(true);
  });

  it('says why a row is not proposed', async () => {
    const host = open([
      row('held', {
        checked: false,
        reason: 'heldBy',
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
  });

  it('shows the placement date of a ticked row', async () => {
    const host = open([row('a')]);
    await settle();
    expect(host.textContent).toContain('eingespielt am');
  });

  it('keeps the confirm button active at n = 0 and says nothing is deleted', async () => {
    const host = open([row('held', { checked: false, reason: 'alreadyPresent' })]);
    await settle();
    expect(summary(host)).toBe('0 werden entfernt, 1 bleiben');
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
    const host = open([row('a')]);
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
    expect(host).toBeTruthy();
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
});
