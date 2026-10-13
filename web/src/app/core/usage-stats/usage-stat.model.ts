/**
 * GET /api/channels/{c}/usage-stats/totals — one DB row per set entry (slot) or, in emote mode, per
 * emote with at least one `UsageStat` under the requested set (spec #200, section 7; emote-slots
 * spec 5.1). Always a real database row: `emoteId` is never empty here, and every count is a real
 * sum, never withheld — the `null` payload only exists one step further up, in
 * {@link EmoteUsageTotal} after {@link mergeSetView} has unioned this with the set's live 7TV
 * membership.
 *
 * **Two modes.** A slot-mode row (`slotId` set) shows exactly one slot: its name, its add date and
 * its own counts. An emote-mode row (`slotId: null`) is the row as it was before slots: the emote's
 * name and first-seen date, and the sum over every slot of the emote under the set. With the slot
 * reconcile flag off every row is in emote mode, which is how the page tells the two states apart
 * without knowing the flag (plan 377, F2). In one response a 7TV id has either exactly one
 * emote-mode row or only slot-mode rows.
 *
 * The slot fields are typed as the wire sends them; {@link mergeSetView} still reads an absent one
 * (an older Api answering mid-deploy) as its off value.
 */
export interface EmoteUsageTotalDto {
  emoteId: string;
  /** The slot this row shows, or `null` in emote mode (see above). */
  slotId: string | null;
  /** The slot's name in slot mode, the emote's name in emote mode. */
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

  /**
   * ISO timestamp of when the entry entered the 7TV set — the slot's add date in slot mode, the
   * emote's first-seen date in emote mode. `null` = unknown, never "new".
   */
  firstSeenAt: string | null;

  /**
   * Whether `firstSeenAt` is 7TV's authoritative date for the slot rather than a provisional one.
   * Always `false` in emote mode.
   */
  firstSeenAtFromRest: boolean;

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

  /**
   * The names of the other rows of the same emote in this response, add date ascending (unknown
   * last), then name, without this row's own name. Always empty in emote mode.
   */
  siblingNames: string[];

  /**
   * `true` only on a slot row with an authoritative date when another own slot of the same emote
   * carries the same authoritative date — computed by the Api over the emote's whole slot list, so a
   * twin without rows under the viewed set still counts (plan 377, F6). Such a date does not identify
   * one slot, so the set view must not bind a renamed live entry by it. Always `false` in emote mode
   * and on provisional or undated rows.
   */
  firstSeenAtShared: boolean;
}

/**
 * One row of the set-ansicht (view) — one card — as {@link mergeSetView} builds it out of a DB-side
 * {@link EmoteUsageTotalDto} list and the set's live 7TV membership (spec #200, section 7.1;
 * emote-slots spec 9.1 as amended by plan 377).
 *
 * In the **active** set's view every row is `'live'` with `slotCount: 1` and both `emoteId`/
 * `totalUseCount` are never `null` — the active set's `/totals` only ever returns rows for emotes
 * that are still live. Both go nullable only in a **non-active** view, where a live 7TV member can
 * have no counted row at all (Klasse 3: `emoteId: null, totalUseCount: null`, still `'live'`).
 *
 * Invariant: `siblingNames` non-empty implies `slotCount === 1`, and `slotCount > 1` implies empty
 * `siblingNames` — a card either sums several set entries (emote mode) or stands next to its
 * siblings as cards of their own (slot mode), never both.
 */
export interface EmoteUsageTotal {
  /** `null` for a live 7TV member with no counted row under this set (Klasse 3). */
  emoteId: string | null;
  /** The slot this card shows; `null` for an emote-mode row and for Klasse 3. */
  slotId: string | null;
  /**
   * The page-wide key of this card (plan 377, 2.3): the `slotId` of a slot-mode row; the
   * `sevenTvEmoteId` of an emote-mode row and of a grouped Klasse-3 row (unchanged from before
   * slots); `live:<id>#<addedAt>` for a Klasse-3 row per entry, `live:<id>#<addedAt>#<name>` when
   * another entry of the id shares that instant, `live:<id>##<name>` when 7TV gave no date.
   */
  slotKey: string;
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

