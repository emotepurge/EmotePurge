import { expect, test } from './support/test';

import { AUTH_USER, mockAuthMe, mockTwitchLoginRedirect, mockWorkerHealth } from './support/mocks';

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

test.describe('login page', () => {
  test.beforeEach(async ({ page }) => {
    await mockWorkerHealth(page);
    await mockTwitchLoginRedirect(page);
  });

  test('offers the Twitch login and no app link to an anonymous visitor', async ({ page }) => {
    await mockAuthMe(page, null);
    await page.goto('/login');

    await expect(page.getByRole('button', { name: 'Mit Twitch einloggen' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Zur App' })).toHaveCount(0);
  });

  test('swaps the login button for a link into the app when logged in, without redirecting', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await page.goto('/login');

    const appLink = page.getByRole('link', { name: 'Zur App' });
    await expect(appLink).toHaveAttribute('href', '/');
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Du bist schon eingeloggt');
    await expect(page.getByText('Angemeldet als Sensitron')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Mit Twitch einloggen' })).toHaveCount(0);
    // The scopes stay readable for everyone, and there is no redirect.
    await expect(page.getByText('user:read:moderated_channels', { exact: true })).toBeVisible();
    await expect(page).toHaveURL(/\/login$/);

    await appLink.click();
    await expect(page).not.toHaveURL(/\/login/);
  });
});
