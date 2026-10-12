import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AdminService } from '../../core/admin/admin.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { HealthMarker, HealthTone } from '../../shared/ui/health-marker';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { UtilizationTone, utilizationTone } from '../../shared/ui/utilization-tone';

/** Three times the worker's 60-second roster cadence — the same rule the Redis TTL follows. */
const STALE_AFTER_SECONDS = 180;

/** Bar fills are small meaning-carrying graphics — the `dot` tokens owe 3:1, not 4.5:1. */
const TONE_FILL: Record<UtilizationTone, string> = {
  success: 'bg-success-dot',
  warning: 'bg-warning-dot',
  danger: 'bg-danger-dot',
};

const TONE_TEXT: Record<UtilizationTone, string> = {
  success: 'text-fg-body',
  warning: 'text-warning-fg',
  danger: 'text-danger-fg',
};

const STATUS_TONES: Record<string, HealthTone> = {
  complete: 'ok',
  incomplete: 'warning',
  starting: 'idle',
  stale: 'warning',
  unavailable: 'danger',
  unknown: 'idle',
};

/**
 * Soll/Ist for the worker's channel roster: what the database says the bot should be in, against
 * what the worker itself reports being in. A channel that dropped out of IRC after a reconnect
 * counts nothing and says nothing — this card is the only place that discrepancy becomes visible
 * without SSH.
 *
 * Its own resource rather than fields on the monitoring page's health call: the roster is published
 * every 60 s, the health snapshot every 20 s, and folding them together would refetch this three
 * times per change.
 */
