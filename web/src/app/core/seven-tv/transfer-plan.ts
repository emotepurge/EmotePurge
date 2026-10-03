import { ImportRow } from './import-source';

/** One target entry a `TransferRow` touches — `replace` and `adoptSourceName` rows carry this,
 *  `add` and `renameSource` rows never do (nothing about the target changes for either) — enforced
 *  at the type level by {@link TransferRow}'s discriminated union. */
export interface TransferRowTarget {
  sevenTvEmoteId: string;
  /** Every *named* alias the target entry holds — `NameCollisionRow`'s `targetAliases` for a
   *  `replace` row, `AliasMismatchRow`'s for an `adoptSourceName` row when the preview is first
   *  built, but overwritten with the live read the moment a drift overlay (`overlayPreview`) or the
   *  pre-run stamp (`stampReplaceTargets`) runs — from then on this is what the target actually
   *  holds, not a snapshot from when the preview was built. */
  aliases: string[];
  /** Whether the target id also carries an *aliasless* entry — always `false` for an
   *  `adoptSourceName` row (`AliasMismatchRow` carries no such marker, and adopting renames an
   *  existing entry in place, so nothing about entry count changes). Only a `replace` row can carry
   *  `true`, from `NameCollisionRow.targetHasAliaslessEntry`. */
  hasAliaslessEntry: boolean;
  /** The target emote's 7TV default name, or `null` while unknown — the transfer-run protocol's
   *  only way to name an aliasless entry (its display name in a restore preview, and the name 7TV
   *  falls back to after an ADD without an alias). `buildTransferPlan` always sets this `null`; the
   *  import confirm dialog stamps it from its own live-verified read once the target is confirmed. */
  defaultName: string | null;
}

/** An `add` or `renameSource` row — an ADD mutation against a name nothing in the target holds
 *  (`add`) or a fresh, user-typed alias (`renameSource`). Neither touches an existing target entry,
 *  so neither carries a {@link TransferRowTarget}. */
export interface AddOrRenameTransferRow {
  action: 'add' | 'renameSource';
  /** The source row this transfer row resolves. */
  source: ImportRow;
  /** The alias the ADD mutation writes — the row's own name for `add`, the user-typed alias for
   *  `renameSource`. */
  alias: string;
}

/** A `replace` or `adoptSourceName` row — both touch an existing target entry (a REMOVE-then-ADD for
 *  `replace`, an UPDATE for `adoptSourceName`), so both carry the {@link TransferRowTarget} the
 *  mutation runs against. Required, not optional: a discriminated union by `action` makes a
 *  target-less `replace`/`adoptSourceName` row a compile error rather than a runtime throw. */
export interface ReplaceOrAdoptTransferRow {
  action: 'replace' | 'adoptSourceName';
  /** The source row this transfer row resolves. */
  source: ImportRow;
  /** The alias the mutation writes — the row's own name for both `replace` (the REMOVE frees it,
   *  the ADD immediately reclaims it) and `adoptSourceName` (the UPDATE renames the target entry to
   *  it). */
  alias: string;
  target: TransferRowTarget;
}

/** One row of a `TransferPlan` — the one 7TV mutation (or plain ADD) a source row resolves to.
 *  `'add'` is an unconditional carry-over of a `toAdd` row (no decision applies to it, there is
 *  nothing to decide); the other three actions each come from exactly one {@link RowDecision}
 *  applied to a `NameCollisionRow` or `AliasMismatchRow` — see `deriveTransferRows`. A discriminated
 *  union on `action`: `target` is only ever present on the two variants that touch an existing
 *  target entry. */
export type TransferRow = AddOrRenameTransferRow | ReplaceOrAdoptTransferRow;

/**
 * The one transfer plan `buildTransferPlan` derives from a preview and a set of decisions.
 *
 * `rows` is **not** in source order. It is grouped, in this fixed order: every `replace` row first,
 * then every `adoptSourceName` row, then every `add` row, then every `renameSource` row — each group
 * internally in the order `buildImportPreview` produced it in.
 *
 * **Why replace-then-adopt-then-add-then-rename, and not source order:** a `replace` row's REMOVE
 * always frees at least as many target entries as its own ADD takes back (one ADD against one or two
 * freed entries — `TransferRowTarget`'s own doc), so running every `replace` before any `add` can
 * only ever *lower* peak occupancy, never raise it. Running them in plain source order instead can
 * overflow a full target set mid-run even when `projectSlots`' net projection says the plan fits —
 * example: a target at 100/100 capacity with one plain add ahead of one replace on a #74 duplicate
 * projects to 100 (no overflow), but if the add runs first against the still-full set, it fails live.
 * Ordering `replace` first removes that peak entirely, for every plan, not just the ones a caller
 * happens to submit with replace decisions already first.
 *
 * **Why this reordering cannot break anything rule 2 already forbids cross-row ordering
 * dependencies for:** after `validateResolution` reports `ok: true`, no row's mutation depends on
 * another row having run first. Rule 2 already forbids any generated alias equal to a name the
 * target holds, except a `replace` row's own freed name — so no row relies on some *other* row's
 * replace having already freed a name it wants to reuse. Rule 1's one exception (two untouched `add`
 * rows sharing a name) only concerns two rows within the same group, whose relative order this
 * reordering never changes. Each `replace`'s own REMOVE and ADD live inside one row regardless of
 * where the plan places it. `ImportPreview` itself exposes no single, fully interleaved row order
 * across `toAdd`/`nameCollisionRows`/`aliasMismatchRows` to begin with, so "source order" was never
 * more than an approximation this function could reach purely from `(preview, decisions)`.
 */
export interface TransferPlan {
  rows: TransferRow[];
}
