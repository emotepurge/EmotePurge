import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockLegalAvailability,
  mockMyChannels,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

/**
 * Pins the sticky-footer layout: on a page shorter than the viewport the footer sits at its
 * bottom, on a page longer than the viewport it follows after the content instead. Operator
 * feedback (with screenshots) reported the opposite of the first case — on a short page (e.g.
 * "Meine Channels" with a handful of rows) the footer used to sit right under the content,
 * stranded mid-screen with a lot of empty space below it. `/my-votings` (`MyVotingsPage`) is used
 * here because its item count is trivial to control via the mock below; the behaviour under test
 * is AppShell's, not this page's.
 *
 * AppShell only renders the footer once `hasLegalLinks()` is true (issue #247, requirement 4), so
 * every case needs at least one legal document configured.
 */
async function mockMyVotings(page: Page, totalCount: number): Promise<void> {
  await page.route('**/api/vote-sessions/mine*', async (route) => {
    const url = new URL(route.request().url());
    const pageSize = Number(url.searchParams.get('pageSize') ?? '20');
    const items = Array.from({ length: Math.min(totalCount, pageSize) }, (_, i) => ({
      sessionId: i + 1,
      title: `Voting Runde ${i + 1}`,
      channelName: 'sensitron',
      isActive: true,
      startedAt: '2026-07-01T12:00:00Z',
      endedAt: null,
      lastVotedAt: '2026-07-05T18:45:00Z',
    }));
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        items,
        page: 1,
        pageSize,
        totalCount,
        totalPages: Math.max(1, Math.ceil(totalCount / pageSize)),
      }),
    });
  });
}

test.describe('footer placement', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await mockLegalAvailability(page, { imprintAvailable: true, privacyAvailable: true });
  });

  test('sits at the bottom of the viewport when the page content is shorter than it', async ({
    page,
  }) => {
    await mockMyVotings(page, 0);
    await page.goto('/my-votings');

    const footer = page.locator('footer');
    await expect(footer).toBeVisible();

    const viewportHeight = page.viewportSize()!.height;
    // The whole document fits without scrolling — otherwise this isn't the short-page case the
    // test claims to cover.
    const documentHeight = await page.evaluate(() => document.documentElement.scrollHeight);
    expect(documentHeight).toBeLessThanOrEqual(viewportHeight + 1);

    const box = await footer.boundingBox();
    expect(box).not.toBeNull();
    // The footer's bottom edge sits on the viewport's bottom edge, not mid-page.
    expect(box!.y + box!.height).toBeGreaterThan(viewportHeight - 2);
    expect(box!.y + box!.height).toBeLessThanOrEqual(viewportHeight + 1);
  });

  test('follows the content instead of pinning to the viewport when the page is longer than it', async ({
    page,
  }) => {
    await mockMyVotings(page, 20);
    await page.goto('/my-votings');

    const footer = page.locator('footer');
    await expect(footer).toBeAttached();

    const viewportHeight = page.viewportSize()!.height;
    const documentHeight = await page.evaluate(() => document.documentElement.scrollHeight);
    expect(documentHeight).toBeGreaterThan(viewportHeight + 100);

    // Not scrolled yet: the footer sits below the fold — proof it is laid out in normal document
    // flow after the content, not fixed to the currently visible viewport.
    const boxBeforeScroll = await footer.boundingBox();
    expect(boxBeforeScroll).not.toBeNull();
    expect(boxBeforeScroll!.y).toBeGreaterThan(viewportHeight);
    await expect(footer).not.toBeInViewport();

    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await expect(footer).toBeInViewport();
  });
});

const DOCK_EMOTES: MockEmoteUsage[] = [
  { name: 'catJAM', uses: 900 },
  { name: 'peepoSad', uses: 700 },
  { name: 'monkaW', uses: 240 },
].map((emote, index) => ({
  emoteId: `e${index + 1}`,
  emoteName: emote.name,
  sevenTvEmoteId: `7tv-${index + 1}`,
  imageUrl: `https://cdn.7tv.app/emote/${index + 1}/2x.webp`,
  totalUseCount: emote.uses,
}));

/**
 * The usage-stats page has its own `position: fixed` bottom bar, `.app-dock` (the marking/import
 * action bar), that AppShell's footer knows nothing about by default. Both are anchored to the
 * viewport, not to each other, so whenever the footer would otherwise land in the same strip the
 * dock renders into — a short page, or the bottom of a long one once scrolled all the way down —
 * they would sit on top of each other without DockClearanceService's reservation (see
 * app-shell.ts and usage-stats-page.ts). Marking a cell is what mounts the dock; three emotes and a
 * tall viewport keep the rest of the page shorter than the reservation needs to cover.
 */
async function openUsageStatsWithDockVisible(page: Page): Promise<void> {
  await installLiveStub(page);
  await mockMyChannels(page, [
    { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
  ]);
  await mockChannelPermissions(page, 'sensitron');
  await mockChannelStatus(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron');
  await mockUsageTotals(page, 'sensitron', DOCK_EMOTES);

  await page.goto('/channels/sensitron/usage-stats');
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();

  await page.getByRole('button', { name: /^monkaW ·/ }).click();
  await expect(page.getByRole('button', { name: 'Löschen (1)' })).toBeVisible();
}

test.describe('footer placement above the usage-stats action dock', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await mockLegalAvailability(page, { imprintAvailable: true, privacyAvailable: true });
  });

  test('does not sit under the dock when the page is short enough to reach the viewport bottom', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 1280, height: 2000 });
    await openUsageStatsWithDockVisible(page);

    const dock = page.locator('.app-dock');
    const links = page.locator('app-legal-footer-links');
    await expect(dock).toBeVisible();
    await expect(links).toBeVisible();

    const documentHeight = await page.evaluate(() => document.documentElement.scrollHeight);
    // Confirms this is really the short-page case the test claims: nothing to scroll, so the
    // footer's normal resting place is the viewport's bottom edge — exactly where the dock is too.
    expect(documentHeight).toBeLessThanOrEqual(2000 + 1);

    const dockBox = await dock.boundingBox();
    const linksBox = await links.boundingBox();
    expect(dockBox).not.toBeNull();
    expect(linksBox).not.toBeNull();
    // The visible link row ends above the dock's top edge, not underneath its translucent panel.
    expect(linksBox!.y + linksBox!.height).toBeLessThanOrEqual(dockBox!.y);
  });

  test('does not sit under the dock once a long page is scrolled all the way down', async ({
    page,
  }) => {
    await openUsageStatsWithDockVisible(page);

    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    // Let the dock's entrance animation (260ms, app-dock-in) settle before measuring.
    await page.waitForTimeout(400);

    const dock = page.locator('.app-dock');
    const links = page.locator('app-legal-footer-links');
    await expect(dock).toBeVisible();
    await expect(links).toBeInViewport();

    const dockBox = await dock.boundingBox();
    const linksBox = await links.boundingBox();
    expect(dockBox).not.toBeNull();
    expect(linksBox).not.toBeNull();
    expect(linksBox!.y + linksBox!.height).toBeLessThanOrEqual(dockBox!.y);
  });
});
