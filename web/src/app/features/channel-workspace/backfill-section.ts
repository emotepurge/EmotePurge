import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';

import { BackfillOption } from '../../core/channels/backfill.model';
import { BackfillService } from '../../core/channels/backfill.service';
import { isUnknownOutcome } from '../../core/http/unknown-outcome';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { pluralKey } from '../../core/i18n/plural';
import { toLocale } from '../../core/i18n/locale';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveReload } from '../../core/live/live-reload';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { Button } from '../../shared/ui/button';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';

/** Same burst window the other live-reloading pages use. */
const STATUS_RELOAD_DEBOUNCE_MS = 500;

const REASON_KEYS: Record<string, string> = {
  no_days_before_counting: 'backfill.reasons.noDaysBeforeCounting',
};
const UNKNOWN_REASON_KEY = 'backfill.reasons.unknown';
const CHANNEL_EXCLUDED_KEY = 'backfill.errors.channelExcluded';

/**
 * The translation key for a failed start. The generic mapping, except that `channel_excluded` gets a
 * sentence about the backfill instead of the join flow's "This channel cannot be added."
 */
export function backfillStartErrorKey(error: HttpErrorResponse): string {
  const code = (error.error as { errorCode?: string } | null)?.errorCode;
  return code === 'channel_excluded' ? CHANNEL_EXCLUDED_KEY : apiErrorTranslationKey(error);
}

