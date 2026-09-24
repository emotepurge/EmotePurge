import { Injectable, signal } from '@angular/core';

/**
 * Lets a page with its own `position: fixed`, full-width bottom bar (today only the usage-stats
 * action dock, `.app-dock` in styles.css) tell `AppShell` how much space to reserve below the
 * footer.
 *
 * AppShell renders the same footer on every route in normal document flow, right after `<main>`
 * (§8.4a — the frame must not vary per route). On a page short enough that the footer reaches the
 * viewport's bottom edge, that is exactly where a `position: fixed` bar also renders, regardless
 * of scroll position — a fixed element is positioned against the viewport, not the document, so it
 * covers whatever currently occupies that strip. `usage-stats-page.html` already guards its own
 * content the same way (`pb-40` while `dockVisible()`), but that padding sits *inside* `<main>`,
 * before the footer, and does nothing for content that comes after it. This service is the shell's
 * side of the same guard, generic rather than usage-stats-specific: any future page with a fixed
 * bottom bar of its own reserves space the same way instead of the shell special-casing one route.
 *
 * A signal, not a route flag: the reservation appears and disappears with the bar itself, which is
 * state-driven (marking a cell, an import/restore run settling) like the dock's own guard, not a
 * second per-route shell contract that would reintroduce the per-route layout variation §8.4a
 * rules out.
 */
@Injectable({ providedIn: 'root' })
export class DockClearanceService {
  private readonly reservedPx = signal(0);

  /** How much space, in pixels, AppShell should reserve below the footer right now. Zero when no
   *  page has an active reservation. */
  readonly px = this.reservedPx.asReadonly();

  /** Call with the fixed bar's reserved height while it is shown; the caller is responsible for
   *  calling `release()` (or `reserve(0)`) once it is not, typically from an `effect()` mirroring
   *  the bar's own visibility signal. */
  reserve(px: number): void {
    this.reservedPx.set(px);
  }

  /** Equivalent to `reserve(0)` — named separately so a component's `DestroyRef.onDestroy` hook
   *  reads as "give the space back", not as a reservation of zero. */
  release(): void {
    this.reservedPx.set(0);
  }
}
