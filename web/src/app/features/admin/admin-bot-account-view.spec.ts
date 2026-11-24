import { describe, expect, it } from 'vitest';

import { BotAccountState } from '../../core/admin/admin.model';
import { botTokenExpiryNotice, canSubmitBotToken } from './admin-bot-account-view';

function state(overrides: Partial<BotAccountState>): BotAccountState {
  return {
    tokenPresent: true,
    status: 'valid',
    expiresAtUtc: null,
    expiresInDays: 30,
    botSevenTvUserId: null,
    lastVerifiedAtUtc: null,
    lastErrorCode: null,
    killSwitch: false,
    pausedUntilUtc: null,
    wishes: {
      queue: { adds: 0, confirms: 0, removals: 0 },
      botChannels: 0,
      suspendedChannels: 0,
      openRepairChecks: 0,
    },
    ...overrides,
  };
}

describe('botTokenExpiryNotice', () => {
  it('stays quiet above seven days', () => {
    expect(botTokenExpiryNotice(state({ expiresInDays: 8 }))).toBeNull();
  });

  it('warns from seven days on', () => {
    expect(botTokenExpiryNotice(state({ expiresInDays: 7 }))).toBe('soon');
    expect(botTokenExpiryNotice(state({ expiresInDays: 0 }))).toBe('soon');
  });

  it('reports an expired status regardless of the day count', () => {
    expect(botTokenExpiryNotice(state({ status: 'expired', expiresInDays: 0 }))).toBe('expired');
  });

  it('says nothing without a token', () => {
    expect(botTokenExpiryNotice(state({ tokenPresent: false, expiresInDays: null }))).toBeNull();
  });
});

describe('canSubmitBotToken', () => {
  it('locks on an empty or blank field', () => {
    expect(canSubmitBotToken('', false)).toBe(false);
    expect(canSubmitBotToken('   ', false)).toBe(false);
  });

  it('locks while a request is in flight', () => {
    expect(canSubmitBotToken('a.b.c', true)).toBe(false);
  });

  it('unlocks with a value and no request', () => {
    expect(canSubmitBotToken('a.b.c', false)).toBe(true);
  });
});
