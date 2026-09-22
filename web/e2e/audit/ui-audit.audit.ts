import * as fs from 'node:fs';
import * as path from 'node:path';

import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';

import { VoteSessionResult } from '../../src/app/core/voting/vote-session.model';
import {
  AUTH_USER,
  MockChannel,
  installLiveStub,
  mockActiveEmoteSet,
  mockAdminChannelDetail,
  mockAdminChannelList,
  mockAdminHealth,
  mockAdminRoster,
  mockAdminUsers,
  mockAuditLog,
  mockAuthMe,
  mockChannelAuditLog,
  mockChannelPermissions,
  mockChannelStatus,
  mockEmoteList,
  mockEmoteSetTargets,
  mockSetWarning,
  failLive,
  mockLiveQuota,
  mockUsageChannelSeries,
  mockUsageDaily,
  mockUsageTotals,
  mockWorkerHealth,
} from '../support/mocks';

// ---------------------------------------------------------------------------
// UI/UX-audit harness: screenshots every route in 5 viewports, both locales
// and edge states (empty/error/long names), plus JSON metrics on horizontal
// overflow and touch-target sizes. Not part of the regular e2e suite — run on
// demand via playwright.audit.config.ts. Output: web/.audit-out/ (gitignored).
// ---------------------------------------------------------------------------

const OUT = path.resolve(__dirname, '../../.audit-out');

const VIEWPORTS = [
  // `pointerCoarse` matches what the viewport ships with, not what the runner defaults to:
  // Chromium under Playwright reports `(pointer: fine)` regardless of viewport size unless touch
  // emulation is switched on. `PointerModeService` (core/pointer/pointer-mode.service.ts) gates the
  // 7TV mass-delete write paths on exactly that media query, so an unemulated `mobile` run rendered
  // a state no phone ever produces -- 360px with a mouse. Everything from `tablet` up keeps a fine
  // pointer on purpose: those are real trackpad/mouse widths, not just "not mobile".
  { name: 'mobile', width: 360, height: 800, pointerCoarse: true },
  // `narrow` closes a real gap, not a hypothetical one: a fine pointer below 768px is a state real
  // users produce every day -- a desktop window at 200% browser zoom, a window snapped to half the
  // screen, a laptop with DevTools docked to one side -- none of which switch the OS pointer to
  // touch. Before this entry, every fine-pointer-only surface (`@if (!isCoarse() ...)`, gated by
  // PointerModeService the same way the mobile-vs-coarse fix above is) was measured only at 768px
  // and up, so nothing here ever caught an overflow anywhere below that width. That is exactly the
  // gap #91 fell into: the header button group in `usage-stats-grid` overflowed at a measured 503px
  // window, a plain desktop browser at less than half a 1080p screen, and the harness had no
  // viewport in that range to catch it. 480 sits below Tailwind's `sm` breakpoint (640), so this
  // measures the unprefixed, mobile-first layout under a mouse -- the same layout `mobile` sees,
  // minus the touch affordances -- rather than re-measuring `sm:` rules `tablet` already covers.
  // 480 is only a single sample of that whole sub-768px range, though: an overflow that starts
  // somewhere between 480 and 768 still passes here unnoticed. This viewport narrows the blind
  // spot, it does not close it.
  { name: 'narrow', width: 480, height: 800, pointerCoarse: false },
  { name: 'tablet', width: 768, height: 1024, pointerCoarse: false },
  // Two desktop cases, because one cannot cover both ends of the lg range.
  // `desktop-narrow` is lg at its tightest: 1024 is exactly Tailwind's lg breakpoint, so both atlas
  // pages open their 16rem sidecar while the shell's 80rem cap does not bind yet -- 992px of content,
  // the geometry in which the sheet is squeezed hardest and wrapping breaks first. It is also what
  // the old 1280 case measured back when the cap was 64rem, so its metrics stay comparable.
  // `desktop` is the capped state: wider than the cap on purpose, because at 1280 the 80rem column
  // (#93) would fill the viewport edge to edge and the state that actually ships would appear in no
  // scenario at all. 1536 is the operator's own screen (1080p at 125%). Raise it with the cap.
  { name: 'desktop-narrow', width: 1024, height: 900, pointerCoarse: false },
  { name: 'desktop', width: 1536, height: 900, pointerCoarse: false },
] as const;

// Theme is the fourth dimension of the matrix. Running it in full would double a run that is
// already minutes long and gets started by hand — so light is desktop-only, the same trade the
// `en` locale already makes. Layout breaks are theme-independent (colour changes no box sizes);
// what light actually needs checking for is contrast, and that is measured per state either way.
// In the wave that first ships a theme, drop the skip below and look at both sets of screenshots.
const THEMES = ['dark', 'light'] as const;

// 1x1 PNG stub so emote images from cdn.7tv.app never hit the network.
const PNG_1X1 = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
  'base64',
);

async function stubSevenTvCdn(page: Page): Promise<void> {
  await page.route('**cdn.7tv.app/**', (route) =>
    route.fulfill({ status: 200, contentType: 'image/png', body: PNG_1X1 }),
  );
}

interface MyChannelsFlags {
  helixUnavailable?: boolean;
  reauthRequired?: boolean;
  sevenTvUnavailable?: boolean;
}

async function mockMyChannelsWithFlags(
  page: Page,
  channels: MockChannel[],
  flags: MyChannelsFlags = {},
): Promise<void> {
  await page.route('**/api/channels/mine', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        helixUnavailable: flags.helixUnavailable ?? false,
        reauthRequired: flags.reauthRequired ?? false,
        sevenTvUnavailable: flags.sevenTvUnavailable ?? false,
        channels: channels.map((c) => ({
          channelName: c.channelName,
          isBroadcaster: c.isBroadcaster ?? false,
          isModerator: c.isModerator ?? false,
          isSevenTvEditor: c.isSevenTvEditor ?? false,
          isTracked: c.isTracked ?? false,
          isBotActive: c.isBotActive ?? false,
          liveState: c.liveState ?? 'unknown',
        })),
        livePolledAtUtc: null,
      }),
    }),
  );
}

function json(route: Parameters<Parameters<Page['route']>[1]>[0], status: number, body: unknown) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

// --- data builders ---------------------------------------------------------

const LONG_EMOTE = 'xXSuperMegaLangesEmoteMitVielZuLangemNamenXx';

function usageEmotes(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    emoteId: `e${i + 1}`,
    emoteName: i === 1 ? LONG_EMOTE : i === 5 ? 'catJAMJAMJAMJAMJAMJAM' : `Emote${i + 1}PogU`,
    sevenTvEmoteId: `7tv-${i + 1}`,
    imageUrl: 'https://cdn.7tv.app/emote/stub/1x.webp',
    totalUseCount: Math.max(1, Math.round(98765 / (i + 1))),
  }));
}

/**
 * Daily curves for the sidecar sparkline, keyed by 7TV id like the real /series response. Only the emotes the
 * sidecar can land on need one — it opens on the busiest, which is `7tv-1` (`e1`).
 */
