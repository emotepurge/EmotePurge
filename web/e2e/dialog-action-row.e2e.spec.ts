import { expect, test } from '@playwright/test';

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
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page);
  await installLiveStub(page);
  await mockChannelPermissions(page, 'sensitron');
  await mockChannelStatus(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron', 'set-1', { capacity: 1000, occupiedSlots: 3 });
  // The header "Übertragen" trigger is disabled with an empty atlas and nothing selected
  // (`usage-stats-page.ts`'s `transferButtonDisabled`) — one row is enough to enable it, and the
  // dialog below is opened without a grid selection anyway (no scope radiogroup).
  await mockUsageTotals(page, 'sensitron', [SOURCE_EMOTE]);
  // 24 tracked accounts — enough rows that the picker's pane overflows a standard desktop viewport
  // even before any set-warning or notice banner adds to it (the reported live case, HandOfBlood's
  // manager account list, is in the same range).
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

  // Opened without a grid selection (same as the audit harness's `usage-stats-import-target-dialog`
  // scenario), so the scope radiogroup stays out of the way and every rendered row belongs to the
  // account list under test.
  await page.getByRole('button', { name: 'Übertragen', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();

  const pane = page.locator('.cdk-overlay-pane.app-dialog-panel');
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
