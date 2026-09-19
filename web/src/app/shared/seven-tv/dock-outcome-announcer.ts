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

/** Translation key for the dock's "n of them hidden by the filter" line, shared by this announcer
 *  and the visible line it speaks for (`usage-stats-page.html`), same reason as `resyncNoticeKey`:
 *  the spoken and the shown wording cannot drift apart if there is only one place to change. */
export function hiddenByFilterNoticeKey(count: number): string {
  return pluralKey(count, 'usageStats.dock.hiddenByFilter');
}

/** Translation key for announcing the dock's marked-count row (`usageStats.dock.marked`) — a
 *  sibling of `hiddenByFilterNoticeKey` above, spoken rather than shown twice for the same reason:
 *  the row is created by the same `@if` that fills it (docs/UI-Designsprache.md §4.5), which a bulk
 *  mark ("mark all", per-band "mark all") can take from unmounted to a double-digit count in one
 *  gesture with nothing announced. */
export function markedCountNoticeKey(count: number): string {
  return pluralKey(count, 'usageStats.dock.markedAnnounced');
}

/**
 * The screen-reader voice for what the usage-stats action dock *shows* — the duplicate notices and
 * the resync acknowledgement of a restore and of an import (#134, #149), plus the standing marked-
 * count row and the "n of them hidden by the filter" line above them. Those visible notices stay
 * inside the dock and are `aria-hidden`; this component carries their text instead.
 *
 * Why it cannot sit in the dock next to them (docs/UI-Designsprache.md §4.5): the dock mounts and
 * unmounts with its own content (`actionDockHasContent`), and a fully refused run is one of the
 * things that mounts it. A `role="status"` region inside the dock is then created in the same
 * change-detection pass that fills it, and most screen reader/browser pairings announce nothing for
 * a region that appears together with its text. So the host page mounts this permanently, outside
 * every gate of the dock (`!isCoarse()` included) — its region always exists, only the text inside
 * comes and goes, mirroring the service signals the visible notices are gated on.
 *
 * Several messages at once: one paragraph each, in the dock's own reading order — the marked-count
 * row first (it sits at the very top of the marking half), then the hidden-by-filter line (it sits
 * just below), then restore (the marking half) before import, and within each the skipped count,
 * the check-unavailable notice, then the resync acknowledgement. `role="status"` is implicitly
 * `aria-atomic="true"` (WAI-ARIA 1.2, §status), and Blink/WebKit apply that default — so without an
 * explicit override, a new or changed paragraph would make the whole region, standing ones
 * included, be read again. This multi-message region therefore sets `aria-atomic="false"` on its
 * host, so a new paragraph is announced alone; each paragraph's sentence stays a single
 * interpolated text node so an in-place pending→succeeded change is still read as the full new
 * sentence, not a fragment.
 *
 * `withImport`: the import section only exists on the usage-stats page. The voting-results page
 * mounts the mass-delete panel alone and must not speak for an import run it does not show.
 *
 * `markedCount`/`hiddenSelectedCount` are the two messages here that are **not** run outcomes but
 * standing conditions: how many rows are marked, and how many of those a filter currently hides.
 * Neither self-clears the way a run outcome does (docs/UI-Designsprache.md §4.4, persisting state —
 * §4.5's 4000-ms pattern would be exactly wrong for either). Both still belong in this region rather
 * than in one of their own: each is a line the dock shows, each appears in the same change-detection
 * pass as its own `@if`, and a second live region speaking beside this one is the failure mode §4.5
 * names. The host page passes 0 whenever the corresponding line is not on screen, so spoken and
 * shown stay the same set of statements; the voting-results page, which has no dock, never passes
 * either at all.
 */
@Component({
  selector: 'app-dock-outcome-announcer',
  imports: [TranslocoPipe],
  host: { role: 'status', class: 'sr-only', 'aria-atomic': 'false' },
  template: `
    @if (markedCount(); as marked) {
      <p>{{ markedKey() | transloco: { count: marked } }}</p>
    }
    @if (hiddenSelectedCount(); as hidden) {
      <p>{{ hiddenByFilterKey() | transloco: { count: hidden } }}</p>
    }
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
  /** How many rows are currently marked, or 0 when the dock's marked-count row is not on screen at
   *  all — the host mirrors its own template gates into this number (see `dockMarkedCount` on
   *  `UsageStatsPage`), same convention as `hiddenSelectedCount` below. */
  readonly markedCount = input(0);
  /** How many marked rows the host page's filter currently hides, or 0 when the dock does not show
   *  that line at all — the host mirrors its own template gates into this number. */
  readonly hiddenSelectedCount = input(0);

  protected readonly restoreService = inject(SevenTvRestoreService);
  protected readonly importService = inject(SevenTvImportService);

  protected readonly markedKey = computed(() => markedCountNoticeKey(this.markedCount()));
  protected readonly hiddenByFilterKey = computed(() =>
    hiddenByFilterNoticeKey(this.hiddenSelectedCount()),
  );
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