function usageSeries(): Record<string, [number, number][]> {
  return {
    '7tv-1': Array.from({ length: 18 }, (_, i) => [
      i * 1.5 + 1,
      40 + Math.round(90 * Math.sin(i / 2.2)),
    ])
      .filter(([, count]) => count > 0)
      .map(([day, count]) => [Math.round(day), count] as [number, number]),
    '7tv-2': [
      [3, 12],
      [4, 30],
      [11, 4],
    ],
  };
}

function voteSummaries(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    id: i + 1,
    title:
      i === 0
        ? 'Frühjahrsputz 2026 — welche Emotes fliegen endgültig raus? (Community-Abstimmung, Runde 2)'
        : `Emote-Voting Runde ${i + 1}`,
    allowedVoterRoles: i % 2 === 0 ? 1 : 8,
    isActive: i < 3,
    startedAt: '2026-07-01T12:00:00Z',
    endedAt: i < 3 ? null : '2026-07-10T12:00:00Z',
    // Mixed ballot scopes: curated subsets and whole-set (null) sessions side by side.
    emoteCount: i % 3 === 0 ? null : 10 + i,
    // Second card carries the "hidden" badge next to the active one — the widest badge row.
    hideResultsUntilEnd: i === 1,
  }));
}

interface VoteResultsOptions {
  // false = the voter view: the server withholds usage entirely (null, not 0).
  withUsage?: boolean;
  // Marks the first two emotes as archived subset members ("no longer in the set" badge).
  withArchived?: boolean;
  voterCount?: number;
  count?: number;
  // A running secret-ballot session. 'voter' is what the server sends to non-managers: all three
  // tally fields null, so the cards render the "Ergebnis verborgen" line and the vote buttons lose
  // their counts. 'manager' keeps the numbers — the flag is set, but this viewer sees through it.
  hidden?: 'voter' | 'manager';
}

function voteResults(sessionId: number, isActive: boolean, options: VoteResultsOptions = {}) {
  const { withUsage = true, withArchived = false, voterCount = 25, count = 12, hidden } = options;
  const talliesWithheld = hidden === 'voter';
  return {
    sessionId,
    title:
      'Frühjahrsputz 2026 — welche Emotes fliegen endgültig raus? (Community-Abstimmung, Runde 2)',
    isActive,
    startedAt: '2026-07-01T12:00:00Z',
    endedAt: isActive ? null : '2026-07-10T12:00:00Z',
    voterCount,
    hideResultsUntilEnd: hidden !== undefined,
    // Backend order: ascending net score, delete candidates first (name order when withheld).
    emotes: usageEmotes(count).map((e, i): VoteSessionResult => ({
      ...e,
      totalUseCount: withUsage ? e.totalUseCount : null,
      keepVotes: talliesWithheld ? null : 2 + i * 4,
      deleteVotes: talliesWithheld ? null : 40 - i * 3,
      score: talliesWithheld ? null : -38 + i * 7,
      isArchived: withArchived && i < 2,
      // Same rule as e2e/support/mocks.ts's mockVoteSessionResults (~:1171): eligible defaults
      // to !isArchived. Left inline here rather than defaulted through `?? !isArchived` because
      // this fixture builds isArchived itself a line below rather than reading it off an input.
      eligible: !(withArchived && i < 2),
      myVote: i % 3 === 0 ? 1 : i % 3 === 1 ? 2 : null,
    })),
  };
}

function myVoteSessions(count: number) {
  return Array.from({ length: count }, (_, i) => ({
    sessionId: i + 1,
    title:
      i === 0
        ? 'Frühjahrsputz 2026 — welche Emotes fliegen endgültig raus? (Runde 2)'
        : `Voting Runde ${i + 1}`,
    channelName: i % 2 === 0 ? 'sensitron' : 'superlangertwitchchannelx',
    isActive: i < 2,
    startedAt: '2026-07-01T12:00:00Z',
    endedAt: i < 2 ? null : '2026-07-08T09:30:00Z',
    lastVotedAt: '2026-07-05T18:45:00Z',
  }));
}

// --- per-page mock bundles -------------------------------------------------

async function authedShell(page: Page): Promise<void> {
  await mockAuthMe(page, AUTH_USER);
  await mockWorkerHealth(page, 'connected');
}

/** Same shell, but the session is a global admin — unlocks the /admin area and its nav entry. */
async function adminShell(page: Page): Promise<void> {
  await mockAuthMe(page, { ...AUTH_USER, isGlobalAdmin: true });
  await mockWorkerHealth(page, 'connected');
}

async function channelWorkspace(
  page: Page,
  overrides: Parameters<typeof mockChannelPermissions>[2] = {},
): Promise<void> {
  await mockChannelPermissions(page, 'sensitron', overrides);
  await mockChannelStatus(page, 'sensitron');
  await mockActiveEmoteSet(page, 'sensitron');
  await page.route(
    (url) => url.pathname === '/api/channels/sensitron/emotes/set-warning',
    (route) =>
      json(route, 200, {
        available: true,
        isOwnSet: true,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      }),
  );
}

function mockVoteList(page: Page, sessions: ReturnType<typeof voteSummaries>): Promise<void> {
  return page.route(
    (url) => url.pathname === '/api/channels/sensitron/vote-sessions',
    (route) => {
      const url = new URL(route.request().url());
      const p = Number(url.searchParams.get('page') ?? '1');
      const ps = Number(url.searchParams.get('pageSize') ?? '20');
      return json(route, 200, {
        items: sessions.slice((p - 1) * ps, p * ps),
        page: p,
        pageSize: ps,
        totalCount: sessions.length,
        totalPages: Math.max(1, Math.ceil(sessions.length / ps)),
      });
    },
  );
}

function mockVoteResults(
  page: Page,
  sessionId: number,
  isActive: boolean,
  options: VoteResultsOptions = {},
): Promise<void> {
  return page.route(
    (url) => url.pathname === `/api/channels/sensitron/vote-sessions/${sessionId}/results`,
    (route) => json(route, 200, voteResults(sessionId, isActive, options)),
  );
}

function mockMyVotings(page: Page, sessions: ReturnType<typeof myVoteSessions>): Promise<void> {
  return page.route(
    (url) => url.pathname === '/api/vote-sessions/mine',
    (route) => {
      const url = new URL(route.request().url());
      const p = Number(url.searchParams.get('page') ?? '1');
      const ps = Number(url.searchParams.get('pageSize') ?? '10');
      return json(route, 200, {
        items: sessions.slice((p - 1) * ps, p * ps),
        page: p,
        pageSize: ps,
        totalCount: sessions.length,
        totalPages: Math.max(1, Math.ceil(sessions.length / ps)),
      });
    },
  );
}

// --- scenarios -------------------------------------------------------------

const TYPICAL_CHANNELS: MockChannel[] = [
  { channelName: 'sensitron', isBroadcaster: true, isTracked: true, isBotActive: true },
  { channelName: 'handofblood', isModerator: true, isTracked: true, isBotActive: true },
  {
    channelName: 'superlangertwitchchannelx',
    isModerator: true,
    isTracked: true,
    isBotActive: false,
  },
  { channelName: 'untrackedbuddy', isSevenTvEditor: true, isTracked: false, isBotActive: false },
];

