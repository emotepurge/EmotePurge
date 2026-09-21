import { HttpErrorResponse } from '@angular/common/http';
import { catchError, forkJoin, map, Observable, of } from 'rxjs';

import { ForeignEmoteSetResponse } from '../seven-tv/foreign-emote-set.model';
import { SevenTvEmoteSetService } from '../seven-tv/seven-tv-emote-set.service';
import { EmoteAdminService, EmoteSetWarning } from './emote-admin.service';
import { EmoteListItem } from './emote-list-item.model';
import { SevenTvSyncFailureReason } from './seven-tv-sync-failure';

/**
 * The import confirm dialog's picture of its target channel — the merge of `getSetStatus`,
 * `getSetWarning` and `listEmotes` into one value. `loading` is the state the dialog opens with
 * (before the first emission ever arrives); `failed`/`no-set` are terminal until a retry.
 */
export type ImportTargetLoadState =
  | { status: 'loading' }
  | { status: 'failed' }
  | { status: 'no-set' }
  | {
      status: 'ready';
      setId: string;
      /** `null` on the "today" path (`EmoteSetStatus` carries no set name) and whenever 7TV
       *  reports none — the confirm dialog falls back to the id in that case (spec 8.6 AK 39). */
      setName: string | null;
      occupiedSlots: number;
      capacity: number | null;
      syncFailureReason: SevenTvSyncFailureReason | null;
      emotes: EmoteListItem[];
      warning: EmoteSetWarning;
    };

/**
 * What the loader needs to know about the chosen target (spec 8.6, F5) — everything an
 * `ImportTargetChoice` (`shared/seven-tv/import-target-dialog.ts`) carries minus `scope`, kept as
 * an independent shape here rather than imported: `core/` may not import from `shared/` (layering
 * rule). TypeScript's structural typing makes an `ImportTargetChoice` (minus `scope`) assignable
 * here without an explicit conversion.
 *
 * `trackedActive` is the one case that must fire *exactly* the three requests the loader always
 * has (AK 36) — it carries no `emoteSetId` at all, on purpose: nothing about *which* set is active
 * is known (or needed) before `getSetStatus` answers, so there is nothing to compare beforehand.
 * The other two cases each name a specific, already-chosen set — one still on a tracked channel
 * (so a set-ownership check is possible, spec 6.8/E9), one not (7TV account with no channel at
 * all, spec E8) — and both read it live via `SevenTvEmoteSetService.loadEmoteSetPreview` instead of
 * `EmoteSetStatus`/`listEmotes` (spec F5).
 */
export type ImportTargetSelection =
  | { kind: 'trackedActive'; channelName: string }
  | { kind: 'trackedSet'; channelName: string; emoteSetId: string }
  | {
      kind: 'untrackedSet';
      /** URL path segment for the set-ID read route (spec 6.4) — never resolved server-side in
       *  this mode (E8), but still format-checked, so this must be shaped like a Twitch login
       *  (the account's own login, never its 7TV display name, which can contain characters a
       *  channel name never does). */
      channelName: string;
      emoteSetId: string;
    };

/** Same fallback shape `mass-delete-panel.ts` uses when its own set-warning check fails — a
 *  failed check must read as "not verified", never as a false all-clear or a false alarm. Also
 *  what an *untracked* target's warning always is (spec 8.6): there is no channel to run
 *  `EmoteSetOwnershipService` against at all, so "not checked" is not a fallback there, it is the
 *  only honest answer, and it costs no request. */
