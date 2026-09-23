import { describe, expect, it } from 'vitest';

import { ImportOrigin, ImportRow } from '../../core/seven-tv/import-source';
import { ImportRunItem } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { TransferPlan, TransferRow } from '../../core/seven-tv/transfer-plan';
import {
  TRANSFER_RUN_FORMAT_VERSION,
  buildTransferPlanRecord,
  buildTransferRunProtocol,
  transferPlanFilename,
  transferRunCsv,
  transferRunFilename,
  transferRunJson,
} from './transfer-run-export';

const ORIGIN: ImportOrigin = { kind: 'channel', channelName: 'quellkanal' };

const SOURCE_KAPPA: ImportRow = { sevenTvEmoteId: 'src-kappa', name: 'Kappa', imageUrl: null };
const SOURCE_POG: ImportRow = { sevenTvEmoteId: 'src-pog', name: 'Pog', imageUrl: null };
const SOURCE_SADGE: ImportRow = { sevenTvEmoteId: 'src-sadge', name: 'Sadge', imageUrl: null };
const SOURCE_LUL: ImportRow = { sevenTvEmoteId: 'src-lul', name: 'LUL', imageUrl: null };

const TARGET: {
  targetEmoteSetId: string;
  targetChannelName: string | null;
  targetOwnerDisplayName: string | null;
} = { targetEmoteSetId: 'set-1', targetChannelName: 'zielkanal', targetOwnerDisplayName: null };

function setEntries(overrides: Partial<SevenTvSetEntries> = {}): SevenTvSetEntries {
  return {
    aliasesById: new Map(),
    aliaslessIds: new Set(),
    defaultNameById: new Map(),
    complete: true,
    ...overrides,
  };
}

/** One settled run item — defaults to a plain `done` ADD-shaped row; callers override `transfer`
 *  and whatever else the case needs. */
function item(overrides: Partial<ImportRunItem> & { transfer: TransferRow }): ImportRunItem {
  return {
    key: overrides.transfer.source.sevenTvEmoteId,
    sevenTvEmoteId: overrides.transfer.source.sevenTvEmoteId,
    name: overrides.transfer.source.name,
    status: 'done',
    completedSteps: 1,
    failedStep: null,
    ...overrides,
  };
}

