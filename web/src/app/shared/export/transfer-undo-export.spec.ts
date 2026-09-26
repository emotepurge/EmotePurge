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
    animatedById: new Map(),
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

  it('gives a full row an empty (not null) removedSource.entries when the live read knows nothing of the source — removedSource itself is still present', () => {
    const rows: TransferUndoRunnableInput[] = [
      {
        candidate: candidate(),
        mode: 'full',
        adds: [{ alias: 'A' }],
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
    expect(row.kind === 'executed' && row.removedSource).toEqual({ entries: [], confirmed: false });
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

/** Builds a valid, correctly-discriminated `TransferUndoExecutedInput` — `mode` decides the shape of
 *  `sourceEntriesAtRemove` (an explicit array for `full`, always `null` for `addOnly`) rather than
 *  letting a caller's override drift out of sync with its own `mode`. */
function executedInput(
  overrides: Partial<TransferUndoExecutedInput> = {},
): TransferUndoExecutedInput {
  const mode = overrides.mode ?? 'full';
  const common = {
    candidate: overrides.candidate ?? candidate(),
    adds: overrides.adds ?? [{ alias: 'A' }],
    omittedEntries: overrides.omittedEntries ?? [],
    notes: overrides.notes ?? [],
    status: overrides.status ?? 'done',
    failedStep: overrides.failedStep ?? null,
    completedSteps: overrides.completedSteps ?? 2,
    errorMessage: overrides.errorMessage ?? null,
    skippedReason: overrides.skippedReason ?? null,
  };
  if (mode === 'addOnly') {
    return { ...common, mode: 'addOnly', sourceEntriesAtRemove: null };
  }
  return {
    ...common,
    mode: 'full',
    sourceEntriesAtRemove: overrides.sourceEntriesAtRemove ?? [{ alias: 'Kappa' }],
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

  it('gives a failed@0 row (REMOVE itself failed) an unconfirmed removal, but still a removedSource object', () => {
    const failed = protocol({
      executed: [
        executedInput({
          status: 'failed',
          failedStep: 0,
          completedSteps: 0,
          sourceEntriesAtRemove: [{ alias: 'Kappa' }],
        }),
      ],
    });
    const [row] = failed.rows;
    expect(row.kind === 'executed' && row.removedSource).toEqual({
      entries: [{ alias: 'Kappa' }],
      confirmed: false,
    });
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

  it('keeps omittedEntries and notes on an addOnly row untouched — orthogonal to status, and a null alias for an unverifiable entry stays null', () => {
    const record = protocol({
      executed: [
        executedInput({
          mode: 'addOnly',
          adds: [{ alias: 'A' }],
          omittedEntries: [
            { alias: 'B', reason: 'targetNameTaken' },
            { alias: null, reason: 'targetNameUnverifiable' },
          ],
          notes: ['targetHasForeignEntries'],
          status: 'partial',
          completedSteps: 1,
        }),
      ],
    });
    const [row] = record.rows;
    expect(row.kind === 'executed' && row.omittedEntries).toEqual([
      { alias: 'B', reason: 'targetNameTaken' },
      { alias: null, reason: 'targetNameUnverifiable' },
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

  it('never drops removedSource for a full row even if a caller manages to hand it null entries — defensive ?? []', () => {
    // Bypasses the discriminated union on purpose: the type now makes this impossible to construct
    // honestly, but the runtime fallback still guards a `full` row against silently looking like it
    // never touched the source at all if some future caller manages to violate the type.
    const bad = {
      ...executedInput({ mode: 'full' }),
      sourceEntriesAtRemove: null,
    } as unknown as TransferUndoExecutedInput;

    const record = protocol({ executed: [bad] });

    const [row] = record.rows;
    expect(row.kind === 'executed' && row.removedSource).toEqual({ entries: [], confirmed: true });
  });

  it.each([
    'duplicateInFile',
    'sourceUnderOtherName',
    'targetNameTaken',
    'skippedDrift',
    'skippedUnproven',
  ] as const)(
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

  it('counts a mixed run — done, failed, cancelled (mid-run skip) and its own removedSource.entries pinned to the last read, not the restored alias', () => {
    const record = protocol({
      executed: [
        executedInput({ status: 'done', completedSteps: 2 }),
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
          status: 'cancelled',
          skippedReason: 'skippedDrift',
          completedSteps: 0,
          // The recheck's own last read before it gave up — deliberately *not* `[{ alias: row.alias }]`,
          // so a builder bug that fabricates entries from the row's own alias instead of the real
          // recheck read would show up here as a mismatch (F13).
          sourceEntriesAtRemove: [{ alias: 'LUL' }, { alias: 'LulSecondAlias' }],
        }),
        executedInput({
          candidate: candidate({
            sourceSevenTvEmoteId: 'src-sadge',
            sourceName: 'Sadge',
            alias: 'Sadge',
          }),
          mode: 'addOnly',
          adds: [{ alias: 'X' }],
          status: 'unknown',
          completedSteps: 0,
        }),
      ],
    });

    expect(record.meta.counts).toMatchObject({
      requested: 4,
      succeeded: 1,
      failed: 1,
      cancelled: 1,
      unknown: 1,
      partial: 0,
    });
    const cancelledRow = record.rows.find(
      (row) => row.kind === 'executed' && row.sourceSevenTvEmoteId === 'src-lul',
    );
    expect(cancelledRow?.kind === 'executed' && cancelledRow.removedSource?.entries).toEqual([
      { alias: 'LUL' },
      { alias: 'LulSecondAlias' },
    ]);
    expect(cancelledRow?.kind === 'executed' && cancelledRow.skippedReason).toBe('skippedDrift');
  });

  it('counts requested/succeeded only over executed rows, and skipped candidates separately on top, with every sub-counter summing to requested', () => {
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
      partial: 0,
      removed: 1,
      added: 1,
      skipped: 1,
    });
    const { succeeded, failed, cancelled, unknown, partial } = record.meta.counts;
    expect(succeeded + failed + cancelled + unknown + partial).toBe(record.meta.counts.requested);
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
      'kind,mode,source_name,alias,source_seven_tv_emote_id,removed_confirmed,target_seven_tv_emote_id,target_default_name,target_aliases,target_added,status,failed_step,error_message,omitted_entries,skipped_reason',
    );
    expect(lines[1]).toBe('executed,full,Kappa,Kappa,src-kappa,true,tgt-1,,A|B,true|true,done,,,,');
  });

  it('writes an empty alias half for a null (targetNameUnverifiable) omitted entry, and joins several with |', () => {
    const record = protocol({
      executed: [
        executedInput({
          mode: 'addOnly',
          omittedEntries: [
            { alias: 'B', reason: 'targetNameTaken' },
            { alias: null, reason: 'targetNameUnverifiable' },
          ],
          status: 'partial',
        }),
      ],
    });

    const lines = transferUndoCsv(record).replace(/^﻿/, '').trimEnd().split('\r\n');
    const omittedCell = lines[1].split(',')[13];
    expect(omittedCell).toBe('B:targetNameTaken|:targetNameUnverifiable');
  });

  it('leaves every executed-only column empty for a skipped row, keeping kind/source/target/reason', () => {
    const record = protocol({
      skipped: [{ candidate: candidate(), skippedReason: 'duplicateInFile' }],
    });

    const lines = transferUndoCsv(record).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(lines[1]).toBe('skipped,,Kappa,Kappa,src-kappa,,tgt-1,,,,,,,,duplicateInFile');
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
  it('names the finished-stage file with its own suffix and the requested extension', () => {
    expect(transferUndoFilename('Zielkanal', '2026-09-25T10:05:12Z', 'json')).toBe(
      'emotepurge_zielkanal_transfer-undo_2026-09-25-1005.json',
    );
    expect(transferUndoFilename('Zielkanal', '2026-09-25T10:05:12Z', 'csv')).toBe(
      'emotepurge_zielkanal_transfer-undo_2026-09-25-1005.csv',
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

  it.each(['planned', 'finished'] as const)(
    'excludes a kind: skipped row in the %s stage even with a manipulated removedSource next to it — the kind check alone must decide',
    (stage) => {
      // Hand-built rather than through the builders: a real `kind: 'skipped'` row can never carry
      // `mode`/`removedSource` at all, so this is the only way to prove the parser's exclusion rests
      // on the `kind` check itself. The manipulated row carries *both* `mode: 'full'` and a confirmed
      // `removedSource` — with only `removedSource` forged, the separate `mode === 'full'` check
      // would already exclude it (it has no `mode` at all, so `undefined !== 'full'`), and removing
      // the `kind !== 'executed'` guard would then change nothing this test can see. Both fields
      // forged together isolate the `kind` guard as the one thing this test actually exercises;
      // removing it now genuinely turns this red.
      const envelope = {
        source: 'emotepurge',
        kind: 'transfer-undo',
        formatVersion: TRANSFER_UNDO_FORMAT_VERSION,
        exportedAt: '2026-09-25T09:05:00Z',
        channelName: 'zielkanal',
        withheld: [],
        meta: {
          stage,
          targetEmoteSetId: 'set-1',
          targetChannelName: 'zielkanal',
          targetOwnerDisplayName: null,
          undoneFile: sourceFile(),
          acknowledgedUnproven: false,
          ...(stage === 'planned'
            ? {
                verifiedAt: '2026-09-25T09:00:00Z',
                counts: { planned: 1, removals: 1, additions: 1 },
              }
            : {
                startedAt: '2026-09-25T09:00:00Z',
                finishedAt: '2026-09-25T09:05:00Z',
                counts: {
                  requested: 1,
                  succeeded: 1,
                  failed: 0,
                  cancelled: 0,
                  unknown: 0,
                  partial: 0,
                  removed: 1,
                  added: 1,
                  skipped: 1,
                },
              }),
        },
        rows: [
          {
            kind: 'executed',
            mode: 'full',
            sourceSevenTvEmoteId: 'src-kappa',
            sourceName: 'Kappa',
            alias: 'Kappa',
            removedSource: { entries: [{ alias: 'Kappa' }], confirmed: true },
            restoredTarget: { sevenTvEmoteId: 'tgt-1', defaultName: null, entries: [] },
            provenance: 'confirmed',
            omittedEntries: [],
            notes: [],
            status: 'done',
            failedStep: null,
            completedSteps: 2,
            errorMessage: null,
            skippedReason: null,
          },
          {
            kind: 'skipped',
            sourceSevenTvEmoteId: 'src-pog',
            sourceName: 'Pog',
            alias: 'Pog',
            targetSevenTvEmoteId: 'tgt-2',
            provenance: 'confirmed',
            skippedReason: 'nothingToDo',
            // Manipulated: a genuine skipped row never has either of these fields.
            mode: 'full',
            removedSource: { confirmed: true },
          },
        ],
      };

      const parsed = parseTransferUndoForRestore(JSON.stringify(envelope));

      expect(parsed.ok && parsed.rows.map((row) => row.sevenTvEmoteId)).toEqual(['src-kappa']);
    },
  );

  it('excludes a row whose mode is not full even if a manipulated removedSource claims it was confirmed', () => {
    const good = protocol({ executed: [executedInput({ completedSteps: 2, status: 'done' })] });
    const parsedGood = JSON.parse(transferUndoJson(good)) as { rows: Record<string, unknown>[] };
    const addOnlyButClaimedRemoved = {
      ...parsedGood.rows[0],
      sourceSevenTvEmoteId: 'src-sneaky',
      mode: 'addOnly',
      removedSource: { entries: [{ alias: 'Sneaky' }], confirmed: true },
    };
    const tampered = { ...parsedGood, rows: [...parsedGood.rows, addOnlyButClaimedRemoved] };

    const parsed = parseTransferUndoForRestore(JSON.stringify(tampered));

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
