import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it } from 'vitest';

import { VoteSessionSummary } from '../../core/voting/vote-session.model';
import { CreateVoteSessionDialog, CreateVoteSessionDialogData } from './create-vote-session-dialog';

// Only the keys this dialog translates — same convention as import-confirm-dialog.spec.ts. Real
// German text, so an assertion reads as the sentence the user gets, not as a key.
const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen' },
  voting: {
    list: {
      titlePlaceholder: 'Titel',
      titleRequired: 'Titel wird benötigt.',
      audienceEveryone: 'Alle',
      audienceSubs: 'Nur Subs',
      audienceMods: 'Nur Mods',
      startCountingLabel: 'Nutzung zählen ab',
    },
    create: {
      dialogTitle: 'Abstimmung aus Auswahl erstellen',
      selectedCount: '{{ count }} Emotes ausgewählt',
      startPrefillHint: 'Vorbelegt mit dem Beginn des gewählten Zeitraums.',
      submit: 'Abstimmung erstellen',
      selectionShrunk: {
        one: 'Ein ausgewähltes Emote ist nicht mehr im Set und steht nicht mehr zur Wahl. {{ remaining }} bleiben auf dem Stimmzettel.',
        other:
          '{{ count }} ausgewählte Emotes sind nicht mehr im Set und stehen nicht mehr zur Wahl. {{ remaining }} bleiben auf dem Stimmzettel.',
      },
      selectionEmpty:
        'Keines der ausgewählten Emotes ist noch im Set — es gibt nichts mehr, das zur Abstimmung gestellt werden könnte.',
    },
  },
};

interface Harness {
  fixture: ComponentFixture<CreateVoteSessionDialog>;
  emoteIds: WritableSignal<readonly string[]>;
  detect(): void;
  text(): string;
  submitButton(): HTMLButtonElement;
  fillTitle(value: string): void;
}

