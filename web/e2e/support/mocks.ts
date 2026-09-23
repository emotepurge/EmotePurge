import { Page } from '@playwright/test';

export const AUTH_USER = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2099-01-01T00:00:00Z',
  // Non-admin by default — admin flows pass { ...AUTH_USER, isGlobalAdmin: true }.
  isGlobalAdmin: false,
  // Null on purpose: the e2e run has no route to Twitch's CDN, so the monogram fallback is both
  // the honest and the only stable thing to assert against.
  profileImageUrl: null as string | null,
};

async function fulfillJson(
  route: Parameters<Parameters<Page['route']>[1]>[0],
  status: number,
  body: unknown,
): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

/** GET /api/auth/me — pass `null` to simulate a logged-out visitor (401). */
export async function mockAuthMe(page: Page, user: typeof AUTH_USER | null): Promise<void> {
  await page.route('**/api/auth/me', (route) =>
    user ? fulfillJson(route, 200, user) : route.fulfill({ status: 401 }),
  );
}

export async function mockWorkerHealth(
  page: Page,
  status: 'connected' | 'disconnected' | 'unknown' = 'unknown',
): Promise<void> {
  await page.route('**/api/worker/health', (route) => fulfillJson(route, 200, { status }));
}

/**
 * GET /api/legal/availability (issue #247) — drives whether the footer shows the imprint/privacy
 * links at all. Defaults to "nothing configured", the opposite of `mockWorkerHealth`'s default,
 * because most specs unrelated to this feature should see the footer stay silent rather than
 * carry two extra links they never asked for.
 */
export async function mockLegalAvailability(
  page: Page,
  overrides: { imprintAvailable?: boolean; privacyAvailable?: boolean } = {},
): Promise<void> {
  await page.route('**/api/legal/availability', (route) =>
    fulfillJson(route, 200, {
      imprintAvailable: overrides.imprintAvailable ?? false,
      privacyAvailable: overrides.privacyAvailable ?? false,
    }),
  );
}

/** GET /api/legal/{kind}/{language}. Pass `null` to simulate "not configured" (404). */
export async function mockLegalDocument(
  page: Page,
  kind: 'imprint' | 'privacy',
  language: 'de' | 'en',
  document: { html: string; isGermanFallback?: boolean } | null,
): Promise<void> {
  await page.route(`**/api/legal/${kind}/${language}`, (route) =>
    document
      ? fulfillJson(route, 200, {
          html: document.html,
          isGermanFallback: document.isGermanFallback ?? false,
        })
      : fulfillJson(route, 404, { errorCode: 'legal_document_not_found' }),
  );
}

export interface MockAdminHealth {
  snapshotAvailable: boolean;
  status: 'connected' | 'stale' | 'disconnected' | 'unknown';
  isConnected: boolean;
  lastMessageReceivedUtc: string | null;
  connectAttemptedUtc: string | null;
  secondsSinceLastMessage: number | null;
  sevenTv: {
    status: 'connected' | 'stale' | 'disconnected' | 'unknown' | 'disabled';
    enabled: boolean;
    connected: boolean;
    lastFrameUtc: string | null;
    lastDispatchUtc: string | null;
    connectAttemptedUtc: string | null;
    secondsSinceLastFrame: number | null;
    desiredChannelCount: number | null;
    desiredSubscriptionCount: number | null;
    unacknowledgedCount: number | null;
    subscriptionLimit: number;
  };
  flush: {
    consecutiveFailures: number | null;
    lastSuccessUtc: string | null;
    lastRowCount: number | null;
    pendingEmoteCount: number | null;
  };
}

/**
 * GET /api/admin/health — the admin monitoring page. Defaults to a fully healthy worker; pass
 * overrides for the degraded cases. `sevenTv`/`flush` are merged one level deep so a test can
 * override a single field without restating the whole sub-object.
 */
export async function mockAdminHealth(
  page: Page,
  overrides: Partial<Omit<MockAdminHealth, 'sevenTv' | 'flush'>> & {
    sevenTv?: Partial<MockAdminHealth['sevenTv']>;
    flush?: Partial<MockAdminHealth['flush']>;
  } = {},
): Promise<void> {
  const { sevenTv, flush, ...rest } = overrides;
  await page.route('**/api/admin/health', (route) =>
    fulfillJson(route, 200, {
      snapshotAvailable: true,
      status: 'connected',
      isConnected: true,
      lastMessageReceivedUtc: '2026-07-31T12:00:00Z',
      connectAttemptedUtc: '2026-07-31T11:00:00Z',
      secondsSinceLastMessage: 12,
      ...rest,
      sevenTv: {
        status: 'connected',
        enabled: true,
        connected: true,
        lastFrameUtc: '2026-07-31T12:00:00Z',
        lastDispatchUtc: '2026-07-31T11:59:00Z',
        connectAttemptedUtc: '2026-07-31T11:00:00Z',
        secondsSinceLastFrame: 5,
        desiredChannelCount: 7,
        desiredSubscriptionCount: 14,
        unacknowledgedCount: 0,
        subscriptionLimit: 500,
        resyncIntervalSeconds: 60,
        ...sevenTv,
      },
      flush: {
        consecutiveFailures: 0,
        lastSuccessUtc: '2026-07-31T11:59:30Z',
        lastRowCount: 42,
        pendingEmoteCount: 3,
        ...flush,
      },
      worker: {
        instanceId: 'a1b2c3d4',
        processStartedUtc: '2026-07-31T09:00:00Z',
      },
    }),
  );
}

