import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { UndoCandidate } from '../export/transfer-run-export';

/**
 * The pure classification behind a replace undo (#254, spec 4.3 / 6.2): given the candidates a
 * transfer-run file offers (`parseTransferRunForUndo`) and one live read of the target set, decide
 * per candidate whether the undo removes the source and re-adds the target (`full`), only re-adds
 * the target (`addOnly`), or leaves the row alone with a reason. No Angular, no DOM, no request —
 * the same function runs in three places against three different reads (the flow's first read,
 * the pre-start freshness check, and the recheck before every single REMOVE, E14/E19).
 *
 * **This function authorizes every REMOVE the undo sends.** A REMOVE takes *all* entries of an id
 * (F2), so the source part only ever becomes a REMOVE when the source's live entries are exactly
 * `{ alias }` (E5, ordinal comparison), and a `full` row is all-or-nothing: any target entry it
 * could not restore — unverifiable name, foreign entry on the target, name held by a third id —
 * skips the whole row instead of removing a source whose target cannot fully come back (E8, E20,
 * E21). The non-destructive `addOnly` row, by contrast, runs entry by entry and records what it
 * left out (`omittedEntries`) or found next to it (`notes`) (E20, E23).
 *
 * The step order of spec table 4.3 is the contract, and in particular **normalisation comes before
 * every target check** (F18): a `{ alias: null }` target entry is named `D` — the target's
 * `defaultName` *from the file*, never from the live read's `defaultNameById` (E21: after a
 * successful replace the target is not in the set, so the read has no default name for it, and the
 * one it would have could have changed since) — and every later check works on those names.
 *
 * The caller owns the read's validity: a failed read or one with `complete: false` must not be
 * classified at all (spec 4.3, last paragraph) — a partial read could hide the second alias that
 * makes a source unsafe to remove. This module never sees a failed read and does not look at
 * `complete`.
 */

/** Why the classification itself leaves a candidate alone (spec 4.3 / 6.2) — one reason per row. */
export type UndoClassificationSkipReason =
  | 'duplicateInFile'
  | 'sourceUnderOtherName'
  | 'sourceHasMoreEntries'
  | 'targetNameUnverifiable'
  | 'targetHasForeignEntries'
  | 'targetNameTaken'
  | 'inconsistent'
  | 'nothingToDo';

/**
 * Every reason a candidate can end up not running: the classification's own reasons plus the three
 * the callers add around it — `skippedUnproven` (an unconfirmed `full` row from a `planned` file
 * without the dialog's confirmation, spec 17 K2), `skippedDrift` (the freshness check or the recheck
 * before a REMOVE classified the row differently than the stamped plan, E14/E19) and
 * `recheckUnavailable` (that check's read failed or came back incomplete, E19). This is the type of
 * `skippedReason` in the transfer-undo file (`transfer-undo-export.ts`).
 */
export type UndoSkipReason =
  UndoClassificationSkipReason | 'skippedUnproven' | 'skippedDrift' | 'recheckUnavailable';

/** The classification's own reasons in table order (spec 4.3) — the keys of
 *  {@link UndoPlanCounts.skippedByReason}. */
export const UNDO_CLASSIFICATION_SKIP_REASONS: readonly UndoClassificationSkipReason[] = [
  'duplicateInFile',
  'sourceUnderOtherName',
  'sourceHasMoreEntries',
  'targetNameUnverifiable',
  'targetHasForeignEntries',
  'targetNameTaken',
  'inconsistent',
  'nothingToDo',
];

/** Every {@link UndoSkipReason}: the classification's own, then the callers'. */
export const UNDO_SKIP_REASONS: readonly UndoSkipReason[] = [
  ...UNDO_CLASSIFICATION_SKIP_REASONS,
  'skippedUnproven',
  'skippedDrift',
  'recheckUnavailable',
];

/** Why a single target entry was not restored (E8, E21, E23). */
export type UndoOmitReason = 'targetNameTaken' | 'targetNameUnverifiable';

/**
 * One target entry the undo cannot restore. `alias` is `null` exactly for `targetNameUnverifiable`
 * — the entry was aliasless and the file carries no `defaultName` to name it (E21), so there is no
 * name to report; a `targetNameTaken` entry always has one. Same shape as
 * `TransferUndoOmittedEntry` in the transfer-undo file.
 */
