import { DestroyRef, Signal, inject } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Observable, debounceTime, defer, filter, map, switchMap, tap } from 'rxjs';

import { LiveEvent } from './live-event.model';
import { LiveUpdateService } from './live-update.service';

/** Event types to react to, or a predicate when the decision needs more than the type (matching a
 *  `sessionId`, for instance). */
export type LiveEventFilter = readonly string[] | ((event: LiveEvent) => boolean);

/**
 * The debounce every channel-scoped reload uses, so that the refetches one `channel.synced` triggers
 * land in one wave instead of several staggered ones.
 *
 * A named constant rather than a literal inline so the reasoning stays attached to the number, not
 * because more than one page shares it today — usage-stats-page.ts is its only channel-scoped
 * consumer (channel-workspace-layout.ts is mounted right alongside it but keeps its own live
 * subscription deliberately undebounced, see that file's own comment). One second is the usage
 * page's figure: the worker flushes chat usage in 30-second batches, so pushes arrive in bursts
 * rather than continuously, and a second merges a burst without making the update feel delayed.
 * Against a 7TV mass delete (one event every ~275 ms) it collapses the whole run into one or two
 * refetches, because the window only elapses in a gap.
 *
 * The vote-session detail page keeps its own, shorter 500 ms window on purpose: a live tally is the
 * thing its user is watching, and it is never mounted while the mass-delete panel runs.
 */
export const CHANNEL_RELOAD_DEBOUNCE_MS = 1000;

/**
 * A live-event stream, filtered, scoped to the injecting component's lifetime.
 *
 * Pass a `Signal<string>` for a URL that follows a route parameter: the previous channel's
 * connection is torn down by the `switchMap`, because `LiveUpdateService.stream()` closes on
 * unsubscribe.
 *
 * Use this when the handler needs the individual event (its `channel`, its `sessionId`). When it
 * only needs "something happened, refetch", prefer {@link liveReload} — it collapses bursts.
 *
 * Must be called from an injection context (field initializer or constructor); it takes its own
 * `DestroyRef`, so callers do not add `takeUntilDestroyed()` themselves.
 */
export function liveEvents(
  url: string | Signal<string>,
  accept: LiveEventFilter,
): Observable<LiveEvent> {
  const liveUpdateService = inject(LiveUpdateService);
  const destroyRef = inject(DestroyRef);

  const accepts =
    typeof accept === 'function' ? accept : (event: LiveEvent) => accept.includes(event.type);

  const events =
    typeof url === 'string'
      ? liveUpdateService.stream(url)
      : toObservable(url).pipe(switchMap((current) => liveUpdateService.stream(current)));

  return events.pipe(filter(accepts), takeUntilDestroyed(destroyRef));
}

/**
 * The "listen, then refetch" pipeline the live-updating pages were each building by hand.
 *
 * Emits **once per debounced burst**, carrying the set of event types merged into that burst. That
 * set is what replaces the `syncSeenSinceReload` field two pages used to keep. Since 2026-08-29
 * `LiveUpdateService.stream()` is shared per URL, so a second subscription no longer costs a second
 * connection — but it would still cost a second debounce pipeline with its own, independently timed
 * burst boundary, which is exactly the thing this function exists to have only one of.
 *
 * @example
 * liveReload(this.liveUrl, {
 *   accept: [LIVE_EVENT_TYPES.usageFlushed, LIVE_EVENT_TYPES.channelSynced],
 *   debounceMs: CHANNEL_RELOAD_DEBOUNCE_MS,
 * }).subscribe((seen) => {
 *   this.reloadQuietly();
 *   if (seen.has(LIVE_EVENT_TYPES.channelSynced)) {
 *     this.refreshActiveEmoteSetId();
 *   }
 * });
 */
export function liveReload(
  url: string | Signal<string>,
  options: { accept: LiveEventFilter; debounceMs: number },
): Observable<ReadonlySet<string>> {
  const events = liveEvents(url, options.accept);

  // defer so each subscription gets its own `seen` set rather than sharing one across subscribers.
  return defer(() => {
    const seen = new Set<string>();
    return events.pipe(
      tap((event) => seen.add(event.type)),
      debounceTime(options.debounceMs),
      map(() => {
        const merged: ReadonlySet<string> = new Set(seen);
        seen.clear();
        return merged;
      }),
    );
  });
}
