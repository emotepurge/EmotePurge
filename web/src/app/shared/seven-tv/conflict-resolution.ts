import {
  AddOrRenameTransferRow,
  ReplaceOrAdoptTransferRow,
  TransferPlan,
  TransferRow,
  TransferRowTarget,
} from '../../core/seven-tv/transfer-plan';
import { ImportPreview, isNameRejectedBySevenTv } from './import-preview';

/**
 * One user decision for a name-collision row (`NameCollisionRow`) or an alias-mismatch row
 * (`AliasMismatchRow`) — never both, since a source row's `sevenTvEmoteId` can only ever land in
 * one of the two lists (`buildImportPreview` puts every row in exactly one group). `'skip'` is both
 * an explicit member and the implicit default for a row absent from a {@link ResolutionDecisions}
 * map — see {@link decisionFor}, the one place that default is applied.
 *
 * Exhaustive teardown follows `ImportOrigin`'s pattern (`core/seven-tv/import-source.ts`): every
 * `switch` over `.kind` in this module covers all four members and ends in a `default` that calls
 * {@link assertUnreachableDecision}, so a fifth member is a compile error, not a silent gap.
 *
 * Which decisions are meaningful for which row type is not encoded in the type itself (both row
 * types share this one union) — `deriveTransferRows` is the single place that interprets a decision
 * in the context of the row it was made for: `renameSource`/`replaceTarget` only take effect on a
 * `NameCollisionRow`, `adoptSourceName` only on an `AliasMismatchRow`. A decision that does not fit
 * the row it is keyed to — including one keyed to a `sevenTvEmoteId` neither list carries at all —
 * is treated the same as an absent one: silently ignored, contributing no row and no violation. The
 * confirm dialog never offers the action for a row it doesn't apply to, so this module does not add
 * an eighth rule for a case its own contract already rules out.
 */
export type RowDecision =
  | { kind: 'skip' }
  | { kind: 'renameSource'; alias: string }
  | { kind: 'replaceTarget' }
  | { kind: 'adoptSourceName' };

/**
 * Per-row decisions for one resolution run, keyed by the *source* row's `sevenTvEmoteId` — the same
 * key `NameCollisionRow.row.sevenTvEmoteId` / `AliasMismatchRow.row.sevenTvEmoteId` carry. A row
 * whose key is absent from the map is `'skip'` by default (see {@link decisionFor}), so an empty map
 * is always a valid, no-op resolution.
 */
export type ResolutionDecisions = ReadonlyMap<string, RowDecision>;

/** Everything `validateResolution` and `buildTransferPlan` need that is not derivable from
 *  `preview`/`decisions` alone — today just rule 7 (`replaceNeedsTrackedTarget`), which depends on
 *  whether the *target* set is restorable, not on anything the import preview itself carries. */
export interface ResolutionContext {
  /** Whether the target set belongs to a tracked channel — see rule 7's own doc for why this, and
   *  only this, field decides it. */
  targetIsTracked: boolean;
}

/**
 * The seven rule names `validateResolution` can report — see that function's doc for what each one
 * checks. A `string` union, not an enum: these are also read by the confirm dialog to pick a
 * disabled-reason / inline-error text, so the literal values are the wire vocabulary between the two, and
 * `replaceNeedsTrackedTarget` is pinned by the plan under that exact name.
 */
export type ViolationRule =
  | 'duplicateGeneratedAlias'
  | 'aliasHeldByTarget'
  | 'invalidTypedAlias'
  | 'duplicateReplaceTarget'
  | 'targetTouchedByReplaceAndAdopt'
  | 'adoptBlocked'
  | 'replaceNeedsTrackedTarget';

/** One rule violation, naming the rule and every source row (`sevenTvEmoteId`) it involves — never
 *  empty. `rowKeys`' order is not a contract callers should rely on (rule checks below build sets,
 *  not sequences, to stay order-independent — see the "order independence" note on
 *  `validateResolution`); it names whichever rows a rule happened to visit first, nothing more. */
export interface Violation {
  rule: ViolationRule;
  rowKeys: string[];
}

/** `validateResolution`'s result — a discriminated union so a caller narrows on `ok` before reading
 *  `violations`, the same shape `ImportPreview`'s own sibling contracts use elsewhere in this
 *  feature. */
export type ResolutionValidation = { ok: true } | { ok: false; violations: Violation[] };

