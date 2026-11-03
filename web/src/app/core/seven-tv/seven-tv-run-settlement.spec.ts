import { describe, expect, it } from 'vitest';

import { RunQueueItem, RunResult } from './seven-tv-run-engine';
import { SevenTvSetEntries } from './seven-tv-set-entries';
import {
  EntryBaseline,
  entryPostCheckOwed,
  entryTimeKey,
  findUnexpectedRemovals,
  freezeBaselineGroup,
  mergeIntoBaseline,
  settleDeleteResult,
  settleEntryDeleteRun,
  settleRestoreResult,
  unknownCount,
} from './seven-tv-run-settlement';

function queueItem(overrides: Partial<RunQueueItem> & Pick<RunQueueItem, 'key'>): RunQueueItem {
  return {
    sevenTvEmoteId: overrides.key,
    name: overrides.key,
    status: 'unknown',
    completedSteps: 0,
    failedStep: 0,
    ...overrides,
  };
}

function runResult(items: RunQueueItem[]): RunResult {
  return {
    items,
    doneKeys: items.filter((item) => item.status === 'done').map((item) => item.key),
    startedAt: 1000,
    finishedAt: 2000,
  };
}

function setEntries(overrides: Partial<SevenTvSetEntries> = {}): SevenTvSetEntries {
  return {
    aliasesById: new Map(),
    aliaslessIds: new Set(),
    defaultNameById: new Map(),
    animatedById: new Map(),
    occupiedSlots: 0,
    complete: true,
    readState: 'complete',
    omittedCount: 0,
    ...overrides,
  };
}