export interface UndoOmittedEntry {
  alias: string | null;
  reason: UndoOmitReason;
}

/** A remark on an `addOnly` row that runs anyway (E20): the target carries an entry the file does
 *  not name, and the row adds its own entries next to it without touching it. */
export type UndoNote = 'targetHasForeignEntries';

/**
 * The live counterpart a skipped row shows (spec 4.3: "Live-Gegenstück wird gezeigt"): every entry
 * the source and the target hold in the read — named aliases in 7TV's order, then `null` once if the
 * id also has an aliasless entry. Empty when the id is not in the set.
 */
export interface UndoLiveCounterpart {
  sourceEntries: (string | null)[];
  targetEntries: (string | null)[];
}

/**
 * A candidate that runs. `adds` are the target entries still missing, in the file's entry order,
 * each under its normalised name — never `null`, never empty (E21, AK 31); a `full` row always has at
 * least one (F11, else it is `inconsistent`). `stepCount` is `1 + adds.length` for `full` (the REMOVE
 * is step 0, E6) and `adds.length` for `addOnly`. `provenance` is the candidate's own, passed through
 * (F17). `omittedEntries` and `notes` are only ever non-empty on an `addOnly` row: a `full` row with
 * either would not run at all (E8, E20).
 *
 * The field names are the transfer-undo builder's input (`TransferUndoRunnableInput`), so a row can
 * be handed to `buildTransferUndoPlanRecord` as it is.
 */
export interface UndoPlanRow {
  candidate: UndoCandidate;
  mode: 'full' | 'addOnly';
  adds: { alias: string }[];
  stepCount: number;
  provenance: UndoCandidate['provenance'];
  omittedEntries: UndoOmittedEntry[];
  notes: UndoNote[];
}

/**
 * A candidate that does not run, with its reason and the live counterpart. `omittedEntries` names
 * the target entries behind the reason where there are any: for a `full` row skipped as
 * `targetNameUnverifiable`/`targetNameTaken` the entries that blocked it, and for a `nothingToDo`
 * row whose every missing entry had to be left out the entries an `addOnly` run would have omitted
 * (plan Festlegung Nr. 7 — the omission stays visible instead of reading as "nothing to do"). Empty
 * otherwise.
 *
 * The classification only ever produces its own reasons; the dialog, the flow and the service
 * build rows with the other three (see {@link UndoSkipReason}), usually with
 * {@link undoLiveCounterpart} for `live`.
 */
export interface UndoSkippedRow<R extends UndoSkipReason = UndoSkipReason> {
  candidate: UndoCandidate;
  reason: R;
  live: UndoLiveCounterpart;
  omittedEntries: UndoOmittedEntry[];
}

/**
 * Totals of one classification. `full`/`addOnly` count rows; `removals` is the number of REMOVEs
 * (one per `full` row) and `additions` the number of ADDs across both modes. `alreadyPresent` counts
 * target *entries* the read already showed on the target (spec 4.3 step 4), over every candidate
 * that got that far — including a row that ends `nothingToDo` because of them (E18) or is skipped
 * later. `skippedByReason` counts skipped *rows*, each exactly once under its own reason; an omitted
 * entry is not a row and is counted by {@link summarizeUndoPlan} instead.
 */
export interface UndoPlanCounts {
  full: number;
  addOnly: number;
  removals: number;
  additions: number;
  alreadyPresent: number;
  skippedByReason: Record<UndoClassificationSkipReason, number>;
}

/** One classification of a candidate list against one read — rows and skipped rows each keep the
 *  candidates' order. */
export interface UndoPlan {
  rows: UndoPlanRow[];
  skipped: UndoSkippedRow<UndoClassificationSkipReason>[];
  counts: UndoPlanCounts;
}

/** A target's entries under their effective names (spec 4.3 step 2). */
export interface NormalizedTargetEntries {
  /** The name set `N`: a named entry under its alias, an aliasless entry under the file's
   *  `defaultName` — first occurrence wins, in the file's entry order. An aliasless entry without a
   *  `defaultName` is not in it. */
  names: string[];
  /** Whether the file names an aliasless entry (`E ∋ null`) — decides whether a live aliasless entry
   *  on the target is the row's own or foreign (E20, F5). */
  hadNull: boolean;
  /** Whether that aliasless entry has no `defaultName` to be named by (E21). */
  unverifiable: boolean;
}

