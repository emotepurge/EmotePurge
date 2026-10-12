import { DestroyRef, Signal, inject } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, map, switchMap, timer } from 'rxjs';

/**
 * A poll tick every `intervalMs` while `visible()` is true, silent while it is false.
 *
 * Every switch from hidden to visible restarts the timer and ticks **once immediately**: a tab that
 * sat in the background for half an hour must not show half-hour-old data until the next interval
 * elapses. The first subscription on an already visible page ticks immediately too, so callers need
 * no separate initial load.
 *
 * Must be called from an injection context (it reads a signal through `toObservable`); it is
 * scoped to the injecting component's lifetime.
 */
export function pollWhileVisible(intervalMs: number, visible: Signal<boolean>): Observable<void> {
  const destroyRef = inject(DestroyRef);

  return toObservable(visible).pipe(
    switchMap((isVisible) => (isVisible ? timer(0, intervalMs) : EMPTY)),
    map(() => undefined),
    takeUntilDestroyed(destroyRef),
  );
}
