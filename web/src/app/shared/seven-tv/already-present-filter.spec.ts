import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ImportRow } from '../../core/seven-tv/import-source';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { TransferPlan, TransferRow } from '../../core/seven-tv/transfer-plan';
import {
  filterAlreadyPresent,
  filterAlreadyPresentForRestore,
  loadRestoreConfirmPreview,
  restoreConfirmPreviewUnavailable,
  stampReplaceTargets,
  verifyReplaceTargets,
} from './already-present-filter';

interface Row {
  sevenTvEmoteId: string;
  name: string;
}

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';

/** A `filterAlreadyPresent` GQL page response containing exactly the given 7TV emote ids, as a page
 *  numbered `page` out of `pageCount` total. `totalCount` is the whole set's size, the same on every
 *  page of one read — it defaults to this page's own size, which is right for a single-page set. */
function page(ids: string[], page = 1, pageCount = 1, totalCount = ids.length) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount,
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

    expect(await result$).toEqual({
      rows,
      skipped: 0,
      available: true,
      entries: expect.objectContaining({ complete: true }),
    });
  });

  it('passes every row through unfiltered when the target set is empty', async () => {
    const rows: Row[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush(page([]));

    expect(await result$).toEqual({
      rows,
      skipped: 0,
      available: true,
      entries: expect.objectContaining({ complete: true }),
    });
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

    expect(await result$).toEqual({
      rows: [rows[1]],
      skipped: 1,
      available: true,
      entries: expect.objectContaining({ complete: true }),
    });
  });

  it('drops every row when all are already present, without erroring on the empty result', async () => {
    const rows: Row[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU' },
      { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
    ];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush(page(['7tv-1', '7tv-2']));

    expect(await result$).toEqual({
      rows: [],
      skipped: 2,
      available: true,
      entries: expect.objectContaining({ complete: true }),
    });
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

    expect(await result$).toEqual({
      rows,
      skipped: 0,
      available: true,
      entries: expect.objectContaining({ complete: true }),
    });
  });

  it('walks every page up to the reported page count', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-999', name: 'Target' }];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));

    const first = httpMock.expectOne(GQL_ENDPOINT);
    expect(first.request.body.variables).toEqual({ id: 'target-set', page: 1, perPage: 500 });
    first.flush(page(['7tv-1'], 1, 2, 2));

    const second = httpMock.expectOne(GQL_ENDPOINT);
    expect(second.request.body.variables).toEqual({ id: 'target-set', page: 2, perPage: 500 });
    second.flush(page(['7tv-999'], 2, 2, 2));

    expect(await result$).toEqual({
      rows: [],
      skipped: 1,
      available: true,
      entries: {
        aliasesById: new Map([
          ['7tv-1', []],
          ['7tv-999', []],
        ]),
        aliaslessIds: new Set(['7tv-1', '7tv-999']),
        defaultNameById: new Map([
          ['7tv-1', ''],
          ['7tv-999', ''],
        ]),
        animatedById: new Map([
          ['7tv-1', false],
          ['7tv-999', false],
        ]),
        occupiedSlots: 2,
        complete: true,
      },
    });
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

    expect(await result$).toEqual({ rows, skipped: 0, available: false, entries: null });
  });

  it('fails open on a GraphQL-level rejection disguised as HTTP 200', async () => {
    const rows: Row[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU' }];

    const result$ = firstValueFrom(filterAlreadyPresent(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'unknown set' }] });

    expect(await result$).toEqual({ rows, skipped: 0, available: false, entries: null });
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

    expect(checked).toEqual({
      rows,
      skipped: 0,
      available: true,
      entries: expect.objectContaining({ complete: true }),
    });
    expect(unverified).toEqual({ rows, skipped: 0, available: false, entries: null });
    expect(checked.available).not.toBe(unverified.available);
  });
});

/** A page of set entries with their aliases — what the restore variant reads. `alias` omitted
 *  models a 7TV entry without one (spec §37/§38's "aliasless" rule). */
