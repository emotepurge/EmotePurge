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
  mockSetWarning,
  mockSevenTvGql,
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
    // Wait for the empty state, not just the footer: the page renders `<app-skeleton-rows>` while
    // `sessionsResource` is loading, and skeleton rows carry their own height. Measuring geometry
    // before it resolves would size the short-page case against a transient layout, not the one
    // this test claims to cover.
    await expect(page.locator('app-empty-state')).toBeVisible();

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
    // Same reasoning as the short-page test above: wait for a real row before measuring
    // scrollHeight, or this can pass while the page still shows `<app-skeleton-rows>`, whose own
    // height happens to already clear the viewport for an unrelated reason.
    await expect(page.getByText('Voting Runde 1', { exact: true })).toBeVisible();

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

/**
 * Waits for `.app-dock` to be fully rendered before any bounding-box measurement reads it — two
 * things race the dock's own mount, and measuring through either would make the assertion about
 * the wrong moment rather than about the layout: the 260ms entrance animation (`app-dock-in`,
 * `translateY`) still moves the element for a beat after it becomes visible, and
 * DockClearanceService's reservation (usage-stats-page.ts's `ResizeObserver` on the dock element)
 * follows the dock's own render on its own tick rather than in the same synchronous pass. Waiting
 * for the footer's actual computed `padding-bottom` to match the dock's rendered height is a
 * direct check on the one invariant every test below asserts on, rather than a guessed delay.
 */
async function waitForDockSettled(page: Page): Promise<void> {
  const dock = page.locator('.app-dock');
  await expect(dock).toBeVisible();
  await dock.evaluate((element) =>
    Promise.all(element.getAnimations().map((animation) => animation.finished)),
  );
  await page.waitForFunction(() => {
    const dockElement = document.querySelector('.app-dock');
    const footerElement = document.querySelector('footer');
    if (!dockElement || !footerElement) {
      return false;
    }
    const paddingBottom = parseFloat(getComputedStyle(footerElement).paddingBottom || '0');
    return Math.abs(paddingBottom - dockElement.getBoundingClientRect().height) < 1;
  });
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
    await waitForDockSettled(page);

    const dock = page.locator('.app-dock');
    const links = page.locator('app-legal-footer-links');
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
    await waitForDockSettled(page);

    const dock = page.locator('.app-dock');
    const links = page.locator('app-legal-footer-links');
    await expect(links).toBeInViewport();

    const dockBox = await dock.boundingBox();
    const linksBox = await links.boundingBox();
    expect(dockBox).not.toBeNull();
    expect(linksBox).not.toBeNull();
    expect(linksBox!.y + linksBox!.height).toBeLessThanOrEqual(dockBox!.y);
  });

  test('does not sit under an expanded dock — a run with failed rows grows it well past a bare mark-count strip', async ({
    page,
  }) => {
    const emotes: MockEmoteUsage[] = [
      { name: 'catJAM', uses: 900 },
      { name: 'peepoSad', uses: 700 },
      { name: 'monkaW', uses: 240 },
      { name: 'KEKW', uses: 120 },
      { name: 'Pog', uses: 90 },
      { name: 'Sadge', uses: 40 },
      { name: 'Bedge', uses: 12 },
      { name: 'Copium', uses: 3 },
    ].map((emote, index) => ({
      emoteId: `x${index + 1}`,
      emoteName: emote.name,
      sevenTvEmoteId: `7tv-x${index + 1}`,
      imageUrl: `https://cdn.7tv.app/emote/x${index + 1}/2x.webp`,
      totalUseCount: emote.uses,
    }));

    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', emotes);
    await mockSetWarning(page, 'sensitron');
    // A plain rejection with no `extensions.code` fails every row without aborting the run
    // (see emote-import.e2e.spec.ts's identical case for the same engine) — the whole selection
    // queues up as failed, and RunProgressPanel's failedItems() list is exactly what used to grow
    // `.app-dock` past the fixed 160px DockClearanceService reservation used to assume.
    await mockSevenTvGql(page, () => ({
      errors: [{ message: '7TV had an internal error' }],
    }));
    await page.clock.install();

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();

    // The toolbar's own mark-all button — unlike usage-atlas.e2e.spec.ts's never-used-band case,
    // this list has no dead band (every emote here has at least one use), so there is no second,
    // identically labelled per-band button to disambiguate against.
    await page.getByRole('button', { name: 'alle markieren' }).click();

    const deleteButton = page.getByRole('button', { name: `Löschen (${emotes.length})` });
    await expect(deleteButton).toBeVisible();
    await deleteButton.click();

    const dialog = page.getByRole('dialog');
    const confirmButton = dialog.getByRole('button', { name: 'Löschen starten' });
    await expect(confirmButton).toBeEnabled();
    await confirmButton.click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Generous, same reasoning as the import-flow tests pacing the same engine: RUN_DELAY_MS
    // (275ms) sits between each of the eight rows, all behind the frozen clock.
    await page.clock.runFor(6000);

    const dock = page.locator('.app-dock');
    // Proof this is really the expanded-dock case the test claims, not just a taller viewport:
    // every marked emote failed and shows up in the run's failure list.
    await expect(dock.getByRole('alert').locator('li')).toHaveCount(emotes.length);

    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await waitForDockSettled(page);

    const links = page.locator('app-legal-footer-links');
    await expect(links).toBeInViewport();

    const dockBox = await dock.boundingBox();
    const linksBox = await links.boundingBox();
    expect(dockBox).not.toBeNull();
    expect(linksBox).not.toBeNull();
    // The regression this guards against: a dock this tall must still clear the footer by its own
    // actual height, not by the old fixed guess that a run with several failed rows would exceed.
    expect(dockBox!.height).toBeGreaterThan(160);
    expect(linksBox!.y + linksBox!.height).toBeLessThanOrEqual(dockBox!.y);
  });
});
