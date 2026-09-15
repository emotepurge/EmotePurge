import { Locator, Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  MockLeaderboardEmote,
  emitLive,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelScopedResync,
  mockChannelStatus,
  mockDuplicateEmoteNames,
  mockEmoteList,
  mockMyChannels,
  mockSetWarning,
  mockSevenTvGql,
  mockSevenTvLeaderboard,
  mockSyncImported,
  mockUsageTotals,
  mockVoteSessionList,
  mockWorkerHealth,
} from './support/mocks';

/**
 * The push flow (#72, K3): pick emotes on the usage-stats grid, choose a destination (another
 * channel or a file), and — for a channel destination — see the confirmation dialog before
 * anything is written. R14: no isolated component tests for these dialogs (Regel 12), so this is
 * their only coverage besides the audit-harness screenshots (`ui-audit.audit.ts`).
 */

const SOURCE_CHANNEL = 'sensitron';
const TARGET_CHANNEL = 'aatrociity';

const SOURCE_EMOTES: MockEmoteUsage[] = [
  {
    emoteId: 'e1',
    emoteName: 'CatJAM',
    sevenTvEmoteId: '7tv-1',
    imageUrl: 'https://cdn.7tv.app/emote/1/2x.webp',
    totalUseCount: 500,
  },
  {
    emoteId: 'e2',
    emoteName: 'KEKW',
    sevenTvEmoteId: '7tv-2',
    imageUrl: 'https://cdn.7tv.app/emote/2/2x.webp',
    totalUseCount: 300,
  },
  {
    emoteId: 'e3',
    emoteName: 'Pog',
    sevenTvEmoteId: '7tv-3',
    imageUrl: 'https://cdn.7tv.app/emote/3/2x.webp',
    totalUseCount: 50,
  },
];

/** The target channel's own grid, deliberately sharing no emote id or name with SOURCE_EMOTES: on
 *  the target page a visible `Sadge` cell is proof the rows really changed hands. */
const TARGET_EMOTES: MockEmoteUsage[] = [
  {
    emoteId: 't1',
    emoteName: 'Sadge',
    sevenTvEmoteId: '7tv-t1',
    imageUrl: 'https://cdn.7tv.app/emote/11/2x.webp',
    totalUseCount: 120,
  },
];

/**
 * Holds an already-routed endpoint until the returned callback is invoked, then lets the handler
 * registered *before* it answer normally (`route.fallback`). Playwright matches handlers in reverse
 * registration order, so this must be registered after the mock it defers to.
 *
 * Used to pin the ordering of two responses that normally race, which is the only way to observe a
 * page state that exists between them.
 */
async function deferRoute(page: Page, pattern: string): Promise<() => void> {
  let release!: () => void;
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(pattern, async (route) => {
    await held;
    await route.fallback();
  });
  return release;
}

/** One channel's usage-stats page, mocked enough to load — permissions, status, the duplicate-name
 *  check and the totals grid. Mirrors `usage-atlas.e2e.spec.ts`'s `openAtlas` helper, generalized
 *  to a channel name so both the source and (in the channel-switch checks) the target page can use
 *  it. */
async function mockWorkspace(
  page: Page,
  channelName: string,
  emotes: MockEmoteUsage[] = [],
  activeEmoteSetId = 'set-1',
): Promise<void> {
  await mockChannelPermissions(page, channelName);
  await mockChannelStatus(page, channelName);
  await mockDuplicateEmoteNames(page, channelName);
  await mockActiveEmoteSet(page, channelName, activeEmoteSetId, {
    capacity: 1000,
    occupiedSlots: 3,
  });
  await mockUsageTotals(page, channelName, emotes);
}

async function gotoUsageStats(page: Page, channelName: string): Promise<void> {
  await page.goto(`/channels/${channelName}/usage-stats`);
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
  // Same reasoning as usage-atlas's openAtlas: wait out the skeleton's own role="status" so a bare
  // getByRole('status') later does not resolve to two elements under strict mode.
  await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
}

const cell = (page: Page, name: string) =>
  page.getByRole('button', { name: new RegExp(`^${name} ·`) });

// Exact match, not the Playwright default (substring): the dock's shortcut (#80, §8.7) carries the
// same verb plus a trailing count — "Übertragen (2)" — which would otherwise also match
// this name and turn every use below into a strict-mode violation. This locator is always the
// HEADER trigger; see dockCopyButton for the dock's second entry point into openImportTarget().
const copyButton = (page: Page) => page.getByRole('button', { name: 'Übertragen', exact: true });

// The dock's shortcut into openImportTarget('selection') (#80, §8.7): same verb as copyButton, no
// scope radiogroup in the dialog it opens, count baked into the accessible name.
const dockCopyButton = (page: Page, count: number) =>
  page.getByRole('button', { name: `Übertragen (${count})`, exact: true });

/**
 * Opens the one import dialog (#91, #147) via the header trigger, walks its first step to the file
 * source, and returns the file input sitting inside it. Locale-independent by position, same
 * reasoning as `ui-audit.audit.ts` for its neighbour: the trigger's label is translated and shares
 * no word with the other header buttons, so this goes by position instead — `main header button`
 * `.nth(2)`, after `.nth(0)` (Exportieren) and `.nth(1)` (Übertragen). Scoped to `main` because the
 * app shell has its own top-level `<header>` (the account menu) that an unscoped `header button`
 * would count first. The source row itself is picked by its label: it is the dialog's own content,
 * not a header button, and there is no position rule to lean on there.
 */
async function openFileImportDialog(page: Page): Promise<Locator> {
  const dialog = page.getByRole('dialog');
  await page.locator('main header button').nth(2).click();
  await expect(dialog.locator('#app-dialog-title')).toHaveText('Emotes importieren');
  await dialog.getByRole('button', { name: /^Aus einer Datei/ }).click();
  await expect(dialog.locator('#app-dialog-title')).toHaveText('Datei importieren');
  return dialog.locator('input[type="file"]');
}

/**
 * Waits past the still-open file-import dialog (plan §1.1, task-5 "Falle 1"): after
 * `setInputFiles`, that dialog stays open until `file.text()` resolves, only then closing and
 * handing off to the confirm dialog. A bare wait on `#app-dialog-title` resolves immediately
 * against the file-import dialog's OWN title (still attached at that instant) rather than waiting
 * for the confirm dialog to replace it — the following click on "Abbrechen" would then land on the
 * wrong dialog, and the failure would look like a timing flake. `toHaveText` instead polls until the
 * title reads as the import-confirm dialog's own ("N Emote(s) nach <channel> kopieren?"), so it
 * survives the transition between the two dialogs.
 */
async function waitForImportConfirmDialog(page: Page): Promise<Locator> {
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('#app-dialog-title')).toHaveText(/kopieren\?$/);
  return dialog;
}

