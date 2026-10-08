import { TagPlacementSnapshotEntry } from '../tags/emote-tag.model';
import { RunResult } from './seven-tv-run-engine';

/**
 * What a tag play-in run carries from the flow to the service (#201 T-C). `operationId` is the id
 * registered with the server before the run started; the same id is sent with the placement report,
 * which makes a retry idempotent. The target owner already lives on the run's target.
 */
export interface ImportTagContext {
  tagId: number;
  operationId: string;
}

/**
 * What a tag removal run carries from the flow to the service (#201 T-C). `snapshot` is the
 * placement revisions read in the preview, sent back verbatim so the server touches only the
 * placements that were shown. `checkedOwnIds` are the ticked own placements, `uncheckedOwnIds` the
 * unticked own ones — both ids of emotes live in the set.
 */
export interface DeleteTagContext {
  tagId: number;
  operationId: string;
  activationOperationId: string | null;
  snapshot: TagPlacementSnapshotEntry[];
  checkedOwnIds: string[];
  uncheckedOwnIds: string[];
  channelName: string;
}

/**
 * The `keptIds` of a removal report: the own placements the run did not remove. Without a run
 * (nothing was sent to 7TV) that is every own placement; with one, the unticked ones plus every
 * ticked one that has no `done` row (`failed`, `cancelled`, `unknown` or missing) — a removal that
 * 7TV did not confirm must stay a placement, or the server would forget an emote that is still
 * there. Unticked ids come first, then the ticked ones in context order, each id once.
 *
 * Run keys are the `sevenTvEmoteId` for a delete run, so `doneKeys` is compared with the ids directly.
 */
export function deriveTagKeptIds(
  tag: Pick<DeleteTagContext, 'checkedOwnIds' | 'uncheckedOwnIds'>,
  result: RunResult | null,
): string[] {
  const done = new Set(result === null ? [] : result.doneKeys);
  const kept = new Set<string>(tag.uncheckedOwnIds);
  for (const id of tag.checkedOwnIds) {
    if (!done.has(id)) {
      kept.add(id);
    }
  }
  return [...kept];
}
