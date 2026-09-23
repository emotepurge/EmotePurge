import { expect, test } from '@playwright/test';

import { mockAuthMe, mockLegalAvailability, mockLegalDocument } from './support/mocks';

/**
 * Issue #247: operator-supplied imprint/privacy pages, reachable without login. Every case here
 * runs as an anonymous visitor, since that is exactly the requirement under test (reachable before
 * the Twitch OAuth redirect) — an authenticated flow would prove nothing this suite doesn't already
 * cover for the footer's other links.
 */
test.describe('legal footer links', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, null);
  });

  test('the landing page shows both links when both documents are configured, and each leads to its page', async ({
    page,
  }) => {
    await mockLegalAvailability(page, { imprintAvailable: true, privacyAvailable: true });
    await mockLegalDocument(page, 'imprint', 'de', {
      html: '<h1>Impressum</h1><p>Testinhalt.</p>',
    });
    await mockLegalDocument(page, 'privacy', 'de', {
      html: '<h1>Datenschutz</h1><p>Testinhalt.</p>',
    });

    await page.goto('/welcome');

    await expect(page.getByRole('link', { name: 'Impressum' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Datenschutz' })).toBeVisible();

    await page.getByRole('link', { name: 'Impressum' }).click();
    await expect(page).toHaveURL(/\/imprint$/);
    await expect(page.getByRole('heading', { name: 'Impressum', level: 1 })).toBeVisible();
  });

  test('the landing page shows no legal links when neither document is configured', async ({
    page,
  }) => {
    await mockLegalAvailability(page); // defaults to both false

    await page.goto('/welcome');

    await expect(page.getByRole('link', { name: 'Impressum' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Datenschutz' })).toHaveCount(0);
  });

  test('shows only the configured document, hiding the other', async ({ page }) => {
    await mockLegalAvailability(page, { imprintAvailable: true, privacyAvailable: false });

    await page.goto('/welcome');

    await expect(page.getByRole('link', { name: 'Impressum' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Datenschutz' })).toHaveCount(0);
  });

  test('the login page shows the configured link too, reachable before the Twitch redirect', async ({
    page,
  }) => {
    await mockLegalAvailability(page, { imprintAvailable: true, privacyAvailable: true });

    await page.goto('/login');

    const imprintLink = page.getByRole('link', { name: 'Impressum' });
    await expect(imprintLink).toBeVisible();
    // Still on /login, nowhere near the Twitch OAuth redirect — requirement 3 of #247.
    await expect(page).toHaveURL(/\/login$/);
  });
});

test.describe('legal document page', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, null);
    await mockLegalAvailability(page, { imprintAvailable: true, privacyAvailable: true });
  });

  test('renders the German document by default, with raw HTML from the source escaped rather than executed', async ({
    page,
  }) => {
    await mockLegalDocument(page, 'privacy', 'de', {
      html: '<h1>Datenschutz</h1><p>Wir erheben keine Daten. &lt;script&gt;alert(1)&lt;/script&gt;</p>',
    });

    await page.goto('/privacy');

    await expect(page.getByRole('heading', { name: 'Datenschutz', level: 1 })).toBeVisible();
    await expect(page.getByText('Wir erheben keine Daten.')).toBeVisible();
    // The escaped sequence must render as inert text, not as a live <script> element.
    await expect(page.locator('script:has-text("alert(1)")')).toHaveCount(0);
    await expect(page.getByText('alert(1)')).toBeVisible();
  });

  test('switching the language via the account menu re-fetches and shows the English document', async ({
    page,
  }) => {
    await mockLegalDocument(page, 'imprint', 'de', {
      html: '<h1>Impressum</h1><p>Deutscher Text.</p>',
    });
    await mockLegalDocument(page, 'imprint', 'en', {
      html: '<h1>Imprint</h1><p>English text.</p>',
    });

    await page.goto('/imprint');
    await expect(page.getByRole('heading', { name: 'Impressum', level: 1 })).toBeVisible();

    await page.getByRole('button', { name: 'Einstellungen' }).click();
    await page.getByRole('radio', { name: 'English' }).click();

    await expect(page.getByRole('heading', { name: 'Imprint', level: 1 })).toBeVisible();
    await expect(page.getByText('English text.')).toBeVisible();
  });

  test('shows a "German only" notice when the English file is missing and falls back to German content', async ({
    page,
  }) => {
    await mockLegalDocument(page, 'privacy', 'de', {
      html: '<h1>Datenschutz</h1><p>Nur Deutsch.</p>',
    });
    await mockLegalDocument(page, 'privacy', 'en', {
      html: '<h1>Datenschutz</h1><p>Nur Deutsch.</p>',
      isGermanFallback: true,
    });

    await page.goto('/privacy');
    await page.getByRole('button', { name: 'Einstellungen' }).click();
    await page.getByRole('radio', { name: 'English' }).click();

    await expect(
      page.getByRole('status').filter({ hasText: 'only available in German' }),
    ).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Datenschutz', level: 1 })).toBeVisible();
  });

  test('an unconfigured document shows an explanatory empty state instead of a broken page', async ({
    page,
  }) => {
    await mockLegalDocument(page, 'imprint', 'de', null);

    await page.goto('/imprint');

    await expect(page.getByText('Kein Impressum hinterlegt')).toBeVisible();
    // No stray error text, no raw JSON, no console-only failure standing in for a real state.
    await expect(page.getByRole('heading', { level: 1 })).toHaveCount(0);
  });
});