test.describe('push flow: picker to confirmation dialog', () => {
  test('a channel target shows origin, target, an already-present row and a name collision', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    // Broadcaster of the current channel; an editor of one tracked and one untracked channel; a
    // moderator-only channel — the filter is isBroadcaster || isSevenTvEditor (import-target-
    // options.ts), so modonly must not appear and the current channel is always excluded.
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
      { channelName: 'untrackedbuddy', isSevenTvEditor: true, isTracked: false },
      { channelName: 'modonly', isModerator: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // Target's own data, fetched by loadImportTarget once a target is chosen: one row shares an id
    // with a selected emote (CatJAM, 7tv-1 — already present), the other shares a NAME with a
    // selected emote under a different id (KEKW's name, different id — a collision).
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, [
      { sevenTvEmoteId: '7tv-1', name: 'CatJAM' },
      { sevenTvEmoteId: 'target-99', name: 'KEKW' },
    ]);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await expect(copyButton(page)).toBeEnabled();
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await expect(picker.locator('#app-dialog-title')).toHaveText('Emotes übertragen');

    // Scope defaults to the selection (R12), not to the visible list.
    await expect(picker.getByRole('radio', { name: 'Auswahl (2)' })).toBeChecked();

    // Exactly the two expected channel rows, in alphabetical order, and nothing else — the picker
    // no longer offers a file destination (#141, moved to the export dialog, see the
    // 'export dialog: purpose-sorted options' describe block below).
    await expect(picker.getByRole('radio', { name: '#aatrociity' })).toBeEnabled();
    await expect(
      picker.getByRole('radio', { name: /^#untrackedbuddy \(Kanal muss erst beitreten\)$/ }),
    ).toBeDisabled();
    await expect(picker.getByText('#modonly')).toHaveCount(0);
    await expect(picker.getByText('#sensitron', { exact: true })).toHaveCount(0);

    await picker.getByRole('radio', { name: '#aatrociity' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '1 Emote nach aatrociity kopieren?',
    );
    await expect(confirm.getByText('Aus Kanal sensitron')).toBeVisible();
    await expect(confirm.getByText('Ziel: aatrociity · Set target-set')).toBeVisible();
    await expect(
      confirm.getByText('1 Emote ist bereits im Zielset und wird übersprungen.'),
    ).toBeVisible();
    await expect(
      confirm.getByText('1 Name ist im Zielset schon vergeben — 7TV wird dieses Emote ablehnen:'),
    ).toBeVisible();
    await expect(confirm.locator('app-name-preview-list')).toContainText('KEKW');
    // occupied 3 + the one row that survives the already-present filter (KEKW, name collision but
    // still added — nameCollisions stays IN toAdd per the import-preview contract) = 4 of 1000.
    await expect(confirm.getByText('Das Set hätte danach 4 von 1000 Slots belegt.')).toBeVisible();
    await expect(confirm.getByRole('button', { name: 'Kopieren' })).toBeEnabled();
  });

  /**
   * The dock's second entry point into the same flow (#80, §8.7): same verb, but `forcedScope:
   * 'selection'` skips the scope question outright instead of merely defaulting to it. Proven two
   * ways rather than just reading `forcedScope` off the component: the radiogroup the header path
   * shows in the test above is entirely absent here despite a selection existing (the condition
   * that would normally render it), and the run that follows touches only the two MARKED rows —
   * Pog stays out of both the confirmation count and the 7TV calls, even though it is visible on
   * the same grid and would have been included under scope `visible`.
   */
  test('the dock shortcut skips the scope question and copies exactly the marked rows', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    // Empty target set: nothing to collide with, so the row count in the confirm dialog is pure
    // proof of scope, not diluted by an already-present or name-collision filter.
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    const addedEmoteIds: unknown[] = [];
    await mockSevenTvGql(page, (request) => {
      addedEmoteIds.push(request.variables['emoteId']);
      return {
        data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
      };
    });
    // Frozen for the same reason as the other run-completion tests: the engine's trailing pacing
    // delay would otherwise race a real wait.
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // Marks CatJAM and KEKW, deliberately leaving Pog unmarked — the third SOURCE_EMOTES row that
    // scope `visible` would have swept in.
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await expect(dockCopyButton(page, 2)).toBeEnabled();
    await dockCopyButton(page, 2).click();

    const picker = page.getByRole('dialog');
    await expect(picker.locator('#app-dialog-title')).toHaveText('Emotes übertragen');
    // The scope question itself is gone, not just pre-answered — contrast with the header path's
    // "Auswahl (2)" radio checked by default in the test above.
    await expect(picker.getByRole('radiogroup', { name: 'Exportumfang' })).toHaveCount(0);
    await expect(picker.getByRole('radio', { name: /^Auswahl/ })).toHaveCount(0);
    await expect(picker.getByRole('radio', { name: /^Gefilterte Liste/ })).toHaveCount(0);

    await picker.getByRole('radio', { name: '#aatrociity' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = page.getByRole('dialog');
    // Two, not three: proof the forced scope actually reached the confirm step, not just the
    // picker's own rendering.
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '2 Emotes nach aatrociity kopieren?',
    );
    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(2000);
    await expect(page.getByText('2 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();

    // CatJAM and KEKW's ids, in either order, and nothing else — Pog's 7tv-3 never went out.
    expect(addedEmoteIds.sort()).toEqual(['7tv-1', '7tv-2']);
  });

  /**
   * #80 review fix: a silent totals reload (`usage.flushed`/`channel.synced`, both routed through
   * `loadTotals(..., { preserveSelection: true })`) can drop a marked row out of `atlasOrder()` —
   * an emote archived on 7TV from outside this tab is the concrete cause, since the totals query
   * filters `!e.IsArchived`. At the time of the #80 fix this happened without touching
   * `selection.selectedKeys()`, which `preserveSelection` back then only ever left alone: the dock
   * shortcut's count and lock followed that raw, now-stale key count, so the label kept promising
   * the old row count and the button stayed enabled — a click ran straight into
   * `openImportTarget`'s `captured.selection.length === 0` guard and did nothing, no dialog, no
   * error, no feedback. The #80 fix put the label and the lock on `importShortcutSelectionCount`
   * (built on `selection.selectedItems()`) instead, which is what kept a *partial* loss (see the
   * `#94` block below) from relanding on that same silent-no-op.
   *
   * #94 went one step further and started pruning `selectedKeySet` itself
   * (`ListSelection.retainAmong`) against the reload's payload, so the outcome asserted below is
   * no longer a relocked "(0)" button — with the raw key count now also at zero, the dock's own
   * mount condition (`actionDockHasContent`) is false and the whole dock, this button included,
   * disappears. See the `#94` block for that mechanism's own coverage; this test only needs to
   * keep proving the reload really happened (Pog survives, CatJAM does not).
   */
  test('a live reload that archives every marked row makes the dock disappear instead of running silently into an empty capture', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // Marks CatJAM and KEKW; Pog is deliberately left both unmarked and, below, the only row the
    // reload still returns — its continued presence is proof the grid really reloaded rather than
    // having gone blank.
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await expect(dockCopyButton(page, 2)).toBeEnabled();

    // Re-registering the same route wins over mockWorkspace's earlier one (Playwright runs the
    // most-recently registered handler first, and this one fulfills instead of falling back) — the
    // next totals fetch answers as if CatJAM and KEKW had just been archived from outside this tab.
    await mockUsageTotals(page, SOURCE_CHANNEL, [SOURCE_EMOTES[2]]);
    await emitLive(page, { type: 'usage.flushed', channel: SOURCE_CHANNEL });
    // liveReload collapses the burst over CHANNEL_RELOAD_DEBOUNCE_MS (1 s) before it reloads.
    await page.clock.runFor(1_500);

    await expect(cell(page, 'Pog')).toBeVisible();
    await expect(cell(page, 'CatJAM')).toHaveCount(0);

    // Since #94, the raw selection no longer survives untouched: both marked keys are pruned, so
    // the dock's own mount condition drops to false and the whole thing — not just this button — is
    // gone, rather than sitting there relocked at "(0)".
    await expect(dockCopyButton(page, 2)).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Übertragen \(\d+\)$/ })).toHaveCount(0);
  });
});

/**
 * #148: 7TV's network-wide leaderboard as the import dialog's third source (spec AK 15/16/22).
 * Unlike the other two sources the target is never asked for — `import-trigger.ts` always starts
 * `startLeaderboardImportFlow` against the page's own channel, exactly as it does for the file path
 * — so both tests below target `SOURCE_CHANNEL` itself rather than `TARGET_CHANNEL`. The mutation
 * pattern (7TV GQL stub seeding the write token, `page.clock.install()`, a captured `sync-imported`
 * body, `mockChannelScopedResync`) mirrors the completed runs elsewhere in this file (e.g. "the dock
 * shortcut skips the scope question…" above) — the same shape the push flow uses for every source.
 */
