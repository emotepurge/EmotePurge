import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DELETE_DELAY_MS, DeleteQueueEmote, SevenTvDeleteService } from './seven-tv-delete.service';
import { RUN_DELAY_MS } from './seven-tv-run-engine';
import { SevenTvRestoreService } from './seven-tv-restore.service';
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
const SYNC_RESTORED_ENDPOINT = '/api/channels/sensitron/emotes/sync-restored';

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
    service.startRestore('set-1', 'sensitron', EMOTES);

    expect(service.queue().map((item) => item.key)).toEqual(['7tv-1#PogU', '7tv-2#KEKW']);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 2, notFoundIds: [] });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('sends the ADD mutation with set id, emote id and the alias to restore under', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);

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
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // Regression guard for #149: v3 rejected any alias outside ASCII+emoji, umlauts included. v4
  // fixed that server-side, but only if the alias actually reaches the wire unmangled — this is
  // the case that would have caught the old `name`-as-sibling-argument shape just as well as a
  // stray transliteration.
  it('sends an alias containing an umlaut unmangled in the mutation variables', () => {
    service.startRestore('set-1', 'sensitron', [{ ...EMOTES[0], name: 'Gänsehosen' }]);

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.body.variables).toEqual({
      setId: 'set-1',
      emoteId: '7tv-1',
      alias: 'Gänsehosen',
    });

    req.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // An entry without an alias (a removed transfer target's `null`) comes back through the ADD with
  // `alias: null` — 7TV's own default-name fallback — keyed with an empty suffix and shown under the
  // emote's default name.
  it('restores a null alias as an ADD without an alias, keyed id# and shown by its default name', () => {
    service.startRestore('set-1', 'sensitron', [
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
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('reports the finished run to sync-restored with the set and the restored 7TV ids', () => {
    service.startRestore('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    const reportReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(reportReq.request.body).toEqual({
      emoteSetId: 'set-1',
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
    });
    expect(service.syncReport()).toBe('pending');
    reportReq.flush({ restoredCount: 2, notFoundIds: [] });

    expect(service.syncReport()).toBe('succeeded');
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('marks the report partial when the backend restored fewer than reported', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock
      .expectOne(SYNC_RESTORED_ENDPOINT)
      .flush({ restoredCount: 0, notFoundIds: ['7tv-1'], targetIsActiveSetOfChannel: true });

    expect(service.syncReport()).toBe('partial');
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('marks the report failed on a 401 and re-sends it via retrySyncReport()', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    // 401 is not retried automatically — waiting cannot fix an expired session.
    httpMock
      .expectOne(SYNC_RESTORED_ENDPOINT)
      .flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(service.syncReport()).toBe('failed');

    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(retryReq.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['7tv-1'] });
    retryReq.flush({ restoredCount: 1, notFoundIds: [] });

    expect(service.syncReport()).toBe('succeeded');
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // Sonde 5, branch A (spec 7.2, AK 69): 7TV takes the same emote under two aliases, so a protocol
  // row of a #74 duplicate becomes one queue row — and one ADD — per alias. The bookkeeping report
  // still names the emote once.
  it('restores a row with two aliases as two ADDs keyed id#alias, reported as one 7TV id', () => {
    service.startRestore('set-1', 'sensitron', [
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
    expect(reportReq.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['7tv-1'] });
    reportReq.flush({ restoredCount: 1, notFoundIds: [] });
    expect(service.syncReport()).toBe('succeeded');
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // AK 71: the set is frozen into the run record at the start — the report and the manual retry
  // name it even when the next run the page asks for names another set.
  it('reports and retries with the set id frozen at the start of the run', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    service.startRestore('set-2', 'sensitron', [EMOTES[1]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    const firstReport = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(firstReport.request.body.emoteSetId).toBe('set-1');
    firstReport.flush({}, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(retryReq.request.body).toEqual({ emoteSetId: 'set-1', sevenTvEmoteIds: ['7tv-1'] });
    retryReq.flush({ restoredCount: 1, notFoundIds: [] });
  });

  // Spec 6.6, E3: only the set-scoped body — also for a protocol row that never had a local emote.
  it('sends only the set-scoped body form for a row without an emoteId, never the legacy emoteIds', () => {
    service.startRestore('set-1', 'sensitron', [{ sevenTvEmoteId: '7tv-live', name: 'LiveOnly' }]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    const reportReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
    expect(Object.keys(reportReq.request.body).sort()).toEqual(['emoteSetId', 'sevenTvEmoteIds']);
    expect(reportReq.request.body.sevenTvEmoteIds).toEqual(['7tv-live']);
    reportReq.flush({ restoredCount: 1, notFoundIds: [] });
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // Spec 6.6: a restore into a non-active set is paper only on the server — `restoredCount: 0` is
  // by design there, not a shortfall.
  it('treats a paper-only answer for a non-active set as succeeded, not partial', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock
      .expectOne(SYNC_RESTORED_ENDPOINT)
      .flush({ restoredCount: 0, notFoundIds: [], targetIsActiveSetOfChannel: false });

    expect(service.syncReport()).toBe('succeeded');
    httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('triggers exactly one resync after the run and reports success', () => {
    service.startRestore('set-1', 'sensitron', EMOTES);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 2, notFoundIds: [] });
    const resyncReq = httpMock.expectOne(RESYNC_ENDPOINT);
    expect(service.resyncTrigger()).toBe('pending');
    resyncReq.flush(null, { status: 202, statusText: 'Accepted' });

    expect(service.resyncTrigger()).toBe('succeeded');
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  it('reports the resync cooldown as "coming on its own", not as a failure', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
    httpMock
      .expectOne(RESYNC_ENDPOINT)
      .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });

    expect(service.resyncTrigger()).toBe('cooldown');
  });

  it('skips both the report and the resync entirely when nothing was restored', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'set is full' }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(service.queue()[0].status).toBe('failed');
    expect(service.syncReport()).toBe('idle');
    expect(service.resyncTrigger()).toBe('idle');
    httpMock.expectNone(SYNC_RESTORED_ENDPOINT);
    httpMock.expectNone(RESYNC_ENDPOINT);
  });

  it('reset() clears queue, report and resync state', () => {
    service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
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
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);

      expect(service.skippedDuplicates()).toBe(0);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reports the caller-supplied skip count even when every row was a duplicate and nothing queues', () => {
      // A second restore over rows already restored: the caller's pre-run filter (T5) removed
      // every row, leaving an empty list — the engine refuses to start on an empty queue, but the
      // skip count must still reach the user rather than the run silently doing nothing.
      service.startRestore('set-1', 'sensitron', [], 2);

      expect(service.isRunning()).toBe(false);
      expect(service.queue()).toEqual([]);
      expect(service.skippedDuplicates()).toBe(2);
    });

    it('resets to 0 on the next call, even without duplicates', () => {
      service.startRestore('set-1', 'sensitron', [], 2);
      expect(service.skippedDuplicates()).toBe(2);

      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
      expect(service.skippedDuplicates()).toBe(0);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reset() clears it back to 0', () => {
      service.startRestore('set-1', 'sensitron', [], 2);
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
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);

      expect(service.duplicateCheckAvailable()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reports false when the caller says its check could not run, even though the run itself still starts', () => {
      service.startRestore('set-1', 'sensitron', [EMOTES[0]], 0, false);

      expect(service.duplicateCheckAvailable()).toBe(false);
      // Fails open, same as always — an unverifiable check does not block the confirmed run.
      expect(service.isRunning()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('resets to true on the next call, even without a fifth argument', () => {
      service.startRestore('set-1', 'sensitron', [], 2, false);
      expect(service.duplicateCheckAvailable()).toBe(false);

      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
      expect(service.duplicateCheckAvailable()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reset() clears it back to true', () => {
      service.startRestore('set-1', 'sensitron', [], 2, false);
      expect(service.duplicateCheckAvailable()).toBe(false);

      service.reset();

      expect(service.duplicateCheckAvailable()).toBe(true);
    });
  });

  // #149 P2 (independent review): a fully-refused (all-duplicates) startRestore leaves no run/queue
  // behind, so this transient flag is what lets `dockVisible()` (`usage-stats-page.ts`, via
  // `action-dock.ts`) mount the notice at all — and what lets it clear on its own afterwards rather
  // than requiring a dismiss control that, in that refused case, has nothing to attach to.
  describe('duplicateNoticePending (#149 P2)', () => {
    it('defaults to false when the caller omits skip info entirely', () => {
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);

      expect(service.duplicateNoticePending()).toBe(false);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('becomes true when the call reports a skip count, even for a refused (all-duplicates) run', () => {
      service.startRestore('set-1', 'sensitron', [], 2);

      expect(service.duplicateNoticePending()).toBe(true);
    });

    it('becomes true when the call reports the check unavailable, even with nothing skipped', () => {
      service.startRestore('set-1', 'sensitron', [EMOTES[0]], 0, false);

      expect(service.duplicateNoticePending()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('clears itself after DUPLICATE_NOTICE_MS without any dismiss call', () => {
      service.startRestore('set-1', 'sensitron', [], 2);
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(3999);
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(1);
      expect(service.duplicateNoticePending()).toBe(false);
    });

    it("a second call within the window restarts it, rather than the first call's timer cutting the new notice short", () => {
      service.startRestore('set-1', 'sensitron', [], 2);
      vi.advanceTimersByTime(3000);

      service.startRestore('set-2', 'other-channel', [], 3);
      vi.advanceTimersByTime(2000);

      // 5000 ms after the first call, but only 2000 ms after the second — still pending.
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(2000);
      expect(service.duplicateNoticePending()).toBe(false);
    });

    it('reset() clears it immediately, without waiting out the timer', () => {
      service.startRestore('set-1', 'sensitron', [], 2);
      expect(service.duplicateNoticePending()).toBe(true);

      service.reset();

      expect(service.duplicateNoticePending()).toBe(false);
    });
  });

  // R15 (#72, T12): finish() flips isRunning() to false *before* the two closing calls resolve, so
  // a second run can legitimately start while the first one's report/resync are still in flight.
  // Their late answers must not land on the second run's state.
  describe('superseded run (R15)', () => {
    it('discards a late sync-restored answer from a superseded run without touching the new one', () => {
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      const staleSyncReq = httpMock.expectOne(SYNC_RESTORED_ENDPOINT);
      // Settle the resync half immediately — this case is about the sync-restored guard alone.
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.syncReport()).toBe('pending');

      // A second run starts, for a different channel, before run 1's sync-restored answer comes
      // back — legitimate, because finish() already flipped isRunning() to false.
      service.startRestore('set-2', 'other-channel', [EMOTES[1]]);
      expect(service.isRunning()).toBe(true);
      expect(service.syncReport()).toBe('idle'); // run 2's own state, reset at start

      staleSyncReq.flush({ restoredCount: 1, notFoundIds: [] });
      expect(service.syncReport()).toBe('idle'); // still run 2's state, untouched by run 1's answer

      // Run 2 finishes normally afterwards — the guard must not have swallowed its own terminal
      // flank along with the stale one.
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock
        .expectOne('/api/channels/other-channel/emotes/sync-restored')
        .flush({ restoredCount: 1, notFoundIds: [] });
      expect(service.syncReport()).toBe('succeeded');
      httpMock
        .expectOne('/api/channels/other-channel/resync')
        .flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('never lets a stale resync answer overwrite a later state — including "cooldown"', () => {
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      const staleResyncReq = httpMock.expectOne(RESYNC_ENDPOINT);

      // A second run starts, runs to completion, and its own resync lands in cooldown — a real
      // state, not the guard's doing.
      service.startRestore('set-2', 'other-channel', [EMOTES[1]]);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock
        .expectOne('/api/channels/other-channel/emotes/sync-restored')
        .flush({ restoredCount: 1, notFoundIds: [] });
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
      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);

      expect(arbiter.activeRun()).toBe('restore');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

      expect(arbiter.activeRun()).toBeNull();
    });

    it('clears the active run once cancel() ends it', () => {
      service.startRestore('set-1', 'sensitron', EMOTES);
      httpMock.expectOne(GQL_ENDPOINT).flush({});

      service.cancel();

      expect(arbiter.activeRun()).toBeNull();

      // Drain the closing sync-restored/resync calls so afterEach's httpMock.verify() stays green.
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('leaves no active run when the engine refuses the start for a cleared token', () => {
      tokenService.clearToken();

      service.startRestore('set-1', 'sensitron', EMOTES);

      expect(service.isRunning()).toBe(false);
      expect(arbiter.activeRun()).toBeNull();
    });

    it('lets a delete start once this restore has ended — the cross-service invariant a held lock could not guarantee', () => {
      const deleteService = TestBed.inject(SevenTvDeleteService);

      service.startRestore('set-1', 'sensitron', [EMOTES[0]]);
      expect(arbiter.activeRun()).toBe('restore');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(SYNC_RESTORED_ENDPOINT).flush({ restoredCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_ENDPOINT).flush(null, { status: 202, statusText: 'Accepted' });

      expect(arbiter.activeRun()).toBeNull();

      deleteService.startDelete('set-1', 'sensitron', [EMOTES[1]]);

      expect(arbiter.activeRun()).toBe('delete');

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      httpMock
        .expectOne('/api/channels/sensitron/emotes/sync-deleted')
        .flush({ archivedCount: 1, notFoundIds: [] });
    });
  });
});
