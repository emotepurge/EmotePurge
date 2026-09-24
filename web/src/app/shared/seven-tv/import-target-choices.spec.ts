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
    ownerSevenTvUserId: 'owner-1',
    // Every existing test below predates `editable` and expects every mocked set to be a valid
    // target — spec F9's "Default true" for the same reason.
    editable: true,
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
    sevenTvUserId: 'owner-1',
    ...overrides,
  };
}

function response(accounts: EmoteSetTargetAccount[]): EmoteSetTargetsResponse {
  return { accounts, sevenTvUnavailable: false };
}

/** The translated fallback `resolveOwnerLabel` uses only when neither a display name nor a login is
 *  present — every test below gives at least one of those two, so this is passed through purely to
 *  satisfy the signature, not because any assertion reads it. See `resolveOwnerLabel`'s own suite
 *  further down for the case where it actually surfaces. */
const UNKNOWN_OWNER = 'unbekannter Besitzer';

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
      UNKNOWN_OWNER,
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
      UNKNOWN_OWNER,
    );

    expect(result.tracked.map((g) => g.twitchChannelId)).toEqual(['1', '2']);
  });

  it('classifies an account by trackedChannelName alone, exposing it as channelName', () => {
    const result = importTargetChoices(
      response([account({ twitchChannelId: '1', trackedChannelName: 'handofblood' })]),
      'source-set',
      UNKNOWN_OWNER,
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
      UNKNOWN_OWNER,
    );

    const [active, halloween] = result.tracked[0].sets;
    expect(active).toMatchObject({ disabled: true, disabledReason: 'isSourceSet' });
    expect(halloween).toMatchObject({ disabled: false, disabledReason: null });
  });

  // PERSONAL is deliberately absent from this table — it is filtered out of the offer entirely
  // (addendum 39) rather than rendered disabled, so it gets its own describe block below instead of
  // sharing this "still offered, but disabled" case with GLOBAL/SPECIAL.
  it.each(['GLOBAL', 'SPECIAL'])('disables a %s-kind set with reason "notNormalKind"', (kind) => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          sets: [set({ id: 'set-x', kind })],
        }),
      ]),
      'source-set',
      UNKNOWN_OWNER,
    );

    expect(result.tracked[0].sets[0]).toMatchObject({
      disabled: true,
      disabledReason: 'notNormalKind',
    });
  });

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
      UNKNOWN_OWNER,
    );

    expect(result.tracked[0].sets[0]).toMatchObject({ disabled: false, disabledReason: null });
  });

  // #253/T4, spec 5.8/E19, AK 29: `editable` moved from something the picker computed itself into
  // something the target list already answers — the picker now just reads it.
  it('disables a set with editable: false, with reason "notEditable"', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          sets: [set({ id: 'set-x', editable: false })],
        }),
      ]),
      'source-set',
      UNKNOWN_OWNER,
    );

    expect(result.tracked[0].sets[0]).toMatchObject({
      disabled: true,
      disabledReason: 'notEditable',
    });
  });

  it('prefers "notNormalKind" over "notEditable" for a set that is both', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          sets: [set({ id: 'set-x', kind: 'GLOBAL', editable: false })],
        }),
      ]),
      'source-set',
      UNKNOWN_OWNER,
    );

    expect(result.tracked[0].sets[0].disabledReason).toBe('notNormalKind');
  });

  it('prefers "isSourceSet" over "notNormalKind" for the implausible case where both apply', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          // GLOBAL, not PERSONAL: a PERSONAL set is filtered out of the offer before this ternary
          // ever runs (see the PERSONAL describe block below), so it can no longer stand in for
          // "some non-NORMAL kind" in this precedence test — GLOBAL still can.
          sets: [set({ id: 'set-x', kind: 'GLOBAL' })],
        }),
      ]),
      'set-x',
      UNKNOWN_OWNER,
    );

    expect(result.tracked[0].sets[0].disabledReason).toBe('isSourceSet');
  });

  it('passes isActive through unchanged, for the "aktiv" label', () => {
    const result = importTargetChoices(
      response([
        account({
          twitchChannelId: '1',
          trackedChannelName: 'handofblood',
          sets: [set({ id: 'set-x', isActive: true })],
        }),
      ]),
      'source-set',
      UNKNOWN_OWNER,
    );

    expect(result.tracked[0].sets[0]).toMatchObject({ isActive: true });
  });

  // Addendum 39 (#217): PERSONAL sets are hidden from the target picker entirely, not shown
  // disabled — a personal 7TV set holds a handful of emotes at most and is not a plausible target.
  describe('PERSONAL sets are hidden entirely (addendum 39, #217)', () => {
    it('filters a PERSONAL set out of the offered list, leaving the NORMAL sibling', () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            trackedChannelName: 'handofblood',
            sets: [
              set({
                id: 'set-personal',
                name: 'Personal Emotes',
                kind: 'PERSONAL',
                isPersonal: true,
              }),
              set({ id: 'set-main', name: 'Main', kind: 'NORMAL' }),
            ],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.tracked[0].sets.map((s) => s.emoteSetId)).toEqual(['set-main']);
    });

    it('sets noUsableSets when every set on the account is PERSONAL (filtered down to zero, not a read failure)', () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            trackedChannelName: 'handofblood',
            sets: [set({ id: 'set-personal', kind: 'PERSONAL', isPersonal: true })],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.tracked[0]).toMatchObject({
        sets: [],
        noUsableSets: true,
        setsUnavailable: false,
      });
    });

    it('sets noUsableSets when the account genuinely has no sets at all', () => {
      const result = importTargetChoices(
        response([account({ twitchChannelId: '1', trackedChannelName: 'handofblood', sets: [] })]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.tracked[0]).toMatchObject({ noUsableSets: true, setsUnavailable: false });
    });

    it('does not set noUsableSets when at least one non-PERSONAL set remains', () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            trackedChannelName: 'handofblood',
            sets: [set({ id: 'set-main', kind: 'NORMAL' })],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.tracked[0].noUsableSets).toBe(false);
    });

    it('does not set noUsableSets when the empty list is a read failure (setsUnavailable) — distinct notices', () => {
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
        UNKNOWN_OWNER,
      );

      expect(result.tracked[0]).toMatchObject({ setsUnavailable: true, noUsableSets: false });
    });

    it("nulls activeEmoteSetId when the account's reported active set is itself PERSONAL", () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            trackedChannelName: 'handofblood',
            activeEmoteSetId: 'set-personal',
            sets: [
              set({ id: 'set-personal', kind: 'PERSONAL', isPersonal: true, isActive: true }),
              set({ id: 'set-main', kind: 'NORMAL' }),
            ],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.tracked[0].activeEmoteSetId).toBeNull();
    });

    it('keeps activeEmoteSetId when the reported active set is not PERSONAL', () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            trackedChannelName: 'handofblood',
            activeEmoteSetId: 'set-main',
            sets: [set({ id: 'set-main', kind: 'NORMAL', isActive: true })],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.tracked[0].activeEmoteSetId).toBe('set-main');
    });
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
      UNKNOWN_OWNER,
    );

    expect(result.untracked[0].sets[0]).toMatchObject({
      setName: 'Halloween',
      ownerDisplayName: 'Stranger',
    });
  });

  it('carries isOwnAccount through per account group, for the picker preselection to key on', () => {
    const result = importTargetChoices(
      response([
        account({ twitchChannelId: '1', trackedChannelName: 'own', isOwnAccount: true }),
        account({ twitchChannelId: '2', trackedChannelName: 'editorof', isOwnAccount: false }),
      ]),
      'source-set',
      UNKNOWN_OWNER,
    );

    expect(result.tracked).toEqual([
      expect.objectContaining({ twitchChannelId: '1', isOwnAccount: true }),
      expect.objectContaining({ twitchChannelId: '2', isOwnAccount: false }),
    ]);
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
      UNKNOWN_OWNER,
    );

    expect(result.tracked).toEqual([expect.objectContaining({ setsUnavailable: true, sets: [] })]);
  });

  it('returns empty groups for an empty account list', () => {
    expect(importTargetChoices(response([]), 'source-set', UNKNOWN_OWNER)).toEqual({
      tracked: [],
      untracked: [],
    });
  });

  // Codex round 3 P2: "Provide a label when the owner display name is absent" — 7TV intentionally
  // omits ownerDisplayName for an account with no owner.mainConnection (E7), and this transform is
  // the one place that fallback is decided (import-target-dialog.ts's confirmUntracked banner, the
  // copy confirm dialog and the progress section all read the already-resolved field instead of
  // each inventing their own `?? ''`).
  describe('owner label fallback (Codex round 3 P2)', () => {
    it('keeps a present ownerDisplayName unchanged — the login never replaces it', () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            twitchLogin: 'stranger',
            trackedChannelName: null,
            sets: [set({ id: 'set-x', ownerDisplayName: 'RealDisplayName' })],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.untracked[0].sets[0].ownerDisplayName).toBe('RealDisplayName');
    });

    it('falls back to the account twitchLogin when ownerDisplayName is null', () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            twitchLogin: 'stranger',
            trackedChannelName: null,
            sets: [set({ id: 'set-x', ownerDisplayName: null })],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.untracked[0].sets[0].ownerDisplayName).toBe('stranger');
    });

    it('treats a blank or whitespace-only ownerDisplayName the same as null', () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            twitchLogin: 'stranger',
            trackedChannelName: null,
            sets: [
              set({ id: 'set-blank', ownerDisplayName: '' }),
              set({ id: 'set-ws', ownerDisplayName: '   ' }),
            ],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.untracked[0].sets[0].ownerDisplayName).toBe('stranger');
      expect(result.untracked[0].sets[1].ownerDisplayName).toBe('stranger');
    });

    it('falls back to the neutral unknown-owner label when neither a display name nor a login is present (the case 6.2 says should not happen)', () => {
      const result = importTargetChoices(
        response([
          account({
            twitchChannelId: '1',
            twitchLogin: '',
            trackedChannelName: null,
            sets: [set({ id: 'set-x', ownerDisplayName: null })],
          }),
        ]),
        'source-set',
        UNKNOWN_OWNER,
      );

      expect(result.untracked[0].sets[0].ownerDisplayName).toBe(UNKNOWN_OWNER);
    });
  });
});
