import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';

/** One `toAdd` row 7TV would refuse to add under its source alias because the target already has
 *  *some other* alias under the same `sevenTvEmoteId` — spec 8.6: "N sind vorhanden, heißen dort
 *  aber anders". `targetAlias` is the target's own alias for that id (the first one found, when
 *  the target itself has the #74 duplicate-id defect — 7TV's own merge glitch, not this preview's
 *  choice of which duplicate to name). */
export interface AliasMismatch {
  sourceName: string;
  targetAlias: string;
}

/**
 * One `nameCollisionRow`: a source row `buildImportPreview` excludes from `toAdd` because its name
 * already belongs to a *different* target id, paired with everything a resolution step needs to act
 * on that target entry — `target` cannot be derived from `row` the way it can for
 * {@link AliasMismatchRow}, because the two ids genuinely differ here (that is what makes it a
 * collision, not a match).
 */
export interface NameCollisionRow {
  /** The source row this preview would refuse to submit as-is. */
  row: ImportRow;
  /** The target entry that currently holds `row.name` — what a REMOVE resolution would target. */
  target: EmoteListItem;
  /** Every *named* alias `target.sevenTvEmoteId` holds in the target set, `target.name` included —
   *  length 1 normally, 2 for a #74 duplicate id (7TV's own set-merge defect: the same id entered
   *  twice under two different aliases). A REMOVE resolution takes all of them at once, so a
   *  slot-delta computation must size off this length, never off `target` alone. */
  targetAliases: string[];
  /** Whether `target.sevenTvEmoteId` also carries at least one *aliasless* entry in the target set —
   *  see the long comment on {@link buildImportPreview}'s `nameCollisionRows` return value below for
   *  what this preview can and cannot tell about that, and why it is only exposed here. A REMOVE
   *  against an id with an aliasless sibling takes that entry too (K5, spec §37/§38's aliasless
   *  rule), so a slot delta must count it as a second removed entry, not zero. */
  targetHasAliaslessEntry: boolean;
}

/**
 * One `aliasMismatchRow`: a source row whose `sevenTvEmoteId` is already in the target set, just
 * under a different alias. Unlike {@link NameCollisionRow} this needs no separate target-entry
 * field — `row.sevenTvEmoteId` already *is* the target id, since matching by id (and failing to
 * match by alias) is exactly what put the row here.
 */
export interface AliasMismatchRow {
  row: ImportRow;
  /** Every *named* alias `row.sevenTvEmoteId` holds in the target set — length 1 normally, 2 for a
   *  #74 duplicate id. None of them equal `row.name` (that disagreement is why the row is here). */
  targetAliases: string[];
  /** Why an "adopt the source alias" resolution (an `UPDATE` renaming the target entry to
   *  `row.name`) cannot run, or `null` when it can (AK 8):
   *  - `'nameTaken'` — `row.name` already belongs to a *different* target id. Checked first: this
   *    is 7TV's ordinary alias-uniqueness rule, and applies to any target, defect-free or not.
   *  - `'duplicateTarget'` — `targetAliases.length > 1` (the #74 case): with two live entries
   *    under one id, adopting cannot say which of them to rename, so this resolution steers clear
   *    of #74 entirely rather than guess (#74 itself stays open).
   *  Both conditions are independent and untested in combination — order matters only for that
   *  untested overlap. */
  adoptBlocked: 'nameTaken' | 'duplicateTarget' | null;
}