/** The summary numbers a confirm dialog and a run protocol both need — see the field docs for what
 *  each counts and why `addCount` and `removeCount` are not simply "rows with this action". */
export interface TransferPlanSummary {
  /** Every ADD mutation the plan performs — `add`, `renameSource` *and* `replace` rows: a `replace`
   *  row's REMOVE is followed by an ADD under the freed name, so it counts here too, once, alongside
   *  its own contribution to `removeCount`/`removedEntryCount` below.
   *  `adoptSourceName` never counts: it is a `7TV` UPDATE on an existing entry, not an ADD. */
  addCount: number;
  /** The count of `replace` *rows* — the deletion count a confirm dialog shows the user (AK 20).
   *  Never `adoptSourceName`: adopting renames an entry in place, nothing is deleted. */
  removeCount: number;
  /** The count of target *entries* the plan's REMOVEs actually take — `>= removeCount`, since one
   *  `replace` row can clear a #74 duplicate's two aliases, or one named plus one aliasless entry,
   *  in a single REMOVE (`TransferRowTarget`'s own doc). This, not `removeCount`, is what a slot
   *  projection must subtract (`projectSlots`' `delta` doc, `slot-projection.ts`). */
  removedEntryCount: number;
  /** The count of `adoptSourceName` rows — a 7TV UPDATE on an existing target entry, neither an ADD
   *  nor a REMOVE, so it lives in none of the three fields above. The confirm dialog's own source for
   *  its rename line and, when the plan has no ADDs at all, for its title. */
  adoptCount: number;
}

/**
 * Validates a set of per-row decisions against the whole run, independent of the order its rows or
 * its decisions happen to be given in (every check below groups by alias or by target id rather than
 * walking decisions sequentially, so permuting `preview`'s row arrays or `decisions`' insertion order
 * never changes the result — a rule that only held for one row order would not be a rule at all).
 * `ok: true` is the precondition `buildTransferPlan` needs; `buildTransferPlan` runs this function
 * itself, so a caller cannot reach a `replaceTarget` decision past rule 7 by skipping straight to
 * `buildTransferPlan`.
 *
 * Seven rules, each producing zero or more {@link Violation}s (a single call to this function can
 * report several, from different rules or the same one):
 *
 * 1. **`duplicateGeneratedAlias`** — no two rows may write the same alias in an ADD, whether that
 *    ADD comes from an unchanged `toAdd` row, a rename, or a replace's own re-add, or from an
 *    adopt's UPDATE. **Exception:** two *unchanged* `toAdd` rows sharing a name is not a violation —
 *    that is today's behavior (7TV rejects the second one live, informational only, same as
 *    `invalidNames`) and a dialog nobody touched must never block (AK 2/5). The moment a decision
 *    hangs off *either* end of a shared alias, the rule applies in full, covering every row in that
 *    group — including any untouched `toAdd` sibling.
 * 2. **`aliasHeldByTarget`** — no generated alias may equal *any* non-falsy name the target holds
 *    when the preview is built (`ImportPreview.targetNames`) — this covers every target entry, not
 *    only the ones a `NameCollisionRow`/`AliasMismatchRow` happens to expose, so it also catches a
 *    generated alias equal to an `alreadyPresent` entry's name, or to one nothing in the source
 *    touches at all. **Exception:** a `replace` row's own alias, when it is one of *its own*
 *    target's aliases (`TransferRowTarget.aliases`) — that is the whole point of replacing (the
 *    REMOVE frees exactly that name, the ADD immediately reclaims
 *    it). No other row gets this pass, even onto the very same target: a `renameSource` row that
 *    types in a name a *different* row's `replaceTarget` decision would free is still a violation
 *    (even though `TransferPlan.rows` is now reordered so every `replace` runs before every
 *    `add`/`renameSource` — see that type's own doc — this rule stays deliberately independent of
 *    any row order: two separate runs solve a legitimate "reuse a freed name" case cleanly, an
 *    ordering-aware exception inside one run would not be worth its complexity). An
 *    `adoptSourceName` row never needs this exception (its alias, by construction, never equals one
 *    of its own target's held aliases — that disagreement is what makes it an alias mismatch in the
 *    first place), and an adopt never frees its target's *old* alias for reuse by another row either
 *    — adopting renames the entry in place, it does not remove and re-add it.
 * 3. **`invalidTypedAlias`** — every `renameSource.alias` must pass {@link isNameRejectedBySevenTv}.
 *    This is the one alias in the whole run a user actually types, so it is the only one checked:
 *    an unchanged `toAdd` row's name reaches 7TV unfiltered today (`import-preview.ts`'s own doc on
 *    `invalidNames` — 7TV decides, a row nobody touched must not block), and a `replace`/
 *    `adoptSourceName` row's alias is always the *source's own* name, never user input. A typed alias
 *    that is empty or pure whitespace already fails `isNameRejectedBySevenTv` (its rejected-character
 *    set includes every whitespace codepoint, and length `0` fails outright) — there is no separate
 *    "empty" case to special-case here.
 * 4. **`duplicateReplaceTarget`** — no two `replaceTarget` decisions may name the same target id (a
 *    #74 duplicate can be reached by two different `NameCollisionRow`s, one per alias, so this is not
 *    merely a decisions-map key collision).
 * 5. **`targetTouchedByReplaceAndAdopt`** — a `replaceTarget` and an `adoptSourceName` decision may
 *    never share a target id: the same id can appear both as a `NameCollisionRow`'s target (some
 *    other source row's name collided with one of its aliases) and as an `AliasMismatchRow`'s own id
 *    (a source row that *is* that id, under a different alias) — replacing it out from under an
 *    adopt, or vice versa, is a contradiction this rule catches regardless of which row was decided
 *    first.
 * 6. **`adoptBlocked`** — an `adoptSourceName` decision is only valid where `adoptBlocked === null`;
 *    this module does not trust the UI to have withheld the option.
 * 7. **`replaceNeedsTrackedTarget`** — a `replaceTarget` decision is only valid when
 *    `context.targetIsTracked`. Only a tracked channel's own resync can currently restore a deleted
 *    entry, so replacing into an untracked target has no way back yet. `skip`, `renameSource` and
 *    `adoptSourceName` are unaffected — none of them delete anything. The restriction is expected to
 *    lift with a future "restore per set" feature.
 */
