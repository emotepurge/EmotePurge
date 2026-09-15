import { expect, test } from '@playwright/test';

import { AUTH_USER, mockAuthMe, mockMyChannels } from './support/mocks';

/**
 * Optimistic language switch with rollback: the switcher should jump immediately on click and
 * snap back to whatever is actually rendered if the locale fails to load. `language.service.spec.ts`
 * covers `selectedLang` itself; only a real DOM binding can show `SegmentedControl` failing to
 * reflect a reverted pick.
 */
test.describe('language switch: a failed locale load snaps the switcher back', () => {
  test('clicking English while its locale file fails to load leaves German selected and rendered', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockMyChannels(page, []);

    // Held open until released below, so the optimistic English state can be asserted before any
    // attempt fails. Every attempt (including Transloco's two default retries) hits this same gate.
    let releaseFailure!: () => void;
    const failureGate = new Promise<void>((resolve) => {
      releaseFailure = resolve;
    });
    await page.route('**/i18n/en.json', async (route) => {
      await failureGate;
      await route.abort('failed');
    });

    await page.goto('/');
    await expect(page.locator('html')).toHaveAttribute('lang', 'de');

    await page.getByRole('button', { name: 'Konto-Menü von Sensitron' }).click();
    await page.getByRole('button', { name: 'Einstellungen' }).click();

    const german = page.getByRole('radio', { name: 'Deutsch' });
    const english = page.getByRole('radio', { name: 'English' });
    await expect(german).toBeChecked();
    await expect(english).not.toBeChecked();

    await english.click();
    // The optimistic switch, rendered before the load has had any chance to fail.
    await expect(english).toBeChecked();

    releaseFailure();

    await expect(german).toBeChecked({ timeout: 10000 });
    await expect(english).not.toBeChecked();
    await expect(page.locator('html')).toHaveAttribute('lang', 'de');
    // The stored preference must not point at the failed language either, or the next boot would
    // retry the same broken load.
    const stored = await page.evaluate(() => localStorage.getItem('ep_lang'));
    expect(stored).toBe('de');
  });
});
