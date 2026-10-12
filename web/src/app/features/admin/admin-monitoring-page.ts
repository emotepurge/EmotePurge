import { HttpErrorResponse } from '@angular/common/http';
import { Dialog } from '@angular/cdk/dialog';
import { Component, computed, inject, linkedSignal, signal, viewChild } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { skip } from 'rxjs';

import { AdminService } from '../../core/admin/admin.service';
import {
  AdminDashboardActivity,
  AdminDashboardActivityRange,
  RateLimitTelemetrySnapshot,
  RateLimitTelemetryTrend,
  SevenTvConnectionStatus,
  WorkerConnectionStatus,
} from '../../core/admin/admin.model';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { ADMIN_LIVE_URL, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveEvents } from '../../core/live/live-reload';
import { pollWhileVisible } from '../../core/live/poll-while-visible';
import { pageVisible } from '../../core/page-visibility/page-visibility';
import { LiveStatus, LiveUpdateService } from '../../core/live/live-update.service';
import { sumMinutesIntoBuckets } from '../../shared/charts/chart-series';
import { LineChart, LineChartSeries, SHARED_PLOT_LEFT } from '../../shared/charts/line-chart';
import { Button } from '../../shared/ui/button';
import { EmptyState } from '../../shared/ui/empty-state';
import { HealthMarker, HealthTone } from '../../shared/ui/health-marker';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonSections } from '../../shared/ui/skeleton-sections';
import { StatusBadge } from '../../shared/ui/status-badge';
import { UtilizationTone, utilizationTone } from '../../shared/ui/utilization-tone';
import { AdminActivitySection } from './admin-activity-section';
import { AdminLoadSection, TrendAxis } from './admin-load-section';
import { openRateLimitDetailsDialog } from './admin-rate-limit-details-dialog';
import { policyExceptions, providerExceptions, trendSeriesSpecs } from './admin-rate-limit-view';
import { AdminRosterCard } from './admin-roster-card';

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

const STATUS_TONES: Record<SevenTvConnectionStatus, HealthTone> = {
  connected: 'ok',
  stale: 'warning',
  disconnected: 'danger',
  unknown: 'idle',
  disabled: 'idle',
};

// `connecting` is deliberately not a warning: every page load passes through it, and a yellow pill
// that flashes on arrival trains an admin to look past yellow pills.
const LIVE_TONES: Record<LiveStatus, HealthTone> = {
  idle: 'idle',
  connecting: 'idle',
  open: 'ok',
  closed: 'danger',
};

/** Shown when a value is absent — an older worker's snapshot simply lacks the newer detail fields. */
const NO_VALUE = '—';

// Neither the rate-limit snapshot/trend nor the activity series has an SSE event of its own (the
// admin stream carries no `usage.flushed`, spec section 9), so they follow a 60 s timer that pauses
// while the tab is hidden and fires once on return (spec section 8).
const DASHBOARD_POLL_INTERVAL_MS = 60_000;

/**
 * Read-only operational view of the worker, fed by the admin-only GET /api/admin/health (Z1 split:
 * the public /api/worker/health stays minimal because every visitor polls it). One card per
 * subsystem, because they fail independently — Twitch IRC can be fine while the 7TV socket is
 * wedged, and the batch flush can be failing while both sockets look healthy.
 */
