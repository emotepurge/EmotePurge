/**
 * Whether a failed write's status says nothing about the write having committed: 0 is a dropped or
 * aborted connection, and every 5xx can follow a commit — the API's own 500 (an uncertain commit: the
 * connection drops before the database acknowledges it, `CommitAsync` throws although the
 * transaction went through) as much as a proxy's 502/503/504 or a CDN's 520–527. Only a 4xx is a
 * confirmed rejection.
 *
 * Shared by every irreversible action that has to tell "nothing changed" apart from "we cannot know"
 * — the account deletion and the broadcaster's channel purge. Meaningless for a read: a dropped GET
 * changes nothing either way.
 */
export function isUnknownOutcome(status: number): boolean {
  return status === 0 || status >= 500;
}
