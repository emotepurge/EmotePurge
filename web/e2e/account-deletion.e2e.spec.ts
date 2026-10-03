import { expect, test } from '@playwright/test';

import { AUTH_USER, mockWorkerHealth } from './support/mocks';

/**
 * Self-service account deletion (GDPR Art. 17). `/api/auth/me` answers GET (the session probe) and
 * DELETE (the deletion) on the same URL, so the mock tells them apart by method — the shared
 * `mockAuthMe` would answer the DELETE with the user object. After a successful DELETE the probe
 * answers 401, like the real API does once the account and its sessions are gone.
 */
async function mockSession(page: import('@playwright/test').Page, deleteStatus: number) {
  let deleted = false;
  await page.route('**/api/auth/me', async (route) => {
    const method = route.request().method();
    if (method === 'DELETE') {
      if (deleteStatus === 204) {
        deleted = true;
        await route.fulfill({ status: 204 });
      } else {
        await route.fulfill({
          status: deleteStatus,
          contentType: 'application/json',
          body: JSON.stringify({}),
        });
      }
      return;
    }
    if (deleted) {
      await route.fulfill({ status: 401 });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(AUTH_USER),
    });
  });
}

async function openDeleteDialog(page: import('@playwright/test').Page) {
  await page.goto('/my-votings');
  await page.getByRole('button', { name: /Konto-Menü von Sensitron/ }).click();
  await page.getByRole('button', { name: 'Konto löschen' }).click();
  const dialog = page.getByRole('dialog');
  await expect(
    dialog.getByRole('heading', { name: 'Dein Konto unwiderruflich löschen' }),
  ).toBeVisible();
  return dialog;
}

test.describe('account deletion from the account menu', () => {
  test.beforeEach(async ({ page }) => {
    await mockWorkerHealth(page, 'connected');
    await page.route('**/api/vote-sessions/mine*', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 }),
      }),
    );
  });

  test('stays locked until the login is retyped, then deletes and lands on the public page', async ({
    page,
  }) => {
    await mockSession(page, 204);
    const dialog = await openDeleteDialog(page);

    // The copy has to say the two things the user cannot take back or would not expect.
    await expect(dialog.getByText(/jede Stimme, die du abgegeben hast/)).toBeVisible();
    await expect(dialog.getByText(/auch in laufenden Abstimmungen/)).toBeVisible();
    await expect(dialog.getByText(/anonymisiert/)).toBeVisible();

    const confirm = dialog.getByRole('button', { name: 'Konto endgültig löschen' });
    await expect(confirm).toBeDisabled();
    const input = dialog.getByLabel('Zur Bestätigung deinen Twitch-Login eingeben');
    await input.fill('Sensitron'); // display name, not the login
    await expect(confirm).toBeDisabled();
    await input.fill('sensitron');
    await expect(confirm).toBeEnabled();

    const deleteRequest = page.waitForRequest(
      (request) => request.url().includes('/api/auth/me') && request.method() === 'DELETE',
    );
    await confirm.click();
    await deleteRequest;

    await expect(page).toHaveURL(/\/welcome$/);
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  test('a failed deletion keeps the user signed in and says so in the reopened menu', async ({
    page,
  }) => {
    await mockSession(page, 500);
    const dialog = await openDeleteDialog(page);

    await dialog.getByLabel('Zur Bestätigung deinen Twitch-Login eingeben').fill('sensitron');
    await dialog.getByRole('button', { name: 'Konto endgültig löschen' }).click();

    await expect(
      page.getByRole('alert').filter({ hasText: 'konnte nicht gelöscht' }),
    ).toBeVisible();
    await expect(page).toHaveURL(/\/my-votings$/);
  });

  test('a 401 on the deletion is not read as deleted: login page says nothing was deleted', async ({
    page,
  }) => {
    // Signed out in another tab or expired: the session is rejected before the handler runs.
    await mockSession(page, 401);
    const dialog = await openDeleteDialog(page);

    await dialog.getByLabel('Zur Bestätigung deinen Twitch-Login eingeben').fill('sensitron');
    await dialog.getByRole('button', { name: 'Konto endgültig löschen' }).click();

    await expect(page).toHaveURL(/\/login$/);
    await expect(
      page.getByRole('alert').filter({ hasText: 'konnte nicht bestätigt werden' }),
    ).toBeVisible();
    await expect(page.getByText('Es wurde nichts gelöscht.')).toBeVisible();
  });

  test('a 410 on the deletion confirms the account is gone and ends on the public page', async ({
    page,
  }) => {
    // Retry or double submit after the user row vanished: the route-scoped 410.
    await mockSession(page, 410);
    const dialog = await openDeleteDialog(page);

    await dialog.getByLabel('Zur Bestätigung deinen Twitch-Login eingeben').fill('sensitron');
    await dialog.getByRole('button', { name: 'Konto endgültig löschen' }).click();

    await expect(page).toHaveURL(/\/welcome$/);
    await expect(page.getByRole('alert')).toHaveCount(0);
  });

  test('cancelling sends nothing', async ({ page }) => {
    await mockSession(page, 204);
    const dialog = await openDeleteDialog(page);

    let deleteSent = false;
    page.on('request', (request) => {
      if (request.url().includes('/api/auth/me') && request.method() === 'DELETE') {
        deleteSent = true;
      }
    });
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();

    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(deleteSent).toBe(false);
    await expect(page).toHaveURL(/\/my-votings$/);
  });
});
