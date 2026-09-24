import { Component, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SyncReportState } from '../../core/seven-tv/sync-report-outcome';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';

/** Renamed from DeleteProgressPanel when the restore run (A6) became its second consumer — the
 *  mechanics (bar, cancel, rate-limit countdown, failure list) are run-generic; only the wording
 *  differs, selected via `labelPrefix`. Dynamic Transloco keys follow the established
 *  `'prefix.' + key` pattern, and the input union keeps them findable. */
@Component({
  selector: 'app-run-progress-panel',
  imports: [Button, NoticeBanner, TranslocoPipe],
  template: `
    <!-- role="status" is implicitly aria-atomic="true" (docs/UI-Designsprache.md §4.5): every change
         to the progress text would re-read the whole region, the progressbar's name and value
         included. Non-atomic, only the changed text is announced. -->
    <div class="rounded-md bg-surface-inset px-4 py-3" role="status" aria-atomic="false">
      <div class="mb-2 flex items-center justify-between text-sm">
        <span>{{
          labelPrefix() + '.progress' | transloco: { finished: finished(), total: total() }
        }}</span>
        @if (isRunning()) {
          <button type="button" appButton="danger-quiet" (click)="cancelled.emit()">
            {{ 'common.cancel' | transloco }}
          </button>
        } @else if (dismissible()) {
          <button type="button" appButton="neutral" (click)="dismissed.emit()">
            {{ 'common.close' | transloco }}
          </button>
        } @else {
          <span class="text-fg-muted">{{ labelPrefix() + '.settling' | transloco }}</span>
        }
      </div>
      <!-- The track is one step further from the surface than the panel it sits in, so it stays
           visible whichever direction "further" means in the current mode. -->
      <div
        class="h-2 w-full overflow-hidden rounded-full bg-surface-inset-hover"
        role="progressbar"
        [attr.aria-valuenow]="finished()"
        aria-valuemin="0"
        [attr.aria-valuemax]="progressValueMax()"
        [attr.aria-label]="labelPrefix() + '.progressBarLabel' | transloco"
      >
        <div class="h-full bg-accent transition-all" [style.width.%]="progressPercent()"></div>
      </div>

      <!-- Without this the bar just stops for up to a minute, which reads as a crash. -->
      @if (rateLimitPauseSeconds() !== null) {
        <p class="mt-2 text-sm text-warning-fg">
          {{ labelPrefix() + '.rateLimitPaused' | transloco: { seconds: rateLimitPauseSeconds() } }}
        </p>
      }

      @if (failedItems().length > 0) {
        <ul class="mt-3 space-y-1 text-sm text-danger-fg" role="alert">
          @for (item of failedItems(); track item.key) {
            <li>{{ item.name }}: {{ failureText(item) }}</li>
          }
        </ul>
      }

      <!-- Post-run summary (A6): the counts as text, plus whatever run-scoped actions the host
           projects (protocol download, restore). Rendered only once the run has settled — during
           the run the bar and the failure list already say everything.

           The gate on the projection slot is a contract, not decoration: whatever a host puts in
           the run-actions slot is post-run-only and must not be something the user needs mid-run.
           Most projected items are already empty during a run on their own -- delete's lastRun,
           import's and restore's resyncTrigger are all first written in onRunComplete, which the
           engine calls after it has set isRunning to false -- so the gate reads redundant. It is
           not: ImportProgressSection's "open the target channel" link carries no condition of its
           own and relies on this gate alone. Without it the link would be clickable while a run
           writes, and would send the user into the leave guard.

           One trap for whoever changes this: abortedForPrivileges is set *during* the run, in the
           engine's per-row abort hook, not afterwards. It is never visible mid-run only because
           the abort and isRunning.set(false) fall in the same synchronous tick, and zoneless
           change detection renders nothing in between. Should that hook ever gain a warning that
           does not abort, this block would swallow it silently. -->
      @if (!isRunning() && total() > 0) {
        <div class="mt-3 flex flex-wrap items-center gap-x-3 gap-y-2 text-sm">
          <span class="text-fg-secondary">
            {{ labelPrefix() + '.summary.counts' | transloco: summaryCounts() }}
          </span>
          <ng-content select="[run-actions]" />
        </div>
      }

      @if (syncReportFailed()) {
        <app-notice-banner class="mt-3 block" variant="warning">
          <span class="flex flex-col gap-1">
            <span class="font-medium">{{ labelPrefix() + '.syncFailedTitle' | transloco }}</span>
            <span>{{ labelPrefix() + '.syncFailed' | transloco }}</span>
          </span>
          <button
            notice-action
            type="button"
            appButton="outline"
            (click)="syncRetryRequested.emit()"
          >
            {{ labelPrefix() + '.syncRetry' | transloco }}
          </button>
        </app-notice-banner>
      } @else if (syncReport() === 'succeeded' && !isRunning()) {
        <p class="mt-3 text-sm text-fg-muted">
          {{ labelPrefix() + '.syncRetrySucceeded' | transloco }}
        </p>
      }
    </div>
  `,
})
export class RunProgressPanel {
  readonly items = input.required<RunQueueItem[]>();
  readonly isRunning = input.required<boolean>();
  /** Which wording family the panel speaks — the union keeps the dynamic keys findable. */
  readonly labelPrefix = input<'massDelete' | 'restore' | 'import'>('massDelete');
  /** State of the run's closing bookkeeping call (sync-deleted / sync-restored). Defaults to the
   *  state that renders nothing; the notice wording follows labelPrefix. */
  readonly syncReport = input<SyncReportState>('idle');
  /** Seconds left on a 7TV rate-limit pause, null while running normally. */
  readonly rateLimitPauseSeconds = input<number | null>(null);
  /** Whether Close is offered once the run stops running. A host binds this to its own settlement
   *  signal (import: `run.settlement === 'settled'`) so Close cannot end a run whose protocol and
   *  unload cover have not been produced yet — see `import-progress-section.ts` for why that window
   *  matters. Defaults to `true`: delete and restore never pass it, so they keep the panel's
   *  original behaviour of offering Close the moment the run stops. */
  readonly dismissible = input(true);
  readonly cancelled = output<void>();
  readonly dismissed = output<void>();
  readonly syncRetryRequested = output<void>();

