import { describe, expect, it } from 'vitest';

import { ActionDockState, actionDockHasContent } from './action-dock';

function state(overrides: Partial<ActionDockState> = {}): ActionDockState {
  return {
    hasActiveSet: true,
    markedCount: 0,
    deleteShown: false,
    deleteConfirmPending: false,
    restoreShown: false,
    importShown: false,
    importNoticePending: false,
    restoreNoticePending: false,
    ...overrides,
  };
}

describe('actionDockHasContent', () => {
  it('is false with nothing marked and no run', () => {
    expect(actionDockHasContent(state())).toBe(false);
  });

  it('shows the marking half for a selection, a delete run or a restore run', () => {
    expect(actionDockHasContent(state({ markedCount: 1 }))).toBe(true);
    expect(actionDockHasContent(state({ deleteShown: true }))).toBe(true);
    expect(actionDockHasContent(state({ restoreShown: true }))).toBe(true);
  });

  it('stays empty without an active set, however much is marked', () => {
    // The regression: a transient 5xx on the set-status request leaves the grid standing with no
    // set, and the marking half is gated on that set — so a click on a cell used to mount an
    // accent-framed bar with nothing inside it, plus the page's pb-40 of empty space.
    expect(actionDockHasContent(state({ hasActiveSet: false, markedCount: 3 }))).toBe(false);
    expect(actionDockHasContent(state({ hasActiveSet: false, deleteShown: true }))).toBe(false);
    expect(actionDockHasContent(state({ hasActiveSet: false, restoreShown: true }))).toBe(false);
  });

  // The regression this closes: a confirmed delete whose pre-run live alias read is still out shows
  // up in none of the clauses above — no run, no queue — so a pushed reload that pruned every
  // marked key unmounted the dock, took MassDeletePanel down with it, and the delete then aborted
  // against a destroyed panel: nothing deleted and nothing said about it either.
  it('keeps the marking half for a confirmed delete that is not a run yet, with nothing marked', () => {
    expect(actionDockHasContent(state({ markedCount: 0, deleteConfirmPending: true }))).toBe(true);
  });

  it('still stays empty for a pending confirmed delete without an active set', () => {
    // Inside the hasActiveSet gate on purpose: the panel it keeps alive renders inside the marking
    // half, so mounting the bar without a set would bring the empty-bar regression back instead.
    expect(actionDockHasContent(state({ hasActiveSet: false, deleteConfirmPending: true }))).toBe(
      false,
    );
  });

  it('shows an import run without an active set — the import half has no set gate (R9)', () => {
    expect(actionDockHasContent(state({ hasActiveSet: false, importShown: true }))).toBe(true);
  });

  // #149 P2: a fully-refused (all-duplicates) import or restore leaves no run/queue behind — the
  // pending notice is the only thing there is to show, so it has to keep the dock open on its own,
  // without an active set either (same reasoning as importShown, R9).
  it('shows a pending duplicate notice without an active set and without anything else shown', () => {
    expect(actionDockHasContent(state({ hasActiveSet: false, importNoticePending: true }))).toBe(
      true,
    );
    expect(actionDockHasContent(state({ hasActiveSet: false, restoreNoticePending: true }))).toBe(
      true,
    );
  });
});
