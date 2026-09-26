import { describe, expect, it } from 'vitest';

import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { UndoCandidate } from '../export/transfer-run-export';
import {
  TransferUndoRunnableInput,
  buildTransferUndoPlanRecord,
} from '../export/transfer-undo-export';
import {
  UndoPlan,
  UndoPlanRow,
  UndoSkippedRow,
  classifyUndoRow,
  classifyUndoRows,
  diffUndoPlans,
  isUndoPlanRow,
  normalizeTargetEntries,
  sameClassification,
  summarizeUndoPlan,
} from './undo-plan';

// Shorthand used throughout: S = the source id, A = the alias the replace moved onto it, T = the
// target id, E = the target's entries in the file, D = the target's defaultName in the file.
const S = 'src-1';
const T = 'tgt-1';
const THIRD = 'third-1';

function candidate(
  overrides: Partial<Omit<UndoCandidate, 'target'>> & {
    target?: Partial<UndoCandidate['target']>;
  } = {},
): UndoCandidate {
  const { target, ...rest } = overrides;
  return {
    sourceSevenTvEmoteId: S,
    sourceName: 'SourceEmote',
    alias: 'A',
    fileStatus: 'done',
    provenance: 'confirmed',
    ...rest,
    target: {
      sevenTvEmoteId: T,
      entries: [{ alias: 'A' }],
      defaultName: 'TargetDefault',
      ...target,
    },
  };
}

/** A complete read holding exactly `entries`; `alias: null` is an aliasless entry. Every id's live
 *  `defaultNameById` is deliberately a value no case expects, so any case that would pass only by
 *  reading it (E21 forbids that) fails. */
function read(entries: { id: string; alias: string | null }[]): SevenTvSetEntries {
  const aliasesById = new Map<string, string[]>();
  const aliaslessIds = new Set<string>();
  const defaultNameById = new Map<string, string>();
  for (const entry of entries) {
    const aliases = aliasesById.get(entry.id) ?? [];
    if (entry.alias === null) {
      aliaslessIds.add(entry.id);
    } else {
      aliases.push(entry.alias);
    }
    aliasesById.set(entry.id, aliases);
    defaultNameById.set(entry.id, `LIVE-DEFAULT-${entry.id}`);
  }
  return {
    aliasesById,
    aliaslessIds,
    defaultNameById,
    animatedById: new Map(),
    occupiedSlots: entries.length,
    complete: true,
  };
}

function only(plan: UndoPlan): UndoPlanRow | UndoSkippedRow {
  const all = [...plan.rows, ...plan.skipped];
  expect(all).toHaveLength(1);
  return all[0];
}

function onlyRow(plan: UndoPlan): UndoPlanRow {
  const row = only(plan);
  if (!isUndoPlanRow(row)) {
    throw new Error(`expected a runnable row, got skipped ${row.reason}`);
  }
  return row;
}

function onlySkipped(plan: UndoPlan): UndoSkippedRow {
  const row = only(plan);
  if (isUndoPlanRow(row)) {
    throw new Error(`expected a skipped row, got ${row.mode}`);
  }
  return row;
}

/** Compiler-enforced seam to the transfer-undo file builder: a runnable row *is* a builder input. */
function asRunnableInput(row: UndoPlanRow): TransferUndoRunnableInput {
  return row;
}

describe('classifyUndoRows — step 0, duplicates in the file', () => {
  it('step 0: skips both candidates sharing a source id as duplicateInFile', () => {
    const first = candidate();
    const second = candidate({ target: { sevenTvEmoteId: 'tgt-2' } });

    const plan = classifyUndoRows([first, second], read([{ id: S, alias: 'A' }]));

    expect(plan.rows).toEqual([]);
    expect(plan.skipped.map((row) => [row.candidate, row.reason])).toEqual([
      [first, 'duplicateInFile'],
      [second, 'duplicateInFile'],
    ]);
    expect(plan.counts.skippedByReason.duplicateInFile).toBe(2);
  });

  it('step 0: skips both candidates sharing a target id as duplicateInFile', () => {
    const first = candidate();
    const second = candidate({ sourceSevenTvEmoteId: 'src-2', alias: 'B' });

    const plan = classifyUndoRows(
      [first, second],
      read([
        { id: S, alias: 'A' },
        { id: 'src-2', alias: 'B' },
      ]),
    );

    expect(plan.rows).toEqual([]);
    expect(plan.skipped.map((row) => row.reason)).toEqual(['duplicateInFile', 'duplicateInFile']);
  });

  it('step 0: leaves the other candidates of the file untouched', () => {
    const plan = classifyUndoRows(
      [
        candidate(),
        candidate({ target: { sevenTvEmoteId: 'tgt-2' } }),
        candidate({
          sourceSevenTvEmoteId: 'src-3',
          alias: 'C',
          target: { sevenTvEmoteId: 'tgt-3', entries: [{ alias: 'C' }] },
        }),
      ],
      read([
        { id: S, alias: 'A' },
        { id: 'src-3', alias: 'C' },
      ]),
    );

    expect(plan.rows.map((row) => row.candidate.sourceSevenTvEmoteId)).toEqual(['src-3']);
    expect(plan.counts.skippedByReason.duplicateInFile).toBe(2);
  });
});

