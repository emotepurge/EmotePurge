import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { catchError, map, of, timeout } from 'rxjs';

import { EmoteAdminService, EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import {
  DeleteQueueEmote,
  SevenTvDeleteService,
} from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
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
import { filterAlreadyPresentForRestore } from './already-present-filter';
import { DeleteConfirmDialogData, openDeleteConfirmDialog } from './delete-confirm-dialog';
import { resyncNoticeKey } from './dock-outcome-announcer';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { RunProgressPanel } from './run-progress-panel';
import { SevenTvSetEntries, loadSevenTvSetEntries } from './seven-tv-set-entries';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/** Per-instance suffix for the lock reason's element id — the panel renders on two pages, and an
 *  `aria-describedby` target has to be unique in the document. */
let nextDeleteLockReasonId = 0;

/** Dedicated reason keys for the one case this panel blocks by itself: the active set's live
 *  entries, read right before a delete, could not be read or came back incomplete — a list that
 *  only knows half must not delete (spec #200, 8.3's rule, applied here). K5 fix round: these used
 *  to reuse the usage page's own `usageStats.setView.lock.*` texts verbatim, but those say "Deleting
 *  and voting are locked: …" — correct for the sticky lock paragraph they were written for, wrong
 *  here, where this is a one-off, transient abort notice ("Nothing was deleted." + reason), not a
 *  standing lock description. */
const MEMBER_READ_UNAVAILABLE_REASON_KEY = 'massDelete.memberRead.unavailable';
const MEMBER_READ_TRUNCATED_REASON_KEY = 'massDelete.memberRead.truncated';

/** Total time budget for the active-set delete's live alias read (K5 fix round) — a hung request
 *  (7TV accepts the connection but never answers) used to leave `liveAliasReadPending` `true`
 *  forever, with the delete button disabled and no way out short of reloading the page. Generous
 *  for a same-origin-adjacent GraphQL call reading at most 10 pages of up to 500 entries each; a
 *  timeout is treated exactly like any other failed read — nothing is deleted, and the reason is
 *  shown. */
const LIVE_ALIAS_READ_TIMEOUT_MS = 20_000;

/** Outcome of the live alias read `openConfirmDialog` starts for an active-set delete: the entries,
 *  or the translation key of the reason the delete is blocked. */
type LiveAliasRead = { entries: SevenTvSetEntries } | { blockedReasonKey: string };

/** A confirmed delete that did not start, and why — shown until the next attempt. `leadKey` says
 *  what happened, `reasonKey` why. */
interface DeleteAbortNotice {
  leadKey: string;
  reasonKey: string;
}

export interface DeletableEmote {
  /** Local `Emote.Id` — optional (spec #200, 7.2): a live member of a non-active set may never
   *  have had one. The run is keyed by `sevenTvEmoteId`; this only reaches the protocol. */
  emoteId?: string;
  sevenTvEmoteId: string;
  name: string;
  /** Every alias the emote sits under in the set — two for a #74 duplicate cell, which one
   *  `REMOVE` takes whole. Recorded in the protocol so a restore can re-add each. Omitted means
   *  `[name]`. A host that cannot know every alias (the active set's view keeps one name per id)
   *  sets `readLiveAliasesFromActiveSet` instead, and the panel reads them from 7TV itself before
   *  the run starts. The vote-session page deliberately does neither: its rows stay on `[name]`, so
   *  a duplicate deleted there still records one alias (DECISIONS, #200 K6 known limitation). */
  aliases?: readonly string[];
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
            deleteLockReasonKey() !== null ||
            deleteService.isRunning() ||
            arbiter.activeRun() !== null ||
            liveAliasReadPending()
          "
          [attr.aria-describedby]="deleteLockReasonKey() !== null ? deleteLockReasonId : null"
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
      <!-- A lock the host page imposes for a reason of its own (spec #200, 8.3 — e.g. the chosen
           set's live member list could not be read) explains itself as text next to the button,
           not only by greying it out (docs/UI-Designsprache.md §10, "Disabled explains itself").
           Unlike the run-in-progress lock above, nothing else on screen already says why. -->
      @if (deleteLockReasonKey(); as reasonKey) {
        <p [id]="deleteLockReasonId" class="text-xs text-fg-muted">
          {{ reasonKey | transloco }}
        </p>
      }
      <!-- A confirmed delete that the host's lock stopped at the last moment (see startDelete): the
           confirm dialog outlives the view it was opened on, so a set switch behind it must not
           run — and must not fail silently either. Same two-element split as the vote dialog's
           shrink notice (docs/UI-Designsprache.md §4.4/§4.5): a permanently mounted sr-only status
           region whose text comes and goes, plus the visible line as an aria-hidden @if, so it
           neither occupies the column's gap while empty nor is read twice. Cleared by the next
           attempt. -->
      <span role="status" class="sr-only">
        @if (abortNotice(); as notice) {
          {{ notice.leadKey | transloco }} {{ notice.reasonKey | transloco }}
        }
      </span>
      @if (abortNotice(); as notice) {
        <p aria-hidden="true" class="text-sm text-fg-secondary">
          {{ notice.leadKey | transloco }} {{ notice.reasonKey | transloco }}
        </p>
      }

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
              @if (run.result.doneKeys.length > 0 && arbiter.activeRun() === null) {
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
  /** The channel's active 7TV set — `null` when the host knows it does not exist or could not be
   *  read (a *known* unknown), `undefined` when the host simply never bound this input at all.
   *  Gates `isActiveSet` below for both confirmations (spec #200, 8.8) and which slot-preview
   *  source the restore confirmation reads (`openRestoreConfirmDialog`): the active set's cheap,
   *  non-7TV-rate-limited `EmoteAdminService.getSetStatus`, or the live per-set preview otherwise.
   *  `undefined` folds onto `setId()` (`effectiveActiveSetId` below) — same convention as
   *  `ImportTrigger`'s identically-named input (DECISIONS K4) — rather than defaulting to `null`
   *  and thereby reading as "known not active": the vote-session-detail page went unbound for a
   *  full spec round (#200 K5 finding B) before it started passing this explicitly — silently
   *  showing a false "this set is not currently active" note the whole time, worse than the
   *  reverse: an *unconfirmed* explicit `null` still means "we checked and don't know", so it
   *  correctly stays `false` below. Since K6 the vote page's `setId` is the vote session's own
   *  set — a set-session's ballot can be frozen against a set that is no longer active — while
   *  `activeSetId` stays the channel's real active set, so the two can legitimately differ there
   *  (spec §9, `massDeletePanelSetId`/`activeEmoteSetId` on that page). */
  readonly activeSetId = input<string | null | undefined>(undefined);
  /** The selected set's display name, for the delete confirmation (spec #200, 8.8) — falls back to
   *  the set id itself, same convention as every other unnamed-set reader in this app. */
  readonly setName = input<string | null>(null);
  /** Set id → display name, for the restore confirmation: a finished delete run's own frozen set
   *  (`DeleteRunInfo.setId`) can differ from `setId()` above if the dropdown moved on between the
   *  delete finishing and Restore being clicked (the dropdown only locks while the run is still
   *  writing). Falls back to the id itself for a set this map does not name, same convention as
   *  `setName` above. Defaults to an empty map, which folds every lookup onto that same fallback —
   *  a caller that predates this (every existing one) sees exactly the id it always effectively
   *  showed. */
  readonly setNames = input<ReadonlyMap<string, string>>(new Map());
  readonly selectedEmotes = input.required<DeletableEmote[]>();
  /**
   * Translation key of a reason the host page locks the delete button for, or `null` for no such
   * lock. The panel neither decides nor knows the reason — the usage page's set view does (spec
   * #200, 8.3: a member list that could not be read, or was truncated, must not delete). Shown as
   * visible text next to the button and wired to it via `aria-describedby`.
   */
  readonly deleteLockReasonKey = input<string | null>(null);

  /**
   * Whether a delete in the channel's **active** set reads that set's entries live from 7TV right
   * before it starts, and records every alias of each selected emote from that read (operator
   * decision 2026-09-22, amending spec #200 E20). The active set's view keeps one name per 7TV id
   * (its rows come from our database, E16), but one `REMOVE` takes *every* entry of a #74 duplicate
   * — without the read, the protocol would record one alias and a restore would silently re-add
   * only one. A failed or incomplete read blocks the run: nothing is deleted, and the reason is shown
   * (spec 8.3's "a list that only knows half must not delete").
   *
   * Opt-in, `false` by default: a non-active set's rows already carry every alias from the live
   * member list the view is built from, so no second read happens there; and the vote-session page
   * deliberately stays on `[name]` (DECISIONS, #200 K6 known limitation).
   */
  readonly readLiveAliasesFromActiveSet = input<boolean>(false);

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

  /** 7TV ids (the run's `doneKeys`, spec #200 E18) the host page may drop from its list without a
   *  refetch — emitted only once the backend has confirmed the report. Hosts match them against
   *  `sevenTvEmoteId`, never against a Guid. */
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
  /** The non-active set's live slot preview (spec #200, 8.3, K5) — read only when the restore
   *  confirmation's target set is not the active one; see `openRestoreConfirmDialog`. */
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  /** Only for `filterAlreadyPresent`'s direct read against 7TV (#149 P1 fix) — every other read in
   *  this component goes through `emoteAdminService`. */
  private readonly httpClient = inject(HttpClient);
  private readonly dialog = inject(Dialog);
  private readonly destroyRef = inject(DestroyRef);

  /** `activeSetId()` with the omitted (`undefined`) case folded onto `setId()` — see that input's
   *  own doc for why. An explicit `null` ("known unknown") is left alone. */
  private readonly effectiveActiveSetId = computed(() => {
    const active = this.activeSetId();
    return active === undefined ? this.setId() : active;
  });

  /** Whether `setId()` — the set the delete button targets — is the channel's active 7TV set
   *  (spec #200, 8.8). `false` whenever `activeSetId()` is a *known* unknown (`null`), the
   *  conservative direction: an unconfirmed "active" claim is worse than a needless "not active"
   *  note. An *omitted* `activeSetId()` folds onto `setId()` first (`effectiveActiveSetId`), so it
   *  reads as active rather than as the same "known unknown". */
  protected readonly isActiveSet = computed(() => {
    const active = this.effectiveActiveSetId();
    return active !== null && active === this.setId();
  });

  /** Public (not `protected`) on purpose: a host page's own controls outside this component's
   *  template — the usage page's dock vote button, gated on the same `voteLocked()` condition the
   *  host derives from an equivalent set-view lock — reach this id through a template reference
   *  variable on `<app-mass-delete-panel>` to describe themselves with the very same visible
   *  reason paragraph, instead of duplicating it. */
  readonly deleteLockReasonId = `mass-delete-lock-reason-${nextDeleteLockReasonId++}`;
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

  /** What stopped the last confirmed delete right before it started (`startDelete`) — a host lock,
   *  a set switch, or a failed live alias read — or `null`. Shown until the next attempt. */
  protected readonly abortNotice = signal<DeleteAbortNotice | null>(null);

  /** A confirmed active-set delete is waiting for its live alias read
   *  (`readLiveAliasesFromActiveSet`) — the delete button stays disabled meanwhile, so a second
   *  click cannot open a second confirmation for the same selection. */
  protected readonly liveAliasReadPending = signal(false);
  private destroyed = false;

  /** Whether the current run's protocol was downloaded at least once — drives the reminder next
   *  to Close, since reset() leaves the file as the only artifact. */
  protected readonly protocolSaved = signal(false);

  /** Live slot view for the restore-confirm dialog, loaded when that dialog opens. */
  private readonly restoreSlots = signal<{ occupied: number; capacity: number } | null>(null);

  /** Same key the host page's DockOutcomeAnnouncer speaks — see `resyncNoticeKey`. */
  protected readonly resyncNoticeKey = computed(() =>
    resyncNoticeKey(this.restoreService.resyncTrigger(), 'restore'),
  );

  /** #149/T5: wording for how many `ADD`s (aliases, since the 2026-09-22 per-alias rule) the
   *  pre-run duplicate check (`already-present-filter.ts`) dropped — shown independently of the run-progress panel below, because a run where *every*
   *  row was already present queues nothing and would otherwise leave that panel hidden (its own
   *  gate is `isRunning() || queue().length > 0`), silently swallowing the one thing the user needs
   *  to see in that case. */
  protected readonly restoreSkippedDuplicatesKey = computed(() =>
    pluralKey(this.restoreService.skippedDuplicates(), 'restore.skippedDuplicates'),
  );

  constructor() {
    this.destroyRef.onDestroy(() => (this.destroyed = true));

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

      // RunResult.doneKeys, not a re-derivation from the queue: a delete run keys every row by its
      // 7TV id, so these are exactly the ids that left the set — rows without a local emote
      // included (spec #200, E18).
      const doneKeys = this.deleteService.lastRun()?.result.doneKeys ?? [];
      if (doneKeys.length > 0) {
        this.deleted.emit(doneKeys);
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
    this.abortNotice.set(null);
    // The button is already disabled under a host lock; this only guards a click that outraces the
    // lock arriving (a set switch landing while the pointer is on the button).
    if (this.deleteLockReasonKey() !== null) {
      return;
    }
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
    // Every row of the run, unfiltered (spec #200, F3): a row without a local emote is written with
    // `emoteId: null`. Filtering it out here — as this panel once did — produced a silently short
    // protocol, i.e. a deletion without a way back that nobody would notice until they needed it.
    const protocol = buildPurgeRunProtocol({
      channelName: run.channelName,
      emoteSetId: run.setId,
      startedAt: run.result.startedAt,
      finishedAt: run.result.finishedAt,
      items: run.result.items,
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
    // Every done row, with or without a local emote — the restore is keyed by the 7TV id.
    const doneItems = run.result.items.filter((item) => item.status === 'done');
    if (doneItems.length === 0) {
      return;
    }

    if (!this.tokenService.hasToken()) {
      openSevenTvTokenPromptDialog(this.dialog).closed.subscribe((saved) => {
        if (saved) {
          this.openRestoreConfirmDialog(run.setId, run.channelName, doneItems);
        }
      });
      return;
    }
    this.openRestoreConfirmDialog(run.setId, run.channelName, doneItems);
  }

  /** `runSetId`/`runChannelName` are the set and channel the delete run removed from (its frozen
   *  record, spec #200 7.2/AK 71) — the restore puts the emotes back there, never into whatever
   *  `setId()`/`channelName()` say by now. Named and slot-previewed against *that* set (spec 8.8),
   *  which the dropdown may since have moved past (it only locks while the run is still writing). */
  private openRestoreConfirmDialog(
    runSetId: string,
    runChannelName: string,
    doneItems: readonly RunQueueItem[],
  ): void {
    const runIsActiveSet = runSetId === this.effectiveActiveSetId();
    // Live slot view, so the projection line pops in once the check answers (the dialog is
    // already open by then) — same pattern as the delete confirm's shared-set warning. The active
    // run's set keeps the cheap, non-7TV-rate-limited status read; any other set reads the live
    // per-set preview instead (spec 8.3) — `getSetStatus` has no set-scoped form at all.
    this.restoreSlots.set(null);
    if (runIsActiveSet) {
      this.emoteAdminService.getSetStatus(runChannelName).subscribe({
        next: (status) =>
          this.restoreSlots.set(
            status.capacity === null
              ? null
              : { occupied: status.occupiedSlots, capacity: status.capacity },
          ),
        error: () => this.restoreSlots.set(null),
      });
    } else {
      this.emoteSetService.loadEmoteSetPreview(runChannelName, runSetId).subscribe({
        next: (preview) =>
          this.restoreSlots.set(
            preview.capacity === null
              ? null
              : { occupied: preview.totalCount, capacity: preview.capacity },
          ),
        error: () => this.restoreSlots.set(null),
      });
    }

    const data: RestoreConfirmDialogData = {
      names: doneItems.map((item) => item.name),
      // spec #200, 7.2: ADDs, not rows — a #74 duplicate cell's row carries every alias it sat
      // under and restores once per alias.
      addCount: doneItems.reduce((sum, item) => sum + (item.aliases?.length ?? 1), 0),
      slots: this.restoreSlots.asReadonly(),
      setName: this.setNames().get(runSetId) ?? runSetId,
      isActiveSet: runIsActiveSet,
    };
    openRestoreConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      const emotes: DeleteQueueEmote[] = doneItems.map((item) => ({
        emoteId: item.emoteId,
        sevenTvEmoteId: item.sevenTvEmoteId,
        name: item.name,
        aliases: item.aliases,
      }));
      // #149/T5: a restore never had any duplicate protection at all — filter it fresh, right here,
      // against the target set's current contents, read from 7TV itself rather than our database
      // (see `filterAlreadyPresent`'s doc — asking our own mirror is exactly wrong for restore,
      // which runs *because* something already went wrong and our mirror may still be stale) for
      // why this sits at confirm-time rather than dialog-open-time and for the residual race it
      // does not close. Per alias, not per id (operator decision 2026-09-22): a row whose id is
      // present only under some of its own aliases re-adds just the missing ones — see
      // `filterAlreadyPresentForRestore`.
      filterAlreadyPresentForRestore(this.httpClient, runSetId, emotes).subscribe(
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
          this.restoreService.startRestore(runSetId, runChannelName, toRestore, skipped, available);
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

    // Frozen here, alongside setName/isActiveSet below, not re-read from the live `setId()` input
    // at confirm time: the dialog outlives the view it was opened on, and a `channel.synced` set
    // switch can move the host's selected set (and thus this input) while it is still open. Passed
    // into `startDelete` so it can compare against the live value and abort rather than delete into
    // whatever set happens to be selected once the dialog closes (#200 K5 finding A).
    const frozenSetId = this.setId();
    const frozenIsActiveSet = this.isActiveSet();
    // Same reasoning, same moment, for the run's channel (K5 fix round item 7): the panel's own
    // `deleteService.startDelete` call used to read the live `channelName()` input instead, which
    // just happens to be stable in production (a panel only ever sees one channel across a run's
    // lifetime) but was the wrong source of truth all the same — the same class of gap finding A
    // closed for `setId`.
    const frozenChannelName = this.channelName();
    // The exact list the dialog showed, frozen at the same moment as the two above — not re-read
    // from the live `selectedEmotes()` input wherever the run actually starts (K5 fix round item 1).
    // For a plain delete that starts synchronously once confirmed this makes no difference, but an
    // active-set delete's live alias read (`readLiveAliasesThenDelete`) is asynchronous, and the
    // confirm dialog is already closed while it is out — nothing locks the grid, so the live
    // selection can change (grow or shrink) in that window. Deleting the frozen snapshot instead of
    // re-reading the input means: an id removed from the selection afterwards is still deleted (the
    // user already confirmed it), and an id added afterwards is not swept in (the dialog never
    // showed it). A defensive copy, not just a reference: `selectedEmotes()` is expected to be a
    // fresh array per host-page change already, but nothing here depends on that staying true.
    const frozenSelection = [...this.selectedEmotes()];
    const data: DeleteConfirmDialogData = {
      emotes: this.visibleSelectedEmoteNames,
      hiddenEmotes: this.hiddenSelectedEmoteNames,
      warning: this.setWarning.asReadonly(),
      warningLoading: this.warningLoading.asReadonly(),
      setName: this.setName() ?? frozenSetId,
      isActiveSet: frozenIsActiveSet,
    };
    openDeleteConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      if (!frozenIsActiveSet || !this.readLiveAliasesFromActiveSet()) {
        this.startDelete(frozenSetId, frozenChannelName, frozenSelection, null);
        return;
      }
      this.readLiveAliasesThenDelete(frozenSetId, frozenChannelName, frozenSelection);
    });
  }

  /**
   * The active-set delete's live alias read (`readLiveAliasesFromActiveSet`), at **confirm** time,
   * not when the dialog opens: the delete confirmation shows nothing alias-dependent (names only,
   * one per cell), so reading earlier would buy no correct number on screen — it would only spend a
   * read on every cancelled dialog and record aliases as they stood when the dialog opened rather
   * than at the irreversible moment. The frozen set id (`openConfirmDialog`) is what is read, and
   * `startDelete` repeats every confirm-time check once the answer is in, since the set can switch
   * while the read is out.
   */
  private readLiveAliasesThenDelete(
    frozenSetId: string,
    frozenChannelName: string,
    frozenSelection: readonly DeletableEmote[],
  ): void {
    // The same checks `startDelete` makes, made once before the read as well: a delete that is
    // already doomed must not wait for (or spend) a 7TV read first.
    if (this.abortReasonBeforeStart(frozenSetId) !== undefined) {
      this.startDelete(frozenSetId, frozenChannelName, frozenSelection, null);
      return;
    }
    this.liveAliasReadPending.set(true);
    loadSevenTvSetEntries(this.httpClient, frozenSetId)
      .pipe(
        // A hung request (7TV accepts the connection but never answers) must not leave the button
        // disabled forever — treated exactly like any other failed read (K5 fix round item 5).
        timeout(LIVE_ALIAS_READ_TIMEOUT_MS),
        map((entries): LiveAliasRead =>
          entries.complete ? { entries } : { blockedReasonKey: MEMBER_READ_TRUNCATED_REASON_KEY },
        ),
        catchError(() =>
          of<LiveAliasRead>({ blockedReasonKey: MEMBER_READ_UNAVAILABLE_REASON_KEY }),
        ),
      )
      .subscribe((read) => {
        this.liveAliasReadPending.set(false);
        this.startDelete(frozenSetId, frozenChannelName, frozenSelection, read);
      });
  }

  private loadSetWarning(): void {
    // Explicit `setId()` since K5 (spec 6.8): this panel's delete target is the page's *selected*
    // set, not necessarily the channel's active one — the old implicit "check the active set" call
    // would ask the wrong question in a non-active view.
    this.emoteAdminService.getSetWarning(this.channelName(), this.setId()).subscribe({
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

  /** `frozenSetId`/`frozenChannelName`/`frozenSelection` are what the dialog named — read once in
   *  `openConfirmDialog`, not re-read from the live `setId()`/`channelName()`/`selectedEmotes()`
   *  inputs here (K5 fix round items 1 and 7). `liveAliases` is the active-set delete's live alias
   *  read (`readLiveAliasesThenDelete`), or `null` when none was made. */
  private startDelete(
    frozenSetId: string,
    frozenChannelName: string,
    frozenSelection: readonly DeletableEmote[],
    liveAliases: LiveAliasRead | null,
  ): void {
    const abort = this.abortReasonBeforeStart(frozenSetId);
    if (abort !== undefined) {
      this.abortNotice.set(abort);
      return;
    }
    if (liveAliases !== null && 'blockedReasonKey' in liveAliases) {
      this.abortNotice.set({
        leadKey: 'massDelete.abortedByMemberRead',
        reasonKey: liveAliases.blockedReasonKey,
      });
      return;
    }
    // Only reachable after the live alias read, i.e. asynchronously after the confirmation: another
    // run may have started in between, outside the mutual-exclusion contract the delete button's
    // own arbiter gate enforces. Unlike the restore paths' identical re-check (silent there — the
    // run that got there first is always the one whose progress panel is already mounted in *this*
    // same dock), this abort has to be visible (K5 fix round item 4): the competing run can be any
    // of the three 7TV-writing kinds, started from anywhere else on the page, and this panel's own
    // dock would otherwise show nothing at all to explain why a confirmed delete just vanished.
    if (liveAliases !== null && this.arbiter.activeRun() !== null) {
      this.abortNotice.set({
        leadKey: 'massDelete.abortedByMemberRead',
        reasonKey: 'massDelete.anotherRunStarted',
      });
      return;
    }
    const liveEntries = liveAliases?.entries;
    const emotes: DeleteQueueEmote[] = frozenSelection.map((emote) => {
      // The live read knows every entry the one `REMOVE` will take; a cell it does not know keeps
      // what the host said.
      const live = liveEntries?.aliasesById.get(emote.sevenTvEmoteId);
      // An id that also carries an aliasless entry (spec §37/§38's "aliasless" rule, K5 fix round
      // item 3) has one more slot than `live` alone shows — 7TV requires an alias string to restore
      // it, and the read cannot invent one, so this falls back to the emote's own display name
      // rather than leaving that entry unrecorded (a protocol that looks complete but is not, F3).
      // Skipped if `live` already happens to contain that exact name — nothing to add twice.
      const hasAliaslessEntry = liveEntries?.aliaslessIds.has(emote.sevenTvEmoteId) ?? false;
      const liveWithAliasless =
        live !== undefined && hasAliaslessEntry && !live.includes(emote.name)
          ? [...live, emote.name]
          : live;
      return {
        emoteId: emote.emoteId,
        sevenTvEmoteId: emote.sevenTvEmoteId,
        name: emote.name,
        aliases:
          liveWithAliasless !== undefined && liveWithAliasless.length > 0
            ? liveWithAliasless
            : emote.aliases,
      };
    });
    this.deleteService.startDelete(frozenSetId, frozenChannelName, emotes);
  }

  /**
   * Why a confirmed delete must not start now, or `undefined` when nothing stops it — re-evaluated
   * at confirm time, not only when the dialog opened: the dialog outlives the view it was opened on,
   * and the host can lock deleting behind it (a set switch in the usage page's dropdown — the rows
   * and the selection would then belong to a set other than `setId()`). A panel already torn down
   * (its host's dock unmounted while the dialog was open) has no selection of its own left to vouch
   * for, so it starts nothing either — `null` then: abort, but with nothing left to show it on.
   */
  private abortReasonBeforeStart(frozenSetId: string): DeleteAbortNotice | null | undefined {
    if (this.destroyed) {
      return null;
    }
    const lockKey = this.deleteLockReasonKey();
    if (lockKey !== null) {
      return { leadKey: 'massDelete.abortedByLock', reasonKey: lockKey };
    }
    // The lock above only catches a switch still *in progress* — once it settles, the lock clears
    // and `setId()` has already moved on, silently, to the new set. Comparing against what the
    // dialog actually named closes that gap: a settled switch behind an open dialog aborts here
    // too, visibly, instead of deleting into a set the confirmation never showed (#200 K5 finding A).
    if (this.setId() !== frozenSetId) {
      return {
        leadKey: 'massDelete.abortedByLock',
        reasonKey: 'massDelete.setChangedDuringConfirm',
      };
    }
    return undefined;
  }
}
