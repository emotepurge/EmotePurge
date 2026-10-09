import { expect, test } from './support/test';

import {
  AUTH_USER,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelDataSummary,
  mockChannelPermissions,
  mockChannelStatus,
  mockJoinLocked,
  mockMyChannels,
  mockPurgeOwnData,
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
  // endpoint stays for the import and restore flows (incl. the undo after a mass delete) and the admin page. The page says so
  // instead, next to the set status — for every audience that sees the usage page, 7TV editors
  // included.
  test('no resync button, a quiet note on the automatic sync instead', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', {
      canManage: false,
      canViewUsageStats: true,
      isBotActive: true,
    });
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(
      page.getByText('Das Emote-Set wird regelmäßig automatisch mit 7TV abgeglichen.'),
    ).toBeVisible();
    await expect(page.getByRole('button', { name: /synchronisieren/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Channel verlassen' })).toHaveCount(0);
  });

  // The worker no longer re-reads a deactivated channel, so the note would be false there — and a
  // 7TV editor gets no deactivation banner that could contradict it.
  test('no automatic-sync note on a deactivated channel', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', {
      canManage: false,
      canViewUsageStats: true,
      isBotActive: false,
    });
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(page.getByText(/Neu hinzugefügte Emotes/)).toHaveCount(0);
    await expect(page.getByText('Das Emote-Set wird regelmäßig')).toHaveCount(0);
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

/**
 * #245: the broadcaster's own "delete channel data" and the lock a moderator runs into afterwards.
 * The workspace needs the permission read, the live stub and (for the usage page behind it) an emote
 * set and totals; everything else falls through to the unreachable dev proxy.
 */