function entriesPage(entries: { id: string; alias?: string }[]) {
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
  aliases?: (string | null)[];
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

  async function run(rows: RestoreRow[], entries: { id: string; alias?: string }[]) {
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

    expect(await run([row], [{ id: '7tv-other', alias: 'Z' }])).toEqual({
      rows: [row],
      skipped: 0,
      skippedNameTaken: 0,
      available: true,
      complete: true,
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
    ).toEqual({ rows: [], skipped: 2, skippedNameTaken: 0, available: true, complete: true });
  });

  // The partial retry the id-only check made impossible: A came back, B failed — re-running the
  // protocol must add B, and only B.
  it('keeps only the missing aliases of a row whose id is present under some of its own aliases', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(await run([row], [{ id: '7tv-1', alias: 'A' }])).toEqual({
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['B'] }],
      skipped: 1,
      skippedNameTaken: 0,
      available: true,
      complete: true,
    });
  });

  // #149: 7TV's addEmote only rejects a colliding alias string — re-adding A or B next to C would
  // enter the same emote a second time.
  it('drops the whole row when its id is present under an alias the row does not name', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(await run([row], [{ id: '7tv-1', alias: 'C' }])).toEqual({
      rows: [],
      skipped: 2,
      skippedNameTaken: 0,
      available: true,
      complete: true,
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
    ).toEqual({ rows: [], skipped: 2, skippedNameTaken: 0, available: true, complete: true });
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
    ).toEqual({ rows: [], skipped: 2, skippedNameTaken: 0, available: true, complete: true });
  });

  it('compares aliases exactly, so a case-only difference counts as a foreign alias', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'pogu', aliases: ['pogu'] };

    expect(await run([row], [{ id: '7tv-1', alias: 'PogU' }])).toEqual({
      rows: [],
      skipped: 1,
      skippedNameTaken: 0,
      available: true,
      complete: true,
    });
  });

  it('fails open on a failed fetch, like the id-only check', async () => {
    const rows: RestoreRow[] = [{ sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] }];

    const result$ = firstValueFrom(filterAlreadyPresentForRestore(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).error(new ProgressEvent('network error'));

    expect(await result$).toEqual({
      rows,
      skipped: 0,
      skippedNameTaken: 0,
      available: false,
      complete: false,
    });
  });

  // K5 fix round, spec §37/§38: an aliasless 7TV entry occupies a slot the row can never name, so
  // it takes rule 2 (drop the whole row) even when the *same id* also has an aliased entry the row
  // does name — the aliasless entry must not be silently absorbed by its aliased sibling.
  it('drops the whole row when the id also carries an aliasless entry, even though the id is present under the alias the row names', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] };

    expect(await run([row], [{ id: '7tv-1', alias: 'PogU' }, { id: '7tv-1' }])).toEqual({
      rows: [],
      skipped: 1,
      skippedNameTaken: 0,
      available: true,
      complete: true,
    });
  });

  it('drops a row whose id is present only as an aliasless entry', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] };

    expect(await run([row], [{ id: '7tv-1' }])).toEqual({
      rows: [],
      skipped: 1,
      skippedNameTaken: 0,
      available: true,
      complete: true,
    });
  });

  // A removed transfer target restores its aliasless entry as `null`: present when the id has a
  // live aliasless entry, missing otherwise — never read as a foreign entry of its own row.
  it('skips a null alias whose aliasless entry is back, and keeps it while the entry is missing', async () => {
    const back: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'PogDefault', aliases: [null] };
    const gone: RestoreRow = { sevenTvEmoteId: '7tv-2', name: 'KappaDefault', aliases: [null] };
    const halfBack: RestoreRow = { sevenTvEmoteId: '7tv-3', name: 'LUL', aliases: ['LUL', null] };

    expect(
      await run([back, gone, halfBack], [{ id: '7tv-1' }, { id: '7tv-3', alias: 'LUL' }]),
    ).toEqual({
      rows: [gone, { ...halfBack, aliases: [null] }],
      skipped: 2,
      skippedNameTaken: 0,
      available: true,
      complete: true,
    });
  });

  // Rule 4: a name another emote holds now cannot come back — its ADD could only end in 7TV's name
  // conflict. Counted apart from "already present".
  it('strikes an alias another emote now holds from the row, counted as name taken', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(await run([row], [{ id: '7tv-other', alias: 'A' }])).toEqual({
      rows: [{ ...row, aliases: ['B'] }],
      skipped: 0,
      skippedNameTaken: 1,
      available: true,
      complete: true,
    });
  });

  it('drops the row when another emote holds every one of its aliases', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A', 'B'] };

    expect(
      await run(
        [row],
        [
          { id: '7tv-other', alias: 'A' },
          { id: '7tv-third', alias: 'B' },
        ],
      ),
    ).toEqual({ rows: [], skipped: 0, skippedNameTaken: 2, available: true, complete: true });
  });

  // The "replace target succeeded" case of a transfer-run file: the source emote holds the target's
  // old name now, its aliasless entry is gone with it. One ADD without an alias goes out, and every
  // entry of the input is accounted for — sent, already present or name taken, never lost.
  it('restores only the missing aliasless entry of a row whose named alias is taken, and counts every entry', async () => {
    const rows: RestoreRow[] = [
      { sevenTvEmoteId: 'tgt-1', name: 'Kappa', aliases: ['Kappa', null] },
      { sevenTvEmoteId: 'tgt-2', name: 'Pog', aliases: ['Pog', 'PogAlt', null] },
    ];
    const live = [
      { id: 'src-1', alias: 'Kappa' },
      { id: 'tgt-2', alias: 'Pog' },
      { id: 'src-2', alias: 'PogAlt' },
    ];

    const result = await run(rows, live);

    expect(result).toEqual({
      rows: [
        { sevenTvEmoteId: 'tgt-1', name: 'Kappa', aliases: [null] },
        { sevenTvEmoteId: 'tgt-2', name: 'Pog', aliases: [null] },
      ],
      skipped: 1,
      skippedNameTaken: 2,
      available: true,
      complete: true,
    });
    const sent = result.rows.reduce((sum, row) => sum + (row.aliases?.length ?? 1), 0);
    const input = rows.reduce((sum, row) => sum + (row.aliases?.length ?? 1), 0);
    expect(sent + result.skipped + result.skippedNameTaken).toBe(input);
  });

  // K5 stays for every row that does not name an aliasless entry itself — a purge-run row never
  // does, so the live aliasless entry is one the row cannot vouch for.
  it('still treats a live aliasless entry as foreign for a row without a null alias', async () => {
    const purgeRow: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] };
    const transferRow: RestoreRow = {
      sevenTvEmoteId: '7tv-1',
      name: 'PogU',
      aliases: ['PogU', null],
    };

    expect(await run([purgeRow], [{ id: '7tv-1' }])).toEqual({
      rows: [],
      skipped: 1,
      skippedNameTaken: 0,
      available: true,
      complete: true,
    });
    expect(await run([transferRow], [{ id: '7tv-1' }])).toEqual({
      rows: [{ ...transferRow, aliases: ['PogU'] }],
      skipped: 1,
      skippedNameTaken: 0,
      available: true,
      complete: true,
    });
  });

  // K5 fix round: `complete: false` (a truncated read) is deliberately not a reason to fail open
  // here — the per-alias comparison still applies to whatever the (partial) read did see, which
  // skips strictly more genuine duplicates than discarding the read entirely would. Restore only
  // fails open on an actual fetch/GraphQL error (see the test above), never on an incomplete one.
  // #255 P2, Codex review: `complete: false` is still surfaced on the result even though filtering
  // does not change because of it — a caller that turns this into an exact-sounding count needs to
  // tell "verified against the whole set" apart from "verified against only part of it".
  it('still filters against a read that hit the runaway guard, rather than failing the whole check open, but reports the result as incomplete', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'A', aliases: ['A'] };
    const result$ = firstValueFrom(filterAlreadyPresentForRestore(httpClient, 'target-set', [row]));

    for (let requested = 1; requested <= 10; requested++) {
      const req = httpMock.expectOne(GQL_ENDPOINT);
      if (requested === 1) {
        req.flush({
          data: {
            emoteSets: {
              emoteSet: {
                emotes: {
                  totalCount: 999,
                  pageCount: 11,
                  items: [{ alias: 'A', emote: { id: '7tv-1' } }],
                },
              },
            },
          },
        });
      } else {
        req.flush({
          data: {
            emoteSets: {
              emoteSet: { emotes: { totalCount: 999, pageCount: 11, items: [] } },
            },
          },
        });
      }
    }

    // The row's own id was seen (and matched) on the very first, well within-guard page — the
    // truncation happened later, for ids this row never needed to know about.
    expect(await result$).toEqual({
      rows: [],
      skipped: 1,
      skippedNameTaken: 0,
      available: true,
      complete: false,
    });
  });
});

