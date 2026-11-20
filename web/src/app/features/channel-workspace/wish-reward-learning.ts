import type {
  ConfirmWishRewardBody,
  UpdateWishRewardBody,
  WishReward,
  WishRewardCandidate,
  WishRewardLearning,
  WishRewardsRead,
} from '../../core/wishes/wish.model';

/** A learning session that has seen a redemption: the candidate is what the manager confirms. */
export type WishRewardLearningWithCandidate = WishRewardLearning & {
  candidate: WishRewardCandidate;
};

/**
 * Where "Belohnung anlernen" stands (plan 2.10): `idle → waiting → candidate → confirmed`, or
 * `expired` / cancelled back to `idle`. Derived from the server's learning row and the section's
 * clock; the server stays the judge (a stale dialog gets 409), this only decides what is shown.
 *
 * - `idle`: no session.
 * - `waiting`: a session runs, no redemption seen yet; `remainingMs` until it ends.
 * - `candidate`: a session runs and holds the first redemption of an unknown reward.
 * - `expired`: the session the section showed ran out (it stays named until the next start, even
 *   once the server has dropped the row).
 */
export type LearningPhase =
  | { kind: 'idle' }
  | { kind: 'waiting'; learning: WishRewardLearning; remainingMs: number | null }
  | {
      kind: 'candidate';
      learning: WishRewardLearningWithCandidate;
      remainingMs: number | null;
    }
  | { kind: 'expired' };

/**
 * The phase for the learning row the server last answered with, at `now`. `expiredSeen`: a session
 * the section showed has run out since the last start (the server deletes such a row on its next
 * read, so without the flag the section would fall silently back to `idle`). An unreadable end time
 * never counts as run out; the countdown is then left out (`remainingMs` null).
 */
export function learningPhase(
  learning: WishRewardLearning | null,
  now: Date,
  expiredSeen: boolean,
): LearningPhase {
  if (learning === null) {
    return expiredSeen ? { kind: 'expired' } : { kind: 'idle' };
  }
  const expires = Date.parse(learning.expiresAtUtc);
  const remainingMs = Number.isNaN(expires) ? null : expires - now.getTime();
  if (remainingMs !== null && remainingMs <= 0) {
    return { kind: 'expired' };
  }
  return learning.candidate === null
    ? { kind: 'waiting', learning, remainingMs }
    : { kind: 'candidate', learning: learning as WishRewardLearningWithCandidate, remainingMs };
}

/**
 * Why "Belohnung anlernen" cannot be started, as the reason the section names; `null` when it can.
 * `unbound` and `readOnly` replace the button with a sentence, `limit` keeps it disabled with the
 * reason beside it (the server would answer 409 `wish_reward_limit` already at the start, plan 2.4).
 */
export type LearningStartBlock = 'unbound' | 'readOnly' | 'limit';

export function learningStartBlock(
  read: Pick<WishRewardsRead, 'channelBound' | 'canEdit' | 'rewards' | 'limit'>,
  restricted: boolean,
): LearningStartBlock | null {
  if (!read.channelBound) {
    return 'unbound';
  }
  if (!read.canEdit || restricted) {
    return 'readOnly';
  }
  return read.rewards.length >= read.limit ? 'limit' : null;
}

/** "m:ss" of a countdown, rounded up to the next whole second (never shows 0:00 while running). */
export function formatRemaining(remainingMs: number): string {
  const seconds = Math.max(0, Math.ceil(remainingMs / 1000));
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, '0')}`;
}

/**
 * The confirm body: exactly the ids of the session and candidate the dialog showed (Sol-P2-4), so a
 * session another manager replaced meanwhile is refused instead of confirmed unseen.
 */
export function confirmRewardBody(
  learning: WishRewardLearningWithCandidate,
  label: string,
  defaultDurationDays: number,
): ConfirmWishRewardBody {
  return {
    sessionId: learning.sessionId,
    candidateId: learning.candidate.candidateId,
    label,
    defaultDurationDays,
  };
}

/** The PATCH body for the fields that differ from the reward; `null` when nothing does. */
export function rewardEditBody(
  reward: Pick<WishReward, 'label' | 'defaultDurationDays'>,
  label: string,
  defaultDurationDays: number,
): UpdateWishRewardBody | null {
  const body: UpdateWishRewardBody = {
    ...(label !== reward.label ? { label } : {}),
    ...(defaultDurationDays !== reward.defaultDurationDays ? { defaultDurationDays } : {}),
  };
  return Object.keys(body).length === 0 ? null : body;
}

/** The translation key of a gap's cause; a reason this client does not know reads as "unknown". */
export function captureGapReasonKey(reason: string): string {
  switch (reason) {
    case 'disconnect':
    case 'worker_restart':
    case 'not_joined':
      return `wishes.settings.rewards.gaps.reason.${reason}`;
    default:
      return 'wishes.settings.rewards.gaps.reason.unknown';
  }
}

/** The capture last reported itself longer ago than this: the section warns (plan 2.7). */
export const CAPTURE_ALIVE_WARN_MS = 3 * 60 * 1000;

/**
 * Whether the capture's life stamp is too old to trust: older than three minutes, or never written.
 * A stamp from the future (clock skew) counts as fresh.
 */
export function captureLooksStopped(captureAliveAtUtc: string | null, now: Date): boolean {
  if (captureAliveAtUtc === null) {
    return true;
  }
  const alive = Date.parse(captureAliveAtUtc);
  return Number.isNaN(alive) ? true : now.getTime() - alive > CAPTURE_ALIVE_WARN_MS;
}

/**
 * How long before the warning the section reads the stamp again, and the least time between two such
 * reads (plan 2.7, Sol review). The worker stamps once a minute and its stamp writes send no
 * `wish.changed`, so without this an open section on a quiet channel would warn falsely, and would not
 * clear a warning once the worker is back.
 */
export const CAPTURE_STAMP_REFRESH_MS = 60 * 1000;

/**
 * Whether the section should read the rewards again for a fresher life stamp: the stamp is missing,
 * unreadable or within `CAPTURE_STAMP_REFRESH_MS` of the warning (or past it), and the last read
 * started at least `CAPTURE_STAMP_REFRESH_MS` ago. A stamp from the future counts as fresh.
 */
export function captureStampRefreshDue(
  captureAliveAtUtc: string | null,
  now: Date,
  lastReadStartedAt: Date,
): boolean {
  if (now.getTime() - lastReadStartedAt.getTime() < CAPTURE_STAMP_REFRESH_MS) {
    return false;
  }
  if (captureAliveAtUtc === null) {
    return true;
  }
  const alive = Date.parse(captureAliveAtUtc);
  return (
    Number.isNaN(alive) || now.getTime() - alive >= CAPTURE_ALIVE_WARN_MS - CAPTURE_STAMP_REFRESH_MS
  );
}
