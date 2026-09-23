import { describe, expect, it } from 'vitest';

import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { ImportRow } from '../../core/seven-tv/import-source';
import {
  ResolutionContext,
  ResolutionDecisions,
  ResolutionValidation,
  RowDecision,
  ViolationRule,
  buildTransferPlan,
  summarizeTransferPlan,
  validateResolution,
} from './conflict-resolution';
import { AliasMismatchRow, ImportPreview, NameCollisionRow } from './import-preview';
import { projectSlots } from './slot-projection';

const TARGET_IMAGE_URL = 'https://cdn.7tv.app/placeholder/1x.webp';
const TRACKED: ResolutionContext = { targetIsTracked: true };
const UNTRACKED: ResolutionContext = { targetIsTracked: false };

function importRow(id: string, name: string): ImportRow {
  return { sevenTvEmoteId: id, name, imageUrl: null };
}

function emoteListItem(id: string, name: string): EmoteListItem {
  return { sevenTvEmoteId: id, name, imageUrl: TARGET_IMAGE_URL };
}

function collisionRow(
  row: ImportRow,
  target: EmoteListItem,
  targetAliases: string[],
  targetHasAliaslessEntry = false,
): NameCollisionRow {
  return { row, target, targetAliases, targetHasAliaslessEntry };
}

function mismatchRow(
  row: ImportRow,
  targetAliases: string[],
  adoptBlocked: 'nameTaken' | 'duplicateTarget' | null = null,
): AliasMismatchRow {
  return { row, targetAliases, adoptBlocked };
}

/** A minimal `ImportPreview` — only `toAdd`/`nameCollisionRows`/`aliasMismatchRows`/`targetNames`
 *  are read by `conflict-resolution.ts`, so the counting-only fields are filled with harmless
 *  defaults rather than kept consistent with the row lists (no test here asserts on them).
 *  `targetNames` defaults to empty, not derived from the row lists — a test that needs rule 2 to
 *  see a name must say so explicitly, the same way a real preview would have found it. */
function preview(partial: Partial<ImportPreview>): ImportPreview {
  return {
    toAdd: [],
    alreadyPresent: 0,
    nameCollisions: [],
    nameCollisionRowCount: 0,
    nameCollisionRows: [],
    aliasMismatches: [],
    aliasMismatchRows: [],
    invalidNames: [],
    targetNames: new Set<string>(),
    ...partial,
  };
}

function decisions(entries: [string, RowDecision][]): ResolutionDecisions {
  return new Map(entries);
}

function expectViolation(
  result: ResolutionValidation,
  rule: ViolationRule,
  rowKeys: string[],
): void {
  expect(result.ok).toBe(false);
  if (result.ok) {
    return;
  }
  const match = result.violations.find((violation) => violation.rule === rule);
  expect(
    match,
    `expected a ${rule} violation, got ${JSON.stringify(result.violations)}`,
  ).toBeDefined();
  expect(match!.rowKeys.slice().sort()).toEqual([...rowKeys].sort());
}

