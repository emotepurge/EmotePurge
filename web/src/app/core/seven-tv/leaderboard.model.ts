import { ForeignEmoteRow } from './foreign-emote-set.model';

/**
 * The two sort orders 7TV's network-wide leaderboard endpoint accepts (spec §4) — the server's
 * closed, ordinal allowlist. This string also travels the wire twice more: as the fourth
 * `ImportOrigin` member's `sortBy` and as `SyncImportedBody.leaderboardSort`, and the server keeps
 * it forever as the language-neutral `AuditLogDetail.text` for the `importedFromLeaderboard` kind
 * (E9) — the audit view's `renderDetail` and this dialog's origin line both translate it through
 * {@link LEADERBOARD_SORT_LABEL_KEYS} rather than showing it raw.
 */
export type LeaderboardSort = 'TRENDING_DAILY' | 'TOP_ALL_TIME';

/**
 * Response shape of `GET /api/seventv/leaderboard?sortBy=...` (spec §4). Built by the T3 backend
 * endpoint; this frontend slice is written against the contract, not the implementation — T3 lands
 * independently in the same worktree.
 */
export interface SevenTvLeaderboardResponse {
  /** Echo of the requested sort — the allowlist vocabulary, not display text. */
  sortBy: LeaderboardSort;
  /** What 7TV reports as the sort's total entry count (up to ~1.37M for `TOP_ALL_TIME`, spec §4). */
  totalCount: number;
  /** `true` whenever the up-to-500-row cap was hit while `totalCount` promised more — the normal
   *  case on a network-wide leaderboard (spec §4/§7), not an edge case like the foreign-channel
   *  set's `truncated`. */
  truncated: boolean;
  /** In 7TV's own rank order, at most 500 entries (spec E5). */
  emotes: ForeignEmoteRow[];
}

/**
 * What `LeaderboardStep` (Task 6) yields once the user has picked emotes off one sort order — the
 * leaderboard counterpart of `ForeignChannelImportResult`. Deliberately no channel/set identity
 * here: unlike a foreign channel's set, a leaderboard row has no source channel at all (E2) —
 * `sortBy` *is* the origin, and `buildLeaderboardImportSource` (Task 6) turns it into the fourth
 * `ImportOrigin` member.
 */
export interface LeaderboardImportResult {
  sortBy: LeaderboardSort;
  /** Only the rows the user marked, in the grid's selection order — same contract as
   *  `ForeignChannelImportResult.rows`. */
  rows: ForeignEmoteRow[];
}

/**
 * Translation key for each sort's display label ("7TV Trend heute" / "7TV Top insgesamt", E2) —
 * the one table both the import confirmation dialog's origin line and the audit view's
 * `renderDetail` translate a `LeaderboardSort`/the server's echoed sort code through, so the two
 * surfaces say exactly the same thing (E2: "Der Bestätigungsdialog zeigt dasselbe") rather than
 * keeping two wordings in sync by hand. Keyed under `audit.details.*` rather than `import.confirm.*`
 * because the audit row is where the value first has to survive an *unrecognized* code (E9) — the
 * dialog only ever sees a `LeaderboardSort` it built itself and can key this table with directly.
 */
export const LEADERBOARD_SORT_LABEL_KEYS: Record<LeaderboardSort, string> = {
  TRENDING_DAILY: 'audit.details.leaderboardSort.TRENDING_DAILY',
  TOP_ALL_TIME: 'audit.details.leaderboardSort.TOP_ALL_TIME',
};

/** Narrows a wire string to {@link LeaderboardSort} — the guard `renderDetail` needs because
 *  `AuditLogDetail.text` arrives as a plain, unvalidated `string`: a future sort this build does not
 *  know about must degrade instead of indexing {@link LEADERBOARD_SORT_LABEL_KEYS} with a key it
 *  does not have. */
export function isLeaderboardSort(value: string): value is LeaderboardSort {
  return value === 'TRENDING_DAILY' || value === 'TOP_ALL_TIME';
}
