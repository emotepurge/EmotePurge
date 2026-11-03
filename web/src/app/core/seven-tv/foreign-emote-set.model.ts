import type { SevenTvSetReadState } from './seven-tv-set-entries';

/**
 * One emote in a foreign channel's active 7Tv set — the row shape the T3 selection grid
 * (`shared/seven-tv/foreign-emote-grid.ts`) and the picker dialog operate on.
 *
 * `sevenTvEmoteId` is the 7TV ObjectID, not our internal `Emote.id` Guid (Regel 8) — it is the only
 * identity this row has, since a foreign channel has no `EmoteUsageTotal` row to key off. This is
 * why the grid needs its own `ListSelection<ForeignEmoteRow>` keyed on this field rather than the
 * `emoteId`-keyed one `usage-stats-page.ts` uses (see the spec's Falle F4).
 *
 * `topAllTime`/`trending` are 7TV-*network-wide* scores, never a channel-local popularity — there is
 * no such thing for a channel this account has no role in, `EmoteSetEmote` carries no usage data.
 * Both are `null` when 7TV reports no score for that emote. Keep the "7TV global" framing wherever
 * these are surfaced; never label them (or a sort by them) as plain "Beliebtheit" (P5').
 */
export interface ForeignEmoteRow {
  sevenTvEmoteId: string;
  /** Alias in the source set — what the channel actually calls it. */
  name: string;
  /** Global base name, which can differ from `name` when the channel aliased the emote. */
  defaultName: string;
  imageUrl: string;
  topAllTime: number | null;
  trending: number | null;
  /**
   * When the entry entered the set, as 7TV reports it (ISO UTC with milliseconds) — the identity a
   * set view joins a dated slot row by (plan 377, 2.4). Optional because the same shape also comes
   * from the leaderboard and the foreign-channel preview without a date; absent or `null` = 7TV
   * named none.
   */
  addedAt?: string | null;
}

/**
 * Response shape of `GET /api/seventv/channels/{channelName}/emotes` (spec §4). Built by T1's
 * backend endpoint; this frontend slice is written against the contract, not the implementation —
 * T1 lands independently in the same worktree.
 */
export interface ForeignEmoteSetResponse {
  /** Normalized (Regel 9). */
  channelName: string;
  /**
   * `null` in the set-ID read mode (spec 2026-09-20, E8) — that path never resolves an identity at
   * all, so there is nothing to report here. Never `null` for a response this login-based load
   * produced.
   */
  sevenTvUserId: string | null;
  emoteSetId: string;
  /**
   * `null` when 7TV reports no name for the set (spec 6.4). Added 2026-09-20 alongside `capacity` —
   * T2.2 shipped both fields on the wire without a frontend reader yet; T2.5b's loader is the first
   * consumer (target-set capacity/name in the picker's confirm dialog).
   */
  emoteSetName: string | null;
  /** 7TV's own figure, `0` normalised to `null` (spec 6.4, F6). */
  capacity: number | null;
  /** What 7TV reports as the set's total entry count. */
  totalCount: number;
  /**
   * `true` whenever the list is not a complete, verified read of the set — since #457 exactly
   * `readState !== 'complete'`: the page cap was hit while `totalCount` promised more (spec F3),
   * 7TV listed fewer or more entries than it counts, or the pages did not add up to one consistent
   * snapshot. Never silently truncate: a caller that has this `true` must say so, not just render
   * however many `emotes` came back. Every reader that only needs "complete or not" keeps reading
   * this; a reader that may act on a stable gap reads `previewReadState` (`preview-read-state.ts`) instead.
   */
  truncated: boolean;
  /**
   * The three-way verdict on the read (#457, the backend counterpart of #454's
   * `SevenTvSetEntries.readState`): `'gap'` is a self-consistent read in which 7TV lists fewer
   * entries than it counts — every listed entry is present, nothing can be concluded about the rest.
   * Optional because an Api from before #457 does not send it; read it through
   * `previewReadState`, never directly, so such a response fails closed.
   */
  readState?: SevenTvSetReadState;
  /** `totalCount` minus the entries listed — greater than zero only for `readState: 'gap'` (#457).
   *  Read it through `previewOmittedCount`. */
  omittedCount?: number;
  emotes: ForeignEmoteRow[];
}
