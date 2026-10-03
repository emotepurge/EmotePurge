/**
 * The animated counterpart of a stored emote url, for the one place that shows a single emote.
 *
 * Every emote reaches the frontend as the 4x *still* (`SevenTvEmoteJsonMapper.BuildImageUrl`),
 * because the atlas draws hundreds of cells at once and the animated frames of a 64 px emote run to
 * 133 KB on average and 1.2 MB at the top — 73 MB for one large set against 15 MB as stills.
 * The sidecar and the readout line show one emote at a time, so there the animation costs a single
 * request and is worth having.
 *
 * The `_static` marker in the stored url is what carries the distinction: 7TV emits it in the
 * file's own `static_name` and only for an animated emote, so a url without it belongs to a still
 * emote that has no animation to upgrade to. That makes this a total function with no flag to
 * thread through the DTOs — but it also means the marker is load-bearing, and the mapper says so.
 *
 * 2x rather than 4x on purpose: the sidecar renders at 56 px and the readout at 28 px, so 4x buys
 * no sharpness there and doubles the worst case (2.5 MB against 1.2 MB for a single hover).
 */
const STILL_SUFFIX = '/4x_static.webp';
const ANIMATED_SUFFIX = '/2x.webp';

export function animatedEmoteUrl(url: string): string {
  return url.endsWith(STILL_SUFFIX)
    ? `${url.slice(0, -STILL_SUFFIX.length)}${ANIMATED_SUFFIX}`
    : url;
}

/**
 * Whether a stored emote url belongs to an animated emote — the same `_static` marker
 * `animatedEmoteUrl` upgrades, read as a flag. Lets a surface that draws many emotes at once mark
 * the animated ones without an extra field on its DTO (the import grid, whose two sources both
 * encode the flag into the url this way). An empty or unrecognised url counts as a still: marking
 * something animated that then has no animation to play is the worse mistake.
 */
export function isAnimatedEmoteUrl(url: string): boolean {
  return url.endsWith(STILL_SUFFIX);
}

/**
 * The 4x still of a 7TV emote, built from its id and its `animated` flag — the one place the
 * frontend derives an image url from an id rather than rewriting one the backend stored (#254,
 * spec 17 K1: the undo confirm dialog shows a source emote that only the live set read knows).
 *
 * Byte-identical to the backend's `BuildForeignImageUrl` (`SevenTvApiClient.cs`) and to the two
 * forms `SevenTvEmoteJsonMapper` stores: `4x_static.webp` only for an animated emote, `4x.webp`
 * otherwise. The `_static` rendition is 7TV's flattened first frame and exists only when there is
 * something to flatten — measured 2026-09-09 against HandOfBlood's set, every still emote answered
 * 404 there while `4x.webp` answered 200. Without the flag, therefore, `4x.webp` is the only safe
 * answer (it exists for every emote; on an animated one it carries the animation), which is why the
 * caller passes `false` for a missing flag, as the backend does.
 *
 * Plan-230 T1 forbade deriving an image url from the id; that rule was about deriving it *without*
 * knowing whether the emote is animated, and it still holds for every such derivation. Both results
 * end in a suffix `animatedEmoteUrl`/`isAnimatedEmoteUrl` and the sprite's image loader already
 * recognise.
 */
export function emoteStillUrl(sevenTvEmoteId: string, animated: boolean): string {
  return `https://cdn.7tv.app/emote/${sevenTvEmoteId}${animated ? STILL_SUFFIX : '/4x.webp'}`;
}
