import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  MockVoteSessionEmote,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelEmoteSetList,
  mockChannelPermissions,
  mockChannelStatus,
  mockForeignEmoteSetPreview,
  mockUsageChannelSeries,
  mockUsageTotals,
  mockVoteSessionResults,
  mockWorkerHealth,
} from './support/mocks';

/**
 * The ballot had no browser coverage at all before its cell was rebuilt (2026-08-06), which is the
 * worst possible combination: the one surface the community actually touches, and the one whose
 * interaction model just changed. The card became a sprite with a two-button verdict strip, and
 * "which half did I press, and did it register" is not a thing a unit test can see.
 */

const SESSION = { id: 7, title: 'Aufräumen im August' };

async function openBallot(page: Page, emotes: MockVoteSessionEmote[], overrides = {}) {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page);
  await installLiveStub(page);
  await mockChannelPermissions(page, 'sensitron');
  await mockChannelStatus(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron');
  await mockVoteSessionResults(page, 'sensitron', { ...SESSION, ...overrides }, emotes);

  await page.goto(`/channels/sensitron/vote-sessions/${SESSION.id}`);
  await expect(page.getByRole('heading', { name: SESSION.title })).toBeVisible();
}

const keepButton = (page: Page, index = 0) =>
  page.getByRole('button', { name: 'Behalten', exact: true }).nth(index);
const deleteButton = (page: Page, index = 0) =>
  page.getByRole('button', { name: 'Löschen vorschlagen', exact: true }).nth(index);

