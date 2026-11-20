/**
 * Client-side mirror of the wish contracts in `EmotePurge.Core/Services/IWishService.cs` (#481, K3).
 * Statuses, reasons, sources and link origins are server strings and the contract; each is typed as the
 * known literals plus `string`, so an unknown value from a newer server still type-checks and is handled
 * (not dropped) by the consumers.
 */

export type WishStatus =
  | 'pending'
  | 'approved'
  | 'unconfirmed'
  | 'failed'
  | 'active'
  | 'presented'
  | 'rejected'
  | 'expired'
  | 'removed'
  | 'removed_externally'
  | 'kept'
  | 'closed'
  | 'merged'
  | (string & {});

export type WishStatusReason =
  | 'expired'
  | 'protected'
  | 'coverage_gap'
  | 'not_bot_owned'
  | 'ownership_lost'
  | 'unconfirmed'
  | 'bot_unavailable'
  | 'unconfirmed_start'
  | 'already_in_set'
  | 'repair_review'
  | 'alias_conflict'
  | 'set_full'
  | 'emote_gone'
  | 'not_editor'
  | 'unknown'
  | 'ambiguous_match'
  | 'already_running'
  | 'unparsed'
  | (string & {});

export type WishSource = 'irc' | 'manual' | 'adopted' | (string & {});

export type WishLinkOrigin = 'auto' | 'adopted' | 'bot' | (string & {});

export type WishMode = 'manual' | 'bot' | (string & {});

/** How the redeemed text was read (K6): a 7TV link, a bare name, or nothing usable. */
export type WishMatchKind = 'link' | 'name' | 'none' | (string & {});

/** The three lists of the channel page; `open` is the server default. */
export type WishListView = 'open' | 'running' | 'ended';

export interface WishUsageDay {
  /** UTC calendar day, `YYYY-MM-DD`. */
  date: string;
  useCount: number;
}

export interface WishUsage {
  windowFromUtc: string;
  windowToUtc: string;
  total: number;
  perDayMean: number;
  /** `null` together with `rankOf` while the emote has no active slot to rank. */
  rank: number | null;
  rankOf: number | null;
  coverageGap: boolean;
  /**
   * One entry per UTC day of the window, in order, summing to `total`. Optional: an older server omits
   * it, and an empty list means the same, "no series".
   */
  daily?: WishUsageDay[];
}

export interface Wish {
  id: number;
  sevenTvEmoteId: string;
  emoteName: string;
  alias: string;
  source: WishSource;
  status: WishStatus;
  statusReason: WishStatusReason | null;
  requesterLogin: string | null;
  durationDays: number;
  createdAtUtc: string;
  approvedAtUtc: string | null;
  startsAtUtc: string | null;
  startsAtAuthoritative: boolean;
  probationStartsAtUtc: string | null;
  expiresAtUtc: string | null;
  endedAtUtc: string | null;
  slotId: string | null;
  /** The slot generation the wish is bound to, verbatim for `completeRemoval`; `null` = unknown. */
  slotAddedAtUtc: string | null;
  imageUrl: string | null;
  linkOrigin: WishLinkOrigin | null;
  extensionCount: number;
  permanent: boolean;
  botOwned: boolean;
  botWritePending: boolean;
  botNextAttemptAtUtc: string | null;
  removalQueuedAtUtc: string | null;
  mode: WishMode;
  /**
   * K6 redeemer data, optional because an older server omits it. `requestText`, `requestedName` and
   * `rewardLabel` (and `requesterLogin` of an `irc` wish) arrive only for a caller confirmed on the
   * channel's Twitch id, otherwise `null`; `rewardLabel` is `null` as well once the reward was forgotten.
   */
  requestText?: string | null;
  requestedName?: string | null;
  /** `null` for a wish not captured in chat. */
  matchKind?: WishMatchKind | null;
  rewardLabel?: string | null;
  /** The running wish a `merged` wish went into. */
  mergedIntoWishId?: number | null;
}

/**
 * Answer of `POST …/approve`: the wish plus the new expiry of the running wish it was merged into
 * (`mergedIntoWishId` is then set), `null` when it was approved on its own. Always present on this route.
 */
export interface ApprovedWish extends Wish {
  extendedExpiresAtUtc: string | null;
}

/** `GET …/wishes/{id}`: the wish plus the usage of its window (`null` without a start). */
export interface WishDetail extends Wish {
  usage: WishUsage | null;
}

/** Answer of the manual entry: the wish, plus the hint that the set was at capacity (never a refusal). */
export interface CreatedWish extends Wish {
  setFull?: boolean;
}

/** One entry of `GET …/wishes/slots`, with the conflict `adopt` would decide. */
export interface WishAdoptableSlot {
  slotId: string;
  sevenTvEmoteId: string;
  alias: string;
  /** `null` = undated. Return it verbatim as `expectedAddedAtUtc` when adopting. */
  addedAtUtc: string | null;
  addedAtAuthoritative: boolean;
  imageUrl: string | null;
  heldByWishId: number | null;
  runningWishId: number | null;
  runningWishAdoptable: boolean;
}

