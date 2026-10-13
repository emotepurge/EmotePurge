import { ForeignEmoteRow } from '../seven-tv/foreign-emote-set.model';
import { EmoteUsageTotal, EmoteUsageTotalDto } from './usage-stat.model';

/**
 * Unions a set's counted `/totals` rows with its live 7TV membership into the row model the usage
 * page renders — one row per card (spec #200, section 7.1, E16; emote-slots spec D22 and 9.1 as
 * amended by plan 377, 2.3 and 2.4).
 *
 * The union happens here, in the frontend, rather than on the Api: a silent reload (`usage.flushed`,
 * every 30 s) must never trigger a 7TV request (E16, E19) — building the union server-side would tie
 * every `/totals` call to a live lookup regardless of which reload triggered it.
 *
 * **Mode of the response.** The page does not know the slot reconcile flag, but the response tells:
 * with the flag off no `/totals` row carries a `slotId`. A response with at least one slot-mode row
 * is a *slot response*; anything else is an *emote response* and is merged exactly as before slots
 * (plan 377, F2). The view (`isActiveSet`) and the response mode are independent.
 *
 * **Active set** (`isActiveSet: true`, or `live === null`): `live` is ignored. Every `totals` row
 * becomes a `'live'` row with `slotCount: 1`, `aliases: [emoteName]` and the DTO's `siblingNames` —
 * a straight, lossless mapping (the active set's `/totals` already reflects live membership, E16).
 *
 * **Non-active set**: every live entry is consumed at most once, in this order.
 *
 * 1. An *emote-mode row* (`slotId: null`) takes every live entry of its 7TV id, as before slots: a
 *    #74 duplicate becomes one cell with `slotCount: 2` and both aliases (E20), name and image from
 *    the first entry in live order. No entry → `'left'` (E23).
 * 2. A *dated slot row* (authoritative `firstSeenAt`) joins by instant, mirroring the backend's
 *    `EmoteSlotReconcilePlanner.BindByDate`: instants are compared in milliseconds via `Date.parse`,
 *    never as strings (the two wire paths print different numbers of fractional digits). For each
 *    (7TV id, instant) the dated slot rows and the dated live entries are grouped over the complete
 *    inputs first, so the order of the DTOs cannot change the outcome. Exactly one row against
 *    exactly one entry binds regardless of the name — a rename keeps its card — but only when the Api
 *    vouches that no other own slot of the emote shares the instant (`firstSeenAtShared: false`,
 *    plan 377 F6: the non-active view hides a twin without rows under the set). Every other group
 *    binds same-named pairs only.
 * 3. A *remaining slot row* binds a still free entry of its id with its name: a provisional row any
 *    such entry, a dated row only an undated one (a preview without a date must not make a counted
 *    card vanish).
 * 4. A bound slot row is `'live'` with the DTO's numbers and the live entry's name and image; an
 *    unbound one is `'left'` with the DTO's values.
 * 5. The rest of the live entries are Klasse 3 (`emoteId: null`, null payload, E17): one row per
 *    entry in a slot response (D22), one row per 7TV id in an emote response (F2).
 * 6. `siblingNames` of a bound slot row or a per-entry Klasse-3 row are the names of the id's other
 *    live entries in D2 order; a `'left'` slot row keeps the DTO's, everything else has none.
 *
 * Keys (`slotKey`) follow plan 377, 2.3: `slotId` for a slot row, the 7TV id for an emote-mode row
 * and a grouped Klasse-3 row (unchanged from before slots), and for a per-entry Klasse-3 row a form
 * fixed over the complete live list, so it never depends on which entries happen to be left over.
 *
 * Output order is a stable function of the input: `totals` rows keep their given order (bound or
 * `'left'`), and Klasse-3 rows follow in the order their entry (or id) first appears in `live`.
 * Callers that need a different order (e.g. the `null`-group-last rule of AK 56) re-sort the result.
 */
