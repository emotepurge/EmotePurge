import { expect, test } from './support/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelEmoteSetList,
  mockChannelPermissions,
  mockChannelStatus,
  mockMyChannels,
  mockTagEntries,
  mockTagMutations,
  mockTags,
  mockTrackedEmoteSetPreview,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

import type { Page } from '@playwright/test';

/**
 * Emote tags (#201, T-B): assigning from the usage grid, the tag filter and the tags page — all
 * against mocked `/api/**`. The state-carrying mocks (`mockTags`, `mockTagEntries`,
 * `mockTagMutations`) answer the page's next read from what its last write did.
 */
const CHANNEL = 'sensitron';
const ACTIVE_SET_ID = 'set-1';
const OTHER_SET_ID = 'set-2';
const FIRST_NEW_TAG_ID = 1000;

const EMOTES: MockEmoteUsage[] = [
  { name: 'catJAM', uses: 900 },
  { name: 'monkaW', uses: 300 },
  { name: 'KEKW', uses: 40 },
  { name: 'Sadge', uses: 0 },
].map((emote, index) => ({
  emoteId: `e${index + 1}`,
  emoteName: emote.name,
  sevenTvEmoteId: `7tv-${index + 1}`,
  imageUrl: `https://cdn.7tv.app/emote/${index + 1}/2x.webp`,
  totalUseCount: emote.uses,
  lastUsedDate: emote.uses > 0 ? '2026-07-14' : null,
}));

const cell = (page: Page, name: string) =>
  page.getByRole('button', { name: new RegExp(`^${name} ·`) });

async function mockChannel(
  page: Page,
  permissions: Parameters<typeof mockChannelPermissions>[2] = {},
): Promise<void> {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page);
  await installLiveStub(page);
  await mockMyChannels(page, [
    { channelName: CHANNEL, isBroadcaster: true, isTracked: true, isBotActive: true },
  ]);
  await mockChannelPermissions(page, CHANNEL, permissions);
  await mockChannelStatus(page, CHANNEL);
  await mockActiveEmoteSet(page, CHANNEL, ACTIVE_SET_ID, { capacity: 1000, occupiedSlots: 4 });
  await mockChannelEmoteSetList(page, CHANNEL, {
    activeEmoteSetId: ACTIVE_SET_ID,
    sets: [
      { id: ACTIVE_SET_ID, name: 'Hauptset' },
      { id: OTHER_SET_ID, name: 'Halloween' },
    ],
  });
  await mockUsageTotals(page, CHANNEL, EMOTES);
}

async function gotoUsage(page: Page): Promise<void> {
  await page.goto(`/channels/${CHANNEL}/usage-stats`);
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
  await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
}