export interface ImportPreview {
  /** Source rows the run would actually submit — every row that is genuinely new, minus a name
   *  collision (spec 8.6: those are counted in `nameCollisions` and never attempted at all, not
   *  merely warned about) and minus an alias mismatch (`aliasMismatches`, same reasoning). An
   *  `invalidNames` row *does* still reach here — see that field's own doc for why. */
  toAdd: ImportRow[];
  /** Count of source rows whose `sevenTvEmoteId` is already in the target set **under the same
   *  alias** — excluded from `toAdd`, and never double-counted with `aliasMismatches`: a #74
   *  duplicate id with two different target aliases counts here the moment *one* of them matches
   *  the source alias, never as a mismatch (spec 8.6, AK 37). */
  alreadyPresent: number;
  /** Deduplicated names `toAdd` would otherwise have carried that 7TV would reject outright — a
   *  different `sevenTvEmoteId` in the target already owns that exact (case-sensitive) name.
   *  Pulled *out* of `toAdd` since spec 2026-09-20 (revises the 2026-09-06 "informational only,
   *  7TV decides" reading, docs/DECISIONS.md): a run that still tried these would fail every one
   *  of them, and counting a doomed row as "added" is worse than not offering it at all.
   *
   *  Deduplicated *by name*, for `app-name-preview-list` — never the right field for a user-facing
   *  count: two distinct source ids can collide on the same target name, and both rows are still
   *  excluded from the run even though the name only appears here once. Use
   *  `nameCollisionRowCount` for any count. */
  nameCollisions: string[];
  /** Row-accurate count of source rows excluded from `toAdd` for a name collision — always
   *  `>= nameCollisions.length`, and strictly greater the moment two different source ids collide
   *  on the same target name. This is what feeds the "N emotes… will not be copied" count and the
   *  toAdd/alreadyPresent/nameCollisionRowCount/aliasMismatches sum-to-source-rows invariant;
   *  `nameCollisions` itself stays deduplicated, purely for the name list under it. */
  nameCollisionRowCount: number;
  /**
   * One {@link NameCollisionRow} per row counted in `nameCollisionRowCount` (same length, same
   * order) — the row-accurate list `nameCollisions`/`nameCollisionRowCount` cannot themselves be,
   * since they only ever carried names. A resolution step reads this, not the two counting fields,
   * to act on a specific target entry.
   *
   * **Aliasless entries (K5):** an id can carry a target entry with no
   * alias at all — `aliaslessIds` in `seven-tv-set-entries.ts` exists precisely because 7TV's `v4`
   * GQL `emoteSet.emotes` query (`items { alias emote { id } }`) can answer `alias: null` for one,
   * confirmed live (`seven-tv-set-entries.ts:112-120`, DECISIONS.md K5 entries 2026-09-22) —
   * contradicting an earlier, narrower reading (DECISIONS.md 2026-08-04) that 7TV's `ADD` mutation
   * always backfills a missing alias with `default_name`, which only ever covered that one write
   * path, not the live states the set can be found in when read. **This preview's target lists come
   * from two genuinely different reads (`ImportTargetSelection`, `import-target-loader.ts:47-58`),
   * not from "tracked" versus "untracked" as such** — a tracked channel's *own* non-active set reads
   * exactly like an untracked one:
   *
   * - **The Postgres-backed path — `trackedActive` only** (`EmoteAdminService.listEmotes` → our own
   *   `Emote` table, `import-target-loader.ts:135-136`). Postgres enforces `HasIndex(e => new {
   *   e.ChannelId, e.SevenTvEmoteId }).IsUnique()` (`AppDbContext.cs:31`), so this path can never
   *   yield two rows sharing one `sevenTvEmoteId` for a channel — **a #74 duplicate is structurally
   *   impossible here**, whatever 7TV itself holds: `targetAliases` has at most one entry and
   *   `targetHasAliaslessEntry` is `false` on this path, unless that one entry's own name is falsy.
   *   That remaining, *aliasless* half is not protected by the index (a single row can still carry a
   *   falsy name) and was not traced with the same confidence as the live path below: `Emote.Name`
   *   is filled by the worker's REST-compat sync (`SevenTvSyncService.cs:595`, `emote.Name =
   *   live.Name`), a *different* 7TV endpoint (`SevenTvEmoteJsonDto.Name`, `SevenTvApiDtos.cs:173`,
   *   "the v4 compat layer") whose behavior for an aliasless set entry is not verified live —
   *   and `Emote.Name` is a `NOT NULL` Postgres column (`Emote.cs`), so if that endpoint ever
   *   did hand the sync a genuine `null` it would fail loudly (a migration/insert error), not
   *   silently, which is itself weak evidence that it does not (untested, not proven).
   * - **The live-GQL path — `trackedSet` and `untrackedSet` alike**, both routed through the same
   *   `fetchLiveTarget` (`import-target-loader.ts:166-206`) regardless of whether the channel is
   *   tracked; only the separate warning check branches on that. It runs the identical GQL query
   *   shape (`GqlEmoteSetPreviewQuery`, `SevenTvApiClient.cs:70-71`, requesting `alias` on
   *   `emoteSet.emotes.items`) that `aliaslessIds` reads, and nothing between there and this preview
   *   ever substitutes `DefaultName` for a missing `Alias`: `ForeignEmoteSetService.cs:141` maps
   *   `item.Alias` straight into `ForeignEmoteRow.Name` (`IForeignEmoteSetService.cs:257-263`, both
   *   declared plain non-nullable `string`), the wire DTO does the same
   *   (`foreign-emote-set.model.ts:18`, `ForeignEmoteRow.name: string`), and
   *   `import-target-loader.ts:197` copies `row.name` into `EmoteListItem.name` unchanged. Nothing
   *   on this path performs the nullable-annotation check `System.Text.Json` needs to *reject* an
   *   explicit JSON `null` for a non-nullable reference-typed property (no
   *   `RespectNullableAnnotations`, no `[JsonRequired]`) — a `"alias": null` in 7TV's response is
   *   free to overwrite `SevenTvGqlEmoteSetPreviewItemDto.Alias`'s `= string.Empty` default
   *   (`SevenTvApiDtos.cs:340`) with a runtime `null`, and every "non-nullable `string`" downstream
   *   of it inherits that lie unexamined. Either way — a JSON `null` surviving as a JS `null`, or an
   *   empty-string default surviving untouched — `EmoteListItem.name` lands here JS-*falsy*
   *   (`''`/`null`, TypeScript's declared `string` notwithstanding) for an aliasless entry on this
   *   path, and this path *can* carry a #74 duplicate (no unique index applies to a live 7TV read).
   *   Nothing else produces a falsy `name` (7TV's alias validator rejects an empty string, and
   *   `DefaultName` is never substituted in), so a falsy `EmoteListItem.name` is read below as
   *   exactly that signal, mirroring `seven-tv-set-entries.ts`'s own `if (item.alias) … else
   *   aliaslessIds.add(...)` falsy check on the same field of the same query.
   *
   * The same falsy-name heuristic is applied uniformly to both paths below regardless — cheap, and
   * harmless even where it never fires. **This preview's `targetHasAliaslessEntry` is therefore a
   * best-effort reading from whatever this preview's target list already holds, not a verified
   * fact:** the verified view belongs to the live read before a transfer run (`loadSevenTvSetEntries`'s
   * `aliaslessIds`), and `removedEntryCount` (AK 21) is built from *that* read, not this preview — a
   * disagreement between the two is a live-data drift (spec section 2, point 3), not a bug here.
   */
  nameCollisionRows: NameCollisionRow[];
  /** Rows whose `sevenTvEmoteId` already exists in the target, but under a different alias —
   *  pulled out of `toAdd` for the same reason as `nameCollisions` (spec 8.6): 7TV already has
   *  this emote in the set, an ADD under a second alias is not what "already present" should mean
   *  here, and this preview never silently renames anything. */
  aliasMismatches: AliasMismatch[];
  /** One {@link AliasMismatchRow} per entry in `aliasMismatches` (same length, same order) — the
   *  resolvable counterpart to it, adding `targetAliases` and `adoptBlocked` for a resolution step. */
  aliasMismatchRows: AliasMismatchRow[];
  /** Names from `toAdd` that 7TV will reject outright, regardless of the target set's contents —
   *  see `isNameRejectedBySevenTv` for what that covers and why it is deliberately narrow.
   *  Informational only, unlike `nameCollisions`/`aliasMismatches`: the row still reaches `toAdd`
   *  because nothing about the *target's contents* is what dooms it, so removing it here would
   *  not even be correct once the alias is fixed upstream — the row stays, 7TV decides. */
  invalidNames: string[];
  /** Every *non-falsy* name across every target entry, regardless of whether any source row
   *  touches it — the full universe a resolution step's "no generated alias may equal a name the
   *  target already holds" rule (`conflict-resolution.ts`'s rule 2) checks against. Narrower
   *  fields like `NameCollisionRow.targetAliases`/`AliasMismatchRow.targetAliases` only cover the
   *  target entries a source row actually collides or mismatches with; this field is the
   *  unfiltered set behind the same name comparison this function already uses for its own
   *  `toAdd`/`nameCollisionRows` split above (`targetNames.has(row.name)`), not a second one. */
  targetNames: ReadonlySet<string>;
}

