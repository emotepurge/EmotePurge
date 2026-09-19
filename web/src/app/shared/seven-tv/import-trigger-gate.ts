/**
 * Whether the import trigger (#91, plan §1.2 point 3; #147) — the "Importieren" button in the
 * usage-stats page header, which opens the one import dialog and with it every import source —
 * should be disabled.
 *
 * Deliberately NOT part of this gate, because both are already enforced by the `@if` block the
 * trigger sits in on the page (`usage-stats-page.html`, the same block that gates "Übertragen"):
 * - `!isCoarse()` — no 7TV write path without a mouse (§2.5).
 * - an active 7TV set (`activeEmoteSetId()`) — there is no channel/set to validate a file against
 *   without one.
 *
 * Also deliberately NOT part of it, unlike the neighbouring "Übertragen" button this trigger
 * otherwise mirrors: the empty-scope lock (`atlasOrder().length === 0 &&
 * selection.selectedItems().length === 0`, Konzept "Auswahl überlebt Suche und Filter" nachtrag,
 * 2026-09-19). Neither the file path nor the foreign-channel path needs a visible row or a
 * selection at all — both bring their own rows regardless.
 *
 * `importScopeCurrent` mirrors `importScopeIsCurrent` (`import-scope.ts`): a channel switch inside
 * the route can leave `channelName()` pointing at the new channel while the set status/rows still
 * belong to the old one. The page computes that boolean the same way it does for "Übertragen" and
 * hands it in as an input, rather than this gate recomputing it — that keeps the gate a pure
 * function taking only booleans, testable without a TestBed (same shape as `import-shortcut.ts`).
 */
export interface ImportTriggerGateState {
  /** `SevenTvRunArbiter.activeRun() !== null` — any of the three 7TV-writing runs, not just this one. */
  readonly hasActiveRun: boolean;
  /** See `importScopeIsCurrent` — false during the window right after a same-route channel switch. */
  readonly importScopeCurrent: boolean;
}

export function importTriggerDisabled(state: ImportTriggerGateState): boolean {
  return state.hasActiveRun || !state.importScopeCurrent;
}