describe('settleDeleteResult', () => {
  it('confirms an unknown row done when the read shows the id gone from the set, and clears its error', () => {
    const item = queueItem({
      key: '7tv-1',
      status: 'unknown',
      errorMessage: 'no answer within 20000 ms',
    });
    const result = settleDeleteResult(runResult([item]), setEntries({ aliasesById: new Map() }));

    expect(result.items[0]).toMatchObject({
      status: 'done',
      completedSteps: 1,
      failedStep: null,
      errorMessage: undefined,
    });
    expect(result.doneKeys).toEqual(['7tv-1']);
  });

  it('leaves the row unknown when the id is still fully in the set', () => {
    const item = queueItem({ key: '7tv-1', status: 'unknown' });
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['PogU']]]) });
    const result = settleDeleteResult(runResult([item]), entries);

    expect(result.items[0].status).toBe('unknown');
    expect(result.doneKeys).toEqual([]);
  });

  // A #74 duplicate cell's one REMOVE takes both of its aliases at once — a read that finds only
  // one of the two still there is exactly as inconclusive as finding both: the id is still in the
  // set, whatever 7TV did or did not finish applying.
  it("leaves the row unknown when only one of the id's two original aliases is still in the set", () => {
    const item = queueItem({ key: '7tv-1', status: 'unknown' });
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['A']]]) });
    const result = settleDeleteResult(runResult([item]), entries);

    expect(result.items[0].status).toBe('unknown');
  });

  it('leaves the row unknown when the id is still there under a different alias than before', () => {
    // A third party could just as well have re-added the id after our own REMOVE went through —
    // the read cannot tell the two apart, so "still there" never becomes done (Plan-275 N1).
    const item = queueItem({ key: '7tv-1', status: 'unknown' });
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['SomeoneElsesAlias']]]) });
    const result = settleDeleteResult(runResult([item]), entries);

    expect(result.items[0].status).toBe('unknown');
  });

  // seven-tv-set-entries.ts sets an (empty-array) aliasesById entry for an id that only sits in the
  // set aliasless — `.has()` alone must therefore already say "still there" for such an id.
  it('leaves the row unknown when the id is still there only as an aliasless entry', () => {
    const item = queueItem({ key: '7tv-1', status: 'unknown' });
    const entries = setEntries({
      aliasesById: new Map([['7tv-1', []]]),
      aliaslessIds: new Set(['7tv-1']),
    });
    const result = settleDeleteResult(runResult([item]), entries);

    expect(result.items[0].status).toBe('unknown');
  });

  it('leaves every unknown row untouched when entries is null (no read, a failed read, or a timeout)', () => {
    const item = queueItem({ key: '7tv-1', status: 'unknown' });
    const result = settleDeleteResult(runResult([item]), null);

    expect(result.items[0]).toBe(item);
    expect(result.doneKeys).toEqual([]);
  });

  it('leaves every unknown row untouched when the read came back incomplete', () => {
    const item = queueItem({ key: '7tv-1', status: 'unknown' });
    const entries = setEntries({ aliasesById: new Map(), complete: false });
    const result = settleDeleteResult(runResult([item]), entries);

    expect(result.items[0]).toBe(item);
  });

  it('returns rows with any other status referentially unchanged, even when the read would confirm them if they were unknown', () => {
    // aliasesById is empty, so every id here reads as "gone" — a settlement that forgot to gate on
    // `status === 'unknown'` would flip every one of these to `done` instead of leaving them alone.
    const done = queueItem({ key: '7tv-1', status: 'done', completedSteps: 1, failedStep: null });
    const failed = queueItem({ key: '7tv-2', status: 'failed' });
    const cancelled = queueItem({ key: '7tv-3', status: 'cancelled' });
    const pending = queueItem({ key: '7tv-4', status: 'pending' });
    const result = settleDeleteResult(
      runResult([done, failed, cancelled, pending]),
      setEntries({ aliasesById: new Map() }),
    );

    expect(result.items[0]).toBe(done);
    expect(result.items[1]).toBe(failed);
    expect(result.items[2]).toBe(cancelled);
    expect(result.items[3]).toBe(pending);
  });

  it('recomputes doneKeys in queue order, including rows that were already done', () => {
    const alreadyDone = queueItem({
      key: '7tv-1',
      status: 'done',
      completedSteps: 1,
      failedStep: null,
    });
    const clearedUnknown = queueItem({ key: '7tv-2', status: 'unknown' });
    const staysUnknown = queueItem({ key: '7tv-3', status: 'unknown' });
    const entries = setEntries({ aliasesById: new Map([['7tv-3', ['StillHere']]]) });
    const result = settleDeleteResult(
      runResult([alreadyDone, clearedUnknown, staysUnknown]),
      entries,
    );

    expect(result.doneKeys).toEqual(['7tv-1', '7tv-2']);
  });

  it('never mutates the RunResult or its RunQueueItem objects in place', () => {
    const item = queueItem({
      key: '7tv-1',
      status: 'unknown',
      errorMessage: 'no answer within 20000 ms',
    });
    const original = runResult([item]);
    // A snapshot of the item's own fields, taken before the call — a copy that in-place mutation of
    // `item` itself cannot reach, unlike a shallow copy of `original.items` (whose entries would
    // still be the very same, and therefore still-mutated, objects).
    const snapshotBeforeCall = structuredClone(item);

    settleDeleteResult(original, setEntries({ aliasesById: new Map() }));

    expect(original.items[0]).toBe(item);
    expect(original.items[0]).toEqual(snapshotBeforeCall);
  });
});

