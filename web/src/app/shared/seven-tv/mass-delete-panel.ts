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
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { catchError, finalize, of, timeout } from 'rxjs';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import {
  DeleteQueueEmote,
  SevenTvDeleteService,
} from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { unknownCount } from '../../core/seven-tv/seven-tv-run-settlement';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { TargetCheckBlockReason } from '../../core/seven-tv/sync-report-outcome';
import { CSV_MIME } from '../export/csv';
import {
  ExportDialogData,
  FORMAT_EXPORT_OPTIONS_JSON_FIRST,
  openExportDialog,
} from '../export/export-dialog';
import { JSON_MIME } from '../export/export-envelope';
import { downloadFile } from '../export/file-download';
import {
  buildPurgeRunProtocol,
  purgeRunCsv,
  purgeRunFilename,
  purgeRunJson,
} from '../export/purge-run-export';
import { Button } from '../ui/button';
import {
  clipToShown,
  filterAlreadyPresentForRestore,
  loadRestoreConfirmPreview,
  RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS,
  RestoreConfirmPreview,
  restoreConfirmPreviewUnavailable,
  RestoreFilterRow,
} from './already-present-filter';
import {
  DeleteAbortNotice,
  DeleteFlowDeps,
  DeleteFlowRequest,
  LIVE_ALIAS_READ_TIMEOUT_MS,
  refusedStartNotice,
  startDeleteFlow,
} from './delete-flow';
import { ResolvedRestoreTarget, restoreStartTarget } from './restore-flow';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { loadRestoreSlotPreview, RestoreSlotPreview } from './restore-slot-preview';
import { RunProgressPanel } from './run-progress-panel';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/** Maps the shared pre-check's block reason (spec 6.2, `TargetCheckBlockReason`) to this panel's
 *  own `restore.errors.*` locale family (Plan-253 §6, Nr. 3) — mirrors the family every other
 *  pre-check caller uses under its own prefix (`restore.import.errors.*`, `massDelete.errors.*`,
 *  `import.errors.*`); the wording was finalized in #255 — all four share one sentence core per
 *  reason, only the set reference (`Das Set`/`Dieses Set` here) varies. */
function restoreTargetCheckReasonKey(reason: TargetCheckBlockReason): string {
  switch (reason) {
    case 'notEditable':
      return 'restore.errors.targetNotEditable';
    case 'notSelectable':
      return 'restore.errors.targetNotSelectable';
    case 'unavailable':
      return 'restore.errors.targetCheckUnavailable';
  }
}

/** Per-instance suffix for the lock reason's element id — the panel renders on two pages, and an
 *  `aria-describedby` target has to be unique in the document. */
let nextDeleteLockReasonId = 0;

/** One row the restore entry hands both duplicate checks: the queue row the run needs, plus the
 *  `uncertain` marker (#275) set from an `unknown` delete row — only the checks read it
 *  (`RestoreFilterRow.uncertain`), the run never does. */
type RestoreCandidate = DeleteQueueEmote & Pick<RestoreFilterRow, 'uncertain'>;

/** The finished delete run's rows the restore entry offers (#275, plan Festlegung 16): every `done`
 *  row — the emote provably left the set — and every `unknown` row, whose delete may or may not
 *  have landed; both with or without a local emote, since the restore is keyed by the 7TV id.
 *  `failed`/`cancelled` rows never left the set and stay out. Whether an `unknown` row is actually
 *  sent is the duplicate checks' call (`filterAlreadyPresentForRestore`, fail-closed), not this
 *  function's. */
