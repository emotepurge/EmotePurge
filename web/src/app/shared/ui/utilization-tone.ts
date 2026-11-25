/**
 * Severity tiers of a utilization bar, purely derived from how full the underlying budget is.
 * Named after the meaning rather than the hue for the same reason `StatusBadgeTone` is: the value
 * behind each tier differs per theme, so a tier called `red` would be promising something it
 * cannot keep.
 *
 * Lives in `shared/ui/` rather than next to its first consumer (`shared/emotes/slot-budget.ts`)
 * because the admin capacity bars grade against the same ladder — and the admin area should not
 * depend on the emote domain for a purely presentational tiering.
 */
export type UtilizationTone = 'success' | 'warning' | 'danger';

/** Above this share of the capacity the bar warns, above the second one it alarms. */
export const WARN_AT_PERCENT = 80;
export const CRITICAL_AT_PERCENT = 95;

/** Per-bar override of the ladder; the default (80/95) belongs to the emote slot budget. */
export interface UtilizationThresholds {
  warnAt: number;
  criticalAt: number;
}

const DEFAULT_THRESHOLDS: UtilizationThresholds = {
  warnAt: WARN_AT_PERCENT,
  criticalAt: CRITICAL_AT_PERCENT,
};

/**
 * Grades an *uncapped* percentage: callers cap their bar width at 100 for rendering, but a budget
 * at 120 % must grade `danger`, not whatever the capped value happens to be. Non-finite and
 * negative inputs grade `success` — absence of a measurable load is not a warning state.
 */
export function utilizationTone(
  percent: number,
  thresholds: UtilizationThresholds = DEFAULT_THRESHOLDS,
): UtilizationTone {
  if (percent >= thresholds.criticalAt) {
    return 'danger';
  }
  return percent >= thresholds.warnAt ? 'warning' : 'success';
}
