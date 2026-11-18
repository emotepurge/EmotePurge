import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  SEVEN_TV_EMOTE_LOOKUP_TIMEOUT_MS,
  SevenTvEmoteLookup,
  SevenTvEmoteLookupResult,
  canonicalSevenTvEmoteId,
} from './seven-tv-emote-lookup';

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';
const ID = '01EZPHAJ3R000C438200A44F19';

function found(animated: boolean | null, name = 'Pag', deleted = false) {
  return {
    data: {
      emotes: {
        emote: {
          id: ID,
          defaultName: name,
          deleted,
          ...(animated === null ? {} : { flags: { animated } }),
        },
      },
    },
  };
}

describe('canonicalSevenTvEmoteId', () => {
  it('maps the measured hex ObjectIDs onto the ULIDs 7TV answers with', () => {
    expect(canonicalSevenTvEmoteId('60ae958e229664e8667aea38')).toBe('01F6MZGCNG000255K4X1K7NTHR');
    expect(canonicalSevenTvEmoteId('603cb17bc20d020014423c29')).toBe('01EZPHAJ3R000C438200A44F19');
    expect(canonicalSevenTvEmoteId('60A1BABB3C3362F9A4B8B33A')).toBe('01F5VW2TKR0003RCV2Z6JBHCST');
  });

  it('upper-cases a ULID and leaves anything else alone', () => {
    expect(canonicalSevenTvEmoteId('01ezphaj3r000c438200a44f19')).toBe(ID);
    expect(canonicalSevenTvEmoteId(ID)).toBe(ID);
    expect(canonicalSevenTvEmoteId('KEKW')).toBe('KEKW');
  });
});

describe('SevenTvEmoteLookup', () => {
  let lookup: SevenTvEmoteLookup;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    lookup = TestBed.inject(SevenTvEmoteLookup);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  function ask(id: string): SevenTvEmoteLookupResult[] {
    const results: SevenTvEmoteLookupResult[] = [];
    lookup.lookup(id).subscribe((result) => results.push(result));
    return results;
  }

  it('asks 7TV directly, without a token, for exactly the given id', () => {
    ask('60ae958e229664e8667aea38');
    const request = http.expectOne(GQL_ENDPOINT);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.has('Authorization')).toBe(false);
    expect(request.request.body.variables).toEqual({ id: '60ae958e229664e8667aea38' });
    expect(request.request.body.query).toContain('emote(id: $id)');
    request.flush({ data: { emotes: { emote: null } } });
  });

  it('reports a found emote with its name and flag, under the id asked for', () => {
    const results = ask('603cb17bc20d020014423c29');
    http.expectOne(GQL_ENDPOINT).flush(found(true, 'Pag'));
    expect(results).toEqual([
      { id: '603cb17bc20d020014423c29', kind: 'found', name: 'Pag', animated: true },
    ]);
  });

  it('reports an id 7TV does not know, and a deleted emote, as unknown', () => {
    const missing = ask(ID);
    http.expectOne(GQL_ENDPOINT).flush({ data: { emotes: { emote: null } } });
    const deleted = ask(ID);
    http.expectOne(GQL_ENDPOINT).flush(found(false, 'Pag', true));
    expect(missing).toEqual([{ id: ID, kind: 'unknown' }]);
    expect(deleted).toEqual([{ id: ID, kind: 'unknown' }]);
  });

  it('reads the rate limit 7TV disguises as HTTP 200, and a real 429, as throttled', () => {
    const disguised = ask(ID);
    http.expectOne(GQL_ENDPOINT).flush({
      data: null,
      errors: [
        { message: 'rate limited', extensions: { code: 'RATE_LIMIT_EXCEEDED', status: 429 } },
      ],
    });
    const real = ask(ID);
    http.expectOne(GQL_ENDPOINT).flush(null, { status: 429, statusText: 'Too Many Requests' });
    expect(disguised).toEqual([{ id: ID, kind: 'throttled' }]);
    expect(real).toEqual([{ id: ID, kind: 'throttled' }]);
  });

  it('reads any other GraphQL error, a network failure and a missing payload as unreachable', () => {
    const gqlError = ask(ID);
    http.expectOne(GQL_ENDPOINT).flush({ data: null, errors: [{ message: 'LOAD_ERROR' }] });
    const network = ask(ID);
    http.expectOne(GQL_ENDPOINT).error(new ProgressEvent('error'));
    const empty = ask(ID);
    http.expectOne(GQL_ENDPOINT).flush({});
    expect(gqlError).toEqual([{ id: ID, kind: 'unreachable' }]);
    expect(network).toEqual([{ id: ID, kind: 'unreachable' }]);
    expect(empty).toEqual([{ id: ID, kind: 'unreachable' }]);
  });

  it('gives up after its deadline as unreachable', () => {
    vi.useFakeTimers();
    const results = ask(ID);
    const request = http.expectOne(GQL_ENDPOINT);
    vi.advanceTimersByTime(SEVEN_TV_EMOTE_LOOKUP_TIMEOUT_MS);
    expect(results).toEqual([{ id: ID, kind: 'unreachable' }]);
    expect(request.cancelled).toBe(true);
  });

  it('remembers "found, not animated" for the session, under every id form', () => {
    expect(lookup.isKnownStill(ID)).toBe(false);
    ask('603cb17bc20d020014423c29');
    http.expectOne(GQL_ENDPOINT).flush(found(false));
    expect(lookup.isKnownStill(ID)).toBe(true);
    expect(lookup.isKnownStill('603cb17bc20d020014423c29')).toBe(true);
    expect(lookup.isKnownStill('01ezphaj3r000c438200a44f19')).toBe(true);
  });

  it('remembers nothing for an animated emote, a missing flag or an unknown one', () => {
    ask(ID);
    http.expectOne(GQL_ENDPOINT).flush(found(true));
    ask(ID);
    http.expectOne(GQL_ENDPOINT).flush(found(null));
    ask(ID);
    http.expectOne(GQL_ENDPOINT).flush({ data: { emotes: { emote: null } } });
    expect(lookup.isKnownStill(ID)).toBe(false);
  });
});
