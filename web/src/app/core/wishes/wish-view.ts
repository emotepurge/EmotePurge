import type { Wish, WishStatus, WishStatusReason } from './wish.model';

/**
 * Every derived notion of the wish UI (#481, K3, plan 2.2 and 2.3) in one pure file. "Now" is always a
 * parameter: the page advances a signal once a minute and passes it in.
 */

const HOUR_MS = 3_600_000;
const DAY_MS = 24 * HOUR_MS;

/** Fields the rules read; a full `Wish` satisfies it. */
export type WishRuleInput = Pick<
  Wish,
  'status' | 'statusReason' | 'slotId' | 'slotAddedAtUtc' | 'expiresAtUtc' | 'mode'
> &
  Partial<Pick<Wish, 'source' | 'linkOrigin'>>;

/**
 * Whether the wish knows its 7TV emote. An open wish captured in chat may not yet: it carries `''` in
 * `sevenTvEmoteId`, `emoteName` and `alias` (plan 2.8a). Every place that treats the id as real (image,
 * 7TV link, usage, a write to 7TV, a dialog pre-fill) asks this one question first.
 */
export function hasEmote(wish: Pick<Wish, 'sevenTvEmoteId'>): boolean {
  return wish.sevenTvEmoteId !== '';
}

/**
 * The name an open wish is shown under: its alias once it knows the emote, else the name the viewer
 * typed, else the redemption text (the last two are redeemer data and may be absent). `''` when nothing
 * is known; the caller decides what an empty title reads as.
 */
export function wishTitle(
  wish: Pick<Wish, 'sevenTvEmoteId' | 'alias' | 'requestedName' | 'requestText'>,
): string {
  if (hasEmote(wish)) {
    return wish.alias;
  }
  return wish.requestedName || wish.requestText || '';
}

/** An automatic chat link the manager may take back: `active`, from chat, linked by the system. */
export function canUnlink(
  wish: Pick<Wish, 'status'> & Partial<Pick<Wish, 'source' | 'linkOrigin'>>,
): boolean {
  return wish.status === 'active' && wish.source === 'irc' && wish.linkOrigin === 'auto';
}

/**
 * Approving a wish whose emote already runs for another wish books an extension of that one instead of
 * a new entry; the button then reads as an extension. That is the hint `already_running`.
 */
export function approveExtendsRunning(wish: Pick<Wish, 'status' | 'statusReason'>): boolean {
  return wish.status === 'pending' && wish.statusReason === 'already_running';
}

function parseUtc(value: string | null | undefined): number | null {
  if (!value) {
    return null;
  }
  const ms = Date.parse(value);
  return Number.isNaN(ms) ? null : ms;
}

/**
 * Needs a manager's attention: the server's `Open` statuses (`pending`, `failed`, `presented`), plus
 * `approved` while the wish is in manual mode.
 */
export function needsAction(wish: Pick<Wish, 'status' | 'mode'>): boolean {
  switch (wish.status) {
    case 'pending':
    case 'failed':
    case 'presented':
      return true;
    case 'approved':
      return wish.mode === 'manual';
    default:
      return false;
  }
}

/**
 * Probation: `active`, a `probationStartsAtUtc` present, and now at or past it. Only the field counts
 * (AK36); it is never recomputed from duration and lead.
 */
export function isProbation(
  wish: Pick<Wish, 'status' | 'probationStartsAtUtc'>,
  now: Date,
): boolean {
  if (wish.status !== 'active') {
    return false;
  }
  const start = parseUtc(wish.probationStartsAtUtc);
  return start !== null && now.getTime() >= start;
}

/** Due: `presented` (any reason), or `active` whose expiry has passed (or is exactly now) with no reaction yet. */
export function isDue(wish: Pick<Wish, 'status' | 'expiresAtUtc'>, now: Date): boolean {
  if (wish.status === 'presented') {
    return true;
  }
  if (wish.status !== 'active') {
    return false;
  }
  const expires = parseUtc(wish.expiresAtUtc);
  return expires !== null && expires <= now.getTime();
}

export type RemainingTime =
  | { kind: 'days'; count: number }
  | { kind: 'hours'; count: number }
  | { kind: 'expired_days'; count: number }
  | { kind: 'expired_today' };

/**
 * Time to expiry: days from one day up with a begun day counted (a fresh 30-day deadline reads 30,
 * not 29), below that whole hours (at least 1); at or after expiry the days since (`expired_today` within the first day). `null` without a usable expiry.
 */
export function remainingTime(expiresAtUtc: string | null, now: Date): RemainingTime | null {
  const expires = parseUtc(expiresAtUtc);
  if (expires === null) {
    return null;
  }
  const diff = expires - now.getTime();
  if (diff > 0) {
    return diff >= DAY_MS
      ? { kind: 'days', count: Math.ceil(diff / DAY_MS) }
      : { kind: 'hours', count: Math.max(1, Math.floor(diff / HOUR_MS)) };
  }
  const daysAgo = Math.floor(-diff / DAY_MS);
  return daysAgo === 0 ? { kind: 'expired_today' } : { kind: 'expired_days', count: daysAgo };
}

export interface TimeInSet {
  sinceUtc: string;
  /** `startsAtAuthoritative` is false: the date is an estimate. */
  provisional: boolean;
}

/**
 * Since when the emote is in the set: `slotAddedAtUtc` when known (a deadline starting "from now"
 * has a `startsAtUtc` later than the entry's arrival), else `startsAtUtc`; `null` without either.
 * Either date is provisional while `startsAtAuthoritative` is false: the flag also says whether 7TV
 * confirmed the slot generation (DECISIONS 2026-11-18, #485 (b)), so an unconfirmed slot date is an
 * estimate too.
 */
