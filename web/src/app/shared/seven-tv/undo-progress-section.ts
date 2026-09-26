import { Dialog } from '@angular/cdk/dialog';
import { Component, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { SevenTvUndoService } from '../../core/seven-tv/seven-tv-undo.service';
import { SyncReportState, isChannelMismatch } from '../../core/seven-tv/sync-report-outcome';
import { CSV_MIME } from '../export/csv';
import { ExportDialogData, FORMAT_EXPORT_OPTIONS, openExportDialog } from '../export/export-dialog';
import { JSON_MIME } from '../export/export-envelope';
import { downloadFile } from '../export/file-download';
import {
  buildUndoRunProtocol,
  transferUndoCsv,
  transferUndoFilename,
  transferUndoJson,
} from '../export/transfer-undo-export';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import { resyncNoticeKey, undoSkippedLines, undoSkippedNotice } from './dock-outcome-announcer';
import { RunProgressPanel, RunProgressTally } from './run-progress-panel';

/**
 * The usage-stats page's window onto a replace undo (#254, spec 4.7, 6.6) — a page-level section
 * beside the import and restore sections, outside the set gate: an undo writes into whatever set its
 * transfer file names, which need not be the set on screen or even this channel's.
 *
 * No inputs, same reasoning as `ImportProgressSection`: the run lives in `SevenTvUndoService`
 * (`providedIn: 'root'`), so the target line is unconditional. Renders nothing without a shown run
 * (`run()`) or the transient skipped notice — a run stays shown, settled, until Close or a channel
 * switch (`resetIfChannelChanged`) detaches it.
 *
 * **What is counted where** (plan Festlegung 3/5; no row is ever counted twice):
 * - The bar and the summary sentence read the service's `progress()` and `summary()` through the
 *   panel's `tally`, never the panel's own count by engine status: a row the recheck before its
 *   REMOVE skipped is `cancelled` for the engine, yet it is finished and named under its reason —
 *   `summary().cancelled` already leaves it out, and it appears once, in its skipped line.
 * - A `partial` row is `done` for the engine and in the sentence's done count; the partial line
 *   next to it says how many of those were only partly done.
 * - Skip reasons reach the user as one line per reason (`undo.summary.skipped`) from
 *   `summary().skippedByReason` — the panel's failure list never shows a cancelled row's text.
 *
 * **Two reports** (Festlegung 3): the panel carries the removal report (`sync-deleted`, the
 * destructive fact) with its reason and retry; this section shows the restore report
 * (`sync-restored`) as its own banner right below the panel, so the two read in the order they are
 * sent (F8). Both retries follow the service's own locks, mirrored here rather than left to a
 * silent refusal: never while a report is pending (no banner then), never for a channel mismatch,
 * and only for a settled run that confirmed something to report.
 */
@Component({
  selector: 'app-undo-progress-section',
  imports: [Button, NoticeBanner, RunProgressPanel, TranslocoPipe],
  template: `
    <!-- The candidates the last start skipped, by reason (spec 4.7) — transient, and the only place
         a start that ran nothing names them; also what keeps the dock mounted for that start. Once
         the run that start began has stopped running, its summary names the same candidates and
         this notice gives way (undoSkippedNotice). aria-hidden: the host page's permanently mounted
         DockOutcomeAnnouncer speaks it (docs/UI-Designsprache.md §4.5). -->
    @for (line of skippedNotice(); track line.reason) {
      <p aria-hidden="true" class="text-sm text-fg-secondary">
        {{
          'undo.summary.skipped'
            | transloco
              : { count: line.count, reason: ('undo.confirm.reason.' + line.reason | transloco) }
        }}
      </p>
    }
    @if (undoService.run(); as run) {
      <div class="flex flex-col gap-2">
        <!-- The target line, the restore's wording: a tracked channel by name, an untracked target
             by its owner — the set name always, since the target need not be the set on screen. -->
        <p class="text-xs text-fg-muted">
          @if (run.trackedChannelName; as channel) {
            {{ 'undo.summary.targetChannel' | transloco: { channel, setName: run.setName } }}
          } @else {
            {{
              'undo.summary.targetOwner'
                | transloco: { owner: run.ownerOrChannelLabel, setName: run.setName }
            }}
          }
        </p>
        <app-run-progress-panel
          [items]="undoService.items()"
          [isRunning]="undoService.isRunning()"
          labelPrefix="undo"
          [tally]="tally()"
          [syncReport]="removalReportShown()"
          [syncReportReason]="undoService.removalReportReason()"
          [rateLimitPauseSeconds]="undoService.rateLimitPauseSeconds()"
          [dismissible]="run.phase === 'closed'"
          (cancelled)="undoService.cancel()"
          (dismissed)="undoService.reset()"
          (syncRetryRequested)="undoService.retryRemovalReport()"
        >
          <ng-container run-actions>
            <!-- The finished protocol (AK 18): from settled on, not before — built from the run's
                 own settled record (buildUndoRunProtocol), never the engine's queue. -->
            @if (run.settlement === 'settled') {
              <button type="button" appButton="neutral" (click)="openProtocolExport()">
                {{ 'undo.summary.downloadProtocol' | transloco }}
              </button>
            }
            @if (undoService.abortedForPrivileges()) {
              <app-notice-banner variant="warning">
                {{ 'undo.summary.insufficientPrivileges' | transloco }}
              </app-notice-banner>
            }
            @for (counter of counters(); track counter.key) {
              <span class="text-xs text-fg-muted">
                {{ counter.key | transloco: { count: counter.count } }}
              </span>
            }
            @for (line of skippedLines(); track line.reason) {
              <span class="text-xs text-fg-muted">
                {{
                  'undo.summary.skipped'
                    | transloco
                      : {
                          count: line.count,
                          reason: ('undo.confirm.reason.' + line.reason | transloco),
                        }
                }}
              </span>
            }
            <!-- Only backendTriggered or the N1 fallback ever get here (E16) — a non-active or
                 untracked target never has a resync of its own. aria-hidden, spoken by the
                 announcer, like the import's and the restore's. -->
            @if (resyncKey(); as key) {
              <span aria-hidden="true" class="text-xs text-fg-muted">{{ key | transloco }}</span>
            }
            @if (run.settlement === 'settled' && !undoService.protocolSaved()) {
              <span class="text-xs text-fg-muted">
                {{ 'undo.summary.protocolNotSaved' | transloco }}
              </span>
            }
          </ng-container>
        </app-run-progress-panel>
        <!-- The restore report (sync-restored), second in line after the panel's removal report. -->
        @if (restoreReportFailed()) {
          <app-notice-banner variant="warning">
            <span class="flex flex-col gap-1">
              <span class="font-medium">{{ restoreSyncTitleKey() | transloco }}</span>
              <span>{{ restoreSyncTextKey() | transloco }}</span>
              @if (undoService.restoreReportReason(); as reason) {
                <span>{{ 'syncReportReason.' + reason | transloco }}</span>
              }
            </span>
            @if (restoreRetryOffered()) {
              <button
                notice-action
                type="button"
                appButton="outline"
                (click)="undoService.retryRestoreReport()"
              >
                {{ 'undo.restoreSyncRetry' | transloco }}
              </button>
            }
          </app-notice-banner>
        } @else if (restoreReportShown() === 'succeeded') {
          <p class="text-sm text-fg-muted">{{ 'undo.restoreSyncSucceeded' | transloco }}</p>
        }
      </div>
    }
  `,
})
export class UndoProgressSection {
  protected readonly undoService = inject(SevenTvUndoService);
  private readonly dialog = inject(Dialog);

  /** The panel's counts from the service (see the class doc): a `partial` row is done there. */
  protected readonly tally = computed<RunProgressTally>(() => {
    const summary = this.undoService.summary();
    return {
      finished: this.undoService.progress().finished,
      done: summary.done + summary.partialRows,
      failed: summary.failed,
      cancelled: summary.cancelled,
    };
  });

  /** The counter lines of spec 4.7, in this order: what was removed, what came back, what stays
   *  unclear (and where an unclear REMOVE is recorded, 11.2 — the import's `unknownRecordedIn`
   *  right after its unknown rows), then what is left to do (gaps, partial rows, omitted entries,
   *  each naming the same way out: the same undo again) and the foreign-entry note. Only non-zero
   *  counters; each a plural key. */
  protected readonly counters = computed(() => {
    const summary = this.undoService.summary();
    return [
      { base: 'undo.summary.removed', count: summary.removedCount },
      { base: 'undo.summary.restored', count: summary.restoredCount },
      { base: 'undo.summary.unknownRows', count: summary.unknownCount },
      { base: 'undo.summary.unknownRecordedIn', count: summary.unknownRemovalCount },
      { base: 'undo.summary.gaps', count: summary.gapCount },
      { base: 'undo.summary.partialRows', count: summary.partialRows },
      { base: 'undo.summary.omittedEntries', count: summary.omittedEntryCount },
      { base: 'undo.summary.foreignNoted', count: summary.foreignNotedRows },
    ]
      .filter((counter) => counter.count > 0)
      .map((counter) => ({ key: pluralKey(counter.count, counter.base), count: counter.count }));
  });

  /** Every skipped candidate of the shown run by reason — the dialog's, the freshness check's,
   *  the service's own locks' and the recheck's — each exactly once. */
  protected readonly skippedLines = computed(() =>
    undoSkippedLines(this.undoService.summary().skippedByReason),
  );

  /** The transient notice of the last start — same gate the announcer speaks it under. */
  protected readonly skippedNotice = computed(
    () =>
      undoSkippedNotice({
        noticePending: this.undoService.noticePending(),
        noticeSkipped: this.undoService.noticeSkipped(),
        run: this.undoService.run(),
        isRunning: this.undoService.isRunning(),
      }) ?? [],
  );

  /** The removal report as the panel sees it — `idle` until the shown run settled with at least one
   *  confirmed REMOVE, the only state in which the service sends (or retries) it. */
  protected readonly removalReportShown = computed<SyncReportState>(() =>
    this.reportable(this.undoService.summary().removedCount)
      ? this.undoService.removalReport()
      : 'idle',
  );

  /** The restore report, gated like `removalReportShown` on confirmed ADDs. */
  protected readonly restoreReportShown = computed<SyncReportState>(() =>
    this.reportable(this.undoService.summary().restoredCount)
      ? this.undoService.restoreReport()
      : 'idle',
  );

  protected readonly restoreReportFailed = computed(
    () => this.restoreReportShown() === 'failed' || this.restoreReportShown() === 'partial',
  );

  /** No retry for either channel-mismatch reason (addendum N4); `pending` never reaches here. */
  protected readonly restoreRetryOffered = computed(
    () => !isChannelMismatch(this.undoService.restoreReportReason()),
  );

  protected readonly restoreSyncTitleKey = computed(() =>
    this.restoreReportShown() === 'partial'
      ? 'undo.restoreSyncPartialTitle'
      : 'undo.restoreSyncFailedTitle',
  );

  protected readonly restoreSyncTextKey = computed(() =>
    this.restoreReportShown() === 'partial' ? 'undo.restoreSyncPartial' : 'undo.restoreSyncFailed',
  );

  /** Same key the page's DockOutcomeAnnouncer speaks — see `resyncNoticeKey`. */
  protected readonly resyncKey = computed(() =>
    resyncNoticeKey(this.undoService.resyncTrigger(), 'undo'),
  );

  /** The `finished` transfer-undo protocol (AK 18), JSON or CSV — `buildUndoRunProtocol` answers
   *  `null` until the run settled. The run is captured before the dialog opens, and only that run is
   *  marked saved: a report of an older run can put that run back on show (`reshow`) meanwhile. */
  protected openProtocolExport(): void {
    const run = this.undoService.run();
    const protocol = run === null ? null : buildUndoRunProtocol(run);
    if (run === null || protocol === null) {
      return;
    }
    // A tracked target by its channel, an untracked one by its set id — never the owner's display
    // name, which can be absent (the import's rule).
    const label = run.trackedChannelName ?? run.targetSetId;
    const data: ExportDialogData = {
      rowCount: protocol.rows.length,
      filtered: false,
      selectionCount: null,
      noticeKeys: [],
      optionsLegendKey: 'export.formatLabel',
      options: FORMAT_EXPORT_OPTIONS,
    };
    openExportDialog(this.dialog, data).closed.subscribe((choice) => {
      if (choice?.optionId === 'csv') {
        downloadFile(
          transferUndoFilename(label, protocol.meta.finishedAt, 'csv'),
          transferUndoCsv(protocol),
          CSV_MIME,
        );
      } else if (choice?.optionId === 'json') {
        downloadFile(
          transferUndoFilename(label, protocol.meta.finishedAt, 'json'),
          transferUndoJson(protocol),
          JSON_MIME,
        );
      }
      if (choice && this.undoService.run()?.runId === run.runId) {
        this.undoService.markProtocolSaved();
      }
    });
  }

  /** A report can only exist for a settled shown run with something confirmed to report. */
  private reportable(confirmedCount: number): boolean {
    return this.undoService.settlement() === 'settled' && confirmedCount > 0;
  }
}
