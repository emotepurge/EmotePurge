import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteAdminService, EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import {
  DeleteQueueEmote,
  SevenTvDeleteService,
} from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { CSV_MIME } from '../export/csv';
import { ExportDialogData, FORMAT_EXPORT_OPTIONS, openExportDialog } from '../export/export-dialog';
import { JSON_MIME } from '../export/export-envelope';
import { downloadFile } from '../export/file-download';
import {
  buildPurgeRunProtocol,
  purgeRunCsv,
  purgeRunFilename,
  purgeRunJson,
} from '../export/purge-run-export';
import { Button } from '../ui/button';
import { filterAlreadyPresent } from './already-present-filter';
import { DeleteConfirmDialogData, openDeleteConfirmDialog } from './delete-confirm-dialog';
import { resyncNoticeKey } from './dock-outcome-announcer';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { RunProgressPanel } from './run-progress-panel';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

export interface DeletableEmote {
  emoteId: string;
  sevenTvEmoteId: string;
  name: string;
  /** Whether the host page's current filter hides this emote right now (`!selection.isVisible`).
   *  Required, not optional (Konzept "Auswahl überlebt Suche und Filter" 2.1, Codex befund 3b):
   *  this panel has no filter/visibility knowledge of its own and must never silently assume
   *  "everything is visible" — the delete-confirm dialog's hidden-by-filter block depends on every
   *  host page actually supplying it. */
  hidden: boolean;
}

/**
 * One reusable delete engine, rendered as two separate instances (Usage-Stats page and
 * Voting-Results page) — each instance owns its host page's local selection, but both talk to
 * the same singleton SevenTvDeleteService/SevenTvTokenService underneath.
 *
 * Since A6 it also owns the run's paper trail: the post-run summary offers the protocol as a
 * download (the file is the restore list), and "restore" re-adds the deleted emotes over the
 * restore service's own engine run — both browser-side, the 7TV token never leaves it.
 */
