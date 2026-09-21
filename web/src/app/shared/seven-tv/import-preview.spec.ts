import { describe, expect, it } from 'vitest';

import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import { buildImportPreview } from './import-preview';

function source(rows: ImportRow[]): ImportSource {
  return {
    origin: { kind: 'channel', channelName: 'sensitron' },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}

describe('buildImportPreview', () => {
  it('separates already-present rows (matched by id) from name collisions and the rest', () => {
    const target: EmoteListItem[] = [
      { sevenTvEmoteId: 'existing-1', name: 'AlreadyThere' },
      { sevenTvEmoteId: 'existing-2', name: 'PogU' },
    ];

    const result = buildImportPreview(
      source([
        // Same id as an existing target emote, but a different alias — an alias mismatch, not a
        // name collision (identity is by sevenTvEmoteId, never by name) and not alreadyPresent
        // either (the target's only alias for this id does not match the source's).
        { sevenTvEmoteId: 'existing-1', name: 'RenamedOnSource' },
        // New id whose name collides with a target emote's name — since spec 2026-09-20 this is
        // pulled *out* of toAdd entirely, not merely reported (revises the 2026-09-06 reading).
        { sevenTvEmoteId: 'new-1', name: 'PogU' },
        // Clean addition, no overlap at all.
        { sevenTvEmoteId: 'new-2', name: 'Kappa' },
      ]),
      target,
    );

    // 'existing-1' shares an id with the target but not its alias — an alias mismatch, not
    // alreadyPresent (target has only the one alias for that id, and it does not match).
    expect(result.alreadyPresent).toBe(0);
    expect(result.aliasMismatches).toEqual([
      { sourceName: 'RenamedOnSource', targetAlias: 'AlreadyThere' },
    ]);
    expect(result.toAdd).toEqual([{ sevenTvEmoteId: 'new-2', name: 'Kappa' }]);
    expect(result.nameCollisions).toEqual(['PogU']);
    expect(result.invalidNames).toEqual([]);
  });

  it('excludes a name collision from toAdd entirely (spec 2026-09-20, AK 37)', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'Collides' }];

    const result = buildImportPreview(
      source([{ sevenTvEmoteId: 'new-1', name: 'Collides' }]),
      target,
    );

    expect(result.nameCollisions).toEqual(['Collides']);
    expect(result.toAdd).toEqual([]);
    expect(result.alreadyPresent).toBe(0);
    expect(result.aliasMismatches).toEqual([]);
  });

  it('compares names ordinally — a case difference is not a collision', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'PogU' }];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'pogu' }]), target);

    expect(result.nameCollisions).toEqual([]);
    expect(result.toAdd).toEqual([{ sevenTvEmoteId: 'new-1', name: 'pogu' }]);
  });

  it('reports a name colliding with more than one duplicate target name only once', () => {
    const target: EmoteListItem[] = [
      { sevenTvEmoteId: 'existing-1', name: 'Dupe' },
      { sevenTvEmoteId: 'existing-2', name: 'Dupe' },
    ];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'Dupe' }]), target);

    expect(result.nameCollisions).toEqual(['Dupe']);
    expect(result.nameCollisionRowCount).toBe(1);
  });

  it('counts every skipped row when two different source ids collide on the same target name', () => {
    // Codex Sol P2: nameCollisions used to be the only signal for "how many rows were skipped" —
    // but it dedupes by name, so two distinct source ids sharing one colliding alias both leave
    // the run while the name list shows only one entry. The row count must not silently halve.
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'Dupe' }];

    const result = buildImportPreview(
      source([
        { sevenTvEmoteId: 'new-1', name: 'Dupe' },
        { sevenTvEmoteId: 'new-2', name: 'Dupe' },
      ]),
      target,
    );

    expect(result.nameCollisions).toEqual(['Dupe']);
    expect(result.nameCollisionRowCount).toBe(2);
    expect(result.toAdd).toEqual([]);
  });

  it('keeps the toAdd/alreadyPresent/nameCollisionRowCount/aliasMismatches sum equal to the source row count', () => {
    // The prüfbare Invariante from the task: every source row lands in exactly one of the four
    // groups (invalidNames is a subset of toAdd, not a fifth group), over a genuinely mixed list —
    // two ids sharing a colliding name, one alias mismatch, one already-present row, two clean adds.
    const target: EmoteListItem[] = [
      { sevenTvEmoteId: 'present-1', name: 'Present' },
      { sevenTvEmoteId: 'mismatch-1', name: 'TargetAlias' },
      { sevenTvEmoteId: 'collision-target', name: 'Dupe' },
    ];
    const rows: ImportRow[] = [
      { sevenTvEmoteId: 'present-1', name: 'Present' },
      { sevenTvEmoteId: 'mismatch-1', name: 'SourceAlias' },
      { sevenTvEmoteId: 'collision-source-1', name: 'Dupe' },
      { sevenTvEmoteId: 'collision-source-2', name: 'Dupe' },
      { sevenTvEmoteId: 'new-1', name: 'Kappa' },
      { sevenTvEmoteId: 'new-2', name: 'PogU' },
    ];

    const result = buildImportPreview(source(rows), target);

    expect(
      result.toAdd.length +
        result.alreadyPresent +
        result.nameCollisionRowCount +
        result.aliasMismatches.length,
    ).toBe(rows.length);
    expect(result.alreadyPresent).toBe(1);
    expect(result.aliasMismatches.length).toBe(1);
    expect(result.nameCollisionRowCount).toBe(2);
    expect(result.toAdd.length).toBe(2);
  });

  it('returns an empty toAdd list when every source row is already present', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'PogU' }];

    const result = buildImportPreview(
      source([{ sevenTvEmoteId: 'existing-1', name: 'PogU' }]),
      target,
    );

    expect(result.toAdd).toEqual([]);
    expect(result.alreadyPresent).toBe(1);
    expect(result.nameCollisions).toEqual([]);
  });

  // 7TV v4's alias validator accepts Unicode — v3's did not, which is what the old version of this
  // check (and this test) got wrong. See `isNameRejectedBySevenTv` for the evidence.
  it('does not flag a Unicode alias — 7TV v4 accepts letters of any script in the alias', () => {
    const target: EmoteListItem[] = [];

    const result = buildImportPreview(
      source([
        { sevenTvEmoteId: 'new-1', name: 'Gänsehosen' },
        { sevenTvEmoteId: 'new-2', name: 'Привет' },
        { sevenTvEmoteId: 'new-3', name: 'Kappa' },
      ]),
      target,
    );

    expect(result.invalidNames).toEqual([]);
    expect(result.toAdd.map((row) => row.sevenTvEmoteId)).toEqual(['new-1', 'new-2', 'new-3']);
  });

  it('flags an alias containing a space as invalid — measured rejected by 7TV v4', () => {
    const target: EmoteListItem[] = [];

    const result = buildImportPreview(
      source([
        { sevenTvEmoteId: 'new-1', name: 'Two Words' },
        { sevenTvEmoteId: 'new-2', name: 'Kappa' },
      ]),
      target,
    );

    expect(result.invalidNames).toEqual(['Two Words']);
    // Informational only, like nameCollisions: the rows are not filtered out of toAdd.
    expect(result.toAdd.map((row) => row.sevenTvEmoteId)).toEqual(['new-1', 'new-2']);
  });

  it('flags an alias containing a measured-rejected punctuation character', () => {
    const target: EmoteListItem[] = [];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'a/b' }]), target);

    expect(result.invalidNames).toEqual(['a/b']);
  });

  it('flags an alias over 100 characters as invalid', () => {
    const target: EmoteListItem[] = [];
    const tooLong = 'a'.repeat(101);

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: tooLong }]), target);

    expect(result.invalidNames).toEqual([tooLong]);
  });

  it('does not flag an alias at exactly the 100-character limit', () => {
    const target: EmoteListItem[] = [];
    const atLimit = 'a'.repeat(100);

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: atLimit }]), target);

    expect(result.invalidNames).toEqual([]);
  });

  it('does not flag a name that is only an ASCII name collision', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'PogU' }];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'PogU' }]), target);

    expect(result.nameCollisions).toEqual(['PogU']);
    expect(result.invalidNames).toEqual([]);
  });

  it('reports a name collision but not also invalidNames — the row never reaches toAdd', () => {
    // Before spec 2026-09-20 this row stayed in toAdd and was flagged both ways; now a name
    // collision excludes it from toAdd outright, and invalidNames only ever looks at rows that are
    // actually still offered — flagging an excluded row as "invalid too" would be noise about a
    // row nobody is about to submit.
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'a/b' }];

    const result = buildImportPreview(source([{ sevenTvEmoteId: 'new-1', name: 'a/b' }]), target);

    expect(result.nameCollisions).toEqual(['a/b']);
    expect(result.invalidNames).toEqual([]);
    expect(result.toAdd).toEqual([]);
  });

  it('groups an alias mismatch separately from alreadyPresent, with source and target alias', () => {
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'existing-1', name: 'TargetAlias' }];

    const result = buildImportPreview(
      source([{ sevenTvEmoteId: 'existing-1', name: 'SourceAlias' }]),
      target,
    );

    expect(result.alreadyPresent).toBe(0);
    expect(result.aliasMismatches).toEqual([
      { sourceName: 'SourceAlias', targetAlias: 'TargetAlias' },
    ]);
    expect(result.toAdd).toEqual([]);
  });

  it('#74 grenzfall: a duplicate target id with one matching alias is alreadyPresent, not a mismatch', () => {
    // 7TV's own set-merge defect (#74) can leave a target set with two rows under the same id but
    // different aliases. The source row must count as alreadyPresent the moment *either* of them
    // agrees with it — never as an alias mismatch just because the *other* duplicate differs.
    const target: EmoteListItem[] = [
      { sevenTvEmoteId: 'dup-1', name: 'AliasA' },
      { sevenTvEmoteId: 'dup-1', name: 'AliasB' },
    ];

    const result = buildImportPreview(
      source([{ sevenTvEmoteId: 'dup-1', name: 'AliasA' }]),
      target,
    );

    expect(result.alreadyPresent).toBe(1);
    expect(result.aliasMismatches).toEqual([]);
  });

  it('#74 grenzfall: a duplicate target id with neither alias matching is one alias mismatch', () => {
    const target: EmoteListItem[] = [
      { sevenTvEmoteId: 'dup-1', name: 'AliasA' },
      { sevenTvEmoteId: 'dup-1', name: 'AliasB' },
    ];

    const result = buildImportPreview(
      source([{ sevenTvEmoteId: 'dup-1', name: 'AliasC' }]),
      target,
    );

    expect(result.alreadyPresent).toBe(0);
    expect(result.aliasMismatches).toEqual([{ sourceName: 'AliasC', targetAlias: 'AliasA' }]);
  });

  // AK 38: the exact proportions measured for HandOfBlood's Halloween-set import — 762 source
  // rows split 338 already-in-the-target (of which ~10 as an alias mismatch) / 192 name
  // collisions / 232 genuinely new, so the target's occupancy (687) plus toAdd (232) projects to
  // 919 without an overflow banner (`slot-projection.spec.ts`/`import-confirm-dialog.spec.ts`
  // exercise the projection itself; this only has to prove the counts feeding it).
  it('reproduces the Halloween-set import proportions (AK 38)', () => {
    const ALREADY_PRESENT_COUNT = 328;
    const ALIAS_MISMATCH_COUNT = 10;
    const NAME_COLLISION_COUNT = 192;
    const TO_ADD_COUNT = 232;

    const target: EmoteListItem[] = [];
    const sourceRows: ImportRow[] = [];

    for (let index = 0; index < ALREADY_PRESENT_COUNT; index++) {
      const id = `present-${index}`;
      target.push({ sevenTvEmoteId: id, name: `Present${index}` });
      sourceRows.push({ sevenTvEmoteId: id, name: `Present${index}` });
    }
    for (let index = 0; index < ALIAS_MISMATCH_COUNT; index++) {
      const id = `mismatch-${index}`;
      target.push({ sevenTvEmoteId: id, name: `TargetAlias${index}` });
      sourceRows.push({ sevenTvEmoteId: id, name: `SourceAlias${index}` });
    }
    for (let index = 0; index < NAME_COLLISION_COUNT; index++) {
      // Distinct ids, deliberately colliding names.
      target.push({ sevenTvEmoteId: `collision-target-${index}`, name: `Collide${index}` });
      sourceRows.push({ sevenTvEmoteId: `collision-source-${index}`, name: `Collide${index}` });
    }
    for (let index = 0; index < TO_ADD_COUNT; index++) {
      sourceRows.push({ sevenTvEmoteId: `new-${index}`, name: `New${index}` });
    }

    expect(sourceRows.length).toBe(762);

    const result = buildImportPreview(source(sourceRows), target);

    expect(result.alreadyPresent).toBe(ALREADY_PRESENT_COUNT);
    expect(result.aliasMismatches.length).toBe(ALIAS_MISMATCH_COUNT);
    expect(result.nameCollisions.length).toBe(NAME_COLLISION_COUNT);
    // Every collision here has a distinct name, so the deduplicated list and the row count agree —
    // this is the case the AK 38 numbers were measured against, not the shared-alias case covered
    // separately above.
    expect(result.nameCollisionRowCount).toBe(NAME_COLLISION_COUNT);
    expect(result.toAdd.length).toBe(TO_ADD_COUNT);
    expect(
      result.toAdd.length +
        result.alreadyPresent +
        result.nameCollisionRowCount +
        result.aliasMismatches.length,
    ).toBe(sourceRows.length);
  });
});