/** The live counterpart of a replace target, as a later read of the target set found it — `null`
 *  when the target id is gone from the set. Same shape as `ReplaceTargetDrift.live`. */
export type TargetOverlay = { aliases: string[]; hasAliaslessEntry: boolean } | null;

/**
 * Projects an `ImportSource` onto a target set's current emotes, splitting it into what the run
 * would add, what it would skip outright (already present, under the same or a different alias, or
 * blocked by a name collision), and what it would add but 7TV will likely still reject for its
 * alias alone (`invalidNames`).
 *
 * Every source row lands in exactly one of `toAdd`, `alreadyPresent`, `nameCollisionRowCount` or
 * `aliasMismatches`, so their sizes sum to the row count —
 * `toAdd.length + alreadyPresent + nameCollisionRowCount + aliasMismatches.length ===
 * source.rows.length`. `invalidNames` is not a fifth group here: it names a subset already
 * counted inside `toAdd`. Every group counts by row, never by deduplicated name — see
 * `nameCollisionRowCount`'s own doc for why that distinction matters.
 *
 * `nameCollisionRows`/`aliasMismatchRows` are the row-accurate, resolvable counterparts to
 * `nameCollisions`/`aliasMismatches` — `nameCollisionRows.length === nameCollisionRowCount` and
 * `aliasMismatchRows.length === aliasMismatches.length` always hold, same order as the fields they
 * pair with. A resolution step reads these, never the counting-only fields above, to act on a
 * specific target entry.
 *
 * The identity comparison is ordinal (`sevenTvEmoteId`, exact string equality — these are 7TV
 * object ids, not display text); every name comparison is exact (`===`, case-sensitive) string
 * equality, matching how 7TV itself treats emote names/aliases.
 *
 * `targetEmotes` is grouped by id first (not deduplicated) precisely because the target set can
 * itself carry a #74 duplicate — two rows sharing one `sevenTvEmoteId` under different aliases,
 * 7TV's own set-merge defect. A source row matching that id is `alreadyPresent` the moment *any*
 * of the target's aliases for it agrees with the source's; only when *none* of them do is it an
 * alias mismatch (spec 8.6, AK 37 — the #74 case).
 */