describe('classifyUndoRows — step 1, the source', () => {
  it('step 1: a source holding exactly { A } becomes a REMOVE (full row)', () => {
    const row = onlyRow(classifyUndoRows([candidate()], read([{ id: S, alias: 'A' }])));

    expect(row.mode).toBe('full');
    expect(row.adds).toEqual([{ alias: 'A' }]);
  });

  it('step 1: a source that is gone has no source part (addOnly row)', () => {
    const row = onlyRow(classifyUndoRows([candidate()], read([])));

    expect(row.mode).toBe('addOnly');
    expect(row.adds).toEqual([{ alias: 'A' }]);
  });

  it.each([
    [
      'a second alias',
      [
        { id: S, alias: 'A' },
        { id: S, alias: 'A2' },
      ],
      'sourceHasMoreEntries',
      ['A', 'A2'],
    ],
    [
      'an aliasless entry next to A',
      [
        { id: S, alias: 'A' },
        { id: S, alias: null },
      ],
      'sourceHasMoreEntries',
      ['A', null],
    ],
    ['another name', [{ id: S, alias: 'Renamed' }], 'sourceUnderOtherName', ['Renamed']],
    ['a different case (ordinal, E5)', [{ id: S, alias: 'a' }], 'sourceUnderOtherName', ['a']],
    ['only an aliasless entry', [{ id: S, alias: null }], 'sourceUnderOtherName', [null]],
  ] as const)(
    'step 1: a source with %s is skipped %s, shows the live source and never checks the target',
    (_label, sourceEntries, reason, liveSource) => {
      // The target is in every state a target check would object to — foreign entry, name held by a
      // third id, one entry already present — so any target check would surface in the result.
      const target = candidate({
        target: { entries: [{ alias: 'A' }, { alias: 'B' }, { alias: 'P' }] },
      });
      const plan = classifyUndoRows(
        [target],
        read([
          ...sourceEntries,
          { id: T, alias: 'Foreign' },
          { id: T, alias: 'P' },
          { id: THIRD, alias: 'B' },
        ]),
      );

      const skipped = onlySkipped(plan);
      expect(skipped.reason).toBe(reason);
      expect(skipped.live.sourceEntries).toEqual(liveSource);
      expect(skipped.omittedEntries).toEqual([]);
      expect(plan.counts.alreadyPresent).toBe(0);
    },
  );
});

describe('classifyUndoRows — step 2, normalisation', () => {
  it('step 2: an aliasless target entry is added back under the file defaultName D', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: 'D' } })],
        read([{ id: S, alias: 'A' }]),
      ),
    );

    expect(row.mode).toBe('full');
    expect(row.adds).toEqual([{ alias: 'A' }, { alias: 'D' }]);
  });

  it('step 2: without D a full row is skipped whole as targetNameUnverifiable', () => {
    const skipped = onlySkipped(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: null } })],
        read([{ id: S, alias: 'A' }]),
      ),
    );

    expect(skipped.reason).toBe('targetNameUnverifiable');
    expect(skipped.omittedEntries).toEqual([{ alias: null, reason: 'targetNameUnverifiable' }]);
  });

  it('step 2: without D an addOnly row omits only that entry and restores the rest', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: null } })],
        read([]),
      ),
    );

    expect(row.mode).toBe('addOnly');
    expect(row.adds).toEqual([{ alias: 'A' }]);
    expect(row.omittedEntries).toEqual([{ alias: null, reason: 'targetNameUnverifiable' }]);
  });

  it('step 2: D comes from the file, not from the live defaultNameById (E21, AK 31)', () => {
    // 7TV's default name for T has changed since the replace; the read even lists it for T.
    const live = read([]);
    live.defaultNameById.set(T, 'RenamedByOwner');

    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: null }], defaultName: 'D' } })],
        live,
      ),
    );

    expect(row.adds).toEqual([{ alias: 'D' }]);
  });
});

