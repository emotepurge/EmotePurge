import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { UsageRange, UsageRangeMenu } from './usage-range-menu';

// Only the keys this menu and its embedded Done button translate — the real German strings, so a
// selector built from them reads as the sentence the user gets, same convention as
// account-menu.spec.ts / segmented-control.spec.ts.
const DE_TRANSLATIONS = {
  usageRange: {
    label: 'Nutzung',
    menuLabel: 'Nutzungsbereich wählen',
    presetAll: 'alle',
    presetUnused: 'nie benutzt',
    presetCustom: 'eigener Bereich',
    min: 'Mindestens',
    max: 'Höchstens',
    valueBetween: '{{min}}–{{max}}×',
    valueAtLeast: 'ab {{min}}×',
    valueUpTo: 'bis {{max}}×',
  },
  datetimePicker: {
    done: 'Fertig',
  },
};

/**
 * `#outside` stands in for "focus was somewhere else on the page" for the focus-return tests,
 * same convention as account-menu.spec.ts. `onRangeChange` also writes the committed bounds back
 * into `min`/`max`, mirroring how a real host binds the output straight back into the inputs it
 * passed in — without that round trip, none of the "reopen after committing" assertions below would
 * say anything about the real component.
 */
@Component({
  imports: [UsageRangeMenu],
  template: `
    <button id="outside" type="button">Outside</button>
    <app-usage-range-menu [min]="min()" [max]="max()" (rangeChange)="onRangeChange($event)" />
  `,
})
class Host {
  readonly min = signal<number | null>(null);
  readonly max = signal<number | null>(null);
  readonly changes: UsageRange[] = [];

  onRangeChange(range: UsageRange): void {
    this.changes.push(range);
    this.min.set(range.min);
    this.max.set(range.max);
  }
}

interface Harness {
  fixture: ComponentFixture<Host>;
  host: Host;
  trigger: () => HTMLButtonElement;
  outside: () => HTMLButtonElement;
  dialog: () => HTMLElement | null;
  radiogroup: () => HTMLElement | null;
  radios: () => HTMLButtonElement[];
  radio: (label: string) => HTMLButtonElement;
  minInput: () => HTMLInputElement | null;
  maxInput: () => HTMLInputElement | null;
  doneButton: () => HTMLButtonElement | null;
  detect: () => void;
}

/** A rough accessible-name computation: visible text content with `aria-hidden` descendants (the
 *  checkmark on a checked option) stripped out first, same as assistive tech would read it. */
function accessibleName(element: HTMLElement): string {
  const clone = element.cloneNode(true) as HTMLElement;
  clone.querySelectorAll('[aria-hidden="true"]').forEach((node) => node.remove());
  return clone.textContent?.trim() ?? '';
}

