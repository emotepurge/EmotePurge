import { ForeignEmoteRow } from '../seven-tv/foreign-emote-set.model';
import { EmoteUsageTotal, EmoteUsageTotalDto } from './usage-stat.model';

/**
 * Unions a set's counted `/totals` rows with its live 7TV membership into the row model the usage
 * page renders (spec #200, section 7.1, E16).
 *
 * The union happens here, in the frontend, rather than on the Api: a silent reload (`usage.flushed`,
 * every 30 s) must never trigger a 7TV request (E16, E19) — building the union server-side would tie
 * every `/totals` call to a live lookup regardless of which reload triggered it.
 *
 * Two modes, selected by `isActiveSet` rather than inferred from `live`, because the active set's
 * `/totals` already reflects live membership by construction (it excludes archived rows outright)
 * and the page never fetches a live list for it at all:
 *
 * - **Active set** (`isActiveSet: true`): `live` is expected to be `null` and is ignored. Every
 *   `totals` row becomes a `'live'` row with `slotCount: 1`, `aliases: [emoteName]` — a straight,
 *   lossless mapping, so nothing here can make an active-set row nullable in practice even though
 *   the row type now allows it (that is only ever exercised by the non-active branch).
 * - **Non-active set** (`isActiveSet: false`): `live` is grouped by `sevenTvEmoteId` (a duplicate
 *   7TV set entry — #74 — groups to one cell with `slotCount: 2` and both aliases, E20). A group
 *   with a matching `totals` row is `'live'` and carries that row's numbers, with `emoteName`/
 *   `imageUrl` taken from the live entry (the fresher, current-membership view) rather than the
 *   possibly-stale DB row. A group without a matching row is `'live'` too, but Klasse 3
 *   (`emoteId: null, totalUseCount: null`, E17) — a member 7TV knows about that never earned a
 *   `UsageStat` under this set. A `totals` row with no matching live group is `'left'` (E23): it has
 *   counts, but is no longer a member of the set.
 *
 * Output order is a stable function of the input: `totals` rows keep their given order (matched or
 * `'left'`), and live-only groups (Klasse 3) are appended afterwards in their first-encountered
 * order in `live`. Callers that need a different order (e.g. the `null`-group-last rule of AK 56)
 * re-sort the result — this function makes no assumption about presentation.
 */
export function mergeSetView(
  totals: readonly EmoteUsageTotalDto[],
  live: readonly ForeignEmoteRow[] | null,
  isActiveSet: boolean,
): EmoteUsageTotal[] {
  if (isActiveSet || live === null) {
    return totals.map((dto) => ({
      emoteId: dto.emoteId,
      emoteName: dto.emoteName,
      sevenTvEmoteId: dto.sevenTvEmoteId,
      imageUrl: dto.imageUrl,
      totalUseCount: dto.totalUseCount,
      lastUsedDate: dto.lastUsedDate,
      previousWindowUseCount: dto.previousWindowUseCount,
      firstSeenAt: dto.firstSeenAt,
      membership: 'live',
      slotCount: 1,
      aliases: [dto.emoteName],
      nameTwinEmoteSetIds: dto.nameTwinEmoteSetIds,
    }));
  }

  // Preserves first-encounter order for the Klasse-3 (live-only) append pass below.
  const liveGroups = new Map<string, ForeignEmoteRow[]>();
  for (const entry of live) {
    const group = liveGroups.get(entry.sevenTvEmoteId);
    if (group) {
      group.push(entry);
    } else {
      liveGroups.set(entry.sevenTvEmoteId, [entry]);
    }
  }

  const consumedIds = new Set<string>();
  const rows: EmoteUsageTotal[] = totals.map((dto) => {
    const group = liveGroups.get(dto.sevenTvEmoteId);
    if (!group) {
      // A counted row that fell out of the live set: 'left', unchanged slot/alias shape.
      return {
        emoteId: dto.emoteId,
        emoteName: dto.emoteName,
        sevenTvEmoteId: dto.sevenTvEmoteId,
        imageUrl: dto.imageUrl,
        totalUseCount: dto.totalUseCount,
        lastUsedDate: dto.lastUsedDate,
        previousWindowUseCount: dto.previousWindowUseCount,
        firstSeenAt: dto.firstSeenAt,
        membership: 'left',
        slotCount: 1,
        aliases: [dto.emoteName],
        nameTwinEmoteSetIds: dto.nameTwinEmoteSetIds,
      };
    }

    consumedIds.add(dto.sevenTvEmoteId);
    return {
      emoteId: dto.emoteId,
      emoteName: group[0].name,
      sevenTvEmoteId: dto.sevenTvEmoteId,
      imageUrl: group[0].imageUrl,
      totalUseCount: dto.totalUseCount,
      lastUsedDate: dto.lastUsedDate,
      previousWindowUseCount: dto.previousWindowUseCount,
      firstSeenAt: dto.firstSeenAt,
      membership: 'live',
      slotCount: group.length,
      aliases: group.map((entry) => entry.name),
      nameTwinEmoteSetIds: dto.nameTwinEmoteSetIds,
    };
  });

  for (const [sevenTvEmoteId, group] of liveGroups) {
    if (consumedIds.has(sevenTvEmoteId)) {
      continue;
    }
    // Klasse 3: a live member with no counted row under this set at all (E17).
    rows.push({
      emoteId: null,
      emoteName: group[0].name,
      sevenTvEmoteId,
      imageUrl: group[0].imageUrl,
      totalUseCount: null,
      lastUsedDate: null,
      previousWindowUseCount: null,
      firstSeenAt: null,
      membership: 'live',
      slotCount: group.length,
      aliases: group.map((entry) => entry.name),
      nameTwinEmoteSetIds: [],
    });
  }

  return rows;
}