interface Scenario {
  slug: string;
  path: string;
  setup: (page: Page) => Promise<void>;
  /**
   * Runs after the page has settled, for states that only exist once something is opened. Popovers
   * were invisible to this harness until it had this hook — an overflowing dropdown panel is
   * exactly the kind of locale-dependent break the mobile viewport is here to catch.
   */
  afterLoad?: (page: Page) => Promise<void>;
  /**
   * Marks a scenario whose `afterLoad` drives a control that only renders behind
   * `!isCoarse()` — the 7TV write paths in the usage-stats header (`PointerModeService`,
   * core/pointer/pointer-mode.service.ts) are gated on `matchMedia('(pointer: coarse)')` because
   * there is no write token off a phone. Before the CDP pointer emulation above this file's
   * `mobile` viewport measured `(pointer: fine)` regardless of its 360px width, so these scenarios
   * used to "work" only because the harness itself was rendering a control no touch device ever
   * sees. Now that `mobile` actually reports `coarse`, that control never mounts, its `afterLoad`
   * never finds it and the test hangs to its timeout — correctly, because the state the scenario
   * describes does not exist on a coarse pointer.
   *
   * This is a pointer condition, not a width one: deliberately not a `skipViewports: ['mobile']`
   * list, so a future coarse-pointer viewport (a tablet in touch mode, say) is covered without
   * editing this file again. Skipping it does not shrink coverage — the scenario was never a real
   * mobile state to begin with, only an artifact of the harness's former pointer bug.
   */
  requiresFinePointer?: boolean;
}

