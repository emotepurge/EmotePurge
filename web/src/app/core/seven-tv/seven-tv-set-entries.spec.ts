import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { loadSevenTvSetEntries } from './seven-tv-set-entries';

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';

function page(entries: { id: string; alias?: string }[], pageCount = 1) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: entries.length,
            pageCount,
            items: entries.map(({ id, alias }) => ({ alias, emote: { id } })),
          },
        },
      },
    },
  };
}

describe('loadSevenTvSetEntries', () => {
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

  // #74: the same emote entered twice under two names — one id, both aliases.
  it('groups a duplicated emote under one id with every alias it sits under', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush(
      page([
        { id: '7tv-1', alias: 'PogU' },
        { id: '7tv-2', alias: 'KEKW' },
        { id: '7tv-1', alias: 'PogU2' },
      ]),
    );

    const result = await result$;
    expect(result.complete).toBe(true);
    expect([...result.aliasesById]).toEqual([
      ['7tv-1', ['PogU', 'PogU2']],
      ['7tv-2', ['KEKW']],
    ]);
  });

  it('collects entries across pages', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush(page([{ id: '7tv-1', alias: 'A' }], 2));
    httpMock.expectOne(GQL_ENDPOINT).flush(page([{ id: '7tv-1', alias: 'B' }], 2));

    expect((await result$).aliasesById.get('7tv-1')).toEqual(['A', 'B']);
  });

  it('reports an incomplete read when the runaway guard stops it while 7TV promises more', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    for (let requested = 1; requested <= 10; requested++) {
      const req = httpMock.expectOne(GQL_ENDPOINT);
      expect(req.request.body.variables.page).toBe(requested);
      req.flush(page([], 11));
    }

    expect((await result$).complete).toBe(false);
  });

  it('reports a read that ends exactly on the tenth page as complete', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    for (let requested = 1; requested <= 10; requested++) {
      const req = httpMock.expectOne(GQL_ENDPOINT);
      expect(req.request.body.variables.page).toBe(requested);
      req.flush(page([], 10));
    }

    expect((await result$).complete).toBe(true);
  });

  // K5 fix round: `complete` used to reflect only the 10-page runaway guard — pagination that ends
  // "normally" (the last page reports itself as the last one) but under-collects against the
  // query's own `totalCount` looked complete too, even though offset pagination shifting between
  // two fetches of the same set can silently drop (or duplicate) an entry at a page boundary.
  it('reports an incomplete read when the last page ends pagination but the collected count does not match totalCount', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: 3,
              pageCount: 1,
              items: [{ emote: { id: '7tv-1' } }],
            },
          },
        },
      },
    });

    expect((await result$).complete).toBe(false);
  });

  it('reports a complete read across pages when the cumulative count matches totalCount', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: { totalCount: 2, pageCount: 2, items: [{ emote: { id: '7tv-1' } }] },
          },
        },
      },
    });
    httpMock.expectOne(GQL_ENDPOINT).flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: { totalCount: 2, pageCount: 2, items: [{ emote: { id: '7tv-2' } }] },
          },
        },
      },
    });

    expect((await result$).complete).toBe(true);
  });

  // K5 fix round, spec §37/§38: an id that carries both an aliased and an aliasless entry must not
  // lose the aliasless one just because `aliasesById` already has a (non-empty) entry for that id.
  it('tracks which ids carry an aliasless entry, even alongside an aliased entry of the same id', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock
      .expectOne(GQL_ENDPOINT)
      .flush(page([{ id: '7tv-1', alias: 'PogU' }, { id: '7tv-1' }, { id: '7tv-2' }]));

    const result = await result$;
    expect(result.aliaslessIds).toEqual(new Set(['7tv-1', '7tv-2']));
    expect(result.aliasesById.get('7tv-1')).toEqual(['PogU']);
    expect(result.aliasesById.get('7tv-2')).toEqual([]);
  });

  it('errors on a GraphQL-level rejection disguised as HTTP 200', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'rate limited' }] });

    await expect(result$).rejects.toThrow();
  });
});