export function buildImportPreview(
  source: ImportSource,
  targetEmotes: EmoteListItem[],
): ImportPreview {
  const targetById = new Map<string, EmoteListItem[]>();
  for (const emote of targetEmotes) {
    const group = targetById.get(emote.sevenTvEmoteId);
    if (group) {
      group.push(emote);
    } else {
      targetById.set(emote.sevenTvEmoteId, [emote]);
    }
  }
  // Every *non-falsy* target name, across every entry regardless of whether any source row
  // touches it — the authoritative "does the target hold this name" check, both for this
  // function's own name-collision split below and for `ImportPreview.targetNames`'s own doc.
  // Filtered the same way `nameCollisionRows`' aliasless signal is (a falsy name is that signal,
  // never something a real source row's name could equal — see that field's own doc).
  const targetNames = new Set<string>();
  for (const emote of targetEmotes) {
    if (emote.name) {
      targetNames.add(emote.name);
    }
  }
  // First-wins by name, and only ever populated from a truthy name: two ids sharing one alias
  // cannot coexist in a real target set (7TV's own alias-uniqueness check, DECISIONS.md
  // 2026-08-04), and a falsy name here is the aliasless signal `nameCollisionRows`' own doc traces
  // through this preview's two read paths — never something a real source row's name could equal.
  const targetByName = new Map<string, EmoteListItem>();
  for (const emote of targetEmotes) {
    if (emote.name && !targetByName.has(emote.name)) {
      targetByName.set(emote.name, emote);
    }
  }

  const toAdd: ImportRow[] = [];
  const nameCollisions = new Set<string>();
  let nameCollisionRowCount = 0;
  const nameCollisionRows: NameCollisionRow[] = [];
  const aliasMismatches: AliasMismatch[] = [];
  const aliasMismatchRows: AliasMismatchRow[] = [];
  const invalidNames = new Set<string>();
  let alreadyPresent = 0;

  for (const row of source.rows) {
    const targetGroup = targetById.get(row.sevenTvEmoteId);
    if (targetGroup) {
      if (targetGroup.some((target) => target.name === row.name)) {
        alreadyPresent++;
      } else {
        aliasMismatches.push({ sourceName: row.name, targetAlias: targetGroup[0].name });
        aliasMismatchRows.push({
          row,
          targetAliases: namedAliasesOf(targetGroup),
          adoptBlocked: adoptBlockedFor(row.name, row.sevenTvEmoteId, targetGroup, targetByName),
        });
      }
      continue;
    }
    if (targetNames.has(row.name)) {
      // Deduplicated set for display, but every row still leaves the run — two different source
      // ids colliding on the same target name must both count, not just the one name.
      nameCollisions.add(row.name);
      nameCollisionRowCount++;
      // targetByName is built from the same targetEmotes this row's name was just found to be in
      // (targetNames), so a lookup here can never miss.
      const target = targetByName.get(row.name)!;
      const group = targetById.get(target.sevenTvEmoteId)!;
      nameCollisionRows.push({
        row,
        target,
        targetAliases: namedAliasesOf(group),
        targetHasAliaslessEntry: group.some((entry) => !entry.name),
      });
      continue;
    }
    toAdd.push(row);
    if (isNameRejectedBySevenTv(row.name)) {
      invalidNames.add(row.name);
    }
  }

  return {
    toAdd,
    alreadyPresent,
    nameCollisions: [...nameCollisions],
    nameCollisionRowCount,
    nameCollisionRows,
    aliasMismatches,
    aliasMismatchRows,
    invalidNames: [...invalidNames],
    targetNames,
  };
}

