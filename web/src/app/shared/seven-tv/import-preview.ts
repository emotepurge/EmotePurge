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
  /** Rows whose `sevenTvEmoteId` already exists in the target, but under a different alias —
   *  pulled out of `toAdd` for the same reason as `nameCollisions` (spec 8.6): 7TV already has
   *  this emote in the set, an ADD under a second alias is not what "already present" should mean
   *  here, and this preview never silently renames anything. */
  aliasMismatches: AliasMismatch[];
  /** Names from `toAdd` that 7TV will reject outright, regardless of the target set's contents —
   *  see `isNameRejectedBySevenTv` for what that covers and why it is deliberately narrow.
   *  Informational only, unlike `nameCollisions`/`aliasMismatches`: the row still reaches `toAdd`
   *  because nothing about the *target's contents* is what dooms it, so removing it here would
   *  not even be correct once the alias is fixed upstream — the row stays, 7TV decides. */
  invalidNames: string[];
}

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
 * The identity comparison is ordinal (`sevenTvEmoteId`, exact string equality — these are 7TV
 * object ids, not display text); every name comparison is exact (`===`, case-sensitive) string
 * equality, matching how 7TV itself treats emote names/aliases.
 *
 * `targetEmotes` is grouped by id first (not deduplicated) precisely because the target set can
 * itself carry a #74 duplicate — two rows sharing one `sevenTvEmoteId` under different aliases,
 * 7TV's own set-merge defect. A source row matching that id is `alreadyPresent` the moment *any*
 * of the target's aliases for it agrees with the source's; only when *none* of them do is it an
 * alias mismatch (spec 8.6, AK 37 — the #74 grenzfall).
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
  const targetNames = new Set(targetEmotes.map((emote) => emote.name));

  const toAdd: ImportRow[] = [];
  const nameCollisions = new Set<string>();
  let nameCollisionRowCount = 0;
  const aliasMismatches: AliasMismatch[] = [];
  const invalidNames = new Set<string>();
  let alreadyPresent = 0;

  for (const row of source.rows) {
    const targetGroup = targetById.get(row.sevenTvEmoteId);
    if (targetGroup) {
      if (targetGroup.some((target) => target.name === row.name)) {
        alreadyPresent++;
      } else {
        aliasMismatches.push({ sourceName: row.name, targetAlias: targetGroup[0].name });
      }
      continue;
    }
    if (targetNames.has(row.name)) {
      // Deduplicated set for display, but every row still leaves the run — two different source
      // ids colliding on the same target name must both count, not just the one name.
      nameCollisions.add(row.name);
      nameCollisionRowCount++;
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
    aliasMismatches,
    invalidNames: [...invalidNames],
  };
}

/**
 * True for an alias 7TV is known to reject when adding an emote to the target set. This tests
 * `row.name` in its role as the `alias` an `addEmote` mutation sends (#149/T2) — not 7TV's
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
 * section 0/T0): 7TV's own `EmoteAliasValidator` regex, read from `SevenTV/SevenTV` at
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
function isNameRejectedBySevenTv(name: string): boolean {
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
