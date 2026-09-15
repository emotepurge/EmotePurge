import { DuplicateEmoteName } from './duplicate-emote-name.model';
import { SevenTvSyncFailureReason } from './seven-tv-sync-failure';

/**
 * The channel's 7TV set as a slot budget, plus since when its usage data can be trusted.
 * Served by `GET /api/channels/{channel}/emotes/active-set`.
 */
export interface EmoteSetStatus {
  /** Empty while the first 7TV sync is still pending — the page polls on that. */
  activeEmoteSetId: string;

  /**
   * The set's slot limit as 7TV reports it, `null` when it reports none. Render no budget at all in
   * that case: 7TV subscribers get sets larger than 1000, so a hard-coded denominator would be
   * wrong in exactly the cases where the number matters most.
   */
  capacity: number | null;

  /** Counted from our own active (non-archived) emote rows — the same rows the grid renders. */
  occupiedSlots: number;

  /** ISO timestamp of the last join that (re)activated the channel, else its creation. */
  trackedSince: string;

  /**
   * Why the last 7TV sync attempt produced nothing, `null` when it succeeded — or when none has
   * been made yet. Read together with `activeEmoteSetId`: an empty id and a `null` reason is the
   * only combination that genuinely means "the first sync is still running", and it is the only one
   * worth polling on.
   */
  syncFailureReason: SevenTvSyncFailureReason | null;

  /** ISO timestamp of the last attempt, successful or not; `null` when none has been made. */
  lastSyncAttemptAtUtc: string | null;

  /**
   * `yyyy-MM-dd` of the earliest day this channel has a UsageStat row with bot usage, or `null` when
   * no bot has ever been seen here. This is the day a bot was first *seen*, not the day bot usage
   * started being counted apart for this channel — see {@link botsExcludedCaptionKey} for why no
   * consumer may read anything sharper into this than "show the sentence, or don't".
   */
  botsExcludedSince: string | null;

  /**
   * `yyyy-MM-dd` of the earliest day this channel has a UsageStat row with shared-chat usage, or
   * `null` when no mirrored message has ever been seen here. Same "first *seen*, not the day the
   * separation started" caveat as {@link botsExcludedSince} — see
   * {@link sharedChatSeparatedCaptionKey} for why no consumer may read anything sharper into this
   * than "show the sentence, or don't". In particular `null` does not mean "shared chat is still
   * counted here": the separation applies everywhere, this channel just never saw any.
   */
  sharedChatSeparatedSince: string | null;

  /**
   * Active emote names carried by more than one emote in the channel's current 7TV set — the same
   * list `EmoteSetStatusDto.DuplicateNames` carries server-side (issue #45). Always an array, never
   * `undefined`: "no collisions" is an empty one, and there is no third state to report. Drives the
   * usage-stats page's collision banner, guarded there against a stale response from a channel the
   * user has since navigated away from — see that page's `duplicateNames` computed.
   */
  duplicateNames: DuplicateEmoteName[];
}
