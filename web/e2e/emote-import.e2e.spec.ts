import { readFileSync } from 'node:fs';

import { Locator, Page, Request, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  MockLeaderboardEmote,
  SevenTvGqlRequest,
  SevenTvGqlRequestKind,
  emitLive,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelEmoteSetList,
  mockChannelPermissions,
  mockChannelScopedResync,
  mockChannelStatus,
  mockEmoteList,
  mockEmoteSetTargets,
  mockForeignChannelEmoteSets,
  mockForeignEmoteSetPreview,
  mockMyChannels,
  mockSetWarning,
  mockSevenTvGql,
  mockSevenTvLeaderboard,
  mockSyncDeletedInSet,
  mockSyncImported,
  mockSyncImportedToSet,
  mockSyncRestoredInSet,
  mockUsageTotals,
  mockVoteSessionList,
  mockWorkerHealth,
  sevenTvGqlRequestKind,
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
  await mockActiveEmoteSet(page, channelName, activeEmoteSetId, {
    capacity: 1000,
    occupiedSlots: 3,
  });
  await mockUsageTotals(page, channelName, emotes);
}

/**
 * The target picker's account list (spec 6.2) for the common two-channel push scenario: the own
 * account (SOURCE_CHANNEL, its own active set `set-1` — matches `mockWorkspace`'s default, so
 * "das ist the Quelle" has the right id to disable) plus one editor-granted tracked account
 * (TARGET_CHANNEL, its active set the one these tests copy into). Replaces the picker's former
 * `GET /api/channels/mine` data source (AK 34) — most tests below only need this shape; the picker
 * UI's own describe block builds a richer one (untracked accounts, several sets) inline instead.
 */
async function mockTargetPicker(page: Page, targetEmoteSetId = 'target-set'): Promise<void> {
  await mockEmoteSetTargets(page, [
    {
      twitchChannelId: 'source-1',
      twitchLogin: SOURCE_CHANNEL,
      isOwnAccount: true,
      trackedChannelName: SOURCE_CHANNEL,
      activeEmoteSetId: 'set-1',
      sets: [{ id: 'set-1', name: 'Hauptset', isActive: true }],
    },
    {
      twitchChannelId: 'target-1',
      twitchLogin: TARGET_CHANNEL,
      trackedChannelName: TARGET_CHANNEL,
      activeEmoteSetId: targetEmoteSetId,
      sets: [{ id: targetEmoteSetId, name: 'Main', isActive: true }],
    },
  ]);
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
    // K2's picker (spec 6.2/8.6) reads accounts/sets, not Twitch roles: the own account (source,
    // its own active set 'set-1' is what "das ist die Quelle" then disables), a second tracked
    // account editable through a 7TV grant, and an untracked one — no moderator-only channel here
    // at all, since 6.2 has no Twitch-mod concept to filter on; it simply never appears.
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [{ id: 'set-1', name: 'Hauptset', isActive: true }],
      },
      {
        twitchChannelId: 'target-1',
        twitchLogin: TARGET_CHANNEL,
        trackedChannelName: TARGET_CHANNEL,
        activeEmoteSetId: 'target-set',
        sets: [{ id: 'target-set', name: 'Main', isActive: true }],
      },
      {
        twitchChannelId: 'untracked-1',
        twitchLogin: 'untrackedbuddy',
        sets: [{ id: 'set-untracked', name: 'Wegwerf-Set', ownerDisplayName: 'UntrackedBuddy' }],
      },
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

    // All three: CatJAM (already present at the target) and KEKW (name collision) both leave
    // toAdd under the current preview contract (8.6: collisions and already-present rows are both
    // excluded, not just flagged) — Pog is the one row with nothing to collide with, so it is what
    // makes the confirm dialog's own row count meaningful instead of a degenerate zero.
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await cell(page, 'Pog').click({ modifiers: ['Shift'] });
    await expect(copyButton(page)).toBeEnabled();
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await expect(picker.locator('#app-dialog-title')).toHaveText('Emotes übertragen');

    // Scope defaults to the selection (R12), not to the visible list.
    await expect(picker.getByRole('radio', { name: 'Auswahl (3)' })).toBeChecked();

    // The target account's active set gets its own radio, same as every other set (addendum
    // 39, #217 — there is no separate account-header radio anymore): enabled, since it is a
    // normal, non-source set.
    await expect(picker.getByRole('radio', { name: 'Main (aktiv)' })).toBeEnabled();
    // The own channel stays in the list since K2 (spec 8.6, a deliberate change from the old
    // picker) — only its own set is disabled, labelled as the run's source, never the account.
    await expect(
      picker.getByRole('radio', { name: /^Hauptset \(aktiv\) \(das ist die Quelle\)$/ }),
    ).toBeDisabled();
    // An untracked account is offered too, labelled as such — not excluded and not a hard "must
    // join first" refusal (T2.6 is exactly the class that makes such a set selectable again).
    await expect(picker.getByText('nicht getrackt')).toBeVisible();
    await expect(picker.getByRole('radio', { name: 'Wegwerf-Set' })).toBeEnabled();

    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '1 Emote nach aatrociity kopieren?',
    );
    await expect(confirm.getByText('Aus Kanal sensitron')).toBeVisible();
    // The picked set is the target account's active one ('target-set'), so the load resolves via
    // the 'trackedActive' path — which never fetches a set name of its own. Third Codex round P2:
    // the picker's own choice already carries the name ('Main') from the very click that picked
    // it, and the confirm header now shows that name instead of falling back to the raw set id.
    await expect(confirm.getByText('Ziel: aatrociity · Set Main')).toBeVisible();
    await expect(
      confirm.getByText('1 Emote ist bereits im Zielset und wird übersprungen.'),
    ).toBeVisible();
    await expect(
      confirm.getByText(
        '1 Emote trägt einen Namen, der im Zielset schon vergeben ist — wird nicht übertragen:',
      ),
    ).toBeVisible();
    await expect(confirm.locator('app-name-preview-list')).toContainText('KEKW');
    // occupied 3 + the one row with nothing to collide with (Pog) — CatJAM (already present) and
    // KEKW (name collision) both leave toAdd under the current preview contract (8.6) = 4 of 1000.
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
    await mockTargetPicker(page);
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

    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
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
 * T2.6/spec 8.6: the two K2 target-picker paths the earlier "picker to confirmation dialog" block
 * does not already cover — a *non-active* set of a still-tracked channel (AK 43), and an untracked
 * account's set, which needs its own confirmation before the picker may close with it at all
 * (AK 35) and reports through the set-centric endpoint instead of the channel-scoped one (AK 41).
 */
