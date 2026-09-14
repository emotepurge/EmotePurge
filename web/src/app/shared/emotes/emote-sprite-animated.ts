import {
  Component,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';

import { ReducedMotionService } from '../../core/motion/reduced-motion.service';
import { EmoteSprite } from './emote-sprite';
import { animatedEmoteUrl } from './emote-url';

/**
 * One emote, still first and animated a moment later — for the surfaces that show a single emote.
 *
 * Every emote is stored as its 4x still, because the atlas draws hundreds at once (the reasoning
 * lives on `animatedEmoteUrl`). The sidecar and the readout line show one at a time, so they can
 * afford the animation. They just must not pay for it on a pointer that is merely passing: the
 * atlas is a dense grid, and one sweep across it makes this component see dozens of emotes. Hence
 * the dwell — the animation is fetched only for an emote the pointer actually stopped on.
 *
 * Stacked rather than swapped. Rebinding one `<img>` to the animated url would hide the sprite
 * until the new bytes arrive (EmoteSprite keeps an unloaded picture invisible, deliberately), so
 * the emote would blink out under the cursor for as long as a 1.2 MB animation takes. Here the
 * still holds the box — already painted, since the atlas loaded that exact url — and the animation
 * lies on top, revealing itself through EmoteSprite's own settle logic once it has decoded.
 *
 * `upgraded` is keyed on url identity rather than a bare boolean, the same way `settled` is, and
 * for a sharper reason: a boolean left true from the previous emote would let the *next* emote's
 * animation mount instantly, skipping the dwell it was supposed to earn. Keying it means a url
 * change withdraws the overlay synchronously, with no dependency on when the effect happens to run.
 *
 * The still is only *painted underneath*, though — it stays in the DOM the whole time, and the
 * `@if` above only ever adds a layer on top of it, never removes it. Nothing had ever hidden the
 * still again once the overlay had something to show, so wherever the animation's own motion left
 * its bounding box (any emote whose frames don't fully cover the paused pose) the still kept
 * showing through it, visibly doubled. `revealedAnimatedUrl` closes that gap: it hides the still
 * once — and only once — `EmoteSprite.settledUrl` reports the *current* animated url has actually
 * painted, so the still keeps holding the box for exactly as long as it is needed and not a frame
 * longer. Keyed on url identity again, for the same race a bare boolean would lose: a stale
 * `settledUrl` emission for a url the pointer has since left must not hide the still for whatever
 * emote is now underneath it. Because the comparison is a computed rather than something an effect
 * writes, a url change clears it synchronously in the same tick — the still reappears immediately,
 * with nothing to wait on.
 *
 * **Under `prefers-reduced-motion: reduce` the still is all there is.** The dwell timer never starts,
 * so the animated variant is never requested, and a preference switched on while an animation is
 * showing withdraws it at once. Handled here rather than at each call site on purpose: every surface
 * that shows a single emote goes through this component (sidecar, inspector row, drilldown dialog,
 * ballot, import grid), and a preference that one of them forgot to ask about would not be honoured.
 */
@Component({
  selector: 'app-emote-sprite-animated',
  imports: [EmoteSprite],
  template: `
    <span class="relative block h-full w-full">
      <app-emote-sprite
        [url]="url()"
        [size]="size()"
        [spriteClass]="spriteClass() + (stillHidden() ? ' invisible' : '')"
        [dimmed]="dimmed()"
      />
      @if (upgraded()) {
        <app-emote-sprite
          [url]="animated()"
          [size]="size()"
          [spriteClass]="spriteClass() + ' absolute inset-0'"
          [dimmed]="dimmed()"
          (settledUrl)="onOverlaySettled($event)"
        />
      }
    </span>
  `,
  host: { class: 'contents' },
})
export class EmoteSpriteAnimated {
  /** The stored still url, exactly as the atlas draws it. */
  readonly url = input.required<string>();
  readonly size = input.required<number>();
  readonly spriteClass = input('h-full w-full object-contain p-1');
  /**
   * Passed to both inner sprites, not just the still: the overlay paints on top once upgraded, so
   * leaving it undimmed would restore full opacity over an archived emote the moment the animation
   * mounts.
   */
  readonly dimmed = input(false);

  /**
   * How long the emote has to stay put before its animation is worth fetching. 200 ms is below the
   * point where a deliberate stop feels delayed, and above the time a sweeping pointer spends on
   * any one cell.
   */
  readonly dwellMs = input(200);

  /**
   * Fires the animated url once the animation has actually painted over the still — the moment the
   * still hides itself. For a caller that keeps a still of its own underneath this component (the
   * import grid does, so that hovering a cell never re-creates, and therefore never blanks, the
   * picture already on screen): that still has to hide at the same moment, or it shows through the
   * animation exactly like the doubled still described in the class comment.
   */
  readonly animationShown = output<string>();

  private readonly reducedMotion = inject(ReducedMotionService);

  protected readonly animated = computed(() => animatedEmoteUrl(this.url()));

  private readonly upgradedUrl = signal<string | null>(null);

  protected readonly upgraded = computed(
    () => !this.reducedMotion.prefersReducedMotion() && this.upgradedUrl() === this.animated(),
  );

  /**
   * See the class comment: which animated url, if any, has actually painted over the still.
   *
   * A `linkedSignal` on `animated`, not a plain one, and that is the whole point: revisiting an
   * emote the pointer had already dwelt on (A -> B -> A) would otherwise find this still holding
   * A's url from the first visit, so the still would hide itself the instant the pointer came back
   * — while the overlay is not mounted yet, because the dwell starts over. That is a blank cell for
   * at least the dwell, and forever if the animation then fails to load. Resetting on every url
   * transition makes the second visit cost exactly what the first one did.
   */
  protected readonly revealedAnimatedUrl = linkedSignal<string, string | null>({
    source: this.animated,
    computation: () => null,
  });

  /** The still hides only once the overlay currently shown has itself settled — see class comment. */
  protected readonly stillHidden = computed(
    // Gated on `upgraded` as well: when the overlay is withdrawn without the url changing (reduced
    // motion switched on mid-animation), the reveal marker still names this url, and the still would
    // stay hidden over nothing.
    () => this.upgraded() && this.revealedAnimatedUrl() === this.animated(),
  );

  constructor() {
    effect((onCleanup) => {
      const target = this.animated();
      // A still emote's animated url is its own: there is nothing to fetch, and mounting a second
      // <img> on it would duplicate a request for a picture already on screen.
      if (target === this.url()) {
        return;
      }
      // See the class comment: under reduced motion there is no animation to earn.
      if (this.reducedMotion.prefersReducedMotion()) {
        return;
      }

      const handle = setTimeout(() => this.upgradedUrl.set(target), this.dwellMs());
      // Runs before the next effect pass and on destroy, so an emote the pointer left behind never
      // fires its request.
      onCleanup(() => clearTimeout(handle));
    });
  }

  protected onOverlaySettled(url: string): void {
    this.revealedAnimatedUrl.set(url);
    if (url === this.animated()) {
      this.animationShown.emit(url);
    }
  }
}
