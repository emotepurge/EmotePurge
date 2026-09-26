import { describe, expect, it } from 'vitest';

import { ImportOrigin } from '../../core/seven-tv/import-source';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { UndoCandidate, UndoSourceFileInfo } from './transfer-run-export';
import {
  TRANSFER_UNDO_FORMAT_VERSION,
  TransferUndoExecutedInput,
  TransferUndoProtocol,
  TransferUndoRunnableInput,
  TransferUndoSkippedInput,
  buildTransferUndoPlanRecord,
  buildTransferUndoProtocol,
  parseTransferUndoForRestore,
  transferUndoCsv,
  transferUndoFilename,
  transferUndoJson,
  transferUndoPlanFilename,
} from './transfer-undo-export';

const ORIGIN: ImportOrigin = { kind: 'channel', channelName: 'quellkanal' };

function setEntries(overrides: Partial<SevenTvSetEntries> = {}): SevenTvSetEntries {
  return {
    aliasesById: new Map(),
    aliaslessIds: new Set(),
    defaultNameById: new Map(),
    occupiedSlots: 0,
    complete: true,
    ...overrides,
  };
}

function sourceFile(overrides: Partial<UndoSourceFileInfo> = {}): UndoSourceFileInfo {
  return {
    stage: 'finished',
    exportedAt: '2026-09-25T09:00:00Z',
    verifiedAt: null,
    finishedAt: '2026-09-25T09:05:00Z',
    origin: ORIGIN,
    ...overrides,
  };
}

function candidate(overrides: Partial<UndoCandidate> = {}): UndoCandidate {
  return {
    sourceSevenTvEmoteId: 'src-kappa',
    sourceName: 'Kappa',
    alias: 'Kappa',
    fileStatus: 'done',
    target: { sevenTvEmoteId: 'tgt-1', entries: [{ alias: 'Kappa' }], defaultName: null },
    provenance: 'confirmed',
    ...overrides,
  };
}

const TARGET = {
  targetEmoteSetId: 'set-1',
  targetChannelName: 'zielkanal',
  targetOwnerDisplayName: null as string | null,
};

