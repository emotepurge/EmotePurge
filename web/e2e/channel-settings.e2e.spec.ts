import { expect, Page, test } from './support/test';

import {
  AUTH_USER,
  backfillCoverageBody,
  backfillRunBody,
  emitLive,
  installLiveStub,
  mockAuthMe,
  mockBackfillCancel,
  mockBackfillRemove,
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
    { id: 'set-personal', name: 'Persönlich', isPersonal: true, kind: 'PERSONAL' },
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
        // What the server sends for an unavailable window: windowFrom (today − months) lies at or
        // after windowTo (the day counting started), and no days are left.
        {
          months: 1,
          windowFrom: '2026-11-08',
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
    // Only the reason: no inverted range, no "0 Tage".
    await expect(page.getByText('08.11.2026')).toHaveCount(0);
    await expect(page.getByText('0 Tage', { exact: true })).toHaveCount(0);
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

  test("a run that would replace another set's days says so, and the start dialog repeats it", async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockBackfillStatus(page, 'sensitron', { coverage: [backfillCoverageBody()] });
    await mockChannelEmoteSetList(page, 'sensitron', SETS);
    const start = await mockBackfillStart(page, 'sensitron');
    const sentence =
      'Dieser Lauf ersetzt die nachgetragenen Tage 01.08.2026 – 31.08.2026, die derzeit für Halloween gezählt werden.';

    await page.goto('/channels/sensitron/settings');
    // The 1-month window (from 08.09.) does not reach the imported days; the 3-month one does.
    await expect(page.getByText(sentence)).toHaveCount(0);
    await page.getByRole('radio', { name: /3 Monate/ }).check();
    await expect(page.getByText(sentence)).toHaveCount(1);

    await page.getByRole('button', { name: 'Backfill starten' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText(sentence);
    expect(start.bodies()).toHaveLength(0);

    await dialog.getByRole('button', { name: 'Backfill starten' }).click();
    await expect.poll(() => start.bodies().length).toBe(1);
  });

  test('without anything to replace, start sends at once and opens no dialog', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockBackfillStatus(page, 'sensitron', {
      coverage: [backfillCoverageBody({ emoteSetId: 'set-active', emoteSetName: 'Normal' })],
    });
    await mockChannelEmoteSetList(page, 'sensitron', SETS);
    const start = await mockBackfillStart(page, 'sensitron');

    await page.goto('/channels/sensitron/settings');
    await page.getByRole('radio', { name: /3 Monate/ }).check();
    await page.getByRole('button', { name: 'Backfill starten' }).click();

    await expect.poll(() => start.bodies().length).toBe(1);
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  test('progress follows a backfill.progress event without a reload', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    const running = (weeksDone: number) =>
      backfillRunBody({ status: 'running', weeksDone, queuePosition: null });
    const status = await mockBackfillStatus(page, 'sensitron', { activeRun: running(3) });
    await mockChannelEmoteSetList(page, 'sensitron', SETS);

    await page.goto('/channels/sensitron/settings');
    await expect(page.getByRole('status').filter({ hasText: '3 von 14 Wochen' })).toBeVisible();
    await expect(page.getByRole('progressbar')).toHaveJSProperty('position', 3 / 14);

    status.set({ activeRun: running(5) });
    await emitLive(page, { type: 'backfill.progress', channel: 'sensitron' });

    await expect(page.getByRole('status').filter({ hasText: '5 von 14 Wochen' })).toBeVisible();
    await expect(page.getByRole('progressbar')).toHaveJSProperty('position', 5 / 14);
  });

  test('a queued run under the archive cooldown shows the wait sentence and its position', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockBackfillStatus(page, 'sensitron', {
      activeRun: backfillRunBody({ queuePosition: 2 }),
      cooldownUntilUtc: '2026-10-09T20:00:00Z',
    });
    await mockChannelEmoteSetList(page, 'sensitron', SETS);

    await page.goto('/channels/sensitron/settings');

    await expect(page.getByText('Position 2 in der Warteschlange')).toBeVisible();
    await expect(page.getByText('Das Archiv hat uns gebeten, bis')).toBeVisible();
  });

  test('cancel asks first, then sends DELETE, and the cancelled run moves to "last run"', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    const status = await mockBackfillStatus(page, 'sensitron', {
      activeRun: backfillRunBody({ status: 'running', queuePosition: null, weeksDone: 2 }),
    });
    await mockChannelEmoteSetList(page, 'sensitron', SETS);
    const cancel = await mockBackfillCancel(page, 'sensitron');

    await page.goto('/channels/sensitron/settings');
    await page.getByRole('button', { name: 'Backfill abbrechen' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Bereits nachgetragene Wochen bleiben bestehen');
    expect(cancel.requests()).toBe(0);

    // Declining sends nothing.
    await dialog.getByRole('button', { name: 'Abbrechen', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(cancel.requests()).toBe(0);

    status.set({
      lastRun: backfillRunBody({ status: 'cancelled', finishedAtUtc: '2026-10-09T19:00:00Z' }),
    });
    await page.getByRole('button', { name: 'Backfill abbrechen' }).click();
    await page
      .getByRole('dialog')
      .getByRole('button', { name: 'Backfill abbrechen', exact: true })
      .click();

    await expect.poll(() => cancel.requests()).toBe(1);
    await expect(page.getByRole('button', { name: 'Backfill abbrechen' })).toHaveCount(0);
    await expect(page.getByText('Letzter Lauf')).toBeVisible();
  });

  test('a cancel that finds no active run is silent', async ({ page }) => {
    await mockChannelPermissions(page, 'sensitron');
    const status = await mockBackfillStatus(page, 'sensitron', {
      activeRun: backfillRunBody({ status: 'running', queuePosition: null }),
    });
    await mockChannelEmoteSetList(page, 'sensitron', SETS);
    await mockBackfillCancel(page, 'sensitron', {
      status: 404,
      errorCode: 'backfill_no_active_run',
    });

    await page.goto('/channels/sensitron/settings');
    // The run must be on screen before the mock stops reporting it, or the page may load without it.
    await expect(page.getByRole('button', { name: 'Backfill abbrechen' })).toBeVisible();
    status.set({ lastRun: backfillRunBody({ status: 'completed', weeksDone: 14 }) });
    await page.getByRole('button', { name: 'Backfill abbrechen' }).click();
    await page
      .getByRole('dialog')
      .getByRole('button', { name: 'Backfill abbrechen', exact: true })
      .click();

    await expect(page.getByRole('button', { name: 'Backfill abbrechen' })).toHaveCount(0);
    await expect(page.getByRole('alert')).toHaveCount(0);
  });

  test('a finished run offers no cancel and shows its outcome, set and translated error', async ({
    page,
  }) => {
    await mockChannelPermissions(page, 'sensitron');
    await mockBackfillStatus(page, 'sensitron', {
      lastRun: backfillRunBody({
        status: 'failed',
        queuePosition: null,
        weeksDone: 4,
        finishedAtUtc: '2026-10-09T19:00:00Z',
        errorCode: 'transport_failure',
      }),
    });
    await mockChannelEmoteSetList(page, 'sensitron', SETS);

    await page.goto('/channels/sensitron/settings');

    await expect(page.getByText('Fehlgeschlagen')).toBeVisible();
    await expect(page.getByText('Das Archiv war nicht erreichbar')).toBeVisible();
    await expect(page.getByText('Set: Other (10 Einträge)')).toBeVisible();
    await expect(page.getByText('Zeitraum: 08.07.2026 – 07.10.2026')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Backfill abbrechen' })).toHaveCount(0);
  });
});

test.describe('channel settings tab (imported data removal)', () => {
  const importedSection = (page: Page) =>
    page.locator('section[aria-labelledby="backfill-imported-heading"]');

  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelEmoteSetList(page, 'sensitron', SETS);
  });

  test('removing sends exactly the shown intervals, then confirms in place with focus on the sentence', async ({
    page,
  }) => {
    const coverage = [
      backfillCoverageBody(),
      backfillCoverageBody({
        from: '2026-09-01',
        to: '2026-09-08',
        emoteSetId: 'set-active',
        emoteSetName: 'Normal',
      }),
    ];
    const status = await mockBackfillStatus(page, 'sensitron', { coverage });
    const remove = await mockBackfillRemove(page, 'sensitron', 'removed', () =>
      status.set({ coverage: [] }),
    );

    await page.goto('/channels/sensitron/settings');
    const section = importedSection(page);
    await expect(section.getByRole('heading', { name: 'Importierte Daten' })).toBeVisible();
    await expect(section).toContainText('Halloween');
    await expect(section).toContainText('Normal');

    await section.getByRole('button', { name: 'Importierte Daten entfernen' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Importierte Daten dieses Channels entfernen?');
    expect(remove.bodies()).toHaveLength(0);
    await dialog.getByRole('button', { name: 'Importierte Daten entfernen' }).click();

    await expect.poll(() => remove.bodies().length).toBe(1);
    expect(remove.bodies()[0]).toEqual({
      expectedCoverage: coverage.map(({ from, to, emoteSetId, archiveHost }) => ({
        from,
        to,
        emoteSetId,
        archiveHost,
      })),
    });

    const confirmation = section.getByText('Importierte Daten entfernt: 31 Tage.');
    await expect(confirmation).toBeVisible();
    await expect(confirmation).toBeFocused();
    await expect(section.getByRole('heading', { name: 'Importierte Daten' })).toBeVisible();
    await expect(section.getByRole('button', { name: 'Importierte Daten entfernen' })).toHaveCount(
      0,
    );
    await expect(section.getByRole('list')).toHaveCount(0);
  });

  test('cancelling the dialog sends nothing', async ({ page }) => {
    await mockBackfillStatus(page, 'sensitron', { coverage: [backfillCoverageBody()] });
    const remove = await mockBackfillRemove(page, 'sensitron');

    await page.goto('/channels/sensitron/settings');
    await importedSection(page)
      .getByRole('button', { name: 'Importierte Daten entfernen' })
      .click();
    await page.getByRole('dialog').getByRole('button', { name: 'Abbrechen' }).click();

    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(remove.bodies()).toHaveLength(0);
  });

  test('an active run locks the button and says why', async ({ page }) => {
    await mockBackfillStatus(page, 'sensitron', {
      coverage: [backfillCoverageBody()],
      activeRun: backfillRunBody({ status: 'running', queuePosition: null }),
    });
    const remove = await mockBackfillRemove(page, 'sensitron');

    await page.goto('/channels/sensitron/settings');
    const section = importedSection(page);
    const button = section.getByRole('button', { name: 'Importierte Daten entfernen' });
    await expect(button).toBeDisabled();
    await expect(section.locator('#backfill-remove-blocked')).toHaveText(
      'Brich zuerst den laufenden Backfill ab.',
    );
    await expect(button).toHaveAccessibleDescription('Brich zuerst den laufenden Backfill ab.');
    expect(remove.bodies()).toHaveLength(0);
  });

  test('backfill_remove_run_active shows its text and reloads the status', async ({ page }) => {
    const status = await mockBackfillStatus(page, 'sensitron', {
      coverage: [backfillCoverageBody()],
    });
    await mockBackfillRemove(page, 'sensitron', {
      status: 409,
      errorCode: 'backfill_remove_run_active',
    });

    await page.goto('/channels/sensitron/settings');
    const section = importedSection(page);
    await expect(
      section.getByRole('button', { name: 'Importierte Daten entfernen' }),
    ).toBeEnabled();
    const before = status.requests();
    // The refetch behind the 409 sees the run the server saw.
    status.set({
      coverage: [backfillCoverageBody()],
      activeRun: backfillRunBody({ status: 'running', queuePosition: null }),
    });
    await section.getByRole('button', { name: 'Importierte Daten entfernen' }).click();
    await page
      .getByRole('dialog')
      .getByRole('button', { name: 'Importierte Daten entfernen' })
      .click();

    await expect(
      page.getByRole('alert').filter({ hasText: 'wartet oder läuft gerade' }),
    ).toBeVisible();
    await expect.poll(() => status.requests()).toBeGreaterThan(before);
    await expect(
      section.getByRole('button', { name: 'Importierte Daten entfernen' }),
    ).toBeDisabled();
  });

  test('backfill_remove_scope_changed shows its text and the new status', async ({ page }) => {
    const status = await mockBackfillStatus(page, 'sensitron', {
      coverage: [backfillCoverageBody()],
    });
    await mockBackfillRemove(page, 'sensitron', {
      status: 409,
      errorCode: 'backfill_remove_scope_changed',
    });

    await page.goto('/channels/sensitron/settings');
    const section = importedSection(page);
    await expect(section).toContainText('01.08.2026');
    status.set({
      coverage: [backfillCoverageBody({ from: '2026-08-01', to: '2026-09-10' })],
    });
    await section.getByRole('button', { name: 'Importierte Daten entfernen' }).click();
    await page
      .getByRole('dialog')
      .getByRole('button', { name: 'Importierte Daten entfernen' })
      .click();

    await expect(
      page.getByRole('alert').filter({ hasText: 'haben sich inzwischen geändert' }),
    ).toBeVisible();
    await expect(section).toContainText('09.09.2026');
  });

  test('without imported coverage there is no section', async ({ page }) => {
    await mockBackfillStatus(page, 'sensitron');

    await page.goto('/channels/sensitron/settings');
    await expect(page.getByRole('heading', { name: 'Backfill aus dem Chat-Archiv' })).toBeVisible();
    await expect(importedSection(page)).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Importierte Daten' })).toHaveCount(0);
  });
});
