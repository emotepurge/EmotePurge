import { expect, test } from './support/test';

import {
  AUTH_USER,
  backfillRunBody,
  emitLive,
  installLiveStub,
  mockAuthMe,
  mockBackfillStart,
  mockBackfillStatus,
  mockChannelEmoteSetList,
  mockChannelPermissions,
  mockWorkerHealth,
} from './support/mocks';

const SETS = {
  activeEmoteSetId: 'set-active',
  sets: [
    { id: 'set-active', name: 'Normal' },
    { id: 'set-other', name: 'Halloween' },
    { id: 'set-personal', name: 'Persönlich', isPersonal: true },
  ],
};

test.describe('channel settings tab (chat-log backfill, start half)', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
  });

  test('a manager sees the tab; it opens the section with the explanations and the source link', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockBackfillStatus(page, 'sensitron');
    await mockChannelEmoteSetList(page, 'sensitron', SETS);

    await page.goto('/channels/sensitron/vote-sessions');
    await page.getByRole('link', { name: 'Einstellungen' }).click();

    await expect(page).toHaveURL(/\/channels\/sensitron\/settings$/);
    await expect(page.getByRole('heading', { name: 'Backfill aus dem Chat-Archiv' })).toBeVisible();
    await expect(page.getByRole('link', { name: /logs\.cyex\.app/ })).toHaveAttribute(
      'href',
      'https://logs.cyex.app/',
    );
    await expect(page.getByText('gehört zu genau einem Set')).toBeVisible();
  });

  test('no tab for a non-manager, and the guard turns a direct visit away without a status request', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron', { canManage: false });
    const status = await mockBackfillStatus(page, 'sensitron');
    await mockChannelEmoteSetList(page, 'sensitron', SETS);

    await page.goto('/channels/sensitron/vote-sessions');
    await expect(page.getByRole('link', { name: 'Votings' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Einstellungen' })).toHaveCount(0);

    await page.goto('/channels/sensitron/settings');
    await expect(page).toHaveURL(/\/channels\/sensitron\/vote-sessions$/);
    expect(status.requests()).toBe(0);
  });

  test('flag off: no tab, the guard redirects and the status is never requested', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron', { chatLogBackfillEnabled: false });
    const status = await mockBackfillStatus(page, 'sensitron');
    await mockChannelEmoteSetList(page, 'sensitron', SETS);

    await page.goto('/channels/sensitron/vote-sessions');
    await expect(page.getByRole('link', { name: 'Aktivität' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Einstellungen' })).toHaveCount(0);

    await page.goto('/channels/sensitron/settings');
    await expect(page).toHaveURL(/\/channels\/sensitron\/vote-sessions$/);
    expect(status.requests()).toBe(0);
  });

  test('an unavailable option shows its reason and cannot be chosen', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockBackfillStatus(page, 'sensitron', {
      options: [
        {
          months: 1,
          windowFrom: '2026-10-07',
          windowTo: '2026-10-08',
          days: 0,
          weeks: 0,
          available: false,
          reason: 'no_days_before_counting',
        },
        {
          months: 3,
          windowFrom: '2026-07-08',
          windowTo: '2026-10-08',
          days: 92,
          weeks: 14,
          available: true,
          reason: null,
        },
      ],
    });
    await mockChannelEmoteSetList(page, 'sensitron', SETS);

    await page.goto('/channels/sensitron/settings');

    const unavailable = page.getByRole('radio', { name: /1 Monat/ });
    await expect(unavailable).toBeDisabled();
    await expect(page.getByText('liegen keine Tage in diesem Zeitraum')).toBeVisible();
    await expect(page.getByRole('radio', { name: /3 Monate/ })).toBeChecked();
  });

  test('the picker preselects the active set, omits personal sets and sends the picked set and months as a number', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    const status = await mockBackfillStatus(page, 'sensitron');
    await mockChannelEmoteSetList(page, 'sensitron', SETS);
    const start = await mockBackfillStart(page, 'sensitron');

    await page.goto('/channels/sensitron/settings');

    const picker = page.getByLabel('Emote-Set');
    await expect(picker).toHaveValue('set-active');
    await expect(picker.locator('option')).toHaveCount(2);
    await expect(picker.locator('option', { hasText: 'Persönlich' })).toHaveCount(0);

    // A non-active set and a non-default window, so a UI that re-derives either gets caught.
    await picker.selectOption('set-other');
    await page.getByRole('radio', { name: /3 Monate/ }).check();
    status.set({ activeRun: backfillRunBody() });
    await page.getByRole('button', { name: 'Backfill starten' }).click();

    await expect.poll(() => start.bodies().length).toBe(1);
    const body = start.bodies()[0] as { emoteSetId: string; months: unknown };
    expect(body).toEqual({ emoteSetId: 'set-other', months: 3 });
    expect(typeof body.months).toBe('number');

    // 202 → the refetched status carries the run → the section says so and start is locked.
    await expect(page.getByText('läuft oder wartet bereits ein Backfill.').first()).toBeVisible();
    await expect(page.getByRole('button', { name: 'Backfill starten' })).toBeDisabled();
  });

  test('a 503 on the set list locks the picker and start and says why', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockBackfillStatus(page, 'sensitron');
    await mockChannelEmoteSetList(page, 'sensitron', 503);

    await page.goto('/channels/sensitron/settings');

    await expect(page.getByText('7TV ist gerade nicht erreichbar')).toBeVisible();
    await expect(page.getByLabel('Emote-Set')).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Backfill starten' })).toBeDisabled();
  });

  test('a backfill.progress event refetches the status', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    const status = await mockBackfillStatus(page, 'sensitron');
    await mockChannelEmoteSetList(page, 'sensitron', SETS);

    await page.goto('/channels/sensitron/settings');
    await expect(page.getByRole('button', { name: 'Backfill starten' })).toBeEnabled();
    expect(status.requests()).toBe(1);

    status.set({ activeRun: backfillRunBody() });
    await emitLive(page, { type: 'backfill.progress', channel: 'sensitron' });

    await expect.poll(() => status.requests()).toBe(2);
    await expect(page.getByRole('button', { name: 'Backfill starten' })).toBeDisabled();
  });

  test('channel_excluded on start is explained in backfill terms, not as "cannot be added"', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockBackfillStatus(page, 'sensitron');
    await mockChannelEmoteSetList(page, 'sensitron', SETS);
    await mockBackfillStart(page, 'sensitron', { status: 409, errorCode: 'channel_excluded' });

    await page.goto('/channels/sensitron/settings');
    await page.getByRole('button', { name: 'Backfill starten' }).click();

    await expect(page.getByRole('alert')).toContainText('von der Erfassung ausgenommen');
    await expect(page.getByText('kann nicht hinzugefügt werden')).toHaveCount(0);
  });
});