describe('normalizeTargetEntries (step 2 on its own, AK 35)', () => {
  it("names an aliasless entry with defaultName 'D' as D", () => {
    expect(
      normalizeTargetEntries({ sevenTvEmoteId: T, entries: [{ alias: null }], defaultName: 'D' }),
    ).toEqual({
      names: ['D'],
      hadNull: true,
      unverifiable: false,
    });
  });

  it('marks an aliasless entry without a defaultName unverifiable and leaves it out of N', () => {
    expect(
      normalizeTargetEntries({
        sevenTvEmoteId: T,
        entries: [{ alias: 'A' }, { alias: null }],
        defaultName: null,
      }),
    ).toEqual({ names: ['A'], hadNull: true, unverifiable: true });
  });

  it('keeps named entries in file order and collapses a name that appears twice', () => {
    expect(
      normalizeTargetEntries({
        sevenTvEmoteId: T,
        entries: [{ alias: 'B' }, { alias: null }, { alias: 'D' }, { alias: 'A' }],
        defaultName: 'D',
      }),
    ).toEqual({ names: ['B', 'D', 'A'], hadNull: true, unverifiable: false });
  });
});

describe('classifyUndoRows — step 3, foreign entries on the target (E20)', () => {
  const twoEntries = { entries: [{ alias: 'A' }, { alias: 'B' }] };

  it('step 3: a foreign named alias on T skips a full row, shows it live and touches nothing (AK 29)', () => {
    const skipped = onlySkipped(
      classifyUndoRows(
        [candidate({ target: twoEntries })],
        read([
          { id: S, alias: 'A' },
          { id: T, alias: 'C' },
        ]),
      ),
    );

    expect(skipped.reason).toBe('targetHasForeignEntries');
    expect(skipped.live).toEqual({ sourceEntries: ['A'], targetEntries: ['C'] });
  });

  it('step 3: an addOnly row next to a foreign alias runs with the missing ADDs and a note (AK 29)', () => {
    const row = onlyRow(
      classifyUndoRows([candidate({ target: twoEntries })], read([{ id: T, alias: 'C' }])),
    );

    expect(row.mode).toBe('addOnly');
    expect(row.adds).toEqual([{ alias: 'A' }, { alias: 'B' }]);
    expect(row.notes).toEqual(['targetHasForeignEntries']);
  });

  it('step 3: a live aliasless entry on T the file does not name is foreign', () => {
    const full = onlySkipped(
      classifyUndoRows(
        [candidate()],
        read([
          { id: S, alias: 'A' },
          { id: T, alias: null },
        ]),
      ),
    );
    const addOnly = onlyRow(classifyUndoRows([candidate()], read([{ id: T, alias: null }])));

    expect(full.reason).toBe('targetHasForeignEntries');
    expect(addOnly.notes).toEqual(['targetHasForeignEntries']);
    expect(addOnly.adds).toEqual([{ alias: 'A' }]);
  });

  it('step 3: a live aliasless entry on T is the row’s own when the file names one (F5)', () => {
    const plan = classifyUndoRows(
      [candidate({ target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: 'D' } })],
      read([
        { id: S, alias: 'A' },
        { id: T, alias: null },
      ]),
    );

    const row = onlyRow(plan);
    expect(row.mode).toBe('full');
    expect(row.notes).toEqual([]);
    expect(row.adds).toEqual([{ alias: 'A' }]);
    expect(plan.counts.alreadyPresent).toBe(1);
  });
});

describe('classifyUndoRows — step 4, already present', () => {
  it('step 4: an entry T already holds by alias is not added again and counts as alreadyPresent', () => {
    // Spec 7: a target entry brought back by hand under one of its old aliases.
    const plan = classifyUndoRows(
      [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
      read([
        { id: S, alias: 'A' },
        { id: T, alias: 'B' },
      ]),
    );

    const row = onlyRow(plan);
    expect(row.mode).toBe('full');
    expect(row.adds).toEqual([{ alias: 'A' }]);
    expect(row.stepCount).toBe(2);
    expect(plan.counts.alreadyPresent).toBe(1);
  });

  it('step 4: an aliasless live entry answers only for the aliasless file entry, not for a named one', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'B' }, { alias: null }], defaultName: 'D' } })],
        read([{ id: T, alias: null }]),
      ),
    );

    expect(row.adds).toEqual([{ alias: 'B' }]);
  });

  it('step 4: a name the file gives both a named and the aliasless entry needs a named live alias', () => {
    // N collapses both entries into D; the aliasless live entry covers only the aliasless one.
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'D' }, { alias: null }], defaultName: 'D' } })],
        read([{ id: T, alias: null }]),
      ),
    );

    expect(row.adds).toEqual([{ alias: 'D' }]);
  });

  it('step 4: D brought back by a previous undo is the row’s own, alreadyPresent, and nothing is left to do (AK 35)', () => {
    const plan = classifyUndoRows(
      [candidate({ target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: 'D' } })],
      read([
        { id: T, alias: 'A' },
        { id: T, alias: 'D' },
      ]),
    );

    const skipped = onlySkipped(plan);
    expect(skipped.reason).toBe('nothingToDo');
    expect(plan.counts.alreadyPresent).toBe(2);
  });

  it('step 4: D brought back, another entry still missing ⇒ addOnly for just that entry, no note (AK 35)', () => {
    const row = onlyRow(
      classifyUndoRows(
        [
          candidate({
            target: {
              entries: [{ alias: 'A' }, { alias: 'B' }, { alias: null }],
              defaultName: 'D',
            },
          }),
        ],
        read([
          { id: T, alias: 'A' },
          { id: T, alias: 'D' },
        ]),
      ),
    );

    expect(row.mode).toBe('addOnly');
    expect(row.adds).toEqual([{ alias: 'B' }]);
    expect(row.notes).toEqual([]);
  });
});