test.describe('vote ballot', () => {
  // Issue #33 baseline flow (c): the guard's own authorization probe and the page's first
  // loadResults() used to each fetch /results independently, 582 ms apart. The guard now hands its
  // response to the page (VoteSessionService.stashGuardResults/takeGuardResults) instead.
  test('entering the page issues exactly one /results request', async ({ page }) => {
    let resultsRequests = 0;
    page.on('request', (request) => {
      if (new URL(request.url()).pathname.endsWith(`/vote-sessions/${SESSION.id}/results`)) {
        resultsRequests += 1;
      }
    });

    await openBallot(page, [{ emoteId: 'e1', emoteName: 'catJAM' }]);

    expect(resultsRequests).toBe(1);
  });

  test('casts a keep vote and shows it as pressed', async ({ page }) => {
    let voted: { emoteId: string; type: number } | null = null;
    await page.route('**/vote-sessions/7/votes', async (route) => {
      voted = route.request().postDataJSON() as { emoteId: string; type: number };
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });
    await openBallot(page, [
      { emoteId: 'e1', emoteName: 'catJAM', keepVotes: 2, deleteVotes: 1 },
      { emoteId: 'e2', emoteName: 'Sadge', keepVotes: 0, deleteVotes: 4 },
    ]);

    await keepButton(page).click();

    // Poll rather than a bare expect(): click() resolves once the click is delivered, while the
    // POST — and with it the route handler that records its body — happens afterwards. A plain
    // expect() on a variable does not retry the way a locator assertion does, so it read the value
    // before the request existed and failed with null under load.
    await expect.poll(() => voted).toEqual({ emoteId: 'e1', type: 1 });
  });

  test('pressing the same side again retracts instead of re-casting', async ({ page }) => {
    let method: string | null = null;
    await page.route('**/vote-sessions/7/votes/**', async (route) => {
      method = route.request().method();
      await route.fulfill({ status: 204, body: '' });
    });
    // Already voted keep: the only way back to neutral is pressing keep again, which has to be a
    // retraction and not a second cast.
    await openBallot(page, [{ emoteId: 'e1', emoteName: 'catJAM', myVote: 1 }]);

    await expect(keepButton(page)).toHaveAttribute('aria-pressed', 'true');
    await keepButton(page).click();

    expect(method).toBe('DELETE');
  });

  // External review, 2026-08-31: local vote success used to feed only the shared 500 ms SSE/reload
  // debounce (VOTE_RELOAD_DEBOUNCE_MS) — `myVote` did not change until that debounced reload ran.
  // The vote buttons unlock right after the first response (well before the debounce fires), so a
  // second click on the same button in that window used to see the pre-vote `myVote` and cast a
  // second time instead of retracting. vote() now patches `myVote` locally the moment its own
  // request succeeds, independent of the reload.
  test('a repeated click on the same button retracts, even before the reload debounce settles', async ({
    page,
  }) => {
    await page.clock.install();

    const methods: string[] = [];
    await page.route('**/vote-sessions/7/votes', async (route) => {
      methods.push(route.request().method());
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });
    await page.route('**/vote-sessions/7/votes/**', async (route) => {
      methods.push(route.request().method());
      await route.fulfill({ status: 204, body: '' });
    });
    await openBallot(page, [{ emoteId: 'e1', emoteName: 'catJAM' }]);

    await keepButton(page).click();
    await expect(keepButton(page)).toHaveAttribute('aria-pressed', 'true');

    // Well inside VOTE_RELOAD_DEBOUNCE_MS (500 ms) — this is the exact window the review flagged.
    await page.clock.runFor(100);
    await keepButton(page).click();
    await expect(keepButton(page)).toHaveAttribute('aria-pressed', 'false');

    expect(methods).toEqual(['POST', 'DELETE']);
  });

  test('an archived emote stays listed but cannot be voted on', async ({ page }) => {
    await openBallot(page, [
      { emoteId: 'e1', emoteName: 'catJAM' },
      { emoteId: 'e2', emoteName: 'Gone', isArchived: true },
    ]);

    await expect(keepButton(page, 1)).toBeDisabled();
    await expect(deleteButton(page, 1)).toBeDisabled();
    // Still votable above it — the disabling is per emote, not a page-wide freeze.
    await expect(keepButton(page, 0)).toBeEnabled();
  });

  test('a running secret ballot shows no tallies on the strip', async ({ page }) => {
    await openBallot(
      page,
      [
        {
          emoteId: 'e1',
          emoteName: 'catJAM',
          keepVotes: null,
          deleteVotes: null,
          score: null,
          totalUseCount: null,
        },
      ],
      { hideResultsUntilEnd: true },
    );

    await expect(page.getByText('werden erst nach ihrem Ende angezeigt')).toBeVisible();
    // A placeholder in the tally slot would read like a value; the number is omitted entirely.
    await expect(keepButton(page)).toHaveText('');
    await expect(deleteButton(page)).toHaveText('');
  });

  test('the readout names whatever emote the pointer is on', async ({ page }) => {
    await openBallot(page, [
      { emoteId: 'e1', emoteName: 'catJAM', totalUseCount: 42, score: 3 },
      { emoteId: 'e2', emoteName: 'Sadge', totalUseCount: 7, score: -2 },
    ]);
    const sidecar = page.getByRole('complementary');

    // Before any hover it describes the first row — the ballot's own order, which the server sorts.
    await expect(sidecar).toContainText('catJAM');

    await page.getByRole('button', { name: 'Sadge', exact: true }).hover();

    await expect(sidecar).toContainText('Sadge');
    await expect(sidecar).toContainText('-2');
  });

  test('a manager selects sprites for the purge without that touching their vote', async ({
    page,
  }) => {
    await openBallot(page, [
      { emoteId: 'e1', emoteName: 'catJAM' },
      { emoteId: 'e2', emoteName: 'Sadge' },
    ]);

    // Selecting is the sprite; voting is the strip below it. They must not bleed into each other,
    // because one of them ends in an irreversible delete on 7TV.
    await page.getByRole('button', { name: 'catJAM', exact: true }).click();

    await expect(page.getByRole('button', { name: 'Löschen (1)' })).toBeVisible();
    await expect(keepButton(page, 0)).toHaveAttribute('aria-pressed', 'false');
  });

  test('sprite DOM nodes survive the reload a vote triggers (no rebuild-on-reload regression)', async ({
    page,
  }) => {
    // Needs multiple rows, not just multiple emotes: rows() hands *cdkVirtualFor a fresh row-array
    // reference on every recompute, so without a trackBy, CdkVirtualForOf's identity differ treats
    // every row as removed-then-inserted on each reload and its recycler reuses the detached row
    // views through a view cache — with 2+ rows that can rebind a recycled row-view to a *different*
    // row's data than it last held. The inner `@for (… track emote.emoteId)` then finds unfamiliar
    // ids in a view it did not expect them in and rebuilds every cell in it — a brand-new
    // `EmoteSprite` per cell, starting hidden until its own `load` event fires. 24 emotes reliably
    // fills more than one 10-14-column desktop row (see atlasColumns/CELL_WIDE_PX). Marking only one
    // sprite is not reliable here — which particular row-views get reshuffled depends on CDK's
    // internal cache order, and only about half of a 24-item grid's rows turned out to be affected
    // when this test was built (confirmed by temporarily reverting the trackBy: 10 of 24 marked
    // sprites survived, not 0) — so every sprite gets marked and every one of them must survive.
    const emotes = Array.from({ length: 24 }, (_, i) => ({
      emoteId: `e${i}`,
      emoteName: `Emote${i}`,
    }));
    await openBallot(page, emotes);

    // Scoped to the virtual-scroll viewport, and marked with a plain DOM attribute rather than
    // asserting on the component: a freshly created <img> never carries an attribute nobody bound
    // to it, so a drop in the marked count is unambiguous proof that elements were destroyed and
    // recreated, not just that their content changed.
    const sprites = page.locator('cdk-virtual-scroll-viewport img');
    const spriteCount = await sprites.count();
    expect(spriteCount).toBe(emotes.length);
    await sprites.evaluateAll((imgs) => {
      imgs.forEach((img, i) => img.setAttribute('data-regression-probe', String(i)));
    });

    await page.route('**/vote-sessions/7/votes', async (route) => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });
    // Overrides the results route openBallot() already registered — Playwright tries the
    // most-recently-added matching handler first, so this wins for every request from here on,
    // including the one vote() fires. e0's tally is bumped to a value nothing else in this test
    // produces, purely so the assertion below has something visible to wait on.
    const votedEmotes = emotes.map((emote, i) => (i === 0 ? { ...emote, keepVotes: 999 } : emote));
    await mockVoteSessionResults(page, 'sensitron', SESSION, votedEmotes);

    await keepButton(page, 0).click();
    // Waiting on the GET's HTTP response is not enough: the response resolves before Angular has
    // applied it, and toHaveCount() below succeeds on its very first passing poll — which can land
    // in that gap, before a rebuild would even have happened, and pass for the wrong reason. This
    // waits on the reload's *rendered* effect instead (a tally only the post-vote response could
    // have produced), so everything after it is guaranteed to run against the post-reload DOM.
    await expect(keepButton(page, 0)).toContainText('999');

    await expect(page.locator('img[data-regression-probe]')).toHaveCount(spriteCount);
  });

  // Issue #33 baseline flow (d): four votes in a 14 ms window used to cost 2n+1 = 9 permits (4
  // mutations + 4 direct reloads + 1 SSE-echo reload) plus n policy-free channel-status reads.
  // vote() now feeds the same 500 ms reload pipeline the SSE echo does instead of reloading on its
  // own, so n votes should settle at n mutations + at most one reload, with the channel status
  // (loadActiveEmoteSetId) untouched by voting entirely.
  test('four fast votes cost four mutations and at most one results reload, no per-vote status recheck', async ({
    page,
  }) => {
    const emotes = Array.from({ length: 4 }, (_, i) => ({
      emoteId: `e${i}`,
      emoteName: `Emote${i}`,
    }));

    let voteRequests = 0;
    await page.route('**/vote-sessions/7/votes', async (route) => {
      voteRequests += 1;
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    });

    let resultsRequests = 0;
    let statusRequests = 0;
    page.on('request', (request) => {
      const path = new URL(request.url()).pathname;
      if (path.endsWith('/vote-sessions/7/results')) {
        resultsRequests += 1;
      }
      if (path === '/api/channels/sensitron') {
        statusRequests += 1;
      }
    });

    await openBallot(page, emotes);
    // openBallot's own mount already issues one /results (guard handoff) and one channel-status
    // read (loadActiveEmoteSetId) — not what this test is about, see the dedicated "exactly one
    // /results request" test above. Snapshot them instead of asserting on raw totals.
    const resultsAfterEntry = resultsRequests;
    const statusAfterEntry = statusRequests;

    // No installLiveStub echo is ever pushed in this test (no emitLive call) — the reload this test
    // observes can therefore only come from vote()'s own success handler, not from a Redis
    // publish/SSE round-trip, which is exactly the independence the spec requires.
    for (const index of emotes.keys()) {
      await keepButton(page, index).click();
    }

    expect(voteRequests).toBe(4);

    // Give the 500 ms debounce window (VOTE_RELOAD_DEBOUNCE_MS) time to fire and settle.
    await page.waitForTimeout(700);

    expect(resultsRequests - resultsAfterEntry).toBeGreaterThan(0);
    expect(resultsRequests - resultsAfterEntry).toBeLessThanOrEqual(1);
    expect(statusRequests).toBe(statusAfterEntry);
  });
});

