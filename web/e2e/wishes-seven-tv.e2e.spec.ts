import { expect, test } from './support/test';
import type { Locator, Page } from './support/test';

import {
  AUTH_USER,
  buildWish,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockAuthoritativeEmoteSets,
  mockChannelPermissions,
  mockEmoteSetTargets,
  mockSetWarning,
  mockSevenTvSetBackend,
  mockSyncDeletedInSet,
  mockVoteSessionList,
  mockWishBackend,
  mockWorkerHealth,
  wishInstant,
  wishUlid,
} from './support/mocks';
import type { MockWish, MockWishBackend } from './support/mocks';

const CHANNEL = 'sensitron';
const URL_WISHES = `/channels/${CHANNEL}/wishes`;
const SET_ID = 'set-1';

/** The generation as the Api carries it (six fractional digits) and as 7TV reads it back (three). */
const ADDED_API = '2026-11-14T10:00:00.123456Z';
const ADDED_7TV = '2026-11-14T10:00:00.123Z';
const NEWER_API = '2026-11-18T09:00:00.500000Z';
const NEWER_7TV = '2026-11-18T09:00:00.500Z';

const EMOTE = wishUlid('R1');

const section = (page: Page, view: 'open' | 'running' | 'ended' | 'pinned') =>
  page.locator(`section[aria-labelledby="wishes-${view}-title"]`);
const row = (page: Page, id: number) => page.locator(`[data-wish-id="${id}"]`);

/** A presented wish that holds a slot: the row offers "Entfernen". */
function removable(overrides: Record<string, unknown> = {}): MockWish {
  return buildWish(10, {
    status: 'presented',
    statusReason: 'expired',
    alias: 'Entfern',
    sevenTvEmoteId: EMOTE,
    slotId: 'slot-10',
    slotAddedAtUtc: ADDED_API,
    expiresAtUtc: wishInstant(-1),
    ...overrides,
  });
}