test.describe('push flow: the 7TV leaderboard source (#148)', () => {
  const LEADERBOARD_EMOTES: MockLeaderboardEmote[] = [
    { sevenTvEmoteId: 'lb-1', name: 'LBOne', topAllTime: 500_000, trending: 900 },
    { sevenTvEmoteId: 'lb-2', name: 'LBTwo', topAllTime: 300_000, trending: 700 },
  ];

  test('picking two leaderboard rows completes a run whose sync-imported body names the sort as origin', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockSetWarning(page, SOURCE_CHANNEL);
    // Empty target list: both picked rows survive the already-present filter, so the confirm
    // dialog's count is pure proof of the picked rows, not diluted by a collision (same reasoning as
    // the dock-shortcut test above).
    await mockEmoteList(page, SOURCE_CHANNEL, []);
    // Only TRENDING_DAILY is answered — it is the step's entry point (LeaderboardStep.sortBy's
    // initial value), so the default load never has to switch lists.
    await mockSevenTvLeaderboard(page, {
      TRENDING_DAILY: { totalCount: 2, truncated: false, emotes: LEADERBOARD_EMOTES },
    });

    let syncImportedBody: Record<string, unknown> | null = null;
    await page.route(`**/api/channels/${SOURCE_CHANNEL}/emotes/sync-imported`, (route) => {
      syncImportedBody = route.request().postDataJSON();
      return route.fulfill({ status: 204 });
    });
    await mockChannelScopedResync(page, SOURCE_CHANNEL);
    await mockSevenTvGql(page, () => ({
      data: { emoteSets: { emoteSet: { addEmote: { id: 'lb-1' } } } },
    }));
    // Frozen for the same reason as the other run-completion tests: the engine's trailing pacing
    // delay would otherwise race a real wait.
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const sourceDialog = page.getByRole('dialog');
    await page.locator('main header button').nth(2).click();
    await expect(sourceDialog.locator('#app-dialog-title')).toHaveText('Emotes importieren');
    await sourceDialog.getByRole('button', { name: /^Aus 7TVs Bestenliste/ }).click();
    await expect(sourceDialog.locator('#app-dialog-title')).toHaveText(
      'Aus 7TVs Bestenliste importieren',
    );

    // The default list loads without any sort switch — the third source's whole point (E7):
    // reachable without first knowing which channel the emotes live in.
    await expect(sourceDialog.getByRole('group', { name: 'Emote-Auswahl' })).toBeVisible();
    // #166: the clear-selection button is not there with nothing marked.
    await expect(sourceDialog.getByRole('button', { name: 'Auswahl aufheben' })).toHaveCount(0);

    await sourceDialog.getByRole('button', { name: /^LBOne/ }).click();
    await sourceDialog.getByRole('button', { name: /^LBTwo/ }).click();
    await expect(sourceDialog.getByRole('button', { name: 'Weiter' })).toBeEnabled();

    // #166: clicking it empties the grid's selection and locks "Weiter" again — proof the button
    // does not just clear visually but actually goes through ForeignEmoteGrid.selectionChange, the
    // one thing this step's result() reacts to.
    await sourceDialog.getByRole('button', { name: 'Auswahl aufheben' }).click();
    await expect(sourceDialog.getByRole('button', { name: 'Weiter' })).toBeDisabled();
    await expect(sourceDialog.getByRole('button', { name: 'Auswahl aufheben' })).toHaveCount(0);

    await sourceDialog.getByRole('button', { name: /^LBOne/ }).click();
    await sourceDialog.getByRole('button', { name: /^LBTwo/ }).click();
    await expect(sourceDialog.getByRole('button', { name: 'Weiter' })).toBeEnabled();
    await sourceDialog.getByRole('button', { name: 'Weiter' }).click();

    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      `2 Emotes nach ${SOURCE_CHANNEL} kopieren?`,
    );
    // The origin line names the sort, not a channel — a leaderboard row has none (spec E2/E8).
    await expect(confirm.getByText('Aus 7TVs Bestenliste: 7TV Trend heute')).toBeVisible();
    await expect(confirm.getByText(`Ziel: ${SOURCE_CHANNEL} · Set set-1`)).toBeVisible();
    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(2000);
    await expect(page.getByText('2 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();

    // AK 15/16's wire contract: the fourth ImportOrigin member sends no source channel, only the
    // sort it was picked off of.
    expect(syncImportedBody).not.toBeNull();
    // Cast through `unknown` first: `syncImportedBody` is a `let` reassigned only inside the
    // `page.route` callback above, so TS's control-flow analysis never sees that reachable and
    // narrows the read here to the literal `null` from the initializer — a direct cast to
    // `Record<string, unknown>` therefore looks like a mistake to the compiler (TS2352) even
    // though the `expect(...).not.toBeNull()` above proves it isn't.
    const body = syncImportedBody as unknown as Record<string, unknown>;
    expect(body['sourceKind']).toBe('seventv-leaderboard');
    expect(body['sourceChannelName']).toBeNull();
    expect(body['leaderboardSort']).toBe('TRENDING_DAILY');
    expect((body['sevenTvEmoteIds'] as string[]).slice().sort()).toEqual(['lb-1', 'lb-2']);
  });

  test('a 503 from the endpoint shows the error state and never offers "Weiter"', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // Every failure state of the endpoint maps onto this one 503/errorCode (E13) — the step's error
    // banner does not distinguish them, so a bare 503 is representative of all of them.
    await mockSevenTvLeaderboard(page, { TRENDING_DAILY: 503 });

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const dialog = page.getByRole('dialog');
    await page.locator('main header button').nth(2).click();
    await dialog.getByRole('button', { name: /^Aus 7TVs Bestenliste/ }).click();

    await expect(dialog.getByRole('alert')).toContainText(
      '7TV ist gerade nicht erreichbar. Bitte versuch es in Kürze erneut.',
    );
    // "Weiter" only ever renders once a list is on screen (ImportSourceDialog.gridVisible, shared
    // with the foreign-channel branch) — an error state never mounts the grid, so the button is not
    // merely disabled, it never appears at all. Its absence is the lock.
    await expect(dialog.getByRole('button', { name: 'Weiter' })).toHaveCount(0);
    await expect(dialog.getByRole('group', { name: 'Emote-Auswahl' })).toHaveCount(0);
  });
});

/**
 * #141: the export dialog sorts by purpose, not by format. Its third row, "Emotes später wieder
 * einlesen", is the file destination the target picker used to offer directly ("Als Datei
 * speichern") — moved here rather than removed, and only offered when an active 7TV set makes it
 * fulfillable (E3, `activeEmoteSetId() !== null && importScopeCurrent()`), both of which
 * `mockWorkspace`'s default `activeEmoteSetId` ('set-1') already satisfies. The written file is
 * unchanged from before the move (same envelope, same filename), so the one thing worth pinning
 * down here is that picking this row still produces a real download.
 */
test.describe('export dialog: purpose-sorted options (#141)', () => {
  const exportButton = (page: Page) => page.getByRole('button', { name: 'Ergebnisse exportieren' });

  test('the emote-list row downloads a re-importable file', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await exportButton(page).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText('Export');

    // The two usage purposes are always offered; the row names are asserted as substrings because
    // Playwright computes the accessible name from the whole `<label>`, hint line included.
    await expect(dialog.getByRole('radio', { name: /Zahlen auswerten/ })).toBeVisible();
    await expect(dialog.getByRole('radio', { name: /Zahlen weiterverarbeiten/ })).toBeVisible();

    // The row this test exists for: the file destination's new home. Same reasoning applies to the
    // accessible name — it also carries the hint line ("Emote-Liste als JSON").
    const emoteListRow = dialog.getByRole('radio', { name: /Emotes später wieder einlesen/ });
    await expect(emoteListRow).toBeVisible();

    const downloadPromise = page.waitForEvent('download');
    await emoteListRow.check();
    await dialog.getByRole('button', { name: 'Exportieren' }).click();
    const download = await downloadPromise;

    // Same filename scheme `startImportFromChoice`'s file branch used before #141 moved this here
    // (`emoteListFilename`): `emotepurge_<channel>_emote-list_<yyyy-mm-dd>.json`. The date is not
    // pinned to keep this test stable across midnight runs.
    expect(download.suggestedFilename()).toMatch(
      /^emotepurge_sensitron_emote-list_\d{4}-\d{2}-\d{2}\.json$/,
    );
  });
});

/**
 * #94: the #80 fix above made the dock shortcut's own count/lock follow `selectedItems()`, but left
 * the underlying `selectedKeySet` — and therefore the dock's own "N markiert" line — holding a key
 * for a row that no longer exists. This block covers the actual fix: a silent, `preserveSelection`d
 * reload now prunes `selectedKeySet` itself (`ListSelection.retainAmong`, against the reload's raw,
 * unfiltered payload) and surfaces what it dropped as a `role="status"` notice next to the emote
 * count — placed there, not in the dock, because the dock unmounts outright once nothing remains
 * selected, which is exactly the case the second test below pins down. Reuses this file's own
 * `mockWorkspace`/`gotoUsageStats`/`cell`/`dockCopyButton` helpers and the same silent-reload
 * mechanism as the #80 test (re-registering `/usage-stats/totals` before `emitLive`), since both
 * exercise the identical `loadTotals(..., { preserveSelection: true, silent: true })` path.
 */
