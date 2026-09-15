import { expect, test } from '@playwright/test';

import { mockAuthMe, mockTwitchLoginRedirect, mockWorkerHealth } from './support/mocks';

/**
 * The public page makes claims, and claims are the one kind of content that rots silently: nothing
 * breaks, nothing throws, the page just starts saying something that is no longer true. These cases
 * pin the three promises the rebuild put on the page — the diagram is declared as a diagram, the
 * frictions are named rather than hidden, and the login lists the exact scopes the backend asks
 * for. A future edit may change any of them; it should not be able to do so by accident.
 */

test.describe('landing', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, null);
    await mockWorkerHealth(page);
    await mockTwitchLoginRedirect(page);
  });

  test('declares the set diagram as schematic and labels its four bands', async ({ page }) => {
    await page.goto('/welcome');

    const figure = page.getByRole('figure');
    // Without this sentence the figure is a fabricated product screenshot, which PRODUCT.md
    // forbids ("Evidence on Hand"). It is the caption that makes the diagram honest.
    await expect(figure).toContainText('Schematische Darstellung');

    // The same four band names the real atlas uses, from the same translation keys — a visitor
    // meets this vocabulary again the moment they are logged in.
    for (const band of ['Tragend', 'Regelmäßig', 'Selten', 'Nie benutzt']) {
      await expect(figure.getByRole('heading', { name: band })).toBeVisible();
    }
  });

  test('ranks the three stages by the word that governs each', async ({ page }) => {
    await page.goto('/welcome');
    const does = page.locator('#does');

    // Measuring runs on its own, voting is opt-in, deleting cannot be undone. The old page showed
    // four equal feature cards, which claimed all of them were the same kind of thing.
    await expect(does).toContainText('dauerhaft');
    await expect(does).toContainText('optional');
    await expect(does).toContainText('endgültig');
  });

  test('names the two frictions instead of hiding them', async ({ page }) => {
    await page.goto('/welcome');
    const how = page.locator('#how');

    // Nothing is measured before the join…
    await expect(how).toContainText('Die ersten Tage sind deshalb leer');
    // …and the 7TV token has to be copied by hand, because 7TV offers no login redirect.
    await expect(how).toContainText('Entwicklertools');
  });

  test('links the real source repository in a new tab', async ({ page }) => {
    await page.goto('/welcome');

    // The one reassurance on this page a visitor can verify in a single click. A dead or private
    // link would discredit the two claims standing next to it.
    const source = page.getByRole('link', { name: 'Auf GitHub ansehen' });
    await expect(source).toHaveAttribute('href', 'https://github.com/emotepurge/EmotePurge');
    await expect(source).toHaveAttribute('rel', 'noopener');
  });

  test('the login page lists exactly the scopes the backend requests', async ({ page }) => {
    await page.goto('/login');

    // Mirrors TwitchOAuthDefaults.RequestedScopes. The visitor sees the same three lines on
    // Twitch's own consent screen one click later; a page that undersells them loses its
    // credibility exactly where it matters.
    for (const scope of [
      'user:read:email',
      'user:read:moderated_channels',
      'user:read:subscriptions',
    ]) {
      await expect(page.getByText(scope, { exact: true })).toBeVisible();
    }
    await expect(page.getByText('Alle drei sind Leserechte', { exact: false })).toBeVisible();
  });
});
