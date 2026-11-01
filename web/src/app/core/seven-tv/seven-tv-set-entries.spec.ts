import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { SevenTvSetReadError, loadSevenTvSetEntries } from './seven-tv-set-entries';
import {
  GAP_FIXTURE_TOTAL_COUNT,
  gapRead496of494Pages,
  gapRead496of494Response,
  hiddenEntries,
  setEntriesResponse,
  visibleEntries,
} from './seven-tv-set-entries.testing';

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';

function page(
  entries: {
    id: string;
    alias?: string;
    defaultName?: string;
    animated?: boolean;
    addedAt?: string | null;
  }[],
  pageCount = 1,
  totalCount = entries.length,
) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount,
            pageCount,
            items: entries.map(({ id, alias, defaultName, animated, addedAt }) => ({
              alias,
              ...(addedAt === undefined ? {} : { addedAt }),
              emote: {
                id,
                defaultName,
                ...(animated === undefined ? {} : { flags: { animated } }),
              },
            })),
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
    // Pages 1..9 are re-read for the shift check.
    for (let requested = 1; requested <= 9; requested++) {
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
    httpMock.expectOne(GQL_ENDPOINT).flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: { totalCount: 2, pageCount: 2, items: [{ emote: { id: '7tv-1' } }] },
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

  // The transfer-run protocol names an aliasless entry by its 7TV default name.
  it("captures each id's 7TV default name, falling back to '' when 7TV omits it", async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock
      .expectOne(GQL_ENDPOINT)
      .flush(page([{ id: '7tv-1', alias: 'PogU', defaultName: 'Pog' }, { id: '7tv-2' }]));

    const result = await result$;
    expect(result.defaultNameById.get('7tv-1')).toBe('Pog');
    expect(result.defaultNameById.get('7tv-2')).toBe('');
  });

  // #254 spec 17 K1: the undo confirm dialog builds a source's still image url from this flag.
  it("captures each id's animated flag from emote.flags.animated", async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    const request = httpMock.expectOne(GQL_ENDPOINT);
    expect(request.request.body.query).toContain('flags { animated }');
    request.flush(
      page([
        { id: '7tv-1', alias: 'PogU', animated: true },
        { id: '7tv-2', alias: 'KEKW', animated: false },
      ]),
    );

    const result = await result$;
    expect([...result.animatedById]).toEqual([
      ['7tv-1', true],
      ['7tv-2', false],
    ]);
  });

  // Same guard as the backend's `dto.Emote?.Flags?.Animated ?? false`: 4x.webp exists for every
  // emote, 4x_static.webp only for an animated one.
  it('records a missing animated flag as false', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush(page([{ id: '7tv-1', alias: 'PogU' }]));

    const result = await result$;
    expect(result.animatedById.get('7tv-1')).toBe(false);
  });

  // Spec #255: the import confirm dialog's live slot projection reads this straight off the read,
  // independent of `complete` — 7TV's own `totalCount` is authoritative regardless of whether this
  // particular read's own page walk collected every entry.
  it("reports the set's own totalCount as occupiedSlots, on a single page and across pages alike", async () => {
    const single$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush(page([{ id: '7tv-1', alias: 'PogU' }]));
    expect((await single$).occupiedSlots).toBe(1);

    const paged$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: { totalCount: 5, pageCount: 2, items: [{ emote: { id: '7tv-1' } }] },
          },
        },
      },
    });
    httpMock.expectOne(GQL_ENDPOINT).flush({
      data: {
        emoteSets: {
          emoteSet: {
            emotes: { totalCount: 5, pageCount: 2, items: [{ emote: { id: '7tv-2' } }] },
          },
        },
      },
    });
    expect((await paged$).occupiedSlots).toBe(5);
  });

  // The runaway guard reports `complete: false` but still carries 7TV's own totalCount — the guard
  // is about this read's own page walk, not about whether the number 7TV reported is trustworthy.
  it('still reports occupiedSlots when the runaway guard stops the read incomplete', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    for (let requested = 1; requested <= 10; requested++) {
      httpMock.expectOne(GQL_ENDPOINT).flush(page([], 11, 200));
    }

    const result = await result$;
    expect(result.complete).toBe(false);
    expect(result.occupiedSlots).toBe(200);
  });

  it('errors on a GraphQL-level rejection disguised as HTTP 200', async () => {
    const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'rate limited' }] });

    await expect(result$).rejects.toThrow();
  });

  /*
   * #454 A3 step 1: the slot delete's binding read must tell 7TV's rate limit apart from any other
   * failed read, so it can wait instead of counting a failure.
   * protects: the rate-limit numbers survive from 7TV's answer to the caller; the thrown value stays
   *   an `Error` for every caller that catches a failed read.
   * fails_when: a rate-limit answer loses its reset, another GraphQL error claims a rate limit, or
   *   the error stops being an `Error`.
   * no_claim: header names beyond the parsed numbers; how the delete run uses them.
   */
  describe('a GraphQL-level rejection', () => {
    it("carries 7TV's rate-limit numbers when the rejection is its rate limit", async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      httpMock.expectOne(GQL_ENDPOINT).flush({
        errors: [
          {
            message: 'RATE_LIMIT_EXCEEDED rate limit exceeded',
            extensions: {
              code: 'RATE_LIMIT_EXCEEDED',
              status: 429,
              headers: {
                'x-ratelimit-emote_set_change-reset': '7',
                'x-ratelimit-emote_set_change-limit': '100',
              },
            },
          },
        ],
      });

      const error = await result$.catch((caught: unknown) => caught);
      expect(error).toBeInstanceOf(Error);
      expect(error).toBeInstanceOf(SevenTvSetReadError);
      expect((error as SevenTvSetReadError).rateLimit).toEqual({
        limit: 100,
        remaining: null,
        reset: 7,
        used: null,
      });
    });

    it('carries no rate limit for any other rejection, or an answer without the entries', async () => {
      const rejected$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      httpMock
        .expectOne(GQL_ENDPOINT)
        .flush({ errors: [{ message: 'unknown set', extensions: { code: 'NOT_FOUND' } }] });
      const empty$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      httpMock.expectOne(GQL_ENDPOINT).flush({ data: { emoteSets: { emoteSet: null } } });

      for (const result$ of [rejected$, empty$]) {
        const error = await result$.catch((caught: unknown) => caught);
        expect(error).toBeInstanceOf(Error);
        expect((error as SevenTvSetReadError).rateLimit).toBeNull();
      }
    });
  });

  // Plan-378 4.1 [E2]: the add date is read only when asked for, so every other reader (import,
  // undo, the id-row delete run) keeps today's query and result.
  describe('with the add date on request', () => {
    const DATE_1 = '2026-10-13T08:00:14.902Z';
    const DATE_2 = '2026-10-13T09:30:00Z';

    it("sends today's query and returns no entry list without the option", async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      const request = httpMock.expectOne(GQL_ENDPOINT);
      request.flush(page([{ id: '7tv-1', alias: 'A', addedAt: DATE_1 }]));

      expect((request.request.body as { query: string }).query).not.toContain('addedAt');
      expect((await result$).entriesById).toBeUndefined();
    });

    it('selects addedAt and lists every entry per id with alias and date, in 7TV order', async () => {
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true }),
      );
      const request = httpMock.expectOne(GQL_ENDPOINT);
      request.flush(
        page([
          { id: '7tv-1', alias: 'hob44', addedAt: DATE_1 },
          { id: '7tv-2', alias: 'KEKW', addedAt: DATE_2 },
          { id: '7tv-1', alias: 'ome44', addedAt: DATE_2 },
          { id: '7tv-1', addedAt: DATE_2 },
        ]),
      );

      expect((request.request.body as { query: string }).query).toContain('alias addedAt emote');
      const result = await result$;
      expect(result.entriesById?.get('7tv-1')).toEqual([
        { alias: 'hob44', addedAt: DATE_1 },
        { alias: 'ome44', addedAt: DATE_2 },
        { alias: null, addedAt: DATE_2 },
      ]);
      expect(result.entriesById?.get('7tv-2')).toEqual([{ alias: 'KEKW', addedAt: DATE_2 }]);
      // The existing fields keep their meaning.
      expect(result.aliasesById.get('7tv-1')).toEqual(['hob44', 'ome44']);
      expect(result.aliaslessIds).toEqual(new Set(['7tv-1']));
    });

    it('records a missing or null date as null', async () => {
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true }),
      );
      httpMock.expectOne(GQL_ENDPOINT).flush(
        page([
          { id: '7tv-1', alias: 'A' },
          { id: '7tv-1', alias: 'B', addedAt: null },
        ]),
      );

      expect((await result$).entriesById?.get('7tv-1')).toEqual([
        { alias: 'A', addedAt: null },
        { alias: 'B', addedAt: null },
      ]);
    });

    it('judges completeness by the same rules as without the option', async () => {
      const mismatch$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true }),
      );
      httpMock
        .expectOne(GQL_ENDPOINT)
        .flush(page([{ id: '7tv-1', alias: 'A', addedAt: DATE_1 }], 1, 3));
      expect((await mismatch$).complete).toBe(false);

      const clean$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true }),
      );
      httpMock.expectOne(GQL_ENDPOINT).flush(page([{ id: '7tv-1', alias: 'A', addedAt: DATE_1 }]));
      expect((await clean$).complete).toBe(true);
    });

    // S4: two entries of one id swap their aliases between the main pass and the re-read. The
    // (id, alias) set is unchanged; only the generations moved.
    describe('an alias swap between the main pass and the verification re-read', () => {
      function swapped(withAddedAt: boolean) {
        const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt }));
        const answer = (first: string, second: string, pageNumber: number) => {
          const request = httpMock.expectOne(GQL_ENDPOINT);
          expect(request.request.body.variables.page).toBe(pageNumber);
          request.flush(
            pageNumber === 1
              ? page(
                  [
                    { id: 'x', alias: 'A', addedAt: first },
                    { id: 'x', alias: 'B', addedAt: second },
                  ],
                  2,
                  3,
                )
              : page([{ id: 'y', alias: 'Y', addedAt: DATE_2 }], 2, 3),
          );
        };
        answer(DATE_1, DATE_2, 1);
        answer(DATE_1, DATE_2, 2);
        answer(DATE_2, DATE_1, 1);
        return result$;
      }

      it('makes a read with dates incomplete', async () => {
        expect((await swapped(true)).complete).toBe(false);
      });

      it('leaves a read without dates complete, as before', async () => {
        expect((await swapped(false)).complete).toBe(true);
      });
    });
  });

  // Another editor changing the set between two page requests shifts entries across the page
  // boundary (offset paging). `flushPages` answers the main pass (and optionally the re-reads of
  // every page but the last) in order.
  describe('entries shifting between page requests', () => {
    const A = { id: 'a', alias: 'A' };
    const B = { id: 'b', alias: 'B' };
    const C = { id: 'c', alias: 'C' };
    const D = { id: 'd', alias: 'D' };
    const E = { id: 'e', alias: 'E' };

    function flushNext(response: ReturnType<typeof page>, expectedPage: number) {
      const req = httpMock.expectOne(GQL_ENDPOINT);
      expect(req.request.body.variables.page).toBe(expectedPage);
      req.flush(response);
    }

    it('is incomplete when an insert before the boundary repeats the last entry of page 1 on page 2', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([B, C], 2, 5), 2);

      expect((await result$).complete).toBe(false);
    });

    it('is incomplete when a removal before the boundary skips an entry, though the count matches', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([D], 2, 3), 2);

      expect((await result$).complete).toBe(false);
    });

    it('is incomplete without re-reads when an insert and a removal leave a duplicate pair and an equal totalCount', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([B, D], 2, 4), 2);

      expect((await result$).complete).toBe(false);
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('detects a swap (remove on page 1, append at the end) only through the re-read', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([C, D], 2, 4), 2);
      // A was removed, E appended: page 1 is now [B, C] — C slid over and was never read.
      flushNext(page([B, C], 2, 4), 1);

      expect((await result$).complete).toBe(false);
    });

    it('is incomplete when totalCount changes during the verification re-read', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([C, D], 2, 4), 2);
      flushNext(page([A, B], 2, 5), 1);

      expect((await result$).complete).toBe(false);
    });

    it('is incomplete when totalCount differs between pages of the main pass', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 5), 1);
      // The count matches the last page's totalCount, so only the drift can flag this read.
      flushNext(page([C, D], 2, 4), 2);

      expect((await result$).complete).toBe(false);
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('is incomplete when only page 2 of three differs on the re-read', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 3, 6), 1);
      flushNext(page([C, D], 3, 6), 2);
      flushNext(page([E, { id: 'f', alias: 'F' }], 3, 6), 3);
      flushNext(page([A, B], 3, 6), 1);
      flushNext(page([C, E], 3, 6), 2);

      expect((await result$).complete).toBe(false);
    });

    it('stops re-reading once totalCount drifts during the verification', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 3, 6), 1);
      flushNext(page([C, D], 3, 6), 2);
      flushNext(page([E, { id: 'f', alias: 'F' }], 3, 6), 3);
      flushNext(page([A, B], 3, 7), 1);

      expect((await result$).complete).toBe(false);
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('starts fresh on every subscription of the same observable', async () => {
      const read$ = loadSevenTvSetEntries(httpClient, 'set-1');
      for (let run = 0; run < 2; run++) {
        const result$ = firstValueFrom(read$);
        flushNext(page([A, B], 2, 4), 1);
        flushNext(page([C, D], 2, 4), 2);
        flushNext(page([A, B], 2, 4), 1);
        const result = await result$;
        expect(result.complete).toBe(true);
        expect(result.aliasesById.size).toBe(4);
      }
    });

    it('re-reads page 1 once for an unchanged two-page set and stays complete', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([C, D], 2, 4), 2);
      flushNext(page([A, B], 2, 4), 1);

      const result = await result$;
      expect(result.complete).toBe(true);
      expect([...result.aliasesById.keys()]).toEqual(['a', 'b', 'c', 'd']);
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('ignores the order within a page when comparing the re-read', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([C, D], 2, 4), 2);
      flushNext(page([B, A], 2, 4), 1);

      expect((await result$).complete).toBe(true);
    });

    it('sends no extra request for a single-page set', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 1, 2), 1);

      expect((await result$).complete).toBe(true);
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('errors when a verification re-read fails', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([C, D], 2, 4), 2);
      httpMock
        .expectOne(GQL_ENDPOINT)
        .flush('boom', { status: 500, statusText: 'Internal Server Error' });

      await expect(result$).rejects.toThrow();
    });

    it('errors when a verification re-read answers with a GraphQL rejection', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([A, B], 2, 4), 1);
      flushNext(page([C, D], 2, 4), 2);
      httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'rate limited' }] });

      await expect(result$).rejects.toThrow();
    });
  });

  /*
   * #454: a read that lists fewer entries than 7TV counts, but is otherwise self-consistent, is a
   * gap — only for a caller that opts in with `detectGap`.
   *
   * protects: decision 1 and AC 1/2/3 of #454 — the gap is told apart from every other incomplete
   *   read, `complete` keeps its meaning, and a caller without `detectGap` sends and gets exactly
   *   what it did before.
   * fails_when: the under-count is reported `complete` or (with `detectGap`) `inconsistent`; an
   *   over-count, drift, repeat, verify mismatch, page cap or empty list becomes `gap`; a gap read
   *   has `complete: true`; a read without `detectGap` sends a verification request for an
   *   under-count or loses its partial maps.
   * no_claim: how the delete flow or the delete run act on a gap (their own specs); request
   *   counts of the fail-closed cases.
   */
  describe('gap classification (detectGap, #454)', () => {
    function flushNext(response: object, expectedPage: number) {
      const req = httpMock.expectOne(GQL_ENDPOINT);
      expect(req.request.body.variables.page).toBe(expectedPage);
      req.flush(response);
    }

    it('classifies the measured 496/494 single page as a gap of 2 with occupiedSlots 496', async () => {
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true, detectGap: true }),
      );
      flushNext(gapRead496of494Response(), 1);

      const result = await result$;
      expect(result.readState).toBe('gap');
      expect(result.omittedCount).toBe(2);
      expect(result.complete).toBe(false);
      expect(result.occupiedSlots).toBe(GAP_FIXTURE_TOTAL_COUNT);
      expect(result.aliasesById.size).toBe(494);
      for (const hidden of hiddenEntries) {
        expect(result.aliasesById.has(hidden.id)).toBe(false);
      }
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('stays a gap when one listed entry carries no add date', async () => {
      const entries = visibleEntries();
      const page = setEntriesResponse(entries, { totalCount: GAP_FIXTURE_TOTAL_COUNT });
      (page.data.emoteSets.emoteSet.emotes.items[0] as { addedAt?: string | null }).addedAt = null;
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true, detectGap: true }),
      );
      flushNext(page, 1);

      const result = await result$;
      expect(result.readState).toBe('gap');
      expect(result.entriesById?.get(entries[0].id)).toEqual([
        { alias: entries[0].alias, addedAt: null },
      ]);
    });

    it('reports an under-count across two pages as a gap only after the verification re-read came back unchanged', async () => {
      const pages = gapRead496of494Pages({ pages: 2, omit: 1 });
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true, detectGap: true }),
      );
      flushNext(pages[0], 1);
      flushNext(pages[1], 2);
      // The verification re-read of page 1 — without it the read would already have answered.
      flushNext(pages[0], 1);

      const result = await result$;
      expect(result.readState).toBe('gap');
      expect(result.omittedCount).toBe(1);
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('fails an under-count across two pages closed when the verification re-read differs', async () => {
      const pages = gapRead496of494Pages({ pages: 2 });
      const shifted = setEntriesResponse(visibleEntries().slice(1, 248), {
        totalCount: GAP_FIXTURE_TOTAL_COUNT,
        pageCount: 2,
      });
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true, detectGap: true }),
      );
      flushNext(pages[0], 1);
      flushNext(pages[1], 2);
      flushNext(shifted, 1);

      const result = await result$;
      expect(result.readState).toBe('inconsistent');
      expect(result.complete).toBe(false);
      expect(result.omittedCount).toBe(0);
    });

    it('errors when the verification re-read of a gap fails, as it does for an equal count', async () => {
      const pages = gapRead496of494Pages({ pages: 2 });
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true, detectGap: true }),
      );
      flushNext(pages[0], 1);
      flushNext(pages[1], 2);
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });

      await expect(result$).rejects.toThrow();
    });

    it('does not take an empty list under a positive count for a gap', async () => {
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { withAddedAt: true, detectGap: true }),
      );
      flushNext(page([], 1, 3), 1);

      const result = await result$;
      expect(result.readState).toBe('inconsistent');
      expect(result.omittedCount).toBe(0);
    });

    describe('keeps every other mismatch fail-closed', () => {
      const A = { id: 'a', alias: 'A' };
      const B = { id: 'b', alias: 'B' };
      const C = { id: 'c', alias: 'C' };

      function gapRead() {
        return firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1', { detectGap: true }));
      }

      it('an over-count', async () => {
        const result$ = gapRead();
        flushNext(page([A, B, C], 1, 2), 1);
        expect(await result$).toMatchObject({ readState: 'inconsistent', complete: false });
      });

      it('a totalCount that drifts between pages', async () => {
        const result$ = gapRead();
        flushNext(page([A], 2, 5), 1);
        flushNext(page([B], 2, 4), 2);
        expect(await result$).toMatchObject({ readState: 'inconsistent', complete: false });
      });

      it('a member repeated across pages', async () => {
        const result$ = gapRead();
        flushNext(page([A, B], 2, 5), 1);
        flushNext(page([B], 2, 5), 2);
        expect(await result$).toMatchObject({ readState: 'inconsistent', complete: false });
      });

      it('the page cap', async () => {
        const result$ = gapRead();
        for (let requested = 1; requested <= 10; requested++) {
          flushNext(page([{ id: `id-${requested}`, alias: `a${requested}` }], 11, 50), requested);
        }
        expect(await result$).toMatchObject({ readState: 'inconsistent', complete: false });
      });
    });

    it('has complete: true exactly for readState complete', async () => {
      const result$ = firstValueFrom(
        loadSevenTvSetEntries(httpClient, 'set-1', { detectGap: true }),
      );
      flushNext(page([{ id: 'a', alias: 'A' }]), 1);

      expect(await result$).toMatchObject({
        readState: 'complete',
        complete: true,
        omittedCount: 0,
      });
    });

    // Codex P2: without the option an under-count must keep its partial evidence and must not
    // risk a verification request that could throw the whole read away.
    it('without detectGap returns a two-page under-count at once as inconsistent with its partial maps', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(page([{ id: 'a', alias: 'A' }], 2, 5), 1);
      flushNext(page([{ id: 'b', alias: 'B' }], 2, 5), 2);

      const result = await result$;
      httpMock.expectNone(GQL_ENDPOINT);
      expect(result).toMatchObject({ readState: 'inconsistent', complete: false, omittedCount: 0 });
      expect([...result.aliasesById]).toEqual([
        ['a', ['A']],
        ['b', ['B']],
      ]);
    });

    it('without detectGap reports the measured single-page under-count as inconsistent', async () => {
      const result$ = firstValueFrom(loadSevenTvSetEntries(httpClient, 'set-1'));
      flushNext(gapRead496of494Response({ withAddedAt: false }), 1);

      expect(await result$).toMatchObject({ readState: 'inconsistent', complete: false });
    });
  });
});