describe('classifyUndoRows — step 5, whether the name is free', () => {
  it('step 5: a free name becomes an ADD with the explicit alias', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'Free' }] } })],
        read([{ id: S, alias: 'A' }]),
      ),
    );

    expect(row.adds).toEqual([{ alias: 'A' }, { alias: 'Free' }]);
  });

  it('step 5: a name the source holds is freed by the REMOVE — also when it is the target’s defaultName', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ alias: 'D', target: { entries: [{ alias: null }], defaultName: 'D' } })],
        read([{ id: S, alias: 'D' }]),
      ),
    );

    expect(row.mode).toBe('full');
    expect(row.adds).toEqual([{ alias: 'D' }]);
  });

  it('step 5: a name a third id holds skips a full row whole as targetNameTaken, naming the entry (AK 33)', () => {
    const skipped = onlySkipped(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
        read([
          { id: S, alias: 'A' },
          { id: THIRD, alias: 'B' },
        ]),
      ),
    );

    expect(skipped.reason).toBe('targetNameTaken');
    expect(skipped.omittedEntries).toEqual([{ alias: 'B', reason: 'targetNameTaken' }]);
  });

  it('step 5: a name a third id holds is omitted from an addOnly row, the rest runs (AK 33)', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
        read([{ id: THIRD, alias: 'B' }]),
      ),
    );

    expect(row.mode).toBe('addOnly');
    expect(row.adds).toEqual([{ alias: 'A' }]);
    expect(row.omittedEntries).toEqual([{ alias: 'B', reason: 'targetNameTaken' }]);
  });

  it('step 5: the name check uses D from the file, so a third id holding D blocks it (AK 31)', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: 'D' } })],
        read([{ id: THIRD, alias: 'D' }]),
      ),
    );

    expect(row.adds).toEqual([{ alias: 'A' }]);
    expect(row.omittedEntries).toEqual([{ alias: 'D', reason: 'targetNameTaken' }]);
  });
});

describe('classifyUndoRows — step 5, a name under two ids (DECISIONS 2026-08-04)', () => {
  it.each([
    {
      order: 'source first',
      entries: [
        { id: S, alias: 'A' },
        { id: THIRD, alias: 'A' },
      ],
    },
    {
      order: 'third id first',
      entries: [
        { id: THIRD, alias: 'A' },
        { id: S, alias: 'A' },
      ],
    },
  ])(
    'step 5: A held by the source and a third id skips a full row as targetNameTaken ($order)',
    ({ entries }) => {
      // The REMOVE would free A only on S; THIRD still holds it, so ADD T{A} would 409 with S gone.
      const skipped = onlySkipped(classifyUndoRows([candidate()], read(entries)));

      expect(skipped.reason).toBe('targetNameTaken');
      expect(skipped.omittedEntries).toEqual([{ alias: 'A', reason: 'targetNameTaken' }]);
    },
  );

  it('step 5: a name two third ids hold is omitted from an addOnly row as targetNameTaken', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
        read([
          { id: THIRD, alias: 'A' },
          { id: 'third-2', alias: 'A' },
        ]),
      ),
    );

    expect(row.mode).toBe('addOnly');
    expect(row.adds).toEqual([{ alias: 'B' }]);
    expect(row.omittedEntries).toEqual([{ alias: 'A', reason: 'targetNameTaken' }]);
  });
});