/** Every *named* alias in `group`, deduplicated, `target.name` included — the shared helper behind
 *  both `NameCollisionRow.targetAliases` and `AliasMismatchRow.targetAliases`. Mirrors
 *  `seven-tv-set-entries.ts`'s `aliasesById` construction: a falsy `name` (the aliasless signal, see
 *  `nameCollisionRows`'s doc) is excluded here rather than kept as a garbage entry, and a genuine
 *  7TV #74 duplicate can otherwise repeat the same alias string for one id, so dedup and not just
 *  `.map` is what keeps the length honest. */
function namedAliasesOf(group: readonly EmoteListItem[]): string[] {
  const aliases: string[] = [];
  for (const entry of group) {
    if (entry.name && !aliases.includes(entry.name)) {
      aliases.push(entry.name);
    }
  }
  return aliases;
}

/** `AliasMismatchRow.adoptBlocked` (AK 8) — see that field's own doc for what each value means and
 *  why the two checks run in this order. */
function adoptBlockedFor(
  sourceName: string,
  sourceId: string,
  targetGroup: readonly EmoteListItem[],
  targetByName: ReadonlyMap<string, EmoteListItem>,
): 'nameTaken' | 'duplicateTarget' | null {
  const nameOwner = targetByName.get(sourceName);
  if (nameOwner && nameOwner.sevenTvEmoteId !== sourceId) {
    return 'nameTaken';
  }
  if (namedAliasesOf(targetGroup).length > 1) {
    return 'duplicateTarget';
  }
  return null;
}