test.describe('silent reload: selection reconciliation feedback (#94)', () => {
  // `role="status"` is not in the "name from content" category of the ARIA accessible-name spec —
  // Chromium's accessibility tree gives this element an EMPTY computed name despite its visible
  // text, so `getByRole('status', { name })` can never match it (confirmed against this exact
  // element: a bare `getByRole('status')` query lists it with its full text, but the same query
  // with any `name` — regex, substring, or the exact string — returns zero). `usage-atlas.e2e.spec.ts`
  // already establishes the fix for this shape: match the role, then `.filter({ hasText })` on the
  // DOM text content instead of the computed name. Matches both the singular and plural translation
  // without depending on which one fired — the exact wording is covered by the unit tests
  // (usage-stats-page.spec.ts), this only needs to identify the notice among the page's other
  // role="status" elements (the emote-count line never contains this phrase).
  const prunedNotice = (page: Page) =>
    page.getByRole('status').filter({ hasText: 'aus der Auswahl entfernt' });

  // The notice is now two elements (usage-stats-page.html, #94 follow-up P2): a permanent sr-only
  // `role="status"` region — the one `prunedNotice` above finds — whose text comes and goes, plus a
  // sibling `aria-hidden="true"` span that carries the same text for sighted users and no role of
  // its own (aria-hidden removes it from the tree, so it is never what `prunedNotice` matches).
  // Tailwind's `sr-only` still gives the region a 1×1px box, which is enough for Playwright's
  // default `toBeVisible()` bounding-box check to pass despite the region being invisible — so a
  // test asserting the notice is genuinely ON SCREEN (not just present in the a11y tree) has to
  // target this visible span instead.
  const visiblePrunedNotice = (page: Page) =>
    page.locator('[aria-hidden="true"]').filter({ hasText: 'aus der Auswahl entfernt' });

  test('a silent reload that drops one of several marked rows shows the notice and lowers the dock count by one', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // All three rows marked, none left as an unmarked witness — the point here is the count
    // dropping by exactly one, not which row keeps standing (that's the other test below).
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await cell(page, 'Pog').click({ modifiers: ['Shift'] });
    await expect(dockCopyButton(page, 3)).toBeEnabled();
    await expect(prunedNotice(page)).toHaveCount(0);

    // Re-registering the same route wins over mockWorkspace's earlier one (see the #80 test above):
    // the next totals fetch answers as if KEKW had just been removed from outside this tab.
    await mockUsageTotals(page, SOURCE_CHANNEL, [SOURCE_EMOTES[0], SOURCE_EMOTES[2]]);
    await emitLive(page, { type: 'usage.flushed', channel: SOURCE_CHANNEL });
    await page.clock.runFor(1_500);

    await expect(visiblePrunedNotice(page)).toBeVisible();
    // The dock count follows the now-pruned selectedKeySet, not just importShortcutSelectionCount:
    // three markiert rows become two, and no stale "(3)" button lingers behind it.
    await expect(dockCopyButton(page, 2)).toBeEnabled();
    await expect(dockCopyButton(page, 3)).toHaveCount(0);
  });

  /**
   * The case the notice's placement exists for: mark exactly one row, have the silent reload
   * remove precisely that one, and the dock — bound to a now-empty selection — unmounts entirely.
   * Proven via two independent controls the dock carried (the shortcut button and, since the
   * source channel's broadcaster can manage it, the vote-session button) rather than one, so this
   * is evidence the whole dock is gone and not just that one button's label stopped matching.
   */
  test('a silent reload that removes the only marked row still shows the notice after the dock unmounts', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await expect(dockCopyButton(page, 1)).toBeEnabled();
    await expect(page.getByRole('button', { name: /^Zur Abstimmung stellen/ })).toBeVisible();

    // CatJAM — the only marked row — is gone from the next totals answer; KEKW and Pog remain.
    await mockUsageTotals(page, SOURCE_CHANNEL, [SOURCE_EMOTES[1], SOURCE_EMOTES[2]]);
    await emitLive(page, { type: 'usage.flushed', channel: SOURCE_CHANNEL });
    await page.clock.runFor(1_500);

    // The dock is gone outright — both of its controls, not merely relocked at zero.
    await expect(page.getByRole('button', { name: /^Übertragen \(\d+\)$/ })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Zur Abstimmung stellen/ })).toHaveCount(0);
    // ...yet the reconciliation notice survives it, because it was never inside the dock.
    await expect(visiblePrunedNotice(page)).toBeVisible();
  });

  test('a silent reload that loses nothing selected shows no notice', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await expect(dockCopyButton(page, 1)).toBeEnabled();

    // Same three rows come back unchanged — a flush with nothing archived in between.
    await mockUsageTotals(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await emitLive(page, { type: 'usage.flushed', channel: SOURCE_CHANNEL });
    await page.clock.runFor(1_500);

    await expect(dockCopyButton(page, 1)).toBeEnabled();
    await expect(prunedNotice(page)).toHaveCount(0);
  });
});

test.describe('push flow: the file path', () => {
  // Both an emote-list and a usage export lead into the same confirmation dialog, uploaded through
  // the file-import dialog opened from the header trigger (not the picker's "save as file" option)
  // — the dialog dispatches on the envelope's `kind` (FileImportStep.onFileSelected) and, on
  // success, closes and hands the result to the trigger. The target here is always the CURRENT
  // channel: ImportTrigger.openDialog always imports into the `channelName` it was opened with.
  test('an emote-list file and a usage-export file both reach the confirm dialog', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockSetWarning(page, SOURCE_CHANNEL);
    // Fixed target picture for the whole test: two emotes already present under these exact ids —
    // the first file's rows collide with both, the third file's rows are deliberately fresh ones.
    await mockEmoteList(page, SOURCE_CHANNEL, [
      { sevenTvEmoteId: '7tv-existing-1', name: 'ExistingA' },
      { sevenTvEmoteId: '7tv-existing-2', name: 'ExistingB' },
    ]);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // File 1: an emote-list export from THIS channel, both rows already in the target — both
    // sameChannelFile and nothingToAdd apply, and the execute button is locked.
    let fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_emote-list_2026-09-01.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'emote-list',
          formatVersion: 1,
          exportedAt: '2026-09-01T09:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: { sourceEmoteSetId: 'set-1', rowCount: 2, scope: 'visible' },
          rows: [
            { sevenTvEmoteId: '7tv-existing-1', name: 'ExistingA' },
            { sevenTvEmoteId: '7tv-existing-2', name: 'ExistingB' },
          ],
        }),
        'utf-8',
      ),
    });
    let dialog = await waitForImportConfirmDialog(page);
    await expect(dialog.getByText('Diese Liste stammt aus diesem Kanal.')).toBeVisible();
    await expect(dialog.getByText('Alle 2 Emotes sind bereits im Zielset.')).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Kopieren' })).toBeDisabled();
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // File 2: a usage export with no `exportedAt` at all — the date reads as unknown rather than
    // crashing or silently defaulting to "now".
    fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_usage_2026-08-01_2026-08-30.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'usage',
          formatVersion: 1,
          channelName: 'sensitron',
          withheld: [],
          meta: {
            from: '2026-08-01',
            to: '2026-08-30',
            rowCount: 1,
            scope: 'visible',
            filtered: false,
          },
          rows: [
            {
              sevenTvEmoteId: '7tv-new-20',
              emoteName: 'FreshEmote',
              totalUseCount: 5,
              previousWindowUseCount: 0,
              lastUsedDate: null,
              firstSeenAt: null,
              trend: 'unknown',
            },
          ],
        }),
        'utf-8',
      ),
    });
    dialog = await waitForImportConfirmDialog(page);
    await expect(dialog.getByText('Export aus sensitron, Datum unbekannt')).toBeVisible();
    await dialog.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // File 3: meta.rowCount claims 5 rows, but the array itself carries 4 — one structurally
    // invalid (no sevenTvEmoteId) and one valid duplicate of another valid row. discardedRows is
    // measured against the claimed count (5 - 3 structurally valid = 2, R6/2.7), independent of
    // duplicatesCollapsed (1, from the dedup that runs after validity filtering) — and the
    // contract (T5) puts the discarded-rows line before the duplicates-collapsed line.
    fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_emote-list_2026-09-02.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'emote-list',
          formatVersion: 1,
          exportedAt: '2026-09-02T09:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: { sourceEmoteSetId: 'set-1', rowCount: 5, scope: 'visible' },
          rows: [
            { sevenTvEmoteId: '7tv-new-10', name: 'NewA' },
            { sevenTvEmoteId: '7tv-new-10', name: 'NewA' },
            { sevenTvEmoteId: '7tv-new-11', name: 'NewB' },
            { name: 'Broken' },
          ],
        }),
        'utf-8',
      ),
    });
    dialog = await waitForImportConfirmDialog(page);
    await expect(dialog.getByText('2 ungültige Zeilen in der Quelle verworfen.')).toBeVisible();
    await expect(dialog.getByText('1 doppelte Zeile in der Quelle zusammengefasst.')).toBeVisible();
    const dialogText = await dialog.innerText();
    expect(dialogText.indexOf('ungültige Zeilen in der Quelle verworfen')).toBeGreaterThan(-1);
    expect(dialogText.indexOf('doppelte Zeile in der Quelle zusammengefasst')).toBeGreaterThan(
      dialogText.indexOf('ungültige Zeilen in der Quelle verworfen'),
    );
    await expect(dialog.getByRole('button', { name: 'Kopieren' })).toBeEnabled();
  });
});

