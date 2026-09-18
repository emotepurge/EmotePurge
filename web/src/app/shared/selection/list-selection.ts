import { computed, signal } from '@angular/core';

/**
 * Card-click + shift-click range multi-select over a (possibly virtual-scrolled) list. A
 * shift-click carries the anchor's current mark onto the whole range — it selects the range when
 * the anchor is marked, or clears it when the anchor isn't, so a range can be undone the same way
 * it was made.
 * Not a service — selection is page-local UI state, like a FormControl, not app-wide state.
 * `items` must return the full logically-ordered/filtered list (not the DOM-rendered subset),
 * so the shift-click range stays correct regardless of what CdkVirtualScrollViewport has mounted.
 *
 * Keyed, never identity- or position-based: the selection stores `keyFn(item)` (the emote id).
 * A refetch hands out freshly deserialized objects for the very same rows, and flipping the sort
 * direction moves every row to a different position — both used to silently desynchronize the
 * rendered selection state from what the delete path actually submitted.
 *
 * Two sources, not one: `items` is the *display list* (filtered, sorted, in atlas order — the
 * basis for shift ranges, keyboard navigation and visibility), `universe` is the *unfiltered*
 * backing set selection resolves against. `universe` is an optional third constructor parameter
 * that defaults to `items` — that default is exactly what keeps `ForeignEmoteGrid`
 * (`shared/seven-tv/foreign-emote-grid.ts`), which has no filter and passes only two arguments,
 * unchanged: for it, display list and universe are the same list, as they always were.
 *
 * Three views on the selection, all derived from the same key set so their counts never disagree:
 *  - `selectedKeys` is authoritative. It changes only through user gestures and through
 *    `retainAmong(universe)`, never through a filter change, so it stays complete across refetch,
 *    re-sort and filtering. Anything that must not miss a selected entry (counting, deciding what
 *    gets deleted) belongs here.
 *  - `selectedItems` resolves those keys against `universe` (not the display list), for consumers
 *    that need more than the key (preview names in the delete confirmation, the delete/export/vote
 *    run itself). It is complete as long as `selectedKeys ⊆ keys(universe)` holds — a key missing
 *    from `universe` simply does not resolve and is left out of every run, the conservative
 *    direction.
 *  - `isVisible(item)`/`hiddenSelectedCount` split that same key set by whether the key is also in
 *    the *display* list — `isVisible` per item, `hiddenSelectedCount` as the total marked-but-not-
 *    shown count, e.g. for a dock notice ("n hidden by the current filter").
 *
 * Backed by a signal (not @angular/cdk/collections' SelectionModel) — a computed() elsewhere that
 * reads the selection needs an actual signal read to know when to recompute; a plain mutable
 * SelectionModel gives it nothing to track, so toggling a card would never trigger a re-render.
 */
export class ListSelection<T> {
  private readonly selectedKeySet = signal<ReadonlySet<string>>(new Set());

  // The shift-click anchor is the anchored row's key, resolved against the *current* items() at
  // click time. Stored as a position index it silently pointed at a different row after every
  // re-sort, which turned a shift-click into a range over rows the user never saw selected.
  private anchorKey: string | null = null;

  readonly selectedKeys = computed(() => Array.from(this.selectedKeySet()));

  readonly selectedItems = computed<T[]>(() => {
    const keys = this.selectedKeySet();
    return this.universe().filter((item) => keys.has(this.keyFn(item)));
  });

  readonly hiddenSelectedCount = computed(() => {
    const keys = this.selectedKeySet();
    if (keys.size === 0) {
      return 0;
    }
    const visibleKeys = new Set(this.items().map((item) => this.keyFn(item)));
    let hidden = 0;
    for (const key of keys) {
      if (!visibleKeys.has(key)) {
        hidden++;
      }
    }
    return hidden;
  });

  constructor(
    private readonly items: () => readonly T[],
    private readonly keyFn: (item: T) => string,
    // Regel 14: a computed() reading mutable state instead of a signal never reacts to it, so the
    // universe is a signal-returning function like `items`, not a plain array — defaulting it to
    // `items` itself (rather than e.g. `() => items()`) keeps identity-equal reads for the
    // filter-less callers that never pass a third argument.
    private readonly universe: () => readonly T[] = items,
  ) {}

