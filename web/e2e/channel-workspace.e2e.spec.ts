import { expect, test, type Page } from '@playwright/test';

import {
  AUTH_USER,
  emitLive,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelScopedResync,
  mockChannelStatus,
  mockMyChannels,
  mockUsageTotals,
  mockVoteSessionList,
  mockWorkerHealth,
} from './support/mocks';

// The resync acknowledgement (#134) is a permanently mounted sr-only role="status" region plus a
// visible aria-hidden twin (docs/UI-Designsprache.md §4.5) — a bare getByText now matches both
// under strict mode, so tests that need the on-screen copy target the aria-hidden span instead.
const resyncQueuedNotice = (page: Page) =>
  page.locator('[aria-hidden="true"]').filter({ hasText: 'Resync angestoßen …' });
const resyncCompletedNotice = (page: Page) =>
  page.locator('[aria-hidden="true"]').filter({ hasText: 'Resync abgeschlossen.' });

// Same tagging technique as emote-import.e2e.spec.ts's "dock outcomes … region that outlives the
// dock" (#134 follow-up): tag every role="status" element present at rest, then require the
// region that later carries the feedback text to still carry that tag. A plain count-under-retry
// check can pass for the wrong reason — the feedback clears after RESYNC_FEEDBACK_MS (4 s) while
// the count assertion itself retries for only 1000 ms, so on a slow run a defect's extra node can
// self-clear inside that retry window before it is ever counted.
const AT_REST = 'data-e2e-at-rest';