// Operator decision 2026-09-25 (#255, "Slot-Zahl nach dem Skip-Filter"): the restore
// confirmation's own numbers, derived over `filterAlreadyPresentForRestore`'s result rather than
// each call site re-deriving them.
describe('loadRestoreConfirmPreview', () => {
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

  async function run(rows: RestoreRow[], entries: { id: string; alias?: string }[]) {
    const result$ = firstValueFrom(loadRestoreConfirmPreview(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).flush(entriesPage(entries));
    return result$;
  }

  it('names and counts every row when nothing in the target set collides', async () => {
    const rows: RestoreRow[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] },
      { sevenTvEmoteId: '7tv-2', name: 'Kappa', aliases: ['Kappa', 'KappaAlt'] },
    ];

    const result = await run(rows, []);

    expect(result.names).toEqual(['PogU', 'Kappa']);
    // spec #200, 7.2: ADDs, one per alias — Kappa's duplicate cell counts as two.
    expect(result.addCount).toBe(3);
    expect(result.available).toBe(true);
  });

  it('drops an already-present row from both names and addCount, not only from rows', async () => {
    const rows: RestoreRow[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] },
      { sevenTvEmoteId: '7tv-2', name: 'Kappa', aliases: ['Kappa'] },
    ];

    const result = await run(rows, [{ id: '7tv-1', alias: 'PogU' }]);

    expect(result.names).toEqual(['Kappa']);
    expect(result.addCount).toBe(1);
    expect(result.skipped).toBe(1);
  });

  it('counts only the surviving aliases of a partially-present duplicate cell', async () => {
    const rows: RestoreRow[] = [
      { sevenTvEmoteId: '7tv-1', name: 'Kappa', aliases: ['Kappa', 'KappaAlt'] },
    ];

    const result = await run(rows, [{ id: '7tv-1', alias: 'Kappa' }]);

    expect(result.names).toEqual(['Kappa']);
    expect(result.addCount).toBe(1);
    expect(result.skipped).toBe(1);
  });

  it('reports every row unfiltered, with available false, when the read fails', async () => {
    const rows: RestoreRow[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }];

    const result$ = firstValueFrom(loadRestoreConfirmPreview(httpClient, 'target-set', rows));
    httpMock.expectOne(GQL_ENDPOINT).error(new ProgressEvent('error'));
    const result = await result$;

    expect(result.names).toEqual(['PogU']);
    expect(result.addCount).toBe(1);
    expect(result.available).toBe(false);
  });

  it('reports an empty preview once every row is filtered out', async () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] };

    const result = await run([row], [{ id: '7tv-1', alias: 'PogU' }]);

    expect(result.rows).toEqual([]);
    expect(result.names).toEqual([]);
    expect(result.addCount).toBe(0);
    expect(result.skipped).toBe(1);
    expect(result.available).toBe(true);
  });

  // #255 P2, Codex review: a read that stopped at the runaway guard or a `totalCount` mismatch
  // still filters the rows it did see — `names`/`addCount` are not thrown away — but reports
  // `complete: false` so a caller (the restore confirmation dialogs) can hedge the wording instead
  // of claiming an exact number a partial read never verified.
  it('still names and counts the surviving rows, but reports complete: false, when the read is truncated', async () => {
    const rows: RestoreRow[] = [{ sevenTvEmoteId: '7tv-2', name: 'Kappa', aliases: ['Kappa'] }];
    const result$ = firstValueFrom(loadRestoreConfirmPreview(httpClient, 'target-set', rows));

    // Single page, but `totalCount` claims more than this page delivered — the K5 mismatch guard,
    // not the 10-page runaway one, but the same `complete: false` outcome.
    httpMock.expectOne(GQL_ENDPOINT).flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: 2,
              pageCount: 1,
              items: [{ alias: 'PogU', emote: { id: '7tv-1' } }],
            },
          },
        },
      },
    });
    const result = await result$;

    expect(result.available).toBe(true);
    expect(result.complete).toBe(false);
    expect(result.names).toEqual(['Kappa']);
    expect(result.addCount).toBe(1);
    expect(result.skipped).toBe(0);
  });
});