@Component({
  selector: 'app-admin-roster-card',
  imports: [HealthMarker, NoticeBanner, RouterLink, TranslocoPipe],
  template: `
    <section class="flex flex-col gap-3 border-t border-border pt-4">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h3 class="text-base font-semibold">{{ 'admin.roster.title' | transloco }}</h3>
        <app-health-marker
          [tone]="tone()"
          [label]="'admin.roster.status.' + statusKey() | transloco"
        />
      </div>

      @if (errorMessage(); as error) {
        <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
      } @else if (roster(); as data) {
        @if (!data.snapshotAvailable) {
          <app-notice-banner variant="warning">
            {{ 'admin.roster.noSnapshot' | transloco }}
          </app-notice-banner>
        } @else {
          @if (data.bootRecoveryCompleted === false) {
            <!-- The single most important caveat on this card: _desiredChannels is empty until boot
                 recovery has rejoined everything, so right after a deploy every channel is
                 legitimately "missing". Without saying so, the card cries wolf on every redeploy. -->
            <app-notice-banner variant="info">
              {{ 'admin.roster.bootRecovery' | transloco }}
            </app-notice-banner>
          }
          @if (isStale()) {
            <app-notice-banner variant="warning">
              {{ 'admin.roster.stale' | transloco: { seconds: data.ageSeconds } }}
            </app-notice-banner>
          }
          @if (data.truncated) {
            <app-notice-banner variant="warning">
              {{ 'admin.roster.truncated' | transloco }}
            </app-notice-banner>
          }

          <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.roster.tracked' | transloco }}</dt>
              <dd class="text-fg-body">{{ data.trackedChannelCount }}</dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.roster.ircConfirmed' | transloco }}</dt>
              <dd class="text-fg-body">
                {{ data.ircConfirmedCount }} / {{ data.trackedChannelCount }}
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.roster.sevenTvAcknowledged' | transloco }}</dt>
              <dd class="text-fg-body">
                {{ data.sevenTvAcknowledgedCount }} / {{ data.trackedChannelCount }}
              </dd>
            </div>
            <div class="flex justify-between gap-4 sm:block">
              <dt class="text-fg-muted">{{ 'admin.roster.worker' | transloco }}</dt>
              <dd class="text-fg-body">{{ data.workerInstanceId }}</dd>
            </div>
          </dl>

          <!-- Twitch's join headroom. Two numbers, separately labelled with where each comes from:
               one is Twitch's rule, the other is ours. Merging them into a single "capacity" would
               state our operating decision as if Twitch had made it. -->
          <div class="flex flex-col gap-1">
            <div class="flex flex-wrap items-baseline justify-between gap-2 text-sm">
              <span class="text-fg-muted">{{ 'admin.roster.joinBudget' | transloco }}</span>
              <span [class]="budgetTextClass()">
                {{ data.trackedChannelCount }} / {{ data.ceilings.twitchJoinBudgetChannels }}
              </span>
            </div>
            <div
              class="h-2 w-full overflow-hidden rounded-full bg-surface-inset"
              role="progressbar"
              [attr.aria-valuenow]="data.trackedChannelCount"
              aria-valuemin="0"
              [attr.aria-valuemax]="data.ceilings.twitchJoinBudgetChannels"
              [attr.aria-label]="'admin.roster.joinBudget' | transloco"
            >
              <div
                class="h-full rounded-full"
                [class]="budgetFillClass()"
                [style.width.%]="budgetPercent()"
              ></div>
            </div>
            @if (budgetWarningKey(); as warningKey) {
              <!-- The colour step alone must never carry the warning (WCAG 1.4.1). -->
              <p class="text-xs" [class]="budgetTextClass()">
                {{ warningKey | transloco: budgetWarningParams() }}
              </p>
            }
            <p class="text-xs text-fg-muted">
              {{ 'admin.roster.joinBudgetHint' | transloco }}
            </p>
            <p class="text-xs text-fg-muted">
              {{
                'admin.roster.twitchLimitHint'
                  | transloco: { limit: data.ceilings.twitchConcurrentChannelLimit }
              }}
            </p>
          </div>

          @if (missingFromIrc().length > 0) {
            <div class="flex flex-col gap-1">
              <p class="text-sm text-warning-fg">
                {{ 'admin.roster.missingIrc' | transloco: { count: data.missingFromIrcTotal } }}
              </p>
              <ul class="flex flex-wrap gap-x-3 gap-y-1 text-sm">
                @for (name of missingFromIrc(); track name) {
                  <li>
                    <a
                      [routerLink]="['/admin/channels', name]"
                      class="text-accent-fg underline-offset-2 hover:underline"
                    >
                      #{{ name }}
                    </a>
                  </li>
                }
              </ul>
            </div>
          }

          @if (missingFromSevenTv().length > 0) {
            <div class="flex flex-col gap-1">
              <p class="text-sm text-warning-fg">
                {{
                  'admin.roster.missingSevenTv' | transloco: { count: data.missingFromSevenTvTotal }
                }}
              </p>
              <ul class="flex flex-wrap gap-x-3 gap-y-1 text-sm">
                @for (name of missingFromSevenTv(); track name) {
                  <li>
                    <a
                      [routerLink]="['/admin/channels', name]"
                      class="text-accent-fg underline-offset-2 hover:underline"
                    >
                      #{{ name }}
                    </a>
                  </li>
                }
              </ul>
            </div>
          }

          @if (unknownToDatabase().length > 0) {
            <div class="flex flex-col gap-1">
              <p class="text-sm text-warning-fg">
                {{
                  'admin.roster.unknownToDatabase'
                    | transloco: { count: data.unknownToDatabaseTotal }
                }}
              </p>
              <ul class="flex flex-wrap gap-x-3 gap-y-1 text-sm text-fg-secondary">
                @for (name of unknownToDatabase(); track name) {
                  <li>#{{ name }}</li>
                }
              </ul>
            </div>
          }
        }
      }
    </section>
  `,
})
export class AdminRosterCard {
  private readonly adminService = inject(AdminService);

  // No defaultValue, same reasoning as the health resource: "not loaded yet" and "an all-zero
  // roster" are different states and must not render alike.
  private readonly rosterResource = rxResource({
    stream: () => this.adminService.getRoster(),
  });

  /** Public: the monitoring page reads the channel count for its activity heading from here. */
  readonly roster = computed(() =>
    this.rosterResource.hasValue() ? this.rosterResource.value() : undefined,
  );

