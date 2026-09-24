import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { Button } from '../ui/button';
import { startForeignChannelImportFlow, startLeaderboardImportFlow } from './foreign-import-flow';
import { importTriggerDisabled } from './import-trigger-gate';
import { openImportSourceDialog } from './import-source-dialog';
import { ImportFlowTarget, startImportFlow } from './import-flow';
import { startRestoreFlow } from './restore-flow';

/**
 * The channel's active set as far as this trigger may assume it: an *omitted* input (`undefined` —
 * a caller with no "selected vs. active" distinction to offer, i.e. every caller that predates
 * T4.5) folds onto `setId` itself, which keeps that caller byte-identical to before. A *known
 * unknown* (`null` — the host knows the distinction but has no active id: the set status failed,
 * e.g. 429, or the channel has no active set) stays `null`: then nothing may be assumed to be the
 * active set.
 */
function resolveActiveSetId(setId: string, activeSetId: string | null | undefined): string | null {
  return activeSetId === undefined ? setId : activeSetId;
}

/**
 * The file/foreign-channel/leaderboard doors' target (spec 8.6, T4.5) — a `'chosen'`
 * `ImportFlowTarget` built from what this trigger's own inputs already carry, without ever opening
 * a picker. With `activeSetId === setId` (or folded onto it, see `resolveActiveSetId`),
 * `toTargetSelection` (`import-flow.ts`) takes the exact same `'trackedActive'` fast path it always
 * took. With a different or an unknown (`null`) active set it takes the explicit `'trackedSet'`
 * path, which reads the selected set live by its id — the safe choice when the active id is unknown:
 * the write still lands exactly in the set on screen, and no step assumes it is the channel's active
 * one (no active-set request, no post-run resync of the channel). Disabling the doors instead would
 * have been just as safe but would take the import away for as long as a status request keeps
 * failing, for no gain in correctness.
 *
 * `ownerDisplayName`/`twitchLogin` are placeholders that `toTargetSelection` never reads for a
 * tracked choice with a `channelName` (every choice this builds has one) — see that function's own
 * doc for why. `setName` falls back to the id, the same convention `targetSetLabel`
 * (`import-confirm-dialog.ts`) already uses for every other unnamed set.
 */
function toImportTarget(
  channelName: string,
  setId: string,
  activeSetId: string | null | undefined,
  setName: string | null,
): ImportFlowTarget {
  return {
    kind: 'chosen',
    choice: {
      emoteSetId: setId,
      channelName,
      ownerDisplayName: channelName,
      setName: setName ?? setId,
      isTracked: true,
      twitchLogin: channelName,
      activeEmoteSetId: resolveActiveSetId(setId, activeSetId),
    },
  };
}

/**
 * The header button that opens the import path — **all of it** (#91, #147). It freezes
 * `channelName`/`setId` at the moment of the click, opens `ImportSourceDialog`, and hands whatever
 * comes back to the chain that fits: `startRestoreFlow` for a restore file (purge-run protocol or
 * transfer-run file),
 * `startImportFlow` for an emote list or usage export read from a file, and
 * `startForeignChannelImportFlow` for emotes picked out of another channel's 7TV set.
 *
 * There used to be a second header button for the foreign-channel source. It is gone: a source with
 * a front door of its own contradicted the spec's E1, and the choice now lives in the dialog's first
 * step where a third source is a third row rather than a third button (#147).
 *
 * No chain runs from inside the still-open dialog: it always closes first (its own contract, see
 * `ImportSourceDialogResult`), so the app's one-dialog-at-a-time rule (`shared/ui/dialog.ts`) holds
 * and each chain keeps its own dialog ordering — Restore asks for the 7TV token before the
 * confirmation, Import only after it (`startImportFlow`'s doc explains why; this trigger must not
 * prompt for a token itself on top of either).
 *
 * Injects its own services (#70/#91) — the page it sits in gets no new method of its own, which
 * keeps the page's own coverage surface small (see plan 2.4).
 *
 * `importScopeCurrent` is an input rather than something computed here from page state, so the
 * lock this button carries stays a pure function of two booleans (`importTriggerDisabled`,
 * testable without a TestBed) — the page computes the boolean itself, the same way it already does
 * for the neighbouring "Übertragen" button (`importScopeIsCurrent`). `atlasOrder().length === 0`
 * and `!isCoarse()` deliberately do NOT appear here: both are already enforced by the `@if` block
 * this trigger is placed inside on the page, alongside "Übertragen" (plan §1.2 point 3).
 *
 * **The three copy doors target `setId` itself (spec 8.6, T4.5)** — the page's *selected* set,
 * active or not (`toImportTarget` above). **A restore file targets whatever set it names** (spec
 * #253, E1): `FileImportStep` reads the set from the file, clears it through the shared pre-check
 * (`resolveEditableSet`, E19) and hands back a `ResolvedRestoreTarget` that this trigger passes to
 * `startRestoreFlow` unchanged — it neither builds nor adjusts a restore target itself, and a
 * blocked check never reaches it (the step keeps the dialog open with its own banner). The page's
 * frozen `setId` only goes along as the step's `hostSelectedSetId`, for the confirmation's "not the
 * set on screen" hint (E21). Restore books its un-archive through the set-centric
 * `SevenTvEmoteSetService.reportRestoredInSet(setId, …)` call (`SevenTvRestoreService`, spec 6.4).
 */
