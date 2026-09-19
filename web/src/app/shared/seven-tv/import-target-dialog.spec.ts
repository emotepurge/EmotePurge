import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { ChannelService } from '../../core/channels/channel.service';
import { MyChannelDto, MyChannelsResult } from '../../core/channels/channel.model';
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
      label: 'Zielkanal',
      notTracked: 'Kanal muss erst beitreten',
      none: 'Kein weiterer Kanal, in dem du Broadcaster oder 7TV-Editor bist.',
      listIncomplete:
        'Die Kanalliste ist gerade unvollständig — fehlende Kanäle erscheinen nach einem erneuten Laden.',
      reauthRequired:
        'Deine Twitch-Anmeldung ist abgelaufen oder wurde widerrufen — Zielkanäle gibt es erst nach einem neuen Login.',
      loadFailed: 'Die Kanalliste konnte nicht geladen werden.',
      retry: 'Erneut laden',
      submit: 'Weiter',
    },
  },
};

const CANCEL = 'Abbrechen';
const SUBMIT = 'Weiter';
const RETRY = 'Erneut laden';

function channelsResult(overrides: Partial<MyChannelsResult> = {}): MyChannelsResult {
  return {
    helixUnavailable: false,
    reauthRequired: false,
    sevenTvUnavailable: false,
    channels: [],
    livePolledAtUtc: null,
    ...overrides,
  };
}

function channel(overrides: Partial<MyChannelDto> = {}): MyChannelDto {
  return {
    channelName: 'chan',
    isBroadcaster: false,
    isModerator: false,
    isSevenTvEditor: false,
    isTracked: true,
    isBotActive: false,
    liveState: 'unknown',
    ...overrides,
  };
}

