import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { DestroyRef, WritableSignal, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, timeout } from 'rxjs';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import {
  RestoreQueueEmote,
  RestoreStartTarget,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { RestoreRow } from '../export/purge-run-export';
import {
  clipToShown,
  filterAlreadyPresentForRestore,
  loadRestoreConfirmPreview,
  RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS,
  RestoreConfirmPreview,
  restoreConfirmPreviewUnavailable,
} from './already-present-filter';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { loadRestoreSlotPreview, RestoreSlotPreview } from './restore-slot-preview';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/**
 * Everything a restore run needs to know where it goes, whom it tells, what its confirmation
 * shows, and whether that differs from the page it was started on (spec 6.1) — the shared
 * pre-check's outcome (`EditableSetTarget`, spec 6.2) plus the two fields only a caller attached to
 * a page can supply:
 *
 * - `hostChannelName` — the channel of the page the run was started on (E13); only
 *   `resetIfChannelChanged` compares against it (F7), never the target itself.
 * - `hostSelectedSetId` — the page's *selected* set, `null` when it has none; the confirmation's
 *   foreign-to-view hint compares this against `emoteSetId` (E21) — a different set of the *same*
 *   channel and a page with no selection both count as foreign.
 *
 * Produced by `resolveEditableSet` plus these two fields, in two places: `FileImportStep` for a
 * restore file (the set the file names, spec 6.1 — `ImportTrigger` passes it on unchanged) and
 * `MassDeletePanel`'s `openRestoreConfirm` for the finished delete run (E16).
 */
export interface ResolvedRestoreTarget {
  emoteSetId: string;
  setName: string;
  ownerDisplayName: string;
  twitchLogin: string;
  trackedChannelName: string | null;
  isActiveSet: boolean;
  hostChannelName: string;
  hostSelectedSetId: string | null;
}

/**
 * Everything the flow needs, handed in rather than injected — same reasoning as `ImportFlowDeps`
 * in `import-flow.ts` (see there): the flow opens dialogs, which live in `shared/`, while the
 * services it drives live in `core/`, and `core/` may not import from `shared/`. A service here
 * would therefore have to sit in `shared/` and be provided there, for a piece of code that holds
 * no state between calls; a function that takes its collaborators keeps the dependency edges
 * pointing the one legal way and lets every entry point pass its own injected instances.
 */
export interface RestoreFlowDeps {
  dialog: Dialog;
  emoteAdminService: EmoteAdminService;
  /** The live slot preview for a non-active target set (spec #200, 8.3/7.2, K5) — read only when
   *  `isActiveSet` is false; the active set keeps using `emoteAdminService.getSetStatus`'s
   *  cheaper, non-7TV-rate-limited path (same fork `loadImportTarget` already uses, AK 36). */
  emoteSetService: SevenTvEmoteSetService;
  /** Only for `filterAlreadyPresent`'s direct read against 7TV (#149 P1 fix) — every other read in
   *  this flow goes through `emoteAdminService`. */
  httpClient: HttpClient;
  tokenService: SevenTvTokenService;
  restoreService: SevenTvRestoreService;
  arbiter: SevenTvRunArbiter;
  /** Set to `true` right before the open-time duplicate check (`loadRestoreConfirmPreview`, #255
   *  P2a) starts, and back to `false` once it has settled — the confirmation opened, the
   *  "everything already there" shortcut taken, or the read failed/timed out and the confirmation
   *  opened anyway with an upper-bound count. Never left `true` on any exit — including the
   *  caller's own teardown mid-read (#255 P2, Codex review): `takeUntilDestroyed` unsubscribes
   *  without ever calling `next` or `error`, so the reset used to live only in `handlePreview`,
   *  reachable from neither. A `finalize` on the read's own pipe now covers exit by teardown the
   *  same way the `next`/`error` branches already covered a settled answer — otherwise, since this
   *  aliases the *shared*, root-level `SevenTvRestoreService.restorePreCheckPending`, tearing down
   *  the flow mid-read (a route change, a closed panel) left both restore entries disabled until a
   *  full page reload, not just this one caller's own button. `openConfirm` also refuses to start a
   *  second read of its own while this is already `true`, so the flow guards itself even if a
   *  caller's own disabled button outraces a click. The caller reads it to disable whatever button
   *  opens this flow — `ImportTrigger` is the only one today.
   *
   *  `ImportTrigger` passes its `SevenTvRestoreService.restorePreCheckPending` here, not a signal
   *  of its own (#255 P2, Codex review): `MassDeletePanel`'s restore button runs the identical
   *  pre-check chain through its own code path (it does not call this function) and mounts on the
   *  same page, so two component-local flags left the *other* entry's button enabled for the
   *  whole read — a click there could open a second confirmation stacked on this one. This
   *  interface still just asks for *a* `WritableSignal<boolean>`; which instance a caller shares
   *  it with is that caller's choice, not this function's. */
  previewPending: WritableSignal<boolean>;
  /** Torn down together with whatever component owns this flow — `startRestoreFlow` has no
   *  injection context of its own to pull one from (see the class doc on this interface), so every
   *  caller supplies its own via `inject(DestroyRef)`. Guards the open-time duplicate check's
   *  timeout-bounded read (`RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS`) the same way
   *  `mass-delete-panel.ts`'s own reads already guard theirs, so a late answer after the caller is
   *  gone cannot open a confirmation nobody can see or answer. */
  destroyRef: DestroyRef;
}

/**
 * Confirms and starts one restore run: 7TV token → confirmation with a live slot preview → run.
 *
 * **The token prompt comes before the confirmation**, unlike the import flow, which asks only
 * after the confirmation (see the note on that in `startImportFlow`). Unchanged from the panel
 * this was extracted from: restoring an already-validated restore file has no read-only
 * preview step worth protecting the token prompt's ordering against — do not "align" this with
 * the import flow.
 *
 * `target` is the value frozen at the moment the caller resolved it — the flow never re-reads it
 * from a live signal, so a channel or set switch while a dialog of this chain is still open cannot
 * change what gets restored or where (spec #200, 8.8, carried over to `ResolvedRestoreTarget`).
 */
export function startRestoreFlow(
  deps: RestoreFlowDeps,
  target: ResolvedRestoreTarget,
  rows: readonly RestoreRow[],
): void {
  const openConfirm = (): void => {
    // #255 P2a: refuses to start a second open-time read while one is already out — belt and
    // suspenders next to the caller's own disabled button (`previewPending`, see the field doc),
    // so a click that outraces it, or a caller with no button of its own, still cannot end up with
    // two confirmations racing for the same rows.
    if (deps.previewPending()) {
      return;
    }
    deps.previewPending.set(true);

    // `emoteId` is `null` for a row that never had a local emote (spec #200, 7.2) — the restore
    // does not need it. `aliases` goes through whole: the restore service sends one `ADD` per
    // alias, so a #74 duplicate comes back under both of its names, and a `null` alias as an
    // `ADD` without one. `defaultName` names that aliasless queue row. Built once, up here: both
    // the open-time preview below and the confirm-time run start from the same rows.
    const emotes: RestoreQueueEmote[] = rows.map((row) => ({
      emoteId: row.emoteId ?? undefined,
      sevenTvEmoteId: row.sevenTvEmoteId,
      name: row.name,
      aliases: row.aliases,
      defaultName: row.defaultName,
    }));

    // Operator decision 2026-09-25 (#255, "Slot-Zahl nach dem Skip-Filter"): the duplicate/
    // name-taken check now also runs here, before the confirmation ever opens, so its title and
    // capacity projection count what the run will actually send — not every row the source names,
    // some of which may be about to be silently skipped as already present. See
    // `loadRestoreConfirmPreview`'s doc for why this is not a second kind of 7TV read next to the
    // slot preview above, and confirmed below for why the confirm-time check still runs again
    // fresh rather than reusing this result.
    //
    // #255 P2a: bounded by the same timeout budget as the read `mass-delete-panel.ts`'s
    // `resolveEditableSet` calls already guard, and dropped on teardown via `deps.destroyRef` —
    // `startRestoreFlow` has no injection context of its own, hence the caller-supplied ref. A
    // `timeout` error lands outside `loadRestoreConfirmPreview`'s own `catchError`, so it is
    // treated exactly like the fetch failure that filter already fails open on:
    // `restoreConfirmPreviewUnavailable` builds the identical "could not verify" shape by hand.
    //
    // #255 P2 (Codex review): `finalize` is what actually clears `previewPending` now, on every
    // exit — a settled answer (`next`/`error`, still handled inside `handlePreview` below for the
    // outcome, not the flag any more) and, the gap this closes, the caller's own teardown, which
    // `takeUntilDestroyed` unsubscribes silently with neither callback ever firing. Left as a
    // manual reset only inside `handlePreview`, that exit never ran it — and since this flag
    // aliases the shared, root-level `restorePreCheckPending` (see the field doc), a route change
    // or a closed panel mid-read left *both* restore entries disabled until a full page reload, not
    // just this caller's own.
    loadRestoreConfirmPreview(deps.httpClient, target.emoteSetId, emotes)
      .pipe(
        timeout(RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS),
        takeUntilDestroyed(deps.destroyRef),
        finalize(() => deps.previewPending.set(false)),
      )
      .subscribe({
        next: (preview) => handlePreview(preview),
        error: () => handlePreview(restoreConfirmPreviewUnavailable(emotes)),
      });

    function handlePreview(preview: RestoreConfirmPreview<RestoreQueueEmote>): void {
      if (preview.available && preview.rows.length === 0) {
        // Nothing survives the filter — every row is already back (or its alias is taken) and
        // there is nothing left to confirm. A dialog with zero names and a button that could only
        // ever restore nothing would ask a question with no real answer; the existing "everything
        // already there" notice (`SevenTvRestoreService.duplicateNoticePending`, `restore.
        // skippedDuplicates`/`skippedNameTaken`) already says exactly this for the identical
        // outcome at confirm-time, so this reuses it instead of a second, dialog-shaped way to say
        // the same thing.
        //
        // #255 P2b (the #149 P2 fix's own reasoning, applied to this shortcut too): this call
        // starts a run exactly as much as the regular path's does, so it needs the same
        // mutual-exclusion check right before it — another 7TV-writing run could have claimed the
        // arbiter while this read was out, a window the regular path already closes just above its
        // own `startRestore` call.
        if (deps.arbiter.activeRun() !== null) {
          return;
        }
        deps.restoreService.startRestore(
          restoreStartTarget(target),
          [],
          preview.skipped,
          true,
          preview.skippedNameTaken,
        );
        return;
      }

      // Live slot view, same pattern as the delete confirm's shared-set warning. Started only now
      // rather than up front (#255 P3(10)): the shortcut above already covers the "nothing left to
      // confirm" case, so starting this read before knowing whether a dialog will even open would
      // spend a 7TV request the "everything already there" outcome above then throws away
      // unread — this way it fires exactly once per flow, only when there is a confirmation for it
      // to populate. The read itself is the fork `loadRestoreSlotPreview` shares with
      // `MassDeletePanel.openRestoreConfirmDialog` (spec 4.3, point 8 / spec 8.3, final fix wave
      // A5) — same fork `loadImportTarget` also uses. Unrelated to the duplicate check above: this
      // one reads occupied/capacity counts, never entries.
      const slots = signal<RestoreSlotPreview>(null);
      loadRestoreSlotPreview(deps, target).subscribe((slotPreview) => slots.set(slotPreview));

      const data: RestoreConfirmDialogData = {
        names: preview.names,
        addCount: preview.addCount,
        countIsUpperBound: !preview.available,
        slots: slots.asReadonly(),
        setName: target.setName,
        isActiveSet: target.isActiveSet,
        emoteSetId: target.emoteSetId,
        ownerDisplayName: target.ownerDisplayName,
        trackedChannelName: target.trackedChannelName,
        // Spec E21: a different set of the page's own channel, and a page with no selected set at
        // all, both count as foreign — never a channel comparison.
        foreignToView: target.emoteSetId !== target.hostSelectedSetId,
      };
      openRestoreConfirmDialog(deps.dialog, data).closed.subscribe((confirmed) => {
        if (!confirmed) {
          return;
        }
        // The engine only refuses *its own* second run; a delete or import running elsewhere is
        // invisible to it, so the cross-kind check happens here — silently, because the progress of
        // that other run is already on screen and saying it twice would be the louder mistake.
        if (deps.arbiter.activeRun() !== null) {
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
        filterAlreadyPresentForRestore(deps.httpClient, target.emoteSetId, emotes).subscribe(
          (confirmCheck) => {
            // #149 P2 review fix: the arbiter check above ran *before* this fetch, outside the
            // mutual-exclusion contract (design doc §4.3) it is meant to enforce — another run can
            // start in that window. Re-checked here, right before the only remaining call that
            // actually starts anything; silent on a block for the same reason as the check above,
            // the run that got there first is already visible in the dock.
            if (deps.arbiter.activeRun() !== null) {
              return;
            }
            // #255 P3(7): a failed confirm-time check normally means every row goes out
            // unfiltered (`filterAlreadyPresentForRestore`'s own fail-open behaviour) — which
            // would silently throw away the open-time check's own, still-valid answer for any row
            // it had already found already present or name-taken. Falls back to that stale-but-
            // real filter instead of no filter at all, whenever the open-time check succeeded.
            // `available` stays what the confirm-time check itself answered either way: it is the
            // freshest check, and its failure still leaves the narrow window since the open-time
            // read unverified, so `duplicateCheckUnavailable` keeps applying — this only changes
            // *which rows* get sent, not whether the caller is told the check could not confirm
            // them just now.
            const fallOnOpenTime = !confirmCheck.available && preview.available;
            // #255 P1 (Codex review): the confirmation only ever showed `preview.rows` — a row (or
            // one alias of a row) the open-time check above had already found present, and which
            // never appeared in the dialog's names or `addCount`, must not come back just because
            // it went missing again by the time this fresher check ran (the target set changing in
            // the few seconds a confirmation sits open, or between the two reads). `confirmCheck`
            // itself still has to query with every row's full, original aliases — `clipToShown`'s
            // own doc explains why a narrower input here would break the #74 partial-retry case —
            // so the invariant is enforced afterward instead: the confirm-time answer only ever
            // narrows what was shown, `startRestore` can never see more than that. `fallOnOpenTime`
            // already reuses `preview.rows` unclipped — that IS what was shown, nothing to narrow
            // further. Unaffected: the skip counters below, which still come straight from
            // `confirmCheck`'s own fresh count, exactly as before this fix.
            const rows = fallOnOpenTime
              ? preview.rows
              : clipToShown(confirmCheck.rows, preview.rows);
            deps.restoreService.startRestore(
              restoreStartTarget(target),
              rows,
              fallOnOpenTime ? preview.skipped : confirmCheck.skipped,
              confirmCheck.available,
              fallOnOpenTime ? preview.skippedNameTaken : confirmCheck.skippedNameTaken,
            );
          },
        );
      });
    }
  };

  if (deps.tokenService.hasToken()) {
    openConfirm();
    return;
  }
  openSevenTvTokenPromptDialog(deps.dialog).closed.subscribe((saved) => {
    if (saved === true) {
      openConfirm();
    }
  });
}

/** Derives the restore service's `RestoreStartTarget` from the resolved target (spec 6.4):
 *  `expectedChannelName` is the tracked channel only when the target is its *active* set (E18);
 *  `resyncChannelName` is the tracked channel only when it is *not*, for `RestoreProgressSection`'s
 *  target line only — it named the case E12 used to have the client resync on its own, but as of
 *  the operator decision 2026-09-25 (#255) a non-active tracked target no longer gets a client
 *  resync at all (`SevenTvRestoreService.resyncAfterReport` no longer reads this field; see
 *  DECISIONS.md, 2026-09-25, and the design doc's §18 addendum). An untracked target sends neither.
 *  `ownerOrChannelLabel` prefers the tracked
 *  channel, falling back to the owner's display name for an untracked target — display only
 *  (the dock's target line), never compared. Exported: `MassDeletePanel`'s own restore-confirm
 *  chain (spec E16) needs the identical derivation and stays its own chain rather than folding
 *  into `startRestoreFlow` — its rows come from a finished delete run's `RunQueueItem`s, not from
 *  a file's `RestoreRow`s, so the two `filterAlreadyPresentForRestore`/`startRestore` call sites
 *  are not otherwise byte-identical (task brief, Plan-253 §6 Nr. 3). */
export function restoreStartTarget(target: ResolvedRestoreTarget): RestoreStartTarget {
  const channel = target.trackedChannelName;
  return {
    setId: target.emoteSetId,
    expectedChannelName: channel !== null && target.isActiveSet ? channel : null,
    resyncChannelName: channel !== null && !target.isActiveSet ? channel : null,
    hostChannelName: target.hostChannelName,
    setName: target.setName,
    ownerOrChannelLabel: channel ?? target.ownerDisplayName,
  };
}
