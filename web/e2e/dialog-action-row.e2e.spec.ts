import { Locator, Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockEmoteSetTargets,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

const SOURCE_EMOTE: MockEmoteUsage = {
  emoteId: 'e1',
  emoteName: 'CatJAM',
  sevenTvEmoteId: '7tv-1',
  imageUrl: 'https://cdn.7tv.app/emote/1/2x.webp',
  totalUseCount: 500,
};

/**
 * Opens the target picker with 24 tracked accounts — enough rows that the picker's pane overflows a
 * standard desktop viewport even before any set-warning or notice banner adds to it (the reported
 * live case, HandOfBlood's own manager account list, is in the same range). Opened without a grid
 * selection (same as the audit harness's `usage-stats-import-target-dialog` scenario), so the scope
 * radiogroup stays out of the way and every rendered row belongs to the account list under test.
 */
async function openLongTargetPicker(page: Page): Promise<{ dialog: Locator; pane: Locator }> {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page);
  await installLiveStub(page);
  await mockChannelPermissions(page, 'sensitron');
  await mockChannelStatus(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron', 'set-1', { capacity: 1000, occupiedSlots: 3 });
  // The header "Übertragen" trigger is disabled with an empty atlas and nothing selected
  // (`usage-stats-page.ts`'s `transferButtonDisabled`) — one row is enough to enable it.
  await mockUsageTotals(page, 'sensitron', [SOURCE_EMOTE]);
  await mockEmoteSetTargets(
    page,
    Array.from({ length: 24 }, (_, index) => ({
      twitchChannelId: `target-${index}`,
      twitchLogin: `targetchannel${index}`,
      trackedChannelName: `targetchannel${index}`,
      activeEmoteSetId: `set-${index}`,
      sets: [{ id: `set-${index}`, name: `Set ${index}`, isActive: true }],
    })),
  );

  await page.goto('/channels/sensitron/usage-stats');
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
  await page.getByRole('button', { name: 'Übertragen', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();

  return { dialog, pane: page.locator('.cdk-overlay-pane.app-dialog-panel') };
}

/**
 * #226: the action row of every `DialogShell` dialog used to scroll away with the body — on a
 * dialog whose content outgrows the pane (the import target picker with many accounts is the
 * reported case), "Weiter"/"Abbrechen" sat below the fold with nothing telling the reader a next
 * step even existed. The fix sits once in `DialogShell` (`shared/ui/dialog-shell.ts`), not in this
 * dialog — this is deliberately a behavioural check (Regel 12: no CSS-class assertion), proving the
 * buttons stay reachable after the body has been scrolled all the way down, not that a particular
 * class produced that. The coarse-pointer half of the same contract lives in
 * `touch-mobile.e2e.spec.ts` instead, because `pointer: coarse` only follows from that file's own
 * Playwright project (`mobile-chrome`, `playwright.config.ts`).
 */
test('the target picker keeps its action row in view once the body has been scrolled past it (#226)', async ({
  page,
}) => {
  const { dialog, pane } = await openLongTargetPicker(page);

  // The premise: the pane genuinely has something to scroll. Without this the assertions below
  // would pass for the wrong reason (nothing ever left the viewport to begin with).
  const overflow = await pane.evaluate((el) => el.scrollHeight - el.clientHeight);
  expect(overflow).toBeGreaterThan(50);

  const actions = dialog.locator('[dialog-actions]');
  await expect(actions).toHaveCount(2);
  // Cancel first (design language §7) — asserted before the scroll below, so a regression there
  // would not hide behind the scroll-then-check sequence.
  await expect(actions.first()).toHaveText('Abbrechen');

  await pane.evaluate((el) => {
    el.scrollTop = el.scrollHeight;
  });

  await expect(actions.first()).toBeInViewport();
  await expect(actions.last()).toBeInViewport();
});

/**
 * #226 fix round: the case above alone does not catch a sticky offset that only "resolves" at the
 * very end of the scroll range (`toBeInViewport()` on the button says nothing about its own padding
 * being clipped away, and full-scroll is an *unstuck*, natural-flow position either way — the bug
 * this pins only exists while the row is actually *stuck*, i.e. any scroll position before the very
 * end). Checked instead by comparing `getBoundingClientRect()` of the action row's own wrapper (not
 * just the button inside it) against the pane's — the row's box, padding included, must stay fully
 * inside the pane's visible rectangle throughout, not just its buttons. A first version of the
 * `DialogShell` fix pinned the row with `sticky -bottom-6` (mirrored, wrongly, from what the sheet
 * handle used to carry) and only reached this while mid-scroll; this failed red against it — see
 * `docs/DECISIONS.md`.
 */
test('the target picker keeps the whole action row — not just its buttons — inside the pane while actually stuck mid-scroll (#226 fix round)', async ({
  page,
}) => {
  const { dialog, pane } = await openLongTargetPicker(page);

  const overflow = await pane.evaluate((el) => el.scrollHeight - el.clientHeight);
  expect(overflow).toBeGreaterThan(50);

  await pane.evaluate((el) => {
    el.scrollTop = Math.round((el.scrollHeight - el.clientHeight) / 2);
  });

  const actionsRow = dialog.locator('[dialog-actions]').first().locator('xpath=..');
  const rowBox = await actionsRow.evaluate((el) => el.getBoundingClientRect());
  const paneBox = await pane.evaluate((el) => el.getBoundingClientRect());

  // A 1px tolerance for sub-pixel layout rounding — anything beyond that is the row's own padding
  // (or worse, a button) sitting outside the pane's visible rectangle.
  expect(rowBox.bottom).toBeLessThanOrEqual(paneBox.bottom + 1);
  expect(rowBox.top).toBeGreaterThanOrEqual(paneBox.top - 1);
});
