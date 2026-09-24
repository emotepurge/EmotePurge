import { expect, test, type Locator, type Page } from '@playwright/test';

import {
  AUTH_USER,
  mockAdminUsers,
  mockAuthMe,
  mockDeleteUser,
  mockInvalidateRoleCache,
  mockRevokeSessions,
  mockWorkerHealth,
} from './support/mocks';

const ADMIN_USER = { ...AUTH_USER, isGlobalAdmin: true };

// The role-cache-cleared acknowledgement (#134) is a permanently mounted sr-only role="status"
// region plus a visible aria-hidden twin (docs/UI-Designsprache.md §4.5) — a bare getByText now
// matches both under strict mode, so a test that needs the on-screen copy targets the aria-hidden
// span instead.
const roleCacheClearedNotice = (row: Locator) =>
  row.locator('[aria-hidden="true"]').filter({ hasText: 'Rollen-Cache geleert' });

// Same tagging technique as emote-import.e2e.spec.ts's "dock outcomes … region that outlives the
// dock" (#134 follow-up): tag every role="status" element present at rest, then require the
// region that later carries the feedback text to still carry that tag. A plain count-under-retry
// check can pass for the wrong reason — the feedback clears after ROLE_CACHE_FEEDBACK_MS (4 s)
// while the count assertion itself retries for only 1000 ms, so on a slow run a defect's extra
// node can self-clear inside that retry window before it is ever counted.
const AT_REST = 'data-e2e-at-rest';

async function tagStatusRegionsAtRest(page: Page): Promise<void> {
  await page.evaluate((attribute) => {
    document.querySelectorAll('[role="status"]').forEach((node) => {
      node.setAttribute(attribute, '');
    });
  }, AT_REST);
}

