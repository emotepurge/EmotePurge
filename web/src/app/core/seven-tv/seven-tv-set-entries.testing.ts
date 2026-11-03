/*
 * Test fixtures for the gap read of #454: 7TV's v4 answer for set `01HCN0ZBX80009XR48886X5MXJ` as
 * measured on 2026-11-01 — `totalCount` 496, 494 entries listed, stable on every read. The two
 * entries 7TV counts but never lists are `brudiWrap2` and `AGGRO5` (both added 2026-10-27); they
 * are deliberately absent from every page built here. Shared by the reader, the delete run and the
 * delete flow specs.
 */

/** One entry as the fixture lists it (or, for {@link hiddenEntries}, as 7TV would if it did). */
export interface GapFixtureEntry {
  id: string;
  alias: string;
  addedAt: string;
}

/** The two entries 7TV counts but does not list. */
export const hiddenEntries: readonly GapFixtureEntry[] = [
  { id: '01M5YDWTX89J4KEPDHK8D67ARS', alias: 'brudiWrap2', addedAt: '2026-10-27T18:02:11.000Z' },
  { id: '01M5Z84MTEDERMD2AZN2FHJJ53', alias: 'AGGRO5', addedAt: '2026-10-27T18:04:40.000Z' },
];

/** The set's own count, hidden entries included. */
export const GAP_FIXTURE_TOTAL_COUNT = 496;

export interface GapFixtureOptions {
  /** Select `addedAt` on every item, like the slot delete's reads. Default `true`. */
  withAddedAt?: boolean;
  /** How many of the counted entries are not listed. Default 2. */
  omit?: number;
  /** Over how many pages the listed entries are spread (equal shares, the rest on the last one).
   *  Default 1. */
  pages?: number;
}

/** The entries the fixture lists, in 7TV order — unique ids, unique aliases, one add date each. */
export function visibleEntries(omit = 2): GapFixtureEntry[] {
  const listed = GAP_FIXTURE_TOTAL_COUNT - omit;
  return Array.from({ length: listed }, (_, index) => ({
    id: `01GAPFIXTURE${String(index).padStart(14, '0')}`,
    alias: `gapEmote${index}`,
    addedAt: new Date(Date.UTC(2025, 0, 1) + index * 60_000).toISOString(),
  }));
}

/** One page of the v4 set read, the way `loadSevenTvSetEntries` receives it. */
export function setEntriesResponse(
  entries: readonly GapFixtureEntry[],
  options: { totalCount: number; pageCount?: number; withAddedAt?: boolean },
) {
  const withAddedAt = options.withAddedAt ?? true;
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: options.totalCount,
            pageCount: options.pageCount ?? 1,
            items: entries.map((entry) => ({
              alias: entry.alias,
              ...(withAddedAt ? { addedAt: entry.addedAt } : {}),
              emote: { id: entry.id, defaultName: entry.alias },
            })),
          },
        },
      },
    },
  };
}

/** Every page of the gap read, in page order — one element for the default single page. */
export function gapRead496of494Pages(options: GapFixtureOptions = {}) {
  const pages = options.pages ?? 1;
  const entries = visibleEntries(options.omit ?? 2);
  const share = Math.floor(entries.length / pages);
  return Array.from({ length: pages }, (_, index) =>
    setEntriesResponse(
      entries.slice(index * share, index === pages - 1 ? entries.length : (index + 1) * share),
      {
        totalCount: GAP_FIXTURE_TOTAL_COUNT,
        pageCount: pages,
        withAddedAt: options.withAddedAt,
      },
    ),
  );
}

/** The single-page gap read as measured: 496 counted, 494 listed, both hidden entries absent. */
export function gapRead496of494Response(options: Omit<GapFixtureOptions, 'pages'> = {}) {
  return gapRead496of494Pages({ ...options, pages: 1 })[0];
}
