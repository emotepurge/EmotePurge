import type { Page } from './test';

export interface FocusClearance {
  /** The focused control's box. */
  controlTop: number;
  controlBottom: number;
  /** The sticky action row's top edge. */
  footerTop: number;
  /** The sheet handle's bottom edge; null when the dialog shows no handle (fine pointer). */
  handleBottom: number | null;
}

/**
 * Where the focused control sits relative to the dialog's sticky layers, measured from the live
 * layout. Null when focus is not on a control in the dialog body (nothing, or the action row itself).
 */
export async function focusClearance(page: Page): Promise<FocusClearance | null> {
  return page.evaluate(() => {
    const active = document.activeElement;
    const dialog = active?.closest('[role="dialog"]');
    const footer = dialog?.querySelector('[dialog-actions]')?.parentElement;
    if (!active || !dialog || !footer || footer.contains(active)) {
      return null;
    }
    const box = active.getBoundingClientRect();
    const handle = dialog.querySelector('[data-sheet-handle][aria-hidden="true"]');
    return {
      controlTop: box.top,
      controlBottom: box.bottom,
      footerTop: footer.getBoundingClientRect().top,
      handleBottom: handle ? handle.getBoundingClientRect().bottom : null,
    };
  });
}