@Component({
  selector: 'app-admin-monitoring-page',
  imports: [
    AdminActivitySection,
    AdminLoadSection,
    AdminRosterCard,
    Button,
    EmptyState,
    HealthMarker,
    LineChart,
    NoticeBanner,
    SkeletonSections,
    StatusBadge,
    TranslocoPipe,
  ],
  template: `
    <div class="flex flex-col gap-4">
      <header class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="text-lg font-semibold">{{ 'admin.monitoring.title' | transloco }}</h2>
        <div class="flex flex-wrap items-center gap-3">
          <!-- The page updates itself from the admin SSE stream, so the button next to this badge
               needs an explanation for why it is still there: when the stream is closed (revoked
               session, 503 over the stream limit, a proxy that eats text/event-stream) nothing
               refetches on its own — LiveUpdateService has no polling fallback by design, and this
               is the page an admin opens to find out. The badge says which of the two states the
               page is in instead of leaving the button looking vestigial. -->
          <app-health-marker
            [tone]="liveTone()"
            [label]="'admin.monitoring.live.' + liveStatus() | transloco"
          />
          <button
            type="button"
            appButton="outline"
            [disabled]="isLoading()"
            (click)="reload()"
            [title]="'admin.monitoring.refreshTitle' | transloco"
          >
            {{ 'admin.monitoring.refresh' | transloco }}
          </button>
        </div>
      </header>

      <!-- Load and work first (spec section 10): it owns its own range, probe and polling. -->
      <app-admin-load-section />

      <app-admin-activity-section
        [errorKey]="activityErrorMessage()"
        [activity]="activity()"
        [loading]="activityLoading()"
        [trackedChannelCount]="rosterContext()?.tracked ?? null"
        [maxActiveChannels]="rosterContext()?.max ?? null"
        [(range)]="range"
      />

      <!-- Rate limits, exception-first: only what rejected something, plus the 24 h trend. Its own
           resources and its own error/loading state, independent of the worker-health snapshot
           below. Everything quiet (full policy list, caches, provider counters, last rejection)
           lives in the details dialog. -->
      @if (showRateLimitsSkeleton()) {
        <app-skeleton-sections [count]="1" />
      } @else {
        <!-- The snapshot and the trend are independent: each part renders from its own last value,
             and a failure of one never hides the other. -->
        <section
          class="flex flex-col gap-4 border-t border-border pt-4"
          data-testid="admin-rate-limits"
        >
          <div class="flex flex-wrap items-center justify-between gap-3">
            <h3 class="text-base font-semibold">
              {{ 'admin.dashboard.rateLimits.title' | transloco }}
            </h3>
            @if (rateLimits(); as rl) {
              <button type="button" appButton="outline" (click)="openDetails(rl)">
                {{ 'admin.dashboard.rateLimits.details.open' | transloco }}
              </button>
            }
          </div>

          @if (rateLimitsErrorMessage(); as error) {
            <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
          }

          @if (rateLimits(); as rl) {
            @if (!rl.telemetryAvailable) {
              <app-notice-banner variant="warning">
                {{ 'admin.rateLimits.degraded' | transloco }}
              </app-notice-banner>
            } @else if (policyRows().length === 0 && providerRows().length === 0) {
              <app-empty-state
                [title]="'admin.dashboard.rateLimits.none.title' | transloco"
                [description]="'admin.dashboard.rateLimits.none.description' | transloco"
              />
            } @else {
              @if (policyRows().length) {
                <div class="flex flex-col gap-2">
                  <h4 id="rate-limit-policies-title" class="text-sm font-medium text-fg-body">
                    {{ 'admin.dashboard.rateLimits.policies.title' | transloco }}
                  </h4>
                  <ul
                    class="-mx-3 divide-y divide-border border-y border-border"
                    aria-labelledby="rate-limit-policies-title"
                  >
                    @for (row of policyRows(); track row.name) {
                      <li class="flex flex-col gap-1 px-3 py-3">
                        <div
                          class="flex flex-wrap items-center gap-2 text-sm font-medium text-fg-body"
                        >
                          {{ 'admin.rateLimits.policies.names.' + row.name | transloco }}
                          @if (row.acute) {
                            <app-status-badge tone="danger">
                              {{ 'admin.dashboard.rateLimits.acute' | transloco }}
                            </app-status-badge>
                          }
                        </div>
                        <p class="text-xs text-fg-muted">
                          @if (row.sharePercent === null) {
                            {{ 'admin.dashboard.rateLimits.policies.noRequests' | transloco }}
                          } @else {
                            {{
                              'admin.dashboard.rateLimits.policies.share'
                                | transloco
                                  : {
                                      rejected: formatNumber(row.rejectedLast24Hours),
                                      decisions: formatNumber(
                                        row.rejectedLast24Hours + row.acceptedLast24Hours
                                      ),
                                      percent: formatPercent(row.sharePercent),
                                    }
                            }}
                          }
                          @if (row.acute) {
                            ·
                            {{
                              'admin.dashboard.rateLimits.lastMinute'
                                | transloco: { count: formatNumber(row.rejectedLastMinute) }
                            }}
                          }
                        </p>
                      </li>
                    }
                  </ul>
                </div>
              }

              @if (providerRows().length) {
                <div class="flex flex-col gap-2">
                  <h4 id="rate-limit-providers-title" class="text-sm font-medium text-fg-body">
                    {{ 'admin.dashboard.rateLimits.providers.title' | transloco }}
                  </h4>
                  <ul
                    class="-mx-3 divide-y divide-border border-y border-border"
                    aria-labelledby="rate-limit-providers-title"
                  >
                    @for (row of providerRows(); track row.providerName + '|' + row.callSource) {
                      <li class="flex flex-col gap-1 px-3 py-3">
                        <div
                          class="flex flex-wrap items-center gap-2 text-sm font-medium text-fg-body"
                        >
                          {{ 'admin.rateLimits.providers.names.' + row.providerName | transloco }}
                          ·
                          {{
                            'admin.rateLimits.providers.callSources.' + row.callSource | transloco
                          }}
                          @if (row.acute) {
                            <app-status-badge tone="danger">
                              {{ 'admin.dashboard.rateLimits.acute' | transloco }}
                            </app-status-badge>
                          }
                        </div>
                        <p class="text-xs text-fg-muted">
                          {{
                            'admin.dashboard.rateLimits.providers.count'
                              | transloco: { count: formatNumber(row.rateLimitedLast24Hours) }
                          }}
                          @if (row.acute) {
                            ·
                            {{
                              'admin.dashboard.rateLimits.lastMinute'
                                | transloco: { count: formatNumber(row.rateLimitedLastMinute) }
                            }}
                          }
                        </p>
                      </li>
                    }
                  </ul>
                </div>
              }
            }
          }

          @if (trendErrorMessage(); as error) {
            <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
          }
          @if (trendUnavailable() && rateLimits()?.telemetryAvailable !== false) {
            <app-notice-banner variant="warning" data-testid="admin-trend-unavailable">
              {{ 'admin.dashboard.rateLimits.trendUnavailable' | transloco }}
            </app-notice-banner>
          }
          <!-- The newest bucket is the running minute by construction (RateLimitTelemetryStore). -->
          @if (trend(); as trendData) {
            @if (trendAxis().kind === 'hidden') {
              <p class="text-xs text-fg-muted" data-testid="admin-trend-hidden">
                {{ 'admin.dashboard.rateLimits.trend.hiddenForRange' | transloco }}
              </p>
            } @else if (trendSeries().length) {
              <div class="flex flex-col gap-2">
                <app-line-chart
                  [series]="trendSeries()"
                  [xStartsUtc]="trendXStarts()"
                  [bucketSeconds]="trendBucketSeconds()"
                  legendStat="sum"
                  unit="count"
                  [partialLastBucket]="true"
                  [minPlotLeft]="trendPlotLeft()"
                  [title]="trendTitleKey() | transloco: trendTitleParams()"
                />
                @if (trendSeries().length === 1) {
                  <p class="text-xs text-fg-muted">
                    {{ 'admin.dashboard.rateLimits.trend.noProviderRejections' | transloco }}
                  </p>
                }
              </div>
            }
          }

          <div class="flex flex-col gap-1 text-xs text-fg-muted">
            <p>{{ 'admin.dashboard.rateLimits.help.share' | transloco }}</p>
            <p>{{ 'admin.rateLimits.hints.clientSevenTv' | transloco }}</p>
            <p>{{ 'admin.rateLimits.hints.policyFreeRoutes' | transloco }}</p>
          </div>
        </section>
      }

      <!-- The worker-health area: its banner stands inside it, not beside the sections above. -->
      <div class="flex flex-col gap-4" data-testid="admin-health">
        @if (errorMessage(); as error) {
          <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
        }

        @if (showSkeleton()) {
          <app-skeleton-sections [count]="3" />
        } @else if (health(); as data) {
          @if (!data.snapshotAvailable) {
            <app-notice-banner variant="warning">
              {{ 'admin.monitoring.noSnapshot' | transloco }}
            </app-notice-banner>
          }

          <!-- Twitch IRC.

               No card box around any of these, unlike before. A card is a boundary against a
               *different* kind of neighbour, and on this page every neighbour is the same kind of
               thing: one subsystem reporting on itself. Four bordered rectangles stacked in a column
               separated nothing that a rule and a heading do not separate better — and the box was
               competing with the only thing here that has to stand out, which is the one subsystem
               that is not fine. -->
          <section class="flex flex-col gap-3 border-t border-border pt-4">
            <div class="flex flex-wrap items-center justify-between gap-3">
              <h3 class="text-base font-semibold">
                {{ 'admin.monitoring.twitch.title' | transloco }}
              </h3>
              <app-health-marker
                [tone]="toneFor(data.status)"
                [label]="'admin.monitoring.status.' + data.status | transloco"
              />
            </div>
            <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.twitch.lastMessage' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ formatDateTime(data.lastMessageReceivedUtc) }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.twitch.connectAttempt' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ formatDateTime(data.connectAttemptedUtc) }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.twitch.processStarted' | transloco }}
                </dt>
                <!-- Every counter on this page resets with the process, so its start time is what
                     makes "0 failures" readable at all. -->
                <dd class="text-fg-body">{{ formatDateTime(data.worker.processStartedUtc) }}</dd>
              </div>
            </dl>
          </section>

          <!-- Roster: the worker's channels against the database's. Its own card because it is fed by
               its own endpoint on its own cadence (see AdminRosterCard). -->
          <app-admin-roster-card />

          <!-- 7TV EventAPI -->
          <section class="flex flex-col gap-3 border-t border-border pt-4">
            <div class="flex flex-wrap items-center justify-between gap-3">
              <h3 class="text-base font-semibold">
                {{ 'admin.monitoring.sevenTv.title' | transloco }}
              </h3>
              <app-health-marker
                [tone]="toneFor(data.sevenTv.status)"
                [label]="'admin.monitoring.status.' + data.sevenTv.status | transloco"
              />
            </div>

            <div class="flex flex-col gap-1">
              <div class="flex flex-wrap items-baseline justify-between gap-2 text-sm">
                <span class="text-fg-muted">
                  {{ 'admin.monitoring.sevenTv.subscriptions' | transloco }}
                </span>
                <!-- The numbers are rendered as text as well, so the bar never carries meaning on its
                     own (it is additionally an ARIA progressbar for assistive tech). -->
                <span [class]="subscriptionTextClass()">
                  {{ formatNumber(data.sevenTv.desiredSubscriptionCount) }} /
                  {{ formatNumber(data.sevenTv.subscriptionLimit) }}
                </span>
              </div>
              <div
                class="h-2 w-full overflow-hidden rounded-full bg-surface-inset"
                role="progressbar"
                [attr.aria-valuenow]="data.sevenTv.desiredSubscriptionCount"
                aria-valuemin="0"
                [attr.aria-valuemax]="data.sevenTv.subscriptionLimit"
                [attr.aria-label]="'admin.monitoring.sevenTv.utilization' | transloco"
              >
                <div
                  class="h-full rounded-full"
                  [class]="subscriptionFillClass()"
                  [style.width.%]="utilizationPercent()"
                ></div>
              </div>
              @if (subscriptionWarningKey(); as warningKey) {
                <!-- The colour step alone must never carry the warning (WCAG 1.4.1). -->
                <p class="text-xs" [class]="subscriptionTextClass()">
                  {{ warningKey | transloco: subscriptionWarningParams() }}
                </p>
              }
            </div>

            <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.sevenTv.channels' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ formatNumber(data.sevenTv.desiredChannelCount) }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.sevenTv.unacknowledged' | transloco }}
                </dt>
                <dd class="text-fg-body">
                  @if (hasUnacknowledged()) {
                    <app-status-badge tone="warning">
                      {{ formatNumber(data.sevenTv.unacknowledgedCount) }}
                    </app-status-badge>
                  } @else {
                    {{ formatNumber(data.sevenTv.unacknowledgedCount) }}
                  }
                </dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.sevenTv.lastFrame' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ formatDateTime(data.sevenTv.lastFrameUtc) }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.sevenTv.lastDispatch' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ formatDateTime(data.sevenTv.lastDispatchUtc) }}</dd>
              </div>
              @if (restRequestRate(); as rate) {
                <div class="flex justify-between gap-4 sm:block">
                  <dt class="text-fg-muted">
                    {{ 'admin.monitoring.sevenTv.restRate' | transloco }}
                  </dt>
                  <!-- Deliberately not a utilization bar: 7TV publishes no REST quota, so there is no
                       honest denominator. A rate is what we actually know. -->
                  <dd class="text-fg-body">
                    {{ 'admin.monitoring.sevenTv.restRateValue' | transloco: rate }}
                  </dd>
                </div>
              }
            </dl>
          </section>

          <!-- Batch flush -->
          <section class="flex flex-col gap-3 border-t border-border pt-4">
            <div class="flex flex-wrap items-center justify-between gap-3">
              <h3 class="text-base font-semibold">
                {{ 'admin.monitoring.flush.title' | transloco }}
              </h3>
              <app-health-marker
                [tone]="hasFlushFailures() ? 'danger' : 'ok'"
                [label]="
                  ('admin.monitoring.flush.consecutiveFailures' | transloco) +
                  ': ' +
                  formatNumber(data.flush.consecutiveFailures)
                "
              />
            </div>
            <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.flush.lastSuccess' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ formatDateTime(data.flush.lastSuccessUtc) }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.monitoring.flush.lastRowCount' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ formatNumber(data.flush.lastRowCount) }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">{{ 'admin.monitoring.flush.pending' | transloco }}</dt>
                <dd class="text-fg-body">{{ formatNumber(data.flush.pendingEmoteCount) }}</dd>
              </div>
            </dl>
          </section>
        }
      </div>
    </div>
  `,
})
export class AdminMonitoringPage {
  private readonly adminService = inject(AdminService);
  private readonly languageService = inject(LanguageService);
  private readonly liveUpdateService = inject(LiveUpdateService);
  private readonly dialog = inject(Dialog);
  private readonly transloco = inject(TranslocoService);

