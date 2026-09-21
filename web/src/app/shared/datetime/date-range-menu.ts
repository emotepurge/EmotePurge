import { DOCUMENT } from '@angular/common';
import {
  Component,
  ElementRef,
  computed,
  inject,
  input,
  model,
  signal,
  viewChild,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { Button } from '../ui/button';
import { Popover } from '../ui/popover';

export type DateRangePreset = '0' | '7' | '30' | 'all' | 'set-observed' | 'custom';

export interface PresetOption {
  value: DateRangePreset;
  labelKey: string;
}

const PRESET_OPTIONS: PresetOption[] = [
  { value: '0', labelKey: 'dateRange.presetToday' },
  { value: '7', labelKey: 'dateRange.preset7Days' },
  { value: '30', labelKey: 'dateRange.preset30Days' },
  { value: 'all', labelKey: 'dateRange.presetAll' },
  { value: 'set-observed', labelKey: 'dateRange.presetSetObserved' },
  { value: 'custom', labelKey: 'dateRange.presetCustom' },
];

/** A closed `[from, to]` range of ISO dates (`yyyy-MM-dd`), both inclusive. */
export interface IsoDateRange {
  from: string;
  to: string;
}

/**
 * The presets the menu offers (spec #200, 8.5, AK 61): `'set-observed'` only when the host has a
 * range for it — i.e. the chosen emote set was observed as active at least once. An option that
 * could only ever pick "nothing" is not an option; it is absent, not disabled, because there is no
 * reason to give that would not just restate the set's own history.
 */
export function dateRangePresetOptions(hasSetObservedRange: boolean): PresetOption[] {
  return hasSetObservedRange
    ? PRESET_OPTIONS
    : PRESET_OPTIONS.filter((option) => option.value !== 'set-observed');
}

/**
 * The range the `'set-observed'` preset selects (spec #200, 8.5, AK 61): the set's **youngest**
 * observation interval — `fromUtc` … `toUtc ?? today`, as dates. Deliberately "observed", not
 * "active": the log only knows when *this* app saw the set as the channel's active one (spec 4.3).
 *
 * `null` when the set has no interval at all. The start is clamped to the widest span the usage
 * endpoints accept ({@link MAX_RANGE_DAYS} before the end) — an interval longer than a year would
 * otherwise turn the preset into a `range_too_large` error — and the end never reaches past today.
 */
export function setObservedRange(
  intervals: readonly { fromUtc: string; toUtc: string | null }[],
  today: string = toIsoDate(new Date()),
): IsoDateRange | null {
  let youngest: { fromUtc: string; toUtc: string | null } | null = null;
  for (const interval of intervals) {
    if (youngest === null || interval.fromUtc > youngest.fromUtc) {
      youngest = interval;
    }
  }
  if (youngest === null) {
    return null;
  }

  const rawTo = youngest.toUtc?.slice(0, 10) ?? today;
  const to = rawTo < today ? rawTo : today;
  const rawFrom = youngest.fromUtc.slice(0, 10);
  const earliestAllowed = shiftIsoDate(to, -MAX_RANGE_DAYS);
  const from = earliestAllowed && rawFrom < earliestAllowed ? earliestAllowed : rawFrom;
  return { from: from > to ? to : from, to };
}

/**
 * How far back "all time" may actually reach. The usage-totals endpoint rejects a wider span with
 * `RangeTooLarge` (`maxRangeDays = 366` in `UsageStatsEndpoints.cs`), so this is a contract, not a
 * preference — one day inside it, because `daysAgo` counts in local time while `toIsoDate` prints
 * UTC and the two can disagree by a day across a date boundary.
 */
export const MAX_RANGE_DAYS = 365;

/**
 * Lower bound for the "all time" preset. Prefers the host's real starting point (for the usage grid:
 * when this channel's tracking began) and otherwise reaches back as far as the endpoint allows —
 * which, for any channel tracked for less than a year, returns exactly the same rows.
 *
 * The floor exists because `earliest` arrives from a second request and is unknown at first paint.
 * It used to double as licence to fetch against it: the page fired its data requests immediately
 * and re-fired them once the tracking start landed. That cost a 366-day aggregate per page open
 * whose answer was always discarded — and, worse, left two answers in flight for the same readout,
 * where the slower one could overwrite the faster (see UsageStatsPage.rangeResolved). The usage
 * grid now waits, trading one round trip at first paint for a request that is right the first time.
 * The floor still stands for anyone who reads from() before that.
 *
 * Once a channel has been tracked for longer than {@link MAX_RANGE_DAYS}, "all time" quietly stops
 * being all time. Nothing is that old yet — the project started in 2026-07 — but the day it is,
 * this needs the server-side range guard lifted for an open-ended `from`, not a bigger number here.
 */
export function allTimeStart(earliest: string | null): string {
  const floor = toIsoDate(daysAgo(MAX_RANGE_DAYS));
  return earliest && earliest > floor ? earliest : floor;
}

/** ISO date-only string (`yyyy-MM-dd`) — the format the native date inputs and the API both speak. */
export function toIsoDate(date: Date): string {
  return date.toISOString().slice(0, 10);
}

export function daysAgo(days: number): Date {
  const date = new Date();
  date.setDate(date.getDate() - days);
  return date;
}

/** Empty for anything that is not a parseable ISO date — clearing a date field yields `''`, and an
 *  Invalid Date would take `toISOString()` down with it mid-change-detection. */
function shiftIsoDate(iso: string, days: number): string {
  const parsed = new Date(`${iso}T00:00:00Z`);
  if (Number.isNaN(parsed.getTime())) {
    return '';
  }

  parsed.setUTCDate(parsed.getUTCDate() + days);
  return toIsoDate(parsed);
}

/**
 * Time-range picker as a single dropdown: one toolbar slot instead of a four-segment control plus a
 * trigger-less popover hanging off it.
 *
 * Three things this buys over the segmented control it replaces. The **custom range stops being a
 * trick**: it was a segment that opened a panel, and reopening the panel meant clicking the already
 * selected segment again (SegmentedControl grew a `reselected` output for nothing else). It is now
 * an ordinary entry that swaps the panel's contents. The **selected range stays legible** — the
 * trigger names it, where before a scrolled-away toolbar was the only place it existed. And
 * **arrow keys no longer refetch**: the segmented control selects on focus, so walking it fired one
 * full reload per keypress. APG allows a radiogroup to decouple selection from focus precisely when
 * selecting has a side effect, so arrows move focus and Enter/Space commits.
 *
 * The earlier worry that a trigger button would wrap the German toolbar onto a third row (see the
 * 2026-08-01 decision log entry) does not apply: this replaces the segmented control rather than
 * adding to it, so the toolbar gets narrower, not wider.
 */
@Component({
  selector: 'app-date-range-menu',
  imports: [Button, Popover, TranslocoPipe],
  template: `
    <div class="relative" data-popover-anchor>
      <button
        #trigger
        type="button"
        appButton="neutral"
        class="whitespace-nowrap"
        aria-haspopup="dialog"
        [attr.aria-expanded]="isOpen()"
        (click)="toggle()"
      >
        {{ 'dateRange.label' | transloco }}:
        @if (preset() === 'custom') {
          {{ formattedRange() }}
        } @else {
          {{ selectedLabelKey() | transloco }}
        }
        <span aria-hidden="true" class="ml-1 text-fg-muted">▾</span>
      </button>

      @if (isOpen()) {
        <app-popover [ariaLabel]="'dateRange.menuLabel' | transloco" (closed)="close()">
          <div
            role="radiogroup"
            [attr.aria-label]="'dateRange.label' | transloco"
            class="flex flex-col gap-0.5 p-1"
          >
            <!-- min-h-11 on touch, tighter from sm up (§10: 44 px comfort target for pointer-coarse
                 rows, 24 px is only the floor). -->
            @for (option of presetOptions(); track option.value; let index = $index) {
              <button
                type="button"
                role="radio"
                [attr.aria-checked]="preset() === option.value"
                [tabindex]="tabIndexFor(index)"
                [class]="
                  'flex min-h-11 items-center justify-between gap-3 rounded px-3 text-left text-sm transition sm:min-h-9 ' +
                  (preset() === option.value
                    ? 'bg-accent-selected font-medium text-on-accent'
                    : 'text-fg-body hover:bg-surface-inset')
                "
                (click)="select(option.value)"
                (keydown)="onKeydown($event, index)"
              >
                <span>{{ option.labelKey | transloco }}</span>
                @if (preset() === option.value) {
                  <span aria-hidden="true">✓</span>
                }
              </button>
            }
          </div>

          <!-- Same panel, swapped contents: choosing "custom" reveals the fields instead of opening
               a second surface, so there is only ever one thing on screen to dismiss. -->
          @if (preset() === 'custom') {
            <div class="border-t border-border p-3">
              <label for="date-range-from" class="block text-xs text-fg-muted">
                {{ 'dateRange.from' | transloco }}
              </label>
              <!-- The two fields bound each other rather than sitting free: the endpoint rejects a
                   span wider than its guard, and a picker that offers the day is a worse teacher
                   than one that greys it out. Typed input can still get past this (a date field
                   marks an out-of-range value invalid but still emits it), so the host's error
                   handling stays the actual safety net. -->
              <input
                id="date-range-from"
                type="date"
                [min]="fromMin()"
                [max]="to()"
                [value]="from()"
                (change)="from.set($any($event.target).value)"
                class="app-input-sm mt-1 w-full"
              />
              <label for="date-range-to" class="mt-3 block text-xs text-fg-muted">
                {{ 'dateRange.to' | transloco }}
              </label>
              <input
                id="date-range-to"
                type="date"
                [min]="from()"
                [max]="toMax()"
                [value]="to()"
                (change)="to.set($any($event.target).value)"
                class="app-input-sm mt-1 w-full"
              />
              <div class="mt-3 flex justify-end">
                <button type="button" appButton="primary" (click)="close()">
                  {{ 'datetimePicker.done' | transloco }}
                </button>
              </div>
            </div>
          }
        </app-popover>
      }
    </div>
  `,
})
export class DateRangeMenu {
  /** ISO date-only strings (`yyyy-MM-dd`), same format the native date inputs speak. */
  readonly from = model.required<string>();
  readonly to = model.required<string>();
  readonly preset = model.required<DateRangePreset>();
  /**
   * ISO date the host's data actually starts at (for the usage grid: when this channel's tracking
   * began). Bounds the "all time" preset — see {@link allTimeStart} for what null falls back to.
   */
  readonly earliest = input<string | null>(null);
  /**
   * What the `'set-observed'` preset selects — see {@link setObservedRange}. `null` (the default,
   * and every host that has no emote set to speak of) leaves the preset out of the list entirely.
   */
  readonly setObservedRange = input<IsoDateRange | null>(null);

  private readonly languageService = inject(LanguageService);
  private readonly document = inject(DOCUMENT);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  protected readonly presetOptions = computed(() =>
    dateRangePresetOptions(this.setObservedRange() !== null),
  );
  protected readonly isOpen = signal(false);
  // Roving tabindex: null means "follow the selection", which is where focus should land the first
  // time the group is tabbed into.
  private readonly focusedIndex = signal<number | null>(null);

  // Each bound is the other field's value walked to the edge of what the endpoint answers, so the
  // custom range cannot be dragged wider than MAX_RANGE_DAYS from either side. The end date also
  // stops at today — usage in the future is not a thing anyone is asking about.
  protected readonly fromMin = computed(() => shiftIsoDate(this.to(), -MAX_RANGE_DAYS));
  protected readonly toMax = computed(() => {
    const today = toIsoDate(new Date());
    const widest = shiftIsoDate(this.from(), MAX_RANGE_DAYS);
    return widest && widest < today ? widest : today;
  });

  protected readonly selectedLabelKey = computed(
    () => PRESET_OPTIONS.find((option) => option.value === this.preset())?.labelKey ?? '',
  );

  // LOCALE_ID is bootstrap-time static and cannot follow a runtime language switch, so dates go
  // through toLocale() — same as the pages that host this.
  protected readonly formattedRange = computed(() => {
    const locale = toLocale(this.languageService.lang());
    const format = (iso: string) =>
      new Date(`${iso}T00:00:00`).toLocaleDateString(locale, {
        day: '2-digit',
        month: '2-digit',
        year: '2-digit',
      });
    return `${format(this.from())}–${format(this.to())}`;
  });

  protected toggle(): void {
    if (this.isOpen()) {
      this.close();
      return;
    }
    this.isOpen.set(true);
  }

  protected close(): void {
    if (!this.isOpen()) {
      return;
    }
    // Focus would otherwise fall to <body> together with the panel that held it.
    const hadFocus = this.elementRef.nativeElement.contains(this.document.activeElement);
    this.isOpen.set(false);
    this.focusedIndex.set(null);
    if (hadFocus) {
      this.trigger()?.nativeElement.focus();
    }
  }

  protected select(value: DateRangePreset): void {
    this.preset.set(value);
    if (value === 'custom') {
      // Stays open: the fields the user just asked for are inside this panel.
      this.focusedIndex.set(this.presetOptions().length - 1);
      return;
    }

    if (value === 'set-observed') {
      const range = this.setObservedRange();
      // Only offered while there is a range (dateRangePresetOptions), so this is a narrowing, not a
      // path anyone can reach with `null`.
      if (range) {
        this.from.set(range.from);
        this.to.set(range.to);
      }
      this.close();
      return;
    }

    this.from.set(
      value === 'all' ? allTimeStart(this.earliest()) : toIsoDate(daysAgo(Number(value))),
    );
    this.to.set(toIsoDate(new Date()));
    this.close();
  }

  protected tabIndexFor(index: number): number {
    const active = this.focusedIndex() ?? this.selectedIndex();
    return index === active ? 0 : -1;
  }

  protected onKeydown(event: KeyboardEvent, index: number): void {
    let delta: number;
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        delta = 1;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        delta = -1;
        break;
      default:
        return;
    }

    event.preventDefault();
    // Focus only — committing here would refetch the page's data on every keypress, which is the
    // wart this control was built to remove. Enter/Space fire click and go through select().
    const count = this.presetOptions().length;
    const next = (index + delta + count) % count;
    this.focusedIndex.set(next);
    const group = (event.currentTarget as HTMLElement).closest('[role="radiogroup"]');
    group?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
  }

  private selectedIndex(): number {
    const index = this.presetOptions().findIndex((option) => option.value === this.preset());
    return index === -1 ? 0 : index;
  }
}
