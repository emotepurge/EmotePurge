import { RunQueueItem, RunResult } from './seven-tv-run-engine';
import { SevenTvSetEntries, SevenTvSetEntry } from './seven-tv-set-entries';

/**
 * How long the settle re-read of the target set may take before the rows it was meant to clear up
 * are simply left `unknown` (#275) — one budget shared by all four runs that settle: delete,
 * restore, import (`seven-tv-import.service.ts`) and undo (`seven-tv-undo.service.ts`), which
 * replaced their own equally long settle-read constants with this one (Plan-284 Festlegung 4).
 * Deliberately *not* shared with the undo's `RECHECK_READ_TIMEOUT_MS`: that read is a gate in front
 * of a mutation (fail-closed, E19), not a settlement after the run, and keeps its own constant even
 * at the same value.
 */
export const SET_ENTRIES_READ_TIMEOUT_MS = 20_000;

/**
 * How long a run service waits, once its own `cancel()` synchronously finished the run, before its
 * settle re-read of the target set — giving 7TV time to finish processing the request that was
 * still in flight when the user clicked "Cancel". Applied by all four settling runs (delete,
 * restore, import, undo; Plan-275 Festlegung 5, Plan-284 Festlegung 2), and only ahead of the settle
 * read: the undo's recheck before each `REMOVE` never waits. Not applied after a plain transport
 * loss (a 5xx or no answer at all): there 7TV has already finished with the request by the time the
 * failure reaches this app, or, for a dropped connection, there is no moment to wait *for*. Neither
 * delay decides anything — it only raises how often the read can confirm the row instead of leaving
 * it `unknown`.
 */
export const CANCEL_SETTLE_GRACE_MS = 3_000;

/**
 * Clears up a delete run's `unknown` rows against a fresh read of the target set — **positive
 * only** (Plan-275 N1): a row becomes `done` exactly when the read positively shows the deletion
 * took effect, and stays `unknown` for every other outcome, including "the id is still there".
 * Nothing else can tell a mutation that never reached 7TV apart from one 7TV is still busy
 * applying, or from a third party having re-added the same id after our own `REMOVE` went through —
 * so "still there" is left exactly as unclear as it was, rather than guessed at as `failed`
 * ("nothing happened") or `cancelled`.
 *
 * `entries` is `null` when there was no read at all, the read failed or timed out, or came back
 * `complete: false` (a partial read of the set is not enough to vouch for anything, Plan-275
 * Festlegung 7) — every `unknown` row is then left untouched. Rows with any other status are
 * always returned unchanged (same object reference). Returns a new `RunResult` with `doneKeys`
 * recomputed in queue order; the `result` passed in is never mutated.
 */
export function settleDeleteResult(
  result: RunResult,
  entries: SevenTvSetEntries | null,
): RunResult {
  const readable = readableEntries(entries);
  const items = result.items.map((item) =>
    item.status !== 'unknown' || readable === null ? item : settleDeleteRow(item, readable),
  );
  return { ...result, items, doneKeys: doneKeysOf(items) };
}

/**
 * Clears up a restore run's `unknown` rows against a fresh read of the target set — the mirror of
 * {@link settleDeleteResult} for `ADD` (Plan-275 Festlegung 8), **positive only** (N1) in exactly
 * the same sense: a row becomes `done` only when the read shows the alias it restores (or, for an
 * aliasless entry, the fallback 7TV gave it) sitting on this id, and stays `unknown` otherwise —
 * including "the id is there under some other alias", which cannot be told apart from a third
 * party's own entry.
 *
 * A row's alias comes from `aliasByKey` (the same map the run's own `addOperation` sends its `ADD`
 * with, keyed like every row by `RunQueueItem.key`) rather than from the row's display `name`,
 * which for an aliasless entry is only ever a label. `null` there means the row restores an entry
 * that had no alias (only ever true for a row that came from a transfer-run protocol file, Spec
 * #254 F5): 7TV then gives the entry either no alias at all (`aliaslessIds`) or the emote's
 * *current* default name as its alias — checked first against the read's own live
 * `defaultNameById`, falling back to `defaultNameByKey`'s own record of that name at delete time
 * only when the live one is empty or unknown (Plan-275 Festlegung 8). An entry under the stale file
 * name cannot come from this row's own `ADD`, since 7TV assigns the current default name — matching
 * on it would confirm a foreign entry. A sibling row of the very same run that separately re-adds this
 * id under an alias equal to its current default name would be read as a false positive here — an
 * accepted gap, not guarded against, because it takes two rows of one run targeting the same id to
 * even raise the question. A row whose key is missing from `aliasByKey` stays `unknown` — fail
 * closed against a wiring bug that leaves a row without one, rather than guessed at as a `null`
 * alias.
 *
 * `entries` is `null` under the same conditions as {@link settleDeleteResult}, with the same
 * effect: every `unknown` row is left untouched. Rows with any other status are always returned
 * unchanged (same object reference). Returns a new `RunResult` with `doneKeys` recomputed in queue
 * order; the `result` passed in is never mutated.
 */
