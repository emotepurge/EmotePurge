import { expect, fulfillCdnStub, test } from './support/test';
import type { Locator, Page } from './support/test';

import {
  AUTH_USER,
  buildWish,
  buildWishSlot,
  buildWishUsage,
  emitLive,
  installLiveStub,
  isWishSummaryNotFound,
  mockAuthMe,
  mockChannelPermissions,
  mockMyChannels,
  mockSevenTvEmoteReads,
  mockVoteSessionList,
  mockWishBackend,
  mockWishMine,
  mockWishSummaryLive,
  mockWorkerHealth,
  wishInstant,
  wishUlid,
} from './support/mocks';

const CHANNEL = 'sensitron';
const URL_WISHES = `/channels/${CHANNEL}/wishes`;

/** One of the page's list sections by view. */
const section = (page: Page, view: 'open' | 'running' | 'ended' | 'pinned') =>
  page.locator(`section[aria-labelledby="wishes-${view}-title"]`);

const row = (page: Page, id: number) => page.locator(`[data-wish-id="${id}"]`);

test.describe('wish emotes: the manager page (#485)', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
    await mockChannelPermissions(page, CHANNEL, { wishesEnabled: true });
    // Where the gate cases land.
    await mockVoteSessionList(page, CHANNEL, []);
  });

  test.describe('filing a wish by hand ("Nachtragen")', () => {
    test('opens with the channel default as duration, normalises the login and lands the new row under "Handlungsbedarf"', async ({
      page,
    }) => {
      const id = wishUlid('F1');
      const backend = await mockWishBackend(page, CHANNEL, {
        settings: { defaultDurationDays: 21 },
      });
      await mockSevenTvEmoteReads(page, { [id]: { name: 'FreshEmote' } });

      await page.goto(URL_WISHES);
      await page.getByRole('button', { name: 'Nachtragen' }).click();
      const dialog = page.getByRole('dialog');

      await expect(dialog.getByLabel('Laufzeit in Tagen')).toHaveValue('21');
      await dialog.getByLabel('7TV-Link oder Emote-ID').fill(`https://7tv.app/emotes/${id}`);
      await expect(dialog.getByLabel('Alias im Set')).toHaveValue('FreshEmote');
      await expect(dialog.getByText('Gefunden: FreshEmote')).toBeVisible();
      await dialog.getByLabel('Gewünscht von (Twitch-Login, freiwillig)').fill('@HandOfBlood');
      await dialog.getByRole('button', { name: 'Nachtragen' }).click();

      await expect.poll(() => backend.of('create').length).toBe(1);
      expect(backend.of('create')[0].body).toEqual({
        sevenTvEmoteId: id,
        alias: 'FreshEmote',
        durationDays: 21,
        emoteName: 'FreshEmote',
        requesterLogin: 'handofblood',
      });
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(section(page, 'open').getByText('FreshEmote').first()).toBeVisible();
      await expect(section(page, 'open').getByText('Freigegeben')).toBeVisible();
    });

    test('a duration of 0 locks the button with a visible reason (AK33)', async ({ page }) => {
      const id = wishUlid('F2');
      const backend = await mockWishBackend(page, CHANNEL);
      await mockSevenTvEmoteReads(page, { [id]: { name: 'FreshEmote' } });

      await page.goto(URL_WISHES);
      await page.getByRole('button', { name: 'Nachtragen' }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByLabel('7TV-Link oder Emote-ID').fill(id);
      await expect(dialog.getByLabel('Alias im Set')).toHaveValue('FreshEmote');
      await dialog.getByLabel('Laufzeit in Tagen').fill('0');

      const submit = dialog.getByRole('button', { name: 'Nachtragen' });
      await expect(submit).toBeDisabled();
      await expect(dialog.locator('#wish-entry-blocked')).toHaveText(
        'Die Laufzeit fehlt oder passt nicht.',
      );
      await expect(submit).toHaveAccessibleDescription('Die Laufzeit fehlt oder passt nicht.');
      expect(backend.of('create')).toHaveLength(0);
    });

    test('a late answer for link A changes nothing once link B is in the field (Sol-P2-10)', async ({
      page,
    }) => {
      const a = wishUlid('FA');
      const b = wishUlid('FB');
      const backend = await mockWishBackend(page, CHANNEL);
      let releaseA!: () => void;
      const heldA = new Promise<void>((resolve) => (releaseA = resolve));
      const reads = await mockSevenTvEmoteReads(
        page,
        { [a]: { name: 'AliasA' }, [b]: { name: 'AliasB' } },
        { gate: (id) => (id === a ? heldA : undefined) },
      );

      await page.goto(URL_WISHES);
      await page.getByRole('button', { name: 'Nachtragen' }).click();
      const dialog = page.getByRole('dialog');
      const field = dialog.getByLabel('7TV-Link oder Emote-ID');

      await field.fill(a);
      await expect.poll(() => reads.requests).toEqual([a]);
      await field.fill(b);
      await expect(dialog.getByLabel('Alias im Set')).toHaveValue('AliasB');
      await expect(dialog.getByText('Gefunden: AliasB')).toBeVisible();

      releaseA();
      // The held answer lands now; it must neither rename the alias nor replace the preview.
      await page.waitForTimeout(300);
      await expect(dialog.getByLabel('Alias im Set')).toHaveValue('AliasB');
      await expect(dialog.getByText('Gefunden: AliasB')).toBeVisible();
      await expect(dialog.getByText('Gefunden: AliasA')).toHaveCount(0);

      await dialog.getByRole('button', { name: 'Nachtragen' }).click();
      await expect.poll(() => backend.of('create').length).toBe(1);
      expect(backend.of('create')[0].body).toMatchObject({
        sevenTvEmoteId: b,
        alias: 'AliasB',
        emoteName: 'AliasB',
      });
    });
  });

  test.describe('the emote is already in the set: take over the entry (AK34)', () => {
    const emote = wishUlid('E1');

    /** The entry dialog up to the 409, whose body the spec chooses; the adopt dialog is then open. */
    async function conflictToAdopt(page: Page): Promise<Locator> {
      await mockSevenTvEmoteReads(page, { [emote]: { name: 'InSet' } });
      await page.goto(URL_WISHES);
      await page.getByRole('button', { name: 'Nachtragen' }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByLabel('7TV-Link oder Emote-ID').fill(emote);
      await expect(dialog.getByLabel('Alias im Set')).toHaveValue('InSet');
      await dialog.getByRole('button', { name: 'Nachtragen' }).click();
      return dialog;
    }

    test('the 409 offers exactly the reported entries, "Übernehmen" sends the picked one and the row runs (AK34)', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        slots: [
          buildWishSlot('slot-a', { sevenTvEmoteId: emote, alias: 'InSetA' }),
          buildWishSlot('slot-b', { sevenTvEmoteId: emote, alias: 'InSetB' }),
          // Another entry of the same emote that the 409 did not name: not offered.
          buildWishSlot('slot-c', { sevenTvEmoteId: emote, alias: 'InSetC' }),
        ],
      });
      backend.onCommand = (call) =>
        call.kind === 'create'
          ? {
              status: 409,
              body: {
                errorCode: 'wish_emote_already_in_set',
                slotIds: ['slot-a', 'slot-b'],
                wishId: null,
              },
            }
          : undefined;

      const dialog = await conflictToAdopt(page);
      await dialog.getByRole('button', { name: 'Bestand übernehmen' }).click();
      await expect(dialog.locator('#app-dialog-title')).toHaveText('Bestand übernehmen');

      // The entries come from the wish read route (`GET …/slots`), never from the usage totals: this
      // page mocks no usage at all, and both reported entries are listed and pickable.
      await expect(dialog.getByRole('radio')).toHaveCount(2);
      await expect(dialog.getByRole('radio', { name: 'InSetA' })).toBeEnabled();
      await expect(dialog.getByRole('radio', { name: 'InSetC' })).toHaveCount(0);
      expect(backend.of('slots')[0].sevenTvEmoteId).toBe(emote);

      await dialog.getByRole('radio', { name: 'InSetB' }).check();
      await dialog.getByRole('button', { name: 'Übernehmen' }).click();

      await expect.poll(() => backend.of('adopt').length).toBe(1);
      const body = backend.of('adopt')[0].body;
      expect(body).toMatchObject({
        slotId: 'slot-b',
        expectedAlias: 'InSetB',
        expectedAddedAtUtc: '2026-11-10T08:00:00.000000Z',
      });
      expect(body).not.toHaveProperty('wishId');
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(section(page, 'running').getByText('InSetB').first()).toBeVisible();
      await expect(section(page, 'running').getByText('Aktiv')).toBeVisible();
    });

    test('a 409 naming a wish that stands on no loaded page offers "Verlängern" and no takeover (Sol-P2-5)', async ({
      page,
    }) => {
      // 25 running wishes: page size 20, so wish 25 is on page 2 and on no loaded list.
      const running = Array.from({ length: 25 }, (_, i) =>
        buildWish(i + 1, { sevenTvEmoteId: i === 24 ? emote : undefined }),
      );
      const backend = await mockWishBackend(page, CHANNEL, { running });
      backend.onCommand = (call) =>
        call.kind === 'create'
          ? {
              status: 409,
              body: {
                errorCode: 'wish_emote_already_in_set',
                slotIds: ['slot-25'],
                wishId: 25,
              },
            }
          : undefined;

      const dialog = await conflictToAdopt(page);
      await expect(
        dialog.getByText('Für dieses Emote läuft bereits ein Wunsch.').first(),
      ).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Verlängern' })).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Zum Wunsch' })).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Bestand übernehmen' })).toHaveCount(0);
      expect(backend.of('detail', 25).length).toBeGreaterThan(0);
      expect(backend.of('adopt')).toHaveLength(0);
    });

    test('a refused adopt that names a kept wish says so, without "Verlängern" and without a second send', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        ended: [buildWish(7, { status: 'kept', sevenTvEmoteId: emote, slotId: 'slot-a' })],
        slots: [buildWishSlot('slot-a', { sevenTvEmoteId: emote, alias: 'InSetA' })],
      });
      backend.onCommand = (call) =>
        call.kind === 'adopt'
          ? { status: 409, body: { errorCode: 'wish_slot_already_linked', wishId: 7 } }
          : undefined;
      await page.goto(URL_WISHES);

      await page.getByRole('button', { name: 'Bestand übernehmen' }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByRole('radio', { name: 'InSetA' }).check();
      await dialog.getByRole('button', { name: 'Übernehmen' }).click();

      await expect(
        dialog.getByText('Der Eintrag ist inzwischen als behaltener Wunsch geführt.').first(),
      ).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Zum Wunsch' }).first()).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Verlängern' })).toHaveCount(0);
      expect(backend.of('adopt')).toHaveLength(1);
    });

    test('a slot id reused with a new date is refused with 404: entries reload, the choice is gone, only a new choice sends the new date (Sol-R2-P2-4)', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        slots: [buildWishSlot('slot-a', { alias: 'ReusedId' })],
      });
      backend.onCommand = (call) =>
        call.kind === 'adopt' && backend.of('adopt').length === 1
          ? { status: 404, body: { errorCode: 'wish_slot_not_found' } }
          : undefined;
      await page.goto(URL_WISHES);

      await page.getByRole('button', { name: 'Bestand übernehmen' }).click();
      const dialog = page.getByRole('dialog');
      const radio = dialog.getByRole('radio', { name: 'ReusedId' });
      await radio.check();
      // 7TV reused the slot id for a newer entry while the dialog was open.
      backend.slots = [
        buildWishSlot('slot-a', { alias: 'ReusedId', addedAtUtc: '2026-11-18T09:30:00.000000Z' }),
      ];
      await dialog.getByRole('button', { name: 'Übernehmen' }).click();

      await expect(
        dialog.getByText('Der Eintrag hat sich inzwischen geändert.').first(),
      ).toBeVisible();
      await expect(
        dialog.getByText('Die Einträge sind neu geladen, wähle erneut.').first(),
      ).toBeVisible();
      await expect(radio).not.toBeChecked();
      await expect(dialog.getByText('im Set seit 18.11.2026')).toBeVisible();
      expect(backend.of('adopt')).toHaveLength(1);

      await radio.check();
      await dialog.getByRole('button', { name: 'Übernehmen' }).click();
      await expect.poll(() => backend.of('adopt').length).toBe(2);
      expect(backend.of('adopt')[1].body).toMatchObject({
        slotId: 'slot-a',
        expectedAddedAtUtc: '2026-11-18T09:30:00.000000Z',
      });
    });

    test('a free entry whose emote has an approved wish says "wird verknüpft", sends that wish id and the row turns active (Sol-R2-P2-5 a)', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [
          buildWish(40, {
            status: 'approved',
            alias: 'Approved40',
            slotId: null,
            slotAddedAtUtc: null,
          }),
        ],
        slots: [
          buildWishSlot('slot-a', {
            sevenTvEmoteId: wishUlid('W40'),
            alias: 'Approved40',
            runningWishId: 40,
            runningWishAdoptable: true,
          }),
        ],
      });
      await page.goto(URL_WISHES);

      await page.getByRole('button', { name: 'Bestand übernehmen' }).click();
      const dialog = page.getByRole('dialog');
      await expect(
        dialog.getByText('Wird mit dem laufenden Wunsch für dieses Emote verknüpft.'),
      ).toBeVisible();
      await dialog.getByRole('radio', { name: 'Approved40' }).check();
      await dialog.getByRole('button', { name: 'Verknüpfen' }).click();

      await expect.poll(() => backend.of('adopt').length).toBe(1);
      expect(backend.of('adopt')[0].body).toMatchObject({ slotId: 'slot-a', wishId: 40 });
      await expect(row(page, 40)).toBeVisible();
      await expect(section(page, 'running').locator('[data-wish-id="40"]')).toContainText('Aktiv');
      await expect(section(page, 'open').locator('[data-wish-id="40"]')).toHaveCount(0);
    });

    test('an entry whose emote runs a wish at another entry is locked; "Zum Wunsch" pins that wish from page 2 (Sol-R2-P2-5 b)', async ({
      page,
    }) => {
      const running = Array.from({ length: 25 }, (_, i) => buildWish(i + 1));
      await mockWishBackend(page, CHANNEL, {
        running,
        slots: [
          buildWishSlot('slot-a', {
            alias: 'Elsewhere',
            runningWishId: 25,
            runningWishAdoptable: false,
          }),
        ],
      });
      await page.goto(URL_WISHES);

      await page.getByRole('button', { name: 'Bestand übernehmen' }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('radio', { name: 'Elsewhere' })).toBeDisabled();
      await expect(
        dialog.getByText('Für dieses Emote läuft schon ein Wunsch an einem anderen Eintrag.'),
      ).toBeVisible();
      await dialog.getByRole('button', { name: 'Zum Wunsch: Elsewhere' }).click();

      await expect(page).toHaveURL(/\/channels\/sensitron\/wishes\?wish=25$/);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(section(page, 'pinned').locator('[data-wish-id="25"]')).toBeVisible();
      await expect(section(page, 'running').locator('[data-wish-id="25"]')).toHaveCount(0);
    });

    test('a long entry list scrolls underneath the action row: no radio or "Zum Wunsch" paints over it, and the dialog fits the viewport', async ({
      page,
    }) => {
      // Regression: the rows lift their radio and "Zum Wunsch" to z-10 above the stretched label, and
      // the sticky action row had no z-index, so while the list scrolled those controls slid over
      // "Abbrechen"/"Übernehmen" and the reason beside them while the alias and date went underneath.
      await page.setViewportSize({ width: 1280, height: 800 });
      await mockWishBackend(page, CHANNEL, {
        slots: Array.from({ length: 80 }, (_, i) =>
          buildWishSlot(`slot-${String(i).padStart(2, '0')}`, {
            alias: `Entry${i}`,
            ...(i % 7 === 3 ? { heldByWishId: 100 + i } : {}),
          }),
        ),
      });
      await page.goto(URL_WISHES);
      await page.getByRole('button', { name: 'Bestand übernehmen' }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('radio', { name: 'Entry0' })).toBeVisible();
      const pane = page.locator('.cdk-overlay-pane.app-dialog-panel');

      // The dialog never outgrows the viewport; its body scrolls inside it instead.
      const paneBox = await pane.evaluate((el) => el.getBoundingClientRect().toJSON() as DOMRect);
      expect(paneBox.top).toBeGreaterThanOrEqual(0);
      expect(paneBox.bottom).toBeLessThanOrEqual(800);
      // The premise: there is plenty to scroll, so rows really pass under the action row.
      expect(await pane.evaluate((el) => el.scrollHeight - el.clientHeight)).toBeGreaterThan(1000);

      const actionsRow = dialog.locator('[dialog-actions]').first().locator('xpath=..');
      for (const scrollTop of [300, 610, 1200]) {
        await pane.evaluate((el, top) => (el.scrollTop = top), scrollTop);
        // Every control the row's rectangle overlaps lies underneath it: the point hit there belongs
        // to the action row, not to the control.
        const covered = await actionsRow.evaluate((row) => {
          const rowBox = row.getBoundingClientRect();
          const list = [
            ...(row.closest('[role="dialog"]')?.querySelectorAll('li input, li button') ?? []),
          ];
          return list.flatMap((control) => {
            const box = control.getBoundingClientRect();
            const top = Math.max(box.top, rowBox.top);
            const bottom = Math.min(box.bottom, rowBox.bottom);
            if (bottom - top < 2) {
              return [];
            }
            const hit = document.elementFromPoint(box.left + box.width / 2, (top + bottom) / 2);
            return [hit !== null && row.contains(hit)];
          });
        });
        expect(covered.length).toBeGreaterThan(0);
        expect(covered.every(Boolean)).toBe(true);
      }
    });

    test('real mouse clicks reach every control of the adopt dialog: each entry row, the start radios and the duration field', async ({
      page,
    }) => {
      // Regression: a stretched label overlay without a positioned row covered the whole dialog, so a
      // click on an entry picked the last one and the start radios and the duration were unreachable.
      // `check()` and `fill()` do not hit-test the way a mouse does, hence real clicks here.
      await mockWishBackend(page, CHANNEL, {
        slots: [
          buildWishSlot('slot-a', { alias: 'EntryA' }),
          buildWishSlot('slot-b', { alias: 'EntryB' }),
          buildWishSlot('slot-c', { alias: 'EntryC' }),
        ],
      });
      await page.goto(URL_WISHES);
      await page.getByRole('button', { name: 'Bestand übernehmen' }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByRole('radio', { name: 'EntryA' })).toBeVisible();

      // A click on the second entry's alias picks exactly that entry, not the last one.
      await dialog.getByText('EntryB', { exact: true }).click();
      await expect(dialog.getByRole('radio', { name: 'EntryB' })).toBeChecked();
      await expect(dialog.getByRole('radio', { name: 'EntryA' })).not.toBeChecked();
      await expect(dialog.getByRole('radio', { name: 'EntryC' })).not.toBeChecked();

      // Each start radio takes a mouse click.
      const startNow = dialog.getByRole('radio', { name: 'Ab jetzt' });
      const startAdded = dialog.getByRole('radio', { name: 'Ab dem Hinzufügedatum' });
      const duration = dialog.getByLabel('Laufzeit in Tagen');
      await startNow.click();
      await expect(startNow).toBeChecked();
      await startAdded.click();
      await expect(startAdded).toBeChecked();
      await startNow.click();
      await expect(startNow).toBeChecked();

      // The duration field takes focus by click.
      await duration.click();
      await expect(duration).toBeFocused();

      // The point at the middle of each control belongs to the control itself.
      for (const control of [startNow, startAdded, duration]) {
        const hit = await control.evaluate((el) => {
          const box = el.getBoundingClientRect();
          const top = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
          return top === el;
        });
        expect(hit).toBe(true);
      }
    });

    test('a link target that became active before sending: 409, entries reload, the entry is locked, nothing is sent again (Sol-R2-P2-5 c)', async ({
      page,
    }) => {
      const approved = buildWish(40, { status: 'approved', slotId: null, slotAddedAtUtc: null });
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [approved],
        slots: [
          buildWishSlot('slot-a', {
            sevenTvEmoteId: wishUlid('W40'),
            alias: 'Racy',
            runningWishId: 40,
            runningWishAdoptable: true,
          }),
        ],
      });
      backend.onCommand = (call) => {
        if (call.kind !== 'adopt') {
          return undefined;
        }
        // Meanwhile another manager linked it: the wish holds the entry now.
        backend.slots = [
          buildWishSlot('slot-a', {
            sevenTvEmoteId: wishUlid('W40'),
            alias: 'Racy',
            heldByWishId: 40,
            runningWishId: 40,
          }),
        ];
        backend.open = [];
        backend.running = [buildWish(40, { slotId: 'slot-a' })];
        return { status: 409, body: { errorCode: 'wish_state_conflict' } };
      };
      await page.goto(URL_WISHES);

      await page.getByRole('button', { name: 'Bestand übernehmen' }).click();
      const dialog = page.getByRole('dialog');
      const radio = dialog.getByRole('radio', { name: 'Racy' });
      await radio.check();
      await dialog.getByRole('button', { name: 'Verknüpfen' }).click();

      await expect(
        dialog.getByText('Der Wunsch hat sich inzwischen geändert.').first(),
      ).toBeVisible();
      await expect(radio).toBeDisabled();
      await expect(radio).not.toBeChecked();
      await page.waitForTimeout(300);
      expect(backend.of('adopt')).toHaveLength(1);
    });
  });

  test.describe('switch and role gates (AK32)', () => {
    test('switch off: no tab, a direct visit lands on the votings, no wish route is asked', async ({
      page,
    }) => {
      await mockChannelPermissions(page, CHANNEL, { wishesEnabled: false });
      const backend = await mockWishBackend(page, CHANNEL);

      await page.goto(`/channels/${CHANNEL}/vote-sessions`);
      await expect(page.getByRole('link', { name: 'Aktivität' })).toBeVisible();
      await expect(page.getByRole('link', { name: 'Wünsche' })).toHaveCount(0);

      await page.goto(URL_WISHES);
      await expect(page).toHaveURL(new RegExp(`/channels/${CHANNEL}/vote-sessions$`));
      expect(backend.calls).toHaveLength(0);
    });

    test('switch on but no manager role: the same', async ({ page }) => {
      await mockChannelPermissions(page, CHANNEL, { wishesEnabled: true, canManage: false });
      const backend = await mockWishBackend(page, CHANNEL);

      await page.goto(`/channels/${CHANNEL}/vote-sessions`);
      await expect(page.getByRole('link', { name: 'Votings' })).toBeVisible();
      await expect(page.getByRole('link', { name: 'Wünsche' })).toHaveCount(0);

      await page.goto(URL_WISHES);
      await expect(page).toHaveURL(new RegExp(`/channels/${CHANNEL}/vote-sessions$`));
      expect(backend.calls).toHaveLength(0);
    });

    test('switch on and a manager: the tab is there and opens the page', async ({ page }) => {
      await mockWishBackend(page, CHANNEL);
      await page.goto(`/channels/${CHANNEL}/vote-sessions`);
      await page.getByRole('link', { name: 'Wünsche' }).click();
      await expect(page).toHaveURL(new RegExp(`${URL_WISHES}$`));
      await expect(section(page, 'open')).toBeVisible();
    });
  });

  test.describe('live, validity and usage (AK39)', () => {
    test('wish.changed reloads the lists and the changed row shows up without a reload', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [buildWish(1, { status: 'presented', statusReason: 'expired', alias: 'Alt' })],
      });
      await page.goto(URL_WISHES);
      await expect(section(page, 'open').getByText('Alt').first()).toBeVisible();
      // Both lists (open, running) are in before the baseline is taken.
      await expect.poll(() => backend.of('list').length).toBeGreaterThanOrEqual(2);
      const before = backend.of('list').length;

      backend.open = [
        buildWish(1, { status: 'presented', statusReason: 'expired', alias: 'Alt' }),
        buildWish(2, { status: 'approved', alias: 'Neu', slotId: null, slotAddedAtUtc: null }),
      ];
      await emitLive(page, { type: 'wish.changed', channel: CHANNEL });

      await expect(row(page, 2)).toBeVisible();
      expect(
        backend.of('list').length,
        JSON.stringify(backend.calls.map((c) => [c.kind, c.view, c.id])),
      ).toBeGreaterThan(before);
    });

    test('two quick requests, the older answer arrives last: the page shows the younger state', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [buildWish(1, { status: 'presented', statusReason: 'expired', alias: 'Erste' })],
      });
      let releaseOld!: () => void;
      const heldOld = new Promise<void>((resolve) => (releaseOld = resolve));
      const openCalls = () => backend.of('list').filter((call) => call.view === 'open');
      backend.gate = (call) =>
        call.kind === 'list' && call.view === 'open' && openCalls().length === 2
          ? heldOld
          : undefined;
      await page.goto(URL_WISHES);
      await expect(section(page, 'open').getByText('Erste').first()).toBeVisible();

      // Request 2 (the older of the two) sees the state without "Juengste" and is held back.
      backend.open = [
        buildWish(1, { status: 'presented', statusReason: 'expired', alias: 'Erste' }),
        buildWish(2, { status: 'presented', statusReason: 'expired', alias: 'Mittlere' }),
      ];
      await emitLive(page, { type: 'wish.changed', channel: CHANNEL });
      await expect.poll(() => openCalls().length).toBe(2);

      // Request 3 sees the younger state and is answered at once.
      backend.open = [
        ...backend.open,
        buildWish(3, { status: 'presented', statusReason: 'expired', alias: 'Juengste' }),
      ];
      await emitLive(page, { type: 'wish.changed', channel: CHANNEL });
      await expect(section(page, 'open').getByText('Juengste').first()).toBeVisible();

      releaseOld();
      await page.waitForTimeout(300);
      await expect(section(page, 'open').getByText('Juengste').first()).toBeVisible();
      await expect(section(page, 'open').getByText('Mittlere').first()).toBeVisible();
    });

    test('channel.synced alone reloads the lists as well (Sol-P2-8)', async ({ page }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        running: [buildWish(1, { alias: 'Laeuft' })],
      });
      await page.goto(URL_WISHES);
      await expect(section(page, 'running').getByText('Laeuft').first()).toBeVisible();
      const before = backend.of('list').length;

      backend.running = [buildWish(1, { alias: 'Laeuft' }), buildWish(2, { alias: 'NachSync' })];
      await emitLive(page, { type: 'channel.synced', channel: CHANNEL });

      await expect(section(page, 'running').getByText('NachSync').first()).toBeVisible();
      expect(backend.of('list').length).toBeGreaterThan(before);
    });

    test('a visible row shows its usage; after five minutes the detail is asked again and shows the new sum (Sol-R2-P2-6)', async ({
      page,
    }) => {
      await page.clock.install();
      const backend = await mockWishBackend(page, CHANNEL, {
        running: [buildWish(1, { alias: 'Gezaehlt' })],
      });
      backend.usage.set(1, buildWishUsage(10));
      await page.goto(URL_WISHES);

      await expect(row(page, 1).getByText('10 Nutzungen')).toBeVisible();
      const asked = backend.of('detail', 1).length;
      expect(asked).toBeGreaterThan(0);

      backend.usage.set(1, buildWishUsage(25));
      // In minute steps: every tick may only fire once the answer of the previous one is in.
      await expect
        .poll(async () => {
          await page.clock.runFor(60_000);
          return backend.of('detail', 1).length;
        })
        .toBeGreaterThan(asked);
      await expect(row(page, 1).getByText('25 Nutzungen')).toBeVisible();
    });
  });

  test.describe('header count and overview (AK40, AK42)', () => {
    test('the count in the header: 3 shows the number, then three more asks in 15 minutes and nothing else', async ({
      page,
    }) => {
      await page.addInitScript(() => {
        const Stub = window.EventSource;
        const counted = window as unknown as { __streams: number };
        counted.__streams = 0;
        window.EventSource = class extends Stub {
          constructor(url: string | URL) {
            super(url);
            counted.__streams += 1;
          }
        } as typeof EventSource;
      });
      await page.clock.install();
      const summary = await mockWishSummaryLive(page, 3);
      await mockWishMine(page, []);
      let mineAsked = 0;
      await page.route('**/api/channels/mine', (route) => {
        mineAsked += 1;
        return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      });

      await page.goto('/my-wishes');
      const link = page.locator('header a[href="/my-wishes"]');
      await expect(link).toHaveAccessibleName('Wünsche, 3 mit Handlungsbedarf');
      expect(summary.requests()).toBe(1);
      const streams = await page.evaluate(
        () => (window as unknown as { __streams: number }).__streams,
      );

      for (const total of [2, 3, 4]) {
        await page.clock.runFor(5 * 60_000);
        await expect.poll(() => summary.requests()).toBe(total);
      }
      expect(mineAsked).toBe(0);
      expect(
        await page.evaluate(() => (window as unknown as { __streams: number }).__streams),
      ).toBe(streams);
    });

    test('a count of 0 shows the link without a number', async ({ page }) => {
      await mockWishSummaryLive(page, 0);
      await mockMyChannels(page, []);
      await page.goto('/');
      const link = page.locator('header a[href="/my-wishes"]');
      await expect(link).toBeVisible();
      await expect(link).toHaveAccessibleName('Wünsche');
    });

    test('wishes_disabled: no link at all, and the console stays clean of everything else', async ({
      page,
    }) => {
      const errors: string[] = [];
      page.on('console', (message) => {
        if (message.type() === 'error' && !isWishSummaryNotFound(message)) {
          errors.push(message.text());
        }
      });
      await mockMyChannels(page, []);
      await page.goto('/');
      await expect(page.locator('header a[href="/my-wishes"]')).toHaveCount(0);
      expect(errors).toEqual([]);
    });

    test('the overview lists "Handlungsbedarf" before "Probezeit" with the channel per row; a click opens the tab with the row in focus or pinned (AK42)', async ({
      page,
    }) => {
      const action = buildWish(5, {
        status: 'presented',
        statusReason: 'expired',
        alias: 'Entscheiden',
      });
      const probation = buildWish(6, {
        alias: 'Bewaehren',
        probationStartsAtUtc: wishInstant(-1),
        expiresAtUtc: wishInstant(2),
      });
      await mockWishSummaryLive(page, 1);
      // The server's order puts the probation wish first; the page groups.
      await mockWishMine(page, [
        { channelName: 'handofblood', wish: probation },
        { channelName: CHANNEL, wish: action },
      ]);
      const own = await mockWishBackend(page, CHANNEL, { open: [action] });
      await mockChannelPermissions(page, 'handofblood', { wishesEnabled: true });
      const other = await mockWishBackend(page, 'handofblood', {});
      other.details.set(6, probation);

      await mockMyChannels(page, []);
      await page.goto('/');
      await page.locator('header a[href="/my-wishes"]').click();
      await expect(page).toHaveURL(/\/my-wishes$/);

      const action_ = page.locator('section[aria-labelledby="my-wishes-action-title"]');
      const probation_ = page.locator('section[aria-labelledby="my-wishes-probation-title"]');
      await expect(action_).toContainText('Entscheiden');
      await expect(action_).toContainText('#sensitron');
      await expect(probation_).toContainText('Bewaehren');
      await expect(probation_).toContainText('#handofblood');
      const actionBox = await action_.boundingBox();
      const probationBox = await probation_.boundingBox();
      expect(actionBox!.y).toBeLessThan(probationBox!.y);

      // A wish on the loaded list is focused in place.
      await action_.getByRole('link', { name: 'Entscheiden' }).click();
      await expect(page).toHaveURL(new RegExp(`${URL_WISHES}\\?wish=5$`));
      await expect(row(page, 5)).toBeFocused();
      expect(own.of('list').length).toBeGreaterThan(0);

      // One that no loaded list holds is loaded on its own and pinned.
      await page.goto('/my-wishes');
      await page
        .locator('section[aria-labelledby="my-wishes-probation-title"]')
        .getByRole('link', { name: 'Bewaehren' })
        .click();
      await expect(page).toHaveURL(/\/channels\/handofblood\/wishes\?wish=6$/);
      await expect(section(page, 'pinned').locator('[data-wish-id="6"]')).toBeVisible();
    });

    test('wishes without a stored picture, with reduced motion: only the still is asked for, a failed one leaves the plate empty (Sol-P3-13)', async ({
      page,
    }) => {
      await page.emulateMedia({ reducedMotion: 'reduce' });
      const requested: string[] = [];
      let failedOnce = false;
      await page.route('https://cdn.7tv.app/**', (route) => {
        requested.push(route.request().url());
        if (!failedOnce) {
          failedOnce = true;
          return route.abort('failed');
        }
        return fulfillCdnStub(route);
      });
      await mockWishBackend(page, CHANNEL, {
        running: [
          buildWish(1, { alias: 'OhneBild1', imageUrl: null }),
          buildWish(2, { alias: 'OhneBild2', imageUrl: null }),
        ],
      });

      await page.goto(URL_WISHES);
      await expect(row(page, 1)).toBeVisible();
      await expect(row(page, 2)).toBeVisible();
      await expect.poll(() => requested.length).toBeGreaterThanOrEqual(2);

      // The first still failed: its plate stays empty (hidden image), no other rendition was tried.
      await expect(row(page, 1).locator('img')).toBeHidden();
      await expect(row(page, 2).locator('img')).toBeVisible();
      for (const url of requested) {
        expect(url).toMatch(/_static\.webp$/);
      }
      expect(requested.filter((url) => url.includes(wishUlid('W1')))).toHaveLength(1);
    });

    // AK44, decided by the operator 2026-11-19 (DECISIONS (z), option B): below `sm` the count gives way
    // while a status badge stands, so the 360 px row holds together; the account menu entry is the way.
    test('at 360 px with a status badge the header count gives way and nothing overlaps (AK44)', async ({
      page,
    }) => {
      await page.setViewportSize({ width: 360, height: 800 });
      await mockWorkerHealth(page, 'disconnected');
      await mockWishSummaryLive(page, 3);
      await mockMyChannels(page, []);
      await page.goto('/');

      const targets = [
        page.locator('header a[href="/my-votings"]'),
        page.locator('header app-account-menu [aria-haspopup="dialog"]'),
        page.locator('header app-health-marker app-status-badge > span'),
      ];
      const boxes = [];
      for (const target of targets) {
        await expect(target).toBeVisible();
        const box = await target.boundingBox();
        expect(box).not.toBeNull();
        boxes.push(box!);
        expect(box!.x).toBeGreaterThanOrEqual(0);
        expect(box!.x + box!.width).toBeLessThanOrEqual(360);
      }
      await expect(page.locator('header a[href="/my-wishes"]')).toBeHidden();
      for (let first = 0; first < boxes.length; first++) {
        for (let second = first + 1; second < boxes.length; second++) {
          const a = boxes[first];
          const b = boxes[second];
          const overlap =
            a.x < b.x + b.width - 0.5 &&
            b.x < a.x + a.width - 0.5 &&
            a.y < b.y + b.height - 0.5 &&
            b.y < a.y + a.height - 0.5;
          expect(overlap, `targets ${first} and ${second} overlap: ${JSON.stringify([a, b])}`).toBe(
            false,
          );
        }
      }
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth),
      ).toBeLessThanOrEqual(0);

      // The way to the page stays open: the account menu entry.
      await page.locator('header app-account-menu [aria-haspopup="dialog"]').click();
      await expect(page.getByRole('link', { name: /Wünsche|Wishes/ })).toBeVisible();
    });

    test('at 360 px without a badge the count shows whole; from sm on it stays with a badge', async ({
      page,
    }) => {
      await page.setViewportSize({ width: 360, height: 800 });
      await mockWorkerHealth(page, 'connected');
      await mockWishSummaryLive(page, 3);
      await mockMyChannels(page, []);
      await page.goto('/');
      const link = page.locator('header a[href="/my-wishes"]');
      await expect(link).toBeVisible();
      const box = await link.boundingBox();
      expect(box!.x).toBeGreaterThanOrEqual(0);
      expect(box!.x + box!.width).toBeLessThanOrEqual(360);

      await mockWorkerHealth(page, 'disconnected');
      await page.setViewportSize({ width: 800, height: 800 });
      await page.reload();
      await expect(page.locator('header app-health-marker')).toBeVisible();
      await expect(link).toBeVisible();
    });
  });
});
