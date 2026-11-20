import { describe, expect, it } from 'vitest';

import type { WishRewardLearning } from '../../core/wishes/wish.model';
import {
  captureGapReasonKey,
  captureLooksStopped,
  captureStampRefreshDue,
  confirmRewardBody,
  formatRemaining,
  learningPhase,
  learningStartBlock,
  rewardEditBody,
  type WishRewardLearningWithCandidate,
} from './wish-reward-learning';

const NOW = new Date('2026-11-19T12:00:00Z');

function learning(over: Partial<WishRewardLearning> = {}): WishRewardLearning {
  return {
    sessionId: 'session-a',
    startedAtUtc: '2026-11-19T11:55:00Z',
    expiresAtUtc: '2026-11-19T12:05:00Z',
    candidate: null,
    ...over,
  };
}

const CANDIDATE = {
  candidateId: 'candidate-1',
  text: 'https://7tv.app/emotes/01FDGSXVJG0004RTSM3QHRP7QN',
  login: 'viewer_one',
  seenAtUtc: '2026-11-19T11:58:00Z',
};

describe('learningPhase (transition table)', () => {
  it.each<[string, WishRewardLearning | null, boolean, string]>([
    ['no session', null, false, 'idle'],
    ['no session after one ran out', null, true, 'expired'],
    ['running, nothing seen', learning(), false, 'waiting'],
    ['running, a redemption seen', learning({ candidate: CANDIDATE }), false, 'candidate'],
    ['ran out while waiting', learning({ expiresAtUtc: '2026-11-19T12:00:00Z' }), false, 'expired'],
    [
      'ran out with a candidate shown',
      learning({ expiresAtUtc: '2026-11-19T11:59:59Z', candidate: CANDIDATE }),
      false,
      'expired',
    ],
    ['a new session clears an earlier expiry', learning(), true, 'waiting'],
  ])('%s -> %s', (_case, row, expiredSeen, kind) => {
    expect(learningPhase(row, NOW, expiredSeen).kind).toBe(kind);
  });

  it('carries the time left and the candidate as the server sent them', () => {
    const phase = learningPhase(learning({ candidate: CANDIDATE }), NOW, false);

    expect(phase).toEqual({
      kind: 'candidate',
      learning: learning({ candidate: CANDIDATE }),
      remainingMs: 5 * 60 * 1000,
    });
  });

  it('never counts an unreadable end time as run out and leaves the countdown out', () => {
    expect(learningPhase(learning({ expiresAtUtc: 'garbage' }), NOW, false)).toEqual({
      kind: 'waiting',
      learning: learning({ expiresAtUtc: 'garbage' }),
      remainingMs: null,
    });
  });
});

describe('learningStartBlock', () => {
  const rewards = (count: number) =>
    Array.from({ length: count }, (_, i) => ({
      id: i,
      label: `R${i}`,
      defaultDurationDays: 14,
      isActive: true,
      createdAtUtc: '2026-11-01T00:00:00Z',
      lastRedemptionSeenAtUtc: null,
    }));

  it.each<[string, boolean, boolean, number, boolean, string | null]>([
    ['bound, may edit, room left', true, true, 9, false, null],
    ['unbound channel row', false, true, 0, false, 'unbound'],
    ['the server says read-only', true, false, 0, false, 'readOnly'],
    ['a write answered restricted', true, true, 0, true, 'readOnly'],
    ['ten rewards already', true, true, 10, false, 'limit'],
    ['unbound wins over read-only', false, false, 10, false, 'unbound'],
  ])('%s -> %s', (_case, channelBound, canEdit, count, restricted, block) => {
    expect(
      learningStartBlock({ channelBound, canEdit, rewards: rewards(count), limit: 10 }, restricted),
    ).toBe(block);
  });
});

describe('formatRemaining', () => {
  it.each([
    [600_000, '10:00'],
    [572_400, '9:33'],
    [1, '0:01'],
    [0, '0:00'],
    [-5, '0:00'],
  ])('%i ms -> %s', (ms, text) => {
    expect(formatRemaining(ms)).toBe(text);
  });
});

describe('confirmRewardBody', () => {
  it('names exactly the session and candidate that were shown', () => {
    const shown = learning({ candidate: CANDIDATE }) as WishRewardLearningWithCandidate;

    expect(confirmRewardBody(shown, 'Emote-Wunsch', 21)).toEqual({
      sessionId: 'session-a',
      candidateId: 'candidate-1',
      label: 'Emote-Wunsch',
      defaultDurationDays: 21,
    });
  });
});

describe('rewardEditBody', () => {
  const reward = { label: 'Emote-Wunsch', defaultDurationDays: 14 };

  it('sends only the fields that changed', () => {
    expect(rewardEditBody(reward, 'Neu', 14)).toEqual({ label: 'Neu' });
    expect(rewardEditBody(reward, 'Emote-Wunsch', 30)).toEqual({ defaultDurationDays: 30 });
    expect(rewardEditBody(reward, 'Neu', 30)).toEqual({ label: 'Neu', defaultDurationDays: 30 });
  });

  it('is null when nothing changed', () => {
    expect(rewardEditBody(reward, 'Emote-Wunsch', 14)).toBeNull();
  });
});

describe('captureLooksStopped', () => {
  it.each<[string | null, boolean]>([
    ['2026-11-19T11:59:00Z', false],
    ['2026-11-19T11:57:00Z', false],
    ['2026-11-19T11:56:59Z', true],
    ['2026-11-19T12:10:00Z', false],
    [null, true],
    ['garbage', true],
  ])('stamp %s -> warn %s', (stamp, warn) => {
    expect(captureLooksStopped(stamp, NOW)).toBe(warn);
  });
});

describe('captureStampRefreshDue', () => {
  const longAgo = new Date('2026-11-19T11:50:00Z');

  it.each<[string | null, boolean]>([
    // Fresh: two minutes and more to the warning.
    ['2026-11-19T11:59:00Z', false],
    ['2026-11-19T11:58:01Z', false],
    // Within a minute of the warning, past it, missing or unreadable: read again.
    ['2026-11-19T11:58:00Z', true],
    ['2026-11-19T11:50:00Z', true],
    [null, true],
    ['garbage', true],
    // From the future (clock skew): fresh.
    ['2026-11-19T12:10:00Z', false],
  ])('stamp %s -> read again %s', (stamp, due) => {
    expect(captureStampRefreshDue(stamp, NOW, longAgo)).toBe(due);
  });

  it('waits a minute after the last read, whatever the stamp says', () => {
    expect(captureStampRefreshDue(null, NOW, new Date('2026-11-19T11:59:01Z'))).toBe(false);
    expect(captureStampRefreshDue(null, NOW, new Date('2026-11-19T11:59:00Z'))).toBe(true);
  });
});

describe('captureGapReasonKey', () => {
  it('names the three known causes and reads anything else as unknown', () => {
    expect(captureGapReasonKey('disconnect')).toBe(
      'wishes.settings.rewards.gaps.reason.disconnect',
    );
    expect(captureGapReasonKey('worker_restart')).toBe(
      'wishes.settings.rewards.gaps.reason.worker_restart',
    );
    expect(captureGapReasonKey('not_joined')).toBe(
      'wishes.settings.rewards.gaps.reason.not_joined',
    );
    expect(captureGapReasonKey('cosmic_ray')).toBe('wishes.settings.rewards.gaps.reason.unknown');
  });
});
