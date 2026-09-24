import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  EmoteSetTargetAccount,
  EmoteSetTargetsResponse,
} from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import {
  ImportTargetChoice,
  ImportTargetDialog,
  ImportTargetDialogData,
} from './import-target-dialog';

// Only the keys this dialog translates — not the full app translation file. Texts are the real
// German ones (from web/public/i18n/de.json), so an assertion reads as the sentence the user gets.
const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen', loading: 'Lädt …' },
  export: {
    scopeLabel: 'Exportumfang',
    scopeVisible: 'Gefilterte Liste ({{count}})',
    scopeSelection: 'Auswahl ({{count}})',
    scopeNoSelectionHint: 'Ohne Auswahl im Raster gilt die ganze sichtbare Liste.',
  },
  import: {
    target: {
      title: 'Emotes übertragen',
      label: 'Ziel',
      active: 'aktiv',
      untracked: 'nicht getrackt',
      kindUnavailable: 'kein Zielset',
      isSource: 'das ist die Quelle',
      setsUnavailable: 'Sets nicht lesbar',
      noUsableSets: 'Kein nutzbares Set',
      none: 'Kein Set gefunden, in das kopiert werden kann.',
      listIncomplete:
        'Die Angebotsliste ist gerade unvollständig — fehlende Ziele erscheinen nach einem erneuten Laden.',
      loadFailed: 'Die Angebotsliste konnte nicht geladen werden.',
      retry: 'Erneut laden',
      submit: 'Weiter',
      confirmUntracked:
        "Ziel ist das Set ‚{{setName}}' von ‚{{ownerDisplayName}}' — dieses Konto trackt EmotePurge nicht.",
      unknownOwner: 'Besitzer unbekannt',
      confirmUntrackedAccept: 'Ja, dieses Set',
      confirmUntrackedReject: 'Anderes Set wählen',
    },
  },
};

const CANCEL = 'Abbrechen';
const SUBMIT = 'Weiter';
const RETRY = 'Erneut laden';
const CONFIRM_ACCEPT = 'Ja, dieses Set';
const CONFIRM_REJECT = 'Anderes Set wählen';
const SOURCE_SET_ID = 'set-source';

function targetsResult(overrides: Partial<EmoteSetTargetsResponse> = {}): EmoteSetTargetsResponse {
  return {
    accounts: [],
    sevenTvUnavailable: false,
    ...overrides,
  };
}

function account(
  overrides: Partial<EmoteSetTargetAccount> & { twitchChannelId: string },
): EmoteSetTargetAccount {
  return {
    twitchLogin: 'someone',
    isOwnAccount: false,
    trackedChannelName: null,
    activeEmoteSetId: null,
    sets: [],
    setsUnavailable: false,
    sevenTvUserId: 'owner-1',
    ...overrides,
  };
}

function set(
  overrides: Partial<EmoteSetTargetAccount['sets'][number]> & { id: string },
): EmoteSetTargetAccount['sets'][number] {
  return {
    name: 'Main',
    capacity: 250,
    kind: 'NORMAL',
    isActive: false,
    isPersonal: false,
    ownerDisplayName: 'SomeOwner',
    ownerSevenTvUserId: 'owner-1',
    // Every existing test below predates `editable` and expects every mocked set to be a valid
    // target — spec F9's "Default true" for the same reason `mockEmoteSetTargets` picks it.
    editable: true,
    ...overrides,
  };
}

function defaultData(overrides: Partial<ImportTargetDialogData> = {}): ImportTargetDialogData {
  return {
    currentChannelName: 'targetchannel',
    visibleCount: 10,
    selectionCount: 0,
    sourceEmoteSetId: SOURCE_SET_ID,
    ...overrides,
  };
}

interface Harness {
  fixture: ComponentFixture<ImportTargetDialog>;
  detect(): void;
  text(): string;
  button(label: string): HTMLButtonElement;
  hasButton(label: string): boolean;
  scopeInputs(): HTMLInputElement[];
  scopeInput(value: 'visible' | 'selection'): HTMLInputElement;
  /** Finds the radio whose label starts with the given set name (labels read "<name> (aktiv)" etc).
   *  Every account's heading is a plain, non-radio `<p>` since addendum 39 (#217) — this is the only
   *  way to reach a set's own radio, there is no separate "account header" radio anymore. */
  setInput(setName: string): HTMLInputElement | undefined;
  targetInputCount(): number;
  /** The target radiogroup itself, keyed on its `aria-label` (`import.target.label`) rather than
   *  just "any `[role=radiogroup]`" — the scope radiogroup above it (`export.scopeLabel`) is a
   *  second, unrelated one and must not be picked up here by accident. */
  targetRadiogroup(): Element | undefined;
  /** AK 35's confirmation banner — present only while an untracked pick is pending. None of these
   *  tests also trigger the load-failed/offer-incomplete banners, so "the one `app-notice-banner`
   *  in the DOM" is unambiguous; a future test that combines both would need a more specific query. */
  confirmationBanner(): HTMLElement | undefined;
  /** The banner's own reject button ("Anderes Set wählen", finding 7) — scoped to the banner rather
   *  than the generic {@link button} lookup purely for locality; unlike before finding 7's rewording
   *  it no longer shares a label with the dialog's own cancel button ("Abbrechen"), but scoping it
   *  to the banner still reads as "the banner's own reject action" at each call site. */
  confirmationCancelButton(): HTMLButtonElement | undefined;
}