const SCENARIOS: Scenario[] = [
  {
    slug: 'welcome',
    path: '/welcome',
    setup: async (page) => {
      await mockAuthMe(page, null);
    },
  },
  {
    slug: 'login',
    path: '/login',
    setup: async (page) => {
      await mockAuthMe(page, null);
    },
  },
  {
    slug: 'overview-typical',
    path: '/',
    setup: async (page) => {
      await authedShell(page);
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
    },
  },
  {
    slug: 'overview-empty',
    path: '/',
    setup: async (page) => {
      await authedShell(page);
      await mockMyChannelsWithFlags(page, []);
    },
  },
  {
    // Both header warning *conditions* true at once — worker stale and the live-quota budget full
    // (issue #42). The expected picture is one badge, not two: this run is what established that a
    // single badge already truncates the wordmark to 3px at 360px, so the quota marker yields to
    // the worker one. Keeping the scenario is what stops a later change from quietly bringing the
    // second badge back.
    slug: 'shell-both-warnings',
    path: '/',
    setup: async (page) => {
      await mockAuthMe(page, AUTH_USER);
      await mockWorkerHealth(page, 'disconnected');
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
      await mockLiveQuota(page, {
        openConnections: 6,
        maxPerSubscriber: 6,
        perSubscriberLimitReached: true,
      });
    },
    afterLoad: async (page) => {
      // The state only exists after a stream has been refused — that is what makes the app ask.
      await failLive(page);
      // The worker marker, not the quota one: waiting for the quota button here would be waiting
      // for the very thing this scenario exists to prove does *not* appear alongside it.
      await page.locator('header app-health-marker').waitFor();
    },
  },
  {
    // The quota explanation open. The panel hangs off a trigger in the *left* group, unlike every
    // other popover in the app, so the alignment that works for the account menu cannot be assumed
    // to work here — this is the scenario that decides it.
    slug: 'shell-live-quota-open',
    path: '/',
    setup: async (page) => {
      await mockAuthMe(page, AUTH_USER);
      await mockWorkerHealth(page, 'connected');
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
      await mockLiveQuota(page, {
        openConnections: 6,
        maxPerSubscriber: 6,
        perSubscriberLimitReached: true,
      });
    },
    afterLoad: async (page) => {
      await failLive(page);
      await page.locator('header button:has(app-health-marker)').click();
    },
  },
  {
    // The account menu open — the only place theme and language can be changed, and the one panel
    // that hangs out of the header instead of out of a filter toolbar. The admin session because
    // that is the tallest the panel ever gets, which is what the clipping question is about.
    slug: 'account-menu-open',
    path: '/',
    setup: async (page) => {
      await adminShell(page);
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
    },
    afterLoad: async (page) => {
      await page.locator('header [aria-haspopup="dialog"]').click();
    },
  },
  {
    // The same panel one level down. Its own shot because this is where theme and language live
    // now, and a scenario that only ever sees the root would leave both controls unmeasured.
    slug: 'account-menu-preferences',
    path: '/',
    setup: async (page) => {
      await adminShell(page);
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
    },
    afterLoad: async (page) => {
      await page.locator('header [aria-haspopup="dialog"]').click();
      // By shape, not by label — this harness runs de and en, and "Einstellungen"/"Settings" would
      // pass in one and fail in the other. The preferences row is the only button in the panel
      // carrying an icon; Logout has none and the admin entry is a link.
      await page.locator('app-popover button:has(svg)').click();
    },
  },
  {
    // The one state in which the header says anything about the worker at all. Worth a shot of its
    // own precisely because it is rare: at 360px the warning shares the bar with the wordmark and
    // the account-menu trigger, and nothing else in the app ever puts a third thing in that row.
    slug: 'overview-worker-stale',
    path: '/',
    setup: async (page) => {
      await mockAuthMe(page, AUTH_USER);
      await mockWorkerHealth(page, 'stale');
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
    },
  },
  {
    slug: 'overview-reauth',
    path: '/',
    setup: async (page) => {
      await authedShell(page);
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS, { reauthRequired: true });
    },
  },
  {
    slug: 'overview-helix-down',
    path: '/',
    setup: async (page) => {
      await authedShell(page);
      await mockMyChannelsWithFlags(page, [], { helixUnavailable: true });
    },
  },
  {
    slug: 'overview-worker-down',
    path: '/',
    setup: async (page) => {
      await mockAuthMe(page, AUTH_USER);
      await mockWorkerHealth(page, 'disconnected');
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
    },
  },
  // The global channel list moved out of the overview into /admin/channels — these three replace the
  // former `overview-admin` scenario.
  {
    slug: 'admin-monitoring',
    path: '/admin/monitoring',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminHealth(page);
      await mockAdminRoster(page);
    },
  },
  {
    slug: 'admin-monitoring-degraded',
    path: '/admin/monitoring',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminHealth(page, {
        status: 'stale',
        isConnected: false,
        secondsSinceLastMessage: 4212,
        sevenTv: { status: 'disconnected', connected: false, unacknowledgedCount: 3 },
        flush: { consecutiveFailures: 5, pendingEmoteCount: 1843 },
      });
      // The roster's own degraded shape: deficits on both transports, so the card renders its
      // banners and deficit lists rather than the all-green layout.
      await mockAdminRoster(page, {
        trackedChannelCount: 24,
        missingFromIrc: ['sensitron', 'olaf_olaf_son'],
        missingFromSevenTv: ['sensitron'],
      });
    },
  },
  {
    // Both capacity bars in their early-warning band (86 % subscriptions, 90 % join budget): the
    // amber fills plus their mandatory text lines are a distinct page state the happy path and the
    // degraded scenario never reach.
    slug: 'admin-monitoring-near-capacity',
    path: '/admin/monitoring',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminHealth(page, {
        sevenTv: { desiredSubscriptionCount: 430, desiredChannelCount: 215 },
      });
      await mockAdminRoster(page, { trackedChannelCount: 18 });
    },
  },
  {
    slug: 'admin-channel-detail',
    path: '/admin/channels/handofblood',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminChannelDetail(
        page,
        {
          channelName: 'handofblood',
          twitchChannelId: '4711',
          emoteCount: 903,
          archivedEmoteCount: 17,
          lastSyncedAtUtc: '2026-08-01T11:59:00Z',
          lastInventoryChangeUtc: '2026-05-01T09:00:00Z',
          activeEmoteSetId: '01HSET',
          activeEmoteSetCapacity: 1000,
        },
        { channel: null },
      );
    },
  },
  {
    slug: 'admin-channels',
    path: '/admin/channels',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminChannelList(
        page,
        Array.from({ length: 10 }, (_, i) => ({
          channelName: i === 3 ? 'superlangertwitchchannelx' : `channel${i + 1}`,
          isBotActive: i % 3 !== 0,
          emoteCount: 87 * (i + 1),
          archivedEmoteCount: 3 * i,
          activeVoteSessionCount: i % 4 === 0 ? 1 : 0,
          voteSessionCount: i % 2,
          lastSyncedAtUtc: i === 5 ? null : '2026-07-31T11:45:00Z',
        })),
      );
    },
  },
  {
    slug: 'admin-channels-empty',
    path: '/admin/channels',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminChannelList(page, []);
    },
  },
  {
    slug: 'admin-users',
    path: '/admin/users',
    setup: async (page) => {
      await adminShell(page);
      await mockAdminUsers(
        page,
        Array.from({ length: 25 }, (_, i) => ({
          twitchUserId: String(1000 + i),
          twitchUsername: i === 3 ? 'superlangertwitchusernamex' : `user${i + 1}`,
          displayName: i === 3 ? 'SuperLangerTwitchUsernameX' : `User${i + 1}`,
          hasRefreshToken: i % 3 !== 0,
          sessionsValidFromUtc: i % 5 === 0 ? '2026-07-30T10:00:00Z' : null,
          twitchAccessTokenExpiresAtUtc: i % 3 !== 0 ? '2026-07-31T16:00:00Z' : null,
          twitchTokenScopes: i % 3 !== 0 ? 'user:read:email moderation:read' : null,
        })),
      );
    },
  },
  {
    slug: 'admin-audit-log',
    path: '/admin/audit-log',
    setup: async (page) => {
      await adminShell(page);
      await mockAuditLog(
        page,
        {
          1: Array.from({ length: 25 }, (_, i) => ({
            id: 100 - i,
            action: ['channel.join', 'channel.leave', 'channel.purge', 'voteSession.create'][i % 4],
            channelName: i % 3 === 0 ? 'superlangertwitchchannelx' : 'sensitron',
            targetType: i % 4 === 3 ? 'VoteSession' : 'Channel',
            targetId: String(i + 1),
          })),
        },
        63,
      );
    },
  },
  {
    slug: 'my-votings-list',
    path: '/my-votings',
    setup: async (page) => {
      await authedShell(page);
      await mockMyVotings(page, myVoteSessions(23));
    },
  },
  {
    slug: 'my-votings-empty',
    path: '/my-votings',
    setup: async (page) => {
      await authedShell(page);
      await mockMyVotings(page, []);
    },
  },
  {
    slug: 'usage-stats-grid',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', usageEmotes(24));
      // The sidecar's curve is only visible from lg upwards, so this shows up in the desktop shots
      // and is correctly absent from the mobile ones.
      await mockUsageChannelSeries(page, 'sensitron', usageSeries(), [2, 3, 4, 9, 10, 15, 16]);
    },
  },
  {
    // A set with a real never-used band — the group the whole page exists to find, and until now
    // the one no shot contained. usageEmotes() never produces a zero, so the tail is spliced on
    // here. Also the case that shows whether a band of identical cells still reads as "unused"
    // after they lost their separate plate on 2026-08-06.
    slug: 'usage-stats-dead-band',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', [
        ...usageEmotes(10),
        ...Array.from({ length: 14 }, (_, i) => ({
          emoteId: `d${i + 1}`,
          emoteName: `Dead${i + 1}Emote`,
          sevenTvEmoteId: `7tv-d${i + 1}`,
          imageUrl: 'https://cdn.7tv.app/emote/stub/1x.webp',
          totalUseCount: 0,
        })),
      ]);
      await mockUsageChannelSeries(page, 'sensitron', usageSeries(), [2, 3, 4, 9, 10, 15, 16]);
    },
  },
  {
    // The date-range menu open on its custom entry: the widest the panel ever gets, and the state
    // its trigger-less predecessor never had a screenshot of.
    slug: 'usage-stats-range-menu',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', usageEmotes(24));
    },
    afterLoad: async (page) => {
      // Locale-independent handles: the label is translated, these are not. Both are scoped to the
      // menu component, because neither handle is unique on this page: the account-menu trigger in
      // the header carries the same aria-haspopup and comes first in the DOM, and the last radio
      // belongs to the sort control — which sits *behind* the open panel and cannot be clicked.
      const rangeMenu = page.locator('app-date-range-menu');
      await rangeMenu.locator('[aria-haspopup="dialog"]').click();
      await rangeMenu.getByRole('radio').last().click();
    },
  },
  {
    // The A6/#91 import path in its refusal state: a protocol from another channel renders the
    // error banner inside the import dialog's file step, under the sort list and the file control
    // (§1.1's body order) — deterministic (no token prompt, no further dialog).
    slug: 'usage-stats-restore-import-error',
    path: '/channels/sensitron/usage-stats',
    requiresFinePointer: true,
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockActiveEmoteSet(page, 'sensitron');
      await mockUsageTotals(page, 'sensitron', usageEmotes(8));
    },
    afterLoad: async (page) => {
      // The file control now lives inside the import dialog (#91, #147), not directly on the page,
      // so the trigger has to be opened first — same position-based handle as the
      // import-target-dialog scenario above, `.nth(2)` because the import trigger sits after export
      // and transfer — and since #147 the file source has to be picked in the dialog's first step.
      await page.locator('main header button').nth(2).click();
      await page.locator('#app-dialog-title').waitFor();
      // "Aus einer Datei" / "From a file" (import.source.file.label) — no shared substring, so
      // both languages need to be in the pattern, unlike the export trigger above.
      await page
        .getByRole('dialog')
        .getByRole('button', { name: /^(Aus einer Datei|From a file)/ })
        .click();

      const foreignProtocol = JSON.stringify({
        source: 'emotepurge',
        kind: 'purge-run',
        formatVersion: 1,
        exportedAt: '2026-08-02T10:00:00Z',
        channelName: 'handofblood',
        withheld: [],
        meta: {
          emoteSetId: 'set-1',
          startedAt: '2026-08-02T10:00:00Z',
          finishedAt: '2026-08-02T10:05:00Z',
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
      });
      // The input stays a hidden native <input type="file"> inside the open dialog (plan §1.1) —
      // setInputFiles works on it directly regardless of visibility.
      await page.locator('input[type="file"]').setInputFiles({
        name: 'emotepurge_handofblood_purge.json',
        mimeType: 'application/json',
        buffer: Buffer.from(foreignProtocol, 'utf-8'),
      });
      await page.getByRole('alert').waitFor();
    },
  },
  {
    // The import dialog's first step (#147): the source rows. Each row is a two-line label, and the
    // German hints are the longest strings in the dialog — §12 flags exactly that as the most
    // common wrap break.
    slug: 'usage-stats-import-source-dialog',
    path: '/channels/sensitron/usage-stats',
    requiresFinePointer: true,
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockActiveEmoteSet(page, 'sensitron');
      await mockUsageTotals(page, 'sensitron', usageEmotes(8));
    },
    afterLoad: async (page) => {
      // Same position-based handle as the scenario above: export, transfer, then this trigger.
      await page.locator('main header button').nth(2).click();
      await page.locator('#app-dialog-title').waitFor();
    },
  },
  {
    // The file step (#91) in its starting state: sort list, file control, no error yet. Its own
    // scenario because the list holds the longest new strings the cutover introduced (three
    // full-sentence-length entries) and §12 flags longer German strings as the most common wrap
    // break — the starting state otherwise has no screenshot of its own at all.
    slug: 'usage-stats-file-import-dialog',
    path: '/channels/sensitron/usage-stats',
    requiresFinePointer: true,
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockActiveEmoteSet(page, 'sensitron');
      await mockUsageTotals(page, 'sensitron', usageEmotes(8));
    },
    afterLoad: async (page) => {
      await page.locator('main header button').nth(2).click();
      await page.locator('#app-dialog-title').waitFor();
      // "Aus einer Datei" / "From a file" (import.source.file.label) — no shared substring, so
      // both languages need to be in the pattern, unlike the export trigger above.
      await page
        .getByRole('dialog')
        .getByRole('button', { name: /^(Aus einer Datei|From a file)/ })
        .click();
    },
  },
  {
    // The drilldown dialog with a real series: sparkline, peak line and the stats grid.
    slug: 'usage-stats-drilldown',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', usageEmotes(24));
      await mockUsageDaily(page, 'sensitron', [
        { date: '2026-07-02', useCount: 4 },
        { date: '2026-07-05', useCount: 19 },
        { date: '2026-07-06', useCount: 7 },
        { date: '2026-07-12', useCount: 11 },
      ]);
    },
    afterLoad: async (page) => {
      // Locale-independent handle: the emote name is part of the button's aria-label in both
      // languages, and Emote1PogU sorts first (highest mocked usage). The :not([aria-pressed])
      // is what tells the sidecar's details trigger apart from the atlas sprite of the same
      // emote — the sprite rebuild added that second match and `.first()` had been landing on it.
      await page.locator('[aria-label*="Emote1PogU"]:not([aria-pressed])').first().click();
      await page.locator('#app-dialog-title').waitFor();
    },
  },
  {
    // The same dialog on an emote without any usage — the empty state is its own layout.
    slug: 'usage-stats-drilldown-empty',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', usageEmotes(24));
      await mockUsageDaily(page, 'sensitron', []);
    },
    afterLoad: async (page) => {
      await page.locator('[aria-label*="Emote1PogU"]:not([aria-pressed])').first().click();
      await page.locator('#app-dialog-title').waitFor();
    },
  },
  {
    // The overview's two-line row shape below sm. The long "not tracked yet" sentence is the branch
    // that used to drop the whole right-hand group onto a second, right-aligned line and wrap again
    // inside it; German is 26 % longer than English, so this is the locale that shows it.
    slug: 'overview-narrow-rows',
    path: '/channels',
    setup: async (page) => {
      await authedShell(page);
      // TYPICAL_CHANNELS carries 'untrackedbuddy' — the one row that hits the notTrackedYet branch
      // this case exists to show; without it the fetch would go unmocked and the screenshot would
      // just be an error banner.
      await mockMyChannelsWithFlags(page, TYPICAL_CHANNELS);
    },
  },
  {
    // The export dialog is a page state of its own: the purpose-sorted option group (#141, three
    // two-line rows here — the third only because channelWorkspace gives sensitron an active set),
    // row count and (elsewhere) the withheld-columns notice all render only here.
    slug: 'usage-stats-export-dialog',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', usageEmotes(24));
    },
    afterLoad: async (page) => {
      // Locale-independent handle: the visible label is translated, the aria-label key is not
      // unique — the export trigger sits in the header action row next to the refresh button.
      await page
        .getByRole('button', { name: /export/i })
        .first()
        .click();
      await page.locator('#app-dialog-title').waitFor();
    },
  },
  {
    // The push flow's first step (#72, K3): the target picker, opened without a grid selection so
    // the scope radiogroup does not render and every visible row is the implied scope. One tracked,
    // one untracked (disabled with its hint) channel is the state the picker is in most often — the
    // file destination moved to the export dialog (#141), see `usage-stats-export-dialog` above.
    slug: 'usage-stats-import-target-dialog',
    path: '/channels/sensitron/usage-stats',
    requiresFinePointer: true,
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', usageEmotes(24));
      await mockMyChannelsWithFlags(page, [
        ...TYPICAL_CHANNELS,
        { channelName: 'aatrociity', isSevenTvEditor: true, isTracked: true },
      ]);
    },
    afterLoad: async (page) => {
      // Locale-independent handle: the visible label is translated ("Übertragen" / "Transfer")
      // with no shared word and no aria-label of its own, unlike the export trigger next
      // to it — so this goes by position in the header action row instead (export, then import,
      // then the file-import trigger "Importieren" / "Import" (#91, `.nth(2)` below),
      // then refresh; see usage-stats-page.html). Scoped to `main` because the app shell has its
      // own top-level `<header>` (the account menu) — an unscoped `header button` counts that one
      // first and silently opens the export dialog instead.
      await page.locator('main header button').nth(1).click();
      await page.locator('#app-dialog-title').waitFor();
    },
  },
  {
    // The confirmation step, past the picker: origin, target, an already-present row and a name
    // collision — the two findings that make a copy differ from the naive "N emotes copied" reading
    // (R8/T5's row-order contract).
    slug: 'usage-stats-import-confirm-dialog',
    path: '/channels/sensitron/usage-stats',
    requiresFinePointer: true,
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', usageEmotes(24));
      await mockMyChannelsWithFlags(page, [
        ...TYPICAL_CHANNELS,
        { channelName: 'aatrociity', isSevenTvEditor: true, isTracked: true },
      ]);
      // The picker's own data source since K2 (spec 6.2) — replaces the pre-K2 assumption that
      // `/api/channels/mine` above was enough to open it on. `target-set` is both this account's
      // active set and the one `mockActiveEmoteSet`/`mockSetWarning`/`mockEmoteList` below already
      // answer for, so picking it below stays on the "chosen set is the account's active one" fast
      // path (spec 8.6 fourth bullet, AK 36) the same way this scenario always has.
      await mockEmoteSetTargets(page, [
        {
          twitchChannelId: 'aatrociity-id',
          twitchLogin: 'aatrociity',
          trackedChannelName: 'aatrociity',
          activeEmoteSetId: 'target-set',
          sets: [{ id: 'target-set', name: 'Main', isActive: true }],
        },
      ]);
      await mockActiveEmoteSet(page, 'aatrociity', 'target-set', {
        capacity: 1000,
        occupiedSlots: 3,
      });
      await mockSetWarning(page, 'aatrociity');
      await mockEmoteList(page, 'aatrociity', [
        { sevenTvEmoteId: '7tv-1', name: 'Emote1PogU' },
        { sevenTvEmoteId: 'target-99', name: 'Emote3PogU' },
      ]);
    },
    afterLoad: async (page) => {
      // See the target-dialog scenario above for why this goes by position, not by label, and for
      // why it is scoped to `main`.
      await page.locator('main header button').nth(1).click();
      const picker = page.getByRole('dialog');
      // Every set is its own radio since addendum 39 (#217) — there is no more merged
      // "#aatrociity" account-header radio to check. The set's own name ("Main", from the mock
      // above) is not translated, so a prefix match on it stays locale-independent the same way the
      // old channel-login match was — unlike its "(aktiv)"/"(active)" suffix, which this regex
      // deliberately does not pin down.
      await picker.getByRole('radio', { name: /^Main/ }).check();
      await picker.locator('[dialog-actions]').last().click();
      // The target load starts async and the dialog opens on its loading skeleton (R8). The dialog
      // title itself already carries the channel name the moment the dialog opens — before the
      // target data has loaded — so waiting on the mocked set id instead (only rendered once
      // `ready()` is true, and, like the channel login, never translated) is what actually proves
      // the confirm dialog has filled in rather than still showing its skeleton.
      await page.getByText('target-set').first().waitFor();
    },
  },
  {
    slug: 'usage-stats-empty',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockUsageTotals(page, 'sensitron', []);
    },
  },
  {
    slug: 'usage-stats-error',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await page.route(
        (url) => url.pathname === '/api/channels/sensitron/usage-stats/totals',
        (route) => json(route, 500, { error: 'boom' }),
      );
    },
  },
  {
    slug: 'usage-stats-shared-set',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await mockChannelPermissions(page, 'sensitron');
      await mockChannelStatus(page, 'sensitron');
      await mockActiveEmoteSet(page, 'sensitron');
      await page.route(
        (url) => url.pathname === '/api/channels/sensitron/emotes/set-warning',
        (route) =>
          json(route, 200, {
            available: true,
            isOwnSet: false,
            otherTrackedChannelsSharingSet: ['handofblood', 'superlangertwitchchannelx'],
            otherModeratedChannelsSharingSet: ['handofblood'],
          }),
      );
      await mockUsageTotals(page, 'sensitron', usageEmotes(8));
    },
  },
  {
    slug: 'usage-stats-viewer-only',
    path: '/channels/sensitron/usage-stats',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page, { canManage: false });
      await mockUsageTotals(page, 'sensitron', usageEmotes(8));
    },
  },
  {
    slug: 'channel-activity',
    path: '/channels/sensitron/activity',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockChannelAuditLog(page, 'sensitron', {
        1: Array.from({ length: 25 }, (_, i) => ({
          id: 100 - i,
          action: ['channel.join', 'channel.resync', 'voteSession.delete', 'emotes.syncDeleted'][
            i % 4
          ],
          actorLogin: i % 2 === 0 ? 'sensitron' : 'averylongmoderatorname',
          detail:
            i % 4 === 2
              ? { kind: 'title', count: null, text: 'Sommer-Purge 2026' }
              : i % 4 === 3
                ? { kind: 'emoteCount', count: 128, text: null }
                : null,
        })),
      });
    },
  },
  {
    slug: 'channel-activity-empty',
    path: '/channels/sensitron/activity',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockChannelAuditLog(page, 'sensitron', { 1: [] });
    },
  },
  {
    slug: 'vote-list',
    path: '/channels/sensitron/vote-sessions',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteList(page, voteSummaries(23));
    },
  },
  {
    slug: 'vote-list-empty',
    path: '/channels/sensitron/vote-sessions',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteList(page, []);
    },
  },
  {
    slug: 'vote-list-voter-only',
    path: '/channels/sensitron/vote-sessions',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page, { canManage: false });
      await mockVoteList(page, voteSummaries(5));
    },
  },
  {
    slug: 'vote-detail-active',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteResults(page, 5, true);
    },
  },
  {
    slug: 'vote-detail-ended',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteResults(page, 5, false);
    },
  },
  {
    // The card without its usage line: the results endpoint reports TotalUseCount as null to
    // everyone but managers, so a voter sees the score alone — and the usage filters disappear
    // with the data.
    slug: 'vote-detail-voter-only',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page, { canManage: false });
      await mockVoteResults(page, 5, true, { withUsage: false });
    },
  },
  {
    // Running secret ballot as a voter sees it: no tallies on the cards, the info banner naming
    // the reason, own votes still marked. The h-44 card contract has to survive the swapped
    // score line and the vote buttons losing their counts.
    slug: 'vote-detail-hidden-voter',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page, { canManage: false });
      await mockVoteResults(page, 5, true, { withUsage: false, hidden: 'voter' });
    },
  },
  {
    // Same session as a manager: numbers visible, plus the banner saying the voters can't see them.
    slug: 'vote-detail-hidden-manager',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteResults(page, 5, true, { hidden: 'manager' });
    },
  },
  {
    // Curated subset ballot: short enough (10 < FILTER_TOOLBAR_MIN_EMOTES) that the filter
    // toolbar disappears, with two archived members carrying the amber badge and disabled
    // vote buttons.
    slug: 'vote-detail-subset-archived',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteResults(page, 5, true, { withArchived: true, count: 10 });
    },
  },
  {
    // Thin participation: the info banner qualifies the numbers as not-a-verdict-yet.
    slug: 'vote-detail-low-participation',
    path: '/channels/sensitron/vote-sessions/5',
    setup: async (page) => {
      await authedShell(page);
      await channelWorkspace(page);
      await mockVoteResults(page, 5, false, { voterCount: 3 });
    },
  },
];

