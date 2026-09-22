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
 * An action's target set (spec 2026-09-20), present on an `AuditLogDetail` whenever the row's
 * `DetailsJson` named one. Two independent write paths feed this: the import ladder (6.7) —
 * optionally on the channel-scoped `sync-imported` (E5) or always on the set-centric endpoint —
 * and the set-scoped `sync-deleted`/`sync-restored` (6.6, K5). `id`/`ownerLogin` are never a display
 * name — identification is by id, and the paper trail records a Twitch login rather than 7TV's own
 * (changeable) display name. `ownerLogin` is exclusively an import-ladder field —
 * `sync-deleted`/`sync-restored` never resolves one.
 */
export interface AuditLogTargetEmoteSet {
  id: string;
  /**
   * Three-valued (E5): `true`/`false` when the channel-scoped endpoint compared the reported set
   * against the channel's active set at write time, `null` when no set was reported, or — for the
   * set-centric endpoint — because there is no channel of ours to compare against at all.
   */
  isActiveSetOfChannel: boolean | null;
  /** The set's owner, as a Twitch login — `null` for the channel-scoped endpoint. */
  ownerLogin: string | null;
}

/**
 * The renderable part of an entry's details, already reduced to a closed set of shapes by the
 * server. `count` is set for the counting kinds, `text` for the naming ones.
 *
 * The whitelist that produces this lives in `AuditLogQueryService.ProjectDetail`, deliberately not
 * here: the underlying column is free-form, and a client-side filter would be one more place to
 * forget when a new write path adds a key. An unknown `kind` renders nothing — that is a display
 * decision, not a safety one.
 *
 * `kind` is typed `AuditDetailKind | (string & {})` rather than plain `AuditDetailKind | string`:
 * the intersection keeps editor autocomplete offering the known literals while still widening to
 * any string, which a bare union with `string` would swallow.
 *
 * `targetEmoteSet` is optional, not just nullable: this field lands here in T2.4 purely additively,
 * ahead of the view that renders it (T2.6) — every existing literal that builds an `AuditLogDetail`
 * without it (tests, e2e mocks) stays valid rather than needing a mechanical `targetEmoteSet: null`
 * added everywhere. A server response always sends the key (`null` when the row has no target set).
 */
export interface AuditLogDetail {
  kind: AuditDetailKind | (string & {});
  count: number | null;
  text: string | null;
  targetEmoteSet?: AuditLogTargetEmoteSet | null;
}

/**
 * One audit-log row (paged via the shared `PagedResult<T>` envelope).
 *
 * `actorLogin` and `channelName` are snapshots taken when the action happened, not live joins — a
 * renamed account or a purged channel still shows what was true at the time, which is the point of
 * an audit log.
 *
 * `action` is typed `AuditAction | (string & {})` for the same reason as `AuditLogDetail.kind`:
 * the intersection keeps the known literals autocompleting without narrowing away a future value.
 */
export interface AuditLogEntry {
  id: number;
  occurredAtUtc: string;
  actorLogin: string;
  action: AuditAction | (string & {});
  channelName: string | null;
  targetType: string | null;
  targetId: string | null;
  detail: AuditLogDetail | null;
}