describe('classifyUndoRows — step 6, the row', () => {
  it('step 6: REMOVE plus ADDs is full with stepCount 1 + ADDs', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
        read([{ id: S, alias: 'A' }]),
      ),
    );

    expect(row).toMatchObject({ mode: 'full', stepCount: 3, omittedEntries: [], notes: [] });
  });

  it('step 6: REMOVE with nothing to add is inconsistent — the F11 guard, reachable only from a contradicting file', () => {
    // The file says the replace took A onto S, yet A is not among the target's entries, and the one
    // entry it does name is already back on T.
    const skipped = onlySkipped(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'B' }] } })],
        read([
          { id: S, alias: 'A' },
          { id: T, alias: 'B' },
        ]),
      ),
    );

    expect(skipped.reason).toBe('inconsistent');
  });

  it('step 6: no source part plus ADDs is addOnly with stepCount = ADDs', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
        read([]),
      ),
    );

    expect(row).toMatchObject({ mode: 'addOnly', stepCount: 2 });
  });

  it('step 6: no source part and nothing missing is nothingToDo (E18)', () => {
    const skipped = onlySkipped(classifyUndoRows([candidate()], read([{ id: T, alias: 'A' }])));

    expect(skipped.reason).toBe('nothingToDo');
    expect(skipped.omittedEntries).toEqual([]);
  });

  it.each([
    { provenance: 'confirmed', mode: 'full', sourceEntries: [{ id: S, alias: 'A' }] },
    { provenance: 'unproven', mode: 'full', sourceEntries: [{ id: S, alias: 'A' }] },
    { provenance: 'confirmed', mode: 'addOnly', sourceEntries: [] },
    { provenance: 'unproven', mode: 'addOnly', sourceEntries: [] },
  ] as const)(
    'step 6: passes provenance $provenance through on a $mode row',
    ({ provenance, mode, sourceEntries }) => {
      const row = onlyRow(classifyUndoRows([candidate({ provenance })], read([...sourceEntries])));

      expect(row.mode).toBe(mode);
      expect(row.provenance).toBe(provenance);
      expect(row.candidate.provenance).toBe(provenance);
    },
  );

  it('step 6: a #74 duplicate cell (two aliases and an aliasless entry) is full with three ADDs', () => {
    const plan = classifyUndoRows(
      [
        candidate({
          alias: 'X',
          target: { entries: [{ alias: 'X' }, { alias: 'Y' }, { alias: null }], defaultName: 'Z' },
        }),
      ],
      read([{ id: S, alias: 'X' }]),
    );

    const row = onlyRow(plan);
    expect(row.adds).toEqual([{ alias: 'X' }, { alias: 'Y' }, { alias: 'Z' }]);
    expect(row.stepCount).toBe(4);
    expect(summarizeUndoPlan(plan).slotDelta).toBe(2);
  });
});

describe('classifyUndoRows — table order for a full row', () => {
  it.each([
    {
      reason: 'targetNameUnverifiable',
      label: 'step 2 before step 3',
      target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: null },
      extra: [{ id: T, alias: 'Foreign' }],
    },
    {
      reason: 'targetHasForeignEntries',
      label: 'step 3 before step 5',
      target: { entries: [{ alias: 'A' }, { alias: 'B' }] },
      extra: [
        { id: T, alias: 'Foreign' },
        { id: THIRD, alias: 'B' },
      ],
    },
  ] as const)('reports $reason when several steps object ($label)', ({ reason, target, extra }) => {
    const skipped = onlySkipped(
      classifyUndoRows(
        [candidate({ target: { entries: [...target.entries], defaultName: target.defaultName } })],
        read([{ id: S, alias: 'A' }, ...extra]),
      ),
    );

    expect(skipped.reason).toBe(reason);
  });
});

