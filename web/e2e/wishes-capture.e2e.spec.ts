import { expect, test } from './support/test';
import type { Page } from './support/test';

import {
  AUTH_USER,
  buildWish,
  buildWishReward,
  buildWishSlot,
  emitLive,
  installLiveStub,
  mockAuthMe,
  mockChannelPermissions,
  mockSevenTvEmoteReads,
  mockVoteSessionList,
  mockWishBackend,
  mockWorkerHealth,
  wishInstant,
  wishUlid,
} from './support/mocks';

const CHANNEL = 'sensitron';
const URL_WISHES = `/channels/${CHANNEL}/wishes`;
const URL_SETTINGS = `/channels/${CHANNEL}/settings`;

const section = (page: Page, view: 'open' | 'running' | 'ended' | 'pending' | 'decide') =>
  page.locator(
    `section[aria-labelledby="wishes-${view}-title"], [aria-labelledby="wishes-${view}-title"]`,
  );

const row = (page: Page, id: number) => page.locator(`[data-wish-id="${id}"]`);

/** An open wish from the chat: no slot, no schedule, the redeemed text stored. */
function pendingWish(id: number, overrides: Record<string, unknown> = {}) {
  return buildWish(id, {
    status: 'pending',
    source: 'irc',
    linkOrigin: null,
    sevenTvEmoteId: '',
    emoteName: '',
    alias: '',
    approvedAtUtc: null,
    startsAtUtc: null,
    startsAtAuthoritative: false,
    probationStartsAtUtc: null,
    expiresAtUtc: null,
    slotId: null,
    slotAddedAtUtc: null,
    imageUrl: null,
    requesterLogin: 'viewer_one',
    rewardLabel: 'Emote wünschen',
    matchKind: 'name',
    requestText: 'catJAM bitte!',
    requestedName: 'catJAM',
    ...overrides,
  });
}

