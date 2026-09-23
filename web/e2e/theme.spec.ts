import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  installLiveStub,
  mockAuthMe,
  mockLegalAvailability,
  mockMyChannels,
  mockWorkerHealth,
} from './support/mocks';

/**
 * The theme is the one piece of UI state that has to be correct *before* Angular exists — hence the
 * FOUC test below, which is the only one here that would still catch a regression if all the others
 * were deleted. The rest of the e2e suite keeps running in a single mode: those specs assert
 * behaviour, and behaviour does not depend on the palette.
 */
test.describe('theme', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, null);
    await mockWorkerHealth(page);
    // AppShell/LoginPage/LandingPage all fetch this on construction (issue #247's footer links) —
    // without a mock the dev proxy answers with a 502, which the logo test below (the only one
    // here that asserts on the console) would otherwise fail on instead of on a theme bug.
    await mockLegalAvailability(page);
    // The overview opens /api/channels/live-events on mount (live.changed pushes); without the
    // stub the real EventSource hits a route no mock serves, and the logo test below — the only
    // one here that asserts on the console — fails on that request instead of on a theme bug.
    await installLiveStub(page);
  });

  test('follows the system preference when nothing has been chosen', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await page.goto('/welcome');

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/welcome');

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  });

  test('a system change reaches the open page while the preference is system', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/welcome');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    // No reload: matchMedia's change listener in ThemeService is what has to fire here.
    await page.emulateMedia({ colorScheme: 'light' });

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  });

  test('an explicit choice survives a reload and outranks the system preference', async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/welcome');
    await page.evaluate(() => localStorage.setItem('emotepurge.theme', 'light'));

    await page.reload();

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    // And it stays put when the system moves underneath it.
    await page.emulateMedia({ colorScheme: 'dark' });
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  });

  test('no flash of the wrong theme: the attribute is set before Angular boots', async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await page.addInitScript(() => {
      // Runs before any page script. A MutationObserver would miss an attribute that is already
      // there by the time it starts, so record the state at the first opportunity Angular could
      // possibly have had — the moment <app-root> appears in the DOM.
      const record = () => {
        (window as unknown as Record<string, unknown>)['__themeAtBoot'] =
          document.documentElement.dataset['theme'] ?? null;
      };
      document.addEventListener('DOMContentLoaded', record, { once: true });
    });

    await page.goto('/welcome');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');

    // Set by public/theme-init.js, which is a render-blocking <script src> in <head>. If it ever
    // regressed to running after bootstrap — or got blocked by the Api's script-src CSP — this
    // would be null and the user would see the dark page flash before the light one.
    const themeAtBoot = await page.evaluate(
      () => (window as unknown as Record<string, unknown>)['__themeAtBoot'],
    );
    expect(themeAtBoot).toBe('light');
  });

  test('the logo is one asset in both modes and survives the switch silently', async ({ page }) => {
    // This test used to assert that the mark SWAPS between a dark and a light PNG. It no longer
    // does: the mark is a single flat-colour SVG that carries on both grounds, so the guarantee
    // worth holding is the opposite one — the src must NOT change when the mode does, and the
    // switch must stay silent. The console check is the part that earned this test its keep;
    // neither the build nor a unit test hears a runtime image warning.
    const problems: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' || message.type() === 'warning') {
        problems.push(`${message.type()}: ${message.text()}`);
      }
    });
    page.on('pageerror', (error) => problems.push(`pageerror: ${error.message}`));

    await mockAuthMe(page, AUTH_USER);
    await mockMyChannels(page, []);
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/');
    await expect(page.locator('header img').first()).toHaveAttribute('src', /logo\.svg/);

    await page.emulateMedia({ colorScheme: 'light' });

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    await expect(page.locator('header img').first()).toHaveAttribute('src', /logo\.svg/);
    expect(problems, problems.join('\n')).toEqual([]);
  });

  test('the header menu switches the mode and persists the choice', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockMyChannels(page, []);
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.goto('/');
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    await page.getByRole('button', { name: 'Konto-Menü von Sensitron' }).click();
    // Logged in, the preferences sit one level down — set once, so they do not hold a permanent
    // place in the panel. Logged out (below) there is nothing else in it and they show directly.
    await page.getByRole('button', { name: 'Einstellungen' }).click();
    await page.getByRole('radio', { name: 'Hell' }).click();

    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    const stored = await page.evaluate(() => localStorage.getItem('emotepurge.theme'));
    expect(stored).toBe('light');
  });

  // The landing and login pages render outside the app shell and therefore outside its header.
  // Leaving them without a theme switch would mean the marketing surface — the first thing anyone
  // ever sees — offers a language switch and no way to change the mode next to it.
  for (const [name, path] of [
    ['landing', '/welcome'],
    ['login', '/login'],
  ]) {
    test(`the ${name} page carries its own theme switch`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: 'dark' });
      await page.goto(path);

      // Logged out, so the trigger is the gear rather than an avatar.
      await page.getByRole('button', { name: 'Einstellungen' }).click();
      await page.getByRole('radio', { name: 'Hell' }).click();

      await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
    });
  }
});
