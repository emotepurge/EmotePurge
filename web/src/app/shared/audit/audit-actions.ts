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
