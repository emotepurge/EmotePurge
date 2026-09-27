import { describe, expect, it } from 'vitest';

import { RunQueueItem, RunResult } from './seven-tv-run-engine';
import { SevenTvSetEntries } from './seven-tv-set-entries';
import { settleDeleteResult, settleRestoreResult, unknownCount } from './seven-tv-run-settlement';

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
    const entries = setEntries({ aliasesById: new Map([['7tv-1', ['PogU']]]) });
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
