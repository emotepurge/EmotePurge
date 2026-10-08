import { HttpClient } from '@angular/common/http';
import { defer, map, Observable, of, switchMap, throwError } from 'rxjs';

/** Host-absolute, same endpoint the write mutations already use (`seven-tv-run-engine.ts`) —
 *  reading a set's contents is public on `v4`, so unlike the mutations this needs no
 *  `Authorization` header and no 7TV token. */
const SEVEN_TV_GQL_ENDPOINT = 'https://7tv.io/v4/gql';

// Mirrors SevenTvApiClient.cs's GqlEmoteSetPreviewQuery/SetEntriesPerPage/MaxSetEntryPages
// (`src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs`): 500 per page keeps even a
// subscriber-sized set (capacity can exceed 1000) at a handful of requests, and the 10-page cap is
// a runaway guard, not an expected limit — nothing in this codebase has ever seen a set anywhere
// near 5000 entries. `alias`, `emote.id`, `emote.defaultName` and `emote.flags.animated` are
// requested: unlike the backend's preview query (which also needs scores for a human-facing list),
// the readers here mostly compare ids and the aliases each id sits under. Two exceptions:
// `defaultName`, needed by the transfer-run protocol (shared/export/transfer-run-export.ts) to name an
// aliasless entry, and `flags { animated }` — the very field the backend preview query reads
// (`GqlEmoteSetPreviewQuery`) — so the undo confirm dialog (#254, spec 17 K1) can show a source
// emote's still image without a second request. `Emote.images` stays deliberately unread, as in the
// backend: the url is built from the id and this flag (`BuildForeignImageUrl`).
const SET_ENTRIES_PER_PAGE = 500;
const MAX_SET_ENTRY_PAGES = 10;

const GQL_EMOTE_SET_ENTRIES_QUERY =
  'query($id: Id!, $page: Int!, $perPage: Int!) { emoteSets { emoteSet(id: $id) { emotes(page: $page, perPage: $perPage) { totalCount pageCount items { alias emote { id defaultName flags { animated } } } } } } }';

interface SevenTvGqlEmoteSetEntriesResponse {
  data?: {
    emoteSets?: {
      emoteSet?: {
        emotes?: {
          totalCount: number;
          pageCount: number;
          items: {
            alias?: string | null;
            emote: {
              id: string;
              defaultName?: string | null;
              flags?: { animated?: boolean | null } | null;
            };
          }[];
        } | null;
      } | null;
    } | null;
  };
  // 7TV can answer a GraphQL-level rejection (e.g. an unknown set id, or its rate limit) with HTTP
  // 200 — presence of this array, regardless of content, is treated as "no usable data".
  errors?: unknown[];
}

type EmotesPayload = NonNullable<
  NonNullable<NonNullable<SevenTvGqlEmoteSetEntriesResponse['data']>['emoteSets']>['emoteSet']
>['emotes'];