export interface MockAdminRoster {
  snapshotAvailable?: boolean;
  trackedChannelCount?: number;
  bootRecoveryCompleted?: boolean;
  ircConfirmedCount?: number;
  sevenTvAcknowledgedCount?: number;
  ageSeconds?: number;
  missingFromIrc?: string[];
  missingFromSevenTv?: string[];
}

/** GET /api/admin/roster — the monitoring page's roster card. Defaults to a complete roster; the
 *  interesting cases are the deficit ones, which a test states explicitly. */
export async function mockAdminRoster(page: Page, overrides: MockAdminRoster = {}): Promise<void> {
  const tracked = overrides.trackedChannelCount ?? 3;
  const missingFromIrc = overrides.missingFromIrc ?? [];
  const missingFromSevenTv = overrides.missingFromSevenTv ?? [];
  await page.route('**/api/admin/roster', (route) =>
    fulfillJson(route, 200, {
      snapshotAvailable: overrides.snapshotAvailable ?? true,
      trackedChannelCount: tracked,
      ceilings: { twitchConcurrentChannelLimit: 100, twitchJoinBudgetChannels: 20 },
      generatedAtUtc: '2026-08-01T12:00:00Z',
      ageSeconds: overrides.ageSeconds ?? 12,
      workerInstanceId: 'a1b2c3d4',
      processStartedUtc: '2026-08-01T09:00:00Z',
      bootRecoveryCompleted: overrides.bootRecoveryCompleted ?? true,
      truncated: false,
      rosterChannelCount: tracked,
      ircConfirmedCount: overrides.ircConfirmedCount ?? tracked - missingFromIrc.length,
      sevenTvAcknowledgedCount:
        overrides.sevenTvAcknowledgedCount ?? tracked - missingFromSevenTv.length,
      missingFromIrc,
      missingFromIrcTotal: missingFromIrc.length,
      missingFromSevenTv,
      missingFromSevenTvTotal: missingFromSevenTv.length,
      unknownToDatabase: [],
      unknownToDatabaseTotal: 0,
    }),
  );
}

/** GET/POST /api/auth/twitch/login — the real endpoint is a full OAuth redirect; here it just
 *  proves the browser actually navigated there, without needing a live Twitch app. */
export async function mockTwitchLoginRedirect(page: Page): Promise<void> {
  await page.route('**/api/auth/twitch/login', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'text/html',
      body: '<html><body>twitch login stub</body></html>',
    }),
  );
}

export interface MockChannel {
  channelName: string;
  isBroadcaster?: boolean;
  isModerator?: boolean;
  isSevenTvEditor?: boolean;
  isTracked?: boolean;
  isBotActive?: boolean;
  liveState?: 'live' | 'offline' | 'unknown';
}

/** GET /api/channels/mine — the overview page's "Meine Channels" section. */
export async function mockMyChannels(page: Page, channels: MockChannel[]): Promise<void> {
  await page.route('**/api/channels/mine', (route) =>
    fulfillJson(route, 200, {
      helixUnavailable: false,
      reauthRequired: false,
      sevenTvUnavailable: false,
      channels: channels.map((c) => ({
        channelName: c.channelName,
        isBroadcaster: c.isBroadcaster ?? false,
        isModerator: c.isModerator ?? false,
        isSevenTvEditor: c.isSevenTvEditor ?? false,
        isTracked: c.isTracked ?? false,
        isBotActive: c.isBotActive ?? false,
        liveState: c.liveState ?? 'unknown',
      })),
      livePolledAtUtc: channels.some((c) => (c.liveState ?? 'unknown') !== 'unknown')
        ? '2026-08-03T18:00:00Z'
        : null,
    }),
  );
}

export interface MockAdminChannel {
  channelName: string;
  twitchChannelId?: string | null;
  isBotActive?: boolean;
  createdAt?: string;
  emoteCount?: number;
  archivedEmoteCount?: number;
  activeVoteSessionCount?: number;
  voteSessionCount?: number;
  lastSyncedAtUtc?: string | null;
  lastInventoryChangeUtc?: string | null;
  activeEmoteSetId?: string | null;
  activeEmoteSetCapacity?: number | null;
  lastSyncFailureReason?: string | null;
  lastSyncAttemptAtUtc?: string | null;
  liveState?: 'live' | 'offline' | 'unknown';
}

/**
 * GET /api/admin/channels — the admin channel page's aggregate list, and since the overview's admin
 * section was removed the only global channel list in the app.
 */
export async function mockAdminChannelList(
  page: Page,
  channels: MockAdminChannel[],
): Promise<void> {
  await page.route('**/api/admin/channels', (route) =>
    fulfillJson(route, 200, {
      channels: channels.map((c) => ({
        channelName: c.channelName,
        twitchChannelId: c.twitchChannelId ?? null,
        isBotActive: c.isBotActive ?? true,
        createdAt: c.createdAt ?? '2026-01-01T00:00:00Z',
        emoteCount: c.emoteCount ?? 0,
        archivedEmoteCount: c.archivedEmoteCount ?? 0,
        activeVoteSessionCount: c.activeVoteSessionCount ?? 0,
        voteSessionCount: c.voteSessionCount ?? 0,
        lastSyncedAtUtc: c.lastSyncedAtUtc ?? null,
        lastInventoryChangeUtc: c.lastInventoryChangeUtc ?? null,
        activeEmoteSetId: c.activeEmoteSetId ?? null,
        activeEmoteSetCapacity: c.activeEmoteSetCapacity ?? null,
        lastSyncFailureReason: c.lastSyncFailureReason ?? null,
        lastSyncAttemptAtUtc: c.lastSyncAttemptAtUtc ?? null,
        trackingResumedAt: null,
        liveState: c.liveState ?? 'unknown',
      })),
      livePolledAtUtc: channels.some((c) => (c.liveState ?? 'unknown') !== 'unknown')
        ? '2026-08-03T18:00:00Z'
        : null,
    }),
  );
}

