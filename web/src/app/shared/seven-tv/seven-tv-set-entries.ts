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
// near 5000 entries. Only `alias` and `emote.id` are requested: unlike the backend's preview query
// (which also needs name/scores for a human-facing list), the readers here only compare ids and the
// aliases each id sits under.
const SET_ENTRIES_PER_PAGE = 500;
const MAX_SET_ENTRY_PAGES = 10;

const GQL_EMOTE_SET_ENTRIES_QUERY =
  'query($id: Id!, $page: Int!, $perPage: Int!) { emoteSets { emoteSet(id: $id) { emotes(page: $page, perPage: $perPage) { totalCount pageCount items { alias emote { id } } } } } }';

interface SevenTvGqlEmoteSetEntriesResponse {
  data?: {
    emoteSets?: {
      emoteSet?: {
        emotes?: {
          pageCount: number;
          items: { alias?: string | null; emote: { id: string } }[];
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
  /** Every 7TV emote id in the set, mapped to every alias it sits under there — two for a #74
   *  duplicate (the same emote entered twice under two names), in the order 7TV lists them. An entry
   *  7TV reports without an alias contributes the id with no alias for it. */
  aliasesById: Map<string, string[]>;
  /** `false` when the read stopped at the runaway guard while 7TV still reported more pages — the
   *  map then only knows part of the set. A caller for which "part" is not good enough (the delete
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
 * Asks **7TV itself**, not our database or our Api's preview route: the two readers of this (the
 * restore's pre-run check in `already-present-filter.ts` and the delete run's alias read in
 * `mass-delete-panel.ts`) both need the set as it stands at the moment of the write, and both run
 * *because* the user just asked for a write — our own mirror can lag exactly there (see
 * `filterAlreadyPresent`'s doc). It also draws on 7TV's *global* rate-limit bucket, not our Api's
 * shared `ForeignEmoteLookup` limiter, so a delete right after a few set switches is not refused by
 * our own budget.
 *
 * Errors (network, HTTP, or a GraphQL-level rejection) all become a thrown error — callers decide
 * whether that fails open (the restore check) or blocks (the delete alias read). Stops early once a
 * page reports it was the last one (`page >= pageCount`), and unconditionally at
 * `MAX_SET_ENTRY_PAGES`, reporting `complete: false` if 7TV still promised more.
 */
export function loadSevenTvSetEntries(
  httpClient: HttpClient,
  setId: string,
): Observable<SevenTvSetEntries> {
  const aliasesById = new Map<string, string[]>();

  function loadPage(page: number): Observable<SevenTvSetEntries> {
    return fetchEmoteSetEntriesPage(httpClient, setId, page).pipe(
      switchMap((response) => {
        const emotes = response.data?.emoteSets?.emoteSet?.emotes;
        if ((response.errors?.length ?? 0) > 0 || !emotes) {
          return throwError(() => new Error('7TV emote set read failed'));
        }
        for (const item of emotes.items) {
          const aliases = aliasesById.get(item.emote.id) ?? [];
          if (item.alias && !aliases.includes(item.alias)) {
            aliases.push(item.alias);
          }
          aliasesById.set(item.emote.id, aliases);
        }
        if (page >= emotes.pageCount) {
          return of({ aliasesById, complete: true });
        }
        if (page >= MAX_SET_ENTRY_PAGES) {
          return of({ aliasesById, complete: false });
        }
        return loadPage(page + 1);
      }),
    );
  }

  return loadPage(1);
}
