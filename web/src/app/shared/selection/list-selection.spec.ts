import { computed, signal } from '@angular/core';
import { describe, expect, it } from 'vitest';

import { ListSelection } from './list-selection';

interface Row {
  id: string;
  label: string;
}

function rows(...ids: string[]): Row[] {
  return ids.map((id) => ({ id, label: id.toUpperCase() }));
}

function click(shiftKey = false): MouseEvent {
  return { shiftKey } as MouseEvent;
}

// The item source is a signal, mirroring how the pages pass a computed() — a plain array would
// never invalidate selectedItems(), so the reorder/refetch cases below could not be observed.
function setup(...ids: string[]) {
  const items = signal(rows(...ids));
  const selection = new ListSelection<Row>(items, (row) => row.id);
  const byId = (id: string): Row => items().find((row) => row.id === id)!;
  return { items, selection, byId };
}

// Display list and universe as two independent signals, the way a filtered page (usage-stats,
// voting) constructs a ListSelection — as opposed to setup() above, which mirrors the
// filter-less ForeignEmoteGrid default where they are the same list.
function setupWithUniverse(displayIds: string[], universeIds: string[]) {
  const items = signal(rows(...displayIds));
  const universe = signal(rows(...universeIds));
  const selection = new ListSelection<Row>(items, (row) => row.id, universe);
  const byId = (id: string): Row =>
    universe().find((row) => row.id === id) ?? items().find((row) => row.id === id)!;
  return { items, universe, selection, byId };
}

