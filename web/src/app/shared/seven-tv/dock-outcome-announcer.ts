import { Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { pluralKey } from '../../core/i18n/plural';
import { ImportRunInfo, SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
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

/** The `import.summary.copiedNotActive` transloco params, or `null` while the notice does not apply
 *  (finding 3, Live-Verifikation K2 2026-09-21) — shared by this announcer and the visible (but
 *  aria-hidden) notice it speaks for (`ImportProgressSection`), same reason as {@link resyncNoticeKey}
 *  above. A copy into a tracked *non*-active set never fires `SevenTvImportService.onRunComplete`'s
 *  resync (there is nothing for `resyncTrigger` to become but 'idle'), so this notice fills the gap
 *  that would otherwise leave the run's actual outcome unstated once it settles. `run.result !==
 *  null` gates it to a *settled* run, mirroring `resyncTrigger`'s own only-set-after-completion
 *  timing — a run still in flight has nothing to report here yet. */
export function copiedNotActiveNotice(
  run: ImportRunInfo | null,
): { channel: string; setName: string } | null {
  if (
    run === null ||
    run.result === null ||
    run.targetChannelName === null ||
    run.targetIsActiveSet
  ) {
    return null;
  }
  return { channel: run.targetChannelName, setName: run.targetSetName };
}

/** Translation key for the dock's "n of them hidden by the filter" line, shared by this announcer
 *  and the visible line it speaks for (`usage-stats-page.html`), same reason as `resyncNoticeKey`:
 *  the spoken and the shown wording cannot drift apart if there is only one place to change. */
export function hiddenByFilterNoticeKey(count: number): string {
  return pluralKey(count, 'usageStats.dock.hiddenByFilter');
}

/** Translation key for announcing the outcome of a bulk-mark gesture ("mark all" in the toolbar, the
 *  per-band one) — the row it names (`usageStats.dock.marked`) is created by the same `@if` that
 *  fills it (docs/UI-Designsprache.md §4.5), which such a gesture can take from unmounted to a
 *  double-digit count in one press with nothing announced. Unlike `hiddenByFilterNoticeKey` above,
 *  the count passed in here is not a continuous mirror of what the row shows — the host
 *  (`UsageStatsPage.dockMarkedCount`) only updates it after `markAll()`/`selectBand()`, never after
 *  an individual click, which already announces itself through its own cell's `aria-pressed`. */
export function markedCountNoticeKey(count: number): string {
  return pluralKey(count, 'usageStats.dock.markedAnnounced');
}

/**
 * The screen-reader voice for what the usage-stats action dock *shows* — the duplicate notices and
 * the resync acknowledgement of a restore and of an import (#134, #149), the "n of them hidden by
 * the filter" line, and a bulk-mark gesture's outcome. The hidden-by-filter line and the duplicate/
 * resync notices stay inside the dock and are `aria-hidden`; this component carries their text
 * instead. The dock's marked-count row is the one exception since the 2026-09-19 Opus review: it is
 * reachable in the accessibility tree itself again (see its own comment in `usage-stats-page.html`),
 * because this component no longer mirrors it continuously — see `markedCount` below.
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
 * just below), then restore (the marking half) before import, and within each the skipped count
 * (for restore followed by its name-taken count), the check-unavailable notice, then the resync
 * acknowledgement. `role="status"` is implicitly
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
 * `hiddenSelectedCount` is not a run outcome but a standing condition: how many marked rows a filter
 * currently hides. It does not self-clear the way a run outcome does (docs/UI-Designsprache.md §4.4,
 * persisting state — §4.5's 4000-ms pattern would be exactly wrong for it). It still belongs in this
 * region rather than in one of its own: it is a line the dock shows, it appears in the same
 * change-detection pass as its own `@if`, and a second live region speaking beside this one is the
 * failure mode §4.5 names. The host page passes 0 whenever the line is not on screen, so spoken and
 * shown stay the same set of statements; the voting-results page, which has no dock, never passes it.
 *
 * `markedCount` looks the same shape but is not a continuous mirror of anything (Opus review,
 * 2026-09-19): mirroring the dock's live marked count here meant an individual mark or unmark — which
 * already announces itself through its own cell's `aria-pressed` — spoke a *second* time, one
 * paragraph per click. So the host only updates it after a bulk-mark gesture (`markAll()`,
 * `selectBand()`), with the selection size the gesture left behind, and passes 0 whenever anything
 * *other* than a bulk gesture has touched the selection since — an individual mark/unmark, a filter-
 * driven prune, a delete — not only once the selection has fully emptied (Codex P2, follow-up
 * 2026-09-19: a plain "did it reach zero" check missed exactly the case where a single deselect
 * leaves the selection non-empty, which then masked a second bulk gesture that happened to land back
 * on the same total). See `UsageStatsPage.dockMarkedCount`'s own comment for the exact gates. It is
 * passed 0 the same way `hiddenSelectedCount` is whenever there is nothing to say, so the `@if` in
 * the template below behaves identically for both; only *when* the host updates the number differs.
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
    @if (restoreService.duplicateNoticePending() && restoreService.skippedNameTaken() > 0) {
      <p>
        {{ restoreNameTakenKey() | transloco: { count: restoreService.skippedNameTaken() } }}
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
      @if (importService.duplicateNoticePending() && importService.replaceSkippedDrift() > 0) {
        <p>
          {{
            importReplaceSkippedDriftKey()
              | transloco: { count: importService.replaceSkippedDrift() }
          }}
        </p>
      }
      @if (importCopiedNotActive(); as notActive) {
        <p>{{ 'import.summary.copiedNotActive' | transloco: notActive }}</p>
      } @else if (importResyncKey(); as key) {
        <p>{{ key | transloco }}</p>
      }
    }
  `,
})
export class DockOutcomeAnnouncer {
  readonly withImport = input(false);
  /** The selection size a bulk-mark gesture ("mark all", per-band "mark all") left behind, or 0
   *  while there is nothing to announce — either because the dock's marked-count row is not on
   *  screen at all, or because a non-bulk change (an individual click, a prune, a clear) has touched
   *  the selection since. NOT the live selection count: an individual click never updates this (see
   *  `dockMarkedCount` on `UsageStatsPage` for exactly when it does), because that click already
   *  announces itself through its own cell's `aria-pressed`. */
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
  protected readonly restoreNameTakenKey = computed(() =>
    pluralKey(this.restoreService.skippedNameTaken(), 'restore.skippedNameTaken'),
  );
  protected readonly importSkippedKey = computed(() =>
    pluralKey(this.importService.skippedDuplicates(), 'import.skippedDuplicates'),
  );
  protected readonly importReplaceSkippedDriftKey = computed(() =>
    pluralKey(this.importService.replaceSkippedDrift(), 'import.summary.replaceSkippedDrift'),
  );
  protected readonly restoreResyncKey = computed(() =>
    resyncNoticeKey(this.restoreService.resyncTrigger(), 'restore'),
  );
  protected readonly importResyncKey = computed(() =>
    resyncNoticeKey(this.importService.resyncTrigger(), 'import'),
  );
  protected readonly importCopiedNotActive = computed(() =>
    copiedNotActiveNotice(this.importService.run()),
  );
}