export function mergeSetView(
  totals: readonly EmoteUsageTotalDto[],
  live: readonly ForeignEmoteRow[] | null,
  isActiveSet: boolean,
): EmoteUsageTotal[] {
  const wire = totals.map(readWire);

  if (isActiveSet || live === null) {
    return wire.map((row) => ({
      ...counted(row),
      membership: 'live',
      slotCount: 1,
      aliases: [row.dto.emoteName],
      siblingNames: [...row.siblingNames],
    }));
  }

  const slotView = wire.some((row) => row.slotId !== null);
  const instants = live.map((entry) => instantOf(entry.addedAt));
  const entriesById = groupIndexes(live, (entry) => entry.sevenTvEmoteId);
  const consumed = new Array<boolean>(live.length).fill(false);
  // Totals index → the live entry indexes it binds (several only for an emote-mode row).
  const bindings = new Map<number, number[]>();
  const bind = (rowIndex: number, entryIndex: number) => {
    bindings.set(rowIndex, [entryIndex]);
    consumed[entryIndex] = true;
  };

  // Step 1: emote-mode rows take every entry of their id.
  wire.forEach((row, rowIndex) => {
    if (row.slotId !== null) {
      return;
    }
    const group = entriesById.get(row.dto.sevenTvEmoteId) ?? [];
    if (group.length > 0) {
      bindings.set(rowIndex, group);
      group.forEach((entryIndex) => (consumed[entryIndex] = true));
    }
  });

  // Step 2: dated slot rows by instant, groups formed over the complete inputs before any binding.
  const datedRowGroups = new Map<string, number[]>();
  wire.forEach((row, rowIndex) => {
    if (row.slotId !== null && row.instant !== null) {
      pushTo(datedRowGroups, instantKey(row.dto.sevenTvEmoteId, row.instant), rowIndex);
    }
  });
  const datedEntryGroups = new Map<string, number[]>();
  live.forEach((entry, entryIndex) => {
    const instant = instants[entryIndex];
    if (instant !== null && !consumed[entryIndex]) {
      pushTo(datedEntryGroups, instantKey(entry.sevenTvEmoteId, instant), entryIndex);
    }
  });
  for (const [key, rowIndexes] of datedRowGroups) {
    const entryIndexes = datedEntryGroups.get(key) ?? [];
    if (rowIndexes.length === 1 && entryIndexes.length === 1 && !wire[rowIndexes[0]].shared) {
      bind(rowIndexes[0], entryIndexes[0]);
      continue;
    }
    for (const rowIndex of rowIndexes) {
      const match = entryIndexes.find(
        (entryIndex) =>
          !consumed[entryIndex] && live[entryIndex].name === wire[rowIndex].dto.emoteName,
      );
      if (match !== undefined) {
        bind(rowIndex, match);
      }
    }
  }

  // Step 3: the remaining slot rows by name — provisional rows any entry, dated rows undated ones.
  // Rows compete for an entry in the backend's `BindByName` order (provisional first, then dated
  // before undated, then by date, then `slotId` ordinal), so the order of `totals` (which is by
  // usage) cannot decide which of two same-named slots keeps the entry. Archived rows are skipped,
  // as the backend only binds active slots.
  const remaining: number[] = [];
  wire.forEach((row, rowIndex) => {
    if (row.slotId !== null && !bindings.has(rowIndex) && !row.dto.isArchived) {
      remaining.push(rowIndex);
    }
  });
  remaining.sort((a, b) => compareByName(wire[a], wire[b]));
  for (const rowIndex of remaining) {
    const row = wire[rowIndex];
    const match = (entriesById.get(row.dto.sevenTvEmoteId) ?? []).find(
      (entryIndex) =>
        !consumed[entryIndex] &&
        live[entryIndex].name === row.dto.emoteName &&
        (row.instant === null || instants[entryIndex] === null),
    );
    if (match !== undefined) {
      bind(rowIndex, match);
    }
  }

  const siblingsOf = (entryIndex: number): string[] =>
    d2Names(
      (entriesById.get(live[entryIndex].sevenTvEmoteId) ?? [])
        .filter((other) => other !== entryIndex && live[other].name !== live[entryIndex].name)
        .map((other) => ({ name: live[other].name, instant: instants[other] })),
    );

  // Step 4: one row per `totals` row, in its order.
  const rows: EmoteUsageTotal[] = wire.map((row, rowIndex) => {
    const group = bindings.get(rowIndex);
    if (!group) {
      return {
        ...counted(row),
        membership: 'left',
        slotCount: 1,
        aliases: [row.dto.emoteName],
        siblingNames: row.slotId === null ? [] : [...row.siblingNames],
      };
    }
    const first = live[group[0]];
    return {
      ...counted(row),
      emoteName: first.name,
      imageUrl: first.imageUrl,
      membership: 'live',
      slotCount: group.length,
      aliases: group.map((entryIndex) => live[entryIndex].name),
      siblingNames: row.slotId === null ? [] : siblingsOf(group[0]),
    };
  });

  // Step 5: Klasse 3 — per entry in a slot response, per 7TV id otherwise.
  if (slotView) {
    const keys = klasse3Keys(live, instants, entriesById);
    live.forEach((entry, entryIndex) => {
      if (!consumed[entryIndex]) {
        rows.push(uncounted(entry, keys[entryIndex], [entry.name], siblingsOf(entryIndex)));
      }
    });
  } else {
    for (const [sevenTvEmoteId, group] of entriesById) {
      const free = group.filter((entryIndex) => !consumed[entryIndex]);
      if (free.length > 0) {
        const names = free.map((entryIndex) => live[entryIndex].name);
        rows.push(uncounted(live[free[0]], sevenTvEmoteId, names, []));
      }
    }
  }

  return rows;
}