/**
 * GET /api/admin/channels/{channelName} — the support drilldown. Registered after
 * mockAdminChannelList so it wins for the longer path (Playwright matches handlers in reverse
 * registration order); the list's own pattern does not match this sub-route anyway.
 */
export async function mockAdminChannelDetail(
  page: Page,
  channel: MockAdminChannel,
  roster: {
    available?: boolean;
    bootRecoveryCompleted?: boolean;
    channel?: {
      ircJoinConfirmed?: boolean;
      lastMessageUtc?: string | null;
      sevenTvEmoteSetId?: string | null;
      sevenTvEmoteSetAcknowledged?: boolean;
      sevenTvUserId?: string | null;
      sevenTvUserAcknowledged?: boolean;
    } | null;
  } = {},
): Promise<void> {
  const rosterChannel = roster.channel === null ? null : (roster.channel ?? {});
  await page.route(`**/api/admin/channels/${channel.channelName}`, (route) =>
    fulfillJson(route, 200, {
      channel: {
        channelName: channel.channelName,
        twitchChannelId: channel.twitchChannelId ?? null,
        isBotActive: channel.isBotActive ?? true,
        createdAt: channel.createdAt ?? '2026-01-01T00:00:00Z',
        emoteCount: channel.emoteCount ?? 0,
        archivedEmoteCount: channel.archivedEmoteCount ?? 0,
        activeVoteSessionCount: channel.activeVoteSessionCount ?? 0,
        voteSessionCount: channel.voteSessionCount ?? 0,
        lastSyncedAtUtc: channel.lastSyncedAtUtc ?? null,
        lastInventoryChangeUtc: channel.lastInventoryChangeUtc ?? null,
        activeEmoteSetId: channel.activeEmoteSetId ?? null,
        activeEmoteSetCapacity: channel.activeEmoteSetCapacity ?? null,
        lastSyncFailureReason: channel.lastSyncFailureReason ?? null,
        lastSyncAttemptAtUtc: channel.lastSyncAttemptAtUtc ?? null,
        trackingResumedAt: null,
        liveState: channel.liveState ?? 'unknown',
      },
      roster: {
        available: roster.available ?? true,
        ageSeconds: 12,
        bootRecoveryCompleted: roster.bootRecoveryCompleted ?? true,
        workerInstanceId: 'a1b2c3d4',
        channel:
          rosterChannel === null
            ? null
            : {
                channelName: channel.channelName,
                ircJoinConfirmed: rosterChannel.ircJoinConfirmed ?? true,
                lastMessageUtc: rosterChannel.lastMessageUtc ?? '2026-08-01T11:59:00Z',
                sevenTvEmoteSetId: rosterChannel.sevenTvEmoteSetId ?? '01HSET',
                sevenTvEmoteSetAcknowledged: rosterChannel.sevenTvEmoteSetAcknowledged ?? true,
                sevenTvUserId: rosterChannel.sevenTvUserId ?? '01HUSER',
                sevenTvUserAcknowledged: rosterChannel.sevenTvUserAcknowledged ?? true,
              },
      },
    }),
  );
}

/**
 * POST /api/admin/channels/{channelName}/resync — answers 202 like the real endpoint ("accepted",
 * not "finished"). Registered after mockAdminChannelList so it wins for this longer path; the list
 * route pattern (`**\/api/admin/channels`) does not match the sub-route anyway.
 */
export async function mockChannelResync(page: Page, channelName: string): Promise<void> {
  await page.route(`**/api/admin/channels/${channelName}/resync`, (route) =>
    route.fulfill({ status: 202 }),
  );
}

/**
 * POST /api/channels/{name}/resync — the channel-scoped variant. Defaults to 202 (queued); pass a
 * status to drive the cooldown case, which answers 429 with its own error code.
 */
