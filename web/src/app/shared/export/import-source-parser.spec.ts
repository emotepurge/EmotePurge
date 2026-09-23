import { describe, expect, it } from 'vitest';

import { ExportEnvelope } from './export-envelope';
import { parseImportSource } from './import-source-parser';

function envelope(overrides: Partial<ExportEnvelope<unknown>>): ExportEnvelope<unknown> {
  return {
    source: 'emotepurge',
    kind: 'emote-list',
    formatVersion: 1,
    exportedAt: '2026-09-05T10:00:00Z',
    channelName: 'sensitron',
    withheld: [],
    meta: { sourceEmoteSetId: 'set-1', rowCount: 2, scope: 'visible' },
    rows: [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'Kappa' },
    ],
    ...overrides,
  };
}

describe('parseImportSource', () => {
  it('parses an emote-list export into a file-origin ImportSource', () => {
    const result = parseImportSource(envelope({}), 'emotes.json');

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.source.origin).toEqual({
      kind: 'file',
      fileName: 'emotes.json',
      exportedAt: '2026-09-05T10:00:00Z',
      channelName: 'sensitron',
      envelopeKind: 'emote-list',
    });
    expect(result.source.rows).toEqual([
      { sevenTvEmoteId: '7tv-1', name: 'PogU', imageUrl: null },
      { sevenTvEmoteId: '7tv-2', name: 'Kappa', imageUrl: null },
    ]);
    expect(result.source.duplicatesCollapsed).toBe(0);
    expect(result.source.discardedRows).toBe(0);
  });

  it('sets imageUrl to null for every parsed row — a file never carries one', () => {
    // emote-list-export.ts writes only id and name (the file format is unchanged); a row read
    // back out of a file must say so honestly rather than guess at an image from the id.
    const result = parseImportSource(envelope({}), 'emotes.json');

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.source.rows.every((row) => row.imageUrl === null)).toBe(true);
  });

  it("maps a usage export's emoteName onto ImportRow.name", () => {
    const result = parseImportSource(
      envelope({
        kind: 'usage',
        rows: [
          { sevenTvEmoteId: '7tv-1', emoteName: 'PogU', totalUseCount: 42 },
          { sevenTvEmoteId: '7tv-2', emoteName: 'Kappa', totalUseCount: 1 },
        ],
      }),
      'usage.json',
    );

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.source.rows).toEqual([
      { sevenTvEmoteId: '7tv-1', name: 'PogU', imageUrl: null },
      { sevenTvEmoteId: '7tv-2', name: 'Kappa', imageUrl: null },
    ]);
    expect(result.source.origin.kind === 'file' && result.source.origin.envelopeKind).toBe('usage');
  });

  it('names a voting export instead of calling it "not importable"', () => {
    const result = parseImportSource(envelope({ kind: 'voting' }), 'vote.json');
    expect(result).toEqual({ ok: false, errorKey: 'restore.import.errors.votingExport' });
  });

  // #230: a transfer-run protocol's rows are 7TV mutations already applied or about to be, not an
  // emote list — both stages are rejected by name, never parsed as a copy source.
  it.each(['planned', 'finished'] as const)(
    'names a transfer-run protocol (stage: %s) instead of trying to read it as an emote list',
    (stage) => {
      const result = parseImportSource(
        envelope({ kind: 'transfer-run', meta: { stage } }),
        'transfer.json',
      );
      expect(result).toEqual({ ok: false, errorKey: 'restore.import.errors.transferRun' });
    },
  );

  it('falls back to wrongKind for anything other than emote-list, usage or voting', () => {
    expect(parseImportSource(envelope({ kind: 'purge-run' }), 'x.json')).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
    expect(parseImportSource(envelope({ kind: 'from-the-future' as never }), 'x.json')).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('rejects an unknown format version', () => {
    const result = parseImportSource(envelope({ formatVersion: 2 }), 'x.json');
    expect(result).toEqual({ ok: false, errorKey: 'restore.import.errors.wrongVersion' });
  });

  it('drops a malformed row and dedupes the rest, counting both independently', () => {
    const result = parseImportSource(
      envelope({
        meta: { sourceEmoteSetId: 'set-1', rowCount: 4, scope: 'visible' },
        rows: [
          { sevenTvEmoteId: '7tv-1', name: 'PogU' },
          { sevenTvEmoteId: '', name: 'broken' }, // empty id — discarded
          { sevenTvEmoteId: '7tv-1', name: 'PogU-again' }, // duplicate of row 1 — collapsed
          { sevenTvEmoteId: '7tv-2', name: 'Kappa' },
        ],
      }),
      'x.json',
    );

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.source.rows).toEqual([
      { sevenTvEmoteId: '7tv-1', name: 'PogU', imageUrl: null },
      { sevenTvEmoteId: '7tv-2', name: 'Kappa', imageUrl: null },
    ]);
    expect(result.source.duplicatesCollapsed).toBe(1);
    expect(result.source.discardedRows).toBe(1);
  });

  it('rejects a file whose rows are all malformed', () => {
    const result = parseImportSource(
      envelope({
        meta: { sourceEmoteSetId: 'set-1', rowCount: 2, scope: 'visible' },
        rows: [
          { sevenTvEmoteId: '', name: 'broken' },
          { sevenTvEmoteId: '7tv-1', name: 42 },
        ],
      }),
      'x.json',
    );

    expect(result).toEqual({ ok: false, errorKey: 'restore.import.errors.noRows' });
  });

  it('reports a missing exportedAt/channelName as null, not as a crash', () => {
    const result = parseImportSource(
      envelope({ exportedAt: undefined as never, channelName: undefined as never }),
      'x.json',
    );

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.source.origin).toMatchObject({ exportedAt: null, channelName: null });
  });

  it('counts discardedRows against meta.rowCount when present', () => {
    const result = parseImportSource(
      envelope({
        meta: { sourceEmoteSetId: 'set-1', rowCount: 5, scope: 'visible' },
        rows: [
          { sevenTvEmoteId: '7tv-1', name: 'PogU' },
          { sevenTvEmoteId: '7tv-2', name: 'Kappa' },
          { sevenTvEmoteId: '', name: 'broken' },
        ],
      }),
      'x.json',
    );

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    // 5 claimed, 2 valid -> 3 discarded, even though the raw array itself only holds 3 rows.
    expect(result.source.discardedRows).toBe(3);
  });

  it('falls back to the raw row count when meta.rowCount is absent or not a number', () => {
    const noRowCount = parseImportSource(
      envelope({
        meta: { sourceEmoteSetId: 'set-1', scope: 'visible' },
        rows: [
          { sevenTvEmoteId: '7tv-1', name: 'PogU' },
          { sevenTvEmoteId: '', name: 'broken' },
        ],
      }),
      'x.json',
    );
    expect(noRowCount.ok).toBe(true);
    if (noRowCount.ok) expect(noRowCount.source.discardedRows).toBe(1);

    const wrongType = parseImportSource(
      envelope({
        meta: { sourceEmoteSetId: 'set-1', rowCount: 'five', scope: 'visible' },
        rows: [
          { sevenTvEmoteId: '7tv-1', name: 'PogU' },
          { sevenTvEmoteId: '', name: 'broken' },
        ],
      }),
      'x.json',
    );
    expect(wrongType.ok).toBe(true);
    if (wrongType.ok) expect(wrongType.source.discardedRows).toBe(1);
  });
});