test.describe('emote tags', () => {
  test('assigns a new tag from the grid, filters by it, and manages it on the tags page', async ({
    page,
  }) => {
    await page.clock.install();
    await mockChannel(page);
    await mockTags(page, CHANNEL);
    const writes = await mockTagMutations(page, CHANNEL);
    await gotoUsage(page);

    // No tag yet: no select, no permanent control for it (E18).
    await expect(page.getByRole('combobox', { name: 'Tag' })).toHaveCount(0);

    await cell(page, 'catJAM').click();
    await cell(page, 'monkaW').click();
    await page.getByRole('button', { name: 'Tag zuweisen…' }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('heading', { name: 'Tag zuweisen' })).toBeVisible();
    await dialog.getByLabel('Neuer Tag').fill('Favoriten');
    await dialog.getByRole('button', { name: 'Anlegen' }).click();
    await expect(dialog.getByRole('checkbox', { name: 'Favoriten' })).toBeChecked();
    await dialog.getByRole('button', { name: '2 Emotes zuweisen' }).click();
    await expect(dialog).toHaveCount(0);

    const assign = writes.requests.find((request) => request.path.endsWith('/entries'));
    expect(assign?.method).toBe('POST');
    expect(assign?.path).toBe(`/${FIRST_NEW_TAG_ID}/entries`);
    const sent = (assign?.body as { sevenTvEmoteIds: string[] }).sevenTvEmoteIds;
    expect([...sent].sort()).toEqual(['7tv-1', '7tv-2']);

    // The acknowledgement is transient (§4.5): stop real time, then let the clock take it away.
    const message = page.getByText('2 Emotes zu Favoriten hinzugefügt.');
    await expect(message.first()).toBeVisible();
    const now = await page.evaluate(() => Date.now());
    await page.clock.pauseAt(now + 1_000);
    await page.clock.runFor(4_000);
    await expect(message).toHaveCount(0);
    // Done with fake time: what follows (resource loads, effects) needs the clock to run again.
    await page.clock.resume();

    // The select appears once the channel has a tag, and choosing it filters the grid.
    await mockTagEntries(page, CHANNEL, FIRST_NEW_TAG_ID, [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM' },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW', inSet: false, currentName: 'monkaWW' },
    ]);
    const select = page.getByRole('combobox', { name: 'Tag' });
    await expect(select).toBeVisible();
    await select.selectOption({ label: 'Favoriten' });
    await expect(cell(page, 'catJAM')).toBeVisible();
    await expect(cell(page, 'KEKW')).toHaveCount(0);
    await expect(cell(page, 'Sadge')).toHaveCount(0);

    await page.getByRole('link', { name: 'Übersicht', exact: true }).click();
    await expect(page).toHaveURL(
      new RegExp(`/channels/${CHANNEL}/tags\\?tag=${FIRST_NEW_TAG_ID}$`),
    );

    // List and detail side by side; one cell is dimmed and says why in its accessible name.
    await expect(page.getByRole('list', { name: 'Tags' }).getByRole('link')).toHaveText([
      /Favoriten/,
    ]);
    await expect(page.getByRole('heading', { name: 'Favoriten', level: 3 })).toBeVisible();
    const grid = page.getByRole('group', { name: 'Emotes in Favoriten' });
    await expect(grid.getByRole('button')).toHaveCount(2);
    await expect(grid.getByRole('button', { name: /nicht im Set/ })).toHaveCount(1);
    await expect(grid.getByRole('button', { name: /^catJAM$/ })).toHaveCount(1);

    // Rename.
    await page.getByRole('button', { name: 'Umbenennen' }).click();
    const rename = page.getByRole('dialog');
    await rename.getByLabel('Name').fill('Beste');
    await rename.getByRole('button', { name: 'Umbenennen' }).click();
    await expect(rename).toHaveCount(0);
    expect(writes.requests.at(-1)).toEqual({
      method: 'PATCH',
      path: `/${FIRST_NEW_TAG_ID}`,
      body: { name: 'Beste' },
    });
    await expect(page.getByRole('heading', { name: 'Beste', level: 3 })).toBeVisible();

    // Delete, with the confirmation naming what survives.
    await page.getByRole('button', { name: 'Löschen' }).click();
    const confirm = page.getByRole('dialog');
    await expect(confirm).toContainText('Beste wird gelöscht; die Emotes bleiben im Set.');
    await confirm.getByRole('button', { name: 'Tag löschen' }).click();
    await expect(confirm).toHaveCount(0);
    expect(writes.requests.at(-1)?.method).toBe('DELETE');
    await expect(page.getByText('Noch keine Tags.')).toBeVisible();
    await expect(page.getByText('Beste gelöscht.').first()).toBeVisible();
  });

  test('without the active set in view there is no assign button, and the filter says why', async ({
    page,
  }) => {
    await mockChannel(page);
    await mockTags(page, CHANNEL, [{ id: 7, name: 'Favoriten', entryCount: 1, inSetCount: 1 }]);
    await mockTagEntries(page, CHANNEL, 7, [{ sevenTvEmoteId: '7tv-1', alias: 'catJAM' }]);
    await mockTrackedEmoteSetPreview(page, CHANNEL, {
      channelName: CHANNEL,
      emoteSetId: OTHER_SET_ID,
      emoteSetName: 'Halloween',
      totalCount: 1,
      emotes: [{ sevenTvEmoteId: '7tv-1', name: 'catJAM' }],
    });
    await gotoUsage(page);

    // On the active set the button is there for a marked emote.
    await cell(page, 'catJAM').click();
    await expect(page.getByRole('button', { name: 'Tag zuweisen…' })).toBeVisible();
    await page.getByRole('combobox', { name: 'Tag' }).selectOption({ label: 'Favoriten' });

    // Deep-link to the other set (the URL is what carries the view); the tag filter set above is
    // page state, so choose it again there.
    await page.goto(`/channels/${CHANNEL}/usage-stats?emoteSetId=${OTHER_SET_ID}`);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    await page.getByRole('combobox', { name: 'Tag' }).selectOption({ label: 'Favoriten' });
    await cell(page, 'catJAM').click();

    await expect(
      page.getByText('Einspielen und Ausräumen wirken auf das aktive Set'),
    ).toBeVisible();
    await expect(page.getByRole('button', { name: 'Tag zuweisen…' })).toHaveCount(0);
  });
});

// `pointer: coarse` follows from the context's touch emulation, which the desktop project can be
// given per describe — the mobile-chrome project is reserved for touch-mobile.e2e.spec.ts.
test.describe('emote tags on a touch device', () => {
  test.use({ viewport: { width: 360, height: 740 }, hasTouch: true, isMobile: true });

  test('walks list, tag and detail as a drilldown and offers nothing to write', async ({
    page,
  }) => {
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: 7, name: 'Favoriten', entryCount: 2, inSetCount: 1 },
      { id: 8, name: 'Neu', entryCount: 0, inSetCount: 0 },
    ]);
    await mockTagEntries(page, CHANNEL, 7, [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM' },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW', inSet: false },
    ]);
    await page.goto(`/channels/${CHANNEL}/tags`);

    // The list alone: no detail beside it.
    const list = page.getByRole('list', { name: 'Tags' });
    await expect(list.getByRole('link')).toHaveCount(2);
    await expect(page.getByRole('heading', { name: 'Favoriten', level: 3 })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Neuer Tag' })).toBeVisible();

    await list.getByRole('link', { name: 'Favoriten' }).tap();
    await expect(page).toHaveURL(/[?&]tag=7\b/);
    await expect(page.getByRole('heading', { name: 'Favoriten', level: 3 })).toBeVisible();
    await expect(page.getByRole('list', { name: 'Tags' })).toHaveCount(0);

    // Cells are labelled images, not toggles: nothing to mark, so no dock.
    const grid = page.getByRole('group', { name: 'Emotes in Favoriten' });
    await expect(grid.getByRole('img')).toHaveCount(2);
    await expect(grid.getByRole('button')).toHaveCount(0);
    await grid.getByRole('img').first().tap();
    await expect(page.getByRole('button', { name: /entfernen/ })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Auswahl aufheben' })).toHaveCount(0);

    await page.getByRole('link', { name: 'Tags', exact: true }).first().tap();
    await expect(page.getByRole('list', { name: 'Tags' })).toBeVisible();
    await expect(page).not.toHaveURL(/[?&]tag=/);

    // The usage grid on the same device: a tap opens the drilldown, so no assign button either.
    await gotoUsage(page);
    await expect(page.getByRole('button', { name: 'Tag zuweisen…' })).toHaveCount(0);
  });
});
