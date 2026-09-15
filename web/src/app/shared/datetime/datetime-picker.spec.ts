import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AppLang, LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { DateTimePicker } from './datetime-picker';

// Only the keys this component translates — not the full app translation file.
const DE_TRANSLATIONS = {
  datetimePicker: {
    placeholder: 'Jetzt (Standard)',
    reset: 'Zurücksetzen',
    done: 'Fertig',
    weekdays: { mon: 'Mo', tue: 'Di', wed: 'Mi', thu: 'Do', fri: 'Fr', sat: 'Sa', sun: 'So' },
  },
};

/** Only the member this component actually reads off `LanguageService` — `satisfies` checks the
 *  fake against the real type, so a renamed or retyped `lang` is a compile error here. */
const languageServiceFake = { lang: signal<AppLang>('de') } satisfies Pick<LanguageService, 'lang'>;

function pad(n: number): string {
  return n.toString().padStart(2, '0');
}

/**
 * Builds the same `YYYY-MM-DDTHH:mm` wire value the component uses, written independently here
 * (not imported — the production formatters are private) so the assertions stay a black-box check
 * of the contract rather than a mirror of the implementation. Local time throughout, matching how
 * the component itself builds dates, so these tests never depend on the runner's TZ.
 */
function dtl(date: Date, hour: number, minute: number): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(hour)}:${pad(minute)}`;
}

function isSameCalendarDate(a: Date, b: Date): boolean {
  return (
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate()
  );
}

/** Generic over whatever `calendarDays()` actually returns — no hand-mirrored copy of the internal
 *  `CalendarDay` shape, just the one field these helpers need to match a cell to a real date. */
function findDay<T extends { date: Date }>(days: readonly T[], date: Date): T {
  const found = days.find((day) => isSameCalendarDate(day.date, date));
  if (!found) {
    throw new Error(`no grid cell for ${date.toDateString()}`);
  }
  return found;
}

function dayIndex<T extends { date: Date }>(days: readonly T[], date: Date): number {
  const index = days.findIndex((day) => isSameCalendarDate(day.date, date));
  if (index < 0) {
    throw new Error(`no grid cell for ${date.toDateString()}`);
  }
  return index;
}

/** Same formatting the component uses for a day button's `aria-label` — built independently here
 *  (not imported) so this stays a black-box check of the accessible name a screen reader would
 *  actually announce, not a mirror of the implementation. */
function fullDateLabel(date: Date, lang: AppLang): string {
  return new Intl.DateTimeFormat(toLocale(lang), { dateStyle: 'full' }).format(date);
}

/** The accname precedence this suite relies on for an accessible name: `aria-labelledby` (joining
 *  the referenced elements' text, space-separated) beats `aria-label`, which beats plain
 *  `textContent`. A local copy — not shared infrastructure — so this spec stays a black-box check
 *  of what a screen reader would actually announce, not a mirror of one specific attribute. */
function accessibleName(el: Element): string {
  const labelledBy = el.getAttribute('aria-labelledby');
  if (labelledBy) {
    const text = labelledBy
      .split(/\s+/)
      .filter(Boolean)
      .map((id) => el.ownerDocument.getElementById(id)?.textContent ?? '')
      .join(' ')
      .trim();
    if (text) {
      return text;
    }
  }
  const label = el.getAttribute('aria-label')?.trim();
  if (label) {
    return label;
  }
  return (el.textContent ?? '').trim();
}

/** Resolves a day button by its accessible name — the semantic locator #89 was missing, which is
 *  why it had to drop this kind of test. Throws with the available names so a failure names what
 *  was actually rendered instead of just "not found". */
function dayButtonNamed(
  fixture: ComponentFixture<DateTimePicker>,
  name: string,
): HTMLButtonElement {
  const rootElement = fixture.nativeElement as HTMLElement;
  const candidates = Array.from(rootElement.querySelectorAll<HTMLButtonElement>('button'));
  const found = candidates.find((button) => accessibleName(button) === name);
  if (!found) {
    const available = candidates.map((button) => accessibleName(button));
    throw new Error(`no day button named "${name}" — available: ${available.join(', ')}`);
  }
  return found;
}

/**
 * Walks forward, in real months, from `from` until it finds one whose 1st falls on `weekday`
 * (`Date#getDay()` convention: 0 = Sunday, 1 = Monday, …). Used to pin the grid's Monday-start and
 * Sunday-start edge cases without hardcoding a calendar date that would eventually go stale — local
 * time throughout, so the result never depends on the runner's TZ, only on the real calendar.
 */
function monthStartingOn(weekday: number, from: Date): { year: number; month: number } {
  let year = from.getFullYear();
  let month = from.getMonth();
  for (let i = 0; i < 12; i++) {
    if (new Date(year, month, 1).getDay() === weekday) {
      return { year, month };
    }
    month += 1;
    if (month > 11) {
      month = 0;
      year += 1;
    }
  }
  throw new Error(
    `no month starting on weekday ${weekday} within a year of ${from.toDateString()}`,
  );
}

