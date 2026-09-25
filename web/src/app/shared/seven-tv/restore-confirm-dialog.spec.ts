import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { RestoreConfirmDialog, RestoreConfirmDialogData } from './restore-confirm-dialog';

// Only the keys this dialog translates — same reasoning as delete-confirm-dialog.spec.ts's own
// DE_TRANSLATIONS: real German text, so an assertion reads as the sentence the user gets.
const DE_TRANSLATIONS = {
  common: {
    cancel: 'Abbrechen',
  },
  restore: {
    confirmTitle: {
      one: '{{ count }} Emote wieder zum Set hinzufügen?',
      other: '{{ count }} Emotes wieder zum Set hinzufügen?',
    },
    confirmTitleUpTo: {
      one: 'Bis zu {{ count }} Emote wieder zum Set hinzufügen?',
      other: 'Bis zu {{ count }} Emotes wieder zum Set hinzufügen?',
    },
    confirmSetLine: 'In das Set „{{ setName }}“.',
    confirmSetIdLine: 'Set-ID: {{ emoteSetId }}',
    confirmOwnerLine: 'Besitzer: {{ ownerDisplayName }}',
    confirmChannelLine: 'Kanal: {{ channelName }}',
    confirmSetNotActive: 'Dieses Set ist gerade nicht aktiv.',
    confirmForeignToView: 'Diese Ansicht zeigt von diesem Lauf nichts.',
    confirmExecute: 'Wiederherstellen',
    capacityProjection: 'Das Set hätte danach {{ projected }} von {{ capacity }} Slots belegt.',
    capacityProjectionUpTo:
      'Das Set hätte danach bis zu {{ projected }} von {{ capacity }} Slots belegt.',
    capacityWarning: 'Das überschreitet die Kapazität — 7TV wird überzählige Emotes ablehnen.',
    capacityWarningUpTo:
      'Das könnte die Kapazität überschreiten — 7TV würde überzählige Emotes dann ablehnen.',
    historyNote: 'Die Nutzungshistorie bleibt unverändert.',
  },
};

const CANCEL = 'Abbrechen';
const CONFIRM = 'Wiederherstellen';

interface RenderOptions {
  names?: string[];
  addCount?: number;
  countIsUpperBound?: boolean;
  slots?: { occupied: number; capacity: number } | null;
  setName?: string;
  isActiveSet?: boolean;
  emoteSetId?: string;
  ownerDisplayName?: string;
  trackedChannelName?: string | null;
  foreignToView?: boolean;
}

interface Harness {
  fixture: ComponentFixture<RestoreConfirmDialog>;
  slots: WritableSignal<{ occupied: number; capacity: number } | null>;
  detect(): void;
  text(): string;
  button(label: string): HTMLButtonElement;
}