export function settleRestoreResult(
  result: RunResult,
  entries: SevenTvSetEntries | null,
  aliasByKey: ReadonlyMap<string, string | null>,
  defaultNameByKey: ReadonlyMap<string, string | null>,
): RunResult {
  const readable = readableEntries(entries);
  const items = result.items.map((item) =>
    item.status !== 'unknown' || readable === null
      ? item
      : settleRestoreRow(item, readable, aliasByKey, defaultNameByKey),
  );
  return { ...result, items, doneKeys: doneKeysOf(items) };
}

/** How many of `items` are (still) `unknown` — what a caller checks to decide whether a run needs
 *  `settling` at all, and what a settled result still has left to show in a dock. */
export function unknownCount(items: readonly RunQueueItem[]): number {
  return items.filter((item) => item.status === 'unknown').length;
}

/**
 * One add date (generation) of one emote in a slot delete run's comparison baseline (Plan-378 4.4
 * step 5) — the entries of that emote the run has seen at that date. More than one alias only in a
 * D4 tie (two entries of one id with the same `addedAt`).
 */
export interface EntryBaselineGroup {
  /** The add date as the first read that saw it carried it, verbatim — `null` when 7TV sent none. */
  readonly addedAt: string | null;
  /** The aliases of the entries at this date, as 7TV listed them (`null` for an aliasless one). */
  aliases: (string | null)[];
  /** Set once the run let its first own `REMOVE` at this date through. From then on later reads
   *  no longer change the group's aliases (Plan-378 4.5, T-3): in a tie the alias each entry had
   *  right before the run's first removal there is what tells a sibling apart from a stranger. */
  frozen: boolean;
}

/** A slot delete run's comparison baseline: per 7TV id of an entry row, its baseline groups keyed
 *  by {@link entryTimeKey}. Lives in the run's own closure, never on a record. */
export type EntryBaseline = Map<string, Map<string, EntryBaselineGroup>>;

/** The key two add dates are compared by: milliseconds via `Date.parse`, never the string (the wire
 *  paths print a different number of fraction digits) — `'null'` for a missing or unparsable date,
 *  which is a value of its own and never equals a real date. */
export function entryTimeKey(addedAt: string | null): string {
  const time = addedAt === null ? Number.NaN : Date.parse(addedAt);
  return Number.isNaN(time) ? 'null' : String(time);
}

/**
 * Takes one complete read's entries of `sevenTvEmoteId` into `baseline` (Plan-378 4.4 step 5, S2,
 * A6), in place.
 *
 * As long as the run has let no `REMOVE` of this emote through (no group of it is `frozen`), the
 * read simply replaces what the baseline held for it: the baseline is "the emote's entries in the
 * last read before its removal", and an entry that went before the run touched the emote cannot
 * have been taken by one of its requests — a removal only ever reaches entries of its own id.
 *
 * From the first such `REMOVE` on, the baseline only **accumulates** and is never overwritten: a
 * date the baseline lacks is added (an entry another editor added since), a known date keeps its
 * entries even when the read no longer shows them (that is exactly what the run must not forget),
 * and a known, not yet frozen date with at least as many entries as before takes the read's aliases
 * — a rename after the fact, not a new entry.
 */
export function mergeIntoBaseline(
  baseline: EntryBaseline,
  sevenTvEmoteId: string,
  entries: readonly SevenTvSetEntry[],
): void {
  const read = groupByTime(entries);
  const known = baseline.get(sevenTvEmoteId);
  if (known === undefined || ![...known.values()].some((group) => group.frozen)) {
    baseline.set(sevenTvEmoteId, read);
    return;
  }
  for (const [key, group] of read) {
    const existing = known.get(key);
    if (existing === undefined) {
      known.set(key, group);
    } else if (!existing.frozen && group.aliases.length >= existing.aliases.length) {
      existing.aliases = group.aliases;
    }
  }
}

