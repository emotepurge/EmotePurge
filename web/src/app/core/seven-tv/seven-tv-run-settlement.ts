import { RunQueueItem, RunResult } from './seven-tv-run-engine';
import { SevenTvSetEntries } from './seven-tv-set-entries';

/**
 * How long a delete or restore run's re-read of the target set may take before the row it was
 * meant to clear up is simply left `unknown` (#275) — same budget as the import's own re-read
 * (`SETTLE_READ_TIMEOUT_MS`, `seven-tv-import.service.ts`) and the undo's
 * (`UNDO_SETTLE_READ_TIMEOUT_MS`/`RECHECK_READ_TIMEOUT_MS`, `seven-tv-undo.service.ts`) — all three
 * are deliberately kept at the same value rather than merged into one shared constant: unifying
 * them belongs to #284, not to this plan (Plan-275 Festlegung 4).
 */
export const SET_ENTRIES_READ_TIMEOUT_MS = 20_000;

/**
 * How long a delete or restore service waits, once its own `cancel()` synchronously finished the
 * run, before it reads the target set — giving 7TV time to finish processing the request that was
 * still in flight when the user clicked "Cancel". Not applied after a plain transport loss (a 5xx
 * or no answer at all): there 7TV has already finished with the request by the time the failure
 * reaches this app, or, for a dropped connection, there is no moment to wait *for* (Plan-275
 * Festlegung 5). Neither delay decides anything — it only raises how often the read can confirm the
 * row instead of leaving it `unknown`.
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
 * only when the live one is empty or unknown (Plan-275 Festlegung 8). A stale name recorded in the
 * file is not checked first: 7TV assigns the name it has *now*, so a live default name that no
 * longer matches the file's is itself evidence that something else changed the entry, not proof
 * that this row's own `ADD` failed. A sibling row of the very same run that separately re-adds this
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
