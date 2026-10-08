import { LeaderboardSort } from './leaderboard.model';

/**
 * One row copied by the import flow (#72, K3): the minimal identity a 7TV ADD mutation needs.
 * Shared shape across the origins — a tracked channel's active set (via the emote grid), a
 * downloaded file (emote-list export or usage export, parsed by `shared/export/import-source-parser`)
 * and a foreign channel's set read straight from 7TV (`shared/seven-tv/foreign-channel-step`).
 */
export interface ImportRow {
  sevenTvEmoteId: string;
  name: string;
  /** `null` for a file row whose file either predates the export writers carrying an image URL
   *  (#230) or whose `imageUrl` field failed to parse as a non-empty string
   *  (`import-source-parser.ts`) — an honest "don't know", never a guess derived from the id. Every
   *  live source (grid, foreign channel, leaderboard) and a file written since #230 carries its
   *  emote's own image URL through unchanged. */
  imageUrl: string | null;
}

/**
 * Where the rows came from. Kept next to the run (not just used to build it once) because the
 * import service's summary and its resync report both need to say "from channel X" / "from file Y"
 * after the run has started.
 *
 * `kind` is also the wire vocabulary of `POST .../emotes/sync-imported` (`SyncImportedBody`), and
 * the server keeps every value forever in a write-once audit row. Adding a member here therefore
 * means six places, not one (spec F1): the endpoint's accepted vocabulary, its name validation, its
 * kind-versus-name/sort agreement, `MarkImportedAsync`'s persistence, `AuditLogQueryService`'s
 * provenance projection — a word the last one does not know costs every row written with it its
 * origin, silently (spec F5) — and, on this side, every consumer of this union.
 *
 * `'channel'` and `'seventv-channel'` are deliberately two words rather than one: the first is a
 * channel EmotePurge tracks and reads out of its own database, the second is any Twitch login, read
 * live from 7TV by a user with no role in it. They behave the same from here on — which is why
 * `!== 'file'` and `=== 'channel'` are no longer interchangeable anywhere in this codebase (F6). Use
 * {@link importOriginSourceChannelName} rather than either.
 *
 * `'seventv-leaderboard'` (#148) has no source channel at all — a leaderboard row has no channel of
 * origin, only the sort it was picked off of (spec E2/E8). It carries that sort as `sortBy` instead
 * of a `channelName`, which is why `sourceChannelName` alone can no longer answer "what does this
 * origin send as its wire name" — use {@link importOriginSourceChannelName} for that and
 * {@link importOriginLeaderboardSort} for the sort, never `origin.kind === 'seventv-leaderboard'`
 * inline: the two helpers are what make a fifth member a compile error here instead of a silent gap.
 */
export type ImportOrigin =
  | { kind: 'channel'; channelName: string }
  | { kind: 'seventv-channel'; channelName: string }
  | {
      kind: 'file';
      fileName: string;
      exportedAt: string | null;
      channelName: string | null;
      envelopeKind: 'emote-list' | 'usage';
    }
  | { kind: 'seventv-leaderboard'; sortBy: LeaderboardSort };

/**
 * The source channel name that belongs on the wire for an origin, or `null` when the origin has
 * none. One of two places the union is taken apart for that question, and deliberately exhaustive: a
 * fifth member makes the `default` arm below a compile error instead of quietly reaching the server
 * as `null`.
 *
 * That failure is the reason this function exists. `sync-imported` rejects any kind that needs a
 * source name (`channel`, `seventv-channel`) without one with a 400 — and it runs *after* the 7TV
 * mutations, so a wrong `null` here means the emotes are already copied, the report fails, and the
 * provenance is gone for good (spec F6).
 *
 * A file origin sends `null` even when the file itself names a channel: the server rejects `file`
 * *with* a name as `invalid_source_kind` (R3, K2 contract). A leaderboard origin sends `null` too —
 * it has no source channel at all (E2/E8); its wire payload is {@link importOriginLeaderboardSort}
 * instead.
 */
export function importOriginSourceChannelName(origin: ImportOrigin): string | null {
  switch (origin.kind) {
    case 'channel':
    case 'seventv-channel':
      return origin.channelName;
    case 'file':
    case 'seventv-leaderboard':
      return null;
    default:
      return assertUnreachableOrigin(origin);
  }
}

/**
 * The leaderboard sort that belongs on the wire (`SyncImportedBody.leaderboardSort`) for an origin,
 * or `null` for the three origins that are not a leaderboard pick. The second exhaustive teardown of
 * the union (spec F1 Station 6) — a fifth member makes the `default` arm below a compile error
 * instead of quietly reaching the server as `null`.
 *
 * Same failure mode as {@link importOriginSourceChannelName}, mirrored: `sync-imported` rejects
 * `seventv-leaderboard` without a `leaderboardSort` with `invalid_leaderboard_sort`, *after* the 7TV
 * mutations (E8). `reportImported` must read both helpers, never `origin.kind === 'seventv-leaderboard'`
 * inline — a direct comparison is exactly the shortcut that left `leaderboardSort` unset here once
 * and lost the provenance of every leaderboard import (the #147 F6 class of bug, repeated for #148).
 */
export function importOriginLeaderboardSort(origin: ImportOrigin): LeaderboardSort | null {
  switch (origin.kind) {
    case 'seventv-leaderboard':
      return origin.sortBy;
    case 'channel':
    case 'seventv-channel':
    case 'file':
      return null;
    default:
      return assertUnreachableOrigin(origin);
  }
}

/** Reached only when a new {@link ImportOrigin} member skipped a `switch` above — the parameter type
 *  is what makes that a build error rather than a runtime surprise. */
function assertUnreachableOrigin(origin: never): never {
  throw new Error(`Unbekannte Import-Herkunft: ${JSON.stringify(origin)}`);
}

/**
 * The rows an import run is offered, already deduplicated by `sevenTvEmoteId` (see
 * `dedupeImportRows`). `duplicatesCollapsed` counts *valid* rows that were folded away by that
 * dedup; `discardedRows` counts rows the source rejected as invalid before dedup even ran — the
 * two never overlap. `discardedRows` is always `0` for both channel origins: `'channel'` rows come
 * out of the grid, which only ever holds rows that passed `EmoteListItem` validation server-side,
 * and `'seventv-channel'` rows come out of our own `/api/seventv/...` endpoint, which parses 7TV's
 * answer into `ForeignEmoteRow` before it ever reaches the browser. Only a parsed file can discard
 * rows, because only a file is written by something other than us.
 */
export interface ImportSource {
  origin: ImportOrigin;
  rows: ImportRow[];
  duplicatesCollapsed: number;
  discardedRows: number;
}

/**
 * The one deduplication both origins go through — first occurrence of a `sevenTvEmoteId` wins,
 * order of the surviving rows follows their first occurrence. Ordinal comparison on the id (plain
 * string equality): these are 7TV object ids, not display text, so there is nothing to localize.
 */
export function dedupeImportRows(rows: readonly ImportRow[]): {
  rows: ImportRow[];
  duplicatesCollapsed: number;
} {
  const seen = new Set<string>();
  const deduped: ImportRow[] = [];

  for (const row of rows) {
    if (seen.has(row.sevenTvEmoteId)) {
      continue;
    }
    seen.add(row.sevenTvEmoteId);
    deduped.push(row);
  }

  return { rows: deduped, duplicatesCollapsed: rows.length - deduped.length };
}
