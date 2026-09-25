import { catchError, map, Observable, of } from 'rxjs';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';

/** What a restore confirmation's live slot view renders — `null` for "unknown, show no number"
 *  (no capacity reported, or the read failed), never a zero standing in for either. */
export type RestoreSlotPreview = { occupied: number; capacity: number } | null;

/** The subset of `ResolvedRestoreTarget` (`restore-flow.ts`) this fork actually reads, kept narrow
 *  so a caller building one for the sole purpose of a slot preview does not have to invent
 *  unrelated fields (`setName`, `ownerDisplayName`, the two host fields, …). */
export interface RestoreSlotPreviewTarget {
  trackedChannelName: string | null;
  isActiveSet: boolean;
  emoteSetId: string;
  twitchLogin: string;
}

export interface RestoreSlotPreviewDeps {
  emoteAdminService: EmoteAdminService;
  emoteSetService: SevenTvEmoteSetService;
}

/**
 * The slot-preview fork shared by `startRestoreFlow` (`restore-flow.ts`) and
 * `MassDeletePanel`'s `openRestoreConfirmDialog` (spec 4.3, point 8 / spec 8.3) — extracted so the
 * two stop drifting apart (final fix wave A5): a tracked, *active* target reads the cheap,
 * non-7TV-rate-limited channel status; anything else — a non-active set of a tracked channel, or
 * an untracked target — reads the live per-set preview instead, keyed by the tracked channel when
 * there is one, otherwise the account's own `twitchLogin` (the active-set status endpoint has no
 * set-scoped or untracked form at all). A failed read of either kind resolves to `null`, never an
 * error — the confirmation still opens, just without a slot number.
 */
export function loadRestoreSlotPreview(
  deps: RestoreSlotPreviewDeps,
  target: RestoreSlotPreviewTarget,
): Observable<RestoreSlotPreview> {
  if (target.trackedChannelName !== null && target.isActiveSet) {
    return deps.emoteAdminService.getSetStatus(target.trackedChannelName).pipe(
      map((status) =>
        status.capacity === null
          ? null
          : { occupied: status.occupiedSlots, capacity: status.capacity },
      ),
      catchError(() => of(null)),
    );
  }

  return deps.emoteSetService
    .loadEmoteSetPreview(target.trackedChannelName ?? target.twitchLogin, target.emoteSetId)
    .pipe(
      map((preview) =>
        preview.capacity === null
          ? null
          : { occupied: preview.totalCount, capacity: preview.capacity },
      ),
      catchError(() => of(null)),
    );
}
