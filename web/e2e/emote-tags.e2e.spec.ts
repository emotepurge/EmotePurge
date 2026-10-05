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
  mockEmoteSetTargets,
  mockForeignEmoteSetPreview,
  mockMyChannels,
  mockSetWarning,
  mockSevenTvGql,
  mockSyncDeletedInSet,
  mockTagEntries,
  mockTagMutations,
  mockTagOperations,
  mockTagPlacements,
  mockTagRemoval,
  mockTags,
  mockTrackedEmoteSetPreview,
  mockUsageTotals,
  mockWorkerHealth,
  emitLive,
  sevenTvGqlRequestKind,
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

    // The usage page has no summary of the tag and no link on: the way to the tags page is the
    // channel's tab, and the list's link chooses the tag.
    await page.getByRole('link', { name: 'Tags', exact: true }).first().click();
    await page.getByRole('list', { name: 'Tags' }).getByRole('link', { name: 'Favoriten' }).click();
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
    await page.getByRole('button', { name: 'Tag löschen' }).click();
    const confirm = page.getByRole('dialog');
    await expect(confirm).toContainText('Beste wird gelöscht; die Emotes bleiben im Set.');
    await confirm.getByRole('button', { name: 'Tag löschen' }).click();
    await expect(confirm).toHaveCount(0);
    expect(writes.requests.at(-1)?.method).toBe('DELETE');
    await expect(page.getByText('Noch keine Tags.')).toBeVisible();
    await expect(page.getByText('Beste gelöscht.').first()).toBeVisible();
  });

  test('a tag without emotes never offers "alle markieren", not even while its emotes are loading', async ({
    page,
  }) => {
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: 7, name: 'Leer', entryCount: 0, inSetCount: 0 },
      { id: 8, name: 'Favoriten', entryCount: 1, inSetCount: 1 },
    ]);
    await mockTagEntries(page, CHANNEL, 7, []);
    await mockTagEntries(page, CHANNEL, 8, [{ sevenTvEmoteId: '7tv-1', alias: 'catJAM' }]);
    // The entries answer only once the test lets them go — the window the bug lived in.
    let release: () => void = () => undefined;
    let gate = new Promise<void>((resolve) => (release = resolve));
    await page.route(
      (url) => /\/tags\/\d+\/entries$/.test(url.pathname),
      async (route) => {
        await gate;
        await route.fallback();
      },
    );
    await gotoUsage(page);
    // Both the toolbar's and the atlas band's button carry this bare-verb label, so counts
    // below cover the pair. For visibility the toolbar button is the one meant: it is first in
    // DOM order and has no stable container of its own (no role/label), hence `.first()`.
    const markAll = page.getByRole('button', { name: 'alle markieren' });
    await expect(markAll.first()).toBeVisible();

    const select = page.getByRole('combobox', { name: 'Tag' });
    await select.selectOption({ label: 'Leer' });
    // Gone while the entries are held back (the original bug kept it here for the whole window).
    await expect(markAll).toHaveCount(0);

    // From now on, record every moment the button exists, not just the ones an assertion hits.
    // Installed only now that it is gone, so the mutations around it while it still stood cannot
    // set the flag; the first check runs at once, not only on the next mutation.
    await page.evaluate(() => {
      const w = window as unknown as { __markAllSeen: boolean };
      const check = (): void => {
        const found = [...document.querySelectorAll('button')].some(
          (button) => button.textContent?.trim() === 'alle markieren',
        );
        if (found) w.__markAllSeen = true;
      };
      w.__markAllSeen = false;
      check();
      new MutationObserver(check).observe(document.body, {
        childList: true,
        subtree: true,
        characterData: true,
      });
    });
    const markAllSeen = () =>
      page.evaluate(() => (window as unknown as { __markAllSeen: boolean }).__markAllSeen);

    release();
    // The empty tag's own state renders only once its entries have landed — the flag is read after
    // that, so a button coming back with the answer would be on record.
    await expect(page.getByText('Keine Emotes passen zu den aktuellen Filtern.')).toBeVisible();
    await expect(markAll).toHaveCount(0);
    expect(await markAllSeen()).toBe(false);

    // A tag with an emote: nothing offered while loading, then the one row's own button.
    gate = new Promise<void>((resolve) => (release = resolve));
    await select.selectOption({ label: 'Favoriten' });
    await expect(markAll).toHaveCount(0);
    release();
    await expect(cell(page, 'catJAM')).toBeVisible();
    await expect(cell(page, 'KEKW')).toHaveCount(0);
    await expect(markAll.first()).toBeVisible();

    // "Alle Tags": the whole set at once, no waiting.
    await select.selectOption({ label: 'Alle Tags' });
    await expect(cell(page, 'KEKW')).toBeVisible();
  });

  test('without the active set in view there is no assign button; the usage page never offers tag runs', async ({
    page,
  }) => {
    // Tag runs are on, and still not here: "Ins Set holen"/"Aus dem Set entfernen" live on the tags page only
    // (operator feedback 2026-10-05).
    await mockChannel(page, { tagRunsEnabled: true });
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
    await expect(page.getByRole('button', { name: 'Ins Set holen' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Aus dem Set entfernen' })).toHaveCount(0);

    // Deep-link to the other set (the URL is what carries the view); the tag filter set above is
    // page state, so choose it again there.
    await page.goto(`/channels/${CHANNEL}/usage-stats?emoteSetId=${OTHER_SET_ID}`);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    await page.getByRole('combobox', { name: 'Tag' }).selectOption({ label: 'Favoriten' });
    await cell(page, 'catJAM').click();

    await expect(page.getByRole('button', { name: 'Tag zuweisen…' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Ins Set holen' })).toHaveCount(0);
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
    await expect(page.getByRole('button', { name: /aus dem tag entfernen/i })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Auswahl aufheben' })).toHaveCount(0);

    await page.getByRole('link', { name: 'Tags', exact: true }).first().tap();
    await expect(page.getByRole('list', { name: 'Tags' })).toBeVisible();
    await expect(page).not.toHaveURL(/[?&]tag=/);

    // The usage grid on the same device: a tap opens the drilldown, so no assign button either.
    await gotoUsage(page);
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#app-dialog-title')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Tag zuweisen…' })).toHaveCount(0);
  });
});

/**
 * Tag play-in and clear-out (#201 T-C, spec 11 scenarios 2-8): the run buttons on the tags page
 * (the only surface that offers them), against mocked `/api/**` and a mocked 7TV GQL that holds the live
 * set. Every scenario counts the mutations 7TV saw (`addEmote`/`removeEmote`) and asserts the exact
 * number, 0 where nothing may be written.
 */
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const OWNER_TWITCH_ID = 'tw-sensitron';
const TAG_ID = 7;
const OTHER_TAG_ID = 9;
const PLACED_AT = '2026-10-01T18:00:00Z';

interface LiveEntry {
  id: string;
  alias: string;
}

/** What 7TV saw, and what it still holds. */
interface MockSevenTvSet {
  live: LiveEntry[];
  adds: string[];
  removes: string[];
  /** Every request to the GQL endpoint, reads included. */
  requests: number;
}

/**
 * Routes everything a run touches besides the tag routes: the set pre-check's owner lookup, the
 * shared-set warning, the bookkeeping routes (`sync-imported`, set-centric `sync-deleted`, resync)
 * and 7TV's GQL, which reads, adds and removes against `live`. `incompleteRead` makes the set read
 * answer a `totalCount` its items do not add up to (a half-known set).
 */
async function mockRunBackend(
  page: Page,
  live: LiveEntry[],
  options: { incompleteRead?: boolean } = {},
): Promise<{
  sevenTv: MockSevenTvSet;
  syncImported: unknown[];
  syncDeleted: unknown[];
}> {
  await mockEmoteSetTargets(page, [
    {
      twitchChannelId: OWNER_TWITCH_ID,
      twitchLogin: CHANNEL,
      isOwnAccount: true,
      trackedChannelName: CHANNEL,
      activeEmoteSetId: ACTIVE_SET_ID,
      sets: [{ id: ACTIVE_SET_ID, name: 'Hauptset', isActive: true }],
    },
  ]);
  await mockSetWarning(page, CHANNEL);
  // The import's target load reads the pinned set by id, over the tracked channel's own name.
  await mockForeignEmoteSetPreview(page, CHANNEL, {
    channelName: CHANNEL,
    emoteSetId: ACTIVE_SET_ID,
    emoteSetName: 'Hauptset',
    emotes: live.map((entry) => ({ sevenTvEmoteId: entry.id, name: entry.alias })),
  });
  const syncImported: unknown[] = [];
  await page.route(`**/api/channels/${CHANNEL}/emotes/sync-imported`, (route) => {
    syncImported.push(route.request().postDataJSON());
    return route.fulfill({ status: 204 });
  });
  await page.route(`**/api/channels/${CHANNEL}/resync`, (route) => route.fulfill({ status: 202 }));
  const syncDeleted = await mockSyncDeletedInSet(page, ACTIVE_SET_ID);

  const sevenTv: MockSevenTvSet = { live: [...live], adds: [], removes: [], requests: 0 };
  await mockSevenTvGql(page, (request) => {
    sevenTv.requests += 1;
    const emoteId = request.variables['emoteId'] as string;
    switch (sevenTvGqlRequestKind(request)) {
      case 'addEmote':
        sevenTv.adds.push(emoteId);
        sevenTv.live.push({ id: emoteId, alias: emoteId });
        return { data: { emoteSets: { emoteSet: { addEmote: { id: emoteId } } } } };
      case 'removeEmote':
        sevenTv.removes.push(emoteId);
        sevenTv.live = sevenTv.live.filter((entry) => entry.id !== emoteId);
        return { data: { emoteSets: { emoteSet: { removeEmote: { id: emoteId } } } } };
      default: {
        const items = sevenTv.live.map((entry) => ({
          alias: entry.alias,
          emote: { id: entry.id, defaultName: entry.alias },
        }));
        return {
          data: {
            emoteSets: {
              emoteSet: {
                emotes: {
                  totalCount: items.length + (options.incompleteRead ? 1 : 0),
                  pageCount: 1,
                  items,
                },
              },
            },
          },
        };
      }
    }
  });
  return { sevenTv, syncImported, syncDeleted };
}

/** Re-routes `active-set` (after `mockChannel`'s own) so a test can move the channel to another set. */
async function mockSwitchableActiveSet(page: Page): Promise<(emoteSetId: string) => void> {
  let activeEmoteSetId = ACTIVE_SET_ID;
  await page.route(`**/api/channels/${CHANNEL}/emotes/active-set`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        activeEmoteSetId,
        capacity: 1000,
        occupiedSlots: 4,
        trackedSince: '2026-06-12T09:14:00Z',
        syncFailureReason: null,
        lastSyncAttemptAtUtc: null,
        botsExcludedSince: null,
        sharedChatSeparatedSince: null,
        duplicateNames: [],
      }),
    }),
  );
  return (emoteSetId) => {
    activeEmoteSetId = emoteSetId;
  };
}

/** The tags page with `Favoriten` chosen — and "Ins Set holen" there, so the tag has a missing emote. */
async function gotoTagWithMissing(page: Page): Promise<void> {
  await page.goto(`/channels/${CHANNEL}/tags?tag=${TAG_ID}`);
  await expect(page.getByRole('heading', { name: 'Favoriten', level: 3 })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ins Set holen' })).toBeVisible();
}

const playIn = (page: Page) => page.getByRole('button', { name: 'Ins Set holen' });
const clearOut = (page: Page) => page.getByRole('button', { name: 'Aus dem Set entfernen' });

test.describe('emote tag runs', () => {
  test('plays a tag in: one emote is added, and the report and the placement carry the tag', async ({
    page,
  }) => {
    await page.clock.install();
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 2, inSetCount: 1 },
    ]);
    await mockTagEntries(page, CHANNEL, TAG_ID, [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM' },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW', inSet: false },
    ]);
    const operations = await mockTagOperations(page, CHANNEL, TAG_ID);
    const placements = await mockTagPlacements(page, CHANNEL, TAG_ID);
    const { sevenTv, syncImported } = await mockRunBackend(page, [
      { id: '7tv-1', alias: 'catJAM' },
    ]);
    await gotoTagWithMissing(page);

    await playIn(page).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('Aus Tag Favoriten')).toBeVisible();
    await expect(dialog.getByText('1 ist schon im Set')).toBeVisible();
    await expect(dialog.locator('#app-dialog-title')).toContainText('1 Emote');
    // Nothing is written before the confirmation; the registration and the complete set read
    // already went out.
    expect(sevenTv.adds).toEqual([]);
    expect(operations.requests).toHaveLength(1);
    expect(sevenTv.requests).toBeGreaterThan(0);
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3_000);
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    await expect.poll(() => placements.requests.length).toBe(1);

    // One ADD, for the one emote the set lacked.
    expect(sevenTv.adds).toEqual(['7tv-2']);
    expect(sevenTv.removes).toEqual([]);
    expect(syncImported).toEqual([
      expect.objectContaining({
        sevenTvEmoteIds: ['7tv-2'],
        sourceKind: 'tag',
        sourceChannelName: null,
        leaderboardSort: null,
        targetEmoteSetId: ACTIVE_SET_ID,
      }),
    ]);
    // The placement report names exactly that emote, under the operation that was registered.
    const registered = operations.requests[0].body as { operationId: string };
    expect(registered).toEqual({
      operationId: expect.stringMatching(UUID),
      kind: 'playIn',
      emoteSetId: ACTIVE_SET_ID,
      targetOwnerTwitchId: OWNER_TWITCH_ID,
    });
    expect(placements.requests[0].body).toEqual({
      operationId: registered.operationId,
      emoteSetId: ACTIVE_SET_ID,
      targetOwnerTwitchId: OWNER_TWITCH_ID,
      sevenTvEmoteIds: ['7tv-2'],
    });
    // The tag now reads as played in (the mock's tag list followed the report).
    await expect(clearOut(page)).toBeVisible();
  });

  test('plays in a tag the set already holds by the time of the click: no run, an empty report, and the tag is played in', async ({
    page,
  }) => {
    await page.clock.install();
    await mockChannel(page);
    // The summary the page loaded still counts the emote as missing; the entries read at the click
    // finds it present — the race the flow's "all present" path stays for.
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 1, inSetCount: 0 },
    ]);
    await mockTagEntries(page, CHANNEL, TAG_ID, [{ sevenTvEmoteId: '7tv-1', alias: 'catJAM' }]);
    const operations = await mockTagOperations(page, CHANNEL, TAG_ID);
    const placements = await mockTagPlacements(page, CHANNEL, TAG_ID);
    const { sevenTv, syncImported } = await mockRunBackend(page, [
      { id: '7tv-1', alias: 'catJAM' },
    ]);
    await gotoTagWithMissing(page);
    await expect(clearOut(page)).toHaveCount(0);

    await playIn(page).click();

    await expect.poll(() => placements.requests.length).toBe(1);
    expect(placements.requests[0].body).toEqual({
      operationId: (operations.requests[0].body as { operationId: string }).operationId,
      emoteSetId: ACTIVE_SET_ID,
      targetOwnerTwitchId: OWNER_TWITCH_ID,
      sevenTvEmoteIds: [],
    });
    await expect(
      page
        .getByText(
          'Das einzige Emote von Favoriten ist schon im Set — der Tag gilt als ins Set geholt.',
        )
        .first(),
    ).toBeVisible();
    // No dialog, no run, nothing written.
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(sevenTv.adds).toEqual([]);
    expect(sevenTv.removes).toEqual([]);
    expect(syncImported).toEqual([]);
    // Played in: the tag's page now offers the clear-out.
    await expect(clearOut(page)).toBeVisible();
    // An empty report placed nothing, so the tag has no placements: the detail head's state line
    // ("über den Tag ins Set geholt am …") and the list's placed count stay hidden alike.
    await expect(page.getByText(/über den Tag ins Set geholt/)).toHaveCount(0);
  });

  test('offers no "Ins Set holen" for a tag whose emotes are all in the set — only "Aus dem Set entfernen", played in or not', async ({
    page,
  }) => {
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 1, inSetCount: 1 },
      {
        id: OTHER_TAG_ID,
        name: 'Lieblinge',
        entryCount: 1,
        inSetCount: 1,
        placedCount: 1,
        active: true,
      },
    ]);
    await mockTagEntries(page, CHANNEL, TAG_ID, [{ sevenTvEmoteId: '7tv-1', alias: 'catJAM' }]);
    await mockTagEntries(page, CHANNEL, OTHER_TAG_ID, [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM' },
    ]);

    await page.goto(`/channels/${CHANNEL}/tags?tag=${TAG_ID}`);
    await expect(page.getByRole('heading', { name: 'Favoriten', level: 3 })).toBeVisible();
    await expect(playIn(page)).toHaveCount(0);
    // Never played in, but its emote is in the set: it can be cleared out (operator 2026-10-05).
    await expect(clearOut(page)).toBeVisible();
    await expect(page.getByText('nicht über den Tag ins Set geholt')).toHaveCount(0);

    await page.goto(`/channels/${CHANNEL}/tags?tag=${OTHER_TAG_ID}`);
    await expect(page.getByRole('heading', { name: 'Lieblinge', level: 3 })).toBeVisible();
    await expect(clearOut(page)).toBeVisible();
    await expect(playIn(page)).toHaveCount(0);
  });

  test('clears a tag out: only the ticked placement goes, the report names ids and revisions, and the tag is no longer played in', async ({
    page,
  }) => {
    await page.clock.install();
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 2, inSetCount: 2, placedCount: 1, active: true },
    ]);
    await mockTagEntries(
      page,
      CHANNEL,
      TAG_ID,
      [
        {
          sevenTvEmoteId: '7tv-1',
          alias: 'catJAM',
          placedByThisTag: true,
          placedAtUtc: PLACED_AT,
          placementOperationId: 'a1b2c3d4-0000-4000-8000-000000000001',
        },
        { sevenTvEmoteId: '7tv-2', alias: 'monkaW' },
      ],
      { activationOperationId: 'a1b2c3d4-0000-4000-8000-0000000000aa' },
    );
    const operations = await mockTagOperations(page, CHANNEL, TAG_ID);
    const removal = await mockTagRemoval(page, CHANNEL, TAG_ID);
    const { sevenTv, syncDeleted } = await mockRunBackend(page, [
      { id: '7tv-1', alias: 'catJAM' },
      { id: '7tv-2', alias: 'monkaW' },
    ]);
    await page.goto(`/channels/${CHANNEL}/tags?tag=${TAG_ID}`);
    await expect(page.getByText(/über den Tag ins Set geholt am/)).toBeVisible();

    await clearOut(page).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText('Favoriten aus dem Set entfernen');
    const placed = dialog.locator('input[data-emote-id="7tv-1"]');
    const already = dialog.locator('input[data-emote-id="7tv-2"]');
    await expect(placed).toBeChecked();
    await expect(already).not.toBeChecked();
    await expect(dialog.getByText(/am .* ins Set geholt/)).toBeVisible();
    await expect(dialog.getByText('nicht über diesen Tag ins Set gekommen')).toBeVisible();
    await expect(dialog.getByRole('status')).toHaveText('1 Emote wird entfernt, 1 bleibt im Set');
    expect(sevenTv.removes).toEqual([]);
    await dialog.getByRole('button', { name: 'Aus dem Set entfernen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3_000);
    await expect(page.getByText('1 gelöscht · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    await expect.poll(() => removal.requests.length).toBe(1);

    expect(sevenTv.removes).toEqual(['7tv-1']);
    expect(sevenTv.adds).toEqual([]);
    expect(syncDeleted).toEqual([
      {
        sevenTvEmoteIds: ['7tv-1'],
        expectedChannelName: CHANNEL,
        targetOwnerTwitchId: OWNER_TWITCH_ID,
      },
    ]);
    expect(removal.requests[0].body).toEqual({
      operationId: (operations.requests[0].body as { operationId: string }).operationId,
      emoteSetId: ACTIVE_SET_ID,
      targetOwnerTwitchId: OWNER_TWITCH_ID,
      activationOperationId: 'a1b2c3d4-0000-4000-8000-0000000000aa',
      snapshot: [
        { sevenTvEmoteId: '7tv-1', placementOperationId: 'a1b2c3d4-0000-4000-8000-000000000001' },
      ],
      removedIds: ['7tv-1'],
      keptIds: [],
    });
    expect(operations.requests[0].body).toEqual(
      expect.objectContaining({ kind: 'removal', emoteSetId: ACTIVE_SET_ID }),
    );
    // The protocol of the run is offered, and the reloaded tag is no longer played in. monkaW is
    // still in the set, so "Aus dem Set entfernen" stays — now for a tag that is not played in.
    await expect(page.getByRole('button', { name: 'Protokoll herunterladen' })).toBeVisible();
    await expect(page.getByText(/über den Tag ins Set geholt am/)).toHaveCount(0);
    await expect(clearOut(page)).toBeVisible();
  });

  test('clears out a tag whose only placement another active tag still needs: no REMOVE, an immediate report, the tag is no longer played in', async ({
    page,
  }) => {
    await page.clock.install();
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 1, inSetCount: 1, placedCount: 1, active: true },
      {
        id: OTHER_TAG_ID,
        name: 'Lieblinge',
        entryCount: 1,
        inSetCount: 1,
        placedCount: 1,
        active: true,
      },
    ]);
    await mockTagEntries(
      page,
      CHANNEL,
      TAG_ID,
      [
        {
          sevenTvEmoteId: '7tv-1',
          alias: 'catJAM',
          placedByThisTag: true,
          placedAtUtc: PLACED_AT,
          placementOperationId: 'a1b2c3d4-0000-4000-8000-000000000001',
          heldByActiveTags: [{ id: OTHER_TAG_ID, name: 'Lieblinge' }],
        },
      ],
      { activationOperationId: 'a1b2c3d4-0000-4000-8000-0000000000aa' },
    );
    await mockTagOperations(page, CHANNEL, TAG_ID);
    const removal = await mockTagRemoval(page, CHANNEL, TAG_ID);
    const { sevenTv, syncDeleted } = await mockRunBackend(page, [{ id: '7tv-1', alias: 'catJAM' }]);
    await page.goto(`/channels/${CHANNEL}/tags?tag=${TAG_ID}`);
    await expect(page.getByText(/über den Tag ins Set geholt am/)).toBeVisible();

    await clearOut(page).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('input[data-emote-id="7tv-1"]')).not.toBeChecked();
    await expect(dialog.getByText(/wird noch von Lieblinge gebraucht/)).toBeVisible();
    await expect(dialog.getByText(/Es wird nichts bei 7TV gelöscht/)).toBeVisible();
    await dialog.getByRole('button', { name: 'Aus dem Set entfernen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await expect.poll(() => removal.requests.length).toBe(1);
    expect(removal.requests[0].body).toEqual(
      expect.objectContaining({
        snapshot: [
          { sevenTvEmoteId: '7tv-1', placementOperationId: 'a1b2c3d4-0000-4000-8000-000000000001' },
        ],
        removedIds: [],
        keptIds: ['7tv-1'],
      }),
    );
    await expect(
      page.getByText('Bei Favoriten war nichts aus dem Set zu entfernen.').first(),
    ).toBeVisible();
    await expect(page.getByText(/über den Tag ins Set geholt am/)).toHaveCount(0);
    // Nothing reached 7TV, and nothing was reported as deleted. catJAM is still in the set, so
    // "Aus dem Set entfernen" stays for the tag that is no longer played in.
    expect(sevenTv.removes).toEqual([]);
    expect(sevenTv.adds).toEqual([]);
    expect(syncDeleted).toEqual([]);
    await expect(clearOut(page)).toBeVisible();
  });

  test('clears out a tag that was never played in because its emotes were all in the set: the dialog proposes all but what another active tag needs, and the report carries no activation and no placements', async ({
    page,
  }) => {
    await page.clock.install();
    await mockChannel(page);
    // The person tagged emotes that were in the set already (say, the Halloween ones, to clear
    // them out later). catJAM is also needed by the active tag "Lieblinge".
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 3, inSetCount: 3 },
      {
        id: OTHER_TAG_ID,
        name: 'Lieblinge',
        entryCount: 1,
        inSetCount: 1,
        placedCount: 1,
        active: true,
      },
    ]);
    await mockTagEntries(page, CHANNEL, TAG_ID, [
      {
        sevenTvEmoteId: '7tv-1',
        alias: 'catJAM',
        heldByActiveTags: [{ id: OTHER_TAG_ID, name: 'Lieblinge' }],
        placedByOtherTags: [{ id: OTHER_TAG_ID, name: 'Lieblinge' }],
      },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW' },
      { sevenTvEmoteId: '7tv-3', alias: 'KEKW' },
    ]);
    const operations = await mockTagOperations(page, CHANNEL, TAG_ID);
    // Not played in: there is no activation the report could end.
    const removal = await mockTagRemoval(page, CHANNEL, TAG_ID, { deactivated: false });
    const { sevenTv, syncDeleted } = await mockRunBackend(page, [
      { id: '7tv-1', alias: 'catJAM' },
      { id: '7tv-2', alias: 'monkaW' },
      { id: '7tv-3', alias: 'KEKW' },
    ]);
    await page.goto(`/channels/${CHANNEL}/tags?tag=${TAG_ID}`);
    await expect(page.getByRole('heading', { name: 'Favoriten', level: 3 })).toBeVisible();
    await expect(playIn(page)).toHaveCount(0);

    await clearOut(page).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText('Favoriten aus dem Set entfernen');
    await expect(dialog.locator('input[data-emote-id="7tv-2"]')).toBeChecked();
    await expect(dialog.locator('input[data-emote-id="7tv-3"]')).toBeChecked();
    await expect(dialog.locator('input[data-emote-id="7tv-1"]')).not.toBeChecked();
    await expect(dialog.getByText(/wird noch von Lieblinge gebraucht/)).toBeVisible();
    // Nothing of it was played in, so nothing reads "not played in by this tag" either.
    await expect(dialog.getByText('nicht über diesen Tag ins Set gekommen')).toHaveCount(0);
    await expect(dialog.getByRole('status')).toHaveText(
      '2 Emotes werden entfernt, 1 bleibt im Set',
    );
    expect(sevenTv.removes).toEqual([]);
    await dialog.getByRole('button', { name: 'Aus dem Set entfernen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3_000);
    await expect(page.getByText('2 gelöscht · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    await expect.poll(() => removal.requests.length).toBe(1);

    expect([...sevenTv.removes].sort()).toEqual(['7tv-2', '7tv-3']);
    expect(sevenTv.adds).toEqual([]);
    expect(syncDeleted).toEqual([
      expect.objectContaining({
        sevenTvEmoteIds: expect.arrayContaining(['7tv-2', '7tv-3']),
        expectedChannelName: CHANNEL,
      }),
    ]);
    const body = removal.requests[0].body as { removedIds: string[] };
    expect(body).toEqual({
      operationId: (operations.requests[0].body as { operationId: string }).operationId,
      emoteSetId: ACTIVE_SET_ID,
      targetOwnerTwitchId: OWNER_TWITCH_ID,
      activationOperationId: null,
      snapshot: [],
      removedIds: expect.any(Array),
      keptIds: [],
    });
    expect([...body.removedIds].sort()).toEqual(['7tv-2', '7tv-3']);
    // catJAM is still in the set and the tag still names it: "Aus dem Set entfernen" stays for that one.
    await expect(clearOut(page)).toBeVisible();
    await expect(page.getByText(/über den Tag ins Set geholt am/)).toHaveCount(0);
  });

  // Operator decision 2026-10-05: a marking on the tag's grid is what the clear-out proposes.
  test('clears out only the emotes marked in the grid: the rest is "nicht markiert", and the marking goes once the run starts', async ({
    page,
  }) => {
    await page.clock.install();
    await mockChannel(page);
    // The operator's case: a tag that is not played in, all four of its emotes in the set.
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 4, inSetCount: 4 },
    ]);
    await mockTagEntries(page, CHANNEL, TAG_ID, [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM' },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW' },
      { sevenTvEmoteId: '7tv-3', alias: 'KEKW' },
      { sevenTvEmoteId: '7tv-4', alias: 'PogU' },
    ]);
    await mockTagOperations(page, CHANNEL, TAG_ID);
    const removal = await mockTagRemoval(page, CHANNEL, TAG_ID, { deactivated: false });
    const { sevenTv } = await mockRunBackend(page, [
      { id: '7tv-1', alias: 'catJAM' },
      { id: '7tv-2', alias: 'monkaW' },
      { id: '7tv-3', alias: 'KEKW' },
      { id: '7tv-4', alias: 'PogU' },
    ]);
    await page.goto(`/channels/${CHANNEL}/tags?tag=${TAG_ID}`);
    await expect(page.getByRole('heading', { name: 'Favoriten', level: 3 })).toBeVisible();

    const grid = page.getByRole('group', { name: 'Emotes in Favoriten' });
    await grid.getByRole('button', { name: 'monkaW' }).click();
    await grid.getByRole('button', { name: 'PogU' }).click();
    await expect(page.getByText('2 markiert', { exact: true })).toBeVisible();

    await clearOut(page).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('Vorgeschlagen sind deine 2 markierten Emotes.')).toBeVisible();
    await expect(dialog.getByText(/Der Tag hat nichts ins Set geholt/)).toHaveCount(0);
    await expect(dialog.locator('input[data-emote-id="7tv-2"]')).toBeChecked();
    await expect(dialog.locator('input[data-emote-id="7tv-4"]')).toBeChecked();
    await expect(dialog.locator('input[data-emote-id="7tv-1"]')).not.toBeChecked();
    await expect(dialog.locator('input[data-emote-id="7tv-3"]')).not.toBeChecked();
    await expect(dialog.getByText('nicht markiert')).toHaveCount(2);
    await expect(dialog.getByRole('status')).toHaveText(
      '2 Emotes werden entfernt, 2 bleiben im Set',
    );
    await dialog.getByRole('button', { name: 'Aus dem Set entfernen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3_000);
    await expect(page.getByText('2 gelöscht · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    await expect.poll(() => removal.requests.length).toBe(1);
    expect([...sevenTv.removes].sort()).toEqual(['7tv-2', '7tv-4']);
    expect(sevenTv.adds).toEqual([]);
    // The marking went with the started run.
    await expect(page.getByText('2 markiert', { exact: true })).toHaveCount(0);
    await expect(grid.locator('[aria-pressed="true"]')).toHaveCount(0);
  });

  // Operator decision 2026-10-05, mirroring the clear-out: a marking narrows the play-in.
  test('adds only the emotes marked in the grid: the button and the dialog count 2, two ADDs, and the marking goes once the run starts', async ({
    page,
  }) => {
    await page.clock.install();
    await mockChannel(page);
    // The operator's case: none of the tag's four emotes is in the set, two of them are marked.
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 4, inSetCount: 0 },
    ]);
    await mockTagEntries(page, CHANNEL, TAG_ID, [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM', inSet: false },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW', inSet: false },
      { sevenTvEmoteId: '7tv-3', alias: 'KEKW', inSet: false },
      { sevenTvEmoteId: '7tv-4', alias: 'PogU', inSet: false },
    ]);
    const operations = await mockTagOperations(page, CHANNEL, TAG_ID);
    const placements = await mockTagPlacements(page, CHANNEL, TAG_ID);
    const { sevenTv } = await mockRunBackend(page, [{ id: '7tv-9', alias: 'OMEGALUL' }]);
    await gotoTagWithMissing(page);

    const grid = page.getByRole('group', { name: 'Emotes in Favoriten' });
    await grid.getByRole('button', { name: /^monkaW/ }).click();
    await grid.getByRole('button', { name: /^PogU/ }).click();
    await expect(page.getByText('2 markiert', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Ins Set holen (2)' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toContainText('2 Emotes');
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3_000);
    await expect(page.getByText('2 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    await expect.poll(() => placements.requests.length).toBe(1);
    expect([...sevenTv.adds].sort()).toEqual(['7tv-2', '7tv-4']);
    expect(sevenTv.removes).toEqual([]);
    // The placement report names exactly the two added emotes, under the registered operation.
    const registered = operations.requests[0].body as { operationId: string };
    expect(placements.requests[0].body).toEqual({
      operationId: registered.operationId,
      emoteSetId: ACTIVE_SET_ID,
      targetOwnerTwitchId: OWNER_TWITCH_ID,
      sevenTvEmoteIds: ['7tv-2', '7tv-4'],
    });
    // The marking went with the started run.
    await expect(page.getByText('2 markiert', { exact: true })).toHaveCount(0);
    await expect(grid.locator('[aria-pressed="true"]')).toHaveCount(0);
  });

  test('an incompletely read set blocks both the play-in and the clear-out without a write', async ({
    page,
  }) => {
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 2, inSetCount: 1, placedCount: 1, active: true },
    ]);
    await mockTagEntries(
      page,
      CHANNEL,
      TAG_ID,
      [
        {
          sevenTvEmoteId: '7tv-1',
          alias: 'catJAM',
          placedByThisTag: true,
          placedAtUtc: PLACED_AT,
          placementOperationId: 'a1b2c3d4-0000-4000-8000-000000000001',
        },
        { sevenTvEmoteId: '7tv-2', alias: 'monkaW', inSet: false },
      ],
      { activationOperationId: 'a1b2c3d4-0000-4000-8000-0000000000aa' },
    );
    const operations = await mockTagOperations(page, CHANNEL, TAG_ID);
    const placements = await mockTagPlacements(page, CHANNEL, TAG_ID);
    const removal = await mockTagRemoval(page, CHANNEL, TAG_ID);
    const { sevenTv, syncImported, syncDeleted } = await mockRunBackend(
      page,
      [{ id: '7tv-1', alias: 'catJAM' }],
      { incompleteRead: true },
    );
    await gotoTagWithMissing(page);

    const reason =
      'Die Einträge des Sets konnten nur teilweise von 7TV gelesen werden — es wurde nichts geändert.';
    await playIn(page).click();
    await expect(page.getByText(reason)).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    // The play-in's own banner, from its own registration and read — before the clear-out runs.
    expect(operations.requests).toHaveLength(1);
    expect(sevenTv.requests).toBe(1);

    await clearOut(page).click();
    // A click clears the old banner at once, so the one standing after the clear-out's own
    // registration and read is the clear-out's.
    await expect.poll(() => sevenTv.requests).toBe(2);
    await expect(page.getByText(reason)).toHaveCount(1);
    await expect(page.getByText(reason)).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Each click registered its operation and read the set; nothing else went anywhere.
    expect(operations.requests).toHaveLength(2);
    expect(sevenTv.requests).toBe(2);
    expect(sevenTv.adds).toEqual([]);
    expect(sevenTv.removes).toEqual([]);
    expect(placements.requests).toEqual([]);
    expect(removal.requests).toEqual([]);
    expect(syncImported).toEqual([]);
    expect(syncDeleted).toEqual([]);
  });

  test('a refused registration (403) shows a banner and goes no further: no set read, no write, no dialog', async ({
    page,
  }) => {
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 2, inSetCount: 1 },
    ]);
    await mockTagEntries(page, CHANNEL, TAG_ID, [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM' },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW', inSet: false },
    ]);
    const operations = await mockTagOperations(page, CHANNEL, TAG_ID, { status: 403 });
    const placements = await mockTagPlacements(page, CHANNEL, TAG_ID);
    const { sevenTv, syncImported } = await mockRunBackend(page, [
      { id: '7tv-1', alias: 'catJAM' },
    ]);
    await gotoTagWithMissing(page);

    await playIn(page).click();

    await expect(page.getByText('Du darfst dieses Set bei 7TV nicht bearbeiten.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Erneut versuchen' })).toHaveCount(0);
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(operations.requests).toHaveLength(1);
    // Not even the read: 7TV was never asked anything.
    expect(sevenTv.requests).toBe(0);
    expect(sevenTv.adds).toEqual([]);
    expect(sevenTv.removes).toEqual([]);
    expect(placements.requests).toEqual([]);
    expect(syncImported).toEqual([]);
  });

  test('a set switch between the click and the confirmation writes nothing', async ({ page }) => {
    await page.clock.install();
    await mockChannel(page);
    const switchActiveSet = await mockSwitchableActiveSet(page);
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 2, inSetCount: 1 },
    ]);
    await mockTagEntries(page, CHANNEL, TAG_ID, [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM' },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW', inSet: false },
    ]);
    await mockTagOperations(page, CHANNEL, TAG_ID);
    const placements = await mockTagPlacements(page, CHANNEL, TAG_ID);
    const { sevenTv, syncImported } = await mockRunBackend(page, [
      { id: '7tv-1', alias: 'catJAM' },
    ]);
    await gotoTagWithMissing(page);

    await playIn(page).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('Aus Tag Favoriten')).toBeVisible();

    // The channel's active set moves on while the dialog is open: the tags page reads it again on
    // `channel.synced`.
    switchActiveSet(OTHER_SET_ID);
    await emitLive(page, { type: 'channel.synced', channel: CHANNEL });
    await page.clock.runFor(2_000);
    // The page has taken over the new set before the confirmation is clicked: its header line
    // names it. (A text query, since the open dialog hides the page from the accessibility tree.)
    await expect(
      page.getByText('Die Zahlen beziehen sich auf das aktive Set Halloween.'),
    ).toBeVisible();

    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await page.clock.runFor(3_000);

    // The guard aborted, and says so on the page — not under the buttons that went with the host.
    await expect(
      page.getByText('Das aktive Set hat gewechselt — Seite neu laden.').first(),
    ).toBeVisible();
    expect(sevenTv.adds).toEqual([]);
    expect(sevenTv.removes).toEqual([]);
    expect(syncImported).toEqual([]);
    expect(placements.requests).toEqual([]);
    await expect(page.locator('app-import-progress-section')).toHaveCount(0);
  });

  test('a set that has already moved on when the click reads the tag says so and registers nothing', async ({
    page,
  }) => {
    await mockChannel(page);
    await mockTags(page, CHANNEL, [
      { id: TAG_ID, name: 'Favoriten', entryCount: 2, inSetCount: 1 },
    ]);
    const entries = [
      { sevenTvEmoteId: '7tv-1', alias: 'catJAM' },
      { sevenTvEmoteId: '7tv-2', alias: 'monkaW', inSet: false },
    ];
    await mockTagEntries(page, CHANNEL, TAG_ID, entries);
    const operations = await mockTagOperations(page, CHANNEL, TAG_ID);
    const { sevenTv, syncImported } = await mockRunBackend(page, [
      { id: '7tv-1', alias: 'catJAM' },
    ]);
    await gotoTagWithMissing(page);

    // The server moved the channel to another set; the page has not heard of it yet.
    await mockTagEntries(page, CHANNEL, TAG_ID, entries, { isActiveSet: false });
    await playIn(page).click();

    await expect(page.getByText('Das aktive Set hat gewechselt — Seite neu laden.')).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(operations.requests).toEqual([]);
    expect(sevenTv.requests).toBe(0);
    expect(sevenTv.adds).toEqual([]);
    expect(syncImported).toEqual([]);
  });
});
