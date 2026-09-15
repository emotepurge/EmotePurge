import { Component, ElementRef, computed, inject, input, model, signal } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { Button } from '../ui/button';

function pad(n: number): string {
  return n.toString().padStart(2, '0');
}

function toDateOnlyString(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function toTimeOnlyString(date: Date): string {
  return `${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toDateTimeLocalValue(date: Date): string {
  return `${toDateOnlyString(date)}T${toTimeOnlyString(date)}`;
}

function parseDateTimeLocal(value: string): Date | null {
  if (!value) {
    return null;
  }
  const [datePart, timePart] = value.split('T');
  const [year, month, day] = datePart.split('-').map(Number);
  const [hour, minute] = (timePart ?? '00:00').split(':').map(Number);
  return new Date(year, month - 1, day, hour, minute);
}

function isSameDay(a: Date, b: Date): boolean {
  return (
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate()
  );
}

function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

interface CalendarDay {
  date: Date;
  dayOfMonth: number;
  inCurrentMonth: boolean;
  isDisabled: boolean;
  isSelected: boolean;
  isToday: boolean;
}

const WEEKDAY_LABEL_KEYS = [
  'datetimePicker.weekdays.mon',
  'datetimePicker.weekdays.tue',
  'datetimePicker.weekdays.wed',
  'datetimePicker.weekdays.thu',
  'datetimePicker.weekdays.fri',
  'datetimePicker.weekdays.sat',
  'datetimePicker.weekdays.sun',
];

/**
 * Self-built calendar-grid + time popup, used in place of a bare native `<input type="datetime-local">`.
 * Value is kept in the same "datetime-local" string format (`YYYY-MM-DDTHH:mm`, local time, empty
 * string = unset) so existing callers barely change. The panel closes via the explicit
 * "Fertig"/"Done" button, a click anywhere outside the component, or Escape.
 */
@Component({
  selector: 'app-datetime-picker',
  imports: [Button, TranslocoPipe],
  host: {
    '(document:click)': 'onDocumentClick($event)',
    '(keydown.escape)': 'onEscape()',
  },
  template: `
    <div class="relative inline-block">
      <button
        type="button"
        class="rounded-md border border-border-field bg-field px-3 py-2 text-left text-sm text-fg hover:border-fg-muted"
        (click)="togglePanel()"
      >
        {{ displayValue() }}
      </button>

      @if (isOpen()) {
        <!-- z-30: must clear the session cards' secondary actions, which sit at relative z-10
             per the stretched-link contract (see .app-card-link) and come later in the DOM. -->
        <div
          class="absolute z-30 mt-1 w-64 rounded-md border border-border-strong bg-surface p-3 shadow-overlay"
        >
          <div class="mb-2 flex items-center justify-between">
            <button
              type="button"
              class="rounded px-2 py-1 text-fg-muted hover:bg-surface-inset"
              (click)="previousMonth()"
            >
              ‹
            </button>
            <span class="text-sm font-medium">{{ monthLabel() }}</span>
            <button
              type="button"
              class="rounded px-2 py-1 text-fg-muted hover:bg-surface-inset"
              (click)="nextMonth()"
            >
              ›
            </button>
          </div>

          <div class="grid grid-cols-7 gap-1 text-center text-xs text-fg-muted">
            @for (labelKey of weekdayLabelKeys; track labelKey) {
              <span>{{ labelKey | transloco }}</span>
            }
          </div>
          <!-- aria-current="date" marks today only, its defined ARIA meaning. No state attribute
               for the selected day: aria-selected is only valid on grid/option roles (out of scope
               here), aria-pressed implies a toggle and clicking the selected day again does not
               un-select it. The trigger shows the chosen value, but does not itself convey which
               grid cell is selected — that needs aria-selected on gridcells, i.e. the grid-role
               rebuild this component does not yet have. -->
          <div class="grid grid-cols-7 gap-1">
            @for (day of calendarDays(); track day.date.getTime()) {
              <button
                type="button"
                [disabled]="day.isDisabled"
                class="rounded p-1 text-xs transition"
                [class]="dayClass(day)"
                [attr.aria-label]="dayAriaLabel(day)"
                [attr.aria-current]="day.isToday ? 'date' : null"
                (click)="selectDay(day)"
              >
                {{ day.dayOfMonth }}
              </button>
            }
          </div>

          <input
            type="time"
            [value]="timeValue()"
            (change)="setTime($any($event.target).value)"
            class="mt-3 w-full rounded-md border border-border-field bg-field px-2 py-1.5 text-sm"
          />

          <div class="mt-3 flex items-center justify-between text-sm">
            <button type="button" class="text-fg-muted hover:underline" (click)="clear()">
              {{ 'datetimePicker.reset' | transloco }}
            </button>
            <!-- appButton instead of the hand-copied utility chain it used to carry (design doc
                 §4.1): the copy had drifted out of reach of every later change to the primary look,
                 and it would have had to be tokenised separately here for no benefit. -->
            <button type="button" appButton="primary" (click)="close()">
              {{ 'datetimePicker.done' | transloco }}
            </button>
          </div>
        </div>
      }
    </div>
  `,
})
export class DateTimePicker {
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);
  private readonly elementRef = inject(ElementRef<HTMLElement>);

  readonly value = model('');
  readonly max = input<string>();
  readonly placeholder = input<string>();

  protected readonly weekdayLabelKeys = WEEKDAY_LABEL_KEYS;
  protected readonly isOpen = signal(false);
  protected readonly viewMonth = signal(startOfMonth(new Date()));

  protected readonly displayValue = computed(() => {
    const parsed = parseDateTimeLocal(this.value());
    if (parsed) {
      return `${toDateOnlyString(parsed)} ${toTimeOnlyString(parsed)}`;
    }
    const lang = this.languageService.lang();
    return (
      this.placeholder() ?? this.translocoService.translate('datetimePicker.placeholder', {}, lang)
    );
  });

  protected readonly timeValue = computed(() => {
    const parsed = parseDateTimeLocal(this.value());
    return parsed ? toTimeOnlyString(parsed) : '00:00';
  });

  protected readonly monthLabel = computed(() =>
    this.viewMonth().toLocaleDateString(toLocale(this.languageService.lang()), {
      month: 'long',
      year: 'numeric',
    }),
  );

  // Full weekday + day + month + year so a screen reader hears an unambiguous date, not just the
  // bare visible number — the 42-cell grid repeats day numbers across neighbouring months. Kept as
  // a computed `Intl.DateTimeFormat` (rather than formatting inline per day) so it re-derives once
  // per language switch instead of once per grid cell.
  protected readonly dayLabelFormatter = computed(
    () => new Intl.DateTimeFormat(toLocale(this.languageService.lang()), { dateStyle: 'full' }),
  );

  protected readonly calendarDays = computed<CalendarDay[]>(() => {
    const month = this.viewMonth();
    const selected = parseDateTimeLocal(this.value());
    const maxRaw = this.max();
    const maxDay = maxRaw ? parseDateTimeLocal(maxRaw) : null;
    const maxDayOnly = maxDay
      ? new Date(maxDay.getFullYear(), maxDay.getMonth(), maxDay.getDate())
      : null;
    const today = new Date();
    today.setHours(0, 0, 0, 0);

    const firstOfMonth = new Date(month.getFullYear(), month.getMonth(), 1);
    // Monday-first grid — JS getDay() is 0=Sunday, shift so Monday=0.
    const leadingBlank = (firstOfMonth.getDay() + 6) % 7;
    const gridStart = new Date(firstOfMonth);
    gridStart.setDate(gridStart.getDate() - leadingBlank);

    const days: CalendarDay[] = [];
    for (let i = 0; i < 42; i++) {
      const date = new Date(gridStart);
      date.setDate(gridStart.getDate() + i);
      date.setHours(0, 0, 0, 0);

      days.push({
        date,
        dayOfMonth: date.getDate(),
        inCurrentMonth: date.getMonth() === month.getMonth(),
        isDisabled: maxDayOnly !== null && date.getTime() > maxDayOnly.getTime(),
        isSelected: selected !== null && isSameDay(date, selected),
        isToday: isSameDay(date, today),
      });
    }
    return days;
  });

  protected togglePanel(): void {
    this.isOpen.update((open) => !open);
  }

  protected close(): void {
    this.isOpen.set(false);
  }

  protected onDocumentClick(event: Event): void {
    // Clicks inside the component (trigger button, calendar, time input) are handled by their own
    // handlers; anything outside dismisses the panel — same pattern as the shell's disclosure menu.
    if (this.isOpen() && !this.elementRef.nativeElement.contains(event.target as Node)) {
      this.close();
    }
  }

  protected onEscape(): void {
    this.close();
  }

  protected previousMonth(): void {
    this.viewMonth.update((month) => new Date(month.getFullYear(), month.getMonth() - 1, 1));
  }

  protected nextMonth(): void {
    this.viewMonth.update((month) => new Date(month.getFullYear(), month.getMonth() + 1, 1));
  }

  protected selectDay(day: CalendarDay): void {
    if (day.isDisabled) {
      return;
    }
    const current = parseDateTimeLocal(this.value()) ?? new Date();
    const updated = new Date(day.date);
    updated.setHours(current.getHours(), current.getMinutes(), 0, 0);
    this.value.set(toDateTimeLocalValue(updated));
  }

  protected setTime(time: string): void {
    const [hour, minute] = time.split(':').map(Number);
    const current = parseDateTimeLocal(this.value()) ?? new Date();
    current.setHours(hour, minute, 0, 0);
    this.value.set(toDateTimeLocalValue(current));
  }

  protected clear(): void {
    this.value.set('');
  }

  protected dayAriaLabel(day: CalendarDay): string {
    return this.dayLabelFormatter().format(day.date);
  }

  /**
   * A three-rung legibility ladder, expressed as roles rather than as greys. It used to read
   * slate-700 < slate-600 < slate-200, which only makes sense downwards from a dark ground — in
   * light mode every rung would have inverted and the least important day would have become the
   * most prominent one. disabled < muted < body holds its order in both modes by construction.
   */
  protected dayClass(day: CalendarDay): string {
    if (day.isDisabled) {
      return 'cursor-not-allowed text-fg-disabled';
    }
    if (day.isSelected) {
      return 'bg-accent-solid text-on-accent';
    }
    if (!day.inCurrentMonth) {
      return 'text-fg-muted hover:bg-surface-inset';
    }
    if (day.isToday) {
      return 'text-accent-fg hover:bg-surface-inset';
    }
    return 'text-fg-body hover:bg-surface-inset';
  }
}