describe('classifyUndoRows — the Codex counterexamples', () => {
  it('a never-started planned file whose state was rebuilt by hand classifies full, marked unproven (AK 28)', () => {
    // T gone, S under A — the full form, but nothing in a planned file proves the run happened.
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ provenance: 'unproven', fileStatus: 'pending' })],
        read([{ id: S, alias: 'A' }]),
      ),
    );

    expect(row).toMatchObject({ mode: 'full', provenance: 'unproven' });
  });

  it('after a partial undo (S gone, A back, B missing) the second run is addOnly [B] (AK 30)', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
        read([{ id: T, alias: 'A' }]),
      ),
    );

    expect(row).toMatchObject({ mode: 'addOnly', adds: [{ alias: 'B' }], notes: [] });
  });

  it('after a partial undo with a foreign C on T the second run is addOnly [B] with a note (AK 36)', () => {
    const row = onlyRow(
      classifyUndoRows(
        [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
        read([
          { id: T, alias: 'A' },
          { id: T, alias: 'C' },
        ]),
      ),
    );

    expect(row).toMatchObject({
      mode: 'addOnly',
      adds: [{ alias: 'B' }],
      notes: ['targetHasForeignEntries'],
    });
  });

  it('when C holds B, S is gone and A is back, the row is nothingToDo with the omission kept visible (Festlegung Nr. 7)', () => {
    const plan = classifyUndoRows(
      [candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } })],
      read([
        { id: T, alias: 'A' },
        { id: 'c-1', alias: 'B' },
      ]),
    );

    const skipped = onlySkipped(plan);
    expect(skipped.reason).toBe('nothingToDo');
    expect(skipped.omittedEntries).toEqual([{ alias: 'B', reason: 'targetNameTaken' }]);
    expect(plan.counts.skippedByReason.nothingToDo).toBe(1);
    expect(plan.rows).toEqual([]);
  });

  it('every ADD of a mixed plan carries a non-empty alias (AK 31)', () => {
    const plan = classifyUndoRows(
      [
        candidate({ target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: 'D' } }),
        candidate({
          sourceSevenTvEmoteId: 'src-2',
          alias: 'B',
          target: {
            sevenTvEmoteId: 'tgt-2',
            entries: [{ alias: null }, { alias: 'B2' }],
            defaultName: null,
          },
        }),
        candidate({
          sourceSevenTvEmoteId: 'src-3',
          alias: 'E',
          target: { sevenTvEmoteId: 'tgt-3', entries: [{ alias: null }], defaultName: 'E' },
        }),
      ],
      read([
        { id: S, alias: 'A' },
        { id: 'src-3', alias: 'E' },
      ]),
    );

    const adds = plan.rows.flatMap((row) => row.adds);
    expect(adds.length).toBeGreaterThan(0);
    expect(adds.every((add) => typeof add.alias === 'string' && add.alias.length > 0)).toBe(true);
  });
});

describe('classifyUndoRows — counts', () => {
  it('counts rows per mode, REMOVEs, ADDs, present entries and skipped rows per reason', () => {
    const plan = classifyUndoRows(
      [
        candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } }),
        candidate({
          sourceSevenTvEmoteId: 'src-2',
          alias: 'M',
          target: { sevenTvEmoteId: 'tgt-2', entries: [{ alias: 'M' }, { alias: 'N' }] },
        }),
        candidate({
          sourceSevenTvEmoteId: 'src-3',
          alias: 'Q',
          target: { sevenTvEmoteId: 'tgt-3', entries: [{ alias: 'Q' }] },
        }),
        candidate({
          sourceSevenTvEmoteId: 'src-4',
          alias: 'R',
          target: { sevenTvEmoteId: 'tgt-4', entries: [{ alias: 'R' }] },
        }),
      ],
      read([
        { id: S, alias: 'A' },
        { id: 'tgt-2', alias: 'M' },
        { id: 'tgt-3', alias: 'Q' },
        { id: 'src-4', alias: 'Other' },
      ]),
    );

    expect(plan.counts).toEqual({
      full: 1,
      addOnly: 1,
      removals: 1,
      additions: 3,
      alreadyPresent: 2,
      skippedByReason: {
        duplicateInFile: 0,
        sourceUnderOtherName: 1,
        sourceHasMoreEntries: 0,
        targetNameUnverifiable: 0,
        targetHasForeignEntries: 0,
        targetNameTaken: 0,
        inconsistent: 0,
        nothingToDo: 1,
      },
    });
  });

  it('hands its runnable rows to the transfer-undo file builder as they are', () => {
    const live = read([
      { id: S, alias: 'A' },
      { id: THIRD, alias: 'Taken' },
    ]);
    const plan = classifyUndoRows(
      [
        candidate({ target: { entries: [{ alias: 'A' }, { alias: null }], defaultName: 'D' } }),
        candidate({
          sourceSevenTvEmoteId: 'src-2',
          alias: 'B',
          provenance: 'unproven',
          target: { sevenTvEmoteId: 'tgt-2', entries: [{ alias: 'B' }, { alias: 'Taken' }] },
        }),
      ],
      live,
    );

    const record = buildTransferUndoPlanRecord({
      targetEmoteSetId: 'set-1',
      targetChannelName: null,
      targetOwnerDisplayName: null,
      sourceFile: {
        stage: 'planned',
        exportedAt: '',
        verifiedAt: '',
        finishedAt: null,
        origin: null,
      },
      verifiedAt: 0,
      acknowledgedUnproven: false,
      read: live,
      rows: plan.rows.map(asRunnableInput),
    });

    expect(
      record.rows.map(
        (row) =>
          row.kind === 'executed' && [
            row.mode,
            row.restoredTarget.entries.map((entry) => entry.alias),
            row.omittedEntries,
            row.provenance,
          ],
      ),
    ).toEqual([
      ['full', ['A', 'D'], [], 'confirmed'],
      ['addOnly', ['B'], [{ alias: 'Taken', reason: 'targetNameTaken' }], 'unproven'],
    ]);
  });
});