test.describe('import dialog: shell contract', () => {
  test('entering the file branch focuses the file control, not the cancel button', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const fileInput = await openFileImportDialog(page);

    // Design-language §7.3: entering a step puts the caret on that step's first meaningful control.
    // This used to hold by accident — the file dialog opened straight onto this content, so the
    // CDK's `first-tabbable` default landed here — and stopped holding when the same content became
    // step two of one dialog (#147): CDK autofocuses once, when the overlay opens, and never again
    // for a swap inside it. The dialog now arranges it after the step renders. A hidden
    // `<input type="file">` cannot itself receive focus, so the visible button in front of it is the
    // target.
    await expect(page.getByRole('button', { name: 'Datei auswählen' })).toBeFocused();
    // The input stays reachable through that button; asserted here so the two locators are not
    // silently talking about different elements.
    await expect(fileInput).toBeAttached();
  });

  test('the grid shrinks on a short window instead of handing the pane a second scrollbar', async ({
    page,
  }) => {
    // The pane is `overflow-y: auto` by design (§7), so it will happily grow a bar of its own the
    // moment the dialog's content outgrows it — which is exactly the double-scrollbar defect the
    // grid's dvh-based height exists to prevent. jsdom has no layout, so this is the only level the
    // arithmetic can be checked on. 500 px is a zoomed window, not an exotic device.
    await page.setViewportSize({ width: 1280, height: 500 });
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await page.route('**/api/seventv/channels/handofblood/emotes*', (route) =>
      route.fulfill({
        json: {
          channelName: 'handofblood',
          sevenTvUserId: '7tv-user-1',
          emoteSetId: 'set-source',
          totalCount: 60,
          truncated: false,
          emotes: Array.from({ length: 60 }, (_, index) => ({
            sevenTvEmoteId: `foreign-${index}`,
            name: `ForeignEmote${index}`,
            defaultName: `ForeignEmote${index}`,
            imageUrl: `https://cdn.7tv.app/emote/foreign-${index}/2x.webp`,
            topAllTime: null,
            trending: null,
          })),
        },
      }),
    );

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const dialog = page.getByRole('dialog');
    await page.locator('main header button').nth(2).click();
    await dialog.getByRole('button', { name: /^Aus einem Kanal/ }).click();
    await dialog.getByLabel('Kanalname').fill('handofblood');
    await dialog.getByRole('button', { name: 'Set laden' }).click();
    await expect(dialog.getByRole('group', { name: 'Emote-Auswahl' })).toBeVisible();

    // One scroll container, and it is the grid's. Measured on the pane itself rather than by
    // looking for a scrollbar, which is a rendering detail the platform may hide.
    const paneOverflow = await page
      .locator('.cdk-overlay-pane.app-dialog-panel')
      .evaluate((pane) => pane.scrollHeight - pane.clientHeight);
    expect(paneOverflow).toBeLessThanOrEqual(1);

    const gridScrolls = await dialog
      .locator('cdk-virtual-scroll-viewport')
      .evaluate((viewport) => viewport.scrollHeight > viewport.clientHeight);
    expect(gridScrolls).toBe(true);
  });

  test('entering the channel branch focuses the channel field', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const dialog = page.getByRole('dialog');
    await page.locator('main header button').nth(2).click();
    await dialog.getByRole('button', { name: /^Aus einem Kanal/ }).click();

    // The other half of the same contract, and here it is more than reachability: the step exists
    // to be typed into, so it can be typed into at once.
    await expect(dialog.getByLabel('Kanalname')).toBeFocused();
  });

  test('lists the three acceptable file sorts before the file control', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await openFileImportDialog(page);

    // §7.3's body-order contract (plan §1.1: heading, the three file-sort list items, THEN the file
    // control): the first list entry's own text must precede the file control's label in the
    // rendered DOM order, same pattern as the discardedRows/duplicatesCollapsed ordering check above
    // (`:492-496`).
    const dialogText = await page.getByRole('dialog').innerText();
    const sortsIndex = dialogText.indexOf('Purge-Protokoll (Wiederherstellen) als JSON');
    const controlIndex = dialogText.indexOf('Datei auswählen');
    expect(sortsIndex).toBeGreaterThan(-1);
    expect(controlIndex).toBeGreaterThan(sortsIndex);
  });
});

/**
 * The import grid marks animated emotes and plays exactly one of them — the cell the pointer rests
 * on. The acceptance criterion is about requests, not pixels ("scrolling without dwelling triggers no
 * animated-variant request"), so the CDN is intercepted and every url it is asked for is recorded.
 * Nothing reaches cdn.7tv.app itself.
 *
 * What a request looks like at this cell size (64 px, DPR 1): an animated emote's still is fetched as
 * `2x_static.webp` and its animation as `2x.webp`; a still emote's own picture is `2x.webp` too (the
 * loader rewrites the stored `4x.webp` down). So "no animation request" is asserted per animated id,
 * and "a still triggers nothing" as no further request for that id.
 */
