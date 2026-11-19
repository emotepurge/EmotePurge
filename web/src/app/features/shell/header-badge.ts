/**
 * Whether the header row carries a status badge right now: the worker warning, or the live-quota
 * badge (which itself only shows while the worker warning does not). One place for the question,
 * because the header's room below `sm` is arithmetic: while a badge stands, nothing optional may
 * stand next to "Abstimmungen" (design language §4.3).
 */
export function headerBadgeShown(workerStale: boolean, liveQuotaPaused: boolean): boolean {
  return workerStale || liveQuotaPaused;
}

/** The wishes count in the header gives way below `sm` while a badge stands (DECISIONS (z)). */
export function wishesCountVisibleBelowSm(badgeShown: boolean): boolean {
  return !badgeShown;
}