// #255 P2a: the "could not verify" shape a caller builds by hand when its own wrapping `timeout`
// fires before `loadRestoreConfirmPreview`'s read does — a timeout error lands outside that
// function's own `catchError`, so nothing inside it ever gets a chance to build this.
describe('restoreConfirmPreviewUnavailable', () => {
  it('passes every row through unfiltered, names and counts them all, and reports the check as unavailable', () => {
    const rows: RestoreRow[] = [
      { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] },
      { sevenTvEmoteId: '7tv-2', name: 'Kappa', aliases: ['Kappa', 'KappaAlt'] },
    ];

    const result = restoreConfirmPreviewUnavailable(rows);

    expect(result.rows).toEqual(rows);
    expect(result.names).toEqual(['PogU', 'Kappa']);
    // ADDs, one per alias — same accounting as `loadRestoreConfirmPreview`'s successful path.
    expect(result.addCount).toBe(3);
    expect(result.skipped).toBe(0);
    expect(result.skippedNameTaken).toBe(0);
    expect(result.available).toBe(false);
    expect(result.complete).toBe(false);
  });

  it('returns a defensive copy of rows, not the same array reference', () => {
    const rows: RestoreRow[] = [{ sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }];

    expect(restoreConfirmPreviewUnavailable(rows).rows).not.toBe(rows);
  });

  it('counts a bare row (no aliases) as one ADD under its own name, like the successful path does', () => {
    const row: RestoreRow = { sevenTvEmoteId: '7tv-1', name: 'PogU' };

    const result = restoreConfirmPreviewUnavailable([row]);

    expect(result.names).toEqual(['PogU']);
    expect(result.addCount).toBe(1);
  });
});