describe('validateResolution', () => {
  describe('rule 1 — no generated alias twice', () => {
    it('flags two renames that land on the same alias', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Old1'), emoteListItem('t-1', 'Old1'), ['Old1']),
          collisionRow(importRow('c-2', 'Old2'), emoteListItem('t-2', 'Old2'), ['Old2']),
        ],
      });
      const d = decisions([
        ['c-1', { kind: 'renameSource', alias: 'Shared' }],
        ['c-2', { kind: 'renameSource', alias: 'Shared' }],
      ]);

      expectViolation(validateResolution(p, d, TRACKED), 'duplicateGeneratedAlias', ['c-1', 'c-2']);
    });

    it('flags a rename onto the name of an untouched toAdd row', () => {
      const p = preview({
        toAdd: [importRow('add-1', 'Shared')],
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Other'), emoteListItem('t-1', 'Other'), ['Other']),
        ],
      });
      const d = decisions([['c-1', { kind: 'renameSource', alias: 'Shared' }]]);

      expectViolation(validateResolution(p, d, TRACKED), 'duplicateGeneratedAlias', [
        'add-1',
        'c-1',
      ]);
    });

    it('does not flag two untouched toAdd rows sharing a name (plan stays as today)', () => {
      const p = preview({
        toAdd: [importRow('add-1', 'Dup'), importRow('add-2', 'Dup')],
      });

      const result = validateResolution(p, decisions([]), TRACKED);

      expect(result).toEqual({ ok: true });
      const plan = buildTransferPlan(p, decisions([]), TRACKED);
      expect(plan.rows).toEqual([
        { action: 'add', source: importRow('add-1', 'Dup'), alias: 'Dup' },
        { action: 'add', source: importRow('add-2', 'Dup'), alias: 'Dup' },
      ]);
    });
  });

  describe('rule 2 — no generated alias equal to a name the target already held', () => {
    it('allows a replace row to reuse the exact name its own REMOVE frees', () => {
      const target = emoteListItem('t-1', 'Name1');
      const p = preview({
        nameCollisionRows: [collisionRow(importRow('c-1', 'Name1'), target, ['Name1'])],
        targetNames: new Set(['Name1']),
      });
      const d = decisions([['c-1', { kind: 'replaceTarget' }]]);

      expect(validateResolution(p, d, TRACKED)).toEqual({ ok: true });
      const plan = buildTransferPlan(p, d, TRACKED);
      expect(plan.rows).toEqual([
        {
          action: 'replace',
          source: importRow('c-1', 'Name1'),
          alias: 'Name1',
          target: {
            sevenTvEmoteId: 't-1',
            aliases: ['Name1'],
            hasAliaslessEntry: false,
            defaultName: null,
          },
        },
      ]);
    });

    it("flags a rename onto a name a different row's replace would free — in both row orders", () => {
      const targetA = emoteListItem('t-a', 'A');
      const targetB = emoteListItem('t-b', 'B');
      const rowFreesA = collisionRow(importRow('c-free', 'A'), targetA, ['A']);
      const rowRenamesToA = collisionRow(importRow('c-rename', 'B'), targetB, ['B']);
      const d = decisions([
        ['c-free', { kind: 'replaceTarget' }],
        ['c-rename', { kind: 'renameSource', alias: 'A' }],
      ]);
      const targetNames = new Set(['A', 'B']);

      const forward = validateResolution(
        preview({ nameCollisionRows: [rowFreesA, rowRenamesToA], targetNames }),
        d,
        TRACKED,
      );
      const backward = validateResolution(
        preview({ nameCollisionRows: [rowRenamesToA, rowFreesA], targetNames }),
        d,
        TRACKED,
      );

      expectViolation(forward, 'aliasHeldByTarget', ['c-rename', 'c-free']);
      expectViolation(backward, 'aliasHeldByTarget', ['c-rename', 'c-free']);
    });

    it('flags a rename onto a name an adopt row would take over (adopt never frees it, Frage 7)', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Unrelated'), emoteListItem('t-1', 'Unrelated'), [
            'Unrelated',
          ]),
        ],
        aliasMismatchRows: [mismatchRow(importRow('m-1', 'NewAlias'), ['OldAlias'])],
        targetNames: new Set(['Unrelated', 'OldAlias']),
      });
      const d = decisions([
        ['m-1', { kind: 'adoptSourceName' }],
        ['c-1', { kind: 'renameSource', alias: 'OldAlias' }],
      ]);

      expectViolation(validateResolution(p, d, TRACKED), 'aliasHeldByTarget', ['c-1', 'm-1']);
    });

    it('flags a rename onto one alias of a #74 duplicate target that a different replace takes', () => {
      const duplicateTarget = emoteListItem('t-dup', 'Alias1');
      const ownRow = collisionRow(importRow('c-own', 'Alias1'), duplicateTarget, [
        'Alias1',
        'Alias2',
      ]);
      const otherRow = collisionRow(
        importRow('c-other', 'Unrelated'),
        emoteListItem('t-other', 'Unrelated'),
        ['Unrelated'],
      );
      const p = preview({
        nameCollisionRows: [ownRow, otherRow],
        targetNames: new Set(['Alias1', 'Alias2', 'Unrelated']),
      });
      const d = decisions([
        ['c-own', { kind: 'replaceTarget' }],
        ['c-other', { kind: 'renameSource', alias: 'Alias2' }],
      ]);

      expectViolation(validateResolution(p, d, TRACKED), 'aliasHeldByTarget', ['c-other', 'c-own']);
    });

    it('flags a rename onto a target name that no conflict row exposes at all', () => {
      // The target also holds 'UntouchedName' on some entry no source row collides or mismatches
      // with — buildImportPreview would never surface that entry in a NameCollisionRow or an
      // AliasMismatchRow, only in targetNames, which is exactly what rule 2 must still catch it
      // against.
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Old'), emoteListItem('t-1', 'Old'), ['Old']),
        ],
        targetNames: new Set(['Old', 'UntouchedName']),
      });
      const d = decisions([['c-1', { kind: 'renameSource', alias: 'UntouchedName' }]]);

      expectViolation(validateResolution(p, d, TRACKED), 'aliasHeldByTarget', ['c-1']);
    });
  });

  describe('rule 3 — a typed rename alias must pass isNameRejectedBySevenTv', () => {
    it('flags a rename alias containing whitespace (typed, unlike an unchanged toAdd name)', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Old'), emoteListItem('t-1', 'Old'), ['Old']),
        ],
      });
      const d = decisions([['c-1', { kind: 'renameSource', alias: 'has space' }]]);

      expectViolation(validateResolution(p, d, TRACKED), 'invalidTypedAlias', ['c-1']);
    });

    it('flags a rename alias over 100 characters', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Old'), emoteListItem('t-1', 'Old'), ['Old']),
        ],
      });
      const d = decisions([['c-1', { kind: 'renameSource', alias: 'a'.repeat(101) }]]);

      expectViolation(validateResolution(p, d, TRACKED), 'invalidTypedAlias', ['c-1']);
    });

    it('flags an empty rename alias', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Old'), emoteListItem('t-1', 'Old'), ['Old']),
        ],
      });
      const d = decisions([['c-1', { kind: 'renameSource', alias: '' }]]);

      expectViolation(validateResolution(p, d, TRACKED), 'invalidTypedAlias', ['c-1']);
    });
  });

  describe('rule 4 — no two rows replace the same target entry', () => {
    it('flags two replace decisions on a #74 duplicate target reached via its two aliases', () => {
      const duplicateTarget = emoteListItem('t-dup', 'Alias1');
      const rowOnAlias1 = collisionRow(importRow('c-1', 'Alias1'), duplicateTarget, [
        'Alias1',
        'Alias2',
      ]);
      const rowOnAlias2 = collisionRow(importRow('c-2', 'Alias2'), duplicateTarget, [
        'Alias1',
        'Alias2',
      ]);
      const p = preview({ nameCollisionRows: [rowOnAlias1, rowOnAlias2] });
      const d = decisions([
        ['c-1', { kind: 'replaceTarget' }],
        ['c-2', { kind: 'replaceTarget' }],
      ]);

      expectViolation(validateResolution(p, d, TRACKED), 'duplicateReplaceTarget', ['c-1', 'c-2']);
    });
  });

  describe('rule 5 — replace and adopt never share a target entry', () => {
    it('flags a replace and an adopt decision touching the same target id', () => {
      // A real NameCollisionRow's own source name is always one of its target's aliases (that
      // agreement is what makes it a name collision) — this row's name is 'OldAlias', not an
      // unrelated string, to match how buildImportPreview actually builds one.
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'OldAlias'), emoteListItem('shared-id', 'OldAlias'), [
            'OldAlias',
          ]),
        ],
        aliasMismatchRows: [mismatchRow(importRow('shared-id', 'NewAlias'), ['OldAlias'])],
      });
      const d = decisions([
        ['c-1', { kind: 'replaceTarget' }],
        ['shared-id', { kind: 'adoptSourceName' }],
      ]);

      expectViolation(validateResolution(p, d, TRACKED), 'targetTouchedByReplaceAndAdopt', [
        'c-1',
        'shared-id',
      ]);
    });
  });

  describe('rule 6 — adopt only when adoptBlocked is null', () => {
    it('flags an adopt decision on a row the preview marked blocked', () => {
      const p = preview({
        aliasMismatchRows: [mismatchRow(importRow('m-1', 'NewAlias'), ['OldAlias'], 'nameTaken')],
      });
      const d = decisions([['m-1', { kind: 'adoptSourceName' }]]);

      expectViolation(validateResolution(p, d, TRACKED), 'adoptBlocked', ['m-1']);
    });
  });

  describe('rule 7 — replace only against a tracked target', () => {
    it('flags a replace decision when the target is untracked', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Name1'), emoteListItem('t-1', 'Name1'), ['Name1']),
        ],
      });
      const d = decisions([['c-1', { kind: 'replaceTarget' }]]);

      expectViolation(validateResolution(p, d, UNTRACKED), 'replaceNeedsTrackedTarget', ['c-1']);
    });

    it('allows the same replace decision once the target is tracked', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Name1'), emoteListItem('t-1', 'Name1'), ['Name1']),
        ],
      });
      const d = decisions([['c-1', { kind: 'replaceTarget' }]]);

      expect(validateResolution(p, d, TRACKED)).toEqual({ ok: true });
    });

    it('allows rename and adopt decisions against an untracked target', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Old'), emoteListItem('t-1', 'Old'), ['Old']),
        ],
        aliasMismatchRows: [mismatchRow(importRow('m-1', 'NewAlias'), ['OldAlias'])],
      });
      const d = decisions([
        ['c-1', { kind: 'renameSource', alias: 'Fresh' }],
        ['m-1', { kind: 'adoptSourceName' }],
      ]);

      expect(validateResolution(p, d, UNTRACKED)).toEqual({ ok: true });
    });
  });

  describe('a decision on a row of the wrong kind or an unknown key', () => {
    it('is silently ignored rather than raising an eighth violation', () => {
      const p = preview({
        nameCollisionRows: [
          collisionRow(importRow('c-1', 'Name1'), emoteListItem('t-1', 'Name1'), ['Name1']),
        ],
        aliasMismatchRows: [mismatchRow(importRow('m-1', 'NewAlias'), ['OldAlias'])],
      });
      const d = decisions([
        ['c-1', { kind: 'adoptSourceName' }], // wrong kind for a name-collision row
        ['m-1', { kind: 'replaceTarget' }], // wrong kind for an alias-mismatch row
        ['unknown-id', { kind: 'replaceTarget' }], // no row carries this key at all
      ]);

      expect(validateResolution(p, d, TRACKED)).toEqual({ ok: true });
      expect(buildTransferPlan(p, d, TRACKED).rows).toEqual([]);
    });
  });
});

