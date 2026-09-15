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
