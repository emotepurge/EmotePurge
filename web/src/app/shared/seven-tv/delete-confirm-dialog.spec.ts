import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { DeleteConfirmDialog, DeleteConfirmDialogData } from './delete-confirm-dialog';

// Only the keys this dialog translates — not the full app translation file. Texts are the real
// German ones, so an assertion reads as the sentence the user gets rather than as a key.
const DE_TRANSLATIONS = {
  common: {
    cancel: 'Abbrechen',
  },
  massDelete: {
    confirmTitle: {
      one: '{{ count }} Emote von 7TV löschen?',
      other: '{{ count }} Emotes von 7TV löschen?',
    },
    startDelete: 'Löschen starten',
    checkingSharedSets: 'Prüfe geteilte Sets…',
    sharedSetWarningTitle:
      'Achtung: Das aktive Emote-Set gehört möglicherweise nicht (nur) diesem Channel.',
    notOwnSet: 'Das aktive Set gehört nicht dem eigenen 7TV-Account dieses Channels.',
    knownAffected: 'Bei uns bekannt betroffen: {{ list }}',
    moderatedAffected: 'Von dir moderiert, ebenfalls betroffen: {{ list }}',
    ownershipCheckUnavailable:
      'Wir konnten gerade nicht prüfen, ob dieses Set wirklich diesem Channel gehört — bitte vor dem Löschen selbst auf 7tv.app kontrollieren.',
    irreversibleNotice:
      'Das kann nicht rückgängig gemacht werden. Löschen läuft danach automatisch nacheinander mit kurzer Verzögerung zwischen den Emotes.',
    undetectableChannelsNotice:
      'Hinweis: Fremde Channels, die weder von uns getrackt werden noch von dir moderiert werden, aber ebenfalls dieses Set nutzen, können wir grundsätzlich nicht erkennen.',
    hiddenByFilter: {
      one: '1 davon ist durch den aktuellen Filter ausgeblendet.',
      other: '{{count}} davon sind durch den aktuellen Filter ausgeblendet.',
    },
  },
};

const CANCEL = 'Abbrechen';
const START_DELETE = 'Löschen starten';

