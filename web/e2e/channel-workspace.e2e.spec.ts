import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockMyChannels,
  mockUsageTotals,
  mockVoteSessionList,
  mockWorkerHealth,
} from './support/mocks';

test.describe('authenticated broadcaster', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    // The usage-stats page opens /api/channels/{name}/live on mount; without the stub it would
    // reconnect-loop against a route no mock serves.
    await installLiveStub(page);
  });

  test('sees their display name and logout button in the header on the overview', async ({
    page,
  }) => {
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);

    await page.goto('/');

    await expect(page).toHaveURL('/');
    await expect(page.getByRole('link', { name: 'Login' })).toHaveCount(0);

    // Name and logout now live behind the account menu — the header itself carries one control.
    await page.getByRole('button', { name: 'Konto-Menü von Sensitron' }).click();
    await expect(page.getByText('Sensitron', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Logout' })).toBeVisible();
  });

  test('opens a tracked channel from the overview and sees its usage stats', async ({ page }) => {
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', [
      {
        emoteId: 'e1',
        emoteName: 'PogU',
        sevenTvEmoteId: '7tv-1',
        imageUrl: 'https://cdn.7tv.app/emote/1/1x.webp',
        totalUseCount: 42,
      },
    ]);

    await page.goto('/');
    // The channel name is the card's stretched-link anchor; the extra "Öffnen" button is gone.
    await page.getByRole('link', { name: '#sensitron' }).click();

    await expect(page).toHaveURL(/\/channels\/sensitron\/usage-stats$/);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    // The atlas prints no name on the cell — the sprite is the cell, and the name lives in the
    // sidecar, which describes the busiest emote until the pointer says otherwise. The cell itself
    // carries name and count in its accessible name, which is what a screen reader gets, so the
    // second assertion covers both at once. Scoped, because the narrow-viewport variant of the same
    // readout is in the DOM too, just hidden by CSS at this width.
    await expect(page.getByRole('complementary').getByText('PogU')).toBeVisible();
    await expect(page.getByRole('button', { name: 'PogU · 42×' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Channel verlassen' })).toBeVisible();
  });

  test('warns about duplicate emote names and reveals the colliding emotes on demand, only on the usage-stats tab', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron', 'set-1', {
      duplicateNames: [
        {
          name: 'ApuDrums',
          emotes: [
            {
              emoteId: 'e-dup-1',
              sevenTvEmoteId: '7tv-dup-1',
              imageUrl: 'https://cdn.7tv.app/emote/1/1x.webp',
            },
            {
              emoteId: 'e-dup-2',
              sevenTvEmoteId: '7tv-dup-2',
              imageUrl: 'https://cdn.7tv.app/emote/2/1x.webp',
            },
          ],
        },
      ],
    });
    await mockUsageTotals(page, 'sensitron', []);
    // Moving to the vote-sessions tab below (#45: the banner no longer lives in the layout, so it
    // must not follow the outlet onto a tab that never fetches active-set at all).
    await mockVoteSessionList(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(
      page.getByText('Ein Emote-Name ist im aktiven 7TV-Set mehrfach vergeben.'),
    ).toBeVisible();
    // Collapsed by default: the colliding name only appears after opening the details.
    await expect(page.getByText('ApuDrums')).toHaveCount(0);

    await page.getByRole('button', { name: 'Details anzeigen' }).click();

    await expect(page.getByText('ApuDrums')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Details ausblenden' })).toBeVisible();

    // Switching tabs unmounts UsageStatsPage — the banner (and the workspace layout does not own a
    // copy of it any more) must disappear along with it rather than lingering under a route that
    // never asked for a duplicate-name check.
    await page.getByRole('link', { name: 'Votings' }).click();
    await expect(page).toHaveURL(/\/channels\/sensitron\/vote-sessions$/);
    await expect(page.getByText('mehrfach vergeben')).toHaveCount(0);
  });

  test('shows no duplicate-name banner when every active emote name is unique', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByText('mehrfach vergeben')).toHaveCount(0);
  });

  test('a burst of sync events costs one active-set refetch, not one per event', async ({
    page,
  }) => {
    // The measured cause of issue #35: a 7TV mass delete pushes one channel.synced per removed
    // emote (~275 ms apart), and a naively undebounced reload would refetch the collision set (now
    // folded into active-set, #45) on every one of them — 22 of the 38 requests the API rejected
    // with 429 on 2026-08-28.
    await mockChannelPermissions(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    // A non-empty set id and a null syncFailureReason, both deliberately: either an empty id or a
    // failure reason would start awaitSync's own probe schedule, and a failure reason would also
    // arm the 60 s sync-failure recheck poll — either adds active-set requests unrelated to what
    // this test counts. Counted via page.route instead of going through mockActiveEmoteSet: the
    // number of calls *is* the assertion here.
    let activeSetRequests = 0;
    await page.route('**/api/channels/sensitron/emotes/active-set', (route) => {
      activeSetRequests++;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          activeEmoteSetId: 'set-1',
          capacity: 1000,
          occupiedSlots: 3,
          trackedSince: '2026-06-12T09:14:00Z',
          syncFailureReason: null,
          lastSyncAttemptAtUtc: null,
          botsExcludedSince: null,
          sharedChatSeparatedSince: null,
          duplicateNames: [],
        }),
      });
    });

    await page.goto('/channels/sensitron/usage-stats');
    // expect.poll rather than a static heading check: the heading only needs the totals response,
    // which says nothing about whether the active-set request (what is actually being counted) has
    // resolved yet.
    await expect.poll(() => activeSetRequests).toBe(1);

    // Emitted inside one evaluate so the five frames really are one burst — five separate
    // round-trips from the test runner could straddle the debounce window.
    await page.evaluate(() => {
      const emit = (window as unknown as { __emitLive: (event: unknown) => void }).__emitLive;
      for (let index = 0; index < 5; index++) {
        emit({ type: 'channel.synced', channel: 'sensitron' });
      }
    });

    // One debounce window plus slack. waitForTimeout is the honest tool here: the thing under test
    // is that nothing happens for a second.
    await page.waitForTimeout(1500);

    expect(activeSetRequests).toBe(2);
  });

  // The manual resync button is gone (the worker re-reads every active channel on its own); the
  // endpoint stays for the import/delete/undo/restore flows and the admin page. The page says so
  // instead, next to the set status — for every audience that sees the usage page, 7TV editors
  // included.
  test('no resync button, a quiet note on the automatic sync instead', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', {
      canManage: false,
      canViewUsageStats: true,
    });
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(page.getByText('Wird regelmäßig automatisch mit 7TV abgeglichen.')).toBeVisible();
    await expect(page.getByRole('button', { name: /synchronisieren/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Channel verlassen' })).toHaveCount(0);
  });

  // Would have caught a regression back to an inner scroll container: with one the window never
  // scrolls, with window scrolling the sticky layers must keep pinning (design doc §8.5).
  test('header, tabs and filter toolbar stay pinned while the emote grid scrolls with the page', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(
      page,
      'sensitron',
      Array.from({ length: 60 }, (_, i) => ({
        emoteId: `e${i}`,
        emoteName: `Emote${i}`,
        sevenTvEmoteId: `7tv-${i}`,
        imageUrl: 'https://cdn.7tv.app/emote/1/1x.webp',
        totalUseCount: 60 - i,
      })),
    );

    await page.goto('/channels/sensitron/usage-stats');
    // Scoped to the sidecar: the same readout also exists as a line for narrow viewports, hidden by
    // CSS at this width but still in the DOM, so an unscoped text locator matches twice.
    await expect(
      page.getByRole('complementary').getByText('Emote0', { exact: true }),
    ).toBeVisible();

    await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(0);

    // The three sticky layers: shell header, workspace tab bar, filter toolbar...
    await expect(page.getByRole('link', { name: 'Emote Purge' })).toBeInViewport();
    await expect(page.getByRole('link', { name: 'Nutzung' })).toBeInViewport();
    await expect(page.getByRole('textbox', { name: 'Name suchen…' })).toBeInViewport();
    // ...while the channel title above them scrolls away like normal content.
    await expect(page.getByRole('heading', { level: 1 })).not.toBeInViewport();
  });
});
