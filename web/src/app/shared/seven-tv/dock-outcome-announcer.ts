import { Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import {
  ResyncTriggerState,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';

/** Translation key for a run's resync acknowledgement, or `null` while there is nothing to say.
 *  Shared by this announcer and the two visible notices it speaks for (`MassDeletePanel`,
 *  `ImportProgressSection`), so the spoken and the shown wording cannot drift apart. */
export function resyncNoticeKey(
  state: ResyncTriggerState,
  family: 'import' | 'restore',
): string | null {
  return state === 'idle' ? null : `${family}.resync.${state}`;
}

/**
 * The screen-reader voice for the outcomes the usage-stats action dock *shows* — the duplicate
 * notices and the resync acknowledgement of a restore and of an import (#134, #149). Those visible
 * notices stay inside the dock and are `aria-hidden`; this component carries their text instead.
 *
 * Why it cannot sit in the dock next to them (docs/UI-Designsprache.md §4.5): the dock mounts and
 * unmounts with its own content (`actionDockHasContent`), and a fully refused run is one of the
 * things that mounts it. A `role="status"` region inside the dock is then created in the same
 * change-detection pass that fills it, and most screen reader/browser pairings announce nothing for
 * a region that appears together with its text. So the host page mounts this permanently, outside
 * every gate of the dock (`!isCoarse()` included) — its region always exists, only the text inside
 * comes and goes, mirroring the service signals the visible notices are gated on.
 *
 * Several outcomes at once: one paragraph each, in the dock's own reading order — restore (the
 * marking half) before import, and within each the skipped count, the check-unavailable notice,
 * then the resync acknowledgement. Each paragraph enters and leaves on its own, so a new one is
 * announced once without repeating those already standing (the region is not `aria-atomic`).
 *
 * `withImport`: the import section only exists on the usage-stats page. The voting-results page
 * mounts the mass-delete panel alone and must not speak for an import run it does not show.
 */
@Component({
  selector: 'app-dock-outcome-announcer',
  imports: [TranslocoPipe],
  host: { role: 'status', class: 'sr-only' },
  template: `
    @if (restoreService.duplicateNoticePending() && restoreService.skippedDuplicates() > 0) {
      <p>
        {{ restoreSkippedKey() | transloco: { count: restoreService.skippedDuplicates() } }}
      </p>
    }
    @if (restoreService.duplicateNoticePending() && !restoreService.duplicateCheckAvailable()) {
      <p>{{ 'restore.duplicateCheckUnavailable' | transloco }}</p>
    }
    @if (restoreResyncKey(); as key) {
      <p>{{ key | transloco }}</p>
    }
    @if (withImport()) {
      @if (importService.duplicateNoticePending() && importService.skippedDuplicates() > 0) {
        <p>
          {{ importSkippedKey() | transloco: { count: importService.skippedDuplicates() } }}
        </p>
      }
      @if (importService.duplicateNoticePending() && !importService.duplicateCheckAvailable()) {
        <p>{{ 'import.duplicateCheckUnavailable' | transloco }}</p>
      }
      @if (importResyncKey(); as key) {
        <p>{{ key | transloco }}</p>
      }
    }
  `,
})
export class DockOutcomeAnnouncer {
  readonly withImport = input(false);

  protected readonly restoreService = inject(SevenTvRestoreService);
  protected readonly importService = inject(SevenTvImportService);

  protected readonly restoreSkippedKey = computed(() =>
    pluralKey(this.restoreService.skippedDuplicates(), 'restore.skippedDuplicates'),
  );
  protected readonly importSkippedKey = computed(() =>
    pluralKey(this.importService.skippedDuplicates(), 'import.skippedDuplicates'),
  );
  protected readonly restoreResyncKey = computed(() =>
    resyncNoticeKey(this.restoreService.resyncTrigger(), 'restore'),
  );
  protected readonly importResyncKey = computed(() =>
    resyncNoticeKey(this.importService.resyncTrigger(), 'import'),
  );
}
