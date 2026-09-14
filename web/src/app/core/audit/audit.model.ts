/**
 * The audit log's wire types, shared by the two endpoints that serve it: the global-admin log
 * (`/api/admin/audit-log`) and a channel's own activity feed (`/api/channels/{c}/audit-log`).
 *
 * They live here rather than in `core/admin/` because that folder is documented as the
 * global-admin-only client, and a channel manager's page must not have to import from it.
 */

/**
 * The audited actions, mirroring `EmotePurge.Core.Entities.AuditActions`. Typed as a union of
 * the literal strings the server sends, but consumers must still handle an unknown value: an entry
 * written by a newer backend carries an action this build has no label for, and a log that hides
 * rows it cannot name would be worse than one that shows the raw string.
 */
export type AuditAction =
  | 'channel.join'
  | 'channel.leave'
  | 'channel.purge'
  | 'channel.resync'
  | 'channel.rename'
  | 'channel.merge'
  | 'voteSession.create'
  | 'voteSession.end'
  | 'voteSession.delete'
  | 'emotes.syncDeleted'
  | 'emotes.syncRestored'
  | 'emotes.syncImported'
  | 'user.revokeSessions'
  | 'user.invalidateRoleCache';

/** The recognized `AuditLogDetail.kind` values, mirroring `AuditLogDetail.Kinds` on the server. */
export type AuditDetailKind =
  | 'emoteCount'
  | 'removedEntries'
  | 'title'
  | 'importedFromChannel'
  | 'importedFromFile'
  | 'importedFromLeaderboard';

/**
 * The renderable part of an entry's details, already reduced to a closed set of shapes by the
 * server. `count` is set for the counting kinds, `text` for the naming ones.
 *
 * The whitelist that produces this lives in `AuditLogQueryService.ProjectDetail`, deliberately not
 * here: the underlying column is free-form, and a client-side filter would be one more place to
 * forget when a new write path adds a key. An unknown `kind` renders nothing — that is a display
 * decision, not a safety one.
 */
export interface AuditLogDetail {
  kind: AuditDetailKind | string;
  count: number | null;
  text: string | null;
}

/**
 * One audit-log row (paged via the shared `PagedResult<T>` envelope).
 *
 * `actorLogin` and `channelName` are snapshots taken when the action happened, not live joins — a
 * renamed account or a purged channel still shows what was true at the time, which is the point of
 * an audit log.
 */
export interface AuditLogEntry {
  id: number;
  occurredAtUtc: string;
  actorLogin: string;
  action: AuditAction | string;
  channelName: string | null;
  targetType: string | null;
  targetId: string | null;
  detail: AuditLogDetail | null;
}
