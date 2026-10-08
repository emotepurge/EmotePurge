import { DestroyRef, Service, inject, signal } from '@angular/core';

/** Tailwind's `lg` breakpoint (64rem). Kept equal to it, so the code's decision and the classes'
 *  `lg:` variants switch at the same width. */
const WIDE_QUERY = '(min-width: 64rem)';

/**
 * Whether the viewport is at least `lg` wide.
 *
 * For layouts whose *structure* changes at that width, not just its styling — the tags page shows
 * list and detail side by side from `lg` up and as a drilldown below it (spec 9.4, design doc §8.6).
 * CSS can hide a column, but it cannot decide which of the two the page renders, whether the up-link
 * to the list exists or which of them a screen reader meets; this signal is for that. Purely visual
 * switching stays with Tailwind's `lg:` variants.
 *
 * Live, the same way `PointerModeService` and `ReducedMotionService` follow their media queries: a
 * window resized across the breakpoint switches without a reload.
 */
@Service()
export class WideViewportService {
  private readonly destroyRef = inject(DestroyRef);

  private readonly wide = signal(false);

  readonly isWide = this.wide.asReadonly();

  constructor() {
    const query = matchMedia(WIDE_QUERY);
    this.wide.set(query.matches);

    const onChange = (event: MediaQueryListEvent) => this.wide.set(event.matches);
    query.addEventListener('change', onChange);
    this.destroyRef.onDestroy(() => query.removeEventListener('change', onChange));
  }
}