describe('buildTransferUndoPlanRecord', () => {
  it('names only the runnable rows it is given, never a skipped candidate (a back-out file names only what its run is about to touch)', () => {
    const rows: TransferUndoRunnableInput[] = [
      {
        candidate: candidate({ sourceSevenTvEmoteId: 'src-kappa' }),
        mode: 'full',
        adds: [{ alias: 'OldKappaHolder' }],
        omittedEntries: [],
        notes: [],
      },
    ];
    const record = buildTransferUndoPlanRecord({
      ...TARGET,
      sourceFile: sourceFile(),
      verifiedAt: 0,
      acknowledgedUnproven: false,
      read: setEntries({ aliasesById: new Map([['src-kappa', ['Kappa']]]) }),
      rows,
    });

    expect(record.rows).toHaveLength(1);
    expect(record.meta.stage).toBe('planned');
    expect(record.kind).toBe('transfer-undo');
    expect(record.rows[0].kind).toBe('executed');
  });

  it("reads a full row's removedSource.entries fresh from the live read, not from the candidate", () => {
    const rows: TransferUndoRunnableInput[] = [
      {
        candidate: candidate(),
        mode: 'full',
        adds: [{ alias: 'OldKappaHolder' }],
        omittedEntries: [],
        notes: [],
      },
    ];
    const record = buildTransferUndoPlanRecord({
      ...TARGET,
      sourceFile: sourceFile(),
      verifiedAt: 0,
      acknowledgedUnproven: false,
      read: setEntries({ aliasesById: new Map([['src-kappa', ['Kappa', 'KappaAlt']]]) }),
      rows,
    });

    const [row] = record.rows;
    expect(row.kind === 'executed' && row.removedSource).toEqual({
      entries: [{ alias: 'Kappa' }, { alias: 'KappaAlt' }],
      confirmed: false,
    });
  });

  it('gives an addOnly row removedSource null — nothing was ever removed for it', () => {
    const rows: TransferUndoRunnableInput[] = [
      {
        candidate: candidate(),
        mode: 'addOnly',
        adds: [{ alias: 'B' }],
        omittedEntries: [],
        notes: [],
      },
    ];
    const record = buildTransferUndoPlanRecord({
      ...TARGET,
      sourceFile: sourceFile(),
      verifiedAt: 0,
      acknowledgedUnproven: false,
      read: setEntries({ aliasesById: new Map([['src-kappa', ['Kappa']]]) }),
      rows,
    });

    const [row] = record.rows;
    expect(row.kind === 'executed' && row.removedSource).toBeNull();
  });

  it("stamps every ADD entry added: false and status 'pending' — nothing has run yet", () => {
    const rows: TransferUndoRunnableInput[] = [
      {
        candidate: candidate(),
        mode: 'full',
        adds: [{ alias: 'A' }, { alias: 'B' }],
        omittedEntries: [],
        notes: [],
      },
    ];
    const record = buildTransferUndoPlanRecord({
      ...TARGET,
      sourceFile: sourceFile(),
      verifiedAt: 0,
      acknowledgedUnproven: false,
      read: setEntries(),
      rows,
    });

    const [row] = record.rows;
    if (row.kind !== 'executed') throw new Error('expected an executed row');
    expect(row.status).toBe('pending');
    expect(row.completedSteps).toBe(0);
    expect(row.restoredTarget.entries).toEqual([
      { alias: 'A', added: false },
      { alias: 'B', added: false },
    ]);
  });

  it('counts planned rows, full rows as removals, and every ADD as an addition', () => {
    const rows: TransferUndoRunnableInput[] = [
      {
        candidate: candidate(),
        mode: 'full',
        adds: [{ alias: 'A' }],
        omittedEntries: [],
        notes: [],
      },
      {
        candidate: candidate({ sourceSevenTvEmoteId: 'src-pog' }),
        mode: 'addOnly',
        adds: [{ alias: 'B' }, { alias: 'C' }],
        omittedEntries: [],
        notes: [],
      },
    ];
    const record = buildTransferUndoPlanRecord({
      ...TARGET,
      sourceFile: sourceFile(),
      verifiedAt: 0,
      acknowledgedUnproven: false,
      read: setEntries(),
      rows,
    });

    expect(record.meta.counts).toEqual({ planned: 2, removals: 1, additions: 3 });
    expect(record.formatVersion).toBe(TRANSFER_UNDO_FORMAT_VERSION);
    expect(TRANSFER_UNDO_FORMAT_VERSION).toBe(1);
  });

  it('carries the undone-file provenance and the origin lock acknowledgement into meta', () => {
    const source = sourceFile({
      stage: 'planned',
      verifiedAt: '2026-09-25T08:00:00Z',
      finishedAt: null,
    });
    const record = buildTransferUndoPlanRecord({
      ...TARGET,
      sourceFile: source,
      verifiedAt: Date.parse('2026-09-25T10:00:00Z'),
      acknowledgedUnproven: true,
      read: setEntries(),
      rows: [],
    });

    expect(record.meta.undoneFile).toEqual(source);
    expect(record.meta.acknowledgedUnproven).toBe(true);
    expect(record.meta.verifiedAt).toBe('2026-09-25T10:00:00.000Z');
    expect(record.channelName).toBe('zielkanal');
  });
});

function executedInput(
  overrides: Partial<TransferUndoExecutedInput> = {},
): TransferUndoExecutedInput {
  return {
    candidate: candidate(),
    mode: 'full',
    adds: [{ alias: 'A' }],
    omittedEntries: [],
    notes: [],
    status: 'done',
    failedStep: null,
    completedSteps: 2,
    errorMessage: null,
    skippedReason: null,
    sourceEntriesAtRemove: [{ alias: 'Kappa' }],
    ...overrides,
  };
}

function protocol(
  overrides: Partial<Parameters<typeof buildTransferUndoProtocol>[0]> = {},
): TransferUndoProtocol {
  return buildTransferUndoProtocol({
    ...TARGET,
    sourceFile: sourceFile(),
    startedAt: 0,
    finishedAt: 1,
    acknowledgedUnproven: false,
    executed: [],
    skipped: [],
    ...overrides,
  });
}