test.describe('broadcaster self-purge and the broadcaster lock', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);
  });

  test('only the channel own broadcaster is offered the button', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', { canPurgeAsBroadcaster: true });
    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('button', { name: 'Channel-Daten löschen' })).toBeVisible();
  });

  test('a moderator does not see the button', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', { canPurgeAsBroadcaster: false });
    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('button', { name: 'Channel verlassen' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Channel-Daten löschen' })).toHaveCount(0);
  });

  test('stays available on a deactivated channel', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', {
      canPurgeAsBroadcaster: true,
      isBotActive: false,
    });
    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('button', { name: 'Bot reaktivieren' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Channel-Daten löschen' })).toBeVisible();
  });

  test('names the numbers and the limits, unlocks on the typed name, deletes bound to the account and lands on the overview', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron', { canPurgeAsBroadcaster: true });
    await mockChannelDataSummary(page, 'sensitron', {
      emoteCount: 903,
      voteSessionCount: 4,
      liveDayCount: 31,
      tagCount: 7,
    });
    const purgeUrls = await mockPurgeOwnData(page, 'sensitron');
    await mockMyChannels(page, []);

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('button', { name: 'Channel-Daten löschen' }).click();

    const dialog = page.getByRole('dialog');
    await expect(
      dialog.getByRole('heading', { name: 'Channel-Daten unwiderruflich löschen' }),
    ).toBeVisible();
    await expect(dialog.getByText('Emotes: 903')).toBeVisible();
    await expect(dialog.getByText(/Abstimmungen: 4/)).toBeVisible();
    await expect(dialog.getByText(/Live-Tage: 31/)).toBeVisible();
    await expect(dialog.getByText(/Tags deines Mod-Teams: 7/)).toBeVisible();
    // The evidence limit and the retention promises have to be in the text, not only in the docs.
    await expect(dialog.getByText(/180 Tage nach ihrer Deaktivierung/)).toBeVisible();
    await expect(dialog.getByText(/Backups bis zu 90 Tage/)).toBeVisible();
    await expect(dialog.getByText(/deine Moderatoren nicht/)).toBeVisible();

    const confirm = dialog.getByRole('button', { name: 'Channel-Daten endgültig löschen' });
    const input = dialog.getByLabel('Zur Bestätigung den Channel-Namen eingeben');
    await expect(confirm).toBeDisabled();
    await input.fill('sensitro');
    await expect(confirm).toBeDisabled();
    await input.fill('sensitron');
    await expect(confirm).toBeEnabled();

    const purgeRequest = page.waitForRequest(
      (request) =>
        request.url().includes('/api/channels/sensitron/data') && request.method() === 'DELETE',
    );
    await confirm.click();
    await purgeRequest;

    expect(purgeUrls).toHaveLength(1);
    expect(new URL(purgeUrls[0]).searchParams.get('expectedTwitchUserId')).toBe(
      AUTH_USER.twitchUserId,
    );
    await expect(page).toHaveURL('/');
  });

  test('cancelling the dialog sends nothing', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron', { canPurgeAsBroadcaster: true });
    await mockChannelDataSummary(page, 'sensitron');
    const purgeUrls = await mockPurgeOwnData(page, 'sensitron');

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('button', { name: 'Channel-Daten löschen' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Abbrechen' }).click();

    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(purgeUrls).toHaveLength(0);
    await expect(page).toHaveURL(/usage-stats$/);
  });

  for (const [code, text] of [
    ['account_mismatch', /in einem anderen Tab mit einem anderen Konto angemeldet/],
    ['channel_identity_unresolved', /Channel-Identität konnte gerade nicht bestätigt werden/],
  ] as const) {
    test(`a 409 ${code} is shown as a message and the page stays`, async ({ page }) => {
      await mockChannelPermissions(page, 'sensitron', { canPurgeAsBroadcaster: true });
      await mockChannelDataSummary(page, 'sensitron');
      await mockPurgeOwnData(page, 'sensitron', { status: 409, body: { errorCode: code } });

      await page.goto('/channels/sensitron/usage-stats');
      await page.getByRole('button', { name: 'Channel-Daten löschen' }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByLabel('Zur Bestätigung den Channel-Namen eingeben').fill('sensitron');
      await dialog.getByRole('button', { name: 'Channel-Daten endgültig löschen' }).click();

      await expect(page.getByRole('alert').filter({ hasText: text })).toBeVisible();
      await expect(page).toHaveURL(/usage-stats$/);
      await expect(page.getByRole('button', { name: 'Channel-Daten löschen' })).toBeEnabled();
    });
  }

  // A moderator must not get a dialog (only the admin can lift a lock): the 403 is a message.
  test('a moderator reactivating a locked channel from the workspace gets the streamer text, no dialog', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron', { isBotActive: false });
    const joinUrls = await mockJoinLocked(page, 'sensitron', { status: 403 });

    await page.goto('/channels/sensitron/usage-stats');
    await page.getByRole('button', { name: 'Bot reaktivieren' }).click();

    await expect(
      page
        .getByRole('alert')
        .filter({ hasText: 'Der Streamer hat diesen Channel aus EmotePurge entfernt' }),
    ).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(joinUrls).toHaveLength(1);
  });

  test('a moderator adding a locked channel from the overview gets the streamer text, no dialog', async ({
    page,
  }) => {
    await mockMyChannels(page, [{ channelName: 'sensitron', isModerator: true, isTracked: false }]);
    const joinUrls = await mockJoinLocked(page, 'sensitron', { status: 403 });

    await page.goto('/');
    await page.getByRole('button', { name: 'Hinzufügen' }).click();

    await expect(
      page
        .getByRole('alert')
        .filter({ hasText: 'Der Streamer hat diesen Channel aus EmotePurge entfernt' }),
    ).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(joinUrls).toHaveLength(1);
  });

  test('a moderator reactivating a locked channel from the overview gets the streamer text, no dialog', async ({
    page,
  }) => {
    await mockMyChannels(page, [
      { channelName: 'sensitron', isModerator: true, isTracked: true, isBotActive: false },
    ]);
    const joinUrls = await mockJoinLocked(page, 'sensitron', { status: 403 });

    await page.goto('/');
    await page.getByRole('button', { name: 'Bot reaktivieren' }).click();

    await expect(
      page
        .getByRole('alert')
        .filter({ hasText: 'Der Streamer hat diesen Channel aus EmotePurge entfernt' }),
    ).toBeVisible();
    expect(joinUrls).toHaveLength(1);
  });

  test('an admin adding a locked channel from the overview is asked, and the retry carries the flag and the confirmed date', async ({
    page,
  }) => {
    await mockAuthMe(page, { ...AUTH_USER, isGlobalAdmin: true });
    await mockMyChannels(page, [{ channelName: 'sensitron', isModerator: true, isTracked: false }]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    const joinUrls = await mockJoinLocked(page, 'sensitron', {
      status: 409,
      lockedAtUtc: '2026-10-01T10:00:00.123456Z',
    });

    await page.goto('/');
    await page.getByRole('button', { name: 'Hinzufügen' }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText(/am 01\.10\.2026 gelöscht und gesperrt/)).toBeVisible();
    await dialog.getByRole('button', { name: 'Trotzdem hinzufügen' }).click();

    await expect(page).toHaveURL(/\/channels\/sensitron/);
    expect(joinUrls).toHaveLength(2);
    const retry = new URL(joinUrls[1]).searchParams;
    expect(retry.get('liftBroadcasterLock')).toBe('true');
    // Verbatim, at full precision: the server lifts only while the lock still carries this date.
    expect(retry.get('confirmedLockedAtUtc')).toBe('2026-10-01T10:00:00.123456Z');
  });
});