// --- metrics ---------------------------------------------------------------

async function collectMetrics(page: Page) {
  return page.evaluate(() => {
    const vw = window.innerWidth;
    const doc = document.documentElement;
    const interactive = Array.from(
      document.querySelectorAll<HTMLElement>(
        'a,button,input,select,textarea,[role="button"],[role="link"]',
      ),
    )
      .map((el) => {
        const r = el.getBoundingClientRect();
        return {
          tag: el.tagName.toLowerCase(),
          text: (el.getAttribute('aria-label') || el.textContent || '').trim().slice(0, 50),
          x: Math.round(r.x + window.scrollX),
          y: Math.round(r.y + window.scrollY),
          w: Math.round(r.width),
          h: Math.round(r.height),
        };
      })
      .filter((t) => t.w > 0 && t.h > 0);
    return {
      horizontalOverflowPx: Math.max(0, doc.scrollWidth - vw),
      smallTargetsUnder24: interactive.filter((t) => t.w < 24 || t.h < 24),
      targets24to43: interactive.filter((t) => (t.w < 44 || t.h < 44) && !(t.w < 24 || t.h < 24)),
      beyondRightEdge: interactive.filter((t) => t.x + t.w > vw + 1),
      interactiveCount: interactive.length,
    };
  });
}

/**
 * axe's `color-contrast` rule, and nothing else. §10 has demanded an AXE pass since the design doc
 * was written, but until now it was checked by eye. Scoped to the one rule on purpose: a full axe
 * run surfaces unrelated findings that have nothing to do with theming and would turn every audit
 * red for reasons this harness cannot act on — those deserve their own round.
 *
 * Gate: 0 on serious/critical. What it cannot see, and what therefore stays hand-work:
 * semi-transparent stacks it refuses to compute, and non-text graphic contrast (1.4.11).
 */