/** The outcome of comparing a stamped plan with a fresh classification (spec 4.3, E14). */
export interface UndoPlanDiff {
  /** The stamped rows whose fresh classification matches, as freshly classified — same mode and ADD
   *  list by construction, with the current `notes`/`omittedEntries`. */
  runnable: UndoPlanRow[];
  /** The stamped rows whose fresh classification differs or is missing — as stamped. */
  drifted: UndoPlanRow[];
}

/** What a plan's runnable rows will do, for the confirm dialog's action line (E17). */
export interface UndoPlanSummary {
  /** One REMOVE per `full` row. */
  removeCount: number;
  /** Every ADD across both modes. */
  addCount: number;
  /** Net slot change, `addCount − removeCount`: a single-entry `full` row is 0, a #74 duplicate cell
   *  with three entries is +2 (E17). */
  slotDelta: number;
  /** Entries left out of runnable (`addOnly`) rows. */
  omittedEntryCount: number;
  /** `addOnly` rows that run next to a foreign target entry (E20). */
  foreignNotedRows: number;
}

/** A read, indexed once for the lookups the table needs. */
interface ReadIndex {
  read: SevenTvSetEntries;
  /** Reverse of `aliasesById`: which id holds a name as an alias (`held(name)`, spec 4.3). */
  holderByName: Map<string, string>;
}

/** One candidate's classification plus its step-4 count, which the plan totals. */
interface ClassifyResult {
  result: UndoPlanRow | UndoSkippedRow<Exclude<UndoClassificationSkipReason, 'duplicateInFile'>>;
  alreadyPresent: number;
}

/** What steps 2–5 find on the target, before step 6 decides what that means for the row's mode. */
interface TargetFindings {
  /** Step 2: an aliasless entry without `D`. */
  unverifiable: boolean;
  /** Step 3: an entry on the target outside `N` (E20). */
  hasForeignEntry: boolean;
  /** Step 4: names of `N` the target already holds. */
  alreadyPresent: number;
  /** Step 5: missing names nobody (or only the source) holds, in `N`'s order. */
  adds: { alias: string }[];
  /** Step 5: missing names a third id holds. */
  taken: UndoOmittedEntry[];
}

const UNVERIFIABLE_ENTRY: UndoOmittedEntry = { alias: null, reason: 'targetNameUnverifiable' };

/**
 * Spec 4.3 step 2 on its own (AK 35): the name set `N` of a candidate's target entries. A named
 * entry keeps its alias; `{ alias: null }` becomes the file's `defaultName` `D` — which the transfer-run parser
 * has already normalised (`''` ⇒ `null`) — or, without one, makes the target `unverifiable` and
 * contributes no name.
 */
export function normalizeTargetEntries(target: UndoCandidate['target']): NormalizedTargetEntries {
  const names: string[] = [];
  let hadNull = false;
  let unverifiable = false;
  for (const entry of target.entries) {
    let name: string | null = entry.alias;
    if (name === null) {
      hadNull = true;
      name = nonEmpty(target.defaultName);
      if (name === null) {
        unverifiable = true;
        continue;
      }
    }
    if (name.length > 0 && !names.includes(name)) {
      names.push(name);
    }
  }
  return { names, hadNull, unverifiable };
}

/**
 * One candidate against one read, spec table 4.3 steps 1–6 — the same rule `classifyUndoRows` applies
 * per candidate, without step 0 (a duplicate is a property of the candidate list, not of one row).
 * This is what the recheck before every REMOVE runs (E19).
 */
export function classifyUndoRow(
  candidate: UndoCandidate,
  read: SevenTvSetEntries,
): UndoPlanRow | UndoSkippedRow<Exclude<UndoClassificationSkipReason, 'duplicateInFile'>> {
  return classifyIndexed(candidate, indexRead(read)).result;
}

/**
 * Every candidate against one read (spec 4.3 steps 0–6, 6.2). Step 0 first: candidates sharing a
 * source id or a target id — only a manipulated file can have them, the writer deduplicates both —
 * are all skipped `duplicateInFile`, the target untouched, before any of them is looked at further.
 */
