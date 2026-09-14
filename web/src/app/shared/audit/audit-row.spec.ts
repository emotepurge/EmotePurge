import { describe, expect, it } from 'vitest';

import { toAuditRows } from './audit-row';
import { AuditLogEntry } from '../../core/audit/audit.model';

function entry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    id: 1,
    occurredAtUtc: '2026-07-31T12:00:00Z',
    actorLogin: 'sensitron',
    action: 'channel.join',
    channelName: 'handofblood',
    targetType: null,
    targetId: null,
    detail: null,
    ...overrides,
  };
}

/** Identity stand-in for `TranslocoService.translate` — every existing kind's `title` is already
 *  displayable text and never runs through it; only `importedFromLeaderboard` does (see the
 *  dedicated `describe` block below), so a fixed dictionary is easier to read there than a stub. */
const IDENTITY_TRANSLATE = (key: string) => key;

describe('toAuditRows', () => {
  it('resolves a known action to its translation key', () => {
    const [row] = toAuditRows(
      [entry({ action: 'voteSession.delete' })],
      'de-DE',
      IDENTITY_TRANSLATE,
    );

    expect(row.actionKey).toBe('audit.actions.voteSessionDelete');
    expect(row.action).toBe('voteSession.delete');
  });

  it('leaves an unknown action without a key but keeps it verbatim', () => {
    // An entry written by a newer backend: showing the raw string beats hiding the row.
    const [row] = toAuditRows(
      [entry({ action: 'channel.somethingNew' })],
      'de-DE',
      IDENTITY_TRANSLATE,
    );

    expect(row.actionKey).toBeNull();
    expect(row.action).toBe('channel.somethingNew');
  });

  it('renders a counting detail with its count parameter', () => {
    const [row] = toAuditRows(
      [entry({ detail: { kind: 'emoteCount', count: 12, text: null } })],
      'de-DE',
      IDENTITY_TRANSLATE,
    );

    expect(row.detail).toEqual({ key: 'audit.details.emoteCount', params: { count: 12 } });
  });

  it('renders a naming detail with its title parameter', () => {
    const [row] = toAuditRows(
      [entry({ detail: { kind: 'title', count: null, text: 'Sommer-Purge' } })],
      'de-DE',
      IDENTITY_TRANSLATE,
    );

    expect(row.detail).toEqual({
      key: 'audit.details.title',
      params: { title: 'Sommer-Purge' },
    });
  });

  it('renders an import-from-channel detail with both count and title parameters', () => {
    // The only kind that carries count and text at once (R1) — the other three kinds set exactly
    // one of the two fields, so this is the case that proves renderDetail passes both through.
    const [row] = toAuditRows(
      [entry({ detail: { kind: 'importedFromChannel', count: 5, text: 'sourcechannel' } })],
      'de-DE',
      IDENTITY_TRANSLATE,
    );

    expect(row.detail).toEqual({
      key: 'audit.details.importedFromChannel',
      params: { count: 5, title: 'sourcechannel' },
    });
  });

  it('renders an import-from-file detail with only its count parameter', () => {
    const [row] = toAuditRows(
      [entry({ detail: { kind: 'importedFromFile', count: 7, text: null } })],
      'de-DE',
      IDENTITY_TRANSLATE,
    );

    expect(row.detail).toEqual({
      key: 'audit.details.importedFromFile',
      params: { count: 7 },
    });
  });

  it('drops a detail kind this build has no label for', () => {
    // Not a safety decision — the server already whitelisted the payload. This build is simply
    // older than the backend, and the row keeps its action and actor.
    const [row] = toAuditRows(
      [entry({ detail: { kind: 'somethingNew', count: 3, text: null } })],
      'de-DE',
      IDENTITY_TRANSLATE,
    );

    expect(row.detail).toBeNull();
  });

  it('formats the timestamp in the given locale', () => {
    const rows = toAuditRows(
      [entry({ occurredAtUtc: '2026-07-31T12:00:00Z' })],
      'en-US',
      IDENTITY_TRANSLATE,
    );

    // Only the shape is asserted: the exact string depends on the runtime's timezone.
    expect(rows[0].timestamp).toMatch(/\d/);
    expect(rows[0].occurredAtUtc).toBe('2026-07-31T12:00:00Z');
  });

  // #148, E9: the server writes a language-neutral sort code here, never text — this is the one
  // kind whose `text` this function translates itself instead of passing through.
  describe('importedFromLeaderboard (#148, E9)', () => {
    const TRANSLATE_LEADERBOARD_SORT = (key: string) =>
      ({
        'audit.details.leaderboardSort.TRENDING_DAILY': '7TV Trend heute',
        'audit.details.leaderboardSort.TOP_ALL_TIME': '7TV Top insgesamt',
      })[key] ?? key;

    it('translates the sort code into the title parameter', () => {
      const [row] = toAuditRows(
        [entry({ detail: { kind: 'importedFromLeaderboard', count: 12, text: 'TRENDING_DAILY' } })],
        'de-DE',
        TRANSLATE_LEADERBOARD_SORT,
      );

      expect(row.detail).toEqual({
        key: 'audit.details.importedFromLeaderboard',
        params: { count: 12, title: '7TV Trend heute' },
      });
    });

    it('translates the other sort code too', () => {
      const [row] = toAuditRows(
        [entry({ detail: { kind: 'importedFromLeaderboard', count: 3, text: 'TOP_ALL_TIME' } })],
        'de-DE',
        TRANSLATE_LEADERBOARD_SORT,
      );

      expect(row.detail?.params['title']).toBe('7TV Top insgesamt');
    });

    it('drops the whole detail for a code outside the allowlist instead of crashing', () => {
      // Forward-compatibility case: a future backend writes a third sort this build does not know.
      // Same degradation as an unrecognized `kind` — no crash, no raw code, no missing-key
      // placeholder, the row simply keeps its action and actor.
      const [row] = toAuditRows(
        [entry({ detail: { kind: 'importedFromLeaderboard', count: 3, text: 'TRENDING_WEEKLY' } })],
        'de-DE',
        TRANSLATE_LEADERBOARD_SORT,
      );

      expect(row.detail).toBeNull();
    });

    it('drops the whole detail when the server sent no code at all', () => {
      const [row] = toAuditRows(
        [entry({ detail: { kind: 'importedFromLeaderboard', count: 3, text: null } })],
        'de-DE',
        TRANSLATE_LEADERBOARD_SORT,
      );

      expect(row.detail).toBeNull();
    });
  });
});
