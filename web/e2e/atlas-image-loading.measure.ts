import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
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
  type MockEmoteUsage,
} from './support/mocks';

/**
 * Measurement harness for how the usage atlas fetches its emote images. Not a test — it asserts
 * almost nothing and writes a JSON record instead, so it is named `*.measure.ts` and stays outside
 * the suite's `*.spec.ts` glob. `npm run e2e` never picks it up; it needs its own config.
 *
 * Why it lives in the repo at all: the 2026-08-29 investigation
 * (`docs/Untersuchung-Emote-Bildladen-2026-08-29.md`) found that single runs of this scenario vary
 * by more than the effect being measured. Anyone re-measuring needs repetitions, and rebuilding the
 * CDP plumbing from scratch to learn that again is the expensive part.
 *
 * Input: a JSON array of `{sid, name, url}` with real 7TV urls, so the CDN sees production-shaped
 * requests. Generate it from a dev database that has a large tracked channel:
 *
 *   docker exec emotepurge-dev-postgres psql -U emotepurge -d emotepurge -t -A -c \
 *     "select json_agg(row_to_json(t)) from (select e.\"SevenTvEmoteId\" as sid, e.\"Name\" as name,
 *      e.\"ImageUrl\" as url from \"Emotes\" e join \"Channels\" c on c.\"Id\"=e.\"ChannelId\"
 *      where c.\"ChannelName\"='<channel>' and e.\"ImageUrl\" is not null) t;" > /tmp/emotes.json
 *
 * Run (one variant, one pacing — repeat and compare medians, never a single run):
 *
 *   MEASURE_EMOTES=/tmp/emotes.json MEASURE_LABEL=before MEASURE_PAUSE=1200 \
 *     npx playwright test --config playwright.measure.config.ts
 *
 * Timings come from CDP rather than from the page: cdn.7tv.app sends no `Timing-Allow-Origin`, so
 * `PerformanceResourceTiming` reports DNS, connect and body size as 0 for every emote.
 *
 * A result with `censored > 0` is NOT comparable to one with `censored: 0`: it means some
 * requests were still in flight when the drain wait gave up, so they never got `ttfb`/`queued`/
 * `finishedAt` and are excluded from `ttfb`, `queueing`, `cellLatency` and `bytesTotal` — and
 * those are, by construction, the slowest requests of the run. The very first measurement round
 * fell into exactly this trap: a flat `waitForTimeout(3000)` after scrolling stood in for waiting
 * on the network, so 13-18% of requests in three of four baseline runs were still open and
 * silently dropped, biasing the comparison in the baseline's favour. Treat any `censored > 0`
 * result as unusable and re-run rather than trusting its percentiles.
 */

const OUT_DIR = process.env['MEASURE_OUT'] ?? '/tmp/webp-measure';
const EMOTES_FILE = process.env['MEASURE_EMOTES'] ?? `${OUT_DIR}/emotes.json`;

if (!fs.existsSync(EMOTES_FILE)) {
  throw new Error(
    `No emote fixture at ${EMOTES_FILE}. Set MEASURE_EMOTES, or generate one with the psql ` +
      `snippet in the header of this file.`,
  );
}

const raw: { sid: string; name: string; url: string }[] = JSON.parse(
  fs.readFileSync(EMOTES_FILE, 'utf8'),
);

// Counts descend so the Pareto banding has something to band, and so row composition is stable
// across runs — the comparison is worthless if the two variants render different rows.
const emotes: MockEmoteUsage[] = raw.map((emote, index) => ({
  emoteId: `emote-${index}`,
  emoteName: emote.name,
  sevenTvEmoteId: emote.sid,
  imageUrl: emote.url,
  totalUseCount: Math.max(1, 20000 - index * 30),
  lastUsedDate: '2026-08-25',
  previousWindowUseCount: 0,
  firstSeenAt: '2026-01-15',
}));

interface RequestRecord {
  url: string;
  sentAt: number;
  connectionId?: number;
  protocol?: string;
  reused?: boolean;
  status?: number;
  /** Header time once the request is on the wire. */
  ttfb?: number;
  /** Everything before that: browser-side queueing, which is what the investigation was about. */
  queued?: number;
  finishedAt?: number;
  bytes?: number;
}

