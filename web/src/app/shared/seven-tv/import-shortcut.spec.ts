import { describe, expect, it } from 'vitest';

import { ImportShortcutState, importShortcutDisabled } from './import-shortcut';

function state(overrides: Partial<ImportShortcutState> = {}): ImportShortcutState {
  return {
    selectionCount: 1,
    importScopeCurrent: true,
    hasActiveRun: false,
    setViewLocked: false,
    ...overrides,
  };
}

describe('importShortcutDisabled', () => {
  it('is enabled when nothing locks it', () => {
    expect(importShortcutDisabled(state())).toBe(false);
  });

  it('disables on an empty selection — the shortcut has no radiogroup to fall back to "visible"', () => {
    expect(importShortcutDisabled(state({ selectionCount: 0 }))).toBe(true);
  });

  it('disables while any 7TV-writing run is active, not just an import run', () => {
    expect(importShortcutDisabled(state({ hasActiveRun: true }))).toBe(true);
  });

  it("disables when importScopeCurrent is false — a mid-channel-switch capture would copy channel A's emotes under B's name", () => {
    expect(importShortcutDisabled(state({ importScopeCurrent: false }))).toBe(true);
  });

  it('disables while the set view is locked — its rows are not verified against the live member list', () => {
    expect(importShortcutDisabled(state({ setViewLocked: true }))).toBe(true);
  });

  it('disables when several locks apply at once', () => {
    expect(
      importShortcutDisabled(
        state({ selectionCount: 0, hasActiveRun: true, importScopeCurrent: false }),
      ),
    ).toBe(true);
  });
});