test.describe('global admin on /admin/users', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, ADMIN_USER);
    await mockWorkerHealth(page, 'connected');
  });

  test('sees every user with derived token status and metadata', async ({ page }) => {
    await mockAdminUsers(page, [
      {
        twitchUserId: '4711',
        twitchUsername: 'handofblood',
        displayName: 'HandOfBlood',
        hasRefreshToken: true,
        twitchAccessTokenExpiresAtUtc: '2026-07-31T16:00:00Z',
        twitchTokenScopes: 'user:read:email',
      },
      {
        twitchUserId: '4712',
        twitchUsername: 'zweitaccount',
        displayName: 'Zweitaccount',
        hasRefreshToken: false,
        sessionsValidFromUtc: '2026-07-30T10:00:00Z',
      },
    ]);

    await page.goto('/admin/users');

    await expect(page.getByText('HandOfBlood')).toBeVisible();
    await expect(page.getByText('Twitch verbunden')).toBeVisible();
    await expect(page.getByText('Keine Tokens')).toBeVisible();
    await expect(page.getByText('Scopes: user:read:email')).toBeVisible();
    await expect(page.getByText('Twitch-ID 4711')).toBeVisible();
    // A user whose sessions were revoked shows the cutoff, so the admin sees the action took.
    await expect(page.getByText(/Sessions widerrufen am/)).toBeVisible();
  });

  test('revoke asks for confirmation, POSTs, and reloads the list', async ({ page }) => {
    await mockAdminUsers(page, [
      { twitchUserId: '4712', twitchUsername: 'zweitaccount', displayName: 'Zweitaccount' },
    ]);
    await mockRevokeSessions(page, '4712');

    await page.goto('/admin/users');
    await page.getByRole('button', { name: 'Sessions widerrufen' }).click();

    // The confirm dialog names the target user; nothing is sent before confirmation.
    await expect(page.getByText(/Alle Sessions von Zweitaccount widerrufen\?/)).toBeVisible();

    const revokeRequest = page.waitForRequest(
      (request) =>
        request.method() === 'POST' &&
        request.url().includes('/api/admin/users/4712/revoke-sessions'),
    );
    const reloadRequest = page.waitForRequest(
      (request) => request.method() === 'GET' && request.url().includes('/api/admin/users?'),
    );
    await page.getByRole('dialog').getByRole('button', { name: 'Sessions widerrufen' }).click();
    await revokeRequest;
    await reloadRequest;

    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  test('clearing the role cache POSTs without a dialog and reports the removed count', async ({
    page,
  }) => {
    await mockAdminUsers(page, [
      { twitchUserId: '4712', twitchUsername: 'zweitaccount', displayName: 'Zweitaccount' },
    ]);
    await mockInvalidateRoleCache(page, '4712', 3);

    await page.goto('/admin/users');

    const cacheRequest = page.waitForRequest(
      (request) =>
        request.method() === 'POST' &&
        request.url().includes('/api/admin/users/4712/invalidate-role-cache'),
    );
    await page.getByRole('button', { name: 'Rollen-Cache leeren' }).click();
    await cacheRequest;

    // Non-destructive, so no confirmation step — the removed count is the whole feedback.
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(roleCacheClearedNotice(page.getByRole('listitem'))).toBeVisible();
  });

  // #134: a role="status" region that enters the DOM together with its content announces nothing
  // to most screen reader/browser pairings — only a mutation *inside* an already-mounted region is
  // announced. Pins the fix (docs/UI-Designsprache.md §4.5): the sr-only status region on the row
  // is mounted permanently and only its text changes via @if; a click must never add a *new*
  // status node to the row, and the visible twin next to it is aria-hidden so nothing is read out
  // twice.
  test('role-cache acknowledgement lives in an already-mounted status region, not a freshly mounted one', async ({
    page,
  }) => {
    await mockAdminUsers(page, [
      { twitchUserId: '4712', twitchUsername: 'zweitaccount', displayName: 'Zweitaccount' },
    ]);
    await mockInvalidateRoleCache(page, '4712', 3);

    await page.goto('/admin/users');

    const row = page.getByRole('listitem').filter({ hasText: 'Zweitaccount' });
    await expect(row.getByRole('button', { name: 'Rollen-Cache leeren' })).toBeVisible();

    // At rest: the row carries no "Rollen-Cache geleert" status text yet. Every role="status"
    // element present now is tagged, so the region that later carries the feedback text can be
    // checked for identity rather than merely counted.
    const rowStatus = () => row.getByRole('status');
    await expect(rowStatus().filter({ hasText: 'Rollen-Cache geleert' })).toHaveCount(0);
    await tagStatusRegionsAtRest(page);

    const cacheRequest = page.waitForRequest(
      (request) =>
        request.method() === 'POST' &&
        request.url().includes('/api/admin/users/4712/invalidate-role-cache'),
    );
    await row.getByRole('button', { name: 'Rollen-Cache leeren' }).click();
    await cacheRequest;

    const notice = rowStatus().filter({ hasText: 'Rollen-Cache geleert' });
    await expect(notice).toHaveText('Rollen-Cache geleert (3 Einträge)');

    // The region the text appeared in must be one that already existed at rest, not a freshly
    // created node — checked with a short timeout so a defect that only self-clears after
    // ROLE_CACHE_FEEDBACK_MS (4 s) cannot pass by outliving this assertion's window.
    await expect(notice).toHaveAttribute(AT_REST, '', { timeout: 1000 });

    // The visible copy is a separate, aria-hidden element — otherwise the same message is spoken
    // twice, once from the live region and once from the visible text.
    await expect(roleCacheClearedNotice(row)).toBeVisible();
  });

  test('delete stays disabled until the exact Twitch login is typed, then deletes and reloads', async ({
    page,
  }) => {
    await mockAdminUsers(page, [
      { twitchUserId: '4712', twitchUsername: 'zweitaccount', displayName: 'Zweitaccount' },
      { twitchUserId: '4713', twitchUsername: 'sensitron2', displayName: 'Sensitron2' },
    ]);
    await mockDeleteUser(page, '4712');

    await page.goto('/admin/users');

    await page
      .getByRole('listitem')
      .filter({ hasText: 'Zweitaccount' })
      .getByRole('button', { name: 'Account löschen' })
      .click();

    // Scoped to the dialog throughout, same reasoning as the channel purge test: the row behind it
    // repeats some of the same text and an unscoped locator would happily match the wrong element.
    const dialog = page.getByRole('dialog');
    await expect(
      dialog.getByRole('heading', { name: 'Account unwiderruflich löschen' }),
    ).toBeVisible();
    await expect(dialog.getByText(/Der Account von Zweitaccount wird gelöscht/)).toBeVisible();
    // Not the caller's own account, so no self-hint.
    await expect(dialog.getByText(/Du wirst damit sofort abgemeldet/)).toHaveCount(0);

    const confirmButton = dialog.getByRole('button', { name: 'Endgültig löschen' });
    const input = dialog.getByLabel('Twitch-Login zur Bestätigung');

    await expect(confirmButton).toBeDisabled();

    // A near-miss must not unlock it — that is the entire point over a plain yes/no confirm
    // (Purge-Präzedenz).
    await input.fill('zweitaccou');
    await expect(confirmButton).toBeDisabled();
    await input.fill('Zweitaccount');
    await expect(confirmButton).toBeDisabled();

    await input.fill('zweitaccount');
    await expect(confirmButton).toBeEnabled();

    // Re-registered so the reload after the deletion answers without the deleted user. Playwright
    // matches route handlers in reverse registration order, so this one wins from here on.
    await mockAdminUsers(page, [
      { twitchUserId: '4713', twitchUsername: 'sensitron2', displayName: 'Sensitron2' },
    ]);

    const deleteRequest = page.waitForRequest(
      (request) => request.url().includes('/api/admin/users/4712') && request.method() === 'DELETE',
    );
    await confirmButton.click();
    await deleteRequest;

    // Row gone = the list actually reloaded rather than only the dialog closing.
    await expect(page.getByText('Zweitaccount')).toHaveCount(0);
    await expect(page.getByText('Sensitron2')).toBeVisible();
  });

  test('deleting one’s own account names the self-hint in the dialog', async ({ page }) => {
    await mockAdminUsers(page, [
      {
        twitchUserId: AUTH_USER.twitchUserId,
        twitchUsername: 'sensitron',
        displayName: 'Sensitron',
      },
    ]);

    await page.goto('/admin/users');
    await page.getByRole('button', { name: 'Account löschen' }).click();

    // The Revoke-precedent self-hint (design decision 6 of the retention plan): allowed, but named.
    await expect(
      page.getByRole('dialog').getByText(/Du wirst damit sofort abgemeldet/),
    ).toBeVisible();
  });

  test('cancelling the dialog sends nothing', async ({ page }) => {
    await mockAdminUsers(page, [
      { twitchUserId: '4712', twitchUsername: 'zweitaccount', displayName: 'Zweitaccount' },
    ]);
    let revokeCalled = false;
    await page.route('**/revoke-sessions', (route) => {
      revokeCalled = true;
      return route.fulfill({ status: 204 });
    });

    await page.goto('/admin/users');
    await page.getByRole('button', { name: 'Sessions widerrufen' }).click();
    await page.getByRole('button', { name: 'Abbrechen' }).click();

    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(revokeCalled).toBe(false);
  });
});
