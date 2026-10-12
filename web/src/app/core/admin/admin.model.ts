import { ChannelLiveState } from '../channels/channel.model';
import { SevenTvSyncFailureReason } from '../emotes/seven-tv-sync-failure';

/** Twitch IRC state, derived server-side from the worker's snapshot (thresholds live in the Api). */
export type WorkerConnectionStatus = 'connected' | 'stale' | 'disconnected' | 'unknown';

/** Same, for the 7TV EventAPI — plus `disabled`, which keeps a switched-off event path
 *  distinguishable from a broken one (the feature flag `SevenTv:EventApi:Enabled`). */
export type SevenTvConnectionStatus = WorkerConnectionStatus | 'disabled';

export interface SevenTvHealth {
  status: SevenTvConnectionStatus;
  enabled: boolean;
  connected: boolean;
  lastFrameUtc: string | null;
  lastDispatchUtc: string | null;
  connectAttemptedUtc: string | null;
  secondsSinceLastFrame: number | null;
  desiredChannelCount: number | null;
  desiredSubscriptionCount: number | null;
  unacknowledgedCount: number | null;
  /** 7TV's per-connection `subscription_limit`, taken from the last Hello frame when the worker has
   *  seen one and falling back to the documented 500 otherwise — so the utilization bar's
   *  denominator isn't hard-coded a second time here. */
  subscriptionLimit: number;
  /** How often the worker runs its full REST resync. Not a quota but a divisor: it is what turns
   *  "one request per channel" into a rate the page can state instead of imply. */
  resyncIntervalSeconds: number | null;
}

export interface FlushHealth {
  consecutiveFailures: number | null;
  lastSuccessUtc: string | null;
  lastRowCount: number | null;
  pendingEmoteCount: number | null;
}

/**
 * GET /api/admin/health — the full worker snapshot, admin-only (Z1 health split; the public
 * GET /api/worker/health stays minimal). Every detail field is nullable because a snapshot written
 * by an older worker simply lacks it, and "unknown" must not render as zero.
 */
export interface AdminHealth {
  /** False when the Redis key expired or was never written — the worker is gone or wedged. */
  snapshotAvailable: boolean;
  status: WorkerConnectionStatus;
  isConnected: boolean;
  lastMessageReceivedUtc: string | null;
  connectAttemptedUtc: string | null;
  secondsSinceLastMessage: number | null;
  sevenTv: SevenTvHealth;
  flush: FlushHealth;
  worker: WorkerIdentity;
}

/** Which worker process wrote the snapshot, and since when it has been running. Counters reset on
 *  restart, so "0 failures" means something different in the first minute than after six hours. */
export interface WorkerIdentity {
  instanceId: string | null;
  processStartedUtc: string | null;
}

/**
 * One row of GET /api/admin/channels. Counts come in total/subset pairs — `emoteCount` and
 * `voteSessionCount` are the full counts, `archivedEmoteCount`/`activeVoteSessionCount` the subsets
 * — so the page renders "12 (3 archiviert)" without doing arithmetic the server didn't sanction.
 */
export interface AdminChannel {
  channelName: string;
  twitchChannelId: string | null;
  isBotActive: boolean;
  createdAt: string;
  emoteCount: number;
  archivedEmoteCount: number;
  activeVoteSessionCount: number;
  voteSessionCount: number;
  /** When a full 7TV REST sync last completed, changed or not. This is the number that answers "is
   *  the sync running at all"; null means none has completed since the column exists. */
  lastSyncedAtUtc: string | null;
  /** When the emote inventory last actually moved. Deliberately separate from `lastSyncedAtUtc`: a
   *  healthy channel whose set nobody edits has a fresh sync and an ancient inventory change, and
   *  showing only the latter made that look like a stalled bot. Null when it has no emotes at all. */
  lastInventoryChangeUtc: string | null;
  /** Null when the channel has never synced — the server maps its empty-string default to null so
   *  the UI never renders a nameless set that exists. */
  activeEmoteSetId: string | null;
  activeEmoteSetCapacity: number | null;
  /** Why the last 7TV sync attempt produced nothing (`SevenTvSyncFailureReason`), else null. The
   *  field that tells a channel with no active 7TV emote set apart from one joined a second ago. */
  lastSyncFailureReason: SevenTvSyncFailureReason | null;
  /** When the last attempt finished, successful or not. Says how current `lastSyncFailureReason`
   *  is; null means none has been made. */
  lastSyncAttemptAtUtc: string | null;
  trackingResumedAt: string | null;
  /** From the worker's live-status snapshot, not the database — see `ChannelLiveState`. */
  liveState: ChannelLiveState;
}

