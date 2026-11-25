import { describe, expect, it } from 'vitest';

import { BotAccountState } from '../../core/admin/admin.model';
import { botTokenExpiryNotice, botTokenStatus, canSubmitBotToken } from './admin-bot-account-view';

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

  it('treats an expiry at or before now as expired although the server still says valid', () => {
    const now = new Date('2026-11-24T12:00:00Z');
    const valid = state({ status: 'valid', expiresInDays: 0 });
    expect(botTokenExpiryNotice({ ...valid, expiresAtUtc: '2026-11-24T12:00:00Z' }, now)).toBe(
      'expired',
    );
    expect(botTokenExpiryNotice({ ...valid, expiresAtUtc: '2026-11-24T11:59:59Z' }, now)).toBe(
      'expired',
    );
    expect(botTokenExpiryNotice({ ...valid, expiresAtUtc: '2026-11-24T12:00:01Z' }, now)).toBe(
      'soon',
    );
  });

  it('shows the effective status: expired past the expiry, the server status otherwise', () => {
    const now = new Date('2026-11-24T12:00:00Z');
    expect(botTokenStatus(state({ expiresAtUtc: '2026-11-24T11:00:00Z' }), now)).toBe('expired');
    expect(botTokenStatus(state({ expiresAtUtc: '2026-11-25T11:00:00Z' }), now)).toBe('valid');
    expect(botTokenStatus(state({ expiresAtUtc: null }), now)).toBe('valid');
    expect(
      botTokenStatus(
        state({ tokenPresent: false, status: 'none', expiresAtUtc: '2020-01-01T00:00:00Z' }),
        now,
      ),
    ).toBe('none');
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
