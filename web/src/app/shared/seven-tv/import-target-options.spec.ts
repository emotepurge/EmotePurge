import { describe, expect, it } from 'vitest';

import { MyChannelDto, MyChannelsResult } from '../../core/channels/channel.model';
import { importTargetOptions } from './import-target-options';

function channel(overrides: Partial<MyChannelDto> & { channelName: string }): MyChannelDto {
  return {
    isBroadcaster: false,
    isModerator: false,
    isSevenTvEditor: false,
    isTracked: true,
    isBotActive: true,
    liveState: 'unknown',
    ...overrides,
  };
}

function result(channels: MyChannelDto[]): MyChannelsResult {
  return {
    helixUnavailable: false,
    reauthRequired: false,
    sevenTvUnavailable: false,
    channels,
    livePolledAtUtc: null,
  };
}

describe('importTargetOptions', () => {
  it('keeps only broadcaster/editor channels, excludes the current channel (normalized), and sorts ordinally', () => {
    const options = importTargetOptions(
      result([
        channel({ channelName: 'zeta', isBroadcaster: true }),
        channel({ channelName: 'alpha', isSevenTvEditor: true }),
        channel({ channelName: 'HandOfBlood', isBroadcaster: true }), // current channel, mixed case
        channel({ channelName: 'modonly', isModerator: true }), // moderator alone does not qualify
      ]),
      'handofblood',
    );

    expect(options).toEqual([
      { channelName: 'alpha', disabled: false },
      { channelName: 'zeta', disabled: false },
    ]);
  });

  it('marks an untracked channel as disabled without dropping it from the list', () => {
    const options = importTargetOptions(
      result([channel({ channelName: 'someone', isBroadcaster: true, isTracked: false })]),
      'me',
    );

    expect(options).toEqual([{ channelName: 'someone', disabled: true }]);
  });

  it('returns an empty list for an empty channel set', () => {
    expect(importTargetOptions(result([]), 'me')).toEqual([]);
  });

  it('keeps insertion order for two channels with the same name (stable sort on a tie)', () => {
    const options = importTargetOptions(
      result([
        channel({ channelName: 'sameuser', isBroadcaster: true, isTracked: true }),
        channel({ channelName: 'sameuser', isSevenTvEditor: true, isTracked: false }),
      ]),
      'me',
    );

    expect(options).toEqual([
      { channelName: 'sameuser', disabled: false },
      { channelName: 'sameuser', disabled: true },
    ]);
  });
});