export async function mockChannelScopedResync(
  page: Page,
  channelName: string,
  status: 202 | 429 = 202,
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/resync`, (route) =>
    status === 202
      ? route.fulfill({ status: 202 })
      : fulfillJson(route, 429, { errorCode: 'resync_cooldown_active', retryAfterSeconds: 42 }),
  );
}

export interface MockAuditLogEntry {
  id: number;
  occurredAtUtc?: string;
  actorLogin?: string;
  action: string;
  channelName?: string | null;
  targetType?: string | null;
  targetId?: string | null;
  /** Already whitelisted by the server — the raw jsonb column never reaches a client. */
  detail?: { kind: string; count: number | null; text: string | null } | null;
}

/**
 * GET /api/admin/audit-log — the paged log. `entries` is keyed by page number, so one registration
 * can serve a pagination flow: the handler reads the `page` query parameter the app actually sent
 * and answers with that page's rows (empty array for a page the test did not define).
 *
 * `totalCount` defaults to enough rows to make the requested pages exist, because `totalPages` is
 * what decides whether the pager renders at all.
 *
 * When the request carries `action`/`channel`/`actor` filter params, the handler mirrors the
 * server's semantics over the union of all defined pages instead: action and channel match exactly
 * (channel case-insensitively — the server normalizes), actor is a case-insensitive substring, and
 * the filtered set is re-paged with its own total.
 */
export async function mockAuditLog(
  page: Page,
  entriesByPage: Record<number, MockAuditLogEntry[]>,
  totalCount?: number,
): Promise<void> {
  const pageSize = 25;
  const pageNumbers = Object.keys(entriesByPage).map(Number);
  const sortedPageNumbers = [...pageNumbers].sort((a, b) => a - b);
  const highestPage = pageNumbers.length > 0 ? Math.max(...pageNumbers) : 1;
  const effectiveTotal =
    totalCount ??
    (highestPage > 1
      ? (highestPage - 1) * pageSize + (entriesByPage[highestPage]?.length ?? 0)
      : (entriesByPage[1]?.length ?? 0));

  const withDefaults = (entry: MockAuditLogEntry) => ({
    id: entry.id,
    occurredAtUtc: entry.occurredAtUtc ?? '2026-07-31T12:00:00Z',
    actorLogin: entry.actorLogin ?? 'sensitron',
    action: entry.action,
    channelName: entry.channelName ?? null,
    targetType: entry.targetType ?? null,
    targetId: entry.targetId ?? null,
    detail: entry.detail ?? null,
  });

  await page.route('**/api/admin/audit-log**', (route) => {
    const params = new URL(route.request().url()).searchParams;
    const requestedPage = Number(params.get('page') ?? '1');
    const action = params.get('action');
    const channel = params.get('channel')?.trim().toLowerCase() || null;
    const actor = params.get('actor')?.trim().toLowerCase() || null;

    if (action || channel || actor) {
      const filtered = sortedPageNumbers
        .flatMap((pageNumber) => entriesByPage[pageNumber])
        .map(withDefaults)
        .filter(
          (entry) =>
            (!action || entry.action === action) &&
            (!channel || entry.channelName?.toLowerCase() === channel) &&
            (!actor || entry.actorLogin.toLowerCase().includes(actor)),
        );
      return fulfillJson(route, 200, {
        items: filtered.slice((requestedPage - 1) * pageSize, requestedPage * pageSize),
        page: requestedPage,
        pageSize,
        totalCount: filtered.length,
        totalPages: filtered.length === 0 ? 0 : Math.ceil(filtered.length / pageSize),
      });
    }

    const items = entriesByPage[requestedPage] ?? [];
    return fulfillJson(route, 200, {
      items: items.map(withDefaults),
      page: requestedPage,
      pageSize,
      totalCount: effectiveTotal,
      totalPages: effectiveTotal === 0 ? 0 : Math.ceil(effectiveTotal / pageSize),
    });
  });
}

/**
 * GET /api/channels/{name}/audit-log — a channel's own activity feed. Same page-keyed shape as
 * `mockAuditLog`, minus the channel filter: the server takes the channel from the route, so the
 * handler asserts that no `channel` query parameter arrives and fails loudly if one ever does.
 */
export async function mockChannelAuditLog(
  page: Page,
  channelName: string,
  entriesByPage: Record<number, MockAuditLogEntry[]>,
): Promise<void> {
  const pageSize = 25;
  const pageNumbers = Object.keys(entriesByPage).map(Number);
  const sortedPageNumbers = [...pageNumbers].sort((a, b) => a - b);
  const highestPage = pageNumbers.length > 0 ? Math.max(...pageNumbers) : 1;
  const effectiveTotal =
    highestPage > 1
      ? (highestPage - 1) * pageSize + (entriesByPage[highestPage]?.length ?? 0)
      : (entriesByPage[1]?.length ?? 0);

  const withDefaults = (entry: MockAuditLogEntry) => ({
    id: entry.id,
    occurredAtUtc: entry.occurredAtUtc ?? '2026-07-31T12:00:00Z',
    actorLogin: entry.actorLogin ?? 'sensitron',
    action: entry.action,
    channelName: entry.channelName ?? channelName,
    targetType: entry.targetType ?? null,
    targetId: entry.targetId ?? null,
    detail: entry.detail ?? null,
  });

  await page.route(`**/api/channels/${channelName}/audit-log**`, (route) => {
    const params = new URL(route.request().url()).searchParams;
    if (params.has('channel')) {
      return fulfillJson(route, 400, { errorCode: 'channel_must_come_from_the_route' });
    }

    const requestedPage = Number(params.get('page') ?? '1');
    const action = params.get('action');
    const actor = params.get('actor')?.trim().toLowerCase() || null;

    if (action || actor) {
      const filtered = sortedPageNumbers
        .flatMap((pageNumber) => entriesByPage[pageNumber])
        .map(withDefaults)
        .filter(
          (entry) =>
            (!action || entry.action === action) &&
            (!actor || entry.actorLogin.toLowerCase().includes(actor)),
        );
      return fulfillJson(route, 200, {
        items: filtered.slice((requestedPage - 1) * pageSize, requestedPage * pageSize),
        page: requestedPage,
        pageSize,
        totalCount: filtered.length,
        totalPages: filtered.length === 0 ? 0 : Math.ceil(filtered.length / pageSize),
      });
    }

    const items = entriesByPage[requestedPage] ?? [];
    return fulfillJson(route, 200, {
      items: items.map(withDefaults),
      page: requestedPage,
      pageSize,
      totalCount: effectiveTotal,
      totalPages: effectiveTotal === 0 ? 0 : Math.ceil(effectiveTotal / pageSize),
    });
  });
}

export interface MockAdminUser {
  twitchUserId: string;
  twitchUsername?: string;
  displayName?: string;
  lastLogin?: string;
  sessionsValidFromUtc?: string | null;
  hasRefreshToken?: boolean;
  twitchAccessTokenExpiresAtUtc?: string | null;
  twitchTokenScopes?: string | null;
}

/**
 * GET /api/admin/users — the paged user overview. The URL pattern also matches the POST
 * revoke-sessions sub-route, so non-GET requests fall through to a separately registered
 * mockRevokeSessions handler.
 */
export async function mockAdminUsers(page: Page, users: MockAdminUser[]): Promise<void> {
  const pageSize = 25;
  await page.route('**/api/admin/users**', (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback();
    }
    const requestedPage = Number(new URL(route.request().url()).searchParams.get('page') ?? '1');
    const items = users.slice((requestedPage - 1) * pageSize, requestedPage * pageSize);
    return fulfillJson(route, 200, {
      items: items.map((user) => ({
        twitchUserId: user.twitchUserId,
        twitchUsername: user.twitchUsername ?? 'sensitron',
        displayName: user.displayName ?? 'Sensitron',
        lastLogin: user.lastLogin ?? '2026-07-31T12:00:00Z',
        sessionsValidFromUtc: user.sessionsValidFromUtc ?? null,
        hasRefreshToken: user.hasRefreshToken ?? true,
        twitchAccessTokenExpiresAtUtc: user.twitchAccessTokenExpiresAtUtc ?? null,
        twitchTokenScopes: user.twitchTokenScopes ?? null,
      })),
      page: requestedPage,
      pageSize,
      totalCount: users.length,
      totalPages: users.length === 0 ? 0 : Math.ceil(users.length / pageSize),
    });
  });
}

/** POST /api/admin/users/{twitchUserId}/revoke-sessions — answers 204 like the real endpoint. */
export async function mockRevokeSessions(page: Page, twitchUserId: string): Promise<void> {
  await page.route(`**/api/admin/users/${twitchUserId}/revoke-sessions`, (route) =>
    route.fulfill({ status: 204 }),
  );
}

/** POST /api/admin/users/{twitchUserId}/invalidate-role-cache — answers 200 with the number of
 *  Redis entries that were dropped, which is what the page renders back to the admin. */
export async function mockInvalidateRoleCache(
  page: Page,
  twitchUserId: string,
  removedEntries = 3,
): Promise<void> {
  await page.route(`**/api/admin/users/${twitchUserId}/invalidate-role-cache`, (route) =>
    fulfillJson(route, 200, { removedEntries }),
  );
}

/** DELETE /api/channels/{channelName}/purge — answers 204 like the real endpoint. Registered
 *  before mockChannelStatus-style routes would matter; the path suffix keeps it unambiguous. */
export async function mockPurge(page: Page, channelName: string): Promise<void> {
  await page.route(`**/api/channels/${channelName}/purge`, (route) =>
    route.fulfill({ status: 204 }),
  );
}

/** GET /api/channels/{channelName} — join status and the "join" response shape. */
export async function mockChannelStatus(
  page: Page,
  channelName: string,
  isBotActive = true,
): Promise<void> {
  await page.route(`**/api/channels/${channelName}`, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback();
    }
    return fulfillJson(route, 200, {
      channelId: '1',
      channelName,
      isBotActive,
      activeEmoteSetId: 'set-1',
    });
  });
}

/**
 * GET /api/channels/{channelName}/permissions — the single permission read behind
 * usageStatsAccessGuard, ChannelWorkspaceLayout and the vote-session list's "may manage" section.
 * Defaults to a full-access manager; pass overrides for the restricted cases.
 */
export async function mockChannelPermissions(
  page: Page,
  channelName: string,
  overrides: Partial<{
    canManage: boolean;
    canViewUsageStats: boolean;
    isGlobalAdmin: boolean;
    isTracked: boolean;
    isBotActive: boolean;
  }> = {},
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/permissions`, (route) =>
    fulfillJson(route, 200, {
      canManage: true,
      canViewUsageStats: true,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: true,
      ...overrides,
    }),
  );
}