/** What one full read of a set's entries found. */
export interface SevenTvSetEntries {
  /** Every 7TV emote id in the set, mapped to every *aliased* entry it sits under there — two for
   *  a #74 duplicate (the same emote entered twice under two names), in the order 7TV lists them.
   *  An id that has only aliasless entries still gets a (empty) map entry, so `.has()` alone still
   *  answers "is this id in the set at all". An id that has both an aliased and an aliasless entry
   *  keeps only the aliased one here — check `aliaslessIds` for the rest (K5 fix round, spec §37/§38:
   *  the aliasless entry must not silently disappear once the same id also has a named one). */
  aliasesById: Map<string, string[]>;
  /** Every 7TV emote id that has **at least one** entry without an alias — set regardless of
   *  whether that same id also has an aliased entry elsewhere in `aliasesById` (an id can carry
   *  both: two separate entries of the same emote, one named, one not). A reader that would
   *  otherwise treat "the id is in the set, and every alias I know of matches" as fully accounted
   *  for must also check this: an aliasless entry is a slot the row can never name, so it counts as
   *  foreign no differently than a genuinely different alias string would (K5 fix round, spec
   *  §37/§38). */
  aliaslessIds: Set<string>;
  /** Every 7TV emote id in the set, mapped to its 7TV default name — filled for every id
   *  regardless of whether it has a named or an aliasless entry (or both). A missing/empty
   *  `defaultName` in 7TV's own answer is recorded as `''`, the same defensive fallback the backend
   *  uses (`SevenTvApiClient.cs`, `dto.Emote?.DefaultName ?? string.Empty`) — never guessed from the
   *  id. Read by the transfer-run protocol (`shared/export/transfer-run-export.ts`) to name an
   *  aliasless target entry, which otherwise has no name a human would recognise. */
  defaultNameById: Map<string, string>;
  /** Every 7TV emote id in the set, mapped to whether 7TV flags it as animated
   *  (`emote.flags.animated`) — filled for every id, like `defaultNameById`. A missing flag is
   *  recorded as `false`, the same guard the backend applies (`SevenTvApiClient.cs`,
   *  `dto.Emote?.Flags?.Animated ?? false`): the still rendition `4x.webp` exists for every emote,
   *  whereas `4x_static.webp` 404s for a static one (measured 2026-09-09). Read by the undo confirm
   *  dialog (#254, spec 17 K1) to build a source emote's still image url. */
  animatedById: Map<string, boolean>;
  /** The set's own `totalCount` as of this read — its live occupied-slot count, independent of how
   *  many pages this read itself collected or whether it came back `complete`. Read by the import
   *  confirm dialog (spec #255) to keep the slot projection current after a live re-read, rather
   *  than only ever showing the number the picker loaded the dialog with. */
  occupiedSlots: number;
  /** `false` when the read stopped at the runaway guard while 7TV still reported more pages, or
   *  when the pages do not add up to one consistent snapshot of the set: the cumulative item count
   *  differs from the query's own `totalCount`, `totalCount` changed between two responses of the
   *  read, the same (emote id, alias) pair showed up on two different pages, or — for a multi-page
   *  read — re-reading every page but the last one afterwards returned different members than the
   *  first pass. Offset pagination shifting between page fetches (another editor changing the set)
   *  can silently drop or duplicate an entry across a page boundary even when every page
   *  individually looked complete. The map then only knows part of the set (or misrepresents it).
   *  A caller for which "part" is not good enough (the delete run's alias read, spec #200 8.3: a
   *  list that only knows half must not delete) checks this. */
  complete: boolean;
}

function memberKey(item: { alias?: string | null; emote: { id: string } }): string {
  return `${item.emote.id}\u0000${item.alias ?? ''}`;
}