describe('settleRestoreResult', () => {
  it('confirms an unknown row done when the read shows the restored alias on the id, and clears its error', () => {
    const item = queueItem({
      key: '7tv-1#PogU',
      sevenTvEmoteId: '7tv-1',
      status: 'unknown',
      errorMessage: 'no answer within 20000 ms',
    });
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['PogU']]]) });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0]).toMatchObject({
      status: 'done',
      completedSteps: 1,
      failedStep: null,
      errorMessage: undefined,
    });
    expect(result.doneKeys).toEqual(['7tv-1#PogU']);
  });

  it('leaves the row unknown when the id is missing from the set entirely', () => {
    const item = queueItem({ key: '7tv-1#PogU', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const result = settleRestoreResult(runResult([item]), setEntries(), aliasByKey, new Map());

    expect(result.items[0].status).toBe('unknown');
  });

  it('leaves the row unknown when the id is there only under a foreign alias', () => {
    const item = queueItem({ key: '7tv-1#PogU', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['SomeoneElsesAlias']]]) });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0].status).toBe('unknown');
  });

  it('leaves a named row unknown when the id is there only as an aliasless entry', () => {
    const item = queueItem({ key: '7tv-1#PogU', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const entries = setEntries({
      aliasesById: new Map([['7tv-1', []]]),
      aliaslessIds: new Set(['7tv-1']),
    });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0].status).toBe('unknown');
  });

  // Alias matching is exact, never case-normalized — 7TV treats "pogu" and "PogU" as different
  // aliases, so a read finding only the former is no confirmation of an `ADD` for the latter.
  it('leaves a named row unknown when the set holds only a differently-cased alias', () => {
    const item = queueItem({ key: '7tv-1#PogU', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['pogu']]]) });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0].status).toBe('unknown');
  });

  it('leaves the row unknown when its key is missing from aliasByKey entirely (fail closed against a wiring bug)', () => {
    const item = queueItem({ key: '7tv-1#PogU', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    // aliaslessIds alone would confirm this id via the null-alias path — without the `.has()` guard,
    // a missing key is read as a null alias and this row would wrongly become `done`.
    const entries = setEntries({ aliaslessIds: new Set(['7tv-1']) });
    const result = settleRestoreResult(runResult([item]), entries, new Map(), new Map());

    expect(result.items[0]).toBe(item);
  });

  // A null alias only ever comes from a transfer-run protocol file (Spec #254 F5): the row restores
  // an entry that originally had no alias at all.
  it('confirms a null-alias row done when the read shows it landed aliasless', () => {
    const item = queueItem({ key: '7tv-1#', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map<string, string | null>([['7tv-1#', null]]);
    const entries = setEntries({ aliaslessIds: new Set(['7tv-1']) });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0].status).toBe('done');
  });

  it('confirms a null-alias row done from the live default name when the file recorded none', () => {
    const item = queueItem({ key: '7tv-1#', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map<string, string | null>([['7tv-1#', null]]);
    const entries = setEntries({
      aliasesById: new Map([['7tv-1', ['Pog']]]),
      defaultNameById: new Map([['7tv-1', 'Pog']]),
    });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0].status).toBe('done');
  });

  it("falls back to the file's own recorded default name when the live read has none for a null-alias row", () => {
    const item = queueItem({ key: '7tv-1#', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map<string, string | null>([['7tv-1#', null]]);
    const defaultNameByKey = new Map([['7tv-1#', 'Pog']]);
    // No entries.defaultNameById entry for '7tv-1' at all — the live read has nothing to say.
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['Pog']]]) });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, defaultNameByKey);

    expect(result.items[0].status).toBe('done');
  });

  it("falls back to the file's own recorded default name when the live one is an explicit empty string", () => {
    const item = queueItem({ key: '7tv-1#', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map<string, string | null>([['7tv-1#', null]]);
    const defaultNameByKey = new Map([['7tv-1#', 'Pog']]);
    const entries = setEntries({
      aliasesById: new Map([['7tv-1', ['Pog']]]),
      defaultNameById: new Map([['7tv-1', '']]),
    });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, defaultNameByKey);

    expect(result.items[0].status).toBe('done');
  });

  // The core of Plan-275's live-first ordering: 7TV names an aliasless ADD after the emote's
  // *current* default name, so a stale name the file recorded at delete time must never win over a
  // live name that disagrees with it.
  it('prefers the live default name over a differing one recorded in the file', () => {
    const item = queueItem({ key: '7tv-1#', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map<string, string | null>([['7tv-1#', null]]);
    const defaultNameByKey = new Map([['7tv-1#', 'OldName']]);
    // Only the live name is actually in the set — matching on the file's stale name instead would
    // wrongly leave this row unknown.
    const entries = setEntries({
      aliasesById: new Map([['7tv-1', ['NewName']]]),
      defaultNameById: new Map([['7tv-1', 'NewName']]),
    });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, defaultNameByKey);

    expect(result.items[0].status).toBe('done');
  });

  it('leaves a null-alias row unknown when the known default name sits under no alias of the id', () => {
    const item = queueItem({ key: '7tv-1#', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map<string, string | null>([['7tv-1#', null]]);
    const entries = setEntries({
      aliasesById: new Map([['7tv-1', ['SomeoneElsesAlias']]]),
      defaultNameById: new Map([['7tv-1', 'Pog']]),
    });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0].status).toBe('unknown');
  });

  it('leaves a null-alias row unknown when no default name is known from either source', () => {
    const item = queueItem({ key: '7tv-1#', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map<string, string | null>([['7tv-1#', null]]);
    const entries = setEntries({
      aliasesById: new Map([['7tv-1', ['Pog']]]),
      defaultNameById: new Map([['7tv-1', '']]),
    });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0].status).toBe('unknown');
  });

  it('leaves every unknown row untouched when entries is null', () => {
    const item = queueItem({ key: '7tv-1#PogU', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const result = settleRestoreResult(runResult([item]), null, aliasByKey, new Map());

    expect(result.items[0]).toBe(item);
  });

  it('leaves every unknown row untouched when the read came back incomplete', () => {
    const item = queueItem({ key: '7tv-1#PogU', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['PogU']]]), complete: false });
    const result = settleRestoreResult(runResult([item]), entries, aliasByKey, new Map());

    expect(result.items[0]).toBe(item);
  });

  it('returns rows with any other status referentially unchanged, even when the read would confirm them if they were unknown', () => {
    const done = queueItem({
      key: '7tv-1#PogU',
      sevenTvEmoteId: '7tv-1',
      status: 'done',
      completedSteps: 1,
      failedStep: null,
    });
    const failed = queueItem({ key: '7tv-2#KEKW', sevenTvEmoteId: '7tv-2', status: 'failed' });
    // Every alias below is present exactly where its row would need it to confirm — a settlement
    // that forgot to gate on `status === 'unknown'` would flip `failed` to `done` here.
    const aliasByKey = new Map([
      ['7tv-1#PogU', 'PogU'],
      ['7tv-2#KEKW', 'KEKW'],
    ]);
    const entries = setEntries({
      aliasesById: new Map([
        ['7tv-1', ['PogU']],
        ['7tv-2', ['KEKW']],
      ]),
    });
    const result = settleRestoreResult(runResult([done, failed]), entries, aliasByKey, new Map());

    expect(result.items[0]).toBe(done);
    expect(result.items[1]).toBe(failed);
  });

  it('dedupes two aliases of one id independently, clearing only the one the read confirms', () => {
    const first = queueItem({ key: '7tv-1#PogU', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const second = queueItem({ key: '7tv-1#PogU2', sevenTvEmoteId: '7tv-1', status: 'unknown' });
    const aliasByKey = new Map([
      ['7tv-1#PogU', 'PogU'],
      ['7tv-1#PogU2', 'PogU2'],
    ]);
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['PogU']]]) });
    const result = settleRestoreResult(runResult([first, second]), entries, aliasByKey, new Map());

    expect(result.items[0].status).toBe('done');
    expect(result.items[1].status).toBe('unknown');
    expect(result.doneKeys).toEqual(['7tv-1#PogU']);
  });

  it('recomputes doneKeys in queue order', () => {
    const clearedUnknown = queueItem({
      key: '7tv-1#PogU',
      sevenTvEmoteId: '7tv-1',
      status: 'unknown',
    });
    const alreadyDone = queueItem({
      key: '7tv-2#KEKW',
      status: 'done',
      completedSteps: 1,
      failedStep: null,
    });
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['PogU']]]) });
    const result = settleRestoreResult(
      runResult([clearedUnknown, alreadyDone]),
      entries,
      aliasByKey,
      new Map(),
    );

    expect(result.doneKeys).toEqual(['7tv-1#PogU', '7tv-2#KEKW']);
  });

  it('never mutates the RunResult or its RunQueueItem objects in place', () => {
    const item = queueItem({
      key: '7tv-1#PogU',
      sevenTvEmoteId: '7tv-1',
      status: 'unknown',
      errorMessage: 'no answer within 20000 ms',
    });
    const original = runResult([item]);
    const snapshotBeforeCall = structuredClone(item);
    const aliasByKey = new Map([['7tv-1#PogU', 'PogU']]);
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['PogU']]]) });

    settleRestoreResult(original, entries, aliasByKey, new Map());

    expect(original.items[0]).toBe(item);
    expect(original.items[0]).toEqual(snapshotBeforeCall);
  });
});