  isSelected(item: T): boolean {
    return this.selectedKeySet().has(this.keyFn(item));
  }

  isVisible(item: T): boolean {
    const key = this.keyFn(item);
    return this.items().some((candidate) => this.keyFn(candidate) === key);
  }

  onRowClick(item: T, event: MouseEvent): void {
    const items = this.items();
    const key = this.keyFn(item);
    const clickedIndex = items.findIndex((candidate) => this.keyFn(candidate) === key);
    const anchorKey = this.anchorKey;
    const anchorIndex =
      anchorKey === null ? -1 : items.findIndex((candidate) => this.keyFn(candidate) === anchorKey);

    const next = new Set(this.selectedKeySet());

    // An anchor that is no longer visible (filtered out, deleted, replaced by another channel's
    // data) has no meaningful range to the clicked row — degrade to a single toggle instead of
    // guessing, since the wrong guess ends in irreversibly deleted emotes.
    if (event.shiftKey && anchorKey !== null && anchorIndex !== -1 && clickedIndex !== -1) {
      const [start, end] =
        anchorIndex < clickedIndex ? [anchorIndex, clickedIndex] : [clickedIndex, anchorIndex];
      // The anchor's own mark at the moment of the shift-click is the verb for the whole range:
      // still marked carries the range in (today's behaviour), already unmarked (a click that just
      // deselected it) carries the range out. Reading it straight off `next` instead of a stored
      // "direction" field means a plain click on the anchor is what steers the next shift-click,
      // and there is nothing separate that could drift out of sync with the actual selection.
      const rangeShouldSelect = next.has(anchorKey);
      for (const ranged of items.slice(start, end + 1)) {
        const rangedKey = this.keyFn(ranged);
        if (rangeShouldSelect) {
          next.add(rangedKey);
        } else {
          next.delete(rangedKey);
        }
      }
    } else if (next.has(key)) {
      next.delete(key);
    } else {
      next.add(key);
    }

    this.selectedKeySet.set(next);
    this.anchorKey = key;
  }

  /**
   * Adds a whole group at once — what the atlas's per-band "select all" acts through.
   *
   * Add-only, never a toggle: the caller means "these too", and a group action that silently
   * *deselects* on the second press would be a way to lose a hand-built selection to one click.
   * This is stricter than the shift-click range (which does carry a deselect, see onRowClick) —
   * that direction-flip belongs to a deliberate per-row gesture, not to a group button one press
   * away from an irreversible delete. The anchor moves to the last added row so a following
   * shift-click extends from the end of the group rather than from wherever the user last clicked.
   */
  selectMany(items: readonly T[]): void {
    if (items.length === 0) {
      return;
    }

    const next = new Set(this.selectedKeySet());
    for (const item of items) {
      next.add(this.keyFn(item));
    }
    this.selectedKeySet.set(next);
    this.anchorKey = this.keyFn(items[items.length - 1]);
  }

  clear(): void {
    this.selectedKeySet.set(new Set());
    this.anchorKey = null;
  }

  /**
   * Prunes the selection against an explicitly given set of still-valid items, returning how many
   * keys were dropped. This is now the only pruning mechanism (the former `retainVisible()`, which
   * pruned against the *filtered* display list on every filter keystroke, is gone — a filter change
   * must not touch `selectedKeys` at all, see the class doc). Callers reconcile against the
   * *unfiltered* universe: a silent reload of the same context (live event, sync poll) drops keys
   * that were actually removed from the backing set, while a row that merely fell out of the
   * current filter window survives because it is still in what gets passed here.
   */
  retainAmong(items: readonly T[]): number {
    const valid = new Set(items.map((item) => this.keyFn(item)));
    let removed = 0;
    this.selectedKeySet.update((keys) => {
      const next = new Set<string>();
      for (const key of keys) {
        if (valid.has(key)) {
          next.add(key);
        } else {
          removed++;
        }
      }
      return next;
    });
    if (this.anchorKey !== null && !valid.has(this.anchorKey)) {
      this.anchorKey = null;
    }
    return removed;
  }
}
