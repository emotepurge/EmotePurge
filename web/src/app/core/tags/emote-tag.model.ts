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
  /** The tag's valid placements in the shown set (read-time rule); 0 without a set. */
  placedCount: number;
  /** Whether the tag counts as played in to the shown set; `false` without a set. */
  active: boolean;
  /** When the activation was last set; `null` when `active` is `false`. */
  activatedAtUtc: string | null;
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
  /** Whether this tag holds a valid placement of the emote in the shown set. */
  placedByThisTag: boolean;
  /** That placement's `PlacedAtUtc`; `null` without a valid placement. */
  placedAtUtc: string | null;
  /** That placement's revision; `null` without a valid placement. Echoed back in a removal snapshot. */
  placementOperationId: string | null;
  /** The channel's other tags that are active in the shown set and have an entry for the emote. */
  heldByActiveTags: EmoteTagRef[];
  /** The channel's other tags with a valid placement of the emote in the shown set. */
  placedByOtherTags: EmoteTagRef[];
}

/** Another tag of the same channel, as an entry read names it. */
export interface EmoteTagRef {
  id: number;
  name: string;
}

export interface EmoteTagEntries {
  emoteSetId: string | null;
  isActiveSet: boolean;
  entries: EmoteTagEntry[];
  /** The tag's current activation operation in the shown set; `null` when it is not active there. */
  activationOperationId: string | null;
}

export interface AddTagEntriesResult {
  addedCount: number;
  alreadyTaggedCount: number;
  skippedNotInSetIds: string[];
}

export interface RemoveTagEntriesResult {
  removedCount: number;
}

/** One placement a removal run read in its preview: the emote and the revision it was read at. */
export interface TagPlacementSnapshotEntry {
  sevenTvEmoteId: string;
  placementOperationId: string;
}

export type TagOperationKind = 'playIn' | 'removal';

/** `POST .../tags/{tagId}/operations` — `emoteSetId` goes in the body, never as a query parameter. */
export interface RegisterTagOperationBody {
  /** Client-generated UUID; the same id on a retry makes the registration idempotent. */
  operationId: string;
  kind: TagOperationKind;
  emoteSetId: string;
  targetOwnerTwitchId: string | null;
}

export interface TagOperationRegistration {
  registeredAtUtc: string;
}

/** `POST .../tags/{tagId}/placements` — the play-in report. */
export interface TagPlacementsBody {
  operationId: string;
  emoteSetId: string;
  targetOwnerTwitchId: string | null;
  sevenTvEmoteIds: readonly string[];
}

/**
 * The play-in report's answer. When `replayed` is `true` the operation was already applied and the
 * response carries no outcome in any other field (they are zero/empty): never derive counts, a
 * "recorded"/"discarded" message or a follow-up decision from a replay — re-read the tag instead.
 */
export interface TagPlacementsResult {
  replayed: boolean;
  recordedCount: number;
  alreadyRecordedCount: number;
  notTaggedIds: string[];
  discardedStaleIds: string[];
}

/** `POST .../tags/{tagId}/placements/removed` — the removal report. Lists are never `null`; empty is legal. */
export interface TagRemovalBody {
  operationId: string;
  emoteSetId: string;
  targetOwnerTwitchId: string | null;
  activationOperationId: string | null;
  snapshot: readonly TagPlacementSnapshotEntry[];
  removedIds: readonly string[];
  keptIds: readonly string[];
}

/**
 * The removal report's answer. When `replayed` is `true` the operation was already applied and every
 * counter is `0` and `deactivated` is `false` — those values are not an outcome. Never show counts
 * from a replay; re-read the tag instead.
 */
export interface TagRemovalResult {
  replayed: boolean;
  deletedCount: number;
  transferredCount: number;
  droppedCount: number;
  sweptCount: number;
  deactivated: boolean;
}
