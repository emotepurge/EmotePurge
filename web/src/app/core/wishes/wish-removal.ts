import { isValidEntryGeneration } from '../seven-tv/seven-tv-entry-target';
import type { DeleteRunSettlement } from '../seven-tv/seven-tv-delete.service';
import type { Wish } from './wish.model';
import { canRemove } from './wish-view';
import type { WishStartAbortReason } from './wish-run-start';

/*
 * The shared vocabulary of "Entfernen" from a wish row (#485, plan 2.6, T9): what a removal is bound
 * to, how its flow reports progress, and why a row cannot start one. The flow
 * (`features/wishes/wish-removal-flow.ts`) drives the start and the delete run; the tracker
 * (`wish-removal-tracker.ts`) keeps the outcome per wish and books it with the Api.
 */

/** What one removal run is bound to, frozen at the click: the channel, the wish, the generation of
 *  its slot (`slotAddedAtUtc` verbatim, as the DTO carried it) and the set the run writes against.
 *  The only source of the `complete-removal` body. */
export interface WishRemovalTarget {
  readonly channelName: string;
  readonly wishId: number;
  readonly slotId: string;
  readonly slotAddedAtUtc: string;
  readonly emoteSetId: string;
}

/** Where the flow reports a removal's progress; the tracker implements it. */
export interface WishRemovalProgress {
  /** The click: the start contract runs. */
  starting(): void;
  /** The start contract stopped before any write. */
  notStarted(reason: WishStartAbortReason): void;
  /** The token prompt or the confirmation was dismissed; nothing to say. */
  dismissed(): void;
  /** The delete run started under `runId`. */
  started(runId: string, target: WishRemovalTarget): void;
  /** The run's one completion report (`DeleteStartOptions.onSettled`). */
  settled(target: WishRemovalTarget, settlement: DeleteRunSettlement): void;
}

/**
 * Why "Entfernen" is locked on this wish, or `null` when the row may start it (plan 2.6): the wish
 * names no slot generation yet, its date is not confirmed by 7TV, or the date is not one the Api
 * would take back in the report. The action stays visible and says why (plan 2.2, `canRemove`).
 */
export function wishRemovalBlockKey(
  wish: Pick<Wish, 'slotId' | 'slotAddedAtUtc' | 'startsAtAuthoritative' | 'alias'>,
): string | null {
  if (!canRemove(wish)) {
    return wish.slotId && wish.slotAddedAtUtc
      ? 'wishes.remove.blocked.provisional'
      : 'wishes.remove.blocked.noEntry';
  }
  const valid = isValidEntryGeneration({
    slotId: wish.slotId ?? undefined,
    alias: wish.alias,
    addedAt: wish.slotAddedAtUtc ?? '',
  });
  return valid ? null : 'wishes.remove.blocked.invalid';
}
