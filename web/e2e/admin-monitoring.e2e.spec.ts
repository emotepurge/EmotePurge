import { expect, test, type Page } from './support/test';

import {
  AUTH_USER,
  installLiveStub,
  mockAdminDashboardActivity,
  mockAdminHealth,
  mockAdminRateLimits,
  mockAdminRateLimitsTrend,
  mockAdminRoster,
  mockAuthMe,
  mockWorkerHealth,
  trendBursts,
} from './support/mocks';

const ADMIN_USER = { ...AUTH_USER, isGlobalAdmin: true };

const DEGRADED_TEXT = /Der Zähler-Store ist gerade nicht erreichbar/;

/** The mocks every case on this page needs; the dashboard endpoints are mocked per test. */
async function adminPage(page: Page): Promise<void> {
  await installLiveStub(page);
  await mockAuthMe(page, ADMIN_USER);
  await mockWorkerHealth(page, 'connected');
  await mockAdminHealth(page);
  await mockAdminRoster(page);
}

const activityChart = (page: Page) =>
  page
    .locator('app-admin-activity-section')
    .getByRole('group', { name: /Beobachtete Live-Channels pro Tag/ });

// The sr-only text alternative is the chart's machine-readable summary (min/max/last/known of
// total) — the cheapest honest proof that the chart was redrawn from a different series.
const activityAlternative = (page: Page) =>
  page.locator('app-admin-activity-section').getByTestId('chart-alternative');

