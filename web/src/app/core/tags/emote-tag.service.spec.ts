import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { EmoteTagService } from './emote-tag.service';

describe('EmoteTagService', () => {
  let service: EmoteTagService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(EmoteTagService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists without a query when no set is given', () => {
    service.list('somechannel').subscribe();

    const req = http.expectOne('/api/channels/somechannel/tags');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.keys()).toEqual([]);
    req.flush({ emoteSetId: null, isActiveSet: false, tags: [] });
  });

  it('lists with emoteSetId only when set', () => {
    service.list('somechannel', 'SET1').subscribe();

    const req = http.expectOne((r) => r.url === '/api/channels/somechannel/tags');
    expect(req.request.params.get('emoteSetId')).toBe('SET1');
    req.flush({ emoteSetId: 'SET1', isActiveSet: false, tags: [] });
  });

  it('lists entries on the entries route, with the optional set', () => {
    service.listEntries('somechannel', 7).subscribe();
    const plain = http.expectOne('/api/channels/somechannel/tags/7/entries');
    expect(plain.request.params.keys()).toEqual([]);
    plain.flush({ emoteSetId: null, isActiveSet: false, entries: [] });

    service.listEntries('somechannel', 7, 'SET1').subscribe();
    const withSet = http.expectOne((r) => r.url === '/api/channels/somechannel/tags/7/entries');
    expect(withSet.request.params.get('emoteSetId')).toBe('SET1');
    withSet.flush({ emoteSetId: 'SET1', isActiveSet: true, entries: [] });
  });

  it('creates, renames and deletes with the name body', () => {
    service.create('somechannel', 'Cats').subscribe();
    const create = http.expectOne('/api/channels/somechannel/tags');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ name: 'Cats' });
    create.flush({ id: 1, name: 'Cats' });

    service.rename('somechannel', 1, 'Dogs').subscribe();
    const rename = http.expectOne('/api/channels/somechannel/tags/1');
    expect(rename.request.method).toBe('PATCH');
    expect(rename.request.body).toEqual({ name: 'Dogs' });
    rename.flush({ id: 1, name: 'Dogs' });

    service.delete('somechannel', 1).subscribe();
    const del = http.expectOne('/api/channels/somechannel/tags/1');
    expect(del.request.method).toBe('DELETE');
    del.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('adds entries via POST and removes them via POST .../entries/remove', () => {
    service.addEntries('somechannel', 3, ['A', 'B']).subscribe();
    const add = http.expectOne('/api/channels/somechannel/tags/3/entries');
    expect(add.request.method).toBe('POST');
    expect(add.request.body).toEqual({ sevenTvEmoteIds: ['A', 'B'] });
    add.flush({ addedCount: 2, alreadyTaggedCount: 0, skippedNotInSetIds: [] });

    service.addEntries('somechannel', 3, ['A'], 'set-b').subscribe();
    const fromOtherSet = http.expectOne('/api/channels/somechannel/tags/3/entries');
    expect(fromOtherSet.request.body).toEqual({ sevenTvEmoteIds: ['A'], emoteSetId: 'set-b' });
    fromOtherSet.flush({ addedCount: 1, alreadyTaggedCount: 0, skippedNotInSetIds: [] });

    service.removeEntries('somechannel', 3, ['A']).subscribe();
    const remove = http.expectOne('/api/channels/somechannel/tags/3/entries/remove');
    expect(remove.request.method).toBe('POST');
    expect(remove.request.body).toEqual({ sevenTvEmoteIds: ['A'] });
    remove.flush({ removedCount: 1 });
  });

  it('registers an operation on /operations with the set in the body, not the query', () => {
    const body = {
      operationId: '11111111-1111-4111-8111-111111111111',
      kind: 'playIn' as const,
      emoteSetId: 'SET1',
      targetOwnerTwitchId: null,
    };
    let result: unknown;
    service.registerOperation('somechannel', 7, body).subscribe((r) => (result = r));

    const req = http.expectOne('/api/channels/somechannel/tags/7/operations');
    expect(req.request.method).toBe('POST');
    expect(req.request.params.keys()).toEqual([]);
    expect(req.request.body).toEqual(body);
    req.flush({ registeredAtUtc: '2026-10-05T10:00:00Z' });
    expect(result).toEqual({ registeredAtUtc: '2026-10-05T10:00:00Z' });
  });

  it('reports placements on /placements, set in the body only', () => {
    const body = {
      operationId: '11111111-1111-4111-8111-111111111111',
      emoteSetId: 'SET1',
      targetOwnerTwitchId: '4711',
      sevenTvEmoteIds: ['A', 'B'],
    };
    service.reportPlacements('somechannel', 7, body).subscribe();

    const req = http.expectOne('/api/channels/somechannel/tags/7/placements');
    expect(req.request.method).toBe('POST');
    expect(req.request.params.keys()).toEqual([]);
    expect(req.request.body).toEqual(body);
    req.flush({
      replayed: false,
      recordedCount: 2,
      alreadyRecordedCount: 0,
      notTaggedIds: [],
      discardedStaleIds: [],
    });
  });

  it('reports a removal on /placements/removed with every snapshot entry carrying its revision', () => {
    const body = {
      operationId: '22222222-2222-4222-8222-222222222222',
      emoteSetId: 'SET1',
      targetOwnerTwitchId: null,
      activationOperationId: null,
      snapshot: [
        { sevenTvEmoteId: 'A', placementOperationId: '33333333-3333-4333-8333-333333333333' },
      ],
      removedIds: ['A'],
      keptIds: [],
    };
    service.reportRemoval('somechannel', 7, body).subscribe();

    const req = http.expectOne('/api/channels/somechannel/tags/7/placements/removed');
    expect(req.request.method).toBe('POST');
    expect(req.request.params.keys()).toEqual([]);
    expect(req.request.body).toEqual(body);
    req.flush({
      replayed: false,
      deletedCount: 1,
      transferredCount: 0,
      droppedCount: 0,
      sweptCount: 0,
      deactivated: true,
    });
  });

  it('url-encodes the channel name on the report routes', () => {
    service
      .reportPlacements('a b', 1, {
        operationId: 'x',
        emoteSetId: 's',
        targetOwnerTwitchId: null,
        sevenTvEmoteIds: [],
      })
      .subscribe();

    http.expectOne('/api/channels/a%20b/tags/1/placements').flush({});
  });
});
