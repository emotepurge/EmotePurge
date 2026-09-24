import { expect, test } from '@playwright/test';

import { mockAuthMe, mockContactConfig, mockContactSubmit, mockTurnstile } from './support/mocks';

/**
 * `/contact` (docs/DECISIONS.md 2026-09-24, "contact form"): reachable without being logged in,
 * same reasoning as `legal-pages.e2e.spec.ts`. Every case runs as an anonymous visitor.
 */
test.describe('contact form', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, null);
  });

  test('shows a notice pointing at the imprint instead of a form when the feature is not configured', async ({
    page,
  }) => {
    await mockContactConfig(page, { available: false });

    await page.goto('/contact');

    await expect(page.getByRole('heading', { name: 'Kontakt' })).toBeVisible();
    await expect(page.locator('form')).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Impressum' })).toBeVisible();
  });

  test('sends a message and shows a success message in place of the form', async ({ page }) => {
    await mockContactConfig(page);
    await mockTurnstile(page);
    await mockContactSubmit(page, { outcome: 'success' });

    await page.goto('/contact');

    await page.getByLabel('E-Mail-Adresse').fill('jane@example.com');
    await page.getByLabel('Nachricht').fill('This is my message to the operator, long enough.');

    // The Turnstile stub resolves the challenge on its own next tick — the submit button clears
    // its disabled state once that lands, without this spec touching the widget itself.
    const submit = page.getByRole('button', { name: 'Nachricht senden' });
    await expect(submit).toBeEnabled();
    await submit.click();

    await expect(page.getByText('Danke — deine Nachricht wurde versendet.')).toBeVisible();
    await expect(page.locator('form')).toHaveCount(0);
  });

  test('shows the captcha-failure message and keeps the form when the API rejects the token', async ({
    page,
  }) => {
    await mockContactConfig(page);
    await mockTurnstile(page);
    await mockContactSubmit(page, { outcome: 'error', status: 400, errorCode: 'contact_captcha_failed' });

    await page.goto('/contact');

    await page.getByLabel('E-Mail-Adresse').fill('jane@example.com');
    await page.getByLabel('Nachricht').fill('This is my message to the operator, long enough.');

    const submit = page.getByRole('button', { name: 'Nachricht senden' });
    await expect(submit).toBeEnabled();
    await submit.click();

    await expect(
      page.getByText('Die Sicherheitsabfrage konnte nicht bestätigt werden. Bitte versuch es erneut.'),
    ).toBeVisible();
    await expect(page.locator('form')).toBeVisible();
  });
});