describe('buildTransferPlanRecord', () => {
  it("reads a replace row's target from the live entries, not from the plan's own (stale) target — #74 duplicate carries both aliases", () => {
    const plan: TransferPlan = {
      rows: [
        {
          action: 'replace',
          source: SOURCE_KAPPA,
          alias: 'Kappa',
          target: {
            sevenTvEmoteId: 'tgt-1',
            aliases: ['StaleName'],
            hasAliaslessEntry: false,
            defaultName: null,
          },
        },
      ],
    };
    const entries = setEntries({
      aliasesById: new Map([['tgt-1', ['Kappa', 'Kappa2']]]),
      defaultNameById: new Map([['tgt-1', 'KappaDefault']]),
    });

    const record = buildTransferPlanRecord({
      ...TARGET,
      origin: ORIGIN,
      verifiedAt: Date.parse('2026-09-23T10:00:00Z'),
      plan,
      entries,
      defaultNameById: entries.defaultNameById,
    });

    expect(record.meta.stage).toBe('planned');
    expect(record.rows).toHaveLength(1);
    const [row] = record.rows;
    expect(row.status).toBe('pending');
    expect(row.failedStep).toBeNull();
    expect(row.errorMessage).toBeNull();
    expect(row.removedTarget).toEqual({
      sevenTvEmoteId: 'tgt-1',
      entries: [{ alias: 'Kappa' }, { alias: 'Kappa2' }],
      aliases: ['Kappa', 'Kappa2'],
      defaultName: 'KappaDefault',
      confirmed: false,
    });
  });

  // Runde 2, Finding 2: an aliasless entry is its own `{ alias: null }` entry, alongside the named
  // one — `aliases` (the CSV-facing field) stays the named subset only.
  it('names an aliasless entry alongside a named one, keeping aliases to the named subset', () => {
    const plan: TransferPlan = {
      rows: [
        {
          action: 'replace',
          source: SOURCE_KAPPA,
          alias: 'Kappa',
          target: {
            sevenTvEmoteId: 'tgt-1',
            aliases: [],
            hasAliaslessEntry: false,
            defaultName: null,
          },
        },
      ],
    };
    const entries = setEntries({
      aliasesById: new Map([['tgt-1', ['Kappa']]]),
      aliaslessIds: new Set(['tgt-1']),
      defaultNameById: new Map([['tgt-1', 'KappaDefault']]),
    });

    const record = buildTransferPlanRecord({
      ...TARGET,
      origin: ORIGIN,
      verifiedAt: 0,
      plan,
      entries,
      defaultNameById: entries.defaultNameById,
    });

    const [row] = record.rows;
    expect(row.removedTarget?.entries).toEqual([{ alias: 'Kappa' }, { alias: null }]);
    expect(row.removedTarget?.aliases).toEqual(['Kappa']);
  });

  it('counts every plan row as planned and only the replace rows as removals, stamping its own format version', () => {
    const plan: TransferPlan = {
      rows: [
        {
          action: 'replace',
          source: SOURCE_KAPPA,
          alias: 'Kappa',
          target: {
            sevenTvEmoteId: 'tgt-1',
            aliases: ['Kappa'],
            hasAliaslessEntry: false,
            defaultName: null,
          },
        },
        { action: 'add', source: SOURCE_POG, alias: 'Pog' },
        {
          action: 'adoptSourceName',
          source: SOURCE_LUL,
          alias: 'LUL',
          target: {
            sevenTvEmoteId: 'src-lul',
            aliases: ['LULOld'],
            hasAliaslessEntry: false,
            defaultName: null,
          },
        },
      ],
    };

    const record = buildTransferPlanRecord({
      ...TARGET,
      origin: ORIGIN,
      verifiedAt: 0,
      plan,
      entries: setEntries({ aliasesById: new Map([['tgt-1', ['Kappa']]]) }),
      defaultNameById: new Map(),
    });

    expect(record.meta.counts).toEqual({ planned: 3, removals: 1 });
    expect(record.formatVersion).toBe(TRANSFER_RUN_FORMAT_VERSION);
    expect(TRANSFER_RUN_FORMAT_VERSION).toBe(1);
  });

  it("carries the target channel as the envelope's channelName for a tracked target", () => {
    const record = buildTransferPlanRecord({
      ...TARGET,
      origin: ORIGIN,
      verifiedAt: 0,
      plan: { rows: [] },
      entries: setEntries(),
      defaultNameById: new Map(),
    });

    expect(record.source).toBe('emotepurge');
    expect(record.kind).toBe('transfer-run');
    expect(record.channelName).toBe('zielkanal');
    expect(record.meta.targetChannelName).toBe('zielkanal');
  });

  it("carries '' as the envelope's channelName for an untracked target, keeping meta's own null", () => {
    const record = buildTransferPlanRecord({
      targetEmoteSetId: 'set-u',
      targetChannelName: null,
      targetOwnerDisplayName: 'Stranger',
      origin: ORIGIN,
      verifiedAt: 0,
      plan: { rows: [] },
      entries: setEntries(),
      defaultNameById: new Map(),
    });

    expect(record.channelName).toBe('');
    expect(record.meta.targetChannelName).toBeNull();
    expect(record.meta.targetOwnerDisplayName).toBe('Stranger');
  });
});