export function validateResolution(
  preview: ImportPreview,
  decisions: ResolutionDecisions,
  context: ResolutionContext,
): ResolutionValidation {
  const rows = deriveTransferRows(preview, decisions);
  const violations = collectViolations(preview, decisions, rows, context);
  return violations.length === 0 ? { ok: true } : { ok: false, violations };
}

/**
 * Derives the one {@link TransferPlan} a preview and a set of decisions resolve to — throwing
 * instead of silently building a broken plan whenever the decisions fail validation.
 *
 * Takes the same {@link ResolutionContext} as `validateResolution` and runs that function itself
 * before deriving anything, so **all seven** rules are checked here, rule 7 included: a caller
 * cannot reach a `replaceTarget` decision against an untracked target by calling `buildTransferPlan`
 * directly instead of `validateResolution` first — this function gives the same guarantee on its
 * own. Calling `validateResolution` separately is only needed when a caller wants to *display*
 * violations before attempting a plan.
 */
export function buildTransferPlan(
  preview: ImportPreview,
  decisions: ResolutionDecisions,
  context: ResolutionContext,
): TransferPlan {
  const validation = validateResolution(preview, decisions, context);
  if (!validation.ok) {
    const ruleNames = validation.violations.map((violation) => violation.rule).join(', ');
    throw new Error(`Cannot build a transfer plan: decisions fail validation (${ruleNames}).`);
  }
  return { rows: deriveTransferRows(preview, decisions) };
}

/** `decisions` minus every decision a violation names — repeated until the rest validates. Dropping
 *  a decision only ever removes rows from the plan, so this terminates; the bound is a guard. */
export function withoutViolations(
  preview: ImportPreview,
  decisions: ResolutionDecisions,
  context: ResolutionContext,
): ResolutionDecisions {
  let current = decisions;
  for (let pass = 0; pass <= decisions.size; pass++) {
    const validation = validateResolution(preview, current, context);
    if (validation.ok) {
      return current;
    }
    const named = new Set(validation.violations.flatMap((violation) => violation.rowKeys));
    current = new Map([...current].filter(([key]) => !named.has(key)));
  }
  return new Map();
}

/** A skip decision and an absent one mean the same (see {@link decisionFor}) — this drops the
 *  explicit ones, so two maps that resolve to the same plan also compare equal. */
export function withoutSkips(decisions: ResolutionDecisions): ResolutionDecisions {
  return new Map([...decisions].filter(([, decision]) => decision.kind !== 'skip'));
}