describe('ImportTargetDialog', () => {
  let dialogData: ImportTargetDialogData;
  let closed: (ImportTargetChoice | undefined)[];
  /** One entry per `loadCachedEmoteSetTargets()` call — a fresh `Subject` each time, so a test can
   *  resolve a specific attempt (e.g. the first, superseded one stays open while the retry
   *  answers). */
  let listTargetsCalls: Subject<EmoteSetTargetsResponse>[];
  /** The `{ refresh }` option each call above was made with, same index as {@link listTargetsCalls}
   *  — what the reload-forces-refresh test (spec 6.2, F3) reads. */
  let listTargetsOptions: { refresh?: boolean }[];
  let loadCachedEmoteSetTargets: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    closed = [];
    listTargetsCalls = [];
    listTargetsOptions = [];
    loadCachedEmoteSetTargets = vi.fn((options: { refresh?: boolean } = {}) => {
      const subject = new Subject<EmoteSetTargetsResponse>();
      listTargetsCalls.push(subject);
      listTargetsOptions.push(options);
      return subject;
    });

    await TestBed.configureTestingModule({
      imports: [
        ImportTargetDialog,
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
            close: (result?: ImportTargetChoice) => closed.push(result),
          },
        },
        {
          provide: SevenTvEmoteSetService,
          useValue: { loadCachedEmoteSetTargets } as unknown as SevenTvEmoteSetService,
        },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  /**
   * One dialog per test, mirroring `import-confirm-dialog.spec.ts`: `DIALOG_DATA` is resolved once
   * per injector. `fixture.detectChanges()` flushes the `rxResource`'s initial effect, which is what
   * actually issues the `loadCachedEmoteSetTargets()` call — a test that never calls
   * `detect()`/resolves the subject inspects the dialog while it is still in the (only) loading
   * state.
   */
  function render(data: ImportTargetDialogData = defaultData()): Harness {
    dialogData = data;
    const fixture = TestBed.createComponent(ImportTargetDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    function labelStartingWith(prefix: string): HTMLLabelElement | undefined {
      return Array.from(host.querySelectorAll('label')).find((label) =>
        label.textContent?.trim().startsWith(prefix),
      );
    }

    return {
      fixture,
      detect: () => fixture.detectChanges(),
      text: () => host.textContent ?? '',
      button: (label) => {
        const found = buttons().find((button) => button.textContent?.trim() === label);
        if (!found) {
          throw new Error(`no button labelled "${label}"`);
        }
        return found;
      },
      hasButton: (label) => buttons().some((button) => button.textContent?.trim() === label),
      scopeInputs: () =>
        Array.from(host.querySelectorAll<HTMLInputElement>('input[name="import-scope"]')),
      scopeInput: (value) => {
        // Rendered in document order: visible first, selection second (§7.2).
        const inputs = Array.from(
          host.querySelectorAll<HTMLInputElement>('input[name="import-scope"]'),
        );
        const index = value === 'visible' ? 0 : 1;
        const input = inputs[index];
        if (!input) {
          throw new Error(`no scope radio for "${value}"`);
        }
        return input;
      },
      setInput: (setName) => labelStartingWith(setName)?.querySelector('input') ?? undefined,
      targetInputCount: () => host.querySelectorAll('input[name="import-target"]').length,
      targetRadiogroup: () =>
        Array.from(host.querySelectorAll('[role="radiogroup"]')).find(
          (el) => el.getAttribute('aria-label') === 'Ziel',
        ),
      confirmationBanner: () => host.querySelector<HTMLElement>('app-notice-banner') ?? undefined,
      confirmationCancelButton: () =>
        host.querySelector<HTMLElement>('app-notice-banner')?.querySelector('button') ?? undefined,
    };
  }

  /**
   * Resolves the `index`-th `loadCachedEmoteSetTargets()` call and flushes the resulting
   * re-render. `rxResource` settles the underlying `resource()` primitive through a promise (see
   * `@angular/core/rxjs-interop`'s `rxResource`), so the update only lands after a microtask —
   * `whenStable()` is what actually waits for that, `detectChanges()` alone is not enough.
   */
  async function resolve(
    dialog: Harness,
    index: number,
    result: EmoteSetTargetsResponse,
  ): Promise<void> {
    listTargetsCalls[index].next(result);
    listTargetsCalls[index].complete();
    await dialog.fixture.whenStable();
    dialog.detect();
  }

  /** Same microtask wait as {@link resolve}, for the error path. */
  async function fail(dialog: Harness, index: number, error: unknown): Promise<void> {
    listTargetsCalls[index].error(error);
    await dialog.fixture.whenStable();
    dialog.detect();
  }

  describe('scope default (R12 — deliberately the opposite of the export dialog)', () => {
    it('defaults to "selection" when a grid selection exists', async () => {
      const dialog = render(defaultData({ selectionCount: 3 }));
      await resolve(dialog, 0, targetsResult());

      expect(dialog.scopeInput('selection').checked).toBe(true);
      expect(dialog.scopeInput('visible').checked).toBe(false);
    });

    it('defaults to "visible" and omits the scope radiogroup entirely when there is no selection', async () => {
      const dialog = render(defaultData({ selectionCount: 0 }));
      await resolve(dialog, 0, targetsResult());

      expect(dialog.scopeInputs()).toHaveLength(0);
    });

    it('still submits the implicit "visible" scope when there was no radiogroup to change it', async () => {
      const dialog = render(defaultData({ selectionCount: 0 }));
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: 'set-a' })],
            }),
          ],
        }),
      );

      dialog.setInput('Main')?.click();
      dialog.detect();
      dialog.button(SUBMIT).click();

      expect(closed).toEqual([
        {
          scope: 'visible',
          emoteSetId: 'set-a',
          channelName: 'chan',
          ownerDisplayName: 'SomeOwner',
          setName: 'Main',
          isTracked: true,
          twitchLogin: 'someone',
          activeEmoteSetId: null,
        },
      ]);
    });

    it('omits the scope radiogroup entirely when a caller forces the scope, even with a selection', async () => {
      const dialog = render(defaultData({ selectionCount: 5, forcedScope: 'selection' }));
      await resolve(dialog, 0, targetsResult());

      expect(dialog.scopeInputs()).toHaveLength(0);
    });

    it('shows the no-selection hint in place of the radiogroup when selectionCount is 0', async () => {
      const dialog = render(defaultData({ selectionCount: 0 }));
      await resolve(dialog, 0, targetsResult());

      expect(dialog.text()).toContain('Ohne Auswahl im Raster gilt die ganze sichtbare Liste.');
      expect(dialog.scopeInputs()).toHaveLength(0);
    });

    it('does not show the no-selection hint once a selection exists', async () => {
      const dialog = render(defaultData({ selectionCount: 3 }));
      await resolve(dialog, 0, targetsResult());

      expect(dialog.text()).not.toContain('Ohne Auswahl im Raster gilt die ganze sichtbare Liste.');
    });
  });

  // Konzept "Auswahl überlebt Suche und Filter" nachtrag (2026-09-19): the same empty-scope guard
  // as ExportDialog's, for the push's own scope radiogroup.
  describe('empty-scope guard (nachtrag 2026-09-19)', () => {
    it('disables the "visible" radio when the visible list is empty but a selection survives it — "selection" is already the default (R12)', async () => {
      const dialog = render(defaultData({ visibleCount: 0, selectionCount: 5 }));
      await resolve(dialog, 0, targetsResult());

      expect(dialog.scopeInput('visible').disabled).toBe(true);
      expect(dialog.scopeInput('visible').checked).toBe(false);
      expect(dialog.scopeInput('selection').checked).toBe(true);
    });

    it('disables "Weiter" when the resolved scope has zero rows, even with a target chosen (defensive)', async () => {
      const dialog = render(defaultData({ visibleCount: 0, selectionCount: 0 }));
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: 'set-a' })],
            }),
          ],
        }),
      );

      dialog.setInput('Main')?.click();
      dialog.detect();

      expect(dialog.button(SUBMIT).disabled).toBe(true);
    });
  });

  describe('post-load notice — loadFailed beats offerIncomplete', () => {
    it('shows only the load-failed error with a retry action when the request itself errors', async () => {
      const dialog = render();
      await fail(dialog, 0, new HttpErrorResponse({ status: 500 }));

      expect(dialog.text()).toContain('Die Angebotsliste konnte nicht geladen werden.');
      expect(dialog.hasButton(RETRY)).toBe(true);
      expect(dialog.text()).not.toContain('Die Angebotsliste ist gerade unvollständig');
    });

    it('re-issues loadCachedEmoteSetTargets() through targetsResource.reload() when the retry button is clicked', async () => {
      const dialog = render();
      await fail(dialog, 0, new HttpErrorResponse({ status: 500 }));

      dialog.button(RETRY).click();
      dialog.detect();

      expect(loadCachedEmoteSetTargets).toHaveBeenCalledTimes(2);
    });

    // Spec 6.2/F3, T4 brief: "Der Picker liest dieselbe Kopie (sein `reload` erzwingt `refresh`)" —
    // the initial load may be a cache hit (another pre-check already warmed it this minute), but a
    // load the user explicitly asked to retry must never come back stale from that same cache.
    it('forces refresh:true on the retried load — the initial load does not', async () => {
      const dialog = render();
      await fail(dialog, 0, new HttpErrorResponse({ status: 500 }));

      expect(listTargetsOptions[0]).toEqual({ refresh: false });

      dialog.button(RETRY).click();
      dialog.detect();

      expect(listTargetsOptions[1]).toEqual({ refresh: true });
    });

    it('shows only the offer-incomplete info notice when sevenTvUnavailable is true on an otherwise-successful load', async () => {
      const dialog = render();
      await resolve(dialog, 0, targetsResult({ sevenTvUnavailable: true }));

      expect(dialog.text()).toContain('Die Angebotsliste ist gerade unvollständig');
      expect(dialog.text()).not.toContain('Die Angebotsliste konnte nicht geladen werden.');
    });

    it('shows no notice at all for a clean, complete result', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({ twitchChannelId: '1', trackedChannelName: 'chan', sets: [set({ id: 'a' })] }),
          ],
        }),
      );

      expect(dialog.text()).not.toContain('Die Angebotsliste konnte nicht geladen werden.');
      expect(dialog.text()).not.toContain('Die Angebotsliste ist gerade unvollständig');
    });

    // P2 fix, #217 review round: the account loop used to sit entirely inside `@if (hasAnySet())`,
    // so a single account with an empty (or PERSONAL-only) set list fell all the way through to the
    // list-wide "no set" placeholder instead of rendering its own heading and notice — the two are
    // now told apart by whether the accounts list itself is empty (see the two tests below), not by
    // whether any of it has a set.
    it('shows the account\'s own heading and "no usable set" notice, not the list-wide placeholder, for a single account with an empty set list', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [account({ twitchChannelId: '1', trackedChannelName: 'chan' })],
        }),
      );

      expect(dialog.text()).toContain('#chan');
      expect(dialog.text()).toContain('Kein nutzbares Set');
      expect(dialog.text()).not.toContain('Kein Set gefunden, in das kopiert werden kann.');
      expect(dialog.targetRadiogroup()).toBeUndefined();
    });

    it('shows the list-wide "no set" placeholder only when the accounts list itself is empty', async () => {
      const dialog = render();
      await resolve(dialog, 0, targetsResult({ accounts: [] }));

      expect(dialog.text()).toContain('Kein Set gefunden, in das kopiert werden kann.');
      expect(dialog.targetRadiogroup()).toBeUndefined();
    });

    it("shows a setsUnavailable-only account's heading and its own notice without a radiogroup role", async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              setsUnavailable: true,
              sets: [],
            }),
          ],
        }),
      );

      expect(dialog.text()).toContain('#chan');
      expect(dialog.text()).toContain('Sets nicht lesbar');
      expect(dialog.targetRadiogroup()).toBeUndefined();
    });
  });

  describe('grouping — tracked above untracked, classified by trackedChannelName alone (AK 34)', () => {
    it('renders tracked accounts before untracked ones regardless of API order', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              twitchLogin: 'stranger',
              trackedChannelName: null,
              sets: [set({ id: 'set-u', name: 'StrangeSet' })],
            }),
            account({
              twitchChannelId: '2',
              trackedChannelName: 'brudivoeller',
              sets: [set({ id: 'set-t', name: 'TrackedSet' })],
            }),
          ],
        }),
      );

      const order = dialog
        .text()
        .split(/\s+/)
        .filter((token) => token.includes('Set'));
      expect(order.indexOf('TrackedSet')).toBeLessThan(order.indexOf('StrangeSet'));
      expect(dialog.text()).toContain('nicht getrackt');
    });

    it('labels an untracked account by its Twitch login, not a channel name', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              twitchLogin: 'stranger',
              trackedChannelName: null,
              sets: [set({ id: 'set-u' })],
            }),
          ],
        }),
      );

      expect(dialog.text()).toContain('stranger');
      expect(dialog.text()).toContain('nicht getrackt');
    });

    it('lists the current channel, disabling only its source set ("das ist die Quelle") — the account itself stays', async () => {
      const dialog = render(defaultData({ currentChannelName: 'handofblood' }));
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'handofblood',
              sets: [
                set({ id: SOURCE_SET_ID, name: 'Main', isActive: true }),
                set({ id: 'set-halloween', name: 'Halloween' }),
              ],
            }),
          ],
        }),
      );

      const sourceInput = dialog.setInput('Main');
      const otherInput = dialog.setInput('Halloween');
      expect(sourceInput?.disabled).toBe(true);
      expect(otherInput?.disabled).toBe(false);
      expect(dialog.text()).toContain('das ist die Quelle');
    });

    it('hides a PERSONAL set entirely — no radio at all, not even disabled (addendum 39, #217)', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [
                set({ id: 'set-p', name: 'Personal Emotes', kind: 'PERSONAL', isPersonal: true }),
                set({ id: 'set-n', name: 'Main', kind: 'NORMAL' }),
              ],
            }),
          ],
        }),
      );

      expect(dialog.setInput('Personal Emotes')).toBeUndefined();
      expect(dialog.setInput('Main')).toBeDefined();
    });

    it('shows a distinct "no usable set" notice for an account left with only a PERSONAL set — not the setsUnavailable wording', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [
                set({ id: 'set-p', name: 'Personal Emotes', kind: 'PERSONAL', isPersonal: true }),
              ],
            }),
          ],
        }),
      );

      expect(dialog.text()).toContain('Kein nutzbares Set');
      expect(dialog.text()).not.toContain('Sets nicht lesbar');
      expect(dialog.setInput('Personal Emotes')).toBeUndefined();
      expect(dialog.targetRadiogroup()).toBeUndefined();
    });

    it('disables a non-NORMAL set with the shared "kein Zielset" label — GLOBAL/SPECIAL', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: 'set-g', name: 'Global Emotes', kind: 'GLOBAL' })],
            }),
          ],
        }),
      );

      const input = dialog.setInput('Global Emotes');
      expect(input?.disabled).toBe(true);
      expect(dialog.text()).toContain('kein Zielset');
    });

    it('shows a visible reason for an account whose sets could not be read, instead of a silently empty flyout', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'handofblood',
              setsUnavailable: true,
              sets: [],
            }),
            account({
              twitchChannelId: '2',
              trackedChannelName: 'other',
              sets: [set({ id: 'x' })],
            }),
          ],
        }),
      );

      expect(dialog.text()).toContain('Sets nicht lesbar');
    });

    it('labels the active set "aktiv"', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: 'set-a', name: 'Main', isActive: true })],
            }),
          ],
        }),
      );

      expect(dialog.text()).toContain('aktiv');
    });
  });

  // P2 fix, #217 review round (Opus + Codex): since addendum 39 removed the merged account-header
  // radio, a set's own radio is the *only* thing naming an account any more — two accounts that
  // each happen to have an active set of the same name render two radios with the exact same
  // accessible name ("Main (aktiv)"), indistinguishable to a screen reader without this fix.
  describe("accessibility — a set radio's account is programmatically discoverable (P2 fix, #217 review round)", () => {
    it('associates each radio with its own account heading via aria-describedby, keeping two same-named active sets on different accounts distinguishable', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan1',
              sets: [set({ id: 'set-1', name: 'Main', isActive: true })],
            }),
            account({
              twitchChannelId: '2',
              trackedChannelName: 'chan2',
              sets: [set({ id: 'set-2', name: 'Main', isActive: true })],
            }),
          ],
        }),
      );

      const host: HTMLElement = dialog.fixture.nativeElement;
      const radios = Array.from(
        host.querySelectorAll<HTMLInputElement>('input[name="import-target"]'),
      );
      expect(radios).toHaveLength(2);

      function describedText(radio: HTMLInputElement): string {
        const id = radio.getAttribute('aria-describedby');
        expect(id).toBeTruthy();
        const heading = host.querySelector(`#${id}`);
        expect(heading).not.toBeNull();
        return heading?.textContent ?? '';
      }

      // Both radios share the exact same accessible name — the label text starts with it
      // unchanged, so a name-based lookup (getByRole('radio', { name: 'Main (aktiv)' }), same as
      // the e2e suite uses) still finds either one; only the description tells them apart.
      for (const radio of radios) {
        expect(radio.closest('label')?.textContent?.trim().startsWith('Main (aktiv)')).toBe(true);
      }
      expect(describedText(radios[0])).toContain('chan1');
      expect(describedText(radios[0])).not.toContain('chan2');
      expect(describedText(radios[1])).toContain('chan2');
      expect(describedText(radios[1])).not.toContain('chan1');
      // Each account's id is unique — the two radios are never accidentally wired to the same
      // description.
      expect(new Set(radios.map((radio) => radio.getAttribute('aria-describedby'))).size).toBe(2);
    });
  });

  describe("preselection — the caller's own account's active set (finding 4)", () => {
    it("preselects the active, selectable set of the caller's own account once the data loads", async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              isOwnAccount: true,
              sets: [set({ id: 'set-a', name: 'Main', isActive: true })],
            }),
          ],
        }),
      );

      // 'Main' is the account's active, selectable set — its own radio row (there is no separate
      // account-header radio since addendum 39, #217).
      expect(dialog.setInput('Main')?.checked).toBe(true);
      expect(dialog.button(SUBMIT).disabled).toBe(false);
    });

    // Finding 4 (Live-Verifikation K2 2026-09-21): a first draft walked the tracked list in order
    // and preselected the first account with a selectable active set — which fell straight through
    // to a *different* tracked account's active set the moment the caller's own one was disabled
    // (observed live as an unrelated, merely moderated channel getting preselected). The fix looks
    // up the account by isOwnAccount specifically, never by list position, so a disabled own set now
    // falls through to "nothing preselected" instead.
    it("falls through to nothing when the caller's own active set is disabled (the source) — never to a different tracked account's active set", async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'own-channel',
              isOwnAccount: true,
              activeEmoteSetId: SOURCE_SET_ID,
              sets: [set({ id: SOURCE_SET_ID, name: 'Main', isActive: true })],
            }),
            account({
              twitchChannelId: '2',
              trackedChannelName: 'moderated-channel',
              isOwnAccount: false,
              activeEmoteSetId: 'set-mod',
              sets: [set({ id: 'set-mod', name: 'ModMain', isActive: true })],
            }),
          ],
        }),
      );

      expect(dialog.setInput('ModMain')?.checked).toBe(false);
      expect(dialog.setInput('Main')?.checked).toBe(false);
      expect(dialog.button(SUBMIT).disabled).toBe(true);
    });

    it('does not preselect the source set even when it is the own account`s active set', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              isOwnAccount: true,
              sets: [set({ id: SOURCE_SET_ID, name: 'Main', isActive: true })],
            }),
          ],
        }),
      );

      expect(dialog.setInput('Main')?.checked).toBe(false);
      expect(dialog.button(SUBMIT).disabled).toBe(true);
    });

    it('does not preselect an untracked own account`s active set', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: null,
              isOwnAccount: true,
              sets: [set({ id: 'set-a', name: 'Main', isActive: true })],
            }),
          ],
        }),
      );

      expect(dialog.setInput('Main')?.checked).toBe(false);
      expect(dialog.button(SUBMIT).disabled).toBe(true);
    });

    it('does not preselect a non-own tracked account`s active set at all', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'moderated-channel',
              isOwnAccount: false,
              sets: [set({ id: 'set-a', name: 'Main', isActive: true })],
            }),
          ],
        }),
      );

      expect(dialog.setInput('Main')?.checked).toBe(false);
      expect(dialog.button(SUBMIT).disabled).toBe(true);
    });

    // The picker can be opened from any channel page — the preselection must land on the caller's
    // own account regardless, never on whichever channel the picker happened to be opened from.
    it("preselects the own channel's active set even when the picker was opened from a different channel", async () => {
      const dialog = render(defaultData({ currentChannelName: 'brudivoeller_tv' }));
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'sensitron',
              isOwnAccount: true,
              sets: [set({ id: 'set-a', name: 'Main', isActive: true })],
            }),
          ],
        }),
      );

      expect(dialog.setInput('Main')?.checked).toBe(true);
    });

    it('never overrides a manual pick the user already made', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              isOwnAccount: true,
              sets: [
                set({ id: 'set-a', name: 'Main', isActive: true }),
                set({ id: 'set-b', name: 'Halloween' }),
              ],
            }),
          ],
        }),
      );

      dialog.setInput('Halloween')?.click();
      dialog.detect();

      expect(dialog.setInput('Halloween')?.checked).toBe(true);
      // 'Main' is the account's active set, preselected by the load-time effect — the manual pick
      // above must not leave it checked either.
      expect(dialog.setInput('Main')?.checked).toBe(false);
    });

    it("a manual click on a second (non-own) tracked account's own active-set radio replaces the preselection", async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan1',
              isOwnAccount: true,
              // activeEmoteSetId matches the set marked isActive below — server-derived (E21),
              // never independent of it (T2.6 straightened out a predecessor fixture where the two
              // disagreed; see the T2.6 handover note).
              activeEmoteSetId: 'set-1',
              sets: [set({ id: 'set-1', name: 'Main1', isActive: true })],
            }),
            account({
              twitchChannelId: '2',
              trackedChannelName: 'chan2',
              isOwnAccount: false,
              activeEmoteSetId: 'set-2',
              sets: [
                set({ id: 'set-2', name: 'Main2', ownerDisplayName: 'Chan2Owner', isActive: true }),
              ],
            }),
          ],
        }),
      );

      // The load-time default already landed on the own account (chan1) — that half is covered by
      // the earlier tests in this describe block.
      expect(dialog.setInput('Main1')?.checked).toBe(true);

      // One click on chan2's own active-set radio is enough to land on it — every set, including a
      // second (non-own) account's active one, is just an ordinary radio in the group since
      // addendum 39 removed the account-header shortcut; isOwnAccount only gates the automatic
      // load-time default, never what a manual click can reach.
      dialog.setInput('Main2')?.click();
      dialog.detect();

      expect(dialog.setInput('Main2')?.checked).toBe(true);
      expect(dialog.setInput('Main1')?.checked).toBe(false);
      dialog.button(SUBMIT).click();
      expect(closed).toEqual([
        {
          scope: 'visible',
          emoteSetId: 'set-2',
          channelName: 'chan2',
          ownerDisplayName: 'Chan2Owner',
          setName: 'Main2',
          isTracked: true,
          twitchLogin: 'someone',
          activeEmoteSetId: 'set-2',
        },
      ]);
    });

    it('keeps a manual pick after the offer list reloads (targetsResource.reload())', async () => {
      const dialog = render();
      const accounts = [
        account({
          twitchChannelId: '1',
          trackedChannelName: 'chan',
          sets: [
            set({ id: 'set-a', name: 'Main', isActive: true }),
            set({ id: 'set-b', name: 'Halloween' }),
          ],
        }),
      ];
      await resolve(dialog, 0, targetsResult({ accounts }));

      dialog.setInput('Halloween')?.click();
      dialog.detect();
      expect(dialog.setInput('Halloween')?.checked).toBe(true);

      // Bracket notation to reach the protected resource, same convention as
      // foreign-channel-step.spec.ts's component['...'] accesses — this simulates whatever future
      // affordance ends up calling reload() after a successful load (today only the load-failed
      // retry button does), to prove the guard itself, not a particular button.
      dialog.fixture.componentInstance['targetsResource'].reload();
      dialog.detect();
      await resolve(dialog, 1, targetsResult({ accounts }));

      expect(dialog.setInput('Halloween')?.checked).toBe(true);
      expect(dialog.setInput('Main')?.checked).toBe(false);
    });
  });

  describe('target selection', () => {
    it('starts with no set selected when no tracked account has a selectable active set', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: 'set-a', name: 'Halloween' })],
            }),
          ],
        }),
      );

      expect(dialog.setInput('Halloween')?.checked).toBe(false);
      expect(dialog.button(SUBMIT).disabled).toBe(true);
    });

    it('checks the chosen set once selected', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: 'set-a', name: 'Halloween' })],
            }),
          ],
        }),
      );

      dialog.setInput('Halloween')?.click();
      dialog.detect();

      expect(dialog.setInput('Halloween')?.checked).toBe(true);
    });

    it('cannot check a disabled set (the source, or a non-NORMAL kind)', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: SOURCE_SET_ID, name: 'Main' })],
            }),
          ],
        }),
      );

      expect(dialog.setInput('Main')?.disabled).toBe(true);
    });
  });

  describe('submit / cancel', () => {
    it('keeps "Weiter" disabled and refuses to close while no target is chosen', async () => {
      const dialog = render();
      await resolve(dialog, 0, targetsResult({ accounts: [account({ twitchChannelId: '1' })] }));

      const submit = dialog.button(SUBMIT);
      expect(submit.disabled).toBe(true);

      submit.click();
      expect(closed).toEqual([]);
    });

    it('closes with the chosen scope and a full ImportTargetChoice — tracked target', async () => {
      const dialog = render(defaultData({ selectionCount: 2 }));
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'somechannel',
              sets: [set({ id: 'set-a', name: 'Main', ownerDisplayName: 'SomeChannel' })],
            }),
          ],
        }),
      );

      dialog.setInput('Main')?.click();
      dialog.detect();
      dialog.button(SUBMIT).click();

      expect(closed).toEqual([
        {
          scope: 'selection',
          emoteSetId: 'set-a',
          channelName: 'somechannel',
          ownerDisplayName: 'SomeChannel',
          setName: 'Main',
          isTracked: true,
          twitchLogin: 'someone',
          activeEmoteSetId: null,
        },
      ]);
    });

    it('closes empty-handed on cancel, even with a target already chosen', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: 'set-a', name: 'Halloween' })],
            }),
          ],
        }),
      );

      dialog.setInput('Halloween')?.click();
      dialog.detect();
      dialog.button(CANCEL).click();

      expect(closed).toEqual([undefined]);
    });
  });

  describe('untracked confirmation (AK 35)', () => {
    function untrackedTargetsResult(): EmoteSetTargetsResponse {
      return targetsResult({
        accounts: [
          account({
            twitchChannelId: '1',
            twitchLogin: 'stranger',
            trackedChannelName: null,
            sets: [set({ id: 'set-a', name: 'Halloween', ownerDisplayName: 'Stranger' })],
          }),
        ],
      });
    }

    it('opens a confirmation instead of selecting the set outright', async () => {
      const dialog = render();
      await resolve(dialog, 0, untrackedTargetsResult());

      expect(dialog.confirmationBanner()).toBeUndefined();

      dialog.setInput('Halloween')?.click();
      dialog.detect();

      expect(dialog.confirmationBanner()?.textContent).toContain(
        "Ziel ist das Set ‚Halloween' von ‚Stranger' — dieses Konto trackt EmotePurge nicht.",
      );
      // The radio shows the pending candidate (it is what the banner is asking to confirm), but
      // nothing is decided yet (AK 35: "ohne Bestätigung keine Wahl") — "Weiter" cannot be used to
      // slip past the banner, and the dialog has not closed with anything.
      expect(dialog.setInput('Halloween')?.checked).toBe(true);
      expect(dialog.button(SUBMIT).disabled).toBe(true);
      expect(closed).toEqual([]);
      // Finding 7: the banner's own buttons name what they actually do — confirm this target, or go
      // back to choosing — not a second, differently-labelled "copy" action next to "Weiter".
      expect(dialog.hasButton(CONFIRM_ACCEPT)).toBe(true);
      expect(dialog.hasButton(CONFIRM_REJECT)).toBe(true);
      expect(dialog.hasButton('Kopieren')).toBe(false);
    });

    // Codex round 3 P2: a selectable untracked NORMAL set with no owner.mainConnection carries
    // ownerDisplayName: null from the API (E7) — the banner must never render the empty quote
    // "von ''" that a raw interpolation of that null would produce.
    it('falls back to the account twitchLogin in the confirmation banner when ownerDisplayName is null', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              twitchLogin: 'stranger',
              trackedChannelName: null,
              sets: [set({ id: 'set-a', name: 'Halloween', ownerDisplayName: null })],
            }),
          ],
        }),
      );

      dialog.setInput('Halloween')?.click();
      dialog.detect();

      expect(dialog.confirmationBanner()?.textContent).toContain(
        "Ziel ist das Set ‚Halloween' von ‚stranger' — dieses Konto trackt EmotePurge nicht.",
      );
    });

    // The case spec 6.2 says should not happen (an empty login too) — a neutral, translated label
    // rather than a second empty quote.
    it('falls back to the neutral unknown-owner label when both ownerDisplayName and twitchLogin are absent', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              twitchLogin: '',
              trackedChannelName: null,
              sets: [set({ id: 'set-a', name: 'Halloween', ownerDisplayName: null })],
            }),
          ],
        }),
      );

      dialog.setInput('Halloween')?.click();
      dialog.detect();

      expect(dialog.confirmationBanner()?.textContent).toContain(
        "Ziel ist das Set ‚Halloween' von ‚Besitzer unbekannt' — dieses Konto trackt EmotePurge nicht.",
      );
    });

    it('leaves the previous choice unchanged when the confirmation is cancelled', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              isOwnAccount: true,
              activeEmoteSetId: 'set-tracked',
              sets: [set({ id: 'set-tracked', name: 'Main', isActive: true })],
            }),
            account({
              twitchChannelId: '2',
              twitchLogin: 'stranger',
              trackedChannelName: null,
              sets: [set({ id: 'set-a', name: 'Halloween', ownerDisplayName: 'Stranger' })],
            }),
          ],
        }),
      );

      // A tracked choice already stands (the load-time preselection of the caller's own account,
      // finding 4) — cancelling an
      // untracked confirmation attempt must leave it exactly as it is.
      expect(dialog.setInput('Main')?.checked).toBe(true);

      dialog.setInput('Halloween')?.click();
      dialog.detect();
      expect(dialog.confirmationBanner()).not.toBeUndefined();
      // The pending candidate takes over the radio group's visual state while its confirmation is
      // up — a native radio group can only ever show one checked item at a time regardless.
      expect(dialog.setInput('Main')?.checked).toBe(false);
      expect(dialog.setInput('Halloween')?.checked).toBe(true);

      dialog.confirmationCancelButton()?.click();
      dialog.detect();

      expect(dialog.confirmationBanner()).toBeUndefined();
      // Not "leert sie" — the previously standing tracked choice is still there, unmodified.
      expect(dialog.setInput('Main')?.checked).toBe(true);
      expect(dialog.setInput('Halloween')?.checked).toBe(false);
      expect(dialog.button(SUBMIT).disabled).toBe(false);

      dialog.button(SUBMIT).click();
      expect(closed).toEqual([
        {
          scope: 'visible',
          emoteSetId: 'set-tracked',
          channelName: 'chan',
          ownerDisplayName: 'SomeOwner',
          setName: 'Main',
          isTracked: true,
          twitchLogin: 'someone',
          activeEmoteSetId: 'set-tracked',
        },
      ]);
    });

    it('closes the whole picker with channelName: null once confirmed', async () => {
      const dialog = render();
      await resolve(dialog, 0, untrackedTargetsResult());

      dialog.setInput('Halloween')?.click();
      dialog.detect();
      dialog.button(CONFIRM_ACCEPT).click();
      dialog.detect();

      expect(closed).toEqual([
        {
          scope: 'visible',
          emoteSetId: 'set-a',
          channelName: null,
          ownerDisplayName: 'Stranger',
          setName: 'Halloween',
          isTracked: false,
          twitchLogin: 'stranger',
          activeEmoteSetId: null,
        },
      ]);
      // The whole dialog closed directly on confirmation — there is no separate "Weiter" click
      // for the untracked class (AK 35: "Bestätigung schließt den Picker").
      expect(dialog.confirmationBanner()).toBeUndefined();
    });

    it('never shows a confirmation for a tracked pick — the one-click path stays', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        targetsResult({
          accounts: [
            account({
              twitchChannelId: '1',
              trackedChannelName: 'chan',
              sets: [set({ id: 'set-a', name: 'Halloween' })],
            }),
          ],
        }),
      );

      dialog.setInput('Halloween')?.click();
      dialog.detect();

      expect(dialog.confirmationBanner()).toBeUndefined();
      expect(dialog.setInput('Halloween')?.checked).toBe(true);

      dialog.button(SUBMIT).click();
      expect(closed).toHaveLength(1);
    });
  });
});
