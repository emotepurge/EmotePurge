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
 */
export function filterAlreadyPresentForRestore<T extends RestoreFilterRow>(
  httpClient: HttpClient,
  targetSetId: string,
  rows: readonly T[],
): Observable<RestoreAlreadyPresentFilterResult<T>> {
  return loadSevenTvSetEntries(httpClient, targetSetId).pipe(
    map(({ aliasesById, aliaslessIds }) => {
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
      return { rows: kept, skipped, skippedNameTaken, available: true };
    }),
    catchError(() => of({ rows: [...rows], skipped: 0, skippedNameTaken: 0, available: false })),
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