/**
 * GET /api/admin/channels. The poll timestamp is snapshot-scoped (one Helix poll covered every
 * row), so it lives here and not on each channel.
 */
export interface AdminChannelsResult {
  channels: AdminChannel[];
  livePolledAtUtc: string | null;
}

/**
 * One channel as the worker itself currently sees it, from the roster snapshot it publishes to
 * Redis. This is the worker's in-memory truth, not the database's — comparing the two is the whole
 * point, so never merge the two shapes.
 */
export interface RosterChannel {
  channelName: string;
  ircJoinConfirmed: boolean;
  lastMessageUtc: string | null;
  /** Null means the worker holds no 7TV subscription intent for this channel at all. */
  sevenTvEmoteSetId: string | null;
  sevenTvEmoteSetAcknowledged: boolean;
  /** Null means no user subscription is desired, so `sevenTvUserAcknowledged: false` is a statement
   *  about nothing rather than a pending acknowledgement. */
  sevenTvUserId: string | null;
  sevenTvUserAcknowledged: boolean;
}

/** The ceilings the roster reports against. Two numbers on the Twitch side on purpose: one is
 *  Twitch's rule, the other is ours — the UI labels each with its provenance. */
export interface RosterCeilings {
  twitchConcurrentChannelLimit: number;
  twitchJoinBudgetChannels: number;
  /** Our own admission cap (`Channels:MaxActiveChannels`); global admins may exceed it. */
  maxActiveChannels: number;
}

/**
 * GET /api/admin/roster — the worker's roster compared against the channels the database considers
 * active. Its own endpoint rather than more fields on /health, which every open monitoring page
 * refetches three times as often.
 *
 * Every field except `snapshotAvailable`, `trackedChannelCount` and `ceilings` is optional: when the
 * Redis key expired there is nothing to report, and inventing zeros would read as "the worker is up
 * and has joined nothing".
 */
export interface AdminRoster {
  snapshotAvailable: boolean;
  trackedChannelCount: number;
  ceilings: RosterCeilings;
  generatedAtUtc?: string;
  /** Staleness as a number, derived server-side — the TTL only ever says "gone", this says
   *  "present, and two minutes old". */
  ageSeconds?: number;
  workerInstanceId?: string;
  processStartedUtc?: string;
  /** False means the worker is still rejoining after a restart and every deficit below is expected.
   *  Without it a redeploy reads as a total outage for about a minute. */
  bootRecoveryCompleted?: boolean;
  truncated?: boolean;
  rosterChannelCount?: number;
  ircConfirmedCount?: number;
  sevenTvAcknowledgedCount?: number;
  /** Capped lists with their untruncated totals alongside — a short list standing in silently for a
   *  long one would read as "almost fine" on the page where that matters most. */
  missingFromIrc?: string[];
  missingFromIrcTotal?: number;
  missingFromSevenTv?: string[];
  missingFromSevenTvTotal?: number;
  /** The other direction: the worker holds a channel the database no longer considers active — what
   *  a leave that never reached the worker looks like. */
  unknownToDatabase?: string[];
  unknownToDatabaseTotal?: number;
}

/** GET /api/admin/channels/{channelName} — the database row and the worker's view of it, side by
 *  side rather than merged, so a disagreement between them is visible instead of resolved. */
export interface AdminChannelDetail {
  channel: AdminChannel;
  roster: {
    available: boolean;
    ageSeconds: number | null;
    bootRecoveryCompleted: boolean | null;
    workerInstanceId: string | null;
    /** Null while `available` is true is the finding, not a gap: the worker published a roster and
     *  this channel is not in it. */
    channel: RosterChannel | null;
  };
}

/**
 * One row of GET /api/admin/users (paged). Token state arrives as derived status only —
 * `hasRefreshToken` is a boolean the server computes over the encrypted column; the ciphertexts
 * themselves never appear in any API response. `sessionsValidFromUtc` is the server-side revocation
 * cutoff (null = never revoked), shown so an admin can see a forced logout took effect.
 */
export interface AdminUser {
  twitchUserId: string;
  twitchUsername: string;
  displayName: string;
  lastLogin: string;
  sessionsValidFromUtc: string | null;
  hasRefreshToken: boolean;
  twitchAccessTokenExpiresAtUtc: string | null;
  twitchTokenScopes: string | null;
}

/**
 * Optional narrowing of GET /api/admin/audit-log; fields are AND-combined server-side.
 * `action` matches exactly, `channel` matches the normalized name exactly (the server normalizes,
 * so raw input like "HandOfBlood" is fine), `actor` is a case-insensitive substring match.
 */
