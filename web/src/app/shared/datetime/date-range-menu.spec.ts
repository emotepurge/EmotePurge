import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';

import {
  DateRangeMenu,
  DateRangePreset,
  IsoDateRange,
  MAX_RANGE_DAYS,
  allTimeStart,
  dateRangePresetOptions,
  daysAgo,
  setObservedRange,
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

describe("the 'set-observed' preset (spec #200, 8.5, AK 61)", () => {
  it('is offered only when the chosen set has at least one observation interval', () => {
    const withoutInterval = setObservedRange([], '2026-10-20');
    const withInterval = setObservedRange(
      [{ fromUtc: '2026-10-01T00:00:00Z', toUtc: null }],
      '2026-10-20',
    );

    expect(withoutInterval).toBeNull();
    expect(
      dateRangePresetOptions(withoutInterval !== null).map((option) => option.value),
    ).not.toContain('set-observed');
    expect(dateRangePresetOptions(withInterval !== null).map((option) => option.value)).toEqual([
      '0',
      '7',
      '30',
      'all',
      'set-observed',
      'custom',
    ]);
  });

  it('selects the youngest interval, open end meaning today, whatever order the intervals arrive in', () => {
    const intervals = [
      { fromUtc: '2026-10-01T18:00:00Z', toUtc: null },
      { fromUtc: '2026-07-20T09:00:00Z', toUtc: '2026-08-02T12:00:00Z' },
    ];

    expect(setObservedRange(intervals, '2026-10-20')).toEqual({
      from: '2026-10-01',
      to: '2026-10-20',
    });
    expect(setObservedRange([...intervals].reverse(), '2026-10-20')).toEqual({
      from: '2026-10-01',
      to: '2026-10-20',
    });
    // A closed youngest interval ends where it ended, not today.
    expect(
      setObservedRange([{ fromUtc: '2026-07-20T09:00:00Z', toUtc: '2026-08-02T12:00:00Z' }]),
    ).toEqual({ from: '2026-07-20', to: '2026-08-02' });
  });
});

// --- Trigger label for 'set-observed' (operator decision 2026-09-22) --------------------------
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
    presetSetObserved: 'Während dieses Set beobachtet wurde',
    presetSetObservedShort: 'beobachtet',
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
  template: `
    <app-date-range-menu
      [(from)]="from"
      [(to)]="to"
      [(preset)]="preset"
      [setObservedRange]="observedRange()"
    />
  `,
})
class Host {
  readonly from = signal('2026-09-01');
  readonly to = signal('2026-09-20');
  readonly preset = signal<DateRangePreset>('7');
  readonly observedRange = signal<IsoDateRange | null>({ from: '2026-08-01', to: '2026-09-20' });
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

  it('shortens only the set-observed trigger label; the option keeps the full sentence', async () => {
    const { fixture, host } = await render();
    fixture.componentInstance.preset.set('set-observed');
    fixture.detectChanges();

    // Short form on the trigger — this is the whole point: the full sentence would push the
    // neighbouring "Set: …" control out of the toolbar.
    expect(accessibleName(trigger(host))).toBe('Zeitraum: beobachtet');

    trigger(host).click();
    fixture.detectChanges();

    const option = radios(host).find((r) => accessibleName(r).includes('beobachtet'))!;
    expect(accessibleName(option)).toBe('Während dieses Set beobachtet wurde');
  });

  it('leaves every other preset unchanged: trigger and option read the same text', async () => {
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
