import { HttpErrorResponse } from '@angular/common/http';
import { describe, expect, it } from 'vitest';

import { SyncDeletedInSetResponse, SyncRestoredInSetResponse } from './seven-tv-emote-set.model';
import {
  classifySyncInSetFailure,
  classifySyncInSetResponse,
  classifyTagReportFailure,
  isChannelMismatch,
} from './sync-report-outcome';

function deletedResponse(
  overrides: Partial<SyncDeletedInSetResponse> = {},
): SyncDeletedInSetResponse {
  return {
    reportedCount: 2,
    channels: [],
    unresolvedChannel: null,
    resyncTriggered: [],
    ...overrides,
  };
}

function restoredResponse(
  overrides: Partial<SyncRestoredInSetResponse> = {},
): SyncRestoredInSetResponse {
  return {
    reportedCount: 2,
    channels: [],
    unresolvedChannel: null,
    resyncTriggered: [],
    ...overrides,
  };
}

// AK 15, F8: the delete, restore and import services all read the same threeway classification —
// exercised once here per response shape (archivedCount vs. restoredCount), the field the two wire
// responses disagree on by name.
describe('classifySyncInSetResponse — AK 15', () => {
  it('reads channels: [] with no unresolvedChannel as succeeded (untracked/non-active target, spec 5.2)', () => {
    expect(classifySyncInSetResponse(deletedResponse(), 2)).toEqual({
      state: 'succeeded',
      reason: null,
    });
  });

  it('reads a fully-matched restore response (restoredCount) as succeeded too', () => {
    const response = restoredResponse({
      channels: [{ channelName: 'handofblood', restoredCount: 2, notFoundIds: [] }],
    });
    expect(classifySyncInSetResponse(response, 2)).toEqual({ state: 'succeeded', reason: null });
  });

  it('reads a channel whose archivedCount fell short of reportedCount as partial/shortfall', () => {
    const response = deletedResponse({
      channels: [{ channelName: 'handofblood', archivedCount: 1, notFoundIds: ['7tv-2'] }],
    });
    expect(classifySyncInSetResponse(response, 2)).toEqual({
      state: 'partial',
      reason: 'shortfall',
    });
  });

  it('reads two channels, one incomplete, as partial/shortfall — F8: judged per channel, not on the total', () => {
    const response = deletedResponse({
      channels: [
        { channelName: 'complete', archivedCount: 2, notFoundIds: [] },
        { channelName: 'short', archivedCount: 1, notFoundIds: ['7tv-2'] },
      ],
    });
    expect(classifySyncInSetResponse(response, 2)).toEqual({
      state: 'partial',
      reason: 'shortfall',
    });
  });

  it('reads an unresolvedChannel with reason activeSetDiffers as partial/channelMismatchActiveSetDiffers even when every resolved channel was complete', () => {
    const response = deletedResponse({
      channels: [{ channelName: 'handofblood', archivedCount: 2, notFoundIds: [] }],
      unresolvedChannel: { channelName: 'othermod', reason: 'activeSetDiffers' },
    });
    expect(classifySyncInSetResponse(response, 2)).toEqual({
      state: 'partial',
      reason: 'channelMismatchActiveSetDiffers',
    });
  });

  // #255: the two unresolvedChannel reasons must not collapse into one — a channel EmotePurge
  // does not track at all reads differently to a user than one that is tracked but currently
  // active under a different set.
  it('reads an unresolvedChannel with reason notTracked as partial/channelMismatchNotTracked', () => {
    const response = deletedResponse({
      unresolvedChannel: { channelName: 'othermod', reason: 'notTracked' },
    });
    expect(classifySyncInSetResponse(response, 2)).toEqual({
      state: 'partial',
      reason: 'channelMismatchNotTracked',
    });
  });
});

describe('isChannelMismatch', () => {
  it('is true for both channel-mismatch reasons', () => {
    expect(isChannelMismatch('channelMismatchNotTracked')).toBe(true);
    expect(isChannelMismatch('channelMismatchActiveSetDiffers')).toBe(true);
  });

  it.each(['forbidden', 'setNotFound', 'unavailable', 'shortfall', 'other'] as const)(
    'is false for %s',
    (reason) => {
      expect(isChannelMismatch(reason)).toBe(false);
    },
  );

  it('is false for null', () => {
    expect(isChannelMismatch(null)).toBe(false);
  });
});

describe('classifySyncInSetFailure — AK 15', () => {
  it('reads 403 as failed/forbidden (the right was revoked between the pre-check and the report, F4)', () => {
    expect(classifySyncInSetFailure(403)).toEqual({ state: 'failed', reason: 'forbidden' });
  });

  it('reads 404 as failed/setNotFound — never succeeded (#224)', () => {
    expect(classifySyncInSetFailure(404)).toEqual({ state: 'failed', reason: 'setNotFound' });
  });

  it('reads 429 as failed/unavailable', () => {
    expect(classifySyncInSetFailure(429)).toEqual({ state: 'failed', reason: 'unavailable' });
  });

  it('reads 503 as failed/unavailable', () => {
    expect(classifySyncInSetFailure(503)).toEqual({ state: 'failed', reason: 'unavailable' });
  });

  it("reads 0 (Angular's network-failure status) as failed/unavailable", () => {
    expect(classifySyncInSetFailure(0)).toEqual({ state: 'failed', reason: 'unavailable' });
  });

  it('reads any other status as failed/other', () => {
    expect(classifySyncInSetFailure(500)).toEqual({ state: 'failed', reason: 'other' });
  });
});

describe('classifyTagReportFailure', () => {
  const failure = (status: number, errorCode?: string): HttpErrorResponse =>
    new HttpErrorResponse({ status, error: errorCode === undefined ? null : { errorCode } });

  it('reads a 404 with emote_set_not_found as the set being gone', () => {
    expect(classifyTagReportFailure(failure(404, 'emote_set_not_found'))).toEqual({
      state: 'failed',
      reason: 'setNotFound',
    });
  });

  it.each(['tag_not_found', 'channel_not_found', 'tag_operation_unknown'])(
    'reads a 404 with %s as tagUnknown, never as a missing set',
    (code) => {
      expect(classifyTagReportFailure(failure(404, code))).toEqual({
        state: 'failed',
        reason: 'tagUnknown',
      });
    },
  );

  it('reads a 404 without a recognized code as other', () => {
    expect(classifyTagReportFailure(failure(404))).toEqual({ state: 'failed', reason: 'other' });
    expect(classifyTagReportFailure(failure(404, 'something_else'))).toEqual({
      state: 'failed',
      reason: 'other',
    });
  });

  it.each([403, 429, 503, 0, 500])('reads status %i like the set-centric reports', (status) => {
    expect(classifyTagReportFailure(failure(status, 'tag_not_found'))).toEqual(
      classifySyncInSetFailure(status),
    );
  });
});