export interface MockEmoteUsage {
  emoteId: string;
  emoteName: string;
  sevenTvEmoteId: string;
  imageUrl: string;
  totalUseCount: number;
  lastUsedDate?: string | null;
  previousWindowUseCount?: number;
  firstSeenAt?: string | null;
}

/** GET /api/channels/{channelName}/usage-stats/totals — the usage-stats page's actual data. */
export async function mockUsageTotals(
  page: Page,
  channelName: string,
  emotes: MockEmoteUsage[],
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/usage-stats/totals**`, (route) =>
    fulfillJson(
      route,
      200,
      emotes.map((emote) => ({
        ...emote,
        // Defaulted so existing specs keep describing only what they assert on. null firstSeenAt
        // is the deliberate default: unknown never counts as under observation, so no spec grows a
        // "New" badge it did not ask for.
        lastUsedDate: emote.lastUsedDate ?? null,
        previousWindowUseCount: emote.previousWindowUseCount ?? 0,
        firstSeenAt: emote.firstSeenAt ?? null,
      })),
    ),
  );
}

/** GET /api/channels/{channelName}/usage-stats/daily — the drilldown's per-emote series (A5). */
export async function mockUsageDaily(
  page: Page,
  channelName: string,
  days: { date: string; useCount: number }[],
  liveDays: string[] = [],
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/usage-stats/daily**`, (route) => {
    const url = new URL(route.request().url());
    return fulfillJson(route, 200, {
      emoteId: url.searchParams.get('emoteId') ?? 'emote-1',
      emoteName: 'MockEmote',
      from: url.searchParams.get('from') ?? '2026-07-01',
      to: url.searchParams.get('to') ?? '2026-07-28',
      totalUseCount: days.reduce((sum, day) => sum + day.useCount, 0),
      firstUsedDate: days[0]?.date ?? null,
      lastUsedDate: days.at(-1)?.date ?? null,
      days,
      liveDays,
    });
  });
}

