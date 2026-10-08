import { expect, test } from './support/test';

import {
  AUTH_USER,
  installLiveStub,
  mockActiveEmoteSet,
  mockAdminChannelList,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockUsageTotals,
  mockVoteSessionList,
  mockVoteSessionResults,
  mockWorkerHealth,
} from './support/mocks';

const SESSION = { id: 7, title: 'Frühjahrsputz' };

test.describe('hierarchical back navigation', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
  });

  /**
   * The regression this exists for: the detail page is regularly opened by deep link (right after
   * creating a session, from My-Votings, from the admin area), so the browser's history has no
   * list page to go back to. A history-back affordance would look fine in a click-through test and
   * be wrong here — entering directly is therefore how this test enters.
   */
  test('leads from a deep-linked vote session up to that channel’s session list', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockVoteSessionResults(page, 'sensitron', SESSION, [
      { emoteId: 'e1', emoteName: 'PogU' },
    ]);
    await mockVoteSessionList(page, 'sensitron', [SESSION]);

    await page.goto('/channels/sensitron/vote-sessions/7');
    await expect(page.getByRole('heading', { name: 'Frühjahrsputz' })).toBeVisible();

    await page.getByRole('link', { name: 'Zurück zu Votings' }).click();

    await expect(page).toHaveURL('/channels/sensitron/vote-sessions');
    // On the list the session is a row link, not a heading — that it is listed at all is what
    // proves we landed on the list of THIS channel.
    await expect(page.getByRole('link', { name: 'Frühjahrsputz' })).toBeVisible();

    // It navigated FORWARD to the parent instead of popping the history — that is the whole
    // difference to a history.back() affordance, and browser-back now returns to the session.
    await page.goBack();
    await expect(page).toHaveURL('/channels/sensitron/vote-sessions/7');
  });

  test('leads from the channel workspace up to the overview', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('link', { name: 'Zurück zu Übersicht' }).click();

    await expect(page).toHaveURL('/');
  });

  // The admin area's tab bar only navigates within /admin — it used to be a dead end.
  test('leads from the admin area up to the overview', async ({ page }) => {
    await mockAuthMe(page, { ...AUTH_USER, isGlobalAdmin: true });
    await mockAdminChannelList(page, []);

    await page.goto('/admin/channels');
    await expect(page.getByRole('heading', { name: 'Admin-Bereich' })).toBeVisible();

    await page.getByRole('link', { name: 'Zurück zu Übersicht' }).click();

    await expect(page).toHaveURL('/');
  });
});
