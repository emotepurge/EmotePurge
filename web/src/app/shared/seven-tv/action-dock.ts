/**
 * The usage-stats action dock is not one block: it renders a *marking* half (the marked count, the
 * mass-delete panel and the vote-session button projected into it) and an *import* half
 * (`app-import-progress-section`), and each carries its own gate in the template. The marking half
 * needs this channel to have an active 7TV set — there is nothing to mark slots against without
 * one — while the import half deliberately does not, because a copy run writes into *another*
 * channel's set and has to stay visible, Cancel button included, on every usage-stats page it is
 * opened from (R9).
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
  /** A restore run is in flight or settled-but-still-shown. */
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
}

export function actionDockHasContent(state: ActionDockState): boolean {
  const markingShown =
    state.markedCount > 0 || state.deleteShown || state.restoreShown || state.deleteConfirmPending;
  return (
    (state.hasActiveSet && markingShown) ||
    state.importShown ||
    state.importNoticePending ||
    state.restoreNoticePending
  );
}
