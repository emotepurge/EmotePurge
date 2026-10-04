/** Wire shapes of `/api/channels/{channel}/tags` (`EmoteTagEndpoints.cs`). */

export interface EmoteTagSummary {
  id: number;
  name: string;
  entryCount: number;
  /**
   * Entries with an unarchived row in the shown set. `null` — not 0 — when the shown set is not the
   * channel's active set, or the channel has no active set: "no set to count in" is not "none in set".
   */
  inSetCount: number | null;
}

export interface EmoteTagList {
  /** `null` when none was asked for and the channel has no active set. */
  emoteSetId: string | null;
  isActiveSet: boolean;
  tags: EmoteTagSummary[];
}

export interface EmoteTag {
  id: number;
  name: string;
}

export interface EmoteTagEntry {
  sevenTvEmoteId: string;
  /** The emote's name when it was tagged (snapshot). */
  alias: string;
  /** The emote's image when it was tagged (snapshot). */
  imageUrl: string;
  /** `null` when the shown set is not the active one (or there is none) — read as "no set". */
  inSet: boolean | null;
  currentName: string | null;
}

export interface EmoteTagEntries {
  emoteSetId: string | null;
  isActiveSet: boolean;
  entries: EmoteTagEntry[];
}

export interface AddTagEntriesResult {
  addedCount: number;
  alreadyTaggedCount: number;
  skippedNotInSetIds: string[];
}

export interface RemoveTagEntriesResult {
  removedCount: number;
}
