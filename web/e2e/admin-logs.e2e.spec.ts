import { expect, test, type Page } from './support/test';

import {
  AUTH_USER,
  buildLogEntry,
  installLiveStub,
  mockAdminLogs,
  mockAuthMe,
  mockWorkerHealth,
  type MockAdminLogsOptions,
} from './support/mocks';

const ADMIN_USER = { ...AUTH_USER, isGlobalAdmin: true };

async function adminLogsPage(page: Page, options: MockAdminLogsOptions = {}) {
  await installLiveStub(page);
  await mockAuthMe(page, ADMIN_USER);
  await mockWorkerHealth(page, 'connected');
  return mockAdminLogs(page, options);
}

const filterField = (page: Page) => page.getByLabel('Filter', { exact: true });
const rows = (page: Page) => page.locator('[data-log-id]');

test.describe('admin log viewer on /admin/logs', () => {
  test('a deep link opens prefiltered and the tab sits between Monitoring and Channels', async ({
    page,
  }) => {
    const calls = await adminLogsPage(page);

    await page.goto('/admin/logs?q=level%3E%3Dwarn%20cat%3ATwitchChatManager&range=7d');

    await expect(filterField(page)).toHaveValue('level>=warn cat:TwitchChatManager');
    await expect(rows(page)).toHaveCount(8);
    expect(calls.list[0].searchParams.get('q')).toBe('level>=warn cat:TwitchChatManager');
    expect(calls.list[0].searchParams.get('range')).toBe('7d');
    expect(calls.summary[0].searchParams.get('range')).toBe('7d');
    const tabs = await page.locator('app-admin-layout nav a').allTextContents();
    expect(tabs.map((t) => t.trim()).slice(0, 3)).toEqual(['Monitoring', 'Logs', 'Channels']);
  });

  test('an invalid filter marks the offending spot in the field', async ({ page }) => {
    await adminLogsPage(page, {
      fail: (url) =>
        (url.searchParams.get('q') ?? '').includes('debug')
          ? {
              status: 400,
              body: {
                errorCode: 'invalid_log_filter',
                reason: 'invalid_level',
                position: 6,
                length: 5,
              },
            }
          : undefined,
    });
    await page.goto('/admin/logs');
    await expect(rows(page)).toHaveCount(8);

    await filterField(page).fill('level:debug');
    await filterField(page).press('Enter');

    await expect(filterField(page)).toHaveAttribute('aria-invalid', 'true');
    await expect(page.getByText('Ungültiges Level')).toBeVisible();
    const selection = await filterField(page).evaluate((el: HTMLInputElement) => [
      el.selectionStart,
      el.selectionEnd,
    ]);
    expect(selection).toEqual([6, 11]);
    await expect(rows(page)).toHaveCount(0);
  });

  test('a facet adds a term and its exclude button adds the negation', async ({ page }) => {
    const calls = await adminLogsPage(page);
    await page.goto('/admin/logs');
    await expect(rows(page)).toHaveCount(8);

    await page.getByRole('button', { name: 'worker (100) zum Filter hinzufügen' }).click();
    await expect(filterField(page)).toHaveValue('source:worker');
    await page.getByRole('button', { name: 'api ausschließen' }).click();
    await expect(filterField(page)).toHaveValue('source:worker -source:api');

    expect(calls.list.at(-1)?.searchParams.get('q')).toBe('source:worker -source:api');
  });

  test('a bucket narrows the range and the way back restores the relative one', async ({
    page,
  }) => {
    const calls = await adminLogsPage(page);
    await page.goto('/admin/logs');
    await expect(rows(page)).toHaveCount(8);

    const chart = page.getByRole('group', { name: /Einträge pro Zeitintervall/ });
    await chart.focus();
    await chart.press('Home');
    await chart.press('Enter');

    await expect(page).toHaveURL(/from=2026-10-13T12:00:00Z&to=2026-10-13T12:15:00Z/);
    expect(calls.list.at(-1)?.searchParams.get('from')).toBe('2026-10-13T12:00:00Z');
    expect(calls.list.at(-1)?.searchParams.has('range')).toBe(false);

    await page.getByRole('button', { name: 'Zurück zu 24 h' }).click();
    await expect(page).not.toHaveURL(/from=/);
    await expect.poll(() => calls.list.at(-1)?.searchParams.get('range')).toBe('24h');
    await expect(page.getByRole('button', { name: 'Zurück zu 24 h' })).toHaveCount(0);
  });

  test('scrolling loads rows 100 at a time and stops at 2,000 with a hint', async ({ page }) => {
    const calls = await adminLogsPage(page, { totalEntries: 2500 });
    await page.goto('/admin/logs');
    await expect(rows(page)).toHaveCount(100);

    await expect(async () => {
      await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
      await expect(page.getByTestId('log-limit')).toBeVisible({ timeout: 150 });
    }).toPass({ timeout: 45_000, intervals: [50] });

    await expect(rows(page)).toHaveCount(2000);
    await expect(page.getByTestId('log-limit')).toContainText('Zeitraum oder Filter eingrenzen');
    // Paging runs on the first answer's exact bounds, never on the preset.
    const paged = calls.list.at(-1)!;
    expect(paged.searchParams.get('from')).toBe('2026-10-13T12:00:00Z');
    expect(paged.searchParams.has('range')).toBe(false);
    const requests = calls.list.length;
    await page.waitForTimeout(500);
    expect(calls.list.length).toBe(requests);
  });

  test('shows the drop banner with every reason, a stale source and a query timeout', async ({
    page,
  }) => {
    let failList = false;
    await adminLogsPage(page, {
      summary: { dropped: { total: 9, queueFull: 4, writeUnconfirmed: 3, rejected: 2 } },
      status: {
        sources: [
          {
            source: 'api',
            state: 'stale',
            instanceId: 'x',
            startedUtc: '2026-10-13T20:00:00Z',
            lastFlushUtc: '2026-10-14T11:20:41Z',
          },
          {
            source: 'worker',
            state: 'healthy',
            instanceId: 'a1b2c3d4',
            startedUtc: '2026-10-13T20:00:00Z',
            lastFlushUtc: '2026-10-14T11:59:30Z',
          },
        ],
      },
      fail: (_url, endpoint) =>
        failList && endpoint === 'list'
          ? { status: 503, body: { errorCode: 'log_query_timeout' } }
          : undefined,
    });
    await page.goto('/admin/logs');
    await expect(rows(page)).toHaveCount(8);

    await expect(
      page.getByText(/9 Einträge nicht oder möglicherweise nicht gespeichert/),
    ).toBeVisible();
    await expect(page.getByText(/Puffer voll: 4/)).toBeVisible();
    await expect(page.getByText(/Abgelehnt: 2/)).toBeVisible();
    await expect(
      page.getByText(/Die Log-Erfassung von api hat seit 11:20 UTC nichts geschrieben/),
    ).toBeVisible();

    failList = true;
    await page.getByRole('radio', { name: '7 Tage' }).click();
    await expect(
      page.getByRole('alert').getByText('Abfrage zu aufwendig — Zeitraum oder Filter eingrenzen'),
    ).toBeVisible();
    // The last good answer stays on screen.
    await expect(rows(page)).toHaveCount(8);
  });

  test('a row unfolds with its exception and a property can be added to the filter', async ({
    page,
  }) => {
    const calls = await adminLogsPage(page, {
      entries: [
        buildLogEntry(5, {
          level: 'error',
          hasException: true,
          properties: { channel: 'handofblood' },
        }),
      ],
    });
    await page.goto('/admin/logs');

    const row = rows(page).first();
    const toggle = row.locator('button[aria-expanded]').first();
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await expect(row.locator('pre')).toContainText('System.InvalidOperationException');
    expect(calls.entry).toHaveLength(1);

    await row
      .getByRole('button', { name: /channel mit Wert handofblood zum Filter hinzufügen/ })
      .click();
    await expect(filterField(page)).toHaveValue('channel:handofblood');
  });

  test('a chip click replaces the filter and keeps the time range; the seed is active at level>=error', async ({
    page,
  }) => {
    const calls = await adminLogsPage(page);
    await page.goto('/admin/logs?range=7d');
    await expect(rows(page)).toHaveCount(8);
    const chips = page.getByRole('group', { name: 'Gespeicherte Filter' });
    await expect(chips.getByRole('button', { name: 'Alle Fehler', exact: true })).toHaveAttribute(
      'aria-pressed',
      'false',
    );

    await chips.getByRole('button', { name: 'Alle Fehler', exact: true }).click();

    await expect(filterField(page)).toHaveValue('level>=error');
    await expect(chips.getByRole('button', { name: 'Alle Fehler', exact: true })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(page).toHaveURL(/range=7d/);
    expect(calls.list.at(-1)?.searchParams.get('q')).toBe('level>=error');
    expect(calls.list.at(-1)?.searchParams.get('range')).toBe('7d');
  });

  test('saves, edits and deletes a filter, the delete behind a confirmation', async ({ page }) => {
    const calls = await adminLogsPage(page);
    await page.goto('/admin/logs?q=source%3Aapi');
    await expect(rows(page)).toHaveCount(8);

    await page.getByRole('button', { name: '+ Filter speichern' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByLabel('Filter', { exact: true })).toHaveValue('source:api');
    await dialog.getByLabel('Name').fill('Nur API');
    await dialog.getByRole('button', { name: 'Speichern' }).click();
    await expect(dialog).toHaveCount(0);
    const chip = page.getByRole('button', { name: 'Nur API', exact: true });
    await expect(chip).toHaveAttribute('aria-pressed', 'true');
    expect(calls.filters.at(-1)).toEqual({
      method: 'POST',
      id: null,
      body: { name: 'Nur API', filter: 'source:api' },
    });

    await page.getByRole('button', { name: 'Menü für Filter Nur API' }).click();
    await page.getByRole('button', { name: 'Bearbeiten' }).click();
    await dialog.getByLabel('Name').fill('Nur Api-Quelle');
    await dialog.getByRole('button', { name: 'Speichern' }).click();
    await expect(page.getByRole('button', { name: 'Nur Api-Quelle', exact: true })).toBeVisible();
    expect(calls.filters.at(-1)?.method).toBe('PUT');

    await page.getByRole('button', { name: 'Menü für Filter Nur Api-Quelle' }).click();
    await page.getByRole('button', { name: 'Löschen' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Abbrechen' }).click();
    await expect(page.getByRole('button', { name: 'Nur Api-Quelle', exact: true })).toBeVisible();
    expect(calls.filters.at(-1)?.method).toBe('PUT');

    await page.getByRole('button', { name: 'Menü für Filter Nur Api-Quelle' }).click();
    await page.getByRole('button', { name: 'Löschen' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Löschen' }).click();
    await expect(page.getByRole('button', { name: 'Nur Api-Quelle', exact: true })).toHaveCount(0);
    expect(calls.filters.at(-1)?.method).toBe('DELETE');
  });

  test('a taken name shows on the name field of the dialog', async ({ page }) => {
    await adminLogsPage(page, {
      failFilter: (call) =>
        call.method === 'POST'
          ? { status: 409, body: { errorCode: 'log_filter_name_taken' } }
          : undefined,
    });
    await page.goto('/admin/logs?q=source%3Aapi');
    await expect(rows(page)).toHaveCount(8);

    await page.getByRole('button', { name: '+ Filter speichern' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('Name').fill('Alle Fehler');
    await dialog.getByRole('button', { name: 'Speichern' }).click();

    await expect(dialog.getByLabel('Name')).toHaveAttribute('aria-invalid', 'true');
    await expect(
      dialog.getByText('Ein gespeicherter Filter mit diesem Namen existiert bereits.'),
    ).toBeVisible();
  });

  test('"save" is locked with a reason while the filter is empty', async ({ page }) => {
    await adminLogsPage(page);
    await page.goto('/admin/logs');
    await expect(rows(page)).toHaveCount(8);

    await expect(page.getByRole('button', { name: '+ Filter speichern' })).toBeDisabled();
    await expect(page.getByText('Zum Speichern erst einen Filter eingeben.')).toBeVisible();
  });

  test('a malformed deep link shows an error and settles', async ({ page }) => {
    const calls = await adminLogsPage(page);
    await page.clock.install();

    for (const url of [
      '/admin/logs?range=2d',
      '/admin/logs?from=2026-10-14T10%3A00%3A00Z',
      `/admin/logs?q=${'a'.repeat(513)}`,
    ]) {
      await page.goto(url);
      if (url.includes('q=')) {
        await expect(filterField(page)).toHaveAttribute('aria-invalid', 'true');
        await expect(page.getByText(/zu lang/)).toBeVisible();
      } else {
        await expect(page.getByText('Der Zeitraum in der Adresse ist ungültig')).toBeVisible();
        // The range control stays operable.
        await expect(page.getByRole('radio', { name: '6 h' })).toBeEnabled();
      }
      await page.clock.runFor(100);
      const requests = calls.list.length + calls.summary.length;
      await page.clock.runFor(5000);
      expect(calls.list.length + calls.summary.length).toBe(requests);
      expect(requests).toBe(0);
    }
  });
});

test.describe('admin log viewer on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('facets sit behind Filter, a row unfolds on a tap and the page does not scroll sideways', async ({
    page,
  }) => {
    const calls = await adminLogsPage(page, {
      entries: [
        buildLogEntry(9, {
          message: 'A very long message '.repeat(20) + 'x'.repeat(200),
          properties: { token: 'y'.repeat(300) },
        }),
        buildLogEntry(8),
      ],
    });
    await page.goto('/admin/logs');
    await expect(rows(page)).toHaveCount(2);

    // No facet column and no range control in the bar; the strip replaces the full chart.
    await expect(page.getByRole('radiogroup')).toHaveCount(0);
    await expect(
      page.getByRole('button', { name: 'worker (100) zum Filter hinzufügen' }),
    ).toHaveCount(0);
    await page.getByRole('button', { name: 'Filter', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('radiogroup')).toBeVisible();
    await dialog.getByRole('button', { name: 'worker (100) zum Filter hinzufügen' }).click();
    await expect(filterField(page)).toHaveValue('source:worker');
    expect(calls.list.at(-1)?.searchParams.get('q')).toBe('source:worker');
    await dialog.getByRole('button', { name: 'Fertig' }).click();
    await expect(dialog).toHaveCount(0);

    const first = rows(page).first();
    await first.getByRole('button').first().click();
    await expect(first.getByRole('button').first()).toHaveAttribute('aria-expanded', 'true');

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });

  test('saved filters live in the Filter sheet: a chip applies and closes it, "save" opens exactly one dialog', async ({
    page,
  }) => {
    const calls = await adminLogsPage(page);
    await page.goto('/admin/logs?range=7d');
    await expect(rows(page)).toHaveCount(8);
    await expect(page.getByRole('group', { name: 'Gespeicherte Filter' })).toHaveCount(0);

    await page.getByRole('button', { name: 'Filter', exact: true }).click();
    const sheet = page.getByRole('dialog');
    await sheet.getByRole('button', { name: 'Alle Fehler', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(filterField(page)).toHaveValue('level>=error');
    await expect(page).toHaveURL(/range=7d/);
    expect(calls.list.at(-1)?.searchParams.get('q')).toBe('level>=error');

    await page.getByRole('button', { name: 'Filter', exact: true }).click();
    await expect(
      page.getByRole('dialog').getByRole('button', { name: 'Alle Fehler', exact: true }),
    ).toHaveAttribute('aria-pressed', 'true');
    await page.getByRole('dialog').getByRole('button', { name: '+ Filter speichern' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(page.getByRole('dialog').getByLabel('Name')).toBeVisible();
    await expect(page.getByRole('dialog').getByLabel('Filter', { exact: true })).toHaveValue(
      'level>=error',
    );

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
