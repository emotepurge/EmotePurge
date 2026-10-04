import { HttpClient, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { NEVER, Observable, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EditableSetResolution } from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import {
  DeleteTargetResolution,
  LIVE_ALIAS_READ_TIMEOUT_MS,
  LiveAliasReadResult,
  MEMBER_READ_TRUNCATED_REASON_KEY,
  MEMBER_READ_UNAVAILABLE_REASON_KEY,
  readLiveSetAliases,
  resolveDeleteTarget,
} from './delete-flow';

// Only the two building blocks the tag page's clear-out run (#201 T-C) consumes on its own. The
// chain that strings them together (`startDeleteFlow`) is characterized end to end by
// `mass-delete-panel.spec.ts` through the panel's button, dialog and service mocks, and is not
// re-tested here.

describe('resolveDeleteTarget', () => {
  function resolve(answer: Observable<EditableSetResolution>): {
    result: DeleteTargetResolution | undefined;
    resolveEditableSet: ReturnType<typeof vi.fn>;
  } {
    const resolveEditableSet = vi.fn().mockReturnValue(answer);
    let result: DeleteTargetResolution | undefined;
    resolveDeleteTarget(
      { emoteSetService: { resolveEditableSet } as unknown as SevenTvEmoteSetService },
      'set-1',
      'somechannel',
    ).subscribe((resolution) => (result = resolution));
    return { result, resolveEditableSet };
  }

  afterEach(() => vi.useRealTimers());

  it("asks the shared pre-check for the set with the channel's login as the owner hint", () => {
    const { resolveEditableSet } = resolve(of({ status: 'notEditable' }));

    expect(resolveEditableSet).toHaveBeenCalledWith('set-1', {
      twitchChannelId: null,
      twitchLogin: 'somechannel',
    });
  });

  it("hands an editable set's resolved owner id through", () => {
    const { result } = resolve(
      of({
        status: 'editable',
        target: {
          emoteSetId: 'set-1',
          setName: 'Main',
          ownerDisplayName: 'SomeChannel',
          twitchLogin: 'somechannel',
          trackedChannelName: 'somechannel',
          isActiveSet: true,
          ownerTwitchChannelId: 'tw-owner',
        },
      }),
    );

    expect(result).toEqual({ status: 'editable', ownerTwitchChannelId: 'tw-owner' });
  });

  it.each([
    ['notEditable', 'massDelete.errors.targetNotEditable'],
    ['notSelectable', 'massDelete.errors.targetNotSelectable'],
    ['unavailable', 'massDelete.errors.targetCheckUnavailable'],
  ] as const)('blocks a %s set with its massDelete.errors reason', (status, reasonKey) => {
    const { result } = resolve(of({ status }));

    expect(result).toEqual({ status: 'blocked', reason: status, reasonKey });
  });

  it('blocks as "unavailable" when the check fails', () => {
    const { result } = resolve(throwError(() => new Error('503')));

    expect(result).toEqual({
      status: 'blocked',
      reason: 'unavailable',
      reasonKey: 'massDelete.errors.targetCheckUnavailable',
    });
  });

  it('blocks as "unavailable" once the check outlasts the time budget', () => {
    vi.useFakeTimers();
    const resolveEditableSet = vi.fn().mockReturnValue(NEVER);
    let result: DeleteTargetResolution | undefined;
    resolveDeleteTarget(
      { emoteSetService: { resolveEditableSet } as unknown as SevenTvEmoteSetService },
      'set-1',
      'somechannel',
    ).subscribe((resolution) => (result = resolution));

    vi.advanceTimersByTime(LIVE_ALIAS_READ_TIMEOUT_MS - 1);
    expect(result).toBeUndefined();
    vi.advanceTimersByTime(1);

    expect(result).toEqual({
      status: 'blocked',
      reason: 'unavailable',
      reasonKey: 'massDelete.errors.targetCheckUnavailable',
    });
  });
});

describe('readLiveSetAliases', () => {
  const GQL = 'https://7tv.io/v4/gql';
  let httpMock: HttpTestingController;
  let httpClient: HttpClient;

  /** A single, last page of `entries`; `truncated` makes 7TV's `totalCount` promise one more entry
   *  than the page delivers, which is what an incomplete read looks like. */
  function entriesPage(entries: { id: string; alias?: string }[], truncated = false) {
    return {
      data: {
        emoteSets: {
          emoteSet: {
            emotes: {
              totalCount: entries.length + (truncated ? 1 : 0),
              pageCount: 1,
              items: entries.map(({ id, alias }) => ({ alias, emote: { id } })),
            },
          },
        },
      },
    };
  }

  function read(): { result: () => LiveAliasReadResult | undefined } {
    let result: LiveAliasReadResult | undefined;
    readLiveSetAliases(httpClient, 'set-1').subscribe((answer) => (result = answer));
    return { result: () => result };
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    httpClient = TestBed.inject(HttpClient);
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it("passes a complete read's entries through", () => {
    const { result } = read();
    httpMock
      .expectOne(GQL)
      .flush(
        entriesPage([
          { id: '7tv-1', alias: 'PogU' },
          { id: '7tv-1', alias: 'PogU2' },
          { id: '7tv-2' },
        ]),
      );

    const answer = result();
    expect(answer?.status).toBe('ok');
    if (answer?.status !== 'ok') {
      return;
    }
    expect(answer.entries.aliasesById.get('7tv-1')).toEqual(['PogU', 'PogU2']);
    expect(answer.entries.aliaslessIds.has('7tv-2')).toBe(true);
  });

  it('blocks an incomplete read with the truncated reason', () => {
    const { result } = read();
    httpMock.expectOne(GQL).flush(entriesPage([{ id: '7tv-1', alias: 'PogU' }], true));

    expect(result()).toEqual({ status: 'blocked', reasonKey: MEMBER_READ_TRUNCATED_REASON_KEY });
  });

  it('blocks a failed read with the unavailable reason', () => {
    const { result } = read();
    httpMock.expectOne(GQL).flush('boom', { status: 503, statusText: 'Service Unavailable' });

    expect(result()).toEqual({ status: 'blocked', reasonKey: MEMBER_READ_UNAVAILABLE_REASON_KEY });
  });

  it('blocks a read that outlasts the time budget with the unavailable reason', () => {
    vi.useFakeTimers();
    const { result } = read();
    const request = httpMock.expectOne(GQL);

    vi.advanceTimersByTime(LIVE_ALIAS_READ_TIMEOUT_MS);

    expect(request.cancelled).toBe(true);
    expect(result()).toEqual({ status: 'blocked', reasonKey: MEMBER_READ_UNAVAILABLE_REASON_KEY });
  });
});