export interface AuditLogFilter {
  action?: string;
  channel?: string;
  actor?: string;
}

// The row type itself lives in `core/audit/audit.model.ts`: both this endpoint and the
// channel-scoped one return it, and a channel manager's page must not import from the
// global-admin client.

/** Which ASP.NET rate-limit algorithm a policy uses — decides which of the refill fields are set. */
export type RateLimitPolicyType = 'token-bucket' | 'fixed-window';

/**
 * One policy's effective configuration plus accepted/rejected counts in both retention windows
 * (`RateLimitPolicyDescriptor` in `AdminEndpoints.cs`). `tokensPerPeriod`/
 * `replenishmentPeriodSeconds` are set only for `token-bucket`, `windowSeconds` only for
 * `fixed-window` — the unused side of either kind is null rather than a borrowed number from the
 * other kind. `policies` always lists every registered policy, even one the counter store never
 * saw traffic for, so a quiet policy stays distinguishable from one that was never registered.
 */
export interface RateLimitPolicySnapshot {
  name: string;
  type: RateLimitPolicyType;
  capacity: number;
  tokensPerPeriod: number | null;
  replenishmentPeriodSeconds: number | null;
  windowSeconds: number | null;
  partition: string;
  queueLimit: number;
  acceptedLastMinute: number;
  rejectedLastMinute: number;
  acceptedLast24Hours: number;
  rejectedLast24Hours: number;
}

/** The most recent rejection produced by any local policy — one entry, overwritten each time. */
export interface RateLimitLastRejection {
  observedAtUtc: string;
  httpMethod: string;
  routeTemplate: string;
  policyName: string;
  partition: string;
  retryAfterSeconds: number | null;
}

/** Hits and misses of one server-side cache, in both retention windows. */
export interface RateLimitCacheCounters {
  cacheName: string;
  hitsLastMinute: number;
  missesLastMinute: number;
  hitsLast24Hours: number;
  missesLast24Hours: number;
}

/** The provider's own `Ratelimit-*` headers as last seen — a sample, never a reservable or
 *  authoritative shared budget. */
export interface ProviderRateLimitHeaderSample {
  observedAtUtc: string;
  limit: string | null;
  remaining: string | null;
  reset: string | null;
}

/**
 * What one provider client did and what came back, per (provider, call-source) pair. Deliberately
 * without a percentage: for 7TV there is no defensible denominator, and reporting one for Twitch
 * only would invite reading it as a budget.
 */
export interface RateLimitProviderCounters {
  providerName: string;
  callSource: string;
  requestsLastMinute: number;
  requestsLast24Hours: number;
  rateLimitedLastMinute: number;
  rateLimitedLast24Hours: number;
  lastRetryAfterSeconds: number | null;
  lastRateLimitedAtUtc: string | null;
  lastHeaderSample: ProviderRateLimitHeaderSample | null;
}

/**
 * GET /api/admin/rate-limits — read-only rate-limit observability (design 4 of the #33
 * architecture doc). There is no write counterpart and no reservation: this only reports what
 * already happened. `caches` and `providers` list only whatever the counter store has entries for.
 * When `telemetryAvailable` is false the counter store could not be reached: every count elsewhere
 * in this snapshot is a fabricated 0, not a real zero, so the UI must show configuration only and
 * must not render the counts as data.
 */
export interface RateLimitTelemetrySnapshot {
  telemetryAvailable: boolean;
  policies: RateLimitPolicySnapshot[];
  lastLocalRejection: RateLimitLastRejection | null;
  caches: RateLimitCacheCounters[];
  providers: RateLimitProviderCounters[];
}

/** Ranges the activity series supports (GET /api/admin/dashboard/activity). */
export type AdminDashboardActivityRange = '30d' | '90d' | '13mo';

/**
 * GET /api/admin/dashboard/activity — one point per UTC calendar day. `liveChannels` is null for a
 * day nobody observed: unknown, not zero, so the chart breaks the line there. The last day is the
 * running UTC day and only partial while `todayIsPartial` is true.
 */
export interface AdminDashboardActivity {
  range: AdminDashboardActivityRange;
  fromDate: string;
  toDate: string;
  todayIsPartial: boolean;
  generatedAtUtc: string;
  days: { date: string; liveChannels: number | null }[];
}

/** Ranges the load history supports (GET /api/admin/dashboard/load); `1h` and `24h` read raw samples. */
export type AdminDashboardLoadRange = '1h' | '24h' | '7d' | '30d' | '90d' | '13mo';

