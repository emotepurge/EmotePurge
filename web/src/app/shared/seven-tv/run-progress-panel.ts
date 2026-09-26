import { Component, computed, inject, input, output } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import {
  SyncReportReason,
  SyncReportState,
  isChannelMismatch,
} from '../../core/seven-tv/sync-report-outcome';
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
            {{ summaryCountsKey() | transloco: summaryCounts() }}
          </span>
          <ng-content select="[run-actions]" />
        </div>
      }

      @if (syncReportFailed()) {
        <app-notice-banner class="mt-3 block" variant="warning">
          <span class="flex flex-col gap-1">
            <span class="font-medium">{{ syncReportTitleKey() | transloco }}</span>
            <span>{{ syncReportTextKey() | transloco }}</span>
            <!-- Why the report failed or fell short (spec E23) — its own line, because a bare
                 "failed" does not say whether the right is gone or the server did not answer. -->
            @if (syncReportReason(); as reason) {
              <span>{{ 'syncReportReason.' + reason | transloco }}</span>
            }
          </span>
          <!-- No retry for a channel mismatch (addendum N4): it is recorded, and the resync that
               heals it already runs — a retry could only repeat the same mismatch. -->
          @if (syncRetryOffered()) {
            <button
              notice-action
              type="button"
              appButton="outline"
              (click)="syncRetryRequested.emit()"
            >
              {{ labelPrefix() + '.syncRetry' | transloco }}
            </button>
          }
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
  /** Why `syncReport` is `'failed'`/`'partial'` (spec E23) — shown as its own line in the report
   *  notice, `null` (the default) shows none. One wording family for all three runs. */
  readonly syncReportReason = input<SyncReportReason | null>(null);
  /** Seconds left on a 7TV rate-limit pause, null while running normally. */
  readonly rateLimitPauseSeconds = input<number | null>(null);
  /** Whether Close is offered once the run stops running. Every host binds this to its run's
   *  lifecycle (`run.phase === 'closed'`, #256) so Close cannot end a run whose report is still
   *  unanswered — the import additionally cannot end one whose protocol does not exist yet, see
   *  `import-progress-section.ts` for why that window matters there. Defaults to `true` only for a
   *  caller that has no run record to bind it to (there is none left in this app since #256 T2
   *  moved delete and restore onto the same lifecycle as the import). */
  readonly dismissible = input(true);
  /** Rows counted `done` that are not a copy — today only an import run's adopted renames (an
   *  existing target entry renamed in place, not a new entry added, spec #255). Subtracted out of
   *  `summaryCounts().done` and broken out as its own `renamed` count once positive; `null` (the
   *  default) leaves `summaryCounts()` exactly as it always was — delete and restore never pass it. */
  readonly renamedCount = input<number | null>(null);
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
    const renamed = this.renamedCount() ?? 0;
    return {
      done: statuses.filter((status) => status === 'done').length - renamed,
      renamed,
      failed: statuses.filter((status) => status === 'failed').length,
      cancelled: statuses.filter((status) => status === 'cancelled').length,
    };
  });

  /** Which summary sentence to speak — the one that also names the renamed count once there is one
   *  to name, otherwise the same `.summary.counts` every host has always had (massDelete and
   *  restore never pass `renamedCount`, so `summaryCounts().renamed` is always 0 for them and this
   *  always resolves to `.summary.counts`). */
  protected readonly summaryCountsKey = computed(() =>
    this.summaryCounts().renamed > 0
      ? `${this.labelPrefix()}.summary.countsWithRenamed`
      : `${this.labelPrefix()}.summary.counts`,
  );

  // 'partial' shares the notice slot with 'failed' — same banner, same reason line, same
  // (conditional) retry button — but not the same title/text (#255): 'failed' means the report
  // never got through, 'partial' means it did and the backend recorded *something*, just not
  // everything (`syncPartialTitle`/`syncPartial` say so — "vermerkt, aber …" — instead of the
  // 'failed' wording's "fehlgeschlagen … konnte es nicht vermerken", which is simply wrong for a
  // report that was in fact recorded).
  protected readonly syncReportFailed = computed(
    () => this.syncReport() === 'failed' || this.syncReport() === 'partial',
  );

  /** `.syncPartialTitle` while `syncReport` is `'partial'`, `.syncFailedTitle` otherwise (only
   *  reached while `syncReportFailed()` is true, i.e. also for `'failed'`). */
  protected readonly syncReportTitleKey = computed(
    () =>
      `${this.labelPrefix()}.${this.syncReport() === 'partial' ? 'syncPartialTitle' : 'syncFailedTitle'}`,
  );

  /** Same split as {@link syncReportTitleKey}, for the notice's body line. */
  protected readonly syncReportTextKey = computed(
    () => `${this.labelPrefix()}.${this.syncReport() === 'partial' ? 'syncPartial' : 'syncFailed'}`,
  );

  /** "Erneut melden" for `failed` (any reason) and `partial`/`shortfall`, never for either
   *  channel-mismatch reason (addendum N4, AK 40) — the services refuse that retry as well. */
  protected readonly syncRetryOffered = computed(() => !isChannelMismatch(this.syncReportReason()));

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
