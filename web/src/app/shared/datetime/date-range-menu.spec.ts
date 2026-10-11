import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';

import {
  DateRangeMenu,
  DateRangePreset,
  MAX_RANGE_DAYS,
  allTimeStart,
  daysAgo,
  toIsoDate,
} from './date-range-menu';

/** What the API actually rejects: `toDate.DayNumber - fromDate.DayNumber > 366` → RangeTooLarge. */
const API_MAX_RANGE_DAYS = 366;

function spanInDays(from: string): number {
  return Math.round(
    (Date.parse(`${toIsoDate(new Date())}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) /
      86_400_000,
  );
}

describe('allTimeStart', () => {
  it('reaches back to the channel’s own starting point when that is inside the window', () => {
    const trackedSince = toIsoDate(daysAgo(20));

    expect(allTimeStart(trackedSince)).toBe(trackedSince);
  });

  it('falls back to the widest accepted span while the starting point is unknown', () => {
    // The first render happens before the set-status response lands, so `earliest` is null exactly
    // once per page open — and must still produce a request the API answers.
    expect(allTimeStart(null)).toBe(toIsoDate(daysAgo(MAX_RANGE_DAYS)));
  });

  it('clamps a starting point older than the API allows instead of sending a 400', () => {
    // Cannot happen yet (tracking began 2026-07), which is why it is pinned here: the day a channel
    // has been tracked for over a year, "all time" must degrade to the widest answerable range
    // rather than turn the default page load into RangeTooLarge.
    expect(allTimeStart('2020-01-01')).toBe(toIsoDate(daysAgo(MAX_RANGE_DAYS)));
  });

  it('never asks for a wider span than the endpoint accepts', () => {
    for (const earliest of [null, '2020-01-01', toIsoDate(daysAgo(5))]) {
      expect(spanInDays(allTimeStart(earliest))).toBeLessThanOrEqual(API_MAX_RANGE_DAYS);
    }
  });
});

// --- Trigger label ------------------------------------------------------------------------------
//
// The real German strings, same convention as usage-range-menu.spec.ts: a selector built from them
// reads as the sentence the user gets.
const DE_TRANSLATIONS = {
  dateRange: {
    label: 'Zeitraum',
    menuLabel: 'Zeitraum wählen',
    presetToday: 'Heute',
    preset7Days: '7 Tage',
    preset30Days: '30 Tage',
    presetAll: 'Seit Beginn',
    presetCustom: 'Eigener Zeitraum',
    from: 'Von',
    to: 'Bis',
  },
  datetimePicker: {
    done: 'Fertig',
  },
};

@Component({
  imports: [DateRangeMenu],
  template: ` <app-date-range-menu [(from)]="from" [(to)]="to" [(preset)]="preset" /> `,
})
class Host {
  readonly from = signal('2026-09-01');
  readonly to = signal('2026-09-20');
  readonly preset = signal<DateRangePreset>('7');
}

/** Accessible-name computation: visible text with `aria-hidden` descendants (the dropdown caret,
 *  the checkmark on a checked option) stripped out first, same as assistive tech would read it. */
function accessibleName(element: HTMLElement): string {
  const clone = element.cloneNode(true) as HTMLElement;
  clone.querySelectorAll('[aria-hidden="true"]').forEach((node) => node.remove());
  return clone.textContent?.trim().replace(/\s+/g, ' ') ?? '';
}

describe('DateRangeMenu trigger label', () => {
  async function render(): Promise<{ fixture: ComponentFixture<Host>; host: HTMLElement }> {
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

    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement };
  }

  function trigger(host: HTMLElement): HTMLButtonElement {
    return host.querySelector<HTMLButtonElement>('button[aria-haspopup="dialog"]')!;
  }

  function radios(host: HTMLElement): HTMLButtonElement[] {
    const group = host.querySelector('[role="radiogroup"]');
    return group ? Array.from(group.querySelectorAll<HTMLButtonElement>('[role="radio"]')) : [];
  }

  it('offers exactly Heute, 7 Tage, 30 Tage, Seit Beginn and Eigener Zeitraum', async () => {
    // protects=the option list of the date menu (no whole-history preset besides Seit Beginn);
    // fails_when=an extra or missing preset appears in the radio group;
    // no_claim=what selecting an option does; the real German strings stand in for the app's i18n.
    const { fixture, host } = await render();
    trigger(host).click();
    fixture.detectChanges();

    expect(radios(host).map(accessibleName)).toEqual([
      'Heute',
      '7 Tage',
      '30 Tage',
      'Seit Beginn',
      'Eigener Zeitraum',
    ]);
  });

  it('names the chosen preset on the trigger with the same text as its option', async () => {
    // protects=the trigger label for each preset;
    // fails_when=a trigger shows a different text than the option it was chosen from;
    // no_claim=the layout of the toolbar.
    const { fixture, host } = await render();

    for (const [preset, expected] of [
      ['0', 'Heute'],
      ['7', '7 Tage'],
      ['30', '30 Tage'],
      ['all', 'Seit Beginn'],
    ] as const) {
      fixture.componentInstance.preset.set(preset);
      fixture.detectChanges();

      expect(accessibleName(trigger(host))).toBe(`Zeitraum: ${expected}`);
    }
  });
});
