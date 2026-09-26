import { provideHttpClient } from '@angular/common/http';
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
import { SyncRestoredInSetResponse } from './seven-tv-emote-set.model';
import {
  RestoreRunInfo,
  RestoreStartTarget,
  SevenTvRestoreService,
} from './seven-tv-restore.service';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { SevenTvTokenService } from './seven-tv-token.service';

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

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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
    req.flush({});
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

    req.flush({});
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
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    const aliasless = httpMock.expectOne(GQL_ENDPOINT);
    expect(aliasless.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      alias: null,
    });
    aliasless.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
  });

  it('reports the finished run to the set-centric sync-restored with the ids and no expected channel for a non-active set', () => {
    service.startRestore(target({ active: false }), EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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
    firstAdd.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    const secondAdd = httpMock.expectOne(GQL_ENDPOINT);
    expect(secondAdd.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      alias: 'PogU2',
    });
    secondAdd.flush({});
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
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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

      httpMock.expectOne(GQL_ENDPOINT).flush({});
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

      httpMock.expectOne(GQL_ENDPOINT).flush({});
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

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('reports false when the caller says its check could not run, even though the run itself still starts', () => {
      service.startRestore(target(), [EMOTES[0]], 0, false);

      expect(service.duplicateCheckAvailable()).toBe(false);
      // Fails open, same as always — an unverifiable check does not block the confirmed run.
      expect(service.isRunning()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    });

    it('resets to true on the next call, even without a fifth argument', () => {
      service.startRestore(target(), [], 2, false);
      expect(service.duplicateCheckAvailable()).toBe(false);

      service.startRestore(target(), [EMOTES[0]]);
      expect(service.duplicateCheckAvailable()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
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

      httpMock.expectOne(GQL_ENDPOINT).flush({});
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

      httpMock.expectOne(GQL_ENDPOINT).flush({});
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
    httpMock.expectOne(GQL_ENDPOINT).flush({});
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
      httpMock.expectOne(GQL_ENDPOINT).flush({});
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

      httpMock.expectOne(GQL_ENDPOINT).flush({});
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

      inFlightReq.flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
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
      httpMock.expectOne(GQL_ENDPOINT).flush({});
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
      httpMock.expectOne(GQL_ENDPOINT).flush({});
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
      httpMock.expectOne(GQL_ENDPOINT).flush({});
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
      httpMock.expectOne(GQL_ENDPOINT).flush({});
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
      httpMock.expectOne(GQL_ENDPOINT).flush({});
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

  // The arbiter (#70, Task 4) has no lock of its own — it reads this service's own isRunning
  // signal, so these cases pin the invariants a hand-kept tryAcquire/release could not have
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

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      // #255: a plain success naming nothing (the default `restoredAnswer()`) produces no resync
      // request for the active set any more — nothing left to drain here.
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());

      expect(arbiter.activeRun()).toBeNull();
    });

    it('clears the active run once cancel() ends it', () => {
      service.startRestore(target(), EMOTES);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      service.cancel();

      expect(arbiter.activeRun()).toBeNull();

      // Drain the closing sync-restored call so afterEach's httpMock.verify() stays green — #255:
      // no resync follows a plain success that names nothing.
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
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

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());

      expect(arbiter.activeRun()).toBeNull();

      deleteService.startDelete('set-1', 'sensitron', [EMOTES[1]], 'sensitron');

      expect(arbiter.activeRun()).toBe('delete');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock.expectOne('/api/seventv/emote-sets/set-1/sync-deleted').flush({
        reportedCount: 1,
        channels: [{ channelName: 'sensitron', archivedCount: 1, notFoundIds: [] }],
        unresolvedChannel: null,
        resyncTriggered: ['sensitron'],
      });
    });
  });
});
