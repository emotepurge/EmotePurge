import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { REMOVE_EMOTE_MUTATION } from './seven-tv-delete.service';
import { flushApplied, flushWithoutResult, mutationSelection } from './seven-tv-mutation.testing';

const ADD_QUERY = `
  mutation AddEmote($setId: Id!, $emoteId: Id!, $alias: String) {
    emoteSets {
      emoteSet(id: $setId) {
        addEmote(id: { emoteId: $emoteId, alias: $alias }) {
          id
        }
      }
    }
  }
`;

const ALIAS_QUERY = `
  mutation UpdateEmoteAlias($setId: Id!, $emoteId: Id!, $currentAlias: String!, $alias: String!) {
    emoteSets {
      emoteSet(id: $setId) {
        updateEmoteAlias(id: { emoteId: $emoteId, alias: $currentAlias }, alias: $alias) {
          alias
        }
      }
    }
  }
`;

describe('mutationSelection', () => {
  it('reads the path and the selected scalar of each run mutation, skipping the arguments', () => {
    expect(mutationSelection(REMOVE_EMOTE_MUTATION.query)).toEqual({
      path: ['emoteSets', 'emoteSet', 'removeEmote'],
      leaf: 'id',
    });
    expect(mutationSelection(ADD_QUERY)).toEqual({
      path: ['emoteSets', 'emoteSet', 'addEmote'],
      leaf: 'id',
    });
    expect(mutationSelection(ALIAS_QUERY)).toEqual({
      path: ['emoteSets', 'emoteSet', 'updateEmoteAlias'],
      leaf: 'alias',
    });
  });

  it('agrees with the path the delete service declares next to its REMOVE', () => {
    expect(REMOVE_EMOTE_MUTATION.resultPath).toEqual(
      mutationSelection(REMOVE_EMOTE_MUTATION.query).path,
    );
  });

  it.each([
    ['an empty text', ''],
    ['a query instead of a mutation', 'query Read { a { b } }'],
    ['a text that ends inside the selection', 'mutation M { a {'],
    ['a selection without a field name', 'mutation M { { a } }'],
    ['a mutation without any nested field', 'mutation M { a }'],
    ['a selection of two sibling fields', 'mutation M { a { b c } }'],
    ['unbalanced arguments', 'mutation M { a(x: 1 { b } }'],
  ])('refuses %s', (_label, query) => {
    expect(() => mutationSelection(query)).toThrow();
  });
});

describe('flushApplied / flushWithoutResult', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function answer(body: unknown, flush: typeof flushApplied): unknown {
    let received: unknown;
    http.post('/gql', body).subscribe((response) => (received = response));
    flush(httpMock.expectOne('/gql'));
    return received;
  }

  it('answers with the result along the query path, or with everything but the mutation field', () => {
    const body = { query: ALIAS_QUERY, variables: {} };

    expect(answer(body, flushApplied)).toEqual({
      data: { emoteSets: { emoteSet: { updateEmoteAlias: { alias: 'applied' } } } },
    });
    expect(answer(body, flushWithoutResult)).toEqual({ data: { emoteSets: { emoteSet: {} } } });
  });

  it('refuses a request that carries no GraphQL query', () => {
    http.post('/gql', { variables: {} }).subscribe();
    const request = httpMock.expectOne('/gql');

    expect(() => flushApplied(request)).toThrow();
    request.flush(null);
  });
});
