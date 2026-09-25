import { describe, expect, it } from 'vitest';

import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import {
  PURGE_RUN_FORMAT_VERSION,
  buildPurgeRunProtocol,
  parsePurgeRunProtocol,
  purgeRunCsv,
  purgeRunFilename,
  purgeRunJson,
} from './purge-run-export';

const ITEMS: RunQueueItem[] = [
  {
    key: 'i1',
    emoteId: 'i1',
    sevenTvEmoteId: '7tv-1',
    name: 'PogU',
    status: 'done',
    completedSteps: 1,
    failedStep: null,
  },
  {
    key: 'i2',
    emoteId: 'i2',
    sevenTvEmoteId: '7tv-2',
    name: 'KEKW',
    status: 'failed',
    completedSteps: 0,
    failedStep: 0,
    errorMessage: 'boom',
  },
  {
    key: 'i3',
    emoteId: 'i3',
    sevenTvEmoteId: '7tv-3',
    name: 'catJAM',
    status: 'cancelled',
    completedSteps: 0,
    failedStep: null,
  },
];

function protocol() {
  return buildPurgeRunProtocol({
    channelName: 'sensitron',
    emoteSetId: 'set-1',
    startedAt: Date.parse('2026-08-02T10:00:00Z'),
    finishedAt: Date.parse('2026-08-02T10:05:00Z'),
    items: ITEMS,
  });
}

describe('buildPurgeRunProtocol', () => {
  it('wraps the run in the shared envelope with counts and set id', () => {
    const proto = protocol();
    expect(proto.source).toBe('emotepurge');
    expect(proto.kind).toBe('purge-run');
    expect(proto.channelName).toBe('sensitron');
    expect(proto.meta.emoteSetId).toBe('set-1');
    expect(proto.meta.counts).toEqual({ requested: 3, succeeded: 1, failed: 1, cancelled: 1 });
    expect(proto.rows).toHaveLength(3);
    expect(proto.rows[1].errorMessage).toBe('boom');
  });

  it('contains no token anywhere', () => {
    expect(purgeRunJson(protocol())).not.toMatch(/token|authorization|bearer/i);
  });

  // #200 K5 finding C: the row shape changed with K5 (nullable emoteId, aliases), so this kind
  // stamps its own version rather than the shared envelope default — a reader that predates the
  // change must refuse a file in the new shape, not parse it silently short.
  it('stamps its own formatVersion, independent of the shared envelope default', () => {
    expect(protocol().formatVersion).toBe(PURGE_RUN_FORMAT_VERSION);
    expect(PURGE_RUN_FORMAT_VERSION).toBe(2);
  });

  // Spec #200, F3/AK 68: a row without a local emote is written, never dropped — a missing row
  // would be a deletion with no way back.
  it('writes a row without an emoteId with emoteId null and counts it', () => {
    const proto = buildPurgeRunProtocol({
      channelName: 'sensitron',
      emoteSetId: 'set-1',
      startedAt: 0,
      finishedAt: 1,
      items: [
        ITEMS[0],
        {
          key: '7tv-live',
          sevenTvEmoteId: '7tv-live',
          name: 'LiveOnly',
          status: 'done',
          completedSteps: 1,
          failedStep: null,
        },
      ],
    });
    expect(proto.rows.map((row) => row.emoteId)).toEqual(['i1', null]);
    expect(proto.meta.counts.succeeded).toBe(2);
  });

  it('writes every alias of a duplicate cell, and the name alone for a single entry', () => {
    const proto = buildPurgeRunProtocol({
      channelName: 'sensitron',
      emoteSetId: 'set-1',
      startedAt: 0,
      finishedAt: 1,
      items: [
        { ...ITEMS[0], aliases: ['PogU', 'PogU2'] },
        {
          key: '7tv-4',
          sevenTvEmoteId: '7tv-4',
          name: 'Solo',
          status: 'done',
          completedSteps: 1,
          failedStep: null,
        },
      ],
    });
    expect(proto.rows.map((row) => row.aliases)).toEqual([['PogU', 'PogU2'], ['Solo']]);
  });
});