async function collectContrastViolations(page: Page) {
  const results = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze();
  return results.violations.flatMap((violation) =>
    violation.nodes
      .filter((node) => node.impact === 'serious' || node.impact === 'critical')
      .map((node) => ({
        impact: node.impact,
        target: node.target.join(' '),
        summary: node.failureSummary?.split('\n').slice(0, 3).join(' ') ?? '',
      })),
  );
}

/**
 * Reads the live pointer/colour-scheme state from the page and asserts it matches what
 * `emulateViewportMedia()` set up. Factored out so the exact same check can run twice: once right
 * after the CDP calls (inside `emulateViewportMedia()`, on `about:blank`, before navigation -- pure
 * sanity check that the CDP calls themselves took), and once more immediately before
 * `collectMetrics()` in the test body -- see that call site for why the first check alone is not
 * enough. `hint` is appended to the failure message so each call site can name what it is actually
 * ruling out, instead of both failures reading as a generic "expected true got false".
 *
 * Asserts both `(pointer: coarse)` and `(pointer: fine)`, not just the one matching `vp.pointerCoarse`
 * -- a known Chromium behaviour leaves pointer matching neither after `Emulation.setTouchEmulationEnabled({enabled:
 * false})` runs on a context that previously had touch emulation on (documented above
 * `emulateViewportMedia()`), and a single `coarse` assertion cannot tell that state apart from a
 * genuine fine-pointer emulation: both make `matches(pointer: coarse)` false. Checking `fine` too
 * closes that hole for every fine-pointer viewport, `narrow` included.
 */