describe('CreateVoteSessionDialog', () => {
  let dialogData: CreateVoteSessionDialogData;
  let httpMock: HttpTestingController;
  let closed: (VoteSessionSummary | undefined)[];

  beforeEach(async () => {
    closed = [];

    await TestBed.configureTestingModule({
      imports: [
        CreateVoteSessionDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // Resolved when the component is created, so a test may shape the data first — same pattern
        // as import-confirm-dialog.spec.ts.
        { provide: DIALOG_DATA, useFactory: () => dialogData },
        {
          provide: DialogRef,
          useValue: { close: (result?: VoteSessionSummary) => closed.push(result) },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  /**
   * One dialog per test, same reasoning as import-confirm-dialog.spec.ts: `DIALOG_DATA` is resolved
   * once per injector. `emoteIds` is a real signal — a `WritableSignal`, standing in for the host
   * page's `ListSelection.selectedKeys` — so a test can shrink it exactly the way a live reload would
   * while the dialog is open, without needing the whole `UsageStatsPage` mounted.
   */
  function render(
    initialIds: readonly string[],
    lockReasonKey?: WritableSignal<string | null>,
  ): Harness {
    const emoteIds = signal<readonly string[]>(initialIds);

    dialogData = {
      channelName: 'sensitron',
      emoteIds,
      usageFromDate: '2026-08-01',
      lockReasonKey,
    };

    const fixture = TestBed.createComponent(CreateVoteSessionDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    return {
      fixture,
      emoteIds,
      detect: () => fixture.detectChanges(),
      text: () => host.textContent ?? '',
      submitButton: () => {
        const found = Array.from(host.querySelectorAll('button')).find(
          (button) => button.textContent?.trim() === 'Abstimmung erstellen',
        );
        if (!found) {
          throw new Error('no submit button');
        }
        return found;
      },
      fillTitle: (value) => {
        const input = host.querySelector<HTMLInputElement>('#create-vote-session-title-input');
        if (!input) {
          throw new Error('no title input');
        }
        input.value = value;
        input.dispatchEvent(new Event('input'));
        fixture.detectChanges();
      },
    };
  }

  describe('submit sends the current live list, not a snapshot (#132)', () => {
    it('sends exactly the ids the signal currently holds', () => {
      const dialog = render(['a', 'b', 'c']);
      dialog.fillTitle('Test session');

      dialog.submitButton().click();

      const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions');
      expect(req.request.body.emoteIds).toEqual(['a', 'b', 'c']);
      req.flush({
        id: 1,
        title: 'Test session',
        allowedVoterRoles: 1,
        isActive: true,
        startedAt: '2026-08-01T00:00:00Z',
        endedAt: null,
        emoteCount: 3,
        hideResultsUntilEnd: false,
      });
      expect(closed).toHaveLength(1);
    });

    it('sends the SHRUNK list on submit after a live reload dropped one id, not the array captured at open time', () => {
      const dialog = render(['a', 'b', 'c']);

      // The host page's selection shrinks while the dialog is open — a silent usage.flushed/
      // channel.synced reload archived 'b' from outside the tab.
      dialog.emoteIds.set(['a', 'c']);
      dialog.detect();

      dialog.fillTitle('Test session');
      dialog.submitButton().click();

      const req = httpMock.expectOne('/api/channels/sensitron/vote-sessions');
      expect(req.request.body.emoteIds).toEqual(['a', 'c']);
    });

    it('a retry after the server rejects the stale ballot sends whatever the live list looks like by then', () => {
      // Regression for the exact defect #132 reports: emoteIds used to be frozen at open time, so a
      // retry after a 400 resent the identical, already-rejected array.
      const dialog = render(['a', 'b']);
      dialog.fillTitle('Test session');

      dialog.submitButton().click();
      httpMock
        .expectOne('/api/channels/sensitron/vote-sessions')
        .flush({ code: 'emote_ids_invalid' }, { status: 400, statusText: 'Bad Request' });

      // The reload that caused the rejection lands between the failed attempt and the retry.
      dialog.emoteIds.set(['a']);
      dialog.detect();

      dialog.submitButton().click();
      expect(
        httpMock.expectOne('/api/channels/sensitron/vote-sessions').request.body.emoteIds,
      ).toEqual(['a']);
    });
  });

  describe('notice on shrink (#132)', () => {
    it('shows no notice while the live list still matches what the dialog opened with', () => {
      const dialog = render(['a', 'b']);

      expect(dialog.text()).not.toContain('nicht mehr im Set');
    });

    it('names the removed count and the remaining count once the live list shrinks', () => {
      const dialog = render(['a', 'b', 'c']);

      dialog.emoteIds.set(['a']);
      dialog.detect();

      expect(dialog.text()).toContain(
        '2 ausgewählte Emotes sind nicht mehr im Set und stehen nicht mehr zur Wahl. 1 bleiben auf dem Stimmzettel.',
      );
    });

    it('picks the singular sibling key for exactly one removed id', () => {
      const dialog = render(['a', 'b']);

      dialog.emoteIds.set(['a']);
      dialog.detect();

      expect(dialog.text()).toContain(
        'Ein ausgewähltes Emote ist nicht mehr im Set und steht nicht mehr zur Wahl. 1 bleiben auf dem Stimmzettel.',
      );
    });

    it('mounts the notice as a permanently present sr-only live region, filled only once there is something to say', () => {
      const dialog = render(['a', 'b']);

      const region = dialog.fixture.nativeElement.querySelector('span[role="status"]');
      expect(region).not.toBeNull();
      expect(region.textContent.trim()).toBe('');

      dialog.emoteIds.set(['a']);
      dialog.detect();

      // Same element, not a region that appeared together with its content — see docs/
      // UI-Designsprache.md §4.5 bullet 4 / §4.4's addendum for why that distinction matters for
      // screen reader announcement.
      const regionAfter = dialog.fixture.nativeElement.querySelector('span[role="status"]');
      expect(regionAfter).toBe(region);
      expect(regionAfter.textContent.trim().length).toBeGreaterThan(0);
    });

    it('keeps the visible banner aria-hidden so the message is not announced twice', () => {
      const dialog = render(['a', 'b']);
      dialog.emoteIds.set(['a']);
      dialog.detect();

      const banner = dialog.fixture.nativeElement.querySelector('app-notice-banner');
      expect(banner).not.toBeNull();
      expect(banner.getAttribute('aria-hidden')).toBe('true');
    });

    it('switches to the dedicated empty-ballot wording once every originally selected emote is gone', () => {
      const dialog = render(['a', 'b']);

      dialog.emoteIds.set([]);
      dialog.detect();

      expect(dialog.text()).toContain(
        'Keines der ausgewählten Emotes ist noch im Set — es gibt nichts mehr, das zur Abstimmung gestellt werden könnte.',
      );
      expect(dialog.text()).not.toContain('bleiben auf dem Stimmzettel');
    });
  });

  describe('submit disabled at zero (#132)', () => {
    it('is enabled while the live list still has entries', () => {
      const dialog = render(['a']);

      expect(dialog.submitButton().disabled).toBe(false);
    });

    it('disables once the live list drops to zero, and does not submit if clicked anyway', () => {
      const dialog = render(['a']);
      dialog.fillTitle('Test session');

      dialog.emoteIds.set([]);
      dialog.detect();

      expect(dialog.submitButton().disabled).toBe(true);

      dialog.submitButton().click();
      httpMock.expectNone('/api/channels/sensitron/vote-sessions');
    });

    it('connects the submit button to a text explanation once blocked, and drops it again once re-enabled (docs/UI-Designsprache.md §7)', () => {
      const dialog = render(['a']);

      dialog.emoteIds.set([]);
      dialog.detect();

      const button = dialog.submitButton();
      const describedById = button.getAttribute('aria-describedby');
      expect(describedById).toBeTruthy();
      const hint = dialog.fixture.nativeElement.querySelector(`#${describedById}`);
      expect(hint).not.toBeNull();
      expect(hint.textContent.trim().length).toBeGreaterThan(0);

      dialog.emoteIds.set(['a']);
      dialog.detect();

      expect(button.getAttribute('aria-describedby')).toBeNull();
    });

    it('re-enables if the live list gains entries back before submit (e.g. the page reloaded again)', () => {
      const dialog = render(['a']);
      dialog.emoteIds.set([]);
      dialog.detect();
      expect(dialog.submitButton().disabled).toBe(true);

      dialog.emoteIds.set(['a', 'b']);
      dialog.detect();

      expect(dialog.submitButton().disabled).toBe(false);
    });
  });

  describe("the host's live lock (#200, K4)", () => {
    it('blocks the submit with the host reason once the host locks behind the open dialog, and does not submit if clicked anyway', () => {
      const lock = signal<string | null>(null);
      const dialog = render(['a'], lock);
      dialog.fillTitle('Test session');
      expect(dialog.submitButton().disabled).toBe(false);

      // A set switch lands behind the open dialog.
      lock.set('usageStats.setView.lock.switching');
      dialog.detect();

      const button = dialog.submitButton();
      expect(button.disabled).toBe(true);
      const hint = dialog.fixture.nativeElement.querySelector(
        `#${button.getAttribute('aria-describedby')}`,
      );
      expect(hint?.textContent).toContain('usageStats.setView.lock.switching');

      button.click();
      dialog.fixture.componentInstance['create']();
      httpMock.expectNone('/api/channels/sensitron/vote-sessions');
    });

    it('submits again once the host lock lifts', () => {
      const lock = signal<string | null>('usageStats.setView.lock.switching');
      const dialog = render(['a'], lock);
      dialog.fillTitle('Test session');
      expect(dialog.submitButton().disabled).toBe(true);

      lock.set(null);
      dialog.detect();
      dialog.submitButton().click();

      httpMock.expectOne('/api/channels/sensitron/vote-sessions');
    });
  });

  it('closes empty-handed on cancel, regardless of the live list', () => {
    const dialog = render(['a']);

    const cancelButton = Array.from(
      dialog.fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.trim() === 'Abbrechen');
    cancelButton?.click();

    expect(closed).toEqual([undefined]);
  });
});
