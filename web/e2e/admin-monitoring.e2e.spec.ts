import { expect, test, type Page } from './support/test';

import {
  AUTH_USER,
  buildDashboardLoad,
  installLiveStub,
  mockAdminDashboardActivity,
  mockAdminDashboardLoad,
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

// The route handlers that count requests run in Node, a round trip behind the browser: a negative
// assertion ("nothing was requested") is only meaningful after this real-time pause.
const settle = (page: Page) => page.waitForTimeout(400);

// Time on the page's (faked) clock.
const clockNow = (page: Page) => page.evaluate(() => Date.now());

async function advanceTo(page: Page, target: number): Promise<void> {
  const now = await clockNow(page);
  if (target > now) {
    await page.clock.runFor(target - now);
  }
}

test.describe('admin monitoring dashboard', () => {
  test.beforeEach(async ({ page }) => {
    await adminPage(page);
    // A full 24 h answer for the load section; the cases below that care register their own.
    await mockAdminDashboardLoad(page);
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

    await page.getByTestId('admin-activity').getByRole('radio', { name: '13 Monate' }).click();

    await expect(
      page.getByTestId('admin-activity').getByRole('radio', { name: '13 Monate' }),
    ).toHaveAttribute('aria-checked', 'true');
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
    await page.getByTestId('admin-activity').getByRole('radio', { name: '90 Tage' }).click();

    // Exactly one banner on the whole page, and it is scoped to the activity section; no other
    // section carries one.
    await expect(page.locator('app-notice-banner')).toHaveCount(1);
    const activitySection = page.getByTestId('admin-activity');
    const banner = activitySection.locator('app-notice-banner');
    await expect(banner).toHaveCount(1);
    await expect(banner).toHaveText(/Ungültiger Zeitraum für das Dashboard\./);
    await expect(page.getByTestId('admin-rate-limits').locator('app-notice-banner')).toHaveCount(0);
    await expect(page.getByTestId('admin-health').locator('app-notice-banner')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Rate-Limit-Ausnahmen' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Twitch-IRC' })).toBeVisible();
    // The old chart stays, and says which range it still shows.
    await expect(activitySection.getByTestId('activity-shown-range')).toContainText('30 Tage');
    // The chart keeps its last series instead of vanishing.
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
    await expect(
      page.getByTestId('admin-rate-limits').getByTestId('chart-legend').getByRole('listitem'),
    ).toHaveCount(3);
  });

  test('the 60 s poll refreshes the numbers without any live event', async ({ page }) => {
    await mockAdminDashboardActivity(page);
    await mockAdminRateLimitsTrend(page);
    const rateLimits = await mockAdminRateLimits(page, {
      policies: [{ name: 'Voting', acceptedLast24Hours: 88, rejectedLast24Hours: 12 }],
    });

    const start = Date.parse('2026-10-11T12:00:00Z');
    await page.clock.install({ time: start });
    await page.goto('/admin/monitoring');
    await expect(page.getByText('12 von 100 Entscheidungen abgewiesen')).toBeVisible();
    // install() lets time run on; stop it so only runFor()/advanceTo() below move the clock.
    await page.clock.pauseAt(start + 5_000);
    await settle(page);
    expect(rateLimits.count).toBe(1);

    // A new registration answers from now on and counts on its own log.
    const updated = await mockAdminRateLimits(page, {
      policies: [{ name: 'Voting', acceptedLast24Hours: 60, rejectedLast24Hours: 40 }],
    });

    // The poll was armed after `start`, so its first tick is at least 60 s after `start`: at
    // start + 59 s nothing may have happened yet. A 30 s cadence would already have fired.
    await advanceTo(page, start + 59_000);
    await settle(page);
    expect(updated.count).toBe(0);
    await expect(page.getByText('12 von 100 Entscheidungen abgewiesen')).toBeVisible();

    // ... and it was armed no later than the pause, so by start + 65 s it has fired once. The
    // answer arrives in real time, but Angular schedules its render on requestAnimationFrame and
    // setTimeout, which the paused clock only fires when advanced: nudge it while waiting.
    await advanceTo(page, start + 65_000);
    await expect(async () => {
      await page.clock.runFor(20);
      await expect(page.getByText('40 von 100 Entscheidungen abgewiesen')).toBeVisible({
        timeout: 300,
      });
    }).toPass({ timeout: 10_000 });
    await expect(page.getByText('12 von 100 Entscheidungen abgewiesen')).toHaveCount(0);
    expect(updated.count).toBe(1);
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
    await settle(page);
    expect(counts()).toEqual([1, 1, 1]);

    await setHidden(true);
    await page.clock.runFor(180_000);
    await settle(page);
    expect(counts()).toEqual([1, 1, 1]);

    // The visibility effect and the response handling run on the paused clock's timers and
    // animation frames, so advance it in small steps until the refresh went out — and bound the
    // virtual time that takes: a regular 60 s tick must not be what delivers it.
    const returnedAt = await clockNow(page);
    await setHidden(false);
    await expect(async () => {
      await page.clock.runFor(20);
      expect(counts().every((count) => count >= 2)).toBe(true);
    }).toPass({ timeout: 10_000 });
    expect((await clockNow(page)) - returnedAt).toBeLessThan(5_000);
    await settle(page);
    expect(counts()).toEqual([2, 2, 2]);

    // Cadence restarts at the return: quiet until a minute after it, then exactly one more.
    await advanceTo(page, returnedAt + 59_000);
    await settle(page);
    expect(counts()).toEqual([2, 2, 2]);
    await advanceTo(page, returnedAt + 65_000);
    await expect(async () => {
      await page.clock.runFor(20);
      expect(counts().every((count) => count >= 3)).toBe(true);
    }).toPass({ timeout: 10_000 });
    await settle(page);
    expect(counts()).toEqual([3, 3, 3]);

    // No burst of catch-up requests afterwards.
    await page.clock.runFor(30_000);
    await settle(page);
    expect(counts()).toEqual([3, 3, 3]);
  });
});