/** A completed read holding exactly `entries`; `alias: null` is an entry without an alias. */
function setEntries(entries: { id: string; alias: string | null }[]): SevenTvSetEntries {
  const aliasesById = new Map<string, string[]>();
  const aliaslessIds = new Set<string>();
  for (const entry of entries) {
    const aliases = aliasesById.get(entry.id) ?? [];
    if (entry.alias === null) {
      aliaslessIds.add(entry.id);
    } else {
      aliases.push(entry.alias);
    }
    aliasesById.set(entry.id, aliases);
  }
  return {
    aliasesById,
    aliaslessIds,
    defaultNameById: new Map(),
    animatedById: new Map(),
    occupiedSlots: entries.length,
    complete: true,
  };
}

const SOURCE: ImportRow = { sevenTvEmoteId: 'src-1', name: 'Kappa', imageUrl: null };

/** A replace row for `SOURCE` against target `tgt-1`, confirmed with `aliases` and, optionally, an
 *  aliasless entry. */
function replacePlan(aliases: string[], hasAliaslessEntry = false): TransferPlan {
  const row: TransferRow = {
    action: 'replace',
    source: SOURCE,
    alias: 'Kappa',
    target: { sevenTvEmoteId: 'tgt-1', aliases, hasAliaslessEntry, defaultName: null },
  };
  return {
    rows: [row, { action: 'add', source: { ...SOURCE, sevenTvEmoteId: 'src-2' }, alias: 'Other' }],
  };
}