test.describe('push flow: K2 target-set picker (T2.6)', () => {
  test('same channel, different set: the run writes into the chosen set, not the active one (AK 43)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [{ id: 'set-1', name: 'Hauptset', isActive: true }],
      },
      {
        twitchChannelId: 'target-1',
        twitchLogin: TARGET_CHANNEL,
        trackedChannelName: TARGET_CHANNEL,
        activeEmoteSetId: 'target-set',
        // Two sets of the SAME tracked account: the active one (would resolve via the
        // unchanged 'trackedActive' path, AK 36) and a second, non-active one — picking the
        // second is what this test is about.
        sets: [
          { id: 'target-set', name: 'Main', isActive: true },
          { id: 'target-set-halloween', name: 'Halloween' },
        ],
      },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // The chosen (non-active) set's own live read (spec 6.4/F5) — never EmoteSetStatus/listEmotes,
    // which only ever describe the channel's *active* set.
    await mockForeignEmoteSetPreview(page, TARGET_CHANNEL, {
      channelName: TARGET_CHANNEL,
      sevenTvUserId: '7tv-user-t',
      emoteSetId: 'target-set-halloween',
      emoteSetName: 'Halloween',
      capacity: 500,
      totalCount: 0,
      emotes: [],
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockSyncImported(page, TARGET_CHANNEL);
    // No mockChannelScopedResync here (finding 3, Live-Verifikation K2 2026-09-21): a non-active
    // target must never trigger it at all — the test below asserts that directly via its own route.

    let capturedSetId: unknown;
    await mockSevenTvGql(page, (request) => {
      capturedSetId = request.variables['setId'];
      return {
        data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
      };
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await expect(picker.locator('#app-dialog-title')).toHaveText('Emotes übertragen');
    // Halloween is not the account's active set — its radio just is not labelled "(aktiv)"
    // (addendum 39, #217: every set is its own radio regardless) — no confirmation banner
    // either, that class is untracked-only (T2.6, see the test below).
    await picker.getByRole('radio', { name: 'Halloween' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = page.getByRole('dialog');
    // Finding 1 (Live-Verifikation K2 2026-09-21): the title names the SET, not "nach aatrociity" —
    // that wording would claim the channel's active set, which this run does not write to.
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      "1 Emote in Set ‚Halloween' kopieren?",
    );
    // The confirm header names the CHOSEN set, never the account's active one (AK 39/F5).
    await expect(confirm.getByText('Ziel: aatrociity · Set Halloween')).toBeVisible();

    let resyncCalled = false;
    await page.route(`**/api/channels/${TARGET_CHANNEL}/resync`, async (route) => {
      resyncCalled = true;
      await route.fulfill({ status: 202 });
    });

    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(1000);
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();

    // AK 43: the GQL mutation's own setId is the chosen set, never 'target-set' (the active one).
    expect(capturedSetId).toBe('target-set-halloween');

    // Finding 2: the dock's own "Ziel: …" line now names the set too, mirroring the confirm
    // dialog's own line above.
    await expect(page.getByText('Ziel: aatrociity · Set Halloween')).toBeVisible();
    // Finding 3: a non-active target never resyncs the channel and never offers to open it — the
    // channel page shows its own active set, never this one.
    await expect(page.getByRole('link', { name: 'Zielkanal öffnen' })).toHaveCount(0);
    // The same text exists twice by design (§4.5): the visible, aria-hidden span in the dock, and
    // DockOutcomeAnnouncer's own spoken paragraph — target the visible one specifically, same
    // pattern as usage-stats-page.e2e.spec.ts's visiblePrunedNotice.
    await expect(
      page.locator('[aria-hidden="true"]').filter({
        hasText:
          "In Set ‚Halloween' kopiert — es ist nicht das aktive Set von aatrociity, die Kanalseite zeigt es deshalb nicht.",
      }),
    ).toBeVisible();
    expect(resyncCalled).toBe(false);
  });

  test('untracked target: the picker asks for confirmation, then reports through the set-centric endpoint (AK 35/41)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [{ id: 'set-1', name: 'Hauptset', isActive: true }],
      },
      {
        twitchChannelId: 'untracked-1',
        twitchLogin: 'stranger',
        // trackedChannelName omitted — null, the picker's untracked class (spec 6.2/8.6).
        sets: [{ id: 'set-untracked', name: 'Wegwerf-Set', ownerDisplayName: 'Stranger' }],
      },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // Keyed on the account's Twitch login ('stranger'), never its display name (E7) — the loader's
    // 'untrackedSet' case routes through twitchLogin for exactly this URL.
    await mockForeignEmoteSetPreview(page, 'stranger', {
      channelName: 'stranger',
      sevenTvUserId: null,
      emoteSetId: 'set-untracked',
      emoteSetName: 'Wegwerf-Set',
      capacity: 250,
      totalCount: 0,
      emotes: [],
    });

    let reportRequestBody: unknown;
    let reportRequestMethod: string | undefined;
    await page.route('**/api/seventv/emote-sets/set-untracked/sync-imported', async (route) => {
      reportRequestMethod = route.request().method();
      reportRequestBody = route.request().postDataJSON();
      await route.fulfill({ status: 204 });
    });

    await mockSevenTvGql(page, () => ({
      data: { emoteSets: { emoteSet: { addEmote: { id: '7tv-1' } } } },
    }));
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await expect(picker.getByText('nicht getrackt')).toBeVisible();

    // AK 35: choosing the untracked set does not select it outright — a confirmation names the
    // set and its owner first, and "Weiter" cannot be used to skip past it. Finding 7
    // (Live-Verifikation K2 2026-09-21): worded as a target confirmation, not a second, differently
    // labelled "copy" action next to the picker's own disabled "Weiter".
    await picker.getByRole('radio', { name: 'Wegwerf-Set' }).check();
    await expect(
      picker.getByText(
        "Ziel ist das Set ‚Wegwerf-Set' von ‚Stranger' — dieses Konto trackt EmotePurge nicht.",
      ),
    ).toBeVisible();
    await expect(picker.getByRole('button', { name: 'Weiter' })).toBeDisabled();

    // Confirming closes the whole picker directly (AK 35: "Bestätigung schließt den Picker mit
    // channelName: null") — there is no separate "Weiter" click for this class.
    await picker.getByRole('button', { name: 'Ja, dieses Set' }).click();

    const confirm = page.getByRole('dialog');
    // Finding 1: the title names the SET, not "nach Stranger" — an untracked target is never the
    // channel's active set (there is no channel at all).
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      "1 Emote in Set ‚Wegwerf-Set' kopieren?",
    );
    await expect(confirm.getByText('Ziel: Set Wegwerf-Set von Stranger')).toBeVisible();
    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(1000);
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    // T2.6/8.6: the dock's own summary line has no channel to name either, and offers no "open
    // target channel" link — there is no channel page behind an untracked target. Finding 2: named
    // by its resolved set NAME now, not the raw id.
    await expect(page.getByText('Ziel: Set Wegwerf-Set von Stranger')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Zielkanal öffnen' })).toHaveCount(0);
    // Finding 3: an untracked target must not claim the channel shows anything either — it already
    // showed nothing before this fix, still true after.
    await expect(page.getByText(/Abgleich/)).toHaveCount(0);

    // AK 41: the set-centric endpoint, POSTed without a targetEmoteSetId (the route already names
    // the set) — never the channel-scoped .../emotes/sync-imported.
    expect(reportRequestMethod).toBe('POST');
    expect(reportRequestBody).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      sourceChannelName: SOURCE_CHANNEL,
      sourceKind: 'channel',
      leaderboardSort: null,
    });
  });

  /**
   * The target picker's own layout follow-up (addendum 39, #217): every account renders as a plain
   * heading with its sets as ordinary radios below it — no merged "channel is the radio" shortcut
   * for a single-set or an active-set account, and PERSONAL sets are hidden outright rather than
   * shown disabled. Mirrors the source-picker's own shape test above (K3, spec addendum
   * 2026-09-21) for the target side.
   */
  test('the target picker gives every account the same heading-plus-radios layout and hides PERSONAL entirely (addendum 39, #217)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [{ id: 'set-1', name: 'Hauptset', isActive: true }],
      },
      {
        // A single-set tracked account — the old picker rendered this account's name itself as the
        // radio ("#brudivoeller_tv (aktiv: …)"); addendum 39 gives it the same heading-plus-radio
        // shape as every other account instead.
        twitchChannelId: 'target-1',
        twitchLogin: TARGET_CHANNEL,
        trackedChannelName: TARGET_CHANNEL,
        activeEmoteSetId: 'target-set',
        sets: [
          { id: 'target-set', name: 'Main', isActive: true },
          // PERSONAL alongside a real set on the same account — hidden entirely, the NORMAL
          // sibling stays.
          { id: 'target-personal', name: 'Persönlich', kind: 'PERSONAL', isPersonal: true },
        ],
      },
      {
        // Every set on this account is PERSONAL — filtered down to zero, distinct from a read
        // failure (setsUnavailable), so it gets the "no usable set" notice, not "Sets nicht
        // lesbar".
        twitchChannelId: 'personal-only-1',
        twitchLogin: 'personalonly',
        trackedChannelName: 'personalonly',
        activeEmoteSetId: 'personal-set',
        sets: [{ id: 'personal-set', name: 'Nur ich', kind: 'PERSONAL', isPersonal: true }],
      },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    const radiogroup = picker.getByRole('radiogroup', { name: 'Ziel' });

    // Every account is a plain heading (never itself a radio) plus its sets below it, source and
    // target account alike.
    await expect(picker.getByText('#sensitron', { exact: false })).toBeVisible();
    await expect(picker.getByText(`#${TARGET_CHANNEL}`, { exact: false })).toBeVisible();
    await expect(picker.getByRole('radio', { name: `#${TARGET_CHANNEL}` })).toHaveCount(0);
    await expect(picker.getByRole('radio', { name: /^#sensitron/ })).toHaveCount(0);

    // The target account's active set is its own radio, checked state aside — same shape a
    // single-set account gets as a multi-set one.
    await expect(radiogroup.getByRole('radio', { name: 'Main (aktiv)' })).toBeEnabled();

    // PERSONAL is absent everywhere, never merely disabled or labelled.
    await expect(radiogroup.getByText('Persönlich')).toHaveCount(0);
    await expect(radiogroup.getByText('Nur ich')).toHaveCount(0);

    // The account left with nothing but a PERSONAL set still renders (its heading stays) with a
    // distinct notice — not the "Sets nicht lesbar" wording a read failure gets.
    await expect(picker.getByText('personalonly', { exact: false })).toBeVisible();
    await expect(picker.getByText('Kein nutzbares Set')).toBeVisible();
    await expect(picker.getByText('Sets nicht lesbar')).toHaveCount(0);
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
    await mockForeignChannelEmoteSets(page, 'handofblood', {
      activeEmoteSetId: 'set-source',
      sets: [{ id: 'set-source', name: 'Hauptset' }],
    });
    await page.route('**/api/seventv/channels/handofblood/emotes*', (route) =>
      route.fulfill({
        json: {
          channelName: 'handofblood',
          sevenTvUserId: null,
          emoteSetId: 'set-source',
          emoteSetName: 'Hauptset',
          capacity: 1000,
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

  /**
   * K3 review finding P2-1: the always-visible source-set radiogroup (spec addendum 2026-09-21)
   * eats into the grid's fixed 26rem allowance, and a four-row radiogroup (HandOfBlood's own shape)
   * ate through the whole ~4rem slack on any but the tallest windows — the exact double-scrollbar
   * defect the height expression exists to prevent, just triggered by the step's own chrome instead
   * of the window. `reservedRem` folds the radiogroup's measured height into the allowance (see
   * `foreign-emote-grid.ts`'s class doc), so the pane must hold at every one of these heights with
   * four sets, the same way the single-set case above holds at 500 px.
   */
  test('a multi-set radiogroup does not grow a second scrollbar, at several window heights (K3 review, P2-1)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockForeignChannelEmoteSets(page, 'handofblood', {
      activeEmoteSetId: 'set-1',
      sets: [
        { id: 'set-1', name: 'Set eins' },
        { id: 'set-2', name: 'Set zwei' },
        { id: 'set-3', name: 'Set drei' },
        { id: 'set-4', name: 'Set vier' },
      ],
    });
    await page.route('**/api/seventv/channels/handofblood/emotes*', (route) =>
      route.fulfill({
        json: {
          channelName: 'handofblood',
          sevenTvUserId: null,
          emoteSetId: 'set-1',
          emoteSetName: 'Set eins',
          capacity: 1000,
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
    await expect(
      dialog.getByRole('radiogroup', { name: 'Quell-Set' }).getByRole('radio'),
    ).toHaveCount(4);
    await expect(dialog.getByRole('group', { name: 'Emote-Auswahl' })).toBeVisible();

    // dvh follows the window size on its own (pure CSS) — no reload or re-interaction needed
    // between resizes, only a re-measurement of the pane.
    for (const height of [700, 800, 960]) {
      await page.setViewportSize({ width: 1280, height });
      const paneOverflow = await page
        .locator('.cdk-overlay-pane.app-dialog-panel')
        .evaluate((pane) => pane.scrollHeight - pane.clientHeight);
      expect(paneOverflow, `pane overflow at ${height}px with 4 sets`).toBeLessThanOrEqual(1);
    }
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

  /**
   * K3's source-set picker (spec 8.7, AK 47–49): a radiogroup of the foreign channel's sets, the
   * active one preselected, a non-`NORMAL` set offered but disabled and labelled, switching the
   * selection re-fetches the preview for the newly picked set — and, the specific regression AK 48
   * calls out, keeping the active set selected the whole time never issues a second preview request.
   */
  test('the source-set picker preselects the active set, disables a non-NORMAL one, hides PERSONAL entirely, and switches the preview on pick (K3, AK 47-49, spec addendum 2026-09-21)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockForeignChannelEmoteSets(page, 'handofblood', {
      activeEmoteSetId: 'set-active',
      sets: [
        { id: 'set-active', name: 'Hauptset' },
        { id: 'set-alt', name: 'Zweitset' },
        // GLOBAL keeps 8.6's original treatment: visible, disabled, labelled.
        { id: 'set-global', name: 'Globales Set', kind: 'GLOBAL' },
        // PERSONAL is hidden from this picker entirely since the spec addendum — never rendered,
        // not even disabled (asserted below by its absence, not by a disabled/labelled state).
        { id: 'set-personal', name: 'Persönlich', kind: 'PERSONAL', isPersonal: true },
      ],
    });
    const previewRequests: string[] = [];
    await page.route('**/api/seventv/channels/handofblood/emotes*', (route) => {
      const url = new URL(route.request().url());
      const emoteSetId = url.searchParams.get('emoteSetId')!;
      previewRequests.push(emoteSetId);
      const emotes =
        emoteSetId === 'set-active'
          ? [{ sevenTvEmoteId: '7tv-active', name: 'ActiveEmote' }]
          : [{ sevenTvEmoteId: '7tv-alt', name: 'AltEmote' }];
      return route.fulfill({
        json: {
          channelName: 'handofblood',
          sevenTvUserId: null,
          emoteSetId,
          emoteSetName: emoteSetId === 'set-active' ? 'Hauptset' : 'Zweitset',
          capacity: 1000,
          totalCount: emotes.length,
          truncated: false,
          emotes: emotes.map((emote) => ({
            ...emote,
            defaultName: emote.name,
            imageUrl: `https://cdn.7tv.app/emote/${emote.sevenTvEmoteId}/2x.webp`,
            topAllTime: null,
            trending: null,
          })),
        },
      });
    });

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const dialog = page.getByRole('dialog');
    await page.locator('main header button').nth(2).click();
    await dialog.getByRole('button', { name: /^Aus einem Kanal/ }).click();
    await dialog.getByLabel('Kanalname').fill('handofblood');
    await dialog.getByRole('button', { name: 'Set laden' }).click();

    const radiogroup = dialog.getByRole('radiogroup', { name: 'Quell-Set' });
    await expect(radiogroup.getByRole('radio', { name: /^Hauptset \(aktiv\)$/ })).toBeChecked();
    await expect(radiogroup.getByRole('radio', { name: 'Zweitset' })).toBeEnabled();
    await expect(
      radiogroup.getByRole('radio', { name: /^Globales Set \(kein Quellset\)$/ }),
    ).toBeDisabled();
    // PERSONAL is absent, not merely disabled — no radio, no name anywhere in the radiogroup.
    await expect(radiogroup.getByRole('radio')).toHaveCount(3);
    await expect(radiogroup.getByText('Persönlich')).toHaveCount(0);

    // The active set's own preview loaded once, and only once — the "kein zweiter Request" case
    // (AK 48). The initial resolve already fetched by set id, so nothing here ever re-requests it.
    // Cell names live in the tile's accessible name/title (foreign-emote-grid.ts's cellLabel), not
    // as visible text — getByRole('button', ...) is the matching locator, same idiom as the earlier
    // cell()-locator tests in this file.
    await expect(dialog.getByRole('button', { name: 'ActiveEmote' })).toBeVisible();
    expect(previewRequests).toEqual(['set-active']);

    // Switching to the other NORMAL set fires exactly one new request, for that set's own id, and
    // the grid replaces the previous set's content with the new one's (AK 49: the picked set's
    // preview, not the active one's).
    await radiogroup.getByRole('radio', { name: 'Zweitset' }).check();
    await expect(dialog.getByRole('button', { name: 'AltEmote' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'ActiveEmote' })).toHaveCount(0);
    expect(previewRequests).toEqual(['set-active', 'set-alt']);

    // Switching back to the active set fires no third request — its preview was already loaded
    // once and is served from the picker's own cache (K3 follow-up fix: found live, toggling
    // between HandOfBlood's 3 sets a few times hit the shared ForeignEmoteLookup 429 after ~8
    // unconditional switches).
    await radiogroup.getByRole('radio', { name: /^Hauptset \(aktiv\)$/ }).check();
    await expect(dialog.getByRole('button', { name: 'ActiveEmote' })).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'AltEmote' })).toHaveCount(0);
    expect(previewRequests).toEqual(['set-active', 'set-alt']);
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
    await mockForeignChannelEmoteSets(page, 'handofblood', {
      activeEmoteSetId: 'set-source',
      sets: [{ id: 'set-source', name: 'Hauptset' }],
    });
    await page.route('**/api/seventv/channels/handofblood/emotes*', (route) =>
      route.fulfill({
        json: {
          channelName: 'handofblood',
          sevenTvUserId: null,
          emoteSetId: 'set-source',
          emoteSetName: 'Hauptset',
          capacity: 1000,
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
    // K3 review, P2-1: the always-visible source-set radiogroup moved the grid down from where it
    // used to render, and "Set laden"'s own on-screen position — where .click() leaves the cursor —
    // now happens to fall inside a cell's box once the grid mounts under it. Chromium recomputes
    // :hover on layout changes even with no further pointer movement, so that stray leftover
    // position played a real animation before any of this test's own explicit hovers ran. Parking
    // the pointer off the grid entirely closes that gap for good, regardless of where a future
    // reflow happens to leave "Set laden".
    await page.mouse.move(0, 0);

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
  test('a voting export and a purge protocol of a set outside the target list are both refused, each with its own error', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // The caller's target list (spec #253, 4.2): only this channel's own active set — the set the
    // protocol below names is in no account's list, so it is not a set this caller can restore into.
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [{ id: 'set-1', name: 'Hauptset', isActive: true }],
      },
    ]);

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

    // The restore branch (spec #253, AK 3): the file names its own set, and a set the target list
    // does not offer is refused as targetNotEditable by the file step itself — no restore
    // confirmation, no 7TV request, and not silently routed through the import path either. Still
    // the SAME dialog — a second failure does not need (and does not get) a fresh open.
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
      'Das Set aus der Datei ist nicht (mehr) bearbeitbar oder existiert nicht mehr.',
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
    await mockTargetPicker(page);
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
    await dialog.getByRole('radio', { name: 'Main (aktiv)' }).check();
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
    await mockTargetPicker(page);
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
    await dialog.getByRole('radio', { name: 'Main (aktiv)' }).check();
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

    // Not copyable while the target's set status is held. Since the #200 K4 fix round the page no
    // longer derives an active set from the SOURCE channel's status once the URL names the target
    // (`activeEmoteSetId` is guarded by `setStatusChannel`), so there is no set to copy from at all
    // yet: the header's set-gated write paths and the dock's marking half are not rendered, rather
    // than rendered over the previous channel's set id.
    await expect(copyButton(page)).toHaveCount(0);
    await expect(dockCopyButton(page, 1)).toHaveCount(0);

    // The totals request is only issued once the set status has resolved the "all time" range, so
    // waiting for it is exact proof that the set status half has landed and the rows half has not.
    const totalsRequested = page.waitForRequest(
      `**/api/channels/${TARGET_CHANNEL}/usage-stats/totals**`,
    );
    releaseTargetStatus();
    await totalsRequested;
    // The target's set is known now, the rows underneath are still the source's: mounted, but
    // locked — header button and dock shortcut alike (both read importScopeCurrent).
    await expect(copyButton(page)).toBeVisible();
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
    await mockTargetPicker(page);
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
    await dialog.getByRole('radio', { name: 'Main (aktiv)' }).check();
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

/**
 * A rejection with no `extensions.code` fails the row without aborting the run — contrast the
 * "a token without write rights" block above, whose `LACKING_PRIVILEGES` error does abort. With
 * one row selected that settles at done 0 / failed 1 / cancelled 0, which is the case the progress
 * wording has to get right.
 */
test.describe('running import: progress wording matches the outcome (#158)', () => {
  test('a single failed row is reported as processed, never as copied', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, []);

    // A plain mutation rejection with no `extensions.code` — `abortsForMissingPrivileges` reads
    // false for it, so the row fails but the run does not abort. With one row selected there is
    // nothing left to cancel: the queue settles at done 0 / failed 1 / cancelled 0.
    await mockSevenTvGql(page, () => ({
      errors: [{ message: '7TV had an internal error' }],
    }));
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await expect(copyButton(page)).toBeEnabled();
    await copyButton(page).click();

    let dialog = page.getByRole('dialog');
    await dialog.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await dialog.getByRole('button', { name: 'Weiter' }).click();

    dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText(
      '1 Emote nach aatrociity kopieren?',
    );
    await dialog.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    // Generous, same reasoning as the abort test above: the row's failure and the engine settling
    // both sit behind the frozen pacing timer.
    await page.clock.runFor(5000);

    const section = page.locator('app-import-progress-section');
    // Must not read "kopiert" — that would claim a row that never made it into the set.
    await expect(section.getByText('1 / 1 verarbeitet')).toBeVisible();
    await expect(section.getByText('1 / 1 kopiert')).toHaveCount(0);
    await expect(section.getByText('0 kopiert · 1 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
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
    await mockTargetPicker(page);
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
    await dialog.getByRole('radio', { name: 'Main (aktiv)' }).check();
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
 * A route answered only once the test says so — and a promise that tells the test the request has
 * actually arrived, which is what makes "the page is waiting for exactly this answer" observable.
 * Holds the first request that `shouldHold` accepts; every other request on the same pattern, and
 * the held one once released, goes to the handler registered *before* this one (`route.fallback`),
 * so this must be registered after the mock it defers to. Same mechanism as {@link deferRoute}; the
 * arrival signal is the addition.
 */
async function holdRoute(
  page: Page,
  url: string,
  shouldHold: (request: Request) => boolean = () => true,
): Promise<{ arrived: Promise<void>; release: () => void }> {
  let release!: () => void;
  const released = new Promise<void>((resolve) => {
    release = resolve;
  });
  let arrive!: () => void;
  const arrived = new Promise<void>((resolve) => {
    arrive = resolve;
  });
  let holding = false;
  await page.route(url, async (route) => {
    if (holding || !shouldHold(route.request())) {
      await route.fallback();
      return;
    }
    holding = true;
    arrive();
    await released;
    await route.fallback();
  });
  return { arrived, release };
}

/**
 * Whether the page's `beforeunload` listeners cancel an unload right now — read by dispatching a
 * synthetic, cancelable `beforeunload` and checking `defaultPrevented`. The run arbiter's handler
 * (`preventUnload`) calls `preventDefault()`, so this is `true` exactly while the guard is armed.
 * The real prompt is checked once, through `page.close({ runBeforeUnload: true })`, below.
 */
function unloadPrevented(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  });
}

/**
 * #256 (Plan-256 T8): the run arbiter counts a run as busy until its reports have an end state,
 * not only while the run engine is going, and it arms the tab's unload guard for as long as any
 * destructive run is open. Both halves only exist between two answers — the post-run re-read of the
 * target and the reports after it — so the window is held open by leaving exactly those routes
 * unanswered (`holdRoute`), not by a fake clock: no step waits for a timer to fire, and the
 * engine's short pacing delay (one row) simply runs in real time. The last two cases are delete
 * runs — the arbiter's window is not the import's alone.
 *
 * What is not here: the "Nichts gestartet" notice. Its trigger is a confirmation given *before*
 * another run starts behind the modal, which one browser tab cannot reach without constructing it;
 * the unit specs of the start points carry it (Plan-256 T4).
 */
test.describe('running import: the settling window', () => {
  const importTrigger = (page: Page) => page.locator('app-import-trigger').getByRole('button');
  const importDock = (page: Page) => page.locator('app-import-progress-section');
  const deleteDock = (page: Page) => page.locator('app-mass-delete-panel');

  /**
   * One replace row (CatJAM over the target's own CatJAM) whose ADD answer is lost in transport:
   * the engine marks the row `unknown` and finishes, and the run is `settling` while it re-reads the
   * target. That re-read, then the two reports it leads to (`sync-imported` for the add,
   * set-centric `sync-deleted` for the removal), are each held until the test releases them.
   */
  async function startSettlingReplaceRun(page: Page) {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, [{ sevenTvEmoteId: 'target-a', name: 'CatJAM' }]);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockSyncDeletedInSet(page, 'target-set');
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    // Before the run the target still holds its own CatJAM; the re-read after it finds the source
    // under that name — the lost ADD did land, so the row settles green once the re-read answers.
    let addAborted = false;
    await mockSevenTvGql(page, (request) => {
      switch (sevenTvGqlRequestKind(request)) {
        case 'setRead':
          return sevenTvSetReadPayload(
            addAborted
              ? [{ id: '7tv-1', aliases: ['CatJAM'] }]
              : [{ id: 'target-a', aliases: ['CatJAM'] }],
          );
        case 'removeEmote':
          return {
            data: {
              emoteSets: { emoteSet: { removeEmote: { id: request.variables['emoteId'] } } },
            },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    // Registered after mockSevenTvGql, so it sees every call first (reverse registration order):
    // the replace's ADD dies in transport — `unknown`, not `failed` (transportLossIsUnknown).
    await page.route('https://7tv.io/v4/gql', async (route) => {
      const body = route.request().postDataJSON() as SevenTvGqlRequest;
      if (sevenTvGqlRequestKind(body) === 'addEmote') {
        addAborted = true;
        await route.abort();
        return;
      }
      await route.fallback();
    });
    // The first read after the lost ADD is the settle re-read; the dialog's own reads before the
    // run pass straight through.
    const reRead = await holdRoute(
      page,
      'https://7tv.io/v4/gql',
      (request) =>
        addAborted &&
        sevenTvGqlRequestKind(request.postDataJSON() as SevenTvGqlRequest) === 'setRead',
    );
    const importedReport = await holdRoute(
      page,
      `**/api/channels/${TARGET_CHANNEL}/emotes/sync-imported`,
    );
    const removalReport = await holdRoute(
      page,
      '**/api/seventv/emote-sets/target-set/sync-deleted',
    );

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();

    // Baseline: with no run anywhere, both entry points are live and the tab is unguarded — the
    // lock and the guard asserted later are the window's, not the page's.
    await expect(copyButton(page)).toBeEnabled();
    await expect(importTrigger(page)).toBeEnabled();
    expect(await unloadPrevented(page)).toBe(false);

    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = await waitForImportConfirmDialog(page);
    await confirm.getByRole('button', { name: 'Namenskollisionen auflösen' }).click();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für CatJAM' })
      .getByRole('radio', { name: 'Ziel ersetzen' })
      .check();
    await confirm.getByRole('button', { name: 'Übernehmen' }).click();

    const downloadPromise = page.waitForEvent('download');
    await confirm.getByRole('button', { name: 'Rückweg sichern' }).click();
    await downloadPromise;

    await confirm.getByRole('button', { name: 'Starten' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    return { reRead, importedReport, removalReport };
  }

  test('the copy and import triggers stay locked through the re-read and both reports, and unlock once both reports have answered', async ({
    page,
  }) => {
    const { reRead, importedReport, removalReport } = await startSettlingReplaceRun(page);

    // The engine is done (the re-read is only issued from `onRunComplete`), the run is not: no
    // "Abbrechen" any more, "Wird abgeschlossen…" instead of "Schließen", and every start locked.
    await reRead.arrived;
    await expect(importDock(page).getByText('Wird abgeschlossen…')).toBeVisible();
    await expect(importDock(page).getByRole('button', { name: 'Abbrechen' })).toHaveCount(0);
    await expect(copyButton(page)).toBeDisabled();
    await expect(importTrigger(page)).toBeDisabled();

    // The re-read answers; both reports go out and are held — still the same window.
    reRead.release();
    await importedReport.arrived;
    await removalReport.arrived;
    await expect(importDock(page).getByText('Wird abgeschlossen…')).toBeVisible();
    await expect(copyButton(page)).toBeDisabled();
    await expect(importTrigger(page)).toBeDisabled();

    // One report alone does not close the run — checked once its answer has actually landed.
    const importedAnswered = page.waitForResponse(
      `**/api/channels/${TARGET_CHANNEL}/emotes/sync-imported`,
    );
    importedReport.release();
    await importedAnswered;
    await expect(importDock(page).getByText('Wird abgeschlossen…')).toBeVisible();
    await expect(copyButton(page)).toBeDisabled();
    await expect(importTrigger(page)).toBeDisabled();

    removalReport.release();
    await expect(importDock(page).getByRole('button', { name: 'Schließen' })).toBeVisible();
    await expect(
      importDock(page).getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen'),
    ).toBeVisible();
    await expect(copyButton(page)).toBeEnabled();
    await expect(importTrigger(page)).toBeEnabled();
  });

  test('the unload guard holds through the re-read and both reports, asks before the tab closes, and lets go at the end state', async ({
    page,
  }) => {
    const { reRead, importedReport, removalReport } = await startSettlingReplaceRun(page);

    await reRead.arrived;
    await expect.poll(() => unloadPrevented(page)).toBe(true);

    // The browser's own prompt, once: closing the tab with `runBeforeUnload` fires the real
    // `beforeunload`, and Chromium answers the armed guard with its native dialog. Dismissing it
    // keeps the page — and the run — alive. (A real navigation would need fresh user activation per
    // prompt, which is why the synthetic event carries every other check here.)
    const unloadDialog = page.waitForEvent('dialog');
    await page.close({ runBeforeUnload: true });
    const dialog = await unloadDialog;
    expect(dialog.type()).toBe('beforeunload');
    await dialog.dismiss();
    expect(page.isClosed()).toBe(false);

    reRead.release();
    await importedReport.arrived;
    await removalReport.arrived;
    expect(await unloadPrevented(page)).toBe(true);

    const importedAnswered = page.waitForResponse(
      `**/api/channels/${TARGET_CHANNEL}/emotes/sync-imported`,
    );
    importedReport.release();
    await importedAnswered;
    await expect(importDock(page).getByText('Wird abgeschlossen…')).toBeVisible();
    expect(await unloadPrevented(page)).toBe(true);

    removalReport.release();
    await expect(importDock(page).getByRole('button', { name: 'Schließen' })).toBeVisible();
    await expect.poll(() => unloadPrevented(page)).toBe(false);
  });

  /**
   * A delete run on the usage-stats page: every delete row is destructive, so the unload guard
   * holds from the start until `sync-deleted` has an end state (Plan-256 Festlegung Nr. 6) — the
   * dock shows "Wird abgeschlossen…" for exactly that stretch.
   */
  async function startReportingDeleteRun(page: Page, reportAnswer: { status?: number } = {}) {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    // The delete's own target pre-check (`resolveEditableSet`) reads the same account/set list the
    // copy picker does; the source account's `set-1` is editable there.
    await mockTargetPicker(page);
    await mockSetWarning(page, SOURCE_CHANNEL);
    await mockChannelScopedResync(page, SOURCE_CHANNEL);
    await mockSyncDeletedInSet(page, 'set-1', reportAnswer);
    await mockSevenTvGql(page, (request) => {
      switch (sevenTvGqlRequestKind(request)) {
        case 'setRead':
          return sevenTvSetReadPayload([{ id: '7tv-1', aliases: ['CatJAM'] }]);
        case 'removeEmote':
          return {
            data: {
              emoteSets: { emoteSet: { removeEmote: { id: request.variables['emoteId'] } } },
            },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    const deletedReport = await holdRoute(page, '**/api/seventv/emote-sets/set-1/sync-deleted');

    await gotoUsageStats(page, SOURCE_CHANNEL);
    expect(await unloadPrevented(page)).toBe(false);

    await cell(page, 'CatJAM').click();
    await page.getByRole('button', { name: 'Löschen (1)' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Löschen starten' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await deletedReport.arrived;
    return deletedReport;
  }

  test('a delete run guards the tab until its sync-deleted report answers', async ({ page }) => {
    const deletedReport = await startReportingDeleteRun(page);

    await expect(deleteDock(page).getByText('Wird abgeschlossen…')).toBeVisible();
    await expect.poll(() => unloadPrevented(page)).toBe(true);

    deletedReport.release();
    await expect(deleteDock(page).getByRole('button', { name: 'Schließen' })).toBeVisible();
    await expect.poll(() => unloadPrevented(page)).toBe(false);
  });

  /**
   * Plan-256 Festlegung Nr. 13, through a real channel change: a delete run whose report is still
   * out follows the user to the next channel's page, and when that report then fails, the failure
   * stays there with its retry. The #256 T2 review found this broken in a way only a live effect
   * shows: the workspace layout's channel effect also tracked the run record, so the report's own
   * end state re-ran it with the *same* channel and reset the now-closed run — the dock vanished at
   * the very moment it had something to say. The route here leaves the channel workspace entirely
   * (via the overview) and enters the other channel's, so the layout is built anew with that
   * channel, the way a user gets there.
   */
  test('a channel change takes a reporting delete run along, and a failed report stays there with its retry', async ({
    page,
  }) => {
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isTracked: true, isBroadcaster: true },
      { channelName: TARGET_CHANNEL, isTracked: true, isSevenTvEditor: true },
    ]);
    await mockWorkspace(page, TARGET_CHANNEL, TARGET_EMOTES, 'target-set');
    // 403 is not retried (the right to the set is gone): the report fails at once.
    const deletedReport = await startReportingDeleteRun(page, { status: 403 });

    await page.getByRole('link', { name: 'Zurück zu Übersicht' }).click();
    await page.getByRole('link', { name: `#${TARGET_CHANNEL}` }).click();
    await page.waitForURL(`**/channels/${TARGET_CHANNEL}/usage-stats`);
    await expect(cell(page, 'Sadge')).toBeVisible();

    // Still reporting, and still here on the other channel's page.
    await expect(deleteDock(page).getByText('Wird abgeschlossen…')).toBeVisible();

    deletedReport.release();
    await expect(
      deleteDock(page).getByText('Rückmeldung an EmotePurge fehlgeschlagen'),
    ).toBeVisible();
    await expect(deleteDock(page).getByRole('button', { name: 'Erneut melden' })).toBeVisible();
    await expect(deleteDock(page).getByRole('button', { name: 'Schließen' })).toBeVisible();
    await expect.poll(() => unloadPrevented(page)).toBe(false);
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

/**
 * #134 follow-up: the dock mounts and unmounts with its own content (`actionDockHasContent`), and
 * a fully refused run is one of the things that mounts it — so a `role="status"` region living
 * inside the dock can be created in the very same change-detection pass that fills it, which most
 * screen reader/browser pairings do not announce at all (docs/UI-Designsprache.md §4.5). The
 * announcement for these outcomes therefore has to come from a region the page keeps mounted
 * independently of the dock.
 *
 * Proof of "same node": every `role="status"` element present at rest is tagged before the run,
 * and the region that later carries the notice must still carry that tag — a region created
 * together with its text would not.
 */
test.describe('dock outcomes: announced from a region that outlives the dock (#134)', () => {
  const AT_REST = 'data-e2e-at-rest';

  async function tagStatusRegionsAtRest(page: Page): Promise<void> {
    await page.evaluate((attribute) => {
      document.querySelectorAll('[role="status"]').forEach((node) => {
        node.setAttribute(attribute, '');
      });
    }, AT_REST);
  }

  /** The 7TV v4 read `filterAlreadyPresent` sends right before a run starts — answered with the
   *  given ids, as one page. Every other GQL call (the ADD mutation) succeeds. */
  async function mockFreshCheck(page: Page, presentIds: string[]): Promise<void> {
    await mockSevenTvGql(page, (request) => {
      if (request.query.includes('addEmote')) {
        return {
          data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
        };
      }
      return {
        data: {
          emoteSets: {
            emoteSet: {
              emotes: {
                totalCount: presentIds.length,
                pageCount: 1,
                items: presentIds.map((id) => ({ emote: { id } })),
              },
            },
          },
        },
      };
    });
  }

  test('a fully refused import mounts the dock, and its notice lands in a region that existed before', async ({
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
    // Our own mirror knows neither row, so both survive the confirm dialog — only the fresh 7TV
    // check at confirm time finds them, which is exactly the "refused with nothing to run" case.
    await mockEmoteList(page, SOURCE_CHANNEL, []);
    await mockSevenTvLeaderboard(page, {
      TRENDING_DAILY: {
        totalCount: 2,
        truncated: false,
        emotes: [
          { sevenTvEmoteId: 'lb-1', name: 'LBOne', topAllTime: 500_000, trending: 900 },
          { sevenTvEmoteId: 'lb-2', name: 'LBTwo', topAllTime: 300_000, trending: 700 },
        ],
      },
    });
    await mockFreshCheck(page, ['lb-1', 'lb-2']);

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await tagStatusRegionsAtRest(page);

    const sourceDialog = page.getByRole('dialog');
    await page.locator('main header button').nth(2).click();
    await sourceDialog.getByRole('button', { name: /^Aus 7TVs Bestenliste/ }).click();
    await sourceDialog.getByRole('button', { name: /^LBOne/ }).click();
    await sourceDialog.getByRole('button', { name: /^LBTwo/ }).click();
    await sourceDialog.getByRole('button', { name: 'Weiter' }).click();

    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      `2 Emotes nach ${SOURCE_CHANNEL} kopieren?`,
    );
    // Nothing marked, no run: the dock is not there yet (its copy shortcut would be).
    await expect(page.getByRole('button', { name: /^Übertragen \(/ })).toHaveCount(0);
    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const text = '2 Emotes waren beim Start bereits im Zielset und wurden übersprungen.';
    const notice = page.getByRole('status').filter({ hasText: text });
    // Exactly one announcing region — the visible copy is aria-hidden, so it is not a second one.
    await expect(notice).toHaveCount(1);
    await expect(notice).toHaveAttribute(AT_REST, '', { timeout: 1000 });
    await expect(page.locator('[aria-hidden="true"]').filter({ hasText: text })).toBeVisible();
  });

  test('a partly skipped import with the dock already open announces from the same standing region', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, []);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);
    // CatJAM (7tv-1) reached the target between the dialog and the confirm click.
    await mockFreshCheck(page, ['7tv-1']);

    await gotoUsageStats(page, SOURCE_CHANNEL);

    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await expect(dockCopyButton(page, 2)).toBeEnabled();
    // Tagged only now: case (a) is about a dock that is already standing when the notice arrives.
    await tagStatusRegionsAtRest(page);
    await dockCopyButton(page, 2).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();
    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '2 Emotes nach aatrociity kopieren?',
    );
    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const text = '1 Emote war beim Start bereits im Zielset und wurde übersprungen.';
    const notice = page.getByRole('status').filter({ hasText: text });
    await expect(notice).toHaveCount(1);
    await expect(notice).toHaveAttribute(AT_REST, '', { timeout: 1000 });
    await expect(page.locator('[aria-hidden="true"]').filter({ hasText: text })).toBeVisible();
  });
});

/**
 * All four import doors follow the page's SELECTED set, not the channel's active one (#200,
 * T4.5/T4.6, spec 8.6 last point) — this is the same channel's header import button used
 * throughout `push flow: the file path` above, just opened while a non-active set is on screen.
 * Restore is the door K4 did not make set-aware; K5/T5.3 do — a purge-run protocol for the shown
 * non-active set restores into it, with the confirmation naming that set (spec 8.8). Since #253 a
 * restore file names its own target (the set on screen here only because the protocol says so).
 */
test.describe('set view: the import doors follow the selected set (#200, K4/T4.5)', () => {
  const HALLOWEEN_SET_ID = 'set-halloween';

  async function mockNonActiveSetView(page: Page): Promise<void> {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockChannelEmoteSetList(page, SOURCE_CHANNEL, {
      activeEmoteSetId: 'set-1',
      sets: [
        { id: 'set-1', name: 'Hauptset' },
        { id: HALLOWEEN_SET_ID, name: 'Halloween' },
      ],
    });
    await mockForeignEmoteSetPreview(page, SOURCE_CHANNEL, {
      channelName: SOURCE_CHANNEL,
      emoteSetId: HALLOWEEN_SET_ID,
      emoteSetName: 'Halloween',
      capacity: 500,
      totalCount: 0,
      emotes: [],
    });
  }

  async function gotoHalloweenView(page: Page): Promise<void> {
    await page.goto(`/channels/${SOURCE_CHANNEL}/usage-stats?emoteSetId=${HALLOWEEN_SET_ID}`);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    // Proves the non-active set really is what is on screen, not just what the mock intended.
    await expect(page.getByRole('button', { name: /^Set: Halloween/ })).toBeVisible();
  }

  test('an emote-list file import writes into the shown non-active set, not the channel’s active one (AK 66)', async ({
    page,
  }) => {
    await mockNonActiveSetView(page);
    await mockSetWarning(page, SOURCE_CHANNEL);
    let reportedTargetSetId: unknown;
    await page.route(`**/api/channels/${SOURCE_CHANNEL}/emotes/sync-imported`, async (route) => {
      reportedTargetSetId = (route.request().postDataJSON() as { targetEmoteSetId?: string })
        .targetEmoteSetId;
      await route.fulfill({ status: 204 });
    });

    let capturedSetId: unknown;
    await mockSevenTvGql(page, (request) => {
      capturedSetId = request.variables['setId'];
      return {
        data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
      };
    });
    await page.clock.install();

    await gotoHalloweenView(page);

    const fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_emote-list_2026-09-21.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'emote-list',
          formatVersion: 1,
          exportedAt: '2026-09-21T09:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: { sourceEmoteSetId: 'set-1', rowCount: 1, scope: 'visible' },
          rows: [{ sevenTvEmoteId: '7tv-spooky-new', name: 'SpookyNew' }],
        }),
        'utf-8',
      ),
    });

    const confirm = await waitForImportConfirmDialog(page);
    // The title and the "Ziel: …" line both name the SET, exactly like the K2 target-set picker's
    // own non-active run (same `import.confirm.titleSet`/`target` keys) — proof this is not the
    // one-click "into the active set" path.
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      "1 Emote in Set ‚Halloween' kopieren?",
    );
    await expect(confirm.getByText('Ziel: sensitron · Set Halloween')).toBeVisible();

    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(1000);
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    // Both the 7TV write itself (AK 43's own assertion, mirrored here) and the closing bookkeeping
    // report the CHOSEN, non-active set — never `set-1`, the channel's active one.
    expect(capturedSetId).toBe(HALLOWEEN_SET_ID);
    expect(reportedTargetSetId).toBe(HALLOWEEN_SET_ID);
  });

  // K5/T5.3 (spec 8.8) lifted the old refusal this test used to prove (`file-import-step.ts`'s
  // `restoreEnabled` is unconditionally true since K5): a purge-run protocol for the shown
  // non-active set now restores into it, and the confirmation names that set. Since #253 the
  // protocol's own `meta.emoteSetId` is the target whichever set is on screen; the file step
  // checks it against the target list instead of against the page.
  test('a purge-run protocol for the shown non-active set opens the restore confirmation, naming that set, instead of the old refusal (K5/T5.3, AK 66/73)', async ({
    page,
  }) => {
    await mockNonActiveSetView(page);
    // Seeds the write token (R14) so the flow goes straight to the confirmation instead of the
    // token prompt — the run itself is out of scope here (unit-level: restore-flow.spec.ts), this
    // only proves the file is accepted and the dialog names the set.
    await page.addInitScript(() => {
      window.sessionStorage.setItem('ep_7tv_write_token', 'e2e-fake-write-token');
    });
    // #255: the confirmation's own open-time duplicate check now reads the target set's live
    // entries before it ever opens — empty here, so nothing about the row it shows is filtered.
    await mockSevenTvGql(page, () => sevenTvSetReadPayload([]));
    // The file step checks the set the protocol names against the target list (spec #253, 4.2) —
    // here the channel's own, non-active Halloween set, editable.
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [
          { id: 'set-1', name: 'Hauptset', isActive: true },
          { id: HALLOWEEN_SET_ID, name: 'Halloween' },
        ],
      },
    ]);

    await gotoHalloweenView(page);

    const fileInput = await openFileImportDialog(page);
    // The protocol names the very set on screen — proof the confirmation targets the shown set,
    // not the channel's active one (AK 66).
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_purge_202609211200.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'purge-run',
          formatVersion: 1,
          exportedAt: '2026-09-21T12:00:00Z',
          channelName: 'sensitron',
          withheld: [],
          meta: {
            emoteSetId: HALLOWEEN_SET_ID,
            startedAt: '2026-09-21T12:00:00Z',
            finishedAt: '2026-09-21T12:05:00Z',
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

    // The restore confirmation replaces the old refusal — it names the set and flags it as not
    // currently active (spec 8.8, AK 73).
    const confirm = page.getByRole('dialog');
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '1 Emote wieder zum Set hinzufügen?',
    );
    await expect(confirm.getByText('In das Set „Halloween“.')).toBeVisible();
    await expect(confirm.getByText('Dieses Set ist gerade nicht aktiv.')).toBeVisible();
    await expect(confirm.getByText('Wiederherstellen geht vorerst nur im aktiven Set')).toHaveCount(
      0,
    );
    // The negative half of AK 19: the target IS the set on screen, so no foreign-to-view hint.
    await expect(confirm.getByText('Diese Ansicht zeigt von diesem Lauf nichts.')).toHaveCount(0);
  });
});

/** One target entry as 7TV's live set read (`loadSevenTvSetEntries`) would report it — an entry
 *  in `aliases` is `null` for the one aliasless entry an id can carry alongside named ones. */
interface LiveSetEntry {
  id: string;
  aliases: (string | null)[];
  defaultName?: string;
}

/** The GQL_EMOTE_SET_ENTRIES_QUERY response shape `loadSevenTvSetEntries` reads — used for every
 *  'setRead' call a test's `mockSevenTvGql` handler answers. */
function sevenTvSetReadPayload(entries: readonly LiveSetEntry[]): {
  data: {
    emoteSets: {
      emoteSet: {
        emotes: {
          totalCount: number;
          pageCount: number;
          items: { alias: string | null; emote: { id: string; defaultName: string } }[];
        };
      };
    };
  };
} {
  const items = entries.flatMap((entry) =>
    entry.aliases.map((alias) => ({
      alias,
      emote: { id: entry.id, defaultName: entry.defaultName ?? entry.id },
    })),
  );
  return {
    data: {
      emoteSets: { emoteSet: { emotes: { totalCount: items.length, pageCount: 1, items } } },
    },
  };
}

/** One 7TV GQL call, classified and with its variables — what the order/argument assertions
 *  below read off a `mockSevenTvGql` handler's own recording. */
interface GqlCall {
  kind: SevenTvGqlRequestKind;
  variables: Record<string, unknown>;
}

/**
 * #230: per-row conflict resolution inside the import confirm dialog — replace, rename and adopt
 * as three separate decisions on the same run; the "Rückweg sichern" recovery file a removal
 * requires before it may start; a live target that drifted since the dialog's own preview; a lost
 * transport answer settling from a re-read of the target; the untouched-dialog path (AK 5); an
 * untracked target's replace option, offered since #253 (R5); and a restore built from a finished
 * run's own result protocol.
 */
test.describe('push flow: resolving name conflicts (#230)', () => {
  test('resolving one collision by replace, one by rename and one mismatch by adopt runs replace, then adopt, then add, then rename, and reports both the add and the removal', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    // CatJAM and KEKW each collide with a DIFFERENT target id under their own name; Pog's id is
    // already in the target set, just under a different alias — an alias mismatch, not a
    // collision.
    await mockEmoteList(page, TARGET_CHANNEL, [
      { sevenTvEmoteId: 'target-catjam', name: 'CatJAM' },
      { sevenTvEmoteId: 'target-kekw', name: 'KEKW' },
      { sevenTvEmoteId: '7tv-3', name: 'PogOld' },
    ]);

    // `Record<string, unknown>`, not a narrower `{ sevenTvEmoteIds?: string[] }` shape: standard
    // control-flow narrowing, not a compiler quirk — TypeScript does not follow an assignment made
    // inside a callback, so from the narrowing analysis's point of view this `let` is never
    // assigned at all, and a narrower object-literal type here narrows to `never` at any later
    // read (reproduces even for a bare `string | null`). The field is still read through a typed
    // cast below.
    let syncImportedBody: Record<string, unknown> | null = null;
    await page.route(`**/api/channels/${TARGET_CHANNEL}/emotes/sync-imported`, async (route) => {
      syncImportedBody = route.request().postDataJSON();
      await route.fulfill({ status: 204 });
    });
    // The replace's removal report is set-centric (spec 6.5): addressed to the target set.
    const syncDeletedBodies = await mockSyncDeletedInSet(page, 'target-set');
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    const liveTarget: LiveSetEntry[] = [
      { id: 'target-catjam', aliases: ['CatJAM'] },
      { id: 'target-kekw', aliases: ['KEKW'] },
      { id: '7tv-3', aliases: ['PogOld'] },
    ];
    const calls: GqlCall[] = [];
    await mockSevenTvGql(page, (request) => {
      const kind = sevenTvGqlRequestKind(request);
      calls.push({ kind, variables: request.variables });
      switch (kind) {
        case 'setRead':
          return sevenTvSetReadPayload(liveTarget);
        case 'removeEmote':
          return {
            data: {
              emoteSets: { emoteSet: { removeEmote: { id: request.variables['emoteId'] } } },
            },
          };
        case 'addEmote':
          return {
            data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
          };
        case 'updateEmoteAlias':
          return {
            data: {
              emoteSets: { emoteSet: { updateEmoteAlias: { alias: request.variables['alias'] } } },
            },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await cell(page, 'Pog').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = await waitForImportConfirmDialog(page);
    await expect(
      confirm.getByText(
        '2 Emotes tragen einen Namen, der im Zielset schon vergeben ist — werden nicht übertragen:',
      ),
    ).toBeVisible();
    await expect(
      confirm.getByText('1 ist im Zielset bereits vorhanden, heißt dort aber anders:'),
    ).toBeVisible();

    await confirm.getByRole('button', { name: 'Namenskollisionen auflösen' }).click();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für CatJAM' })
      .getByRole('radio', { name: 'Ziel ersetzen' })
      .check();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für KEKW' })
      .getByRole('radio', { name: 'Umbenennen' })
      .check();
    await confirm.getByLabel('Neuer Name').fill('KEKWv2');
    await confirm.getByRole('button', { name: 'Übernehmen' }).click();

    await confirm.getByRole('button', { name: 'Abweichende Namen auflösen' }).click();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für Pog' })
      .getByRole('radio', { name: 'Namen übernehmen' })
      .check();
    await confirm.getByRole('button', { name: 'Übernehmen' }).click();

    // The removal banner and — from the operator decision behind adjustment G row 7 — the neutral
    // rename line, here caused by the ADOPT decision on Pog, not by the RENAME decision on KEKW
    // (deriveTransferRows never turns a renameSource decision into an `adoptSourceName` row).
    await expect(
      confirm.getByText('1 Emote wird aus dem Zielset entfernt und durch das Quell-Emote ersetzt.'),
    ).toBeVisible();
    await expect(confirm.getByText('1 Eintrag im Zielset wird umbenannt.')).toBeVisible();

    const downloadPromise = page.waitForEvent('download');
    await confirm.getByRole('button', { name: 'Rückweg sichern' }).click();
    const download = await downloadPromise;
    const plannedPath = await download.path();
    const planned = JSON.parse(readFileSync(plannedPath!, 'utf-8')) as {
      meta: { stage: string };
      rows: { action: string; removedTarget: { aliases: string[]; confirmed: boolean } | null }[];
    };
    expect(planned.meta.stage).toBe('planned');
    const plannedReplaceRow = planned.rows.find((row) => row.action === 'replace');
    expect(plannedReplaceRow?.removedTarget).toMatchObject({
      aliases: ['CatJAM'],
      confirmed: false,
    });

    await confirm.getByRole('button', { name: 'Starten' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(5000);
    // The adopt (Pog) renames an existing target entry rather than copying one in, so it no longer
    // counts as "kopiert" (spec #255) — the replace and the rename-under-KEKWv2 are the 2 copies.
    await expect(
      page.getByText('2 kopiert · 1 umbenannt · 0 fehlgeschlagen · 0 abgebrochen'),
    ).toBeVisible();

    // buildTransferPlan groups rows replace-then-adopt-then-add-then-rename (never source order,
    // `transfer-plan.ts`'s own doc) — with no untouched `add` row here the mutations run REMOVE,
    // ADD (the replace's own re-add), updateEmoteAlias (the adopt), ADD (the rename, under the
    // typed alias), in that order.
    const mutations = calls.filter((call) => call.kind !== 'setRead');
    expect(mutations.map((call) => call.kind)).toEqual([
      'removeEmote',
      'addEmote',
      'updateEmoteAlias',
      'addEmote',
    ]);
    expect(mutations[0].variables).toMatchObject({ emoteId: 'target-catjam' });
    expect(mutations[1].variables).toMatchObject({ emoteId: '7tv-1', alias: 'CatJAM' });
    expect(mutations[2].variables).toMatchObject({
      emoteId: '7tv-3',
      currentAlias: 'PogOld',
      alias: 'Pog',
    });
    expect(mutations[3].variables).toMatchObject({ emoteId: '7tv-2', alias: 'KEKWv2' });

    // sync-imported names every row that added an emote (CatJAM's replace, KEKW's rename) — never
    // the adopt, which renames an existing entry rather than adding one.
    expect((syncImportedBody?.['sevenTvEmoteIds'] as string[] | undefined)?.slice().sort()).toEqual(
      ['7tv-1', '7tv-2'],
    );
    // sync-deleted (the set-centric removal report `SevenTvImportService.removalReport` sends,
    // spec 6.5) names only the replaced target.
    expect(syncDeletedBodies[0]?.sevenTvEmoteIds).toEqual(['target-catjam']);
    // The target is the tracked channel's active set: its channel is the expected hit (AK 8).
    expect(syncDeletedBodies[0]?.expectedChannelName).toBe(TARGET_CHANNEL);
  });

  test('a replace whose ADD 409s ends the row failed with the gap reason, still reports the removal, and the finished protocol records failedStep 1', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, [
      { sevenTvEmoteId: 'target-catjam', name: 'CatJAM' },
    ]);

    // The replace's removal report is set-centric (spec 6.5): addressed to the target set.
    const syncDeletedBodies = await mockSyncDeletedInSet(page, 'target-set');
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    const liveTarget: LiveSetEntry[] = [{ id: 'target-catjam', aliases: ['CatJAM'] }];
    await mockSevenTvGql(page, (request) => {
      const kind = sevenTvGqlRequestKind(request);
      switch (kind) {
        case 'setRead':
          return sevenTvSetReadPayload(liveTarget);
        case 'removeEmote':
          return {
            data: {
              emoteSets: { emoteSet: { removeEmote: { id: request.variables['emoteId'] } } },
            },
          };
        case 'addEmote':
          // A live 409 on the ADD half of a replace — the mutation itself, not a transport loss,
          // so the engine ends the row `failed`, never `unknown`.
          return {
            errors: [
              {
                message: 'emote alias already in use',
                extensions: { code: 'MUTATION_ERROR', status: 409 },
              },
            ],
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = await waitForImportConfirmDialog(page);
    await confirm.getByRole('button', { name: 'Namenskollisionen auflösen' }).click();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für CatJAM' })
      .getByRole('radio', { name: 'Ziel ersetzen' })
      .check();
    await confirm.getByRole('button', { name: 'Übernehmen' }).click();

    const downloadPromise = page.waitForEvent('download');
    await confirm.getByRole('button', { name: 'Rückweg sichern' }).click();
    await downloadPromise;
    await confirm.getByRole('button', { name: 'Starten' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3000);
    await expect(page.getByText('0 kopiert · 1 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    // The gap reason (import.errors.removedButNotAdded) — a replace's own REMOVE-succeeded-but-ADD-
    // failed wording, chosen over the generic "name taken" text even though 7TV answered 409
    // (withFailureReason's own priority, seven-tv-import.service.ts).
    await expect(
      page.getByText(
        'Das Ziel-Emote wurde entfernt, das neue aber nicht hinzugefügt — im Zielset fehlt jetzt dieser Name. Die Rückweg-Datei stellt das Ziel-Emote wieder her.',
      ),
    ).toBeVisible();

    // The removal report still names the target: 7TV confirmed the REMOVE regardless of the row's
    // own final status.
    expect(syncDeletedBodies[0]?.sevenTvEmoteIds).toEqual(['target-catjam']);
    // The target is the tracked channel's active set: its channel is the expected hit (AK 8).
    expect(syncDeletedBodies[0]?.expectedChannelName).toBe(TARGET_CHANNEL);

    const protocolDownloadPromise = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Protokoll herunterladen' }).click();
    const exportDialog = page.getByRole('dialog');
    await exportDialog.getByRole('radio', { name: 'JSON (Datenauszug)' }).check();
    await exportDialog.getByRole('button', { name: 'Exportieren' }).click();
    const protocolDownload = await protocolDownloadPromise;
    const protocolPath = await protocolDownload.path();
    const protocol = JSON.parse(readFileSync(protocolPath!, 'utf-8')) as {
      meta: { stage: string };
      rows: {
        action: string;
        failedStep: number | null;
        removedTarget: { confirmed: boolean } | null;
      }[];
    };
    expect(protocol.meta.stage).toBe('finished');
    const replaceRow = protocol.rows.find((row) => row.action === 'replace');
    expect(replaceRow?.failedStep).toBe(1);
    expect(replaceRow?.removedTarget?.confirmed).toBe(true);
  });

  test('leaving both conflicts untouched and clicking Kopieren sends only the plain add, exactly the toAdd count, with no removal line and no download', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, [
      { sevenTvEmoteId: 'target-catjam', name: 'CatJAM' },
      { sevenTvEmoteId: '7tv-2', name: 'KekwOld' },
    ]);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    const liveTarget: LiveSetEntry[] = [
      { id: 'target-catjam', aliases: ['CatJAM'] },
      { id: '7tv-2', aliases: ['KekwOld'] },
    ];
    const calls: GqlCall[] = [];
    await mockSevenTvGql(page, (request) => {
      const kind = sevenTvGqlRequestKind(request);
      calls.push({ kind, variables: request.variables });
      switch (kind) {
        case 'setRead':
          return sevenTvSetReadPayload(liveTarget);
        case 'addEmote':
          return {
            data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    let downloadFired = false;
    page.on('download', () => {
      downloadFired = true;
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await cell(page, 'Pog').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = await waitForImportConfirmDialog(page);
    await expect(
      confirm.getByText(
        '1 Emote trägt einen Namen, der im Zielset schon vergeben ist — wird nicht übertragen:',
      ),
    ).toBeVisible();
    await expect(
      confirm.getByText('1 ist im Zielset bereits vorhanden, heißt dort aber anders:'),
    ).toBeVisible();
    await expect(confirm.getByRole('button', { name: 'Kopieren' })).toBeEnabled();

    // Nothing opened either resolve step and no decision was made — the dialog itself never reads
    // the target set live merely for having unresolved conflicts on screen (AK 5).
    expect(calls).toEqual([]);

    await confirm.getByRole('button', { name: 'Kopieren' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(2000);
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    await expect(page.getByText(/Emote wird aus dem Zielset entfernt/)).toHaveCount(0);
    await expect(page.getByText(/Eintrag im Zielset wird umbenannt/)).toHaveCount(0);

    // The unconditional pre-send duplicate re-check (#149/T5) still reads the target once through
    // 7TV's own GQL endpoint even for a plan with nothing to remove — a constant of every import
    // run, not something #230 changes, and not what "no set read" above is about (that assertion
    // pins the ABSENCE of a read before the click, i.e. that the dialog itself never triggers one).
    const setReads = calls.filter((call) => call.kind === 'setRead');
    const mutations = calls.filter((call) => call.kind !== 'setRead');
    expect(setReads).toHaveLength(1);
    expect(mutations).toHaveLength(1);
    expect(mutations[0]).toMatchObject({
      kind: 'addEmote',
      variables: { emoteId: '7tv-3', alias: 'Pog' },
    });
    expect(downloadFired).toBe(false);
  });

  test('a live target that gained a third alias since the preview blocks the run, names the row in the banner and overlays the live counterpart without a reload', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, [
      { sevenTvEmoteId: 'target-catjam', name: 'CatJAM' },
    ]);
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    // The REST preview (mockEmoteList above) and the live 7TV read disagree from the start — the
    // live set already carries a third alias the dialog's own preview never saw, exactly what
    // "drifted since the preview" means for a read taken only once, at dialog-open time.
    const driftedTarget: LiveSetEntry[] = [
      { id: 'target-catjam', aliases: ['CatJAM', 'CatJAMOld', 'CatJAMExtra'] },
    ];
    await mockSevenTvGql(page, (request) => {
      const kind = sevenTvGqlRequestKind(request);
      switch (kind) {
        case 'setRead':
          return sevenTvSetReadPayload(driftedTarget);
        case 'addEmote':
          return {
            data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    let downloadFired = false;
    page.on('download', () => {
      downloadFired = true;
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await cell(page, 'Pog').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = await waitForImportConfirmDialog(page);
    await confirm.getByRole('button', { name: 'Namenskollisionen auflösen' }).click();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für CatJAM' })
      .getByRole('radio', { name: 'Ziel ersetzen' })
      .check();
    await confirm.getByRole('button', { name: 'Übernehmen' }).click();

    await confirm.getByRole('button', { name: 'Rückweg sichern' }).click();

    await expect(
      confirm.getByText(
        'Das Zielset hat sich seit der Vorschau geändert: CatJAM. Diese Zeilen stehen wieder auf „Überspringen“ und zeigen jetzt das aktuelle Gegenstück — bitte prüfen und erneut bestätigen.',
      ),
    ).toBeVisible();
    // "Ziel neu laden" is the banner's own action and stays offered — the point of this case is
    // that the resolution step already shows the live counterpart without it being clicked
    // (adjustment D), not that the button is gone.
    await expect(confirm.getByRole('button', { name: 'Ziel neu laden' })).toBeVisible();
    await expect(confirm.getByRole('button', { name: 'Starten' })).toHaveCount(0);
    // Pog alone still fills the plan (a plain add), so the execute button reads "Kopieren" rather
    // than vanishing or reading "nothing to add" — CatJAM's decision fell back to skip.
    await expect(confirm.getByRole('button', { name: 'Kopieren' })).toBeEnabled();
    expect(downloadFired).toBe(false);

    await confirm.getByRole('button', { name: 'Namenskollisionen auflösen' }).click();
    await expect(
      confirm
        .getByRole('radiogroup', { name: 'Aktion für CatJAM' })
        .getByRole('radio', { name: 'Überspringen' }),
    ).toBeChecked();
    await expect(confirm.getByText('CatJAM · CatJAMOld · CatJAMExtra')).toBeVisible();
  });

  test('a lost ADD answer settles green when the re-read shows the source under its alias, and red with the gap reason when it does not', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set', {
      capacity: 1000,
      occupiedSlots: 3,
    });
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, [
      { sevenTvEmoteId: 'target-a', name: 'CatJAM' },
      { sevenTvEmoteId: 'target-b', name: 'KEKW' },
    ]);

    // `Record<string, unknown>`, not a narrower `{ sevenTvEmoteIds?: string[] }` shape: standard
    // control-flow narrowing, not a compiler quirk — TypeScript does not follow an assignment made
    // inside a callback, so from the narrowing analysis's point of view this `let` is never
    // assigned at all, and a narrower object-literal type here narrows to `never` at any later
    // read (reproduces even for a bare `string | null`). The field is read as `unknown` below
    // instead, compared with `toEqual` rather than through a typed cast.
    let syncImportedBody: Record<string, unknown> | null = null;
    await page.route(`**/api/channels/${TARGET_CHANNEL}/emotes/sync-imported`, async (route) => {
      syncImportedBody = route.request().postDataJSON();
      await route.fulfill({ status: 204 });
    });
    // The replace's removal report is set-centric (spec 6.5): addressed to the target set.
    const syncDeletedBodies = await mockSyncDeletedInSet(page, 'target-set');
    await mockChannelScopedResync(page, TARGET_CHANNEL);

    const confirmedTarget: LiveSetEntry[] = [
      { id: 'target-a', aliases: ['CatJAM'] },
      { id: 'target-b', aliases: ['KEKW'] },
    ];
    // What the post-run re-read finds: CatJAM's replace really did land (the SOURCE id now holds
    // the freed name), KEKW's REMOVE went through but its ADD never landed anywhere and nothing
    // else took the freed name either — the honest state of a lost answer that genuinely failed.
    // One run covers both outcomes of a lost transport answer at once, rather than two runs under
    // two separately mocked re-reads.
    const settledTarget: LiveSetEntry[] = [{ id: '7tv-1', aliases: ['CatJAM'] }];
    let setReadCount = 0;
    await mockSevenTvGql(page, (request) => {
      const kind = sevenTvGqlRequestKind(request);
      switch (kind) {
        case 'setRead':
          setReadCount++;
          // Calls 1 and 2 are the dialog's own verify and the pre-send recheck, both against the
          // still-confirmed state; call 3 onward is the post-run settle read.
          return sevenTvSetReadPayload(setReadCount <= 2 ? confirmedTarget : settledTarget);
        case 'removeEmote':
          return {
            data: {
              emoteSets: { emoteSet: { removeEmote: { id: request.variables['emoteId'] } } },
            },
          };
        case 'addEmote':
          return {
            data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    // Aborts the ADD half of both replace rows — a transport loss, not a GQL rejection, so the run
    // engine marks both rows `unknown` (transportLossIsUnknown) instead of `failed` and re-reads
    // the target before either can settle. Registered after mockSevenTvGql so it runs first
    // (Playwright matches route handlers in reverse registration order) and falls back to it for
    // every other call.
    await page.route('https://7tv.io/v4/gql', async (route) => {
      const body = route.request().postDataJSON() as {
        query: string;
        variables: Record<string, unknown>;
      };
      const kind = sevenTvGqlRequestKind(body);
      if (
        kind === 'addEmote' &&
        (body.variables['emoteId'] === '7tv-1' || body.variables['emoteId'] === '7tv-2')
      ) {
        await route.abort();
        return;
      }
      await route.fallback();
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = await waitForImportConfirmDialog(page);
    await confirm.getByRole('button', { name: 'Namenskollisionen auflösen' }).click();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für CatJAM' })
      .getByRole('radio', { name: 'Ziel ersetzen' })
      .check();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für KEKW' })
      .getByRole('radio', { name: 'Ziel ersetzen' })
      .check();
    await confirm.getByRole('button', { name: 'Übernehmen' }).click();

    const downloadPromise = page.waitForEvent('download');
    await confirm.getByRole('button', { name: 'Rückweg sichern' }).click();
    await downloadPromise;
    await confirm.getByRole('button', { name: 'Starten' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(6000);
    await expect(page.getByText('1 kopiert · 1 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    await expect(
      page.getByText(/Das Ziel-Emote wurde entfernt, das neue aber nicht hinzugefügt/),
    ).toBeVisible();

    expect(syncImportedBody?.['sevenTvEmoteIds']).toEqual(['7tv-1']);
    expect(syncDeletedBodies[0]?.sevenTvEmoteIds?.slice().sort()).toEqual(['target-a', 'target-b']);
    // The target is the tracked channel's active set: its channel is the expected hit (AK 8).
    expect(syncDeletedBodies[0]?.expectedChannelName).toBe(TARGET_CHANNEL);
  });

  test('restoring from the finished protocol of a two-replace run skips the row a successful replace now owns and re-adds only the gap, exactly once', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockTargetPicker(page);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockWorkspace(page, TARGET_CHANNEL, [], 'target-set');
    await mockSetWarning(page, TARGET_CHANNEL);
    await mockEmoteList(page, TARGET_CHANNEL, [
      { sevenTvEmoteId: 'target-a', name: 'CatJAM' },
      { sevenTvEmoteId: 'target-b', name: 'KEKW' },
    ]);

    // The replace's removal report is set-centric (spec 6.5): addressed to the target set.
    const syncDeletedBodies = await mockSyncDeletedInSet(page, 'target-set');
    await mockSyncImported(page, TARGET_CHANNEL);
    await mockChannelScopedResync(page, TARGET_CHANNEL);
    const syncRestoredBodies = await mockSyncRestoredInSet(page, 'target-set');

    const confirmedTarget: LiveSetEntry[] = [
      { id: 'target-a', aliases: ['CatJAM'] },
      { id: 'target-b', aliases: ['KEKW'] },
    ];
    // CatJAM's replace succeeds — 7tv-1 now holds the name — and KEKW's ADD 409s: its old target
    // is gone and nothing took the freed name, the gap the restore below is meant to close.
    const postRunTarget: LiveSetEntry[] = [{ id: '7tv-1', aliases: ['CatJAM'] }];
    let setReadCount = 0;
    await mockSevenTvGql(page, (request) => {
      const kind = sevenTvGqlRequestKind(request);
      switch (kind) {
        case 'setRead':
          setReadCount++;
          return sevenTvSetReadPayload(setReadCount <= 2 ? confirmedTarget : postRunTarget);
        case 'removeEmote':
          return {
            data: {
              emoteSets: { emoteSet: { removeEmote: { id: request.variables['emoteId'] } } },
            },
          };
        case 'addEmote':
          if (request.variables['emoteId'] === '7tv-2') {
            return {
              errors: [
                {
                  message: 'emote alias already in use',
                  extensions: { code: 'MUTATION_ERROR', status: 409 },
                },
              ],
            };
          }
          return {
            data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await cell(page, 'KEKW').click({ modifiers: ['Shift'] });
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Main (aktiv)' }).check();
    await picker.getByRole('button', { name: 'Weiter' }).click();

    const confirm = await waitForImportConfirmDialog(page);
    await confirm.getByRole('button', { name: 'Namenskollisionen auflösen' }).click();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für CatJAM' })
      .getByRole('radio', { name: 'Ziel ersetzen' })
      .check();
    await confirm
      .getByRole('radiogroup', { name: 'Aktion für KEKW' })
      .getByRole('radio', { name: 'Ziel ersetzen' })
      .check();
    await confirm.getByRole('button', { name: 'Übernehmen' }).click();

    const plannedDownloadPromise = page.waitForEvent('download');
    await confirm.getByRole('button', { name: 'Rückweg sichern' }).click();
    await plannedDownloadPromise;
    await confirm.getByRole('button', { name: 'Starten' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(4000);
    await expect(page.getByText('1 kopiert · 1 fehlgeschlagen · 0 abgebrochen')).toBeVisible();
    expect(syncDeletedBodies[0]?.sevenTvEmoteIds?.slice().sort()).toEqual(['target-a', 'target-b']);
    // The target is the tracked channel's active set: its channel is the expected hit (AK 8).
    expect(syncDeletedBodies[0]?.expectedChannelName).toBe(TARGET_CHANNEL);

    const protocolDownloadPromise = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Protokoll herunterladen' }).click();
    const exportDialog = page.getByRole('dialog');
    await exportDialog.getByRole('radio', { name: 'JSON (Datenauszug)' }).check();
    await exportDialog.getByRole('button', { name: 'Exportieren' }).click();
    const finishedDownload = await protocolDownloadPromise;
    const finishedPath = await finishedDownload.path();
    const finishedProtocolText = readFileSync(finishedPath!, 'utf-8');

    await page.goto(`/channels/${TARGET_CHANNEL}/usage-stats`);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);

    const restoreCalls: { kind: SevenTvGqlRequestKind; variables: Record<string, unknown> }[] = [];
    // Same endpoint the transfer run above used; page.route survives the navigation, so this
    // second handler only has to add its own recording on top, in front of the still-registered
    // mockSevenTvGql handler (reverse registration order).
    await page.route('https://7tv.io/v4/gql', async (route) => {
      const body = route.request().postDataJSON() as {
        query: string;
        variables: Record<string, unknown>;
      };
      restoreCalls.push({ kind: sevenTvGqlRequestKind(body), variables: body.variables });
      await route.fallback();
    });

    const fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_aatrociity_transfer_202609231200.json',
      mimeType: 'application/json',
      buffer: Buffer.from(finishedProtocolText, 'utf-8'),
    });

    // #255: the confirmation's own open-time duplicate check (`loadRestoreConfirmPreview`) is a
    // third `setRead` here, past the two the transfer run above already spent — so it, too, lands
    // on `postRunTarget`, the same state the confirm-time re-check below still sees. CatJAM's row
    // (target-a) is dropped before the dialog ever opens: target-a itself is gone, but 7tv-1 now
    // holds the name 'CatJAM', so the row is skipped as name-taken (rule 4) rather than restored.
    // KEKW's row (target-b) is genuinely missing, so it survives. The dialog therefore opens
    // already counting and naming only the one row that will actually be sent.
    const restoreConfirm = page.getByRole('dialog');
    await expect(restoreConfirm.locator('#app-dialog-title')).toHaveText(
      '1 Emote wieder zum Set hinzufügen?',
    );
    await expect(restoreConfirm.locator('app-name-preview-list')).toContainText('KEKW');
    await expect(restoreConfirm.locator('app-name-preview-list')).not.toContainText('CatJAM');
    await restoreConfirm.getByRole('button', { name: 'Wiederherstellen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3000);

    const restoreMutations = restoreCalls.filter((call) => call.kind !== 'setRead');
    expect(restoreMutations.map((call) => call.kind)).toEqual(['addEmote']);
    expect(restoreMutations[0].variables).toMatchObject({ emoteId: 'target-b', alias: 'KEKW' });
    expect(restoreCalls.some((call) => call.kind === 'removeEmote')).toBe(false);
    // The restore reports set-centrically too (spec 6.4, AK 7): the one re-added id, with the
    // target's tracked channel expected, since the set is its active one.
    await expect
      .poll(() => syncRestoredBodies[0])
      .toEqual({ sevenTvEmoteIds: ['target-b'], expectedChannelName: TARGET_CHANNEL });
    // The dock's own "name taken" notice for the row the restore itself could not bring back
    // (target-a, since 7tv-1 already holds 'CatJAM') — lives in the mass-delete panel + its
    // announcer, not import-progress-section (adjustment G row 5). Two elements carry this text by
    // design (§4.5, same pattern as usage-stats-page's own pruned-selection notice): the panel's
    // own aria-hidden paragraph for sighted users, and DockOutcomeAnnouncer's spoken twin — this
    // targets the aria-hidden one specifically.
    await expect(
      page.locator('[aria-hidden="true"]').filter({
        hasText: '1 Alias übersprungen — der Name gehört inzwischen einem anderen Emote.',
      }),
    ).toBeVisible();
  });

  // #253, spec 4.5 point 15/18: the replace lock for an untracked target is gone — "Ziel ersetzen"
  // is offered exactly as for a tracked target, still requires the recovery file before it starts
  // (AK 16, 17), and the removal report goes to the set-centric route with `expectedChannelName:
  // null` (F2, the untracked account has no channel of ours to expect a hit from).
  test('an untracked target offers replace, requires the recovery file named by set id, and reports the removal set-centrically with no expected channel (R5, AK 16, 17)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [{ id: 'set-1', name: 'Hauptset', isActive: true }],
      },
      {
        twitchChannelId: 'untracked-1',
        twitchLogin: 'stranger',
        sets: [{ id: 'set-untracked', name: 'Wegwerf-Set', ownerDisplayName: 'Stranger' }],
      },
    ]);
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockForeignEmoteSetPreview(page, 'stranger', {
      channelName: 'stranger',
      sevenTvUserId: null,
      emoteSetId: 'set-untracked',
      emoteSetName: 'Wegwerf-Set',
      capacity: 250,
      totalCount: 1,
      emotes: [{ sevenTvEmoteId: 'target-catjam', name: 'CatJAM' }],
    });
    await mockSyncImportedToSet(page, 'set-untracked');
    // The replace's removal report is set-centric (spec 6.5), addressed to the untracked target
    // set — there is no channel of ours to report against.
    const syncDeletedBodies = await mockSyncDeletedInSet(page, 'set-untracked');

    const liveTarget: LiveSetEntry[] = [{ id: 'target-catjam', aliases: ['CatJAM'] }];
    const calls: GqlCall[] = [];
    await mockSevenTvGql(page, (request) => {
      const kind = sevenTvGqlRequestKind(request);
      calls.push({ kind, variables: request.variables });
      switch (kind) {
        case 'setRead':
          return sevenTvSetReadPayload(liveTarget);
        case 'removeEmote':
          return {
            data: {
              emoteSets: { emoteSet: { removeEmote: { id: request.variables['emoteId'] } } },
            },
          };
        case 'addEmote':
          return {
            data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await cell(page, 'CatJAM').click();
    await copyButton(page).click();

    const picker = page.getByRole('dialog');
    await picker.getByRole('radio', { name: 'Wegwerf-Set' }).check();
    await picker.getByRole('button', { name: 'Ja, dieses Set' }).click();

    // No title assertion here: the row is a collision, so the title's own `addCount` settles at 0
    // the moment the target set loads (a transient "1" — the honest upper bound before the target
    // answers — would make this racy).
    const confirm = page.getByRole('dialog');
    await confirm.getByRole('button', { name: 'Namenskollisionen auflösen' }).click();

    // Replace is listed and enabled, exactly like a tracked target's own — the lock is gone (AK 16).
    const replaceRadio = confirm
      .getByRole('radiogroup', { name: 'Aktion für CatJAM' })
      .getByRole('radio', { name: 'Ziel ersetzen' });
    await expect(replaceRadio).toBeEnabled();
    await replaceRadio.check();
    await confirm.getByRole('button', { name: 'Übernehmen' }).click();

    await expect(
      confirm.getByText('1 Emote wird aus dem Zielset entfernt und durch das Quell-Emote ersetzt.'),
    ).toBeVisible();

    // The safeguard is still a file, not a typed confirmation (AK 17) — the recovery file's name
    // falls back to the set id where a tracked run would have named the channel (spec 4.5 point 18).
    const downloadPromise = page.waitForEvent('download');
    await confirm.getByRole('button', { name: 'Rückweg sichern' }).click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toMatch(
      /^emotepurge_set-untracked_transfer-plan_.*\.json$/,
    );

    await confirm.getByRole('button', { name: 'Starten' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(2000);
    await expect(page.getByText('1 kopiert · 0 fehlgeschlagen · 0 abgebrochen')).toBeVisible();

    const mutations = calls.filter((call) => call.kind !== 'setRead');
    expect(mutations.map((call) => call.kind)).toEqual(['removeEmote', 'addEmote']);
    expect(mutations[0].variables).toMatchObject({ emoteId: 'target-catjam' });
    expect(mutations[1].variables).toMatchObject({ emoteId: '7tv-1', alias: 'CatJAM' });

    expect(syncDeletedBodies[0]?.sevenTvEmoteIds).toEqual(['target-catjam']);
    // No channel of ours to expect a hit from — the untracked account has none (F2).
    expect(syncDeletedBodies[0]?.expectedChannelName).toBeNull();
  });
});

/**
 * #253 end to end: a restore file names its own target set, the shared pre-check resolves it
 * against the caller's target list, and the run writes and reports there — whichever channel page,
 * and whichever set on it (or none), it was read on (spec 9.4; AK 1, 2, 7, 18, 19, 21, 33, 34).
 */
test.describe('restore per set: the file names the target (#253)', () => {
  /** The untracked account the first test below restores into: a 7TV editor grant on a Twitch
   *  account this app does not track (spec 8.6) — no channel of ours to expect a hit from, and no
   *  channel page that could ever show the run. */
  const UNTRACKED_LOGIN = 'stranger';
  const UNTRACKED_SET_ID = 'set-untracked';

  /**
   * A `finished` transfer-run protocol (spec 4.1 point 3) for a run into `targetEmoteSetId`: one
   * replace row whose REMOVE 7TV confirmed (its ADD 409'd — the gap a restore closes) and one whose
   * REMOVE never ran (cancelled, `confirmed: false`). Only the confirmed one is a restore row — the
   * confirmation's "1 Emote" title is what proves it, since the cancelled row's target is still in
   * the set and the duplicate filter would drop it before the run anyway. `targetChannelName: null`
   * and the envelope's `''` are what an untracked target writes (F2) — the parser reads neither.
   */
  function finishedTransferRunFile(input: {
    targetEmoteSetId: string;
    targetChannelName: string | null;
    targetOwnerDisplayName: string | null;
  }): { name: string; mimeType: string; buffer: Buffer } {
    return {
      name: `emotepurge_${input.targetChannelName ?? input.targetEmoteSetId}_transfer_2026-09-24-1200.json`,
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'transfer-run',
          formatVersion: 1,
          exportedAt: '2026-09-24T12:05:00Z',
          channelName: input.targetChannelName ?? '',
          withheld: [],
          meta: {
            stage: 'finished',
            targetEmoteSetId: input.targetEmoteSetId,
            targetChannelName: input.targetChannelName,
            targetOwnerDisplayName: input.targetOwnerDisplayName,
            origin: { kind: 'channel', channelName: SOURCE_CHANNEL },
            startedAt: '2026-09-24T12:00:00Z',
            finishedAt: '2026-09-24T12:01:00Z',
            counts: {
              requested: 2,
              succeeded: 0,
              failed: 1,
              cancelled: 1,
              removed: 1,
              unknown: 0,
            },
          },
          rows: [
            {
              action: 'replace',
              sourceName: 'CatJAM',
              alias: 'CatJAM',
              sevenTvEmoteId: '7tv-1',
              status: 'failed',
              failedStep: 1,
              errorMessage: 'emote alias already in use',
              removedTarget: {
                sevenTvEmoteId: 'target-catjam',
                entries: [{ alias: 'CatJAM' }],
                aliases: ['CatJAM'],
                defaultName: 'CatJAM',
                confirmed: true,
              },
            },
            {
              action: 'replace',
              sourceName: 'KEKW',
              alias: 'KEKW',
              sevenTvEmoteId: '7tv-2',
              status: 'cancelled',
              failedStep: null,
              errorMessage: null,
              removedTarget: {
                sevenTvEmoteId: 'target-kekw',
                entries: [{ alias: 'KEKW' }],
                aliases: ['KEKW'],
                defaultName: 'KEKW',
                confirmed: false,
              },
            },
          ],
        }),
        'utf-8',
      ),
    };
  }

  /** Answers the restore's 7TV traffic — the duplicate filter's live read of the target (only the
   *  never-removed `target-kekw` is still there) and the ADD — and records every call. Seeds the
   *  write token too (`mockSevenTvGql`), so the flow goes straight to the confirmation. */
  async function mockRestoreGql(page: Page): Promise<GqlCall[]> {
    const calls: GqlCall[] = [];
    await mockSevenTvGql(page, (request) => {
      const kind = sevenTvGqlRequestKind(request);
      calls.push({ kind, variables: request.variables });
      switch (kind) {
        case 'setRead':
          return sevenTvSetReadPayload([{ id: 'target-kekw', aliases: ['KEKW'] }]);
        case 'addEmote':
          return {
            data: { emoteSets: { emoteSet: { addEmote: { id: request.variables['emoteId'] } } } },
          };
        default:
          throw new Error(`unexpected 7TV GQL request: ${request.query}`);
      }
    });
    return calls;
  }

  /** Every client-side resync request (`POST /api/channels/{c}/resync`), whatever the channel —
   *  recorded without answering it, so a stray one cannot hide behind a missing mock. */
  function recordResyncPosts(page: Page): string[] {
    const posts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST' && new URL(request.url()).pathname.endsWith('/resync')) {
        posts.push(request.url());
      }
    });
    return posts;
  }

  test('a transfer file for an untracked set, read on another channel’s page, restores into the file’s set and reports it set-centrically with no expected channel (AK 1, 7, 18, 19, 21)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    // The page the file is read on: this channel, its own active set selected — neither is the
    // file's target.
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [{ id: 'set-1', name: 'Hauptset', isActive: true }],
      },
      {
        twitchChannelId: 'untracked-1',
        twitchLogin: UNTRACKED_LOGIN,
        activeEmoteSetId: UNTRACKED_SET_ID,
        sets: [
          {
            id: UNTRACKED_SET_ID,
            name: 'Wegwerf-Set',
            isActive: true,
            ownerDisplayName: 'Stranger',
            editable: true,
          },
        ],
      },
    ]);
    // The confirmation's slot preview for an untracked target: the per-set live read, keyed by the
    // account's own login (spec 4.3 point 8).
    await mockForeignEmoteSetPreview(page, UNTRACKED_LOGIN, {
      channelName: UNTRACKED_LOGIN,
      emoteSetId: UNTRACKED_SET_ID,
      emoteSetName: 'Wegwerf-Set',
      capacity: 250,
      totalCount: 1,
      emotes: [{ sevenTvEmoteId: 'target-kekw', name: 'KEKW' }],
    });
    const syncRestoredBodies = await mockSyncRestoredInSet(page, UNTRACKED_SET_ID);
    const calls = await mockRestoreGql(page);
    const resyncPosts = recordResyncPosts(page);
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    const fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles(
      finishedTransferRunFile({
        targetEmoteSetId: UNTRACKED_SET_ID,
        targetChannelName: null,
        targetOwnerDisplayName: 'Stranger',
      }),
    );

    // The confirmation names what the TARGET LIST resolved (AK 35): set name, set id and owner; no
    // channel line and no "not active" line for an untracked target (spec 4.3 point 6); and the
    // foreign-to-view hint, since the set on screen is `set-1` (AK 19).
    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '1 Emote wieder zum Set hinzufügen?',
    );
    await expect(confirm.getByText('In das Set „Wegwerf-Set“.')).toBeVisible();
    await expect(confirm.getByText(`Set-ID: ${UNTRACKED_SET_ID}`)).toBeVisible();
    await expect(confirm.getByText('Besitzer: Stranger')).toBeVisible();
    await expect(confirm.getByText(/^Kanal:/)).toHaveCount(0);
    await expect(confirm.getByText('Dieses Set ist gerade nicht aktiv.')).toHaveCount(0);
    await expect(confirm.getByText('Diese Ansicht zeigt von diesem Lauf nichts.')).toBeVisible();

    await confirm.getByRole('button', { name: 'Wiederherstellen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3000);
    await expect(
      page.getByText('1 wiederhergestellt · 0 fehlgeschlagen · 0 abgebrochen'),
    ).toBeVisible();
    // The dock names the run's target, since this page shows none of it (spec 4.4 point 12).
    await expect(page.getByText('Ziel: Set Wegwerf-Set von Stranger')).toBeVisible();

    // Exactly one ADD, into the file's set — the unconfirmed REMOVE's target is not a restore row.
    const mutations = calls.filter((call) => call.kind !== 'setRead');
    expect(mutations.map((call) => call.kind)).toEqual(['addEmote']);
    expect(mutations[0].variables).toMatchObject({
      setId: UNTRACKED_SET_ID,
      emoteId: 'target-catjam',
      alias: 'CatJAM',
    });
    // The one report route there is (AK 7, the request half of AK 18): set-centric, no channel of
    // ours expected for an untracked target.
    await expect
      .poll(() => syncRestoredBodies)
      .toEqual([{ sevenTvEmoteIds: ['target-catjam'], expectedChannelName: null }]);
    // No client resync for an untracked target (AK 21) — nothing of ours could show the change.
    expect(resyncPosts).toEqual([]);
  });

  test('a channel page without a selected set still opens the file import and restores a transfer file, with the copy doors locked and the dock shown (AK 33, 34)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    // The workspace of a channel with no active 7TV set (the same state `usage-atlas.e2e.spec.ts`'s
    // "a channel without an active 7TV emote set" sets up): an empty set id plus a reason, so the
    // page settles instead of polling for a first sync — `selectedEmoteSetId()` is `null`.
    await mockChannelPermissions(page, SOURCE_CHANNEL);
    await mockChannelStatus(page, SOURCE_CHANNEL);
    await mockActiveEmoteSet(page, SOURCE_CHANNEL, '', {
      capacity: null,
      occupiedSlots: 0,
      syncFailureReason: 'no_active_emote_set',
    });
    await mockUsageTotals(page, SOURCE_CHANNEL, []);
    // The file's target: another tracked channel's active set.
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        sets: [],
      },
      {
        twitchChannelId: 'target-1',
        twitchLogin: TARGET_CHANNEL,
        trackedChannelName: TARGET_CHANNEL,
        activeEmoteSetId: 'target-set',
        sets: [{ id: 'target-set', name: 'Main', isActive: true }],
      },
    ]);
    // Slot preview for a tracked, active target: the channel's own status (spec 4.3 point 8).
    await mockActiveEmoteSet(page, TARGET_CHANNEL, 'target-set');
    const syncRestoredBodies = await mockSyncRestoredInSet(page, 'target-set', {
      channels: [{ channelName: TARGET_CHANNEL }],
      resyncTriggered: [TARGET_CHANNEL],
    });
    const calls = await mockRestoreGql(page);
    const resyncPosts = recordResyncPosts(page);
    await page.clock.install();

    await gotoUsageStats(page, SOURCE_CHANNEL);

    // AK 34: no copy button without a set, and no new button either — the one import entry.
    await expect(copyButton(page)).toHaveCount(0);
    // By name, not by header position like `openFileImportDialog`: without the copy button the
    // trigger is no longer the header's third button.
    const trigger = page.getByRole('button', { name: 'Importieren', exact: true });
    await expect(trigger).toBeEnabled();
    await trigger.click();

    // AK 33: the channel and leaderboard doors are locked with their reason; the file door is not.
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText('Emotes importieren');
    await expect(dialog.getByRole('button', { name: /^Aus einem Kanal/ })).toBeDisabled();
    await expect(dialog.getByRole('button', { name: /^Aus 7TVs Bestenliste/ })).toBeDisabled();
    await expect(dialog.getByText('Kein Set, in das kopiert werden könnte')).toHaveCount(2);
    const fileDoor = dialog.getByRole('button', { name: /^Aus einer Datei/ });
    await expect(fileDoor).toBeEnabled();
    await fileDoor.click();
    await expect(dialog.locator('#app-dialog-title')).toHaveText('Datei importieren');

    await dialog.locator('input[type="file"]').setInputFiles(
      finishedTransferRunFile({
        targetEmoteSetId: 'target-set',
        targetChannelName: TARGET_CHANNEL,
        targetOwnerDisplayName: TARGET_CHANNEL,
      }),
    );

    // A page with no selected set shows the foreign-to-view hint for any target (E21, AK 19).
    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '1 Emote wieder zum Set hinzufügen?',
    );
    await expect(confirm.getByText('Set-ID: target-set')).toBeVisible();
    await expect(confirm.getByText(`Kanal: ${TARGET_CHANNEL}`)).toBeVisible();
    await expect(confirm.getByText('Diese Ansicht zeigt von diesem Lauf nichts.')).toBeVisible();

    await confirm.getByRole('button', { name: 'Wiederherstellen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await page.clock.runFor(3000);
    // AK 33: the restore dock stands outside the set gate — visible on a page with no set at all.
    await expect(
      page.getByText('1 wiederhergestellt · 0 fehlgeschlagen · 0 abgebrochen'),
    ).toBeVisible();
    await expect(page.getByText(`Ziel: ${TARGET_CHANNEL} · Set Main`)).toBeVisible();

    const mutations = calls.filter((call) => call.kind !== 'setRead');
    expect(mutations.map((call) => call.kind)).toEqual(['addEmote']);
    expect(mutations[0].variables).toMatchObject({ setId: 'target-set', emoteId: 'target-catjam' });
    // The target is its tracked channel's active set: that channel is the expected hit (E18).
    await expect
      .poll(() => syncRestoredBodies)
      .toEqual([{ sevenTvEmoteIds: ['target-catjam'], expectedChannelName: TARGET_CHANNEL }]);
    // The backend already resynced it (`resyncTriggered`), and an active set never needs the
    // client's own (E12).
    expect(resyncPosts).toEqual([]);
  });

  test('a purge protocol of another set of the same channel opens the confirmation with the foreign-to-view hint and the "not active" line (AK 2, 19)', async ({
    page,
  }) => {
    const HALLOWEEN_SET_ID = 'set-halloween';
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: SOURCE_CHANNEL, isBroadcaster: true, isTracked: true },
    ]);
    // The page shows the channel's ACTIVE set; the protocol names its other, non-active one.
    await mockWorkspace(page, SOURCE_CHANNEL, SOURCE_EMOTES);
    await mockChannelEmoteSetList(page, SOURCE_CHANNEL, {
      activeEmoteSetId: 'set-1',
      sets: [
        { id: 'set-1', name: 'Hauptset' },
        { id: HALLOWEEN_SET_ID, name: 'Halloween' },
      ],
    });
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'source-1',
        twitchLogin: SOURCE_CHANNEL,
        isOwnAccount: true,
        trackedChannelName: SOURCE_CHANNEL,
        activeEmoteSetId: 'set-1',
        sets: [
          { id: 'set-1', name: 'Hauptset', isActive: true },
          { id: HALLOWEEN_SET_ID, name: 'Halloween', ownerDisplayName: 'Sensitron' },
        ],
      },
    ]);
    // Slot preview for a non-active set of a tracked channel: the per-set live read, keyed by the
    // channel (spec 4.3 point 8).
    await mockForeignEmoteSetPreview(page, SOURCE_CHANNEL, {
      channelName: SOURCE_CHANNEL,
      emoteSetId: HALLOWEEN_SET_ID,
      emoteSetName: 'Halloween',
      capacity: 500,
      totalCount: 0,
      emotes: [],
    });
    // Seeds the write token (R14) so the flow goes straight to the confirmation.
    await page.addInitScript(() => {
      window.sessionStorage.setItem('ep_7tv_write_token', 'e2e-fake-write-token');
    });
    // #255: the confirmation's own open-time duplicate check reads the target set's live entries
    // before it ever opens — empty here, so the row it shows is unfiltered.
    await mockSevenTvGql(page, () => sevenTvSetReadPayload([]));
    const sevenTvRequests: string[] = [];
    page.on('request', (request) => {
      if (request.url().startsWith('https://7tv.io/')) {
        sevenTvRequests.push(request.url());
      }
    });

    await gotoUsageStats(page, SOURCE_CHANNEL);
    await expect(page.getByRole('button', { name: /^Set: Hauptset/ })).toBeVisible();

    const fileInput = await openFileImportDialog(page);
    await fileInput.setInputFiles({
      name: 'emotepurge_sensitron_purge_202609241200.json',
      mimeType: 'application/json',
      buffer: Buffer.from(
        JSON.stringify({
          source: 'emotepurge',
          kind: 'purge-run',
          formatVersion: 1,
          exportedAt: '2026-09-24T12:00:00Z',
          channelName: SOURCE_CHANNEL,
          withheld: [],
          meta: {
            emoteSetId: HALLOWEEN_SET_ID,
            startedAt: '2026-09-24T12:00:00Z',
            finishedAt: '2026-09-24T12:05:00Z',
            counts: { requested: 1, succeeded: 1, failed: 0, cancelled: 0 },
          },
          rows: [
            {
              emoteId: 'i1',
              sevenTvEmoteId: '7tv-spooky',
              name: 'Spooky',
              status: 'done',
              errorMessage: null,
            },
          ],
        }),
        'utf-8',
      ),
    });

    // Not refused (AK 2): the file's set is the target, and since it is a non-active set of its
    // tracked channel the confirmation says so; it is not the set on screen, so the foreign-to-
    // view hint shows too (AK 19 — a different set of the SAME channel counts as foreign, E21).
    const confirm = page.getByRole('dialog');
    await expect(confirm.locator('#app-dialog-title')).toHaveText(
      '1 Emote wieder zum Set hinzufügen?',
    );
    await expect(confirm.getByText('In das Set „Halloween“.')).toBeVisible();
    await expect(confirm.getByText(`Set-ID: ${HALLOWEEN_SET_ID}`)).toBeVisible();
    await expect(confirm.getByText('Besitzer: Sensitron')).toBeVisible();
    await expect(confirm.getByText(`Kanal: ${SOURCE_CHANNEL}`)).toBeVisible();
    await expect(confirm.getByText('Dieses Set ist gerade nicht aktiv.')).toBeVisible();
    await expect(confirm.getByText('Diese Ansicht zeigt von diesem Lauf nichts.')).toBeVisible();

    // A cancelled confirmation writes nothing — the only 7TV traffic is the read-only open-time
    // duplicate check (#255), never an `addEmote` mutation.
    await confirm.getByRole('button', { name: 'Abbrechen' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(sevenTvRequests).toEqual(['https://7tv.io/v4/gql']);
  });
});
