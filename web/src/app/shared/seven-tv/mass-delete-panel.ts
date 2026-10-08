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
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { unknownCount } from '../../core/seven-tv/seven-tv-run-settlement';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { Button } from '../ui/button';
import {
  DeleteAbortNotice,
  DeleteFlowDeps,
  DeleteFlowRequest,
  startDeleteFlow,
} from './delete-flow';
import { DeleteProgressSection } from './delete-progress-section';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/** Per-instance suffix for the lock reason's element id — the panel renders on two pages, and an
 *  `aria-describedby` target has to be unique in the document. */
let nextDeleteLockReasonId = 0;

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
 * Since A6 it also shows the run's paper trail: the post-run summary offers the protocol as a
 * download (the file is the restore list), and "restore" re-adds the deleted emotes over the
 * restore service's own engine run — both browser-side, the 7TV token never leaves it. Since #201
 * T-A that whole run surface — progress, protocol, unclear rows, the restore entry and its chain —
 * is `DeleteProgressSection`, mounted in this template where it always sat; the panel keeps the
 * button, its locks, the delete flow's first steps, the status pair and the two run latches.
 */
@Component({
  selector: 'app-mass-delete-panel',
  imports: [Button, DeleteProgressSection, TranslocoPipe],
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

      <!-- The run surface (#201 T-A): progress, protocol, unclear rows and the restore entry. It
           renders nothing without a run or queue, and its host is display: contents, so it adds no
           gap here. A blocked restore reports through (notice) into the status pair above. -->
      <app-delete-progress-section
        [hostSelectedSetId]="setId()"
        (notice)="abortNotice.set($event)"
      />
    </div>
  `,
})
export class MassDeletePanel {
  readonly setId = input.required<string>();
  readonly channelName = input.required<string>();
  /** The channel's active 7TV set — `null` when the host knows it does not exist or could not be
   *  read (a *known* unknown), `undefined` when the host simply never bound this input at all.
   *  Gates `isActiveSet` below for both confirmations (spec #200, 8.8) and which slot-preview
   *  source the restore confirmation reads (`DeleteProgressSection`): the active set's cheap,
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
  /** Read only by the restore latch in the constructor — the restore entry itself lives in
   *  `DeleteProgressSection`. */
  private readonly restoreService = inject(SevenTvRestoreService);
  /** Read here only for the four start-site checks below — the template needs it too, hence
   *  `protected` rather than `private` (#70, Task 4; see docs/DECISIONS.md). */
  protected readonly arbiter = inject(SevenTvRunArbiter);
  private readonly emoteAdminService = inject(EmoteAdminService);
  /** Only handed to the delete flow (`DeleteFlowDeps`) — its shared pre-check
   *  (`resolveEditableSet`, spec 4.6 point 20). */
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  /** Only handed to the delete flow, for its direct read against 7TV (the live alias read) —
   *  every other read goes through `emoteAdminService`. */
  private readonly httpClient = inject(HttpClient);
  private readonly dialog = inject(Dialog);
  private readonly destroyRef = inject(DestroyRef);
  /** Only handed to the delete flow, for the refused-start notice's kind noun and the missing-row
   *  abort reason's "and N more" tail (#227 P2-c) — every other string in this component goes
   *  through the template's own `TranslocoPipe`. */
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
    // quiet the same way `DeleteProgressSection.openRestoreConfirm`'s own pre-dialog guard does.
    // The re-check in the delete flow (`delete-flow.ts`) is what covers the far side of the dialog,
    // and it does show a reason (#256 T4).
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
}
