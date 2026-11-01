import type { ForeignEmoteSetResponse } from './foreign-emote-set.model';
import type { SevenTvSetReadState } from './seven-tv-set-entries';

type PreviewVerdict = Pick<ForeignEmoteSetResponse, 'truncated' | 'readState' | 'omittedCount'>;

/**
 * The three-way verdict on a backend set preview (#457), read fail-closed.
 *
 * Returns the response's own `readState` when it is present and agrees with `truncated`; without
 * it (an Api from before #457) the state is derived from `truncated` alone — `true` reads as
 * `'inconsistent'`, never as `'gap'`, so an older response keeps today's locks. Every answer that
 * does not fit the wire contract (`truncated === (readState !== 'complete')`, `omittedCount > 0`
 * exactly for a gap, one of the three known values) is `'inconsistent'` too: a reader may only act
 * on a gap when the Api positively said so.
 */
export function previewReadState(response: PreviewVerdict): SevenTvSetReadState {
  // `== null`: an Api that serialises a missing value as `null` is just as old as one omitting it.
  if (response.readState == null) {
    return response.truncated ? 'inconsistent' : 'complete';
  }
  switch (response.readState) {
    case 'complete':
      return response.truncated ? 'inconsistent' : 'complete';
    case 'gap':
      return response.truncated && (response.omittedCount ?? 0) > 0 ? 'gap' : 'inconsistent';
    default:
      return 'inconsistent';
  }
}

/** How many entries 7TV counts but does not list — `omittedCount` for a gap read
 *  (`previewReadState` says `'gap'`), `0` for every other read. */
export function previewOmittedCount(response: PreviewVerdict): number {
  return previewReadState(response) === 'gap' ? (response.omittedCount ?? 0) : 0;
}
