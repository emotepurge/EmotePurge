import {
  SyncDeletedInSetChannelResult,
  SyncDeletedInSetResponse,
  SyncRestoredInSetChannelResult,
  SyncRestoredInSetResponse,
} from './seven-tv-emote-set.model';

/**
 * Outcome of reporting a run's changes back to our own API (not to 7TV) — moved here from
 * `seven-tv-delete.service.ts` (spec 6.4/T4) so a pure classification module does not have to
 * import an injectable service just for a type. Every prior importer now reads it from here; there
 * is deliberately no re-export at the old location (a re-export would leave two "correct" import
 * paths for the same name, which is exactly the drift this move is meant to close).
 *
 * `'partial'` means the call itself succeeded but the backend's answer does not amount to a full
 * success — either it archived/restored fewer rows than reported (`'shortfall'`) or it could not
 * resolve the channel it was told to expect (`'channelMismatch'`); see {@link SyncReportReason}. */
export type SyncReportState = 'idle' | 'pending' | 'succeeded' | 'partial' | 'failed';

/** Why a `SyncReportState` of `'failed'` or `'partial'` is what it is (spec E23) — shown by the
 *  dock as its own line under the bare state, because a naked `'failed'` does not say whether the
 *  right was revoked mid-run or 7TV simply did not answer (H3). `null` whenever the state is
 *  `'idle'`, `'pending'` or a plain `'succeeded'`. */
export type SyncReportReason =
  | 'forbidden' // 403 — the actor's editor/owner right was revoked between the pre-check and the report (F4).
  | 'setNotFound' // 404 — the set is gone from 7TV's side (#224: this must never read as 'succeeded').
  | 'unavailable' // 429/503, or a network failure, after the automatic retries.
  | 'channelMismatch' // an `unresolvedChannel` came back — the expected channel was not hit (E18).
  | 'shortfall' // at least one touched channel's count fell short of `reportedCount` (F8, per channel).
  | 'other'; // any other HTTP failure.

/** Why the shared pre-check (`SevenTvEmoteSetService.resolveEditableSet`, spec 6.2) blocked a run
 *  before it could start — the three non-`'editable'` outcomes of `EditableSetResolution`, pulled
 *  out as their own type because a blocked caller (`FileImportStep`, `MassDeletePanel`,
 *  `import-flow.ts`) only ever needs *this* half of the union, not the resolved target that comes
 *  with `'editable'`. */
export type TargetCheckBlockReason = 'notEditable' | 'notSelectable' | 'unavailable';

/** What {@link classifySyncInSetResponse} and {@link classifySyncInSetFailure} both return — a
 *  `SyncReportState` paired with the reason behind it, `null` exactly when the state does not need
 *  one (`'succeeded'`). Kept as one small object rather than two return values so a caller cannot
 *  set one signal without the other and leave them momentarily out of sync. */
export interface SyncReportOutcome {
  readonly state: SyncReportState;
  readonly reason: SyncReportReason | null;
}

const SUCCEEDED: SyncReportOutcome = { state: 'succeeded', reason: null };

function channelCount(
  channel: SyncDeletedInSetChannelResult | SyncRestoredInSetChannelResult,
): number {
  return 'archivedCount' in channel ? channel.archivedCount : channel.restoredCount;
}

/**
 * Classifies a successful (2xx) answer from `sync-deleted`/`sync-restored` into the threeway state
 * F8 and E23 require, for the delete, restore and import services alike (spec 6.4, 6.5, AK 15).
 * Deliberately generic over both response shapes — `archivedCount`/`restoredCount` are the two
 * field names the exact same rule reads under, never something a caller normalizes first.
 *
 * Order matters:
 *
 * 1. An `unresolvedChannel` always means `'partial'`/`'channelMismatch'` — even when every channel
 *    that *was* resolved came back complete (spec edge case: "even given fully complete
 *    channels"). The expected channel not being hit is itself the problem (E18), independent of
 *    how the hit channels fared.
 * 2. Otherwise, any resolved channel whose count fell short of `reportedCount` makes the whole
 *    report `'partial'`/`'shortfall'` — F8: a shared set can span several channels, and "one of
 *    them was incomplete" is not masked by another that was fine.
 * 3. `channels: []` with no mismatch is `'succeeded'` — the common case for an untracked or
 *    non-active target, where there was never a channel row to touch in the first place (spec
 *    5.2's "no hit ⇒ just the paper entry" is not a failure).
 */
export function classifySyncInSetResponse(
  response: SyncDeletedInSetResponse | SyncRestoredInSetResponse,
  reportedCount: number,
): SyncReportOutcome {
  if (response.unresolvedChannel !== null) {
    return { state: 'partial', reason: 'channelMismatch' };
  }
  const hasShortfall = response.channels.some((channel) => channelCount(channel) < reportedCount);
  if (hasShortfall) {
    return { state: 'partial', reason: 'shortfall' };
  }
  return SUCCEEDED;
}

/**
 * Classifies an HTTP failure of `sync-deleted`/`sync-restored`, after the automatic retries have
 * given up (spec 6.4, AK 15). `0` is Angular's own status for a network failure (no response ever
 * arrived) — grouped with 429/503 under `'unavailable'` because none of the three says anything
 * about the actor's right to the set, only that the answer could not be had right now.
 */
export function classifySyncInSetFailure(httpStatus: number): SyncReportOutcome {
  switch (httpStatus) {
    case 403:
      return { state: 'failed', reason: 'forbidden' };
    case 404:
      return { state: 'failed', reason: 'setNotFound' };
    case 429:
    case 503:
    case 0:
      return { state: 'failed', reason: 'unavailable' };
    default:
      return { state: 'failed', reason: 'other' };
  }
}
