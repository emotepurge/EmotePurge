/**
 * What the monitoring row for the wish capture (K6, plan #486 2.3) says. The worker alone decides
 * whether redemptions are stored; the Api's switch only shows or hides the routes and the settings
 * section. The two processes read their own environment, so a partial restart can leave them
 * disagreeing, and this row is where an operator sees that.
 *
 * `workerActive` is `null` for a worker that predates the field or has published no snapshot —
 * "unknown", never "off".
 */
export type WishCaptureRow =
  /** Both processes agree that capture is on. */
  | { state: 'active' }
  /** Both sides know their state and it differs: a partial restart or a partial rollback. */
  | { state: 'mismatch'; workerActive: boolean }
  /** The Api shows the capture, the worker has not said whether it runs. */
  | { state: 'unknown' };

/**
 * The row for the two values from `GET /api/admin/health`, or `null` when there is nothing to
 * show: the row appears only while at least one side is switched on, so an installation that does
 * not use the capture carries no extra line. A worker that says `true` while the Api says nothing
 * (an Api that predates the field) counts as a disagreement of the Api being off.
 */
export function wishCaptureRow(
  workerActive: boolean | null | undefined,
  apiEnabled: boolean | undefined,
): WishCaptureRow | null {
  const worker = workerActive ?? null;
  const api = apiEnabled === true;
  if (worker === null) {
    return api ? { state: 'unknown' } : null;
  }
  if (worker && api) {
    return { state: 'active' };
  }
  if (!worker && !api) {
    return null;
  }
  return { state: 'mismatch', workerActive: worker };
}
