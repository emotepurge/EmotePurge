import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of } from 'rxjs';

import { SevenTvSetEntries, loadSevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { ReplaceOrAdoptTransferRow, TransferPlan } from '../../core/seven-tv/transfer-plan';

export interface AlreadyPresentFilterResult<T> {
  /** `rows` minus every entry already present in the target set. What the run should actually send —
   *  unfiltered (a copy of the input) when `available` is `false`. */
  rows: T[];
  /** How many entries were removed because they were already present. Always `0` when `available` is
   *  `false` — a failed check found nothing, it verified nothing. */
  skipped: number;
  /** Whether the check actually ran. `false` means the fetch failed: every row still passed through
   *  (see `rows`), but nobody verified it against the target set, so an undetected duplicate is
   *  possible. Same vocabulary as `EmoteSetWarning.available` in `emote-admin.service.ts` and the
   *  `UNAVAILABLE_WARNING` fallback in `import-target-loader.ts` — a failed check reads as "not
   *  verified", never as a false all-clear. */
  available: boolean;
}

/** `filterAlreadyPresent`'s result: the filter's own outcome plus the read it was computed from, so
 *  a caller that needs a second comparison against the same state of the set (the import flow's
 *  `verifyReplaceTargets`) does not have to read the set a second time. `null` when the read
 *  failed — the same case as `available: false`. */
export interface ImportAlreadyPresentFilterResult<T> extends AlreadyPresentFilterResult<T> {
  entries: SevenTvSetEntries | null;
}

/**
 * The one place that guards against #149's duplicate-push hole (docs/plans/Plan-149-7TV-v4-Schreibflaeche.md,
 * §0.6/T5): 7TV's `addEmote` mutation does not dedupe by emote id — it only rejects a colliding
 * *alias* string, otherwise it blindly appends. An emote already in the target set under a
 * *different* alias therefore gets pushed a second time, and a rollback cannot undo that; 7TV keeps
 * the duplicate. This became reachable once #149 moved the write surface to `v4`, whose alias
 * validator lets umlaut aliases through where `v3` used to reject them before the push ever ran.
 *
 * Used at the last moment before a run actually starts — import (`import-flow.ts`) calls this, and
 * both restore entry points (`restore-flow.ts`, `mass-delete-panel.ts`) call its restore variant
 * `filterAlreadyPresentForRestore` below, right in the confirm-dialog-closed handler, immediately
 * before handing rows to `startRestore`/`startImport` — so the fetch it does is always fresh, never
 * a dialog-open-time snapshot reused later. This one compares the 7TV id alone (spec #200, 7.2: the
 * import and the delete path stay on the id axis). For import
 * this sits *on top of* `buildImportPreview`'s own dialog-time filter (`import-preview.ts`), not
 * instead of it: that filter can already be stale by the time the user actually confirms (another
 * editor, another tab, a long-open dialog), so this re-checks right before anything is sent.
 *
 * What this check can and cannot see: it asks **7TV itself**, not our database — a P1 review finding
 * on the first version of this filter caught it asking `EmoteAdminService.listEmotes`, which serves
 * our Postgres mirror (`ListActiveAsync`, `!IsArchived`). That is wrong specifically for restore:
 * restore is the operation run *because* something already went wrong, most often right after a
 * delete whose closing `sync-deleted` report to our own backend is still pending, failed, or only
 * partially applied. In exactly that window our database still lists the just-deleted emotes as
 * active, while 7TV has already dropped them — so a check against our own API would misclassify the
 * rows the user is trying to restore as "already present", hand `startRestore` an empty or partial
 * queue, and silently not roll back the very thing the user is here to undo. Reading 7TV's live
 * `emoteSet` contents instead has no such staleness relative to our own mirror; it is the
 * authoritative source for what asking the alias-collision question is really about. Reading it also
 * costs nothing extra worth worrying about: this draws on 7TV's *global* rate-limit bucket
 * (5000/60s, HTTP-layer), not the far tighter `emote_set_change` bucket the mutations themselves
 * share — one read per run is negligible against it.
 *
 * And even against a perfectly fresh view, a window remains between this check and each individual
 * `addEmote` call, in which another editor could write to the set. That race cannot be closed
 * without an atomic operation on 7TV's side, and this code does not attempt to close it.
 *
 * Fails open on a failed fetch — every row still passes through unfiltered, rather than blocking a
 * run the user already confirmed; a best-effort safety net, not a hard gate. What changed from the
 * first version of this filter is *only the reporting*: a failed fetch now sets `available: false`
 * instead of the indistinguishable `skipped: 0` a successful, nothing-to-skip check also produces.
 * That distinction is the rule `import-target-loader.ts` states explicitly for its own
 * `UNAVAILABLE_WARNING` fallback — "a failed check must read as 'not verified', never as a false
 * all-clear" — which this filter cited as precedent without actually following before this fix. It
 * matters more here than there: `UNAVAILABLE_WARNING` only silences an informational hint, but a
 * duplicate this filter misses is unrepairable (see the class doc above) — an unnoticed unguarded
 * run is worse than a visible one, so the failure has to reach the caller, not just the log.
 */
export function filterAlreadyPresent<T extends { sevenTvEmoteId: string }>(
  httpClient: HttpClient,
  targetSetId: string,
  rows: readonly T[],
): Observable<ImportAlreadyPresentFilterResult<T>> {
  return loadSevenTvSetEntries(httpClient, targetSetId).pipe(
    map((entries) => {
      const filtered = rows.filter((row) => !entries.aliasesById.has(row.sevenTvEmoteId));
      return { rows: filtered, skipped: rows.length - filtered.length, available: true, entries };
    }),
    catchError(() => of({ rows: [...rows], skipped: 0, available: false, entries: null })),
  );
}

/** A restore row: one purge-protocol row or one removed transfer target, re-added once per alias
 *  (spec #200, 7.2). `aliases` missing or empty means `[name]`, the same fallback the restore queue
 *  applies. A `null` alias is an entry without an alias (only a transfer-run file records one). */
export interface RestoreFilterRow {
  sevenTvEmoteId: string;
  name: string;
  aliases?: readonly (string | null)[];
}

/** `filterAlreadyPresentForRestore`'s result: the shared filter outcome plus the aliases dropped
 *  because another emote now holds their name (rule 4). Every alias of the input is accounted for
 *  exactly once — sent (`rows`), `skipped` or `skippedNameTaken`. */
export interface RestoreAlreadyPresentFilterResult<T> extends AlreadyPresentFilterResult<T> {
  /** How many aliases were dropped because a *different* id holds that name in the target set.
   *  Kept apart from `skipped` ("already present"): the entry is not back, it cannot come back under
   *  that name. Always `0` when `available` is `false`. */
  skippedNameTaken: number;
  /** `SevenTvSetEntries.complete` from the read this result was computed from (#255 P2, Codex
   *  review) — `false` when the read stopped at the runaway guard or a `totalCount` mismatch even
   *  though the fetch itself succeeded (`available: true`). This does **not** change what gets
   *  filtered: `filterAlreadyPresentForRestore`'s own doc explains why a truncated read still filters
   *  against whatever it saw rather than failing the whole check open. It exists so a caller that
   *  turns this result into an exact-sounding count — the restore confirmation's title and slot
   *  projection (`loadRestoreConfirmPreview`) — can tell "verified against the whole set" apart from
   *  "verified against only part of it" and hedge its wording accordingly, the same way it already
   *  hedges on `available: false`. Always `false` when `available` is `false`: a failed fetch saw
   *  nothing at all, complete or otherwise. */
  complete: boolean;
}

/**
 * The restore run's pre-run check — `filterAlreadyPresent`'s id comparison, refined per alias
 * ("middle rule", operator decision 2026-09-22, refining spec #200 7.2's literal
 * `(sevenTvEmoteId, alias)` comparison). Per row, against the target set's live entries:
 *
 * 1. **The id is not in the set** — every alias of the row is missing (rule 4 still applies).
 * 2. **The id sits in the set under an alias the row does not name** — the whole row is dropped,
 *    exactly as the id-only check always did. Re-adding any of its aliases would put the same emote
 *    into the set a second time under another name: the #149 hole this filter exists for (7TV's
 *    `addEmote` only rejects a colliding alias string, never a second entry of the same id). A live
 *    entry *without* an alias counts as such an unnamed alias — unless the row itself names one
 *    (`null`), in which case it is that entry of the row, not a foreign one.
 * 3. **The id sits in the set only under aliases the row names** — those aliases are dropped from
 *    the row, the rest are missing. This is the partial retry of a #74 duplicate cell: a restore
 *    in which `A` came back and `B` failed is re-run from the same protocol, and `B` is the only
 *    thing still missing. The id-only check dropped the whole row there, and `B` was then
 *    unrecoverable from the protocol (spec 7.2, "Vorprüfung des Restore"). A row's `null` alias is
 *    present when the id has a live entry without an alias (`aliaslessIds`), missing otherwise.
 * 4. **A missing alias that a different id now holds in the set** is dropped from the row too, and
 *    counted in `skippedNameTaken`, not in `skipped`. Its `ADD` could only ever end in 7TV's name
 *    conflict (a burnt ticket and a red row): the name went to another emote since the file was
 *    written — after a successful "replace target" transfer the source emote holds it by design,
 *    after a purge someone may have reused it. Nothing is removed to make room; a restore only
 *    closes gaps. The held names come from the same read (`aliasesById`), no second request, and
 *    only named aliases are compared — a `null` alias names nothing yet.
 *
 * A row none of whose aliases is left drops out entirely. Every row, whichever source it came from
 * (purge-run protocol, transfer-run file, finished delete run), goes through all four rules.
 *
 * `skipped` and `skippedNameTaken` count **aliases**, i.e. `ADD`s not sent, not rows (a `null`
 * alias is one): the restore confirmation already speaks in `ADD`s
 * (`RestoreConfirmDialogData.addCount`) and the run's own queue is one row per `ADD`
 * (`${sevenTvEmoteId}#${alias}`), so what the run shows plus both counts adds up to the number the
 * dialog named. For every single-alias row — nearly all of them — the two units are the same thing.
 * Fails open exactly like `filterAlreadyPresent` (see there).
 *
 * `complete: false` from the read (the 10-page runaway guard, or a `totalCount` mismatch — K5 fix
 * round, see `seven-tv-set-entries.ts`) is deliberately **not** treated as a reason to fail open
 * here, unlike the delete run's own live alias read (`mass-delete-panel.ts`, spec 8.3's "a list
 * that only knows half must not delete"): failing open would return every row completely
 * unfiltered, while the per-alias comparison below, even against a partial read, still catches
 * every duplicate and every taken name genuinely inside the pages it did see — strictly fewer wrong
 * re-adds than discarding that signal outright would produce. This only widens the existing,
 * already-accepted gap (a window remains, always has, between any read — complete or not — and each
 * individual `addEmote` call); it does not create a new one. Restore only ever fails open
 * (available: false, nothing filtered) on an actual fetch/GraphQL error.
 *
 * The read's own `complete` flag is still passed through on the result (#255 P2, Codex review),
 * separately from this filtering decision — a caller that turns `rows`/`skipped` into an
 * exact-sounding count (the restore confirmation, `loadRestoreConfirmPreview`) needs to know when
 * that count was only ever checked against part of the set, so it can say "up to N" instead of a
 * number it cannot actually vouch for. What is filtered does not change; only what a caller may
 * claim about the result does.
 */
export function filterAlreadyPresentForRestore<T extends RestoreFilterRow>(
  httpClient: HttpClient,
  targetSetId: string,
  rows: readonly T[],
): Observable<RestoreAlreadyPresentFilterResult<T>> {
  return loadSevenTvSetEntries(httpClient, targetSetId).pipe(
    map(({ aliasesById, aliaslessIds, complete }) => {
      const heldNames = new Set([...aliasesById.values()].flat());
      const kept: T[] = [];
      let skipped = 0;
      let skippedNameTaken = 0;
      for (const row of rows) {
        const rowAliases: readonly (string | null)[] =
          row.aliases && row.aliases.length > 0 ? row.aliases : [row.name];
        const missing = missingAliases(row.sevenTvEmoteId, rowAliases, aliasesById, aliaslessIds);
        skipped += rowAliases.length - missing.length;
        // A missing alias is never held by the row's own id (that would make it present), so any
        // holder is another emote.
        const free = missing.filter((alias) => alias === null || !heldNames.has(alias));
        skippedNameTaken += missing.length - free.length;
        if (free.length === rowAliases.length) {
          kept.push(row);
        } else if (free.length > 0) {
          kept.push({ ...row, aliases: free });
        }
      }
      return { rows: kept, skipped, skippedNameTaken, available: true, complete };
    }),
    catchError(() =>
      of({ rows: [...rows], skipped: 0, skippedNameTaken: 0, available: false, complete: false }),
    ),
  );
}

/**
 * Intersects a fresh `filterAlreadyPresentForRestore` result with what an earlier, open-time run
 * of the same check already showed the user (`shown` — a `RestoreConfirmPreview.rows`) — id by id,
 * then alias by alias for whichever ids survive that. The result can only be a subset of `rows`,
 * never anything beyond `shown`: a row whose id is not in `shown` at all (every alias of it was
 * already hidden from the confirmation) is dropped even if `rows` calls it missing, and a row that
 * only partially survived the open-time filter keeps at most the aliases `shown` still names for
 * that id, however many `rows` itself found missing.
 *
 * Confirm-time (#255 P1, Codex review): the confirmation only ever shows the open-time check's
 * `rows`, so `startRestore` must never be handed more than that — but the confirm-time re-check
 * cannot simply be *run* over `shown` instead of the caller's full original row set, because
 * `filterAlreadyPresentForRestore`'s rule 2 needs a row's *complete* alias list to tell an entry
 * genuinely foreign to the row from one of the row's own aliases that a narrower input would no
 * longer name — feeding it only `shown`'s already-trimmed aliases would misclassify a live entry
 * under the row's own *other*, correctly-still-missing alias as foreign and drop it outright (the
 * #74 duplicate-cell partial retry this filter exists to support). So the confirm-time check keeps
 * querying with full context, and this function clips its answer down afterward instead — the
 * narrowing the rule needs and the narrowing the confirmation promised stay two separate steps.
 *
 * Named for what it does to `rows`, not for when it runs: this is a pure intersection, no request
 * of its own.
 */
export function clipToShown<T extends RestoreFilterRow>(
  rows: readonly T[],
  shown: readonly RestoreFilterRow[],
): T[] {
  const shownAliasesById = new Map(shown.map((row) => [row.sevenTvEmoteId, clipAliasSet(row)]));
  const clipped: T[] = [];
  for (const row of rows) {
    const allowed = shownAliasesById.get(row.sevenTvEmoteId);
    if (allowed === undefined) {
      continue;
    }
    const kept = clipRowAliases(row).filter((alias) => allowed.has(alias));
    if (kept.length > 0) {
      clipped.push({ ...row, aliases: kept });
    }
  }
  return clipped;
}

/** A row's aliases, applying the same `[name]` fallback `filterAlreadyPresentForRestore` and the
 *  restore queue both use for a row with no `aliases` of its own. Named apart from that function's
 *  own identically-shaped local (`rowAliases`, inside its loop) purely to avoid shadowing it —
 *  `clipToShown` is the only caller. */
function clipRowAliases(row: RestoreFilterRow): (string | null)[] {
  return row.aliases && row.aliases.length > 0 ? [...row.aliases] : [row.name];
}

function clipAliasSet(row: RestoreFilterRow): ReadonlySet<string | null> {
  return new Set(clipRowAliases(row));
}

/** `loadRestoreConfirmPreview`'s result: `filterAlreadyPresentForRestore`'s own outcome, plus the
 *  two numbers the restore confirmation dialog actually renders — derived here, once, so its two
 *  call sites (`restore-flow.ts`, `mass-delete-panel.ts`) compute them identically rather than each
 *  reimplementing the same reduction over `rows`. */
export interface RestoreConfirmPreview<
  T extends RestoreFilterRow,
> extends RestoreAlreadyPresentFilterResult<T> {
  /** Display names for the confirmation's name-preview list — one per surviving row, in the same
   *  order `filterAlreadyPresentForRestore` returned them. Unfiltered (every input row's name) when
   *  `available` is `false`: a failed check fails open, so nothing was actually dropped from `rows`
   *  either, only the *reason* to trust that count differs (see `countIsUpperBound` on
   *  `RestoreConfirmDialogData`). Still filtered, and still worth showing, when `complete` is
   *  `false`: a truncated read only ever widens `rows` (an id it never saw counts as missing), never
   *  narrows it — see `filterAlreadyPresentForRestore`'s doc on why that stays fail-*open*, not a
   *  reason to discard the list. */
  names: string[];
  /** The confirmation's ADD count (spec #200, 7.2) — aliases, not rows, computed over the *filtered*
   *  `rows` rather than the caller's original input, so it reports what the run will actually send
   *  once it starts (operator decision 2026-09-25, #255). Same caveat as `names` when `available` is
   *  `false`, and the same "still meaningful, just not guaranteed exact" caveat when `complete` is
   *  `false` — a caller renders both as an upper bound rather than an exact count in either case
   *  (`countIsUpperBound` on `RestoreConfirmDialogData` is `!available || !complete`). */
  addCount: number;
}

/**
 * `filterAlreadyPresentForRestore` plus the confirmation dialog's own derived numbers (operator
 * decision 2026-09-25, #255, "Slot-Zahl nach dem Skip-Filter"): before this, both restore
 * confirmations (`restore-flow.ts`'s `startRestoreFlow`, `mass-delete-panel.ts`'s
 * `openRestoreConfirmDialog`) ran the duplicate/name-taken check only once the user actually
 * confirmed, so the dialog's title and its slot-capacity projection counted every row the source
 * named — including ones that were about to be silently skipped as already present. Now both call
 * sites run this fresh 7TV read once more, right when the confirmation is about to open, and show
 * its filtered result instead: the number displayed is the number that will actually be sent, not
 * an upper bound that happens to match it only when nothing gets skipped.
 *
 * This does **not** add a second kind of 7TV read next to the slot preview
 * (`restore-slot-preview.ts`'s `loadRestoreSlotPreview`): that one reads a set's *occupied/capacity
 * counts*, a question this filter's read (`loadSevenTvSetEntries`, full alias membership) cannot
 * answer at all, since it never requests a capacity field. The two stay separate reads for separate
 * questions; what changes here is only *when* the existing duplicate-check read runs, from
 * confirm-time-only to open-time-and-confirm-time (the confirm-time read stays exactly as it was —
 * deliberately re-run fresh right before the run starts, not reused from this earlier snapshot, for
 * the same staleness reason `filterAlreadyPresentForRestore`'s own doc gives).
 */
export function loadRestoreConfirmPreview<T extends RestoreFilterRow>(
  httpClient: HttpClient,
  targetSetId: string,
  rows: readonly T[],
): Observable<RestoreConfirmPreview<T>> {
  return filterAlreadyPresentForRestore(httpClient, targetSetId, rows).pipe(
    map((result) => ({
      ...result,
      names: result.rows.map((row) => row.name),
      addCount: restoreAddCount(result.rows),
    })),
  );
}

/** Total time budget for the open-time duplicate check both restore entry points
 *  (`restore-flow.ts`'s `startRestoreFlow`, `mass-delete-panel.ts`'s `openRestoreConfirmDialog`)
 *  run right before their confirmation opens (#255 P2a) — same value and reasoning as
 *  `mass-delete-panel.ts`'s own `LIVE_ALIAS_READ_TIMEOUT_MS`: generous for a same-origin-adjacent
 *  GraphQL read of at most 10 pages of up to 500 entries each, against a hung request (7TV accepts
 *  the connection but never answers). Exported rather than duplicated as a private constant in both
 *  callers, or imported from `mass-delete-panel.ts` itself, which would make `restore-flow.ts`
 *  depend on a component file for a plain number. */
export const RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS = 20_000;

/** The same "could not verify" shape `loadRestoreConfirmPreview`'s own failed fetch produces
 *  (`available: false`, every row passed through unfiltered) — for a caller whose own wrapping
 *  `timeout(RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS)` fires before the read itself does (#255 P2a). A
 *  `timeout` error surfaces *outside* `loadRestoreConfirmPreview`/`filterAlreadyPresentForRestore`,
 *  so their own internal `catchError` never sees it and never gets a chance to build this shape —
 *  a caller applying its own timeout on top has to build it itself, from exactly the rows it sent.
 *  Never reduces `rows` to nothing on its own (unlike a genuine filtered answer): a caller that
 *  reaches for this always still has a confirmation to open, hedged as an upper bound
 *  (`RestoreConfirmDialogData.countIsUpperBound`), never the "everything already there" shortcut. */
export function restoreConfirmPreviewUnavailable<T extends RestoreFilterRow>(
  rows: readonly T[],
): RestoreConfirmPreview<T> {
  return {
    rows: [...rows],
    skipped: 0,
    skippedNameTaken: 0,
    available: false,
    complete: false,
    names: rows.map((row) => row.name),
    addCount: restoreAddCount(rows),
  };
}

/** How many `ADD`s `rows` will send — one per alias, a `null` alias (an entry without one) included,
 *  a row with no `aliases` at all (or an empty one) counted once for its bare `name` (the same
 *  fallback `filterAlreadyPresentForRestore` and the restore queue both apply). Shared by
 *  `loadRestoreConfirmPreview` above and nothing else — the two call sites used to each carry their
 *  own copy of this reduction. */
function restoreAddCount(rows: readonly { aliases?: readonly (string | null)[] }[]): number {
  return rows.reduce(
    (sum, row) => sum + (row.aliases && row.aliases.length > 0 ? row.aliases.length : 1),
    0,
  );
}

/** Rules 1–3 of `filterAlreadyPresentForRestore` for one row: the aliases of `rowAliases` the set
 *  does not hold under `id` yet — all of them when the id is absent, none when the id sits under
 *  an entry the row does not name (rule 2). */
function missingAliases(
  id: string,
  rowAliases: readonly (string | null)[],
  aliasesById: ReadonlyMap<string, readonly string[]>,
  aliaslessIds: ReadonlySet<string>,
): (string | null)[] {
  const present = aliasesById.get(id);
  if (present === undefined) {
    // Never encountered at all — not in the set, not even under an aliasless entry (every entry
    // this reader sees, aliased or not, gets a map entry; see `loadSevenTvSetEntries`).
    return [...rowAliases];
  }
  // An entry 7TV lists without an alias occupies the set under a name the row cannot vouch for, so
  // it takes rule 2 like any foreign alias would — even when the same id also has an aliased entry
  // the row does name (K5 fix round, spec §37/§38: an aliasless entry must not be silently absorbed
  // by a sibling aliased entry of the same id). A row that names an aliasless entry itself (`null`)
  // does vouch for it: reading it as foreign would drop every such row without a trace.
  const hasAliaslessLive = aliaslessIds.has(id);
  const foreignEntry =
    (hasAliaslessLive && !rowAliases.includes(null)) ||
    present.some((alias) => !rowAliases.includes(alias));
  if (foreignEntry) {
    return [];
  }
  return rowAliases.filter((alias) =>
    alias === null ? !hasAliaslessLive : !present.includes(alias),
  );
}

/** Why a replace row's target no longer matches what the user confirmed. */
export type ReplaceTargetDriftReason =
  /** The target id is not in the set any more. */
  | 'targetGone'
  /** The colliding name now belongs to a different emote id. */
  | 'nameHeldElsewhere'
  /** The target id sits under a different set of named aliases. */
  | 'aliasesChanged'
  /** The target id gained or lost an entry without an alias. */
  | 'aliaslessEntryChanged';

/** One replace row whose target drifted, with what the set holds for that target id right now. */
export interface ReplaceTargetDrift {
  /** The row's key: its source `sevenTvEmoteId`, the same key the decisions map and the run queue
   *  use. */
  key: string;
  reason: ReplaceTargetDriftReason;
  /** The target id's live entries, taken from the same read — in the shape of
   *  `TransferRowTarget`, minus the id, so a caller can overlay the row's confirmed target with it
   *  and let the user confirm again. `null` when the target id is gone from the set. */
  live: { aliases: string[]; hasAliaslessEntry: boolean } | null;
}

/**
 * `verifyReplaceTargets`' result. `available: false` means the read cannot vouch for any target
 * (it stopped short of the whole set, `complete: false`): **no** replace row passes, unlike the
 * duplicate filter, which fails open — deleting on an unchecked basis is the worse outcome (spec
 * #200, 8.3: a list that only knows half must not delete). A caller whose read failed outright
 * applies the same rule without calling this function.
 */
export type ReplaceTargetVerification =
  { available: true; drifted: ReplaceTargetDrift[] } | { available: false };

/**
 * Compares every `replace` row of `plan` with the target set as `entries` read it, at entry level:
 * the target id is still in the set, the name the row replaces is still held by that id, the id's
 * named aliases equal `target.aliases` as a set, and whether the id has an aliasless entry equals
 * `target.hasAliaslessEntry`. A single `removeEmote` takes every entry of the id, so an entry the
 * confirmed state did not know about — a new alias or a new aliasless entry — would be deleted
 * without the user having seen it.
 *
 * Pure, over an already completed read, so the confirm dialog (before the recovery file is written)
 * and the import flow (right before the run starts) compute the same comparison. Rows of any other
 * action are ignored. The window between this read and each individual `removeEmote` stays open —
 * the same residual race `filterAlreadyPresent` documents.
 */
export function verifyReplaceTargets(
  entries: SevenTvSetEntries,
  plan: TransferPlan,
): ReplaceTargetVerification {
  if (!entries.complete) {
    return { available: false };
  }
  const drifted: ReplaceTargetDrift[] = [];
  for (const row of plan.rows) {
    if (row.action !== 'replace') {
      continue;
    }
    const reason = replaceTargetDriftReason(entries, row);
    if (reason === null) {
      continue;
    }
    const targetId = row.target.sevenTvEmoteId;
    const liveAliases = entries.aliasesById.get(targetId);
    drifted.push({
      key: row.source.sevenTvEmoteId,
      reason,
      live:
        liveAliases === undefined
          ? null
          : { aliases: [...liveAliases], hasAliaslessEntry: entries.aliaslessIds.has(targetId) },
    });
  }
  return { available: true, drifted };
}

/** `plan` with every replace target as `entries` read it: its aliases (in the read's order), its
 *  aliasless entry and its 7TV default name. Meant for a read {@link verifyReplaceTargets} has just
 *  passed, so the aliases only change in order — the second check right before the run
 *  (`recheckTransferPlan` in `import-flow.ts`) compares against exactly these, and the finished run
 *  protocol names an aliasless entry by the default name. Rows of any other action are returned
 *  unchanged. */
export function stampReplaceTargets(plan: TransferPlan, entries: SevenTvSetEntries): TransferPlan {
  return {
    rows: plan.rows.map((row) => {
      if (row.action !== 'replace') {
        return row;
      }
      const id = row.target.sevenTvEmoteId;
      return {
        ...row,
        target: {
          ...row.target,
          aliases: [...(entries.aliasesById.get(id) ?? row.target.aliases)],
          hasAliaslessEntry: entries.aliaslessIds.has(id),
          defaultName: entries.defaultNameById.get(id) ?? null,
        },
      };
    }),
  };
}

function replaceTargetDriftReason(
  entries: SevenTvSetEntries,
  row: ReplaceOrAdoptTransferRow,
): ReplaceTargetDriftReason | null {
  const targetId = row.target.sevenTvEmoteId;
  const liveAliases = entries.aliasesById.get(targetId);
  if (liveAliases === undefined) {
    return 'targetGone';
  }
  if (!liveAliases.includes(row.alias)) {
    const heldElsewhere = [...entries.aliasesById].some(
      ([id, aliases]) => id !== targetId && aliases.includes(row.alias),
    );
    return heldElsewhere ? 'nameHeldElsewhere' : 'aliasesChanged';
  }
  const confirmed = new Set(row.target.aliases);
  const live = new Set(liveAliases);
  if (confirmed.size !== live.size || [...confirmed].some((alias) => !live.has(alias))) {
    return 'aliasesChanged';
  }
  if (entries.aliaslessIds.has(targetId) !== row.target.hasAliaslessEntry) {
    return 'aliaslessEntryChanged';
  }
  return null;
}
