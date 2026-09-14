import { ACTION_KEYS, DETAIL_KEYS } from './audit-actions';
import { AuditLogDetail, AuditLogEntry } from '../../core/audit/audit.model';
import {
  LEADERBOARD_SORT_LABEL_KEYS,
  isLeaderboardSort,
} from '../../core/seven-tv/leaderboard.model';

/** A detail reduced to what the template hands to Transloco. */
export interface RenderedDetail {
  key: string;
  params: Record<string, string | number>;
}

/** A row as the template consumes it — every derivation done once, in TypeScript. */
export interface AuditRow {
  id: number;
  occurredAtUtc: string;
  timestamp: string;
  actorLogin: string;
  /** Translation key for the action, or null when this build does not know the action. */
  actionKey: string | null;
  /** The raw action string, shown verbatim when `actionKey` is null. */
  action: string;
  channelName: string | null;
  detail: RenderedDetail | null;
}

/**
 * Projects wire entries into rows for a given locale.
 *
 * The locale is a parameter rather than read from a service, so a language switch re-formats the
 * timestamps: `LOCALE_ID` is fixed at bootstrap and cannot follow one. Seconds are shown because
 * several audited actions can legitimately land in the same minute.
 *
 * `translate` is likewise a parameter rather than an injected `TranslocoService`: this is a plain
 * function, not a component, and it is called from inside a `computed()`, which does not run in an
 * injection context. The one detail kind that needs it is `importedFromLeaderboard` — the server
 * sends a language-neutral sort code there (E9), the only `AuditLogDetail.text` that is a code
 * rather than already-displayable text.
 */
export function toAuditRows(
  entries: readonly AuditLogEntry[],
  locale: string,
  translate: (key: string) => string,
): AuditRow[] {
  return entries.map((entry) => ({
    id: entry.id,
    occurredAtUtc: entry.occurredAtUtc,
    timestamp: new Date(entry.occurredAtUtc).toLocaleString(locale, {
      dateStyle: 'short',
      timeStyle: 'medium',
    }),
    actorLogin: entry.actorLogin,
    actionKey: ACTION_KEYS[entry.action] ?? null,
    action: entry.action,
    channelName: entry.channelName,
    detail: renderDetail(entry.detail, translate),
  }));
}

/**
 * Builds the interpolation params conditionally, one per field that is actually set, rather than
 * picking either-or: `importedFromChannel` is the one kind that carries both a count and a title
 * at once (R1 in the #71 import plan), and the older kinds each set exactly one of the two fields
 * anyway, so this stays a no-op change for them.
 *
 * `importedFromLeaderboard` is the one exception to "`text` is already the title": its `text` is
 * the server's language-neutral sort code (#148, E9), translated here through
 * `LEADERBOARD_SORT_LABEL_KEYS` rather than interpolated raw. An unrecognized code — a build older
 * than the backend that wrote the row — drops the whole detail, same as an unrecognized `kind`
 * below: a missing-key placeholder or the bare code would be worse than the row simply keeping its
 * action and actor.
 */
function renderDetail(
  detail: AuditLogDetail | null,
  translate: (key: string) => string,
): RenderedDetail | null {
  const key = detail && DETAIL_KEYS[detail.kind];
  if (!detail || !key) {
    return null;
  }

  const params: Record<string, string | number> = {};
  if (detail.count !== null) {
    params['count'] = detail.count;
  }

  if (detail.kind === 'importedFromLeaderboard') {
    if (detail.text === null || !isLeaderboardSort(detail.text)) {
      return null;
    }
    params['title'] = translate(LEADERBOARD_SORT_LABEL_KEYS[detail.text]);
  } else if (detail.text !== null) {
    params['title'] = detail.text;
  }

  return { key, params };
}
