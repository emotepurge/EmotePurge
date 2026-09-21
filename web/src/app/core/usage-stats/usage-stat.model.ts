/**
 * GET /api/channels/{c}/usage-stats/totals — one DB row per emote with at least one `UsageStat`
 * under the requested set (spec #200, section 7). Always a real database row: `emoteId` is never
 * empty here, and every count is a real sum, never withheld — the `null` payload only exists one
 * step further up, in {@link EmoteUsageTotal} after {@link mergeSetView} has unioned this with the
 * set's live 7TV membership.
 */
export interface EmoteUsageTotalDto {
  emoteId: string;
  emoteName: string;
  sevenTvEmoteId: string;
  imageUrl: string;
  totalUseCount: number;

  /**
   * ISO date (`yyyy-MM-dd`) of the emote's last use ever — deliberately not bounded by the selected
   * range, so switching to "7 days" does not report the whole set as never used. `null` = never
   * used since tracking began.
   */
  lastUsedDate: string | null;

  /** Uses over the equally long window immediately before the selected range. */
  previousWindowUseCount: number;

  /** ISO timestamp of when the emote entered the 7TV set. `null` = unknown, never "new". */
  firstSeenAt: string | null;

  /**
   * Whether this row is archived (gone from 7TV). Always `false` for the active set, which never
   * returns archived rows; only a non-active set can carry `true` (spec E23) — the row still has
   * counts, but the 7TV emote is no longer live anywhere.
   */
  isArchived: boolean;

  /**
   * Emote set ids under which a *different* `sevenTvEmoteId` of the same channel bearing the same
   * `emoteName` carries at least one `UsageStat` row (spec E24). Ascending, empty in the ordinary
   * case; the requested set itself is never among them.
   */
  nameTwinEmoteSetIds: string[];
}

/**
 * One row of the set-ansicht (view), as {@link mergeSetView} builds it out of a DB-side
 * {@link EmoteUsageTotalDto} list and the set's live 7TV membership (spec #200, section 7.1).
 *
 * In the **active** set's view every row is `'live'` with `slotCount: 1` and both `emoteId`/
 * `totalUseCount` are never `null` — the active set's `/totals` only ever returns rows for emotes
 * that are still live. Both go nullable only in a **non-active** view, where a live 7TV member can
 * have no counted row at all (Klasse 3: `emoteId: null, totalUseCount: null`, still `'live'`).
 */
export interface EmoteUsageTotal {
  /** `null` for a live 7TV member with no counted row under this set (Klasse 3). */
  emoteId: string | null;
  /** Alias from the live 7TV list when the row has one, `emoteName` of the DB row otherwise. */
  emoteName: string;
  sevenTvEmoteId: string;
  imageUrl: string;
  /** `null` ≠ `0`: no `UsageStat` under this set at all (E17), never "unused". */
  totalUseCount: number | null;

  /**
   * ISO date (`yyyy-MM-dd`) of the emote's last use ever — deliberately not bounded by the selected
   * range, so switching to "7 days" does not report the whole set as never used. `null` = never
   * used since tracking began, or no row under this set at all.
   */
  lastUsedDate: string | null;

  /** Uses over the equally long window immediately before the selected range. `null` per above. */
  previousWindowUseCount: number | null;

  /** ISO timestamp of when the emote entered the 7TV set. `null` = unknown, never "new". */
  firstSeenAt: string | null;

  /**
   * `'left'` = a counted row that is no longer in the set's live membership (E23) — in the active
   * set's view this is always `'live'`, because that view never fetches a live list at all (E16).
   */
  membership: 'live' | 'left';

  /** `1`, or `2` for a #74 duplicate cell (two live members sharing one `sevenTvEmoteId`, E20). */
  slotCount: number;

  /** Every alias of the live entry, in encounter order; `[emoteName]` when there is no live entry. */
  aliases: string[];

  /** See {@link EmoteUsageTotalDto.nameTwinEmoteSetIds}; `[]` for a live member with no row (E24). */
  nameTwinEmoteSetIds: string[];
}

/** One day with actual usage — days without usage are absent, not zero (see EmoteUsageSeries). */
export interface EmoteDailyUsage {
  /** ISO date, `yyyy-MM-dd`. */
  date: string;
  useCount: number;
}

/** GET /api/channels/{c}/usage-stats/daily — one emote's series for the drilldown (A5). */
export interface EmoteUsageSeries {
  emoteId: string;
  emoteName: string;
  /** The requested range, both inclusive, echoed back. */
  from: string;
  to: string;
  totalUseCount: number;
  /** First/last use ever, deliberately not bounded by the range. `null` = never used. */
  firstUsedDate: string | null;
  lastUsedDate: string | null;
  /** Sparse: only days with usage, ascending — the client zero-fills for rendering. */
  days: EmoteDailyUsage[];
  /**
   * ISO dates (`yyyy-MM-dd`) within [from, to] on which the channel was live, ascending (A10).
   * Coverage data only exists since the worker's live poll shipped — an absent day in an older
   * range means "unknown", not "offline", so the chart marks live days and states nothing else.
   */
  liveDays: string[];
}

/** One emote inside {@link ChannelUsageSeries}: `[dayOffset, useCount]` pairs, ascending. */
export interface EmoteSeriesEntry {
  emoteId: string;
  /** Offsets count days from the response's `from`; only days with usage are present. */
  days: [number, number][];
}

/**
 * GET /api/channels/{c}/usage-stats/series — every unarchived emote's days in one response, so the
 * atlas can draw a curve for whichever emote the pointer is on without asking per emote.
 *
 * Offsets instead of ISO dates and omitted emotes instead of zero-filled ones: this response covers
 * a whole set where {@link EmoteUsageSeries} covers one emote, and nothing between the Api and the
 * browser compresses JSON. An emote missing from `emotes` had no usage in the range.
 */
export interface ChannelUsageSeries {
  /** The requested range, both inclusive, echoed back. */
  from: string;
  to: string;
  /** Day offsets from `from` with live coverage — same "absent means unknown" caveat as above. */
  liveDays: number[];
  emotes: EmoteSeriesEntry[];
}