describe('unknownCount', () => {
  it('counts only unknown rows', () => {
    const items = [
      queueItem({ key: '1', status: 'unknown' }),
      queueItem({ key: '2', status: 'done' }),
      queueItem({ key: '3', status: 'unknown' }),
      queueItem({ key: '4', status: 'failed' }),
      queueItem({ key: '5', status: 'cancelled' }),
    ];

    expect(unknownCount(items)).toBe(2);
  });

  it('returns 0 for an empty list and for a list with no unknown rows', () => {
    expect(unknownCount([])).toBe(0);
    expect(unknownCount([queueItem({ key: '1', status: 'done' })])).toBe(0);
  });
});

// Plan-378 4.4 step 5 (S2, A6): the comparison baseline of a slot delete run.
describe('mergeIntoBaseline', () => {
  const T1 = '2026-10-13T08:00:00.000Z';
  const T2 = '2026-10-13T09:00:00Z';
  const T3 = '2026-10-13T10:00:00Z';

  function aliasesOf(baseline: EntryBaseline, id = 'x'): Record<string, (string | null)[]> {
    return Object.fromEntries(
      [...(baseline.get(id) ?? new Map()).values()].map((group) => [group.addedAt, group.aliases]),
    );
  }

  it('compares add dates in milliseconds, whatever the number of fraction digits', () => {
    expect(entryTimeKey('2026-10-13T08:00:00Z')).toBe(entryTimeKey('2026-10-13T08:00:00.000000Z'));
    expect(entryTimeKey(null)).toBe(entryTimeKey('not a date'));
    expect(entryTimeKey(null)).not.toBe(entryTimeKey(T1));
  });

  it('takes each read as it is while the run has removed nothing of the emote yet', () => {
    const baseline: EntryBaseline = new Map();
    mergeIntoBaseline(baseline, 'x', [
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T2 },
    ]);
    // b left before the run touched the emote — none of the run's requests can have taken it.
    mergeIntoBaseline(baseline, 'x', [{ alias: 'a', addedAt: T1 }]);

    expect(aliasesOf(baseline)).toEqual({ [T1]: ['a'] });
  });

  it('accumulates once a removal of the emote was let through and never forgets an entry', () => {
    const baseline: EntryBaseline = new Map();
    mergeIntoBaseline(baseline, 'x', [
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T2 },
      { alias: 'c', addedAt: T3 },
    ]);
    freezeBaselineGroup(baseline, 'x', entryTimeKey(T1));
    // The next read misses b (and a, which the run removed): both stay in the baseline.
    mergeIntoBaseline(baseline, 'x', [{ alias: 'c', addedAt: T3 }]);
    // An entry another editor added since joins it.
    mergeIntoBaseline(baseline, 'x', [
      { alias: 'c', addedAt: T3 },
      { alias: 'd', addedAt: '2026-10-13T11:00:00Z' },
    ]);

    expect(aliasesOf(baseline)).toEqual({
      [T1]: ['a'],
      [T2]: ['b'],
      [T3]: ['c'],
      '2026-10-13T11:00:00Z': ['d'],
    });
  });

  it('takes a later alias of a known, unfrozen date as a rename, but keeps a frozen date as it was', () => {
    const baseline: EntryBaseline = new Map();
    mergeIntoBaseline(baseline, 'x', [
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T1 },
      { alias: 'c', addedAt: T2 },
    ]);
    freezeBaselineGroup(baseline, 'x', entryTimeKey(T1));
    mergeIntoBaseline(baseline, 'x', [
      { alias: 'z', addedAt: T1 },
      { alias: 'a', addedAt: T1 },
      { alias: 'cc', addedAt: T2 },
    ]);

    expect(aliasesOf(baseline)).toEqual({ [T1]: ['a', 'b'], [T2]: ['cc'] });
  });

  /*
   * #454 review: a gap read proves presence only, so it must never replace the baseline — an entry
   * it does not list may be hidden, not gone.
   * protects: the collateral check after a removal keeps an entry a complete read established.
   * fails_when: merging a gap read drops a date of an unfrozen baseline.
   * no_claim: complete reads, which still replace an unfrozen baseline (test above).
   */
  it('only accumulates a gap read, even while nothing of the emote is frozen', () => {
    const baseline: EntryBaseline = new Map();
    mergeIntoBaseline(baseline, 'x', [
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T2 },
    ]);
    mergeIntoBaseline(baseline, 'x', [{ alias: 'a', addedAt: T1 }], { gap: true });
    mergeIntoBaseline(
      baseline,
      'x',
      [
        { alias: 'a2', addedAt: T1 },
        { alias: 'c', addedAt: T3 },
      ],
      { gap: true },
    );

    expect(aliasesOf(baseline)).toEqual({ [T1]: ['a2'], [T2]: ['b'], [T3]: ['c'] });
  });
});

