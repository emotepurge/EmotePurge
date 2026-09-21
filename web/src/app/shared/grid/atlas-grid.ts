import { UsageBandKey } from '../emotes/usage-bands';

/**
 * Row model for the emote atlas.
 *
 * CdkVirtualScrollViewport's fixed-size strategy needs every virtualized item to be the same
 * height, so a band header is a row like any other rather than a block wrapped around a group. It
 * gets the same height as a row of cells and spends most of it on the air above the label, which is
 * what a section heading wants anyway.
 */
export type AtlasRow<T, K extends string = UsageBandKey> =
  | { kind: 'band'; band: K; count: number; share: number }
  | { kind: 'cells'; items: T[]; startIndex: number };

/** Cell edge and gutter in px. The row height derived from them is the viewport's `itemSize`. */
export const ATLAS_CELL_PX = 64;
export const ATLAS_GAP_PX = 4;
export const ATLAS_ROW_PX = ATLAS_CELL_PX + ATLAS_GAP_PX;

/**
 * Shell header (h-14) + workspace tabs (h-10) — the top of a workspace page's own sticky layer, and
 * a cross-page contract (design doc §8.5). A sheet's sidecar pins below the page's own toolbar,
 * whose height depends on how many rows the filters wrap into, so the offset is this plus a
 * measurement rather than a single hard-coded number.
 */
export const ATLAS_STICKY_TOP_PX = 96;
export const SIDECAR_GAP_PX = 16;

/**
 * How many cells fit across a container of `width` px.
 *
 * Deliberately measured against the *container*, not `window.innerWidth`. The incumbent grid
 * computed its column count from the viewport while the shell capped content at 1024 px, so on a
 * wide monitor it laid eight cards into 992 px and stretched each one to 113 px — the emote inside
 * stayed 40 px and the rest was padding. Reading the element that actually holds the cells is what
 * makes the atlas fill the width it has.
 */
export function atlasColumns(width: number, cell = ATLAS_CELL_PX): number {
  if (!Number.isFinite(width) || width <= 0) {
    // Pre-measurement (first paint, before ResizeObserver reports): one column is the only
    // honest answer, and it is corrected within a frame.
    return 1;
  }
  // `cell` is a parameter because the ballot's cell is not the atlas's: a vote target has to stay
  // a real target, so on a narrow screen the ballot cell grows instead of the vote buttons
  // shrinking. Same packing, different edge.
  return Math.max(1, Math.floor((width + ATLAS_GAP_PX) / (Math.max(cell, 1) + ATLAS_GAP_PX)));
}

/**
 * Lays the bands out as a flat list of equal-height rows and, alongside it, the flat display order
 * the keyboard navigation and the selection work on.
 *
 * Bands are packed independently: a band never continues on a row that already holds cells of the
 * previous one, because the header would then sit next to emotes it does not describe.
 *
 * `K` defaults to the four usage bands; the usage page widens it by one group key of its own (the
 * set view's "no counts under this set" group, spec #200 8.2) that is deliberately *not* a band —
 * it has no share of usage and no place in `USAGE_BAND_ORDER`, it only needs a heading row the same
 * way a band does.
 */
export function packAtlasRows<T, K extends string = UsageBandKey>(
  bands: readonly { key: K; items: readonly T[]; share: number }[],
  columns: number,
): { rows: AtlasRow<T, K>[]; flat: T[] } {
  const safeColumns = Math.max(1, columns);
  const rows: AtlasRow<T, K>[] = [];
  const flat: T[] = [];

  for (const band of bands) {
    if (band.items.length === 0) {
      continue;
    }
    rows.push({ kind: 'band', band: band.key, count: band.items.length, share: band.share });
    for (let i = 0; i < band.items.length; i += safeColumns) {
      rows.push({
        kind: 'cells',
        items: band.items.slice(i, i + safeColumns),
        startIndex: flat.length + i,
      });
    }
    flat.push(...band.items);
  }

  return { rows, flat };
}

/**
 * Roving-tabindex movement across the atlas.
 *
 * A grid of 900 cells must not be 900 tab stops — one cell holds the tab stop and the arrow keys
 * move it (WAI-ARIA grid pattern). The move is computed on the row structure rather than by adding
 * `columns` to the index, so a band boundary in between cannot make ArrowDown land in a column the
 * user did not aim at.
 *
 * Returns the new flat index, or `null` when the key is not a navigation key or the move would
 * leave the atlas.
 */
export function moveInAtlas<T, K extends string = UsageBandKey>(
  rows: readonly AtlasRow<T, K>[],
  current: number,
  key: string,
): number | null {
  const cellRows = rows.filter(
    (row): row is Extract<AtlasRow<T, K>, { kind: 'cells' }> => row.kind === 'cells',
  );
  if (cellRows.length === 0) {
    return null;
  }

  const last = cellRows[cellRows.length - 1];
  const total = last.startIndex + last.items.length;
  if (key === 'Home') {
    return 0;
  }
  if (key === 'End') {
    return total - 1;
  }

  const rowIndex = cellRows.findIndex(
    (row) => current >= row.startIndex && current < row.startIndex + row.items.length,
  );
  if (rowIndex < 0) {
    return null;
  }
  const column = current - cellRows[rowIndex].startIndex;

  switch (key) {
    case 'ArrowRight':
      return current + 1 < total ? current + 1 : null;
    case 'ArrowLeft':
      return current > 0 ? current - 1 : null;
    case 'ArrowUp':
    case 'ArrowDown': {
      const target = cellRows[rowIndex + (key === 'ArrowDown' ? 1 : -1)];
      if (!target) {
        return null;
      }
      // A shorter target row (the last one of a band) clamps to its final cell rather than
      // refusing the move — landing somewhere is what the user asked for.
      return target.startIndex + Math.min(column, target.items.length - 1);
    }
    default:
      return null;
  }
}

/** Which virtualized row holds a flat index — what `scrollToIndex` needs before focusing a cell. */
export function atlasRowOfIndex<T, K extends string = UsageBandKey>(
  rows: readonly AtlasRow<T, K>[],
  index: number,
): number {
  return rows.findIndex(
    (row) =>
      row.kind === 'cells' && index >= row.startIndex && index < row.startIndex + row.items.length,
  );
}
