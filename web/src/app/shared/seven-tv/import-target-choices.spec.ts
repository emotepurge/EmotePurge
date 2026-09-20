import { describe, expect, it } from 'vitest';

import {
  EmoteSetTargetAccount,
  EmoteSetTargetsResponse,
} from '../../core/seven-tv/seven-tv-emote-set.model';
import { importTargetChoices } from './import-target-choices';

function set(
  overrides: Partial<EmoteSetTargetAccount['sets'][number]> & { id: string },
): EmoteSetTargetAccount['sets'][number] {
  return {
    name: 'Main',
    capacity: 250,
    kind: 'NORMAL',
    isActive: false,
    isPersonal: false,
    ownerDisplayName: 'SomeOwner',
    ...overrides,
  };
}

function account(
  overrides: Partial<EmoteSetTargetAccount> & { twitchChannelId: string },
): EmoteSetTargetAccount {
  return {
    twitchLogin: 'someone',
    isOwnAccount: false,
    trackedChannelName: null,
    activeEmoteSetId: null,
    sets: [],
    setsUnavailable: false,
    ...overrides,
  };
}

function response(accounts: EmoteSetTargetAccount[]): EmoteSetTargetsResponse {
  return { accounts, sevenTvUnavailable: false };
}

describe('importTargetChoices', () => {
  it('reorders tracked accounts above untracked ones, regardless of the API response order', () => {
    const result = importTargetChoices(
      response([
        account({ twitchChannelId: '1', twitchLogin: 'untrackedone', trackedChannelName: null }),
        account({
          twitchChannelId: '2',
          twitchLogin: 'trackedone',
          trackedChannelName: 'trackedone',
        }),
      ]),
      'source-set',
    );

    expect(result.tracked.map((g) => g.twitchChannelId)).toEqual(['2']);
    expect(result.untracked.map((g) => g.twitchChannelId)).toEqual(['1']);
  });

  it('keeps each side in the order the API already returned it (a stable partition, not a re-sort)', () => {
    const result = importTargetChoices(
      response([
        account({ twitchChannelId: '1', trackedChannelName: 'zulu' }),
        account({ twitchChannelId: '2', trackedChannelName: 'alpha' }),
      ]),
      'source-set',
    );

    expect(result.tracked.map((g) => g.twitchChannelId)).toEqual(['1', '2']);
  });

  it('classifies an account by trackedChannelName alone, exposing it as channelName', () => {
    const result = importTargetChoices(
      response([account({ twitchChannelId: '1', trackedChannelName: 'handofblood' })]),
      'source-set',
    );

    expect(result.tracked).toEqual([
      expect.objectContaining({ channelName: 'handofblood', isTracked: true }),
    ]);
  });

  it('disables only the set matching sourceEmoteSetId, with reason "isSourceSet" — the rest of the same account stays selectable', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          sets: [
            set({ id: 'set-active', isActive: true }),
            set({ id: 'set-halloween', name: 'Halloween' }),
          ],
        }),
      ]),
      'set-active',
    );

    const [active, halloween] = result.tracked[0].sets;
    expect(active).toMatchObject({ disabled: true, disabledReason: 'isSourceSet' });
    expect(halloween).toMatchObject({ disabled: false, disabledReason: null });
  });

  it.each(['PERSONAL', 'GLOBAL', 'SPECIAL'])(
    'disables a %s-kind set with reason "notNormalKind"',
    (kind) => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            trackedChannelName: 'handofblood',
            sets: [set({ id: 'set-x', kind })],
          }),
        ]),
        'source-set',
      );

      expect(result.tracked[0].sets[0]).toMatchObject({
        disabled: true,
        disabledReason: 'notNormalKind',
      });
    },
  );

  it('leaves a NORMAL-kind set enabled', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          sets: [set({ id: 'set-x', kind: 'NORMAL' })],
        }),
      ]),
      'source-set',
    );

    expect(result.tracked[0].sets[0]).toMatchObject({ disabled: false, disabledReason: null });
  });

  it('prefers "isSourceSet" over "notNormalKind" for the implausible case where both apply', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          sets: [set({ id: 'set-x', kind: 'PERSONAL' })],
        }),
      ]),
      'set-x',
    );

    expect(result.tracked[0].sets[0].disabledReason).toBe('isSourceSet');
  });

  it('passes isActive and isPersonal through unchanged, for the "aktiv"/"persönliches Set" labels', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          sets: [set({ id: 'set-x', isActive: true, isPersonal: true, kind: 'PERSONAL' })],
        }),
      ]),
      'source-set',
    );

    expect(result.tracked[0].sets[0]).toMatchObject({ isActive: true, isPersonal: true });
  });

  it('carries ownerDisplayName and setName per set, for the eventual ImportTargetChoice', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: null,
          twitchLogin: 'stranger',
          sets: [set({ id: 'set-x', name: 'Halloween', ownerDisplayName: 'Stranger' })],
        }),
      ]),
      'source-set',
    );

    expect(result.untracked[0].sets[0]).toMatchObject({
      setName: 'Halloween',
      ownerDisplayName: 'Stranger',
    });
  });

  it('carries setsUnavailable through per account, without dropping the account', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          setsUnavailable: true,
          sets: [],
        }),
      ]),
      'source-set',
    );

    expect(result.tracked).toEqual([expect.objectContaining({ setsUnavailable: true, sets: [] })]);
  });

  it('returns empty groups for an empty account list', () => {
    expect(importTargetChoices(response([]), 'source-set')).toEqual({ tracked: [], untracked: [] });
  });
});
