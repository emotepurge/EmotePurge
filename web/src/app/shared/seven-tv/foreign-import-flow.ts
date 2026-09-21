import { ImportRow, ImportSource, dedupeImportRows } from '../../core/seven-tv/import-source';
import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { LeaderboardImportResult } from '../../core/seven-tv/leaderboard.model';
import { ForeignChannelImportResult } from './foreign-channel-step';
import { ImportFlowDeps, startImportFlow } from './import-flow';

/**
 * The third import source, wired to the chain the other two already use: pick a foreign channel and
 * its emotes → the ordinary confirm/token/run flow.
 *
 * **There is no target picker in between (#147).** It used to open `ImportTargetDialog` with
 * `forcedScope: 'selection'`, which suppressed the scope radiogroup and left exactly one question:
 * into which channel. That question is already answered — the flow starts from the header of *this*
 * channel's usage-stats page, and the file path has always taken its target from that same page
 * context without asking. Both paths now behave the same way.
 *
 * Fremd ist die Quelle, nie das Ziel: the target is the page's own channel, so an untracked channel
 * can be read here but never written into.
 */
export function startForeignChannelImportFlow(
  deps: ImportFlowDeps,
  picked: ForeignChannelImportResult,
  targetChannelName: string,
): void {
  startImportFlow(deps, buildForeignImportSource(picked), {
    kind: 'activeSet',
    channelName: targetChannelName,
  });
}

/**
 * The picked rows as an `ImportSource`. The name taken over is the source set's **alias** (`name`),
 * not `defaultName`: the copy should keep the name the source channel knew the emote by, which is
 * also what the ADD mutation sends. It is the reason the target set's collision hint matters for
 * this source at all — an alias is far likelier to clash than a global base name (spec E7).
 *
 * `discardedRows` is `0` for the same reason it is for the tracked-channel grid: these rows came out
 * of our own endpoint already parsed into `ForeignEmoteRow`, so there is nothing left to reject.
 * `dedupeImportRows` still runs — the engine never deduplicates, and this is the one place that can
 * promise it for this source.
 */
export function buildForeignImportSource(picked: ForeignChannelImportResult): ImportSource {
  const deduped = dedupeImportRows(picked.rows.map(toImportRow));
  return {
    origin: { kind: 'seventv-channel', channelName: picked.channelName },
    rows: deduped.rows,
    duplicatesCollapsed: deduped.duplicatesCollapsed,
    discardedRows: 0,
  };
}

function toImportRow(row: ForeignEmoteRow): ImportRow {
  return { sevenTvEmoteId: row.sevenTvEmoteId, name: row.name };
}

/**
 * The fourth import source, on the same chain: pick emotes off one of 7TV's network-wide
 * leaderboards → the ordinary confirm/token/run flow. Written **beside**
 * {@link startForeignChannelImportFlow} rather than as a parameter of it: a leaderboard pick has no
 * channel identity at all — no `channelName`, no `sevenTvUserId`, no `emoteSetId` — so the two
 * "picked" payloads share only their rows, and folding them into one function would mean three
 * optional fields whose absence nothing checks (spec F4).
 *
 * The target is this page's own channel, exactly as it is for the file and foreign-channel paths;
 * there is no target picker in between (#147).
 */
export function startLeaderboardImportFlow(
  deps: ImportFlowDeps,
  picked: LeaderboardImportResult,
  targetChannelName: string,
): void {
  startImportFlow(deps, buildLeaderboardImportSource(picked), {
    kind: 'activeSet',
    channelName: targetChannelName,
  });
}

/**
 * The picked leaderboard rows as an `ImportSource`. The name taken over is `defaultName` — 7TV's
 * global base name — and here that is not a choice between two candidates the way it is for a
 * foreign channel's set: a leaderboard row is an `Emote`, not an `EmoteSetEmote` (spec F7), so it
 * has no per-set alias at all and `name` is only ever a copy of `defaultName`. Reading
 * `defaultName` says which of the two this source actually has.
 *
 * The origin carries the sort instead of a channel name: `sortBy` **is** the provenance of a
 * leaderboard pick (spec E2/E8), and it is what `sync-imported` records in the write-once audit
 * row. `discardedRows` is `0` for the same reason as the other 7TV-fed sources — these rows came
 * out of our own endpoint already parsed — while `dedupeImportRows` still runs, because the run
 * engine never deduplicates and this is the one place that can promise it for this source.
 */
export function buildLeaderboardImportSource(picked: LeaderboardImportResult): ImportSource {
  const deduped = dedupeImportRows(picked.rows.map(toLeaderboardImportRow));
  return {
    origin: { kind: 'seventv-leaderboard', sortBy: picked.sortBy },
    rows: deduped.rows,
    duplicatesCollapsed: deduped.duplicatesCollapsed,
    discardedRows: 0,
  };
}

function toLeaderboardImportRow(row: ForeignEmoteRow): ImportRow {
  return { sevenTvEmoteId: row.sevenTvEmoteId, name: row.defaultName };
}
