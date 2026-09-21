import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import { copiedNotActiveNotice, resyncNoticeKey } from './dock-outcome-announcer';
import { RunProgressPanel } from './run-progress-panel';

/**
 * The usage-stats page's window onto a copy run (#72, K3) — a page-level section, not content
 * projected into `app-mass-delete-panel`: that panel is also mounted on the voting-results page,
 * where a run copying emotes into some *other* channel's set has no business appearing.
 *
 * No inputs, on purpose: the run lives in `SevenTvImportService` (`providedIn: 'root'`), so this
 * component shows the same run regardless of which channel's usage-stats page happens to mount
 * it — including a channel that is neither the run's source nor its target (R9). That is exactly
 * why the target line below is unconditional rather than folded into some "your import" framing:
 * on a page that is not the target, an unlabelled progress bar would read as a run *of* that page.
 *
 * Renders nothing until there is something to show (`isRunning()` or a non-empty `queue()`) — the
 * same "settled run stays visible" contract the mass-delete panel already has, so the post-run
 * summary and the resync notice remain readable after the last row finishes.
 */
@Component({
  selector: 'app-import-progress-section',
  imports: [Button, NoticeBanner, RouterLink, RunProgressPanel, TranslocoPipe],
  template: `
    <!-- #149 P2 (independent review): gated on duplicateNoticePending, not just skippedDuplicates() >
         0 — a transient notice (design doc §4.5), not a persistent one, so it never sits attached to
         a *later*, unrelated run's details with nothing to clear it. See that signal's doc for why
         it also has to be what keeps the dock (and this section) mounted for a fully-refused
         (all-duplicates) run, which leaves no run/queue behind of its own.

         Every notice in this section is aria-hidden: its announcement comes from the host page's
         permanently mounted DockOutcomeAnnouncer, not from here. This section lives in the dock,
         which can mount in the same pass that sets the notice, and a status region created
         together with its text announces nothing (docs/UI-Designsprache.md §4.5). -->
    @if (importService.duplicateNoticePending() && importService.skippedDuplicates() > 0) {
      <p aria-hidden="true" class="text-sm text-fg-secondary">
        {{ skippedDuplicatesKey() | transloco: { count: importService.skippedDuplicates() } }}
      </p>
    }
    <!-- The fresh pre-send duplicate check's fetch failed (already-present-filter.ts) — every row
         still went through, so a duplicate may have slipped in undetected. A quiet notice, not an
         alarm: the run is still expected to succeed, this only says the guard could not run. -->
    @if (importService.duplicateNoticePending() && !importService.duplicateCheckAvailable()) {
      <p aria-hidden="true" class="text-sm text-fg-secondary">
        {{ 'import.duplicateCheckUnavailable' | transloco }}
      </p>
    }
    @if (importService.isRunning() || importService.queue().length > 0) {
      @if (importService.run(); as run) {
        <div class="flex flex-col gap-2">
          <p class="text-xs text-fg-muted">
            <!-- targetChannelName is null for an untracked run (T2.6, spec 8.6) — there is no
                 channel of ours to name, so this reads off the set/owner the picker's own
                 confirmation already named instead (import-target-dialog.ts). Both branches now
                 name the set too (finding 2, Live-Verifikation K2 2026-09-21): a tracked *active*
                 target keeps the plain "Ziel: channel" wording (the one-click path's own set is
                 never in question there), a tracked *non*-active one adds the set name — mirroring
                 the confirm dialog's own "Ziel: channel · Set setName" line — and the untracked
                 branch now names the set by its resolved *name* (run.targetSetName, id-falls-back)
                 rather than the raw id it used to show. -->
            @if (run.targetChannelName; as targetChannelName) {
              @if (run.targetIsActiveSet) {
                {{ 'import.summary.target' | transloco: { channel: targetChannelName } }}
              } @else {
                {{
                  'import.summary.targetWithSet'
                    | transloco: { channel: targetChannelName, setName: run.targetSetName }
                }}
              }
            } @else {
              {{
                'import.summary.targetSet'
                  | transloco
                    : { setName: run.targetSetName, owner: run.targetOwnerDisplayName ?? '' }
              }}
            }
          </p>
          <app-run-progress-panel
            [items]="importService.queue()"
            [isRunning]="importService.isRunning()"
            labelPrefix="import"
            [syncReport]="importService.syncReport()"
            [rateLimitPauseSeconds]="importService.rateLimitPauseSeconds()"
            (cancelled)="importService.cancel()"
            (dismissed)="importService.reset()"
            (syncRetryRequested)="importService.retrySyncReport()"
          >
            <ng-container run-actions>
              @if (importService.abortedForPrivileges()) {
                <app-notice-banner variant="warning">
                  {{ 'import.summary.insufficientPrivileges' | transloco }}
                </app-notice-banner>
              }
              <!-- A tracked *non*-active target never gets the resync notice (onRunComplete skips
                   the resync itself, finding 3, Live-Verifikation K2 2026-09-21) — this notice takes
                   its place, naming what actually happened instead of claiming a channel-page update
                   that never comes. aria-hidden for the same reason as the duplicate notices above,
                   and as the resync notice it replaces. -->
              @if (copiedNotActiveNotice(); as notActive) {
                <span aria-hidden="true" class="text-xs text-fg-muted">
                  {{ 'import.summary.copiedNotActive' | transloco: notActive }}
                </span>
              } @else if (resyncNoticeKey(); as noticeKey) {
                <span aria-hidden="true" class="text-xs text-fg-muted">
                  {{ noticeKey | transloco }}
                </span>
              }
              <!-- No channel of ours to open for an untracked run (T2.6), and none for a tracked
                   *non*-active run either (finding 3): the channel page shows its active set, never
                   the one this run actually wrote to, so the link would point at a page that does
                   not show the result — the notice above says so instead. -->
              @if (run.targetIsActiveSet && run.targetChannelName; as targetChannelName) {
                <a
                  appButton="outline"
                  [routerLink]="['/channels', targetChannelName, 'usage-stats']"
                >
                  {{ 'import.summary.openTarget' | transloco }}
                </a>
              }
            </ng-container>
          </app-run-progress-panel>
        </div>
      }
    }
  `,
})
export class ImportProgressSection {
  protected readonly importService = inject(SevenTvImportService);

  /** #149/T5: wording for how many rows the fresh pre-run duplicate check
   *  (`already-present-filter.ts`, run from `import-flow.ts`) dropped — shown independently of the
   *  run-progress panel below, because a run where the fresh check caught everything queues nothing
   *  and would otherwise leave that panel hidden (its own gate is `isRunning() || queue().length
   *  > 0`), silently swallowing the one thing the user needs to see in that case. */
  protected readonly skippedDuplicatesKey = computed(() =>
    pluralKey(this.importService.skippedDuplicates(), 'import.skippedDuplicates'),
  );

  /** Same key the page's DockOutcomeAnnouncer speaks — see `resyncNoticeKey`. */
  protected readonly resyncNoticeKey = computed(() =>
    resyncNoticeKey(this.importService.resyncTrigger(), 'import'),
  );

  /** Same params the page's DockOutcomeAnnouncer speaks — see `copiedNotActiveNotice`. */
  protected readonly copiedNotActiveNotice = computed(() =>
    copiedNotActiveNotice(this.importService.run()),
  );
}