/** Marks the baseline group of (`sevenTvEmoteId`, `timeKey`) as having had an own `REMOVE` let
 *  through — see {@link EntryBaselineGroup.frozen}. A date the baseline does not know is left alone. */
export function freezeBaselineGroup(
  baseline: EntryBaseline,
  sevenTvEmoteId: string,
  timeKey: string,
): void {
  const group = baseline.get(sevenTvEmoteId)?.get(timeKey);
  if (group !== undefined) {
    group.frozen = true;
  }
}

/** An entry of an emote a slot delete run removed from that is gone from the set although none of
 *  the run's own removals accounts for it (Plan-378 4.5, D33, AK 40) — possibly taken by one of the
 *  run's requests, possibly by another editor. Named from the comparison baseline. */
export interface UnexpectedEntryRemoval {
  sevenTvEmoteId: string;
  /** The entry's alias in the baseline — `null` for an aliasless entry. */
  alias: string | null;
  /** The entry's add date as read, verbatim — `null` when 7TV sent none. */
  addedAt: string | null;
  /** The emote's default name as the run's reads listed it — only for an entry without an alias,
   *  which 7TV shows under that name, so the dock can call it by it (the delete service adds it).
   *  Absent when no read carried one. */
  defaultName?: string;
}

/**
 * The counting rule of Plan-378 4.5 (S5, T-3) for one emote: which entries of its baseline `groups`
 * are missing from `live` although none of the run's own removals accounts for them. `own` holds,
 * per {@link entryTimeKey}, the aliases of the run's own removals at that date that may have taken
 * their entry.
 *
 * Without a tie (one baseline entry at a date) the count decides: the entry is expected unless an
 * own removal went to that date, and it is missing only when the read has no entry at that date at
 * all — a renamed entry is still told apart by its own date. In a D4 tie the alias decides: every
 * baseline entry no own removal named is expected, and counts as present only when an entry at that
 * date carries its baseline alias; an entry under an unknown alias meets no expectation (a rename
 * inside a tie is remove plus add). The price is a false alarm for a legitimate rename inside a tie
 * by someone else — hence "possibly" in the dock.
 */
export function findUnexpectedRemovals(
  sevenTvEmoteId: string,
  groups: ReadonlyMap<string, EntryBaselineGroup>,
  live: readonly SevenTvSetEntry[],
  own: ReadonlyMap<string, readonly string[]>,
): UnexpectedEntryRemoval[] {
  const liveByTime = new Map<string, (string | null)[]>();
  for (const entry of live) {
    const key = entryTimeKey(entry.addedAt);
    liveByTime.set(key, [...(liveByTime.get(key) ?? []), entry.alias]);
  }
  const missing: UnexpectedEntryRemoval[] = [];
  for (const [key, group] of groups) {
    const ownAliases = own.get(key) ?? [];
    const liveAliases = liveByTime.get(key) ?? [];
    const named = (alias: string | null) => ({ sevenTvEmoteId, alias, addedAt: group.addedAt });
    if (group.aliases.length <= 1) {
      const expected = group.aliases.length - ownAliases.length;
      if (expected > 0 && liveAliases.length < expected) {
        missing.push(...group.aliases.map(named));
      }
      continue;
    }
    const expected = [...group.aliases];
    for (const alias of ownAliases) {
      const index = expected.indexOf(alias);
      if (index >= 0) {
        expected.splice(index, 1);
      }
    }
    missing.push(...expected.filter((alias) => !liveAliases.includes(alias)).map(named));
  }
  return missing;
}

/** `found` appended to `known`, without an entry `known` already names (same emote, date and
 *  alias) — so a removal the run noticed early is never listed twice. */
export function mergeUnexpectedRemovals(
  known: readonly UnexpectedEntryRemoval[],
  found: readonly UnexpectedEntryRemoval[],
): UnexpectedEntryRemoval[] {
  const keyOf = (removal: UnexpectedEntryRemoval) =>
    `${removal.sevenTvEmoteId}\u0000${entryTimeKey(removal.addedAt)}\u0000${removal.alias ?? ''}`;
  const seen = new Set(known.map(keyOf));
  const merged = [...known];
  for (const removal of found) {
    if (!seen.has(keyOf(removal))) {
      seen.add(keyOf(removal));
      merged.push(removal);
    }
  }
  return merged;
}