test.describe('import dialog: animated emotes in the grid', () => {
  const PNG_1X1 = Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
    'base64',
  );

  // Every other emote animated, and enough rows that scrolling recycles row views well beyond the
  // viewport's buffer — the situation a per-cell animated sprite would have fired requests in.
  const FOREIGN_EMOTES = Array.from({ length: 400 }, (_, index) => {
    const animated = index % 2 === 0;
    const id = `${animated ? 'anim' : 'still'}-${index}`;
    const name = `${animated ? 'Anim' : 'Still'}${index}`;
    return {
      sevenTvEmoteId: id,
      name,
      defaultName: name,
      imageUrl: `https://cdn.7tv.app/emote/${id}/${animated ? '4x_static' : '4x'}.webp`,
      topAllTime: null,
      trending: null,
    };
  });

  async function openGrid(page: Page) {
    const cdnRequests: string[] = [];
    await page.route('https://cdn.7tv.app/**', (route) => {
      cdnRequests.push(route.request().url());
      return route.fulfill({ status: 200, contentType: 'image/png', body: PNG_1X1 });
    });

    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await page.route('**/api/seventv/channels/handofblood/emotes*', (route) =>
      route.fulfill({
        json: {
          channelName: 'handofblood',
          sevenTvUserId: '7tv-user-1',
          emoteSetId: 'set-source',
          totalCount: FOREIGN_EMOTES.length,
          truncated: false,
          emotes: FOREIGN_EMOTES,
        },
      }),
    );
    // The dwell is a setTimeout. install() alone still lets time flow in real time, which is fine for
    // loading and for single hovers; the scroll phases pause it.
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const dialog = page.getByRole('dialog');
    await page.locator('main header button').nth(2).click();
    await dialog.getByRole('button', { name: /^Aus einem Kanal/ }).click();
    await dialog.getByLabel('Kanalname').fill('handofblood');
    await dialog.getByRole('button', { name: 'Set laden' }).click();
    const grid = dialog.getByRole('group', { name: 'Emote-Auswahl' });
    await expect(grid).toBeVisible();

    const viewport = dialog.locator('cdk-virtual-scroll-viewport');
    return {
      dialog,
      grid,
      viewport,
      cdnRequests,
      animationRequests: () =>
        cdnRequests.filter((url) => /\/emote\/anim-\d+\/2x\.webp$/.test(url)),
      requestsFor: (id: string) => cdnRequests.filter((url) => url.includes(`/emote/${id}/`)),
      // Stops real time, so nothing but runFor can complete a dwell from here on.
      pauseClock: async () => {
        const now = await page.evaluate(() => Date.now());
        await page.clock.pauseAt(now + 1000);
      },
      // Sets scrollTop and advances the paused clock frame by frame until the viewport has rendered
      // for the new offset: the CDK renders on animation frames, which a paused clock holds back.
      // Bounded under the 200 ms dwell, so a hover the scroll caused can never complete one here.
      scrollTo: async (top: number, rendered: () => Promise<boolean>) => {
        await viewport.evaluate((element, value) => {
          element.scrollTop = value;
        }, top);
        await expect(viewport).toHaveJSProperty('scrollTop', top);
        let elapsed = 0;
        while (!(await rendered())) {
          if (elapsed + 16 > 150) {
            throw new Error(
              `scrollTop ${top} not rendered after ${elapsed} ms of fake time; a dwell could complete`,
            );
          }
          await page.clock.runFor(16);
          elapsed += 16;
        }
      },
      // Moves the pointer straight onto an animated cell lying fully inside the viewport. Not hover():
      // that scrolls a clipped cell into view, and the scroll would end the hover it just started.
      restOnVisibleAnimatedCell: async (except: string) => {
        const area = (await viewport.boundingBox())!;
        const cells = await grid
          .getByRole('button', { name: /^Anim\d+, animiert$/ })
          .evaluateAll((elements) =>
            elements.map((element) => {
              const rect = element.getBoundingClientRect();
              return { label: element.getAttribute('aria-label') ?? '', rect: rect.toJSON() };
            }),
          );
        const pick = cells.find(
          ({ label, rect }) =>
            !label.startsWith(`${except},`) &&
            rect.top >= area.y &&
            rect.bottom <= area.y + area.height,
        );
        expect(pick).toBeDefined();
        await page.mouse.move(
          pick!.rect.x + pick!.rect.width / 2,
          pick!.rect.y + pick!.rect.height / 2,
        );
        return `anim-${/^Anim(\d+),/.exec(pick!.label)![1]}`;
      },
      renderedAnimatedIndices: () =>
        grid
          .getByRole('button', { name: /^Anim\d+, animiert$/ })
          .evaluateAll((cells) =>
            cells.map((cell) =>
              Number(/^Anim(\d+),/.exec(cell.getAttribute('aria-label') ?? '')![1]),
            ),
          ),
    };
  }

  test('marks animated cells, and fetches an animation only for the cell the pointer rests on', async ({
    page,
  }) => {
    const {
      dialog,
      grid,
      viewport,
      cdnRequests,
      animationRequests,
      requestsFor,
      pauseClock,
      scrollTo,
      renderedAnimatedIndices,
      restOnVisibleAnimatedCell,
    } = await openGrid(page);

    // (a) The corner marker is aria-hidden; what marks the cell for everyone is its name.
    const animatedCell = grid.getByRole('button', { name: 'Anim0, animiert', exact: true });
    const stillCell = grid.getByRole('button', { name: 'Still1', exact: true });
    await expect(animatedCell).toBeVisible();
    await expect(stillCell).toBeVisible();
    await expect.poll(() => requestsFor('still-1').length).toBeGreaterThan(0);
    await expect.poll(() => requestsFor('anim-0').length).toBeGreaterThan(0);
    // Long past the dwell, with no cell hovered. A request fired here would show up in the flush for
    // (c) below.
    await page.clock.runFor(1000);

    // (d) A still has nothing to play: resting on it costs no request.
    const stillRequestsBefore = requestsFor('still-1').length;
    await stillCell.hover();
    await page.clock.runFor(1000);

    // (c) Resting on an animated cell fetches exactly its animation. Also the flush for (d): a stray
    // request from the still hover would have arrived by the time this one does.
    await animatedCell.hover();
    await page.clock.runFor(300);
    await expect.poll(animationRequests).toEqual(['https://cdn.7tv.app/emote/anim-0/2x.webp']);
    expect(requestsFor('still-1')).toHaveLength(stillRequestsBefore);

    // (b) Scroll with the pointer resting over the grid, in the gutter between its first two columns:
    // no cell passes under it, so every cell the scroll renders is one nobody pointed at.
    await dialog.locator('#app-dialog-title').hover();
    await page.clock.runFor(50);
    await pauseClock();
    const initiallyRendered = Math.max(...(await renderedAnimatedIndices()));
    const box = await viewport.boundingBox();
    expect(box).not.toBeNull();
    const [firstColumn, secondColumn] = await grid
      .getByRole('button')
      .evaluateAll((cells) =>
        cells.slice(0, 2).map((each) => each.getBoundingClientRect().toJSON()),
      );
    expect(secondColumn.top).toBe(firstColumn.top);
    expect(secondColumn.left - firstColumn.right).toBeGreaterThanOrEqual(2);
    await page.mouse.move((firstColumn.right + secondColumn.left) / 2, box!.y + box!.height / 2);
    const maxScroll = await viewport.evaluate(
      (element) => element.scrollHeight - element.clientHeight,
    );
    let firstRendered = Math.min(...(await renderedAnimatedIndices()));
    let previousTop = 0;
    for (let step = 1; step <= 4; step++) {
      const top = Math.min(step * 320, maxScroll);
      if (top === previousTop) {
        break;
      }
      const before = firstRendered;
      await scrollTo(top, async () => {
        const indices = await renderedAnimatedIndices();
        if (indices.length > 0) {
          firstRendered = Math.min(...indices);
        }
        return firstRendered > before;
      });
      previousTop = top;
    }
    // The scroll really rendered cells past what was on screen at the start.
    expect(firstRendered).toBeGreaterThan(initiallyRendered);
    await dialog.locator('#app-dialog-title').hover();
    // Long past any dwell: a timer left behind by a cell the scroll went past would fire now.
    await page.clock.runFor(2000);

    // Their stills were fetched, so the rendered cells were real.
    await expect
      .poll(() =>
        cdnRequests.some((url) => {
          const match = /\/emote\/anim-(\d+)\/\dx_static\.webp$/.exec(url);
          return match !== null && Number(match[1]) >= firstRendered;
        }),
      )
      .toBe(true);

    // Flush for (b), same as above: rest on a cell that is now on screen and wait for its animation.
    // Had the scroll fired any, they would be in the list alongside it.
    const laterId = await restOnVisibleAnimatedCell('Anim0');
    await page.clock.runFor(300);
    await expect
      .poll(animationRequests)
      .toEqual([
        'https://cdn.7tv.app/emote/anim-0/2x.webp',
        `https://cdn.7tv.app/emote/${laterId}/2x.webp`,
      ]);
  });

  // A cell recycled under a resting pointer fires no mouseleave. If the pointer then ends up over a
  // gap, nothing else replaces the hover either — the cell must not play once it renders again.
  // During a real wheel scroll the browser defers its hover update, so the viewport recycles the cell
  // first. A paused clock would reverse that order, so each scroll here renders its new range in the
  // same task, through Angular's dev-mode globals, before the browser can update hover.
  test('a cell scrolled away under a resting pointer does not play when it renders again', async ({
    page,
  }) => {
    const { grid, viewport, animationRequests, pauseClock, restOnVisibleAnimatedCell } =
      await openGrid(page);
    const scrollAndRender = (top: number) =>
      viewport.evaluate((element, value) => {
        const ng = (
          window as unknown as {
            ng: { getComponent<T>(host: Element): T; applyChanges(component: unknown): void };
          }
        ).ng;
        element.scrollTop = value;
        ng.getComponent<{ checkViewportSize(): void }>(element).checkViewportSize();
        ng.applyChanges(ng.getComponent(element.closest('app-foreign-emote-grid')!));
      }, top);
    const target = grid.getByRole('button', { name: 'Anim0, animiert', exact: true });
    await expect(target).toBeVisible();
    await pauseClock();

    // Rest the pointer 2 px above the bottom edge of the cell. Scrolling by whole rows plus 3 px then
    // leaves it in the gap below a cell, whichever row that is.
    const cell = (await target.boundingBox())!;
    const pitch = await grid
      .getByRole('button')
      .evaluateAll(
        (cells, top) =>
          Math.min(
            ...cells
              .map((each) => each.getBoundingClientRect().top)
              .filter((each) => each > top + 1),
          ) - top,
        cell.y,
      );
    expect(pitch - cell.height).toBeGreaterThanOrEqual(2);
    await page.mouse.move(cell.x + cell.width / 2, cell.y + cell.height - 2);
    await page.clock.runFor(50);

    await scrollAndRender(10 * pitch + 3);
    await expect(target).toHaveCount(0);
    await page.clock.runFor(50);
    await scrollAndRender(3);
    await expect(target).toHaveCount(1);
    // Long past the dwell, the pointer still resting in the gap.
    await page.clock.runFor(1000);

    // Flush: a stray request for Anim0 would have arrived by the time the one for this cell does.
    const flushId = await restOnVisibleAnimatedCell('Anim0');
    await page.clock.runFor(300);
    await expect.poll(animationRequests).toEqual([`https://cdn.7tv.app/emote/${flushId}/2x.webp`]);
  });
});

test.describe('push flow: rejection', () => {
  test('a voting export and a foreign purge protocol are both refused with the existing errors', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const fileInput = await openFileImportDialog(page);
    const dialog = page.getByRole('dialog');

    // A voting export: no import path exists for it at all — parseImportSource's votingExport
    // branch runs before the emote-list/usage dispatch even applies. Unlike the success path
    // (`push flow: the file path`), a rejection keeps the file-import dialog OPEN with the error as
    // a banner inside it — there is exactly one dialog throughout, never zero.
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_voting_2026-08-01.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'voting',
          formatVersion: 1,
          exportedAt: '2026-08-01T10:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: {},
          rows: [],
        }),
        'utf-8',
      ),
    });
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(dialog.getByRole('alert')).toContainText(
      'Das ist ein Export einer Abstimmung, kein Purge-Protokoll.',
    );

    // Regression guard for the restore branch (unchanged by #72): a purge protocol from THIS
    // channel but a DIFFERENT (now inactive) emote set is rejected as wrongSet, not silently routed
    // through the new import path. Still the SAME dialog — a second failure does not need (and does
    // not get) a fresh open.
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_purge_202608011200.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'purge-run',
          formatVersion: 1,
          exportedAt: '2026-08-01T12:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: {
            emoteSetId: 'a-long-gone-set',
            startedAt: '2026-08-01T12:00:00Z',
            finishedAt: '2026-08-01T12:05:00Z',
            counts: { requested: 1, succeeded: 1, failed: 0, cancelled: 0 },
          },
          rows: [
            {
              emoteId: 'i1',
              sevenTvEmoteId: '7tv-1',
              name: 'PogU',
              status: 'done',
              errorMessage: null,
            },
          ],
        }),
        'utf-8',
      ),
    });
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(dialog.getByRole('alert')).toContainText(
      'Das Protokoll gehört zu einem anderen Emote-Set — der Channel hat das aktive Set gewechselt.',
    );
  });
});