/**
 * GET /api/channels/{channelName}/usage-stats/series — the whole set's daily curves in one
 * response, which is what feeds the atlas sidecar's sparkline.
 *
 * `days` is keyed by emote id and holds `[dayOffset, useCount]` pairs counted from the range's
 * `from` — the encoding the real endpoint uses, deliberately not prettied up here, because a mock
 * that speaks a friendlier dialect than the server is a mock that cannot catch a decoding bug.
 * Emotes absent from the map get no entry at all, which is how the server says "no usage".
 */
export async function mockUsageChannelSeries(
  page: Page,
  channelName: string,
  days: Record<string, [number, number][]>,
  liveDays: number[] = [],
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/usage-stats/series**`, (route) => {
    const url = new URL(route.request().url());
    return fulfillJson(route, 200, {
      from: url.searchParams.get('from') ?? '2026-07-01',
      to: url.searchParams.get('to') ?? '2026-07-28',
      liveDays,
      emotes: Object.entries(days).map(([emoteId, entries]) => ({ emoteId, days: entries })),
    });
  });
}

export interface MockEmoteListItem {
  sevenTvEmoteId: string;
  name: string;
}

/** GET /api/channels/{channelName}/emotes — the import target dialog's own picture of the target
 *  set (#72, K3): no usage numbers, just enough to answer "already there?" / "name collision?". */
export async function mockEmoteList(
  page: Page,
  channelName: string,
  emotes: MockEmoteListItem[],
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/emotes`, (route) => {
    if (route.request().method() !== 'GET') {
      return route.fallback();
    }
    return fulfillJson(route, 200, { emotes });
  });
}

/**
 * GET /api/channels/{channelName}/emotes/set-warning — the shared/foreign-set ownership check both
 * the mass-delete panel and the import confirm dialog read. Defaults to a clean, own, unshared set;
 * pass overrides for the shared-set-warning or check-unavailable cases.
 */
export async function mockSetWarning(
  page: Page,
  channelName: string,
  overrides: {
    available?: boolean;
    isOwnSet?: boolean;
    otherTrackedChannelsSharingSet?: string[];
    otherModeratedChannelsSharingSet?: string[];
  } = {},
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/emotes/set-warning`, (route) =>
    fulfillJson(route, 200, {
      available: overrides.available ?? true,
      isOwnSet: overrides.isOwnSet ?? true,
      otherTrackedChannelsSharingSet: overrides.otherTrackedChannelsSharingSet ?? [],
      otherModeratedChannelsSharingSet: overrides.otherModeratedChannelsSharingSet ?? [],
    }),
  );
}

/** POST /api/channels/{channelName}/emotes/sync-imported — the import run's only server-side
 *  effect (the audit entry at the target). Answers 204 like the real endpoint. */
export async function mockSyncImported(page: Page, channelName: string): Promise<void> {
  await page.route(`**/api/channels/${channelName}/emotes/sync-imported`, (route) =>
    route.fulfill({ status: 204 }),
  );
}

export interface MockLeaderboardEmote {
  sevenTvEmoteId: string;
  /** Sent as both `name` and `defaultName`: a leaderboard row is an `Emote`, not a set's aliased
   *  `EmoteSetEmote`, so it never has a separate alias (spec F7, `buildLeaderboardImportSource`). */
  name: string;
  topAllTime: number | null;
  trending: number | null;
  imageUrl?: string;
}

/**
 * GET /api/seventv/leaderboard?sortBy=... (#148, spec §4) — 7TV's network-wide leaderboard as the
 * import dialog's third source. Keyed by the exact sort code so a test answers only the sorts it
 * exercises; a requested sort with no entry here falls through unhandled (`route.fallback()`), same
 * "answer only what you mock" contract as the rest of this file. A numeric entry answers with that
 * HTTP status instead of a 200 body — used for the 503 the endpoint maps every upstream failure onto
 * (`foreign_channel_seventv_unavailable`, E13).
 */
export async function mockSevenTvLeaderboard(
  page: Page,
  responses: Partial<
    Record<
      'TRENDING_DAILY' | 'TOP_ALL_TIME',
      { totalCount: number; truncated: boolean; emotes: MockLeaderboardEmote[] } | number
    >
  >,
): Promise<void> {
  await page.route('**/api/seventv/leaderboard**', (route) => {
    const sortBy = new URL(route.request().url()).searchParams.get('sortBy');
    const entry =
      sortBy === 'TRENDING_DAILY' || sortBy === 'TOP_ALL_TIME' ? responses[sortBy] : undefined;
    if (entry === undefined) {
      return route.fallback();
    }
    if (typeof entry === 'number') {
      return fulfillJson(route, entry, { errorCode: 'foreign_channel_seventv_unavailable' });
    }
    return fulfillJson(route, 200, {
      sortBy,
      totalCount: entry.totalCount,
      truncated: entry.truncated,
      emotes: entry.emotes.map((emote) => ({
        sevenTvEmoteId: emote.sevenTvEmoteId,
        name: emote.name,
        defaultName: emote.name,
        imageUrl: emote.imageUrl ?? `https://cdn.7tv.app/emote/${emote.sevenTvEmoteId}/2x.webp`,
        topAllTime: emote.topAllTime,
        trending: emote.trending,
      })),
    });
  });
}