// --- "Last und Arbeit" (issue #381, cut B) ----------------------------------------------------

const loadSection = (page: Page) => page.getByTestId('admin-load');
const loadCharts = (page: Page) => loadSection(page).getByRole('img');
const loadRangeMenu = (page: Page) => page.getByRole('radiogroup', { name: 'Lastzeitraum' });
const chartAlternative = (page: Page, id: string) =>
  page.getByTestId(`load-chart-${id}`).getByTestId('chart-alternative');

/** The page's other dashboard endpoints, answered with their defaults. */
async function restOfDashboard(page: Page): Promise<void> {
  await mockAdminDashboardActivity(page);
  await mockAdminRateLimits(page);
  await mockAdminRateLimitsTrend(page, { localRejections: trendBursts(3) });
}

/**
 * Runs the paused clock in small steps until `check` passes: responses arrive in real time, but
 * Angular renders on timers and frames that a paused clock only fires when it is advanced.
 */
async function nudgeUntil(page: Page, check: () => Promise<void>): Promise<void> {
  await expect(async () => {
    await page.clock.runFor(20);
    await check();
  }).toPass({ timeout: 10_000 });
}

test.describe('admin monitoring: load and work', () => {
  test.beforeEach(async ({ page }) => {
    await adminPage(page);
    await restOfDashboard(page);
  });

  test('without any samples in 24 h and 13 months there is an empty state and no range menu', async ({
    page,
  }) => {
    const load = await mockAdminDashboardLoad(page, { buckets: { '24h': 0, '13mo': 0 } });

    await page.goto('/admin/monitoring');

    await expect(page.getByTestId('load-empty')).toContainText(
      'Wartet auf erste erfolgreiche Samples und den Hourly-Rollup',
    );
    await expect(loadRangeMenu(page)).toHaveCount(0);
    await expect(loadCharts(page)).toHaveCount(0);
    // The probe asked for the widest hourly range, once the default came back empty.
    expect(load.ranges).toContain('13mo');
  });

  test('empty recent samples with hourly history reveal the menu, name the hourly start and switch to 7d', async ({
    page,
  }) => {
    const load = await mockAdminDashboardLoad(page, { buckets: { '24h': 0, '13mo': 40 } });

    await page.goto('/admin/monitoring');

    await expect(loadRangeMenu(page)).toBeVisible();
    await expect(page.getByTestId('load-empty')).toContainText(
      'Für 24 Stunden liegen keine Samples vor',
    );
    await expect(page.getByTestId('load-empty')).toContainText('Stundenwerte gibt es ab');
    await expect(loadCharts(page)).toHaveCount(0);

    await loadRangeMenu(page).getByRole('radio', { name: '7 Tage' }).click();

    await expect(loadCharts(page)).toHaveCount(11);
    await expect(page.getByTestId('load-empty')).toHaveCount(0);
    expect(load.ranges).toContain('7d');
  });

  test('a history shorter than the range says how much data there is', async ({ page }) => {
    await mockAdminDashboardLoad(page, { buckets: { '24h': 40, '13mo': 30 } });

    await page.goto('/admin/monitoring');

    await expect(loadCharts(page)).toHaveCount(11);
    await expect(page.getByTestId('load-coverage')).toContainText(/bisher nur \d+ Stunden/);
    await expect(page.getByTestId('load-data-through')).toContainText('Datenstand');

    await loadRangeMenu(page).getByRole('radio', { name: '13 Monate' }).click();

    await expect(page.getByTestId('load-coverage')).toContainText(/bisher nur 30 Tage/);
    await expect(loadCharts(page)).toHaveCount(11);
  });

  test('24 h to 13 months keeps 11 charts and their series, retitles the axis and drops the rate-limit series', async ({
    page,
  }) => {
    await mockAdminDashboardLoad(page);
    await page.goto('/admin/monitoring');

    await expect(loadCharts(page)).toHaveCount(11);
    // The series of the two-line charts, named in each chart's text alternative.
    const seriesPresent = async () => {
      await expect(chartAlternative(page, 'process-cpu')).toContainText('Api');
      await expect(chartAlternative(page, 'process-cpu')).toContainText('Worker');
      await expect(chartAlternative(page, 'channels')).toContainText('Aktive Channels');
      await expect(chartAlternative(page, 'channels')).toContainText('Live-Channels');
    };
    await seriesPresent();
    await expect(page.getByTestId('load-chart-host-cpu').getByRole('heading')).toHaveText(
      'Host-CPU belegt (P95)',
    );
    // Within 24 h the rate-limit series sits on the load axis, below the charts.
    await expect(
      page.getByRole('group', { name: /Ablehnungen je \d+ min auf der Lastachse/ }),
    ).toBeVisible();
    await expect(page.getByTestId('admin-trend-hidden')).toHaveCount(0);

    await loadRangeMenu(page).getByRole('radio', { name: '13 Monate' }).click();

    await expect(page.getByTestId('load-chart-host-cpu').getByRole('heading')).toHaveText(
      'Host-CPU belegt (höchster Stunden-P95)',
    );
    await expect(loadCharts(page)).toHaveCount(11);
    await seriesPresent();
    await expect(page.getByTestId('admin-trend-hidden')).toBeVisible();
    await expect(
      page.getByRole('group', { name: /Ablehnungen je \d+ min auf der Lastachse/ }),
    ).toHaveCount(0);
    // The quota chart stays and says why it has no curve.
    await expect(page.getByTestId('load-chart-cgroup-cpu-share')).toContainText(
      'Keine Quota in Stundenwerten',
    );
  });

  test('repeated 503 and 500 keep the last answer, mark it stale, and the next success replaces it', async ({
    page,
  }) => {
    await mockAdminDashboardLoad(page, { buckets: { '24h': 289 } });
    const start = Date.parse('2026-10-11T12:00:00Z');
    await page.clock.install({ time: start });
    await page.goto('/admin/monitoring');
    await expect(loadCharts(page)).toHaveCount(11);
    await expect(chartAlternative(page, 'host-cpu')).toContainText('289 von 289 Werten bekannt');
    await page.clock.pauseAt(start + 5_000);
    await settle(page);
    await expect(page.getByTestId('load-stale')).toHaveCount(0);

    const failing = await mockAdminDashboardLoad(page, {
      respond: (_range, call) => ({ status: call === 1 ? 503 : 500 }),
    });
    await advanceTo(page, start + 65_000);
    await nudgeUntil(page, () =>
      expect(page.getByTestId('load-stale')).toBeVisible({ timeout: 300 }),
    );
    await expect(loadCharts(page)).toHaveCount(11);
    await expect(chartAlternative(page, 'host-cpu')).toContainText('289 von 289 Werten bekannt');
    expect(failing.count).toBe(1);

    // The second failure (a 500) changes nothing about what is on screen.
    await advanceTo(page, start + 125_000);
    await nudgeUntil(page, async () => expect(failing.count).toBe(2));
    await settle(page);
    await expect(page.getByTestId('load-stale')).toBeVisible();
    await expect(loadCharts(page)).toHaveCount(11);
    await expect(chartAlternative(page, 'host-cpu')).toContainText('289 von 289 Werten bekannt');

    await mockAdminDashboardLoad(page, { buckets: { '24h': 200 } });
    await advanceTo(page, start + 185_000);
    await nudgeUntil(page, () =>
      expect(page.getByTestId('load-stale')).toHaveCount(0, { timeout: 300 }),
    );
    await expect(chartAlternative(page, 'host-cpu')).toContainText('200 von 200 Werten bekannt');
  });

  test('older hours that appear later extend the chart to the left with the next poll', async ({
    page,
  }) => {
    // Same end, more history on the second request: the hourly rollup caught up in between.
    await mockAdminDashboardLoad(page, {
      respond: (range, call) =>
        range === '24h'
          ? { load: buildDashboardLoad('24h', { buckets: call === 1 ? 100 : 140 }) }
          : undefined,
    });
    const start = Date.parse('2026-10-11T12:00:00Z');
    await page.clock.install({ time: start });
    await page.goto('/admin/monitoring');
    await expect(chartAlternative(page, 'host-cpu')).toContainText('100 von 100 Werten bekannt');
    await page.clock.pauseAt(start + 5_000);
    await settle(page);

    await advanceTo(page, start + 65_000);
    await nudgeUntil(page, () =>
      expect(chartAlternative(page, 'host-cpu')).toContainText('140 von 140 Werten bekannt', {
        timeout: 300,
      }),
    );
    await expect(loadCharts(page)).toHaveCount(11);
  });

  test('a tab hidden for 30 minutes makes no load request and one immediate request on return', async ({
    page,
  }) => {
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
    const load = await mockAdminDashboardLoad(page);

    const start = Date.parse('2026-10-11T12:00:00Z');
    await page.clock.install({ time: start });
    await page.goto('/admin/monitoring');
    await expect(loadCharts(page)).toHaveCount(11);
    await page.clock.pauseAt(start + 5_000);
    await settle(page);
    expect(load.count).toBe(1);

    await setHidden(true);
    await page.clock.runFor(30 * 60_000);
    await settle(page);
    expect(load.count).toBe(1);

    const returnedAt = await clockNow(page);
    await setHidden(false);
    await nudgeUntil(page, async () => expect(load.count).toBeGreaterThanOrEqual(2));
    expect((await clockNow(page)) - returnedAt).toBeLessThan(5_000);
    await settle(page);
    expect(load.count).toBe(2);

    // No catch-up burst for the 30 missed minutes.
    await page.clock.runFor(30_000);
    await settle(page);
    expect(load.count).toBe(2);
  });

  test('render measurement: 400 buckets to all 11 charts drawn', async ({ page }, testInfo) => {
    await mockAdminDashboardLoad(page, { buckets: { '24h': 400 } });
    const samples: number[] = [];
    for (let run = 0; run < 3; run++) {
      await page.goto('about:blank');
      await page.goto('/admin/monitoring');
      await expect(loadCharts(page)).toHaveCount(11);
      await expect(loadSection(page).getByTestId('chart-alternative')).toHaveCount(11);
      for (const id of ['host-cpu', 'emoteMatches']) {
        await expect(chartAlternative(page, id)).toContainText('400 von 400 Werten bekannt');
      }
      // Milliseconds since the navigation started, taken inside the page.
      samples.push(Math.round(await page.evaluate(() => performance.now())));
    }
    samples.sort((a, b) => a - b);
    const message = `admin load render, 400 buckets x 11 charts: ${samples.join(' / ')} ms (median ${samples[1]} ms, soft limit 2000 ms)`;
    // Soft limit: logged for the protocol, deliberately no assertion.
    console.log(message);
    testInfo.annotations.push({ type: 'render-ms', description: message });
  });
});