export function classifyUndoRows(
  candidates: readonly UndoCandidate[],
  read: SevenTvSetEntries,
): UndoPlan {
  const index = indexRead(read);
  const sourceCount = countBy(candidates, (candidate) => candidate.sourceSevenTvEmoteId);
  const targetCount = countBy(candidates, (candidate) => candidate.target.sevenTvEmoteId);
  const rows: UndoPlanRow[] = [];
  const skipped: UndoSkippedRow<UndoClassificationSkipReason>[] = [];
  const counts: UndoPlanCounts = {
    full: 0,
    addOnly: 0,
    removals: 0,
    additions: 0,
    alreadyPresent: 0,
    skippedByReason: Object.fromEntries(
      UNDO_CLASSIFICATION_SKIP_REASONS.map((reason) => [reason, 0]),
    ) as Record<UndoClassificationSkipReason, number>,
  };

  for (const candidate of candidates) {
    if (
      (sourceCount.get(candidate.sourceSevenTvEmoteId) ?? 0) > 1 ||
      (targetCount.get(candidate.target.sevenTvEmoteId) ?? 0) > 1
    ) {
      skipped.push(skippedRow(candidate, 'duplicateInFile', index.read, []));
      counts.skippedByReason.duplicateInFile += 1;
      continue;
    }
    const { result, alreadyPresent } = classifyIndexed(candidate, index);
    counts.alreadyPresent += alreadyPresent;
    if (isUndoPlanRow(result)) {
      rows.push(result);
      counts.additions += result.adds.length;
      if (result.mode === 'full') {
        counts.full += 1;
        counts.removals += 1;
      } else {
        counts.addOnly += 1;
      }
    } else {
      skipped.push(result);
      counts.skippedByReason[result.reason] += 1;
    }
  }

  return { rows, skipped, counts };
}

/** Whether a classification result runs (rather than being skipped). */
export function isUndoPlanRow(row: UndoPlanRow | UndoSkippedRow): row is UndoPlanRow {
  return 'mode' in row;
}

/**
 * Whether a fresh classification still matches the stamped row (spec 4.3, E14, E19): it runs, in the
 * same mode, with the same ADD list — names **and** order. `notes` do not count: a foreign entry
 * that appeared next to an `addOnly` row changes nothing it sends. Anything else — a mode change, a
 * changed ADD list, a row that is now skipped — is drift.
 */
export function sameClassification(
  stamped: UndoPlanRow,
  fresh: UndoPlanRow | UndoSkippedRow,
): boolean {
  if (!isUndoPlanRow(fresh) || fresh.mode !== stamped.mode) {
    return false;
  }
  return (
    fresh.adds.length === stamped.adds.length &&
    fresh.adds.every((add, index) => add.alias === stamped.adds[index].alias)
  );
}

/**
 * The freshness check's comparison (spec 4.3, E14): each stamped row against the fresh
 * classification of the same candidate (matched on source and target id). Only stamped rows can run
 * — a candidate that was skipped when the plan was stamped stays out even if it would run now. A
 * stamped row whose candidate is missing from the fresh plan, or matches more than once, is drift
 * (fail-closed).
 *
 * Takes only what it compares, so a caller can stamp the *effective* plan (spec 17 K2: the dialog's
 * `runnable` rows after the origin lock) as `{ rows }`.
 */
export function diffUndoPlans(
  stamped: Pick<UndoPlan, 'rows'>,
  fresh: Pick<UndoPlan, 'rows' | 'skipped'>,
): UndoPlanDiff {
  const freshByKey = new Map<string, (UndoPlanRow | UndoSkippedRow)[]>();
  for (const row of [...fresh.rows, ...fresh.skipped]) {
    const key = candidateKey(row.candidate);
    freshByKey.set(key, [...(freshByKey.get(key) ?? []), row]);
  }
  const runnable: UndoPlanRow[] = [];
  const drifted: UndoPlanRow[] = [];
  for (const row of stamped.rows) {
    const matches = freshByKey.get(candidateKey(row.candidate)) ?? [];
    const [match] = matches;
    if (matches.length === 1 && sameClassification(row, match) && isUndoPlanRow(match)) {
      runnable.push(match);
    } else {
      drifted.push(row);
    }
  }
  return { runnable, drifted };
}

/**
 * What the runnable rows will do (spec 6.2, E17). Takes only the rows, so it works for a whole plan
 * as well as for the effective plan after the origin lock (spec 17 K2).
 */
