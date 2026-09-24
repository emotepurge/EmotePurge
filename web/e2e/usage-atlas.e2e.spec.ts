import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  emitLive,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelEmoteSetList,
  mockChannelPermissions,
  mockChannelStatus,
  mockForeignEmoteSetPreview,
  mockLegalAvailability,
  mockMyChannels,
  mockUsageChannelSeries,
  mockUsageDaily,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

/**
 * The atlas replaced a card grid with a sprite sheet, and with it the interaction model: 900 cells
 * share ONE tab stop that the arrow keys move (roving tabindex), the bands are a real grouping the
 * navigation has to cross correctly, and the action bar only exists while a selection does. None of
 * that is visible to a unit test — the pure parts are covered in atlas-grid.spec.ts, but whether
 * the focus actually lands on the cell the arrow key aimed at only shows in a browser.
 */

/** A transparent 1x1 PNG — what a stubbed CDN answers so no test depends on cdn.7tv.app. */
const PNG_1X1 = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
  'base64',
);

/** A set with a clear head, a middle, a tail and a block of never-used emotes. */
const EMOTES = [
  { name: 'catJAM', uses: 900 },
  { name: 'peepoSad', uses: 700 },
  { name: 'monkaW', uses: 240 },
  { name: 'KEKW', uses: 120 },
  { name: 'Pog', uses: 90 },
  { name: 'Sadge', uses: 40 },
  { name: 'Bedge', uses: 12 },
  { name: 'Copium', uses: 0 },
  { name: 'Susge', uses: 0 },
  { name: 'Clueless', uses: 0 },
].map((emote, index) => ({
  emoteId: `e${index + 1}`,
  emoteName: emote.name,
  sevenTvEmoteId: `7tv-${index + 1}`,
  imageUrl: `https://cdn.7tv.app/emote/${index + 1}/2x.webp`,
  totalUseCount: emote.uses,
  lastUsedDate: emote.uses > 0 ? '2026-07-14' : null,
}));

async function openAtlas(
  page: Page,
  emotes: MockEmoteUsage[] = EMOTES,
  botsExcludedSince: string | null = null,
  // A fourth positional parameter rather than an options object: reshaping the signature would
  // touch every existing caller for no gain here.
  sharedChatSeparatedSince: string | null = null,
): Promise<void> {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page);
  await installLiveStub(page);
  await mockMyChannels(page, [
    { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
  ]);
  await mockChannelPermissions(page, 'sensitron');
  await mockChannelStatus(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron', 'set-1', {
    capacity: 1000,
    occupiedSlots: 10,
    botsExcludedSince,
    sharedChatSeparatedSince,
  });
  await mockUsageTotals(page, 'sensitron', emotes);

  await page.goto('/channels/sensitron/usage-stats');
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
  // The heading sits outside the loading branch, so it proves nothing about the sheet. Wait for the
  // skeleton to go: it is a second role="status" (§6.1 gives every skeleton one), and while it is up
  // a bare getByRole('status') resolves to two elements and fails on strict mode rather than on the
  // thing under test.
  await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
}

// The permanent sr-only announcement region for the selection-pruned notice (usage-stats-page.html,
// §4.5) is ALSO role="status" and stays mounted even with nothing to say (#94 follow-up P2) — so a
// bare getByRole('status') resolves to two elements once loading is done, same strict-mode trap the
// comment above already works around for the loading skeleton. A plain `.filter({ hasText: 'von' })`
// used to pick this line out, but that word alone latently collides with the dock-outcome announcer
// that is now also permanently mounted (#134 follow-up): its German restore-cooldown text contains
// "von selbst" ("… aktualisiert sich innerhalb einer Minute von selbst."). The emote-count line's own
// shape — "{{ shown }} von {{ total }} Emote(s)" (de.json) — is numeric on both sides of "von", which
// the cooldown text never is, so a regex on that shape is specific to it.
const emoteCountStatus = (page: Page) =>
  page.getByRole('status').filter({ hasText: /\d+ von \d+/ });

const cell = (page: Page, name: string) =>
  page.getByRole('button', { name: new RegExp(`^${name} ·`) });