/** Whether two decision maps hold the same decision for every key — compare {@link withoutSkips}
 *  of both when an explicit skip and an absent key should count as equal. */
export function sameDecisions(a: ResolutionDecisions, b: ResolutionDecisions): boolean {
  if (a.size !== b.size) {
    return false;
  }
  for (const [key, decision] of a) {
    const other = b.get(key);
    if (other === undefined || JSON.stringify(other) !== JSON.stringify(decision)) {
      return false;
    }
  }
  return true;
}

/** The summary numbers behind a confirm dialog and a run protocol — see {@link TransferPlanSummary}
 *  for what each field counts. */
export function summarizeTransferPlan(plan: TransferPlan): TransferPlanSummary {
  let addCount = 0;
  let removeCount = 0;
  let removedEntryCount = 0;
  let adoptCount = 0;

  for (const row of plan.rows) {
    switch (row.action) {
      case 'add':
      case 'renameSource':
        addCount++;
        break;
      case 'replace':
        addCount++;
        removeCount++;
        removedEntryCount += removedEntryCountOf(row.target);
        break;
      case 'adoptSourceName':
        adoptCount++;
        break;
      default:
        assertUnreachableAction(row);
    }
  }

  return { addCount, removeCount, removedEntryCount, adoptCount };
}

/** {@link TransferPlanSummary.removedEntryCount}'s per-target contribution: the target's named
 *  aliases plus, if present, its one aliasless entry — see {@link TransferRowTarget.hasAliaslessEntry}
 *  for why that can only ever be one extra entry, never more. */
function removedEntryCountOf(target: TransferRowTarget): number {
  return target.aliases.length + (target.hasAliaslessEntry ? 1 : 0);
}

/** The decision for one row, `'skip'` when the map carries none — the single place that default is
 *  applied (see {@link ResolutionDecisions}'s own doc). */
function decisionFor(decisions: ResolutionDecisions, rowKey: string): RowDecision {
  return decisions.get(rowKey) ?? { kind: 'skip' };
}

/**
 * Builds the row list both `validateResolution` and `buildTransferPlan` reason over — the one place
 * a {@link RowDecision} is interpreted in the context of the row it was made for, and the one place
 * `TransferPlan.rows`' grouped order (see that type's own doc) is assembled.
 * A decision that does not fit its row's type (e.g. `replaceTarget` keyed to an `AliasMismatchRow`,
 * or a key that matches neither a `NameCollisionRow` nor an `AliasMismatchRow` at all) is treated the
 * same as `skip`, not reported as a violation — see {@link RowDecision}'s own doc for why that is not
 * this module's rule to add.
 */
function deriveTransferRows(preview: ImportPreview, decisions: ResolutionDecisions): TransferRow[] {
  const addRows: AddOrRenameTransferRow[] = [];
  const renameRows: AddOrRenameTransferRow[] = [];
  const replaceRows: ReplaceOrAdoptTransferRow[] = [];
  const adoptRows: ReplaceOrAdoptTransferRow[] = [];

  for (const row of preview.toAdd) {
    addRows.push({ action: 'add', source: row, alias: row.name });
  }

  for (const collision of preview.nameCollisionRows) {
    const decision = decisionFor(decisions, collision.row.sevenTvEmoteId);
    switch (decision.kind) {
      case 'skip':
      case 'adoptSourceName':
        break;
      case 'renameSource':
        renameRows.push({ action: 'renameSource', source: collision.row, alias: decision.alias });
        break;
      case 'replaceTarget':
        replaceRows.push({
          action: 'replace',
          source: collision.row,
          alias: collision.row.name,
          target: {
            sevenTvEmoteId: collision.target.sevenTvEmoteId,
            aliases: collision.targetAliases,
            hasAliaslessEntry: collision.targetHasAliaslessEntry,
            // Stamped later, from a live read — see TransferRowTarget.defaultName's own doc.
            defaultName: null,
          },
        });
        break;
      default:
        assertUnreachableDecision(decision);
    }
  }

  for (const mismatch of preview.aliasMismatchRows) {
    const decision = decisionFor(decisions, mismatch.row.sevenTvEmoteId);
    switch (decision.kind) {
      case 'skip':
      case 'renameSource':
      case 'replaceTarget':
        break;
      case 'adoptSourceName':
        adoptRows.push({
          action: 'adoptSourceName',
          source: mismatch.row,
          alias: mismatch.row.name,
          target: {
            sevenTvEmoteId: mismatch.row.sevenTvEmoteId,
            aliases: mismatch.targetAliases,
            hasAliaslessEntry: false,
            // Stamped later, from a live read — see TransferRowTarget.defaultName's own doc.
            defaultName: null,
          },
        });
        break;
      default:
        assertUnreachableDecision(decision);
    }
  }

  // Grouped order — see `TransferPlan`'s own doc for why replace comes first.
  return [...replaceRows, ...adoptRows, ...addRows, ...renameRows];
}

