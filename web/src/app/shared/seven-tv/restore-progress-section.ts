import { Component, computed, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { resyncNoticeKey } from './dock-outcome-announcer';
import { RunProgressPanel } from './run-progress-panel';

/**
 * The restore half of the action dock (spec #253, E22, 6.6) — a page-level section, not content
 * projected into `app-mass-delete-panel`: since T9 a restore can be started from a page with no
 * selected set at all (`app-import-trigger` reading a restore file, spec 4.1) or can target a set
 * other than the one on screen (E13/E21), so the panel — bound to *this* channel's set — is the
 * wrong home for it. `MassDeletePanel` keeps only the entry points that still need it: the
 * "Restore" button at a finished delete run and the pre-check + confirmation it opens (E16); this
 * section owns everything about *showing* the run once it exists — notices, progress, the target
 * line, the sync report and the resync acknowledgement (T7).
 *
 * No inputs, same reasoning as `ImportProgressSection`: the run lives in `SevenTvRestoreService`
 * (`providedIn: 'root'`), so this component shows the same run regardless of which page happens to
 * mount it. The target line is therefore unconditional, never folded into "your restore" framing —
 * on a page that did not start the run and is not its target, an unlabelled progress bar would read
 * as a run *of* that page.
 *
 * Renders nothing until there is something to show (`isRunning()`/non-empty `queue()`, or the
 * transient duplicate/name-taken/check-unavailable notice) — the same "settled run stays visible"
 * contract `ImportProgressSection` and the panel this was extracted from already have.
 */
@Component({
  selector: 'app-restore-progress-section',
  imports: [RunProgressPanel, TranslocoPipe],
  template: `
    <!-- #149 P2 (independent review): gated on duplicateNoticePending, not just
         skippedDuplicates() > 0 — a transient notice (design doc §4.5), not a persistent one, so it
         never sits attached to a *later*, unrelated run's details with nothing to clear it. It also
         keeps this section (and the dock) mounted for a fully-refused (all-duplicates) restore,
         which leaves no run/queue behind of its own — including a file-based restore reached via
         ImportTrigger, which has nothing marked in any channel's grid to keep the dock open
         otherwise.

         Every notice in this section is aria-hidden: its announcement comes from the host page's
         permanently mounted DockOutcomeAnnouncer, not from here. This section lives in the dock,
         which can mount in the same pass that sets the notice, and a status region created together
         with its text announces nothing (docs/UI-Designsprache.md §4.5). -->
    @if (restoreService.duplicateNoticePending() && restoreService.skippedDuplicates() > 0) {
      <p aria-hidden="true" class="text-sm text-fg-secondary">
        {{ skippedDuplicatesKey() | transloco: { count: restoreService.skippedDuplicates() } }}
      </p>
    }
    <!-- Aliases the same check left out because another emote now holds the name — its own line, so
         "skipped" is never read as "was already there". -->
    @if (restoreService.duplicateNoticePending() && restoreService.skippedNameTaken() > 0) {
      <p aria-hidden="true" class="text-sm text-fg-secondary">
        {{ skippedNameTakenKey() | transloco: { count: restoreService.skippedNameTaken() } }}
      </p>
    }
    <!-- The pre-run duplicate check's fetch failed (already-present-filter.ts) — every row still
         went through, so a duplicate may have slipped in undetected. A quiet notice, not an alarm:
         the run is still expected to succeed, this only says the guard could not run. -->
    @if (restoreService.duplicateNoticePending() && !restoreService.duplicateCheckAvailable()) {
      <p aria-hidden="true" class="text-sm text-fg-secondary">
        {{ 'restore.duplicateCheckUnavailable' | transloco }}
      </p>
    }
    @if (restoreService.isRunning() || restoreService.queue().length > 0) {
      @if (restoreService.run(); as run) {
        <div class="flex flex-col gap-2">
          <!-- The dock's target line (spec 4.4 point 12), the restore's own version of
               ImportProgressSection's target line: a run started on this page, or read from
               another page entirely (E13), can write into a set neither of them shows — the channel
               that started it is the only thing resetIfChannelChanged (F7) reads, never this
               line. A tracked channel (either expectedChannelName, an active set, or
               resyncChannelName, a non-active one — RestoreStartTarget, spec 6.4) is named
               directly; an untracked target names its owner instead. Either way the set name is
               always shown too, unlike the import target line's tracked-active fast path
               (import-progress-section.ts:104-112): a restore's own page is never assumed to be
               the run's target the way a copy's source page is. -->
          <p class="text-xs text-fg-muted">
            @if (targetChannelName(run); as channel) {
              {{ 'restore.targetLine.channel' | transloco: { channel, setName: run.setName } }}
            } @else {
              {{
                'restore.targetLine.owner'
                  | transloco: { owner: run.ownerOrChannelLabel, setName: run.setName }
              }}
            }
          </p>
          <app-run-progress-panel
            [items]="restoreService.queue()"
            [isRunning]="restoreService.isRunning()"
            labelPrefix="restore"
            [syncReport]="restoreService.syncReport()"
            [syncReportReason]="restoreService.syncReportReason()"
            [rateLimitPauseSeconds]="restoreService.rateLimitPauseSeconds()"
            (cancelled)="restoreService.cancel()"
            (dismissed)="restoreService.reset()"
            (syncRetryRequested)="restoreService.retrySyncReport()"
          >
            <ng-container run-actions>
              <!-- aria-hidden for the same reason as the duplicate notices above. -->
              @if (resyncNoticeKeyValue(); as noticeKey) {
                <span aria-hidden="true" class="text-xs text-fg-muted">
                  {{ noticeKey | transloco }}
                </span>
              }
            </ng-container>
          </app-run-progress-panel>
        </div>
      }
    }
  `,
})
export class RestoreProgressSection {
  protected readonly restoreService = inject(SevenTvRestoreService);

  /** #149/T5: wording for how many `ADD`s (aliases, since the 2026-09-22 per-alias rule) the
   *  pre-run duplicate check (`already-present-filter.ts`) dropped — shown independently of the
   *  run-progress panel below, because a run where *every* row was already present queues nothing
   *  and would otherwise leave that panel hidden (its own gate is `isRunning() || queue().length >
   *  0`), silently swallowing the one thing the user needs to see in that case. */
  protected readonly skippedDuplicatesKey = computed(() =>
    pluralKey(this.restoreService.skippedDuplicates(), 'restore.skippedDuplicates'),
  );

  /** Wording for how many aliases the same check dropped because another emote now holds the name
   *  (`restoreService.skippedNameTaken`) — same reason to live apart from `skippedDuplicatesKey`. */
  protected readonly skippedNameTakenKey = computed(() =>
    pluralKey(this.restoreService.skippedNameTaken(), 'restore.skippedNameTaken'),
  );

  /** Same key the page's DockOutcomeAnnouncer speaks — see `resyncNoticeKey`. A method, not a
   *  `computed()`: it needs no memoisation of its own (the template only reads it once per render),
   *  and a `computed()` here would just re-wrap the already-memoised `restoreService.resyncTrigger`
   *  signal. */
  protected resyncNoticeKeyValue(): string | null {
    return resyncNoticeKey(this.restoreService.resyncTrigger(), 'restore');
  }

  /** The tracked channel a settled or running run expects to touch, or `null` for an untracked
   *  target — decides which of the two target-line wordings the template shows (see its own
   *  comment). `expectedChannelName` is set for a tracked *active* target (E18), `resyncChannelName`
   *  for a tracked *non*-active one (E12); the two are mutually exclusive by construction
   *  (`restoreStartTarget`, `restore-flow.ts`), so at most one is ever non-null. */
  protected targetChannelName(run: {
    expectedChannelName: string | null;
    resyncChannelName: string | null;
  }): string | null {
    return run.expectedChannelName ?? run.resyncChannelName;
  }
}