describe('classifyUndoRow', () => {
  it('classifies one candidate exactly as classifyUndoRows does, without the file-level step 0', () => {
    const live = read([
      { id: S, alias: 'A' },
      { id: THIRD, alias: 'B' },
    ]);
    const cases = [
      candidate(),
      candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } }),
      candidate({
        sourceSevenTvEmoteId: 'gone',
        target: { entries: [{ alias: 'A' }, { alias: 'B' }] },
      }),
    ];

    for (const each of cases) {
      expect(classifyUndoRow(each, live)).toEqual(only(classifyUndoRows([each], live)));
    }
    // A duplicate is a property of the list: twice in one file it is skipped, alone it runs.
    expect(classifyUndoRows([cases[0], cases[0]], live).skipped.map((row) => row.reason)).toEqual([
      'duplicateInFile',
      'duplicateInFile',
    ]);
    expect(isUndoPlanRow(classifyUndoRow(cases[0], live))).toBe(true);
  });
});

describe('sameClassification', () => {
  const stamped: UndoPlanRow = {
    candidate: candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } }),
    mode: 'full',
    adds: [{ alias: 'A' }, { alias: 'B' }],
    stepCount: 3,
    provenance: 'confirmed',
    omittedEntries: [],
    notes: [],
  };

  it('holds for the same mode and the same ADD list', () => {
    expect(
      sameClassification(stamped, { ...stamped, adds: [{ alias: 'A' }, { alias: 'B' }] }),
    ).toBe(true);
  });

  it.each([
    ['a mode change', { ...stamped, mode: 'addOnly' as const, stepCount: 2 }],
    ['a different ADD name', { ...stamped, adds: [{ alias: 'A' }, { alias: 'C' }] }],
    ['the same ADDs in another order', { ...stamped, adds: [{ alias: 'B' }, { alias: 'A' }] }],
    ['one ADD fewer', { ...stamped, adds: [{ alias: 'A' }], stepCount: 2 }],
    [
      'one ADD more',
      { ...stamped, adds: [{ alias: 'A' }, { alias: 'B' }, { alias: 'C' }], stepCount: 4 },
    ],
  ])('fails on %s', (_label, fresh) => {
    expect(sameClassification(stamped, fresh)).toBe(false);
  });

  it('fails when the row is skipped now', () => {
    const fresh: UndoSkippedRow = {
      candidate: stamped.candidate,
      reason: 'sourceHasMoreEntries',
      live: { sourceEntries: ['A', 'A2'], targetEntries: [] },
      omittedEntries: [],
    };

    expect(sameClassification(stamped, fresh)).toBe(false);
  });

  it('ignores notes (and omissions) — a foreign entry next to an addOnly row changes nothing it sends', () => {
    const addOnly: UndoPlanRow = { ...stamped, mode: 'addOnly', stepCount: 2 };

    expect(
      sameClassification(addOnly, {
        ...addOnly,
        notes: ['targetHasForeignEntries'],
        omittedEntries: [{ alias: 'Z', reason: 'targetNameTaken' }],
      }),
    ).toBe(true);
  });
});