export function timeInSet(
  wish: Pick<Wish, 'startsAtUtc' | 'startsAtAuthoritative'> & { slotAddedAtUtc?: string | null },
): TimeInSet | null {
  if (wish.slotAddedAtUtc) {
    return { sinceUtc: wish.slotAddedAtUtc, provisional: !wish.startsAtAuthoritative };
  }
  return wish.startsAtUtc
    ? { sinceUtc: wish.startsAtUtc, provisional: !wish.startsAtAuthoritative }
    : null;
}

/** The day the entry left the set or the decision fell (`endedAtUtc`), as the server's UTC `YYYY-MM-DD`; `null` while running. */
export function endedDate(wish: Pick<Wish, 'endedAtUtc'>): string | null {
  const ms = parseUtc(wish.endedAtUtc);
  return ms === null ? null : new Date(ms).toISOString().slice(0, 10);
}

/**
 * Whether a removal can be started from the row: a slot, the slot generation and an authoritative
 * start date (plan 2.6). A missing one makes the action locked, not hidden.
 */
export function canRemove(
  wish: Pick<Wish, 'slotId' | 'slotAddedAtUtc' | 'startsAtAuthoritative'>,
): boolean {
  return !!wish.slotId && !!wish.slotAddedAtUtc && wish.startsAtAuthoritative;
}

export type WishAction =
  | 'add7tv'
  | 'adopt'
  | 'edit'
  | 'close'
  | 'retry'
  | 'extend'
  | 'keep'
  | 'remove'
  | 'reAdd'
  | 'approve'
  | 'reject'
  | 'unlink';

/** The actions that write to 7TV (the stars of the plan table); they vanish on a coarse pointer. */
const SEVEN_TV_ACTIONS: ReadonlySet<WishAction> = new Set<WishAction>(['add7tv', 'remove']);

export function isSevenTvAction(action: WishAction): boolean {
  return SEVEN_TV_ACTIONS.has(action);
}

/** Actions that end a wish, or take a link back, and need a confirmation (plan 2.7, K6 2.10). */
export function isConfirmedAction(action: WishAction): boolean {
  return action === 'keep' || action === 'close' || action === 'reject' || action === 'unlink';
}

/**
 * The actions of an open (`pending`) wish by its hint (`statusReason`), in the order the row shows them
 * (plan 2.10). An unknown or missing hint reads as no hint.
 */
const PENDING_ACTIONS: Readonly<Record<string, readonly WishAction[]>> = {
  unparsed: ['approve', 'adopt', 'reject'],
  ambiguous_match: ['adopt', 'approve', 'reject'],
  already_in_set: ['adopt', 'reject'],
  already_running: ['approve', 'reject'],
};
const PENDING_ACTIONS_DEFAULT: readonly WishAction[] = ['approve', 'adopt', 'edit', 'reject'];

function pendingActions(reason: string | null): WishAction[] {
  const known = reason === null ? undefined : PENDING_ACTIONS[reason];
  return [...(known ?? PENDING_ACTIONS_DEFAULT)];
}

/** A `presented` wish may be linked to an entry only for these reasons. */
const ADOPT_REASONS: ReadonlySet<string> = new Set<WishStatusReason>([
  'unconfirmed',
  'already_in_set',
]);

/**
 * The ordered actions of a row. Constructive actions come first, the destroying one (close) last,
 * with `remove` before it. An unknown status gives none, and an unknown reason changes nothing: the
 * row only renders its neutral reason line.
 */
export function wishActions(wish: WishRuleInput): WishAction[] {
  const hasSlot = !!wish.slotId;
  const hasExpiry = !!wish.expiresAtUtc;
  switch (wish.status as WishStatus) {
    case 'pending':
      return pendingActions(wish.statusReason);
    case 'approved':
      return wish.mode === 'manual' ? ['add7tv', 'adopt', 'edit', 'close'] : [];
    case 'failed':
      return ['retry', 'adopt', 'edit', 'close'];
    case 'active':
      return [
        ...(hasExpiry ? (['extend'] as const) : []),
        'keep',
        ...(canUnlink(wish) ? (['unlink'] as const) : []),
        ...(hasSlot ? (['remove'] as const) : []),
      ];
    case 'presented':
      return [
        ...(hasExpiry ? (['extend'] as const) : []),
        'keep',
        ...(wish.statusReason !== null && ADOPT_REASONS.has(wish.statusReason)
          ? (['adopt'] as const)
          : []),
        ...(hasSlot ? (['remove'] as const) : []),
        ...(hasSlot ? [] : (['close'] as const)),
      ];
    case 'removed':
    case 'removed_externally':
      return ['reAdd'];
    default:
      return [];
  }
}

export interface WishGroups<T> {
  actionNeeded: T[];
  /** Probation first, then everything that fits no group (a newer server's status) at the end. */
  probation: T[];
}

/**
 * Splits the server list of `my-wishes` in two, keeping the server order inside each group.
 * What belongs to neither group lands at the end of "probation" instead of disappearing.
 */
export function groupWishes<T>(
  items: readonly T[],
  wishOf: (item: T) => Wish,
  now: Date,
): WishGroups<T> {
  const actionNeeded: T[] = [];
  const probation: T[] = [];
  const rest: T[] = [];
  for (const item of items) {
    const wish = wishOf(item);
    if (needsAction(wish)) {
      actionNeeded.push(item);
    } else if (isProbation(wish, now)) {
      probation.push(item);
    } else {
      rest.push(item);
    }
  }
  return { actionNeeded, probation: [...probation, ...rest] };
}