describe('buildTransferUndoProtocol', () => {
  it('derives a full row confirmed/added from completedSteps — REMOVE (step 0) then one ADD (step 1)', () => {
    const done = protocol({ executed: [executedInput({ completedSteps: 2, status: 'done' })] });
    const [row] = done.rows;
    expect(row.kind === 'executed' && row.removedSource?.confirmed).toBe(true);
    expect(row.kind === 'executed' && row.restoredTarget.entries).toEqual([
      { alias: 'A', added: true },
    ]);
  });

  it('gives a failed@1 row (REMOVE confirmed, its one ADD never ran) a confirmed removal and an unconfirmed ADD', () => {
    const failed = protocol({
      executed: [executedInput({ status: 'failed', failedStep: 1, completedSteps: 1 })],
    });
    const [row] = failed.rows;
    expect(row.kind === 'executed' && row.removedSource?.confirmed).toBe(true);
    expect(row.kind === 'executed' && row.restoredTarget.entries).toEqual([
      { alias: 'A', added: false },
    ]);
  });

  it('gives a failed@0 row (REMOVE itself failed) an unconfirmed removal', () => {
    const failed = protocol({
      executed: [executedInput({ status: 'failed', failedStep: 0, completedSteps: 0 })],
    });
    const [row] = failed.rows;
    expect(row.kind === 'executed' && row.removedSource?.confirmed).toBe(false);
  });

  it('never sets removedSource for an addOnly row, however many of its ADDs completed', () => {
    const addOnly = protocol({
      executed: [
        executedInput({
          mode: 'addOnly',
          adds: [{ alias: 'A' }, { alias: 'B' }],
          completedSteps: 2,
          status: 'done',
        }),
      ],
    });
    const [row] = addOnly.rows;
    expect(row.kind === 'executed' && row.removedSource).toBeNull();
    expect(row.kind === 'executed' && row.restoredTarget.entries).toEqual([
      { alias: 'A', added: true },
      { alias: 'B', added: true },
    ]);
  });

  it('derives an unknown row (Schritt 0 unconfirmed answer, addOnly) as no ADD confirmed', () => {
    const unknown = protocol({
      executed: [
        executedInput({
          mode: 'addOnly',
          adds: [{ alias: 'A' }],
          status: 'unknown',
          completedSteps: 0,
        }),
      ],
    });
    const [row] = unknown.rows;
    expect(row.kind === 'executed' && row.restoredTarget.entries).toEqual([
      { alias: 'A', added: false },
    ]);
  });

  it('builds three ADDs for a #74 duplicate cell (two aliases plus the recovered null entry under its default name)', () => {
    const record = protocol({
      executed: [
        executedInput({
          adds: [{ alias: 'A' }, { alias: 'B' }, { alias: 'KappaDefault' }],
          completedSteps: 4,
          status: 'done',
        }),
      ],
    });
    const [row] = record.rows;
    expect(row.kind === 'executed' && row.restoredTarget.entries).toEqual([
      { alias: 'A', added: true },
      { alias: 'B', added: true },
      { alias: 'KappaDefault', added: true },
    ]);
  });

  it('keeps omittedEntries and notes on an addOnly row untouched — orthogonal to status', () => {
    const record = protocol({
      executed: [
        executedInput({
          mode: 'addOnly',
          adds: [{ alias: 'A' }],
          omittedEntries: [{ alias: 'B', reason: 'targetNameTaken' }],
          notes: ['targetHasForeignEntries'],
          status: 'partial',
          completedSteps: 1,
        }),
      ],
    });
    const [row] = record.rows;
    expect(row.kind === 'executed' && row.omittedEntries).toEqual([
      { alias: 'B', reason: 'targetNameTaken' },
    ]);
    expect(row.kind === 'executed' && row.notes).toEqual(['targetHasForeignEntries']);
    expect(row.kind === 'executed' && row.status).toBe('partial');
  });

  it('never lets partial override a failed or unknown status — those stay exactly what they are', () => {
    const record = protocol({
      executed: [
        executedInput({ status: 'failed', mode: 'addOnly', completedSteps: 0, failedStep: 0 }),
        executedInput({ status: 'unknown', mode: 'addOnly', completedSteps: 0 }),
      ],
    });
    expect(record.rows.map((row) => row.kind === 'executed' && row.status)).toEqual([
      'failed',
      'unknown',
    ]);
  });

  it.each([
    'duplicateInFile',
    'sourceUnderOtherName',
    'targetNameTaken',
    'skippedDrift',
    'skippedUnproven',
  ])(
    'records a candidate skipped for %s as its own kind: skipped row, never kind: executed',
    (reason) => {
      const skippedInput: TransferUndoSkippedInput = {
        candidate: candidate(),
        skippedReason: reason,
      };
      const record = protocol({ skipped: [skippedInput] });

      expect(record.rows).toEqual([
        {
          kind: 'skipped',
          sourceSevenTvEmoteId: 'src-kappa',
          sourceName: 'Kappa',
          alias: 'Kappa',
          targetSevenTvEmoteId: 'tgt-1',
          provenance: 'confirmed',
          skippedReason: reason,
        },
      ]);
    },
  );

  it('counts requested/succeeded only over executed rows, and skipped candidates separately on top', () => {
    const record = protocol({
      executed: [
        executedInput({ status: 'done', completedSteps: 2 }),
        executedInput({ status: 'failed', failedStep: 0, completedSteps: 0 }),
      ],
      skipped: [
        { candidate: candidate({ sourceSevenTvEmoteId: 'src-pog' }), skippedReason: 'nothingToDo' },
      ],
    });

    expect(record.meta.counts).toEqual({
      requested: 2,
      succeeded: 1,
      failed: 1,
      cancelled: 0,
      unknown: 0,
      removed: 1,
      added: 1,
      skipped: 1,
    });
  });

  it('contains no token anywhere', () => {
    const record = protocol({ executed: [executedInput()] });
    expect(transferUndoJson(record)).not.toMatch(/token|authorization|bearer/i);
  });
});

