import { HttpRequest, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  DELETE_DELAY_MS,
  DeleteQueueEmote,
  REPORT_TIMEOUT_MS,
  SevenTvDeleteService,
} from './seven-tv-delete.service';
import { RUN_DELAY_MS } from './seven-tv-run-engine';
import { CANCEL_SETTLE_GRACE_MS, SET_ENTRIES_READ_TIMEOUT_MS } from './seven-tv-run-settlement';
import { SyncRestoredInSetResponse } from './seven-tv-emote-set.model';
import {
  RestoreRunInfo,
  RestoreStartTarget,
  SevenTvRestoreService,
} from './seven-tv-restore.service';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { SevenTvTokenService } from './seven-tv-token.service';
import { flushApplied, flushWithoutResult } from './seven-tv-mutation.testing';

const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig.',
      rateLimited: 'Rate Limit.',
      networkError: 'Netzwerkfehler.',
      genericStatus: '7TV-Fehler ({{ status }}).',
      rateLimitedGaveUp: 'Übersprungen.',
    },
  },
};

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';
const RESYNC_ENDPOINT = '/api/channels/sensitron/resync';
const SYNC_RESTORED_ENDPOINT = '/api/seventv/emote-sets/set-1/sync-restored';
const SYNC_RESTORED_SET_2 = '/api/seventv/emote-sets/set-2/sync-restored';

/** The default target of these cases: a *tracked, active* set — since the operator decision
 *  2026-09-25 (#255) the only case in which the client resyncs itself any more (see the "resync
 *  after the report" describe block below), so the report is followed by exactly one `POST /resync`
 *  unless the answer already names the channel. `active: false` makes it a *non-active* set of the
 *  same tracked channel instead (`resyncChannelName` still names it, for
 *  `RestoreProgressSection`'s target line only — no request follows any more); `untracked: true` a
 *  set with no channel at all. */
function target(
  overrides: { setId?: string; channel?: string; active?: boolean; untracked?: boolean } = {},
): RestoreStartTarget {
  const setId = overrides.setId ?? 'set-1';
  const channel = overrides.channel ?? 'sensitron';
  const tracked = overrides.untracked !== true;
  const active = tracked && overrides.active !== false;
  return {
    setId,
    expectedChannelName: active ? channel : null,
    resyncChannelName: tracked && !active ? channel : null,
    hostChannelName: channel,
    setName: `Name of ${setId}`,
    ownerOrChannelLabel: tracked ? channel : 'Some Owner',
  };
}

/** A set-centric `sync-restored` answer (spec 5.3) — paper only by default. */
function restoredAnswer(
  overrides: Partial<SyncRestoredInSetResponse> = {},
): SyncRestoredInSetResponse {
  return {
    reportedCount: 1,
    channels: [],
    unresolvedChannel: null,
    resyncTriggered: [],
    ...overrides,
  };
}

