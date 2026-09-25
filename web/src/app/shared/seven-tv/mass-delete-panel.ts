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
import { catchError, finalize, map, of, timeout } from 'rxjs';

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
import { TargetCheckBlockReason } from '../../core/seven-tv/sync-report-outcome';
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
import { PREVIEW_CAP } from '../ui/name-preview-list';
import { filterAlreadyPresentForRestore } from './already-present-filter';
import { DeleteConfirmDialogData, openDeleteConfirmDialog } from './delete-confirm-dialog';
import { ResolvedRestoreTarget, restoreStartTarget } from './restore-flow';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { loadRestoreSlotPreview, RestoreSlotPreview } from './restore-slot-preview';
import { RunProgressPanel } from './run-progress-panel';
import { SevenTvSetEntries, loadSevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/** Maps the shared pre-check's block reason (spec 6.2, `TargetCheckBlockReason`) to this panel's
 *  own `restore.errors.*` locale family (Plan-253 §6, Nr. 3) — mirrors the family every other
 *  pre-check caller uses under its own prefix (`restore.import.errors.*`, `massDelete.errors.*`,
 *  `import.errors.*`); the wording is provisional (#255), the mapping is the contract. */
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

/** Same mapping as {@link restoreTargetCheckReasonKey}, for the delete confirmation's own
 *  pre-check before it opens (spec 4.6 point 20, AK 31) — `massDelete.errors.*`, this panel's own
 *  family for the delete, never `restore.errors.*` (that one names the *restore* entry at a
 *  finished run, a different first mutation with its own copy). `notSelectable` cannot actually
 *  occur in production here — the delete panel is always given a set the host already resolved as
 *  `NORMAL` — but the mapping stays total rather than assuming that at the type level, the same
 *  discipline `restoreTargetCheckReasonKey` keeps. */
function deleteTargetCheckReasonKey(reason: TargetCheckBlockReason): string {
  switch (reason) {
    case 'notEditable':
      return 'massDelete.errors.targetNotEditable';
    case 'notSelectable':
      return 'massDelete.errors.targetNotSelectable';
    case 'unavailable':
      return 'massDelete.errors.targetCheckUnavailable';
  }
}

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

/** A confirmed delete, or a restore this panel's own button tried to start, that did not run, and
 *  why — shown until the next attempt. `leadKey` says what happened, `reasonKey` why. Shared by
 *  both: the restore entry's pre-check (spec E16, 4.6 point 22) has no banner of its own, and the
 *  panel's existing abort notice is where the plan puts it (Plan-253 §6, Nr. 3). */
interface DeleteAbortNotice {
  leadKey: string;
  reasonKey: string;
  /** Extra transloco interpolation params for `reasonKey` (e.g. a count for a plural reason) —
   *  omitted for every reason that needs none, which the template folds onto `{}`. */
  reasonParams?: Record<string, unknown>;
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
            arbiter.activeRun() !== null ||
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
      <!-- A confirmed delete that the host's lock stopped at the last moment (see startDelete): the
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
   * setting both would simply mean the same read fires from the same `wantsLiveAliasRead` check
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
  /** Only for `filterAlreadyPresent`'s direct read against 7TV (#149 P1 fix) — every other read in
   *  this component goes through `emoteAdminService`. */
  private readonly httpClient = inject(HttpClient);
  private readonly dialog = inject(Dialog);
  private readonly destroyRef = inject(DestroyRef);
  /** Only for the missing-row abort reason's "and N more" tail (#227 P2-c) — every other string in
   *  this component goes through the template's own `TranslocoPipe`. */
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
   *  a set switch, an emptied selection or a failed live alias read — or `null`. Shown until the
   *  next attempt.
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
   *  `readLiveAliasesFromSet`, see `wantsLiveAliasRead`) — the delete button stays disabled
   *  meanwhile, so a second click cannot open a second confirmation for the same selection. */
  protected readonly liveAliasReadPending = signal(false);

  /** The shared pre-check (spec 4.6 point 20, AK 31) is out for the delete confirmation about to
   *  open — the delete button stays disabled meanwhile, same idiom as `liveAliasReadPending`, so a
   *  second click cannot start the check twice or open a second confirmation once it answers. */
  protected readonly deleteTargetCheckPending = signal(false);
  private destroyed = false;

  /** Whether the current run's protocol was downloaded at least once — drives the reminder next
   *  to Close, since reset() leaves the file as the only artifact. */
  protected readonly protocolSaved = signal(false);

  /** Live slot view for the restore-confirm dialog, loaded when that dialog opens. */
  private readonly restoreSlots = signal<RestoreSlotPreview>(null);

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
    // Same shape, same reason, for the mutual-exclusion contract (design doc §4.3): the button is
    // disabled while any of the three 7TV-writing runs holds the arbiter, and this catches the
    // click that outraces such a run starting elsewhere on the page. Silent, like the lock guard
    // above — nothing has been confirmed yet, and the run that got there first is already visible
    // in the dock. The re-check in `startDelete` is what covers the far side of the dialog.
    if (this.arbiter.activeRun() !== null) {
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

  /** The restore entry at the finished delete run (spec E16, 4.6 point 22): the pre-check runs
   *  first, like every other first mutation (E19) — in the normal case a cache hit, because the
   *  delete's own report just warmed the target list for this very set. A block shows the panel's
   *  abort notice with a restore-specific lead line and the `restore.errors.*` reason family
   *  (Plan-253 §6, Nr. 3); nothing opens, nothing is sent to 7TV.
   *
   *  `timeout`/`error` and `takeUntilDestroyed` mirror `openConfirmDialog`'s own pre-check exactly
   *  (review round 1, finding 4): before this fix the subscription had no `error` branch at all, so
   *  a failed request (429, 503, no connection — spec F3) surfaced nothing and silently left the
   *  restore entry inert; a hung one left it inert forever; and a late answer after this panel was
   *  torn down could still have opened a confirmation nobody could see or answer. */
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

    this.emoteSetService
      .resolveEditableSet(run.setId)
      .pipe(timeout(LIVE_ALIAS_READ_TIMEOUT_MS), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (resolution) => {
          if (resolution.status !== 'editable') {
            this.abortNotice.set({
              leadKey: 'restore.nothingRestored',
              reasonKey: restoreTargetCheckReasonKey(resolution.status),
            });
            return;
          }
          // The panel's own inputs are the host fields (spec 6.3): the page this restore starts
          // from, not the delete run's frozen channel — a restore into a non-active or foreign set
          // must still be attributed to whichever page the button was clicked on (E13, E21).
          const target: ResolvedRestoreTarget = {
            ...resolution.target,
            hostChannelName: this.channelName(),
            hostSelectedSetId: this.setId(),
          };
          if (!this.tokenService.hasToken()) {
            openSevenTvTokenPromptDialog(this.dialog).closed.subscribe((saved) => {
              if (saved) {
                this.openRestoreConfirmDialog(target, doneItems);
              }
            });
            return;
          }
          this.openRestoreConfirmDialog(target, doneItems);
        },
        // 429, 503, no connection, or a timeout: "cannot be checked right now", never "not
        // allowed" (F3) — the same distinction `openConfirmDialog`'s own pre-check makes.
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
    doneItems: readonly RunQueueItem[],
  ): void {
    // Live slot view, so the projection line pops in once the check answers (the dialog is
    // already open by then) — same pattern as the delete confirm's shared-set warning. The read
    // itself is `loadRestoreSlotPreview`, the fork this shares with `restore-flow.ts`'s
    // `startRestoreFlow` (spec 4.3, point 8 / spec 8.3, final fix wave A5).
    this.restoreSlots.set(null);
    loadRestoreSlotPreview(
      { emoteAdminService: this.emoteAdminService, emoteSetService: this.emoteSetService },
      target,
    ).subscribe((preview) => this.restoreSlots.set(preview));

    const data: RestoreConfirmDialogData = {
      names: doneItems.map((item) => item.name),
      // spec #200, 7.2: ADDs, not rows — a #74 duplicate cell's row carries every alias it sat
      // under and restores once per alias.
      addCount: doneItems.reduce((sum, item) => sum + (item.aliases?.length ?? 1), 0),
      slots: this.restoreSlots.asReadonly(),
      setName: target.setName,
      isActiveSet: target.isActiveSet,
      emoteSetId: target.emoteSetId,
      ownerDisplayName: target.ownerDisplayName,
      trackedChannelName: target.trackedChannelName,
      // Spec E21: the run's set against the page's *selected* set — a different set of the same
      // channel, and a page with no selection, both count as foreign.
      foreignToView: target.emoteSetId !== target.hostSelectedSetId,
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
      // present only under some of its own aliases re-adds just the missing ones, and an alias
      // another emote now holds is left out rather than sent into a certain name conflict — see
      // `filterAlreadyPresentForRestore`.
      filterAlreadyPresentForRestore(this.httpClient, target.emoteSetId, emotes).subscribe(
        ({ rows: toRestore, skipped, skippedNameTaken, available }) => {
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
            restoreStartTarget(target),
            toRestore,
            skipped,
            available,
            skippedNameTaken,
          );
        },
      );
    });
  }

  /** The shared pre-check (spec 4.2, 6.2, E19), before the delete confirmation ever opens (spec 4.6
   *  point 20, AK 31) — in the normal case a cache hit, because the page's own set view or the
   *  target picker already warmed the target list this minute. A block shows the panel's existing
   *  abort notice (`massDelete.nothingDeleted` + `massDelete.errors.*`); no dialog opens, no
   *  request reaches 7TV.
   *
   *  `checkedSetId` is read once, here, and threaded through to {@link openConfirmDialogAfterCheck}
   *  rather than that method re-reading the live `setId()` input: the request can take a moment
   *  (a cache miss), and a set switch landing in that window must not let the confirmation open
   *  for whatever set happens to be selected once the answer arrives — only for the one the answer
   *  actually vouches for (review round 1, finding 3a). A genuine switch still surfaces, just later
   *  and visibly: `abortReasonBeforeStart` already compares the live `setId()` against this same
   *  frozen value once the dialog itself closes.
   *
   *  `timeout` (same budget as the live alias read, `LIVE_ALIAS_READ_TIMEOUT_MS`) keeps a hung
   *  request from leaving the delete button disabled forever — a timeout lands in the `error`
   *  branch like any other failed check, i.e. `unavailable` (review round 1, finding 3b).
   *  `takeUntilDestroyed` drops a late answer once this panel is gone, so a torn-down component
   *  never opens a dialog nobody can see or answer (review round 1, finding 3c). */
  private openConfirmDialog(): void {
    const checkedSetId = this.setId();
    this.deleteTargetCheckPending.set(true);
    this.emoteSetService
      .resolveEditableSet(checkedSetId)
      .pipe(timeout(LIVE_ALIAS_READ_TIMEOUT_MS), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (resolution) => {
          this.deleteTargetCheckPending.set(false);
          if (resolution.status !== 'editable') {
            this.abortNotice.set({
              leadKey: 'massDelete.nothingDeleted',
              reasonKey: deleteTargetCheckReasonKey(resolution.status),
            });
            return;
          }
          this.openConfirmDialogAfterCheck(checkedSetId);
        },
        // 429, 503, no connection, or a timeout: "cannot be checked right now", never "not
        // allowed" (F3) — the same distinction `FileImportStep`'s own pre-check makes.
        error: () => {
          this.deleteTargetCheckPending.set(false);
          this.abortNotice.set({
            leadKey: 'massDelete.nothingDeleted',
            reasonKey: deleteTargetCheckReasonKey('unavailable'),
          });
        },
      });
  }

  private openConfirmDialogAfterCheck(checkedSetId: string): void {
    this.setWarning.set(null);
    this.warningLoading.set(true);

    // The dialog is already open while this runs — it reads the panel's signals live (see
    // DeleteConfirmDialogData), so the shared-set warning pops in as soon as the check answers.
    this.loadSetWarning();

    // `checkedSetId` (the pre-check's own argument, `openConfirmDialog`), not the live `setId()`
    // input at this later moment: the dialog outlives the view it was opened on, and a
    // `channel.synced` set switch can move the host's selected set (and thus this input) while it
    // is still open — or even while the pre-check request itself was still out (review round 1,
    // finding 3a). Passed into `startDelete` so it can compare against the live value and abort
    // rather than delete into whatever set happens to be selected once the dialog closes (#200 K5
    // finding A). `frozenIsActiveSet` follows the same rule — computed against `checkedSetId`
    // rather than `this.isActiveSet()` (which reads the live `setId()`), so it never claims a
    // set is active that was not the one actually checked.
    const frozenSetId = checkedSetId;
    const activeSetId = this.effectiveActiveSetId();
    const frozenIsActiveSet = activeSetId !== null && activeSetId === checkedSetId;
    // Same reasoning, same moment, for the run's channel (K5 fix round item 7): the panel's own
    // `deleteService.startDelete` call used to read the live `channelName()` input instead, which
    // just happens to be stable in production (a panel only ever sees one channel across a run's
    // lifetime) but was the wrong source of truth all the same — the same class of gap finding A
    // closed for `setId`.
    const frozenChannelName = this.channelName();
    const data: DeleteConfirmDialogData = {
      emotes: this.visibleSelectedEmoteNames,
      hiddenEmotes: this.hiddenSelectedEmoteNames,
      warning: this.setWarning.asReadonly(),
      warningLoading: this.warningLoading.asReadonly(),
      setName: this.setName() ?? frozenSetId,
      isActiveSet: frozenIsActiveSet,
    };
    // Claimed from the moment the confirmation opens, not from the moment a read starts: the CDK
    // dialog is opened without a `viewContainerRef`, so it outlives this panel. A pushed reload that
    // prunes every marked key while the modal is up unmounts the host dock and destroys the panel
    // under it, the modal stays, the user clicks Delete — and the confirmed delete then runs its
    // checks against a torn-down component, which by contract starts nothing and has no view left to
    // say so on. Holding the dock for the whole life of the confirmation is what keeps that from
    // happening; the no-read branch, which never had a claim at all, is covered by the same move.
    // Every exit below releases it (`endConfirmedRun` after an attempt, `clearConfirmedRun` when
    // nothing was confirmed) — a leaked claim pins an empty dock.
    this.deleteService.beginConfirmedRun();
    openDeleteConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        this.deleteService.clearConfirmedRun();
        return;
      }
      // The exact list the dialog last showed, snapshotted **at confirm** and synchronously, before
      // anything asynchronous can run (operator decision 2026-09-22, amending the K5 fix round's
      // open-time freeze). The dialog renders the live `visibleSelectedEmoteNames`/
      // `hiddenSelectedEmoteNames`, both computed over this very input, so a pushed reload
      // (`channel.synced`, `usage.flushed` → `retainAmong`) that shrinks the selection behind the
      // open modal changes what is on screen — and an open-time snapshot would then delete emotes
      // the confirmation had already stopped naming. Reading the same signal the dialog rendered,
      // at the moment of the irreversible click, makes "what was shown" and "what is deleted" the
      // same list by construction. From here on the snapshot is what both branches act on: the
      // active-set delete's live alias read (`readLiveAliasesThenDelete`) is asynchronous and the
      // dialog is already closed while it is out, so an id deselected afterwards is still deleted
      // (it was confirmed) and an id selected afterwards is not swept in (it was never shown).
      // Unlike `frozenSetId`/`frozenIsActiveSet`/`frozenChannelName` above, which stay frozen at
      // **open** on purpose: those are compared against their live values here and abort the run on
      // a mismatch, which only works if they still say what the dialog was built from.
      // A defensive copy, not just a reference: `selectedEmotes()` is expected to be a fresh array
      // per host-page change already, but nothing here depends on that staying true.
      const confirmedSelection = [...this.selectedEmotes()];
      // The same reload can prune the selection down to nothing. Deleting the confirmed snapshot
      // then means deleting nothing at all, and `deleteService.startDelete` would refuse the empty
      // list silently — the one outcome this panel must never produce after a confirmed delete
      // (before the snapshot moved to confirm time, an emptied selection still started a doomed run
      // whose failed rows were at least visible). Said out loud instead, like every other
      // last-moment abort here.
      if (confirmedSelection.length === 0) {
        this.abortNotice.set({
          leadKey: 'massDelete.abortedByLock',
          reasonKey: 'massDelete.selectionGoneDuringConfirm',
        });
        // Not `clearConfirmedRun`: this exit has a notice to show, so it needs the window.
        this.deleteService.endConfirmedRun();
        return;
      }
      if (!this.wantsLiveAliasRead(frozenIsActiveSet)) {
        // `finally`, because a leaked claim pins an empty dock until the page is reloaded — a worse
        // outcome than whatever threw, and one nothing on screen could explain.
        try {
          this.startDelete(frozenSetId, frozenChannelName, confirmedSelection, null);
        } finally {
          this.deleteService.endConfirmedRun();
        }
        return;
      }
      // Owns the claim from here to the end of the read — see `readLiveAliasesThenDelete`.
      this.readLiveAliasesThenDelete(frozenSetId, frozenChannelName, confirmedSelection);
    });
  }

  /**
   * The live alias read (`readLiveAliasesFromActiveSet` or `readLiveAliasesFromSet`, decided by
   * `wantsLiveAliasRead`), at **confirm** time, not when the dialog opens: the delete confirmation
   * shows nothing alias-dependent (names only, one per cell), so reading earlier would buy no
   * correct number on screen — it would only spend a
   * read on every cancelled dialog and record aliases as they stood when the dialog opened rather
   * than at the irreversible moment. The frozen set id (`openConfirmDialog`) is what is read, and
   * `startDelete` repeats every confirm-time check once the answer is in, since the set can switch
   * while the read is out.
   */
  private readLiveAliasesThenDelete(
    frozenSetId: string,
    frozenChannelName: string,
    confirmedSelection: readonly DeletableEmote[],
  ): void {
    // The same checks `startDelete` makes, made once before the read as well: a delete that is
    // already doomed must not wait for (or spend) a 7TV read first.
    if (this.abortReasonBeforeStart(frozenSetId) !== undefined) {
      try {
        this.startDelete(frozenSetId, frozenChannelName, confirmedSelection, null);
      } finally {
        this.deleteService.endConfirmedRun();
      }
      return;
    }
    this.liveAliasReadPending.set(true);
    // The dock claim taken when the confirmation opened (`openConfirmDialog`) is held across this
    // read and released in the subscribe below — the read is the longest stretch in which a
    // confirmed delete exists without a run for the dock to see.
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
        // Released here rather than at the end of the `next` handler: `finalize` runs after that
        // handler on the completing path *and* on every other way out, so a throw inside
        // `startDelete` cannot leak the claim and pin an empty dock until the page is reloaded.
        // The service decides from its own `isRunning()` whether the dock still needs holding for
        // the abort notice or the run now carries it, so the ordering (after `startDelete`) is what
        // matters, not the call site.
        finalize(() => this.deleteService.endConfirmedRun()),
      )
      .subscribe((read) => {
        this.liveAliasReadPending.set(false);
        this.startDelete(frozenSetId, frozenChannelName, confirmedSelection, read);
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

  /** `frozenSetId`/`frozenChannelName` are what the dialog was built from — read once in
   *  `openConfirmDialog` and compared against their live inputs below, not re-read as the truth
   *  here (K5 fix round item 7). `confirmedSelection` is the list the dialog last *showed*,
   *  snapshotted in the `closed` callback at confirm time (operator decision 2026-09-22) — never
   *  the live `selectedEmotes()` input at this point, which an async live alias read can have let
   *  move on. `liveAliases` is the active-set delete's live alias read
   *  (`readLiveAliasesThenDelete`), or `null` when none was made. */
  private startDelete(
    frozenSetId: string,
    frozenChannelName: string,
    confirmedSelection: readonly DeletableEmote[],
    liveAliases: LiveAliasRead | null,
  ): void {
    const abort = this.abortReasonBeforeStart(frozenSetId);
    if (abort !== undefined) {
      this.abortNotice.set(abort);
      return;
    }
    if (liveAliases !== null && 'blockedReasonKey' in liveAliases) {
      this.abortNotice.set({
        leadKey: 'massDelete.nothingDeleted',
        reasonKey: liveAliases.blockedReasonKey,
      });
      return;
    }
    // Unconditional, on both paths (K5 fix round 2): the live alias read is the *longer* window in
    // which another run can claim the arbiter, not the only one — the confirmation itself is a
    // modal the user can leave open for minutes, and a run started from anywhere else on the page
    // lands just as well behind it. Qualifying this on `liveAliases !== null` left the no-read
    // branch relying on `deleteService.startDelete`'s own refusal, which is silent, so a confirmed
    // delete in a non-active view simply evaporated. Unlike the restore paths' identical re-check
    // (silent there — the run that got there first is always the one whose progress panel is
    // already mounted in *this* same dock), this abort is visible: the competing run can be any of
    // the three 7TV-writing kinds, started from anywhere on the page, and this panel's own dock
    // would otherwise show nothing at all to explain why a confirmed delete just vanished.
    if (this.arbiter.activeRun() !== null) {
      this.abortNotice.set({
        leadKey: 'massDelete.nothingDeleted',
        reasonKey: 'massDelete.anotherRunStarted',
      });
      return;
    }
    // The third way `deleteService.startDelete` can refuse without a word — the other two, a run
    // already going and an empty list, are caught above. The engine needs the stored 7TV token, and
    // any 401 from 7TV behind the open confirmation clears it (`SevenTvTokenService.clearToken`);
    // the dock claim of the commits above would then hold an empty dock over a delete that simply
    // never happened.
    if (!this.tokenService.hasToken()) {
      this.abortNotice.set({
        leadKey: 'massDelete.nothingDeleted',
        reasonKey: 'massDelete.tokenGoneDuringConfirm',
      });
      return;
    }
    const liveEntries = liveAliases?.entries;
    if (liveEntries !== undefined) {
      // A live read only ever reaches here complete (an incomplete one was already blocked above,
      // via `blockedReasonKey`) — so an id it does not know at all under either map means 7TV no
      // longer has it, not merely that it has no alias. Deleting such a row anyway would issue a
      // `RemoveEmote` for something that is not there: on the vote page specifically the exact
      // defect #227 point 2 forbids (a departed set-session member reaching the run), and equally a
      // bug for the active-set path this same read also serves. Fails the WHOLE batch, not just the
      // missing rows: a partial run would record a protocol that no longer matches what the
      // confirmation showed as a whole ("gezeigt = gelöscht", spec §8.3, K5 follow-up #229). The
      // reason names the missing rows (P2-c, Opus review) rather than only a count, and tells the
      // user to deselect exactly those and start again — not "reload", which on the usage page's own
      // legitimate normal case (an emote removed on 7TV directly, ahead of our periodic resync
      // noticing) would not help at all: our own database still shows the row as present until that
      // resync runs, so every confirmed selection containing it would keep failing the same way
      // regardless of how many times the page is reloaded.
      const missingRows = confirmedSelection.filter(
        (emote) =>
          !liveEntries.aliasesById.has(emote.sevenTvEmoteId) &&
          !liveEntries.aliaslessIds.has(emote.sevenTvEmoteId),
      );
      if (missingRows.length > 0) {
        this.abortNotice.set({
          leadKey: 'massDelete.nothingDeleted',
          reasonKey: pluralKey(missingRows.length, 'massDelete.memberRead.missingFromSet'),
          reasonParams: this.missingRowsReasonParams(missingRows.map((emote) => emote.name)),
        });
        return;
      }
    }
    const emotes: DeleteQueueEmote[] = confirmedSelection.map((emote) => {
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
    // The page's channel is the expected hit only when the run's set is its active one (spec 4.6
    // point 21); a non-active set's report is paper only and expects no channel.
    const expectedChannelName =
      frozenSetId === this.effectiveActiveSetId() ? frozenChannelName : null;
    this.deleteService.startDelete(frozenSetId, frozenChannelName, emotes, expectedChannelName);
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

  /** Whether a confirmed delete reads its target set live from 7TV before it starts (#227) — either
   *  opt-in that applies: the active-set one only for the set this delete is actually targeting
   *  (`frozenIsActiveSet`), the vote page's own-set one unconditionally. */
  private wantsLiveAliasRead(frozenIsActiveSet: boolean): boolean {
    return (
      (frozenIsActiveSet && this.readLiveAliasesFromActiveSet()) || this.readLiveAliasesFromSet()
    );
  }

  /** Comma-joined, capped names for the missing-row abort reason (#227 P2-c, Opus review). A bare
   *  count told the user nothing they could act on — this run is blocked outright ("gezeigt =
   *  gelöscht" stays the rule, spec §8.3), so the way forward is deselecting exactly these rows and
   *  starting again, which needs their names, not just how many. `PREVIEW_CAP`/the "and N more" tail
   *  are the identical ones `NamePreviewList` uses for the same "many names" problem in a dialog —
   *  reused here rather than a second threshold, just rendered as one line of status text instead of
   *  a scrollable list, since the abort notice has no dialog to put a list into. */
  private missingRowsReasonParams(missingNames: readonly string[]): Record<string, unknown> {
    const preview = missingNames.slice(0, PREVIEW_CAP);
    const remaining = missingNames.length - preview.length;
    const joined = preview.join(', ');
    if (remaining <= 0) {
      return { names: joined };
    }
    const tail = this.translocoService.translate(pluralKey(remaining, 'common.andMore'), {
      count: remaining,
    });
    return { names: `${joined} ${tail}` };
  }
}