  // The roster lives in its own card on its own endpoint and its own cadence, so refreshing it is
  // its own call — the refresh button used to reload the health snapshot only, which left the one
  // card an admin comes here for (which channels is the worker actually in?) reachable by nothing
  // but the 60 s worker.roster push.
  private readonly rosterCard = viewChild(AdminRosterCard);
  private readonly loadSection = viewChild(AdminLoadSection);

  // No defaultValue: "no snapshot yet" and "an all-zero snapshot" must not look alike, so the
  // template branches on undefined instead of rendering a fabricated empty one.
  private readonly healthResource = rxResource({
    stream: () => this.adminService.getHealth(),
  });

  // Its own resource rather than folded into healthResource: the two endpoints have nothing to do
  // with each other, and a shared resource would mean an error on one hides the other's data.
  private readonly rateLimitsResource = rxResource({
    stream: () => this.adminService.getRateLimits(),
  });

  // Its own resource as well: the 24 h minute buckets come from a separate endpoint and a failure
  // there must not blank the snapshot or the health cards.
  private readonly trendResource = rxResource({
    stream: () => this.adminService.getRateLimitTrend(),
  });

  // The range menu drives `params`; a change reloads the series (status 'loading', value cleared).
  protected readonly range = signal<AdminDashboardActivityRange>('30d');