/**
 * Whether a slot delete run owes the post-check (Plan-378 4.5, T-4): for at least one emote an
 * entry row's removal actually went out — its row is `done` or `unknown`; a skipped or failed row
 * does not count — and the emote's baseline holds more entries than such removals. A marked row
 * that was skipped is itself an entry that should still be there, so it does not suppress the
 * check; a run whose entry rows were all skipped sent nothing and owes none.
 */
export function entryPostCheckOwed(result: RunResult, baseline: EntryBaseline): boolean {
  for (const [sevenTvEmoteId, sent] of sentEntryRemovals(result.items)) {
    const total = [...(baseline.get(sevenTvEmoteId)?.values() ?? [])].reduce(
      (sum, group) => sum + group.aliases.length,
      0,
    );
    const sentCount = [...sent.values()].reduce((sum, aliases) => sum + aliases.length, 0);
    if (total > sentCount) {
      return true;
    }
  }
  return false;
}

/**
 * Clears up a slot delete run against its one re-read after the run and checks the removals'
 * emotes for entries the run did not report (Plan-378 4.5, D33).
 *
 * An `unknown` id row settles as {@link settleDeleteResult} does. An `unknown` **entry** row
 * becomes `done` only when the read shows no entry with its alias at its date **and** fewer entries
 * at that date than the baseline held minus the run's own `done` removals there — positive only
 * (Plan-275 N1). Several `unknown` rows at one date are settled in queue order against that
 * difference, so never more of them become `done` than entries are gone; a renamed entry still
 * there (same count, another alias) leaves its row `unknown`.
 *
 * Then, per emote with a removal that went out (`done` or `unknown`), {@link findUnexpectedRemovals}
 * with those removals as the own ones; a row still `unknown` may have taken its entry, a skipped or
 * failed one is expected to still be there.
 *
 * `unexpectedRemovals` is `null` when there is nothing to judge by: no read, a failed or incomplete
 * one, or one without add dates — every row is then left as it was.
 */
export function settleEntryDeleteRun(
  result: RunResult,
  entries: SevenTvSetEntries | null,
  baseline: EntryBaseline,
): { result: RunResult; unexpectedRemovals: UnexpectedEntryRemoval[] | null } {
  const readable = readableEntries(entries);
  const liveById = readable?.entriesById;
  if (readable === null || liveById === undefined) {
    return { result: settleDeleteResult(result, null), unexpectedRemovals: null };
  }
  const doneAt = new Map<string, number>();
  for (const item of result.items) {
    if (item.entry && item.status === 'done') {
      const key = emoteTimeKey(item.sevenTvEmoteId, item.entry.addedAt);
      doneAt.set(key, (doneAt.get(key) ?? 0) + 1);
    }
  }
  const items = result.items.map((item) => {
    if (item.status !== 'unknown') {
      return item;
    }
    if (!item.entry) {
      return settleDeleteRow(item, readable);
    }
    const timeKey = entryTimeKey(item.entry.addedAt);
    const group = baseline.get(item.sevenTvEmoteId)?.get(timeKey);
    const live = (liveById.get(item.sevenTvEmoteId) ?? []).filter(
      (entry) => entryTimeKey(entry.addedAt) === timeKey,
    );
    const key = emoteTimeKey(item.sevenTvEmoteId, item.entry.addedAt);
    const done = doneAt.get(key) ?? 0;
    if (
      group === undefined ||
      live.some((entry) => entry.alias === item.entry?.alias) ||
      live.length >= group.aliases.length - done
    ) {
      return item;
    }
    doneAt.set(key, done + 1);
    return asDone(item);
  });
  const unexpectedRemovals: UnexpectedEntryRemoval[] = [];
  for (const [sevenTvEmoteId, own] of sentEntryRemovals(items)) {
    const groups = baseline.get(sevenTvEmoteId);
    if (groups !== undefined) {
      unexpectedRemovals.push(
        ...findUnexpectedRemovals(sevenTvEmoteId, groups, liveById.get(sevenTvEmoteId) ?? [], own),
      );
    }
  }
  return { result: { ...result, items, doneKeys: doneKeysOf(items) }, unexpectedRemovals };
}

/** `entries` is usable for clearing a row up only when it is a full read — a read that stopped
 *  short (`complete: false`) has itself only partial knowledge of the set, which is not enough to
 *  positively confirm anything either way (Plan-275 Festlegung 7/8). */
function readableEntries(entries: SevenTvSetEntries | null): SevenTvSetEntries | null {
  return entries !== null && entries.complete ? entries : null;
}