/** Which source a bucket value comes from: raw samples or the hourly rollup. */
export type AdminDashboardLoadSource = 'raw' | 'hourly';

/** Which of the two processes contributed to / produced a host value (`A` = Api, `W` = Worker). */
export type AdminDashboardLoadProvenance = 'A' | 'W' | 'AW';

/**
 * Host series, column-oriented: every list is as long as `bucketStartsUtc`, `null` = unknown. Both
 * processes measure the same host; `provenance` and the `*ValueSource` columns say who contributed
 * and whose hourly P95 is shown (`A` / `W`).
 */
export interface AdminDashboardHostSeries {
  cpuBusyP95: (number | null)[];
  cpuBusyMax: (number | null)[];
  memoryUsedP95Bytes: (number | null)[];
  memoryUsedMaxBytes: (number | null)[];
  memoryTotalBytes: (number | null)[];
  loadAverage1P95: (number | null)[];
  loadAverage1Max: (number | null)[];
  provenance: (AdminDashboardLoadProvenance | null)[];
  cpuValueSource: ('A' | 'W' | null)[];
  memoryValueSource: ('A' | 'W' | null)[];
  loadAverageValueSource: ('A' | 'W' | null)[];
  apiSampleCount: (number | null)[];
  workerSampleCount: (number | null)[];
  apiCpuBusyMean: (number | null)[];
  workerCpuBusyMean: (number | null)[];
  apiMemoryUsedMeanBytes: (number | null)[];
  workerMemoryUsedMeanBytes: (number | null)[];
  apiLoadAverage1Mean: (number | null)[];
  workerLoadAverage1Mean: (number | null)[];
}

/**
 * Per-process series (Api or Worker). Quota shares exist only for raw ranges; in hourly ranges the
 * limit columns carry the last limit of the hour and the share columns are all `null`.
 */
export interface AdminDashboardProcessSeries {
  processCpuP95: (number | null)[];
  processCpuMax: (number | null)[];
  cgroupCpuP95: (number | null)[];
  cgroupCpuMax: (number | null)[];
  cgroupCpuQuotaCores: (number | null)[];
  cgroupCpuQuotaShareP95: (number | null)[];
  cgroupCpuQuotaShareMax: (number | null)[];
  cgroupMemoryP95Bytes: (number | null)[];
  cgroupMemoryMaxBytes: (number | null)[];
  cgroupMemoryLimitBytes: (number | null)[];
  cgroupMemoryShareP95: (number | null)[];
  cgroupMemoryShareMax: (number | null)[];
  sampleCount: (number | null)[];
}

/**
 * Worker-only workload. `perMinute` (raw): counts divided by the real interval seconds. `perBucket`
 * (hourly): the stored interval counters summed over the hours of one output bucket.
 */
export interface AdminDashboardWorkloadSeries {
  workloadUnit: 'perMinute' | 'perBucket';
  activeChannelsMax: (number | null)[];
  liveChannelsMax: (number | null)[];
  chatLines: (number | null)[];
  emoteMatches: (number | null)[];
  workloadIntervalSeconds: (number | null)[];
  workloadHoursWithData: (number | null)[];
}

/**
 * GET /api/admin/dashboard/load — the complete, server-bucketed range. `available` is false when
 * the range holds no observation at all (every list empty, `dataThroughUtc` null). Leading empty
 * buckets are omitted, so `bucketStartsUtc[0]` may lie after `fromUtc`.
 */
export interface AdminDashboardLoad {
  available: boolean;
  range: AdminDashboardLoadRange;
  sourceKind: AdminDashboardLoadSource;
  bucketSeconds: number;
  fromUtc: string;
  toUtc: string;
  generatedAtUtc: string;
  dataThroughUtc: string | null;
  samplingIntervalSeconds: number;
  bucketStartsUtc: string[];
  host: AdminDashboardHostSeries;
  api: AdminDashboardProcessSeries;
  worker: AdminDashboardProcessSeries;
  workload: AdminDashboardWorkloadSeries;
  swap: { lastSwapUsedBytes: number; lastSwapTotalBytes: number; observedAtUtc: string } | null;
}

/**
 * GET /api/admin/rate-limits/trend — the last 24 hours in UTC minute buckets, column-oriented: one
 * shared bucket list and one equally long value list per series. When `telemetryAvailable` is false
 * the lists are empty (counter store unreachable), not zero-filled.
 */
export interface RateLimitTelemetryTrend {
  telemetryAvailable: boolean;
  fromUtc: string;
  toUtc: string;
  minuteStartsUtc: string[];
  localRejections: number[];
  providers: { providerName: string; callSource: string; rateLimited: number[] }[];
}