describe('verifyReplaceTargets', () => {
  it('passes a replace row whose target matches the confirmed state entry for entry', () => {
    const entries = setEntries([
      { id: 'tgt-1', alias: 'Kappa' },
      { id: 'tgt-1', alias: 'KappaDup' },
      { id: 'other', alias: 'Other' },
    ]);

    expect(verifyReplaceTargets(entries, replacePlan(['KappaDup', 'Kappa']))).toEqual({
      available: true,
      drifted: [],
    });
  });

  it('reports a target that gained a named alias, with its live entries for the overlay', () => {
    const entries = setEntries([
      { id: 'tgt-1', alias: 'Kappa' },
      { id: 'tgt-1', alias: 'KappaNew' },
    ]);

    expect(verifyReplaceTargets(entries, replacePlan(['Kappa']))).toEqual({
      available: true,
      drifted: [
        {
          key: 'src-1',
          reason: 'aliasesChanged',
          live: { aliases: ['Kappa', 'KappaNew'], hasAliaslessEntry: false },
        },
      ],
    });
  });

  it('reports a target whose colliding name now belongs to another emote', () => {
    const entries = setEntries([
      { id: 'tgt-1', alias: 'KappaRenamed' },
      { id: 'someone-else', alias: 'Kappa' },
    ]);

    expect(verifyReplaceTargets(entries, replacePlan(['Kappa']))).toEqual({
      available: true,
      drifted: [
        {
          key: 'src-1',
          reason: 'nameHeldElsewhere',
          live: { aliases: ['KappaRenamed'], hasAliaslessEntry: false },
        },
      ],
    });
  });

  it('compares the aliasless entry too: equal when the plan knows it, drifted when it is new or gone', () => {
    const mixed = setEntries([
      { id: 'tgt-1', alias: 'Kappa' },
      { id: 'tgt-1', alias: null },
    ]);
    const namedOnly = setEntries([{ id: 'tgt-1', alias: 'Kappa' }]);

    expect(verifyReplaceTargets(mixed, replacePlan(['Kappa'], true))).toEqual({
      available: true,
      drifted: [],
    });
    // New since the confirmation: the REMOVE would take an entry the user never saw.
    expect(verifyReplaceTargets(mixed, replacePlan(['Kappa'], false))).toEqual({
      available: true,
      drifted: [
        {
          key: 'src-1',
          reason: 'aliaslessEntryChanged',
          live: { aliases: ['Kappa'], hasAliaslessEntry: true },
        },
      ],
    });
    // Gone since the confirmation.
    expect(verifyReplaceTargets(namedOnly, replacePlan(['Kappa'], true))).toEqual({
      available: true,
      drifted: [
        {
          key: 'src-1',
          reason: 'aliaslessEntryChanged',
          live: { aliases: ['Kappa'], hasAliaslessEntry: false },
        },
      ],
    });
  });

  it('lets no replace row through on an incomplete read, and reports a vanished target without live entries', () => {
    const incomplete = { ...setEntries([{ id: 'tgt-1', alias: 'Kappa' }]), complete: false };
    const gone = setEntries([{ id: 'other', alias: 'Other' }]);

    expect(verifyReplaceTargets(incomplete, replacePlan(['Kappa']))).toEqual({ available: false });
    expect(verifyReplaceTargets(gone, replacePlan(['Kappa']))).toEqual({
      available: true,
      drifted: [{ key: 'src-1', reason: 'targetGone', live: null }],
    });
  });
});

describe('stampReplaceTargets', () => {
  it('gives each replace target the read’s aliases in the read’s order, its aliasless entry and its default name', () => {
    const entries = setEntries([
      { id: 'tgt-1', alias: 'KappaDup' },
      { id: 'tgt-1', alias: 'Kappa' },
      { id: 'tgt-1', alias: null },
    ]);
    entries.defaultNameById.set('tgt-1', 'KappaDefault');
    const plan = replacePlan(['Kappa', 'KappaDup'], true);

    const stamped = stampReplaceTargets(plan, entries);

    expect(stamped.rows[0]).toEqual({
      ...plan.rows[0],
      target: {
        sevenTvEmoteId: 'tgt-1',
        aliases: ['KappaDup', 'Kappa'],
        hasAliaslessEntry: true,
        defaultName: 'KappaDefault',
      },
    });
    // Rows that touch no target entry leave unchanged.
    expect(stamped.rows[1]).toBe(plan.rows[1]);
    // The input plan is not mutated.
    expect(plan.rows[0].action === 'replace' && plan.rows[0].target.defaultName).toBeNull();
  });

  it('keeps the confirmed aliases and names no default when the read does not know the target', () => {
    const stamped = stampReplaceTargets(replacePlan(['Kappa']), setEntries([]));

    expect(stamped.rows[0]).toMatchObject({
      target: { aliases: ['Kappa'], hasAliaslessEntry: false, defaultName: null },
    });
  });
});