describe('ListSelection', () => {
  it('starts with nothing selected', () => {
    const { selection, byId } = setup('a', 'b', 'c');

    expect(selection.selectedKeys()).toEqual([]);
    expect(selection.selectedItems()).toEqual([]);
    expect(selection.isSelected(byId('a'))).toBe(false);
  });

  it('toggles a row on a plain click', () => {
    const { selection, byId } = setup('a', 'b', 'c');

    selection.onRowClick(byId('b'), click());
    expect(selection.isSelected(byId('b'))).toBe(true);
    expect(selection.selectedKeys()).toEqual(['b']);
    expect(selection.selectedItems()).toEqual([byId('b')]);

    selection.onRowClick(byId('b'), click());
    expect(selection.isSelected(byId('b'))).toBe(false);
    expect(selection.selectedKeys()).toEqual([]);
  });

  it('selects a contiguous range on shift-click', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e');

    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('d'), click(true));

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c', 'd']);
  });

  it('selects a range regardless of click direction (end before start)', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e');

    selection.onRowClick(byId('d'), click());
    selection.onRowClick(byId('b'), click(true));

    expect(selection.selectedKeys().sort()).toEqual(['b', 'c', 'd']);
  });

  it('a shift-click with no prior anchor behaves like a plain toggle', () => {
    const { selection, byId } = setup('a', 'b', 'c');

    selection.onRowClick(byId('c'), click(true));

    expect(selection.selectedKeys()).toEqual(['c']);
  });

  it('survives a refetch that replaces every item object with an equal-keyed one', () => {
    const { items, selection, byId } = setup('a', 'b', 'c');
    selection.onRowClick(byId('b'), click());

    // Same rows, brand new object identities — what an HTTP refetch produces.
    items.set(rows('a', 'b', 'c'));

    expect(selection.selectedKeys()).toEqual(['b']);
    expect(selection.isSelected(byId('b'))).toBe(true);
    // Resolved against the new objects, so a delete path reading selectedItems() submits fresh data
    // instead of double-counting the stale ones.
    expect(selection.selectedItems()).toEqual([byId('b')]);
    expect(selection.selectedItems()[0]).toBe(byId('b'));
  });

  it('resolves the shift range against the current order, not the order at anchor time', () => {
    const { items, selection, byId } = setup('a', 'b', 'c', 'd', 'e');
    selection.onRowClick(byId('a'), click());

    // Sort direction flipped after the anchor was set.
    items.set(rows('e', 'd', 'c', 'b', 'a'));
    selection.onRowClick(byId('c'), click(true));

    // 'a' sits last now, so the range from the anchor to 'c' is c-b-a. A position-index anchor
    // would have produced e-d-c here — a completely different set of rows.
    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c']);
  });

  it('falls back to a plain toggle when the anchor is no longer in the list', () => {
    const { items, selection, byId } = setup('a', 'b', 'c', 'd');
    selection.onRowClick(byId('a'), click());

    // 'a' (the anchor) and 'b' filtered out of view.
    items.set(rows('c', 'd'));

    expect(() => selection.onRowClick(byId('d'), click(true))).not.toThrow();
    // Only the clicked row was added — and the invisible 'a' stays authoritatively selected.
    expect(selection.selectedKeys().sort()).toEqual(['a', 'd']);
    expect(selection.selectedItems()).toEqual([byId('d')]);
  });

  it('clear() empties the keys and resets the shift-click anchor', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e');
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('c'), click(true));
    expect(selection.selectedKeys()).toHaveLength(3);

    selection.clear();

    expect(selection.selectedKeys()).toEqual([]);
    expect(selection.selectedItems()).toEqual([]);
    // Anchor was reset — a subsequent shift-click has nothing to range from.
    selection.onRowClick(byId('e'), click(true));
    expect(selection.selectedKeys()).toEqual(['e']);
  });

  it('a display-list shrink (a filter change) leaves selectedKeys untouched, raises hiddenSelectedCount, and selectedItems stays complete', () => {
    // The successor to the old retainVisible() cases: a filter change must no longer prune
    // anything — it only affects how much of the (unchanged) selection is currently on screen.
    const { items, selection, byId } = setupWithUniverse(
      ['a', 'b', 'c', 'd'],
      ['a', 'b', 'c', 'd'],
    );
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('c'), click(true)); // a, b, c selected

    // A filter narrows the display list to just 'c' and 'd'.
    items.set(rows('c', 'd'));

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c']);
    expect(selection.hiddenSelectedCount()).toBe(2);
    expect(
      selection
        .selectedItems()
        .map((row) => row.id)
        .sort(),
    ).toEqual(['a', 'b', 'c']);
  });

  it('the shift anchor is untouched by the display list shrinking and growing again, and a later shift-click ranges over the now-visible order', () => {
    const { items, selection, byId } = setupWithUniverse(
      ['a', 'b', 'c', 'd'],
      ['a', 'b', 'c', 'd'],
    );
    selection.onRowClick(byId('a'), click()); // anchor = 'a'

    // A filter narrows the display list, then widens again with a different order — a plain
    // items() change, with no retainAmong() call, exactly as the pages will do once they stop
    // calling retainVisible() on every keystroke (Konzept 2.4).
    items.set(rows('c', 'd'));
    items.set(rows('d', 'c', 'b', 'a'));

    selection.onRowClick(byId('c'), click(true));

    // The anchor was never reset, and the range resolves against the current (re-sorted) order:
    // 'a' sits last now, so the range from 'a' to 'c' is c-b-a, not a-b-c.
    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c']);
  });

  it('selectedItems resolves against the universe, not the display list', () => {
    const { items, selection, byId } = setupWithUniverse(
      ['a', 'b', 'c', 'd'],
      ['a', 'b', 'c', 'd'],
    );
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('b'), click());

    // Both selected rows are filtered out of the display list — under the old contract
    // (resolving against items()) selectedItems() would have silently gone empty here.
    items.set(rows('c', 'd'));

    expect(
      selection
        .selectedItems()
        .map((row) => row.id)
        .sort(),
    ).toEqual(['a', 'b']);
  });

  it('without a universe argument, selectedItems resolves against items() and hiddenSelectedCount is always 0 (the default ForeignEmoteGrid relies on)', () => {
    const { selection, byId } = setup('a', 'b', 'c');
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('b'), click());

    expect(selection.selectedItems()).toEqual([byId('a'), byId('b')]);
    expect(selection.hiddenSelectedCount()).toBe(0);
  });

  it('retainAmong() prunes against an explicitly given set, not items(), and returns the removed count', () => {
    const { items, selection, byId } = setup('a', 'b', 'c', 'd');
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('c'), click(true)); // a, b, c selected

    // items() (the filtered/current view) still holds everything — retainAmong() must not consult
    // it, only the explicit set it is handed.
    const removed = selection.retainAmong([byId('c'), byId('d')]);

    expect(removed).toBe(2);
    expect(selection.selectedKeys()).toEqual(['c']);
    // items() itself is untouched by the call.
    expect(items()).toHaveLength(4);
  });

  it('retainAmong() returns 0 and changes nothing when every selected key is still present', () => {
    const { selection, byId } = setup('a', 'b', 'c');
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('b'), click());

    const removed = selection.retainAmong([byId('a'), byId('b'), byId('c')]);

    expect(removed).toBe(0);
    expect(selection.selectedKeys().sort()).toEqual(['a', 'b']);
  });

  it('retainAmong() resets the shift anchor when the anchored row is not in the given set', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd');
    selection.onRowClick(byId('a'), click()); // anchor is 'a'

    selection.retainAmong([byId('b'), byId('c'), byId('d')]);

    expect(selection.selectedKeys()).toEqual([]);
    // Anchor was reset — a shift-click degrades to a plain toggle instead of ranging from 'a'.
    selection.onRowClick(byId('d'), click(true));
    expect(selection.selectedKeys()).toEqual(['d']);
  });

  it('a merely filtered-out (but still loaded) row survives retainAmong() against the unfiltered set', () => {
    // items() is the FILTERED display view (see the class doc), so pruning a silent reload's
    // selection against it would wrongly treat a row that only fell out of the current filter
    // window as if it had been deleted from the backing set (#94). retainAmong() takes the true,
    // unfiltered set explicitly instead, and must not drop it.
    const { items, selection, byId } = setup('a', 'b', 'c', 'd');
    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('b'), click());

    // A filter narrows items() to just 'c', 'd' — 'a' and 'b' are still loaded, only hidden.
    items.set(rows('c', 'd'));
    const unfilteredReload = rows('a', 'b', 'c', 'd');

    const removed = selection.retainAmong(unfilteredReload);

    expect(removed).toBe(0);
    expect(selection.selectedKeys().sort()).toEqual(['a', 'b']);
  });

  it('adds a whole group without dropping what was already selected', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd');

    selection.onRowClick(byId('a'), click());
    selection.selectMany([byId('c'), byId('d')]);

    expect(selection.selectedKeys().sort()).toEqual(['a', 'c', 'd']);
  });

  it('never deselects on a second group action', () => {
    // The atlas's per-band "mark all" is add-only on purpose: a toggle would let one stray click
    // wipe a hand-built selection, and the next step after this button is an irreversible delete.
    const { selection, byId } = setup('a', 'b');

    selection.selectMany([byId('a'), byId('b')]);
    selection.selectMany([byId('a'), byId('b')]);

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b']);
  });

  it('leaves an empty group alone, anchor included', () => {
    const { selection, byId } = setup('a', 'b', 'c');

    selection.onRowClick(byId('a'), click());
    selection.selectMany([]);
    // The anchor is still 'a', so this shift-click ranges a..c rather than degrading to a toggle.
    selection.onRowClick(byId('c'), click(true));

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c']);
  });

  it('moves the shift anchor to the end of the group it added', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd');

    selection.selectMany([byId('a'), byId('b')]);
    selection.onRowClick(byId('d'), click(true));

    expect(selection.selectedKeys().sort()).toEqual(['a', 'b', 'c', 'd']);
  });

  it('a shift-click after selectMany() stays additive, matching the marked anchor it leaves behind', () => {
    // Regression guard for #40: selectMany() always leaves its anchor marked, so a shift-click
    // right after it must keep extending, never flip into a deselect.
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e');

    selection.onRowClick(byId('a'), click()); // pre-existing selection, unrelated to the group below
    selection.selectMany([byId('c'), byId('d')]); // anchor moves to 'd', which selectMany leaves marked

    selection.onRowClick(byId('e'), click(true)); // range d-e must be added, not removed

    expect(selection.selectedKeys().sort()).toEqual(['a', 'c', 'd', 'e']);
  });

  it('a shift-click after a deselecting click removes the whole range, leaving marks outside it untouched', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e', 'f');

    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('f'), click()); // a and f selected individually, outside the range below
    selection.onRowClick(byId('c'), click());
    selection.onRowClick(byId('e'), click(true)); // range c-e selected, anchor 'e' is marked

    selection.onRowClick(byId('d'), click()); // deselects 'd' — anchor now points at an unmarked row
    selection.onRowClick(byId('c'), click(true)); // shift-click ranges c-d, anchor 'd' says "deselect"

    expect(selection.selectedKeys().sort()).toEqual(['a', 'e', 'f']);
  });

  it('a further shift-click after a deselecting one keeps deselecting in the same direction', () => {
    const { selection, byId } = setup('a', 'b', 'c', 'd', 'e', 'f');

    selection.onRowClick(byId('a'), click());
    selection.onRowClick(byId('f'), click(true)); // a-f all selected, anchor 'f' marked

    selection.onRowClick(byId('c'), click()); // deselect c, anchor 'c' now unmarked
    selection.onRowClick(byId('d'), click(true)); // range c-d removed, anchor 'd' unmarked

    selection.onRowClick(byId('b'), click(true)); // another shift-click: still deselecting, range b-d

    expect(selection.selectedKeys().sort()).toEqual(['a', 'e', 'f']);
  });

  it('notifies a computed() that reads the selection', () => {
    const { selection, byId } = setup('a', 'b', 'c');
    // Regression guard: with a plain mutable set instead of a signal, these stayed frozen at their
    // first value, which left the mass-delete button reading "(0)" and disabled forever.
    const keyCount = computed(() => selection.selectedKeys().length);
    const itemLabels = computed(() => selection.selectedItems().map((row) => row.label));

    expect(keyCount()).toBe(0);
    expect(itemLabels()).toEqual([]);

    selection.onRowClick(byId('a'), click());
    expect(keyCount()).toBe(1);
    expect(itemLabels()).toEqual(['A']);

    selection.onRowClick(byId('a'), click());
    expect(keyCount()).toBe(0);
    expect(itemLabels()).toEqual([]);
  });
});