@Component({
  selector: 'app-mass-delete-panel',
  imports: [Button, RunProgressPanel, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-3">
      <div class="flex flex-wrap items-center gap-2">
        <!-- Host-page peers acting on the same grid selection (e.g. the usage page's copy
             shortcut and create-vote-session button) share this row, constructive before
             destructive (design doc §8.7) — stacked rows hid that both consume one selection and
             cost a row of vertical space. Wrapped together with its trailing gap behind
             leadingActionsPresent(): an @if block projected into the slot still leaves a comment
             node, so the panel cannot tell an empty slot from a populated one by inspecting the
             projection itself (see the input's doc comment) — without the gate, the voting-detail
             page's always-empty slot would leave this gap floating in front of a lone delete
             button. -->
        @if (leadingActionsPresent()) {
          <div class="mr-2 flex flex-wrap items-center gap-2">
            <ng-content select="[selection-actions]" />
          </div>
        }
        <button
          type="button"
          appButton="danger-solid"
          buttonSize="lg"
          class="disabled:cursor-not-allowed"
          [disabled]="
            selectedEmotes().length === 0 ||
            deleteService.isRunning() ||
            arbiter.activeRun() !== null
          "
          (click)="openConfirm()"
        >
          {{ 'massDelete.deleteButton' | transloco: { count: selectedEmotes().length } }}
        </button>
        <!-- Without this, the only way out of a large selection was deselecting card by card
             (user finding, 2026-07-30) — the panel owns the "n selected" wording, so the way to
             zero belongs next to it; the actual clear stays with the host page's selection. -->
        @if (selectedEmotes().length > 0 && !deleteService.isRunning()) {
          <button
            type="button"
            appButton="neutral"
            buttonSize="lg"
            (click)="selectionCleared.emit()"
          >
            {{ 'massDelete.clearSelection' | transloco }}
          </button>
        }
      </div>

      @if (deleteService.isRunning() || deleteService.queue().length > 0) {
        <app-run-progress-panel
          [items]="deleteService.queue()"
          [isRunning]="deleteService.isRunning()"
          [syncReport]="deleteService.syncReport()"
          [rateLimitPauseSeconds]="deleteService.rateLimitPauseSeconds()"
          (cancelled)="deleteService.cancel()"
          (dismissed)="deleteService.reset()"
          (syncRetryRequested)="deleteService.retrySyncReport()"
        >
          <ng-container run-actions>
            @if (deleteService.lastRun(); as run) {
              <button type="button" appButton="neutral" (click)="openProtocolExport()">
                {{ 'massDelete.summary.downloadProtocol' | transloco }}
              </button>
              @if (run.result.doneIds.length > 0 && arbiter.activeRun() === null) {
                <!-- The two-tier *shape* of the destructive convention, not its colour: outline
                     triggers, the dialog's primary-solid executes — restore is constructive. -->
                <button type="button" appButton="outline" (click)="openRestoreConfirm()">
                  {{ 'restore.button' | transloco }}
                </button>
              }
              @if (!protocolSaved()) {
                <!-- reset() wipes the run — the downloaded file is the only durable artifact. -->
                <span class="text-xs text-fg-muted">
                  {{ 'massDelete.summary.protocolNotSaved' | transloco }}
                </span>
              }
            }
          </ng-container>
        </app-run-progress-panel>
      }

      <!-- #149 P2 (independent review): gated on duplicateNoticePending, not just
           skippedDuplicates() > 0 — a transient notice (design doc §4.5), not a persistent one, so
           it never sits attached to a *later*, unrelated run's details with nothing to clear it.
           See that signal's doc for why it also has to be what keeps the dock (and this panel)
           mounted for a fully-refused (all-duplicates) restore, which leaves no run/queue behind of
           its own — including the file-based restore reached via ImportTrigger, which has nothing
           marked in this channel's grid to keep the dock open otherwise.

           Every notice in this panel is aria-hidden: its announcement comes from the host page's
           permanently mounted DockOutcomeAnnouncer, not from here. On the usage-stats page this
           panel lives in the dock, which can mount in the same pass that sets the notice, and a
           status region created together with its text announces nothing
           (docs/UI-Designsprache.md §4.5). -->
      @if (restoreService.duplicateNoticePending() && restoreService.skippedDuplicates() > 0) {
        <p aria-hidden="true" class="text-sm text-fg-secondary">
          {{
            restoreSkippedDuplicatesKey() | transloco: { count: restoreService.skippedDuplicates() }
          }}
        </p>
      }
      <!-- The pre-run duplicate check's fetch failed (already-present-filter.ts) — every row still
           went through, so a duplicate may have slipped in undetected. A quiet notice, not an
           alarm: the run is still expected to succeed, this only says the guard could not run. -->
      @if (restoreService.duplicateNoticePending() && !restoreService.duplicateCheckAvailable()) {
        <p aria-hidden="true" class="text-sm text-fg-secondary">
          {{ 'restore.duplicateCheckUnavailable' | transloco }}
        </p>
      }
      @if (restoreService.isRunning() || restoreService.queue().length > 0) {
        <app-run-progress-panel
          [items]="restoreService.queue()"
          [isRunning]="restoreService.isRunning()"
          labelPrefix="restore"
          [syncReport]="restoreService.syncReport()"
          [rateLimitPauseSeconds]="restoreService.rateLimitPauseSeconds()"
          (cancelled)="restoreService.cancel()"
          (dismissed)="restoreService.reset()"
          (syncRetryRequested)="restoreService.retrySyncReport()"
        >
          <ng-container run-actions>
            <!-- aria-hidden for the same reason as the duplicate notices above. -->
            @if (resyncNoticeKey(); as noticeKey) {
              <span aria-hidden="true" class="text-xs text-fg-muted">
                {{ noticeKey | transloco }}
              </span>
            }
          </ng-container>
        </app-run-progress-panel>
      }
    </div>
  `,
})
export class MassDeletePanel {
  readonly setId = input.required<string>();
  readonly channelName = input.required<string>();
  readonly selectedEmotes = input.required<DeletableEmote[]>();

  /**
   * Whether the `[selection-actions]` slot actually has something projected into it — the panel
   * cannot detect that reliably on its own: a projected `@if` block leaves a comment node in the
   * slot regardless of whether its condition held, so a truthy-content check here would see
   * "populated" even for an always-false host condition. The host page therefore feeds this from
   * the very same conditions that gate its own projected buttons.
   *
   * Defaults to `false` so the voting-detail page, which mounts this panel with nothing projected
   * (`vote-session-detail-page.html`), keeps its current layout — a lone delete button with no
   * leading gap — without having to pass anything.
   */
  readonly leadingActionsPresent = input<boolean>(false);

  /** Ids the host page may drop from its list without a refetch — emitted only once the backend has
   *  confirmed it archived them. */
  readonly deleted = output<string[]>();

  /** The run finished on 7TV, but the backend does not (fully) know about it. The host page must
   *  reload rather than filter locally, so it never shows a state the server does not share. */
  readonly reloadRequested = output<void>();

  /** The user wants the whole selection gone — the host page owns the ListSelection, so clearing
   *  is its job, not this panel's. */
  readonly selectionCleared = output<void>();

  protected readonly tokenService = inject(SevenTvTokenService);
  protected readonly deleteService = inject(SevenTvDeleteService);
  protected readonly restoreService = inject(SevenTvRestoreService);
  /** Read here only for the four start-site checks below — the template needs it too, hence
   *  `protected` rather than `private` (#70, Task 4; see docs/DECISIONS.md). */
  protected readonly arbiter = inject(SevenTvRunArbiter);
  private readonly emoteAdminService = inject(EmoteAdminService);
  /** Only for `filterAlreadyPresent`'s direct read against 7TV (#149 P1 fix) — every other read in
   *  this component goes through `emoteAdminService`. */
  private readonly httpClient = inject(HttpClient);
  private readonly dialog = inject(Dialog);

  private readonly setWarning = signal<EmoteSetWarning | null>(null);
  private readonly warningLoading = signal(false);
  // Split by `hidden` for the delete-confirm dialog (Konzept "Auswahl überlebt Suche und Filter"
  // 2.1): the visible names keep today's capped preview, the hidden ones get their own, uncapped
  // block, because a filtered-out delete target must stay identifiable by name right up to the
  // irreversible action rather than collapsing into "n weitere".
  private readonly visibleSelectedEmoteNames = computed(() =>
    this.selectedEmotes()
      .filter((emote) => !emote.hidden)
      .map((emote) => emote.name),
  );
  private readonly hiddenSelectedEmoteNames = computed(() =>
    this.selectedEmotes()
      .filter((emote) => emote.hidden)
      .map((emote) => emote.name),
  );

  /** Whether the current run's protocol was downloaded at least once — drives the reminder next
   *  to Close, since reset() leaves the file as the only artifact. */
  protected readonly protocolSaved = signal(false);

  /** Live slot view for the restore-confirm dialog, loaded when that dialog opens. */
  private readonly restoreSlots = signal<{ occupied: number; capacity: number } | null>(null);

  /** Same key the host page's DockOutcomeAnnouncer speaks — see `resyncNoticeKey`. */
  protected readonly resyncNoticeKey = computed(() =>
    resyncNoticeKey(this.restoreService.resyncTrigger(), 'restore'),
  );

  /** #149/T5: wording for how many rows the pre-run duplicate check (`already-present-filter.ts`)
   *  dropped — shown independently of the run-progress panel below, because a run where *every*
   *  row was already present queues nothing and would otherwise leave that panel hidden (its own
   *  gate is `isRunning() || queue().length > 0`), silently swallowing the one thing the user needs
   *  to see in that case. */
  protected readonly restoreSkippedDuplicatesKey = computed(() =>
    pluralKey(this.restoreService.skippedDuplicates(), 'restore.skippedDuplicates'),
  );

  constructor() {
    // The queue settling is not on its own a reason to tell the host page anything: the backend only
    // learns about the deletion through the closing sync-deleted call, and that call can fail (rate
    // limit, session expired mid-run). Emitting on the isRunning edge alone therefore showed a
    // cleaned-up list while the database still held every emote. So: wait for a terminal sync
    // report, then either allow the optimistic drop or ask for a real reload.
    let notifiedForThisRun = false;
    effect(() => {
      const running = this.deleteService.isRunning();
      const report = this.deleteService.syncReport();

      if (running) {
        notifiedForThisRun = false;
        this.protocolSaved.set(false);
        return;
      }

      // 'idle' also covers a run in which nothing succeeded — there is nothing to report either way.
      if (notifiedForThisRun || report === 'idle' || report === 'pending') {
        return;
      }

      notifiedForThisRun = true;
      if (report !== 'succeeded') {
        this.reloadRequested.emit();
        return;
      }

      // RunResult.doneIds, not a re-derivation from the queue — it already excludes rows without
      // an emoteId (none exist on a delete run), so no `?? ''` is needed to satisfy the string[]
      // output.
      const doneIds = this.deleteService.lastRun()?.result.doneIds ?? [];
      if (doneIds.length > 0) {
        this.deleted.emit(doneIds);
      }
    });

    // A finished restore changes the emote inventory back — the host page must refetch rather
    // than trust its current list, same reasoning as the delete's reload path.
    let restoreNotified = false;
    effect(() => {
      if (this.restoreService.isRunning()) {
        restoreNotified = false;
        return;
      }
      if (restoreNotified || this.restoreService.queue().length === 0) {
        return;
      }
      restoreNotified = true;
      this.reloadRequested.emit();
    });
  }

  protected openConfirm(): void {
    // No stored 7TV token yet: ask for it first. The prompt closes itself with `true` the moment
    // the token is saved, which chains straight into the confirm dialog — the flow the old
    // hand-built overlay produced via its reactive template switch.
    if (!this.tokenService.hasToken()) {
      openSevenTvTokenPromptDialog(this.dialog).closed.subscribe((saved) => {
        if (saved) {
          this.openConfirmDialog();
        }
      });
      return;
    }

    this.openConfirmDialog();
  }

  /** Offers the finished run's protocol in both formats — the JSON is the restore list. */
  protected openProtocolExport(): void {
    const run = this.deleteService.lastRun();
    if (!run) {
      return;
    }
    // The engine's RunResult is generic over rows that may lack an internal emoteId (an import
    // run has none), but a delete run is built from DeleteQueueEmote, which requires it — so this
    // narrowing is where that guarantee is actually known, rather than inside the shared export
    // helper where a dropped row would become a silently short protocol. Filtered once and reused,
    // so the envelope's counts and rows cannot drift apart.
    const items = run.result.items.filter(
      (item): item is RunQueueItem & { emoteId: string } => item.emoteId !== undefined,
    );
    const protocol = buildPurgeRunProtocol({
      channelName: run.channelName,
      emoteSetId: run.setId,
      startedAt: run.result.startedAt,
      finishedAt: run.result.finishedAt,
      items,
    });
    const data: ExportDialogData = {
      rowCount: protocol.rows.length,
      filtered: false,
      // The protocol is always the whole run — a scope choice would make no sense here.
      selectionCount: null,
      noticeKeys: [],
      optionsLegendKey: 'export.formatLabel',
      options: FORMAT_EXPORT_OPTIONS,
    };
    openExportDialog(this.dialog, data).closed.subscribe((choice) => {
      if (choice?.optionId === 'csv') {
        downloadFile(
          purgeRunFilename(run.channelName, protocol.meta.finishedAt, 'csv'),
          purgeRunCsv(protocol),
          CSV_MIME,
        );
      } else if (choice?.optionId === 'json') {
        downloadFile(
          purgeRunFilename(run.channelName, protocol.meta.finishedAt, 'json'),
          purgeRunJson(protocol),
          JSON_MIME,
        );
      }
      if (choice) {
        this.protocolSaved.set(true);
      }
    });
  }

  protected openRestoreConfirm(): void {
    const run = this.deleteService.lastRun();
    if (!run || this.arbiter.activeRun() !== null) {
      return;
    }
    // A delete run always sets emoteId on every row; the guard below narrows the type rather than
    // papering over a hole that cannot occur here (see R3 in docs/DECISIONS.md).
    const doneItems = run.result.items.filter(
      (item): item is RunQueueItem & { emoteId: string } =>
        item.status === 'done' && item.emoteId !== undefined,
    );
    if (doneItems.length === 0) {
      return;
    }

    if (!this.tokenService.hasToken()) {
      openSevenTvTokenPromptDialog(this.dialog).closed.subscribe((saved) => {
        if (saved) {
          this.openRestoreConfirmDialog(doneItems);
        }
      });
      return;
    }
    this.openRestoreConfirmDialog(doneItems);
  }

  private openRestoreConfirmDialog(
    doneItems: { emoteId: string; sevenTvEmoteId: string; name: string }[],
  ): void {
    // Live slot view, so the projection line pops in once the check answers (the dialog is
    // already open by then) — same pattern as the delete confirm's shared-set warning.
    this.restoreSlots.set(null);
    this.emoteAdminService.getSetStatus(this.channelName()).subscribe({
      next: (status) =>
        this.restoreSlots.set(
          status.capacity === null
            ? null
            : { occupied: status.occupiedSlots, capacity: status.capacity },
        ),
      error: () => this.restoreSlots.set(null),
    });

    const data: RestoreConfirmDialogData = {
      names: doneItems.map((item) => item.name),
      slots: this.restoreSlots.asReadonly(),
    };
    openRestoreConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      const emotes: DeleteQueueEmote[] = doneItems.map((item) => ({
        emoteId: item.emoteId,
        sevenTvEmoteId: item.sevenTvEmoteId,
        name: item.name,
      }));
      // #149/T5: a restore never had any duplicate protection at all — filter it fresh, right here,
      // against the target set's current contents, read from 7TV itself rather than our database
      // (see `filterAlreadyPresent`'s doc — asking our own mirror is exactly wrong for restore,
      // which runs *because* something already went wrong and our mirror may still be stale) for
      // why this sits at confirm-time rather than dialog-open-time and for the residual race it
      // does not close.
      filterAlreadyPresent(this.httpClient, this.setId(), emotes).subscribe(
        ({ rows: toRestore, skipped, available }) => {
          // #149 P2 review fix: openRestoreConfirm()'s own arbiter check ran before this dialog
          // even opened — well outside the mutual-exclusion contract (design doc §4.3) it exists
          // to enforce, since a delete or import can start while the confirm dialog is open and
          // this fetch is in flight. Re-checked here, right before the only remaining call that
          // actually starts anything; silent on a block, same reasoning as elsewhere in this
          // file — the run that got there first is already visible in the dock.
          if (this.arbiter.activeRun() !== null) {
            return;
          }
          this.restoreService.startRestore(
            this.setId(),
            this.channelName(),
            toRestore,
            skipped,
            available,
          );
        },
      );
    });
  }

  private openConfirmDialog(): void {
    this.setWarning.set(null);
    this.warningLoading.set(true);

    // The dialog is already open while this runs — it reads the panel's signals live (see
    // DeleteConfirmDialogData), so the shared-set warning pops in as soon as the check answers.
    this.loadSetWarning();

    const data: DeleteConfirmDialogData = {
      emotes: this.visibleSelectedEmoteNames,
      hiddenEmotes: this.hiddenSelectedEmoteNames,
      warning: this.setWarning.asReadonly(),
      warningLoading: this.warningLoading.asReadonly(),
    };
    openDeleteConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (confirmed) {
        this.startDelete();
      }
    });
  }

  private loadSetWarning(): void {
    this.emoteAdminService.getSetWarning(this.channelName()).subscribe({
      next: (warning) => {
        this.setWarning.set(warning);
        this.warningLoading.set(false);
      },
      error: () => {
        // `available: false` is the signal the dialog acts on — it renders a neutral "couldn't
        // check" notice instead of the red "confirmed foreign set" alarm, so a failed check no
        // longer produces a false accusation. `isOwnSet` stays `false` deliberately: it is
        // meaningless while `available` is false, and should a future reader consume it without
        // checking `available`, the conservative direction ("not verified as ours") is the safe one.
        this.setWarning.set({
          available: false,
          isOwnSet: false,
          otherTrackedChannelsSharingSet: [],
          otherModeratedChannelsSharingSet: [],
        });
        this.warningLoading.set(false);
      },
    });
  }

  private startDelete(): void {
    const emotes: DeleteQueueEmote[] = this.selectedEmotes().map((emote) => ({
      emoteId: emote.emoteId,
      sevenTvEmoteId: emote.sevenTvEmoteId,
      name: emote.name,
    }));
    this.deleteService.startDelete(this.setId(), this.channelName(), emotes);
  }
}
