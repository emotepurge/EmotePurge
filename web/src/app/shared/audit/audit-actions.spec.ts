import { describe, expect, it } from 'vitest';

import {
  ACTION_KEYS,
  CHANNEL_SCOPED_ACTIONS,
  CHANNELLESS_ACTIONS,
  DETAIL_KEYS,
  TARGET_EMOTE_SET_KEYS,
} from './audit-actions';
import de from '../../../../public/i18n/de.json';
import en from '../../../../public/i18n/en.json';

/** Walks a dotted translation key, returning undefined as soon as a segment is missing. */
function lookup(bundle: unknown, key: string): unknown {
  return key
    .split('.')
    .reduce<unknown>(
      (node, segment) =>
        typeof node === 'object' && node !== null
          ? (node as Record<string, unknown>)[segment]
          : undefined,
      bundle,
    );
}

describe('audit action tables', () => {
  it('names all fourteen AuditActions constants', () => {
    // A gap here is the whole point of the lookup: a newly added backend action must show up as a
    // missing entry rather than as a silently generated key.
    expect(Object.keys(ACTION_KEYS)).toHaveLength(14);
  });

  it('excludes the user-scoped actions from the channel-scoped set', () => {
    // A single channel's log can never contain them, so its filter must not offer them.
    expect(CHANNEL_SCOPED_ACTIONS).toHaveLength(12);
    expect(CHANNEL_SCOPED_ACTIONS).not.toContain('user.revokeSessions');
    expect(CHANNEL_SCOPED_ACTIONS).not.toContain('user.invalidateRoleCache');
    expect(CHANNELLESS_ACTIONS.size).toBe(2);
  });

  it.each(Object.entries(ACTION_KEYS))('translates %s in both locales', (_action, key) => {
    expect(lookup(de, key)).toBeTypeOf('string');
    expect(lookup(en, key)).toBeTypeOf('string');
  });

  it.each(Object.entries(DETAIL_KEYS))('translates the %s detail in both locales', (_kind, key) => {
    expect(lookup(de, key)).toBeTypeOf('string');
    expect(lookup(en, key)).toBeTypeOf('string');
  });

  it('covers every detail kind the server can send', () => {
    // Mirrors AuditLogDetail.Kinds — the server whitelist is the source of truth for this list.
    expect(Object.keys(DETAIL_KEYS).sort()).toEqual([
      'emoteCount',
      'importedFromChannel',
      'importedFromFile',
      'importedFromLeaderboard',
      'removedEntries',
      'title',
    ]);
  });

  it.each(Object.values(TARGET_EMOTE_SET_KEYS))(
    'translates the target-set addendum key %s in both locales (spec 8.10)',
    (key) => {
      expect(lookup(de, key)).toBeTypeOf('string');
      expect(lookup(en, key)).toBeTypeOf('string');
    },
  );

  it('translates both leaderboard sort codes in both locales (#148, E9)', () => {
    // Not covered by DETAIL_KEYS itself — `leaderboardSort` is a separate lookup table
    // (`leaderboard.model.ts`, `LEADERBOARD_SORT_LABEL_KEYS`) that `renderDetail` keys into for
    // exactly this one kind.
    for (const code of ['TRENDING_DAILY', 'TOP_ALL_TIME']) {
      expect(lookup(de, `audit.details.leaderboardSort.${code}`)).toBeTypeOf('string');
      expect(lookup(en, `audit.details.leaderboardSort.${code}`)).toBeTypeOf('string');
    }
  });
});