test.describe('running import: channel switch', () => {
  // R11/R9: the dock is a root-provided-service view, not page state, so it must survive a
  // navigation to a different channel's usage-stats page, and it must keep naming the run's target
  // channel there rather than reading as "a run of THIS page". Also exercises the "open target
  // channel" link (the only in-app, same-route channel-to-channel navigation anywhere in the app)
  // end to end: a full page reload would lose the root-provided SevenTvImportService's state
  // entirely, so the dock surviving on the new URL is itself proof this was an Angular soft
  // navigation, not a reload.
  //
  // What this does NOT establish (see the final report): whether the leave guard's `leadsToSameRoute`
  // exemption (usage-stats-leave.guard.ts) is reachable while `isRunning()` is still true. The one
  // link that goes from one channel's usage-stats page straight to another's
  // (`app-import-progress-section`'s "Zielkanal öffnen") only renders once the run has settled
  // (`run-progress-panel.ts`: the run-actions slot is gated on `!isRunning()`), so by the time it is
  // clickable the guard's own first check (`if (!importService.isRunning()) return of(true)`)
  // already lets the navigation through unconditionally — the same-route comparison never runs for
  // this particular click. No other UI affordance in the app links two channels' usage-stats pages
  // directly (the overview page does, but only via a different route in between), so that ONE
  // exemption has no reachable trigger through the browser and stays unit-test-only. The branch
  // that asks does have one and is covered below ('leaving the page').
  test('a settled run keeps its target line and its progress on the target channel page', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockWorkspace(page, TARGET_CHANNEL, [], 'target-set');
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    // R14: seeds the write token via addInitScript and routes 7tv.io's GQL endpoint — must be
    // registered, like installLiveStub, before the first goto.
    await mockSevenTvGql(page, () => ({
      data: { emoteSets: { emoteSet: { addEmote: { id: '7tv-1' } } } },
    }));
    // Frozen from the start: the run engine paces every row with a trailing RUN_DELAY_MS timer
    // (seven-tv-run-engine.ts), and a real wait would race it. runFor() below drives it explicitly.
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await copyButton(page).click();

    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: '#aatrociity' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();

    dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText(
      '1 Emote nach aatrociity kopieren?',
    );
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Lets the one queued row's HTTP call resolve and its trailing pacing delay elapse, which is
    // what the engine's `finish()` waits on even for a single-row run.
    await page.clock.runFor(1000);

    await expect(page.getByText('Ziel: aatrociity')).toBeVisible();
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();

    const openTargetLink = page.getByRole('link', { name: 'Zielkanal öffnen' });
    await expect(openTargetLink).toBeVisible();
    await openTargetLink.click();

    await page.waitForURL(`**/channels/${TARGET_CHANNEL}/usage-stats`);
    // No confirm dialog interrupted the navigation — the guard's own isRunning() check already
    // lets a settled run's navigation straight through (see the test-level comment above).
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();

    // Still there on the new page, with the same target line — proof the dock reads off the
    // root-provided service rather than off this page's own channel (R9), and proof this really was
    // an in-app navigation rather than a reload (a reload would have reset the service to no run at
    // all, and none of the following would render).
    await expect(page.getByText('Ziel: aatrociity')).toBeVisible();
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
  });

  /**
   * The same link, used as the only reachable trigger for a channel switch *inside* the usage-stats
   * route — and therefore the only way to reach the window this test is about.
   *
   * `channelName()` follows the URL at once while the set status and the totals keep describing the
   * previous channel until their own responses land. The copy button used to stay live throughout:
   * it hangs on `atlasOrder().length` and the run arbiter, neither of which notices a channel
   * switch. A click in that window captured channel B's name together with channel A's rows and A's
   * `sourceEmoteSetId` — the confirm dialog said "origin: b", the saved file was named after b, and
   * a run copied A's emotes into a third set under B's name. That is a wrong 7TV write, not a
   * display glitch.
   *
   * Both halves are asserted separately, because they resolve independently and a fix that only
   * waited for the set status would still pass the first: after the status lands the button must
   * STILL be locked, since the grid underneath is the previous channel's until the totals answer.
   * (Which is also why `isLoading()` alone is not the condition — in the other response order it is
   * already false while the set id is still the old one.)
   */
  test('the copy button stays locked until BOTH the set status and the rows are the new channel’s', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // The target needs rows of its own: without them the button would end up disabled on
    // `atlasOrder().length === 0` and the final assertion could not tell the fix from an empty grid.
    await mockWorkspace(page, TARGET_CHANNEL, TARGET_EMOTES, 'target-set');
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    await mockSevenTvGql(page, () => ({
      data: { emoteSets: { emoteSet: { addEmote: { id: '7tv-1' } } } },
    }));
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // A minimal run, only to reach the settled state that renders the "open target channel" link.
    // Runs BEFORE the two routes below are deferred: the confirm dialog's own `loadImportTarget`
    // resolves the very same target-channel endpoints (`getSetStatus` → `emotes/active-set`) to
    // decide when "Kopieren" may be clicked, and deferring them any earlier would starve the
    // dialog itself, not just the post-switch page load this test is actually about.
    await cell(page, 'CatJAM').click();
    await copyButton(page).click();
    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: '#aatrociity' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();
    dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await page.clock.runFor(1000);

    // On the source page, with everything current, the button is live — the baseline the two
    // assertions below are a change from. The CatJAM row is still selected too (nothing about
    // finishing an import run clears the selection, unlike a delete — see onDeleted vs.
    // startImportFromChoice), so the dock shortcut is live as well: importShortcutLocked shares
    // importScopeCurrent with the header button, and this is the one other trigger built on it.
    await expect(copyButton(page)).toBeEnabled();
    await expect(dockCopyButton(page, 1)).toBeEnabled();

    // Registered only now, so the two routes above answer normally for the confirm dialog's own
    // load and are held only for the page navigation triggered below (see deferRoute).
    const releaseTargetStatus = await deferRoute(
      page,
      `**/api/channels/${TARGET_CHANNEL}/emotes/active-set`,
    );
    const releaseTargetTotals = await deferRoute(
      page,
      `**/api/channels/${TARGET_CHANNEL}/usage-stats/totals**`,
    );

    await page.getByRole('link', { name: 'Zielkanal öffnen' }).click();
    await page.waitForURL(`**/channels/${TARGET_CHANNEL}/usage-stats`);

    // Still mounted, because activeEmoteSetId() is the SOURCE channel's set — which is precisely
    // the state that must not be copyable. The dock shortcut shares the same lock (importScopeCurrent)
    // and must therefore be just as disabled, not only the header's own trigger.
    await expect(copyButton(page)).toBeVisible();
    await expect(copyButton(page)).toBeDisabled();
    await expect(dockCopyButton(page, 1)).toBeDisabled();

    // The totals request is only issued once the set status has resolved the "all time" range, so
    // waiting for it is exact proof that the set status half has landed and the rows half has not.
    const totalsRequested = page.waitForRequest(
      `**/api/channels/${TARGET_CHANNEL}/usage-stats/totals**`,
    );
    releaseTargetStatus();
    await totalsRequested;
    await expect(copyButton(page)).toBeDisabled();
    await expect(dockCopyButton(page, 1)).toBeDisabled();

    // Only the header button is checked for "live again" below: the totals load that follows
    // clears the selection (loadTotals without preserveSelection — see the effect above), so the
    // dock shortcut goes on to lock for its OTHER reason, an empty selection, which is a separate,
    // already-covered concern (importShortcutDisabled) rather than a second data point on the
    // channel-switch lock this test is about.
    releaseTargetTotals();
    await expect(cell(page, 'Sadge')).toBeVisible();
    await expect(copyButton(page)).toBeEnabled();
  });
});

