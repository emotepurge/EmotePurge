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
import { startImportFlow } from './import-flow';
import { startRestoreFlow } from './restore-flow';

/**
 * The header button that opens the import path — **all of it** (#91, #147). It freezes
 * `channelName`/`setId` at the moment of the click, opens `ImportSourceDialog`, and hands whatever
 * comes back to the chain that fits: `startRestoreFlow` for a purge-run protocol,
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
  /** The channel's *current* active set — a purge-run protocol is validated against it. */
  readonly setId = input.required<string>();
  /** See `importScopeIsCurrent` on the page; defaults to true so a caller that has no such window
   *  to guard against (there is currently only one, the usage-stats page) need not pass it. */
  readonly importScopeCurrent = input(true);

  private readonly arbiter = inject(SevenTvRunArbiter);
  private readonly dialog = inject(Dialog);
  private readonly emoteAdminService = inject(EmoteAdminService);
  /** Only threaded through to `ImportFlowDeps` — `loadImportTarget`'s live-list collaborator for a
   *  non-active/untracked target, not exercised from any of this component's three chains, which
   *  all target the current channel's active set (spec F5). */
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
    // while a dialog further down either chain is still open cannot retarget what gets read,
    // validated or restored/imported (plan §1.5).
    const channelName = this.channelName();
    const setId = this.setId();

    openImportSourceDialog(this.dialog, { channelName, setId }).closed.subscribe((result) => {
      if (!result) {
        return;
      }
      if (result.kind === 'restore') {
        startRestoreFlow(
          {
            dialog: this.dialog,
            emoteAdminService: this.emoteAdminService,
            httpClient: this.httpClient,
            tokenService: this.tokenService,
            restoreService: this.restoreService,
            arbiter: this.arbiter,
          },
          channelName,
          setId,
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
      if (result.kind === 'foreign') {
        // The target is this page's channel, exactly as it is for the file path — no target picker
        // in between any more (#147).
        startForeignChannelImportFlow(importDeps, result.picked, channelName);
        return;
      }
      if (result.kind === 'leaderboard') {
        // Same target rule, and even less to ask for: a leaderboard row belongs to no channel at
        // all. Foreign is the source, never the target.
        startLeaderboardImportFlow(importDeps, result.picked, channelName);
        return;
      }
      startImportFlow(importDeps, result.source, { kind: 'activeSet', channelName });
    });
  }
}
