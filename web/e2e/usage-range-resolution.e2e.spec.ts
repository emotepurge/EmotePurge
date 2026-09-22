import { Page, expect, test } from '@playwright/test';

import {
  AUTH_USER,
  installLiveStub,
  mockActiveEmoteSet,
  mockAuthMe,
  mockChannelPermissions,
  mockChannelStatus,
  mockMyChannels,
  mockUsageChannelSeries,
  mockUsageTotals,
  mockWorkerHealth,
} from './support/mocks';

/**
 * "All time" cannot be turned into a request until the set status names when tracking began, and
 * that answer arrives over the network. What the page does in the meantime is not a detail: firing
 * against the placeholder span asked for a year of rows on a channel counted for days, and left two
 * answers racing for the same signal — the sidecar takes its axis from whichever range the winning
 * response echoes, so a superseded answer drew a year-wide curve with no way to tell.
 *
 * Only a browser shows this. The ordering lives in an effect graph, and the assertion is about the
 * requests that leave the page, which no unit test of the pure parts can observe.
 */

const EMOTES = [
  {
    emoteId: 'e1',
    emoteName: 'catJAM',
    sevenTvEmoteId: '7tv-1',
    imageUrl: 'https://cdn.7tv.app/emote/1/2x.webp',
    totalUseCount: 900,
    lastUsedDate: '2026-07-14',
  },
];

/** Mirrors toIsoDate/daysAgo in date-range-menu.ts, so the expectation is computed the same way the
 *  page computes the value — a hardcoded date would rot once it drifts past the 365-day floor. */
function isoDaysAgo(days: number): string {
  const date = new Date();
  date.setDate(date.getDate() - days);
  return date.toISOString().slice(0, 10);
}

interface RangeRequests {
  series: URLSearchParams[];
  totals: URLSearchParams[];
  activeSet: number;
}

function recordRangeRequests(page: Page): RangeRequests {
  const recorded: RangeRequests = { series: [], totals: [], activeSet: 0 };
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.pathname.endsWith('/usage-stats/series')) {
      recorded.series.push(url.searchParams);
    }
    if (url.pathname.endsWith('/usage-stats/totals')) {
      recorded.totals.push(url.searchParams);
    }
    if (url.pathname.endsWith('/emotes/active-set')) {
      recorded.activeSet += 1;
    }
  });
  return recorded;
}

async function mockWorkspace(page: Page): Promise<void> {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page);
  await installLiveStub(page);
  await mockMyChannels(page, [
    { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
  ]);
  await mockChannelPermissions(page, 'sensitron');
  await mockChannelStatus(page, 'sensitron');
  await mockUsageTotals(page, 'sensitron', EMOTES);
  await mockUsageChannelSeries(page, 'sensitron', { '7tv-1': [[2, 900]] });
}

test.describe('"all time" range resolution', () => {
  test('asks once, against the tracking start rather than the placeholder year', async ({
    page,
  }) => {
    const trackedSince = isoDaysAgo(12);
    await mockWorkspace(page);
    await mockActiveEmoteSet(page, 'sensitron', 'set-1', {
      capacity: 1000,
      occupiedSlots: 1,
      trackedSince: `${trackedSince}T09:14:00Z`,
    });
    const requests = recordRangeRequests(page);

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^catJAM ·/ })).toBeVisible();

    expect(requests.series.map((params) => params.get('from'))).toEqual([trackedSince]);
    expect(requests.totals.map((params) => params.get('from'))).toEqual([trackedSince]);
    // The set status is what resolves "all time" against the tracking start in the first place, but
    // once it has landed for this channel, the second effect run (the corrected range) must not ask
    // for it again — that second active-set request is exactly the client amplifier #33 measured.
    expect(requests.activeSet).toBe(1);
  });

  test('loads anyway when the set status fails, rather than waiting for a range forever', async ({
    page,
  }) => {
    // The failure mode the wait introduces: nothing else resolves the range, so a page that only
    // resumed on a successful status would sit in its skeleton for good.
    await mockWorkspace(page);
    await page.route('**/api/channels/sensitron/emotes/active-set', (route) =>
      route.fulfill({ status: 500 }),
    );
    const requests = recordRangeRequests(page);

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^catJAM ·/ })).toBeVisible();

    // Without a tracking start the placeholder span is the honest answer — it is the widest range
    // the endpoint accepts, and for any channel younger than that it returns the same rows.
    expect(requests.totals.map((params) => params.get('from'))).toEqual([isoDaysAgo(365)]);
  });
});

test.describe('a failed set-status request stays retryable', () => {
  /**
   * The set status is bound to the channel rather than the range (see the "asks once" test above),
   * on purpose — a range correction must not repeat the request. The failure path used to bind the
   * channel exactly the same way on a failed attempt, which silenced every later request for that
   * channel, the refresh button included, until the user navigated away and back. This asserts the
   * one recovery path a stuck page has: the refresh button must still ask again.
   */
  test('the refresh button asks again after the first active-set request failed', async ({
    page,
  }) => {
    await mockWorkspace(page);

    let activeSetCalls = 0;
    await page.route('**/api/channels/sensitron/emotes/active-set', (route) => {
      activeSetCalls += 1;
      if (activeSetCalls === 1) {
        return route.fulfill({ status: 500 });
      }
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          activeEmoteSetId: 'set-1',
          capacity: 1000,
          occupiedSlots: 1,
          trackedSince: `${isoDaysAgo(12)}T09:14:00Z`,
          syncFailureReason: null,
          lastSyncAttemptAtUtc: null,
        }),
      });
    });

    await page.goto('/channels/sensitron/usage-stats');
    await expect(page.getByRole('status', { name: 'Lädt…' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^catJAM ·/ })).toBeVisible();

    // The slot-budget bar only renders once a set status has landed; the first request just failed,
    // so it must still be absent, and nothing else should have retried it on its own yet.
    await expect(page.getByRole('progressbar', { name: 'Auslastung des Emote-Sets' })).toHaveCount(
      0,
    );
    expect(activeSetCalls).toBe(1);

    await page.getByRole('button', { name: 'Aktualisieren' }).click();

    // The bug: a failed attempt used to mark the channel "resolved" exactly like a successful one,
    // so this explicit retry was silently swallowed and the bar never appeared.
    await expect(
      page.getByRole('progressbar', { name: 'Auslastung des Emote-Sets' }),
    ).toBeVisible();
    expect(activeSetCalls).toBe(2);
  });
});
