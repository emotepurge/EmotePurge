import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { DestroyRef, Signal, WritableSignal, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoService } from '@jsverse/transloco';
import { Observable, catchError, finalize, map, of, timeout } from 'rxjs';

import { EmoteAdminService, EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import {
  DeleteQueueEmote,
  SevenTvDeleteService,
} from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import {
  refusedStartMessage,
  SevenTvRunArbiter,
  SevenTvRunClaim,
} from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvSetEntries, loadSevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { TargetCheckBlockReason } from '../../core/seven-tv/sync-report-outcome';
import { PREVIEW_CAP } from '../ui/name-preview-list';
import { DeleteConfirmDialogData, openDeleteConfirmDialog } from './delete-confirm-dialog';
import type { DeletableEmote } from './mass-delete-panel';

/*
 * The delete confirmation's pre-check chain, extracted from `MassDeletePanel` without a behaviour
 * change (#201 T-A): shared pre-check → confirmation → live alias read → start, with every freeze,
 * check and abort exactly where the panel had it.
 *
 * The panel keeps the steps **before** the set check — clearing its abort notice, the host-lock and
 * `startLocked` guards, the token prompt — because they hang off its own button, and calls
 * `startDeleteFlow` once those have passed.
 *
 * `resolveDeleteTarget` and `readLiveSetAliases` are exported on their own, and the chain below uses
 * those very functions rather than a second copy: the tag page's clear-out run (#201 T-C) needs the
 * same two checks in front of a confirmation dialog of its **own**.
 */

/** Maps the shared pre-check's block reason (spec 6.2, `TargetCheckBlockReason`) for the delete
 *  confirmation's own pre-check before it opens (spec 4.6 point 20, AK 31) — `massDelete.errors.*`,
 *  the panel's own family for the delete, never `restore.errors.*` (that one names the *restore*
 *  entry at a finished run, a different first mutation with its own copy). `notSelectable` cannot
 *  actually occur in production here — the delete panel is always given a set the host already
 *  resolved as `NORMAL` — but the mapping stays total rather than assuming that at the type level,
 *  the same discipline the panel's `restoreTargetCheckReasonKey` keeps. */
export function deleteTargetCheckReasonKey(reason: TargetCheckBlockReason): string {
  switch (reason) {
    case 'notEditable':
      return 'massDelete.errors.targetNotEditable';
    case 'notSelectable':
      return 'massDelete.errors.targetNotSelectable';
    case 'unavailable':
      return 'massDelete.errors.targetCheckUnavailable';
  }
}

/** Dedicated reason keys for the one case the delete blocks by itself: the target set's live
 *  entries, read right before a delete, could not be read or came back incomplete — a list that
 *  only knows half must not delete (spec #200, 8.3's rule, applied here). K5 fix round: these used
 *  to reuse the usage page's own `usageStats.setView.lock.*` texts verbatim, but those say "Deleting
 *  and voting are locked: …" — correct for the sticky lock paragraph they were written for, wrong
 *  here, where this is a one-off, transient abort notice ("Nothing was deleted." + reason), not a
 *  standing lock description. */
export const MEMBER_READ_UNAVAILABLE_REASON_KEY = 'massDelete.memberRead.unavailable';
export const MEMBER_READ_TRUNCATED_REASON_KEY = 'massDelete.memberRead.truncated';

/** Total time budget for the delete's live alias read (K5 fix round) — a hung request (7TV accepts
 *  the connection but never answers) used to leave `liveAliasReadPending` `true` forever, with the
 *  delete button disabled and no way out short of reloading the page. Generous for a
 *  same-origin-adjacent GraphQL call reading at most 10 pages of up to 500 entries each; a timeout
 *  is treated exactly like any other failed read — nothing is deleted, and the reason is shown. The
 *  shared pre-check and the panel's restore entry use the same budget. */
export const LIVE_ALIAS_READ_TIMEOUT_MS = 20_000;

/** A confirmed delete, or a restore the panel's own button tried to start, that did not run, and
 *  why — shown until the next attempt. `leadKey` says what happened, `reasonKey` why. Shared by
 *  both: the restore entry's pre-check (spec E16, 4.6 point 22) has no banner of its own, and the
 *  panel's existing abort notice is where the plan puts it (Plan-253 §6, Nr. 3). */
export interface DeleteAbortNotice {
  leadKey: string;
  reasonKey: string;
  /** Extra transloco interpolation params for `reasonKey` (e.g. a count for a plural reason) —
   *  omitted for every reason that needs none, which the template folds onto `{}`. */
  reasonParams?: Record<string, unknown>;
}

/** What {@link resolveDeleteTarget} answers: the set may be deleted from, with the owner id the
 *  shared pre-check resolved (frozen by the caller for the confirmation, the run and its protocol),
 *  or it is blocked — `reason` is the pre-check's own outcome, `reasonKey` the delete's
 *  `massDelete.errors.*` text for it ({@link deleteTargetCheckReasonKey}). A failed or timed-out
 *  check is `unavailable`: "cannot be checked right now", never "not allowed" (F3). */
export type DeleteTargetResolution =
  | { status: 'editable'; ownerTwitchChannelId: string | null }
  | { status: 'blocked'; reason: TargetCheckBlockReason; reasonKey: string };

/** What {@link readLiveSetAliases} answers: the set's complete live entries, or the translation key
 *  of the reason a delete must not start — an incomplete read
 *  ({@link MEMBER_READ_TRUNCATED_REASON_KEY}) or a failed/timed-out one
 *  ({@link MEMBER_READ_UNAVAILABLE_REASON_KEY}). */
export type LiveAliasReadResult =
  { status: 'ok'; entries: SevenTvSetEntries } | { status: 'blocked'; reasonKey: string };

/**
 * Everything the flow needs, handed in rather than injected — same reasoning as `RestoreFlowDeps`
 * (see there): the flow opens dialogs from `shared/` and drives services from `core/`, and holds no
 * state between calls.
 */
export interface DeleteFlowDeps {
  dialog: Dialog;
  /** The confirmation's shared-set warning (`getSetWarning`). */
  emoteAdminService: EmoteAdminService;
  /** The shared pre-check (`resolveEditableSet`, spec 6.2). */
  emoteSetService: SevenTvEmoteSetService;
  /** Only for the live alias read (`loadSevenTvSetEntries`), a direct read against 7TV. */
  httpClient: HttpClient;
  tokenService: SevenTvTokenService;
  deleteService: SevenTvDeleteService;
  arbiter: SevenTvRunArbiter;
  /** Only for the missing-row abort reason's "and N more" tail (#227 P2-c) and the refused-start
   *  notice's kind noun — every other string goes through the caller's own template. */
  translocoService: TranslocoService;
  /** The caller's own teardown: the shared pre-check is dropped with it (a torn-down caller never
   *  opens a dialog nobody can see or answer), and a confirmed delete whose caller is gone starts
   *  nothing (`abortReasonBeforeStart`). */
  destroyRef: DestroyRef;
}

/**
 * Everything the chain reads from its caller — as signals, so it re-reads or freezes them at
 * exactly the points the panel did.
 */
export interface DeleteFlowRequest {
  /** The set the delete targets — the page's *selected* set. Frozen at the pre-check, and compared
   *  live against that frozen value after it, after the confirmation and after the live read. */
  setId: Signal<string>;
  /** The channel's active set, with an omitted host input already folded onto `setId()` (the
   *  panel's `effectiveActiveSetId`); `null` is a *known* unknown. */
  activeSetId: Signal<string | null>;
  channelName: Signal<string>;
  /** The set's display name for the confirmation, `null` to fall back to the set id. */
  setName: Signal<string | null>;
  /** The live selection — snapshotted at confirm time, never earlier. */
  selectedEmotes: Signal<readonly DeletableEmote[]>;
  /** The confirmation's two name lists, split by `hidden` (Konzept "Auswahl überlebt Suche und
   *  Filter" 2.1). Owned by the caller and handed to the dialog as they are, so the dialog renders
   *  the caller's very signals live. */
  visibleEmoteNames: Signal<string[]>;
  hiddenEmoteNames: Signal<string[]>;
  /** The host page's own lock reason (spec #200, 8.3), `null` for no lock — re-read at confirm
   *  time. */
  hostLockReasonKey: Signal<string | null>;
  /** Whether a confirmed delete reads its target set live from 7TV before it starts (#227):
   *  `'activeSet'` only when the set it targets is the active one, `'set'` unconditionally. */
  liveAliasRead: Signal<'none' | 'activeSet' | 'set'>;
  /** The caller's abort notice — every block and abort of the chain lands here. */
  notice: WritableSignal<DeleteAbortNotice | null>;
  /** `true` while the shared pre-check is out — the caller disables its button meanwhile, so a
   *  second click cannot start the check twice or open a second confirmation once it answers. */
  targetCheckPending: WritableSignal<boolean>;
}

/**
 * The shared pre-check (spec 4.2, 6.2, E19) for a delete from `setId` — in the normal case a cache
 * hit, because the page's own set view or the target picker already warmed the target list this
 * minute. Owner-hint design 3.6, third row: the caller knows only its own channel's login — every
 * set it shows belongs to that one account (spec 6.1).
 *
 * `timeout` (same budget as the live alias read, `LIVE_ALIAS_READ_TIMEOUT_MS`) keeps a hung request
 * from leaving the caller's button disabled forever — a timeout lands like any other failed check
 * (429, 503, no connection), i.e. `unavailable`: "cannot be checked right now", never "not allowed"
 * (F3) — the same distinction `FileImportStep`'s own pre-check makes (review round 1, finding 3b).
 *
 * Whether the caller's selected set moved on while this was out is the caller's own question, not
 * this function's — it only answers for the `setId` it was given.
 */
export function resolveDeleteTarget(
  deps: Pick<DeleteFlowDeps, 'emoteSetService'>,
  setId: string,
  channelName: string,
): Observable<DeleteTargetResolution> {
  return deps.emoteSetService
    .resolveEditableSet(setId, { twitchChannelId: null, twitchLogin: channelName })
    .pipe(
      timeout(LIVE_ALIAS_READ_TIMEOUT_MS),
      map((resolution): DeleteTargetResolution =>
        resolution.status === 'editable'
          ? { status: 'editable', ownerTwitchChannelId: resolution.target.ownerTwitchChannelId }
          : blockedTarget(resolution.status),
      ),
      catchError(() => of(blockedTarget('unavailable'))),
    );
}

/**
 * The live alias read of `setId` a confirmed delete makes right before it starts (#227, operator
 * decision 2026-09-22): every entry the one `REMOVE` per emote will take, so the protocol records
 * every alias. A failed or incomplete read blocks — "a list that only knows half must not delete"
 * (spec 8.3).
 */
export function readLiveSetAliases(
  httpClient: HttpClient,
  setId: string,
): Observable<LiveAliasReadResult> {
  return loadSevenTvSetEntries(httpClient, setId).pipe(
    // A hung request (7TV accepts the connection but never answers) must not leave the button
    // disabled forever — treated exactly like any other failed read (K5 fix round item 5).
    timeout(LIVE_ALIAS_READ_TIMEOUT_MS),
    map((entries): LiveAliasReadResult =>
      entries.complete
        ? { status: 'ok', entries }
        : { status: 'blocked', reasonKey: MEMBER_READ_TRUNCATED_REASON_KEY },
    ),
    catchError(() =>
      of<LiveAliasReadResult>({
        status: 'blocked',
        reasonKey: MEMBER_READ_UNAVAILABLE_REASON_KEY,
      }),
    ),
  );
}

/**
 * Runs the delete's chain from the shared pre-check on: pre-check → confirmation → (live alias
 * read) → `deleteService.startDelete`. A block shows the caller's abort notice
 * (`massDelete.nothingDeleted` + `massDelete.errors.*`); no dialog opens, no request reaches 7TV.
 *
 * `checkedSetId` is read once, here, and threaded through rather than re-reading the live `setId()`
 * later: the request can take a moment (a cache miss), and a set switch landing in that window must
 * not let the confirmation open for whatever set happens to be selected once the answer arrives —
 * only for the one the answer actually vouches for (review round 1, finding 3a). A genuine switch
 * still surfaces, just later and visibly: `abortReasonBeforeStart` compares the live `setId()`
 * against this same frozen value once the dialog itself closes.
 *
 * `takeUntilDestroyed` drops a late answer once the caller is gone, so a torn-down component never
 * opens a dialog nobody can see or answer (review round 1, finding 3c).
 */
export function startDeleteFlow(deps: DeleteFlowDeps, request: DeleteFlowRequest): void {
  const checkedSetId = request.setId();
  request.targetCheckPending.set(true);
  resolveDeleteTarget(deps, checkedSetId, request.channelName())
    .pipe(takeUntilDestroyed(deps.destroyRef))
    .subscribe((resolution) => {
      request.targetCheckPending.set(false);
      if (resolution.status !== 'editable') {
        request.notice.set({
          leadKey: 'massDelete.nothingDeleted',
          reasonKey: resolution.reasonKey,
        });
        return;
      }
      // Codex C3 (final fix wave A6): a set switch that lands while the check was still out used to
      // be caught only once the dialog closed (`abortReasonBeforeStart`), which left a confirmation
      // open for a set the host was no longer looking at. Caught here instead, before the dialog
      // ever opens — same lead/reason pair `abortReasonBeforeStart` already uses for the
      // settled-switch case it still covers.
      if (request.setId() !== checkedSetId) {
        request.notice.set({
          leadKey: 'massDelete.abortedByLock',
          reasonKey: 'massDelete.setChangedDuringConfirm',
        });
        return;
      }
      // Frozen from this very answer (owner-hint design, Codex finding 1's own "never re-resolve"
      // spirit): the confirmation, the run it starts and the purge protocol all carry this one
      // resolved owner id, never a later re-check's.
      openConfirmDialogAfterCheck(deps, request, checkedSetId, resolution.ownerTwitchChannelId);
    });
}

/** The caller's abort notice for a confirmed start the arbiter refused (#256 contract P2,
 *  Festlegung Nr. 8) — the shared `sevenTvRun.notStarted.*` family with the blocking kind's own,
 *  already-translated noun, the same wording `usage-stats-page.ts`'s transient region shows for
 *  the two flows that have no notice of their own. Every call site reads `activeClaim()` itself,
 *  right before this, and only calls this once it is non-null. Deliberately does **not** call
 *  `SevenTvRunArbiter.noteRefusedStart()`: the panel's `abortNotice` is already the visible,
 *  persistent explanation for as long as the panel stays mounted, so routing the same refusal
 *  through the arbiter's own 4-second transient notice too would announce it twice on a page that
 *  mounts both (`usage-stats-page.html`). Used by the delete chain here and by the panel's restore
 *  entry. */
export function refusedStartNotice(
  translocoService: TranslocoService,
  claim: SevenTvRunClaim,
  leadKey: string,
): DeleteAbortNotice {
  const { messageKey, kind } = refusedStartMessage(claim, (key) => translocoService.translate(key));
  return { leadKey, reasonKey: messageKey, reasonParams: { kind } };
}

function blockedTarget(reason: TargetCheckBlockReason): DeleteTargetResolution {
  return { status: 'blocked', reason, reasonKey: deleteTargetCheckReasonKey(reason) };
}

function openConfirmDialogAfterCheck(
  deps: DeleteFlowDeps,
  request: DeleteFlowRequest,
  checkedSetId: string,
  frozenOwnerTwitchId: string | null,
): void {
  const setWarning = signal<EmoteSetWarning | null>(null);
  const warningLoading = signal(true);

  // The dialog is already open while this runs — it reads these signals live (see
  // DeleteConfirmDialogData), so the shared-set warning pops in as soon as the check answers.
  loadSetWarning(deps, request, setWarning, warningLoading);

  // `checkedSetId` (the pre-check's own argument, `startDeleteFlow`), not the live `setId()` at this
  // later moment: the dialog outlives the view it was opened on, and a `channel.synced` set switch
  // can move the host's selected set (and thus this signal) while it is still open — or even while
  // the pre-check request itself was still out (review round 1, finding 3a). Passed into
  // `startDelete` so it can compare against the live value and abort rather than delete into
  // whatever set happens to be selected once the dialog closes (#200 K5 finding A).
  // `frozenIsActiveSet` follows the same rule — computed against `checkedSetId` rather than the live
  // `setId()`, so it never claims a set is active that was not the one actually checked.
  const frozenSetId = checkedSetId;
  const activeSetId = request.activeSetId();
  const frozenIsActiveSet = activeSetId !== null && activeSetId === checkedSetId;
  // Same reasoning, same moment, for the run's channel (K5 fix round item 7): the panel's own
  // `deleteService.startDelete` call used to read the live `channelName()` input instead, which
  // just happens to be stable in production (a panel only ever sees one channel across a run's
  // lifetime) but was the wrong source of truth all the same — the same class of gap finding A
  // closed for `setId`.
  const frozenChannelName = request.channelName();
  const data: DeleteConfirmDialogData = {
    emotes: request.visibleEmoteNames,
    hiddenEmotes: request.hiddenEmoteNames,
    warning: setWarning.asReadonly(),
    warningLoading: warningLoading.asReadonly(),
    setName: request.setName() ?? frozenSetId,
    isActiveSet: frozenIsActiveSet,
  };
  // Claimed from the moment the confirmation opens, not from the moment a read starts: the CDK
  // dialog is opened without a `viewContainerRef`, so it outlives the caller. A pushed reload that
  // prunes every marked key while the modal is up unmounts the host dock and destroys the panel
  // under it, the modal stays, the user clicks Delete — and the confirmed delete then runs its
  // checks against a torn-down component, which by contract starts nothing and has no view left to
  // say so on. Holding the dock for the whole life of the confirmation is what keeps that from
  // happening; the no-read branch, which never had a claim at all, is covered by the same move.
  // Every exit below releases it (`endConfirmedRun` after an attempt, `clearConfirmedRun` when
  // nothing was confirmed) — a leaked claim pins an empty dock.
  deps.deleteService.beginConfirmedRun();
  openDeleteConfirmDialog(deps.dialog, data).closed.subscribe((confirmed) => {
    if (!confirmed) {
      deps.deleteService.clearConfirmedRun();
      return;
    }
    // The exact list the dialog last showed, snapshotted **at confirm** and synchronously, before
    // anything asynchronous can run (operator decision 2026-09-22, amending the K5 fix round's
    // open-time freeze). The dialog renders the live `visibleEmoteNames`/`hiddenEmoteNames`, both
    // computed over this very selection, so a pushed reload (`channel.synced`, `usage.flushed` →
    // `retainAmong`) that shrinks the selection behind the open modal changes what is on screen —
    // and an open-time snapshot would then delete emotes the confirmation had already stopped
    // naming. Reading the same signal the dialog rendered, at the moment of the irreversible click,
    // makes "what was shown" and "what is deleted" the same list by construction. From here on the
    // snapshot is what both branches act on: the live alias read (`readLiveAliasesThenDelete`) is
    // asynchronous and the dialog is already closed while it is out, so an id deselected afterwards
    // is still deleted (it was confirmed) and an id selected afterwards is not swept in (it was
    // never shown). Unlike `frozenSetId`/`frozenIsActiveSet`/`frozenChannelName` above, which stay
    // frozen at **open** on purpose: those are compared against their live values here and abort
    // the run on a mismatch, which only works if they still say what the dialog was built from.
    // A defensive copy, not just a reference: `selectedEmotes()` is expected to be a fresh array
    // per host-page change already, but nothing here depends on that staying true.
    const confirmedSelection = [...request.selectedEmotes()];
    // The same reload can prune the selection down to nothing. Deleting the confirmed snapshot then
    // means deleting nothing at all, and `deleteService.startDelete` would refuse the empty list
    // silently — the one outcome this chain must never produce after a confirmed delete (before
    // the snapshot moved to confirm time, an emptied selection still started a doomed run whose
    // failed rows were at least visible). Said out loud instead, like every other last-moment
    // abort here.
    if (confirmedSelection.length === 0) {
      request.notice.set({
        leadKey: 'massDelete.abortedByLock',
        reasonKey: 'massDelete.selectionGoneDuringConfirm',
      });
      // Not `clearConfirmedRun`: this exit has a notice to show, so it needs the window.
      deps.deleteService.endConfirmedRun();
      return;
    }
    if (!wantsLiveAliasRead(request, frozenIsActiveSet)) {
      // `finally`, because a leaked claim pins an empty dock until the page is reloaded — a worse
      // outcome than whatever threw, and one nothing on screen could explain.
      try {
        startDelete(
          deps,
          request,
          frozenSetId,
          frozenChannelName,
          confirmedSelection,
          null,
          frozenOwnerTwitchId,
        );
      } finally {
        deps.deleteService.endConfirmedRun();
      }
      return;
    }
    // Owns the claim from here to the end of the read — see `readLiveAliasesThenDelete`.
    readLiveAliasesThenDelete(
      deps,
      request,
      frozenSetId,
      frozenChannelName,
      confirmedSelection,
      frozenOwnerTwitchId,
    );
  });
}

/**
 * The live alias read (`liveAliasRead`, decided by `wantsLiveAliasRead`), at **confirm** time, not
 * when the dialog opens: the delete confirmation shows nothing alias-dependent (names only, one per
 * cell), so reading earlier would buy no correct number on screen — it would only spend a read on
 * every cancelled dialog and record aliases as they stood when the dialog opened rather than at the
 * irreversible moment. The frozen set id (`startDeleteFlow`) is what is read, and `startDelete`
 * repeats every confirm-time check once the answer is in, since the set can switch while the read
 * is out.
 */
function readLiveAliasesThenDelete(
  deps: DeleteFlowDeps,
  request: DeleteFlowRequest,
  frozenSetId: string,
  frozenChannelName: string,
  confirmedSelection: readonly DeletableEmote[],
  frozenOwnerTwitchId: string | null,
): void {
  // The same checks `startDelete` makes, made once before the read as well: a delete that is
  // already doomed must not wait for (or spend) a 7TV read first.
  if (abortReasonBeforeStart(deps, request, frozenSetId) !== undefined) {
    try {
      startDelete(
        deps,
        request,
        frozenSetId,
        frozenChannelName,
        confirmedSelection,
        null,
        frozenOwnerTwitchId,
      );
    } finally {
      deps.deleteService.endConfirmedRun();
    }
    return;
  }
  // Aliased by the panel as `liveAliasReadPending` (#280): registered with the arbiter, the same
  // state also locks every other 7TV start trigger and is spoken by the page's
  // `DockOutcomeAnnouncer`.
  deps.deleteService.startCheckPending.set(true);
  // The dock claim taken when the confirmation opened (`openConfirmDialogAfterCheck`) is held across
  // this read and released below — the read is the longest stretch in which a confirmed delete
  // exists without a run for the dock to see. No `takeUntilDestroyed`, as before #280: a torn-down
  // caller's `startDelete` starts nothing by contract (`abortReasonBeforeStart`), and the read is
  // bounded, so its `finalize` always runs.
  readLiveSetAliases(deps.httpClient, frozenSetId)
    .pipe(
      // Released here rather than at the end of the `next` handler: `finalize` runs after that
      // handler on the completing path *and* on every other way out, so a throw inside
      // `startDelete` cannot leak the claim and pin an empty dock until the page is reloaded.
      // The service decides from its own `isRunning()` whether the dock still needs holding for
      // the abort notice or the run now carries it, so the ordering (after `startDelete`) is what
      // matters, not the call site.
      //
      // #280: the start check is released in the same place, for the same reason — and after
      // `startDelete`, whose run then holds the triggers through the arbiter. `startDelete`'s own
      // re-check reads `activeClaim`, never `startLocked`, which this very flag would still set.
      finalize(() => {
        deps.deleteService.startCheckPending.set(false);
        deps.deleteService.endConfirmedRun();
      }),
    )
    .subscribe((read) => {
      startDelete(
        deps,
        request,
        frozenSetId,
        frozenChannelName,
        confirmedSelection,
        read,
        frozenOwnerTwitchId,
      );
    });
}

function loadSetWarning(
  deps: DeleteFlowDeps,
  request: DeleteFlowRequest,
  setWarning: WritableSignal<EmoteSetWarning | null>,
  warningLoading: WritableSignal<boolean>,
): void {
  // Explicit `setId()` since K5 (spec 6.8): the delete target is the page's *selected* set, not
  // necessarily the channel's active one — the old implicit "check the active set" call would ask
  // the wrong question in a non-active view.
  deps.emoteAdminService.getSetWarning(request.channelName(), request.setId()).subscribe({
    next: (warning) => {
      setWarning.set(warning);
      warningLoading.set(false);
    },
    error: () => {
      // `available: false` is the signal the dialog acts on — it renders a neutral "couldn't
      // check" notice instead of the red "confirmed foreign set" alarm, so a failed check no
      // longer produces a false accusation. `isOwnSet` stays `false` deliberately: it is
      // meaningless while `available` is false, and should a future reader consume it without
      // checking `available`, the conservative direction ("not verified as ours") is the safe one.
      setWarning.set({
        available: false,
        isOwnSet: false,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      });
      warningLoading.set(false);
    },
  });
}

/** `frozenSetId`/`frozenChannelName` are what the dialog was built from — read once at the
 *  pre-check and compared against their live signals below, not re-read as the truth here (K5 fix
 *  round item 7). `confirmedSelection` is the list the dialog last *showed*, snapshotted in the
 *  `closed` callback at confirm time (operator decision 2026-09-22) — never the live
 *  `selectedEmotes()` at this point, which an async live alias read can have let move on.
 *  `liveAliases` is the live alias read (`readLiveAliasesThenDelete`), or `null` when none was
 *  made. `frozenOwnerTwitchId` is the pre-check's own resolved owner id (`startDeleteFlow`), frozen
 *  the same way — never re-resolved here. */
function startDelete(
  deps: DeleteFlowDeps,
  request: DeleteFlowRequest,
  frozenSetId: string,
  frozenChannelName: string,
  confirmedSelection: readonly DeletableEmote[],
  liveAliases: LiveAliasReadResult | null,
  frozenOwnerTwitchId: string | null,
): void {
  const abort = abortReasonBeforeStart(deps, request, frozenSetId);
  if (abort !== undefined) {
    request.notice.set(abort);
    return;
  }
  if (liveAliases?.status === 'blocked') {
    request.notice.set({
      leadKey: 'massDelete.nothingDeleted',
      reasonKey: liveAliases.reasonKey,
    });
    return;
  }
  // Unconditional, on both paths (K5 fix round 2): the live alias read is the *longer* window in
  // which another run can claim the arbiter, not the only one — the confirmation itself is a
  // modal the user can leave open for minutes, and a run started from anywhere else on the page
  // lands just as well behind it. Qualifying this on `liveAliases !== null` left the no-read
  // branch relying on `deleteService.startDelete`'s own refusal, which is silent, so a confirmed
  // delete in a non-active view simply evaporated. This abort is visible, and — since #256 T4 —
  // so is the restore paths' identical re-check in the panel: the competing run can be any
  // 7TV-writing kind, running or settling, started from anywhere on the page, and the panel's own
  // dock would otherwise show nothing at all to explain why a confirmed delete (or restore) just
  // vanished.
  const claim = deps.arbiter.activeClaim();
  if (claim !== null) {
    request.notice.set(
      refusedStartNotice(deps.translocoService, claim, 'massDelete.nothingDeleted'),
    );
    return;
  }
  // The third way `deleteService.startDelete` can refuse without a word — the other two, a run
  // already going and an empty list, are caught above. The engine needs the stored 7TV token, and
  // any 401 from 7TV behind the open confirmation clears it (`SevenTvTokenService.clearToken`);
  // the dock claim of the commits above would then hold an empty dock over a delete that simply
  // never happened.
  if (!deps.tokenService.hasToken()) {
    request.notice.set({
      leadKey: 'massDelete.nothingDeleted',
      reasonKey: 'massDelete.tokenGoneDuringConfirm',
    });
    return;
  }
  const liveEntries = liveAliases?.entries;
  if (liveEntries !== undefined) {
    // A live read only ever reaches here complete (an incomplete one was already blocked above) —
    // so an id it does not know at all under either map means 7TV no longer has it, not merely
    // that it has no alias. Deleting such a row anyway would issue a `RemoveEmote` for something
    // that is not there: on the vote page specifically the exact defect #227 point 2 forbids (a
    // departed set-session member reaching the run), and equally a bug for the active-set path this
    // same read also serves. Fails the WHOLE batch, not just the missing rows: a partial run would
    // record a protocol that no longer matches what the confirmation showed as a whole ("gezeigt =
    // gelöscht", spec §8.3, K5 follow-up #229). The reason names the missing rows (P2-c, Opus
    // review) rather than only a count, and tells the user to deselect exactly those and start
    // again — not "reload", which on the usage page's own legitimate normal case (an emote removed
    // on 7TV directly, ahead of our periodic resync noticing) would not help at all: our own
    // database still shows the row as present until that resync runs, so every confirmed selection
    // containing it would keep failing the same way regardless of how many times the page is
    // reloaded.
    const missingRows = confirmedSelection.filter(
      (emote) =>
        !liveEntries.aliasesById.has(emote.sevenTvEmoteId) &&
        !liveEntries.aliaslessIds.has(emote.sevenTvEmoteId),
    );
    if (missingRows.length > 0) {
      request.notice.set({
        leadKey: 'massDelete.nothingDeleted',
        reasonKey: pluralKey(missingRows.length, 'massDelete.memberRead.missingFromSet'),
        reasonParams: missingRowsReasonParams(
          deps.translocoService,
          missingRows.map((emote) => emote.name),
        ),
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
  const expectedChannelName = frozenSetId === request.activeSetId() ? frozenChannelName : null;
  deps.deleteService.startDelete(
    frozenSetId,
    frozenChannelName,
    emotes,
    expectedChannelName,
    frozenOwnerTwitchId,
  );
}

/**
 * Why a confirmed delete must not start now, or `undefined` when nothing stops it — re-evaluated at
 * confirm time, not only when the dialog opened: the dialog outlives the view it was opened on, and
 * the host can lock deleting behind it (a set switch in the usage page's dropdown — the rows and
 * the selection would then belong to a set other than `setId()`). A caller already torn down (its
 * host's dock unmounted while the dialog was open) has no selection of its own left to vouch for,
 * so it starts nothing either — `null` then: abort, but with nothing left to show it on.
 */
function abortReasonBeforeStart(
  deps: DeleteFlowDeps,
  request: DeleteFlowRequest,
  frozenSetId: string,
): DeleteAbortNotice | null | undefined {
  if (deps.destroyRef.destroyed) {
    return null;
  }
  const lockKey = request.hostLockReasonKey();
  if (lockKey !== null) {
    return { leadKey: 'massDelete.abortedByLock', reasonKey: lockKey };
  }
  // The lock above only catches a switch still *in progress* — once it settles, the lock clears
  // and `setId()` has already moved on, silently, to the new set. Comparing against what the
  // dialog actually named closes that gap: a settled switch behind an open dialog aborts here
  // too, visibly, instead of deleting into a set the confirmation never showed (#200 K5 finding A).
  if (request.setId() !== frozenSetId) {
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
function wantsLiveAliasRead(request: DeleteFlowRequest, frozenIsActiveSet: boolean): boolean {
  const mode = request.liveAliasRead();
  return (frozenIsActiveSet && mode === 'activeSet') || mode === 'set';
}

/** Comma-joined, capped names for the missing-row abort reason (#227 P2-c, Opus review). A bare
 *  count told the user nothing they could act on — this run is blocked outright ("gezeigt =
 *  gelöscht" stays the rule, spec §8.3), so the way forward is deselecting exactly these rows and
 *  starting again, which needs their names, not just how many. `PREVIEW_CAP`/the "and N more" tail
 *  are the identical ones `NamePreviewList` uses for the same "many names" problem in a dialog —
 *  reused here rather than a second threshold, just rendered as one line of status text instead of
 *  a scrollable list, since the abort notice has no dialog to put a list into. */
function missingRowsReasonParams(
  translocoService: TranslocoService,
  missingNames: readonly string[],
): Record<string, unknown> {
  const preview = missingNames.slice(0, PREVIEW_CAP);
  const remaining = missingNames.length - preview.length;
  const joined = preview.join(', ');
  if (remaining <= 0) {
    return { names: joined };
  }
  const tail = translocoService.translate(pluralKey(remaining, 'common.andMore'), {
    count: remaining,
  });
  return { names: `${joined} ${tail}` };
}