describe('transferUndoCsv', () => {
  it('emits every column for an executed full row', () => {
    const record = protocol({
      executed: [executedInput({ adds: [{ alias: 'A' }, { alias: 'B' }], completedSteps: 3 })],
    });

    const lines = transferUndoCsv(record).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(lines[0]).toBe(
      'kind,mode,source_name,alias,source_seven_tv_emote_id,removed_confirmed,target_seven_tv_emote_id,target_default_name,target_aliases,target_added,status,failed_step,error_message,skipped_reason',
    );
    expect(lines[1]).toBe('executed,full,Kappa,Kappa,src-kappa,true,tgt-1,,A|B,true|true,done,,,');
  });

  it('leaves every executed-only column empty for a skipped row, keeping kind/source/target/reason', () => {
    const record = protocol({
      skipped: [{ candidate: candidate(), skippedReason: 'duplicateInFile' }],
    });

    const lines = transferUndoCsv(record).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(lines[1]).toBe('skipped,,Kappa,Kappa,src-kappa,,tgt-1,,,,,,,duplicateInFile');
  });
});

describe('transferUndoPlanFilename', () => {
  it('names the planned-stage file with its own suffix, down to the minute', () => {
    expect(transferUndoPlanFilename('Zielkanal', '2026-09-25T10:05:12Z')).toBe(
      'emotepurge_zielkanal_transfer-undo-plan_2026-09-25-1005.json',
    );
  });
});

describe('transferUndoFilename', () => {
  it('names the finished-stage file with its own suffix and the requested extension, for a tracked channel', () => {
    expect(transferUndoFilename('Zielkanal', '2026-09-25T10:05:12Z', 'json')).toBe(
      'emotepurge_zielkanal_transfer-undo_2026-09-25-1005.json',
    );
    expect(transferUndoFilename('Zielkanal', '2026-09-25T10:05:12Z', 'csv')).toBe(
      'emotepurge_zielkanal_transfer-undo_2026-09-25-1005.csv',
    );
  });

  it('falls back to the set id for an untracked target', () => {
    expect(transferUndoFilename('set-42', '2026-09-25T10:05:12Z', 'json')).toBe(
      'emotepurge_set-42_transfer-undo_2026-09-25-1005.json',
    );
  });
});