test.describe('wish emotes: chat capture (#486)', () => {
  test.beforeEach(async ({ page }) => {
    await mockAuthMe(page, AUTH_USER);
    await mockWorkerHealth(page, 'connected');
    await installLiveStub(page);
    await mockChannelPermissions(page, CHANNEL, { wishesEnabled: true, wishCaptureEnabled: true });
    await mockVoteSessionList(page, CHANNEL, []);
  });

  test.describe('learning a reward (settings tab)', () => {
    const rewards = (page: Page) => page.locator('app-wish-rewards-section');
    /** The waiting sentence stands twice (status region and visible twin); the visible one is last. */
    const waiting = (page: Page) =>
      rewards(page).getByText('Anlernen läuft: Warte auf eine Einlösung.').last();

    test('E1: waits with a countdown, shows the candidate, sends exactly one confirm after the click', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        rewards: { rewards: [buildWishReward(1, { label: 'Emote wünschen' })] },
      });
      await page.goto(URL_SETTINGS);
      await expect(rewards(page).getByText('Emote wünschen')).toBeVisible();

      await rewards(page).getByRole('button', { name: 'Belohnung anlernen' }).click();
      await expect(waiting(page)).toBeVisible();
      await expect(
        rewards(page)
          .getByText(/Noch \d+:\d\d/)
          .first(),
      ).toBeVisible();
      await expect.poll(() => backend.of('learning-start').length).toBe(1);

      const session = backend.rewards.learning as { sessionId: string };
      backend.rewards.learning = {
        ...backend.rewards.learning,
        candidate: {
          candidateId: 'candidate-1',
          text: 'https://7tv.app/emotes/01FDGSXVJG0004RTSM3QHRP7QN bitte!',
          login: 'viewer_one',
          seenAtUtc: wishInstant(0),
        },
      };
      await emitLive(page, { type: 'wish.changed', channel: CHANNEL });
      await expect(rewards(page).getByText('viewer_one', { exact: false }).first()).toBeVisible();
      await expect(
        rewards(page)
          .getByText(/bitte!/)
          .first(),
      ).toBeVisible();
      // Seeing a candidate is not confirming it.
      expect(backend.of('learning-confirm')).toHaveLength(0);

      await rewards(page).getByRole('button', { name: 'Bestätigen', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByLabel('Bezeichnung', { exact: true }).fill('Emote wünschen 2');
      await dialog.getByLabel('Standardlaufzeit neuer Wünsche in Tagen').fill('21');
      expect(backend.of('learning-confirm')).toHaveLength(0);
      await dialog.getByRole('button', { name: 'Speichern' }).click();

      await expect.poll(() => backend.of('learning-confirm').length).toBe(1);
      expect(backend.of('learning-confirm')[0].body).toEqual({
        sessionId: session.sessionId,
        candidateId: 'candidate-1',
        label: 'Emote wünschen 2',
        defaultDurationDays: 21,
      });
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(rewards(page).getByText('Emote wünschen 2', { exact: true })).toBeVisible();
    });

    test('E18: a later read refused with 403 removes the rewards and the candidate from the page', async ({
      page,
    }) => {
      await mockWishBackend(page, CHANNEL, {
        rewards: {
          rewards: [buildWishReward(1, { label: 'Emote wünschen' })],
          learning: {
            sessionId: 'session-1',
            startedAtUtc: wishInstant(0),
            expiresAtUtc: new Date(Date.now() + 8 * 60_000).toISOString(),
            candidate: {
              candidateId: 'candidate-1',
              text: 'catJAM bitte!',
              login: 'viewer_one',
              seenAtUtc: wishInstant(0),
            },
          },
        },
      });
      await page.goto(URL_SETTINGS);
      await expect(rewards(page).getByText('viewer_one', { exact: false }).first()).toBeVisible();

      // Twitch no longer confirms the manager for the channel: the read answers 403 without a body.
      await page.route(
        (url) => url.pathname === `/api/channels/${CHANNEL}/wishes/rewards`,
        (route) => route.fulfill({ status: 403, body: '' }),
      );
      await emitLive(page, { type: 'wish.changed', channel: CHANNEL });

      await expect(
        rewards(page).getByText('Die Belohnungen dieses Kanals kannst du nicht sehen.', {
          exact: false,
        }),
      ).toBeVisible();
      await expect(rewards(page).getByText('viewer_one', { exact: false })).toHaveCount(0);
      await expect(rewards(page).getByText('catJAM bitte!', { exact: false })).toHaveCount(0);
      await expect(rewards(page).getByText('Emote wünschen', { exact: true })).toHaveCount(0);
      await expect(rewards(page).getByRole('button', { name: 'Bestätigen' })).toHaveCount(0);
    });

    test('E2: a session that runs out says so, allows a new start and never sends a confirm', async ({
      page,
    }) => {
      // Before goto, or the zoneless change detection races the fake timers. The few real
      // milliseconds up to the click do not matter against ten minutes; `runFor` does the waiting.
      await page.clock.install();
      const backend = await mockWishBackend(page, CHANNEL);
      await page.goto(URL_SETTINGS);
      await rewards(page).getByRole('button', { name: 'Belohnung anlernen' }).click();
      await expect(waiting(page)).toBeVisible();

      await page.clock.runFor(10 * 60_000 + 5_000);

      await expect(
        rewards(page)
          .getByText('Das Anlernen ist abgelaufen, ohne dass eine Belohnung bestätigt wurde.')
          .last(),
      ).toBeVisible();
      await expect(rewards(page).getByRole('button', { name: 'Belohnung anlernen' })).toBeEnabled();
      expect(backend.of('learning-confirm')).toHaveLength(0);
    });

    test('E16: on a quiet channel a healthy worker never shows the capture warning while the tab stays open', async ({
      page,
    }) => {
      // Before goto (zoneless change detection vs. fake timers); `runFor`, not `fastForward`, so the
      // one-second clock of the section fires every time.
      await page.clock.install();
      const backend = await mockWishBackend(page, CHANNEL);
      const browserNow = () => page.evaluate(() => Date.now());
      backend.rewards.captureAliveAtUtc = new Date(await browserNow()).toISOString();
      await page.goto(URL_SETTINGS);
      await expect(rewards(page).getByRole('button', { name: 'Belohnung anlernen' })).toBeVisible();

      // Ten quiet minutes: the worker stamps once a minute, and no `wish.changed` is sent.
      for (let minute = 0; minute < 10; minute++) {
        backend.rewards.captureAliveAtUtc = new Date(await browserNow()).toISOString();
        await page.clock.runFor(60_000);
        await expect(
          rewards(page).getByText('Die Erfassung im Chat hat sich seit mehr als drei Minuten'),
        ).toHaveCount(0);
      }
      // Read again for the stamp, but not every second.
      const reads = backend.of('rewards-read').length;
      expect(reads).toBeGreaterThan(1);
      expect(reads).toBeLessThanOrEqual(11);
    });

    test('E3: a second start while one runs (409) is named and the state is read again', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL);
      const other = {
        sessionId: 'session-of-another-manager',
        startedAtUtc: wishInstant(0),
        expiresAtUtc: new Date(Date.now() + 9 * 60_000).toISOString(),
        candidate: null,
      };
      backend.onCommand = (call, state) => {
        if (call.kind !== 'learning-start') {
          return undefined;
        }
        // The other manager's session exists by the time this one asks.
        state.rewards.learning = other;
        return { status: 409, body: { errorCode: 'wish_learning_active' } };
      };
      await page.goto(URL_SETTINGS);
      await rewards(page).getByRole('button', { name: 'Belohnung anlernen' }).click();

      await expect(rewards(page).getByRole('alert')).toBeVisible();
      await expect(waiting(page)).toBeVisible();
      expect(backend.of('rewards-read').length).toBeGreaterThanOrEqual(2);
    });

    test('E12: a confirm against a stale state (409 wish_learning_stale) keeps the dialog on its snapshot and adds no reward', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL);
      const candidate = (id: string) => ({
        candidateId: id,
        text: 'catJAM',
        login: 'viewer_one',
        seenAtUtc: wishInstant(0),
      });
      backend.rewards.learning = {
        sessionId: 'session-a',
        startedAtUtc: wishInstant(0),
        expiresAtUtc: new Date(Date.now() + 9 * 60_000).toISOString(),
        candidate: candidate('candidate-1'),
      };
      await page.goto(URL_SETTINGS);
      await rewards(page).getByRole('button', { name: 'Bestätigen', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await dialog.getByLabel('Bezeichnung', { exact: true }).fill('Alte Belohnung');

      // A newer session replaced the one shown while the dialog stands open.
      backend.rewards.learning = {
        sessionId: 'session-b',
        startedAtUtc: wishInstant(0),
        expiresAtUtc: new Date(Date.now() + 9 * 60_000).toISOString(),
        candidate: candidate('candidate-2'),
      };
      await dialog.getByRole('button', { name: 'Speichern' }).click();

      await expect.poll(() => backend.of('learning-confirm').length).toBe(1);
      // The ids of the state the dialog showed, not of the newer one.
      expect(backend.of('learning-confirm')[0].body).toMatchObject({
        sessionId: 'session-a',
        candidateId: 'candidate-1',
      });
      await expect(dialog.getByRole('alert')).toBeVisible();
      await expect(dialog.getByRole('button', { name: 'Speichern' })).toBeDisabled();
      await dialog.getByRole('button', { name: 'Neu laden' }).click();
      await page.keyboard.press('Escape');
      expect(backend.rewards.rewards).toHaveLength(0);
      await expect(rewards(page).getByText('Alte Belohnung', { exact: true })).toHaveCount(0);
    });
  });

  test('E10: capture off: no rewards section, no read of the rewards, an open wish stays operable', async ({
    page,
  }) => {
    await mockChannelPermissions(page, CHANNEL, { wishesEnabled: true, wishCaptureEnabled: false });
    const backend = await mockWishBackend(page, CHANNEL, {
      open: [pendingWish(1, { sevenTvEmoteId: wishUlid('O1'), requestText: 'noch offen' })],
    });
    await page.goto(URL_SETTINGS);
    await expect(page.getByRole('heading', { name: 'Wünsche' })).toBeVisible();
    await expect(page.locator('app-wish-rewards-section')).toHaveCount(0);

    await page.goto(URL_WISHES);
    const open = row(page, 1);
    await expect(open).toBeVisible();
    await open.getByRole('button', { name: 'Ablehnen' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Ablehnen' }).click();
    await expect.poll(() => backend.of('reject', 1).length).toBe(1);
    expect(backend.of('rewards-read')).toHaveLength(0);
  });

  test.describe('the work list (wishes page)', () => {
    test('E4: an open wish turns active live and moves to the running ones with "Zuordnung lösen"', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [pendingWish(1, { requestText: 'catJAM bitte!' })],
      });
      await page.goto(URL_WISHES);
      await expect(section(page, 'pending').locator('[data-wish-id="1"]')).toBeVisible();
      await expect(row(page, 1).getByText('catJAM bitte!')).toBeVisible();

      const linked = buildWish(1, {
        source: 'irc',
        linkOrigin: 'auto',
        alias: 'catJAM',
        emoteName: 'catJAM',
        requestText: 'catJAM bitte!',
        matchKind: 'name',
      });
      backend.open = [];
      backend.running = [linked];
      await emitLive(page, { type: 'wish.changed', channel: CHANNEL });

      await expect(section(page, 'running').locator('[data-wish-id="1"]')).toBeVisible();
      await expect(row(page, 1).getByRole('button', { name: 'Zuordnung lösen' })).toBeVisible();
      await expect(section(page, 'pending').locator('[data-wish-id="1"]')).toHaveCount(0);
    });

    test('E5: rejecting names the refund in Twitch; the wish ends under "Beendet"', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, { open: [pendingWish(1)] });
      await page.goto(URL_WISHES);
      await row(page, 1).getByRole('button', { name: 'Ablehnen' }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toContainText('Die Kanalpunkte erstattest du in Twitch');
      expect(backend.of('reject')).toHaveLength(0);
      await dialog.getByRole('button', { name: 'Ablehnen' }).click();

      await expect.poll(() => backend.of('reject', 1).length).toBe(1);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(row(page, 1)).toHaveCount(0);
      expect(backend.ended.map((w) => w.id)).toEqual([1]);
      expect(backend.ended[0]['status']).toBe('rejected');
    });

    test('E6: approving a wish without an emote is locked with a reason until a link is given', async ({
      page,
    }) => {
      const id = wishUlid('A6');
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [
          pendingWish(1, {
            statusReason: 'unparsed',
            matchKind: 'none',
            requestedName: null,
            requestText: 'das mit dem Frosch',
          }),
        ],
      });
      await mockSevenTvEmoteReads(page, { [id]: { name: 'FrogDance' } });
      await page.goto(URL_WISHES);
      await row(page, 1).getByRole('button', { name: 'Freigeben', exact: true }).click();
      const dialog = page.getByRole('dialog');
      const submit = dialog.getByRole('button', { name: 'Freigeben', exact: true });

      await expect(submit).toBeDisabled();
      await expect(dialog).toContainText('Ohne Emote lässt sich der Wunsch nicht freigeben');
      await dialog.getByLabel('7TV-Link oder Emote-ID').fill(`https://7tv.app/emotes/${id}`);
      await expect(dialog.getByText('Gefunden: FrogDance')).toBeVisible();
      await expect(submit).toBeEnabled();
      expect(backend.of('approve')).toHaveLength(0);
      await submit.click();

      await expect.poll(() => backend.of('approve', 1).length).toBe(1);
      expect(backend.of('approve', 1)[0].body).toMatchObject({
        sevenTvEmoteId: id,
        emoteName: 'FrogDance',
        alias: 'FrogDance',
      });
      await expect(page.getByRole('dialog')).toHaveCount(0);
    });

    test('E7: approving into a set that holds the emote (409) offers to take the entry over', async ({
      page,
    }) => {
      const id = wishUlid('A7');
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [pendingWish(1, { sevenTvEmoteId: id, matchKind: 'link', requestedName: null })],
        slots: [buildWishSlot('slot-1', { sevenTvEmoteId: id, alias: 'AlreadyThere' })],
      });
      backend.onCommand = (call) =>
        call.kind === 'approve'
          ? {
              status: 409,
              body: { errorCode: 'wish_emote_already_in_set', slotIds: ['slot-1'], wishId: null },
            }
          : undefined;
      await mockSevenTvEmoteReads(page, { [id]: { name: 'AlreadyThere' } });
      await page.goto(URL_WISHES);
      await row(page, 1).getByRole('button', { name: 'Freigeben', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByText('Gefunden: AlreadyThere')).toBeVisible();
      await dialog.getByRole('button', { name: 'Freigeben', exact: true }).click();

      await expect(dialog.getByText(/Das Emote ist schon im Set/).first()).toBeVisible();
      await dialog.getByRole('button', { name: 'Bestand übernehmen' }).click();
      // The adopt dialog for this very wish replaces the approve dialog.
      await expect(page.getByRole('dialog', { name: 'Mit Eintrag verknüpfen' })).toBeVisible();
      await expect(page.getByRole('dialog', { name: 'Wunsch freigeben' })).toHaveCount(0);
      await expect(page.getByRole('dialog').getByText('AlreadyThere')).toBeVisible();
    });

    test('E8: an "already running" hint extends the running wish; the page says so and the wish ends', async ({
      page,
    }) => {
      const id = wishUlid('A8');
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [
          pendingWish(1, {
            statusReason: 'already_running',
            sevenTvEmoteId: id,
            matchKind: 'link',
            requestedName: null,
          }),
        ],
        running: [buildWish(2, { sevenTvEmoteId: id, alias: 'catJAM', emoteName: 'catJAM' })],
      });
      await mockSevenTvEmoteReads(page, { [id]: { name: 'catJAM' } });
      await page.goto(URL_WISHES);
      await row(page, 1).getByRole('button', { name: 'Als Verlängerung freigeben' }).click();
      await page
        .getByRole('dialog')
        .getByRole('button', { name: 'Als Verlängerung freigeben' })
        .click();

      await expect.poll(() => backend.of('approve', 1).length).toBe(1);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.getByText(/Als Verlängerung verbucht/).first()).toBeVisible();
      expect(backend.ended.map((w) => w.id)).toEqual([1]);
      expect(backend.ended[0]['status']).toBe('merged');
      await expect(row(page, 1)).toHaveCount(0);
    });

    test('E17: an extension names the channel default it books and offers no duration to edit', async ({
      page,
    }) => {
      const id = wishUlid('A17');
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [
          pendingWish(1, {
            statusReason: 'already_running',
            sevenTvEmoteId: id,
            matchKind: 'link',
            requestedName: null,
            durationDays: 7,
          }),
        ],
        running: [buildWish(2, { sevenTvEmoteId: id, alias: 'catJAM', emoteName: 'catJAM' })],
        settings: { defaultDurationDays: 30 },
      });
      await mockSevenTvEmoteReads(page, { [id]: { name: 'catJAM' } });
      await page.goto(URL_WISHES);
      await row(page, 1).getByRole('button', { name: 'Als Verlängerung freigeben' }).click();
      const dialog = page.getByRole('dialog');

      await expect(dialog).toContainText('30 Tage, die Standardlaufzeit des Kanals');
      await expect(dialog.getByRole('textbox', { name: 'Laufzeit in Tagen' })).toHaveCount(0);
      await dialog.getByRole('button', { name: 'Als Verlängerung freigeben' }).click();

      await expect.poll(() => backend.of('approve', 1).length).toBe(1);
      expect(backend.of('approve', 1)[0].body).not.toHaveProperty('durationDays');
    });

    test('E14: no extension possible yet (409), then merged on the second try with the new expiry named', async ({
      page,
    }) => {
      const id = wishUlid('A14');
      const backend = await mockWishBackend(page, CHANNEL, {
        open: [
          pendingWish(1, {
            statusReason: 'already_running',
            sevenTvEmoteId: id,
            matchKind: 'link',
            requestedName: null,
          }),
        ],
        running: [buildWish(2, { sevenTvEmoteId: id, alias: 'catJAM', emoteName: 'catJAM' })],
      });
      let refused = false;
      backend.onCommand = (call) => {
        if (call.kind !== 'approve' || refused) {
          return undefined;
        }
        refused = true;
        return { status: 409, body: { errorCode: 'wish_running_not_extendable', wishId: 2 } };
      };
      await mockSevenTvEmoteReads(page, { [id]: { name: 'catJAM' } });
      await page.goto(URL_WISHES);
      await row(page, 1).getByRole('button', { name: 'Als Verlängerung freigeben' }).click();
      const dialog = page.getByRole('dialog');
      const submit = dialog.getByRole('button', { name: 'Als Verlängerung freigeben' });
      await submit.click();

      await expect(dialog).toContainText(
        'noch nicht im Set, deshalb lässt er sich nicht verlängern',
      );
      await expect(dialog.getByRole('button', { name: 'Zum Wunsch' })).toBeVisible();
      // The wish stays open with its hint while the dialog is up.
      expect(backend.open.map((w) => w.id)).toEqual([1]);
      expect(backend.open[0]['statusReason']).toBe('already_running');

      // The second try is allowed, and this time the running wish is extendable.
      await expect(submit).toBeEnabled();
      await submit.click();
      await expect.poll(() => backend.of('approve', 1).length).toBe(2);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.getByText(/Der laufende Wunsch läuft jetzt bis/).first()).toBeVisible();
      expect(backend.ended[0]['status']).toBe('merged');
    });

    test('E9: unlinking asks first and puts the wish back under "Offen"', async ({ page }) => {
      const backend = await mockWishBackend(page, CHANNEL, {
        running: [
          buildWish(1, {
            source: 'irc',
            linkOrigin: 'auto',
            alias: 'catJAM',
            requestText: 'catJAM bitte!',
          }),
        ],
      });
      await page.goto(URL_WISHES);
      await row(page, 1).getByRole('button', { name: 'Zuordnung lösen' }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog).toContainText('wieder offen');
      expect(backend.of('unlink')).toHaveLength(0);
      await dialog.getByRole('button', { name: 'Zuordnung lösen' }).click();

      await expect.poll(() => backend.of('unlink', 1).length).toBe(1);
      await expect(section(page, 'pending').locator('[data-wish-id="1"]')).toBeVisible();
      await expect(section(page, 'running').locator('[data-wish-id="1"]')).toHaveCount(0);
    });

    test('E13: editing an open wish without an emote shows no alias field and sends only the duration', async ({
      page,
    }) => {
      const backend = await mockWishBackend(page, CHANNEL, { open: [pendingWish(1)] });
      await page.goto(URL_WISHES);
      await row(page, 1).getByRole('button', { name: 'Bearbeiten' }).click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByLabel('Alias im Set')).toHaveCount(0);
      await dialog.getByLabel('Laufzeit in Tagen').fill('30');
      await dialog.getByRole('button', { name: 'Speichern' }).click();

      await expect.poll(() => backend.of('update', 1).length).toBe(1);
      expect(backend.of('update', 1)[0].body).toEqual({ durationDays: 30 });
    });

    test('E15: a live-added link wish whose first 7TV read failed gets its picture with the next reload', async ({
      page,
    }) => {
      const id = wishUlid('E15');
      const backend = await mockWishBackend(page, CHANNEL, { open: [] });
      const reads = await mockSevenTvEmoteReads(page, { [id]: { name: 'dinoCry' } });
      // The first read fails at 7TV; every later one goes to the stub above.
      let failed = false;
      await page.route('https://7tv.io/v4/gql', (route) => {
        if (failed) {
          return route.fallback();
        }
        failed = true;
        return route.fulfill({ status: 503, body: '' });
      });
      await page.goto(URL_WISHES);
      await expect(page.getByText('Gerade ist nichts zu entscheiden')).toBeVisible();

      const link = pendingWish(1, {
        sevenTvEmoteId: id,
        matchKind: 'link',
        requestText: `https://7tv.app/emotes/${id}`,
        requestedName: null,
      });
      backend.open = [link];
      await emitLive(page, { type: 'wish.changed', channel: CHANNEL });
      const picture = row(page, 1).locator('img');
      // Not proven still yet: only the rendition that cannot move.
      await expect(picture).toHaveAttribute('src', /_static/);
      await expect.poll(() => failed).toBe(true);
      expect(reads.requests).toEqual([]);

      // The next reload asks again for the row in view; the second read finds the still emote.
      await emitLive(page, { type: 'wish.changed', channel: CHANNEL });
      await expect.poll(() => reads.requests).toEqual([id]);
      await expect(picture).not.toHaveAttribute('src', /_static/);
      await expect(row(page, 1).getByText('dinoCry')).toBeVisible();
    });
  });
});
