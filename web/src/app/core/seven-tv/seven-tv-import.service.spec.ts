import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ImportOrigin, ImportRow } from './import-source';
import { SevenTvImportService } from './seven-tv-import.service';
import { RUN_DELAY_MS } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';

// Only the keys the run engine itself translates — the import service adds none of its own.
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
const TARGET_B = { setId: 'set-b', channelName: 'kanal_b' };
const TARGET_C = { setId: 'set-c', channelName: 'kanal_c' };
// An untracked target (spec 8.6, T2.6) — `channelName: null`, no `Channel` of ours to resync.
const TARGET_UNTRACKED = { setId: 'set-u', channelName: null, ownerDisplayName: 'Stranger' };
const SYNC_IMPORTED_B = '/api/channels/kanal_b/emotes/sync-imported';
const RESYNC_B = '/api/channels/kanal_b/resync';
const SYNC_IMPORTED_C = '/api/channels/kanal_c/emotes/sync-imported';
const RESYNC_C = '/api/channels/kanal_c/resync';
const SYNC_IMPORTED_SET_U = '/api/seventv/emote-sets/set-u/sync-imported';

const CHANNEL_ORIGIN: ImportOrigin = { kind: 'channel', channelName: 'brudivoeller_tv' };
const FOREIGN_CHANNEL_ORIGIN: ImportOrigin = {
  kind: 'seventv-channel',
  channelName: 'handofblood',
};
const FILE_ORIGIN: ImportOrigin = {
  kind: 'file',
  fileName: 'emotepurge_brudivoeller_tv_emote-list_2026-09-05.json',
  exportedAt: '2026-09-05T10:00:00Z',
  // Deliberately set: the file *knows* a channel, and the body must still send null (R3).
  channelName: 'brudivoeller_tv',
  envelopeKind: 'emote-list',
};
const LEADERBOARD_ORIGIN: ImportOrigin = { kind: 'seventv-leaderboard', sortBy: 'TRENDING_DAILY' };