export function summarizeUndoPlan(plan: Pick<UndoPlan, 'rows'>): UndoPlanSummary {
  let removeCount = 0;
  let addCount = 0;
  let omittedEntryCount = 0;
  let foreignNotedRows = 0;
  for (const row of plan.rows) {
    if (row.mode === 'full') {
      removeCount += 1;
    }
    addCount += row.adds.length;
    omittedEntryCount += row.omittedEntries.length;
    if (row.notes.includes('targetHasForeignEntries')) {
      foreignNotedRows += 1;
    }
  }
  return {
    removeCount,
    addCount,
    slotDelta: addCount - removeCount,
    omittedEntryCount,
    foreignNotedRows,
  };
}

/**
 * The live counterpart of a candidate in a read — what a skipped row shows. Exported for the callers
 * that skip a row themselves (`skippedUnproven` in the dialog, `skippedDrift` in the flow), so every
 * skipped row describes the live state the same way.
 */
export function undoLiveCounterpart(
  candidate: UndoCandidate,
  read: SevenTvSetEntries,
): UndoLiveCounterpart {
  return {
    sourceEntries: entriesOf(read, candidate.sourceSevenTvEmoteId),
    targetEntries: entriesOf(read, candidate.target.sevenTvEmoteId),
  };
}

/** Spec 4.3 steps 1–6 for one candidate; step order is the contract (see the module doc). */
function classifyIndexed(candidate: UndoCandidate, index: ReadIndex): ClassifyResult {
  const skip = (
    reason: Exclude<UndoClassificationSkipReason, 'duplicateInFile'>,
    omittedEntries: UndoOmittedEntry[] = [],
    alreadyPresent = 0,
  ): ClassifyResult => ({
    result: skippedRow(candidate, reason, index.read, omittedEntries),
    alreadyPresent,
  });

  // Step 1 — anything but "exactly { alias }" or "gone" leaves the whole row alone, the target
  // included: no target check runs for it (E5, F2).
  const source = sourcePart(candidate, index.read);
  if (source !== 'remove' && source !== 'none') {
    return skip(source);
  }
  const findings = targetFindings(candidate, index);

  if (source === 'remove') {
    // A `full` row is all-or-nothing, checked in table order: step 2, step 3, step 5, step 6.
    if (findings.unverifiable) {
      return skip('targetNameUnverifiable', [{ ...UNVERIFIABLE_ENTRY }]);
    }
    if (findings.hasForeignEntry) {
      return skip('targetHasForeignEntries');
    }
    if (findings.taken.length > 0) {
      return skip('targetNameTaken', findings.taken, findings.alreadyPresent);
    }
    if (findings.adds.length === 0) {
      // F11: the collision alias is always one of the target's entries, so a removable source
      // with nothing to restore contradicts the file — a guard, not a path.
      return skip('inconsistent', [], findings.alreadyPresent);
    }
    return {
      result: {
        candidate,
        mode: 'full',
        adds: findings.adds,
        stepCount: 1 + findings.adds.length,
        provenance: candidate.provenance,
        omittedEntries: [],
        notes: [],
      },
      alreadyPresent: findings.alreadyPresent,
    };
  }

  // An `addOnly` row runs entry by entry: omissions and a foreign entry travel with it (E20, E23).
  const omittedEntries: UndoOmittedEntry[] = [
    ...(findings.unverifiable ? [{ ...UNVERIFIABLE_ENTRY }] : []),
    ...findings.taken,
  ];
  if (findings.adds.length === 0) {
    // Festlegung Nr. 7: an addOnly row whose every missing entry was omitted is `nothingToDo`, with
    // the omissions kept on the skipped row — never a runnable row with zero ADDs.
    return skip('nothingToDo', omittedEntries, findings.alreadyPresent);
  }
  return {
    result: {
      candidate,
      mode: 'addOnly',
      adds: findings.adds,
      stepCount: findings.adds.length,
      provenance: candidate.provenance,
      omittedEntries,
      notes: findings.hasForeignEntry ? ['targetHasForeignEntries'] : [],
    },
    alreadyPresent: findings.alreadyPresent,
  };
}

/**
 * Spec 4.3 step 1. Only exactly `{ alias }` — one named entry, ordinal equal, no aliasless entry —
 * makes the source part a REMOVE; an empty source means it is already gone; the alias among others
 * is `sourceHasMoreEntries`, the alias missing is `sourceUnderOtherName` (E5).
 */
