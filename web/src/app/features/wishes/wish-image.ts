import { Component, computed, input } from '@angular/core';

import { emoteStillUrl } from '../../shared/emotes/emote-url';
import { EmoteSprite } from '../../shared/emotes/emote-sprite';

/**
 * Which picture a wish row asks for (#485, plan 2.10, Sol-P3-13 and Sol-R2-P3). The row never shows a
 * rendition that can move unless it is proven that the emote does not:
 *
 * 1. the DTO carries `imageUrl` → exactly that (the channel's stored still: the first frame of an
 *    animated emote, or the only picture of one confirmed still);
 * 2. no `imageUrl`, and this session's single 7TV read (T6) found the emote not animated → the plain
 *    rendition, which is still by that proof;
 * 3. no `imageUrl` and nothing known → **only** the `_static` rendition. If it fails, for whatever
 *    reason, the surface stays empty: an `<img>` cannot tell a 404 (a still emote has no `_static`)
 *    from a short network failure, so a failed load proves nothing and there is no fallback to the
 *    plain rendition, which on an animated emote is the animation.
 *
 * None of the three can be animated, so `prefers-reduced-motion: reduce` needs no branch of its own:
 * the rule already never loads an animatable rendition, with or without the preference.
 */
export function wishImageUrl(
  imageUrl: string | null,
  sevenTvEmoteId: string,
  knownStill: boolean,
): string {
  if (imageUrl) {
    return imageUrl;
  }
  return emoteStillUrl(sevenTvEmoteId, !knownStill);
}

/** The picture of a wish row, after {@link wishImageUrl}; a failed load leaves the plate empty. */
@Component({
  selector: 'app-wish-image',
  imports: [EmoteSprite],
  template: `
    <span class="flex size-10 shrink-0 bg-emote-canvas" aria-hidden="true">
      <app-emote-sprite [url]="url()" [size]="40" />
    </span>
  `,
  host: { class: 'contents' },
})
export class WishImage {
  readonly imageUrl = input.required<string | null>();
  readonly sevenTvEmoteId = input.required<string>();
  /** Set by the caller once the 7TV read of this session found the emote not animated (T6). */
  readonly knownStill = input(false);

  protected readonly url = computed(() =>
    wishImageUrl(this.imageUrl(), this.sevenTvEmoteId(), this.knownStill()),
  );
}
