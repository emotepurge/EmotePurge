import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DELETE_DELAY_MS, DeleteQueueEmote, SevenTvDeleteService } from './seven-tv-delete.service';
import { RUN_DELAY_MS } from './seven-tv-run-engine';
import { SyncRestoredInSetResponse } from './seven-tv-emote-set.model';
import { RestoreStartTarget, SevenTvRestoreService } from './seven-tv-restore.service';
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

/** The default target of these cases: a *non-active* set of the tracked channel `channel` — the
 *  one case in which the client resyncs itself (spec E12), so the report is followed by exactly one
 *  `POST /resync` unless the answer names the channel. `active: true` makes it the channel's active
 *  set instead (expected hit, no client resync); `untracked: true` a set with no channel at all. */
function target(
  overrides: { setId?: string; channel?: string; active?: boolean; untracked?: boolean } = {},
): RestoreStartTarget {
  const setId = overrides.setId ?? 'set-1';
  const channel = overrides.channel ?? 'sensitron';
  const tracked = overrides.untracked !== true;
  const active = tracked && overrides.active === true;
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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('reports the finished run to the set-centric sync-restored with the ids and no expected channel for a non-active set', () => {
    service.startRestore(target(), EMOTES);

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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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
    service.startRestore(target(), [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    // 401 is not retried automatically — waiting cannot fix an expired session.
    httpMock
      .expectOne(SYNC_RESTORED_ENDPOINT)
      .flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(service.syncReport()).toBe('failed');

    // The failed report still hands over to the resync (the answer named nothing).
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

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
    service.startRestore(target(), [
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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // AK 71: the set is frozen into the run record at the start — the report and the manual retry
  // name it even when the next run the page asks for names another set.
  it('reports and retries with the set id frozen at the start of the run', () => {
    service.startRestore(target(), [EMOTES[0]]);
    service.startRestore(target({ setId: 'set-2' }), [EMOTES[1]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    // The set is in the route now: the report is addressed to set-1, never to set-2.
    const firstReport = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    httpMock.expectNone(SYNC_RESTORED_SET_2);
    firstReport.flush({}, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('triggers exactly one resync after the run and reports success', () => {
    service.startRestore(target(), EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    const resyncReq = httpMock.expectOne(RESYNC_ENDPOINT);
    expect(service.resyncTrigger()).toBe('pending');
    resyncReq.flush(null, { status: 202, statusText: 'Accepted' });

    expect(service.resyncTrigger()).toBe('succeeded');
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  it('reports the resync cooldown as "coming on its own", not as a failure', () => {
    service.startRestore(target(), [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
    httpMock
      .expectOne(RESYNC_ENDPOINT)
      .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });

    expect(service.resyncTrigger()).toBe('cooldown');
  });

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
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

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
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reports the caller-supplied skip count even when every row was a duplicate and nothing queues', () => {
      // A second restore over rows already restored: the caller's pre-run filter (T5) removed
      // every row, leaving an empty list — the engine refuses to start on an empty queue, but the
      // skip count must still reach the user rather than the run silently doing nothing.
      service.startRestore(target(), [], 2);

      expect(service.isRunning()).toBe(false);
      expect(service.queue()).toEqual([]);
      expect(service.skippedDuplicates()).toBe(2);
    });

    it('resets to 0 on the next call, even without duplicates', () => {
      service.startRestore(target(), [], 2);
      expect(service.skippedDuplicates()).toBe(2);

      service.startRestore(target(), [EMOTES[0]]);
      expect(service.skippedDuplicates()).toBe(0);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reports false when the caller says its check could not run, even though the run itself still starts', () => {
      service.startRestore(target(), [EMOTES[0]], 0, false);

      expect(service.duplicateCheckAvailable()).toBe(false);
      // Fails open, same as always — an unverifiable check does not block the confirmed run.
      expect(service.isRunning()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('resets to true on the next call, even without a fifth argument', () => {
      service.startRestore(target(), [], 2, false);
      expect(service.duplicateCheckAvailable()).toBe(false);

      service.startRestore(target(), [EMOTES[0]]);
      expect(service.duplicateCheckAvailable()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
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

    it('reads an unresolved expected channel as partial/channelMismatch', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({
          unresolvedChannel: { channelName: 'sensitron', reason: 'activeSetDiffers' },
          resyncTriggered: ['sensitron'],
        }),
      );

      expect(service.syncReport()).toBe('partial');
      expect(service.syncReportReason()).toBe('channelMismatch');
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
  });

  // Spec 6.4, E12, F15, AK 21/27: the client's own resync follows the report, and only for a
  // non-active set of a tracked channel the backend did not already resync.
  describe('resync after the report (spec 6.4, E12, AK 21/27)', () => {
    it('sends no resync before the report has answered', () => {
      const report = runOneRestoreToReport(target());

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('idle');

      report.flush(restoredAnswer());
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.resyncTrigger()).toBe('succeeded');
    });

    it('sends no resync of its own when the answer names the channel, and says the backend is on it', () => {
      runOneRestoreToReport(target()).flush(restoredAnswer({ resyncTriggered: ['sensitron'] }));

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('backendTriggered');
    });

    it('still resyncs the channel itself when the answer names only other channels', () => {
      runOneRestoreToReport(target()).flush(restoredAnswer({ resyncTriggered: ['otherchannel'] }));

      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.resyncTrigger()).toBe('succeeded');
    });

    it('sends no resync for the active set of a tracked channel — the backend covers it — and shows no resync line', () => {
      runOneRestoreToReport(target({ active: true })).flush(
        restoredAnswer({
          channels: [{ channelName: 'sensitron', restoredCount: 1, notFoundIds: [] }],
          resyncTriggered: ['sensitron'],
        }),
      );

      httpMock.expectNone(RESYNC_ENDPOINT);
      expect(service.resyncTrigger()).toBe('idle');
    });

    it('sends no resync for an untracked target, not even after a failed report', () => {
      runOneRestoreToReport(target({ untracked: true })).flush(null, {
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

  // R15 (#72, T12): finish() flips isRunning() to false *before* the two closing calls resolve, so
  // a second run can legitimately start while the first one's report/resync are still in flight.
  // Their late answers must not land on the second run's state.
  describe('superseded run (R15)', () => {
    it('discards a late sync-restored answer from a superseded run without touching the new one', () => {
      service.startRestore(target(), [EMOTES[0]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      const staleSyncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      expect(service.syncReport()).toBe('pending');

      // A second run starts, for a different channel, before run 1's sync-restored answer comes
      // back — legitimate, because finish() already flipped isRunning() to false.
      service.startRestore(target({ setId: 'set-2', channel: 'other-channel' }), [EMOTES[1]]);
      expect(service.isRunning()).toBe(true);
      expect(service.syncReport()).toBe('idle'); // run 2's own state, reset at start

      staleSyncReq.flush(restoredAnswer());
      expect(service.syncReport()).toBe('idle'); // still run 2's state, untouched by run 1's answer
      // Run 1's resync still goes out — it is owed to 7TV's state, not to the dock — but its answer
      // does not land on run 2's state either.
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.resyncTrigger()).toBe('idle');

      // Run 2 finishes normally afterwards — the guard must not have swallowed its own terminal
      // flank along with the stale one.
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_SET_2).flush(restoredAnswer());
      expect(service.syncReport()).toBe('succeeded');
      httpMock
        .expectOne('/api/channels/other-channel/resync')
        .flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('never lets a stale resync answer overwrite a later state — including "cooldown"', () => {
      service.startRestore(target(), [EMOTES[0]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      const staleResyncReq = httpMock.expectOne(RESYNC_ENDPOINT);

      // A second run starts, runs to completion, and its own resync lands in cooldown — a real
      // state, not the guard's doing.
      service.startRestore(target({ setId: 'set-2', channel: 'other-channel' }), [EMOTES[1]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_SET_2).flush(restoredAnswer());
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
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

      expect(arbiter.activeRun()).toBeNull();
    });

    it('clears the active run once cancel() ends it', () => {
      service.startRestore(target(), EMOTES);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      service.cancel();

      expect(arbiter.activeRun()).toBeNull();

      // Drain the closing sync-restored/resync calls so afterEach's httpMock.verify() stays green.
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('leaves no active run when the engine refuses the start for a cleared token', () => {
      tokenService.clearToken();

      service.startRestore(target(), EMOTES);

      expect(service.isRunning()).toBe(false);
      expect(arbiter.activeRun()).toBeNull();
    });

    it('lets a delete start once this restore has ended — the cross-service invariant a held lock could not guarantee', () => {
      const deleteService = TestBed.inject(SevenTvDeleteService);

      service.startRestore(target(), [EMOTES[0]]);
      expect(arbiter.activeRun()).toBe('restore');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush(restoredAnswer());
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

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
