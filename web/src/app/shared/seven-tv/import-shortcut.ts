/**
 * Whether the dock's copy shortcut — the second entry point into `openImportTarget()`, sitting next
 * to "Zur Abstimmung stellen" in the mass-delete panel's `selection-actions` slot — should be
 * disabled.
 *
 * The shortcut forces `scope: 'selection'` (design doc §8.7, "erzwungener Bereich"): there is no
 * radiogroup in the target dialog to fall back to `visible`, so an empty selection is nothing to
 * act on. Beyond that it inherits every lock the header button ("Übertragen") already
 * carries: `importScopeCurrent()` — a capture mid-channel-switch would copy channel A's emotes out
 * of A's set under B's name, see `importScopeIsCurrent` — and `SevenTvRunArbiter.activeRun()`, any
 * 7TV-writing run active or settling, not just this button's own kind.
 *
 * `!isCoarse()` and an active 7TV set deliberately do NOT appear in this state: the dock this
 * shortcut lives in is already gated on both — `usage-stats-page.html`'s
 * `@if (dockVisible() && !isCoarse())` around the whole bar, and the `@if (activeEmoteSetId(); as
 * setId)` around the marking half the mass-delete panel sits in. Repeating either here would check
 * a condition the shortcut can never actually be rendered without.
 */
export interface ImportShortcutState {
  /** Size of the grid selection — the only scope the shortcut can act on. */
  readonly selectionCount: number;
  /** See `importScopeIsCurrent` — false during the window right after a same-route channel switch. */
  readonly importScopeCurrent: boolean;
  /** `SevenTvRunArbiter.activeRun() !== null` — any 7TV-writing run active or settling, not just
   *  import. */
  readonly hasActiveRun: boolean;
}

export function importShortcutDisabled(state: ImportShortcutState): boolean {
  return state.selectionCount === 0 || state.hasActiveRun || !state.importScopeCurrent;
}