describe('parseTransferUndoForRestore', () => {
  it('offers every full row of the planned stage — no addOnly row, whatever its own confirmed flag', () => {
    const record = buildTransferUndoPlanRecord({
      ...TARGET,
      sourceFile: sourceFile(),
      verifiedAt: 0,
      acknowledgedUnproven: false,
      read: setEntries({ aliasesById: new Map([['src-kappa', ['Kappa']]]) }),
      rows: [
        {
          candidate: candidate(),
          mode: 'full',
          adds: [{ alias: 'A' }],
          omittedEntries: [],
          notes: [],
        },
        {
          candidate: candidate({
            sourceSevenTvEmoteId: 'src-pog',
            sourceName: 'Pog',
            alias: 'Pog',
          }),
          mode: 'addOnly',
          adds: [{ alias: 'B' }],
          omittedEntries: [],
          notes: [],
        },
      ],
    });

    const parsed = parseTransferUndoForRestore(transferUndoJson(record));

    expect(parsed).toEqual({
      ok: true,
      stage: 'planned',
      target: { emoteSetId: 'set-1' },
      rows: [
        {
          emoteId: null,
          sevenTvEmoteId: 'src-kappa',
          name: 'Kappa',
          aliases: ['Kappa'],
          defaultName: null,
        },
      ],
    });
  });

  it('offers only confirmed-REMOVE rows of the finished stage', () => {
    const record = protocol({
      executed: [
        executedInput({ completedSteps: 2, status: 'done' }),
        executedInput({
          candidate: candidate({
            sourceSevenTvEmoteId: 'src-pog',
            sourceName: 'Pog',
            alias: 'Pog',
          }),
          status: 'failed',
          failedStep: 0,
          completedSteps: 0,
        }),
        executedInput({
          candidate: candidate({
            sourceSevenTvEmoteId: 'src-lul',
            sourceName: 'LUL',
            alias: 'LUL',
          }),
          mode: 'addOnly',
          adds: [{ alias: 'X' }],
          completedSteps: 1,
        }),
      ],
    });

    const parsed = parseTransferUndoForRestore(transferUndoJson(record));

    expect(parsed.ok && parsed.rows.map((row) => row.sevenTvEmoteId)).toEqual(['src-kappa']);
  });

  it('excludes a kind: skipped row in both stages — nothing was ever removed for it', () => {
    const record = protocol({
      executed: [executedInput({ completedSteps: 2, status: 'done' })],
      skipped: [
        { candidate: candidate({ sourceSevenTvEmoteId: 'src-pog' }), skippedReason: 'nothingToDo' },
      ],
    });

    const parsed = parseTransferUndoForRestore(transferUndoJson(record));

    expect(parsed.ok && parsed.rows.map((row) => row.sevenTvEmoteId)).toEqual(['src-kappa']);
  });

  it('refuses the whole file as wrongKind when any row carries no recognized kind — the shape is new, there is no legacy file to stay lenient for', () => {
    const record = protocol({ executed: [executedInput({ completedSteps: 2, status: 'done' })] });
    const broken = JSON.parse(transferUndoJson(record)) as { rows: Record<string, unknown>[] };
    broken.rows.push({ sourceSevenTvEmoteId: 'src-ghost', sourceName: 'Ghost', alias: 'Ghost' });

    expect(parseTransferUndoForRestore(JSON.stringify(broken))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });

  it('refuses a file with no restorable rows as transferUndoNoRows', () => {
    const record = protocol({
      skipped: [{ candidate: candidate(), skippedReason: 'nothingToDo' }],
    });

    expect(parseTransferUndoForRestore(transferUndoJson(record))).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.transferUndoNoRows',
    });
  });

  it('refuses a file of another row-shape version', () => {
    const record = JSON.parse(
      transferUndoJson(
        protocol({ executed: [executedInput({ completedSteps: 2, status: 'done' })] }),
      ),
    ) as Record<string, unknown>;
    const text = JSON.stringify({ ...record, formatVersion: TRANSFER_UNDO_FORMAT_VERSION + 1 });

    expect(parseTransferUndoForRestore(text)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongVersion',
    });
  });

  it('refuses a foreign envelope kind as wrongKind', () => {
    const record = JSON.parse(
      transferUndoJson(
        protocol({ executed: [executedInput({ completedSteps: 2, status: 'done' })] }),
      ),
    ) as Record<string, unknown>;
    const text = JSON.stringify({ ...record, kind: 'transfer-run' });

    expect(parseTransferUndoForRestore(text)).toEqual({
      ok: false,
      errorKey: 'restore.import.errors.wrongKind',
    });
  });
});
