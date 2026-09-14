import { DestroyRef, Service, inject, signal } from '@angular/core';

const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)';

/**
 * Whether the operating system asks for reduced motion.
 *
 * CSS already honours the preference where the motion is a stylesheet's (`styles.css` turns off the
 * dock and sheet slide-ins under the same query). This signal is for motion the code decides to
 * start, which a media query in a stylesheet cannot stop: `EmoteSpriteAnimated` fetches an animated
 * emote after a dwell, and under `reduce` it never asks for it in the first place.
 *
 * Live rather than read once, the same way `PointerModeService` follows a change of pointing
 * device: the setting can be toggled while the app is open, and a reload should not be the way to
 * make it count.
 */
@Service()
export class ReducedMotionService {
  private readonly destroyRef = inject(DestroyRef);

  private readonly reduce = signal(false);

  readonly prefersReducedMotion = this.reduce.asReadonly();

  constructor() {
    const query = matchMedia(REDUCED_MOTION_QUERY);
    this.reduce.set(query.matches);

    const onChange = (event: MediaQueryListEvent) => this.reduce.set(event.matches);
    query.addEventListener('change', onChange);
    this.destroyRef.onDestroy(() => query.removeEventListener('change', onChange));
  }
}
