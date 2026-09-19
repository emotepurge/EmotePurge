import { expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockEmoteUsage,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockUsageChannelSeries,
  mockUsageDaily,
  mockUsageTotals,
  mockVoteSessionList,
  mockVoteSessionResults,
  mockWorkerHealth,
} from './support/mocks';

// The single emote the atlas needs to render a cell at data-atlas-index="0" — nothing here asserts
// on usage numbers, so one row is enough.
const TOUCH_EMOTE: MockEmoteUsage = {
  emoteId: 'e1',
  emoteName: 'PogU',
  sevenTvEmoteId: '7tv-1',
  imageUrl: 'https://cdn.7tv.app/emote/stub/1x.webp',
  totalUseCount: 23,
};

// The contract this file exists for: no 7TV write access without a mouse. The token can only be read
// out of DevTools' local-storage view on 7tv.app, which a phone does not have — so selection, mass
// delete and protocol re-import are desktop work, and the tap on a cell means one thing.
test.describe('touch: reading and voting only', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
    await mockChannelPermissions(page, 'sensitron');
    await mockChannelStatus(page, 'sensitron');
    await mockActiveEmoteSet(page, 'sensitron');
    await mockUsageChannelSeries(page, 'sensitron', {});
    await mockUsageDaily(page, 'sensitron', [
      { date: '2026-07-02', useCount: 4 },
      { date: '2026-07-05', useCount: 19 },
    ]);
  });

  test('tapping a cell opens the drilldown instead of selecting it', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');

    const cell = page.locator('[data-atlas-index="0"]');
    await expect(cell).toBeVisible();
    // The tell that the cell is no longer a toggle: on a mouse it carries aria-pressed.
    await expect(cell).not.toHaveAttribute('aria-pressed', /.*/);
    await expect(cell).toHaveAttribute('aria-haspopup', 'dialog');
    // The count has to stay in the label: the inspector row that states it for a fine pointer is
    // `pointer-coarse:hidden`, so this is the only place a screen reader can read it without
    // opening the dialog.
    await expect(cell).toHaveAttribute('aria-label', 'Details zu PogU anzeigen (23×)');

    await cell.tap();

    await expect(page.locator('#app-dialog-title')).toBeVisible();
  });

  test('the restore panel never appears, and marking cannot happen to raise the dock either', async ({
    page,
  }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#app-dialog-title')).toHaveCount(0);

    // The restore panel's only gate is `!isCoarse()` (usage-stats-page.html) — nothing else stands
    // between it and rendering, so this assertion is load-bearing: drop that guard and "Protokoll
    // importieren" appears on a plain page load, no tap required.
    await expect(page.getByRole('button', { name: /Protokoll/ })).toHaveCount(0);
    // The dock's guard is `dockVisible() && !isCoarse()`, and dockVisible() needs a selection that a
    // coarse tap can never produce (onCellClick's early return, covered by the first test above) —
    // so on its own this assertion cannot fail from that guard being dropped, only from the two-fault
    // case the first test already rules out. Kept as belt-and-braces documentation of the contract,
    // not as this case's proof.
    await expect(page.getByRole('button', { name: /Löschen/ })).toHaveCount(0);
  });

  test('the drilldown arrives as a sheet docked to the bottom edge', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();

    // Pins one half of the DialogShell/SheetDrag coupling: that a real dialog on a real coarse
    // pointer renders `data-sheet-handle` at all, under exactly that name. That is `dialog-shell.ts`
    // alone — a rename of the literal inside `SheetDrag.onPointerDown` (the other half, which reads
    // it to decide whether a drag may start) would still slip past this, and only the directive's
    // own spec stands under that. The geometry checks below say nothing about either: they come from
    // a `styles.css` media query and hold with no handle in the DOM at all.
    //
    // Two of them: the grab bar and the heading block. The heading is a handle because a drag that
    // starts there means the sheet, the way it does in a native one.
    const handles = page.locator('.app-dialog-panel [data-sheet-handle]');
    await expect(handles).toHaveCount(2);

    const grab = (await handles.first().boundingBox())!;
    const heading = (await handles.last().boundingBox())!;

    // The measurement the phone failed on. The bar itself is 4 px; what a thumb has to hit is the
    // strip around it, and at the bar's own padding that came to 24 px — technically a target
    // (WCAG 2.5.8), nowhere near the 44-px comfort target the design language sets for a primary
    // one. Asserted on the rendered box, so a padding change that silently shrinks it fails here.
    expect(grab.height).toBeGreaterThanOrEqual(44);

    // And they have to touch. Two drag zones with a strip of hull between them is worse than one
    // small zone, because the dead strip sits exactly where a thumb aims — between the bar it can
    // see and the heading it can read — and a gesture that starts there does nothing a slow pull can
    // finish. The hull's own `gap-4` used to be that strip; the heading cancels it and re-adds it as
    // padding it owns, so the sheet's whole top is continuous.
    expect(heading.y).toBeLessThanOrEqual(grab.y + grab.height + 1);

    const pane = page.locator('.cdk-overlay-pane.app-dialog-panel');
    const paneBox = (await pane.boundingBox())!;
    const viewport = page.viewportSize()!;

    // Flush with the bottom, full width — the centred card is neither.
    expect(paneBox.y + paneBox.height).toBeGreaterThanOrEqual(viewport.height - 1);
    expect(paneBox.width).toBe(viewport.width);
  });

  test('the sheet closes when the backdrop is tapped', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');
    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();

    await page.locator('.app-dialog-backdrop').tap({ position: { x: 10, y: 10 } });

    await expect(page.locator('#app-dialog-title')).toHaveCount(0);
  });

  // The regression net under SheetDrag's press/drag split. The directive is on the sheet's hull, so
  // every control inside the sheet sits underneath it; taking pointer capture on `pointerdown` — as
  // it used to — retargets the following `click` to the hull and the control never fires.
  //
  // Both halves are here on purpose, because only one of them ever saw the defect: with a
  // touch-type pointer the click lands anyway, so `tap()` passed throughout. `click()` drives a
  // MOUSE-type pointer into a context where `(pointer: coarse)` still matches — which is not an
  // exotic case but the way this branch gets tested by hand (DevTools → Rendering → Emulate CSS
  // media feature `pointer: coarse`, named in PointerModeService's own doc comment). Before the
  // split, that second half left every button in every dialog dead.
  test('a control inside the sheet still works — tapped and clicked', async ({ page }) => {
    await mockUsageTotals(page, 'sensitron', [TOUCH_EMOTE]);
    await page.goto('/channels/sensitron/usage-stats');

    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();
    await page.getByRole('button', { name: 'Schließen' }).tap();
    await expect(page.locator('#app-dialog-title')).toHaveCount(0);

    await page.locator('[data-atlas-index="0"]').tap();
    await expect(page.locator('#app-dialog-title')).toBeVisible();
    await page.getByRole('button', { name: 'Schließen' }).click();
    await expect(page.locator('#app-dialog-title')).toHaveCount(0);
  });

  // The ballot is the other half of the coarse surface and had no permanent coverage at all: two
  // tasks rebuilt what a tap on its sprite means (cellAction()) and took its delete engine away,
  // and nothing committed reached the page.
  test('the ballot sprite opens the drilldown and the ballot has no delete engine', async ({
    page,
  }) => {
    await mockVoteSessionResults(page, 'sensitron', { id: 7, title: 'Aufräumen im August' }, [
      { emoteId: 'e1', emoteName: 'catJAM' },
    ]);
    await page.goto('/channels/sensitron/vote-sessions/7');
    await expect(page.getByRole('heading', { name: 'Aufräumen im August' })).toBeVisible();

    const sprite = page.getByRole('button', { name: 'Details zu catJAM anzeigen' });
    // The tell that the sprite is no longer a selection toggle — on a mouse it carries aria-pressed.
    await expect(sprite).not.toHaveAttribute('aria-pressed', /.*/);
    await expect(sprite).toHaveAttribute('aria-haspopup', 'dialog');
    // Same reasoning as the atlas cell: the ballot's readout row is `pointer-coarse:hidden` too, so
    // the label is the only place a screen reader reads the count here.
    await expect(sprite).toHaveAttribute('aria-label', 'Details zu catJAM anzeigen (10×)');

    // Asserted BEFORE anything is opened, and on the element rather than on its role: the CDK
    // aria-hides the whole background while a modal is up, so a role query run after the tap would
    // report zero for every button on the page and prove nothing. The panel's gate is `!isCoarse()`
    // — everything else about it (a manager, a loaded set id) is true in this fixture, so dropping
    // that clause really does bring it back and really does fail here.
    await expect(page.locator('app-mass-delete-panel')).toHaveCount(0);

    await sprite.tap();

    await expect(page.locator('#app-dialog-title')).toHaveText('catJAM');
  });

  // The create entry point in the page header survives on a coarse pointer only as a sentence, not
  // as the link: creating a session writes to our own database, not to 7TV, so the contract this
  // file guards does not reach it directly — but the destination it would link to (the usage-stats
  // atlas's marking + dock) is itself `!isCoarse()`-gated, so a mod on a phone has nowhere to land.
  // The page stays reachable, so this is not broken navigation but a question its destination
  // cannot answer here (docs/UI-Designsprache.md §2.5/§8.7, vote-session-list-page.html's header).
  //
  // The permanent create-entry hint in the header (moved out of the empty state, 2026-09-19
  // correction, since it needs to survive a non-empty list too) used to double this note for the
  // same manager on the same pointer — that was the defect, not a second, independent concern: a
  // manager on coarse would read the header say "create on desktop" and then, one line down, "mark
  // emotes on the usage page", which is exactly as unreachable there as the link above it. The hint
  // is fine-pointer-only for the same reason the header note exists at all, so this case now
  // asserts it does *not* render on coarse, not that it does.
  //
  // The fixture below seeds one session on purpose: the hint's whole load-bearing case is that it
  // survives a non-empty list (it used to live only inside the empty state), so a fixture that never
  // leaves that state would let this case pass against either placement. The wording assertion also
  // takes `{ exact: true }` — the shortened one-sentence hint is a plain string prefix of the old
  // two-sentence `noSessionsManagerHint` it replaced, so a substring match here would stay green
  // against the retired copy too.
  test('the header entry point trades the create link for a desktop-only note on a coarse pointer', async ({
    page,
  }) => {
    await mockVoteSessionList(page, 'sensitron', [{ id: 1, title: 'Aufräumen im August' }]);
    await page.goto('/channels/sensitron/vote-sessions');

    await expect(page.getByRole('heading', { name: 'Votings' })).toBeVisible();
    // Counted before it is asserted hidden: toBeHidden() also passes for an element that is not in
    // the DOM at all, so on its own it would greenlight the link being deleted outright. The pair
    // says what is meant — still rendered, and `pointer-coarse:hidden` is what takes it out of
    // sight and out of the accessibility tree.
    const entryLink = page.locator(
      'a[href="/channels/sensitron/usage-stats"].pointer-coarse\\:hidden',
    );
    await expect(entryLink).toHaveCount(1);
    await expect(entryLink).toHaveText('Emotes zur Abstimmung stellen');
    await expect(entryLink).toBeHidden();
    // The replacement is the reverse pair (`hidden pointer-coarse:inline`); asserting the literal
    // German also fails if the key is missing from de.json, since Transloco renders the key path.
    await expect(
      page.getByText('Abstimmungen erstellst du am Rechner, auf der Nutzungsseite.'),
    ).toBeVisible();

    // Same pair as the header link above (rendered, then asserted hidden): the permanent
    // create-entry hint must not repeat the header's note to a reader who cannot act on it either
    // way. Its own sentence is shorter than it used to be — it no longer names the destination
    // either (the button right above it already does) or an order relative to it, only the
    // precondition of marking.
    const createEntryHint = page.getByText(
      'Markiere die Emotes, über die abgestimmt werden soll.',
      { exact: true },
    );
    await expect(createEntryHint).toHaveCount(1);
    await expect(createEntryHint).toBeHidden();
  });
});