const ROWS: ImportRow[] = [
  { sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

describe('SevenTvImportService', () => {
  let service: SevenTvImportService;
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
    service = TestBed.inject(SevenTvImportService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  /** Runs both rows of ROWS to 'done' and drains the closing calls of a successful run. */
  function runTwoRowsToDone(): void {
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
  }

  it('keys every queue row by its 7TV id and carries no internal emoteId', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

    expect(service.queue().map((item) => item.key)).toEqual(['7tv-1', '7tv-2']);
    expect(Object.hasOwn(service.queue()[0], 'emoteId')).toBe(false);

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.headers.get('Authorization')).toBe('Bearer write-token');
    // v4 dropped the ADD action in favour of a dedicated field, and the alias travels *inside* the
    // input object — pin both, not the vanished enum.
    expect(req.request.body.query).toContain('addEmote(id: { emoteId: $emoteId, alias: $alias })');
    expect(req.request.body.variables).toEqual({
      setId: 'set-b',
      emoteId: '7tv-1',
      alias: 'PogU',
    });
    req.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // Regression guard for #149: v3 rejected any alias outside ASCII+emoji, umlauts included. v4
  // fixed that server-side, but only if the alias actually reaches the wire unmangled — this is
  // the case that would have caught the old `name`-as-sibling-argument shape just as well as a
  // stray transliteration.
  it('sends an alias containing an umlaut unmangled in the mutation variables', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, [
      { sevenTvEmoteId: '7tv-1', name: 'Sitzgemüse' },
    ]);

    const req = httpMock.expectOne(GQL_ENDPOINT);
    expect(req.request.body.variables).toEqual({
      setId: 'set-b',
      emoteId: '7tv-1',
      alias: 'Sitzgemüse',
    });

    req.flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('reports a channel import with its source channel and triggers exactly one resync', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: 'brudivoeller_tv',
      sourceKind: 'channel',
      leaderboardSort: null,
      targetEmoteSetId: 'set-b',
    });
    expect(service.syncReport()).toBe('pending');
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    expect(service.syncReport()).toBe('succeeded');

    const resyncReq = httpMock.expectOne(RESYNC_B);
    expect(service.resyncTrigger()).toBe('pending');
    resyncReq.flush(null, { status: 202, statusText: 'Accepted' });

    expect(service.resyncTrigger()).toBe('succeeded');
    httpMock.expectNone(RESYNC_B);
    expect(service.run()?.result?.doneKeys).toEqual(['7tv-1', '7tv-2']);
  });

  it('reports a foreign-channel import with both its kind and its source channel', () => {
    // The expensive failure this pins (spec F6): the body used to be built with a
    // `kind === 'channel'` test, which sent `sourceChannelName: null` for this origin. The server
    // rejects a non-file kind without a name with a 400 — and this call runs *after* the ADD
    // mutations, so the emotes would already be copied and their origin lost for good.
    service.startImport(TARGET_B, FOREIGN_CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: 'handofblood',
      sourceKind: 'seventv-channel',
      leaderboardSort: null,
      targetEmoteSetId: 'set-b',
    });
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    expect(service.syncReport()).toBe('succeeded');

    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    expect(service.resyncTrigger()).toBe('succeeded');
  });

  it('sends sourceChannelName null for a file import even when the file names a channel', () => {
    service.startImport(TARGET_B, FILE_ORIGIN, ROWS);
    runTwoRowsToDone();

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: null,
      sourceKind: 'file',
      leaderboardSort: null,
      targetEmoteSetId: 'set-b',
    });
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('reports a leaderboard import with its sort and no source channel', () => {
    // The case F1 Station 6/#148 exists for: `sourceKind` alone used to be enough to reconstruct the
    // wire body from `origin.kind`. A fourth origin without a source channel needs a second field,
    // and a direct `origin.kind === 'seventv-leaderboard'` test here would be exactly the shortcut
    // that could ship this call without it — sent through the two exhaustive helpers instead.
    service.startImport(TARGET_B, LEADERBOARD_ORIGIN, ROWS);
    runTwoRowsToDone();

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: null,
      sourceKind: 'seventv-leaderboard',
      leaderboardSort: 'TRENDING_DAILY',
      targetEmoteSetId: 'set-b',
    });
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // AK 44: the run's own targetSetId always rides along, whatever it is — this is what lets the
  // audit row say which (possibly non-active) set a copy actually landed in, once T2.6 wires a
  // chosen non-active target through to `startImport`.
  it("sends the run's own targetSetId as targetEmoteSetId, not the channel active set", () => {
    service.startImport(TARGET_C, CHANNEL_ORIGIN, ROWS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_C);
    expect(reportReq.request.body.targetEmoteSetId).toBe('set-c');
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_C).flush(null, { status: 202, statusText: 'Accepted' });
  });

  // AK 41/T2.6: an untracked target (channelName: null, spec 8.6) reports through the
  // set-centric endpoint instead of the channel-scoped one, and never resyncs — there is no
  // Channel of ours to pull rows into.
  it('reports an untracked target through the set-centric endpoint and never resyncs (AK 41)', () => {
    service.startImport(TARGET_UNTRACKED, CHANNEL_ORIGIN, ROWS);
    // Threaded straight onto the run record — import-progress-section.ts's own "Ziel: …" line for
    // an untracked run (T2.6) has no channel to read, so this is what it names instead.
    expect(service.run()?.targetOwnerDisplayName).toBe('Stranger');
    runTwoRowsToDone();

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_SET_U);
    expect(reportReq.request.method).toBe('POST');
    // No targetEmoteSetId in the body — the route already names the set (spec 6.7).
    expect(reportReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-1', '7tv-2'],
      sourceChannelName: 'brudivoeller_tv',
      sourceKind: 'channel',
      leaderboardSort: null,
    });
    reportReq.flush(null, { status: 204, statusText: 'No Content' });

    expect(service.syncReport()).toBe('succeeded');
    // Never even touched: onRunComplete's own guard returns before setting it to 'pending', let
    // alone firing a request — afterEach's httpMock.verify() is what proves no resync request went
    // out at all.
    expect(service.resyncTrigger()).toBe('idle');
  });

  it('aborts the whole run on a 7TV privileges rejection and reports nothing', () => {
    const threeRows = [...ROWS, { sevenTvEmoteId: '7tv-3', name: 'Sadge' }];
    service.startImport(TARGET_B, CHANNEL_ORIGIN, threeRows);

    // v4's shape: HTTP 200, the rejection lives in `extensions.code` — there is no transport-level
    // status to catch this on.
    httpMock.expectOne(GQL_ENDPOINT).flush({
      errors: [
        {
          message: 'LACKING_PRIVILEGES you are not an editor for this user',
          extensions: { code: 'LACKING_PRIVILEGES', status: 403 },
        },
      ],
    });

    expect(service.queue().map((item) => item.status)).toEqual([
      'failed',
      'cancelled',
      'cancelled',
    ]);
    expect(service.abortedForPrivileges()).toBe(true);
    expect(service.isRunning()).toBe(false);
    expect(service.run()?.result?.doneKeys).toEqual([]);
    expect(service.syncReport()).toBe('idle');
    httpMock.expectNone(SYNC_IMPORTED_B);
    httpMock.expectNone(RESYNC_B);
  });

  it('aborts on a 401 as well — the token is gone, every further row would fail the same way', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

    httpMock.expectOne(GQL_ENDPOINT).flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(service.queue().map((item) => item.status)).toEqual(['failed', 'cancelled']);
    expect(service.abortedForPrivileges()).toBe(true);
    httpMock.expectNone(SYNC_IMPORTED_B);
  });

  it('keeps running on an ordinary 7TV rejection', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

    httpMock
      .expectOne(GQL_ENDPOINT)
      .flush({ errors: [{ message: 'BAD_REQUEST this emote has a conflicting name' }] });
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(service.queue().map((item) => item.status)).toEqual(['failed', 'done']);
    expect(service.abortedForPrivileges()).toBe(false);

    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body.sevenTvEmoteIds).toEqual(['7tv-2']);
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('reports the resync cooldown as "coming on its own" and leaves the report untouched', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock
      .expectOne(RESYNC_B)
      .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });

    expect(service.resyncTrigger()).toBe('cooldown');
    expect(service.syncReport()).toBe('succeeded');
  });

  it('re-sends a failed report against the current run on retrySyncReport()', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    // 401 is not retried automatically — waiting cannot fix an expired session.
    httpMock.expectOne(SYNC_IMPORTED_B).flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(service.syncReport()).toBe('failed');
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });

    service.retrySyncReport();

    const retryReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(retryReq.request.body.sevenTvEmoteIds).toEqual(['7tv-1', '7tv-2']);
    retryReq.flush(null, { status: 204, statusText: 'No Content' });
    expect(service.syncReport()).toBe('succeeded');
  });

  it('reset() clears queue, run, report and resync state', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();
    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });

    service.reset();

    expect(service.queue()).toEqual([]);
    expect(service.run()).toBeNull();
    expect(service.syncReport()).toBe('idle');
    expect(service.resyncTrigger()).toBe('idle');
    expect(service.abortedForPrivileges()).toBe(false);
  });

  // #149/T5: this service does no filtering of its own — `import-flow.ts` runs the fresh pre-send
  // duplicate check (already-present-filter.ts) before ever calling startImport. What this service
  // owns is surfacing that caller-supplied count to the user, including the case a caller could
  // otherwise leave silent: every row was a duplicate, so nothing gets queued at all.
  describe('skippedDuplicates (#149/T5)', () => {
    it('defaults to 0 when the caller omits it', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

      expect(service.skippedDuplicates()).toBe(0);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reports the caller-supplied skip count even when every row was a duplicate and nothing queues', () => {
      // The fresh check filtered every row out, leaving an empty list — the engine refuses to
      // start on an empty queue, but the skip count must still reach the user.
      service.startImport(TARGET_B, CHANNEL_ORIGIN, [], 2);

      expect(service.isRunning()).toBe(false);
      expect(service.queue()).toEqual([]);
      expect(service.skippedDuplicates()).toBe(2);
    });

    it('reset() clears it back to 0', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, [], 2);
      expect(service.skippedDuplicates()).toBe(2);

      service.reset();

      expect(service.skippedDuplicates()).toBe(0);
    });
  });

  // #149: whether the caller's fresh pre-send check (already-present-filter.ts) actually ran —
  // distinct from skippedDuplicates above, which alone cannot tell "nothing to skip" apart from
  // "could not check". A caller that never passes the fifth argument (every pre-fix test above, and
  // every caller that predates this fix) must keep reading as "checked".
  describe('duplicateCheckAvailable (#149)', () => {
    it('defaults to true when the caller omits it', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

      expect(service.duplicateCheckAvailable()).toBe(true);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reports false when the caller says its check could not run, even though the run itself still starts', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS, 0, false);

      expect(service.duplicateCheckAvailable()).toBe(false);
      // Fails open, same as always — an unverifiable check does not block the confirmed run.
      expect(service.isRunning()).toBe(true);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reset() clears it back to true', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, [], 2, false);
      expect(service.duplicateCheckAvailable()).toBe(false);

      service.reset();

      expect(service.duplicateCheckAvailable()).toBe(true);
    });
  });

  // #149 P2 (independent review): a fully-refused (all-duplicates) startImport leaves no run/queue
  // behind, so this transient flag is what lets `dockVisible()` (`usage-stats-page.ts`, via
  // `action-dock.ts`) mount the notice at all — and what lets it clear on its own afterwards rather
  // than requiring a dismiss control that, in that refused case, has nothing to attach to.
  describe('duplicateNoticePending (#149 P2)', () => {
    it('defaults to false when the caller omits skip info entirely', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);

      expect(service.duplicateNoticePending()).toBe(false);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('becomes true when the call reports a skip count, even for a refused (all-duplicates) run', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, [], 2);

      expect(service.duplicateNoticePending()).toBe(true);
    });

    it('becomes true when the call reports the check unavailable, even with nothing skipped', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS, 0, false);

      expect(service.duplicateNoticePending()).toBe(true);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('clears itself after DUPLICATE_NOTICE_MS without any dismiss call', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, [], 2);
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(3999);
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(1);
      expect(service.duplicateNoticePending()).toBe(false);
    });

    it("a second call within the window restarts it, rather than the first call's timer cutting the new notice short", () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, [], 2);
      vi.advanceTimersByTime(3000);

      service.startImport(TARGET_C, CHANNEL_ORIGIN, [], 3);
      vi.advanceTimersByTime(2000);

      // 5000 ms after the first call, but only 2000 ms after the second — still pending.
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(2000);
      expect(service.duplicateNoticePending()).toBe(false);
    });

    it('reset() clears it immediately, without waiting out the timer', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, [], 2);
      expect(service.duplicateNoticePending()).toBe(true);

      service.reset();

      expect(service.duplicateNoticePending()).toBe(false);
    });
  });

  // R15: the engine sets isRunning false *before* the closing calls go out, so a second run can be
  // started while the first one's follow-up is still in flight. Everything the follow-up needs hangs
  // off the run record it closed over, and a late answer that no longer matches run() is dropped.
  it('drops the follow-up answers of a superseded run and retries the current one instead', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, ROWS);
    runTwoRowsToDone();

    const staleReport = httpMock.expectOne(SYNC_IMPORTED_B);
    const staleResync = httpMock.expectOne(RESYNC_B);
    expect(service.syncReport()).toBe('pending');

    // Run 2, started while run 1's follow-up is still open — the engine allows it, isRunning is
    // already false.
    expect(service.isRunning()).toBe(false);
    service.startImport(TARGET_C, FILE_ORIGIN, [{ sevenTvEmoteId: '7tv-9', name: 'Clueless' }]);
    expect(service.isRunning()).toBe(true);

    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(SYNC_IMPORTED_C).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_C).flush(null, { status: 202, statusText: 'Accepted' });
    expect(service.syncReport()).toBe('succeeded');
    expect(service.resyncTrigger()).toBe('succeeded');

    // Run 1 answers late, and badly — without the guard this would flip both signals of run 2.
    staleReport.flush({}, { status: 401, statusText: 'Unauthorized' });
    staleResync.flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too' });

    expect(service.syncReport()).toBe('succeeded');
    expect(service.resyncTrigger()).toBe('succeeded');
    expect(service.run()?.targetChannelName).toBe('kanal_c');
    expect(service.run()?.result?.doneKeys).toEqual(['7tv-9']);

    // And a retry now belongs to run 2: run 2's keys, run 2's channel — never run 1's.
    service.retrySyncReport();
    const retryReq = httpMock.expectOne(SYNC_IMPORTED_C);
    expect(retryReq.request.body).toEqual({
      sevenTvEmoteIds: ['7tv-9'],
      sourceChannelName: null,
      sourceKind: 'file',
      leaderboardSort: null,
      targetEmoteSetId: 'set-c',
    });
    retryReq.flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectNone(SYNC_IMPORTED_B);
  });
});
