import { firstValueFrom, of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { ForeignEmoteSetResponse } from '../../core/seven-tv/foreign-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { loadRestoreSlotPreview, RestoreSlotPreviewTarget } from './restore-slot-preview';

function setStatus(overrides: Partial<EmoteSetStatus> = {}): EmoteSetStatus {
  return {
    activeEmoteSetId: 'set-1',
    capacity: 100,
    occupiedSlots: 3,
    trackedSince: '2026-01-01T00:00:00Z',
    syncFailureReason: null,
    lastSyncAttemptAtUtc: null,
    botsExcludedSince: null,
    sharedChatSeparatedSince: null,
    duplicateNames: [],
    ...overrides,
  };
}

function preview(overrides: Partial<ForeignEmoteSetResponse> = {}): ForeignEmoteSetResponse {
  return {
    channelName: 'somechannel',
    sevenTvUserId: null,
    emoteSetId: 'set-1',
    emoteSetName: 'Main',
    capacity: 250,
    totalCount: 7,
    truncated: false,
    emotes: [],
    ...overrides,
  };
}

function deps() {
  const getSetStatus = vi.fn();
  const loadEmoteSetPreview = vi.fn();
  return {
    getSetStatus,
    loadEmoteSetPreview,
    emoteAdminService: { getSetStatus } as unknown as EmoteAdminService,
    emoteSetService: { loadEmoteSetPreview } as unknown as SevenTvEmoteSetService,
  };
}

describe('loadRestoreSlotPreview — final fix wave A5, the fork shared by restore-flow and MassDeletePanel', () => {
  it('reads getSetStatus (never the per-set preview) for a tracked, active target', async () => {
    const d = deps();
    d.getSetStatus.mockReturnValue(of(setStatus({ occupiedSlots: 4, capacity: 100 })));
    const target: RestoreSlotPreviewTarget = {
      trackedChannelName: 'somechannel',
      isActiveSet: true,
      emoteSetId: 'set-1',
      twitchLogin: 'somechannel',
    };

    const result = await firstValueFrom(
      loadRestoreSlotPreview(
        { emoteAdminService: d.emoteAdminService, emoteSetService: d.emoteSetService },
        target,
      ),
    );

    expect(result).toEqual({ occupied: 4, capacity: 100 });
    expect(d.getSetStatus).toHaveBeenCalledWith('somechannel');
    expect(d.loadEmoteSetPreview).not.toHaveBeenCalled();
  });

  it('reads the per-set preview, keyed by the tracked channel, for a tracked but non-active target', async () => {
    const d = deps();
    d.loadEmoteSetPreview.mockReturnValue(of(preview({ totalCount: 9, capacity: 250 })));
    const target: RestoreSlotPreviewTarget = {
      trackedChannelName: 'somechannel',
      isActiveSet: false,
      emoteSetId: 'set-halloween',
      twitchLogin: 'somechannel',
    };

    const result = await firstValueFrom(
      loadRestoreSlotPreview(
        { emoteAdminService: d.emoteAdminService, emoteSetService: d.emoteSetService },
        target,
      ),
    );

    expect(result).toEqual({ occupied: 9, capacity: 250 });
    expect(d.getSetStatus).not.toHaveBeenCalled();
    expect(d.loadEmoteSetPreview).toHaveBeenCalledWith('somechannel', 'set-halloween');
  });

  it('reads the per-set preview, keyed by twitchLogin, for an untracked target', async () => {
    const d = deps();
    d.loadEmoteSetPreview.mockReturnValue(of(preview({ totalCount: 2, capacity: 500 })));
    const target: RestoreSlotPreviewTarget = {
      trackedChannelName: null,
      isActiveSet: false,
      emoteSetId: 'set-foreign',
      twitchLogin: 'accountlogin',
    };

    const result = await firstValueFrom(
      loadRestoreSlotPreview(
        { emoteAdminService: d.emoteAdminService, emoteSetService: d.emoteSetService },
        target,
      ),
    );

    expect(result).toEqual({ occupied: 2, capacity: 500 });
    expect(d.getSetStatus).not.toHaveBeenCalled();
    expect(d.loadEmoteSetPreview).toHaveBeenCalledWith('accountlogin', 'set-foreign');
  });

  it('resolves to null, never a number, when the tracked-active read reports no capacity', async () => {
    const d = deps();
    d.getSetStatus.mockReturnValue(of(setStatus({ capacity: null })));

    const result = await firstValueFrom(
      loadRestoreSlotPreview(
        { emoteAdminService: d.emoteAdminService, emoteSetService: d.emoteSetService },
        {
          trackedChannelName: 'somechannel',
          isActiveSet: true,
          emoteSetId: 'set-1',
          twitchLogin: 'somechannel',
        },
      ),
    );

    expect(result).toBeNull();
  });

  it('resolves to null, not an error, when the tracked-active read fails', async () => {
    const d = deps();
    d.getSetStatus.mockReturnValue(throwError(() => new Error('boom')));

    const result = await firstValueFrom(
      loadRestoreSlotPreview(
        { emoteAdminService: d.emoteAdminService, emoteSetService: d.emoteSetService },
        {
          trackedChannelName: 'somechannel',
          isActiveSet: true,
          emoteSetId: 'set-1',
          twitchLogin: 'somechannel',
        },
      ),
    );

    expect(result).toBeNull();
  });

  it('resolves to null, not an error, when the per-set preview read fails', async () => {
    const d = deps();
    d.loadEmoteSetPreview.mockReturnValue(throwError(() => new Error('boom')));

    const result = await firstValueFrom(
      loadRestoreSlotPreview(
        { emoteAdminService: d.emoteAdminService, emoteSetService: d.emoteSetService },
        {
          trackedChannelName: null,
          isActiveSet: false,
          emoteSetId: 'set-1',
          twitchLogin: 'accountlogin',
        },
      ),
    );

    expect(result).toBeNull();
  });
});