/**
 * True for an alias 7TV is known to reject when adding an emote to the target set. This tests
 * `row.name` in its role as the `alias` an `addEmote` mutation sends (#149) — not 7TV's
 * separate, stricter validator for the canonical emote name, which this preview does not touch.
 *
 * This used to claim non-ASCII aliases were doomed ("the alias is not affected — 7TV does allow
 * umlauts there" was the previous wording here, and it was wrong: the two live rejections it cited,
 * `Hänno` and `HörMalZuBrudi`, were themselves alias rejections, not name rejections). What was
 * actually true is narrower: 7TV `v3`'s alias validator rejects every non-ASCII codepoint outright,
 * because 7TV never backported the Unicode-aware alias validator it shipped for `v4`
 * (`SevenTV/SevenTV#228`, merged 2025-12-01) onto `v3`. Since #149 we write against `v4`, so that
 * rejection no longer applies — but `v4` has its own, different validator, and *that* one is what
 * this check now has to reflect.
 *
 * The `v4` rule below is evidenced from two independent directions that agree on all 25 data
 * points measured live against `7tv.io` on 2026-09-10 (docs/plans/Plan-149-7TV-v4-Schreibflaeche.md,
 * section 0): 7TV's own `EmoteAliasValidator` regex, read from `SevenTV/SevenTV` at
 * `apps/api/src/http/validators.rs` —
 *
 *     ^[\w\-():!+|.'?><&\p{Emoji_Presentation}*$#]{1,100}$
 *
 * (Rust's `\w` is Unicode-aware, which is why letters of any script pass) — and a live probe of
 * that same field with 25 aliases. Both agree on what gets rejected: whitespace anywhere in the
 * alias (space, tab, newline), a zero-width space, the characters `/ \ " , ; @ % = [ ] { } ~ ^`,
 * an alias over 100 characters, and the empty string. Both agree on what gets accepted: letters of
 * any script, `ß`, emoji, and a 100-character alias.
 *
 * This check still only tests for what was actually observed rejected — a blocklist, not the
 * regex's allow-list transcribed into code. Guessing at 7TV's allowed character set has gone wrong
 * twice already in this project (#33, #37 — code and test mock shared the same wrong assumption,
 * so the tests stayed green while the behavior was broken), and the one time 7TV changed this rule
 * it only ever *loosened* it (`v3` to `v4`). A blocklist errs safe under that trend: an alias 7TV
 * newly allows that this check does not yet know about still gets flagged (an unnecessary but
 * harmless warning — this is informational only, `toAdd` is never filtered by it), where an
 * allow-list built from the regex above would instead silently wave it through on the strength of
 * a regex reading, not a probe. If a further rejection reason is observed, extend this blocklist;
 * do not turn it into a guessed-at, or even regex-transcribed, allow-list.
 */
export function isNameRejectedBySevenTv(name: string): boolean {
  const codepoints = [...name];
  if (codepoints.length === 0 || codepoints.length > 100) {
    return true;
  }
  return codepoints.some((char) => SEVEN_TV_REJECTED_ALIAS_CHARS.has(char));
}

/** Every character 7TV's `v4` alias validator was observed to reject — see
 *  `isNameRejectedBySevenTv` for the evidence and why this is a blocklist, not an allow-list. */
const SEVEN_TV_REJECTED_ALIAS_CHARS = new Set([
  ' ',
  '\t',
  '\n',
  '\u200b', // zero-width space
  '/',
  '\\',
  '"',
  ',',
  ';',
  '@',
  '%',
  '=',
  '[',
  ']',
  '{',
  '}',
  '~',
  '^',
]);

/** `preview` with each name-collision row keyed in `overlays` (by its source id) pointing at the
 *  live counterpart of its target instead of the one the preview was built from — a gone target
 *  keeps no alias and no aliasless entry. Every other field is left as it is: the overlay only
 *  changes what a replace of that row would remove. */
export function overlayPreview(
  preview: ImportPreview,
  overlays: ReadonlyMap<string, TargetOverlay>,
): ImportPreview {
  if (overlays.size === 0) {
    return preview;
  }
  return {
    ...preview,
    nameCollisionRows: preview.nameCollisionRows.map((collision) => {
      const key = collision.row.sevenTvEmoteId;
      if (!overlays.has(key)) {
        return collision;
      }
      const live = overlays.get(key) ?? null;
      return {
        ...collision,
        targetAliases: live === null ? [] : [...live.aliases],
        targetHasAliaslessEntry: live?.hasAliaslessEntry ?? false,
      };
    }),
  };
}