async function openAtlas(page: Page): Promise<void> {
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
    occupiedSlots: emotes.length,
  });
  await mockUsageTotals(page, 'sensitron', emotes);
  await mockUsageChannelSeries(page, 'sensitron', {});
}

function percentile(sorted: number[], fraction: number): number {
  return sorted.length ? sorted[Math.floor((sorted.length - 1) * fraction)] : -1;
}

// Upper bound on how long we wait for in-flight requests to settle after scrolling stops. A
// fixed, shorter `waitForTimeout` was the original bug (see the header comment): it let the run
// finish while the slowest requests were still open, dropping them out of the metrics instead of
// counting them. 30s comfortably exceeds every `cellLatency.max` observed so far.
const DRAIN_TIMEOUT_MS = 30_000;
const DRAIN_POLL_INTERVAL_MS = 100;

test('atlas image loading under scrolling', async ({ page, context }) => {
  const cdp = await context.newCDPSession(page);
  await cdp.send('Network.enable');

  const byRequestId = new Map<string, RequestRecord>();
  // Requests that have been sent but have seen neither `loadingFinished` nor `loadingFailed` yet.
  // Draining this to empty (or timing out) before evaluating is what keeps the slowest requests
  // in the metrics instead of quietly falling out of them.
  const openRequestIds = new Set<string>();
  const start = Date.now();

  cdp.on('Network.requestWillBeSent', (event) => {
    if (event.request.url.includes('cdn.7tv.app')) {
      byRequestId.set(event.requestId, {
        url: event.request.url,
        sentAt: Date.now() - start,
      });
      openRequestIds.add(event.requestId);
    }
  });
  cdp.on('Network.responseReceived', (event) => {
    const record = byRequestId.get(event.requestId);
    if (!record) {
      return;
    }
    record.status = event.response.status;
    record.protocol = event.response.protocol;
    record.connectionId = event.response.connectionId;
    record.reused = event.response.connectionReused;
    const timing = event.response.timing;
    if (timing) {
      record.ttfb = Math.round(timing.receiveHeadersEnd - timing.sendEnd);
      record.queued = Math.round(timing.sendStart);
    }
  });
  cdp.on('Network.loadingFinished', (event) => {
    const record = byRequestId.get(event.requestId);
    if (record) {
      record.finishedAt = Date.now() - start;
      record.bytes = event.encodedDataLength;
    }
    openRequestIds.delete(event.requestId);
  });
  cdp.on('Network.loadingFailed', (event) => {
    const record = byRequestId.get(event.requestId);
    if (record) {
      record.status = -1;
    }
    openRequestIds.delete(event.requestId);
  });

  await openAtlas(page);
  await page.goto('/channels/sensitron/usage-stats');
  await expect(page.getByRole('heading', { name: 'Emote-Nutzung' })).toBeVisible();
  await page.waitForSelector('img[ng-img]', { timeout: 30_000 });
  await page.waitForTimeout(2500);

  // Walk the whole set the way a reader does, recording what is still blank at each stop.
  const pause = Number(process.env['MEASURE_PAUSE'] ?? '1200');
  const steps: { y: number; imgs: number; blank: number }[] = [];
  for (let step = 0; step < 60; step++) {
    await page.mouse.wheel(0, 600);
    await page.waitForTimeout(pause);
    steps.push(
      await page.evaluate(() => {
        const imgs = [...document.querySelectorAll('img[ng-img]')] as HTMLImageElement[];
        return {
          y: Math.round(window.scrollY),
          imgs: imgs.length,
          blank: imgs.filter((img) => {
            const box = img.getBoundingClientRect();
            const onScreen = box.bottom > 0 && box.top < window.innerHeight;
            return onScreen && !(img.complete && img.naturalWidth > 0);
          }).length,
        };
      }),
    );
    const atBottom = await page.evaluate(
      () => window.scrollY + window.innerHeight >= document.body.scrollHeight - 5,
    );
    if (atBottom) {
      break;
    }
  }
  // Wait for whatever is still in flight instead of assuming a fixed delay covers it — a request
  // that is still open when we evaluate has no `ttfb`/`queued`/`finishedAt`, and it is dropped
  // out of the metrics below rather than counted, which erases exactly the tail latency this
  // harness is meant to measure (see the header comment and the `censored` field).
  const drainStart = Date.now();
  while (openRequestIds.size > 0 && Date.now() - drainStart < DRAIN_TIMEOUT_MS) {
    await page.waitForTimeout(DRAIN_POLL_INTERVAL_MS);
  }
  const drainedAfterMs = Date.now() - drainStart;
  const censored = openRequestIds.size;
  if (censored > 0) {
    console.warn(
      `\n!!! ${censored} request(s) still in flight after a ${drainedAfterMs}ms drain wait — ` +
        `this result is CENSORED and not comparable to a censored: 0 run !!!\n`,
    );
  }

  const records = [...byRequestId.values()];
  const answered = records.filter((record) => record.ttfb !== undefined);
  const ttfbs = answered.map((record) => record.ttfb!).sort((a, b) => a - b);
  const queued = answered.map((record) => record.queued ?? 0).sort((a, b) => a - b);
  // What a reader actually sees: wall clock from "cell rendered, image requested" to "pixels
  // available". Unlike TTFB this includes the queueing, which is where the seconds came from.
  const cellLatency = answered
    .filter((record) => record.finishedAt !== undefined)
    .map((record) => record.finishedAt! - record.sentAt)
    .sort((a, b) => a - b);

  const label = process.env['MEASURE_LABEL'] ?? 'unlabelled';
  const result = {
    label,
    pause,
    emotesInPayload: emotes.length,
    totalRequests: records.length,
    // Requests still in flight when the drain wait gave up: excluded from ttfb/queueing/
    // cellLatency/bytesTotal below, by construction the slowest of the run. `censored > 0` means
    // this result is not comparable to a `censored: 0` one — see the header comment.
    censored,
    drainWaitMs: drainedAfterMs,
    statuses: records.reduce<Record<string, number>>((acc, record) => {
      const key = String(record.status ?? 'pending');
      acc[key] = (acc[key] ?? 0) + 1;
      return acc;
    }, {}),
    // Which 7TV size variant was actually fetched — the check that a loader change took effect.
    urlVariants: records.reduce<Record<string, number>>((acc, record) => {
      const key = record.url.split('/').pop() ?? '?';
      acc[key] = (acc[key] ?? 0) + 1;
      return acc;
    }, {}),
    protocols: [...new Set(answered.map((record) => record.protocol))],
    connections: {
      distinct: new Set(answered.map((record) => record.connectionId)).size,
      reused: answered.filter((record) => record.reused).length,
    },
    ttfb: {
      n: ttfbs.length,
      p50: percentile(ttfbs, 0.5),
      p90: percentile(ttfbs, 0.9),
      max: ttfbs[ttfbs.length - 1] ?? -1,
    },
    queueing: {
      p50: percentile(queued, 0.5),
      p90: percentile(queued, 0.9),
      max: queued[queued.length - 1] ?? -1,
    },
    cellLatency: {
      n: cellLatency.length,
      p50: percentile(cellLatency, 0.5),
      p90: percentile(cellLatency, 0.9),
      p99: percentile(cellLatency, 0.99),
      max: cellLatency[cellLatency.length - 1] ?? -1,
      over2s: cellLatency.filter((value) => value > 2000).length,
      over5s: cellLatency.filter((value) => value > 5000).length,
    },
    bytesTotal: answered.reduce((sum, record) => sum + (record.bytes ?? 0), 0),
    scroll: {
      steps: steps.length,
      maxBlankOnScreen: steps.reduce((max, step) => Math.max(max, step.blank), 0),
      blankPerStep: steps.map((step) => step.blank),
      maxImgsInDom: steps.reduce((max, step) => Math.max(max, step.imgs), 0),
    },
  };

  fs.mkdirSync(OUT_DIR, { recursive: true });
  fs.writeFileSync(`${OUT_DIR}/${label}-${pause}.json`, JSON.stringify(result, null, 2));
  console.log(`\n===== ${label} / pause ${pause} =====\n${JSON.stringify(result, null, 2)}\n`);
});
