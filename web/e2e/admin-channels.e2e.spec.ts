import { expect, test, type Locator, type Page } from '@playwright/test';

import {
  AUTH_USER,
  emitLive,
  installLiveStub,
  mockAdminChannelDetail,
  mockAdminChannelList,
  mockAuthMe,
  mockChannelResync,
  mockMyChannels,
  mockPurge,
  mockWorkerHealth,
} from './support/mocks';

const ADMIN_USER = { ...AUTH_USER, isGlobalAdmin: true };

// The resync acknowledgement (#134) is a permanently mounted sr-only role="status" region plus a
// visible aria-hidden twin (docs/UI-Designsprache.md §4.5) — a bare getByText now matches both
// under strict mode, so tests that need the on-screen copy target the aria-hidden span instead.
const resyncQueuedNotice = (row: Locator) =>
  row.locator('[aria-hidden="true"]').filter({ hasText: 'Resync angestoßen' });
const resyncCompletedNotice = (row: Locator) =>
  row.locator('[aria-hidden="true"]').filter({ hasText: 'Resync abgeschlossen' });

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

test.describe('global admin on /admin/channels', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, ADMIN_USER);
    await mockWorkerHealth(page, 'connected');
    // The page opens /api/admin/live on mount; without the stub it would reconnect-loop against a
    // route no mock serves.
    await installLiveStub(page);
  });

  test('sees every tracked channel with its aggregates', async ({ page }) => {
    await mockAdminChannelList(page, [
      {
        channelName: 'handofblood',
        emoteCount: 903,
        archivedEmoteCount: 17,
        voteSessionCount: 4,
        activeVoteSessionCount: 1,
        lastSyncedAtUtc: '2026-07-31T11:00:00Z',
      },
      { channelName: 'sensitron', isBotActive: false },
    ]);

    await page.goto('/admin/channels');

    await expect(page.getByRole('link', { name: '#handofblood' })).toBeVisible();
    await expect(page.getByRole('link', { name: '#sensitron' })).toBeVisible();
    // Total/subset pairing, straight from the server — no client-side arithmetic.
    await expect(page.getByText('903 Emotes (17 archiviert)')).toBeVisible();
    await expect(page.getByText('4 Votings (1 aktiv)')).toBeVisible();
    // The inactive channel offers "Beitreten", the active one "Verlassen".
    await expect(page.getByRole('button', { name: 'Beitreten' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Verlassen' })).toBeVisible();
  });

  test('the row links into the support drilldown, which names the worker-side gap', async ({
    page,
  }) => {
    await mockAdminChannelList(page, [{ channelName: 'handofblood', emoteCount: 903 }]);
    // The interesting case, and the reason this page exists: the database row looks entirely
    // healthy while the worker does not have the channel at all.
    await mockAdminChannelDetail(
      page,
      {
        channelName: 'handofblood',
        emoteCount: 903,
        archivedEmoteCount: 17,
        lastSyncedAtUtc: '2026-08-01T11:59:00Z',
        lastInventoryChangeUtc: '2026-05-01T09:00:00Z',
      },
      { channel: null },
    );

    await page.goto('/admin/channels');
    await page.getByRole('link', { name: '#handofblood' }).click();

    // The stretched link now goes here rather than to the channel workspace — from this list the
    // next question is "what is wrong with it", not "show me its emotes".
    await expect(page).toHaveURL('/admin/channels/handofblood');
    await expect(page.getByText('Der Worker kennt diesen Channel nicht.')).toBeVisible();
    // Both timestamps are shown, and they differ: that pairing is what stops a healthy channel
    // nobody edits from reading as a stalled bot.
    await expect(page.getByText('Letzter erfolgreicher Sync')).toBeVisible();
    await expect(page.getByText('Letzte Inventaränderung')).toBeVisible();

    // The workspace stays reachable, demoted to a secondary action inside the drilldown.
    await expect(page.getByRole('link', { name: 'Zum Channel' })).toHaveAttribute(
      'href',
      '/channels/handofblood/usage-stats',
    );
  });

  test('resync is offered on the active channel only, and POSTs with a transient confirmation', async ({
    page,
  }) => {
    await mockAdminChannelList(page, [
      { channelName: 'handofblood', isBotActive: true },
      { channelName: 'sensitron', isBotActive: false },
    ]);
    await mockChannelResync(page, 'handofblood');

    await page.goto('/admin/channels');

    const activeRow = page.getByRole('listitem').filter({ hasText: '#handofblood' });
    const inactiveRow = page.getByRole('listitem').filter({ hasText: '#sensitron' });

    // The worker resolves the command against its joined channels, so an inactive row must not even
    // offer the button — a 409 is not a useful thing to hand an admin.
    await expect(activeRow.getByRole('button', { name: 'Resync' })).toBeVisible();
    await expect(inactiveRow.getByRole('button', { name: 'Resync' })).toHaveCount(0);

    const resyncRequest = page.waitForRequest(
      (request) =>
        request.method() === 'POST' &&
        request.url().includes('/api/admin/channels/handofblood/resync'),
    );
    await activeRow.getByRole('button', { name: 'Resync' }).click();
    await resyncRequest;

    // 202 means "queued": nothing on the row changes, so the inline hint is the only feedback.
    await expect(resyncQueuedNotice(activeRow)).toBeVisible();

    // The worker's channel.synced push turns the guess into a fact — this is what the live stream
    // buys over the old setTimeout-and-hope.
    await emitLive(page, { type: 'channel.synced', channel: 'handofblood' });
    await expect(resyncCompletedNotice(activeRow)).toBeVisible();
    await expect(activeRow.getByText('Resync angestoßen')).toHaveCount(0);
  });

  // #134: a role="status" region that enters the DOM together with its content announces nothing
  // to most screen reader/browser pairings — only a mutation *inside* an already-mounted region is
  // announced. Pins the fix (docs/UI-Designsprache.md §4.5): the sr-only status region on the row
  // is mounted permanently and only its text changes via @if; a click must never add a *new*
  // status node to the row, and the visible twin next to it is aria-hidden so nothing is read out
  // twice.
  test('resync acknowledgement lives in an already-mounted status region, not a freshly mounted one', async ({
    page,
  }) => {
    await mockAdminChannelList(page, [{ channelName: 'handofblood', isBotActive: true }]);
    await mockChannelResync(page, 'handofblood');

    await page.goto('/admin/channels');

    const activeRow = page.getByRole('listitem').filter({ hasText: '#handofblood' });
    await expect(activeRow.getByRole('button', { name: 'Resync' })).toBeVisible();

    // At rest: the row carries no "Resync …" status text yet. Every role="status" element
    // present now is tagged, so the region that later carries the feedback text can be checked
    // for identity rather than merely counted.
    const rowStatus = () => activeRow.getByRole('status');
    await expect(rowStatus().filter({ hasText: 'Resync' })).toHaveCount(0);
    await tagStatusRegionsAtRest(page);

    const resyncRequest = page.waitForRequest(
      (request) =>
        request.method() === 'POST' &&
        request.url().includes('/api/admin/channels/handofblood/resync'),
    );
    await activeRow.getByRole('button', { name: 'Resync' }).click();
    await resyncRequest;

    const notice = rowStatus().filter({ hasText: 'Resync' });
    await expect(notice).toHaveText('Resync angestoßen');

    // The region the text appeared in must be one that already existed at rest, not a freshly
    // created node — checked with a short timeout so a defect that only self-clears after
    // RESYNC_FEEDBACK_MS (4 s) cannot pass by outliving this assertion's window.
    await expect(notice).toHaveAttribute(AT_REST, '', { timeout: 1000 });

    // The visible copy is a separate, aria-hidden element — otherwise the same message is spoken
    // twice, once from the live region and once from the visible text.
    await expect(
      activeRow.locator('[aria-hidden="true"]').filter({ hasText: 'Resync angestoßen' }),
    ).toBeVisible();
  });

  test('a pushed reload keeps the rows on screen instead of swapping in the skeleton', async ({
    page,
  }) => {
    // Hand-rolled instead of mockAdminChannelList: the *second* response is held open on purpose,
    // because the only moment a skeleton swap is observable is while the reload is genuinely in
    // flight. `channel.synced` arrives on every periodic 7TV resync, so a swap there made the whole
    // page twitch under the admin's cursor.
    let release: (() => void) | undefined;
    let requestCount = 0;
    await page.route('**/api/admin/channels', async (route) => {
      requestCount += 1;
      if (requestCount > 1) {
        await new Promise<void>((resolve) => (release = resolve));
      }
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          channels: [
            {
              channelName: 'handofblood',
              twitchChannelId: null,
              isBotActive: true,
              createdAt: '2026-01-01T00:00:00Z',
              emoteCount: 903,
              archivedEmoteCount: 17,
              activeVoteSessionCount: 1,
              voteSessionCount: 4,
              lastSyncedAtUtc: '2026-07-31T11:00:00Z',
              liveState: 'unknown',
            },
          ],
          livePolledAtUtc: null,
        }),
      });
    });

    await page.goto('/admin/channels');
    const row = page.getByRole('listitem').filter({ hasText: '#handofblood' });
    await expect(row).toBeVisible();

    const reloadRequest = page.waitForRequest(
      (request) => request.method() === 'GET' && request.url().endsWith('/api/admin/channels'),
    );
    await emitLive(page, { type: 'channel.synced', channel: 'handofblood' });
    await reloadRequest;

    // Reload in flight: the previous list is still rendered and no shimmer block exists anywhere.
    await expect(row).toBeVisible();
    await expect(page.locator('.app-skeleton')).toHaveCount(0);
    // The refresh button is the one thing that may react — that is the documented action pattern.
    await expect(page.getByRole('button', { name: 'Aktualisieren' })).toBeDisabled();

    release?.();
    await expect(page.getByRole('button', { name: 'Aktualisieren' })).toBeEnabled();
    await expect(row).toBeVisible();
  });

  test('purge stays disabled until the channel name is typed verbatim, then deletes and reloads', async ({
    page,
  }) => {
    await mockAdminChannelList(page, [
      { channelName: 'handofblood', emoteCount: 903 },
      { channelName: 'sensitron' },
    ]);
    await mockPurge(page, 'handofblood');

    await page.goto('/admin/channels');

    await page
      .getByRole('listitem')
      .filter({ hasText: '#handofblood' })
      .getByRole('button', { name: 'Löschen' })
      .click();

    // Scoped to the dialog throughout: the row behind it shows the same emote count, and an
    // unscoped locator would happily pass on the wrong element.
    const dialog = page.getByRole('dialog');
    // The dialog names what the cascade takes with it — emotes, usage stats, sessions, votes.
    await expect(
      dialog.getByRole('heading', { name: 'Channel unwiderruflich löschen' }),
    ).toBeVisible();
    await expect(dialog.getByText('903 Emotes')).toBeVisible();

    const confirmButton = dialog.getByRole('button', { name: 'Endgültig löschen' });
    const input = dialog.getByLabel('Channel-Name zur Bestätigung');

    await expect(confirmButton).toBeDisabled();

    // A near-miss must not unlock it — that is the entire point over a plain yes/no confirm.
    await input.fill('handofbloo');
    await expect(confirmButton).toBeDisabled();
    await input.fill('HandOfBlood');
    await expect(confirmButton).toBeDisabled();

    await input.fill('handofblood');
    await expect(confirmButton).toBeEnabled();

    // Re-registered so the reload after the purge answers without the deleted channel; Playwright
    // matches route handlers in reverse registration order, so this one wins from here on.
    await mockAdminChannelList(page, [{ channelName: 'sensitron' }]);

    const purgeRequest = page.waitForRequest(
      (request) =>
        request.url().includes('/api/channels/handofblood/purge') && request.method() === 'DELETE',
    );
    await confirmButton.click();
    await purgeRequest;

    // Row gone = the list actually reloaded rather than only the dialog closing.
    await expect(page.getByRole('link', { name: '#handofblood' })).toHaveCount(0);
    await expect(page.getByRole('link', { name: '#sensitron' })).toBeVisible();
  });

  test('the drilldown names why a channel never synced', async ({ page }) => {
    await mockAdminChannelList(page, [{ channelName: 'sensitron' }]);
    await mockAdminChannelDetail(page, {
      channelName: 'sensitron',
      lastSyncedAtUtc: null,
      activeEmoteSetId: null,
      lastSyncFailureReason: 'no_active_emote_set',
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
    });

    await page.goto('/admin/channels/sensitron');

    // The sentence that used to be missing entirely: the page showed "kein Sync" and left the
    // admin to guess between four causes. `.first()` because the same short reason renders twice
    // on purpose — once as the banner above the verdict, once again in the database `<dl>` — so a
    // bare getByText resolves to two elements and fails on strict mode rather than on the assertion.
    await expect(page.getByText('7TV: kein aktives Emote-Set.').first()).toBeVisible();
    await expect(page.getByText('Letzter Sync-Versuch')).toBeVisible();
  });
});

test.describe('non-admin', () => {
  test('is redirected from /admin to the overview', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER); // isGlobalAdmin: false
    await mockWorkerHealth(page, 'connected');
    await mockMyChannels(page, []);
    await installLiveStub(page);

    await page.goto('/admin/channels');

    // adminGuard sends them to '/' rather than '/login': the session is fine, the role is not.
    await expect(page).toHaveURL('/');
    await expect(page.getByRole('link', { name: 'Admin' })).toHaveCount(0);
  });
});
