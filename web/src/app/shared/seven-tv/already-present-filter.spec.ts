import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { filterAlreadyPresent, filterAlreadyPresentForRestore } from './already-present-filter';

interface Row {
  sevenTvEmoteId: string;
  name: string;
}

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';

/** A `filterAlreadyPresent` GQL page response containing exactly the given 7TV emote ids, as a page
 *  numbered `page` out of `pageCount` total. */
function page(ids: string[], page = 1, pageCount = 1) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: ids.length,
            pageCount,
            items: ids.map((id) => ({ emote: { id } })),
          },
        },
      },
    },
  };
}

describe('filterAlreadyPresent', () => {
  let httpClient: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpClient = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('asks 7TV directly, not our own API', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.method).toBe('POST');
    // Public read — reading an emote set's contents needs no 7TV token, unlike the write mutations.
    expect(req.request.headers.has('Authorization')).toBe(false);
    expect(req.request.body.variables).toEqual({ id: 'target-set', page: 1, perPage: 500 });
    req.flush(page([]));

    expect(await result$).toEqual({ rows, skipped: 0, available: true });
  });

  it('passes every row through unfiltered when the target set is empty', async () => {
    const rows: Row[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush(page([]));

    expect(await result$).toEqual({ rows, skipped: 0, available: true });
  });

  it('drops a row already present in the target set, keyed on the 7TV emote id regardless of alias', async () => {
    const rows: Row[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];
    // Same 7TV id as row 1 — #149/T5's actual hole: 7TV's addEmote only checks the alias string,
    // not the emote id, so a second entry under a different alias would otherwise be pushed too.
    // This filter compares ids alone (#149 P1; spec #200 7.2 keeps import on the id axis) — the
    // entries here carry no alias at all, and none is needed.

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush(page(['7tv-1']));

    expect(await result$).toEqual({ rows: [rows[1]], skipped: 1, available: true });
  });

  it('drops every row when all are already present, without erroring on the empty result', async () => {
    const rows: Row[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush(page(['7tv-1', '7tv-2']));

    expect(await result$).toEqual({ rows: [], skipped: 2, available: true });
  });

  it('fetches the given set id, not the channel name — a fresh call, never a cached/shared result', async () => {
    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'my-set-id', []));

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.body.variables.id).toBe('my-set-id');
    req.flush(page([]));

    await result$;
  });

  // #149 P1 (independent review): the first version of this filter asked our own database
  // (`EmoteAdminService.listEmotes`), which is exactly wrong for restore — restore runs *because*
  // something already went wrong, most often right after a delete whose closing report to our own
  // backend (sync-deleted) is still pending, failed, or partial. In that window our database still
  // lists the row as active while 7TV has already dropped it. Phrased as behaviour: a row 7TV no
  // longer has must be treated as absent (i.e. not skipped) even though it is still in the input as
  // "recently deleted" — which is exactly what an empty 7TV response for it produces here, since
  // this filter only ever removes what 7TV *does* still have.
  it('does not drop a row our database might still consider active but 7TV has already lost — it only removes what 7TV currently has', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    // 7TV's own answer for the target set — empty, i.e. the row genuinely is not there any more,
    // regardless of what a stale database mirror might still say.
    httpMock.expectOne(GQL_ENDPOINT).flush(page([]));

    expect(await result$).toEqual({ rows, skipped: 0, available: true });
  });

  it('walks every page up to the reported page count', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-999', name: 'Target' }];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));

    const first = httpMock.expectOne(GQL_ENDPOINT);
    expect(first.request.body.variables).toEqual({ id: 'target-set', page: 1, perPage: 500 });
    first.flush(page(['7tv-1'], 1, 2));

    const second = httpMock.expectOne(GQL_ENDPOINT);
    expect(second.request.body.variables).toEqual({ id: 'target-set', page: 2, perPage: 500 });
    second.flush(page(['7tv-999'], 2, 2));

    expect(await result$).toEqual({ rows: [], skipped: 1, available: true });
  });

  it('stops at the 10-page runaway guard rather than paginating forever', async () => {
    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', []));

    // A set that would need an 11th page has never been seen in this codebase (the guard mirrors
    // the backend's own MaxSetEntryPages) — every page here still reports more pages available
    // (pageCount stays far above the current page) to prove the loop stops on the count, not
    // because pageCount happened to run out.
    for (let requestedPage = 1; requestedPage <= 10; requestedPage++) {
      const req = httpMock.expectOne(GQL_ENDPOINT);
      expect(req.request.body.variables.page).toBe(requestedPage);
      req.flush(page([], requestedPage, 999));
    }

    await result$;
    httpMock.expectNone(GQL_ENDPOINT);
  });

  // Fails open: a run the user already confirmed must not be blocked by a failed best-effort check.
  // But it must not read as a clean all-clear either — that is `available`'s whole job, see the two
  // cases below.
  it('fails open on a failed fetch, returning every row unfiltered and reporting the check as unavailable', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).error(new ProgressEvent('network error'));

    expect(await result$).toEqual({ rows, skipped: 0, available: false });
  });

  it('fails open on a GraphQL-level rejection disguised as HTTP 200', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'unknown set' }] });

    expect(await result$).toEqual({ rows, skipped: 0, available: false });
  });

  // The whole point of `available`: "nothing needed skipping" and "nothing could be checked" must
  // never collapse into the same `skipped: 0` a caller can no longer tell apart (the defect this
  // filter used to have).
  it('distinguishes a successful check that found nothing to skip from a check that could not run at all', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }];

    const checked$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush(page([]));
    const checked = await checked$;

    const unverified$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).error(new ProgressEvent('network error'));
    const unverified = await unverified$;

    expect(checked).toEqual({ rows, skipped: 0, available: true });
    expect(unverified).toEqual({ rows, skipped: 0, available: false });
    expect(checked.available).not.toBe(unverified.available);
  });
});