describe('buildTransferRunProtocol', () => {
  it('builds one row per action — add, renameSource, replace and adoptSourceName — with removedTarget only on replace', () => {
    const addRow: TransferRow = { action: 'add', source: SOURCE_POG, alias: 'Pog' };
    const renameRow: TransferRow = {
      action: 'renameSource',
      source: SOURCE_SADGE,
      alias: 'SadgeAlt',
    };
    const replaceRow: TransferRow = {
      action: 'replace',
      source: SOURCE_KAPPA,
      alias: 'Kappa',
      target: {
        sevenTvEmoteId: 'tgt-1',
        aliases: ['Kappa'],
        hasAliaslessEntry: false,
        defaultName: 'KappaDefault',
      },
    };
    const adoptRow: TransferRow = {
      action: 'adoptSourceName',
      source: SOURCE_LUL,
      alias: 'LUL',
      target: {
        sevenTvEmoteId: 'src-lul',
        aliases: ['LULOld'],
        hasAliaslessEntry: false,
        defaultName: null,
      },
    };
    const items: ImportRunItem[] = [
      item({ transfer: addRow }),
      item({ transfer: renameRow }),
      item({ transfer: replaceRow, completedSteps: 2 }),
      item({ transfer: adoptRow }),
    ];

    const protocol = buildTransferRunProtocol({
      ...TARGET,
      origin: ORIGIN,
      startedAt: 0,
      finishedAt: 1,
      items,
    });

    expect(protocol.meta.stage).toBe('finished');
    expect(protocol.rows.map((row) => row.action)).toEqual([
      'add',
      'renameSource',
      'replace',
      'adoptSourceName',
    ]);
    expect(protocol.rows.map((row) => row.removedTarget !== null)).toEqual([
      false,
      false,
      true,
      false,
    ]);
    expect(protocol.rows[2].sourceName).toBe('Kappa');
    expect(protocol.meta.counts).toEqual({
      requested: 4,
      succeeded: 4,
      failed: 0,
      cancelled: 0,
      removed: 1,
      unknown: 0,
    });
  });

  it('writes every row unfiltered — failed, cancelled and unknown rows included (F3)', () => {
    const items: ImportRunItem[] = [
      item({ transfer: { action: 'add', source: SOURCE_POG, alias: 'Pog' }, status: 'failed' }),
      item({
        transfer: { action: 'add', source: SOURCE_SADGE, alias: 'Sadge' },
        status: 'cancelled',
      }),
      item({ transfer: { action: 'add', source: SOURCE_LUL, alias: 'LUL' }, status: 'unknown' }),
    ];

    const protocol = buildTransferRunProtocol({
      ...TARGET,
      origin: ORIGIN,
      startedAt: 0,
      finishedAt: 1,
      items,
    });

    expect(protocol.rows).toHaveLength(3);
    expect(protocol.meta.counts).toEqual({
      requested: 3,
      succeeded: 0,
      failed: 1,
      cancelled: 1,
      removed: 0,
      unknown: 1,
    });
  });

  it("gives a replace row that failed after its REMOVE removedTarget.confirmed true, and errorMessage 7TV's raw text", () => {
    const replaceRow: TransferRow = {
      action: 'replace',
      source: SOURCE_KAPPA,
      alias: 'Kappa',
      target: {
        sevenTvEmoteId: 'tgt-1',
        aliases: ['Kappa'],
        hasAliaslessEntry: false,
        defaultName: null,
      },
    };
    const items: ImportRunItem[] = [
      item({
        transfer: replaceRow,
        status: 'failed',
        failedStep: 1,
        completedSteps: 1,
        errorMessage: 'Removed but not added.',
        sevenTvErrorMessage: 'BAD_REQUEST emote name conflict',
      }),
    ];

    const protocol = buildTransferRunProtocol({
      ...TARGET,
      origin: ORIGIN,
      startedAt: 0,
      finishedAt: 1,
      items,
    });

    const [row] = protocol.rows;
    expect(row.status).toBe('failed');
    expect(row.failedStep).toBe(1);
    expect(row.removedTarget?.confirmed).toBe(true);
    expect(row.errorMessage).toBe('BAD_REQUEST emote name conflict');
    expect(protocol.meta.counts.removed).toBe(1);
  });

  // Runde 2, Finding 1: a lost ADD answer after a confirmed REMOVE still confirms the removal —
  // the row counts in *both* counts.removed and counts.unknown, never just one.
  it('counts an unknown row whose REMOVE was confirmed (completedSteps >= 1) as both removed and unknown', () => {
    const replaceRow: TransferRow = {
      action: 'replace',
      source: SOURCE_KAPPA,
      alias: 'Kappa',
      target: {
        sevenTvEmoteId: 'tgt-1',
        aliases: ['Kappa'],
        hasAliaslessEntry: false,
        defaultName: null,
      },
    };
    const items: ImportRunItem[] = [
      item({ transfer: replaceRow, status: 'unknown', failedStep: 1, completedSteps: 1 }),
    ];

    const protocol = buildTransferRunProtocol({
      ...TARGET,
      origin: ORIGIN,
      startedAt: 0,
      finishedAt: 1,
      items,
    });

    expect(protocol.rows[0].removedTarget?.confirmed).toBe(true);
    expect(protocol.meta.counts).toMatchObject({ removed: 1, unknown: 1 });
  });

  it('gives an unknown row whose REMOVE was never confirmed (completedSteps 0) removedTarget.confirmed false, uncounted as removed', () => {
    const replaceRow: TransferRow = {
      action: 'replace',
      source: SOURCE_KAPPA,
      alias: 'Kappa',
      target: {
        sevenTvEmoteId: 'tgt-1',
        aliases: ['Kappa'],
        hasAliaslessEntry: false,
        defaultName: null,
      },
    };
    const items: ImportRunItem[] = [
      item({ transfer: replaceRow, status: 'unknown', failedStep: 0, completedSteps: 0 }),
    ];

    const protocol = buildTransferRunProtocol({
      ...TARGET,
      origin: ORIGIN,
      startedAt: 0,
      finishedAt: 1,
      items,
    });

    expect(protocol.rows[0].removedTarget?.confirmed).toBe(false);
    expect(protocol.meta.counts.removed).toBe(0);
    expect(protocol.meta.counts.unknown).toBe(1);
  });

  it('contains no token anywhere', () => {
    const replaceRow: TransferRow = {
      action: 'replace',
      source: SOURCE_KAPPA,
      alias: 'Kappa',
      target: {
        sevenTvEmoteId: 'tgt-1',
        aliases: ['Kappa'],
        hasAliaslessEntry: false,
        defaultName: null,
      },
    };
    const protocol = buildTransferRunProtocol({
      ...TARGET,
      origin: ORIGIN,
      startedAt: 0,
      finishedAt: 1,
      items: [item({ transfer: replaceRow, completedSteps: 2 })],
    });

    expect(transferRunJson(protocol)).not.toMatch(/token|authorization|bearer/i);
  });
});