describe('RestoreConfirmDialog', () => {
  let dialogData: RestoreConfirmDialogData;
  let closed: boolean[];

  beforeEach(async () => {
    closed = [];

    await TestBed.configureTestingModule({
      imports: [
        RestoreConfirmDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        // Resolved when the component is created, so a test may shape the data first — same
        // pattern as delete-confirm-dialog.spec.ts's render().
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

  /** One dialog per test — DIALOG_DATA is resolved once per injector, same reasoning as
   *  delete-confirm-dialog.spec.ts's render(). */
  function render(options: RenderOptions = {}): Harness {
    const names = options.names ?? ['PogU'];
    const slots = signal<{ occupied: number; capacity: number } | null>(
      options.slots === undefined ? null : options.slots,
    );

    dialogData = {
      names,
      addCount: options.addCount ?? names.length,
      countIsUpperBound: options.countIsUpperBound ?? false,
      slots: slots.asReadonly(),
      setName: options.setName ?? 'Hauptset',
      isActiveSet: options.isActiveSet ?? true,
      emoteSetId: options.emoteSetId ?? 'set-1',
      ownerDisplayName: options.ownerDisplayName ?? 'SomeOwner',
      trackedChannelName:
        options.trackedChannelName === undefined ? 'somechannel' : options.trackedChannelName,
      foreignToView: options.foreignToView ?? false,
    };

    const fixture = TestBed.createComponent(RestoreConfirmDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    return {
      fixture,
      slots,
      detect: () => fixture.detectChanges(),
      text: () => host.textContent ?? '',
      button: (label) => {
        const found = buttons().find((button) => button.textContent?.trim() === label);
        if (!found) {
          throw new Error(`no button labelled "${label}"`);
        }
        return found;
      },
    };
  }

  describe('dialog outcome', () => {
    it('closes with true when the user confirms the run', () => {
      const dialog = render();

      dialog.button(CONFIRM).click();

      expect(closed).toEqual([true]);
    });

    it('closes with false on cancel', () => {
      const dialog = render();

      dialog.button(CANCEL).click();

      expect(closed).toEqual([false]);
    });
  });

  // spec #200, 8.8 (AK 73); since #253 the "not currently active" addition is gated on a tracked
  // channel too (spec 4.3, point 6) — see the "target-class line order" describe block below for
  // the untracked case.
  describe('naming the set (spec #200, 8.8)', () => {
    it('names the set and shows no "not active" note for the active set', () => {
      const dialog = render({ setName: 'Halloween', isActiveSet: true });

      expect(dialog.text()).toContain('In das Set „Halloween“.');
      expect(dialog.text()).not.toContain('Dieses Set ist gerade nicht aktiv.');
    });

    it('adds the "not active" note for a non-active set', () => {
      const dialog = render({ setName: 'Halloween', isActiveSet: false });

      expect(dialog.text()).toContain('In das Set „Halloween“.');
      expect(dialog.text()).toContain('Dieses Set ist gerade nicht aktiv.');
    });
  });

  // Task brief T6, "+4": the line order is contract (set, set id, owner, channel [tracked only],
  // "not active" [tracked and not active only], foreign-to-view hint) — one test per target class,
  // asserting presence/absence and relative order rather than wording (Regel 12).
  describe('target-class line order (spec 4.3, point 6; E21)', () => {
    it('shows set, set id, owner and channel in order for a tracked, active target', () => {
      const dialog = render({
        setName: 'Halloween',
        emoteSetId: 'set-halloween',
        ownerDisplayName: 'HandOfBlood',
        trackedChannelName: 'handofblood',
        isActiveSet: true,
      });

      const text = dialog.text();
      expect(text).toContain('In das Set „Halloween“.');
      expect(text).toContain('Set-ID: set-halloween');
      expect(text).toContain('Besitzer: HandOfBlood');
      expect(text).toContain('Kanal: handofblood');
      expect(text).not.toContain('Dieses Set ist gerade nicht aktiv.');

      const setIndex = text.indexOf('In das Set');
      const idIndex = text.indexOf('Set-ID:');
      const ownerIndex = text.indexOf('Besitzer:');
      const channelIndex = text.indexOf('Kanal:');
      expect(setIndex).toBeLessThan(idIndex);
      expect(idIndex).toBeLessThan(ownerIndex);
      expect(ownerIndex).toBeLessThan(channelIndex);
    });

    it('adds the "not active" line after the channel line for a tracked, non-active target', () => {
      const dialog = render({
        trackedChannelName: 'handofblood',
        isActiveSet: false,
      });

      const text = dialog.text();
      expect(text).toContain('Kanal: handofblood');
      expect(text).toContain('Dieses Set ist gerade nicht aktiv.');
      expect(text.indexOf('Kanal:')).toBeLessThan(
        text.indexOf('Dieses Set ist gerade nicht aktiv.'),
      );
    });

    it('shows the owner but no channel and no "not active" line for an untracked target', () => {
      const dialog = render({
        ownerDisplayName: 'SomeOwner',
        trackedChannelName: null,
        isActiveSet: false,
      });

      const text = dialog.text();
      expect(text).toContain('Besitzer: SomeOwner');
      expect(text).not.toContain('Kanal:');
      expect(text).not.toContain('Dieses Set ist gerade nicht aktiv.');
    });

    it('shows the foreign-to-view hint when the target differs from the host-selected set', () => {
      const dialog = render({ foreignToView: true });
      expect(dialog.text()).toContain('Diese Ansicht zeigt von diesem Lauf nichts.');
    });

    it('shows no foreign-to-view hint for the host-selected set itself', () => {
      const dialog = render({ foreignToView: false });
      expect(dialog.text()).not.toContain('Diese Ansicht zeigt von diesem Lauf nichts.');
    });
  });

  describe('title pluralization', () => {
    it('uses the singular wording for exactly one row', () => {
      const dialog = render({ names: ['PogU'] });

      expect(dialog.text()).toContain('1 Emote wieder zum Set hinzufügen?');
    });

    it('uses the plural wording for several rows', () => {
      const dialog = render({ names: ['PogU', 'Kappa'] });

      expect(dialog.text()).toContain('2 Emotes wieder zum Set hinzufügen?');
    });
  });

  // spec #200, 7.2: the projection counts ADDs, not rows — a #74 duplicate cell is one row in
  // `names` but restores under two aliases, i.e. two ADDs.
  describe('capacity projection counts ADDs, not rows (spec 7.2)', () => {
    it('projects against addCount when it differs from the row count', () => {
      const dialog = render({
        names: ['AliasOne'],
        addCount: 2,
        slots: { occupied: 10, capacity: 20 },
      });

      expect(dialog.text()).toContain('Das Set hätte danach 12 von 20 Slots belegt.');
    });

    it('shows the overflow warning once the ADD count pushes past capacity', () => {
      const dialog = render({
        names: ['AliasOne'],
        addCount: 2,
        slots: { occupied: 19, capacity: 20 },
      });

      expect(dialog.text()).toContain('Das Set hätte danach 21 von 20 Slots belegt.');
      expect(dialog.text()).toContain(
        'Das überschreitet die Kapazität — 7TV wird überzählige Emotes ablehnen.',
      );
    });

    it('shows no projection line at all while the slot check has not answered yet', () => {
      const dialog = render({ slots: null });

      expect(dialog.text()).not.toContain('Slots belegt');
    });

    it('shows no projection line when the set reports no usable capacity', () => {
      const dialog = render({ slots: { occupied: 5, capacity: 0 } });

      expect(dialog.text()).not.toContain('Slots belegt');
    });

    it('updates once a live slot check answers after the dialog opened', () => {
      const dialog = render({ names: ['PogU'], addCount: 1, slots: null });
      expect(dialog.text()).not.toContain('Slots belegt');

      dialog.slots.set({ occupied: 3, capacity: 100 });
      dialog.detect();

      expect(dialog.text()).toContain('Das Set hätte danach 4 von 100 Slots belegt.');
    });
  });

  // Operator decision 2026-09-25 (#255): once the open-time duplicate check could not verify the
  // count (its own 7TV read failed), the dialog hedges rather than claiming an exact number.
  describe('upper-bound wording (#255)', () => {
    it('uses the "up to" title instead of the plain one', () => {
      const dialog = render({ names: ['PogU'], countIsUpperBound: true });

      expect(dialog.text()).toContain('Bis zu 1 Emote wieder zum Set hinzufügen?');
    });

    it('pluralizes the "up to" title for several rows', () => {
      const dialog = render({ names: ['PogU', 'Kappa'], countIsUpperBound: true });

      expect(dialog.text()).toContain('Bis zu 2 Emotes wieder zum Set hinzufügen?');
    });

    it('uses the "up to" capacity line instead of the plain one', () => {
      const dialog = render({
        names: ['PogU'],
        addCount: 1,
        countIsUpperBound: true,
        slots: { occupied: 3, capacity: 100 },
      });

      expect(dialog.text()).toContain('Das Set hätte danach bis zu 4 von 100 Slots belegt.');
    });

    // #255 P3(9): the overflow warning itself must not claim certainty it does not have — a
    // projection computed from an unverified count is a "could", not a "does".
    it('hedges the overflow warning too, once the projected count is only an upper bound', () => {
      const dialog = render({
        names: ['PogU'],
        addCount: 2,
        countIsUpperBound: true,
        slots: { occupied: 19, capacity: 20 },
      });

      expect(dialog.text()).toContain(
        'Das könnte die Kapazität überschreiten — 7TV würde überzählige Emotes dann ablehnen.',
      );
      expect(dialog.text()).not.toContain(
        'Das überschreitet die Kapazität — 7TV wird überzählige Emotes ablehnen.',
      );
    });

    it('uses the plain title and capacity line when the count is not an upper bound', () => {
      const dialog = render({
        names: ['PogU'],
        addCount: 1,
        countIsUpperBound: false,
        slots: { occupied: 3, capacity: 100 },
      });

      expect(dialog.text()).toContain('1 Emote wieder zum Set hinzufügen?');
      expect(dialog.text()).toContain('Das Set hätte danach 4 von 100 Slots belegt.');
      expect(dialog.text()).not.toContain('Bis zu');
    });
  });
});