/**
 * GET /api/channels/{channelName}/emotes/active-set — needed for the mass-delete panel to render,
 * and the source of the slot-budget bar above the grid. Also carries `duplicateNames` (#45): the
 * usage-stats page's collision banner reads it straight from here since the dedicated
 * `.../emotes/duplicate-names` route was retired — an empty array (the default) means every active
 * emote name is unique and no banner renders.
 */
export async function mockActiveEmoteSet(
  page: Page,
  channelName: string,
  activeEmoteSetId = 'set-1',
  status: {
    capacity?: number | null;
    occupiedSlots?: number;
    trackedSince?: string;
    syncFailureReason?: string | null;
    lastSyncAttemptAtUtc?: string | null;
    // Default null, matching today's behaviour for every existing caller that does not set it: no
    // bot ever seen, no caption. The same holds for the shared-chat date below — no mirrored
    // message ever seen, so no shared-chat sentence.
    botsExcludedSince?: string | null;
    sharedChatSeparatedSince?: string | null;
    duplicateNames?: {
      name: string;
      emotes: { emoteId: string; sevenTvEmoteId: string; imageUrl: string }[];
    }[];
  } = {},
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/emotes/active-set`, (route) =>
    fulfillJson(route, 200, {
      activeEmoteSetId,
      capacity: status.capacity ?? 1000,
      occupiedSlots: status.occupiedSlots ?? 3,
      trackedSince: status.trackedSince ?? '2026-06-12T09:14:00Z',
      syncFailureReason: status.syncFailureReason ?? null,
      lastSyncAttemptAtUtc: status.lastSyncAttemptAtUtc ?? null,
      botsExcludedSince: status.botsExcludedSince ?? null,
      sharedChatSeparatedSince: status.sharedChatSeparatedSince ?? null,
      duplicateNames: status.duplicateNames ?? [],
    }),
  );
}

export interface MockVoteSession {
  id: number;
  title: string;
  isActive?: boolean;
  allowedVoterRoles?: number;
  startedAt?: string;
  endedAt?: string | null;
  emoteCount?: number | null;
  hideResultsUntilEnd?: boolean;
}

/**
 * GET /api/channels/{channelName}/vote-sessions — the session list. Matched by an exact pathname
 * predicate rather than a glob: the request carries `?page=…` (whose `?` a glob would read as a
 * wildcard), and a trailing `**` would also swallow the `/{sessionId}/results` sub-route below.
 */
export async function mockVoteSessionList(
  page: Page,
  channelName: string,
  sessions: MockVoteSession[],
): Promise<void> {
  const path = `/api/channels/${channelName}/vote-sessions`;
  await page.route(
    (url) => url.pathname === path,
    (route) => {
      if (route.request().method() !== 'GET') {
        return route.fallback();
      }
      return fulfillJson(route, 200, {
        items: sessions.map((session) => ({
          id: session.id,
          title: session.title,
          allowedVoterRoles: session.allowedVoterRoles ?? 1,
          isActive: session.isActive ?? true,
          startedAt: session.startedAt ?? '2026-07-31T12:00:00Z',
          endedAt: session.endedAt ?? null,
          emoteCount: session.emoteCount ?? null,
          hideResultsUntilEnd: session.hideResultsUntilEnd ?? false,
        })),
        page: 1,
        pageSize: 20,
        totalCount: sessions.length,
        totalPages: sessions.length === 0 ? 0 : 1,
      });
    },
  );
}

export interface MockVoteSessionEmote {
  emoteId: string;
  emoteName: string;
  sevenTvEmoteId?: string;
  imageUrl?: string;
  totalUseCount?: number | null;
  keepVotes?: number | null;
  deleteVotes?: number | null;
  score?: number | null;
  isArchived?: boolean;
  myVote?: 1 | 2 | null;
}

/** GET /api/channels/{channelName}/vote-sessions/{sessionId}/results — the detail page's data. */
export async function mockVoteSessionResults(
  page: Page,
  channelName: string,
  session: MockVoteSession,
  emotes: MockVoteSessionEmote[],
): Promise<void> {
  await page.route(`**/api/channels/${channelName}/vote-sessions/${session.id}/results`, (route) =>
    fulfillJson(route, 200, {
      sessionId: session.id,
      title: session.title,
      allowedVoterRoles: session.allowedVoterRoles ?? 1,
      isActive: session.isActive ?? true,
      startedAt: session.startedAt ?? '2026-07-31T12:00:00Z',
      endedAt: session.endedAt ?? null,
      voterCount: 3,
      hideResultsUntilEnd: session.hideResultsUntilEnd ?? false,
      emotes: emotes.map((emote) => ({
        emoteId: emote.emoteId,
        emoteName: emote.emoteName,
        sevenTvEmoteId: emote.sevenTvEmoteId ?? `7tv-${emote.emoteId}`,
        imageUrl: emote.imageUrl ?? 'https://cdn.7tv.app/emote/1/1x.webp',
        // `?? default` would swallow an explicit null, and null is not "unset" here — it is the
        // server's withholding verdict for a running secret ballot, which a spec has to be able
        // to express. Only `undefined` falls back.
        totalUseCount: emote.totalUseCount === undefined ? 10 : emote.totalUseCount,
        keepVotes: emote.keepVotes === undefined ? 2 : emote.keepVotes,
        deleteVotes: emote.deleteVotes === undefined ? 1 : emote.deleteVotes,
        score: emote.score === undefined ? 1 : emote.score,
        isArchived: emote.isArchived ?? false,
        myVote: emote.myVote ?? null,
      })),
    }),
  );
}

export interface LiveEventFrame {
  type: string;
  channel?: string;
  sessionId?: number;
}

/**
 * Replaces `window.EventSource` with an inert stub before the app boots.
 *
 * `page.route()` cannot help here: SSE is a long-lived streaming response, and the live endpoints
 * do not exist behind `ng serve`'s dev proxy in these tests — every page that opens one would sit
 * in the browser's ~3-second reconnect loop for the whole spec. In the audit harness it is worse
 * still: an open EventSource means `waitForLoadState('networkidle')` never resolves.
 *
 * Also exposes `window.__emitLive(event)` so a spec can push a frame into the running app; use the
 * typed {@link emitLive} wrapper below rather than calling it by hand.
 */
export async function installLiveStub(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const instances: StubEventSource[] = [];

    type MessageListener = (event: { data: string }) => void;

    class StubEventSource {
      static readonly CONNECTING = 0;
      static readonly OPEN = 1;
      static readonly CLOSED = 2;

      readyState = 1;
      onopen: (() => void) | null = null;
      onmessage: MessageListener | null = null;
      onerror: (() => void) | null = null;
      private readonly listeners: MessageListener[] = [];

      constructor(readonly url: string) {
        instances.push(this);
      }

      addEventListener(type: string, listener: MessageListener): void {
        if (type === 'message') {
          this.listeners.push(listener);
        }
      }

      removeEventListener(type: string, listener: MessageListener): void {
        if (type !== 'message') {
          return;
        }
        const index = this.listeners.indexOf(listener);
        if (index >= 0) {
          this.listeners.splice(index, 1);
        }
      }

      close(): void {
        this.readyState = 2;
        const index = instances.indexOf(this);
        if (index >= 0) {
          instances.splice(index, 1);
        }
      }

      dispatch(data: string): void {
        this.onmessage?.({ data });
        for (const listener of [...this.listeners]) {
          listener({ data });
        }
      }
    }

    const global = window as unknown as Record<string, unknown>;
    global['EventSource'] = StubEventSource;
    // The refusal half of the stub: the browser signals a rejected handshake by moving to CLOSED
    // and firing onerror, carrying neither status code nor body. That is the whole reason
    // LiveQuotaService has to go and ask — so a state built on it needs this to be reproducible.
    global['__failLive'] = () => {
      for (const instance of [...instances]) {
        instance.readyState = 2;
        instance.onerror?.();
      }
    };
    global['__emitLive'] = (event: unknown) => {
      const data = JSON.stringify(event);
      for (const instance of [...instances]) {
        if (instance.readyState !== 2) {
          instance.dispatch(data);
        }
      }
    };
  });
}

/**
 * Fails every open stream the way a refused handshake does — readyState CLOSED plus `onerror`, and
 * nothing else, because that is all the browser gives the page. What made the stream fail (429 over
 * the connection budget, 503, a revoked session) is only knowable by asking afterwards.
 */
export async function failLive(page: Page): Promise<void> {
  await page.evaluate(() => (window as unknown as { __failLive: () => void }).__failLive());
}

/** Answers GET /api/live/status — the probe LiveQuotaService runs after a stream was refused. */
export async function mockLiveQuota(
  page: Page,
  status: { openConnections: number; maxPerSubscriber: number; perSubscriberLimitReached: boolean },
): Promise<void> {
  await page.route('**/api/live/status', (route) => fulfillJson(route, 200, status));
}

/** Pushes one live-update frame into every EventSource the app currently holds open. */
export async function emitLive(page: Page, event: LiveEventFrame): Promise<void> {
  await page.evaluate(
    (frame) => (window as unknown as { __emitLive: (e: unknown) => void }).__emitLive(frame),
    event,
  );
}

/** Parsed body of one call to 7TV's GQL endpoint, handed to a `mockSevenTvGql` handler. */
export interface SevenTvGqlRequest {
  query: string;
  variables: Record<string, unknown>;
}

/**
 * One GQL error as 7TV's v4 actually shapes it — `extensions.code`/`extensions.status` alongside the
 * message, not just the message. Mirrors `SevenTvGqlError` in `seven-tv-run-engine.ts`, which is what
 * `abortsForMissingPrivileges` and the rate-limit detection both read. A handler that only needs a
 * plain-text failure can still omit `extensions`.
 */
export interface SevenTvGqlErrorFixture {
  message: string;
  extensions?: {
    code?: string;
    status?: number;
    headers?: Record<string, string>;
  };
}

/**
 * Routes `https://7tv.io/v4/gql` — the run engine's one write endpoint (ADD/REMOVE mutations for
 * import, delete and restore alike). The handler sees each call's parsed body plus a zero-based
 * call index (the run engine issues one request per queued row, in order), and returns the GQL
 * response body to answer with; always a 200 with either `data` or `errors` — 7TV's own contract,
 * which is what `SevenTvRunEngine` reads its outcome from rather than the HTTP status.
 *
 * Sets `ep_7tv_write_token` in `sessionStorage` via `addInitScript` (R14) so the write flow never
 * hits the token prompt — call before `page.goto`, same as `installLiveStub`.
 */
export async function mockSevenTvGql(
  page: Page,
  handler: (
    request: SevenTvGqlRequest,
    callIndex: number,
  ) => { data?: unknown; errors?: SevenTvGqlErrorFixture[] },
): Promise<void> {
  await page.addInitScript(() => {
    window.sessionStorage.setItem('ep_7tv_write_token', 'e2e-fake-write-token');
  });
  let callIndex = 0;
  await page.route('https://7tv.io/v4/gql', (route) => {
    const request = route.request().postDataJSON() as SevenTvGqlRequest;
    const body = handler(request, callIndex);
    callIndex += 1;
    return fulfillJson(route, 200, body);
  });
}
