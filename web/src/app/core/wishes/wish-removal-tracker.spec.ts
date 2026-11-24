import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  DeleteRunSettlement,
  REPORT_TIMEOUT_MS,
  SevenTvDeleteService,
} from '../seven-tv/seven-tv-delete.service';
import { RunQueueItem } from '../seven-tv/seven-tv-run-engine';
import { WishRemovalTarget } from './wish-removal';
import { WishRemovalEnd, WishRemovalTracker } from './wish-removal-tracker';

const TARGET: WishRemovalTarget = {
  channelName: 'olaf',
  wishId: 7,
  slotId: 'slot-1',
  slotAddedAtUtc: '2026-11-02T10:00:00.123456Z',
  emoteSetId: 'set-a',
};
const URL_7 = '/api/channels/olaf/wishes/7/complete-removal';

function row(status: RunQueueItem['status'], over: Partial<RunQueueItem> = {}): RunQueueItem {
  return {
    key: '01EMOTE#wish-7',
    sevenTvEmoteId: '01EMOTE',
    name: 'kekw2',
    entry: { slotId: 'slot-1', alias: 'kekw2', addedAt: TARGET.slotAddedAtUtc, cardKey: 'wish-7' },
    status,
    completedSteps: status === 'done' ? 1 : 0,
    failedStep: null,
    ...over,
  };
}

function settlement(
  item: RunQueueItem,
  evidence: 'acknowledged' | 'absent' = 'acknowledged',
  runId = 'run-1',
): DeleteRunSettlement {
  const done = item.status === 'done';
  return {
    runId,
    result: { doneKeys: done ? [item.key] : [], items: [item], startedAt: 0, finishedAt: 1 },
    evidence: new Map(done ? [[item.key, evidence]] : []),
  };
}