/** A `/totals` row with its slot fields read tolerantly — an older Api omits them (plan 377, 2.1). */
interface WireRow {
  dto: EmoteUsageTotalDto;
  slotId: string | null;
  firstSeenAtFromRest: boolean;
  siblingNames: readonly string[];
  /** Absent reads as `true`: without the Api's proof a dated row binds by name only (F6). */
  shared: boolean;
  /** Milliseconds of an authoritative, parseable `firstSeenAt` of a slot row; `null` otherwise. */
  instant: number | null;
}

/** The backend's `BindByName` candidate order for two slot rows. */
function compareByName(a: WireRow, b: WireRow): number {
  if (a.firstSeenAtFromRest !== b.firstSeenAtFromRest) {
    return a.firstSeenAtFromRest ? 1 : -1;
  }
  const aDate = instantOf(a.dto.firstSeenAt);
  const bDate = instantOf(b.dto.firstSeenAt);
  if ((aDate === null) !== (bDate === null)) {
    return aDate === null ? 1 : -1;
  }
  if (aDate !== null && bDate !== null && aDate !== bDate) {
    return aDate - bDate;
  }
  const aId = a.slotId ?? '';
  const bId = b.slotId ?? '';
  return aId < bId ? -1 : aId > bId ? 1 : 0;
}

function readWire(dto: EmoteUsageTotalDto): WireRow {
  const older: Partial<
    Pick<
      EmoteUsageTotalDto,
      'slotId' | 'firstSeenAtFromRest' | 'siblingNames' | 'firstSeenAtShared'
    >
  > = dto;
  const slotId = older.slotId ?? null;
  const firstSeenAtFromRest = older.firstSeenAtFromRest ?? false;
  return {
    dto,
    slotId,
    firstSeenAtFromRest,
    siblingNames: older.siblingNames ?? [],
    shared: older.firstSeenAtShared ?? true,
    instant: slotId !== null && firstSeenAtFromRest ? instantOf(dto.firstSeenAt) : null,
  };
}

