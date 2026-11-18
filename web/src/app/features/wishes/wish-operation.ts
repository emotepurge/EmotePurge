import type { WishAddState } from '../../core/wishes/wish-add.service';
import type { WishRemovalState } from '../../core/wishes/wish-removal-tracker';
import {
  WISH_START_ABORT_KEY,
  WISH_START_NOTHING_CHANGED_KEY,
} from '../../core/wishes/wish-run-start';

/** How a row's 7TV operation reads: in progress, done, or stopped/refused. */
export type WishOperationTone = 'progress' | 'done' | 'problem';

/**
 * The line a wish row shows about its own 7TV operation (plan 2.5a, 2.6): a lead
 * sentence (for an abort "Es wurde nichts geändert."), the sentence itself with its parameters,
 * and a tone. The row renders it and decides nothing about it.
 */
export interface WishOperationView {
  readonly tone: WishOperationTone;
  readonly leadKey: string | null;
  readonly key: string;
  readonly params?: Record<string, string | number>;
  /** Label of the one follow-up the line offers ("Erneut melden" for an owed removal report);
   *  the row emits `retryOperation` for it. Absent: no button. */
  readonly retryKey?: string;
}

/** The row line of an add; `null` shows nothing. */
export function wishAddOperationView(state: WishAddState | null): WishOperationView | null {
  switch (state?.kind) {
    case 'starting':
      return { tone: 'progress', leadKey: null, key: 'wishes.add.starting' };
    case 'running':
      return state.pauseSeconds === null
        ? { tone: 'progress', leadKey: null, key: 'wishes.add.running' }
        : {
            tone: 'progress',
            leadKey: null,
            key: 'wishes.add.paused',
            params: { seconds: state.pauseSeconds },
          };
    case 'added':
      return { tone: 'done', leadKey: null, key: 'wishes.add.added' };
    case 'unknown':
      return { tone: 'problem', leadKey: null, key: 'wishes.add.unknown' };
    case 'rejected':
      switch (state.reason) {
        case 'nameTaken':
          return { tone: 'problem', leadKey: null, key: 'wishes.add.nameTaken' };
        case 'tokenInvalid':
          return { tone: 'problem', leadKey: null, key: 'wishes.add.tokenInvalid' };
        default:
          return {
            tone: 'problem',
            leadKey: null,
            key: 'wishes.add.rejected',
            params: { message: state.message },
          };
      }
    case 'notStarted':
    case 'skipped':
      return {
        tone: 'problem',
        leadKey: WISH_START_NOTHING_CHANGED_KEY,
        key: WISH_START_ABORT_KEY[state.reason],
      };
    default:
      return null;
  }
}

/** "Es wurde nichts entfernt." — the lead of a removal that stopped before anything left 7TV. */
const NOTHING_REMOVED_KEY = 'wishes.remove.nothingRemoved';

/** The row line of a removal (plan 2.6); `null` shows nothing. */
export function wishRemovalOperationView(state: WishRemovalState | null): WishOperationView | null {
  switch (state?.kind) {
    case 'starting':
      return { tone: 'progress', leadKey: null, key: 'wishes.remove.starting' };
    case 'running':
      return state.pauseSeconds === null
        ? { tone: 'progress', leadKey: null, key: 'wishes.remove.running' }
        : {
            tone: 'progress',
            leadKey: null,
            key: 'wishes.remove.paused',
            params: { seconds: state.pauseSeconds },
          };
    case 'notStarted':
      return {
        tone: 'problem',
        leadKey: WISH_START_NOTHING_CHANGED_KEY,
        key: WISH_START_ABORT_KEY[state.reason],
      };
    case 'skipped':
      return {
        tone: 'problem',
        leadKey: NOTHING_REMOVED_KEY,
        key: state.reason.key,
        ...(state.reason.params ? { params: state.reason.params } : {}),
      };
    case 'failed':
      return {
        tone: 'problem',
        leadKey: NOTHING_REMOVED_KEY,
        key: 'wishes.remove.failed',
        params: { message: state.message },
      };
    case 'cancelled':
      return { tone: 'progress', leadKey: null, key: 'wishes.remove.cancelled' };
    case 'unknown':
      return { tone: 'problem', leadKey: null, key: 'wishes.remove.unknown' };
    case 'reporting':
      return { tone: 'progress', leadKey: null, key: 'wishes.remove.reporting' };
    case 'booked':
      return { tone: 'done', leadKey: null, key: 'wishes.remove.booked' };
    case 'notBooked':
      return { tone: 'problem', leadKey: null, key: 'wishes.remove.notBooked' };
    case 'reportPending':
      return {
        tone: 'problem',
        leadKey: null,
        key: 'wishes.remove.reportPending',
        retryKey: 'wishes.remove.retryReport',
      };
    default:
      return null;
  }
}
