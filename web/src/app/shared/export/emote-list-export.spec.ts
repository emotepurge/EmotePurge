import { describe, expect, it } from 'vitest';

import { ImportRow } from '../../core/seven-tv/import-source';
import { buildEmoteListEnvelope, emoteListFilename, emoteListJson } from './emote-list-export';

const ROWS: ImportRow[] = [
  { sevenTvEmoteId: '7tv-1', name: 'PogU', imageUrl: 'https://cdn.7tv.app/emote/7tv-1/2x.webp' },
  { sevenTvEmoteId: '7tv-2', name: 'Kappa', imageUrl: null },
];

describe('buildEmoteListEnvelope', () => {
  it('wraps the rows in the shared envelope with source set and scope in meta', () => {
    const envelope = buildEmoteListEnvelope({
      channelName: 'sensitron',
      emoteSetId: 'set-1',
      scope: 'selection',
      rows: ROWS,
    });

    expect(envelope.source).toBe('emotepurge');
    expect(envelope.kind).toBe('emote-list');
    expect(envelope.channelName).toBe('sensitron');
    expect(envelope.meta).toEqual({ sourceEmoteSetId: 'set-1', rowCount: 2, scope: 'selection' });
    // The written file carries the same three fields as the in-memory `ImportRow[]` since #230 —
    // including a `null` image url when the source row itself had none.
    expect(envelope.rows).toEqual(
      ROWS.map(({ sevenTvEmoteId, name, imageUrl }) => ({ sevenTvEmoteId, name, imageUrl })),
    );
  });

  it('emits rows with exactly sevenTvEmoteId, name and imageUrl — no emoteId, no usage figures', () => {
    const envelope = buildEmoteListEnvelope({
      channelName: 'sensitron',
      emoteSetId: 'set-1',
      scope: 'visible',
      rows: ROWS,
    });

    for (const row of envelope.rows) {
      expect(Object.keys(row).sort()).toEqual(['imageUrl', 'name', 'sevenTvEmoteId']);
    }
  });

  it('contains no token anywhere', () => {
    const envelope = buildEmoteListEnvelope({
      channelName: 'sensitron',
      emoteSetId: 'set-1',
      scope: 'visible',
      rows: ROWS,
    });
    expect(emoteListJson(envelope)).not.toMatch(/token|authorization|bearer/i);
  });
});

describe('emoteListFilename', () => {
  it('stamps channel and the export date, not the time', () => {
    expect(emoteListFilename('Sensitron', '2026-09-05T10:05:12Z')).toBe(
      'emotepurge_sensitron_emote-list_2026-09-05.json',
    );
  });
});
