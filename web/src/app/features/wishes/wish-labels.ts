import { pluralKey } from '../../core/i18n/plural';
import type { Wish } from '../../core/wishes/wish.model';
import { remainingTime } from '../../core/wishes/wish-view';
import type { StatusBadgeTone } from '../../shared/ui/status-badge';

/** The thirteen statuses of spec 5 that have a name; anything else reads as "unknown". */
const KNOWN_STATUSES: ReadonlySet<string> = new Set([
  'pending',
  'approved',
  'unconfirmed',
  'failed',
  'active',
  'presented',
  'rejected',
  'expired',
  'removed',
  'removed_externally',
  'kept',
  'closed',
  'merged',
]);

/** The reasons K3 explains in words; every other one renders the neutral line (plan 2.3, 2.10). */
const EXPLAINED_REASONS: ReadonlySet<string> = new Set(['expired', 'unconfirmed_start']);

/** A translation key with its parameters. */
export interface TextPart {
  key: string;
  params?: Record<string, string | number>;
}

/** Key of the status name; an unknown status reads `wishes.status.unknown`. */
export function wishStatusKey(wish: Pick<Wish, 'status'>): string {
  return KNOWN_STATUSES.has(wish.status) ? `wishes.status.${wish.status}` : 'wishes.status.unknown';
}

/** "Notable how often?" (§4.3): only the two states that ask for a decision get a colour. */
export function wishStatusTone(wish: Pick<Wish, 'status'>): StatusBadgeTone {
  switch (wish.status) {
    case 'presented':
      return 'warning';
    case 'failed':
      return 'danger';
    default:
      return 'neutral';
  }
}

/** The hints of an open wish (K6, plan 2.5) that the row says as a sentence; another hint says nothing. */
const PENDING_HINTS: ReadonlySet<string> = new Set([
  'unparsed',
  'ambiguous_match',
  'already_in_set',
  'already_running',
]);

/**
 * Key of the reason line: `presented` and `failed` with a reason, and an open wish with one of its
 * known hints (an unknown hint changes nothing, plan 2.10).
 */
export function wishReasonKey(wish: Pick<Wish, 'status' | 'statusReason'>): string | null {
  const { status, statusReason } = wish;
  if (status === 'pending') {
    return statusReason !== null && PENDING_HINTS.has(statusReason)
      ? `wishes.pending.hint.${statusReason}`
      : null;
  }
  if ((status !== 'presented' && status !== 'failed') || statusReason === null) {
    return null;
  }
  return EXPLAINED_REASONS.has(statusReason)
    ? `wishes.reason.${statusReason}`
    : 'wishes.reason.other';
}

/** Time left or "expired since" of an active or presented, non-permanent wish; else `null`. */
export function wishRemainingPart(
  wish: Pick<Wish, 'status' | 'permanent' | 'expiresAtUtc'>,
  now: Date,
): TextPart | null {
  if ((wish.status !== 'active' && wish.status !== 'presented') || wish.permanent) {
    return null;
  }
  const left = remainingTime(wish.expiresAtUtc, now);
  switch (left?.kind) {
    case 'days':
      return { key: pluralKey(left.count, 'wishes.row.remaining.days'), params: left };
    case 'hours':
      return { key: pluralKey(left.count, 'wishes.row.remaining.hours'), params: left };
    case 'expired_days':
      return { key: pluralKey(left.count, 'wishes.row.remaining.expiredDays'), params: left };
    case 'expired_today':
      return { key: 'wishes.row.remaining.expiredToday' };
    default:
      return null;
  }
}

/**
 * The run time part of a row: "N days run time", or with extensions "initial run time N days" (the
 * extension count follows as its own part), since an extension moves the expiry, not `durationDays`.
 * `null` for a permanent wish.
 */
export function wishDurationPart(
  wish: Pick<Wish, 'permanent' | 'durationDays' | 'extensionCount'>,
): TextPart | null {
  if (wish.permanent) {
    return null;
  }
  const base = wish.extensionCount > 0 ? 'wishes.row.initialDuration' : 'wishes.row.duration';
  return { key: pluralKey(wish.durationDays, base), params: { count: wish.durationDays } };
}

const DAY_MS = 86_400_000;

/**
 * How long an open wish has been waiting, counted from its redemption (`createdAtUtc`): "today" within
 * the first day, then whole days. `null` for any other status or an unusable date.
 */
export function wishPendingAgePart(
  wish: Pick<Wish, 'status' | 'createdAtUtc'>,
  now: Date,
): TextPart | null {
  if (wish.status !== 'pending') {
    return null;
  }
  const created = Date.parse(wish.createdAtUtc);
  if (Number.isNaN(created)) {
    return null;
  }
  const days = Math.floor(Math.max(0, now.getTime() - created) / DAY_MS);
  return days === 0
    ? { key: 'wishes.pending.age.today' }
    : { key: pluralKey(days, 'wishes.pending.age.days'), params: { count: days } };
}
