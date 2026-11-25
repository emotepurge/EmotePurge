import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { skip, tap } from 'rxjs';
import { TranslocoPipe } from '@jsverse/transloco';

import { IrcUnjoinedChannel } from '../../core/admin/admin.model';
import { AdminService } from '../../core/admin/admin.service';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { pollWhileVisible } from '../../core/live/poll-while-visible';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import { HealthMarker, HealthTone } from '../../shared/ui/health-marker';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import {
  UtilizationThresholds,
  UtilizationTone,
  utilizationTone,
} from '../../shared/ui/utilization-tone';

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

/** Refresh while the card is visible, in addition to the 60 s `worker.roster` push. */
const POLL_INTERVAL_MS = 60_000;

/** How often the visible age is recomputed; coarse on purpose, the threshold is minutes. */
const AGE_TICK_MS = 5_000;

/** Our admission cap warns at 90 % and alarms at 100 % — it loses no data, so no earlier alarm. */
const CAPACITY_THRESHOLDS: UtilizationThresholds = { warnAt: 90, criticalAt: 100 };

const KNOWN_REASONS = new Set([
  'noConfirmation',
  'channelSuspended',
  'concurrentChannelLimit',
  'banned',
]);

const STATUS_TONES: Record<string, HealthTone> = {
  complete: 'ok',
  failing: 'danger',
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

      @if (roster(); as capacityData) {
        <!-- Our own admission cap is the denominator. Twitch's 100 is reference text only: it is
             not binding for the anonymous connection, and stating our decision as Twitch's rule
             would mislead. -->
        <div class="flex flex-col gap-1" data-testid="roster-capacity">
          <div class="flex flex-wrap items-baseline justify-between gap-2 text-sm">
            <span class="text-fg-muted">{{ 'admin.roster.capacity.label' | transloco }}</span>
            <span [class]="capacityTextClass()">
              {{ capacityData.trackedChannelCount }} / {{ capacityData.ceilings.maxActiveChannels }}
            </span>
          </div>
          <div
            class="h-2 w-full overflow-hidden rounded-full bg-surface-inset"
            role="progressbar"
            [attr.aria-valuenow]="capacityData.trackedChannelCount"
            aria-valuemin="0"
            [attr.aria-valuemax]="capacityData.ceilings.maxActiveChannels"
            [attr.aria-label]="'admin.roster.capacity.label' | transloco"
          >
            <div
              class="h-full rounded-full"
              [class]="capacityFillClass()"
              [style.width.%]="capacityPercent()"
            ></div>
          </div>
          @if (capacityWarningKey(); as warningKey) {
            <!-- The colour step alone must never carry the warning (WCAG 1.4.1). -->
            <p class="text-xs" [class]="capacityTextClass()">
              {{ warningKey | transloco: capacityWarningParams() }}
              <a
                routerLink="/admin/logs"
                [queryParams]="{ q: 'event:ChannelCapacityReached', range: '7d' }"
                class="inline-flex min-h-11 items-center px-1 text-accent-fg underline-offset-2 hover:underline lg:min-h-6"
              >
                {{ 'admin.roster.capacity.rejectionsLink' | transloco }}
              </a>
            </p>
          }
          <p class="text-xs text-fg-muted">{{ 'admin.roster.capacity.hint' | transloco }}</p>
          <p class="text-xs text-fg-muted">
            {{
              'admin.roster.capacity.twitchLimitHint'
                | transloco: { limit: capacityData.ceilings.twitchConcurrentChannelLimit }
            }}
          </p>
        </div>
      }

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
              {{ 'admin.roster.stale' | transloco: { seconds: staleSeconds() } }}
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

          @if (data.joinHealth?.status === 'failing') {
            <app-notice-banner variant="error">
              {{
                'admin.roster.joinFailing'
                  | transloco: { count: data.joinHealth.alarmingTotal ?? 0 }
              }}
            </app-notice-banner>
          } @else if (data.joinHealth?.status === 'unavailable') {
            <app-notice-banner variant="warning">
              {{ 'admin.roster.joinUnavailable' | transloco }}
            </app-notice-banner>
          }

          @if (unjoinedRows().length > 0) {
            <div class="flex flex-col gap-1" data-testid="roster-unjoined">
              <p class="text-sm text-warning-fg">
                {{ 'admin.roster.unjoined.heading' | transloco: { count: unjoinedTotal() } }}
              </p>
              <ul class="flex flex-col gap-1 text-sm">
                @for (row of unjoinedRows(); track row.name) {
                  <li class="flex flex-wrap items-baseline gap-x-2">
                    <a
                      [routerLink]="['/admin/channels', row.name]"
                      class="text-accent-fg underline-offset-2 hover:underline"
                    >
                      #{{ row.name }}
                    </a>
                    <span class="text-fg-body">{{ row.reasonKey | transloco }}</span>
                    <span class="text-fg-muted">
                      {{ row.durationKey | transloco: { value: row.durationValue } }}
                    </span>
                    <span [class]="row.alarming ? 'text-danger-fg' : 'text-fg-muted'">
                      {{ row.stateKey | transloco }}
                    </span>
                  </li>
                }
              </ul>
              @if (unjoinedMore() > 0) {
                <p class="text-xs text-fg-muted">
                  {{ 'admin.roster.unjoined.more' | transloco: { count: unjoinedMore() } }}
                </p>
              }
            </div>
          }

          @if (notInWorker().length > 0) {
            <div class="flex flex-col gap-1" data-testid="roster-not-in-worker">
              <p class="text-sm text-warning-fg">
                {{ 'admin.roster.notInWorker.heading' | transloco: { count: notInWorkerTotal() } }}
              </p>
              <ul class="flex flex-wrap gap-x-3 gap-y-1 text-sm">
                @for (item of notInWorker(); track item.name) {
                  <li>
                    <a
                      [routerLink]="['/admin/channels', item.name]"
                      class="text-accent-fg underline-offset-2 hover:underline"
                    >
                      #{{ item.name }}
                    </a>
                    @if (item.overdue) {
                      <span class="text-danger-fg">
                        {{ 'admin.roster.notInWorker.overdue' | transloco }}
                      </span>
                    }
                  </li>
                }
              </ul>
              @if (notInWorkerTruncated()) {
                <p class="text-xs text-fg-muted">
                  {{ 'admin.roster.notInWorker.truncated' | transloco }}
                </p>
              }
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

  private readonly receivedAtMs = signal(Date.now());
  private readonly nowMs = signal(Date.now());

  // No defaultValue, same reasoning as the health resource: "not loaded yet" and "an all-zero
  // roster" are different states and must not render alike.
  private readonly rosterResource = rxResource({
    stream: () =>
      this.adminService.getRoster().pipe(
        tap(() => {
          const now = Date.now();
          this.receivedAtMs.set(now);
          this.nowMs.set(now);
        }),
      ),
  });

  /** Public: the monitoring page reads the channel count for its activity heading from here. */
  readonly roster = computed(() =>
    this.rosterResource.hasValue() ? this.rosterResource.value() : undefined,
  );

  protected readonly errorMessage = computed(() => {
    const error = this.rosterResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  /**
   * The server's `ageSeconds` at receipt plus the time elapsed since — both sides measured on one
   * clock each, so browser/server clock skew never enters. Without this a worker that stopped
   * publishing would keep showing the age of the last response until the next poll, and a failing
   * poll would keep a green badge on a dead worker.
   */
  protected readonly ageSeconds = computed(() => {
    const base = this.roster()?.ageSeconds ?? 0;
    return base + Math.max(0, this.nowMs() - this.receivedAtMs()) / 1000;
  });

  /** Older than three publish intervals — at which point the TTL is about to drop the key anyway. */
  protected readonly isStale = computed(() => this.ageSeconds() > STALE_AFTER_SECONDS);

  protected readonly staleSeconds = computed(() => Math.floor(this.ageSeconds()));

  /**
   * Uncapped share of the admission cap, null when the ceiling is missing or zero: without a
   * denominator there is no utilization to grade, and a green bar would claim a health we cannot
   * know. The tone must see values past 100; the capped `capacityPercent` is only the bar width.
   */
  private readonly rawCapacityPercent = computed<number | null>(() => {
    const data = this.roster();
    const max = data?.ceilings?.maxActiveChannels;
    if (!data || !max) {
      return null;
    }
    return (data.trackedChannelCount / max) * 100;
  });

  protected readonly capacityTone = computed<UtilizationTone | null>(() => {
    const percent = this.rawCapacityPercent();
    return percent === null ? null : utilizationTone(percent, CAPACITY_THRESHOLDS);
  });

  protected readonly capacityFillClass = computed(() => {
    const tone = this.capacityTone();
    return tone === null ? 'bg-accent' : TONE_FILL[tone];
  });

  protected readonly capacityTextClass = computed(() => {
    const tone = this.capacityTone();
    return tone === null ? 'text-fg-body' : TONE_TEXT[tone];
  });

  /** Near the cap, at it, past it (global admins may exceed it). Null below 90 %. */
  protected readonly capacityWarningKey = computed<string | null>(() => {
    const data = this.roster();
    const max = data?.ceilings?.maxActiveChannels;
    if (!data || !max) {
      return null;
    }
    if (data.trackedChannelCount > max) {
      return 'admin.roster.capacity.over';
    }
    switch (this.capacityTone()) {
      case 'danger':
        return 'admin.roster.capacity.reached';
      case 'warning':
        return 'admin.roster.capacity.near';
      default:
        return null;
    }
  });

  protected readonly capacityWarningParams = computed(() => {
    const data = this.roster();
    return {
      percent: Math.round(this.rawCapacityPercent() ?? 0),
      tracked: data?.trackedChannelCount ?? 0,
      max: data?.ceilings?.maxActiveChannels ?? 0,
    };
  });

  /** Capped so an over-cap channel count cannot overflow the bar out of its track. */
  protected readonly capacityPercent = computed(() =>
    Math.min(100, Math.max(0, this.rawCapacityPercent() ?? 0)),
  );

  /** Server order is the contract (alarming, excluded, waiting; longest first) and is kept. */
  protected readonly unjoinedRows = computed(() =>
    (this.roster()?.ircUnjoined ?? []).map((row) => this.toUnjoinedRow(row)),
  );

  protected readonly unjoinedTotal = computed(() => {
    const data = this.roster();
    return Math.max(data?.ircUnjoinedTotal ?? 0, data?.ircUnjoined?.length ?? 0);
  });

  protected readonly unjoinedMore = computed(() =>
    Math.max(0, this.unjoinedTotal() - this.unjoinedRows().length),
  );

  /**
   * "Not in the worker": database says active, the worker holds no intent. Exactly the server's
   * `missingIntent` list in its order (overdue first), which it decides on the full roster before
   * capping; subtracting two capped lists here could name a channel the worker is already joining.
   */
  protected readonly notInWorker = computed(() =>
    (this.roster()?.missingIntent ?? []).map((row) => ({
      name: row.channelName,
      overdue: row.overdue,
    })),
  );

  protected readonly notInWorkerTotal = computed(() => {
    const data = this.roster();
    return Math.max(data?.missingIntentTotal ?? 0, data?.missingIntent?.length ?? 0);
  });

  protected readonly notInWorkerTruncated = computed(
    () => this.notInWorkerTotal() > this.notInWorker().length,
  );

  protected readonly missingFromSevenTv = computed(() => this.roster()?.missingFromSevenTv ?? []);
  protected readonly unknownToDatabase = computed(() => this.roster()?.unknownToDatabase ?? []);

  /**
   * Precedence: unavailable > stale > failing > starting > incomplete > complete. `failing` comes
   * from the server's join evaluation and outranks boot recovery: a starting worker must not hide a
   * channel that is already overdue.
   */
  protected readonly statusKey = computed(() => {
    const data = this.roster();
    if (!data) {
      return 'unknown';
    }
    if (!data.snapshotAvailable || data.joinHealth?.status === 'unavailable') {
      return 'unavailable';
    }
    if (this.isStale()) {
      return 'stale';
    }
    if (data.joinHealth?.status === 'failing') {
      return 'failing';
    }
    if (data.bootRecoveryCompleted === false || data.joinHealth?.status === 'starting') {
      return 'starting';
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

    // The push alone cannot show a dead publisher: nothing arrives. A visible-only minute poll
    // keeps the data fresh; the first (immediate) tick is skipped because the resource has just
    // loaded itself, a card that opened hidden counts its first tick as the return to visible.
    const visible = pageVisible();
    pollWhileVisible(POLL_INTERVAL_MS, visible)
      .pipe(skip(visible() ? 1 : 0))
      .subscribe(() => this.rosterResource.reload());

    const ticker = setInterval(() => this.nowMs.set(Date.now()), AGE_TICK_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(ticker));
  }

  reload(): void {
    this.rosterResource.reload();
  }

  private toUnjoinedRow(row: IrcUnjoinedChannel) {
    const seconds = Math.max(0, Math.round(row.unconfirmedSeconds));
    const minutes = seconds >= 60;
    const reason = row.reason && KNOWN_REASONS.has(row.reason) ? row.reason : 'unknown';
    return {
      name: row.channelName,
      reasonKey: `admin.roster.joinFailure.reason.${reason}`,
      durationKey: minutes ? 'admin.roster.unjoined.minutes' : 'admin.roster.unjoined.seconds',
      durationValue: minutes ? Math.floor(seconds / 60) : seconds,
      alarming: row.overdue && row.countsForAlarm,
      // `overdue` first: the server sends `countsForAlarm: false` for every row still within its
      // grace, rejected or not, so only an overdue row can be told apart as excluded.
      stateKey: !row.overdue
        ? 'admin.roster.unjoined.waiting'
        : row.countsForAlarm
          ? 'admin.roster.unjoined.overdue'
          : 'admin.roster.unjoined.excluded',
    };
  }
}
