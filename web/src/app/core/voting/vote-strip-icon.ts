/**
 * How the ballot strip's thumb icon renders — decided per strip and, only on the dense strip, per
 * tally. `vote-session-detail-page.ts` calls this once per button (keep/delete checked
 * independently, since one side's tally can cross the overflow line without the other's doing
 * so) and maps `'full'`/`'compact'` to an icon size class; `'none'` renders no icon at all.
 *
 * See DECISIONS 2026-09-23 for the measurement behind `STRIP_ICON_DENSE_OVERFLOW_AT` and the two
 * amendments this codifies: the icon is never hidden outright any more (a withheld tally on the
 * dense strip still gets the full-size icon, since nothing shares its 24 px height with it), and
 * only a *rendered* wide tally can push it out.
 */
export type VoteStripIconMode = 'full' | 'compact' | 'none';

/**
 * 1000 keep or delete votes on one emote is not a case this app has ever seen, but "never truncate
 * the number" rules out clipping it instead of the icon — so past this threshold `'compact'` gives
 * way to `'none'` rather than overflowing. Exported so the DECISIONS entry and the spec can both
 * point at the one number instead of a second copy of it.
 */
export const STRIP_ICON_DENSE_OVERFLOW_AT = 1000;

/**
 * `isNarrowStrip` is the mobile 44 px strip (96 px cell, ~48 px per half) — room was never the
 * constraint there, so it always gets the full 14 px icon regardless of tally width. On the dense
 * 24 px strip (64 px cell, ~32 px per half): a withheld tally (`null`) has no number to share that
 * width with, so it gets the full icon too; a *rendered* tally under `STRIP_ICON_DENSE_OVERFLOW_AT`
 * gets the smaller 8 px icon instead (measured to survive up to three digits — see the constant's
 * own comment); at or past the threshold the icon gives way entirely.
 */
export function voteStripIconMode(isNarrowStrip: boolean, tally: number | null): VoteStripIconMode {
  if (isNarrowStrip || tally === null) {
    return 'full';
  }
  return tally < STRIP_ICON_DENSE_OVERFLOW_AT ? 'compact' : 'none';
}
