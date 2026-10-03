import { expect, test } from './support/test';

import {
  AUTH_USER,
  installLiveStub,
  mockAuthMe,
  mockChannelPermissions,
  mockWorkerHealth,
} from './support/mocks';

/**
 * The app must not render before it can speak.
 *
 * TranslocoPipe reports a late-arriving translation with ChangeDetectorRef.markForCheck(), which
 * marks the view dirty but schedules no change-detection run in a zoneless app. Anything rendered
 * before the translation file landed therefore kept its empty string until some unrelated event
 * happened to run change detection — permanently, from the user's point of view. It cost the tab
 * bar and the up-link their accessible names, both of which the design language makes a contract
 * (UI-Designsprache §8.1, §8.6).
 *
 * The guard is the boot order, so the test attacks the boot order: hold the translation file back
 * well past first paint and assert the labels are right anyway, WITHOUT interacting with the page.
 * A click would run change detection and hide the very defect this covers.
 */
test('labels are translated even when the translation file arrives late', async ({ page }) => {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page, 'connected');
  await installLiveStub(page);
  await mockChannelPermissions(page, 'sensitron', { canManage: false, canViewUsageStats: true });

  await page.route('**/i18n/de.json', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500));
    await route.continue();
  });

  await page.goto('/channels/sensitron/vote-sessions');

  // Bound into a child component's input — the case that never recovered.
  await expect(page.getByRole('link', { name: 'Votings' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Nutzung' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Zurück zu Übersicht' })).toBeVisible();
});
