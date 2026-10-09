import { Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { BackfillRun, BackfillRunStatus } from '../../core/channels/backfill.model';
import {
  backfillSetLabel,
  formatInstant,
  formatIsoDay,
  lastDayOf,
} from '../../core/channels/backfill-window';
import { LanguageService } from '../../core/i18n/language.service';
import { pluralKey } from '../../core/i18n/plural';
import { StatusBadge, StatusBadgeTone } from '../../shared/ui/status-badge';

/**
 * Every `ChatLogBackfillRun.ErrorCode` the backend writes (spec 2026-10-09, §4.6, checked against
 * the string constants in `ChatLogBackfillService`, `ChatLogBackfillWorker` and `ChannelDeactivation`).
 * The vocabulary spec asserts a de and an en sentence for each.
 */
export const BACKFILL_RUN_ERROR_CODES = [
  'transport_failure',
  'rate_limited',
  'malformed_response',
  'block_too_large',
  'snapshot_missing',
  'channel_not_active',
  'channel_excluded',
  'archive_mismatch',
  'twitch_id_unknown',
  'live_row_conflict',
  'channel_left',
  'worker_error',
] as const;

export const BACKFILL_RUN_STATUSES: readonly BackfillRunStatus[] = [
  'queued',
  'running',
  'paused',
  'completed',
  'failed',
  'cancelled',
];

const UNKNOWN_ERROR_KEY = 'backfill.errors.unknown';
const UNKNOWN_STATUS_KEY = 'backfill.status.unknown';

/** The sentence for a run's error: the vocabulary's own, a generic one for a code from a newer
 *  server, never the raw code. `null` when the run carries no error. */
export function backfillRunErrorKey(code: string | null): string | null {
  if (code === null || code === '') {
    return null;
  }
  return (BACKFILL_RUN_ERROR_CODES as readonly string[]).includes(code)
    ? `backfill.errors.${code}`
    : UNKNOWN_ERROR_KEY;
}

export function backfillRunStatusKey(status: string): string {
  return (BACKFILL_RUN_STATUSES as readonly string[]).includes(status)
    ? `backfill.status.${status}`
    : UNKNOWN_STATUS_KEY;
}

const STATUS_TONES: Record<BackfillRunStatus, StatusBadgeTone> = {
  queued: 'neutral',
  running: 'info',
  paused: 'warning',
  completed: 'success',
  failed: 'danger',
  cancelled: 'neutral',
};

/**
 * One backfill run, as the running one (`kind="active"`: progress, queue position, the archive's
 * wait) or as the last finished one (`kind="last"`: outcome, finish time, error). Presentational:
 * everything it decides is a `computed()` over its inputs, and the section owns the state.
 */
@Component({
  selector: 'app-backfill-run-status',
  imports: [StatusBadge, TranslocoPipe],
  template: `
    <section class="flex flex-col gap-1.5 text-sm" [attr.aria-labelledby]="headingId()">
      <h4 [id]="headingId()" class="flex items-center gap-2 font-medium">
        {{ (kind() === 'active' ? 'backfill.activeRun' : 'backfill.lastRun') | transloco }}
        <app-status-badge [tone]="tone()">{{ statusKey() | transloco }}</app-status-badge>
      </h4>

      @if (kind() === 'active') {
        <progress
          class="h-2 w-full max-w-md"
          [value]="run().weeksDone"
          [max]="run().weeksTotal"
          [attr.aria-label]="'backfill.progressLabel' | transloco"
        ></progress>
      }
      <p [attr.role]="kind() === 'active' ? 'status' : null">
        {{ 'backfill.progress' | transloco: { done: run().weeksDone, total: run().weeksTotal } }}
      </p>

      @if (queuePosition(); as position) {
        <p>{{ 'backfill.queuePosition' | transloco: { position } }}</p>
      }
      @if (waitUntil(); as until) {
        <p>{{ 'backfill.waitUntil' | transloco: { time: formatTime(until) } }}</p>
      }

      <p class="text-fg-secondary">
        {{
          'backfill.runWindow'
            | transloco: { from: formatDay(run().windowFrom), to: formatDay(lastDay()) }
        }}
      </p>
      <p class="text-fg-secondary">
        {{
          pluralKey(run().emoteCount, 'backfill.runSet')
            | transloco: { set: setLabel(), count: run().emoteCount }
        }}
      </p>
      <p class="text-fg-secondary">
        {{ 'backfill.requestedBy' | transloco: { login: run().requestedByLogin } }}
      </p>

      @if (kind() === 'last') {
        @if (run().finishedAtUtc; as finished) {
          <p class="text-fg-secondary">
            {{ 'backfill.finishedAt' | transloco: { time: formatTime(finished) } }}
          </p>
        }
        @if (errorKey(); as key) {
          <p class="text-danger-fg">{{ key | transloco }}</p>
        }
      }
    </section>
  `,
})
export class BackfillRunStatusView {
  readonly run = input.required<BackfillRun>();
  readonly kind = input.required<'active' | 'last'>();
  /** The provider-wide cooldown (`GET` payload): a queued run waits it out too. */
  readonly cooldownUntilUtc = input<string | null>(null);

  private readonly languageService = inject(LanguageService);

  protected readonly pluralKey = pluralKey;

  protected readonly headingId = computed(() => `backfill-${this.kind()}-run-heading`);

  readonly statusKey = computed(() => backfillRunStatusKey(this.run().status));

  protected readonly tone = computed<StatusBadgeTone>(
    () => STATUS_TONES[this.run().status] ?? 'neutral',
  );

  /** `weeksDone` of `weeksTotal` as 0..1; a run with no weeks is neither done nor started. */
  readonly progressFraction = computed(() => {
    const { weeksDone, weeksTotal } = this.run();
    return weeksTotal > 0 ? Math.min(1, weeksDone / weeksTotal) : 0;
  });

  /** Only a queued run has a position. */
  readonly queuePosition = computed(() => {
    const run = this.run();
    return run.status === 'queued' ? run.queuePosition : null;
  });

  /**
   * When the archive asked us to wait: the paused run's own deadline, or the provider cooldown for
   * a run that is still queued behind it. A running run is not waiting; a terminal one never is.
   */
  readonly waitUntil = computed(() => {
    const run = this.run();
    if (run.status === 'paused') {
      return run.pausedUntilUtc;
    }
    return run.status === 'queued' ? this.cooldownUntilUtc() : null;
  });

  readonly setLabel = computed(() => backfillSetLabel(this.run()));

  /** `windowTo` is exclusive: the last imported day is the one before it. */
  readonly lastDay = computed(() => lastDayOf(this.run().windowTo));

  readonly errorKey = computed(() => backfillRunErrorKey(this.run().errorCode));

  protected formatDay(isoDay: string): string {
    return formatIsoDay(isoDay, this.languageService.lang());
  }

  protected formatTime(iso: string): string {
    return formatInstant(iso, this.languageService.lang());
  }
}