function navigateToMonth(
  component: DateTimePicker,
  target: { year: number; month: number },
  from: Date,
): void {
  const diff = (target.year - from.getFullYear()) * 12 + (target.month - from.getMonth());
  for (let i = 0; i < diff; i++) {
    component['nextMonth']();
  }
  for (let i = 0; i < -diff; i++) {
    component['previousMonth']();
  }
}

// Fixed so `new Date()` — both here and inside the component — always lands on the same day, with
// the same weekday layout, on every run. Mid-month and not-on-the-hour on purpose, so nothing here
// accidentally lines up with a date-only or midnight edge case by coincidence.
const FIXED_NOW = new Date(2026, 5, 15, 12, 34, 0);

describe('DateTimePicker', () => {
  beforeEach(async () => {
    // Only `Date` is faked — timers, microtasks and `compileComponents()`'s own async work are real,
    // so Angular's rendering pipeline is untouched; this just pins what `new Date()` returns.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(FIXED_NOW);
    languageServiceFake.lang.set('de');

    await TestBed.configureTestingModule({
      imports: [
        DateTimePicker,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [{ provide: LanguageService, useValue: languageServiceFake }],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // `open` defaults to closed, matching the component's own initial state; pass true for any test
  // that needs the day grid in the DOM (the panel body is behind `@if (isOpen())`).
  function render(options: { value?: string; max?: string; open?: boolean } = {}): {
    fixture: ComponentFixture<DateTimePicker>;
    component: DateTimePicker;
  } {
    const fixture = TestBed.createComponent(DateTimePicker);
    if (options.value !== undefined) {
      fixture.componentRef.setInput('value', options.value);
    }
    if (options.max !== undefined) {
      fixture.componentRef.setInput('max', options.max);
    }
    fixture.detectChanges();
    if (options.open) {
      fixture.componentInstance['togglePanel']();
      fixture.detectChanges();
    }

    return { fixture, component: fixture.componentInstance };
  }

  describe('the 42-cell, Monday-based grid', () => {
    it('always renders exactly 42 cells, whatever month is showing', () => {
      const { component } = render();

      expect(component['calendarDays']()).toHaveLength(42);
    });

    it('starts the grid on a Monday, whatever weekday the month itself begins on', () => {
      const { component } = render();

      const days = component['calendarDays']();
      expect(days[0].date.getDay()).toBe(1);
    });

    it('puts the 1st of the month in the first cell when the month begins on a Monday', () => {
      const { component } = render();
      const now = new Date();
      const target = monthStartingOn(1, now);

      navigateToMonth(component, target, now);
      const days = component['calendarDays']();

      expect(days[0].dayOfMonth).toBe(1);
      expect(days[0].inCurrentMonth).toBe(true);
      expect(days[0].date.getFullYear()).toBe(target.year);
      expect(days[0].date.getMonth()).toBe(target.month);
    });

    it('leads with six trailing days from the previous month when the month begins on a Sunday', () => {
      const { component } = render();
      const now = new Date();
      const target = monthStartingOn(0, now);

      navigateToMonth(component, target, now);
      const days = component['calendarDays']();

      for (let i = 0; i < 6; i++) {
        expect(days[i].inCurrentMonth).toBe(false);
      }
      expect(days[6].dayOfMonth).toBe(1);
      expect(days[6].inCurrentMonth).toBe(true);
    });
  });

  describe('max compared at day granularity', () => {
    it('keeps the day equal to max selectable, even at the very earliest instant of that day', () => {
      const now = new Date();
      const maxDay = new Date(now.getFullYear(), now.getMonth(), 10);
      // 00:00 on the max day itself — the tightest possible case: if the comparison ever stopped
      // truncating to day granularity and started comparing full timestamps instead, this is the
      // one point where "the max day" and "later than max" meet.
      const { component } = render({ max: dtl(maxDay, 0, 0) });

      const days = component['calendarDays']();
      expect(days[dayIndex(days, maxDay)].isDisabled).toBe(false);
    });

    it('disables the day right after max, but leaves the day before it selectable', () => {
      const now = new Date();
      const maxDay = new Date(now.getFullYear(), now.getMonth(), 10);
      const dayBefore = new Date(now.getFullYear(), now.getMonth(), 9);
      const dayAfter = new Date(now.getFullYear(), now.getMonth(), 11);
      const { component } = render({ max: dtl(maxDay, 9, 0) });

      const days = component['calendarDays']();
      expect(days[dayIndex(days, dayBefore)].isDisabled).toBe(false);
      expect(days[dayIndex(days, dayAfter)].isDisabled).toBe(true);
    });

    it('leaves a disabled day inert to selectDay — the value never changes', () => {
      const now = new Date();
      const maxDay = new Date(now.getFullYear(), now.getMonth(), 10);
      const dayAfter = new Date(now.getFullYear(), now.getMonth(), 11);
      const startingValue = dtl(new Date(now.getFullYear(), now.getMonth(), 1), 10, 0);
      const { component } = render({ max: dtl(maxDay, 9, 0), value: startingValue });

      const disabledDay = findDay(component['calendarDays'](), dayAfter);
      expect(disabledDay.isDisabled).toBe(true);

      component['selectDay'](disabledDay);

      expect(component.value()).toBe(startingValue);
    });
  });

  describe('the value round trip', () => {
    it('selectDay keeps the already-set time of day', () => {
      const now = new Date();
      const from = new Date(now.getFullYear(), now.getMonth(), 5);
      const to = new Date(now.getFullYear(), now.getMonth(), 20);
      const { component } = render({ value: dtl(from, 14, 45) });

      component['selectDay'](findDay(component['calendarDays'](), to));

      expect(component.value()).toBe(dtl(to, 14, 45));
    });

    it('selectDay falls back to the current time of day when no value was set yet', () => {
      const now = new Date();
      const to = new Date(now.getFullYear(), now.getMonth(), 20);
      const { component } = render();
      const target = findDay(component['calendarDays'](), to);

      component['selectDay'](target);

      expect(component.value()).toBe(dtl(to, now.getHours(), now.getMinutes()));
    });

    it('setTime keeps the already-set date', () => {
      const now = new Date();
      const day = new Date(now.getFullYear(), now.getMonth(), 12);
      const { component } = render({ value: dtl(day, 8, 0) });

      component['setTime']('16:30');

      expect(component.value()).toBe(dtl(day, 16, 30));
    });

    it('setTime falls back to today’s date when no value was set yet', () => {
      const { component } = render();
      const now = new Date();

      component['setTime']('07:05');

      expect(component.value()).toBe(dtl(now, 7, 5));
    });
  });

  describe('day button accessible names (#180)', () => {
    it('selects a day located by its accessible name — the semantic locator #89 had to drop', () => {
      const { fixture, component } = render({ open: true });
      const now = new Date();
      const to = new Date(now.getFullYear(), now.getMonth(), 20);

      dayButtonNamed(fixture, fullDateLabel(to, 'de')).click();
      fixture.detectChanges();

      expect(component.value()).toBe(dtl(to, now.getHours(), now.getMinutes()));
    });

    it('gives a neighbouring-month day a distinct accessible name from the same day number in the current month', () => {
      const { fixture, component } = render({ open: true });
      const days = component['calendarDays']();

      // Trailing overflow always exists: leadingBlank (0-6) plus a month's length (28-31) never
      // reaches 42 cells, so the grid's last cell is always a next-month day (day number
      // 42 - leadingBlank - monthLength, i.e. 5..14 — 12 for the fixed June 2026), and the current
      // month always contains a day with that same low number.
      const trailing = days[days.length - 1];
      expect(trailing.inCurrentMonth).toBe(false);

      const currentMonthMatch = days.find(
        (day) => day.inCurrentMonth && day.dayOfMonth === trailing.dayOfMonth,
      );
      expect(currentMonthMatch).toBeDefined();

      const trailingButton = dayButtonNamed(fixture, fullDateLabel(trailing.date, 'de'));
      const currentMonthButton = dayButtonNamed(
        fixture,
        fullDateLabel(currentMonthMatch!.date, 'de'),
      );

      expect(accessibleName(trailingButton)).not.toBe(accessibleName(currentMonthButton));
    });

    it('marks today with aria-current="date" and leaves other days without it', () => {
      const { fixture, component } = render({ open: true });
      const days = component['calendarDays']();
      const today = days.find((day) => day.isToday);
      const other = days.find((day) => !day.isToday);
      if (!today || !other) {
        throw new Error('expected both a today cell and a non-today cell in the grid');
      }

      const todayButton = dayButtonNamed(fixture, fullDateLabel(today.date, 'de'));
      const otherButton = dayButtonNamed(fixture, fullDateLabel(other.date, 'de'));

      expect(todayButton.getAttribute('aria-current')).toBe('date');
      expect(otherButton.hasAttribute('aria-current')).toBe(false);
    });

    // Fake LanguageService exposes `lang` as a plain signal, so flipping it here is cheap.
    it('follows a language switch', () => {
      const { fixture } = render({ open: true });
      const now = new Date();
      const day = new Date(now.getFullYear(), now.getMonth(), 20);

      expect(dayButtonNamed(fixture, fullDateLabel(day, 'de'))).toBeTruthy();

      languageServiceFake.lang.set('en');
      fixture.detectChanges();

      expect(dayButtonNamed(fixture, fullDateLabel(day, 'en'))).toBeTruthy();
    });
  });
});
