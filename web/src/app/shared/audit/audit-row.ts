import { ACTION_KEYS, DETAIL_KEYS, TARGET_EMOTE_SET_KEYS } from './audit-actions';
import { AuditLogDetail, AuditLogEntry } from '../../core/audit/audit.model';
import { pluralKey } from '../../core/i18n/plural';
import {
  LEADERBOARD_SORT_LABEL_KEYS,
  isLeaderboardSort,
} from '../../core/seven-tv/leaderboard.model';

/** How many characters of a 7TV set id the audit row shows (spec 8.10's "id-Kurzform") — the same
 *  short-hash convention as a Git commit, long enough to disambiguate by eye, short enough not to
 *  dominate the line. The full id is never shown anywhere in this view; it identifies nothing a
 *  reader compares by hand. */
const TARGET_SET_ID_SHORT_LENGTH = 8;

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
  /** The target-set addendum (spec 8.10) — a second, independent line segment appended after
   *  `detail`, never folded into it: `detail` already has its own kind-specific key (`emoteCount`,
   *  `importedFromFile`, …), and multiplying that by the three target-set cases would turn one small
   *  table into a combinatorial one for no reason a reader would notice. Present on both the import
   *  ladder's rows and the set-scoped `sync-deleted`/`sync-restored` rows (spec 6.6, K5) — purely
   *  from `AuditLogDetail.targetEmoteSet`, never from the row's action or kind. `null` whenever the
   *  entry names no target set at all. */
  targetSet: RenderedDetail | null;
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
    targetSet: renderTargetSet(entry.detail),
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
 *
 * Five of the six `DETAIL_KEYS` carry a `count` (every one but `title`) — routed through
 * `pluralKey` here rather than baked into `DETAIL_KEYS` itself, because the lookup table stays a
 * plain kind-to-key map and the `.one`/`.other` suffixing is this function's business alone, same
 * as everywhere else in the app that calls `pluralKey`.
 */
function renderDetail(
  detail: AuditLogDetail | null,
  translate: (key: string) => string,
): RenderedDetail | null {
  const baseKey = detail && DETAIL_KEYS[detail.kind];
  if (!detail || !baseKey) {
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

  const key = detail.count === null ? baseKey : pluralKey(detail.count, baseKey);
  return { key, params };
}

/**
 * The target-set addendum (spec 8.10) — the import ladder's rows and the set-scoped
 * `sync-deleted`/`sync-restored` rows alike (spec 6.6, K5). Independent of `renderDetail` above — it
 * reads the same `AuditLogDetail`, but the two functions never influence each other's outcome: a
 * row with an unrecognized `kind` (renderDetail returning `null`) can still name its target set,
 * and a row naming no target set at all renders exactly as it always did.
 *
 * `ownerLogin` is checked before `isActiveSetOfChannel`, not the other way round, but the order is
 * moot in practice: the two are mutually exclusive by construction (`AuditLogTargetEmoteSet`'s own
 * doc) — the channel-scoped endpoint sets `isActiveSetOfChannel` and leaves `ownerLogin` `null`, the
 * set-centric one does the reverse. A row can therefore never need the not-active *and* the owner
 * phrasing at once.
 */
function renderTargetSet(detail: AuditLogDetail | null): RenderedDetail | null {
  const targetSet = detail?.targetEmoteSet;
  if (targetSet == null) {
    return null;
  }

  const setId = targetSet.id.slice(0, TARGET_SET_ID_SHORT_LENGTH);
  if (targetSet.ownerLogin !== null) {
    return {
      key: TARGET_EMOTE_SET_KEYS.forOwner,
      params: { setId, ownerLogin: targetSet.ownerLogin },
    };
  }
  if (targetSet.isActiveSetOfChannel === false) {
    return { key: TARGET_EMOTE_SET_KEYS.notActive, params: { setId } };
  }
  return { key: TARGET_EMOTE_SET_KEYS.plain, params: { setId } };
}