  private readonly activityResource = rxResource({
    params: () => this.range(),
    stream: ({ params }) => this.adminService.getDashboardActivity(params),
  });

  // Service-wide, so with two concurrent streams the last writer wins — harmless here because both
  // subscriptions on this page point at the same endpoint and therefore share a fate.
  protected readonly liveStatus = this.liveUpdateService.status;
  protected readonly liveTone = computed<HealthTone>(() => LIVE_TONES[this.liveStatus()]);

  // value() throws once the resource is in its error state, so it is only ever read behind
  // hasValue() — the error banner below renders from error() instead.
  protected readonly health = computed(() =>
    this.healthResource.hasValue() ? this.healthResource.value() : undefined,
  );

  /** Drives the refresh button's disabled state only — never a content swap. */
  protected readonly isLoading = computed(
    () =>
      this.healthResource.isLoading() ||
      this.rateLimitsResource.isLoading() ||
      this.trendResource.isLoading() ||
      this.activityResource.isLoading(),
  );

  // Skeleton on the *first* load only. Every later load is a reload (status 'reloading', see
  // Angular's ResourceStatus), and the worker pushes `worker.health` about every 20 s: swapping the
  // rendered cards for a skeleton that often made the whole page twitch on its own. A reload keeps
  // the previous snapshot on screen and replaces it in place when the new one arrives.
  protected readonly showSkeleton = computed(() => this.healthResource.status() === 'loading');