/** A single `REMOVE`'s id is gone from the set exactly when it has no entry left in `aliasesById`
 *  at all — that map carries an (empty-array) entry for an id that only sits in the set aliasless,
 *  so `.has()` alone already answers "is this id in the set, under any alias or none" (verified
 *  against `loadSevenTvSetEntries`, `seven-tv-set-entries.ts`). */
function settleDeleteRow(item: RunQueueItem, entries: SevenTvSetEntries): RunQueueItem {
  return entries.aliasesById.has(item.sevenTvEmoteId) ? item : asDone(item);
}

function settleRestoreRow(
  item: RunQueueItem,
  entries: SevenTvSetEntries,
  aliasByKey: ReadonlyMap<string, string | null>,
  defaultNameByKey: ReadonlyMap<string, string | null>,
): RunQueueItem {
  if (!aliasByKey.has(item.key)) {
    // A row this run's own queue-building did not put a (possibly null) alias in for is a wiring
    // bug the settlement itself cannot diagnose — fail closed rather than treat the gap as a `null`
    // alias and go looking for a default name that was never meant to apply here.
    return item;
  }
  const alias = aliasByKey.get(item.key) ?? null;
  if (alias !== null) {
    return holdsAlias(entries, item.sevenTvEmoteId, alias) ? asDone(item) : item;
  }
  if (entries.aliaslessIds.has(item.sevenTvEmoteId)) {
    return asDone(item);
  }
  // Live first: 7TV assigns an ADD without an alias the emote's *current* default name, so the
  // read's own `defaultNameById` is what an unanswered ADD would actually show up under. The file's
  // own record (`defaultNameByKey`, from a transfer-run protocol at delete time) is only a fallback
  // for when the live read has nothing to say — never checked ahead of a live name that disagrees
  // with it.
  const defaultName =
    entries.defaultNameById.get(item.sevenTvEmoteId) || defaultNameByKey.get(item.key) || null;
  if (defaultName === null) {
    // Neither the live read nor the file's own record knows a default name for this id — nothing
    // left to check the read against, so the row stays exactly as unclear as it was.
    return item;
  }
  return holdsAlias(entries, item.sevenTvEmoteId, defaultName) ? asDone(item) : item;
}

function holdsAlias(entries: SevenTvSetEntries, sevenTvEmoteId: string, alias: string): boolean {
  return entries.aliasesById.get(sevenTvEmoteId)?.includes(alias) ?? false;
}

/** A single-step row (the delete's `REMOVE`, the restore's `ADD`) that the read positively
 *  confirmed — `completedSteps: 1` and `failedStep: null` match what the engine itself would have
 *  set had 7TV's own answer arrived instead of being lost (`seven-tv-run-engine.ts`, `runRowFrom`'s
 *  `done` branch). */
function asDone(item: RunQueueItem): RunQueueItem {
  return { ...item, status: 'done', completedSteps: 1, failedStep: null, errorMessage: undefined };
}

function doneKeysOf(items: readonly RunQueueItem[]): string[] {
  return items.filter((item) => item.status === 'done').map((item) => item.key);
}

/** Per emote, per {@link entryTimeKey}: the aliases of the entry rows whose removal went out —
 *  `done` or `unknown`. */
function sentEntryRemovals(items: readonly RunQueueItem[]): Map<string, Map<string, string[]>> {
  const sent = new Map<string, Map<string, string[]>>();
  for (const item of items) {
    if (!item.entry || (item.status !== 'done' && item.status !== 'unknown')) {
      continue;
    }
    const perTime = sent.get(item.sevenTvEmoteId) ?? new Map<string, string[]>();
    const key = entryTimeKey(item.entry.addedAt);
    perTime.set(key, [...(perTime.get(key) ?? []), item.entry.alias]);
    sent.set(item.sevenTvEmoteId, perTime);
  }
  return sent;
}

function emoteTimeKey(sevenTvEmoteId: string, addedAt: string): string {
  return `${sevenTvEmoteId}\u0000${entryTimeKey(addedAt)}`;
}

function groupByTime(entries: readonly SevenTvSetEntry[]): Map<string, EntryBaselineGroup> {
  const groups = new Map<string, EntryBaselineGroup>();
  for (const entry of entries) {
    const key = entryTimeKey(entry.addedAt);
    const group = groups.get(key);
    if (group === undefined) {
      groups.set(key, { addedAt: entry.addedAt, aliases: [entry.alias], frozen: false });
    } else {
      group.aliases.push(entry.alias);
    }
  }
  return groups;
}