test.describe('running import: a token without write rights', () => {
  /**
   * The `abortOn` detour (`abortsForMissingPrivileges`) end to end. Two things are asserted that no
   * unit test can see together: that the run really stops after the FIRST row rather than burning
   * the whole selection against the same refusal, and — the part that slips through most easily —
   * that no follow-up call goes out afterwards. `onRunComplete` returns early on an empty
   * `doneKeys`, so neither the audit report (`sync-imported`) nor the target resync may fire; both
   * are routed here rather than left unmocked, so a stray call is counted instead of dying in the
   * dev proxy and being mistaken for "nothing happened".
   */
  test('stops after the first row, cancels the rest and reports nothing back', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    // Empty target set: both selected rows survive the already-present filter, so the run has a
    // second row that the abort has to cancel.
    await mockEmoteList(page, TARGET_CHANNEL, []);

    const followUps: string[] = [];
    await page.route(`**/api/channels/${TARGET_CHANNEL}/emotes/sync-imported`, (route) => {
      followUps.push('sync-imported');
      return route.fulfill({ status: 204 });
    });
    await page.route(`**/api/channels/${TARGET_CHANNEL}/resync`, (route) => {
      followUps.push('resync');
      return route.fulfill({ status: 202 });
    });

    // 7TV v4's real shape for a token that may not write the set: a GQL error inside a 200, with
    // `extensions.code` carrying the structured reason `abortsForMissingPrivileges` actually reads —
    // not a message substring.
    //
    // #149 P1: the fresh pre-run duplicate check (`already-present-filter.ts`) now reads 7TV
    // directly too, over this same endpoint, right before the run starts — so this handler must
    // tell that read apart from the real `addEmote` attempt it exists to count. A read the token
    // *can* make even without write rights (reading a set is public, no token at all is even sent —
    // see that file's doc), so it gets a clean empty-set answer here rather than the same
    // privilege error, keeping `mutationCount` exactly what its name says: attempts at the actual
    // mutation, not at the read in front of it.
    let mutationCount = 0;
    await mockSevenTvGql(page, (request) => {
      if (!request.query.includes('addEmote')) {
        return {
          data: { emoteSets: { emoteSet: { emotes: { totalCount: 0, pageCount: 1, items: [] } } } },
        };
      }
      mutationCount += 1;
      return {
        errors: [
          {
            message: 'LACKING_PRIVILEGES you are not an editor for this user',
            extensions: { code: 'LACKING_PRIVILEGES', status: 403 },
          },
        ],
      };
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: '#aatrociity' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();

    dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText(
      '2 Emotes nach aatrociity kopieren?',
    );
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Generous: the first row's failure, the abort detour and the engine's own settling all sit
    // behind the frozen pacing timers, and a rate-limit retry would too (it is not one here).
    await page.clock.runFor(5000);

    const section = page.locator('app-import-progress-section');
    await expect(section.getByText('0 kopiert · 1 fehlgeschlagen · 1 abgebrochen')).toBeVisible();
    await expect(
      section.getByText(
        'Das 7TV-Token hat im Zielset kein Schreibrecht — der Lauf wurde nach der ersten Zeile abgebrochen.',
      ),
    ).toBeVisible();

    // One attempt, not two: the second row never reached 7TV's rate-limit bucket.
    expect(mutationCount).toBe(1);
    expect(followUps).toEqual([]);
  });
});

test.describe('running import: leaving the page', () => {
  /**
   * The half of R11 that asks (`usageStatsLeaveGuard`). Its exemption for a pure channel switch has
   * no reachable trigger in the browser (see the channel-switch test above), but the branch that
   * *asks* has one: any navigation to a different KIND of page while the run is still going — here
   * the workspace's own "Votings" tab, which is a different route definition and therefore not the
   * same-route reuse the guard exempts. Cancelling stays put, confirming leaves; the run is
   * untouched either way, the guard only asks.
   */
  test('a navigation to another page asks first, and only a confirmation leaves', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
      { channelName: TARGET_CHANNEL, isSevenTvEditor: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);
    await mockVoteSessionList(page, SOURCE_CHANNEL, []);

    await mockSevenTvGql(page, () => ({
      data: { emoteSets: { emoteSet: { addEmote: { id: '7tv-1' } } } },
    }));
    // Frozen and never advanced in this test: the engine's trailing pacing delay is what keeps the
    // run in flight, so `isRunning()` stays true for as long as the clock does not move — which is
    // exactly the state the guard is written for.
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: '#aatrociity' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();

    dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // The run is live: the panel offers to cancel it, which it only does while running.
    const section = page.locator('app-import-progress-section');
    await expect(section.getByRole('button', { name: 'Abbrechen' })).toBeVisible();

    await page.getByRole('link', { name: 'Votings' }).click();

    const leavePrompt = page.getByRole('dialog');
    await expect(leavePrompt).toContainText('Der Kopierlauf läuft noch.');
    await leavePrompt.getByRole('button', { name: 'Abbrechen' }).click();

    // Declining keeps the page AND the run — the guard never touches the service.
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(new URL(page.url()).pathname).toBe(`/channels/${SOURCE_CHANNEL}/usage-stats`);
    await expect(section.getByRole('button', { name: 'Abbrechen' })).toBeVisible();

    await page.getByRole('link', { name: 'Votings' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Verlassen' }).click();

    await page.waitForURL(`**/channels/${SOURCE_CHANNEL}/vote-sessions`);
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});

/**
 * #132: the create-vote-session dialog used to freeze `emoteIds` at open time
 * (`openCreateVoteSession()`), so a silent reload that pruned a marked emote WHILE the dialog was
 * open went unnoticed there — the dialog kept offering the stale list, and the backend's
 * all-or-nothing check (`VoteSessionService.CreateAsync`) rejected the submit with
 * `emote_ids_invalid`. Same silent-reload mechanism as the #94 block above (re-registering
 * `/usage-stats/totals` before `emitLive`), the only difference being that here the reload has to
 * land while the create dialog — not the grid itself — is what is on screen.
 */
test.describe('create-vote-session dialog: follows the live selection (#132)', () => {
  test('a silent reload that removes a selected emote while the dialog is open updates the ballot, shows a notice, and excludes the id from the POST body', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await page.clock.install();

    let createRequestBody: { emoteIds?: string[] } | null = null;
    await page.route(`**/api/channels/${SOURCE_CHANNEL}/vote-sessions`, async (route) => {
      if (route.request().method() !== 'POST') {
        return route.fallback();
      }
      createRequestBody = route.request().postDataJSON();
      return route.fulfill({
        json: {
          id: 99,
          title: 'Test session',
          allowedVoterRoles: 1,
          isActive: true,
          startedAt: '2026-09-11T00:00:00Z',
          endedAt: null,
          emoteCount: 2,
          hideResultsUntilEnd: false,
        },
      });
    });

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // Marks all three rows.
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await cell(page, 'Pog').click({ modifiers: ['Shift'] });
    await page.getByRole('button', { name: /^Zur Abstimmung stellen/ }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText(
      'Abstimmung aus Auswahl erstellen',
    );
    await expect(dialog.getByText('3 Emotes ausgewählt')).toBeVisible();

    // KEKW gets archived on 7TV from outside this tab while the dialog is still open — the same
    // silent usage.flushed reload the #94 block above drives, only now with the dialog on top.
    await mockUsageTotals(page, SOURCE_CHANNEL, [SOURCE_EMOTES[0], SOURCE_EMOTES[2]]);
    await emitLive(page, { type: 'usage.flushed', channel: SOURCE_CHANNEL });
    await page.clock.runFor(1_500);

    // The dialog's own header count follows the live selection...
    await expect(dialog.getByText('2 Emotes ausgewählt')).toBeVisible();
    // ...and the in-dialog notice says so. Scoped to the visible banner, not a bare text match: the
    // permanently mounted sr-only role="status" twin (create-vote-session-dialog.ts, #132) carries
    // the identical text and would otherwise make this a strict-mode violation — same shape as the
    // #94 notice's own `visiblePrunedNotice` helper above.
    await expect(dialog.locator('app-notice-banner').getByText(/nicht mehr im Set/)).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Abstimmung erstellen' })).toBeEnabled();

    await dialog.locator('#create-vote-session-title-input').fill('Test session');
    await dialog.getByRole('button', { name: 'Abstimmung erstellen' }).click();

    await expect(page.getByRole('dialog')).toHaveCount(0);
    // CatJAM (e1) and Pog (e3) — KEKW (e2), pruned by the live reload, never went out.
    expect(createRequestBody).not.toBeNull();
    // Same cast-through-`unknown` reasoning as the leaderboard body above: `createRequestBody` is
    // only ever reassigned inside the `page.route` callback, so TS still sees it as the literal
    // `null` initializer here and flags a direct cast as TS2352.
    expect((createRequestBody as unknown as { emoteIds?: string[] }).emoteIds?.sort()).toEqual([
      'e1',
      'e3',
    ]);
  });
});