test.describe('admin monitoring dashboard', () => {
  test.beforeEach(async ({ page }) => {
    await adminPage(page);
  });

  test('switching the range to 13 months requests range=13mo and redraws the chart', async ({
    page,
  }) => {
    const activity = await mockAdminDashboardActivity(page);
    await mockAdminRateLimits(page);
    await mockAdminRateLimitsTrend(page);

    await page.goto('/admin/monitoring');

    await expect(page.getByRole('heading', { name: 'Aktivitätsverlauf' })).toBeVisible();
    await expect(activityAlternative(page)).toContainText('30 von 30 Werten bekannt');
    await expect(page.getByText('Verfolgt: 3 / 80')).toBeVisible();
    expect(activity.ranges).toEqual(['30d']);

    await page.getByRole('radio', { name: '13 Monate' }).click();

    await expect(page.getByRole('radio', { name: '13 Monate' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    await expect(activityAlternative(page)).toContainText('396 von 396 Werten bekannt');
    expect(activity.ranges).toEqual(['30d', '13mo']);
    await expect(activityChart(page)).toBeVisible();
  });

  test('a series of only unknown days renders a gap chart without an error', async ({ page }) => {
    await mockAdminDashboardActivity(page, { value: () => null });
    await mockAdminRateLimits(page);
    await mockAdminRateLimitsTrend(page);

    await page.goto('/admin/monitoring');

    await expect(activityChart(page)).toBeVisible();
    await expect(activityAlternative(page)).toContainText('keine Daten');
    await expect(page.getByText('Ungültiger Zeitraum für das Dashboard.')).toHaveCount(0);
    await expect(page.getByText('Etwas ist schiefgelaufen')).toHaveCount(0);
  });

  test('invalid_dashboard_range shows its banner in the activity area only', async ({ page }) => {
    const activity = await mockAdminDashboardActivity(page);
    await mockAdminRateLimits(page);
    await mockAdminRateLimitsTrend(page);

    await page.goto('/admin/monitoring');
    await expect(activityChart(page)).toBeVisible();

    await mockAdminDashboardActivity(page, {
      failWith: { status: 400, errorCode: 'invalid_dashboard_range' },
    });
    await page.getByRole('radio', { name: '90 Tage' }).click();

    await expect(page.getByText('Ungültiger Zeitraum für das Dashboard.')).toHaveCount(1);
    // The other sections keep working, and the chart keeps its last series instead of vanishing.
    await expect(page.getByRole('heading', { name: 'Rate-Limit-Ausnahmen' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Twitch-IRC' })).toBeVisible();
    await expect(activityChart(page)).toBeVisible();
    expect(activity.count).toBe(1);
  });

  test('Redis degraded shows the warning and the dialog still lists the configuration', async ({
    page,
  }) => {
    await mockAdminDashboardActivity(page);
    await mockAdminRateLimits(page, { telemetryAvailable: false });
    await mockAdminRateLimitsTrend(page, { telemetryAvailable: false });

    await page.goto('/admin/monitoring');

    const section = page.locator('section').filter({
      has: page.getByRole('heading', { name: 'Rate-Limit-Ausnahmen' }),
    });
    await expect(section.getByText(DEGRADED_TEXT)).toBeVisible();
    // No counts and no chart are fabricated while the counter store is away.
    await expect(section.getByRole('group', { name: /Ablehnungen pro Minute/ })).toHaveCount(0);

    await section.getByRole('button', { name: 'Details' }).click();

    const dialog = page.getByRole('dialog', { name: 'Rate-Limit-Details' });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText(DEGRADED_TEXT)).toBeVisible();
    await expect(dialog.getByText('Navigation & Reads')).toBeVisible();
    await expect(dialog.getByText('Voting', { exact: true })).toBeVisible();
  });

  test('rejections list acute policies first and the trend chart gets one series per provider', async ({
    page,
  }) => {
    await mockAdminDashboardActivity(page);
    await mockAdminRateLimits(page, {
      policies: [
        ...[
          { name: 'InteractiveRead', acceptedLast24Hours: 5000, rejectedLast24Hours: 50 },
          {
            name: 'Voting',
            acceptedLast24Hours: 100,
            rejectedLast24Hours: 4,
            rejectedLastMinute: 2,
          },
          { name: 'Bookkeeping', acceptedLast24Hours: 900 },
        ],
      ],
      providers: [
        { providerName: 'twitch', callSource: 'twitch-helix', rateLimitedLast24Hours: 3 },
        { providerName: 'seventv', callSource: 'seventv-rest', rateLimitedLast24Hours: 9 },
      ],
    });
    await mockAdminRateLimitsTrend(page, {
      localRejections: trendBursts(4),
      providers: [
        { providerName: 'twitch', callSource: 'twitch-helix', rateLimited: trendBursts(1) },
        { providerName: 'seventv', callSource: 'seventv-rest', rateLimited: trendBursts(2) },
        { providerName: 'seventv', callSource: 'seventv-leaderboard', rateLimited: trendBursts(0) },
      ],
    });

    await page.goto('/admin/monitoring');

    const policies = page
      .locator('section')
      .filter({ has: page.getByRole('heading', { name: 'Rate-Limit-Ausnahmen' }) })
      .getByRole('list')
      .first()
      .getByRole('listitem');
    await expect(policies).toHaveCount(2);
    // Voting rejected something a minute ago, so it leads although InteractiveRead has more in 24 h.
    await expect(policies.first()).toContainText('Voting');
    await expect(policies.first()).toContainText('aktuell');
    await expect(policies.last()).toContainText('Navigation & Reads');
    await expect(policies.last()).not.toContainText('aktuell');
    await expect(policies.last()).toContainText('50 von 5.050 Entscheidungen abgewiesen');

    // A provider dimension without a single 429 (seventv-leaderboard) is left out: 1 + 2 series.
    await expect(page.getByTestId('chart-legend').getByRole('listitem')).toHaveCount(3);
  });

  test('the 60 s poll refreshes the numbers without any live event', async ({ page }) => {
    await mockAdminDashboardActivity(page);
    await mockAdminRateLimitsTrend(page);
    await mockAdminRateLimits(page, {
      policies: [{ name: 'Voting', acceptedLast24Hours: 88, rejectedLast24Hours: 12 }],
    });

    const start = Date.parse('2026-10-11T12:00:00Z');
    await page.clock.install({ time: start });
    await page.goto('/admin/monitoring');
    await expect(page.getByText('12 von 100 Entscheidungen abgewiesen')).toBeVisible();
    // install() lets time run on; stop it so only runFor() below moves the clock.
    await page.clock.pauseAt(start + 5_000);

    await mockAdminRateLimits(page, {
      policies: [{ name: 'Voting', acceptedLast24Hours: 60, rejectedLast24Hours: 40 }],
    });
    await page.clock.runFor(60_000);

    // The answer arrives in real time, but Angular schedules its render on requestAnimationFrame
    // and setTimeout, which the paused clock only fires when it is advanced: nudge it while waiting.
    await expect(async () => {
      await page.clock.runFor(100);
      await expect(page.getByText('40 von 100 Entscheidungen abgewiesen')).toBeVisible({
        timeout: 300,
      });
    }).toPass();
    await expect(page.getByText('12 von 100 Entscheidungen abgewiesen')).toHaveCount(0);
  });

  test('a hidden tab stops polling and one immediate refresh follows the return', async ({
    page,
  }) => {
    // A controllable page-visibility flag: Chromium headless always reports "visible", so the
    // document's own getters are replaced and visibilitychange is dispatched by hand.
    await page.addInitScript(() => {
      let hidden = false;
      Object.defineProperty(document, 'visibilityState', {
        configurable: true,
        get: () => (hidden ? 'hidden' : 'visible'),
      });
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
      (window as unknown as { __setHidden: (value: boolean) => void }).__setHidden = (value) => {
        hidden = value;
        document.dispatchEvent(new Event('visibilitychange'));
      };
    });
    const setHidden = (value: boolean) =>
      page.evaluate(
        (v) => (window as unknown as { __setHidden: (v: boolean) => void }).__setHidden(v),
        value,
      );

    const activity = await mockAdminDashboardActivity(page);
    const rateLimits = await mockAdminRateLimits(page);
    const trend = await mockAdminRateLimitsTrend(page);
    const counts = () => [activity.count, rateLimits.count, trend.count];

    const start = Date.parse('2026-10-11T12:00:00Z');
    await page.clock.install({ time: start });
    await page.goto('/admin/monitoring');
    await expect(activityChart(page)).toBeVisible();
    await page.clock.pauseAt(start + 5_000);
    expect(counts()).toEqual([1, 1, 1]);

    await setHidden(true);
    await page.clock.runFor(180_000);
    expect(counts()).toEqual([1, 1, 1]);

    // The visibility effect and the response handling run on the (paused) clock's timers and
    // animation frames, so advance it in small steps until the refresh went out.
    await setHidden(false);
    await expect(async () => {
      await page.clock.runFor(100);
      expect(counts()).toEqual([2, 2, 2]);
    }).toPass();

    // Back on the normal cadence afterwards, no burst of catch-up requests.
    await page.clock.runFor(60_000);
    await expect(async () => {
      await page.clock.runFor(100);
      expect(counts()).toEqual([3, 3, 3]);
    }).toPass();
  });
});