  /** ISO timestamp of when the entry entered the 7TV set. `null` = unknown, never "new". */
  firstSeenAt: string | null;

  /** See {@link EmoteUsageTotalDto.firstSeenAtFromRest}; `false` for Klasse 3. */
  firstSeenAtFromRest: boolean;

  /**
   * The add date of the one live entry a per-entry Klasse-3 card stands for, exactly as 7TV sent it
   * (the same string its `live:` key carries) — what a slot delete binds and reports such a card
   * by (plan 378, R1 a). `firstSeenAt` stays `null` there: the date is 7TV's, not a counted one.
   * Absent on every other card, and when 7TV named no date.
   */
  liveAddedAt?: string;

  /**
   * `'left'` = a counted row that is no longer in the set's live membership (E23) — in the active
   * set's view this is always `'live'`, because that view never fetches a live list at all (E16).
   */
  membership: 'live' | 'left';

  /**
   * How many set entries this one card sums up: the live entries of its id for an emote-mode row in
   * a non-active view (a #74 duplicate cell, E20) or a grouped Klasse-3 row, `1` otherwise. Marked
   * `×n` on the card (plan 377, F1).
   */
  slotCount: number;

  /**
   * The names of exactly the entries this card sums up, in live order; `[emoteName]` when it stands
   * for one entry or has no live entry.
   */
  aliases: string[];

  /**
   * The emote's other names in the set that stand as cards of their own — slot-mode rows and
   * per-entry Klasse-3 rows only, `[]` otherwise. Active view: from the DTO; non-active view: from
   * the other live entries of the id, add date ascending (unknown last), then name.
   */
  siblingNames: string[];

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
  /** The slot the series shows when asked for by slot; `null` on the emote path (sum of slots). */
  slotId: string | null;
}

/** One emote inside {@link ChannelUsageSeries}: `[dayOffset, useCount]` pairs, ascending. */
export interface EmoteSeriesEntry {
  /**
   * The key the page matches an entry to its grid row by (spec #200, 6.5 step 1, 7.2) — a set view's
   * row may have no `Emote.Id` at all, but it always has this.
   */
  sevenTvEmoteId: string;
  /**
   * The row's `Emote.Id` Guid — transitional only: `/series` still sends it next to
   * `sevenTvEmoteId` (additive, 6.5 step 1) so an older open tab keeps reading what it knows, but no
   * frontend reader keys on it any more. Drops with follow-up issue 5 (spec section 21), behind the
   * same gate as the legacy `sync-deleted` body form (E3).
   */
  emoteId: string;
  /**
   * The slot whose days these are, or `null` in emote mode (each day sums the emote's slots) — the
   * join key to {@link EmoteUsageTotal.slotId}; an emote-mode entry joins by `sevenTvEmoteId`
   * (plan 377, F4).
   */
  slotId: string | null;
  /** The row's name, the same as {@link EmoteUsageTotalDto.emoteName}. */
  name: string;
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
  /**
   * First and last day (ISO `yyyy-MM-dd`) in the requested range with a count under the viewed set,
   * archived emotes included; `null` when the range holds no count. Absent from an older Api
   * response taken mid-deploy, which reads as `null`.
   */
  firstCountedDay: string | null;
  lastCountedDay: string | null;
  /**
   * Earliest day in the range with a count under ANY set of the channel; `null` when no set has a
   * count. Only meaningful next to `firstCountedDay`: an earlier value than it means another set
   * counted first, which is what tells a set change from a quiet start of the range. Absent from
   * an older Api response taken mid-deploy, which reads as `null`.
   */
  channelFirstCountedDay: string | null;
  emotes: EmoteSeriesEntry[];
}

/**
 * The "every emote set" scope of a daily series (`setScope=all`): a null-session has no set of its
 * own, and the usage on its rows is what the emote got across all of them. A named object rather
 * than a string, because every string is a valid set id — `null` stays "the channel's active set".
 */
export const ALL_EMOTE_SETS = { allSets: true } as const;
export type AllEmoteSets = typeof ALL_EMOTE_SETS;

/** Which sets a daily series reads: one set (its id), the channel's active set (`null`), or all. */
export type DailySeriesSetScope = string | null | AllEmoteSets;
