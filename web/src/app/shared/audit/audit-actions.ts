import { AuditAction } from '../../core/audit/audit.model';

/**
 * One translation key per `AuditActions` constant. A lookup rather than a string transform
 * ("channel.join" → "channelJoin"), so a newly added action shows up as an obvious gap here instead
 * of silently producing a key that exists in neither locale file.
 *
 * The keys sit under a top-level `audit.*` namespace rather than `admin.audit.*`: the same table
 * now labels the global-admin log and a channel's own activity feed, and a broadcaster's page
 * reading its wording out of the admin namespace would be a trap for whoever reorganizes that area
 * next.
 */
export const ACTION_KEYS: Record<string, string> = {
  'channel.join': 'audit.actions.channelJoin',
  'channel.leave': 'audit.actions.channelLeave',
  'channel.purge': 'audit.actions.channelPurge',
  'channel.resync': 'audit.actions.channelResync',
  'channel.rename': 'audit.actions.channelRename',
  'channel.merge': 'audit.actions.channelMerge',
  'voteSession.create': 'audit.actions.voteSessionCreate',
  'voteSession.end': 'audit.actions.voteSessionEnd',
  'voteSession.delete': 'audit.actions.voteSessionDelete',
  'emotes.syncDeleted': 'audit.actions.emotesSyncDeleted',
  'emotes.syncRestored': 'audit.actions.emotesSyncRestored',
  'emotes.syncImported': 'audit.actions.emotesSyncImported',
  'user.revokeSessions': 'audit.actions.userRevokeSessions',
  'user.invalidateRoleCache': 'audit.actions.userInvalidateRoleCache',
  'user.delete': 'audit.actions.userDelete',
};

/**
 * Actions without a channel dimension. Two consumers, opposite readings: the admin log disables its
 * channel filter while one of these is selected, and a channel-scoped feed can never contain them
 * at all — so its filter must not offer them.
 */
export const CHANNELLESS_ACTIONS: ReadonlySet<string> = new Set<AuditAction>([
  'user.revokeSessions',
  'user.invalidateRoleCache',
  'user.delete',
]);

/** The actions a single channel's log can contain — everything except the user-scoped ones. */
export const CHANNEL_SCOPED_ACTIONS: readonly string[] = Object.keys(ACTION_KEYS).filter(
  (action) => !CHANNELLESS_ACTIONS.has(action),
);

/**
 * One translation key per recognized detail kind. Purely a display table: the server has already
 * whitelisted the payload down to these kinds (`AuditLogQueryService.ProjectDetail`), so an
 * unfamiliar kind here means this build is older than the backend, not that something unsafe
 * arrived — it renders nothing and the row keeps its action and actor.
 */
export const DETAIL_KEYS: Record<string, string> = {
  emoteCount: 'audit.details.emoteCount',
  removedEntries: 'audit.details.removedEntries',
  title: 'audit.details.title',
  importedFromChannel: 'audit.details.importedFromChannel',
  importedFromFile: 'audit.details.importedFromFile',
  importedFromLeaderboard: 'audit.details.importedFromLeaderboard',
};

/**
 * Translation keys for the target-set addenda (spec 8.10) — appended *after* a row's own detail
 * line, never in place of it. Deliberately not part of `DETAIL_KEYS`: `targetEmoteSet` never selects
 * a `kind` on its own, it only annotates whichever kind the row already has (mirroring the server,
 * `AuditLogQueryService.ProjectDetail`/`TryProjectImportDetail`), so it needs its own small table
 * rather than a seventh entry there. Covers the import ladder's rows and the set-scoped
 * `sync-deleted`/`sync-restored` rows alike (spec 6.6, K5) — both feed the same
 * `AuditLogDetail.targetEmoteSet` shape.
 *
 * Three keys, one per case the row can show: the plain form (a set was reported, nothing further to
 * say); the not-the-active-set form (channel-scoped endpoints — `sync-imported` or the set-scoped
 * `sync-deleted`/`sync-restored` — `isActiveSetOfChannel === false`); and the owner form (set-centric
 * `sync-imported` endpoint, which has no channel of ours to compare against at all and names its
 * owner's Twitch login instead, spec 6.7). The latter two never co-occur — a row's
 * `AuditLogTargetEmoteSet` is either channel-scoped (`isActiveSetOfChannel` three-valued,
 * `ownerLogin` always `null`) or set-centric (`isActiveSetOfChannel` always `null`, `ownerLogin`
 * set) — so a row needs at most one of the three keys, never two.
 */
export const TARGET_EMOTE_SET_KEYS = {
  plain: 'audit.details.targetEmoteSet',
  notActive: 'audit.details.targetEmoteSetNotActive',
  forOwner: 'audit.details.targetEmoteSetForOwner',
} as const;