/** Code-unit order: deterministic and locale-independent, which is all a membership comparison needs. */
function compareKeys(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function fetchEmoteSetEntriesPage(
  httpClient: HttpClient,
  setId: string,
  page: number,
): Observable<SevenTvGqlEmoteSetEntriesResponse> {
  return httpClient.post<SevenTvGqlEmoteSetEntriesResponse>(SEVEN_TV_GQL_ENDPOINT, {
    query: GQL_EMOTE_SET_ENTRIES_QUERY,
    variables: { id: setId, page, perPage: SET_ENTRIES_PER_PAGE },
  });
}

/**
 * Reads `setId`'s current entries straight from 7TV — every page, fresh, never cached — and groups
 * them by emote id with every alias each id sits under.
 *
 * Asks **7TV itself**, not our database or our Api's preview route: its readers (the pre-run checks
 * in `already-present-filter.ts`, the delete run's alias read in `mass-delete-panel.ts`, and the
 * re-read after a run with an unanswered step in `seven-tv-import.service.ts`,
 * `seven-tv-delete.service.ts` and `seven-tv-restore.service.ts`) all need the set as it stands at
 * the moment of the write, and all run *because* the user just asked for a write — our own mirror
 * can lag exactly there (see `filterAlreadyPresent`'s doc). It also draws on 7TV's *global*
 * rate-limit bucket, not our Api's shared `ForeignEmoteLookup` limiter, so a delete right after a few
 * set switches is not refused by our own budget.
 *
 * Errors (network, HTTP, or a GraphQL-level rejection) all become a thrown error — callers decide
 * whether that fails open (the restore check) or blocks (the delete alias read); this includes the
 * verification re-reads. Stops early once a page reports it was the last one (`page >= pageCount`),
 * and unconditionally at `MAX_SET_ENTRY_PAGES`, reporting `complete: false` if 7TV still promised
 * more. Even when pagination ended "normally" the read is `complete: false` if the cumulative item
 * count does not match `totalCount`, if `totalCount` differs between responses, or if an (emote id,
 * alias) pair repeats across pages (7TV keeps aliases unique per set, so a repeat means a shift).
 * Only when all of that is clean and the set spans several pages, pages 1..n-1 are fetched once
 * more and compared by membership: an editor swapping an emote (removing one on an early page,
 * adding one at the end) keeps `totalCount` equal and creates no duplicate, yet the first entry of
 * the next page slides onto the earlier page after that page was read and would otherwise go
 * unnoticed. A remove-and-re-add sequence fully contained between a page's first read and its
 * re-read cannot be detected by any finite re-read (it needs 7TV to order entries other than by
 * insertion, plus several edits within about a second). The returned maps come from the first pass only; single-page sets cost no extra request.
 */
export function loadSevenTvSetEntries(
  httpClient: HttpClient,
  setId: string,
): Observable<SevenTvSetEntries> {
  // Deferred so every subscription accumulates into its own state.
  return defer(() => {
    const aliasesById = new Map<string, string[]>();
    const aliaslessIds = new Set<string>();
    const defaultNameById = new Map<string, string>();
    const animatedById = new Map<string, boolean>();
    let collected = 0;
    // Consistency bookkeeping for `complete` (see its doc): totalCount per response, the members of
    // every main-pass page, and whether a pair already seen on an earlier page came back again.
    const totalCounts = new Set<number>();
    const pageMembers: string[][] = [];
    const seenMembers = new Set<string>();
    let repeatedAcrossPages = false;

    function readPage(page: number): Observable<NonNullable<EmotesPayload>> {
      return fetchEmoteSetEntriesPage(httpClient, setId, page).pipe(
        switchMap((response) => {
          const emotes = response.data?.emoteSets?.emoteSet?.emotes;
          if ((response.errors?.length ?? 0) > 0 || !emotes) {
            return throwError(() => new Error('7TV emote set read failed'));
          }
          totalCounts.add(emotes.totalCount);
          return of(emotes);
        }),
      );
    }

    // Re-reads pages 1..lastPage-1 and resolves `false` as soon as one differs from the main pass.
    function verifyPage(page: number, lastPage: number): Observable<boolean> {
      return readPage(page).pipe(
        switchMap((emotes) => {
          if (totalCounts.size > 1) {
            return of(false);
          }
          const again = emotes.items.map(memberKey).sort(compareKeys);
          const first = [...pageMembers[page - 1]].sort(compareKeys);
          if (again.length !== first.length || again.some((key, index) => key !== first[index])) {
            return of(false);
          }
          return page + 1 < lastPage ? verifyPage(page + 1, lastPage) : of(true);
        }),
      );
    }

    function loadPage(page: number): Observable<SevenTvSetEntries> {
      return readPage(page).pipe(
        switchMap((emotes) => {
          const members = emotes.items.map(memberKey);
          pageMembers.push(members);
          // Checked against earlier pages only — a repeat within one page is not a shift.
          if (members.some((key) => seenMembers.has(key))) {
            repeatedAcrossPages = true;
          }
          members.forEach((key) => seenMembers.add(key));
          for (const item of emotes.items) {
            const aliases = aliasesById.get(item.emote.id) ?? [];
            if (item.alias) {
              if (!aliases.includes(item.alias)) {
                aliases.push(item.alias);
              }
            } else {
              aliaslessIds.add(item.emote.id);
            }
            aliasesById.set(item.emote.id, aliases);
            defaultNameById.set(item.emote.id, item.emote.defaultName ?? '');
            animatedById.set(item.emote.id, item.emote.flags?.animated ?? false);
          }
          collected += emotes.items.length;
          const result = (complete: boolean): SevenTvSetEntries => ({
            aliasesById,
            aliaslessIds,
            defaultNameById,
            animatedById,
            occupiedSlots: emotes.totalCount,
            complete,
          });
          if (page >= emotes.pageCount) {
            const consistent =
              collected === emotes.totalCount && totalCounts.size === 1 && !repeatedAcrossPages;
            if (!consistent || page === 1) {
              return of(result(consistent));
            }
            return verifyPage(1, page).pipe(
              map((unchanged) => result(unchanged && totalCounts.size === 1)),
            );
          }
          if (page >= MAX_SET_ENTRY_PAGES) {
            return of(result(false));
          }
          return loadPage(page + 1);
        }),
      );
    }

    return loadPage(1);
  });
}