const EMOTES: DeleteQueueEmote[] = [
  { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];
const THREE_EMOTES: DeleteQueueEmote[] = [
  ...EMOTES,
  { emoteId: 'internal-3', sevenTvEmoteId: '7tv-3', name: 'OMEGALUL' },
];

// #256 P3-1 (Plan-256 review): a closed run's own record — never goes through the engine, so it
// leaves `queue()` untouched — standing in for whatever the dock already shows when a *second*
// `startRestore` is refused (no token, or nothing left to queue). See either `it` that uses it.
const PREVIOUS_CLOSED_RUN: RestoreRunInfo = {
  runId: 'restore-previous',
  phase: 'closed',
  destructive: false,
  targetSetId: 'set-2',
  expectedChannelName: 'sensitron',
  resyncChannelName: null,
  hostChannelName: 'sensitron',
  setName: 'set-2',
  ownerOrChannelLabel: 'sensitron',
  result: { doneKeys: ['7tv-9'], items: [], startedAt: 0, finishedAt: 1 },
  syncReport: 'succeeded',
  syncReportReason: null,
  resyncTrigger: 'idle',
};

describe('SevenTvRestoreService', () => {
  let service: SevenTvRestoreService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    vi.spyOn(console, 'info').mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    service = TestBed.inject(SevenTvRestoreService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('keys every queue row by its 7TV id and alias', () => {
    service.startRestore(target(), EMOTES);

    expect(service.queue().map((item) => item.key)).toEqual(['7tv-1#PogU', '7tv-2#KEKW']);

    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
  });

  it('sends the ADD mutation with set id, emote id and the alias to restore under', () => {
    service.startRestore(target(), [EMOTES[0]]);

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.headers.get('Authorization')).toBe('Bearer write-token');
    // v4 dropped the ADD action in favour of a dedicated field, and the alias travels *inside* the
    // input object — pin both, not the vanished enum.
    expect(req.request.body.query).toContain('addEmote(id: { emoteId: $emoteId, alias: $alias })');
    expect(req.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      alias: 'PogU',
    });
    flushApplied(req);
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
  });

  // Regression guard for #149: v3 rejected any alias outside ASCII+emoji, umlauts included. v4
  // fixed that server-side, but only if the alias actually reaches the wire unmangled — this is
  // the case that would have caught the old `name`-as-sibling-argument shape just as well as a
  // stray transliteration.
  it('sends an alias containing an umlaut unmangled in the mutation variables', () => {
    service.startRestore(target(), [{ ...EMOTES[0], name: 'Gänsehosen' }]);

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      alias: 'Gänsehosen',
    });

    flushApplied(req);
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
  });

  // An entry without an alias (a removed transfer target's `null`) comes back through the ADD with
  // `alias: null` — 7TV's own default-name fallback — keyed with an empty suffix and shown under the
  // emote's default name.
  it('restores a null alias as an ADD without an alias, keyed id# and shown by its default name', () => {
    service.startRestore(target(), [
      {
        sevenTvEmoteId: '7tv-1',
        name: 'PogU',
        aliases: ['PogU', null],
        defaultName: 'PogDefault',
      },
    ]);

    expect(service.queue().map((item) => [item.key, item.name])).toEqual([
      ['7tv-1#PogU', 'PogU'],
      ['7tv-1#', 'PogDefault'],
    ]);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);
    const aliasless = httpMock.expectOne(GQL_ENDPOINT);
    expect(aliasless.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      alias: null,
    });
    flushApplied(aliasless);
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
  });

  it('reports the finished run to the set-centric sync-restored with the ids and no expected channel for a non-active set', () => {
    service.startRestore(target({ active: false }), EMOTES);

    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);

    const reportReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      expectedChannelName: null,
    });
    expect(service.syncReport()).toBe('pending');
    reportReq.flush(restoredAnswer());

    expect(service.syncReport()).toBe('succeeded');
    // #255: a non-active tracked target no longer triggers its own resync.
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  // AK 15, F8: a touched channel whose count falls short of the reported ids is partial/shortfall.
  it('marks the report partial/shortfall when a channel restored fewer than reported', () => {
    service.startRestore(target({ active: true }), [EMOTES[0]]);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(
      restoredAnswer({
        channels: [{ channelName: 'sensitron', restoredCount: 0, notFoundIds: ['7tv-1'] }],
        resyncTriggered: ['sensitron'],
      }),
    );

    expect(service.syncReport()).toBe('partial');
    expect(service.syncReportReason()).toBe('shortfall');
  });

  it('marks the report failed on a 401 and re-sends it via retrySyncReport()', () => {
    service.startRestore(target({ active: false }), [EMOTES[0]]);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);

    // 401 is not retried automatically — waiting cannot fix an expired session.
    httpMock
      .expectOne(SYNC_RESTORED_ENDPOINT)
      .flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(service.syncReport()).toBe('failed');

    // #255: a non-active tracked target gets no resync of its own, not even the N1 fallback.
    httpMock.expectNone(RESYNC_ENDPOINT);

    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(retryReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      expectedChannelName: null,
    });
    retryReq.flush(restoredAnswer());

    expect(service.syncReport()).toBe('succeeded');
    expect(service.syncReportReason()).toBeNull();
    // A manual retry re-sends the report only, never a second resync.
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  // Sonde 5, branch A (spec 7.2, AK 69): 7TV takes the same emote under two aliases, so a protocol
  // row of a #74 duplicate becomes one queue row — and one ADD — per alias. The bookkeeping report
  // still names the emote once.
  it('restores a row with two aliases as two ADDs keyed id#alias, reported as one 7TV id', () => {
    service.startRestore(target({ active: false }), [
      { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] },
    ]);

    expect(service.queue().map((item) => item.key)).toEqual(['7tv-1#PogU', '7tv-1#PogU2']);

    const firstAdd = httpMock.expectOne(GQL_ENDPOINT);
    expect(firstAdd.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      alias: 'PogU',
    });
    flushApplied(firstAdd);
    vi.advanceTimersByTime(RUN_DELAY_MS);
    const secondAdd = httpMock.expectOne(GQL_ENDPOINT);
    expect(secondAdd.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      alias: 'PogU2',
    });
    flushApplied(secondAdd);
    vi.advanceTimersByTime(RUN_DELAY_MS);

    const reportReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      expectedChannelName: null,
    });
    reportReq.flush(restoredAnswer());
    expect(service.syncReport()).toBe('succeeded');
    // #255: no resync for a non-active tracked target.
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  // AK 71: the set is frozen into the run record at the start — the report and the manual retry
  // name it even when the next run the page asks for names another set.
  it('reports and retries with the set id frozen at the start of the run', () => {
    service.startRestore(target({ active: false }), [EMOTES[0]]);
    service.startRestore(target({ setId: 'set-2', active: false }), [EMOTES[1]]);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);

    // The set is in the route now: the report is addressed to set-1, never to set-2.
    const firstReport = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    httpMock.expectNone(SYNC_RESTORED_SET_2);
    firstReport.flush({}, { status: 401, statusText: 'Unauthorized' });
    // #255: no resync for a non-active tracked target, not even the N1 fallback.
    httpMock.expectNone(RESYNC_ENDPOINT);

    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(retryReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1'],
      expectedChannelName: null,
    });
    retryReq.flush(restoredAnswer());
  });

  // Spec 5.1, E3: only the set-centric body — also for a protocol row that never had a local emote.
  it('sends only the set-centric body form for a row without an emoteId, never the legacy emoteIds', () => {
    service.startRestore(target(), [{ sevenTvEmoteId: '7tv-live', name: 'LiveOnly' }]);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);

    const reportReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(Object.keys(reportReq.request.body).sort()).toEqual([
      'expectedChannelName',
      'sevenTvEmoteIds',
    ]);
    expect(reportReq.request.body.sevenTvEmoteIds).toEqual(['7tv-live']);
    reportReq.flush(restoredAnswer());
  });

  // AK 15, spec 6.4: `channels: []` without an `unresolvedChannel` is paper only — no channel to
  // touch, a plain success without a reason.
  it('treats a paper-only answer (channels: [], no unresolvedChannel) as succeeded, without a reason', () => {
    service.startRestore(target(), [EMOTES[0]]);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());

    expect(service.syncReport()).toBe('succeeded');
    expect(service.syncReportReason()).toBeNull();
  });

  // #255: a plain success (whether or not the answer names any channel) no longer produces a
  // resync request of the client's own — see the dedicated "resync after the report" describe
  // block below, including the one remaining case that still fires one at all (the N1 fallback,
  // active set only, after a report that fails for good — its own cooldown case is covered there
  // too, "reads a 429 on the fallback resync as cooldown, not as a failure").

  it('skips both the report and the resync entirely when nothing was restored', () => {
    service.startRestore(target(), [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'set is full' }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(service.queue()[0].status).toBe('failed');
    expect(service.syncReport()).toBe('idle');
    expect(service.resyncTrigger()).toBe('idle');
    httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  it('reset() clears queue, report and resync state', () => {
    service.startRestore(target(), [EMOTES[0]]);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());

    service.reset();

    expect(service.queue()).toEqual([]);
    expect(service.syncReport()).toBe('idle');
    expect(service.resyncTrigger()).toBe('idle');
  });

  // #149/T5: this service does not filter `emotes` itself (its callers — restore-flow.ts,
  // mass-delete-panel.ts — do, via already-present-filter.ts, before ever calling startRestore).
  // What it owns is surfacing the caller's skip count to the user, including in the one case a
  // caller could otherwise leave silent: every row was a duplicate, so nothing gets queued at all.
  describe('skippedDuplicates (#149/T5)', () => {
    it('defaults to 0 when the caller omits it', () => {
      service.startRestore(target(), [EMOTES[0]]);

      expect(service.skippedDuplicates()).toBe(0);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('reports the caller-supplied skip count even when every row was a duplicate and nothing queues', () => {
      // A second restore over rows already restored: the caller's pre-run filter (T5) removed
      // every row, leaving an empty list — the engine refuses to start on an empty queue, but the
      // skip count must still reach the user rather than the run silently doing nothing.
      //
      // #256 P3-1 (Plan-256 review, "open() vor start()"): a previous, already-closed run is shown
      // on the dock when this refused start comes in. `startRestore` opens its own record *before*
      // asking the engine to start (#256 review finding) — a refused start must take that record
      // back (`discardUnstarted`) rather than leave it dangling in the lifecycle's map, or
      // `isSettling` would leak `true` forever and the previous run's dock would be silently
      // replaced.
      service.run.set(PREVIOUS_CLOSED_RUN);

      service.startRestore(target(), [], 2);

      expect(service.isRunning()).toBe(false);
      expect(service.queue()).toEqual([]);
      expect(service.skippedDuplicates()).toBe(2);
      expect(service.destructiveOpen()).toBe(false);
      expect(service.isSettling()).toBe(false);
      expect(service.run()).toBe(PREVIOUS_CLOSED_RUN);
    });

    it('resets to 0 on the next call, even without duplicates', () => {
      service.startRestore(target(), [], 2);
      expect(service.skippedDuplicates()).toBe(2);

      service.startRestore(target(), [EMOTES[0]]);
      expect(service.skippedDuplicates()).toBe(0);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('reset() clears it back to 0', () => {
      service.startRestore(target(), [], 2);
      expect(service.skippedDuplicates()).toBe(2);

      service.reset();

      expect(service.skippedDuplicates()).toBe(0);
    });
  });

  // #149: whether the caller's pre-run check (already-present-filter.ts) actually ran — distinct
  // from skippedDuplicates above, which alone cannot tell "nothing to skip" apart from "could not
  // check". A caller that never passes the fifth argument (every pre-fix test above, and every
  // caller that predates this fix) must keep reading as "checked".
  describe('duplicateCheckAvailable (#149)', () => {
    it('defaults to true when the caller omits it', () => {
      service.startRestore(target(), [EMOTES[0]]);

      expect(service.duplicateCheckAvailable()).toBe(true);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('reports false when the caller says its check could not run, even though the run itself still starts', () => {
      service.startRestore(target(), [EMOTES[0]], 0, false);

      expect(service.duplicateCheckAvailable()).toBe(false);
      // Fails open, same as always — an unverifiable check does not block the confirmed run.
      expect(service.isRunning()).toBe(true);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('resets to true on the next call, even without a fifth argument', () => {
      service.startRestore(target(), [], 2, false);
      expect(service.duplicateCheckAvailable()).toBe(false);

      service.startRestore(target(), [EMOTES[0]]);
      expect(service.duplicateCheckAvailable()).toBe(true);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('reset() clears it back to true', () => {
      service.startRestore(target(), [], 2, false);
      expect(service.duplicateCheckAvailable()).toBe(false);

      service.reset();

      expect(service.duplicateCheckAvailable()).toBe(true);
    });
  });

  // Aliases the caller's pre-run check left out because another emote holds the name: carried apart
  // from the "already present" count, and — for a run where that leaves nothing to queue — the only
  // outcome there is, so it must open the notice window on its own and clear with reset().
  it('carries the name-taken count apart from skippedDuplicates, opening the notice even when nothing queues', () => {
    service.startRestore(target(), [], 0, true, 3);

    expect(service.queue()).toEqual([]);
    expect(service.skippedNameTaken()).toBe(3);
    expect(service.skippedDuplicates()).toBe(0);
    expect(service.duplicateNoticePending()).toBe(true);

    service.reset();

    expect(service.skippedNameTaken()).toBe(0);
  });

  // #149 P2 (independent review): a fully-refused (all-duplicates) startRestore leaves no run/queue
  // behind, so this transient flag is what lets `dockVisible()` (`usage-stats-page.ts`, via
  // `action-dock.ts`) mount the notice at all — and what lets it clear on its own afterwards rather
  // than requiring a dismiss control that, in that refused case, has nothing to attach to.
  describe('duplicateNoticePending (#149 P2)', () => {
    it('defaults to false when the caller omits skip info entirely', () => {
      service.startRestore(target(), [EMOTES[0]]);

      expect(service.duplicateNoticePending()).toBe(false);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('becomes true when the call reports a skip count, even for a refused (all-duplicates) run', () => {
      service.startRestore(target(), [], 2);

      expect(service.duplicateNoticePending()).toBe(true);
    });

    it('becomes true when the call reports the check unavailable, even with nothing skipped', () => {
      service.startRestore(target(), [EMOTES[0]], 0, false);

      expect(service.duplicateNoticePending()).toBe(true);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('clears itself after DUPLICATE_NOTICE_MS without any dismiss call', () => {
      service.startRestore(target(), [], 2);
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(3999);
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(1);
      expect(service.duplicateNoticePending()).toBe(false);
    });

    it("a second call within the window restarts it, rather than the first call's timer cutting the new notice short", () => {
      service.startRestore(target(), [], 2);
      vi.advanceTimersByTime(3000);

      service.startRestore(target({ setId: 'set-2', channel: 'other-channel' }), [], 3);
      vi.advanceTimersByTime(2000);

      // 5000 ms after the first call, but only 2000 ms after the second — still pending.
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(2000);
      expect(service.duplicateNoticePending()).toBe(false);
    });

    it('reset() clears it immediately, without waiting out the timer', () => {
      service.startRestore(target(), [], 2);
      expect(service.duplicateNoticePending()).toBe(true);

      service.reset();

      expect(service.duplicateNoticePending()).toBe(false);
    });
  });

  /** Runs one restore of `EMOTES[0]` into `restoreTarget` up to its closing report request. */
  function runOneRestoreToReport(
    restoreTarget: RestoreStartTarget,
    endpoint = SYNC_RESTORED_ENDPOINT,
  ) {
    service.startRestore(restoreTarget, [EMOTES[0]]);
    flushApplied(httpMock.expectOne(GQL_ENDPOINT));
    vi.advanceTimersByTime(RUN_DELAY_MS);
    return httpMock.expectOne(endpoint);
  }

  // Spec 6.4, E23, AK 15: the threeway reading with its reason, for every answer the report can get.
  describe('report outcome and reason (spec 6.4, AK 15)', () => {
    it('sends the tracked channel as the expected hit when the target is its active set', () => {
      const report = runOneRestoreToReport(target({ active: true }));

      expect(report.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1'],
        expectedChannelName: 'sensitron',
      });
      report.flush(
        restoredAnswer({
          channels: [{ channelName: 'sensitron', restoredCount: 1, notFoundIds: [] }],
          resyncTriggered: ['sensitron'],
        }),
      );
      expect(service.syncReport()).toBe('succeeded');
      expect(service.syncReportReason()).toBeNull();
    });

    // #255: kept apart from the notTracked case below — the two read differently to a user.
    it('reads an unresolved expected channel with reason activeSetDiffers as partial/channelMismatchActiveSetDiffers', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({
          unresolvedChannel: { channelName: 'sensitron', reason: 'activeSetDiffers' },
          resyncTriggered: ['sensitron'],
        }),
      );

      expect(service.syncReport()).toBe('partial');
      expect(service.syncReportReason()).toBe('channelMismatchActiveSetDiffers');
    });

    it('reads an unresolved expected channel with reason notTracked as partial/channelMismatchNotTracked', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({
          unresolvedChannel: { channelName: 'sensitron', reason: 'notTracked' },
        }),
      );

      expect(service.syncReport()).toBe('partial');
      expect(service.syncReportReason()).toBe('channelMismatchNotTracked');
    });

    // addendum N4, AK 40: nothing a retry could improve — the service refuses it, no request.
    it('refuses a manual retry of a report that ended partial/channelMismatchNotTracked', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({
          unresolvedChannel: { channelName: 'sensitron', reason: 'notTracked' },
        }),
      );

      service.retrySyncReport();

      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
      expect(service.syncReport()).toBe('partial');
    });

    it('reads a 403 as failed/forbidden, without an automatic retry', () => {
      runOneRestoreToReport(target({ untracked: true })).flush(null, {
        status: 403,
        statusText: 'Forbidden',
      });

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('forbidden');
      vi.advanceTimersByTime(10_000);
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
    });

    // #224: a set that vanished between the pre-check and the report must never read as success.
    it('reads a 404 after the retries as failed/setNotFound, never succeeded', () => {
      runOneRestoreToReport(target({ untracked: true })).flush(null, {
        status: 404,
        statusText: 'Not Found',
      });
      vi.advanceTimersByTime(2000);
      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush(null, { status: 404, statusText: 'Not Found' });
      vi.advanceTimersByTime(4000);
      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush(null, { status: 404, statusText: 'Not Found' });

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('setNotFound');
    });

    it('reads 429/503/network failures after the retries as failed/unavailable', () => {
      runOneRestoreToReport(target({ untracked: true })).flush(null, {
        status: 429,
        statusText: 'Too Many',
      });
      vi.advanceTimersByTime(2000);
      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush(null, { status: 503, statusText: 'Unavailable' });
      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).error(new ProgressEvent('error'));

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('unavailable');
    });

    // #256 P2-1 (Plan-256 review): a malformed 200 answer makes `classifySyncInSetResponse` throw
    // inside the `map` ahead of `retry` — this proves that throw is retried exactly like an HTTP
    // failure (the comment beside that `map` call explains why: a plain `TypeError`, not an
    // `HttpErrorResponse`, so `retry`'s 401/403 check never matches it) and, once the retries are
    // exhausted, still reaches an end state rather than leaving the run `reporting` forever.
    it('retries a malformed 200 answer that makes the classification throw, and closes the run once the retries are exhausted', () => {
      runOneRestoreToReport(target({ untracked: true })).flush(null);

      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(null);

      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(null);

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('other');
      expect(service.run()?.phase).toBe('closed');
      // Restore is never destructive (Plan-256 Festlegung 6) — checked here all the same, so a
      // future change to that constant would surface in this test too, not only in the dedicated
      // "always false" case elsewhere in this file.
      expect(service.destructiveOpen()).toBe(false);
    });

    // #256 P2-2 (Plan-256 review, Festlegung 15): a report that never answers must not keep its run
    // open for good — same contract and constant as the import's own version of this test
    // (seven-tv-import.service.spec.ts) and the delete's (seven-tv-delete.service.spec.ts).
    it('gives up a report without an answer after REPORT_TIMEOUT_MS per attempt and closes the run', () => {
      const firstAttempt = runOneRestoreToReport(target({ untracked: true }));
      vi.advanceTimersByTime(REPORT_TIMEOUT_MS - 1);
      expect(firstAttempt.cancelled).toBe(false);
      expect(service.run()?.phase).toBe('reporting');
      vi.advanceTimersByTime(1);
      expect(firstAttempt.cancelled).toBe(true);

      vi.advanceTimersByTime(2000);
      const secondAttempt = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      vi.advanceTimersByTime(REPORT_TIMEOUT_MS);
      expect(secondAttempt.cancelled).toBe(true);

      vi.advanceTimersByTime(4000);
      const thirdAttempt = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      vi.advanceTimersByTime(REPORT_TIMEOUT_MS);
      expect(thirdAttempt.cancelled).toBe(true);

      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('unavailable');
      expect(service.run()?.phase).toBe('closed');
      expect(service.destructiveOpen()).toBe(false);
    });
  });

  // Spec 6.4, F15, AK 21/27, operator decision 2026-09-25 (#255): the client's own resync follows
  // the report, and now only ever for the target's *active* set — a non-active tracked target no
  // longer gets a client resync of its own at all, whatever the report answers (see
  // docs/DECISIONS.md, 2026-09-25, and the design doc's §18 addendum, which supersedes the former
  // E12). `resyncChannelName` still names a non-active target's tracked channel, but only for
  // `RestoreProgressSection`'s target line, never here any more.
  describe('resync after the report (spec 6.4, AK 21/27, #255)', () => {
    // Neither before nor after a successful report that does not name the channel does the client
    // request anything of its own — the backend's own resync (E17) covers the active set
    // unconditionally; only the dock's `'backendTriggered'` display depends on the answer actually
    // naming it (see the next tests).
    it('sends no resync before the report has answered, nor after a plain success that does not name the channel', () => {
      const report = runOneRestoreToReport(target({ active: true }));

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('idle');

      report.flush(restoredAnswer());
      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('idle');
    });

    it('sends no resync of its own when the answer names the channel, and says the backend is on it', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({ resyncTriggered: ['sensitron'] }),
      );

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('backendTriggered');
    });

    // Spec 6.4 ("außer der Kanal steht in resyncTriggered"), 4.4 point 11: for the active set the
    // backend covers the resync, and when its answer names the expected channel the dock says "being
    // re-synced" — without a request of ours. Compared case-insensitively.
    it('sends no resync for the active set of a tracked channel and says the backend is on it when the answer names the expected channel', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({
          channels: [{ channelName: 'sensitron', restoredCount: 1, notFoundIds: [] }],
          resyncTriggered: ['Sensitron'],
        }),
      );

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('backendTriggered');
    });

    // The same for a stale active set (E18, activeSetDiffers): the backend resyncs the unresolved
    // expected channel and names it.
    it('says the backend is on it for an unresolved expected channel it names in resyncTriggered', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({
          unresolvedChannel: { channelName: 'sensitron', reason: 'activeSetDiffers' },
          resyncTriggered: ['sensitron'],
        }),
      );

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('backendTriggered');
    });

    // Not named (cooldown not acquired, F15, or notTracked): no request of ours, no resync line.
    it('stays idle without a request for the active set when the answer does not name the expected channel', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({
          channels: [{ channelName: 'sensitron', restoredCount: 1, notFoundIds: [] }],
          resyncTriggered: [],
        }),
      );

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('idle');
    });

    // addendum N1, AK 36: a report that fails for good never reached the backend's resync stage, so
    // the client stands in with `expectedChannelName` — active-set only since #255.
    it('resyncs the expected channel itself when the report for its active set fails for good', () => {
      runOneRestoreToReport(target({ active: true })).flush(null, {
        status: 503,
        statusText: 'Unavailable',
      });
      vi.advanceTimersByTime(2000);
      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush(null, { status: 503, statusText: 'Unavailable' });
      httpMock.expectNone(RESYNC_ENDPOINT);
      vi.advanceTimersByTime(4000);
      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush(null, { status: 503, statusText: 'Unavailable' });

      expect(service.syncReport()).toBe('failed');
      const resync = httpMock.expectOne(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('pending');
      resync.flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.resyncTrigger()).toBe('succeeded');
    });

    it('reads a 429 on the fallback resync as cooldown, not as a failure', () => {
      runOneRestoreToReport(target({ active: true })).flush(null, {
        status: 403,
        statusText: 'Forbidden',
      });

      httpMock
        .expectOne(RESYNC_ENDPOINT)
        .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });
      expect(service.resyncTrigger()).toBe('cooldown');
    });

    it('sends no second fallback resync for a manual retry that fails again', () => {
      runOneRestoreToReport(target({ active: true })).flush(null, {
        status: 403,
        statusText: 'Forbidden',
      });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

      service.retrySyncReport();
      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush(null, { status: 403, statusText: 'Forbidden' });

      expect(service.syncReport()).toBe('failed');
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it('sends no resync for an untracked target, not even after a failed report', () => {
      runOneRestoreToReport(target({ untracked: true })).flush(null, {
        status: 403,
        statusText: 'Forbidden',
      });

      httpMock.expectNone((request) => request.url.endsWith('/resync'));
      expect(service.resyncTrigger()).toBe('idle');
    });

    // #255: a non-active tracked target's own resync (former E12) is gone entirely — the channel
    // resync only ever pulled the channel's *active* set view, never the non-active set the run
    // actually wrote to, so a client resync there could succeed while confirming nothing the user
    // cares about (same reasoning the import already follows for a non-active target,
    // `seven-tv-import.service.ts:657-671`).
    it('sends no resync for a non-active tracked target when the answer names the channel', () => {
      runOneRestoreToReport(target({ active: false })).flush(
        restoredAnswer({ resyncTriggered: ['sensitron'] }),
      );

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('idle');
    });

    it('sends no resync for a non-active tracked target when the answer names no channel', () => {
      runOneRestoreToReport(target({ active: false })).flush(restoredAnswer());

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('idle');
    });

    it('sends no resync for a non-active tracked target even after the report fails for good (N1 is active-only)', () => {
      runOneRestoreToReport(target({ active: false })).flush(null, {
        status: 403,
        statusText: 'Forbidden',
      });

      httpMock.expectNone((request) => request.url.endsWith('/resync'));
      expect(service.resyncTrigger()).toBe('idle');
    });
  });

  // F7, E13, AK 20: the dock belongs to the page the run was started on, not to its target.
  describe('resetIfChannelChanged (spec 6.4, AK 20)', () => {
    it('clears a finished run when the page moves off its host channel', () => {
      runOneRestoreToReport(target({ active: true })).flush(restoredAnswer());

      service.resetIfChannelChanged('other-channel');

      expect(service.queue()).toEqual([]);
      expect(service.run()).toBeNull();
      expect(service.syncReport()).toBe('idle');
    });

    it('compares against the host channel, not the target: a run into a foreign set stays on its own page', () => {
      const foreignTarget: RestoreStartTarget = {
        ...target({ channel: 'foreignchannel', active: true }),
        hostChannelName: 'sensitron',
      };
      runOneRestoreToReport(foreignTarget).flush(restoredAnswer());

      service.resetIfChannelChanged('sensitron');

      expect(service.queue()).toHaveLength(1);
      expect(service.run()?.hostChannelName).toBe('sensitron');
      expect(service.run()?.setName).toBe('Name of set-1');
      expect(service.run()?.ownerOrChannelLabel).toBe('foreignchannel');
    });

    it('leaves a running run alone, and its report still goes out afterwards', () => {
      service.startRestore(target({ active: true }), [EMOTES[0]]);

      service.resetIfChannelChanged('other-channel');

      expect(service.isRunning()).toBe(true);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });
  });

  // #256, Plan-256 Festlegungen 3, 6, 13: the run-bound lifecycle. `run`/`isSettling`/
  // `destructiveOpen` are the lifecycle's own signals; the report keeps going on the run's own
  // record whether or not the dock shows it.
  describe('#256 run lifecycle', () => {
    it('destructiveOpen stays false for a restore — only ADDs, never destructive', () => {
      expect(service.destructiveOpen()).toBe(false);

      service.startRestore(target({ active: true }), [EMOTES[0]]);
      expect(service.destructiveOpen()).toBe(false);

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      expect(service.destructiveOpen()).toBe(false); // reporting, but still never destructive

      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      expect(service.destructiveOpen()).toBe(false);
    });

    it('isSettling is true while the report is out, false once it closes', () => {
      expect(service.isSettling()).toBe(false);
      const reportReq = runOneRestoreToReport(target({ active: true }));
      expect(service.isSettling()).toBe(true);

      reportReq.flush(restoredAnswer());
      expect(service.isSettling()).toBe(false);
    });

    // Codex-Befund 1 on the plan: `reset()` during `running` must not cancel the engine — an ADD
    // already in flight when the display detaches can still be confirmed by 7TV afterwards, and the
    // run must still report it, even though nothing shows it any more.
    it('reset() while running lets the engine finish and still reports an ADD that was in flight', () => {
      service.startRestore(target({ active: true }), EMOTES);
      const inFlightReq = httpMock.expectOne(GQL_ENDPOINT);

      service.reset();

      expect(service.run()).toBeNull();
      expect(service.isRunning()).toBe(true); // the engine itself was not touched

      flushApplied(inFlightReq);
      vi.advanceTimersByTime(RUN_DELAY_MS);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);

      const reportReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      expect(reportReq.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1', '7tv-2'],
        expectedChannelName: 'sensitron',
      });
      reportReq.flush(restoredAnswer());

      expect(service.run()).toBeNull(); // still nothing shown — a success needs no reshow
    });

    // Festlegung 13, Codex-Befund 2: a channel switch mid-report must not drop a run whose report
    // could still fail — only a `closed` run follows resetIfChannelChanged's old "engine stopped"
    // rule. Non-active target: no resync noise to flush, the report alone is the point here.
    it('resetIfChannelChanged() during reporting leaves the run shown until it closes', () => {
      const reportReq = runOneRestoreToReport(target({ active: false }));

      service.resetIfChannelChanged('other-channel');
      expect(service.run()).not.toBeNull();
      expect(service.syncReport()).toBe('pending');

      reportReq.flush({}, { status: 403, statusText: 'Forbidden' });

      expect(service.run()).not.toBeNull();
      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('forbidden');

      // Only now, once closed, does a channel switch actually reset it.
      service.resetIfChannelChanged('other-channel');
      expect(service.run()).toBeNull();
    });

    // Festlegung 13: a programmatic reset() detaches a run whose report has not answered yet; if
    // that report then does not succeed, the run shows itself again so its reason and retry stay
    // reachable — unless something else is shown by then, in which case the failure only reaches
    // the console.
    it('shows a detached run again once its report fails, and lets a retry send from there', () => {
      const reportReq = runOneRestoreToReport(target({ active: false }));
      service.reset();
      expect(service.run()).toBeNull();

      reportReq.flush({}, { status: 403, statusText: 'Forbidden' });

      expect(service.run()).not.toBeNull();
      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('forbidden');

      service.retrySyncReport();
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    it('does not reshow a failed report once a newer run is shown, and logs it instead', () => {
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
      const reportReq = runOneRestoreToReport(target({ active: false }));
      service.reset();

      service.startRestore(target({ setId: 'set-2', channel: 'other-channel', active: false }), [
        EMOTES[1],
      ]);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      const run2ReportReq = httpMock.expectOne(SYNC_RESTORED_SET_2);
      expect(service.isRunning()).toBe(false); // run 2's engine work is done, only its report is out
      expect(service.run()?.targetSetId).toBe('set-2');

      reportReq.flush({}, { status: 403, statusText: 'Forbidden' });

      expect(service.run()?.targetSetId).toBe('set-2'); // run 1's failure did not take the dock back
      expect(warnSpy).toHaveBeenCalledWith(
        '[EmotePurge] 7TV restore report of a run no longer shown did not succeed',
        expect.objectContaining({ state: 'failed', reason: 'forbidden' }),
      );

      run2ReportReq.flush(restoredAnswer());
    });
  });

  // #256 P2 (Plan-256-Robustheit review, branch-review round): a manual retry used to leave the
  // record on its previous end state until the retry's own answer came in — the retry button stayed
  // up for a second, parallel report, and a `closed`-but-nothing-pending record could not survive a
  // "Close" click mid-retry. `reportRestored` now patches the record to `pending` before sending,
  // mirroring the import's `reportImported`/`reportRemoved` — same fix as the delete service's.
  describe('#256 P2: a manual retry marks the record pending before sending', () => {
    it('retrySyncReport() patches the record to pending before the request goes out', () => {
      runOneRestoreToReport(target({ active: false })).flush(
        {},
        {
          status: 403,
          statusText: 'Forbidden',
        },
      );
      expect(service.syncReport()).toBe('failed');

      service.retrySyncReport();

      // Neither 'failed' nor 'partial' — run-progress-panel's `syncReportFailed` computed reads
      // this and hides the retry button/reason line the moment it is not one of those two.
      expect(service.syncReport()).toBe('pending');
      expect(service.syncReportReason()).toBeNull();

      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    it('a second click while the retry is out sends nothing', () => {
      runOneRestoreToReport(target({ active: false })).flush(
        {},
        {
          status: 403,
          statusText: 'Forbidden',
        },
      );

      service.retrySyncReport();
      const retryReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);

      service.retrySyncReport(); // no-op: syncReport is already 'pending'
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);

      retryReq.flush(restoredAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    it('isSettling and destructiveOpen stay false while a closed run’s retry is out', () => {
      runOneRestoreToReport(target({ active: false })).flush(
        {},
        {
          status: 403,
          statusText: 'Forbidden',
        },
      );
      expect(service.run()?.phase).toBe('closed');

      service.retrySyncReport();

      // `closed` is a one-way door (#256): the retry never reopens the phase, so neither signal —
      // both derived from the phase, not from `syncReport` — sees this run as busy again. Restore's
      // destructiveOpen is always false anyway, checked here for parity with the delete service.
      expect(service.isSettling()).toBe(false);
      expect(service.destructiveOpen()).toBe(false);

      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('reshows a run whose manual retry fails after the dock was closed mid-retry', () => {
      runOneRestoreToReport(target({ active: false })).flush(
        {},
        {
          status: 403,
          statusText: 'Forbidden',
        },
      );

      service.retrySyncReport();
      const retryReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      service.reset();
      expect(service.run()).toBeNull();

      // Without the pending patch, this record had already left the lifecycle's map (`closed`,
      // nothing pending) the moment reset() detached it, and this answer would have found no
      // record at all — no reshow, no console.warn.
      retryReq.flush({}, { status: 403, statusText: 'Forbidden' });

      expect(service.run()).not.toBeNull();
      expect(service.syncReport()).toBe('failed');
      expect(service.syncReportReason()).toBe('forbidden');
    });
  });

  // #256 P3 (Plan-256-Robustheit review, branch-review round): the success-path counterpart to
  // "reset() while running lets the engine finish and still reports an ADD that was in flight" —
  // this one detaches while the *report itself* (not the engine) is in flight, and the report
  // succeeds.
  it('#256 P3: reset() while the report is in flight still lands a successful answer on the record exactly once, closing isSettling only after', () => {
    const reportReq = runOneRestoreToReport(target({ active: true }));
    expect(service.isSettling()).toBe(true);

    service.reset();
    expect(service.run()).toBeNull();
    expect(service.isSettling()).toBe(true); // still open — the report has not answered yet

    reportReq.flush(restoredAnswer());

    // httpMock's own afterEach.verify() proves the answer landed exactly once (no leftover, no
    // second request); a success needs no reshow.
    expect(service.isSettling()).toBe(false);
    expect(service.run()).toBeNull();
  });

  // R15 (#72, T12): finish() flips isRunning() to false *before* the two closing calls resolve, so
  // a second run can legitimately start while the first one's report/resync are still in flight.
  // Their late answers must not land on the second run's state.
  describe('superseded run (R15)', () => {
    // #255: the only path left that still makes the client resync itself is the N1 fallback
    // (active set, report failed for good) — a plain success no longer produces a request to
    // guard against, so both runs here fail their report (401, no automatic retry) rather than
    // succeed, to exercise that path.
    it('discards a late sync-restored answer from a superseded run without touching the new one', () => {
      service.startRestore(target({ active: true }), [EMOTES[0]]);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);

      const staleSyncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      expect(service.syncReport()).toBe('pending');

      // A second run starts, for a different channel, before run 1's sync-restored answer comes
      // back — legitimate, because finish() already flipped isRunning() to false.
      service.startRestore(target({ setId: 'set-2', channel: 'other-channel', active: true }), [
        EMOTES[1],
      ]);
      expect(service.isRunning()).toBe(true);
      expect(service.syncReport()).toBe('idle'); // run 2's own state, reset at start

      staleSyncReq.flush({}, { status: 401, statusText: 'Unauthorized' });
      expect(service.syncReport()).toBe('idle'); // still run 2's state, untouched by run 1's answer
      // #256 P3-4 (Plan-256 review): run 1's late answer is still verbucht on *its own* record and
      // closes it (Plan-256 Festlegung 2, identity by `runId`) — `isSettling` here reads run 2's own
      // state, not a stale leftover of run 1's: run 2 is still `running` (its own GQL mutation has
      // not even been flushed yet), so `isSettling` is false. `destructiveOpen` is always false for
      // restore (Plan-256 Festlegung 6) — checked here all the same as a run-1-leaked-open guard.
      expect(service.isSettling()).toBe(false);
      expect(service.destructiveOpen()).toBe(false);
      // Run 1's resync still goes out — it is owed to 7TV's state, not to the dock — but its answer
      // does not land on run 2's state either.
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.resyncTrigger()).toBe('idle');

      // Run 2 finishes normally afterwards — the guard must not have swallowed its own terminal
      // flank along with the stale one.
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock
        .expectOne(SYNC_RESTORED_SET_2)
        .flush({}, { status: 401, statusText: 'Unauthorized' });
      expect(service.syncReport()).toBe('failed');
      httpMock
        .expectOne('/api/channels/other-channel/resync')
        .flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('never lets a stale resync answer overwrite a later state — including "cooldown"', () => {
      service.startRestore(target({ active: true }), [EMOTES[0]]);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush({}, { status: 401, statusText: 'Unauthorized' });
      const staleResyncReq = httpMock.expectOne(RESYNC_ENDPOINT);

      // A second run starts, runs to completion, and its own resync lands in cooldown — a real
      // state, not the guard's doing.
      service.startRestore(target({ setId: 'set-2', channel: 'other-channel', active: true }), [
        EMOTES[1],
      ]);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock
        .expectOne(SYNC_RESTORED_SET_2)
        .flush({}, { status: 401, statusText: 'Unauthorized' });
      httpMock
        .expectOne('/api/channels/other-channel/resync')
        .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });
      expect(service.resyncTrigger()).toBe('cooldown');

      // Run 1's late resync answer must not disturb run 2's already-settled 'cooldown'.
      staleResyncReq.flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.resyncTrigger()).toBe('cooldown');
    });
  });

  // The arbiter (#70, Task 4) has no lock of its own — it reads the signals this service registers
  // with it (#256: isRunning, isSettling, destructiveOpen), so these cases pin the invariants a hand-kept tryAcquire/release could not have
  // guaranteed (see R1 in docs/DECISIONS.md): the derived state can never outlive the run it
  // describes, not even across cancel(), a start the engine itself refused, or a hand-off to the
  // sibling delete service once this run has ended.
  describe('run arbiter', () => {
    let arbiter: SevenTvRunArbiter;

    beforeEach(() => {
      arbiter = TestBed.inject(SevenTvRunArbiter);
    });

    it('reports "restore" as the active run while this service runs, then null once it ends', () => {
      service.startRestore(target(), [EMOTES[0]]);

      expect(arbiter.activeRun()).toBe('restore');

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      // #255: a plain success naming nothing (the default `restoredAnswer()`) produces no resync
      // request for the active set any more — nothing left to drain here.
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());

      expect(arbiter.activeRun()).toBeNull();
    });

    it('clears the active run once a run ended by cancel() has had its report answered', () => {
      service.startRestore(target(), EMOTES);
      flushApplied(httpMock.expectOne(GQL_ENDPOINT));

      service.cancel();

      // #256 (contract P2): the confirmed row is still being reported — the arbiter counts that
      // settling window as busy, and frees up only once the report has an end state. #255: no
      // resync follows a plain success that names nothing.
      expect(service.isRunning()).toBe(false);
      expect(arbiter.activeRun()).toBe('restore');

      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());

      expect(arbiter.activeRun()).toBeNull();
    });

    it('leaves no active run when the engine refuses the start for a cleared token', () => {
      // #256 P3-1 — same reasoning as the empty-list case above.
      service.run.set(PREVIOUS_CLOSED_RUN);
      tokenService.clearToken();

      service.startRestore(target(), EMOTES);

      expect(service.isRunning()).toBe(false);
      expect(arbiter.activeRun()).toBeNull();
      expect(service.destructiveOpen()).toBe(false);
      expect(service.isSettling()).toBe(false);
      expect(service.run()).toBe(PREVIOUS_CLOSED_RUN);
    });

    it('lets a delete start once this restore has ended — the cross-service invariant a held lock could not guarantee', () => {
      const deleteService = TestBed.inject(SevenTvDeleteService);

      service.startRestore(target(), [EMOTES[0]]);
      expect(arbiter.activeRun()).toBe('restore');

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());

      expect(arbiter.activeRun()).toBeNull();

      deleteService.startDelete('set-1', 'sensitron', [EMOTES[1]], 'sensitron');

      expect(arbiter.activeRun()).toBe('delete');

      flushApplied(httpMock.expectOne(GQL_ENDPOINT));
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne('/api/seventv/emote-sets/set-1/sync-deleted').flush({
        reportedCount: 1,
        channels: [{ channelName: 'sensitron', archivedCount: 1, notFoundIds: [] }],
        unresolvedChannel: null,
        resyncTriggered: ['sensitron'],
      });
    });
  });

  // #275 (Plan-275 Festlegungen 1, 2, 5, 6, 8, 10–13, 19, 21; mirrors the delete's own "#275
  // settling an unknown REMOVE" block): an `ADD` whose answer was lost, or that a cancel aborted in
  // flight, ends its row `unknown`; the run is then `settling` while the target set is read once,
  // the read only ever confirms (alias/default name present ⇒ `done`), and only the settled outcome
  // is published and reported.
  describe('#275 settling an unknown ADD', () => {
    const isAdd = (request: HttpRequest<unknown>) =>
      request.url === GQL_ENDPOINT &&
      (request.body as { query: string }).query.includes('addEmote(');
    const isSetRead = (request: HttpRequest<unknown>) =>
      request.url === GQL_ENDPOINT &&
      (request.body as { query: string }).query.includes('emotes(page: $page, perPage: $perPage)');

    /** One page of the tokenless set read (`loadSevenTvSetEntries`) holding exactly `entries`. */
    function setEntriesPage(entries: { id: string; alias: string | null; defaultName?: string }[]) {
      return {
        data: {
          emoteSets: {
            emoteSet: {
              emotes: {
                totalCount: entries.length,
                pageCount: 1,
                items: entries.map((entry) => ({
                  alias: entry.alias,
                  emote: { id: entry.id, defaultName: entry.defaultName },
                })),
              },
            },
          },
        },
      };
    }

    /** Starts a one-row run (EMOTES[0], key `7tv-1#PogU`) and cancels it while its ADD is in
     *  flight. */
    function cancelOneRowInFlight(overrides: Parameters<typeof target>[0] = {}) {
      service.startRestore(target(overrides), [EMOTES[0]]);
      const add = httpMock.expectOne(isAdd);
      service.cancel();
      expect(add.cancelled).toBe(true);
    }

    /** Lets the cancel's grace period run out and returns the one re-read it then sends. */
    function readAfterGrace() {
      vi.advanceTimersByTime(CANCEL_SETTLE_GRACE_MS);
      return httpMock.expectOne(isSetRead);
    }

    /** A first report that failed for good is followed by the client's fallback resync of the
     *  expected channel (addendum N1) — flushed here where a case is about something else. */
    function flushFallbackResync() {
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    }

    it('settles an ADD cancelled in flight: settling, no read before the grace period, then one read that confirms, then the report', () => {
      cancelOneRowInFlight();

      expect(service.isRunning()).toBe(false);
      expect(service.run()?.phase).toBe('settling');
      expect(service.isSettling()).toBe(true);
      // Restore is never destructive — not even while settling (Plan-275: no guard for restore).
      expect(service.destructiveOpen()).toBe(false);
      // Festlegung 10: nothing is published before the settle.
      expect(service.run()?.result).toBeNull();
      // Festlegung 11: the dock shows the engine's own snapshot meanwhile.
      expect(service.queue().map((item) => item.status)).toEqual(['unknown']);
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);

      vi.advanceTimersByTime(CANCEL_SETTLE_GRACE_MS - 1);
      httpMock.expectNone(isSetRead);
      vi.advanceTimersByTime(1);
      const read = httpMock.expectOne(isSetRead);
      expect(read.request.headers.has('Authorization')).toBe(false);
      expect(read.request.body.variables.id).toBe('set-1');
      read.flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));

      expect(service.run()?.phase).toBe('reporting');
      expect(service.queue()[0]).toMatchObject({ key: '7tv-1#PogU', status: 'done' });
      expect(service.run()?.result?.doneKeys).toEqual(['7tv-1#PogU']);
      const syncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      expect(syncReq.request.body).toEqual({
        sevenTvEmoteIds: ['7tv-1'],
        expectedChannelName: 'sensitron',
      });
      syncReq.flush(restoredAnswer());

      expect(service.run()?.phase).toBe('closed');
      expect(service.syncReport()).toBe('succeeded');
      expect(service.isSettling()).toBe(false);
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it('leaves the row unknown when the read shows the id only under a foreign alias, reports nothing and resyncs the active set', () => {
      cancelOneRowInFlight();

      // Under a different alias than the one restored — still not a confirmation, never a guess.
      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'SomeoneElse' }]));

      expect(service.run()?.phase).toBe('closed');
      expect(service.run()?.result?.items[0].status).toBe('unknown');
      expect(service.run()?.result?.doneKeys).toEqual([]);
      expect(service.syncReport()).toBe('idle');
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('leaves the row unknown when the id is missing from the set entirely, reports nothing and resyncs the active set', () => {
      cancelOneRowInFlight();

      readAfterGrace().flush(setEntriesPage([]));

      expect(service.run()?.result?.items[0].status).toBe('unknown');
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('sends no resync for an unknown-only run whose target is not the active set', () => {
      cancelOneRowInFlight({ active: false });

      readAfterGrace().flush(setEntriesPage([]));

      expect(service.run()?.phase).toBe('closed');
      expect(service.run()?.result?.items[0].status).toBe('unknown');
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it('sends no resync for an unknown-only run on an untracked target', () => {
      cancelOneRowInFlight({ untracked: true });

      readAfterGrace().flush(setEntriesPage([]));

      expect(service.run()?.result?.items[0].status).toBe('unknown');
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it.each([
      [
        'a failed read',
        (read: ReturnType<HttpTestingController['expectOne']>) =>
          read.error(new ProgressEvent('error')),
      ],
      [
        'a GraphQL-level rejection of the read',
        (read: ReturnType<HttpTestingController['expectOne']>) =>
          read.flush({ errors: [{ message: 'unavailable' }] }),
      ],
      [
        'an incomplete read',
        (read: ReturnType<HttpTestingController['expectOne']>) => {
          // The alias *is* on this page — a settle that ignored `complete: false` would wrongly
          // confirm the row. `totalCount` disagreeing with what the page carries is what
          // `loadSevenTvSetEntries` reads as incomplete (K5, `seven-tv-set-entries.ts`).
          const partial = setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]);
          partial.data.emoteSets.emoteSet.emotes.totalCount = 7;
          read.flush(partial);
        },
      ],
    ])('treats %s like no read at all: the row stays unknown, D6 resync', (_label, answer) => {
      cancelOneRowInFlight();

      answer(readAfterGrace());

      expect(service.run()?.phase).toBe('closed');
      expect(service.run()?.result?.items[0].status).toBe('unknown');
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('gives a read that never answers SET_ENTRIES_READ_TIMEOUT_MS, then settles without it', () => {
      cancelOneRowInFlight();
      const read = readAfterGrace();

      vi.advanceTimersByTime(SET_ENTRIES_READ_TIMEOUT_MS - 1);
      expect(service.run()?.phase).toBe('settling');
      vi.advanceTimersByTime(1);

      expect(read.cancelled).toBe(true);
      expect(service.run()?.phase).toBe('closed');
      expect(service.run()?.result?.items[0].status).toBe('unknown');
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it.each([
      [
        'an HTTP 503',
        (add: ReturnType<HttpTestingController['expectOne']>) =>
          add.flush('boom', { status: 503, statusText: 'Unavailable' }),
      ],
      [
        'no answer at all (status 0)',
        (add: ReturnType<HttpTestingController['expectOne']>) =>
          add.error(new ProgressEvent('error')),
      ],
      // #285: a 200 that neither rejects nor confirms the mutation is as unclear as a lost answer.
      [
        'an HTTP 200 with an empty body',
        (add: ReturnType<HttpTestingController['expectOne']>) => add.flush(null),
      ],
      [
        'an HTTP 200 with neither data nor errors',
        (add: ReturnType<HttpTestingController['expectOne']>) => add.flush({}),
      ],
      [
        'an HTTP 200 whose data stops short of the addEmote result',
        (add: ReturnType<HttpTestingController['expectOne']>) => flushWithoutResult(add),
      ],
    ])(
      'makes %s mid-run unknown, keeps the run going and reads right after it without a grace period',
      (_label, answer) => {
        service.startRestore(target(), THREE_EMOTES);
        flushApplied(httpMock.expectOne(isAdd));
        vi.advanceTimersByTime(RUN_DELAY_MS);
        answer(httpMock.expectOne(isAdd));
        expect(service.queue()[1].status).toBe('unknown');
        expect(service.isRunning()).toBe(true);
        vi.advanceTimersByTime(RUN_DELAY_MS);
        flushApplied(httpMock.expectOne(isAdd));
        vi.advanceTimersByTime(RUN_DELAY_MS);

        expect(service.run()?.phase).toBe('settling');
        // No grace period after a plain transport loss: the read is already out. Confirms exactly
        // the row that was lost — restore needs positive evidence, unlike the delete's "gone".
        httpMock.expectOne(isSetRead).flush(setEntriesPage([{ id: '7tv-2', alias: 'KEKW' }]));

        // One report after the settle, with the union in queue order.
        const syncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
        expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1', '7tv-2', '7tv-3']);
        syncReq.flush(restoredAnswer());
        httpMock.expectNone((request) => request.url.endsWith('/resync'));
      },
    );

    it('reports only the confirmed rows of a mixed run and leaves the rest to the report’s backend resync — no client resync', () => {
      service.startRestore(target(), THREE_EMOTES);
      flushApplied(httpMock.expectOne(isAdd));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(isAdd).flush('boom', { status: 502, statusText: 'Bad Gateway' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      flushApplied(httpMock.expectOne(isAdd));
      vi.advanceTimersByTime(RUN_DELAY_MS);

      // Nothing confirms the lost row's alias (KEKW) — it stays unknown.
      httpMock.expectOne(isSetRead).flush(setEntriesPage([]));

      expect(service.run()?.result?.items.map((item) => item.status)).toEqual([
        'done',
        'unknown',
        'done',
      ]);
      const syncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1', '7tv-3']);
      syncReq.flush(restoredAnswer({ reportedCount: 2, resyncTriggered: ['sensitron'] }));

      expect(service.run()?.phase).toBe('closed');
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it('resyncs exactly once when a mixed run’s report fails for good', () => {
      service.startRestore(target(), THREE_EMOTES);
      flushApplied(httpMock.expectOne(isAdd));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(isAdd).flush('boom', { status: 502, statusText: 'Bad Gateway' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      flushApplied(httpMock.expectOne(isAdd));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(isSetRead).flush(setEntriesPage([{ id: '7tv-2', alias: 'KEKW' }]));

      // A 403 is final — no automatic retry, straight to the N1 fallback.
      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush(null, { status: 403, statusText: 'Forbidden' });

      expect(service.syncReport()).toBe('failed');
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      vi.advanceTimersByTime(10_000);
      httpMock.expectNone((request) => request.url.endsWith('/resync'));
    });

    it('settles a cancel in flight after already confirmed rows into one report, not two', () => {
      service.startRestore(target(), EMOTES);
      flushApplied(httpMock.expectOne(isAdd));
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(isAdd);
      service.cancel();

      expect(service.queue().map((item) => item.status)).toEqual(['done', 'unknown']);
      httpMock.expectNone(SYNC_RESTORED_ENDPOINT); // the confirmed row waits for the settle as well

      // Nothing confirms KEKW — the second row stays unknown, only the first is reported.
      readAfterGrace().flush(setEntriesPage([]));

      const syncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
      syncReq.flush(restoredAnswer());
    });

    it('also waits out the grace period when a cancel between rows ends a run with an older transport loss', () => {
      service.startRestore(target(), EMOTES);
      httpMock.expectOne(isAdd).flush('boom', { status: 500, statusText: 'Server Error' });
      service.cancel(); // between rows: nothing in flight

      expect(service.queue().map((item) => item.status)).toEqual(['unknown', 'cancelled']);
      httpMock.expectNone(isSetRead);

      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));

      expect(httpMock.expectOne(SYNC_RESTORED_ENDPOINT).request.body.sevenTvEmoteIds).toEqual([
        '7tv-1',
      ]);
    });

    // Sonde 5, branch A / #74: two aliases of one duplicate cell are two rows, `7tv-1#PogU` and
    // `7tv-1#PogU2` — a cancel in flight only ever hits one of the two ADDs, and the settle clears
    // each row independently of the other's fate.
    describe('two aliases of one duplicate cell (#74)', () => {
      function startTwoAliasRow() {
        service.startRestore(target(), [
          { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU', 'PogU2'] },
        ]);
      }

      it('confirms only the alias the read shows and leaves the other row unknown, reporting the id once', () => {
        startTwoAliasRow();
        flushApplied(httpMock.expectOne(isAdd)); // PogU lands
        vi.advanceTimersByTime(RUN_DELAY_MS);
        httpMock.expectOne(isAdd); // PogU2 in flight
        service.cancel();

        expect(service.queue().map((item) => item.status)).toEqual(['done', 'unknown']);

        // Only the first alias shows up on the read — the second stays unclear.
        readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));

        expect(service.run()?.result?.items.map((item) => item.status)).toEqual([
          'done',
          'unknown',
        ]);
        const syncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
        expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
        syncReq.flush(restoredAnswer());
      });

      it('confirms both aliases once the read shows both, reporting the id exactly once', () => {
        startTwoAliasRow();
        flushApplied(httpMock.expectOne(isAdd)); // PogU lands
        vi.advanceTimersByTime(RUN_DELAY_MS);
        httpMock.expectOne(isAdd); // PogU2 in flight
        service.cancel();

        readAfterGrace().flush(
          setEntriesPage([
            { id: '7tv-1', alias: 'PogU' },
            { id: '7tv-1', alias: 'PogU2' },
          ]),
        );

        expect(service.run()?.result?.items.map((item) => item.status)).toEqual(['done', 'done']);
        const syncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
        // One id, even though both of its rows ended done (`doneSevenTvEmoteIds` dedupes).
        expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
        syncReq.flush(restoredAnswer());
      });
    });

    // Aliasless entries only ever come from a transfer-run protocol file (Spec #254 F5): 7TV names
    // an aliasless ADD after the emote's current default name.
    describe('a null-alias row from a transfer-run source', () => {
      function startNullAliasRow(defaultName: string | undefined = 'PogDefault') {
        service.startRestore(target(), [
          { sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: [null], defaultName },
        ]);
        const add = httpMock.expectOne(isAdd);
        expect(add.request.body.variables.alias).toBeNull();
        service.cancel();
      }

      it('confirms it done via aliaslessIds once the read shows the id sitting aliasless', () => {
        startNullAliasRow();

        readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: null }]));

        expect(service.run()?.result?.items[0].status).toBe('done');
        const syncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
        expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
        syncReq.flush(restoredAnswer());
      });

      it('confirms it done via the live default name when 7TV named it instead of leaving it aliasless', () => {
        startNullAliasRow();

        readAfterGrace().flush(
          setEntriesPage([{ id: '7tv-1', alias: 'PogDefault', defaultName: 'PogDefault' }]),
        );

        expect(service.run()?.result?.items[0].status).toBe('done');
        httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      });

      it('stays unknown when neither aliasless nor the (file-recorded) default name shows up in the read', () => {
        startNullAliasRow();

        readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'SomeoneElsesAlias' }]));

        expect(service.run()?.result?.items[0].status).toBe('unknown');
        httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
        httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      });

      // Pins the live-first ordering (Plan-275 Festlegung 8): only the *stale* file-recorded name
      // ('PogDefault', from `startNullAliasRow`'s default) sits in the set — under a live default
      // name that disagrees with it. A settle that checked the file's name ahead of the live one
      // would wrongly confirm this row; checking the live name first (which the read says is
      // 'CurrentLiveName', not 'PogDefault') correctly leaves it unclear.
      it('stays unknown when only the stale file-recorded default name sits in the set, not the current live one', () => {
        startNullAliasRow();

        readAfterGrace().flush(
          setEntriesPage([{ id: '7tv-1', alias: 'PogDefault', defaultName: 'CurrentLiveName' }]),
        );

        expect(service.run()?.result?.items[0].status).toBe('unknown');
        httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
        httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      });
    });

    it('keeps the settled result referentially stable across the later report patches', () => {
      cancelOneRowInFlight();
      expect(service.run()?.result).toBeNull();

      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));
      const settled = service.run()?.result;
      expect(settled).toBeDefined();

      httpMock
        .expectOne(SYNC_RESTORED_ENDPOINT)
        .flush(null, { status: 401, statusText: 'Unauthorized' });
      flushFallbackResync();
      expect(service.run()?.result).toBe(settled);

      service.retrySyncReport();
      const retryReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      // The retry reads the settled doneKeys — the row the re-read confirmed is in it.
      expect(retryReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
      retryReq.flush(restoredAnswer());
      expect(service.run()?.result).toBe(settled);
      expect(service.syncReport()).toBe('succeeded');
    });

    it('refuses a manual retry while the run is still settling', () => {
      cancelOneRowInFlight();

      service.retrySyncReport();

      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    // Festlegung 19: reset() while settling only drops the display; the record settles, reports and
    // closes on its own, and a report that then fails shows the run again with its retry.
    it('reset() while settling detaches the display; the run still settles, reports, and a failed report comes back with its retry', () => {
      cancelOneRowInFlight();

      service.reset();

      expect(service.run()).toBeNull();
      expect(service.queue()).toEqual([]);
      expect(service.isSettling()).toBe(true);

      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));
      expect(service.run()).toBeNull(); // settled on its own record, still not shown
      const syncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      expect(syncReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
      syncReq.flush(null, { status: 403, statusText: 'Forbidden' });
      flushFallbackResync();

      expect(service.run()?.phase).toBe('closed');
      expect(service.syncReport()).toBe('failed');
      expect(service.queue().map((item) => item.status)).toEqual(['done']);

      service.retrySyncReport();
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      expect(service.syncReport()).toBe('succeeded');
    });

    // D6 (a) fires for a detached run too (#256: a run completes run-bound whether or not it is
    // shown) — the resync is owed to 7TV's state, not to what the dock shows.
    it('resyncs the active target for a detached run that settles with nothing but unknown rows', () => {
      cancelOneRowInFlight();

      service.reset();
      expect(service.run()).toBeNull();

      readAfterGrace().flush(setEntriesPage([])); // confirms nothing

      httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('resetIfChannelChanged() leaves a settling run shown', () => {
      cancelOneRowInFlight();

      service.resetIfChannelChanged('other-channel');

      expect(service.run()?.phase).toBe('settling');
      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    // Festlegung 5: the cancel flag lives only for the synchronous span of `cancel()` — a later run
    // that ends on a plain transport loss must read at once, not inherit an earlier run's grace.
    it('does not carry a cancelled run’s grace period over to a later run that ends on a transport loss', () => {
      cancelOneRowInFlight();
      readAfterGrace().flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      expect(service.run()?.phase).toBe('closed');

      service.startRestore(target({ setId: 'set-2' }), [EMOTES[1]]);
      httpMock.expectOne(isAdd).flush('boom', { status: 503, statusText: 'Unavailable' });
      vi.advanceTimersByTime(RUN_DELAY_MS);

      // Already out — no grace period for this run.
      httpMock.expectOne(isSetRead).flush(setEntriesPage([{ id: '7tv-2', alias: 'KEKW' }]));
      expect(httpMock.expectOne(SYNC_RESTORED_SET_2).request.body.sevenTvEmoteIds).toEqual([
        '7tv-2',
      ]);
    });

    // The settle works on the run's own record by runId: a newer run shown in the meantime keeps
    // the dock, and the superseded run still settles and reports on its own.
    it('settles and reports a detached run by its runId without touching a newer run shown meanwhile', () => {
      cancelOneRowInFlight();
      const runA = service.run()?.runId;
      service.reset();

      service.startRestore(target({ setId: 'set-2' }), [EMOTES[1]]);
      const runB = service.run()?.runId;
      expect(runB).not.toBe(runA);
      const addB = httpMock.expectOne(isAdd);

      const readA = readAfterGrace();
      expect(readA.request.body.variables.id).toBe('set-1');
      readA.flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));

      // B stays on the dock, with its own live queue.
      expect(service.run()?.runId).toBe(runB);
      expect(service.run()?.phase).toBe('running');
      expect(service.queue().map((item) => [item.key, item.status])).toEqual([
        ['7tv-2#KEKW', 'in-progress'],
      ]);
      // A reports all the same, for its own set and id.
      const syncA = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      expect(syncA.request.body.sevenTvEmoteIds).toEqual(['7tv-1']);
      syncA.flush(restoredAnswer());
      expect(service.run()?.runId).toBe(runB);

      flushApplied(addB);
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_SET_2).flush(restoredAnswer());
      expect(service.run()?.result?.doneKeys).toEqual(['7tv-2#KEKW']);
    });

    it('keeps the arbiter busy through settling and reporting, destructiveOpen staying false throughout', () => {
      const arbiter = TestBed.inject(SevenTvRunArbiter);
      cancelOneRowInFlight();

      expect(arbiter.activeClaim()).toEqual({ kind: 'restore', phase: 'settling' });
      expect(arbiter.destructiveOpen()).toBe(false);

      const read = readAfterGrace();
      expect(arbiter.activeClaim()).toEqual({ kind: 'restore', phase: 'settling' });
      read.flush(setEntriesPage([{ id: '7tv-1', alias: 'PogU' }]));

      // Confirmed done, so the report goes out — reporting counts as settling for the arbiter too.
      expect(arbiter.activeRun()).toBe('restore');
      expect(arbiter.destructiveOpen()).toBe(false);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());

      expect(arbiter.activeClaim()).toBeNull();
      expect(arbiter.destructiveOpen()).toBe(false);
    });

    it('keeps the arbiter busy through settling even for an unknown-only run, freeing up as soon as it closes', () => {
      const arbiter = TestBed.inject(SevenTvRunArbiter);
      cancelOneRowInFlight();

      const read = readAfterGrace();
      read.flush(setEntriesPage([]));

      // Nothing to report — the run closes at once (D6 (a)); the resync it fires off is
      // fire-and-forget from the arbiter's point of view.
      expect(arbiter.activeRun()).toBeNull();
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });
  });
});