describe('WishRemovalTracker', () => {
  let tracker: WishRemovalTracker;
  let httpMock: HttpTestingController;
  let ends: WishRemovalEnd[];
  const deleteRunning = signal(false);
  const pauseSeconds = signal<number | null>(null);

  beforeEach(() => {
    vi.useFakeTimers();
    deleteRunning.set(false);
    pauseSeconds.set(null);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: SevenTvDeleteService,
          useValue: { isRunning: deleteRunning, rateLimitPauseSeconds: pauseSeconds },
        },
      ],
    });
    tracker = TestBed.inject(WishRemovalTracker);
    httpMock = TestBed.inject(HttpTestingController);
    ends = [];
    tracker.ended.subscribe((end) => ends.push(end));
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  function startRun(target = TARGET, runId = 'run-1') {
    const progress = tracker.progressFor(target.channelName, target.wishId);
    progress.starting();
    progress.started(runId, target);
    return progress;
  }

  function stateOf(wishId = 7) {
    return tracker.stateFor('olaf', { id: wishId });
  }

  it('books a done row with exactly one complete-removal carrying the frozen target', () => {
    const progress = startRun();
    expect(stateOf()).toEqual({ kind: 'running', pauseSeconds: null });
    // A running removal holds the arbiter; only a booking out or owed locks the row itself.
    expect(tracker.owesReport('olaf', 7)).toBe(false);

    const done = settlement(row('done'));
    progress.settled(TARGET, done);
    // The same report again (a signal firing twice) sends nothing more.
    progress.settled(TARGET, done);

    const request = httpMock.expectOne(URL_7);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      slotId: 'slot-1',
      slotAddedAtUtc: '2026-11-02T10:00:00.123456Z',
      emoteSetId: 'set-a',
      evidence: 'acknowledged',
    });
    expect(stateOf()).toEqual({ kind: 'reporting' });
    expect(tracker.owesReport('olaf', 7)).toBe(true);
    request.flush({});

    expect(stateOf()).toEqual({ kind: 'booked' });
    expect(tracker.owesReport('olaf', 7)).toBe(false);
    expect(ends).toEqual([{ channelName: 'olaf', wishId: 7, state: { kind: 'booked' } }]);
  });

  it('shows the delete run’s rate-limit pause while the removal runs', () => {
    startRun();
    deleteRunning.set(true);
    pauseSeconds.set(42);

    expect(stateOf()).toEqual({ kind: 'running', pauseSeconds: 42 });
  });

  it('passes on that only the re-read found the entry gone', () => {
    startRun().settled(TARGET, settlement(row('done'), 'absent'));

    const request = httpMock.expectOne(URL_7);
    expect(request.request.body.evidence).toBe('absent');
    request.flush({});
  });

  it.each([
    [
      'a skip before the removal',
      row('cancelled', { skipReason: { key: 'wishes.remove.tooSlow' } }),
      { kind: 'skipped', reason: { key: 'wishes.remove.tooSlow' } },
    ],
    ['a cancel', row('cancelled'), { kind: 'cancelled' }],
    [
      'a refusal by 7TV',
      row('failed', { errorMessage: 'nope' }),
      { kind: 'failed', message: 'nope' },
    ],
    ['an outcome nobody could confirm', row('unknown'), { kind: 'unknown' }],
  ])('books nothing after %s and shows why', (_label, item, state) => {
    startRun().settled(TARGET, settlement(item));

    httpMock.expectNone(URL_7);
    expect(stateOf()).toEqual(state);
    expect(ends).toEqual([{ channelName: 'olaf', wishId: 7, state }]);
  });

  it('books a 404 wish_not_found as deleted meanwhile, final', () => {
    startRun().settled(TARGET, settlement(row('done')));
    httpMock
      .expectOne(URL_7)
      .flush({ errorCode: 'wish_not_found' }, { status: 404, statusText: 'No' });

    expect(stateOf()).toEqual({ kind: 'notBooked', deleted: true });
    tracker.retry('olaf', 7);
    vi.advanceTimersByTime(60_000);
    httpMock.expectNone(URL_7);
  });

  it.each([
    [404, 'channel_not_found'],
    [404, 'wishes_disabled'],
    [409, 'wish_state_conflict'],
  ])('takes %s %s as plain not booked, not deleted', (status, code) => {
    startRun().settled(TARGET, settlement(row('done')));
    httpMock.expectOne(URL_7).flush({ errorCode: code }, { status, statusText: 'No' });

    expect(stateOf()).toEqual({ kind: 'notBooked' });
    expect(stateOf()).not.toHaveProperty('deleted');
  });

  it.each([409, 404])('takes a %s as final: not booked, never sent again', (status) => {
    startRun().settled(TARGET, settlement(row('done')));
    httpMock.expectOne(URL_7).flush({ code: 'wish_state_conflict' }, { status, statusText: 'No' });

    expect(stateOf()).toEqual({ kind: 'notBooked' });
    tracker.retry('olaf', 7);
    vi.advanceTimersByTime(60_000);
    httpMock.expectNone(URL_7);
    expect(tracker.owesReport('olaf', 7)).toBe(false);
  });

  it.each([
    ['a network failure', 0],
    ['a server error', 503],
  ])('keeps the report owed after %s and sends the same body again on retry', (_label, status) => {
    startRun().settled(TARGET, settlement(row('done')));
    const first = httpMock.expectOne(URL_7);
    const body = first.request.body;
    if (status === 0) {
      first.error(new ProgressEvent('error'));
    } else {
      first.flush(null, { status, statusText: 'Down' });
    }

    expect(stateOf()).toEqual({ kind: 'reportPending' });
    expect(tracker.owesReport('olaf', 7)).toBe(true);
    vi.advanceTimersByTime(60_000);
    httpMock.expectNone(URL_7);

    tracker.retry('olaf', 7);
    const again = httpMock.expectOne(URL_7);
    expect(again.request.body).toEqual(body);
    // A 200 to a repeated report is the Api's answer to "already booked" (plan 2.1a, row 5).
    again.flush({});
    expect(stateOf()).toEqual({ kind: 'booked' });
  });

  it('keeps the report owed when the Api does not answer within the attempt budget', () => {
    startRun().settled(TARGET, settlement(row('done')));
    const request = httpMock.expectOne(URL_7);

    vi.advanceTimersByTime(REPORT_TIMEOUT_MS);

    expect(request.cancelled).toBe(true);
    expect(stateOf()).toEqual({ kind: 'reportPending' });
  });

  it('follows nothing for a start that was refused or dismissed', () => {
    const refused = tracker.progressFor('olaf', 7);
    refused.starting();
    refused.notStarted('busy');
    expect(stateOf()).toEqual({ kind: 'notStarted', reason: 'busy' });

    const dismissed = tracker.progressFor('olaf', 8);
    dismissed.starting();
    dismissed.dismissed();
    expect(stateOf(8)).toBeNull();
    httpMock.expectNone(() => true);
  });

  it('keeps two runs’ targets apart, whichever settles first', () => {
    const other: WishRemovalTarget = {
      ...TARGET,
      wishId: 8,
      slotId: 'slot-2',
      emoteSetId: 'set-a',
    };
    const first = startRun(TARGET, 'run-1');
    const second = startRun(other, 'run-2');

    second.settled(
      other,
      settlement(
        row('done', {
          key: '01OTHER#wish-8',
          entry: {
            slotId: 'slot-2',
            alias: 'x',
            addedAt: TARGET.slotAddedAtUtc,
            cardKey: 'wish-8',
          },
        }),
        'acknowledged',
        'run-2',
      ),
    );
    first.settled(TARGET, settlement(row('done'), 'acknowledged', 'run-1'));

    expect(
      httpMock.expectOne('/api/channels/olaf/wishes/8/complete-removal').request.body.slotId,
    ).toBe('slot-2');
    expect(httpMock.expectOne(URL_7).request.body.slotId).toBe('slot-1');
  });

  it('drops a finished line once the wish is no longer active, presented or removed, never an owed report', () => {
    startRun().settled(TARGET, settlement(row('done')));
    httpMock.expectOne(URL_7).flush(null, { status: 503, statusText: 'Down' });
    const other = tracker.progressFor('olaf', 8);
    other.starting();
    other.notStarted('setChanged');

    tracker.prune('olaf', [
      { id: 7, status: 'approved' },
      { id: 8, status: 'active' },
    ]);
    expect(stateOf(7)).toEqual({ kind: 'reportPending' });
    expect(stateOf(8)).toEqual({ kind: 'notStarted', reason: 'setChanged' });

    tracker.prune('olaf', [{ id: 8, status: 'approved' }]);
    expect(stateOf(8)).toBeNull();
  });
});