// Plan-378 4.5 (S5, T-3, T-4): clearing up and checking a slot delete run per entry.
describe('slot delete settlement', () => {
  const T1 = '2026-10-13T08:00:00Z';
  const T2 = '2026-10-13T09:00:00Z';

  function entryItem(
    alias: string,
    addedAt: string,
    status: RunQueueItem['status'],
    sevenTvEmoteId = 'x',
  ): RunQueueItem {
    return queueItem({
      key: `${sevenTvEmoteId}#${alias}`,
      sevenTvEmoteId,
      name: alias,
      status,
      entry: { slotId: `slot-${alias}`, alias, addedAt, cardKey: alias },
    });
  }

  function baselineOf(entries: { alias: string | null; addedAt: string }[]): EntryBaseline {
    const baseline: EntryBaseline = new Map();
    mergeIntoBaseline(baseline, 'x', entries);
    return baseline;
  }

  function datedRead(entries: { alias: string | null; addedAt: string }[], complete = true) {
    return setEntries({
      aliasesById: new Map([['x', entries.flatMap((entry) => entry.alias ?? [])]]),
      entriesById: new Map([['x', entries]]),
      complete,
    });
  }

  it('confirms an unknown entry row only when its alias is gone and fewer entries are left at its date', () => {
    const baseline = baselineOf([
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T2 },
    ]);
    const gone = settleEntryDeleteRun(
      runResult([entryItem('a', T1, 'unknown')]),
      datedRead([{ alias: 'b', addedAt: T2 }]),
      baseline,
    );
    expect(gone.result.items[0].status).toBe('done');
    expect(gone.result.doneKeys).toEqual(['x#a']);
    expect(gone.unexpectedRemovals).toEqual([]);

    // Renamed rather than removed: same count at the date, another alias — positive only.
    const renamed = settleEntryDeleteRun(
      runResult([entryItem('a', T1, 'unknown')]),
      datedRead([
        { alias: 'c', addedAt: '2026-10-13T08:00:00.000000Z' },
        { alias: 'b', addedAt: T2 },
      ]),
      baseline,
    );
    expect(renamed.result.items[0].status).toBe('unknown');
    expect(renamed.unexpectedRemovals).toEqual([]);
  });

  it('confirms no more unknown rows of one tie date than entries are gone there', () => {
    const baseline = baselineOf([
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T1 },
    ]);
    const settled = settleEntryDeleteRun(
      runResult([entryItem('a', T1, 'unknown'), entryItem('b', T1, 'unknown')]),
      datedRead([{ alias: 'c', addedAt: T1 }]),
      baseline,
    );

    expect(settled.result.items.map((item) => item.status)).toEqual(['done', 'unknown']);
  });

  it('settles id rows as before next to entry rows', () => {
    const baseline = baselineOf([{ alias: 'a', addedAt: T1 }]);
    const idGone = queueItem({ key: 'gone', status: 'unknown' });
    const idThere = queueItem({ key: 'there', status: 'unknown' });
    const read = setEntries({
      aliasesById: new Map([['there', ['T']]]),
      entriesById: new Map([['there', [{ alias: 'T', addedAt: T2 }]]]),
    });
    const settled = settleEntryDeleteRun(
      runResult([idGone, entryItem('a', T1, 'unknown'), idThere]),
      read,
      baseline,
    );

    expect(settled.result.items.map((item) => item.status)).toEqual(['done', 'done', 'unknown']);
    expect(settled.result.doneKeys).toEqual(['gone', 'x#a']);
  });

  it('judges nothing without a complete read with add dates', () => {
    const baseline = baselineOf([{ alias: 'a', addedAt: T1 }]);
    const result = runResult([entryItem('a', T1, 'unknown')]);

    for (const entries of [
      null,
      datedRead([], false),
      setEntries({ aliasesById: new Map() }), // a read without the add dates
    ]) {
      const settled = settleEntryDeleteRun(result, entries, baseline);
      expect(settled.unexpectedRemovals).toBeNull();
      expect(settled.result.items[0].status).toBe('unknown');
    }
  });

  /*
   * #454: a gap read (7TV lists fewer entries than it counts) proves presence only, never absence.
   * protects: decision 2 and AC 7 — no unknown row is cleared up and no unexpected removal is
   *   concluded from a gap read, because `readableEntries` takes only `complete` reads.
   * fails_when: a gap read settles an unknown id row whose id it does not list, or an unknown entry
   *   row whose entry it does not list, or yields a non-null `unexpectedRemovals`.
   * no_claim: who owes the post-check (`entryPostCheckOwed`), the delete service's merge of earlier
   *   findings.
   */
  it('settles nothing against a gap read: an absent id or entry stays unknown, no unexpected removal', () => {
    const baseline = baselineOf([
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T2 },
    ]);
    const idRow = queueItem({ key: 'gone', status: 'unknown' });
    const gap = setEntries({
      aliasesById: new Map([['x', []]]),
      entriesById: new Map([['x', []]]),
      complete: false,
      readState: 'gap',
      omittedCount: 2,
    });

    const settled = settleEntryDeleteRun(
      runResult([idRow, entryItem('a', T1, 'unknown')]),
      gap,
      baseline,
    );

    expect(settled.result.items.map((item) => item.status)).toEqual(['unknown', 'unknown']);
    expect(settled.result.doneKeys).toEqual([]);
    expect(settled.unexpectedRemovals).toBeNull();
    expect(settleDeleteResult(runResult([idRow]), gap).items[0]).toBe(idRow);
  });

  it('counts without a tie and goes by alias inside one', () => {
    const groups = baselineOf([
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T2 },
      { alias: 'c', addedAt: T2 },
    ]).get('x')!;
    const own = new Map([[entryTimeKey(T2), ['b']]]);

    // a renamed (own date, so still there) and c renamed inside the tie (counts as gone).
    expect(
      findUnexpectedRemovals(
        'x',
        groups,
        [
          { alias: 'a2', addedAt: T1 },
          { alias: 'c2', addedAt: T2 },
        ],
        own,
      ),
    ).toEqual([{ sevenTvEmoteId: 'x', alias: 'c', addedAt: T2 }]);
    // An entry still there, but an own removal at the date accounts for it: nothing missing.
    expect(
      findUnexpectedRemovals(
        'x',
        groups,
        [
          { alias: 'a', addedAt: T1 },
          { alias: 'c', addedAt: T2 },
        ],
        own,
      ),
    ).toEqual([]);
  });

  it('owes the check when a sent removal leaves baseline entries of its emote, skipped rows included', () => {
    const baseline = baselineOf([
      { alias: 'a', addedAt: T1 },
      { alias: 'b', addedAt: T2 },
    ]);

    expect(
      entryPostCheckOwed(
        runResult([entryItem('a', T1, 'cancelled'), entryItem('b', T2, 'done')]),
        baseline,
      ),
    ).toBe(true);
    expect(
      entryPostCheckOwed(
        runResult([entryItem('a', T1, 'unknown'), entryItem('b', T2, 'done')]),
        baseline,
      ),
    ).toBe(false);
    expect(
      entryPostCheckOwed(
        runResult([entryItem('a', T1, 'cancelled'), entryItem('b', T2, 'failed')]),
        baseline,
      ),
    ).toBe(false);
  });
});
