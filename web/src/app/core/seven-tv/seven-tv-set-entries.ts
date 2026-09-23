import { HttpClient } from '@angular/common/http';
import { Observable, of, switchMap, throwError } from 'rxjs';

/** Host-absolute, same endpoint the write mutations already use (`seven-tv-run-engine.ts`) —
 *  reading a set's contents is public on `v4`, so unlike the mutations this needs no
 *  `Authorization` header and no 7TV token. */
const SEVEN_TV_GQL_ENDPOINT = 'https://7tv.io/v4/gql';

// Mirrors SevenTvApiClient.cs's GqlEmoteSetPreviewQuery/SetEntriesPerPage/MaxSetEntryPages
// (`src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs`): 500 per page keeps even a
// subscriber-sized set (capacity can exceed 1000) at a handful of requests, and the 10-page cap is
// a runaway guard, not an expected limit — nothing in this codebase has ever seen a set anywhere
// near 5000 entries. `alias`, `emote.id` and `emote.defaultName` are requested: unlike the
// backend's preview query (which also needs scores for a human-facing list), the readers here only
// compare ids and the aliases each id sits under — `defaultName` is the one exception, needed by the
// transfer-run protocol (shared/export/transfer-run-export.ts) to name an aliasless entry.
const SET_ENTRIES_PER_PAGE = 500;
const MAX_SET_ENTRY_PAGES = 10;

const GQL_EMOTE_SET_ENTRIES_QUERY =
  'query($id: Id!, $page: Int!, $perPage: Int!) { emoteSets { emoteSet(id: $id) { emotes(page: $page, perPage: $perPage) { totalCount pageCount items { alias emote { id defaultName } } } } } }';

interface SevenTvGqlEmoteSetEntriesResponse {
  data?: {
    emoteSets?: {
      emoteSet?: {
        emotes?: {
          totalCount: number;
          pageCount: number;
          items: { alias?: string | null; emote: { id: string; defaultName?: string | null } }[];
        } | null;
      } | null;
    } | null;
  };
  // 7TV can answer a GraphQL-level rejection (e.g. an unknown set id, or its rate limit) with HTTP
  // 200 — presence of this array, regardless of content, is treated as "no usable data".
  errors?: unknown[];
}

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
  /** `false` when the read stopped at the runaway guard while 7TV still reported more pages, or
   *  when the last page's cumulative item count did not match the query's own `totalCount` — offset
   *  pagination shifting between page fetches can silently drop or duplicate an entry across the
   *  page boundary even when every page individually looked complete. The map then only knows part
   *  of the set (or misrepresents it). A caller for which "part" is not good enough (the delete
   *  run's alias read, spec #200 8.3: a list that only knows half must not delete) checks this. */
  complete: boolean;
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
 * Asks **7TV itself**, not our database or our Api's preview route: its readers (the pre-run
 * checks in `already-present-filter.ts`, the delete run's alias read in `mass-delete-panel.ts`, and
 * the import run's re-read after a run with an unanswered step in `seven-tv-import.service.ts`) all
 * need the set as it stands at the moment of the write, and all run *because* the user just asked
 * for a write — our own mirror can lag exactly there (see
 * `filterAlreadyPresent`'s doc). It also draws on 7TV's *global* rate-limit bucket, not our Api's
 * shared `ForeignEmoteLookup` limiter, so a delete right after a few set switches is not refused by
 * our own budget.
 *
 * Errors (network, HTTP, or a GraphQL-level rejection) all become a thrown error — callers decide
 * whether that fails open (the restore check) or blocks (the delete alias read). Stops early once a
 * page reports it was the last one (`page >= pageCount`), and unconditionally at
 * `MAX_SET_ENTRY_PAGES`, reporting `complete: false` if 7TV still promised more — or, even when
 * pagination ended "normally" (`page >= pageCount`), if the cumulative item count across every page
 * does not match the last page's own `totalCount` (K5 fix round): offset pagination shifting
 * between two fetches of the same set can silently miss (or double-count) an entry at a page
 * boundary without ever tripping the runaway guard.
 */
export function loadSevenTvSetEntries(
  httpClient: HttpClient,
  setId: string,
): Observable<SevenTvSetEntries> {
  const aliasesById = new Map<string, string[]>();
  const aliaslessIds = new Set<string>();
  const defaultNameById = new Map<string, string>();
  let collected = 0;

  function loadPage(page: number): Observable<SevenTvSetEntries> {
    return fetchEmoteSetEntriesPage(httpClient, setId, page).pipe(
      switchMap((response) => {
        const emotes = response.data?.emoteSets?.emoteSet?.emotes;
        if ((response.errors?.length ?? 0) > 0 || !emotes) {
          return throwError(() => new Error('7TV emote set read failed'));
        }
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
        }
        collected += emotes.items.length;
        if (page >= emotes.pageCount) {
          return of({
            aliasesById,
            aliaslessIds,
            defaultNameById,
            complete: collected === emotes.totalCount,
          });
        }
        if (page >= MAX_SET_ENTRY_PAGES) {
          return of({ aliasesById, aliaslessIds, defaultNameById, complete: false });
        }
        return loadPage(page + 1);
      }),
    );
  }

  return loadPage(1);
}