  protected readonly errorMessage = computed(() => {
    const error = this.healthResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  // Same undefined-until-loaded / skeleton-on-first-load-only shape as the health resource above,
  // kept as its own trio rather than merged: the two resources load, error and reload
  // independently.
  // Last-value pattern: a failed 60 s poll keeps the previous snapshot on screen (the error banner
  // stands beside it) instead of removing the section.
  protected readonly rateLimits = linkedSignal<
    RateLimitTelemetrySnapshot | undefined,
    RateLimitTelemetrySnapshot | undefined
  >({
    source: () =>
      this.rateLimitsResource.hasValue() ? this.rateLimitsResource.value() : undefined,
    computation: (next, previous) => next ?? previous?.value,
  });

  protected readonly showRateLimitsSkeleton = computed(
    () => this.rateLimitsResource.status() === 'loading' && this.rateLimits() === undefined,
  );

  protected readonly rateLimitsErrorMessage = computed(() => {
    const error = this.rateLimitsResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  /**
   * The last series that loaded. A range change or a failed reload keeps it on screen instead of
   * blanking the chart (no layout jumps); `undefined` only before the very first load, which is
   * the only time the section shows a skeleton.
   */
  protected readonly activity = linkedSignal<
    AdminDashboardActivity | undefined,
    AdminDashboardActivity | undefined
  >({
    source: () => (this.activityResource.hasValue() ? this.activityResource.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  protected readonly activityLoading = computed(() => this.activityResource.status() === 'loading');

  protected readonly activityErrorMessage = computed(() => {
    const error = this.activityResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  protected readonly trend = linkedSignal<
    RateLimitTelemetryTrend | undefined,
    RateLimitTelemetryTrend | undefined
  >({
    source: () => (this.trendResource.hasValue() ? this.trendResource.value() : undefined),
    // An unavailable response carries no series: it never replaces the last good one.
    computation: (next, previous) =>
      next?.telemetryAvailable === false ? previous?.value : (next ?? previous?.value),
  });

  /** The latest trend response said the counter store could not be read (HTTP 200, no series). */
  protected readonly trendUnavailable = computed(
    () => this.trendResource.hasValue() && this.trendResource.value().telemetryAvailable === false,
  );

  protected readonly trendErrorMessage = computed(() => {
    const error = this.trendResource.error();
    return error instanceof HttpErrorResponse ? apiErrorTranslationKey(error) : null;
  });

  /**
   * Channel count for the activity heading, read from the roster card — no second /roster call.
   * Appears only once health has loaded: the card lives in the health branch (deliberate).
   */
  protected readonly rosterContext = computed(() => {
    const roster = this.rosterCard()?.roster();
    return roster
      ? { tracked: roster.trackedChannelCount, max: roster.ceilings.maxActiveChannels }
      : null;
  });

  protected readonly policyRows = computed(() => policyExceptions(this.rateLimits()));
  protected readonly providerRows = computed(() => providerExceptions(this.rateLimits()));

  /** The load section's axis: the trend shares it up to 24 h, has its own minutes otherwise or hides. */
  protected readonly trendAxis = computed<TrendAxis>(
    () => this.loadSection()?.trendAxis() ?? { kind: 'own' },
  );

  /** On the load axis the trend lines up with the load charts above it; on its own axis it does not. */
  protected readonly trendPlotLeft = computed(() =>
    this.trendAxis().kind === 'load' ? SHARED_PLOT_LEFT : 0,
  );
  protected readonly trendXStarts = computed(() => {
    const axis = this.trendAxis();
    return axis.kind === 'load' ? axis.startsUtc : (this.trend()?.minuteStartsUtc ?? []);
  });

  protected readonly trendBucketSeconds = computed(() => {
    const axis = this.trendAxis();
    return axis.kind === 'load' ? axis.bucketSeconds : 60;
  });

  protected readonly trendTitleKey = computed(() =>
    this.trendAxis().kind === 'load'
      ? 'admin.dashboard.rateLimits.trend.titleBucketed'
      : 'admin.dashboard.rateLimits.trend.title',
  );

  protected readonly trendTitleParams = computed(() => ({
    minutes: this.trendBucketSeconds() / 60,
  }));

  /**
   * Local rejections plus one series per rejecting provider dimension. All are real rejections, so
   * all carry the danger tone; the dash pattern and the legend tell them apart.
   */
  protected readonly trendSeries = computed<LineChartSeries[]>(() => {
    // Re-evaluates on a language switch: the provider names are translated here, not in the chart.
    this.languageService.lang();
    const axis = this.trendAxis();
    const trend = this.trend();
    // On the load axis the minute counts are summed into the load buckets, so both share one x axis.
    const valuesOf = (minutes: number[]): (number | null)[] =>
      axis.kind === 'load' && trend
        ? sumMinutesIntoBuckets(trend.minuteStartsUtc, minutes, axis.startsUtc, axis.bucketSeconds)
        : minutes;
    return trendSeriesSpecs(trend).map((spec) =>
      spec.kind === 'local'
        ? {
            id: spec.id,
            labelKey: 'admin.dashboard.rateLimits.series.local',
            values: valuesOf(spec.values),
            tone: 'danger',
          }
        : {
            id: spec.id,
            labelKey: 'admin.dashboard.rateLimits.series.provider',
            labelParams: {
              provider: this.transloco.translate(
                `admin.rateLimits.providers.names.${spec.providerName}`,
              ),
              source: this.transloco.translate(
                `admin.rateLimits.providers.callSources.${spec.callSource}`,
              ),
            },
            values: valuesOf(spec.values),
            tone: 'danger',
          },
    );
  });

  /** Capped at 100 so a future over-limit state can't overflow the bar out of its track. */
  protected readonly utilizationPercent = computed(() => {
    const raw = this.rawUtilizationPercent();
    return raw === null ? 0 : Math.min(100, raw);
  });

  /**
   * Uncapped share of the subscription limit, null when there is nothing to divide: either 7TV
   * never reported a count (older worker snapshot — `null`, not `0`) or the limit itself is 0. The
   * two are checked separately on purpose: `desiredSubscriptionCount: 0` is a real, known value (no
   * subscriptions desired) and must grade as 0 % utilization, not read as "unknown" the way a
   * `!desiredSubscriptionCount` falsy check would. The tone must see values past 100, so this is
   * deliberately not the capped `utilizationPercent` above.
   */
  private readonly rawUtilizationPercent = computed<number | null>(() => {
    const sevenTv = this.health()?.sevenTv;
    const desired = sevenTv?.desiredSubscriptionCount;
    if (desired === null || desired === undefined || !sevenTv || !sevenTv.subscriptionLimit) {
      return null;
    }
    return (desired / sevenTv.subscriptionLimit) * 100;
  });

  protected readonly subscriptionTone = computed<UtilizationTone | null>(() => {
    const percent = this.rawUtilizationPercent();
    return percent === null ? null : utilizationTone(percent);
  });

  protected readonly subscriptionFillClass = computed(() => {
    const tone = this.subscriptionTone();
    return tone === null ? 'bg-accent' : TONE_FILL[tone];
  });

  protected readonly subscriptionTextClass = computed(() => {
    const tone = this.subscriptionTone();
    return tone === null ? 'text-fg-body' : TONE_TEXT[tone];
  });

  /** Text line under the bar from `warning` upwards (WCAG 1.4.1 — colour is never alone). */
  protected readonly subscriptionWarningKey = computed<string | null>(() => {
    switch (this.subscriptionTone()) {
      case 'danger':
        return 'admin.monitoring.sevenTv.utilizationCritical';
      case 'warning':
        return 'admin.monitoring.sevenTv.utilizationWarning';
      default:
        return null;
    }
  });

  protected readonly subscriptionWarningParams = computed(() => ({
    percent: Math.round(this.rawUtilizationPercent() ?? 0),
  }));

  /**
   * The periodic resync issues one 7TV REST call per tracked channel per tick, so the load we put on
   * a third party is a rate, not a share of a quota — 7TV publishes no limit to divide by. Null
   * until both figures are known; a fabricated interval would state a rate we never measured.
   */
  protected readonly restRequestRate = computed(() => {
    const sevenTv = this.health()?.sevenTv;
    const channels = sevenTv?.desiredChannelCount;
    const intervalSeconds = sevenTv?.resyncIntervalSeconds;
    // `channels: 0` is a real, known value (no channels tracked, so a real 0/s rate) and must not
    // be read as "not reported" the way a `!channels` falsy check would; `intervalSeconds` is the
    // divisor and still needs the zero/missing guard to avoid a division by zero.
    if (channels === null || channels === undefined || !intervalSeconds) {
      return null;
    }
    return {
      requests: channels,
      seconds: intervalSeconds,
      perSecond: (channels / intervalSeconds).toFixed(2),
    };
  });

  protected readonly hasUnacknowledged = computed(
    () => (this.health()?.sevenTv.unacknowledgedCount ?? 0) > 0,
  );

  protected readonly hasFlushFailures = computed(
    () => (this.health()?.flush.consecutiveFailures ?? 0) > 0,
  );

  constructor() {
    // No debounce: WorkerHealthPublisher writes its snapshot on a ~20 s cadence, so this can never
    // fire faster than the refetch it triggers. The URL is constant, so no toObservable indirection.
    liveEvents(ADMIN_LIVE_URL, [LIVE_EVENT_TYPES.workerHealth]).subscribe(() =>
      this.healthResource.reload(),
    );

    // Activity, rate-limit snapshot and trend share one visibility-aware 60 s timer. The first tick
    // (immediate, on a visible page) is skipped: the resources have just loaded themselves.
    // A page that opened hidden has not had that tick: its first emission is the return to visible.
    const visible = pageVisible();
    pollWhileVisible(DASHBOARD_POLL_INTERVAL_MS, visible)
      .pipe(skip(visible() ? 1 : 0))
      .subscribe(() => this.reloadDashboard());
  }

  // Undefined while the first load still shows the skeleton — the roster card renders inside the
  // health branch, so there is nothing to reload yet and nothing to do about it.
  protected reload(): void {
    this.healthResource.reload();
    this.rosterCard()?.reload();
    this.loadSection()?.reload();
    this.reloadDashboard();
  }

  protected openDetails(snapshot: RateLimitTelemetrySnapshot): void {
    openRateLimitDetailsDialog(this.dialog, snapshot);
  }

  protected toneFor(status: SevenTvConnectionStatus | WorkerConnectionStatus): HealthTone {
    return STATUS_TONES[status] ?? 'idle';
  }

  // LOCALE_ID is bootstrap-time static and can't follow a runtime language switch, so dates and
  // numbers go through toLocale() — same reasoning as vote-session-detail-page.ts.
  protected formatDateTime(iso: string | null): string {
    if (!iso) {
      return NO_VALUE;
    }
    return new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
      timeStyle: 'medium',
    });
  }

  protected formatNumber(value: number | null): string {
    if (value === null || value === undefined) {
      return NO_VALUE;
    }
    return new Intl.NumberFormat(toLocale(this.languageService.lang())).format(value);
  }

  protected formatPercent(value: number): string {
    const format = new Intl.NumberFormat(toLocale(this.languageService.lang()), {
      maximumFractionDigits: 1,
    });
    // The row exists because something was rejected: never show it as "0 %".
    return value > 0 && value < 0.1 ? `<${format.format(0.1)}` : format.format(value);
  }

  private reloadDashboard(): void {
    this.rateLimitsResource.reload();
    this.trendResource.reload();
    this.activityResource.reload();
  }
}