/** A page of set entries with their aliases — what the restore variant reads. */
function entriesPage(entries: { id: string; alias: string }[]) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: entries.length,
            pageCount: 1,
            items: entries.map(({ id, alias }) => ({ alias, emote: { id } })),
          },
        },
      },
    },
  };
}

interface RestoreRow {
  sevenTvEmoteId: string;
  name: string;
  aliases?: string[];
}

// Operator decision 2026-09-22 ("middle rule"), refining spec #200 7.2's (sevenTvEmoteId, alias)
// comparison for the restore run only.
describe('filterAlreadyPresentForRestore', () => {
  let httpClient: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpClient = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  async function run(rows: RestoreRow[], entries: { id: string; alias: string }[]) {
    const result$ = firstValueFrom(filterAlreadyPresentForRestore(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush(entriesPage(entries));
    return result$;
  }

  it("asks 7TV for each entry's alias, not only its id", async () => {
    const result$ = firstValueFrom(filterAlreadyPresentForRestore(httpClient, 'target-set', []));
    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.body.query).toContain('alias');
    req.flush(entriesPage([]));
    await result$;
  });

  it('keeps a row whose emote is not in the set at all, unchanged', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(await run([row], [{ id: '7tv-other', alias: 'A' }])).toEqual({
      rows: [row],
      skipped: 0,
      available: true,
    });
  });

  it('drops a row whose every alias is already present under the same id', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(
      await run(
        [row],
        [
          { id: '7tv-1', alias: 'A' },
          { id: '7tv-1', alias: 'B' },
        ],
      ),
    ).toEqual({ rows: [], skipped: 2, available: true });
  });

  // The partial retry the id-only check made impossible: A came back, B failed — re-running the
  // protocol must add B, and only B.
  it('keeps only the missing aliases of a row whose id is present under some of its own aliases', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(await run([row], [{ id: '7tv-1', alias: 'A' }])).toEqual({
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['B'] }],
      skipped: 1,
      available: true,
    });
  });

  // #149: 7TV's addEmote only rejects a colliding alias string — re-adding A or B next to C would
  // enter the same emote a second time.
  it('drops the whole row when its id is present under an alias the row does not name', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(await run([row], [{ id: '7tv-1', alias: 'C' }])).toEqual({
      rows: [],
      skipped: 2,
      available: true,
    });
  });

  it('drops the whole row when a foreign alias sits next to one of its own', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(
      await run(
        [row],
        [
          { id: '7tv-1', alias: 'A' },
          { id: '7tv-1', alias: 'C' },
        ],
      ),
    ).toEqual({ rows: [], skipped: 2, available: true });
  });

  it('reads a row without aliases as [name], like the restore queue does', async () => {
    const present: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A' };
    const renamed: RestoreRow = { sevenTvEmoteId: '7tv-2', name: 'B' };

    expect(
      await run(
        [present, renamed],
        [
          { id: '7tv-1', alias: 'A' },
          { id: '7tv-2', alias: 'NotB' },
        ],
      ),
    ).toEqual({ rows: [], skipped: 2, available: true });
  });

  it('compares aliases exactly, so a case-only difference counts as a foreign alias', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'pogu', aliases: ['pogu'] };

    expect(await run([row], [{ id: '7tv-1', alias: 'PogU' }])).toEqual({
      rows: [],
      skipped: 1,
      available: true,
    });
  });

  it('fails open on a failed fetch, like the id-only check', async () => {
    const rows: RestoreRow[] = [{ sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] }];

    const result$ = firstValueFrom(filterAlreadyPresentForRestore(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).error(new ProgressEvent('network error'));

    expect(await result$).toEqual({ rows, skipped: 0, available: false });
  });
});
