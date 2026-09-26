/**
 * The usage-stats action dock is not one block: it renders a *marking* half (the marked count, the
 * mass-delete panel and the vote-session button projected into it), an *import* half
 * (`app-import-progress-section`), since #253/T9 a *restore* half (`app-restore-progress-section`)
 * and since #254 an *undo* half (`app-undo-progress-section`), each with its own gate in the
 * template. The marking half needs this channel to have an active 7TV set — there is nothing to
 * mark slots against without one — while the import, restore and undo halves deliberately do not:
 * a copy run writes into *another* channel's set and has to stay visible, Cancel button included,
 * on every usage-stats page it is opened from (R9); a restore reached via `ImportTrigger`'s file
 * branch can start on a page with no selected set at all and write into whatever set the file
 * names (spec E22, AK 33); and an undo writes into the set its transfer file names, whichever set
 * the page shows (#254 spec 6.6).
 *
 * Mounting the bar on "something is selected" alone therefore has an empty state: a transient 5xx on
 * `GET …/emote-set` leaves the grid standing with no set status, and the first click on a cell then
 * produced an accent-framed bar with nothing in it, plus the `pb-40` of empty space the page keeps
 * free for it. This function is what the bar and that padding are bound to instead.
 *
 * Pure rather than inlined in the page's `computed`, so the empty-bar case can be pinned by a test
 * that does not have to stand the whole usage-stats page up in a TestBed.
 */
export interface ActionDockState {
  /** This channel has an active 7TV set — without one the marking half renders nothing. */
  readonly hasActiveSet: boolean;
  readonly markedCount: number;
  /** A delete run is in flight or settled-but-still-shown (its protocol download lives there). */
  readonly deleteShown: boolean;
  /** A delete has been confirmed but is not a run yet — `MassDeletePanel`'s pre-run live alias read
   *  is out — or it just ended without becoming one and its abort notice is still showing
   *  (`SevenTvDeleteService.confirmedRunPending`). Neither state is visible in `deleteShown`, and
   *  neither needs anything to be marked: a pushed reload can prune every marked key while the read
   *  is in flight, which used to unmount the dock, destroy the panel underneath it and turn the
   *  confirmed delete into a silent no-op — no `REMOVE` sent and no notice left to say so. Inside
   *  the `hasActiveSet` gate like the two above, not beside it: the panel this keeps alive renders
   *  inside the marking half, so mounting the dock without a set would only bring the empty bar
   *  back. */
  readonly deleteConfirmPending: boolean;
  /** A restore run is in flight or settled-but-still-shown. Independent of `hasActiveSet` since
   *  #253/T9 (AK 33, fix round 1) — unlike a delete, a restore need not target this channel's
   *  active set at all: `ImportTrigger`'s file branch stays reachable on a page with no selected
   *  set (E22), and the run it starts can write into any set the file names. Gating it behind
   *  `hasActiveSet` left `RestoreProgressSection` unmountable for exactly that run once its
   *  transient duplicate/name-taken notice (`restoreNoticePending`, already independent below) had
   *  cleared or never applied — a plain, notice-free restore in flight showed no dock at all on such
   *  a page. */
  readonly restoreShown: boolean;
  /** An import run is in flight or settled-but-still-shown. Independent of `hasActiveSet` on
   *  purpose — see above. */
  readonly importShown: boolean;
  /** #149 P2 (independent review): the fresh pre-run duplicate check (`already-present-filter.ts`)
   *  just reported something and the transient notice for it is still showing
   *  (`SevenTvImportService.duplicateNoticePending`). Independent of `hasActiveSet`, same reasoning
   *  as `importShown` — an all-duplicates *refused* import leaves no run/queue behind, so without
   *  this the dock (and the section that renders the notice) would never mount for exactly the
   *  outcome the notice exists to report. */
  readonly importNoticePending: boolean;
  /** Same as `importNoticePending`, for the restore side (`SevenTvRestoreService.duplicateNoticePending`)
   *  — covers both restore entry points (`MassDeletePanel`'s own confirm, and a file-based restore
   *  reached via `ImportTrigger`, which need not have anything marked in this channel's grid at
   *  all). */
  readonly restoreNoticePending: boolean;
  /** An undo run (#254) is shown — in flight, settling, reporting or settled until Close.
   *  Independent of `hasActiveSet` like the import and the restore: an undo writes into the set its
   *  transfer file names, whichever set this page shows (spec 6.6). */
  readonly undoShown: boolean;
  /** The undo's transient skipped notice (`SevenTvUndoService.noticePending`) — a start that
   *  skipped every candidate leaves no run behind, and this is what mounts the dock for its notice,
   *  same reasoning as `importNoticePending`. */
  readonly undoNoticePending: boolean;
}

export function actionDockHasContent(state: ActionDockState): boolean {
  // Delete stays inside the hasActiveSet clause: unlike restore, a delete always targets THIS
  // channel's own set — MassDeletePanel's delete half never mounts without one (spec #200, 8.1),
  // so marking, a delete run and a pending delete confirmation genuinely have nothing to show
  // without an active set. restoreShown is deliberately NOT part of markingShown any more (AK 33,
  // fix round 1) — see that field's own doc.
  const markingShown = state.markedCount > 0 || state.deleteShown || state.deleteConfirmPending;
  return (
    (state.hasActiveSet && markingShown) ||
    state.restoreShown ||
    state.importShown ||
    state.importNoticePending ||
    state.restoreNoticePending ||
    state.undoShown ||
    state.undoNoticePending
  );
}
