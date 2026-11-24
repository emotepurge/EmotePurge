import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { wishConflict, WishService } from './wish.service';
import { ApprovedWish, CreatedWish, Wish, WishDetail } from './wish.model';

const WISH = { id: 7, alias: 'KEKW', status: 'active' } as Wish;
const BASE = '/api/channels/handofblood/wishes';

describe('WishService', () => {
  let service: WishService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WishService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('lists a view with paging and hands the page back unchanged', () => {
    const page = { items: [WISH], page: 2, pageSize: 20, totalCount: 21, totalPages: 2 };
    let got: unknown;
    service.list('handofblood', 'running', 2).subscribe((r) => (got = r));

    const req = httpMock.expectOne(`${BASE}?view=running&page=2&pageSize=20`);
    expect(req.request.method).toBe('GET');
    req.flush(page);
    expect(got).toEqual(page);
  });

  it('reads the detail with its usage series', () => {
    const detail = {
      ...WISH,
      usage: { total: 3, daily: [{ date: '2026-11-18', useCount: 3 }] },
    } as unknown as WishDetail;
    let got: WishDetail | undefined;
    service.get('handofblood', 7).subscribe((r) => (got = r));

    httpMock.expectOne(`${BASE}/7`).flush(detail);
    expect(got?.usage?.daily).toEqual([{ date: '2026-11-18', useCount: 3 }]);
  });

  it('lists adoptable slots, narrowed by emote id only when one is given', () => {
    service.listSlots('handofblood').subscribe();
    httpMock.expectOne(`${BASE}/slots`).flush([]);

    service.listSlots('handofblood', '01ABC').subscribe();
    const req = httpMock.expectOne(`${BASE}/slots?sevenTvEmoteId=01ABC`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('creates with the body verbatim and passes the setFull hint through', () => {
    const body = { sevenTvEmoteId: '01ABC', alias: 'KEKW', durationDays: 30 };
    let got: CreatedWish | undefined;
    service.create('handofblood', body).subscribe((r) => (got = r));

    const req = httpMock.expectOne(BASE);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    req.flush({ ...WISH, setFull: true });
    expect(got?.setFull).toBe(true);
  });

  it('adopts with the expected alias and generation, null staying null for an undated entry', () => {
    service
      .adopt('handofblood', {
        slotId: 's1',
        expectedAlias: 'KEKW',
        expectedAddedAtUtc: '2026-11-01T10:00:00.1234567Z',
        wishId: 7,
      })
      .subscribe();
    const dated = httpMock.expectOne(`${BASE}/adopt`);
    expect(dated.request.method).toBe('POST');
    expect(dated.request.body).toEqual({
      slotId: 's1',
      expectedAlias: 'KEKW',
      expectedAddedAtUtc: '2026-11-01T10:00:00.1234567Z',
      wishId: 7,
    });
    dated.flush(WISH);

    service
      .adopt('handofblood', { slotId: 's2', expectedAlias: 'Pog', expectedAddedAtUtc: null })
      .subscribe();
    const undated = httpMock.expectOne(`${BASE}/adopt`);
    expect(undated.request.body.expectedAddedAtUtc).toBeNull();
    expect('expectedAddedAtUtc' in undated.request.body).toBe(true);
    undated.flush(WISH);
  });

  it('deletes an ended wish, sending the ended-at string unchanged (microseconds, Z, offset)', () => {
    for (const ended of ['2026-11-17T10:00:00.1234567Z', '2026-11-17T10:00:00.123456+00:00']) {
      service.deleteWish('handofblood', 7, ended).subscribe();
      const req = httpMock.expectOne((r) => r.url === `${BASE}/7`);
      expect(req.request.method).toBe('DELETE');
      expect(req.request.params.get('endedAtUtc')).toBe(ended);
      expect(req.request.urlWithParams).toBe(
        `${BASE}/7?endedAtUtc=${encodeURIComponent(ended).replace(/%3A/g, ':')}`,
      );
      expect(req.request.body).toBeNull();
      req.flush(null, { status: 204, statusText: 'No Content' });
    }
  });

  it('PATCHes the edit and POSTs re-add with its mode', () => {
    service.update('handofblood', 7, { alias: 'New' }).subscribe();
    const patch = httpMock.expectOne(`${BASE}/7`);
    expect(patch.request.method).toBe('PATCH');
    expect(patch.request.body).toEqual({ alias: 'New' });
    patch.flush(WISH);

    service.reAdd('handofblood', 7, { mode: 'timed', durationDays: 14 }).subscribe();
    const reAdd = httpMock.expectOne(`${BASE}/7/re-add`);
    expect(reAdd.request.method).toBe('POST');
    expect(reAdd.request.body).toEqual({ mode: 'timed', durationDays: 14 });
    reAdd.flush(WISH);
  });

  it.each(['extend', 'keep', 'close', 'retry', 'recheck'] as const)(
    'POSTs %s to its own route without a body',
    (action) => {
      service[action]('handofblood', 7).subscribe();

      const req = httpMock.expectOne(`${BASE}/7/${action}`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toBeNull();
      req.flush(WISH);
    },
  );

  it('reports a removal with slot, generation and set verbatim, evidence only when given', () => {
    const body = {
      slotId: 's1',
      slotAddedAtUtc: '2026-11-01T10:00:00.1234567Z',
      emoteSetId: 'set9',
    };
    service.completeRemoval('handofblood', 7, body).subscribe();
    const plain = httpMock.expectOne(`${BASE}/7/complete-removal`);
    expect(plain.request.method).toBe('POST');
    expect(plain.request.body).toEqual(body);
    plain.flush(WISH);

    service.completeRemoval('handofblood', 7, { ...body, evidence: 'absent' }).subscribe();
    const withEvidence = httpMock.expectOne(`${BASE}/7/complete-removal`);
    expect(withEvidence.request.body.evidence).toBe('absent');
    withEvidence.flush(WISH);
  });

  it('keeps the three threshold cases of the settings write apart on the wire', () => {
    const base = { defaultDurationDays: 30, probationLeadDays: 3 };

    service.updateSettings('handofblood', base).subscribe();
    const absent = httpMock.expectOne(`${BASE}/settings`);
    expect(absent.request.method).toBe('PUT');
    expect('protectionThresholdPerDay' in absent.request.body).toBe(false);
    absent.flush({});

    service.updateSettings('handofblood', { ...base, protectionThresholdPerDay: null }).subscribe();
    const off = httpMock.expectOne(`${BASE}/settings`);
    expect(off.request.body.protectionThresholdPerDay).toBeNull();
    off.flush({});

    service.updateSettings('handofblood', { ...base, protectionThresholdPerDay: 2.5 }).subscribe();
    const set = httpMock.expectOne(`${BASE}/settings`);
    expect(set.request.body.protectionThresholdPerDay).toBe(2.5);
    set.flush({});
  });

  it('sends pendingExpiryDays with the settings write only when given', () => {
    const base = { defaultDurationDays: 30, probationLeadDays: 3 };

    service.updateSettings('handofblood', base).subscribe();
    const absent = httpMock.expectOne(`${BASE}/settings`);
    expect('pendingExpiryDays' in absent.request.body).toBe(false);
    absent.flush({});

    service.updateSettings('handofblood', { ...base, pendingExpiryDays: 21 }).subscribe();
    const set = httpMock.expectOne(`${BASE}/settings`);
    expect(set.request.body.pendingExpiryDays).toBe(21);
    set.flush({});
  });

  it('approves with the body as given, an empty one included, and hands the extension back', () => {
    let got: ApprovedWish | undefined;
    service.approve('handofblood', 9).subscribe((r) => (got = r));
    const plain = httpMock.expectOne(`${BASE}/9/approve`);
    expect(plain.request.method).toBe('POST');
    expect(plain.request.body).toEqual({});
    plain.flush({ ...WISH, extendedExpiresAtUtc: null });
    expect(got?.extendedExpiresAtUtc).toBeNull();

    const body = { sevenTvEmoteId: '01ABC', alias: 'KEKW', durationDays: 30 };
    service.approve('handofblood', 9, body).subscribe((r) => (got = r));
    const full = httpMock.expectOne(`${BASE}/9/approve`);
    expect(full.request.body).toEqual(body);
    full.flush({ ...WISH, mergedIntoWishId: 4, extendedExpiresAtUtc: '2026-12-20T00:00:00Z' });
    expect(got?.extendedExpiresAtUtc).toBe('2026-12-20T00:00:00Z');
    expect(got?.mergedIntoWishId).toBe(4);
  });

  it.each(['reject', 'unlink'] as const)('posts %s without a body', (action) => {
    service[action]('handofblood', 9).subscribe();
    const req = httpMock.expectOne(`${BASE}/9/${action}`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    req.flush(WISH);
  });

  describe('rewards', () => {
    const REWARDS = `${BASE}/rewards`;

    it('reads the reward state and hands it back unchanged', () => {
      const read = { channelBound: true, rewards: [], limit: 10, learning: null, gaps: [] };
      let got: unknown;
      service.getRewards('handofblood').subscribe((r) => (got = r));
      const req = httpMock.expectOne(REWARDS);
      expect(req.request.method).toBe('GET');
      req.flush(read);
      expect(got).toEqual(read);
    });

    it('starts a learning session with no body', () => {
      service.startLearning('handofblood').subscribe();
      const req = httpMock.expectOne(`${REWARDS}/learning`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toBeNull();
      req.flush({});
    });

    it('confirms with exactly the ids and fields it is given', () => {
      const body = { sessionId: 's1', candidateId: 'c1', label: 'Wish an emote' };
      service.confirmLearning('handofblood', body).subscribe();
      const bare = httpMock.expectOne(`${REWARDS}/learning/confirm`);
      expect(bare.request.method).toBe('POST');
      expect(bare.request.body).toEqual(body);
      bare.flush({});

      service.confirmLearning('handofblood', { ...body, defaultDurationDays: 21 }).subscribe();
      const timed = httpMock.expectOne(`${REWARDS}/learning/confirm`);
      expect(timed.request.body.defaultDurationDays).toBe(21);
      timed.flush({});
    });

    it('cancels a learning session by its id in the query', () => {
      service.cancelLearning('handofblood', 's-1').subscribe();
      const req = httpMock.expectOne(`${REWARDS}/learning?sessionId=s-1`);
      expect(req.request.method).toBe('DELETE');
      req.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('patches and deletes a reward by its row id', () => {
      service.updateReward('handofblood', 5, { isActive: false }).subscribe();
      const patch = httpMock.expectOne(`${REWARDS}/5`);
      expect(patch.request.method).toBe('PATCH');
      expect(patch.request.body).toEqual({ isActive: false });
      patch.flush({});

      service.deleteReward('handofblood', 5).subscribe();
      const del = httpMock.expectOne(`${REWARDS}/5`);
      expect(del.request.method).toBe('DELETE');
      del.flush(null, { status: 204, statusText: 'No Content' });
    });
  });

  it('reads settings with canEdit and the cross-channel routes', () => {
    let canEdit: boolean | undefined;
    service.getSettings('handofblood').subscribe((s) => (canEdit = s.canEdit));
    httpMock.expectOne(`${BASE}/settings`).flush({ canEdit: true });
    expect(canEdit).toBe(true);

    service.listMine().subscribe();
    httpMock.expectOne('/api/wishes/mine').flush([]);

    let count: number | undefined;
    service.summary().subscribe((s) => (count = s.actionCount));
    httpMock.expectOne('/api/wishes/summary').flush({ actionCount: 4 });
    expect(count).toBe(4);
  });

  it('does not swallow errors: the caller gets the HttpErrorResponse with its body', () => {
    let error: HttpErrorResponse | undefined;
    service.keep('handofblood', 7).subscribe({ error: (e: HttpErrorResponse) => (error = e) });

    httpMock
      .expectOne(`${BASE}/7/keep`)
      .flush({ errorCode: 'wish_state_conflict' }, { status: 409, statusText: 'Conflict' });
    expect(error?.status).toBe(409);
    expect(error?.error).toEqual({ errorCode: 'wish_state_conflict' });
  });
});

describe('wishConflict', () => {
  const err = (body: unknown) => new HttpErrorResponse({ status: 409, error: body });

  it('reads slotIds and the running wish of wish_emote_already_in_set', () => {
    expect(
      wishConflict(
        err({ errorCode: 'wish_emote_already_in_set', slotIds: ['a', 'b'], wishId: 12 }),
      ),
    ).toEqual({ slotIds: ['a', 'b'], wishId: 12 });
  });

  it('keeps a null wishId (no running wish) distinct from an absent one', () => {
    expect(wishConflict(err({ slotIds: [], wishId: null }))).toEqual({ slotIds: [], wishId: null });
    expect(wishConflict(err({ errorCode: 'wish_state_conflict' }))).toEqual({});
  });

  it('leaves out fields of the wrong type and survives a missing or foreign body', () => {
    expect(wishConflict(err({ slotIds: 'a', wishId: '3' }))).toEqual({});
    expect(wishConflict(err({ slotIds: [1] }))).toEqual({});
    expect(wishConflict(err(null))).toEqual({});
    expect(wishConflict(err('<html>bad gateway</html>'))).toEqual({});
  });
});