/** The day before an exclusive upper bound (`windowTo` is the day counting started). */
export function lastDayOf(exclusiveEnd: string): string {
  const date = new Date(`${exclusiveEnd}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() - 1);
  return date.toISOString().slice(0, 10);
}

interface OptionView {
  months: number;
  from: string;
  /** Inclusive last day. */
  to: string;
  days: number;
  available: boolean;
  reasonKey: string | null;
}

interface SetView {
  id: string;
  label: string;
  capacity: number | null;
  isActive: boolean;
}

/**
 * The start half of the chat-log backfill (spec 2026-10-09, §7): what the numbers are, which of the
 * channel's 7TV sets to match against, how far back to go, and the start button. Progress, cancel
 * and the last run's result belong to the second half.
 */
@Component({
  selector: 'app-backfill-section',
  imports: [Button, NoticeBanner, SkeletonRows, TranslocoPipe],
  template: `
    <section class="flex flex-col gap-4" aria-labelledby="backfill-heading">
      <h3 id="backfill-heading" class="text-base font-semibold">
        {{ 'backfill.title' | transloco }}
      </h3>

      @if (statusResource.hasValue()) {
        @let status = statusResource.value();
        <div class="flex flex-col gap-2 text-sm text-fg-secondary">
          <p>
            {{ 'backfill.explain.source' | transloco }}
            <a
              class="text-accent-fg underline underline-offset-4 transition hover:text-fg"
              [href]="status.archive.url"
              target="_blank"
              rel="noopener noreferrer"
              >{{ status.archive.name
              }}<span class="sr-only"> {{ 'common.opensInNewTab' | transloco }}</span></a
            >.
          </p>
          <ul class="list-disc pl-5">
            <li>{{ 'backfill.explain.inaccurate' | transloco }}</li>
            <li>{{ 'backfill.explain.namesOnly' | transloco }}</li>
            <li>{{ 'backfill.explain.renamed' | transloco }}</li>
            <li>{{ 'backfill.explain.gaps' | transloco }}</li>
            <li>{{ 'backfill.explain.oneSetPerDay' | transloco }}</li>
          </ul>
        </div>

        <div class="flex flex-col gap-1">
          <label for="backfill-set" class="text-sm font-medium">
            {{ 'backfill.setLabel' | transloco }}
          </label>
          <select
            id="backfill-set"
            class="app-input max-w-md"
            [disabled]="!setPickerEnabled()"
            [attr.aria-describedby]="'backfill-set-note'"
            (change)="onSetChange($event)"
          >
            @if (selectedSetId() === null) {
              <option value="" selected>{{ 'backfill.setPlaceholder' | transloco }}</option>
            }
            @for (set of offeredSets(); track set.id) {
              <option [value]="set.id" [selected]="set.id === selectedSetId()">
                {{ set.label }}{{ set.capacity !== null ? ' (' + set.capacity + ')' : ''
                }}{{ set.isActive ? ' · ' + ('backfill.setActiveMarker' | transloco) : '' }}
              </option>
            }
          </select>
          <p id="backfill-set-note" class="text-xs text-fg-muted">
            {{ 'backfill.setNote' | transloco }}
          </p>
          @if (setListErrorKey(); as key) {
            <app-notice-banner variant="warning">{{ key | transloco }}</app-notice-banner>
          }
        </div>

        <fieldset class="flex flex-col gap-1">
          <legend class="mb-1 text-sm font-medium">{{ 'backfill.windowLabel' | transloco }}</legend>
          @for (option of optionViews(); track option.months) {
            <label class="flex flex-wrap items-center gap-x-2 py-1 text-sm text-fg-secondary">
              <input
                type="radio"
                class="h-4 w-4 accent-accent-solid"
                name="backfill-months"
                [checked]="option.months === selectedMonths()"
                [disabled]="!option.available"
                (change)="selectedMonthsChoice.set(option.months)"
              />
              <span>{{
                pluralKey(option.months, 'backfill.option.months')
                  | transloco: { count: option.months }
              }}</span>
              <span class="text-fg-muted">
                {{
                  'backfill.option.range'
                    | transloco: { from: formatDay(option.from), to: formatDay(option.to) }
                }}
                ·
                {{
                  pluralKey(option.days, 'backfill.option.days') | transloco: { count: option.days }
                }}
              </span>
              @if (option.reasonKey; as reasonKey) {
                <span class="text-fg-muted">· {{ reasonKey | transloco }}</span>
              }
            </label>
          }
        </fieldset>

        @if (status.activeRun) {
          <p class="text-sm" role="status">{{ 'backfill.runActive' | transloco }}</p>
        }

        <div class="flex flex-col items-start gap-2">
          <button
            type="button"
            appButton="primary"
            [disabled]="!canStart() || starting()"
            (click)="start()"
          >
            {{ 'backfill.start' | transloco }}
          </button>
          @if (startErrorKey(); as key) {
            <app-notice-banner variant="error" class="block">{{
              key | transloco
            }}</app-notice-banner>
          }
        </div>
      } @else if (statusResource.error()) {
        <app-notice-banner variant="error">{{ statusErrorKey() | transloco }}</app-notice-banner>
      } @else {
        <app-skeleton-rows [count]="3" />
      }
    </section>
  `,
})
export class BackfillSection {
  readonly channelName = input.required<string>();

  private readonly backfillService = inject(BackfillService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  private readonly languageService = inject(LanguageService);

  protected readonly statusResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.backfillService.getStatus(params),
  });

  private readonly setListResource = rxResource({
    params: () => this.channelName(),
    stream: ({ params }) => this.emoteSetService.listChannelEmoteSets(params),
  });

  /** The user's own pick; `undefined` = never touched, so the preselection applies. */
  private readonly setChoice = signal<string | undefined>(undefined);
  protected readonly selectedMonthsChoice = signal<number | null>(null);
  protected readonly starting = signal(false);
  protected readonly startErrorKey = signal<string | null>(null);
  protected readonly pluralKey = pluralKey;

  /** The active set, or `null` while no sync has completed (`""` on the wire). */
  readonly preselectedSetId = computed(() => {
    if (!this.statusResource.hasValue()) {
      return null;
    }
    return this.statusResource.value().activeEmoteSetId || null;
  });

  /** The channel's non-personal sets; empty until the list is on hand. */
  readonly offeredSets = computed<SetView[]>(() => {
    if (!this.setListResource.hasValue()) {
      return [];
    }
    return this.setListResource
      .value()
      .sets.filter((set) => !set.isPersonal)
      .map((set) => ({
        id: set.id,
        label: set.name || set.id,
        capacity: set.capacity,
        isActive: set.isActive,
      }));
  });

  /** Only an offered set can be selected, so a stale pick or a personal active set yields `null`. */
  readonly selectedSetId = computed(() => {
    const candidate = this.setChoice() ?? this.preselectedSetId();
    return candidate !== null && this.offeredSets().some((set) => set.id === candidate)
      ? candidate
      : null;
  });

  readonly setListLoaded = computed(() => this.setListResource.hasValue());

  protected readonly setPickerEnabled = computed(() => this.setListLoaded());

  /** The list's own error sentence while it is unreadable (503) — picker and start stay locked. */
  readonly setListErrorKey = computed(() => {
    const error = this.setListResource.error();
    if (!error || this.setListResource.hasValue()) {
      return null;
    }
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : 'errors.generic';
  });

  readonly optionViews = computed<OptionView[]>(() => {
    if (!this.statusResource.hasValue()) {
      return [];
    }
    return this.statusResource.value().options.map((option) => this.toOptionView(option));
  });

  /** The user's pick if it is still available, otherwise the first available option. */
  readonly selectedMonths = computed(() => {
    const options = this.optionViews();
    const choice = this.selectedMonthsChoice();
    const chosen = options.find((option) => option.months === choice && option.available);
    return (chosen ?? options.find((option) => option.available))?.months ?? null;
  });

  readonly canStart = computed(
    () =>
      this.statusResource.hasValue() &&
      this.statusResource.value().activeRun === null &&
      this.selectedMonths() !== null &&
      this.selectedSetId() !== null &&
      this.setListLoaded(),
  );

  protected readonly statusErrorKey = computed(() => {
    const error = this.statusResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : 'errors.generic';
  });

  constructor() {
    liveReload(
      computed(() => channelLiveUrl(this.channelName())),
      {
        accept: [LIVE_EVENT_TYPES.backfillProgress],
        debounceMs: STATUS_RELOAD_DEBOUNCE_MS,
      },
    ).subscribe(() => this.statusResource.reload());
  }

  protected onSetChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.setChoice.set(value === '' ? undefined : value);
  }

  protected formatDay(isoDay: string): string {
    return new Date(`${isoDay}T00:00:00Z`).toLocaleDateString(
      toLocale(this.languageService.lang()),
      { timeZone: 'UTC', day: '2-digit', month: '2-digit', year: 'numeric' },
    );
  }

  protected start(): void {
    const emoteSetId = this.selectedSetId();
    const months = this.selectedMonths();
    if (!this.canStart() || this.starting() || emoteSetId === null || months === null) {
      return;
    }
    this.starting.set(true);
    this.startErrorKey.set(null);
    this.backfillService.start(this.channelName(), emoteSetId, months).subscribe({
      next: () => {
        this.starting.set(false);
        this.statusResource.reload();
      },
      error: (error: HttpErrorResponse) => {
        this.starting.set(false);
        this.startErrorKey.set(backfillStartErrorKey(error));
        const code = (error.error as { errorCode?: string } | null)?.errorCode;
        // A race lost to another manager, or an answer that may have been lost after the commit:
        // either way the status says what is really queued.
        if (code === 'backfill_already_active' || isUnknownOutcome(error.status)) {
          this.statusResource.reload();
        }
      },
    });
  }

  private toOptionView(option: BackfillOption): OptionView {
    return {
      months: option.months,
      from: option.windowFrom,
      to: lastDayOf(option.windowTo),
      days: option.days,
      available: option.available,
      reasonKey: option.available ? null : (REASON_KEYS[option.reason ?? ''] ?? UNKNOWN_REASON_KEY),
    };
  }
}