describe('buildTransferPlan', () => {
  it('builds exactly preview.toAdd as add rows when there are no decisions (AK 5)', () => {
    const p = preview({ toAdd: [importRow('add-1', 'Foo'), importRow('add-2', 'Bar')] });

    const plan = buildTransferPlan(p, decisions([]), TRACKED);

    expect(plan.rows).toEqual([
      { action: 'add', source: importRow('add-1', 'Foo'), alias: 'Foo' },
      { action: 'add', source: importRow('add-2', 'Bar'), alias: 'Bar' },
    ]);
    expect(summarizeTransferPlan(plan)).toEqual({
      addCount: 2,
      removeCount: 0,
      removedEntryCount: 0,
    });
  });

  it('keeps an invalidNames toAdd row in the plan when there are no decisions (Runde 2, Finding 5)', () => {
    const invalidRow = importRow('add-1', 'has space');
    const p = preview({ toAdd: [invalidRow], invalidNames: ['has space'] });

    expect(validateResolution(p, decisions([]), TRACKED)).toEqual({ ok: true });
    const plan = buildTransferPlan(p, decisions([]), TRACKED);
    expect(plan.rows).toEqual([{ action: 'add', source: invalidRow, alias: 'has space' }]);
  });

  it('throws when the decisions fail validation instead of building a broken plan', () => {
    const p = preview({
      nameCollisionRows: [
        collisionRow(importRow('c-1', 'Old1'), emoteListItem('t-1', 'Old1'), ['Old1']),
        collisionRow(importRow('c-2', 'Old2'), emoteListItem('t-2', 'Old2'), ['Old2']),
      ],
    });
    const d = decisions([
      ['c-1', { kind: 'renameSource', alias: 'Shared' }],
      ['c-2', { kind: 'renameSource', alias: 'Shared' }],
    ]);

    expect(() => buildTransferPlan(p, d, TRACKED)).toThrow();
  });

  it('throws on a replace decision against an untracked target, with no separate validateResolution call', () => {
    const p = preview({
      nameCollisionRows: [
        collisionRow(importRow('c-1', 'Name1'), emoteListItem('t-1', 'Name1'), ['Name1']),
      ],
    });
    const d = decisions([['c-1', { kind: 'replaceTarget' }]]);

    expect(() => buildTransferPlan(p, d, UNTRACKED)).toThrow();
  });

  describe('row order in the plan', () => {
    it('orders every replace row first, then adopt, then add, then rename — regardless of source order', () => {
      const p = preview({
        toAdd: [importRow('add-1', 'Fresh')],
        nameCollisionRows: [
          // Rename appears before replace in source order, precisely to prove the plan does not
          // just mirror it.
          collisionRow(importRow('c-rename', 'Old'), emoteListItem('t-1', 'Old'), ['Old']),
          collisionRow(importRow('c-replace', 'Name2'), emoteListItem('t-2', 'Name2'), ['Name2']),
        ],
        aliasMismatchRows: [mismatchRow(importRow('m-1', 'NewAlias'), ['OldAlias'])],
      });
      const d = decisions([
        ['c-rename', { kind: 'renameSource', alias: 'Renamed' }],
        ['c-replace', { kind: 'replaceTarget' }],
        ['m-1', { kind: 'adoptSourceName' }],
      ]);

      const plan = buildTransferPlan(p, d, TRACKED);

      expect(plan.rows.map((row) => row.source.sevenTvEmoteId)).toEqual([
        'c-replace', // every replace row first
        'm-1', // then every adopt row
        'add-1', // then add rows (existing toAdd-then-collision group order)
        'c-rename', // then rename rows
      ]);
    });
  });
});

