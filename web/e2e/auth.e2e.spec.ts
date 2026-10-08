import { expect, test } from './support/test';

import { mockAuthMe, mockTwitchLoginRedirect, mockWorkerHealth } from './support/mocks';

test.describe('unauthenticated visitor', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, null);
    await mockWorkerHealth(page);
    await mockTwitchLoginRedirect(page);
  });

  test('is redirected from the overview to /welcome and can trigger the Twitch login redirect', async ({
    page,
  }) => {
    await page.goto('/');

    // homeGuard sends anonymous visitors to the public marketing page, not straight to /login
    // (see home.guard.ts) — /login itself is still reachable directly, covered by authGuard below.
    await expect(page).toHaveURL(/\/welcome$/);
    // `.first()` because the landing carries the same call to action twice — once in the hero and
    // once at the close. Both are the same button doing the same thing; either one satisfies this
    // test, which is about the redirect, not about the page's composition.
    const loginButton = page.getByRole('button', { name: 'Mit Twitch einloggen' }).first();
    await expect(loginButton).toBeVisible();

    const loginRequest = page.waitForRequest('**/api/auth/twitch/login');
    await loginButton.click();
    await loginRequest;
  });

  test('an authGuard-protected deep link stashes the return URL before redirecting to /login', async ({
    page,
  }) => {
    await page.goto('/my-votings');

    await expect(page).toHaveURL(/\/login$/);
    const returnUrl = await page.evaluate(() => sessionStorage.getItem('ep_return_url'));
    expect(returnUrl).toBe('/my-votings');
  });
});