test.describe('emote atlas', () => {
  test('groups the set into weight bands derived from its own usage', async ({ page }) => {
    await openAtlas(page);

    // Pareto, not fixed thresholds: catJAM and peepoSad together are the first 1600 of the 2102
    // total, so both are heavy; monkaW alone then carries the set past 80 % and is the whole
    // regular band.
    await expect(page.getByRole('heading', { name: 'Tragend', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Regelmäßig' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Selten' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Nie benutzt' })).toBeVisible();
  });

  test('states each band as a share of usage and a count of emotes', async ({ page }) => {
    await openAtlas(page);

    // Both numbers carry their unit, and the percentage is measured rather than the 50 % cut that
    // produced the band: the cut lands mid-emote and takes the whole of peepoSad with it, so the
    // band really carries 1600 of 2102. "The first half of usage" would have claimed 50 % here.
    const heavy = page.getByRole('heading', { name: 'Tragend', exact: true }).locator('..');
    await expect(heavy).toContainText('76 % der Nutzung');
    await expect(heavy).toContainText('2 Emotes');

    // The singular is its own key — Transloco runs without a plural plugin here.
    const regular = page.getByRole('heading', { name: 'Regelmäßig' }).locator('..');
    await expect(regular).toContainText('1 Emote');
  });

  test('the distribution strip legend wraps its entries instead of clipping them', async ({
    page,
  }) => {
    await page.setViewportSize({ width: 393, height: 851 });
    await openAtlas(page);

    // No entry may be wider than the box it renders into — a fixed, segment-proportional width is
    // exactly what clipped "11 % regelmäßig" at every viewport width, not only a narrow one.
    const entries = page.locator('[data-distribution-legend] > *');
    await expect(entries).toHaveCount(3);
    const overflows = await entries.evaluateAll((elements) =>
      elements.map((element) => ({
        text: element.textContent,
        scrollWidth: element.scrollWidth,
        clientWidth: Math.ceil(element.getBoundingClientRect().width),
      })),
    );
    for (const entry of overflows) {
      expect(entry.scrollWidth, `"${entry.text}" clipped`).toBeLessThanOrEqual(entry.clientWidth);
    }

    // The 9 % threshold used to hide small bands outright; catch a regression to "just drop it"
    // rather than fixing the width.
    // The names render visually lowercase (a CSS transform), but textContent keeps its original
    // casing — match case-insensitively rather than assert on a presentation detail.
    const legend = page.locator('[data-distribution-legend]');
    await expect(legend).toContainText(/tragend/i);
    await expect(legend).toContainText(/regelmäßig/i);
    await expect(legend).toContainText(/selten/i);
  });

  test('holds exactly one tab stop and moves it with the arrow keys', async ({ page }) => {
    await openAtlas(page);

    // 900 focusable cells would make the keyboard route through the page unusable, which is what
    // the incumbent card grid did.
    await expect(page.locator('cdk-virtual-scroll-viewport button[tabindex="0"]')).toHaveCount(1);

    await cell(page, 'catJAM').focus();
    await page.keyboard.press('ArrowRight');
    // peepoSad is the next cell in the display order — same band here, but the move is computed on
    // the row structure either way, which is what makes a band boundary in between harmless.
    await expect(cell(page, 'peepoSad')).toBeFocused();

    // The last cell of the last band. Never-used emotes all sort equal, so the name tiebreaker
    // decides their order — Clueless, Copium, Susge.
    await page.keyboard.press('End');
    await expect(cell(page, 'Susge')).toBeFocused();

    await page.keyboard.press('Home');
    await expect(cell(page, 'catJAM')).toBeFocused();
  });

  test('marks a cell from the keyboard and opens the action bar', async ({ page }) => {
    await openAtlas(page);

    await expect(page.getByRole('button', { name: /^Löschen \(/ })).toHaveCount(0);

    await cell(page, 'monkaW').focus();
    await page.keyboard.press('Space');

    await expect(cell(page, 'monkaW')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Löschen (1)' })).toBeVisible();
    // The dock states what the selection costs the set, which is the number the decision turns on.
    await expect(page.getByText('9 von 1000 Slots nach dem Löschen')).toBeVisible();
  });

  test('space marks a cell, enter opens its history', async ({ page }) => {
    await mockUsageDaily(page, 'sensitron', [{ date: '2026-07-14', useCount: 120 }]);
    await openAtlas(page);

    // Both used to do the same thing, because both fire a native click on a button. Splitting them
    // is what gives the keyboard a route to the per-cell trigger without a second tab stop.
    await cell(page, 'KEKW').focus();
    await page.keyboard.press('Enter');

    await expect(page.getByRole('dialog')).toContainText('KEKW');
    // Closed first on purpose: the CDK dialog puts aria-hidden on everything behind it, so any
    // role-based assertion about the grid would pass while the dialog is up whatever the truth is.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(cell(page, 'KEKW')).toHaveAttribute('aria-pressed', 'false');
  });

  test('marks the whole never-used band in one go, and only that band', async ({ page }) => {
    await openAtlas(page);

    // Scoped to the atlas group: the toolbar's own mark-all button carries the same bare-verb
    // label (docs/UI-Designsprache.md §8.7 — both use a bare verb because each has a count stated
    // right beside it), so an unscoped lookup by name now matches two buttons.
    await page
      .getByRole('group', { name: 'Emote-Bogen' })
      .getByRole('button', { name: 'alle markieren' })
      .click();

    await expect(page.getByRole('button', { name: 'Löschen (3)' })).toBeVisible();
    await expect(cell(page, 'Copium')).toHaveAttribute('aria-pressed', 'true');
    await expect(cell(page, 'catJAM')).toHaveAttribute('aria-pressed', 'false');
  });

  test('a shift-click takes a range back out once the anchor click has unmarked it', async ({
    page,
  }) => {
    await openAtlas(page);

    // Build the range the way it always worked: click the head, shift-click the tail.
    await cell(page, 'catJAM').click();
    await cell(page, 'Bedge').click({ modifiers: ['Shift'] });
    await expect(page.getByRole('button', { name: 'Löschen (7)' })).toBeVisible();

    // A plain click on a marked cell takes that one out — and leaves the anchor on an unmarked row,
    // which is what turns the next shift-click around. Covered as state in list-selection.spec.ts;
    // what only a browser shows is that a real shift-click reaches that branch at all.
    await cell(page, 'monkaW').click();
    await expect(page.getByRole('button', { name: 'Löschen (6)' })).toBeVisible();

    await cell(page, 'Pog').click({ modifiers: ['Shift'] });
    await expect(page.getByRole('button', { name: 'Löschen (4)' })).toBeVisible();

    for (const name of ['monkaW', 'KEKW', 'Pog']) {
      await expect(cell(page, name)).toHaveAttribute('aria-pressed', 'false');
    }
    // Everything outside the range keeps its mark — a deselect must not reach past its own ends.
    for (const name of ['catJAM', 'peepoSad', 'Sadge', 'Bedge']) {
      await expect(cell(page, name)).toHaveAttribute('aria-pressed', 'true');
    }
  });

  test('a shift-click still adds while the anchor stays marked', async ({ page }) => {
    await openAtlas(page);

    await cell(page, 'KEKW').click();
    await cell(page, 'catJAM').click({ modifiers: ['Shift'] });

    await expect(page.getByRole('button', { name: 'Löschen (4)' })).toBeVisible();
    await expect(cell(page, 'Sadge')).toHaveAttribute('aria-pressed', 'false');
  });

  test('the action bar disappears again once nothing is marked', async ({ page }) => {
    await openAtlas(page);

    await cell(page, 'Bedge').click();
    await expect(page.getByRole('button', { name: 'Löschen (1)' })).toBeVisible();

    await page.getByRole('button', { name: 'Auswahl aufheben' }).click();

    await expect(page.getByRole('button', { name: /^Löschen \(/ })).toHaveCount(0);
  });

  test('a running selection survives typing into and clearing the name search, and the dock names what the filter hides', async ({
    page,
  }) => {
    // The mod-team report, reproduced literally (Konzept "Auswahl überlebt Suche und Filter",
    // 2026-09-18): mark cells, type into the search, keep typing, clear the search — the dock
    // counts the whole selection throughout, and nothing gets lost along the way.
    await openAtlas(page);

    await cell(page, 'catJAM').click();
    await cell(page, 'Bedge').click();
    await expect(page.getByRole('button', { name: 'Löschen (2)' })).toBeVisible();

    const dock = page.locator('.app-dock');
    // Plain text, not a role: the visible line is aria-hidden on purpose, because the permanently
    // mounted announcer outside the dock is its voice (docs/UI-Designsprache.md §4.5). A row that
    // is created together with its own sentence announces nothing.
    const hiddenLine = () => dock.getByText('davon durch den Filter ausgeblendet');
    const announcer = () =>
      page.getByRole('status').filter({ hasText: 'davon durch den Filter ausgeblendet' });
    const search = page.getByPlaceholder('Name suchen…');

    // "cat" only matches catJAM — Bedge falls out of the sheet without leaving the selection.
    await search.fill('cat');
    await expect(cell(page, 'Bedge')).toHaveCount(0);
    await expect(cell(page, 'catJAM')).toHaveAttribute('aria-pressed', 'true');
    await expect(dock.getByRole('button', { name: 'Löschen (2)' })).toBeVisible();
    // 2.1/2.2: while the filter hides part of the mark, the dock names the count, not just a
    // shrunk total — and the live region that stood before the search was touched carries the
    // same sentence for a screen reader.
    await expect(hiddenLine()).toContainText('1 davon durch den Filter ausgeblendet');
    await expect(announcer()).toContainText('1 davon durch den Filter ausgeblendet');

    // Weitertippen: narrowing the query further keeps the same emote hidden and the same count.
    await search.fill('catJ');
    await expect(cell(page, 'Bedge')).toHaveCount(0);
    await expect(dock.getByRole('button', { name: 'Löschen (2)' })).toBeVisible();
    await expect(hiddenLine()).toContainText('1 davon durch den Filter ausgeblendet');

    // Suche löschen: both marks reappear, nothing was pruned, and the hidden-by-filter line is
    // gone again because nothing is hidden any more.
    await search.fill('');
    await expect(cell(page, 'Bedge')).toHaveAttribute('aria-pressed', 'true');
    await expect(cell(page, 'catJAM')).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByRole('button', { name: 'Löschen (2)' })).toBeVisible();
    await expect(hiddenLine()).toHaveCount(0);
    await expect(announcer()).toHaveCount(0);
  });

  test('the sidecar names whatever cell the pointer is on', async ({ page }) => {
    await openAtlas(page);
    const sidecar = page.getByRole('complementary');

    // Before any hover it describes the busiest emote — the honest thing to be looking at first.
    await expect(sidecar).toContainText('catJAM');

    await cell(page, 'Sadge').hover();

    await expect(sidecar).toContainText('Sadge');
    await expect(sidecar).toContainText('Selten');
  });

  test('the curve states its scale, and the green bands say what the emote did on them', async ({
    page,
  }) => {
    // catJAM's 900 uses, as a curve peaking at 700. Distinct from every other number the sidecar
    // prints, so an exact-text match can tell the axis apart from the totals below it.
    // Live offsets counted from the mocked tracking start 2026-06-12: the 15., 16., 17. and 21.
    // The curve puts 700 on the 15. and nothing on the other three.
    await mockUsageChannelSeries(
      page,
      'sensitron',
      {
        '7tv-1': [
          [1, 200],
          [3, 700],
        ],
      },
      [3, 4, 5, 9],
    );
    await openAtlas(page);
    const sidecar = page.getByRole('complementary');

    // The axis is aria-hidden — the peak line carries the same maximum in words, which is what
    // keeps the graphic from meaning anything on its own. Asserted on the rendered text all the
    // same, because a scale nobody can read is the thing this exists to prevent.
    await expect(sidecar.getByText('700', { exact: true })).toBeVisible();
    await expect(sidecar.getByText('0', { exact: true })).toBeVisible();
    // The emote-specific statement, not the channel-wide one that used to stand here and read the
    // same for every emote. Both numbers are pinned: they follow the offsets above, not the calendar.
    await expect(sidecar).toContainText('An 3 von 4 Live-Tagen nicht benutzt');
    await expect(sidecar).not.toContainText('Live an');
  });

  test('the channel-wide live count is stated once, above the sheet', async ({ page }) => {
    // It answers a question about the stream, not about any one emote, so it belongs to the page.
    await mockUsageChannelSeries(page, 'sensitron', { '7tv-1': [[3, 700]] }, [3, 4, 5, 9]);
    await openAtlas(page);

    await expect(
      page.getByText(/Im gewählten Zeitraum war der Stream an 4 Tagen live\./),
    ).toBeVisible();
  });

  test('names the bot-exclusion date when the Api reports one, alongside the other honesty statements', async ({
    page,
  }) => {
    await mockUsageChannelSeries(page, 'sensitron', { '7tv-1': [[3, 700]] }, [3, 4, 5, 9]);
    await openAtlas(page, EMOTES, '2026-08-15');

    await expect(
      page.getByText(/Nachrichten bekannter Bots zählen seit dem 15\.08\.2026 nicht mit/),
    ).toBeVisible();
    // The two sentences it shares the caption paragraph with must still be there — this is one more
    // sentence in the same place, not a replacement.
    await expect(page.getByText(/Wir zählen für diesen Channel seit dem/)).toBeVisible();
    await expect(
      page.getByText(/Im gewählten Zeitraum war der Stream an 4 Tagen live\./),
    ).toBeVisible();
  });

  test('says nothing about bot exclusion when the Api reports no date', async ({ page }) => {
    // mockActiveEmoteSet's default (see openAtlas) — no botsExcludedSince set at all.
    await openAtlas(page);

    await expect(page.getByText(/Nachrichten bekannter Bots/)).toHaveCount(0);
  });

  test('names the shared-chat separation date when the Api reports one, alongside the other honesty statements', async ({
    page,
  }) => {
    await mockUsageChannelSeries(page, 'sensitron', { '7tv-1': [[3, 700]] }, [3, 4, 5, 9]);
    await openAtlas(page, EMOTES, '2026-08-15', '2026-09-07');

    await expect(
      page.getByText(
        /Nutzung aus dem geteilten Chat anderer Kanäle zählt seit dem 07\.09\.2026 nicht mit/,
      ),
    ).toBeVisible();
    // The three sentences it shares the caption paragraph with must still be there — the wording is
    // only how each sentence is recognized here, the subject under test is that a fourth exclusion
    // queues up behind them rather than displacing one (Designsprache §2.5).
    await expect(page.getByText(/Wir zählen für diesen Channel seit dem/)).toBeVisible();
    await expect(
      page.getByText(/Im gewählten Zeitraum war der Stream an 4 Tagen live\./),
    ).toBeVisible();
    await expect(page.getByText(/Nachrichten bekannter Bots zählen seit dem/)).toBeVisible();
  });

  test('says nothing about shared chat when the Api reports no date', async ({ page }) => {
    // mockActiveEmoteSet's default (see openAtlas) — no sharedChatSeparatedSince set at all.
    await openAtlas(page);

    await expect(page.getByText(/geteilten Chat anderer Kanäle/)).toHaveCount(0);
  });

  test('says nothing about live days for a range with no coverage', async ({ page }) => {
    // "0 of 57 days" would report an absence we never measured: a range older than the live poll has
    // no coverage data at all, which is not the same as a channel that never went live.
    await mockUsageChannelSeries(page, 'sensitron', {
      '7tv-1': [
        [1, 200],
        [3, 700],
      ],
    });
    await openAtlas(page);
    const sidecar = page.getByRole('complementary');

    await expect(sidecar.getByText('700', { exact: true })).toBeVisible();
    await expect(sidecar).not.toContainText('Live-Tag');
    await expect(page.getByText(/war der Stream an/)).toHaveCount(0);
  });

  test('draws no line for the days before the emote entered the set', async ({ page }) => {
    // The emote joined the set on the 20., eight days into the range. A baseline over the days
    // before that reads as "unused" where it should read as "did not exist" — the whole point.
    // The usage sits on day 10, inside that lifetime: a count *before* the 20. would be drawn on
    // purpose (a re-added emote keeps its history, see firstDrawableIndex) and would say nothing
    // about the leading silence this test is here for.
    await mockUsageChannelSeries(page, 'sensitron', { '7tv-1': [[10, 700]] }, [3, 4, 5, 9]);
    await openAtlas(
      page,
      EMOTES.map((emote) =>
        emote.emoteId === 'e1' ? { ...emote, firstSeenAt: '2026-06-20T00:00:00Z' } : emote,
      ),
    );
    const sidecar = page.getByRole('complementary');

    const points = await sidecar.locator('polyline').getAttribute('points');
    const firstX = Number(points!.split(' ')[0].split(',')[0]);
    expect(firstX).toBeGreaterThan(0);

    // Only the 21. falls inside the emote's lifetime, and the curve has nothing on it. Singular,
    // because "An 1 von 1 Live-Tagen" is not a sentence.
    await expect(sidecar).toContainText('Am einzigen Live-Tag nicht benutzt');
  });

  test('below the sidecar breakpoint the same readout is a line, and only one of them shows', async ({
    page,
  }) => {
    // 16rem of panel is a third of a narrow window, so under lg the readout collapses back into a
    // row of the toolbar. Both existing at once would say the same thing twice.
    await page.setViewportSize({ width: 900, height: 900 });
    await openAtlas(page);

    await expect(page.getByRole('complementary')).toBeHidden();
    await expect(page.locator('.app-sticky-bar').last()).toContainText('catJAM');
  });

  test('opens one emote history straight from its own cell', async ({ page }) => {
    await mockUsageDaily(page, 'sensitron', [{ date: '2026-07-14', useCount: 40 }]);
    await openAtlas(page);

    // The reason this trigger sits on the cell rather than in the inspector: the inspector follows
    // the pointer, so reaching a button inside it from a cell in the middle of the sheet means
    // crossing other cells, and it repoints under way. Here the travel is zero — which this test
    // reproduces by hovering the cell and clicking without leaving it.
    await cell(page, 'Sadge').hover();
    await page.getByRole('button', { name: 'Details zu Sadge anzeigen' }).click();

    await expect(page.getByRole('dialog')).toContainText('Sadge');
    // Closed first: the CDK dialog hides everything behind it from the accessibility tree, so this
    // assertion would pass vacuously with the dialog still up.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    // The trigger must not leak into the selection the delete path reads.
    await expect(cell(page, 'Sadge')).toHaveAttribute('aria-pressed', 'false');
    await expect(page.getByRole('button', { name: /^Löschen \(/ })).toHaveCount(0);
  });

  // The usage filter used to be three controls — Min, Max, and an "unused only" toggle that was not
  // a filter of its own but *was* min = 0 / max = 0, overwriting the two fields beside it. As one
  // menu the states have to stay reachable and, unlike the old toggle, re-picking the selected one
  // must not switch it back off.
  test('the usage menu narrows the sheet to the never-used emotes and says so on its trigger', async ({
    page,
  }) => {
    await openAtlas(page);
    await expect(emoteCountStatus(page)).toContainText('10 von 10');

    await page.getByRole('button', { name: /^Nutzung:/ }).click();
    await page.getByRole('radio', { name: 'nie benutzt' }).click();

    await expect(page.getByRole('button', { name: 'Nutzung: nie benutzt' })).toBeVisible();
    await expect(emoteCountStatus(page)).toContainText('3 von 10');
    await expect(cell(page, 'catJAM')).toHaveCount(0);
    await expect(cell(page, 'Copium')).toBeVisible();

    // The way back, which before this only existed once a filter had emptied the sheet entirely.
    await page.getByRole('button', { name: 'Filter zurücksetzen' }).click();
    await expect(page.getByRole('button', { name: 'Nutzung: alle' })).toBeVisible();
    await expect(emoteCountStatus(page)).toContainText('10 von 10');
  });

  test('a custom bound survives reopening the menu and states itself on the trigger', async ({
    page,
  }) => {
    await openAtlas(page);

    await page.getByRole('button', { name: /^Nutzung:/ }).click();
    await page.getByRole('radio', { name: 'eigener Bereich' }).click();
    await page.getByLabel('Höchstens').fill('100');
    await page.getByRole('button', { name: 'Fertig' }).click();

    await expect(page.getByRole('button', { name: 'Nutzung: bis 100×' })).toBeVisible();
    await expect(cell(page, 'catJAM')).toHaveCount(0);
    await expect(cell(page, 'Pog')).toBeVisible();

    // Reopening must land back on "custom" with the value still in the field: the preset cannot be
    // derived from the bounds alone, and an emptied bound would otherwise read as "all".
    await page.getByRole('button', { name: /^Nutzung:/ }).click();
    await expect(page.getByRole('radio', { name: 'eigener Bereich' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    await expect(page.getByLabel('Höchstens')).toHaveValue('100');
  });

  test('the drilldown curve keeps quiet about the days before the emote existed', async ({
    page,
  }) => {
    // Same statement as in the sidecar, from the other data path: the dialog loads its own per-emote
    // series with ISO live days, while the sidecar reads the batch response's offsets.
    await mockUsageDaily(
      page,
      'sensitron',
      [{ date: '2026-06-21', useCount: 40 }],
      ['2026-06-15', '2026-06-16', '2026-06-21'],
    );
    await openAtlas(
      page,
      EMOTES.map((emote) =>
        emote.emoteName === 'Sadge' ? { ...emote, firstSeenAt: '2026-06-20T00:00:00Z' } : emote,
      ),
    );

    await cell(page, 'Sadge').hover();
    await page.getByRole('button', { name: 'Details zu Sadge anzeigen' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toContainText('Sadge');

    const points = await dialog.locator('polyline').getAttribute('points');
    const firstX = Number(points!.split(' ')[0].split(',')[0]);
    expect(firstX).toBeGreaterThan(0);

    // Only the 21. falls inside the emote's lifetime, and it was used that day — so the positive
    // form, not "0 unused".
    await expect(dialog).toContainText('Am einzigen Live-Tag benutzt');
    await expect(dialog).not.toContainText('Live an');

    // Closed first: the CDK dialog hides everything behind it from the accessibility tree.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});

test.describe('a channel without an active 7TV emote set', () => {
  test('names the missing emote set instead of guessing', async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    // Empty set id *and* a reason: exactly the state issue #32 describes.
    await mockActiveEmoteSet(page, 'sensitron', '', {
      capacity: null,
      occupiedSlots: 0,
      syncFailureReason: 'no_active_emote_set',
      lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
    });
    await mockUsageTotals(page, 'sensitron', []);

    await page.goto('/channels/sensitron/usage-stats');

    await expect(
      page.getByText('Dieser Channel hat auf 7TV kein aktives Emote-Set.'),
    ).toBeVisible();
    await expect(page.getByText('Auf 7tv.app lässt sich ein Emote-Set anlegen')).toBeVisible();
    // The poll banner must not appear at all: with a reason in hand there is nothing to wait for,
    // and it used to hold the page for 30 seconds before falling back to the wrong message.
    await expect(page.getByText('Emote-Set wird geladen')).toHaveCount(0);
    await expect(
      page.getByText('Entweder ist das 7TV-Emote-Set leer, oder der erste Sync läuft noch'),
    ).toHaveCount(0);
  });

  // Issue #32 shipped the reason but not a way for it to keep up: SyncChannelAsync answers `null`
  // both when a sync fails again and when it succeeds without changing anything, so `channel.synced`
  // never fires either way and an open page kept describing a state that had already moved on — a
  // real, observed case is a moderator registering their 7TV account mid-session. The 60 s recheck
  // (SYNC_FAILURE_RECHECK_INTERVAL_MS in usage-stats-page.ts) closes that gap. Both tests below
  // drive it with a route whose response can change mid-test, unlike mockActiveEmoteSet's fixed one,
  // and with Playwright's `page.clock` rather than 61 real seconds each — see the "waiting for the
  // first 7TV sync" describe below for why `install()` runs before `goto` and only `runFor()` moves
  // time afterwards.
  test('adopts a resolved set once the reason clears, without a reload', async ({ page }) => {
    await page.clock.install();

    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    // Empty for now: loadTotals runs unconditionally, independent of the set status, so a stray
    // real emote here would make the grid render straight away and the reason-branch would never
    // even be reached — the same reason the "names the missing emote set" test above mocks `[]`.
    await mockUsageTotals(page, 'sensitron', []);

    const activeSetRequests = { count: 0 };
    page.on('request', (request) => {
      if (new URL(request.url()).pathname.endsWith('/emotes/active-set')) {
        activeSetRequests.count += 1;
      }
    });

    let resolved = false;
    await page.route('**/api/channels/sensitron/emotes/active-set', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(
          resolved
            ? {
                activeEmoteSetId: 'set-1',
                capacity: 1000,
                occupiedSlots: 10,
                trackedSince: '2026-06-12T09:14:00Z',
                syncFailureReason: null,
                lastSyncAttemptAtUtc: '2026-08-29T12:05:00Z',
              }
            : {
                activeEmoteSetId: '',
                capacity: null,
                occupiedSlots: 0,
                trackedSince: '2026-06-12T09:14:00Z',
                syncFailureReason: 'no_active_emote_set',
                lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
              },
        ),
      }),
    );

    await page.goto('/channels/sensitron/usage-stats');
    await expect(
      page.getByText('Dieser Channel hat auf 7TV kein aktives Emote-Set.'),
    ).toBeVisible();

    // The recheck's own comment justifies the 60 s cadence — prove the lower bound, not just that
    // it eventually fires: nothing may ask again inside the first 59 s, only load()'s own initial
    // read landed by now.
    const afterInitialLoad = activeSetRequests.count;
    await page.clock.runFor(59_000);
    // A fired timer still has to cross into a real browser request event before the listener above
    // sees it — give that a beat of genuine wall-clock time so a false negative here (asserting
    // "nothing happened" before anything that did happen had a chance to be observed) cannot pass.
    await page.waitForTimeout(200);
    expect(activeSetRequests.count).toBe(afterInitialLoad);

    // Nothing else happens on the page — no click, no reload — between flipping the mocks and the
    // grid showing up. The recheck is the only thing that can have picked this up.
    resolved = true;
    // Re-registered rather than mutated in place: Playwright tries the most-recently-added matching
    // route first, so this simply supersedes the `[]` response above for the recheck's own totals
    // fetch — mirroring the real backend, where a resolved sync is what makes the totals endpoint
    // start returning rows at all.
    await mockUsageTotals(page, 'sensitron', EMOTES);

    // Crosses the 60 s mark the recheck fires on.
    await page.clock.runFor(2_000);

    await expect(page.getByRole('heading', { name: 'Tragend', exact: true })).toBeVisible();
    await expect(page.getByText('Dieser Channel hat auf 7TV kein aktives Emote-Set.')).toHaveCount(
      0,
    );
  });

  test('adopts a changed reason without a reload', async ({ page }) => {
    await page.clock.install();

    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockUsageTotals(page, 'sensitron', []);

    const activeSetRequests = { count: 0 };
    page.on('request', (request) => {
      if (new URL(request.url()).pathname.endsWith('/emotes/active-set')) {
        activeSetRequests.count += 1;
      }
    });

    let reason: 'no_active_emote_set' | 'no_seventv_account' = 'no_active_emote_set';
    await page.route('**/api/channels/sensitron/emotes/active-set', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          activeEmoteSetId: '',
          capacity: null,
          occupiedSlots: 0,
          trackedSince: '2026-06-12T09:14:00Z',
          syncFailureReason: reason,
          lastSyncAttemptAtUtc: '2026-08-29T12:00:00Z',
        }),
      }),
    );

    await page.goto('/channels/sensitron/usage-stats');
    await expect(
      page.getByText('Dieser Channel hat auf 7TV kein aktives Emote-Set.'),
    ).toBeVisible();

    // Same lower-bound proof as the case above: no second read inside the first 59 s.
    const afterInitialLoad = activeSetRequests.count;
    await page.clock.runFor(59_000);
    await page.waitForTimeout(200);
    expect(activeSetRequests.count).toBe(afterInitialLoad);

    // Still no set id — just a different cause. The sentence has to change under the same "no
    // grid" state, not merely disappear.
    reason = 'no_seventv_account';

    // Crosses the 60 s mark the recheck fires on.
    await page.clock.runFor(2_000);

    await expect(page.getByText('Für diesen Twitch-Channel gibt es kein 7TV-Konto.')).toBeVisible();
    await expect(page.getByText('Dieser Channel hat auf 7TV kein aktives Emote-Set.')).toHaveCount(
      0,
    );
  });
});

/**
 * The wait for the very first 7TV sync used to be a two-second poll with fifteen attempts — up to
 * fifteen `active-set` reads per page visit, the single largest client amplifier issue #33 measured
 * (baseline flow (e)). The completion signal is now the `channel.synced` event the page already
 * subscribes to; the probes below exist only as a fallback for a lost event, and there are at most
 * three of them.
 *
 * Both cases drive the clock with Playwright's `page.clock` rather than waiting real seconds — the
 * same technique the failure-reason recheck cases above use for their own 60 s cadence, for the
 * same reason: real waits of that length would make this one spec run minutes longer than the rest
 * of the suite. `install()` lets timers run normally while the page boots — zoneless change
 * detection races `setTimeout` against `requestAnimationFrame`, both of which the fake clock owns,
 * so a clock that were paused during boot would render nothing at all — and only the explicit
 * `runFor()` calls jump ahead afterwards.
 */
test.describe('waiting for the first 7TV sync', () => {
  /** No set id *and* no reason: the one state awaitSync waits on (see load() in usage-stats-page). */
  const PENDING_SET = {
    activeEmoteSetId: '',
    capacity: null,
    occupiedSlots: 0,
    trackedSince: '2026-06-12T09:14:00Z',
    syncFailureReason: null,
    lastSyncAttemptAtUtc: null,
  };

  /** Opens the workspace on a channel whose 7TV set has not been resolved yet, counting every
   *  `active-set` read the page makes from the very first one. */
  async function openPendingSync(page: Page): Promise<{ activeSet: number }> {
    const counter = { activeSet: 0 };
    page.on('request', (request) => {
      if (new URL(request.url()).pathname.endsWith('/emotes/active-set')) {
        counter.activeSet += 1;
      }
    });

    await page.clock.install();
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    // Empty on purpose: loadTotals runs independently of the set status, so a set of emotes here
    // would render the sheet straight away and the awaiting branch would never show.
    await mockUsageTotals(page, 'sensitron', []);
    await page.route('**/api/channels/sensitron/emotes/active-set', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(PENDING_SET),
      }),
    );

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByText('Emote-Set wird geladen')).toBeVisible();
    return counter;
  }

  test('probes at most three times while no sync event arrives', async ({ page }) => {
    const requests = await openPendingSync(page);

    // The live stub stays silent for the whole span: nothing but the fallback probes can ask.
    await page.clock.runFor(35_000);

    // At least one probe has to have gone out — otherwise this would pass on a page that gave up
    // immediately, which is not the behaviour under test.
    await expect.poll(() => requests.activeSet).toBeGreaterThan(1);
    // Let anything the fake 35 s put on the wire actually reach the recorder before counting.
    await page.waitForTimeout(500);
    // One initial read from load(), then at most three fallback probes — four in total today, and
    // the ceiling rather than the exact number because how the three are staggered is free.
    expect(requests.activeSet).toBeLessThanOrEqual(4);

    // The banner ends with the last probe rather than hanging around: an unbounded wait would be
    // just as wrong as the old burst.
    await expect(page.getByText('Emote-Set wird geladen')).toHaveCount(0);
  });

  test('stops probing as soon as channel.synced arrives', async ({ page }) => {
    const requests = await openPendingSync(page);

    await emitLive(page, { type: 'channel.synced', channel: 'sensitron' });
    // liveReload collapses a burst over CHANNEL_RELOAD_DEBOUNCE_MS (1 s) before it hands over.
    await page.clock.runFor(1_500);

    // The event ended the wait — not a probe: `active-set` still answers "no set, no reason", so
    // nothing a probe could see would clear this banner.
    await expect(page.getByText('Emote-Set wird geladen')).toHaveCount(0);

    const afterEvent = requests.activeSet;
    await page.clock.runFor(60_000);
    await page.waitForTimeout(500);
    expect(requests.activeSet).toBe(afterEvent);
  });
});

/**
 * The set view (#200, K4, T4.6): the header dropdown that switches which of the channel's sets the
 * page shows, the URL round trip spec 8.1 asks for, and the non-active view's row classes (8.2) —
 * a class-3 "no counts under this set" group, a `'left'` row and a #74 duplicate cell, all unionned
 * from `/totals` and the set's live 7TV membership (`mergeSetView`, mocked here via
 * {@link mockForeignEmoteSetPreview}, the same route the K3/K2 picker tests already mock).
 *
 * `mockUsageTotals` answers every request for a channel identically regardless of `emoteSetId`
 * (existing contract, unchanged) — the switch/URL tests below need two DIFFERENT answers for the
 * same channel, so they register their own `/totals` route keyed on the query parameter instead of
 * extending that shared helper's default behaviour.
 */
test.describe('set view (#200, K4)', () => {
  const CHANNEL = 'sensitron';
  const ACTIVE_SET_ID = 'set-1';
  const HALLOWEEN_SET_ID = 'set-2';
  const PERSONAL_SET_ID = 'set-personal';

  /** Answers `/usage-stats/totals` with a different fixture per `emoteSetId` — needed only by the
   *  switch/URL tests, which is why this stays local rather than joining `support/mocks.ts`: every
   *  other caller of `mockUsageTotals` is fine with one fixture for the whole channel. */
  async function mockUsageTotalsBySet(
    page: Page,
    channelName: string,
    totalsBySet: Record<string, MockEmoteUsage[]>,
  ): Promise<void> {
    await page.route(`**/api/channels/${channelName}/usage-stats/totals**`, (route) => {
      const emoteSetId = new URL(route.request().url()).searchParams.get('emoteSetId') ?? '';
      const emotes = totalsBySet[emoteSetId] ?? [];
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(
          emotes.map((emote) => ({
            ...emote,
            lastUsedDate: emote.lastUsedDate ?? null,
            previousWindowUseCount: emote.previousWindowUseCount ?? 0,
            firstSeenAt: emote.firstSeenAt ?? null,
            isArchived: false,
            nameTwinEmoteSetIds: [],
          })),
        ),
      });
    });
  }

  async function mockSetViewChannel(
    page: Page,
    sets: { id: string; name: string; kind?: string }[] = [
      { id: ACTIVE_SET_ID, name: 'Hauptset' },
      { id: HALLOWEEN_SET_ID, name: 'Halloween' },
    ],
  ): Promise<void> {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockMyChannels(page, [
      { channelName: CHANNEL, isBroadcaster: true, isTracked: true, isBotActive: true },
    ]);
    await mockChannelPermissions(page, CHANNEL);
    await mockChannelStatus(page, CHANNEL);
    await mockActiveEmoteSet(page, CHANNEL, ACTIVE_SET_ID, { capacity: 1000, occupiedSlots: 10 });
    await mockChannelEmoteSetList(page, CHANNEL, { activeEmoteSetId: ACTIVE_SET_ID, sets });
  }

  async function gotoSetView(page: Page, query = ''): Promise<void> {
    await page.goto(`/channels/${CHANNEL}/usage-stats${query}`);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
  }

  const setMenuTrigger = (page: Page) => page.getByRole('button', { name: /^Set:/ });

  test('the dropdown offers the channel’s NORMAL sets with the active one preselected, and hides every other kind (AK 50, spec §35)', async ({
    page,
  }) => {
    await mockSetViewChannel(page, [
      { id: ACTIVE_SET_ID, name: 'Hauptset' },
      { id: HALLOWEEN_SET_ID, name: 'Halloween' },
      { id: PERSONAL_SET_ID, name: 'Mein Set', kind: 'PERSONAL' },
    ]);
    await mockUsageTotals(page, CHANNEL, []);

    await gotoSetView(page);

    await expect(setMenuTrigger(page)).toHaveAccessibleName(/^Set: Hauptset \(aktiv\)/);
    await setMenuTrigger(page).click();

    const menu = page.getByRole('radiogroup', { name: 'Set wählen' });
    await expect(menu.getByRole('radio')).toHaveCount(2);
    await expect(menu.getByRole('radio', { name: 'Hauptset (aktiv)' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    await expect(menu.getByRole('radio', { name: 'Halloween' })).toBeVisible();
    // §35: not "disabled with a label", gone entirely.
    await expect(menu.getByRole('radio', { name: /Mein Set/ })).toHaveCount(0);
  });

  test('opening the set menu does not shift the sort controls next to it in the header (no-layout-jump contract, docs/UI-Designsprache.md)', async ({
    page,
  }) => {
    // Regression for a real bug: the anchor `div` around the trigger and the popover used to be a
    // `flex ... gap-x-2` row that had the popover itself as a flex child. The popover's panel is
    // `position: absolute` and paints nothing there, but its host element still claimed a flex slot
    // and the gap next to it — widening EmoteSetMenu by one `gap-x-2` every time it opened and
    // shoving every header control after it sideways. A geometry assertion is the only thing that
    // actually pins this down; anything checking markup or classes would miss a regression that
    // reintroduces the same box model by a different route.
    await mockSetViewChannel(page);
    await mockUsageTotals(page, CHANNEL, []);

    await gotoSetView(page);

    const sortKeyGroup = page.getByRole('radiogroup', { name: 'Sortieren nach' });
    const sortDirGroup = page.getByRole('radiogroup', { name: 'Reihenfolge' });
    const before = {
      sortKey: await sortKeyGroup.boundingBox(),
      sortDir: await sortDirGroup.boundingBox(),
    };
    expect(before.sortKey).not.toBeNull();
    expect(before.sortDir).not.toBeNull();

    await setMenuTrigger(page).click();
    await expect(page.getByRole('radiogroup', { name: 'Set wählen' })).toBeVisible();

    const after = {
      sortKey: await sortKeyGroup.boundingBox(),
      sortDir: await sortDirGroup.boundingBox(),
    };
    expect(after.sortKey).not.toBeNull();
    expect(after.sortDir).not.toBeNull();

    // toBeCloseTo(value, 0) accepts a difference below 0.5 — exactly the ±0.5px tolerance the
    // no-layout-jump contract asks for, without being so tight that sub-pixel rounding flakes it.
    for (const key of ['x', 'y', 'width', 'height'] as const) {
      expect(after.sortKey![key]).toBeCloseTo(before.sortKey![key], 0);
      expect(after.sortDir![key]).toBeCloseTo(before.sortDir![key], 0);
    }
  });

  test('choosing a non-active set writes it into the URL and swaps the grid; a reload restores it; choosing the active set removes the param again (AK 50-52, spec 8.1)', async ({
    page,
  }) => {
    await mockSetViewChannel(page);
    await mockUsageTotalsBySet(page, CHANNEL, {
      [ACTIVE_SET_ID]: [
        {
          emoteId: 'e-cat',
          emoteName: 'CatJAM',
          sevenTvEmoteId: '7tv-cat',
          imageUrl: 'https://cdn.7tv.app/emote/1/2x.webp',
          totalUseCount: 500,
        },
      ],
      [HALLOWEEN_SET_ID]: [
        {
          emoteId: 'e-spooky',
          emoteName: 'Spooky',
          sevenTvEmoteId: '7tv-spooky',
          imageUrl: 'https://cdn.7tv.app/emote/2/2x.webp',
          totalUseCount: 12,
        },
      ],
    });
    // The Halloween set's own live membership matches the totals row above 1:1, so it renders as a
    // plain 'live' row (spec 7.1) rather than 'left' — the switch itself is what this test is about.
    await mockForeignEmoteSetPreview(page, CHANNEL, {
      channelName: CHANNEL,
      emoteSetId: HALLOWEEN_SET_ID,
      emoteSetName: 'Halloween',
      capacity: 500,
      totalCount: 1,
      emotes: [{ sevenTvEmoteId: '7tv-spooky', name: 'Spooky' }],
    });

    await gotoSetView(page);
    await expect(page.getByRole('button', { name: /^CatJAM ·/ })).toBeVisible();

    await setMenuTrigger(page).click();
    await page.getByRole('radio', { name: 'Halloween' }).click();

    await expect(page).toHaveURL(new RegExp(`[?&]emoteSetId=${HALLOWEEN_SET_ID}\\b`));
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Spooky ·/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /^CatJAM ·/ })).toHaveCount(0);

    // Reload: the URL, not local state, is what carries the choice, so a full reload must restore
    // it rather than snap back to the active set.
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    await expect(setMenuTrigger(page)).toHaveAccessibleName('Set: Halloween');
    await expect(page.getByRole('button', { name: /^Spooky ·/ })).toBeVisible();

    // Choosing the active set again removes the parameter instead of spelling out its id (comment
    // on `onEmoteSetSelected`).
    await setMenuTrigger(page).click();
    await page.getByRole('radio', { name: 'Hauptset (aktiv)' }).click();
    await expect(page).not.toHaveURL(/emoteSetId=/);
    await expect(page.getByRole('button', { name: /^CatJAM ·/ })).toBeVisible();
  });

  test('an emoteSetId in the URL that the set list does not confirm — unknown, or a hidden kind — falls back to the active set and is removed from the URL (spec 8.1, §35)', async ({
    page,
  }) => {
    await mockSetViewChannel(page, [
      { id: ACTIVE_SET_ID, name: 'Hauptset' },
      { id: PERSONAL_SET_ID, name: 'Mein Set', kind: 'PERSONAL' },
    ]);
    await mockUsageTotals(page, CHANNEL, []);

    await gotoSetView(page, '?emoteSetId=does-not-exist');
    await expect(setMenuTrigger(page)).toHaveAccessibleName(/^Set: Hauptset \(aktiv\)/);
    await expect(page).not.toHaveURL(/emoteSetId=/);

    // A hidden (non-NORMAL) set's id is deliberately treated identically to an unknown one (§35).
    await gotoSetView(page, `?emoteSetId=${PERSONAL_SET_ID}`);
    await expect(setMenuTrigger(page)).toHaveAccessibleName(/^Set: Hauptset \(aktiv\)/);
    await expect(page).not.toHaveURL(/emoteSetId=/);
  });

  test('the non-active view groups a countless live member on its own, badges a left-behind row, folds a #74 duplicate into one cell, and offers both deleting (K5/T5.3) and voting (K6) (spec 8.2, AK 54-58, 62, 73; no NG0955)', async ({
    page,
  }) => {
    const consoleProblems: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' || message.type() === 'warning') {
        consoleProblems.push(`${message.type()}: ${message.text()}`);
      }
    });
    page.on('pageerror', (error) => consoleProblems.push(`pageerror: ${error.message}`));

    // The app shell asks for the legal-page availability on every page; unanswered, the dev
    // proxy's 502 lands in the console this test asserts to be clean.
    await mockLegalAvailability(page);
    await mockSetViewChannel(page);
    await mockUsageTotalsBySet(page, CHANNEL, {
      [ACTIVE_SET_ID]: [],
      [HALLOWEEN_SET_ID]: [
        {
          emoteId: 'e-ghost',
          emoteName: 'GhostA',
          sevenTvEmoteId: '7tv-ghost',
          imageUrl: 'https://cdn.7tv.app/emote/3/2x.webp',
          totalUseCount: 50,
        },
        {
          // Counted here, but absent from the live list below — 'left' (E23).
          emoteId: 'e-gone',
          emoteName: 'GoneEmote',
          sevenTvEmoteId: '7tv-gone',
          imageUrl: 'https://cdn.7tv.app/emote/4/2x.webp',
          totalUseCount: 20,
        },
        {
          // A #74 duplicate: one totals row, two live entries under the same 7TV id below.
          emoteId: 'e-dup',
          emoteName: 'AliasOne',
          sevenTvEmoteId: '7tv-dup',
          imageUrl: 'https://cdn.7tv.app/emote/5/2x.webp',
          totalUseCount: 15,
        },
      ],
    });
    await mockForeignEmoteSetPreview(page, CHANNEL, {
      channelName: CHANNEL,
      emoteSetId: HALLOWEEN_SET_ID,
      emoteSetName: 'Halloween',
      capacity: 500,
      totalCount: 5,
      emotes: [
        { sevenTvEmoteId: '7tv-ghost', name: 'GhostA' },
        { sevenTvEmoteId: '7tv-dup', name: 'AliasOne' },
        { sevenTvEmoteId: '7tv-dup', name: 'AliasTwo' },
        // Two DISTINCT class-3 rows (live, no counted row at all) — the exact shape AK 55 guards:
        // keyed by Emote.Id (always null here) the two would collide (NG0955); keyed by
        // sevenTvEmoteId (spec 7.2) they are two cells.
        { sevenTvEmoteId: '7tv-noc-1', name: 'NoCountsOne' },
        { sevenTvEmoteId: '7tv-noc-2', name: 'NoCountsTwo' },
      ],
    });
    // Unmocked, /series would 502 through the dev proxy (no Api on :5151 in this suite) and log a
    // browser console error unrelated to the thing this test actually checks — mocked purely to
    // keep the console clean for the NG0955 assertion below.
    await mockUsageChannelSeries(page, CHANNEL, {});
    // The live members above carry made-up 7TV ids, and the atlas builds their sprite URLs from
    // those ids — unstubbed, the browser really fetches https://cdn.7tv.app/emote/7tv-ghost/2x.webp,
    // 7TV answers 400 for an id that is not an ObjectID, and Chromium logs "Failed to load
    // resource: … 400". Whether that line lands before or after the assertion below depended on
    // the CDN's round trip, which made this test flaky. Stubbing the CDN keeps it hermetic.
    await page.route('https://cdn.7tv.app/**', (route) =>
      route.fulfill({ status: 200, contentType: 'image/png', body: PNG_1X1 }),
    );

    // Deep link straight into the non-active view — the switch itself is the previous test's job.
    await gotoSetView(page, `?emoteSetId=${HALLOWEEN_SET_ID}`);

    // Klasse 3, own trailing group (AK 56).
    await expect(page.getByRole('heading', { name: 'Keine Zählungen', exact: true })).toBeVisible();
    await expect(page.getByText('unter diesem Set')).toBeVisible();
    await expect(
      page.getByRole('button', { name: /^NoCountsOne · keine Zählungen unter diesem Set/ }),
    ).toBeVisible();
    await expect(
      page.getByRole('button', { name: /^NoCountsTwo · keine Zählungen unter diesem Set/ }),
    ).toBeVisible();

    // 'left' row: badge, still counted (AK 57) — no NG0955 assertion needed here, that is the
    // console check at the end of this test.
    await expect(
      page.getByRole('button', { name: /^GoneEmote ·.*Nicht mehr im Set/ }),
    ).toBeVisible();

    // #74 duplicate: one cell, slot count 2, both aliases (AK 58).
    await expect(
      page.getByRole('button', {
        name: /^AliasOne ·.*2 Plätze im Set: AliasOne, AliasTwo/,
      }),
    ).toBeVisible();

    // Mark a plain live row: in a settled non-active view with a readable member list, deleting is
    // unlocked since K5/T5.3 (spec 8.8 — the run is set-aware and the confirmation names the set)
    // and voting since K6 (spec 9 — it creates a set-session over the shown set, whose ballot the
    // server checks against the live 7TV membership at submit time). No lock, so no reason text.
    await page.getByRole('button', { name: /^GhostA ·/ }).click();
    const deleteButton = page.getByRole('button', { name: 'Löschen (1)' });
    await expect(deleteButton).toBeVisible();
    await expect(deleteButton).toBeEnabled();

    const voteButton = page.getByRole('button', { name: 'Zur Abstimmung stellen (1)' });
    await expect(voteButton).toBeVisible();
    await expect(voteButton).toBeEnabled();
    await expect(voteButton).not.toHaveAttribute('aria-describedby');

    expect(consoleProblems, consoleProblems.join('\n')).toEqual([]);
  });

  test('a set switch whose rows fail to load shows the error in place of the sheet — no endless skeleton — and retrying recovers (K4 fix round)', async ({
    page,
  }) => {
    await mockSetViewChannel(page);
    await mockUsageChannelSeries(page, CHANNEL, {});
    let halloweenAttempts = 0;
    await page.route(`**/api/channels/${CHANNEL}/usage-stats/totals**`, (route) => {
      const emoteSetId = new URL(route.request().url()).searchParams.get('emoteSetId');
      if (emoteSetId === HALLOWEEN_SET_ID && ++halloweenAttempts === 1) {
        return route.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
      }
      const row =
        emoteSetId === HALLOWEEN_SET_ID
          ? { emoteId: 'e-spooky', emoteName: 'Spooky', sevenTvEmoteId: '7tv-spooky' }
          : { emoteId: 'e-cat', emoteName: 'CatJAM', sevenTvEmoteId: '7tv-cat' };
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          {
            ...row,
            imageUrl: 'https://cdn.7tv.app/emote/1/2x.webp',
            totalUseCount: 40,
            lastUsedDate: null,
            previousWindowUseCount: 0,
            firstSeenAt: null,
            isArchived: false,
            nameTwinEmoteSetIds: [],
          },
        ]),
      });
    });
    await mockForeignEmoteSetPreview(page, CHANNEL, {
      channelName: CHANNEL,
      emoteSetId: HALLOWEEN_SET_ID,
      emoteSetName: 'Halloween',
      capacity: 500,
      totalCount: 1,
      emotes: [{ sevenTvEmoteId: '7tv-spooky', name: 'Spooky' }],
    });

    await gotoSetView(page);
    await expect(page.getByRole('button', { name: /^CatJAM ·/ })).toBeVisible();

    await setMenuTrigger(page).click();
    await page.getByRole('radio', { name: 'Halloween' }).click();

    // Settled into the error state: no skeleton left, the previous set's rows gone from the sheet,
    // and both ways forward usable.
    await expect(page.getByText('Das gewählte Set konnte nicht geladen werden')).toBeVisible();
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^CatJAM ·/ })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Aktualisieren' })).toBeEnabled();

    await page.getByRole('button', { name: 'Erneut versuchen' }).click();
    await expect(page.getByRole('button', { name: /^Spooky ·/ })).toBeVisible();
    await expect(page.getByText('Das gewählte Set konnte nicht geladen werden')).toHaveCount(0);
  });
});