@Component({
  selector: 'app-import-trigger',
  imports: [Button, TranslocoPipe],
  template: `
    <button
      type="button"
      appButton="neutral"
      class="disabled:cursor-not-allowed"
      [disabled]="disabled()"
      (click)="openDialog()"
    >
      {{ 'restore.import.trigger' | transloco }}
    </button>
  `,
})
export class ImportTrigger {
  readonly channelName = input.required<string>();
  /** The set this trigger's three copy doors target — the page's *selected* set (spec #200, T4.5),
   *  active or not. A restore file does not target it (it names its own set); it only travels to
   *  the file step as `hostSelectedSetId`. Named `setId`, not `selectedSetId`: the only place
   *  "selected vs. active" matters is the comparison against {@link activeSetId} below. */
  readonly setId = input.required<string>();
  /** The channel's actual active set; `null` when the host knows it has none to offer (unknown —
   *  status failed — or no active set at all); omitted (`undefined`) by a caller with no such
   *  distinction (every caller that predates T4.5, and any test that never sets it), which folds
   *  back onto `setId` (`resolveActiveSetId`) and keeps that caller byte-identical to before. Only
   *  the copy doors read it; a restore file's target says for itself whether it is an active set. */
  readonly activeSetId = input<string | null | undefined>(undefined);
  /** The selected set's display name, for the import confirm dialog's title when it is not the
   *  active one (spec 8.6) — `null` falls back to the id, same as every other unnamed set there. */
  readonly setName = input<string | null>(null);
  /** See `importScopeIsCurrent` on the page; defaults to true so a caller that has no such window
   *  to guard against (there is currently only one, the usage-stats page) need not pass it. */
  readonly importScopeCurrent = input(true);

  private readonly arbiter = inject(SevenTvRunArbiter);
  private readonly dialog = inject(Dialog);
  private readonly emoteAdminService = inject(EmoteAdminService);
  /** `loadImportTarget`'s live-list collaborator for a non-active/untracked target (spec F5) —
   *  reached from here whenever `setId` names a set other than `activeSetId` (T4.5) — and the
   *  restore flow's slot preview for a target that is not a tracked channel's active set. */
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  /** Only for `filterAlreadyPresent`'s direct read against 7TV (#149 P1 fix) — every other read
   *  reached from here goes through `emoteAdminService`. */
  private readonly httpClient = inject(HttpClient);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly restoreService = inject(SevenTvRestoreService);
  private readonly importService = inject(SevenTvImportService);

  protected readonly disabled = computed(() =>
    importTriggerDisabled({
      hasActiveRun: this.arbiter.activeRun() !== null,
      importScopeCurrent: this.importScopeCurrent(),
    }),
  );

  protected openDialog(): void {
    // Frozen here, at the click — never read again from the live inputs below, so a channel switch
    // (or a set switch, T4.5) while a dialog further down either chain is still open cannot
    // retarget what gets read, validated or restored/imported (plan §1.5).
    const channelName = this.channelName();
    const setId = this.setId();
    const activeSetId = this.activeSetId();
    const setName = this.setName();

    openImportSourceDialog(this.dialog, {
      channelName,
      setId,
    }).closed.subscribe((result) => {
      if (!result) {
        return;
      }
      if (result.kind === 'restore') {
        // The target is the file's, already resolved and cleared by the file step (spec 6.1,
        // 4.2) — passed on as it came, host fields included.
        startRestoreFlow(
          {
            dialog: this.dialog,
            emoteAdminService: this.emoteAdminService,
            emoteSetService: this.emoteSetService,
            httpClient: this.httpClient,
            tokenService: this.tokenService,
            restoreService: this.restoreService,
            arbiter: this.arbiter,
          },
          result.target,
          result.rows,
        );
        return;
      }
      const importDeps = {
        dialog: this.dialog,
        emoteAdminService: this.emoteAdminService,
        emoteSetService: this.emoteSetService,
        httpClient: this.httpClient,
        tokenService: this.tokenService,
        importService: this.importService,
        arbiter: this.arbiter,
      };
      const target = toImportTarget(channelName, setId, activeSetId, setName);
      if (result.kind === 'foreign') {
        startForeignChannelImportFlow(importDeps, result.picked, target);
        return;
      }
      if (result.kind === 'leaderboard') {
        // Same target rule, and even less to ask for: a leaderboard row belongs to no channel at
        // all. Foreign is the source, never the target.
        startLeaderboardImportFlow(importDeps, result.picked, target);
        return;
      }
      startImportFlow(importDeps, result.source, target);
    });
  }
}
