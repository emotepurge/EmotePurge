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

    service.removeEntries('somechannel', 3, ['A']).subscribe();
    const remove = http.expectOne('/api/channels/somechannel/tags/3/entries/remove');
    expect(remove.request.method).toBe('POST');
    expect(remove.request.body).toEqual({ sevenTvEmoteIds: ['A'] });
    remove.flush({ removedCount: 1 });
  });
});