/** Runs all seven rules and concatenates their findings. */
function collectViolations(
  preview: ImportPreview,
  decisions: ResolutionDecisions,
  rows: TransferRow[],
  context: ResolutionContext,
): Violation[] {
  return [
    ...ruleDuplicateGeneratedAlias(rows),
    ...ruleAliasHeldByTarget(preview, rows),
    ...ruleInvalidTypedAlias(preview, decisions),
    ...ruleDuplicateReplaceTarget(rows),
    ...ruleTargetTouchedByReplaceAndAdopt(rows),
    ...ruleAdoptBlocked(preview, decisions),
    ...ruleReplaceNeedsTrackedTarget(rows, context),
  ];
}

/** Rule 1 — see `validateResolution`'s doc, point 1. */
function ruleDuplicateGeneratedAlias(rows: TransferRow[]): Violation[] {
  const byAlias = new Map<string, TransferRow[]>();
  for (const row of rows) {
    const group = byAlias.get(row.alias);
    if (group) {
      group.push(row);
    } else {
      byAlias.set(row.alias, [row]);
    }
  }

  const violations: Violation[] = [];
  for (const group of byAlias.values()) {
    if (group.length < 2) {
      continue;
    }
    const allUnchangedAdds = group.every((row) => row.action === 'add');
    if (allUnchangedAdds) {
      continue;
    }
    violations.push({
      rule: 'duplicateGeneratedAlias',
      rowKeys: group.map((row) => row.source.sevenTvEmoteId),
    });
  }
  return violations;
}

/** `sevenTvEmoteId` of every `NameCollisionRow`/`AliasMismatchRow` whose `targetAliases` includes a
 *  given alias — normally one row, but a #74 duplicate target can be reached by two different
 *  `NameCollisionRow`s (one per alias) at once. Used only to enrich a rule-2 violation's `rowKeys`
 *  with the row(s) that happen to *expose* a held name through a conflict — the check itself runs
 *  against `ImportPreview.targetNames`, the full target name set, not this narrower map. */
function aliasExposedByRows(preview: ImportPreview): Map<string, string[]> {
  const exposedBy = new Map<string, string[]>();
  const record = (alias: string, rowKey: string) => {
    const keys = exposedBy.get(alias);
    if (!keys) {
      exposedBy.set(alias, [rowKey]);
    } else if (!keys.includes(rowKey)) {
      keys.push(rowKey);
    }
  };

  for (const collision of preview.nameCollisionRows) {
    for (const alias of collision.targetAliases) {
      record(alias, collision.row.sevenTvEmoteId);
    }
  }
  for (const mismatch of preview.aliasMismatchRows) {
    for (const alias of mismatch.targetAliases) {
      record(alias, mismatch.row.sevenTvEmoteId);
    }
  }
  return exposedBy;
}

/** Rule 2 — see `validateResolution`'s doc, point 2. Checks every generated alias against
 *  `preview.targetNames` — the full set of non-falsy names the target holds, not only the ones a
 *  `NameCollisionRow`/`AliasMismatchRow` happens to expose. */
function ruleAliasHeldByTarget(preview: ImportPreview, rows: TransferRow[]): Violation[] {
  const exposedBy = aliasExposedByRows(preview);
  const violations: Violation[] = [];

  for (const row of rows) {
    if (!preview.targetNames.has(row.alias)) {
      continue;
    }
    const isOwnReplace = row.action === 'replace' && row.target.aliases.includes(row.alias);
    if (isOwnReplace) {
      continue;
    }
    const rowKeys = [row.source.sevenTvEmoteId];
    for (const exposerKey of exposedBy.get(row.alias) ?? []) {
      if (!rowKeys.includes(exposerKey)) {
        rowKeys.push(exposerKey);
      }
    }
    violations.push({ rule: 'aliasHeldByTarget', rowKeys });
  }
  return violations;
}