/** The check ran and found nothing worth flagging — the quiet case. */
const OWN_SET: EmoteSetWarning = {
  available: true,
  isOwnSet: true,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

/** The check ran and found the active set is not the channel's own. */
const FOREIGN_SET: EmoteSetWarning = {
  ...OWN_SET,
  isOwnSet: false,
};

/** What the check falls back to when it fails outright, shaped so that reading `isOwnSet` or the
 *  sharing lists as evidence would look like the worst possible finding. Regression fixture for
 *  the bug the issue describes: `available: false` means "the check failed", not "confirmed
 *  foreign" — this must render only the amber "could not check" notice, never the red one, even
 *  though every other field here looks alarming. */
const CHECK_FAILED_LOOKS_ALARMING: EmoteSetWarning = {
  available: false,
  isOwnSet: false,
  otherTrackedChannelsSharingSet: ['tracked1'],
  otherModeratedChannelsSharingSet: ['modded1'],
};

interface RenderOptions {
  warning?: EmoteSetWarning | null;
  warningLoading?: boolean;
  hiddenEmotes?: string[];
}

interface Harness {
  fixture: ComponentFixture<DeleteConfirmDialog>;
  warning: WritableSignal<EmoteSetWarning | null>;
  warningLoading: WritableSignal<boolean>;
  hiddenEmotes: WritableSignal<string[]>;
  detect(): void;
  text(): string;
  button(label: string): HTMLButtonElement;
  element(id: string): HTMLElement | null;
  /** Resolves an `aria-describedby` reference against the rendered DOM rather than comparing id
   *  strings, so a test proves the button points at an actual node with the right content. */
  describedBy(button: HTMLButtonElement): HTMLElement | null;
  roleElements(role: 'alert' | 'status'): HTMLElement[];
}

describe('DeleteConfirmDialog', () => {
  let dialogData: DeleteConfirmDialogData;
  let closed: boolean[];

  beforeEach(async () => {
    closed = [];

    await TestBed.configureTestingModule({
      imports: [
        DeleteConfirmDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        // Resolved when the component is created, so a test may shape the data first.
        { provide: DIALOG_DATA, useFactory: () => dialogData },
        {
          provide: DialogRef,
          useValue: {
            close: (result: boolean) => closed.push(result),
          } satisfies Pick<DialogRef<boolean>, 'close'>,
        },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  /**
   * One dialog per test: `DIALOG_DATA` is resolved once per injector, so a second `render()` in
   * the same test would silently hand the first test's data to the second component. Everything
   * that has to change while the dialog is up changes through the two signals in the returned
   * harness — which is also how the host panel feeds this dialog (see the type doc on
   * `DeleteConfirmDialogData`).
   */
  function render(options: RenderOptions = {}): Harness {
    const emotes = signal(['Kappa', 'PogU']);
    // Default only when the option is absent — `options.warning ?? OWN_SET` would also default an
    // explicit `warning: null`, which is exactly the pending state the host passes while the
    // ownership check is still running (mass-delete-panel.ts) and the confirm-lock tests below
    // exist to exercise.
    const warning = signal<EmoteSetWarning | null>(
      options.warning === undefined ? OWN_SET : options.warning,
    );
    const warningLoading = signal(options.warningLoading ?? false);
    const hiddenEmotes = signal(options.hiddenEmotes ?? []);

    dialogData = { emotes, hiddenEmotes, warning, warningLoading };

    const fixture = TestBed.createComponent(DeleteConfirmDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    return {
      fixture,
      warning,
      warningLoading,
      hiddenEmotes,
      detect: () => fixture.detectChanges(),
      text: () => host.textContent ?? '',
      button: (label) => {
        const found = buttons().find((button) => button.textContent?.trim() === label);
        if (!found) {
          throw new Error(`no button labelled "${label}"`);
        }
        return found;
      },
      element: (id) => host.querySelector<HTMLElement>(`#${id}`),
      describedBy: (button) => {
        const id = button.getAttribute('aria-describedby');
        return id ? host.querySelector<HTMLElement>(`#${id}`) : null;
      },
      roleElements: (role) => Array.from(host.querySelectorAll<HTMLElement>(`[role="${role}"]`)),
    };
  }

  describe('dialog outcome', () => {
    it('closes with true when the user confirms the run', () => {
      const dialog = render();

      dialog.button(START_DELETE).click();

      expect(closed).toEqual([true]);
    });

    it('closes with false on cancel', () => {
      const dialog = render();

      dialog.button(CANCEL).click();

      expect(closed).toEqual([false]);
    });
  });

  describe('confirm lock', () => {
    it('locks the confirm button while the shared-set check is still running, and points at the reason next to it', () => {
      const dialog = render({ warning: null, warningLoading: true });

      const confirm = dialog.button(START_DELETE);
      expect(confirm.disabled).toBe(true);

      const hint = dialog.describedBy(confirm);
      expect(hint).not.toBeNull();
      // Same node as the visible hint paragraph, not merely a matching id string.
      expect(hint).toBe(dialog.element('delete-confirm-hint'));
      expect(hint?.textContent).toContain('Prüfe geteilte Sets…');

      // Quiet otherwise: the check hasn't answered yet, so neither finding banner has an opinion.
      expect(dialog.roleElements('alert')).toHaveLength(0);
      expect(dialog.roleElements('status')).toHaveLength(0);
    });

    it('releases the lock once the check finishes, and drops the aria-describedby reference', () => {
      const dialog = render({ warning: null, warningLoading: true });
      expect(dialog.button(START_DELETE).disabled).toBe(true);

      dialog.warningLoading.set(false);
      dialog.warning.set(OWN_SET);
      dialog.detect();

      const confirm = dialog.button(START_DELETE);
      expect(confirm.disabled).toBe(false);
      expect(confirm.getAttribute('aria-describedby')).toBeNull();
    });
  });

  describe('shared-set finding (issue #89): "check failed" vs. "confirmed foreign"', () => {
    it('stays quiet when the check ran and found an unshared own set', () => {
      const dialog = render({ warning: OWN_SET });

      expect(dialog.roleElements('alert')).toHaveLength(0);
      expect(dialog.roleElements('status')).toHaveLength(0);
    });

    it("raises a red, alert-role finding when the set is not the channel's own", () => {
      const dialog = render({ warning: FOREIGN_SET });

      const alerts = dialog.roleElements('alert');
      expect(alerts).toHaveLength(1);
      expect(alerts[0].textContent).toContain('Achtung: Das aktive Emote-Set');
      expect(alerts[0].textContent).toContain(
        'Das aktive Set gehört nicht dem eigenen 7TV-Account',
      );
      // Red and amber exclude each other.
      expect(dialog.roleElements('status')).toHaveLength(0);
    });

    it('raises a red finding for channels EmotePurge tracks sharing the set, even when the set is the own', () => {
      const dialog = render({
        warning: { ...OWN_SET, otherTrackedChannelsSharingSet: ['tracked1', 'tracked2'] },
      });

      const alerts = dialog.roleElements('alert');
      expect(alerts).toHaveLength(1);
      expect(alerts[0].textContent).toContain('Bei uns bekannt betroffen: tracked1, tracked2');
      // The set is the channel's own, so that line stays out of the banner.
      expect(alerts[0].textContent).not.toContain(
        'Das aktive Set gehört nicht dem eigenen 7TV-Account',
      );
      expect(dialog.roleElements('status')).toHaveLength(0);
    });

    it('raises a red finding for moderated channels sharing the set', () => {
      const dialog = render({
        warning: { ...OWN_SET, otherModeratedChannelsSharingSet: ['modded1'] },
      });

      const alerts = dialog.roleElements('alert');
      expect(alerts).toHaveLength(1);
      expect(alerts[0].textContent).toContain('Von dir moderiert, ebenfalls betroffen: modded1');
      expect(dialog.roleElements('status')).toHaveLength(0);
    });

    it('raises only the amber, status-role "could not check" finding when the check itself failed — never red, even though the fallback shape looks alarming', () => {
      const dialog = render({ warning: CHECK_FAILED_LOOKS_ALARMING });

      const statuses = dialog.roleElements('status');
      expect(statuses).toHaveLength(1);
      expect(statuses[0].textContent).toContain('Wir konnten gerade nicht prüfen');

      // `available: false` must not be read as "confirmed foreign": no red finding at all, even
      // though isOwnSet is false and both sharing lists are non-empty in this fixture.
      expect(dialog.roleElements('alert')).toHaveLength(0);
      expect(dialog.text()).not.toContain('Achtung: Das aktive Emote-Set');
      // And it blocks nothing — an unavailable check only downgrades the finding, per the dialog's
      // own comment ("Colour here means *this run is unusual*, nothing else").
      expect(dialog.button(START_DELETE).disabled).toBe(false);
    });
  });

  describe('hidden-by-filter block (Konzept "Auswahl überlebt Suche und Filter" 2.1)', () => {
    it('is absent when nothing is hidden', () => {
      const dialog = render();

      expect(dialog.text()).not.toContain('durch den aktuellen Filter ausgeblendet');
    });

    it('names every hidden target once something is hidden', () => {
      const dialog = render({ hiddenEmotes: ['Foo', 'Bar', 'Baz'] });

      expect(dialog.text()).toContain('3 davon sind durch den aktuellen Filter ausgeblendet.');
      // The three names are listed next to the notice, not merely counted.
      expect(dialog.text()).toContain('Foo');
      expect(dialog.text()).toContain('Bar');
      expect(dialog.text()).toContain('Baz');
    });

    it('uses the singular wording for exactly one hidden target', () => {
      const dialog = render({ hiddenEmotes: ['Foo'] });

      expect(dialog.text()).toContain('1 davon ist durch den aktuellen Filter ausgeblendet.');
    });

    /**
     * The block is created with the dialog, which moves focus into itself and is read out whole on
     * open — a live region there could never announce its own arrival (docs/UI-Designsprache.md
     * §4.5) and would only add a second status region next to the amber "could not check" banner.
     * The remaining `role="status"` in this dialog therefore belongs to that banner alone.
     */
    it('is a plain paragraph, not a live region — the amber finding stays the only status region', () => {
      const dialog = render({
        hiddenEmotes: ['Foo'],
        warning: CHECK_FAILED_LOOKS_ALARMING,
      });

      const statuses = dialog.roleElements('status');
      expect(statuses).toHaveLength(1);
      expect(statuses[0].textContent).toContain('Wir konnten gerade nicht prüfen');
      expect(statuses[0].textContent).not.toContain('durch den aktuellen Filter ausgeblendet');
    });

    it('counts the title over every marked emote, hidden ones included', () => {
      const dialog = render({ hiddenEmotes: ['Foo', 'Bar'] });

      // render()'s default visible fixture is ['Kappa', 'PogU'] (2) + 2 hidden = 4.
      expect(dialog.text()).toContain('4 Emotes von 7TV löschen?');
    });
  });
});