/**
 * Spec section 9, 6.9 (T6.3, AK 83): creating a vote session from a NON-active set's view (K6)
 * builds a set-session — `emoteSetId` + `sevenTvEmoteIds` in the POST body, not the null-session's
 * `emoteIds` — and the resulting detail page shows the ballot's frozen name/eligibility rather than
 * the live Emote row's. Deleting from that page is locked for as long as the session's own set is
 * not the channel's active one (Ruling D, K6 whole-branch review): today's `sync-deleted` still
 * archives by `Emote.Id` alone and assumes the active set, so a member shared between the two sets
 * would archive the wrong row — temporary until K5's set-scoped body lands.
 */
test.describe('vote ballot — a set-session created from a non-active (Halloween) set view', () => {
  const CHANNEL = 'sensitron';
  const ACTIVE_SET_ID = 'set-1';
  const HALLOWEEN_SET_ID = 'set-halloween';
  const NEW_SESSION_ID = 42;

  test('the create dialog sends a set-session body, the detail page shows the frozen ballot, and delete stays locked for the non-active Halloween set (Ruling D)', async ({
    page,
  }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page);
    await installLiveStub(page);
    await mockChannelPermissions(page, CHANNEL);
    await mockChannelStatus(page, CHANNEL);
    await mockActiveEmoteSet(page, CHANNEL, ACTIVE_SET_ID);
    await mockChannelEmoteSetList(page, CHANNEL, {
      activeEmoteSetId: ACTIVE_SET_ID,
      sets: [
        { id: ACTIVE_SET_ID, name: 'Hauptset' },
        { id: HALLOWEEN_SET_ID, name: 'Halloween' },
      ],
    });
    await mockUsageTotals(page, CHANNEL, [
      {
        emoteId: 'e-pump',
        emoteName: 'Pumpkin',
        sevenTvEmoteId: '7tv-pump',
        imageUrl: 'https://cdn.7tv.app/emote/pump/2x.webp',
        totalUseCount: 5,
      },
    ]);
    await mockUsageChannelSeries(page, CHANNEL, {});
    await mockForeignEmoteSetPreview(page, CHANNEL, {
      channelName: CHANNEL,
      emoteSetId: HALLOWEEN_SET_ID,
      emoteSetName: 'Halloween',
      capacity: 500,
      totalCount: 1,
      emotes: [{ sevenTvEmoteId: '7tv-pump', name: 'Pumpkin' }],
    });

    await page.goto(`/channels/${CHANNEL}/usage-stats?emoteSetId=${HALLOWEEN_SET_ID}`);
    await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);

    // Marks the Halloween view's only row (fine pointer + manager ⇒ select, not drilldown).
    await page.getByRole('button', { name: /^Pumpkin ·/ }).click();

    let createBody: {
      emoteSetId?: string;
      sevenTvEmoteIds?: string[];
      emoteIds?: string[];
    } | null = null;
    await page.route(`**/api/channels/${CHANNEL}/vote-sessions`, async (route) => {
      if (route.request().method() !== 'POST') {
        return route.fallback();
      }
      createBody = route.request().postDataJSON() as {
        emoteSetId?: string;
        sevenTvEmoteIds?: string[];
        emoteIds?: string[];
      };
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          id: NEW_SESSION_ID,
          title: 'Halloween-Wahl',
          allowedVoterRoles: 1,
          isActive: true,
          startedAt: '2026-10-01T00:00:00Z',
          endedAt: null,
          emoteCount: 1,
          hideResultsUntilEnd: false,
          emoteSetId: HALLOWEEN_SET_ID,
        }),
      });
    });

    await page.getByRole('button', { name: 'Zur Abstimmung stellen (1)' }).click();
    const createDialog = page.getByRole('dialog');
    await expect(createDialog.locator('#app-dialog-title')).toHaveText(
      'Abstimmung aus Auswahl erstellen',
    );
    await createDialog.locator('#create-vote-session-title-input').fill('Halloween-Wahl');

    // The ballot the detail page is about to load: a frozen name (T6.1's NameAtCreation, "as if" a
    // later sync had already overwritten the live Emote.Name to something else) on a member that
    // has since left 7TV (isArchived: true) but stays eligible — a set-session's fixed ballot never
    // closes to voting on that account (spec section 9, AK 82): no "left the set" badge, no vote
    // lock, unlike a null-session's archived row.
    await mockVoteSessionResults(
      page,
      CHANNEL,
      { id: NEW_SESSION_ID, title: 'Halloween-Wahl', emoteSetId: HALLOWEEN_SET_ID },
      [
        {
          emoteId: 'guid-pump',
          emoteName: 'PumpkinAtCreation',
          sevenTvEmoteId: '7tv-pump',
          isArchived: true,
          eligible: true,
          totalUseCount: null,
        },
      ],
    );

    await createDialog.getByRole('button', { name: 'Abstimmung erstellen' }).click();

    // The POST carries the set-session shape (E4) — emoteSetId + sevenTvEmoteIds, no emoteIds.
    // Every read goes through a fresh closure passed to expect.poll(), never a bare `createBody`
    // expression afterwards — the same idiom this spec's own `voted`/`method` variables use above
    // (see e.g. "casts a keep vote"), which keeps a captured, closure-mutated `let` from narrowing
    // to `never` under this project's TypeScript settings.
    await expect.poll(() => createBody?.emoteSetId).toBe(HALLOWEEN_SET_ID);
    await expect.poll(() => createBody?.sevenTvEmoteIds).toEqual(['7tv-pump']);
    await expect.poll(() => createBody?.emoteIds).toBeUndefined();

    // Navigated to the new session's detail page.
    await expect(page).toHaveURL(new RegExp(`/vote-sessions/${NEW_SESSION_ID}$`));
    await expect(page.getByRole('heading', { name: 'Halloween-Wahl' })).toBeVisible();
    // The ballot shows the FROZEN name, not whatever 'Pumpkin' the usage page had it as.
    await expect(
      page.getByRole('button', { name: 'PumpkinAtCreation', exact: true }),
    ).toBeVisible();
    // Eligible despite isArchived: no badge, votes stay open (AK 82).
    await expect(page.getByText('Nicht mehr im Set')).toHaveCount(0);
    await expect(keepButton(page)).toBeEnabled();

    // Selecting the ballot's card: deleting from THIS page is locked (Ruling D, temporary until
    // K5's set-scoped sync-deleted body lands) — today's sync-deleted still archives by Guid alone
    // and assumes the ACTIVE set, so a member shared between the Halloween set and the active set
    // would delete correctly on 7TV but archive the wrong (active-set) row server-side. Voting
    // stays unaffected (AK 82, asserted above) — only the delete action is locked here.
    await page.getByRole('button', { name: 'PumpkinAtCreation', exact: true }).click();
    const massDeleteButton = page.getByRole('button', { name: 'Löschen (1)' });
    await expect(massDeleteButton).toBeVisible();
    await expect(massDeleteButton).toBeDisabled();
    await expect(page.getByText('Löschen geht vorerst nur im aktiven Set.')).toBeVisible();
    await expect(massDeleteButton).toHaveAccessibleDescription(
      'Löschen geht vorerst nur im aktiven Set.',
    );
  });
});