/** The fields a counted row takes from its DTO, whatever its membership. */
function counted(
  row: WireRow,
): Omit<EmoteUsageTotal, 'membership' | 'slotCount' | 'aliases' | 'siblingNames'> {
  const { dto } = row;
  return {
    emoteId: dto.emoteId,
    slotId: row.slotId,
    slotKey: row.slotId ?? dto.sevenTvEmoteId,
    emoteName: dto.emoteName,
    sevenTvEmoteId: dto.sevenTvEmoteId,
    imageUrl: dto.imageUrl,
    totalUseCount: dto.totalUseCount,
    lastUsedDate: dto.lastUsedDate,
    previousWindowUseCount: dto.previousWindowUseCount,
    firstSeenAt: dto.firstSeenAt,
    firstSeenAtFromRest: row.firstSeenAtFromRest,
    nameTwinEmoteSetIds: dto.nameTwinEmoteSetIds,
  };
}

/** Klasse 3: a live member with no counted row under this set at all (E17). */
function uncounted(
  first: ForeignEmoteRow,
  slotKey: string,
  aliases: string[],
  siblingNames: string[],
): EmoteUsageTotal {
  return {
    emoteId: null,
    slotId: null,
    slotKey,
    emoteName: first.name,
    sevenTvEmoteId: first.sevenTvEmoteId,
    imageUrl: first.imageUrl,
    totalUseCount: null,
    lastUsedDate: null,
    previousWindowUseCount: null,
    firstSeenAt: null,
    firstSeenAtFromRest: false,
    membership: 'live',
    slotCount: aliases.length,
    aliases,
    siblingNames,
    nameTwinEmoteSetIds: [],
  };
}

/**
 * The per-entry Klasse-3 keys (plan 377, 2.3, Sol S1), fixed over the complete live list:
 * `live:<id>#<addedAt>` for the only entry of its id at that instant, `live:<id>#<addedAt>#<name>`
 * when another entry of the id shares the instant, `live:<id>##<name>` without a usable date.
 * `addedAt` is the raw wire string; names are unique within a set, so every form is unique.
 */
function klasse3Keys(
  live: readonly ForeignEmoteRow[],
  instants: readonly (number | null)[],
  entriesById: ReadonlyMap<string, number[]>,
): string[] {
  return live.map((entry, entryIndex) => {
    const instant = instants[entryIndex];
    const prefix = `live:${entry.sevenTvEmoteId}#`;
    if (instant === null || !entry.addedAt) {
      return `${prefix}#${entry.name}`;
    }
    const tied = (entriesById.get(entry.sevenTvEmoteId) ?? []).some(
      (other) => other !== entryIndex && instants[other] === instant,
    );
    return tied ? `${prefix}${entry.addedAt}#${entry.name}` : `${prefix}${entry.addedAt}`;
  });
}

/** Names in D2 order — add date ascending, unknown last, then name ordinal — ordinal distinct. */
function d2Names(entries: { name: string; instant: number | null }[]): string[] {
  const sorted = [...entries].sort((a, b) => {
    if (a.instant !== b.instant) {
      if (a.instant === null) {
        return 1;
      }
      if (b.instant === null) {
        return -1;
      }
      return a.instant - b.instant;
    }
    return a.name < b.name ? -1 : a.name > b.name ? 1 : 0;
  });
  return [...new Set(sorted.map((entry) => entry.name))];
}

/** Milliseconds of an ISO timestamp, `null` when absent or unparseable. */
function instantOf(value: string | null | undefined): number | null {
  if (!value) {
    return null;
  }
  const ms = Date.parse(value);
  return Number.isNaN(ms) ? null : ms;
}

function instantKey(sevenTvEmoteId: string, instant: number): string {
  return `${sevenTvEmoteId}|${instant}`;
}

/** Indexes of `items` grouped by key, groups in first-encounter order, indexes ascending. */
function groupIndexes<T>(items: readonly T[], keyOf: (item: T) => string): Map<string, number[]> {
  const groups = new Map<string, number[]>();
  items.forEach((item, index) => pushTo(groups, keyOf(item), index));
  return groups;
}

function pushTo(groups: Map<string, number[]>, key: string, value: number): void {
  const group = groups.get(key);
  if (group) {
    group.push(value);
  } else {
    groups.set(key, [value]);
  }
}