describe('UsageRangeMenu', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        Host,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(min: number | null = null, max: number | null = null): Harness {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.min.set(min);
    fixture.componentInstance.max.set(max);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function radios(): HTMLButtonElement[] {
      const group = host.querySelector('[role="radiogroup"]');
      return group ? Array.from(group.querySelectorAll<HTMLButtonElement>('[role="radio"]')) : [];
    }

    return {
      fixture,
      host: fixture.componentInstance,
      trigger: () => host.querySelector<HTMLButtonElement>('button[aria-haspopup="dialog"]')!,
      outside: () => host.querySelector<HTMLButtonElement>('#outside')!,
      dialog: () => host.querySelector<HTMLElement>('[role="dialog"]'),
      radiogroup: () => host.querySelector<HTMLElement>('[role="radiogroup"]'),
      radios,
      radio: (label) => {
        // Accessible name of the role=radio element itself, not a lookup into its markup: a
        // checked option renders a second, checkmark <span aria-hidden="true">, which the
        // accessible-name computation excludes the same way assistive tech does.
        const found = radios().find((candidate) => accessibleName(candidate) === label);
        if (!found) {
          throw new Error(`no radio labelled "${label}"`);
        }
        return found;
      },
      minInput: () => host.querySelector<HTMLInputElement>('#usage-range-min'),
      maxInput: () => host.querySelector<HTMLInputElement>('#usage-range-max'),
      doneButton: () =>
        Array.from(host.querySelectorAll<HTMLButtonElement>('button')).find(
          (candidate) => candidate.textContent?.trim() === 'Fertig',
        ) ?? null,
      detect: () => fixture.detectChanges(),
    };
  }

  function commit(input: HTMLInputElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function pressKey(target: HTMLElement, key: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    target.dispatchEvent(event);
    return event;
  }

  describe('preset derived from the bounds', () => {
    it('is "all" when both bounds are unset, and names it on the trigger', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();

      expect(menu.radios().map((r) => r.getAttribute('aria-checked'))).toEqual([
        'true',
        'false',
        'false',
      ]);
      expect(menu.trigger().textContent).toContain('Nutzung: alle');
    });

    it('is "unused" exactly when both bounds are 0 — the only bit pattern the old toggle owned', () => {
      const menu = render(0, 0);
      menu.trigger().click();
      menu.detect();

      expect(menu.radio('nie benutzt').getAttribute('aria-checked')).toBe('true');
      expect(menu.trigger().textContent).toContain('Nutzung: nie benutzt');
    });

    it('is "custom" with only a floor set, and phrases the trigger as "at least"', () => {
      const menu = render(5, null);
      menu.trigger().click();
      menu.detect();

      expect(menu.radio('eigener Bereich').getAttribute('aria-checked')).toBe('true');
      expect(menu.trigger().textContent).toContain('Nutzung: ab 5×');
    });

    it('is "custom" with only a ceiling set, and phrases the trigger as "up to"', () => {
      const menu = render(null, 10);

      expect(menu.trigger().textContent).toContain('Nutzung: bis 10×');
    });

    it('is "custom" with both bounds set, and phrases the trigger as a range', () => {
      const menu = render(3, 8);

      expect(menu.trigger().textContent).toContain('Nutzung: 3–8×');
    });
  });

  describe('choosing "custom" is sticky and does not snap back to "all" for an empty range', () => {
    it('stays open and shows the custom fields without emitting a range for a still-empty selection', () => {
      const menu = render(null, null); // bounds alone would read as "all"
      menu.trigger().click();
      menu.detect();

      menu.radio('eigener Bereich').click();
      menu.detect();

      expect(menu.host.changes).toEqual([]); // "custom" alone commits nothing
      expect(menu.dialog()).not.toBeNull(); // menu did not close, unlike every other preset
      expect(menu.radio('eigener Bereich').getAttribute('aria-checked')).toBe('true');
      expect(menu.minInput()).not.toBeNull();
      expect(menu.maxInput()).not.toBeNull();
    });

    it('keeps the custom selection after the panel is closed and reopened, bounds still null/null', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();
      menu.radio('eigener Bereich').click();
      menu.detect();

      menu.trigger().click(); // close
      menu.detect();
      menu.trigger().click(); // reopen
      menu.detect();

      expect(menu.radio('eigener Bereich').getAttribute('aria-checked')).toBe('true');
      expect(menu.minInput()).not.toBeNull();
    });

    it('is cleared by committing any other preset, so a later empty "custom" click shows the fields again', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();
      menu.radio('eigener Bereich').click();
      menu.detect();

      menu.radio('alle').click(); // committing "all" clears the stickiness
      menu.detect();

      expect(menu.host.changes).toEqual([{ min: null, max: null }]);
      expect(menu.dialog()).toBeNull(); // "all" does close, unlike "custom"

      // The stickiness must not survive the close — reopening shows "all" selected and no custom
      // fields, proving select() actually cleared it rather than the panel merely being closed.
      menu.trigger().click();
      menu.detect();

      expect(menu.radio('alle').getAttribute('aria-checked')).toBe('true');
      expect(menu.minInput()).toBeNull();
      expect(menu.maxInput()).toBeNull();
    });
  });

  describe('emitted range per action', () => {
    it('commits {min:null,max:null} and closes when "all" is picked', () => {
      const menu = render(0, 0); // start from "unused"
      menu.trigger().click();
      menu.detect();

      menu.radio('alle').click();
      menu.detect();

      expect(menu.host.changes).toEqual([{ min: null, max: null }]);
      expect(menu.dialog()).toBeNull();
    });

    it('commits {min:0,max:0} and closes when "unused" is picked', () => {
      const menu = render(3, 8);
      menu.trigger().click();
      menu.detect();

      menu.radio('nie benutzt').click();
      menu.detect();

      expect(menu.host.changes).toEqual([{ min: 0, max: 0 }]);
      expect(menu.dialog()).toBeNull();
    });

    it('commits only the min field on change, keeps the current max, and leaves the panel open', () => {
      const menu = render(null, 20); // bounds alone already read as "custom"
      menu.trigger().click();
      menu.detect();

      commit(menu.minInput()!, '5');
      menu.detect();

      expect(menu.host.changes).toEqual([{ min: 5, max: 20 }]);
      expect(menu.dialog()).not.toBeNull();
    });

    it('commits only the max field on change, keeping the current min', () => {
      const menu = render(5, null);
      menu.trigger().click();
      menu.detect();

      commit(menu.maxInput()!, '30');
      menu.detect();

      expect(menu.host.changes).toEqual([{ min: 5, max: 30 }]);
    });

    it('clears a bound to null on an empty field rather than guessing a value', () => {
      const menu = render(5, 20);
      menu.trigger().click();
      menu.detect();

      commit(menu.minInput()!, '');
      menu.detect();

      expect(menu.host.changes).toEqual([{ min: null, max: 20 }]);
    });

    it('clears a bound to null on unparseable input instead of committing NaN', () => {
      const menu = render(5, 20);
      menu.trigger().click();
      menu.detect();

      // A real <input type="number"> sanitizes an unparseable string to '' before the (change)
      // handler ever sees it — that only re-exercises the empty-field case above and never reaches
      // parseBound's Number.isNaN guard. Switching the input's type first stops jsdom (like a real
      // browser) from sanitizing it away, so the handler receives the same raw string a laxer input
      // source (paste, some mobile keyboards) can still produce.
      const input = menu.minInput()!;
      input.type = 'text';
      commit(input, 'not-a-number');
      menu.detect();

      expect(menu.host.changes).toEqual([{ min: null, max: 20 }]);
    });

    it('closes the panel from the Done button without committing anything further', () => {
      const menu = render(5, 20); // already "custom" from the bounds
      menu.trigger().click();
      menu.detect();

      menu.doneButton()!.click();
      menu.detect();

      expect(menu.dialog()).toBeNull();
      expect(menu.host.changes).toEqual([]);
    });
  });

  describe('keyboard navigation moves focus, never commits a range', () => {
    it('gives the roving tabindex to the option matching the current preset before any arrow key', () => {
      const menu = render(null, null); // "all" is option index 0
      menu.trigger().click();
      menu.detect();

      expect(menu.radios().map((r) => r.tabIndex)).toEqual([0, -1, -1]);
    });

    it('moves the roving tabindex and DOM focus forward on ArrowDown, wrapping past the last option', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();

      pressKey(menu.radios()[0], 'ArrowDown');
      menu.detect();
      expect(menu.radios().map((r) => r.tabIndex)).toEqual([-1, 0, -1]);
      expect(document.activeElement).toBe(menu.radios()[1]);

      pressKey(menu.radios()[1], 'ArrowDown');
      menu.detect();
      expect(document.activeElement).toBe(menu.radios()[2]);

      pressKey(menu.radios()[2], 'ArrowDown'); // wraps past the last option
      menu.detect();
      expect(document.activeElement).toBe(menu.radios()[0]);
    });

    it('moves backward on ArrowUp, wrapping past the first option', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();

      pressKey(menu.radios()[0], 'ArrowUp'); // wraps past the first option
      menu.detect();
      expect(document.activeElement).toBe(menu.radios()[2]);
    });

    it('never emits a range change while only arrowing between options', () => {
      // The class doc comment is explicit about why: committing on every keypress would clear the
      // emote selection each time the user simply walks the list.
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();

      pressKey(menu.radios()[0], 'ArrowDown');
      pressKey(menu.radios()[1], 'ArrowDown');
      menu.detect();

      expect(menu.host.changes).toEqual([]);
    });

    it('leaves a non-arrow key alone instead of swallowing it', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();

      const event = pressKey(menu.radios()[0], 'Tab');
      menu.detect();

      expect(event.defaultPrevented).toBe(false);
      expect(menu.radios().map((r) => r.tabIndex)).toEqual([0, -1, -1]); // unchanged
    });
  });

  describe('accessibility semantics', () => {
    it('names the radiogroup and the popover dialog from their own, distinct translation keys', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();

      expect(menu.radiogroup()!.getAttribute('aria-label')).toBe('Nutzung');
      expect(menu.dialog()!.getAttribute('aria-label')).toBe('Nutzungsbereich wählen');
    });

    it('marks the trigger as a dialog discloser and reflects open state in aria-expanded', () => {
      const menu = render(null, null);

      expect(menu.trigger().getAttribute('aria-haspopup')).toBe('dialog');
      expect(menu.trigger().getAttribute('aria-expanded')).toBe('false');

      menu.trigger().click();
      menu.detect();

      expect(menu.trigger().getAttribute('aria-expanded')).toBe('true');
    });
  });

  describe('open/close state machine and focus return', () => {
    it('opens on trigger click and closes again on a second click', () => {
      const menu = render(null, null);

      expect(menu.dialog()).toBeNull();

      menu.trigger().click();
      menu.detect();
      expect(menu.dialog()).not.toBeNull();

      menu.trigger().click();
      menu.detect();
      expect(menu.dialog()).toBeNull();
    });

    it('returns focus to the trigger on Escape when focus was inside the panel', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();
      menu.radios()[0].focus();
      expect(document.activeElement).toBe(menu.radios()[0]);

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      menu.detect();

      expect(menu.dialog()).toBeNull();
      expect(document.activeElement).toBe(menu.trigger());
    });

    it('leaves focus untouched on Escape when it was outside the component entirely', () => {
      const menu = render(null, null);
      menu.trigger().click();
      menu.detect();
      menu.outside().focus();

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      menu.detect();

      expect(menu.dialog()).toBeNull();
      expect(document.activeElement).toBe(menu.outside());
    });
  });
});