describe('purgeRunCsv', () => {
  it('emits name, id, status and error columns', () => {
    const lines = purgeRunCsv(protocol()).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(lines[0]).toBe('name,seven_tv_emote_id,status,error_message');
    expect(lines[1]).toBe('PogU,7tv-1,done,');
    expect(lines[2]).toBe('KEKW,7tv-2,failed,boom');
  });
});

describe('purgeRunFilename', () => {
  it('stamps channel and run end down to the minute', () => {
    expect(purgeRunFilename('Sensitron', '2026-08-02T10:05:12Z', 'json')).toBe(
      'emotepurge_sensitron_purge_2026-08-02-1005.json',
    );
  });
});

describe('parsePurgeRunProtocol', () => {
  it('accepts a well-formed protocol and returns only the done rows', () => {
    const result = parsePurgeRunProtocol(purgeRunJson(protocol()));
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.rows.map((row) => row.emoteId)).toEqual(['i1']);
      expect(result.meta.emoteSetId).toBe('set-1');
    }
  });

  it('rejects non-JSON', () => {
    expect(parsePurgeRunProtocol('nope{')).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.notJson',
    });
  });

  it('names a voting export instead of calling it "not a protocol"', () => {
    const envelope = (kind: string) => JSON.stringify({ source: 'emotepurge', kind });
    expect(parsePurgeRunProtocol(envelope('voting'))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.votingExport',
    });
  });

  it('falls back to wrongKind for a foreign file or an unknown kind', () => {
    const unknown = JSON.stringify({ source: 'emotepurge', kind: 'from-the-future' });
    expect(parsePurgeRunProtocol(unknown)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
    expect(parsePurgeRunProtocol(JSON.stringify({ hello: 'world' }))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  // #230: this parser runs only for `purge-run` files — a `transfer-run` file reaching it at all
  // means the dispatch was bypassed, and it is answered the same generic way as any other kind this
  // parser does not know by name (FOREIGN_KIND_ERROR_KEYS carries no entry for it; the named
  // rejection lives in `import-source-parser.ts` instead).
  it('falls back to wrongKind for a transfer-run file rather than naming it', () => {
    const transferRun = JSON.stringify({ source: 'emotepurge', kind: 'transfer-run' });
    expect(parsePurgeRunProtocol(transferRun)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('tells the CSV version of an export apart from a corrupt file', () => {
    expect(parsePurgeRunProtocol(purgeRunCsv(protocol()))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.csvInsteadOfJson',
    });
  });

  it('rejects an unknown format version', () => {
    const future = purgeRunJson(protocol()).replace(
      `"formatVersion": ${PURGE_RUN_FORMAT_VERSION}`,
      '"formatVersion": 99',
    );
    expect(parsePurgeRunProtocol(future)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongVersion',
    });
  });

  // #200 K5 finding C: a file written before K5 carried formatVersion 1 in today's row shape's
  // absence (a Guid emoteId, no aliases) — it must keep reading, not just the current version.
  it('accepts formatVersion 1, the version every protocol before K5 wrote', () => {
    const legacy = purgeRunJson(protocol()).replace(
      `"formatVersion": ${PURGE_RUN_FORMAT_VERSION}`,
      '"formatVersion": 1',
    );
    const result = parsePurgeRunProtocol(legacy);
    expect(result.ok).toBe(true);
  });

  it("accepts formatVersion 2, today's row shape", () => {
    const result = parsePurgeRunProtocol(purgeRunJson(protocol()));
    expect(result.ok).toBe(true);
  });

  // #253 (spec 6.1, E1/E15): the file names its own target — the parser no longer holds it against
  // any page, so a protocol of another channel or another set is read, not refused.
  it.each([1, PURGE_RUN_FORMAT_VERSION])(
    "returns the file's own meta.emoteSetId as the target, whatever page reads it (formatVersion %i)",
    (version) => {
      const text = purgeRunJson(protocol()).replace(
        `"formatVersion": ${PURGE_RUN_FORMAT_VERSION}`,
        `"formatVersion": ${version}`,
      );
      const result = parsePurgeRunProtocol(text);
      expect(result.ok).toBe(true);
      if (result.ok) {
        expect(result.target).toEqual({ emoteSetId: 'set-1' });
        expect(result.channelName).toBe('sensitron');
      }
    },
  );

  // F1: every purge-run file ever written carries meta.emoteSetId — one without it is not a
  // protocol of ours, and there is no fallback that would guess a set for it.
  it('refuses a protocol without meta.emoteSetId as wrongKind, with no fallback target (F1)', () => {
    const proto = JSON.parse(purgeRunJson(protocol()));
    delete proto.meta.emoteSetId;
    expect(parsePurgeRunProtocol(JSON.stringify(proto))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('refuses a protocol whose meta.emoteSetId is an empty string as wrongKind', () => {
    const proto = JSON.parse(purgeRunJson(protocol()));
    proto.meta.emoteSetId = '';
    expect(parsePurgeRunProtocol(JSON.stringify(proto))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('rejects a protocol whose channelName is not a string as wrongKind', () => {
    const proto = JSON.parse(purgeRunJson(protocol()));
    proto.channelName = 42;
    expect(parsePurgeRunProtocol(JSON.stringify(proto))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('rejects a protocol without rows array', () => {
    const broken = JSON.stringify({
      source: 'emotepurge',
      kind: 'purge-run',
      formatVersion: 1,
      channelName: 'sensitron',
      meta: { emoteSetId: 'set-1' },
    });
    expect(parsePurgeRunProtocol(broken)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  // AK 69: today's protocol — a row without a local emote and a duplicate's two aliases — reads
  // back restorable, aliases intact.
  it('reads a row with emoteId null and its aliases back as restorable', () => {
    const proto = buildPurgeRunProtocol({
      channelName: 'sensitron',
      emoteSetId: 'set-1',
      startedAt: 0,
      finishedAt: 1,
      items: [
        {
          key: '7tv-live',
          sevenTvEmoteId: '7tv-live',
          name: 'LiveOnly',
          aliases: ['LiveOnly', 'LiveTwo'],
          status: 'done',
          completedSteps: 1,
          failedStep: null,
        },
      ],
    });
    const result = parsePurgeRunProtocol(purgeRunJson(proto));
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.rows).toEqual([
        {
          emoteId: null,
          sevenTvEmoteId: '7tv-live',
          name: 'LiveOnly',
          aliases: ['LiveOnly', 'LiveTwo'],
          status: 'done',
          errorMessage: null,
        },
      ]);
    }
  });

  // AK 69: a protocol written before K5 carries a Guid and no `aliases` — it stays restorable, its
  // one alias being the row's `name`.
  it('reads an old protocol with a Guid and without aliases as restorable under its name', () => {
    const old = JSON.parse(purgeRunJson(protocol()));
    for (const row of old.rows) {
      delete row.aliases;
    }
    const result = parsePurgeRunProtocol(JSON.stringify(old));
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.rows).toEqual([
        {
          emoteId: 'i1',
          sevenTvEmoteId: '7tv-1',
          name: 'PogU',
          aliases: ['PogU'],
          status: 'done',
          errorMessage: null,
        },
      ]);
    }
  });

  it('keeps a row whose aliases field is malformed, restoring it under its name', () => {
    const proto = JSON.parse(purgeRunJson(protocol()));
    proto.rows[0].aliases = ['PogU', 42];
    const result = parsePurgeRunProtocol(JSON.stringify(proto));
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.rows.map((row) => row.aliases)).toEqual([['PogU']]);
    }
  });

  it('drops malformed rows and rejects when nothing restorable remains', () => {
    const proto = protocol();
    proto.rows = [
      { ...proto.rows[1] }, // failed — never left the set
      { ...proto.rows[0], sevenTvEmoteId: '' }, // malformed
    ];
    expect(parsePurgeRunProtocol(purgeRunJson(proto))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.noRestorableRows',
    });
  });
});