/** Resolves once `condition` holds; the polling sits in the test, so a held answer can wait on it. */
async function until(condition: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 400 && !condition(); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

test.describe('wish emotes: runs against 7TV from a row (#485)', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
    await mockChannelPermissions(page, CHANNEL, { wishesEnabled: true });
    // Where the tab-change case lands.
    await mockVoteSessionList(page, CHANNEL, []);
  });

  /** Everything a wish run touches besides the wish routes and 7TV's GQL, registered before `goto`. */
  async function surroundings(page: Page, activeSetId: string | null = SET_ID) {
    await mockActiveEmoteSet(page, CHANNEL, activeSetId ?? '');
    const auth = await mockAuthoritativeEmoteSets(page, CHANNEL, activeSetId);
    await mockEmoteSetTargets(page, [
      {
        twitchChannelId: 'sensitron-1',
        twitchLogin: CHANNEL,
        isOwnAccount: true,
        trackedChannelName: CHANNEL,
        activeEmoteSetId: SET_ID,
        sets: [{ id: SET_ID, name: 'Hauptset', isActive: true }],
      },
    ]);
    await mockSetWarning(page, CHANNEL);
    await page.route(`**/api/channels/${CHANNEL}/resync`, (route) =>
      route.fulfill({ status: 202 }),
    );
    return auth;
  }

  async function confirmRemoval(page: Page, wish: Locator): Promise<void> {
    await wish.getByRole('button', { name: 'Entfernen' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('#app-dialog-title')).toHaveText('Wunsch-Emote aus 7TV entfernen?');
    await dialog.getByRole('button', { name: 'Entfernen' }).click();
  }

  const nothingChanged = 'Es wurde nichts geändert.';
  const nothingRemoved = 'Es wurde nichts entfernt.';

  /** Whether the row says `text` (it is on the page twice: the status region and its visible twin). */
  const says = (wish: Locator, text: string | RegExp) => wish.getByText(text).first();

  test.describe('removal', () => {
    test('confirms, sends one REMOVE for the alias, books it once with slot, generation and set, and the row moves to "Beendet" (AK37)', async ({
      page,
    }) => {
      await surroundings(page);
      const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
      const sevenTv = await mockSevenTvSetBackend(page, [
        { id: EMOTE, alias: 'Entfern', addedAt: ADDED_7TV },
      ]);
      await mockSyncDeletedInSet(page, SET_ID);

      await page.goto(URL_WISHES);
      await confirmRemoval(page, row(page, 10));

      await expect.poll(() => wishes.of('complete-removal').length).toBe(1);
      expect(sevenTv.mutations()).toHaveLength(1);
      expect(sevenTv.mutations()[0].variables).toEqual({
        setId: SET_ID,
        emoteId: EMOTE,
        alias: 'Entfern',
      });
      expect(wishes.of('complete-removal')[0].body).toEqual({
        slotId: 'slot-10',
        slotAddedAtUtc: ADDED_API,
        emoteSetId: SET_ID,
        evidence: 'acknowledged',
      });

      await section(page, 'ended').getByRole('button', { name: 'Beendet' }).click();
      await expect(section(page, 'ended').locator('[data-wish-id="10"]')).toBeVisible();
      await expect(section(page, 'open').locator('[data-wish-id="10"]')).toHaveCount(0);
      expect(wishes.of('complete-removal')).toHaveLength(1);
    });

    test('the entry sits under another alias: no REMOVE, no booking, the row says why', async ({
      page,
    }) => {
      await surroundings(page);
      const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
      const sevenTv = await mockSevenTvSetBackend(page, [
        { id: EMOTE, alias: 'Umbenannt', addedAt: ADDED_7TV },
      ]);

      await page.goto(URL_WISHES);
      await confirmRemoval(page, row(page, 10));

      await expect(says(row(page, 10), nothingRemoved)).toBeVisible();
      await expect(
        row(page, 10)
          .getByText(/Entfern wurde seit dem Laden der Seite/)
          .first(),
      ).toBeVisible();
      expect(sevenTv.mutations()).toHaveLength(0);
      expect(wishes.of('complete-removal')).toHaveLength(0);
    });

    test('same alias and slot id, newer add date; the fresh wish answers before the set read (Sol-P1-1 b)', async ({
      page,
    }) => {
      const auth = await surroundings(page);
      const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
      const sevenTv = await mockSevenTvSetBackend(page, [
        { id: EMOTE, alias: 'Entfern', addedAt: NEWER_7TV },
      ]);
      await page.goto(URL_WISHES);
      await expect(row(page, 10)).toBeVisible();
      await until(() => wishes.of('detail', 10).length > 0);

      // The fresh wish already carries the newer generation; its answer is in before the set read's.
      const detailsBefore = wishes.of('detail', 10).length;
      wishes.details.set(10, removable({ slotAddedAtUtc: NEWER_API }));
      auth.gate = async (index) => {
        if (index === 1) {
          await until(() => wishes.of('detail', 10).length > detailsBefore);
        }
      };
      await confirmRemoval(page, row(page, 10));

      await expect(says(row(page, 10), nothingChanged)).toBeVisible();
      await expect(says(row(page, 10), 'Der Wunsch hat sich inzwischen geändert.')).toBeVisible();
      expect(sevenTv.mutations()).toHaveLength(0);
      expect(wishes.of('complete-removal')).toHaveLength(0);
    });

    test('the same with the answers the other way round: the set read is in before the wish read (Sol-P1-1 c)', async ({
      page,
    }) => {
      const auth = await surroundings(page);
      const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
      const sevenTv = await mockSevenTvSetBackend(page, [
        { id: EMOTE, alias: 'Entfern', addedAt: NEWER_7TV },
      ]);
      await page.goto(URL_WISHES);
      await expect(row(page, 10)).toBeVisible();
      await until(() => wishes.of('detail', 10).length > 0);

      // Now the fresh wish is the late one: held until the authoritative read of step 3a was asked.
      const detailsBefore = wishes.of('detail', 10).length;
      wishes.details.set(10, removable({ slotAddedAtUtc: NEWER_API }));
      wishes.gate = async (call) => {
        if (
          call.kind === 'detail' &&
          call.id === 10 &&
          wishes.of('detail', 10).length > detailsBefore
        ) {
          await until(() => auth.calls.length >= 2);
          await new Promise((resolve) => setTimeout(resolve, 150));
        }
      };
      await confirmRemoval(page, row(page, 10));

      await expect(says(row(page, 10), nothingChanged)).toBeVisible();
      await expect(says(row(page, 10), 'Der Wunsch hat sich inzwischen geändert.')).toBeVisible();
      expect(sevenTv.mutations()).toHaveLength(0);
      expect(wishes.of('complete-removal')).toHaveLength(0);
    });

    test('a refused booking (409) is final: no second attempt, a notice, the lists reload (Sol-P1-2)', async ({
      page,
    }) => {
      await surroundings(page);
      const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
      wishes.onCommand = (call) =>
        call.kind === 'complete-removal'
          ? { status: 409, body: { errorCode: 'wish_state_conflict' } }
          : undefined;
      await mockSevenTvSetBackend(page, [{ id: EMOTE, alias: 'Entfern', addedAt: ADDED_7TV }]);
      await mockSyncDeletedInSet(page, SET_ID);

      await page.goto(URL_WISHES);
      const listsBefore = wishes.of('list').length;
      await confirmRemoval(page, row(page, 10));

      await expect(
        says(row(page, 10), 'Entfernt in 7TV. Der Wunsch wurde inzwischen anders gebucht.'),
      ).toBeVisible();
      await expect.poll(() => wishes.of('list').length).toBeGreaterThan(listsBefore);
      await page.waitForTimeout(500);
      expect(wishes.of('complete-removal')).toHaveLength(1);
    });

    test('a lost booking is owed: "Erneut melden" sends the identical body, then it is booked (Sol-P1-2)', async ({
      page,
    }) => {
      await surroundings(page);
      const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
      wishes.onCommand = (call) =>
        call.kind === 'complete-removal' && wishes.of('complete-removal').length === 1
          ? { status: 0 }
          : undefined;
      await mockSevenTvSetBackend(page, [{ id: EMOTE, alias: 'Entfern', addedAt: ADDED_7TV }]);
      await mockSyncDeletedInSet(page, SET_ID);

      await page.goto(URL_WISHES);
      await confirmRemoval(page, row(page, 10));

      await expect(
        says(
          row(page, 10),
          'Aus 7TV entfernt, aber die Meldung an Emote Purge ist nicht angekommen.',
        ),
      ).toBeVisible();
      // Owed means locked: "Entfernen" cannot start a second removal over it.
      await expect(row(page, 10).getByRole('button', { name: 'Entfernen' })).toHaveAttribute(
        'aria-disabled',
        'true',
      );
      await row(page, 10).getByRole('button', { name: 'Erneut melden' }).click();

      await expect.poll(() => wishes.of('complete-removal').length).toBe(2);
      expect(wishes.of('complete-removal')[1].body).toEqual(wishes.of('complete-removal')[0].body);
      await section(page, 'ended').getByRole('button', { name: 'Beendet' }).click();
      await expect(section(page, 'ended').locator('[data-wish-id="10"]')).toBeVisible();
    });

    test.describe('7TV rate limit during the removal', () => {
      // The pause is the 2 s 7TV reports (plus the engine's buffer), on the page clock.
      const rateLimit = [
        {
          message: 'rate limited',
          extensions: { status: 429, headers: { 'x-ratelimit-emote_set_change-reset': '2' } },
        },
      ];

      /** Click, let the first REMOVE meet its 429, and be in the pause when the spec acts. */
      async function removeIntoPause(
        page: Page,
        sevenTv: Awaited<ReturnType<typeof mockSevenTvSetBackend>>,
      ): Promise<void> {
        await confirmRemoval(page, row(page, 10));
        await expect.poll(() => sevenTv.mutations().length).toBe(1);
        await expect(says(row(page, 10), /7TV bremst gerade/)).toBeVisible();
      }

      test('the set shows a newer generation of the alias in the pause: no second REMOVE, no booking, the row says why (Sol-R2-P1-1)', async ({
        page,
      }) => {
        await page.clock.install();
        await surroundings(page);
        const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
        const sevenTv = await mockSevenTvSetBackend(
          page,
          [{ id: EMOTE, alias: 'Entfern', addedAt: ADDED_7TV }],
          { rejectRemove: (index) => (index === 0 ? rateLimit : undefined) },
        );
        await mockSyncDeletedInSet(page, SET_ID);

        await page.goto(URL_WISHES);
        await removeIntoPause(page, sevenTv);
        sevenTv.live[0].addedAt = NEWER_7TV;
        await expect
          .poll(
            async () => {
              await page.clock.runFor(1_000);
              return (await row(page, 10).textContent()) ?? '';
            },
            { intervals: [100], timeout: 20_000 },
          )
          .toContain(nothingRemoved);

        expect(sevenTv.mutations()).toHaveLength(1);
        expect(wishes.of('complete-removal')).toHaveLength(0);
      });

      test('counter-check: the set is unchanged in the pause, so exactly one more REMOVE goes out and is booked', async ({
        page,
      }) => {
        await page.clock.install();
        await surroundings(page);
        const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
        const sevenTv = await mockSevenTvSetBackend(
          page,
          [{ id: EMOTE, alias: 'Entfern', addedAt: ADDED_7TV }],
          { rejectRemove: (index) => (index === 0 ? rateLimit : undefined) },
        );
        await mockSyncDeletedInSet(page, SET_ID);

        await page.goto(URL_WISHES);
        await removeIntoPause(page, sevenTv);
        await expect
          .poll(
            async () => {
              await page.clock.runFor(1_000);
              return wishes.of('complete-removal').length;
            },
            { intervals: [100], timeout: 20_000 },
          )
          .toBe(1);

        expect(sevenTv.mutations()).toHaveLength(2);
        // Booked: the reload moves the row to "Beendet" (its own line goes with the open row).
        await section(page, 'ended').getByRole('button', { name: 'Beendet' }).click();
        await expect(section(page, 'ended').locator('[data-wish-id="10"]')).toBeVisible();
      });

      test('another active set in the pause (asked with refresh; the cached answer still shows the old one): no second REMOVE (Sol-R3)', async ({
        page,
      }) => {
        await page.clock.install();
        const auth = await surroundings(page);
        auth.cachedActiveEmoteSetId = SET_ID;
        const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
        const sevenTv = await mockSevenTvSetBackend(
          page,
          [{ id: EMOTE, alias: 'Entfern', addedAt: ADDED_7TV }],
          { rejectRemove: (index) => (index === 0 ? rateLimit : undefined) },
        );
        await mockSyncDeletedInSet(page, SET_ID);

        await page.goto(URL_WISHES);
        await removeIntoPause(page, sevenTv);
        auth.activeEmoteSetId = 'set-2';
        await expect
          .poll(
            async () => {
              await page.clock.runFor(1_000);
              return (await row(page, 10).textContent()) ?? '';
            },
            { intervals: [100], timeout: 20_000 },
          )
          .toContain(nothingRemoved);

        expect(sevenTv.mutations()).toHaveLength(1);
        expect(wishes.of('complete-removal')).toHaveLength(0);
        // Every authoritative read of the whole run asked for the fresh answer.
        expect(auth.calls.every((call) => call.refresh)).toBe(true);
        await expect(
          row(page, 10).getByText('Das aktive Set hat gewechselt.').first(),
        ).toBeVisible();
      });

      test('the 7TV token is removed in the pause: no second REMOVE (Sol-R3)', async ({ page }) => {
        await page.clock.install();
        await surroundings(page);
        const wishes = await mockWishBackend(page, CHANNEL, { open: [removable()] });
        const sevenTv = await mockSevenTvSetBackend(
          page,
          [{ id: EMOTE, alias: 'Entfern', addedAt: ADDED_7TV }],
          { rejectRemove: (index) => (index === 0 ? rateLimit : undefined) },
        );
        await mockSyncDeletedInSet(page, SET_ID);

        await page.goto(URL_WISHES);
        await removeIntoPause(page, sevenTv);
        await page.evaluate(() => sessionStorage.removeItem('ep_7tv_write_token'));
        await expect
          .poll(
            async () => {
              await page.clock.runFor(1_000);
              return (await row(page, 10).textContent()) ?? '';
            },
            { intervals: [100], timeout: 20_000 },
          )
          .toContain(nothingRemoved);

        expect(sevenTv.mutations()).toHaveLength(1);
        expect(wishes.of('complete-removal')).toHaveLength(0);
        await expect(
          row(page, 10).getByText('Das 7TV-Token wurde entfernt oder gewechselt.').first(),
        ).toBeVisible();
      });
    });
  });

  test.describe('lock and start contract (AK38)', () => {
    /** Two approved wishes and one with a removable entry: the first starts, the others are locked. */
    function threeRows(): Record<'open' | 'running', MockWish[]> {
      const approved = (id: number, alias: string) =>
        buildWish(id, {
          status: 'approved',
          alias,
          slotId: null,
          slotAddedAtUtc: null,
          startsAtUtc: null,
          probationStartsAtUtc: null,
          expiresAtUtc: null,
          sevenTvEmoteId: wishUlid(`A${id}`),
        });
      return {
        open: [approved(20, 'AddEins'), approved(22, 'AddZwei')],
        running: [buildWish(21, { alias: 'Laeuft', sevenTvEmoteId: wishUlid('R21') })],
      };
    }

    async function setup(page: Page): Promise<{
      wishes: MockWishBackend;
      sevenTv: Awaited<ReturnType<typeof mockSevenTvSetBackend>>;
      auth: Awaited<ReturnType<typeof surroundings>>;
    }> {
      const auth = await surroundings(page);
      const wishes = await mockWishBackend(page, CHANNEL, threeRows());
      const sevenTv = await mockSevenTvSetBackend(page, [
        { id: wishUlid('R21'), alias: 'Laeuft', addedAt: ADDED_7TV },
      ]);
      return { wishes, sevenTv, auth };
    }

    test('while the add is out, the other rows lock their 7TV buttons and name the operation (AK38)', async ({
      page,
    }) => {
      const { sevenTv } = await setup(page);
      let releaseAdd!: () => void;
      const heldAdd = new Promise<void>((resolve) => (releaseAdd = resolve));
      await page.route('https://7tv.io/v4/gql', async (route) => {
        const query = (route.request().postDataJSON() as { query: string }).query;
        if (query.includes('addEmote(')) {
          await heldAdd;
        }
        return route.fallback();
      });

      await page.goto(URL_WISHES);
      await row(page, 20).getByRole('button', { name: 'In 7TV hinzufügen' }).click();
      // The ADD itself is held in front of the 7TV stand-in; the page's lock line says it is out.
      await expect(page.locator('#wishes-run-lock')).toBeVisible();

      const otherAdd = row(page, 22).getByRole('button', { name: 'In 7TV hinzufügen' });
      const remove = row(page, 21).getByRole('button', { name: 'Entfernen' });
      for (const button of [otherAdd, remove]) {
        await expect(button).toHaveAttribute('aria-disabled', 'true');
        await expect(button).toHaveAccessibleDescription(
          'In 7TV läuft gerade das Hinzufügen eines Wunsch-Emotes. Bis dahin sind die 7TV-Aktionen hier gesperrt.',
        );
      }
      // A locked button is a no-op: no second start, no dialog.
      // `aria-disabled` stays focusable and clickable for the browser: forced, to see it do nothing.
      await remove.click({ force: true });
      await expect(page.getByRole('dialog')).toHaveCount(0);

      expect(sevenTv.mutations()).toHaveLength(0);
      releaseAdd();
      await expect(
        says(row(page, 20), 'In 7TV hinzugefügt. Der nächste Sync übernimmt den Wunsch.'),
      ).toBeVisible();
      await expect(otherAdd).not.toHaveAttribute('aria-disabled', 'true');
      expect(sevenTv.mutations()).toHaveLength(1);
    });

    test('the pre-check is held back while the fresh wish reports "closed": no mutation, the row names the reason (Sol-P2-3)', async ({
      page,
    }) => {
      const { wishes, sevenTv } = await setup(page);
      let releaseCheck!: () => void;
      const heldCheck = new Promise<void>((resolve) => (releaseCheck = resolve));
      let checkAsked = false;
      await page.route('**/api/seventv/me/emote-set-targets/*', async (route) => {
        checkAsked = true;
        await heldCheck;
        return route.fallback();
      });

      await page.goto(URL_WISHES);
      await expect(row(page, 20)).toBeVisible();
      await row(page, 20).getByRole('button', { name: 'In 7TV hinzufügen' }).click();
      await expect.poll(() => checkAsked).toBe(true);
      // Meanwhile somebody closed the wish.
      wishes.details.set(20, { ...(wishes.find(20) as MockWish), status: 'closed' });
      releaseCheck();

      await expect(says(row(page, 20), nothingChanged)).toBeVisible();
      await expect(says(row(page, 20), 'Der Wunsch hat sich inzwischen geändert.')).toBeVisible();
      expect(sevenTv.mutations()).toHaveLength(0);
    });

    test('the last read of the wish is held while the tab changes: after the answer nothing goes out (Sol-R2-P2-3)', async ({
      page,
    }) => {
      const { wishes, sevenTv } = await setup(page);
      await page.goto(URL_WISHES);
      await expect(row(page, 20)).toBeVisible();
      await until(() => wishes.of('detail', 20).length > 0);

      const detailsBefore = wishes.of('detail', 20).length;
      let releaseRead!: () => void;
      const heldRead = new Promise<void>((resolve) => (releaseRead = resolve));
      wishes.gate = (call) =>
        call.kind === 'detail' && call.id === 20 && wishes.of('detail', 20).length > detailsBefore
          ? heldRead
          : undefined;
      await row(page, 20).getByRole('button', { name: 'In 7TV hinzufügen' }).click();
      await expect.poll(() => wishes.of('detail', 20).length).toBeGreaterThan(detailsBefore);

      await page.getByRole('link', { name: 'Votings' }).click();
      await expect(page).toHaveURL(new RegExp(`/channels/${CHANNEL}/vote-sessions$`));
      releaseRead();
      await page.waitForTimeout(600);

      expect(sevenTv.mutations()).toHaveLength(0);
    });

    test("the active set at 7TV is another than the row's: no start, with the reason (Sol-P2-3)", async ({
      page,
    }) => {
      const { sevenTv, auth } = await setup(page);
      // The channel row (the set the last sync stored) says set-1; 7TV itself already says set-2.
      auth.activeEmoteSetId = 'set-2';

      await page.goto(URL_WISHES);
      await row(page, 20).getByRole('button', { name: 'In 7TV hinzufügen' }).click();

      await expect(says(row(page, 20), nothingChanged)).toBeVisible();
      await expect(
        says(
          row(page, 20),
          'Das Set hat in 7TV gewechselt. Nach dem nächsten Sync erneut versuchen.',
        ),
      ).toBeVisible();
      expect(sevenTv.mutations()).toHaveLength(0);
      expect(auth.calls.every((call) => call.refresh)).toBe(true);
    });
  });
});
