/**
 * One entry of a channel's current 7TV set, reduced to what the import dialog needs to answer
 * "already in the target set?" and "name collision?" — no usage numbers, no time range, no
 * internal `Emote.Id` (channel-scoped, meaningless across channels). One per entry (slot), not per
 * emote (#444): an emote under two aliases is two items with the same `sevenTvEmoteId`, and every
 * consumer groups by id itself. Served by `GET /api/channels/{channel}/emotes` and built from the
 * live set read for a non-active or untracked target (`import-target-loader.ts`).
 */
export interface EmoteListItem {
  sevenTvEmoteId: string;
  name: string;
  /** Straight copy of the tracked emote's own image URL — never derived from `sevenTvEmoteId`
   *  (static vs. animated emotes use different 7TV URL shapes). */
  imageUrl: string;
}