  private readonly translocoService = inject(TranslocoService);

  protected readonly total = computed(() => this.items().length);
  // An `unknown` row is finished too: the run is done with it, 7TV's answer is what is missing.
  protected readonly finished = computed(
    () =>
      this.items().filter(
        (item) => item.status === 'done' || item.status === 'failed' || item.status === 'unknown',
      ).length,
  );
  protected readonly progressPercent = computed(() =>
    this.total() === 0 ? 0 : (this.finished() / this.total()) * 100,
  );
  // Raw counts, not a percentage, so aria-valuenow/valuemax are never rounded: rounding would
  // report 100 at 199/200 (early completion) and 0 at 1/201 (false zero). `Math.max(1, …)` only
  // keeps min <= max for an empty queue; hosts never render the bar for one.
  protected readonly progressValueMax = computed(() => Math.max(1, this.total()));
  protected readonly failedItems = computed(() =>
    this.items().filter((item) => item.status === 'failed' || item.status === 'unknown'),
  );

  protected readonly summaryCounts = computed(() => {
    const statuses = this.items().map((item) => item.status);
    return {
      done: statuses.filter((status) => status === 'done').length,
      failed: statuses.filter((status) => status === 'failed').length,
      cancelled: statuses.filter((status) => status === 'cancelled').length,
    };
  });

  // 'partial' shares the notice with 'failed': in both cases the backend's view of the set differs
  // from what was actually deleted, and the remedy (retry, or wait for the periodic resync) is the same.
  protected readonly syncReportFailed = computed(
    () => this.syncReport() === 'failed' || this.syncReport() === 'partial',
  );

  /** An `unknown` row gets its own wording family rather than its transport error: the point for
   *  the user is not *why* 7TV's answer is missing but that the row's outcome has to be checked. */
  protected failureText(item: RunQueueItem): string {
    if (item.status === 'unknown') {
      return this.translocoService.translate(`${this.labelPrefix()}.unknownOutcome`);
    }
    return (
      item.errorMessage ??
      this.translocoService.translate(`${this.labelPrefix()}.deleteFailedFallback`)
    );
  }
}