describe('transferRunCsv', () => {
  it('emits every column, including the removed_* trio, for a replace row with an aliasless entry', () => {
    const replaceRow: TransferRow = {
      action: 'replace',
      source: SOURCE_KAPPA,
      alias: 'Kappa',
      target: {
        sevenTvEmoteId: 'tgt-1',
        aliases: ['Kappa'],
        hasAliaslessEntry: true,
        defaultName: 'KappaDefault',
      },
    };
    const protocol = buildTransferRunProtocol({
      ...TARGET,
      origin: ORIGIN,
      startedAt: 0,
      finishedAt: 1,
      items: [item({ transfer: replaceRow, completedSteps: 2 })],
    });

    const lines = transferRunCsv(protocol).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(lines[0]).toBe(
      'action,source_name,alias,seven_tv_emote_id,status,failed_step,error_message,removed_seven_tv_emote_id,removed_aliases,removed_aliasless_entry,removed_confirmed',
    );
    expect(lines[1]).toBe('replace,Kappa,Kappa,src-kappa,done,,,tgt-1,Kappa,true,true');
  });

  it('leaves every removed_* column empty for a row without a removedTarget', () => {
    const protocol = buildTransferRunProtocol({
      ...TARGET,
      origin: ORIGIN,
      startedAt: 0,
      finishedAt: 1,
      items: [item({ transfer: { action: 'add', source: SOURCE_POG, alias: 'Pog' } })],
    });

    const lines = transferRunCsv(protocol).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(lines[1]).toBe('add,Pog,Pog,src-pog,done,,,,,,');
  });
});

describe('transferPlanFilename', () => {
  it('names the planned-stage file with its own suffix, down to the minute', () => {
    expect(transferPlanFilename('Zielkanal', '2026-09-23T10:05:12Z')).toBe(
      'emotepurge_zielkanal_transfer-plan_2026-09-23-1005.json',
    );
  });
});

describe('transferRunFilename', () => {
  it('names the finished-stage file with its own suffix and the requested extension', () => {
    expect(transferRunFilename('Zielkanal', '2026-09-23T10:05:12Z', 'json')).toBe(
      'emotepurge_zielkanal_transfer_2026-09-23-1005.json',
    );
    expect(transferRunFilename('Zielkanal', '2026-09-23T10:05:12Z', 'csv')).toBe(
      'emotepurge_zielkanal_transfer_2026-09-23-1005.csv',
    );
  });
});