function sourcePart(
  candidate: UndoCandidate,
  read: SevenTvSetEntries,
): 'remove' | 'none' | 'sourceHasMoreEntries' | 'sourceUnderOtherName' {
  const sourceEntries = entriesOf(read, candidate.sourceSevenTvEmoteId);
  if (sourceEntries.length === 0) {
    return 'none';
  }
  if (sourceEntries.length === 1 && sourceEntries[0] === candidate.alias) {
    return 'remove';
  }
  return sourceEntries.includes(candidate.alias) ? 'sourceHasMoreEntries' : 'sourceUnderOtherName';
}

/**
 * Spec 4.3 steps 2–5 on the normalised names — never on the raw entries (F18). A name is free when
 * nobody holds it or the source does (the REMOVE frees it, E6); a third id holding it is E8.
 */
function targetFindings(candidate: UndoCandidate, index: ReadIndex): TargetFindings {
  const { read } = index;
  const target = candidate.target.sevenTvEmoteId;
  const normalized = normalizeTargetEntries(candidate.target);
  const hasForeignEntry =
    (read.aliasesById.get(target) ?? []).some((alias) => !normalized.names.includes(alias)) ||
    (read.aliaslessIds.has(target) && !normalized.hadNull);
  const findings: TargetFindings = {
    unverifiable: normalized.unverifiable,
    hasForeignEntry,
    alreadyPresent: 0,
    adds: [],
    taken: [],
  };
  for (const name of normalized.names) {
    const holder = index.holderByName.get(name);
    if (isPresentOnTarget(name, candidate, normalized, index)) {
      findings.alreadyPresent += 1;
    } else if (holder === undefined || holder === candidate.sourceSevenTvEmoteId) {
      findings.adds.push({ alias: name });
    } else {
      findings.taken.push({ alias: name, reason: 'targetNameTaken' });
    }
  }
  return findings;
}

/** Spec 4.3 step 4 for one name of `N`. */
function isPresentOnTarget(
  name: string,
  candidate: UndoCandidate,
  normalized: NormalizedTargetEntries,
  index: ReadIndex,
): boolean {
  const target = candidate.target.sevenTvEmoteId;
  if ((index.read.aliasesById.get(target) ?? []).includes(name)) {
    return true;
  }
  // An aliasless live entry answers only for the name the file's aliasless entry stands for — and
  // only if no named entry of the file carries that same name, which a named live alias must match.
  const fromAliaslessOnly =
    normalized.hadNull &&
    name === candidate.target.defaultName &&
    !candidate.target.entries.some((entry) => entry.alias === name);
  return fromAliaslessOnly && index.read.aliaslessIds.has(target);
}

function indexRead(read: SevenTvSetEntries): ReadIndex {
  const holderByName = new Map<string, string>();
  for (const [id, aliases] of read.aliasesById) {
    for (const alias of aliases) {
      if (!holderByName.has(alias)) {
        holderByName.set(alias, id);
      }
    }
  }
  return { read, holderByName };
}

/** `entriesOf(id)` from spec 4.3: the named aliases, then `null` once if the id has an aliasless
 *  entry. */
function entriesOf(read: SevenTvSetEntries, id: string): (string | null)[] {
  const entries: (string | null)[] = [...(read.aliasesById.get(id) ?? [])];
  if (read.aliaslessIds.has(id)) {
    entries.push(null);
  }
  return entries;
}

function skippedRow<R extends UndoClassificationSkipReason>(
  candidate: UndoCandidate,
  reason: R,
  read: SevenTvSetEntries,
  omittedEntries: UndoOmittedEntry[],
): UndoSkippedRow<R> {
  return { candidate, reason, live: undoLiveCounterpart(candidate, read), omittedEntries };
}

function candidateKey(candidate: UndoCandidate): string {
  return `${candidate.sourceSevenTvEmoteId}\u0000${candidate.target.sevenTvEmoteId}`;
}

function countBy<T>(items: readonly T[], key: (item: T) => string): Map<string, number> {
  const counts = new Map<string, number>();
  for (const item of items) {
    const value = key(item);
    counts.set(value, (counts.get(value) ?? 0) + 1);
  }
  return counts;
}

function nonEmpty(value: string | null): string | null {
  return value !== null && value.length > 0 ? value : null;
}