async function assertEmulatedState(
  page: Page,
  vp: (typeof VIEWPORTS)[number],
  theme: (typeof THEMES)[number],
  hint: string,
): Promise<void> {
  const actual = await page.evaluate(() => ({
    coarse: matchMedia('(pointer: coarse)').matches,
    fine: matchMedia('(pointer: fine)').matches,
    dark: matchMedia('(prefers-color-scheme: dark)').matches,
  }));
  expect(
    actual.coarse,
    `pointer emulation is not in effect for viewport "${vp.name}": expected ` +
      `matchMedia('(pointer: coarse)').matches === ${vp.pointerCoarse}, got ${actual.coarse}. ${hint}`,
  ).toBe(vp.pointerCoarse);
  expect(
    actual.fine,
    `pointer emulation is not in effect for viewport "${vp.name}": expected ` +
      `matchMedia('(pointer: fine)').matches === ${!vp.pointerCoarse}, got ${actual.fine} (neither ` +
      `coarse nor fine matching means touch emulation was toggled off after being on -- see the ` +
      `Chromium caveat documented above emulateViewportMedia()). ${hint}`,
  ).toBe(!vp.pointerCoarse);
  expect(
    actual.dark,
    `colour-scheme emulation is not in effect for viewport "${vp.name}" [${theme}]: expected ` +
      `matchMedia('(prefers-color-scheme: dark)').matches === ${theme === 'dark'}, got ${actual.dark}. ${hint}`,
  ).toBe(theme === 'dark');
}

/**
 * Emulates viewport-appropriate pointer/hover media features together with the colour scheme, via
 * a single CDP `Emulation.setEmulatedMedia` call, then asserts the emulation actually took.
 *
 * Why CDP instead of `page.emulateMedia()`: Playwright's `emulateMedia()` has no `pointer`/`hover`
 * option at all, which is exactly how the `mobile` scenario used to measure `pointer: fine` --
 * PointerModeService (core/pointer/pointer-mode.service.ts) gates the 7TV write paths on
 * `matchMedia('(pointer: coarse)')`, so the 360px run rendered controls no phone can reach (#107).
 *
 * Two things had to be gotten right, both found by probing this Playwright/Chromium build directly
 * rather than assumed from docs:
 * - `Emulation.setEmulatedMedia` replaces the whole feature set per call, it does not merge into a
 *   previous one. Calling `page.emulateMedia({ colorScheme })` separately (before or after) would
 *   silently drop whichever override was applied first, so colour-scheme and pointer/hover must go
 *   in through the same call.
 * - The `pointer`/`hover` feature values are inert on their own: Chromium derives coarse-vs-fine
 *   from whether touch emulation is active, not from the media-feature override. `coarse` viewports
 *   therefore also need `Emulation.setTouchEmulationEnabled`. Calling it with `enabled: false` for
 *   fine-pointer viewports does not restore the default -- once touch emulation has been toggled in
 *   a context, disabling it leaves pointer matching neither `coarse` nor `fine`. The fix is to never
 *   call it for fine-pointer viewports at all and rely on Chromium's untouched default, which is
 *   `pointer: fine`. Each Playwright test gets its own browser context, so there is no state to
 *   leak between scenarios.
 *
 * Note this only proves the CDP calls above took effect on the current (pre-navigation) page. It
 * does NOT prove the emulation survives navigation, `afterLoad`, or -- critically -- the screenshot
 * call: see the second `assertEmulatedState()` call right before `collectMetrics()` in the test body
 * for the check that actually guards the state this harness measures and ships.
 */
async function emulateViewportMedia(
  page: Page,
  vp: (typeof VIEWPORTS)[number],
  theme: (typeof THEMES)[number],
): Promise<void> {
  const client = await page.context().newCDPSession(page);
  if (vp.pointerCoarse) {
    await client.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
  }
  await client.send('Emulation.setEmulatedMedia', {
    media: 'screen',
    features: [
      { name: 'prefers-color-scheme', value: theme },
      { name: 'pointer', value: vp.pointerCoarse ? 'coarse' : 'fine' },
      { name: 'any-pointer', value: vp.pointerCoarse ? 'coarse' : 'fine' },
      { name: 'hover', value: vp.pointerCoarse ? 'none' : 'hover' },
      { name: 'any-hover', value: vp.pointerCoarse ? 'none' : 'hover' },
    ],
  });

  // Hard self-check, not a nice-to-have: this is the exact failure mode this function exists to
  // fix, so a silent regression here (a Chromium/Playwright upgrade that changes how emulated media
  // features compose, say) must fail loudly instead of quietly going back to measuring a state
  // nothing ships. Only covers the CDP call itself, on about:blank -- it cannot see a reset that
  // happens later (that is what the pre-collectMetrics check is for).
  await assertEmulatedState(
    page,
    vp,
    theme,
    'Checked right after Emulation.setEmulatedMedia/setTouchEmulationEnabled, before navigation ' +
      '(about:blank) -- Emulation.setTouchEmulationEnabled/setEmulatedMedia may not be composing ' +
      'pointer + prefers-color-scheme the way emulateViewportMedia() assumes in this Chromium build.',
  );
}

