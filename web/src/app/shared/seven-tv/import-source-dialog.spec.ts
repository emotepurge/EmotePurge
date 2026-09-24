import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LanguageService } from '../../core/i18n/language.service';
import { ForeignEmoteSetResponse } from '../../core/seven-tv/foreign-emote-set.model';
import { SevenTvLeaderboardResponse } from '../../core/seven-tv/leaderboard.model';
import { FileImportStep } from './file-import-step';
import { ForeignChannelStep } from './foreign-channel-step';
import { LeaderboardStep } from './leaderboard-step';
import { ImportSourceDialog, ImportSourceDialogResult } from './import-source-dialog';

const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen' },
  restore: {
    import: {
      title: 'Datei importieren',
      sorts: {
        purgeRun: 'Purge-Protokoll (Wiederherstellen) als JSON',
        emoteList: 'Emote-Liste (Kopieren) als JSON',
        usageExport: 'Nutzungs-Export (Kopieren) als JSON',
      },
      fileLabel: 'Datei auswählen',
    },
  },
  import: {
    source: {
      title: 'Emotes importieren',
      legend: 'Woher kommen die Emotes?',
      back: 'Zurück',
      file: { label: 'Aus einer Datei', hint: 'Purge-Protokoll, Emote-Liste oder Nutzungs-Export' },
      channel: { label: 'Aus einem Kanal', hint: 'Das aktive 7TV-Set eines Twitch-Kanals' },
      leaderboard: {
        label: 'Aus 7TVs Bestenliste',
        hint: 'Die netzwerkweit vorn liegenden Emotes',
      },
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
    foreignChannel: {
      title: 'Aus einem Kanal importieren',
      channelLabel: 'Kanalname',
      placeholder: 'twitch-channel-name',
      invalidChannelName: 'Kein gültiger Twitch-Kanalname.',
      load: 'Set laden',
      reload: 'Neu laden',
      retry: 'Erneut versuchen',
      continue: 'Weiter',
      empty: 'Das aktive 7TV-Set dieses Kanals hat keine Emotes.',
      truncated: 'Nur ein Teil des Sets konnte geladen werden.',
      selectedCount: '{{ count }} ausgewählt',
      grid: { ariaLabel: 'Emote-Auswahl' },
      sort: {
        label: 'Sortieren nach',
        none: 'Set-Reihenfolge',
        topAllTime: 'Top',
        trending: 'Trend',
        scoreHint: 'Die Zahl sagt, in wie vielen 7TV-Sets das Emote steckt.',
      },
    },
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

const FOREIGN_SET: ForeignEmoteSetResponse = {
  channelName: 'handofblood',
  sevenTvUserId: 'user-1',
  emoteSetId: 'set-1',
  totalCount: 1,
  truncated: false,
  emotes: [
    {
      sevenTvEmoteId: 'e1',
      name: 'catJAM',
      defaultName: 'catJAM',
      imageUrl: 'https://cdn.7tv.app/e1/4x.webp',
      topAllTime: null,
      trending: null,
    },
  ],
};

const LEADERBOARD: SevenTvLeaderboardResponse = {
  sortBy: 'TRENDING_DAILY',
  totalCount: 705,
  truncated: false,
  emotes: [
    {
      sevenTvEmoteId: 'l1',
      name: 'Dance',
      defaultName: 'Dance',
      imageUrl: 'https://cdn.7tv.app/l1/4x.webp',
      topAllTime: 99,
      trending: 12,
    },
  ],
};

describe('ImportSourceDialog', () => {
  let fixture: ComponentFixture<ImportSourceDialog>;
  let host: HTMLElement;
  let httpMock: HttpTestingController;
  let closed: (ImportSourceDialogResult | undefined)[];
  let addPanelClass: ReturnType<typeof vi.fn>;
  let removePanelClass: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    closed = [];
    addPanelClass = vi.fn();
    removePanelClass = vi.fn();

    await TestBed.configureTestingModule({
      imports: [
        ImportSourceDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useValue: { channelName: 'somechannel', setId: 'set-current' } },
        {
          provide: DialogRef,
          useValue: {
            close: (result?: ImportSourceDialogResult) => closed.push(result),
            overlayRef: { addPanelClass, removePanelClass },
          },
        },
        {
          provide: LanguageService,
          useValue: { lang: signal('de') } as unknown as LanguageService,
        },
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ImportSourceDialog);
    host = fixture.nativeElement;
    fixture.detectChanges();
  });

  function button(label: string): HTMLButtonElement {
    const found = Array.from(host.querySelectorAll('button')).find(
      (candidate) => candidate.textContent?.trim() === label,
    );
    if (!found) {
      throw new Error(`no button labelled "${label}"`);
    }
    return found;
  }

  /** A source row of the first step. Its accessible name is label plus hint, deliberately — the
   *  same two-line label the export dialog's options use (§7.4). */
  function sourceOption(label: string): HTMLButtonElement {
    const found = Array.from(host.querySelectorAll('button')).find((candidate) =>
      candidate.textContent?.trim().startsWith(label),
    );
    if (!found) {
      throw new Error(`no source option labelled "${label}"`);
    }
    return found;
  }

  function heading(): string {
    return host.querySelector('h2')?.textContent?.trim() ?? '';
  }

  function goToChannelStep(): void {
    sourceOption('Aus einem Kanal').click();
    fixture.detectChanges();
  }

  function channelStep(): {
    channelNameControl: { setValue(value: string): void };
    submit(): void;
    onSelectionChange(rows: unknown[]): void;
  } {
    return fixture.debugElement.query(By.directive(ForeignChannelStep))
      .componentInstance as unknown as {
      channelNameControl: { setValue(value: string): void };
      submit(): void;
      onSelectionChange(rows: unknown[]): void;
    };
  }

  /** Answers "Set laden" with a set — the moment the grid appears. */
  function loadSet(): void {
    const internals = channelStep();
    internals.channelNameControl.setValue('handofblood');
    internals.submit();
    httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(FOREIGN_SET);
    fixture.detectChanges();
  }

  /** Marks one emote in the loaded grid — what unlocks "Weiter". */
  function markOneEmote(): void {
    channelStep().onSelectionChange(FOREIGN_SET.emotes);
    fixture.detectChanges();
  }

  /** Enters the leaderboard branch. Unlike the channel branch this one asks for its list straight
   *  away — there is nothing to type in first — so the request is part of entering. */
  function goToLeaderboardStep(): void {
    sourceOption('Aus 7TVs Bestenliste').click();
    fixture.detectChanges();
  }

  function answerLeaderboard(response: SevenTvLeaderboardResponse = LEADERBOARD): void {
    httpMock.expectOne((candidate) => candidate.url === '/api/seventv/leaderboard').flush(response);
    fixture.detectChanges();
  }

  function leaderboardStep(): { onSelectionChange(rows: unknown[]): void } {
    return fixture.debugElement.query(By.directive(LeaderboardStep))
      .componentInstance as unknown as { onSelectionChange(rows: unknown[]): void };
  }

  function actionRowLabels(): string[] {
    return Array.from(host.querySelectorAll('[dialog-actions]')).map(
      (candidate) => candidate.textContent?.trim() ?? '',
    );
  }

  describe('the source choice is the first step (spec E1)', () => {
    it('offers every source and starts none of them', () => {
      expect(heading()).toBe('Emotes importieren');
      expect(sourceOption('Aus einer Datei')).toBeTruthy();
      expect(sourceOption('Aus einem Kanal')).toBeTruthy();
      // Nothing of either branch is mounted yet — the choice is a choice, not a preview.
      expect(host.querySelector('input[type="file"]')).toBeNull();
      expect(host.querySelector('app-foreign-channel-step')).toBeNull();
    });

    it('names the chosen branch in the heading and can be walked back', () => {
      sourceOption('Aus einer Datei').click();
      fixture.detectChanges();
      expect(heading()).toBe('Datei importieren');
      expect(host.querySelector('input[type="file"]')).not.toBeNull();

      button('Zurück').click();
      fixture.detectChanges();
      expect(heading()).toBe('Emotes importieren');
      expect(host.querySelector('input[type="file"]')).toBeNull();
    });

    it('has no way back on the first step, since there is nowhere to go', () => {
      expect(
        Array.from(host.querySelectorAll('button')).some(
          (candidate) => candidate.textContent?.trim() === 'Zurück',
        ),
      ).toBe(false);
    });
  });

  describe('the pane widens for the grid and for nothing else', () => {
    it('stays narrow on every form state, including the channel step before a set has loaded', () => {
      // All three carry a form's worth of content and looked lost at 72rem. Entering the channel
      // branch is explicitly not enough — the grid is what needs the width.
      sourceOption('Aus einer Datei').click();
      fixture.detectChanges();
      expect(addPanelClass).not.toHaveBeenCalled();

      button('Zurück').click();
      fixture.detectChanges();
      goToChannelStep();

      expect(addPanelClass).not.toHaveBeenCalled();
    });

    it('widens when the set arrives and narrows again when it is gone', () => {
      goToChannelStep();
      loadSet();
      expect(addPanelClass).toHaveBeenCalledWith('app-dialog-panel-wide');

      // The one resize coincides with "Set laden"; going back takes the width with it.
      button('Zurück').click();
      fixture.detectChanges();

      expect(removePanelClass).toHaveBeenLastCalledWith('app-dialog-panel-wide');
    });
  });

  describe('focus follows the step (#147)', () => {
    // CDK autofocuses once, when the overlay opens, and never again for a swap inside it. Before
    // #147 the file dialog opened straight onto its own file button, so this held by accident; as
    // step two of one dialog it has to be arranged, and a mouse user would never notice it missing.
    it('puts the caret on the file control when the file branch is entered', async () => {
      sourceOption('Aus einer Datei').click();
      fixture.detectChanges();
      await fixture.whenStable();

      expect(document.activeElement).toBe(button('Datei auswählen'));
    });

    it('puts the caret in the channel field when the channel branch is entered', async () => {
      goToChannelStep();
      await fixture.whenStable();

      expect(document.activeElement).toBe(host.querySelector('input[type="text"]'));
    });

    it('puts the caret on the list chooser when the leaderboard branch is entered', async () => {
      // The integration proof of the step's own contract, and the ordering that actually occurs:
      // the dialog focuses right after the render that mounted the step, while its first list is
      // still in flight. A control disabled for that window would swallow the focus silently.
      goToLeaderboardStep();
      await fixture.whenStable();

      expect(document.activeElement).toBe(host.querySelector('select'));

      answerLeaderboard();
    });

    it('returns the caret to the source row it came from', async () => {
      goToChannelStep();
      await fixture.whenStable();

      button('Zurück').click();
      fixture.detectChanges();
      await fixture.whenStable();

      // The row that opened the branch, not the first one — "wrong branch, try the other" then
      // costs no tabbing, the same courtesy a menu button does when its submenu closes.
      expect(document.activeElement).toBe(sourceOption('Aus einem Kanal'));
    });

    it('leaves the caret where it is when the set arrives, since that control survives', async () => {
      // "Set laden" is above the switch and is not torn down by the load, so the caret stays on a
      // control that still means something (reload, or correct the name). Nothing to move.
      goToChannelStep();
      await fixture.whenStable();
      button('Set laden').focus();

      loadSet();

      expect(document.activeElement).toBe(button('Set laden'));
    });
  });

  describe('the action row follows the same state as the pane', () => {
    it('offers no second forward action while the step is still a form', () => {
      // "Set laden" already carries the step forward, at the field it acts on. A permanently
      // disabled "Weiter" beside it made one text input look like it needed four buttons.
      goToChannelStep();

      expect(actionRowLabels()).toEqual(['Abbrechen', 'Zurück']);
    });

    it('gains "Weiter" when the grid arrives, without disturbing what stood before it', () => {
      goToChannelStep();
      loadSet();

      // §7: cancel stays first, so the CDK's first-tabbable default keeps landing on it.
      expect(actionRowLabels()).toEqual(['Abbrechen', 'Zurück', 'Weiter']);
    });

    it('carries only the cancel way out on the source choice', () => {
      expect(actionRowLabels()).toEqual(['Abbrechen']);
    });
  });

  describe('closing contract', () => {
    it('closes with whatever the file step read, unchanged', () => {
      sourceOption('Aus einer Datei').click();
      fixture.detectChanges();

      const step = fixture.debugElement.query(By.directive(FileImportStep))
        .componentInstance as FileImportStep;
      step.picked.emit({ kind: 'restore', rows: [] });

      expect(closed).toEqual([{ kind: 'restore', rows: [] }]);
    });

    it('keeps "Weiter" locked until the channel step has something to carry forward', () => {
      goToChannelStep();
      loadSet();
      expect(button('Weiter').disabled).toBe(true);

      markOneEmote();

      expect(button('Weiter').disabled).toBe(false);
    });

    it('closes with the foreign source and its picked rows', () => {
      goToChannelStep();
      loadSet();
      markOneEmote();

      button('Weiter').click();

      expect(closed).toEqual([
        {
          kind: 'foreign',
          picked: {
            channelName: 'handofblood',
            sevenTvUserId: 'user-1',
            emoteSetId: 'set-1',
            rows: FOREIGN_SET.emotes,
          },
        },
      ]);
    });

    it('closes with the leaderboard source, its picked rows and the list they came off', () => {
      goToLeaderboardStep();
      answerLeaderboard();
      leaderboardStep().onSelectionChange(LEADERBOARD.emotes);
      fixture.detectChanges();

      button('Weiter').click();

      expect(closed).toEqual([
        { kind: 'leaderboard', picked: { sortBy: 'TRENDING_DAILY', rows: LEADERBOARD.emotes } },
      ]);
    });

    it('cancel closes with no result, from any step', () => {
      goToChannelStep();
      loadSet();

      button('Abbrechen').click();

      expect(closed).toEqual([undefined]);
    });
  });

  describe('the leaderboard is the third source (#148)', () => {
    it('offers it as a row of the same menu, not as a door of its own', () => {
      // Spec E1: every source is reached the same way. A third one is a third row.
      expect(sourceOption('Aus 7TVs Bestenliste')).toBeTruthy();
      expect(host.querySelector('app-leaderboard-step')).toBeNull();
    });

    it('names the branch in the heading and enters it without a form to fill in first', () => {
      goToLeaderboardStep();

      expect(heading()).toBe('Aus 7TVs Bestenliste importieren');
      expect(host.querySelector('app-leaderboard-step')).not.toBeNull();
      // Still narrow, and still no "Weiter": the list is what needs the width, and it is in flight.
      expect(addPanelClass).not.toHaveBeenCalled();
      expect(actionRowLabels()).toEqual(['Abbrechen', 'Zurück']);
    });

    it('widens the pane and grows a "Weiter" when the list arrives — the same state as the channel branch', () => {
      goToLeaderboardStep();
      answerLeaderboard();

      expect(addPanelClass).toHaveBeenCalledWith('app-dialog-panel-wide');
      expect(actionRowLabels()).toEqual(['Abbrechen', 'Zurück', 'Weiter']);
    });

    it('keeps "Weiter" locked until something is marked on the list', () => {
      goToLeaderboardStep();
      answerLeaderboard();
      expect(button('Weiter').disabled).toBe(true);

      leaderboardStep().onSelectionChange(LEADERBOARD.emotes);
      fixture.detectChanges();

      expect(button('Weiter').disabled).toBe(false);
    });
  });
});