async function tagStatusRegionsAtRest(page: Page): Promise<void> {
  await page.evaluate((attribute) => {
    document.querySelectorAll('[role="status"]').forEach((node) => {
      node.setAttribute(attribute, '');
    });
  }, AT_REST);
}

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

  test('resync reports queued and upgrades to finished when the sync event arrives', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockChannelScopedResync(page, 'sensitron');

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('button', { name: 'Neu synchronisieren' }).click();

    // The 202 only means the worker was told; the confirmation is a separate live event.
    // Scoped to the aria-hidden visible twin, not a bare role="status" query: since #134 the
    // acknowledgement's sr-only region is permanently mounted (docs/UI-Designsprache.md §4.5), and
    // the usage grid below carries its own role="status" counter — either would make a bare query
    // ambiguous under Playwright strict mode.
    await expect(resyncQueuedNotice(page)).toBeVisible();

    await emitLive(page, { type: 'channel.synced', channel: 'sensitron' });

    await expect(resyncCompletedNotice(page)).toBeVisible();
  });

  // #134: a role="status" region that enters the DOM together with its content announces nothing
  // to most screen reader/browser pairings — only a mutation *inside* an already-mounted region is
  // announced. Pins the fix (docs/UI-Designsprache.md §4.5): the sr-only status region is mounted
  // permanently and only its text changes via @if; a click must never add a *new* status node to
  // the page, and the visible twin next to it is aria-hidden so nothing is read out twice.
  test('resync acknowledgement lives in an already-mounted status region, not a freshly mounted one', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockChannelScopedResync(page, 'sensitron');

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    // The heading sits outside the loading branch and proves nothing about the sheet below it
    // (usage-atlas.e2e.spec.ts). Wait for the loading skeleton's own role="status" to go before
    // taking the baseline count, otherwise a transient loading-state region could be counted in
    // its place.
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);

    // At rest: no status region anywhere on the page carries resync wording yet. Every
    // role="status" element present now is tagged, so the region that later carries the feedback
    // text can be checked for identity rather than merely counted.
    const resyncStatus = () => page.getByRole('status').filter({ hasText: 'Resync' });
    await expect(resyncStatus()).toHaveCount(0);
    await tagStatusRegionsAtRest(page);

    await page.getByRole('button', { name: 'Neu synchronisieren' }).click();

    await expect(resyncStatus()).toHaveText('Resync angestoßen …');

    // The region the text appeared in must be one that already existed at rest, not a freshly
    // created node — checked with a short timeout so a defect that only self-clears after
    // RESYNC_FEEDBACK_MS (4 s) cannot pass by outliving this assertion's window.
    await expect(resyncStatus()).toHaveAttribute(AT_REST, '', { timeout: 1000 });

    // The visible copy is a separate, aria-hidden element — otherwise the same message is spoken
    // twice, once from the live region and once from the visible text.
    await expect(
      page.locator('[aria-hidden="true"]').filter({ hasText: 'Resync angestoßen …' }),
    ).toBeVisible();
  });

  // Regression pair for the race a shared, debounced liveReload subscription used to produce (see
  // channel-workspace-layout.ts): a stray event straddling the click either faked a finish that
  // never happened, or a real finish went missing behind the debounce window. The fix keeps the
  // confirmation on its own undebounced subscription; these two cases pin the reason it needed to
  // be undebounced, not just that it still eventually turns green.
  test('a channel.synced shortly before the resync click does not report a premature finish', async ({
    page,
  }) => {
    // Stands in for the periodic sync from #35's t=0: it used to sit in the shared debounce window
    // and fire only after the click's 202 had set resyncFeedbackKey, reporting "abgeschlossen" for a
    // resync that had barely started. The confirmation handler now only acts while
    // resyncFeedbackKey is already set, so an event that arrived before the click must never flip it
    // — however long afterwards we wait.
    await mockChannelPermissions(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockChannelScopedResync(page, 'sensitron');

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();

    // A sync from before the user touched anything on this page.
    await emitLive(page, { type: 'channel.synced', channel: 'sensitron' });

    await page.getByRole('button', { name: 'Neu synchronisieren' }).click();
    await expect(resyncQueuedNotice(page)).toBeVisible();

    // Longer than CHANNEL_RELOAD_DEBOUNCE_MS (1000 ms): the old bug needed exactly this wait for the
    // stale event to fall out of the debounce window and fire.
    await page.waitForTimeout(1500);
    await expect(resyncQueuedNotice(page)).toBeVisible();
    await expect(page.getByText('Resync abgeschlossen.')).toHaveCount(0);
  });

  test('a channel.synced after the resync click upgrades to finished without waiting out a debounce window', async ({
    page,
  }) => {
    // The other half of the same bug: during a dense burst (7TV mass delete, ~275 ms apart) the
    // shared debounce window never elapsed, so a resync started mid-burst could lose its
    // confirmation entirely once RESYNC_FEEDBACK_MS cleared "angestoßen" back off screen. The
    // confirmation subscription only sets a signal — it makes no HTTP request — so it no longer
    // needs debouncing at all; the tight timeout below is what tells this apart from the old,
    // merely-eventually-correct behaviour.
    await mockChannelPermissions(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockChannelScopedResync(page, 'sensitron');

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('button', { name: 'Neu synchronisieren' }).click();
    await expect(resyncQueuedNotice(page)).toBeVisible();

    await emitLive(page, { type: 'channel.synced', channel: 'sensitron' });

    await expect(resyncCompletedNotice(page)).toBeVisible({ timeout: 500 });
  });

  // The endpoint sits behind the wider permission check on purpose: the person who just added an
  // emote and is wondering why it is missing is usually the channel's 7TV editor.
  test('a 7TV editor sees the resync button but not the leave button', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', {
      canManage: false,
      canViewUsageStats: true,
    });
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(page.getByRole('button', { name: 'Neu synchronisieren' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Channel verlassen' })).toHaveCount(0);
  });

  test('a second resync inside the cooldown says so instead of failing silently', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
    await mockChannelScopedResync(page, 'sensitron', 429);

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('button', { name: 'Neu synchronisieren' }).click();

    // The dedicated error code, not the rate limiter's generic 429 message — that difference is
    // the whole reason the cooldown answers with a body.
    await expect(
      page.getByText('Für diesen Channel läuft bereits ein Resync. Bitte kurz warten.'),
    ).toBeVisible();
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