function defaultData(overrides: Partial<ImportTargetDialogData> = {}): ImportTargetDialogData {
  return {
    currentChannelName: 'targetchannel',
    visibleCount: 10,
    selectionCount: 0,
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
  channelInput(name: string): HTMLInputElement | undefined;
  targetInputCount(): number;
  /** The target radiogroup itself, keyed on its `aria-label` (`import.target.label`) rather than
   *  just "any `[role=radiogroup]`" — the scope radiogroup above it (`export.scopeLabel`) is a
   *  second, unrelated one and must not be picked up here by accident. */
  targetRadiogroup(): Element | undefined;
}

describe('ImportTargetDialog', () => {
  let dialogData: ImportTargetDialogData;
  let closed: (ImportTargetChoice | undefined)[];
  /** One entry per `listMine()` call — a fresh `Subject` each time, so a test can resolve a
   *  specific attempt (e.g. the first, superseded one stays open while the retry answers). */
  let listMineCalls: Subject<MyChannelsResult>[];
  let listMine: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    closed = [];
    listMineCalls = [];
    listMine = vi.fn(() => {
      const subject = new Subject<MyChannelsResult>();
      listMineCalls.push(subject);
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
        { provide: ChannelService, useValue: { listMine } as unknown as ChannelService },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  /**
   * One dialog per test, mirroring `import-confirm-dialog.spec.ts`: `DIALOG_DATA` is resolved once
   * per injector. `fixture.detectChanges()` flushes the `rxResource`'s initial effect, which is what
   * actually issues the `listMine()` call — a test that never calls `detect()`/resolves the subject
   * inspects the dialog while it is still in the (only) loading state.
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
        label.textContent
          ?.trim()
          .split(/\s+/)
          .slice(0, prefix.split(/\s+/).length)
          .join(' ')
          .startsWith(prefix),
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
      channelInput: (name) => labelStartingWith(`#${name}`)?.querySelector('input') ?? undefined,
      targetInputCount: () => host.querySelectorAll('input[name="import-target"]').length,
      targetRadiogroup: () =>
        Array.from(host.querySelectorAll('[role="radiogroup"]')).find(
          (el) => el.getAttribute('aria-label') === 'Zielkanal',
        ),
    };
  }

  /**
   * Resolves the `index`-th `listMine()` call and flushes the resulting re-render. `rxResource`
   * settles the underlying `resource()` primitive through a promise (see
   * `@angular/core/rxjs-interop`'s `rxResource`), so the update only lands after a microtask —
   * `whenStable()` is what actually waits for that, `detectChanges()` alone is not enough.
   */
  async function resolve(dialog: Harness, index: number, result: MyChannelsResult): Promise<void> {
    listMineCalls[index].next(result);
    listMineCalls[index].complete();
    await dialog.fixture.whenStable();
    dialog.detect();
  }

  /** Same microtask wait as {@link resolve}, for the error path. */
  async function fail(dialog: Harness, index: number, error: unknown): Promise<void> {
    listMineCalls[index].error(error);
    await dialog.fixture.whenStable();
    dialog.detect();
  }

  describe('scope default (R12 — deliberately the opposite of the export dialog)', () => {
    it('defaults to "selection" when a grid selection exists', async () => {
      const dialog = render(defaultData({ selectionCount: 3 }));
      await resolve(dialog, 0, channelsResult());

      expect(dialog.scopeInput('selection').checked).toBe(true);
      expect(dialog.scopeInput('visible').checked).toBe(false);
    });

    it('defaults to "visible" and omits the scope radiogroup entirely when there is no selection', async () => {
      const dialog = render(defaultData({ selectionCount: 0 }));
      await resolve(dialog, 0, channelsResult());

      expect(dialog.scopeInputs()).toHaveLength(0);
    });

    it('still submits the implicit "visible" scope when there was no radiogroup to change it', async () => {
      const dialog = render(defaultData({ selectionCount: 0 }));
      await resolve(dialog, 0, channelsResult({ channels: [channel({ isBroadcaster: true })] }));

      dialog.channelInput('chan')?.click();
      dialog.detect();
      dialog.button(SUBMIT).click();

      expect(closed).toEqual([{ scope: 'visible', channelName: 'chan' }]);
    });

    it('omits the scope radiogroup entirely when a caller forces the scope, even with a selection', async () => {
      const dialog = render(defaultData({ selectionCount: 5, forcedScope: 'selection' }));
      await resolve(dialog, 0, channelsResult());

      expect(dialog.scopeInputs()).toHaveLength(0);
    });

    // #144: the target dialog names no quantity at all once the radiogroup is hidden, so the
    // "auswählen, dann kopieren" wedge (§7.2) is otherwise invisible to a viewer who never selects.
    it('shows the no-selection hint in place of the radiogroup when selectionCount is 0', async () => {
      const dialog = render(defaultData({ selectionCount: 0 }));
      await resolve(dialog, 0, channelsResult());

      expect(dialog.text()).toContain('Ohne Auswahl im Raster gilt die ganze sichtbare Liste.');
      expect(dialog.scopeInputs()).toHaveLength(0);
    });

    it('does not show the no-selection hint once a selection exists', async () => {
      const dialog = render(defaultData({ selectionCount: 3 }));
      await resolve(dialog, 0, channelsResult());

      expect(dialog.text()).not.toContain('Ohne Auswahl im Raster gilt die ganze sichtbare Liste.');
    });

    // A forced scope is not an absent choice (design doc §8.7): the dock shortcut already decided
    // for the user, so neither the radiogroup nor the hint that explains its absence belongs here.
    it('does not show the no-selection hint when a caller forces the scope with no selection', async () => {
      const dialog = render(defaultData({ selectionCount: 0, forcedScope: 'selection' }));
      await resolve(dialog, 0, channelsResult());

      expect(dialog.text()).not.toContain('Ohne Auswahl im Raster gilt die ganze sichtbare Liste.');
    });

    it('submits exactly the forced scope, not whatever selectionCount would otherwise default to', async () => {
      const dialog = render(defaultData({ selectionCount: 0, forcedScope: 'selection' }));
      await resolve(dialog, 0, channelsResult({ channels: [channel({ isBroadcaster: true })] }));

      dialog.channelInput('chan')?.click();
      dialog.detect();
      dialog.button(SUBMIT).click();

      expect(closed).toEqual([{ scope: 'selection', channelName: 'chan' }]);
    });
  });

  // Konzept "Auswahl überlebt Suche und Filter" nachtrag (2026-09-19): the same empty-scope guard
  // as ExportDialog's, for the push's own scope radiogroup.
  describe('empty-scope guard (nachtrag 2026-09-19)', () => {
    it('disables the "visible" radio when the visible list is empty but a selection survives it — "selection" is already the default (R12)', async () => {
      const dialog = render(defaultData({ visibleCount: 0, selectionCount: 5 }));
      await resolve(dialog, 0, channelsResult());

      expect(dialog.scopeInput('visible').disabled).toBe(true);
      expect(dialog.scopeInput('visible').checked).toBe(false);
      expect(dialog.scopeInput('selection').checked).toBe(true);
    });

    it('disables "Weiter" when the resolved scope has zero rows, even with a target chosen (defensive — the header button already keeps this state out of reach)', async () => {
      const dialog = render(defaultData({ visibleCount: 0, selectionCount: 0 }));
      await resolve(dialog, 0, channelsResult({ channels: [channel({ isBroadcaster: true })] }));

      dialog.channelInput('chan')?.click();
      dialog.detect();

      expect(dialog.button(SUBMIT).disabled).toBe(true);
    });

    it('does not extend that lock to a caller-forced scope — the dock shortcut is already guarded upstream instead', async () => {
      const dialog = render(defaultData({ selectionCount: 0, forcedScope: 'selection' }));
      await resolve(dialog, 0, channelsResult({ channels: [channel({ isBroadcaster: true })] }));

      dialog.channelInput('chan')?.click();
      dialog.detect();

      expect(dialog.button(SUBMIT).disabled).toBe(false);
    });
  });

  describe('post-load notice — exclusive, ranked reauthRequired > loadFailed > listIncomplete (§7.2)', () => {
    it('shows only the reauth warning, and hides the channel radiogroup and the "none" placeholder', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        channelsResult({
          reauthRequired: true,
          helixUnavailable: true,
          sevenTvUnavailable: true,
          channels: [channel({ channelName: 'reachable', isBroadcaster: true })],
        }),
      );

      expect(dialog.text()).toContain(
        'Deine Twitch-Anmeldung ist abgelaufen oder wurde widerrufen',
      );
      expect(dialog.text()).not.toContain('Die Kanalliste konnte nicht geladen werden.');
      expect(dialog.text()).not.toContain('Die Kanalliste ist gerade unvollständig');
      expect(dialog.text()).not.toContain('Kein weiterer Kanal');
      // reauthRequired blanks options() outright, even though the fixture handed one over.
      expect(dialog.channelInput('reachable')).toBeUndefined();
    });

    it('shows only the load-failed error with a retry action when the request itself errors', async () => {
      const dialog = render();
      await fail(dialog, 0, new HttpErrorResponse({ status: 500 }));

      expect(dialog.text()).toContain('Die Kanalliste konnte nicht geladen werden.');
      expect(dialog.hasButton(RETRY)).toBe(true);
      expect(dialog.text()).not.toContain('Twitch-Anmeldung');
      expect(dialog.text()).not.toContain('Die Kanalliste ist gerade unvollständig');
    });

    it('re-issues listMine() through channelsResource.reload() when the retry button is clicked', async () => {
      const dialog = render();
      await fail(dialog, 0, new HttpErrorResponse({ status: 500 }));

      dialog.button(RETRY).click();
      dialog.detect();

      expect(listMine).toHaveBeenCalledTimes(2);
    });

    it('shows only the incomplete-list info notice when neither reauth nor a hard failure applies', async () => {
      const dialog = render();
      await resolve(dialog, 0, channelsResult({ sevenTvUnavailable: true }));

      expect(dialog.text()).toContain('Die Kanalliste ist gerade unvollständig');
      expect(dialog.text()).not.toContain('Twitch-Anmeldung');
      expect(dialog.text()).not.toContain('Die Kanalliste konnte nicht geladen werden.');
    });

    it('shows no notice at all for a clean, complete result', async () => {
      const dialog = render();
      await resolve(dialog, 0, channelsResult({ channels: [channel({ isBroadcaster: true })] }));

      expect(dialog.text()).not.toContain('Twitch-Anmeldung');
      expect(dialog.text()).not.toContain('Die Kanalliste konnte nicht geladen werden.');
      expect(dialog.text()).not.toContain('Die Kanalliste ist gerade unvollständig');
    });

    it('shows the "no channel" placeholder only once the result is clean, complete, AND empty', async () => {
      const dialog = render();
      await resolve(dialog, 0, channelsResult());

      expect(dialog.text()).toContain(
        'Kein weiterer Kanal, in dem du Broadcaster oder 7TV-Editor bist.',
      );
    });
  });

  describe('options() — importTargetOptions applied to the loaded channel list', () => {
    it('offers broadcaster and 7TV-editor channels, excludes moderator-only channels', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        channelsResult({
          channels: [
            channel({ channelName: 'modonly', isModerator: true }),
            channel({ channelName: 'ownchannel', isBroadcaster: true }),
            channel({ channelName: 'editorchannel', isSevenTvEditor: true }),
          ],
        }),
      );

      expect(dialog.channelInput('modonly')).toBeUndefined();
      expect(dialog.channelInput('ownchannel')).toBeDefined();
      expect(dialog.channelInput('editorchannel')).toBeDefined();
    });

    it('excludes the current channel by normalized name, even when cased differently', async () => {
      const dialog = render(defaultData({ currentChannelName: 'HandOfBlood' }));
      await resolve(
        dialog,
        0,
        channelsResult({
          channels: [
            channel({ channelName: 'handofblood', isBroadcaster: true }),
            channel({ channelName: 'otherchannel', isBroadcaster: true }),
          ],
        }),
      );

      expect(dialog.channelInput('handofblood')).toBeUndefined();
      expect(dialog.channelInput('otherchannel')).toBeDefined();
    });

    it('disables the radio for a channel EmotePurge does not track yet, but still lists it', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        channelsResult({
          channels: [channel({ channelName: 'untracked', isBroadcaster: true, isTracked: false })],
        }),
      );

      const input = dialog.channelInput('untracked');
      expect(input).toBeDefined();
      expect(input?.disabled).toBe(true);
      expect(dialog.text()).toContain('Kanal muss erst beitreten');
    });

    it('sorts channel options ordinally by name', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        channelsResult({
          channels: [
            channel({ channelName: 'zulu', isBroadcaster: true }),
            channel({ channelName: 'alpha', isBroadcaster: true }),
            channel({ channelName: 'mike', isBroadcaster: true }),
          ],
        }),
      );

      const host = dialog.fixture.nativeElement as HTMLElement;
      const order = Array.from(host.querySelectorAll<HTMLLabelElement>('label'))
        .map((label) => label.textContent?.trim().split(/\s+/)[0])
        .filter((first): first is string => !!first && first.startsWith('#'));
      expect(order).toEqual(['#alpha', '#mike', '#zulu']);
    });

    it('renders an empty target group with the "none" message and a disabled "Weiter" when the account has no eligible channel (E5)', async () => {
      const dialog = render();
      await resolve(dialog, 0, channelsResult());

      expect(dialog.targetInputCount()).toBe(0);
      expect(dialog.text()).toContain(
        'Kein weiterer Kanal, in dem du Broadcaster oder 7TV-Editor bist.',
      );
      expect(dialog.button(SUBMIT).disabled).toBe(true);
      // ARIA forbids a radiogroup that owns zero radios — with the "none" message rendered as a
      // sibling instead of inside the group (see import-target-dialog.ts), this state must not
      // expose a `role="radiogroup"` at all.
      expect(dialog.targetRadiogroup()).toBeUndefined();
    });

    it('still exposes the target radiogroup once at least one channel qualifies', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        channelsResult({ channels: [channel({ channelName: 'alpha', isBroadcaster: true })] }),
      );

      expect(dialog.targetInputCount()).toBe(1);
      expect(dialog.targetRadiogroup()).toBeDefined();
    });
  });

  describe('target selection', () => {
    it('starts with no channel selected', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        channelsResult({
          channels: [channel({ channelName: 'somechannel', isBroadcaster: true })],
        }),
      );

      expect(dialog.channelInput('somechannel')?.checked).toBe(false);
    });

    it('checks the chosen channel once selected', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        channelsResult({
          channels: [channel({ channelName: 'somechannel', isBroadcaster: true })],
        }),
      );

      dialog.channelInput('somechannel')?.click();
      dialog.detect();

      expect(dialog.channelInput('somechannel')?.checked).toBe(true);
    });
  });

  describe('submit / cancel', () => {
    it('keeps "Weiter" disabled and refuses to close while no target is chosen', async () => {
      const dialog = render();
      await resolve(dialog, 0, channelsResult());

      const submit = dialog.button(SUBMIT);
      expect(submit.disabled).toBe(true);

      submit.click();
      expect(closed).toEqual([]);
    });

    it('closes with the chosen scope and a channel target', async () => {
      const dialog = render(defaultData({ selectionCount: 2 }));
      await resolve(
        dialog,
        0,
        channelsResult({
          channels: [channel({ channelName: 'somechannel', isBroadcaster: true })],
        }),
      );

      dialog.channelInput('somechannel')?.click();
      dialog.detect();
      dialog.button(SUBMIT).click();

      expect(closed).toEqual([{ scope: 'selection', channelName: 'somechannel' }]);
    });

    it('closes empty-handed on cancel, even with a target already chosen', async () => {
      const dialog = render();
      await resolve(
        dialog,
        0,
        channelsResult({
          channels: [channel({ channelName: 'somechannel', isBroadcaster: true })],
        }),
      );

      dialog.channelInput('somechannel')?.click();
      dialog.detect();
      dialog.button(CANCEL).click();

      expect(closed).toEqual([undefined]);
    });
  });
});