// --- test matrix -----------------------------------------------------------

fs.mkdirSync(path.join(OUT, 'shots'), { recursive: true });
fs.mkdirSync(path.join(OUT, 'metrics'), { recursive: true });

for (const theme of THEMES) {
  for (const vp of VIEWPORTS) {
    for (const sc of SCENARIOS) {
      test(`${sc.slug} @ ${vp.name} [${theme}]`, async ({ page }, testInfo) => {
        const locale = testInfo.project.name;
        // English pass only in mobile (worst-case overflow) + both desktop widths, to keep the
        // matrix sane. That is: this skips exactly `tablet` and `narrow`, not "everything but
        // mobile+desktop" -- `desktop-narrow` was never skipped and still runs in en. `tablet` and
        // `narrow` stay de-only for the same reason: German strings are the longer ones, so de is
        // the stricter overflow test at the widths this skip removes from en.
        test.skip(
          locale === 'en' && (vp.name === 'tablet' || vp.name === 'narrow'),
          'en only in mobile+desktop-narrow+desktop',
        );
        test.skip(theme === 'light' && vp.name !== 'desktop', 'light only at the widest viewport');
        test.skip(
          Boolean(sc.requiresFinePointer) && vp.pointerCoarse,
          'shows a control off the coarse-pointer write path (isCoarse) — this state has no ' +
            'coarse-pointer equivalent to measure',
        );

        await page.setViewportSize({ width: vp.width, height: vp.height });
        // Both, and deliberately: media emulation covers the system-preference path, the storage
        // seed covers the explicit-choice path, and together they make the state independent of
        // which one the app happens to read first. Pointer/hover ride along with colour-scheme here
        // (see emulateViewportMedia) because they have to be set in the same CDP call.
        await emulateViewportMedia(page, vp, theme);
        await page.addInitScript(
          (value) => localStorage.setItem('emotepurge.theme', value),
          theme as string,
        );
        await stubSevenTvCdn(page);
        // Mandatory here, not just nice to have: a live EventSource keeps the network busy forever,
        // so the waitForLoadState('networkidle') below would never resolve.
        await installLiveStub(page);
        await sc.setup(page);
        await page.goto(sc.path);
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(400);
        if (sc.afterLoad) {
          await sc.afterLoad(page);
          await page.waitForTimeout(200);
        }

        const base = `${sc.slug}--${vp.name}--${locale}--${theme}`;

        // `page.screenshot({ fullPage: true })` does not just leave the CDP overrides reset once it
        // *returns* -- measured directly (a data: URL whose colour is driven by a pure-CSS
        // `@media (pointer: coarse)` rule, so no app/JS latency can confound it): a plain
        // viewport-sized screenshot preserves both `Emulation.setEmulatedMedia` and
        // `Emulation.setTouchEmulationEnabled` across the call, but `fullPage: true` does not -- the
        // composed PNG itself came back showing the fine-pointer/light-touch default, even though
        // `matchMedia()` still reported the emulated state the instant before `screenshot()` was
        // called. So the reset happens *during* Playwright's full-page composition, not afterwards,
        // and reordering `collectMetrics()` before the screenshot (the previous attempt at this fix)
        // could not have helped: the screenshot itself would still ship the wrong state. Switching
        // the pointer emulation to Playwright's context-level `hasTouch`/`isMobile` instead of CDP
        // does not sidestep this either (verified the same way) -- the reset lives in the
        // `fullPage: true` capture path itself, independent of how the emulation was established.
        //
        // The workaround: never invoke that composition path. Grow the viewport to the page's actual
        // content height and take a normal, viewport-sized screenshot instead -- confirmed to survive
        // the call, pixels included -- then shrink back so collectMetrics() below runs at the same
        // viewport size as the rest of the scenario (and every other viewport in the matrix).
        const contentHeight = await page.evaluate(() => document.documentElement.scrollHeight);
        await page.setViewportSize({ width: vp.width, height: Math.max(vp.height, contentHeight) });
        await page.screenshot({ path: path.join(OUT, 'shots', `${base}.png`), fullPage: false });

        // A viewport-sized screenshot is exactly what leaves emulation intact (see above), but it
        // is also, by construction, only `vp.width` wide -- horizontally overflowing content is
        // simply outside the captured frame, not composited in and cropped. That is the one thing
        // this audit exists to catch (collectMetrics()'s `horizontalOverflowPx`, below), so silently
        // clipping it out of the screenshot would hide the defect from anyone reading the images.
        // `fullPage: true` would show it, but re-triggers the composition-path emulation reset this
        // fix works around, and a raw CDP `Page.captureScreenshot({ captureBeyondViewport: true })`
        // does too -- the reset lives in Chromium's capture-beyond-viewport path itself, not in
        // Playwright's wrapper, so there is no capture mode that gets both in one shot (measured,
        // not assumed -- see the comment above `assertEmulatedState()`'s CDP note).
        //
        // Scrolling, by contrast, does not touch emulation at all -- it is plain page state, not a
        // capture mode -- so a second viewport-sized screenshot taken after scrolling to the
        // horizontal end reveals the clipped-off content without the trade-off. Only take it when
        // there is actually something past the right edge, so a clean scenario does not grow a
        // second, identical-looking image for no reason.
        const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth);
        if (scrollWidth > vp.width) {
          await page.evaluate((w) => window.scrollTo(w, 0), scrollWidth);
          await page.screenshot({
            path: path.join(OUT, 'shots', `${base}--right.png`),
            fullPage: false,
          });
          await page.evaluate(() => window.scrollTo(0, 0));
        }

        await page.setViewportSize({ width: vp.width, height: vp.height });

        // The gate that actually matters: collectMetrics() below is what the audit's pass/fail
        // reading is based on, so this is the check that must catch a regression -- e.g. a future
        // Chromium/Playwright upgrade that resets emulation on the resized-viewport screenshot too,
        // or someone reintroducing `fullPage: true` above without reading the comment.
        await assertEmulatedState(
          page,
          vp,
          theme,
          'Checked immediately before collectMetrics() -- if this fires, something between ' +
            'navigation and here reset the CDP pointer/colour-scheme overrides. The known cause is ' +
            'page.screenshot({ fullPage: true }), which resets Emulation.setEmulatedMedia/' +
            'setTouchEmulationEnabled mid-capture; that is why the screenshot above no longer uses ' +
            'fullPage: true. If that comment is still true, look for a new source of the same reset.',
        );
        const metrics = await collectMetrics(page);
        const contrastViolations = await collectContrastViolations(page);
        fs.writeFileSync(
          path.join(OUT, 'metrics', `${base}.json`),
          JSON.stringify(
            {
              scenario: sc.slug,
              viewport: vp.name,
              locale,
              theme,
              url: sc.path,
              ...metrics,
              contrastViolations,
            },
            null,
            2,
          ),
        );
      });
    }
  }
}