function restorableItems(items: readonly RunQueueItem[]): RunQueueItem[] {
  return items.filter((item) => item.status === 'done' || item.status === 'unknown');
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
   *  the run starts. The vote-session page's rows are frozen at session-creation time
   *  (`VoteSessionEmote.NameAtCreation`) and so can never know a live alias either — it sets
   *  `readLiveAliasesFromSet` instead, the same live read against the panel's own (possibly
   *  non-active) `setId()` rather than only the active one (#227, fixing the #200 K6 known
   *  limitation this comment used to describe as open). */
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
            arbiter.startLocked() ||
            liveAliasReadPending() ||
            deleteTargetCheckPending()
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
      <!-- A confirmed delete that the host's lock stopped at the last moment (see delete-flow.ts): the
           confirm dialog outlives the view it was opened on, so a set switch behind it must not
           run — and must not fail silently either. Same two-element split as the vote dialog's
           shrink notice (docs/UI-Designsprache.md §4.4/§4.5): a permanently mounted sr-only status
           region whose text comes and goes, plus the visible line as an aria-hidden @if, so it
           neither occupies the column's gap while empty nor is read twice. Cleared by the next
           attempt. -->
      <span role="status" class="sr-only">
        @if (abortNotice(); as notice) {
          {{ notice.leadKey | transloco }}
          {{ notice.reasonKey | transloco: notice.reasonParams ?? {} }}
        }
      </span>
      @if (abortNotice(); as notice) {
        <p aria-hidden="true" class="text-sm text-fg-secondary">
          {{ notice.leadKey | transloco }}
          {{ notice.reasonKey | transloco: notice.reasonParams ?? {} }}
        </p>
      }

      @if (deleteService.isRunning() || deleteService.queue().length > 0) {
        <app-run-progress-panel
          [items]="deleteService.queue()"
          [isRunning]="deleteService.isRunning()"
          [settling]="deleteService.run()?.phase === 'settling'"
          [dismissible]="deleteService.run()?.phase === 'closed'"
          [syncReport]="deleteService.syncReport()"
          [syncReportReason]="deleteService.syncReportReason()"
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
              @if (unknownRowCount() > 0) {
                <!-- #275: the settled run still has rows 7TV's answer never clarified — the backend
                     resync this run already triggered (a report's own, or the client fallback for a
                     run with nothing to report) heals them without another click, but the admin
                     needs telling to go check the set directly, and that a restore from the protocol
                     below still offers these rows back in case the resync finds them still gone. -->
                <span class="text-xs text-fg-muted">
                  {{ unknownRowsKey() | transloco: { count: unknownRowCount() } }}
                </span>
                <span class="text-xs text-fg-muted">
                  {{ unknownInProtocolKey() | transloco }}
                </span>
              }
              @if (restoreOffered() && arbiter.activeRun() === null) {
                <!-- The two-tier *shape* of the destructive convention, not its colour: outline
                     triggers, the dialog's primary-solid executes — restore is constructive.
                     Disabled while restoreConfirmPending() (#255 P2a): the target check and the
                     open-time duplicate check both run before any dialog is on screen, and a
                     second click in that window must not start a second read racing towards a
                     second confirmation. Likewise while arbiter.startPending() (#280): a confirmed
                     start of any run has closed its confirmation, but its last live read still
                     decides whether the run starts, and the button must not look free meanwhile —
                     DockOutcomeAnnouncer speaks that wait. Hidden only once a run holds the
                     arbiter, as before; disabled rather than hidden here, so nothing jumps. -->
                <button
                  type="button"
                  appButton="outline"
                  class="disabled:cursor-not-allowed"
                  [disabled]="restoreConfirmPending() || arbiter.startPending()"
                  (click)="openRestoreConfirm()"
                >
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
   *  the set id itself, same convention as every other unnamed-set reader in this app. The restore
   *  confirmation used to read a `setNames` map for the same reason (a finished delete run's own
   *  frozen set could differ from `setId()` by the time Restore was clicked); since T6/T7 that
   *  confirmation names the set fresh from the shared pre-check's own target list instead
   *  (`resolveEditableSet`, spec 6.2), so the map became dead and was removed (Plan-253 §6, Nr. 1). */
  readonly setName = input<string | null>(null);
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
   * reads its own set instead, through `readLiveAliasesFromSet` below — never both.
   */
  readonly readLiveAliasesFromActiveSet = input<boolean>(false);

  /**
   * The vote-session page's counterpart to `readLiveAliasesFromActiveSet` above (#227, fixing the
   * #200 K6 known limitation "a delete started from the vote page records only the frozen name as
   * its alias"): whether a delete reads the panel's own `setId()` live from 7TV right before it
   * starts, **regardless of whether that set is the channel's active one**. Where the active-set
   * flag only fires when `setId()` happens to be active (a non-active set's rows there already
   * carry live aliases from the member list they were built from, E20/K5), the vote page's rows
   * never carry a live alias at all — `VoteSessionEmote.NameAtCreation` is frozen the moment the
   * session is created, whether the session's set is active or not — so this reads unconditionally
   * whenever set at all. Same failure handling as the active-set read: a failed or incomplete read
   * blocks the run rather than silently deleting under the frozen name (spec 8.3's "a list that
   * only knows half must not delete") — the exact defect #227 exists to close.
   *
   * Opt-in, `false` by default, and mutually exclusive with `readLiveAliasesFromActiveSet` in
   * practice (only one of the two host pages ever sets either) — nothing here enforces that, since
   * setting both would simply mean the same read fires from the same `liveAliasRead` check
   * either way.
   */
  readonly readLiveAliasesFromSet = input<boolean>(false);

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
  /** Only for the direct reads against 7TV — `filterAlreadyPresent` (#149 P1 fix) here, the live
   *  alias read in the delete flow — every other read goes through `emoteAdminService`. */
  private readonly httpClient = inject(HttpClient);
  private readonly dialog = inject(Dialog);
  private readonly destroyRef = inject(DestroyRef);
  /** Only for the refused-start notice's kind noun and, in the delete flow, the missing-row abort
   *  reason's "and N more" tail (#227 P2-c) — every other string in this component goes through the
   *  template's own `TranslocoPipe`. */
  private readonly translocoService = inject(TranslocoService);

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
  // Split by `hidden` for the delete-confirm dialog (Konzept "Auswahl überlebt Suche und Filter"
  // 2.1): the visible names keep today's capped preview, the hidden ones get their own, uncapped
  // block, because a filtered-out delete target must stay identifiable by name right up to the
  // irreversible action rather than collapsing into "n weitere". Owned here and handed to the
  // delete flow as they are (`DeleteFlowRequest`), so the dialog renders these very signals.
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

  /** What stopped the last confirmed delete right before it started (the delete flow's start-time
   *  re-check, `delete-flow.ts`) — a host lock, a set switch, an emptied selection or a failed live
   *  alias read — or `null`. Shown until the next attempt.
   *
   *  Deliberately panel-local, unlike `SevenTvDeleteService.duplicateNoticePending`, which sits on
   *  the service because the *service* is what produces it (`startRestore` sets it). Every reason
   *  here is decided from this panel's own inputs — the host lock, the frozen set id, the arbiter,
   *  the confirmed selection — so moving the text onto the root singleton would move panel-local
   *  knowledge into shared state, and both mounted panels (usage page and vote-session page) would
   *  render the same notice: an abort on one page would surface on the other. The one thing a
   *  service-held notice would buy — a *freshly mounted* panel still showing it — is precisely the
   *  case where showing it is wrong: a panel is remounted by a route, set or pointer change, i.e.
   *  in a view the aborted delete never belonged to. What keeps the notice readable is instead
   *  keeping the panel that set it alive, which is what `confirmedRunPending` does. */
  protected readonly abortNotice = signal<DeleteAbortNotice | null>(null);

  /** A confirmed delete is waiting for its live alias read (`readLiveAliasesFromActiveSet` or
   *  `readLiveAliasesFromSet`, folded into `liveAliasRead` below) — the delete button stays disabled
   *  meanwhile, so a second click cannot open a second confirmation for the same selection.
   *
   *  Aliases `SevenTvDeleteService.startCheckPending` since #280 rather than holding a flag of its
   *  own: registered with the arbiter, the same state now also locks every other 7TV start trigger
   *  and is spoken by the page's `DockOutcomeAnnouncer`. Root-level, so it is released by the read's
   *  own `finalize`, never by this panel's lifecycle — the read is not dropped with the panel. */
  protected readonly liveAliasReadPending = this.deleteService.startCheckPending;

  /** The shared pre-check (spec 4.6 point 20, AK 31) is out for the delete confirmation about to
   *  open — the delete button stays disabled meanwhile, same idiom as `liveAliasReadPending`, so a
   *  second click cannot start the check twice or open a second confirmation once it answers. */
  protected readonly deleteTargetCheckPending = signal(false);
  /** The restore entry's own pre-check chain is out (#255 P2a) — from `openRestoreConfirm`'s own
   *  `resolveEditableSet` call through `openRestoreConfirmDialog`'s open-time duplicate check,
   *  right up until the confirmation opens (or one of the two shortcuts fires instead: the abort
   *  notice, or the "everything already there" restore). `false` again while a token prompt is
   *  open in between the two reads — that dialog already blocks the background on its own, same
   *  reasoning as every other CDK-modal gap in this file.
   *
   *  Aliases `SevenTvRestoreService.restorePreCheckPending` rather than holding a signal of its
   *  own (#255 P2, Codex review): this panel's restore button and `ImportTrigger`'s restore-file
   *  door mount together on the usage-stats page, and a component-local flag here only ever
   *  guarded *this* button against itself — a click on `ImportTrigger` while this panel's own read
   *  was out (or the reverse) could open a second confirmation stacked on the first, with a
   *  duplicate `app-dialog-title` id. Reading the shared signal here closes that window: the
   *  button's own `[disabled]` binding now also reflects a pre-check the *other* entry started.
   *
   *  Because it is shared and root-level, `openRestoreConfirm`/`openRestoreConfirmDialog` release
   *  it via `finalize` on every read's own pipe rather than a manual `.set(false)` in each outcome
   *  branch (#255 P2, second Codex finding): `takeUntilDestroyed` tears a pipe down silently on
   *  this panel's own destroy, calling neither `next` nor `error`, so a manual reset reachable only
   *  from those never ran — and left both restore entries disabled until a full page reload, not
   *  just this panel's own button, since the flag they share outlives the component. */
  protected readonly restoreConfirmPending = this.restoreService.restorePreCheckPending;

  /** The two live-alias-read opt-ins folded into one mode for the delete flow (#227): the vote
   *  page's own-set read wins, as it always did — it fires unconditionally, while the active-set one
   *  only fires once the flow has frozen that the targeted set is the active one. */
  private readonly liveAliasRead = computed(() => {
    if (this.readLiveAliasesFromSet()) {
      return 'set' as const;
    }
    return this.readLiveAliasesFromActiveSet() ? ('activeSet' as const) : ('none' as const);
  });

  /** The delete flow's collaborators (`delete-flow.ts`), built once — this panel's own injected
   *  instances, its `destroyRef` included: a confirmed delete whose panel is gone starts nothing. */
  private readonly deleteFlowDeps: DeleteFlowDeps = {
    dialog: this.dialog,
    emoteAdminService: this.emoteAdminService,
    emoteSetService: this.emoteSetService,
    httpClient: this.httpClient,
    tokenService: this.tokenService,
    deleteService: this.deleteService,
    arbiter: this.arbiter,
    translocoService: this.translocoService,
    destroyRef: this.destroyRef,
  };

  /** What the delete flow reads from this panel — signals throughout, so the flow re-reads or
   *  freezes each one at exactly the point the chain always did. */
  private readonly deleteFlowRequest: DeleteFlowRequest = {
    setId: this.setId,
    activeSetId: this.effectiveActiveSetId,
    channelName: this.channelName,
    setName: this.setName,
    selectedEmotes: this.selectedEmotes,
    visibleEmoteNames: this.visibleSelectedEmoteNames,
    hiddenEmoteNames: this.hiddenSelectedEmoteNames,
    hostLockReasonKey: this.deleteLockReasonKey,
    liveAliasRead: this.liveAliasRead,
    notice: this.abortNotice,
    targetCheckPending: this.deleteTargetCheckPending,
  };

  /** Whether the current run's protocol was downloaded at least once — drives the reminder next
   *  to Close, since reset() leaves the file as the only artifact. */
  protected readonly protocolSaved = signal(false);

  /** Live slot view for the restore-confirm dialog, loaded when that dialog opens. */
  private readonly restoreSlots = signal<RestoreSlotPreview>(null);

  /** Whether the finished run has anything the restore entry could offer — a `done` or, since
   *  #275, an `unknown` row (`restorableItems`). An `unknown`-only run shows the entry too, even
   *  though its `doneKeys` are empty. */
  protected readonly restoreOffered = computed(() => {
    const run = this.deleteService.lastRun();
    return run !== null && restorableItems(run.result.items).length > 0;
  });

  /** How many of the settled run's rows 7TV's answer never clarified (#275) — `0` before the run has
   *  settled (`lastRun()` is `null` while running or settling), same source `restoreOffered` reads. */
  protected readonly unknownRowCount = computed(() => {
    const run = this.deleteService.lastRun();
    return run === null ? 0 : unknownCount(run.result.items);
  });

  protected readonly unknownRowsKey = computed(() =>
    pluralKey(this.unknownRowCount(), 'massDelete.summary.unknownRows'),
  );

  /** Delete-only companion line to `unknownRowsKey` (Plan-275 Festlegung 18): the restore entry
   *  right below already offers these very rows back from the protocol, in case the resync the
   *  settle triggered still finds them gone. Restore's own summary has no such second line — a
   *  restore's protocol is not itself restorable. */
  protected readonly unknownInProtocolKey = computed(() =>
    pluralKey(this.unknownRowCount(), 'massDelete.summary.unknownInProtocol'),
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

      if (notifiedForThisRun) {
        return;
      }

      // 'idle' also covers a run in which nothing succeeded — usually nothing to report either way,
      // except the nothing-but-unknown case (#275, Festlegung 13 (a)): every remaining row stayed
      // `unknown`, none `done`, so `settleRun` never sends a sync-deleted call and `syncReport` stays
      // `idle` for good — the service asks the backend for a resync of the active set instead. What
      // this reload mainly does is clear the host's selection, so the rows this run may or may not
      // have deleted do not stay marked as if nothing had happened. Its refetch will usually still
      // see the state from before that resync, which runs asynchronously: the new state reaches the
      // page through `channel.synced` once the resync is done, like any other sync.
      if (report === 'idle') {
        const run = this.deleteService.lastRun();
        if (run !== null && unknownCount(run.result.items) > 0) {
          notifiedForThisRun = true;
          this.reloadRequested.emit();
        }
        return;
      }

      if (report === 'pending') {
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
    // Same shape, same reason, for the mutual-exclusion contract (design doc §4.3): the button is
    // disabled while any 7TV-writing run holds the arbiter, running or settling, and this catches
    // the click that outraces such a run starting elsewhere on the page. Silent, like the lock guard
    // above — nothing has been confirmed yet (Festlegung Nr. 8, #256 contract P2), so this stays
    // quiet the same way `openRestoreConfirm`'s own pre-dialog guard does. The re-check in
    // the delete flow (`delete-flow.ts`) is what covers the far side of the dialog, and it does show
    // a reason (#256 T4).
    // `startLocked`, not `activeRun` alone (#280): a confirmed start of any run still being checked
    // before its start locks this button too, and a click outracing that lock stays quiet as well.
    if (this.arbiter.startLocked()) {
      return;
    }
    // No stored 7TV token yet: ask for it first. The prompt closes itself with `true` the moment
    // the token is saved, which chains straight into the confirm dialog — the flow the old
    // hand-built overlay produced via its reactive template switch.
    if (!this.tokenService.hasToken()) {
      openSevenTvTokenPromptDialog(this.dialog).closed.subscribe((saved) => {
        if (saved) {
          startDeleteFlow(this.deleteFlowDeps, this.deleteFlowRequest);
        }
      });
      return;
    }

    startDeleteFlow(this.deleteFlowDeps, this.deleteFlowRequest);
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
      targetOwnerTwitchId: run.targetOwnerTwitchId,
    });
    const data: ExportDialogData = {
      rowCount: protocol.rows.length,
      filtered: false,
      // The protocol is always the whole run — a scope choice would make no sense here.
      selectionCount: null,
      noticeKeys: [],
      optionsLegendKey: 'export.formatLabel',
      options: FORMAT_EXPORT_OPTIONS_JSON_FIRST,
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

  /** The restore entry at the finished delete run (spec E16, 4.6 point 22): the pre-check runs
   *  first, like every other first mutation (E19) — in the normal case a cache hit, because the
   *  delete's own report just warmed the target list for this very set. A block shows the panel's
   *  abort notice with a restore-specific lead line and the `restore.errors.*` reason family
   *  (Plan-253 §6, Nr. 3); nothing opens, nothing is sent to 7TV.
   *
   *  `timeout`/`error` and `takeUntilDestroyed` mirror the delete's own pre-check exactly
   *  (review round 1, finding 4): before this fix the subscription had no `error` branch at all, so
   *  a failed request (429, 503, no connection — spec F3) surfaced nothing and silently left the
   *  restore entry inert; a hung one left it inert forever; and a late answer after this panel was
   *  torn down could still have opened a confirmation nobody could see or answer. */
  protected openRestoreConfirm(): void {
    // #255 P2a: refuses a second click while the pre-check chain below (this method's own
    // `resolveEditableSet`, or `openRestoreConfirmDialog`'s open-time duplicate check) is still
    // out — belt and suspenders next to the button's own `[disabled]="restoreConfirmPending()"`.
    if (this.restoreConfirmPending() || this.arbiter.startPending()) {
      return;
    }
    const run = this.deleteService.lastRun();
    // The button that calls this is already hidden while the arbiter is busy (the template's own
    // `arbiter.activeRun() === null` guard around it) — this only catches a click outracing such a
    // run starting elsewhere on the page, same shape and same reason as `openConfirm`'s own
    // pre-dialog guard above: nothing has been confirmed yet (Festlegung Nr. 8, #256 contract P2),
    // so it stays quiet. The re-checks further down, once a restore actually has something to
    // confirm, do show a reason (#256 T4).
    if (!run || this.arbiter.activeRun() !== null) {
      return;
    }
    // Every done and unknown row (#275), with or without a local emote — see `restorableItems`.
    const restoreItems = restorableItems(run.result.items);
    if (restoreItems.length === 0) {
      return;
    }
    // #256 P3-3 (Plan-256 review): the delete service is a root singleton, so its finished run can
    // still be the one shown here after the workspace has moved to a different channel (Plan-256
    // Festlegung 13 lets a reporting run follow the user; this panel's own `channelName()` input
    // then updates to the new page while `run` keeps pointing at the old one). Attributing the
    // restore to the *live* page in that case — the pre-#256 behaviour, back when a run's `channelName`
    // and the panel's own input could never drift apart — would tag a run whose actual removals
    // happened on `run.channelName` as belonging to wherever the dock was merely still visible.
    // Fail-closed: use the run's own frozen channel, which is exactly the live page's value in the
    // ordinary case (nothing has been "taken along") and only differs in the carried-over one, where
    // it is the correct answer. `channelName` is a required field of `DeleteRunInfo`, never empty in
    // practice — the guard below only exists so a future run shape that cannot supply one locks the
    // button instead of silently mis-attributing it.
    const hostChannelName = run.channelName;
    if (!hostChannelName) {
      this.abortNotice.set({
        leadKey: 'restore.nothingRestored',
        reasonKey: 'restore.errors.channelUnknown',
      });
      return;
    }
    this.restoreConfirmPending.set(true);

    // #255 P2 (Codex review, second finding): `handedOff` is `true` exactly when this read's own
    // `next` branch goes on to start the *next* stage of the same shared pre-check chain
    // (`openRestoreConfirmDialog`) without releasing the gate first — every other exit (not
    // editable, the request itself failing, or the caller tearing this panel down mid-read) must
    // release it right here instead. `finalize` is what makes teardown release it too:
    // `takeUntilDestroyed` unsubscribes silently, calling neither `next` nor `error`, so a reset
    // living only inside those branches never ran for that exit — and since this gate is the
    // shared, root-level `restorePreCheckPending`, leaving it `true` there left both restore
    // entries disabled until a full page reload, not just this panel's own button.
    let handedOff = false;
    this.emoteSetService
      // Owner-hint design 3.6, fourth row: the delete run's own owner hint when it has one, else
      // its frozen channel login (a run carried over from an older tab that started before this
      // field existed).
      .resolveEditableSet(run.setId, {
        twitchChannelId: run.targetOwnerTwitchId,
        twitchLogin: run.channelName,
      })
      .pipe(
        timeout(LIVE_ALIAS_READ_TIMEOUT_MS),
        takeUntilDestroyed(this.destroyRef),
        finalize(() => {
          if (!handedOff) {
            this.restoreConfirmPending.set(false);
          }
        }),
      )
      .subscribe({
        next: (resolution) => {
          if (resolution.status !== 'editable') {
            this.abortNotice.set({
              leadKey: 'restore.nothingRestored',
              reasonKey: restoreTargetCheckReasonKey(resolution.status),
            });
            return;
          }
          // `hostChannelName` is the delete run's own frozen channel, not necessarily this panel's
          // live `channelName()` input (spec 6.3, E13/E21 — revised by #256 P3-3): in the ordinary
          // case, where nothing has moved the dock to another channel since the delete started,
          // the two are the same value, so this still attributes a restore into a non-active or
          // foreign set to whichever page the delete itself ran on. `hostSelectedSetId` stays the
          // live dropdown value — it only feeds `RestoreProgressSection`'s "this view shows nothing
          // from this run" line, which is about the set currently on screen, not about ownership.
          const target: ResolvedRestoreTarget = {
            ...resolution.target,
            hostChannelName,
            hostSelectedSetId: this.setId(),
          };
          if (!this.tokenService.hasToken()) {
            // Released while the token prompt is open (via this pipe's own `finalize` above, since
            // `handedOff` stays `false` on this exit) — a CDK modal already blocks the button
            // behind it, same as every other token-prompt gap in this file — and reclaimed right
            // before the next read starts, whichever way the prompt closes.
            openSevenTvTokenPromptDialog(this.dialog).closed.subscribe((saved) => {
              if (saved) {
                this.restoreConfirmPending.set(true);
                this.openRestoreConfirmDialog(target, restoreItems);
              }
            });
            return;
          }
          handedOff = true;
          this.openRestoreConfirmDialog(target, restoreItems);
        },
        // 429, 503, no connection, or a timeout: "cannot be checked right now", never "not
        // allowed" (F3) — the same distinction the delete's own pre-check makes.
        error: () => {
          this.abortNotice.set({
            leadKey: 'restore.nothingRestored',
            reasonKey: restoreTargetCheckReasonKey('unavailable'),
          });
        },
      });
  }

  /** `target` is the resolved target the pre-check produced (spec 6.2) — the restore puts the
   *  emotes back into `target.emoteSetId`, never into whatever `setId()` says by now. Named and
   *  slot-previewed against *that* set (spec 8.8), which the dropdown may since have moved past
   *  (it only locks while the run is still writing). */
  private openRestoreConfirmDialog(
    target: ResolvedRestoreTarget,
    restoreItems: readonly RunQueueItem[],
  ): void {
    // #275: an `unknown` row carries the `uncertain` marker into both duplicate checks, which drop
    // it whenever their read cannot vouch for it — the rule lives in the filter, not here.
    const emotes: RestoreCandidate[] = restoreItems.map((item) => ({
      emoteId: item.emoteId,
      sevenTvEmoteId: item.sevenTvEmoteId,
      name: item.name,
      aliases: item.aliases,
      ...(item.status === 'unknown' ? { uncertain: true as const } : {}),
    }));

    // Operator decision 2026-09-25 (#255, "Slot-Zahl nach dem Skip-Filter") — same open-time
    // check as `startRestoreFlow` (`restore-flow.ts`), reused here rather than duplicated: see
    // `loadRestoreConfirmPreview`'s doc for why this is not a second 7TV read next to the slot
    // preview above, and the confirm-time re-check below for why it still runs fresh again.
    //
    // #255 P2a: bounded by the same timeout budget as this panel's other reads
    // (`LIVE_ALIAS_READ_TIMEOUT_MS`, exported as `RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS` for
    // `restore-flow.ts` to share) and dropped on teardown via `takeUntilDestroyed`. A `timeout`
    // error lands outside `loadRestoreConfirmPreview`'s own `catchError`, so it is treated exactly
    // like the fetch failure that filter already fails open on: `restoreConfirmPreviewUnavailable`
    // builds the identical "could not verify" shape by hand.
    //
    // #255 P2 (Codex review, second finding): this is the last stage of the shared pre-check chain
    // — whatever happens next (the "everything already there" shortcut, the confirmation opening,
    // or nothing at all) no longer needs `restoreConfirmPending` held, so `finalize` releases it
    // unconditionally on every exit, teardown included, rather than the single manual reset that
    // used to sit at the top of `handleRestoreConfirmPreview` and could not run when
    // `takeUntilDestroyed` tore this down first.
    loadRestoreConfirmPreview(this.httpClient, target.emoteSetId, emotes)
      .pipe(
        timeout(RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS),
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.restoreConfirmPending.set(false)),
      )
      .subscribe({
        next: (preview) => this.handleRestoreConfirmPreview(target, emotes, preview),
        error: () =>
          this.handleRestoreConfirmPreview(
            target,
            emotes,
            restoreConfirmPreviewUnavailable(emotes),
          ),
      });
  }

  /** `emotes` is always the full, unfiltered list `openRestoreConfirmDialog` built from
   *  `restoreItems` — never `preview.rows` — because the confirm-time re-check below (`closed`'s
   *  handler) has to run against the *complete* row set again, fresh, not against this open-time
   *  answer's already-filtered subset (see the comment on that re-check). Its *result*, though, is
   *  clipped back down to `preview.rows` before it ever reaches `startRestore` (#255 P1, Codex
   *  review) — see the comment on that clip for why querying full and clipping after, rather than
   *  querying `preview.rows` directly, is the fix. */
  private handleRestoreConfirmPreview(
    target: ResolvedRestoreTarget,
    emotes: readonly RestoreCandidate[],
    preview: RestoreConfirmPreview<RestoreCandidate>,
  ): void {
    // #275: not when unclear rows were left out — "everything already there" would be untrue for
    // them; the confirmation opens instead and says how many were not offered.
    if (preview.available && preview.rows.length === 0 && preview.uncertainDropped === 0) {
      // Nothing survives the filter — same "everything already there" shortcut `startRestoreFlow`
      // takes, reusing the existing notice instead of a dialog that could only ever show zero
      // names.
      //
      // #255 P2b (the #149 P2 fix's own reasoning, applied to this shortcut too): this call starts
      // a run exactly as much as the regular path's does, so it needs the same mutual-exclusion
      // check right before it — another 7TV-writing run could have claimed the arbiter while this
      // read was out, a window the regular path already closes just above its own `startRestore`
      // call. #256 contract P2, Festlegung Nr. 8: this shortcut only runs once the open-time check
      // found nothing left to confirm — a confirmed start finding nothing to start, same as the
      // regular path below, so it shows the abort notice with the blocking kind rather than
      // vanishing, as it used to.
      const directStartClaim = this.arbiter.activeClaim();
      if (directStartClaim !== null) {
        this.abortNotice.set(
          refusedStartNotice(this.translocoService, directStartClaim, 'restore.nothingRestored'),
        );
        return;
      }
      this.restoreService.startRestore(
        restoreStartTarget(target),
        [],
        preview.skipped,
        true,
        preview.skippedNameTaken,
      );
      return;
    }

    // Live slot view, so the projection line pops in once the check answers (the dialog is
    // already open by then) — same pattern as the delete confirm's shared-set warning. Started
    // only now rather than up front (#255 P3(10)): the shortcut above already covers the "nothing
    // left to confirm" case, so starting this read before knowing whether a dialog will even open
    // would spend a 7TV request the "everything already there" outcome above then throws away
    // unread — this way it fires exactly once per call, only when there is a confirmation for it
    // to populate. The read itself is `loadRestoreSlotPreview`, the fork this shares with
    // `restore-flow.ts`'s `startRestoreFlow` (spec 4.3, point 8 / spec 8.3, final fix wave A5).
    // Unrelated to the duplicate check above: this one reads occupied/capacity counts, never
    // entries.
    this.restoreSlots.set(null);
    // #275: nothing left to add projects nothing — no slot read either (same as `restore-flow.ts`).
    if (preview.addCount > 0) {
      loadRestoreSlotPreview(
        { emoteAdminService: this.emoteAdminService, emoteSetService: this.emoteSetService },
        target,
      ).subscribe((slotPreview) => this.restoreSlots.set(slotPreview));
    }

    const data: RestoreConfirmDialogData = {
      names: preview.names,
      addCount: preview.addCount,
      // #255 P2, Codex review: same hedge as `restore-flow.ts`'s `startRestoreFlow` — a read that
      // stopped short of the whole target set (`SevenTvSetEntries.complete: false`) still filters
      // `preview.rows` against whatever it saw, but the title and slot projection would otherwise
      // claim an exact number a partial read never verified.
      countIsUpperBound: !preview.available || !preview.complete,
      slots: this.restoreSlots.asReadonly(),
      setName: target.setName,
      isActiveSet: target.isActiveSet,
      emoteSetId: target.emoteSetId,
      ownerDisplayName: target.ownerDisplayName,
      trackedChannelName: target.trackedChannelName,
      // Spec E21: the run's set against the page's *selected* set — a different set of the same
      // channel, and a page with no selection, both count as foreign.
      foreignToView: target.emoteSetId !== target.hostSelectedSetId,
      uncertainDropped: preview.uncertainDropped,
    };
    openRestoreConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      // #149/T5: a restore never had any duplicate protection at all — filter it fresh, right
      // here, against the target set's current contents, read from 7TV itself rather than our
      // database (see `filterAlreadyPresent`'s doc — asking our own mirror is exactly wrong for
      // restore, which runs *because* something already went wrong and our mirror may still be
      // stale) for why this sits at confirm-time and re-reads rather than reusing the open-time
      // preview above, and for the residual race it does not close. Per alias, not per id
      // (operator decision 2026-09-22): a row whose id is present only under some of its own
      // aliases re-adds just the missing ones, and an alias another emote now holds is left out
      // rather than sent into a certain name conflict — see `filterAlreadyPresentForRestore`.
      //
      // #280: held from here until the check has settled, same as `startRestoreFlow`'s own
      // confirm-time check (see there) — bounded, a timeout reading as a failed check. No
      // `takeUntilDestroyed` on purpose: the restore is confirmed and the root service shows it
      // wherever it runs, so this panel's teardown must not silently drop it.
      this.restoreService.startCheckPending.set(true);
      filterAlreadyPresentForRestore(this.httpClient, target.emoteSetId, emotes)
        .pipe(
          timeout(RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS),
          catchError(() => of(restoreConfirmPreviewUnavailable(emotes))),
          finalize(() => this.restoreService.startCheckPending.set(false)),
        )
        .subscribe((confirmCheck) => {
          // #149 P2 review fix: openRestoreConfirm()'s own arbiter check ran before this dialog
          // even opened — well outside the mutual-exclusion contract (design doc §4.3) it exists
          // to enforce, since a delete or import can start while the confirm dialog is open and
          // this fetch is in flight. Re-checked here, right before the only remaining call that
          // actually starts anything — a *confirmed* start finding nothing to start (#256 contract
          // P2, Festlegung Nr. 8), so it shows the abort notice with the blocking kind instead of
          // vanishing, as this used to.
          const confirmTimeClaim = this.arbiter.activeClaim();
          if (confirmTimeClaim !== null) {
            this.abortNotice.set(
              refusedStartNotice(
                this.translocoService,
                confirmTimeClaim,
                'restore.nothingRestored',
              ),
            );
            return;
          }
          // #255 P3(7): a failed confirm-time check normally means every row goes out unfiltered
          // (`filterAlreadyPresentForRestore`'s own fail-open behaviour) — which would silently
          // throw away the open-time check's own, still-valid answer for any row it had already
          // found already present or name-taken. Falls back to that stale-but-real filter instead
          // of no filter at all, whenever the open-time check succeeded. `available` stays what
          // the confirm-time check itself answered either way: it is the freshest check, and its
          // failure still leaves the narrow window since the open-time read unverified, so
          // `duplicateCheckUnavailable` keeps applying — this only changes *which rows* get sent,
          // not whether the caller is told the check could not confirm them just now.
          const fallOnOpenTime = !confirmCheck.available && preview.available;
          // #255 P1 (Codex review): the confirmation only ever showed `preview.rows` — a row (or
          // one alias of a row) the open-time check above had already found present, and which
          // never appeared in the dialog's names or `addCount`, must not come back just because it
          // went missing again by the time this fresher check ran (the target set changing while
          // the confirmation sat open, or between the two reads). `confirmCheck` itself still has
          // to query with every row's full, original aliases — `clipToShown`'s own doc explains why
          // a narrower input here would break the #74 partial-retry case — so the invariant is
          // enforced afterward instead: the confirm-time answer only ever narrows what was shown,
          // `startRestore` can never see more than that. `fallOnOpenTime` already reuses
          // `preview.rows` unclipped — that IS what was shown, nothing to narrow further.
          // Unaffected: the skip counters below, which still come straight from `confirmCheck`'s
          // own fresh count, exactly as before this fix.
          const rows = fallOnOpenTime ? preview.rows : clipToShown(confirmCheck.rows, preview.rows);
          this.restoreService.startRestore(
            restoreStartTarget(target),
            rows,
            fallOnOpenTime ? preview.skipped : confirmCheck.skipped,
            confirmCheck.available,
            fallOnOpenTime ? preview.skippedNameTaken : confirmCheck.skippedNameTaken,
          );
        });
    });
  }
}