describe('diffUndoPlans', () => {
  const candidates = [
    candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } }),
    candidate({
      sourceSevenTvEmoteId: 'src-2',
      alias: 'M',
      target: { sevenTvEmoteId: 'tgt-2', entries: [{ alias: 'M' }] },
    }),
  ];
  const stampedRead = read([
    { id: S, alias: 'A' },
    { id: 'src-2', alias: 'M' },
  ]);
  const stamped = classifyUndoRows(candidates, stampedRead);

  it('lets every row run when nothing changed', () => {
    const diff = diffUndoPlans(stamped, classifyUndoRows(candidates, stampedRead));

    expect(diff.runnable.map((row) => row.candidate)).toEqual(candidates);
    expect(diff.drifted).toEqual([]);
  });

  it('drifts a row whose mode changed (the source vanished since the stamp)', () => {
    const diff = diffUndoPlans(
      stamped,
      classifyUndoRows(candidates, read([{ id: 'src-2', alias: 'M' }])),
    );

    expect(diff.drifted).toEqual([stamped.rows[0]]);
    expect(diff.runnable.map((row) => row.candidate)).toEqual([candidates[1]]);
  });

  it('drifts a row whose ADD list changed (an entry came back by hand)', () => {
    const diff = diffUndoPlans(
      stamped,
      classifyUndoRows(
        candidates,
        read([
          { id: S, alias: 'A' },
          { id: T, alias: 'B' },
          { id: 'src-2', alias: 'M' },
        ]),
      ),
    );

    expect(diff.drifted).toEqual([stamped.rows[0]]);
  });

  it('drifts a row that is skipped now (a second alias on the source, AK 26)', () => {
    const diff = diffUndoPlans(
      stamped,
      classifyUndoRows(
        candidates,
        read([
          { id: S, alias: 'A' },
          { id: 'src-2', alias: 'M' },
          { id: 'src-2', alias: 'M2' },
        ]),
      ),
    );

    expect(diff.drifted).toEqual([stamped.rows[1]]);
    expect(diff.runnable).toHaveLength(1);
  });

  it('runs the fresh row, so a note that appeared since the stamp travels with it', () => {
    const addOnlyCandidates = [candidate()];
    const before = classifyUndoRows(addOnlyCandidates, read([]));
    const diff = diffUndoPlans(
      before,
      classifyUndoRows(addOnlyCandidates, read([{ id: T, alias: 'C' }])),
    );

    expect(diff.drifted).toEqual([]);
    expect(diff.runnable.map((row) => row.notes)).toEqual([['targetHasForeignEntries']]);
  });

  it('never runs a candidate that was not a row of the stamped plan', () => {
    // The effective plan (spec 17 K2) holds only the second row; the first would run fresh too.
    const diff = diffUndoPlans(
      { rows: [stamped.rows[1]] },
      classifyUndoRows(candidates, stampedRead),
    );

    expect(diff.runnable.map((row) => row.candidate)).toEqual([candidates[1]]);
  });

  it('drifts a stamped row the fresh plan does not know at all (fail-closed)', () => {
    const diff = diffUndoPlans(stamped, classifyUndoRows([candidates[1]], stampedRead));

    expect(diff.drifted).toEqual([stamped.rows[0]]);
  });
});

describe('summarizeUndoPlan', () => {
  it('a single-entry full row removes one and adds one — slot delta 0', () => {
    expect(
      summarizeUndoPlan(classifyUndoRows([candidate()], read([{ id: S, alias: 'A' }]))),
    ).toEqual({
      removeCount: 1,
      addCount: 1,
      slotDelta: 0,
      omittedEntryCount: 0,
      foreignNotedRows: 0,
    });
  });

  it('counts ADDs of both modes, omitted entries and foreign-noted rows', () => {
    const plan = classifyUndoRows(
      [
        candidate({ target: { entries: [{ alias: 'A' }, { alias: 'B' }] } }),
        candidate({
          sourceSevenTvEmoteId: 'src-2',
          alias: 'M',
          target: {
            sevenTvEmoteId: 'tgt-2',
            entries: [{ alias: 'M' }, { alias: 'Taken' }, { alias: null }],
            defaultName: null,
          },
        }),
        candidate({
          sourceSevenTvEmoteId: 'src-3',
          alias: 'Q',
          target: { sevenTvEmoteId: 'tgt-3', entries: [{ alias: 'Q' }] },
        }),
      ],
      read([
        { id: S, alias: 'A' },
        { id: THIRD, alias: 'Taken' },
        { id: 'tgt-3', alias: 'Foreign' },
      ]),
    );

    expect(summarizeUndoPlan(plan)).toEqual({
      removeCount: 1,
      addCount: 4,
      slotDelta: 3,
      omittedEntryCount: 2,
      foreignNotedRows: 1,
    });
  });

  it('summarizes only the rows it is given — the effective plan after the origin lock (spec 17 K2)', () => {
    const plan = classifyUndoRows(
      [
        candidate({ provenance: 'unproven' }),
        candidate({
          sourceSevenTvEmoteId: 'src-2',
          alias: 'M',
          target: { sevenTvEmoteId: 'tgt-2', entries: [{ alias: 'M' }] },
        }),
      ],
      read([{ id: S, alias: 'A' }]),
    );

    const effective = plan.rows.filter(
      (row) => !(row.mode === 'full' && row.provenance === 'unproven'),
    );

    expect(summarizeUndoPlan({ rows: effective })).toMatchObject({
      removeCount: 0,
      addCount: 1,
      slotDelta: 1,
    });
  });
});