const UNAVAILABLE_WARNING: EmoteSetWarning = {
  available: false,
  isOwnSet: false,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

/** Tags one of the blocking requests with what its failure means, instead of letting the error
 *  propagate — that is what lets `forkJoin` below combine every request without ever aborting on
 *  the first rejection. */
type BlockingOutcome<T> = { kind: 'ok'; value: T } | { kind: 'no-set' } | { kind: 'failed' };

function fetchBlocking<T>(source$: Observable<T>): Observable<BlockingOutcome<T>> {
  return source$.pipe(
    map((value): BlockingOutcome<T> => ({ kind: 'ok', value })),
    catchError((error: unknown) => {
      const notFound = error instanceof HttpErrorResponse && error.status === 404;
      return of<BlockingOutcome<T>>(notFound ? { kind: 'no-set' } : { kind: 'failed' });
    }),
  );
}

/** Same blocking contract as {@link fetchBlocking}, plus spec 6.4's "`truncated` is `failed` for
 *  the import target loader" rule (spec 8.6 point 5, F3): a page cap that hid part of the set's
 *  real contents is not a smaller-but-usable answer here — the preview would undercount both the
 *  set's occupied slots and its name collisions, so it counts as a failed load, not a partial one. */
function fetchLiveTarget(
  emoteSetService: SevenTvEmoteSetService,
  channelName: string,
  emoteSetId: string,
): Observable<BlockingOutcome<ForeignEmoteSetResponse>> {
  return emoteSetService.loadEmoteSetPreview(channelName, emoteSetId).pipe(
    map((response): BlockingOutcome<ForeignEmoteSetResponse> =>
      response.truncated ? { kind: 'failed' } : { kind: 'ok', value: response },
    ),
    catchError((error: unknown) => {
      const notFound = error instanceof HttpErrorResponse && error.status === 404;
      return of<BlockingOutcome<ForeignEmoteSetResponse>>(
        notFound ? { kind: 'no-set' } : { kind: 'failed' },
      );
    }),
  );
}

/**
 * Loads everything the import confirm dialog needs about its target — a tracked channel's active
 * set (the "today" path, unchanged since #72), a tracked channel's *other* set, or an untracked
 * 7TV account's set (spec 2026-09-20, 8.6/F5).
 *
 * Deliberately not a plain `forkJoin` over the raw requests: that would abort the whole load the
 * moment any one of them errors, while the requirement is that a failed `getSetWarning` alone must
 * still leave the dialog usable ("check unavailable"), whereas a failed set/emotes read must block
 * it. The requests therefore each catch their own error and resolve to a tagged outcome — the
 * observable returned here always emits exactly once and never errors, so a bare
 * `subscribe(state => ...)` on it is enough, and a retry is just calling this function again (it
 * holds no state of its own to reset first).
 *
 * A missing active set (`activeEmoteSetId === ''`, or either blocking request 404ing) and any
 * other failure of a blocking request both count against `no-set`/`failed` — when both occur at
 * once, `no-set` wins: a 404 is the more conclusive of the two statements.
 */
export function loadImportTarget(
  emoteAdminService: EmoteAdminService,
  emoteSetService: SevenTvEmoteSetService,
  target: ImportTargetSelection,
): Observable<ImportTargetLoadState> {
  if (target.kind === 'trackedActive') {
    // The "today" path (#72) — unchanged since before this spec, on purpose (AK 36): the caller
    // has not chosen a specific, possibly non-active set, so there is nothing to compare
    // `activeEmoteSetId` against, and no reason to fire anything but exactly these three requests.
    const status$ = fetchBlocking(emoteAdminService.getSetStatus(target.channelName));
    const emotes$ = fetchBlocking(emoteAdminService.listEmotes(target.channelName));
    const warning$ = emoteAdminService
      .getSetWarning(target.channelName)
      .pipe(catchError(() => of(UNAVAILABLE_WARNING)));

    return forkJoin([status$, emotes$, warning$]).pipe(
      map(([status, emotes, warning]): ImportTargetLoadState => {
        if (status.kind === 'no-set' || emotes.kind === 'no-set') {
          return { status: 'no-set' };
        }
        if (status.kind === 'failed' || emotes.kind === 'failed') {
          return { status: 'failed' };
        }
        if (status.value.activeEmoteSetId === '') {
          return { status: 'no-set' };
        }
        return {
          status: 'ready',
          setId: status.value.activeEmoteSetId,
          setName: null,
          occupiedSlots: status.value.occupiedSlots,
          capacity: status.value.capacity,
          syncFailureReason: status.value.syncFailureReason,
          emotes: emotes.value,
          warning,
        };
      }),
    );
  }

  // A specifically chosen set, tracked-but-not-active or untracked (spec F5): belongs and
  // occupancy come from the live list, never from `EmoteSetStatus` (that status describes the
  // channel's *active* set, a different set from the one chosen here). The warning source is the
  // one thing that still depends on the class (spec 8.6): a tracked channel can still be checked
  // for ownership (E9); an untracked account has no channel to check at all, so its warning is the
  // unavailable fallback, at zero extra cost.
  const target$ = fetchLiveTarget(emoteSetService, target.channelName, target.emoteSetId);
  const warning$ =
    target.kind === 'trackedSet'
      ? emoteAdminService
          .getSetWarning(target.channelName, target.emoteSetId)
          .pipe(catchError(() => of(UNAVAILABLE_WARNING)))
      : of(UNAVAILABLE_WARNING);

  return forkJoin([target$, warning$]).pipe(
    map(([loaded, warning]): ImportTargetLoadState => {
      if (loaded.kind === 'no-set') {
        return { status: 'no-set' };
      }
      if (loaded.kind === 'failed') {
        return { status: 'failed' };
      }
      return {
        status: 'ready',
        setId: loaded.value.emoteSetId,
        setName: loaded.value.emoteSetName,
        occupiedSlots: loaded.value.totalCount,
        capacity: loaded.value.capacity,
        // No sync history exists for a set that is not this channel's own active one — there is
        // nothing stale to warn about, only 7TV's live answer, which the request above already is.
        syncFailureReason: null,
        emotes: loaded.value.emotes.map((row) => ({
          sevenTvEmoteId: row.sevenTvEmoteId,
          name: row.name,
        })),
        warning,
      };
    }),
  );
}