export interface WishSettings {
  defaultDurationDays: number;
  probationLeadDays: number;
  protectionThresholdPerDay: number | null;
  botModeEnabled: boolean;
  botSuspendedReason: string | null;
  /** K6: how many days a captured wish may stay `pending` (1 to 90). */
  pendingExpiryDays: number;
}

/** `GET …/wishes/settings`: the settings plus whether this caller may write them. */
export interface WishSettingsRead extends WishSettings {
  canEdit: boolean;
}

/**
 * Body of `PUT …/wishes/settings`. `protectionThresholdPerDay` absent keeps the stored value, `null`
 * switches protection off, a number sets it; the three are different on the wire, so the key is optional.
 */
export interface WishSettingsUpdate {
  defaultDurationDays: number;
  probationLeadDays: number;
  protectionThresholdPerDay?: number | null;
  /** Absent keeps the stored value; there is no "off" for it. */
  pendingExpiryDays?: number;
}

export interface CreateWishBody {
  sevenTvEmoteId: string;
  emoteName?: string;
  alias?: string;
  durationDays?: number;
  requesterLogin?: string;
}

export interface AdoptWishBody {
  slotId: string;
  /** Alias of the entry the manager picked from `listSlots`. */
  expectedAlias: string;
  /** `addedAtUtc` of that entry, verbatim; `null` = an undated entry was picked. */
  expectedAddedAtUtc: string | null;
  wishId?: number;
  durationDays?: number;
  startsAtUtc?: string;
}

/**
 * Body of `POST …/approve`. Every field is optional: the emote id falls back to the wish's own (a wish
 * without one needs it here, else 400 `wish_emote_required`), duration to the wish's.
 */
export interface ApproveWishBody {
  sevenTvEmoteId?: string;
  emoteName?: string;
  alias?: string;
  durationDays?: number;
}

export type WishRemovalEvidence = 'acknowledged' | 'absent';

/** What the client's removal run did: slot, generation and set are all required. */
export interface CompleteWishRemovalBody {
  slotId: string;
  /** `slotAddedAtUtc` of the wish, verbatim from the DTO. */
  slotAddedAtUtc: string;
  /** The 7TV set the run wrote against. */
  emoteSetId: string;
  evidence?: WishRemovalEvidence;
}

export interface UpdateWishBody {
  alias?: string;
  durationDays?: number;
}

export type WishReAddMode = 'timed' | 'permanent';

export interface ReAddWishBody {
  mode: WishReAddMode;
  durationDays?: number;
}

/** One row of `GET /api/wishes/mine`. */
export interface WishOverviewItem {
  channelName: string;
  wish: Wish;
}

export interface WishSummary {
  actionCount: number;
}

/**
 * The ids a 409 names (`slotIds` on `wish_emote_already_in_set`, `wishId` on it, on `wish_already_running`
 * and on `wish_slot_already_linked`). Absent fields stay `undefined`; `wishId` may be `null`.
 */
export interface WishConflict {
  slotIds?: string[];
  wishId?: number | null;
}

/** A learned reward of the channel; `id` is the row id the routes take, Twitch's tag never reaches the client. */
export interface WishReward {
  id: number;
  label: string;
  defaultDurationDays: number;
  isActive: boolean;
  createdAtUtc: string;
  /** `null` until a redemption of it was seen. */
  lastRedemptionSeenAtUtc: string | null;
}

/** The redemption a running learning session saw: `candidateId` is the opaque id it is confirmed by. */
export interface WishRewardCandidate {
  candidateId: string;
  text: string;
  login: string;
  seenAtUtc: string;
}

/** A running learning session; `candidate` is `null` until the first redemption of an unknown reward. */
export interface WishRewardLearning {
  sessionId: string;
  startedAtUtc: string;
  expiresAtUtc: string;
  candidate: WishRewardCandidate | null;
}

export type WishCaptureGapReason = 'disconnect' | 'worker_restart' | 'not_joined' | (string & {});

/** A stretch in which the capture may have missed redemptions; the bounds are approximate. */
export interface WishCaptureGap {
  fromUtc: string;
  toUtc: string;
  reason: WishCaptureGapReason;
}

/**
 * `GET …/wishes/rewards`. An unbound channel row (`channelBound` false) has no rewards, no gaps and no
 * learning whatever the tables hold; `canEdit`, `pendingExpiryDays` and `captureAliveAtUtc` stay set.
 */
export interface WishRewardsRead {
  channelBound: boolean;
  rewards: WishReward[];
  /** The most rewards a channel may have. */
  limit: number;
  learning: WishRewardLearning | null;
  /** Gaps ending within the last 14 days, newest first. */
  gaps: WishCaptureGap[];
  canEdit: boolean;
  pendingExpiryDays: number;
  /** The capture's last life stamp; `null` while it never wrote one. */
  captureAliveAtUtc: string | null;
}

/** Body of `POST …/rewards/learning/confirm`: the ids exactly as the dialog showed them. */
export interface ConfirmWishRewardBody {
  sessionId: string;
  candidateId: string;
  label: string;
  /** Absent takes the channel's default duration. */
  defaultDurationDays?: number;
}

/** Body of `PATCH …/rewards/{id}`: an absent field stays as it is. */
export interface UpdateWishRewardBody {
  label?: string;
  defaultDurationDays?: number;
  isActive?: boolean;
}
