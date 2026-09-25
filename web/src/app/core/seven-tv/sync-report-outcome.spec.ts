import { describe, expect, it } from 'vitest';

import { SyncDeletedInSetResponse, SyncRestoredInSetResponse } from './seven-tv-emote-set.model';
import { classifySyncInSetFailure, classifySyncInSetResponse } from './sync-report-outcome';

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

  it('reads an unresolvedChannel as partial/channelMismatch even when every resolved channel was complete', () => {
    const response = deletedResponse({
      channels: [{ channelName: 'handofblood', archivedCount: 2, notFoundIds: [] }],
      unresolvedChannel: { channelName: 'othermod', reason: 'activeSetDiffers' },
    });
    expect(classifySyncInSetResponse(response, 2)).toEqual({
      state: 'partial',
      reason: 'channelMismatch',
    });
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