/** Rule 3 — see `validateResolution`'s doc, point 3. */
function ruleInvalidTypedAlias(
  preview: ImportPreview,
  decisions: ResolutionDecisions,
): Violation[] {
  const violations: Violation[] = [];
  for (const collision of preview.nameCollisionRows) {
    const decision = decisionFor(decisions, collision.row.sevenTvEmoteId);
    if (decision.kind === 'renameSource' && isNameRejectedBySevenTv(decision.alias)) {
      violations.push({ rule: 'invalidTypedAlias', rowKeys: [collision.row.sevenTvEmoteId] });
    }
  }
  return violations;
}

/** Rule 4 — see `validateResolution`'s doc, point 4. */
function ruleDuplicateReplaceTarget(rows: TransferRow[]): Violation[] {
  const byTargetId = new Map<string, TransferRow[]>();
  for (const row of rows) {
    if (row.action !== 'replace') {
      continue;
    }
    const group = byTargetId.get(row.target.sevenTvEmoteId);
    if (group) {
      group.push(row);
    } else {
      byTargetId.set(row.target.sevenTvEmoteId, [row]);
    }
  }

  const violations: Violation[] = [];
  for (const group of byTargetId.values()) {
    if (group.length < 2) {
      continue;
    }
    violations.push({
      rule: 'duplicateReplaceTarget',
      rowKeys: group.map((row) => row.source.sevenTvEmoteId),
    });
  }
  return violations;
}

/** Rule 5 — see `validateResolution`'s doc, point 5. */
function ruleTargetTouchedByReplaceAndAdopt(rows: TransferRow[]): Violation[] {
  const replacesByTargetId = new Map<string, TransferRow[]>();
  const adoptsByTargetId = new Map<string, TransferRow[]>();

  for (const row of rows) {
    if (row.action !== 'replace' && row.action !== 'adoptSourceName') {
      continue;
    }
    const bucket = row.action === 'replace' ? replacesByTargetId : adoptsByTargetId;
    const group = bucket.get(row.target.sevenTvEmoteId);
    if (group) {
      group.push(row);
    } else {
      bucket.set(row.target.sevenTvEmoteId, [row]);
    }
  }

  const violations: Violation[] = [];
  for (const [targetId, replaceRows] of replacesByTargetId) {
    const adoptRows = adoptsByTargetId.get(targetId);
    if (!adoptRows) {
      continue;
    }
    violations.push({
      rule: 'targetTouchedByReplaceAndAdopt',
      rowKeys: [...replaceRows, ...adoptRows].map((row) => row.source.sevenTvEmoteId),
    });
  }
  return violations;
}

/** Rule 6 — see `validateResolution`'s doc, point 6. */
function ruleAdoptBlocked(preview: ImportPreview, decisions: ResolutionDecisions): Violation[] {
  const violations: Violation[] = [];
  for (const mismatch of preview.aliasMismatchRows) {
    const decision = decisionFor(decisions, mismatch.row.sevenTvEmoteId);
    if (decision.kind === 'adoptSourceName' && mismatch.adoptBlocked !== null) {
      violations.push({ rule: 'adoptBlocked', rowKeys: [mismatch.row.sevenTvEmoteId] });
    }
  }
  return violations;
}

/** Rule 7 — see `validateResolution`'s doc, point 7. */
function ruleReplaceNeedsTrackedTarget(
  rows: TransferRow[],
  context: ResolutionContext,
): Violation[] {
  if (context.targetIsTracked) {
    return [];
  }
  const violations: Violation[] = [];
  for (const row of rows) {
    if (row.action === 'replace') {
      violations.push({ rule: 'replaceNeedsTrackedTarget', rowKeys: [row.source.sevenTvEmoteId] });
    }
  }
  return violations;
}

/** Reached only when a new {@link RowDecision} member skips a `switch` above — the parameter type is
 *  what makes that a build error rather than a runtime surprise, mirroring
 *  `assertUnreachableOrigin` in `core/seven-tv/import-source.ts`. */
function assertUnreachableDecision(decision: never): never {
  throw new Error(`Unknown row decision: ${JSON.stringify(decision)}`);
}

/** Reached only when a new {@link TransferRow} variant skips the `switch` in `summarizeTransferPlan`
 *  — same purpose as {@link assertUnreachableDecision}, for the other union in this module. */
function assertUnreachableAction(row: never): never {
  throw new Error(`Unknown transfer row: ${JSON.stringify(row)}`);
}