  protected readonly errorMessage = computed(() => {
    const error = this.rosterResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  /** Older than three publish intervals — at which point the TTL is about to drop the key anyway. */
  protected readonly isStale = computed(
    () => (this.roster()?.ageSeconds ?? 0) > STALE_AFTER_SECONDS,
  );

  /**
   * Uncapped share of the join budget, null when the ceiling is missing or zero: without a
   * denominator there is no utilization to grade, and a green bar would claim a health we cannot
   * know. The tone must see values past 100 — a budget at 120 % grades `danger`, the capped
   * `budgetPercent` below is only the bar width.
   */
  private readonly rawBudgetPercent = computed<number | null>(() => {
    const data = this.roster();
    const budget = data?.ceilings.twitchJoinBudgetChannels;
    if (!data || !budget) {
      return null;
    }
    return (data.trackedChannelCount / budget) * 100;
  });

  protected readonly budgetTone = computed<UtilizationTone | null>(() => {
    const percent = this.rawBudgetPercent();
    return percent === null ? null : utilizationTone(percent);
  });

  protected readonly budgetFillClass = computed(() => {
    const tone = this.budgetTone();
    return tone === null ? 'bg-accent' : TONE_FILL[tone];
  });

  protected readonly budgetTextClass = computed(() => {
    const tone = this.budgetTone();
    return tone === null ? 'text-fg-body' : TONE_TEXT[tone];
  });

  /**
   * Text line under the bar from `warning` upwards — three states: nearing the budget, critically
   * close, and past it. Null in the healthy (or denominator-less) state, which un-renders the line.
   */
  protected readonly budgetWarningKey = computed<string | null>(() => {
    const data = this.roster();
    if (!data?.ceilings.twitchJoinBudgetChannels) {
      return null;
    }
    if (data.trackedChannelCount > data.ceilings.twitchJoinBudgetChannels) {
      return 'admin.roster.joinBudgetExceeded';
    }
    switch (this.budgetTone()) {
      case 'danger':
        return 'admin.roster.joinBudgetCritical';
      case 'warning':
        return 'admin.roster.joinBudgetWarning';
      default:
        return null;
    }
  });

  /** One params object serving all three warning keys — each key picks what it interpolates. */
  protected readonly budgetWarningParams = computed(() => {
    const data = this.roster();
    return {
      percent: Math.round(this.rawBudgetPercent() ?? 0),
      tracked: data?.trackedChannelCount ?? 0,
      budget: data?.ceilings.twitchJoinBudgetChannels ?? 0,
    };
  });

  /** Capped so an over-budget channel count cannot overflow the bar out of its track. */
  protected readonly budgetPercent = computed(() => {
    const data = this.roster();
    if (!data?.ceilings.twitchJoinBudgetChannels) {
      return 0;
    }
    return Math.min(100, (data.trackedChannelCount / data.ceilings.twitchJoinBudgetChannels) * 100);
  });

  protected readonly missingFromIrc = computed(() => this.roster()?.missingFromIrc ?? []);
  protected readonly missingFromSevenTv = computed(() => this.roster()?.missingFromSevenTv ?? []);
  protected readonly unknownToDatabase = computed(() => this.roster()?.unknownToDatabase ?? []);

  protected readonly statusKey = computed(() => {
    const data = this.roster();
    if (!data) {
      return 'unknown';
    }
    if (!data.snapshotAvailable) {
      return 'unavailable';
    }
    // Boot recovery outranks every deficit: during it the deficits are expected, and reporting them
    // as a problem would train an admin to ignore this badge after every deploy.
    if (data.bootRecoveryCompleted === false) {
      return 'starting';
    }
    if (this.isStale()) {
      return 'stale';
    }
    const complete =
      data.ircConfirmedCount === data.trackedChannelCount &&
      data.sevenTvAcknowledgedCount === data.trackedChannelCount &&
      (data.unknownToDatabaseTotal ?? 0) === 0;
    return complete ? 'complete' : 'incomplete';
  });

  protected readonly tone = computed<HealthTone>(() => STATUS_TONES[this.statusKey()]);

  constructor() {
    // No debounce: the publisher writes once a minute, so this can never outpace the refetch.
    liveEvents(ADMIN_LIVE_URL, [LIVE_EVENT_TYPES.workerRoster]).subscribe(() =>
      this.rosterResource.reload(),
    );
  }

  reload(): void {
    this.rosterResource.reload();
  }
}