describe('summarizeTransferPlan + projectSlots — the slot projection for a name-conflict run', () => {
  it('derives {addCount: 1, removeCount: 0, removedEntryCount: 0} for a single rename', () => {
    const p = preview({
      nameCollisionRows: [
        collisionRow(importRow('c-1', 'Old'), emoteListItem('t-1', 'Old'), ['Old']),
      ],
    });
    const d = decisions([['c-1', { kind: 'renameSource', alias: 'Renamed' }]]);

    const summary = summarizeTransferPlan(buildTransferPlan(p, d, TRACKED));

    expect(summary).toEqual({ addCount: 1, removeCount: 0, removedEntryCount: 0 });
  });

  it('derives {addCount: 1, removeCount: 1, removedEntryCount: 1} for a plain replace', () => {
    const target = emoteListItem('t-1', 'Name1');
    const p = preview({
      nameCollisionRows: [collisionRow(importRow('c-1', 'Name1'), target, ['Name1'])],
    });
    const d = decisions([['c-1', { kind: 'replaceTarget' }]]);

    const summary = summarizeTransferPlan(buildTransferPlan(p, d, TRACKED));

    expect(summary).toEqual({ addCount: 1, removeCount: 1, removedEntryCount: 1 });
  });

  it('nets +1 for a mixed add + rename + replace-on-duplicate plan (3 ADDs − 2 removed entries)', () => {
    const duplicateTarget = emoteListItem('t-dup', 'Alias1');
    const p = preview({
      toAdd: [importRow('add-1', 'Fresh')],
      nameCollisionRows: [
        collisionRow(importRow('c-rename', 'Old'), emoteListItem('t-1', 'Old'), ['Old']),
        collisionRow(importRow('c-replace', 'Alias1'), duplicateTarget, ['Alias1', 'Alias2']),
      ],
    });
    const d = decisions([
      ['c-rename', { kind: 'renameSource', alias: 'Renamed' }],
      ['c-replace', { kind: 'replaceTarget' }],
    ]);

    const summary = summarizeTransferPlan(buildTransferPlan(p, d, TRACKED));

    expect(summary).toEqual({ addCount: 3, removeCount: 1, removedEntryCount: 2 });
    expect(projectSlots(0, 10, summary.addCount - summary.removedEntryCount)).toEqual({
      projected: 1,
      capacity: 10,
      overflow: false,
    });
  });

  it('nets −1 for a replace on a target with one named alias and one aliasless entry', () => {
    const target = emoteListItem('t-1', 'Name1');
    const p = preview({
      nameCollisionRows: [collisionRow(importRow('c-1', 'Name1'), target, ['Name1'], true)],
    });
    const d = decisions([['c-1', { kind: 'replaceTarget' }]]);

    const summary = summarizeTransferPlan(buildTransferPlan(p, d, TRACKED));

    expect(summary).toEqual({ addCount: 1, removeCount: 1, removedEntryCount: 2 });
    expect(projectSlots(10, 100, summary.addCount - summary.removedEntryCount)).toEqual({
      projected: 9,
      capacity: 100,
      overflow: false,
    });
  });
});
