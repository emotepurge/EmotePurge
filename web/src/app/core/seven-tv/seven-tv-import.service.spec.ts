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
import { TransferPlan, TransferRow } from './transfer-plan';

// The keys the run engine translates, plus the import service's own row reasons.
const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig.',
      rateLimited: 'Rate Limit.',
      networkError: 'Netzwerkfehler.',
      genericStatus: '7TV-Fehler ({{ status }}).',
      rateLimitedGaveUp: 'Übersprungen.',
      cancelledMidRow: 'Mitten in der Zeile abgebrochen.',
    },
  },
  import: {
    errors: {
      nameTakenNow: 'Name inzwischen vergeben.',
      removedButNotAdded: 'Entfernt, aber nicht hinzugefügt.',
      unknownOutcome: 'Laut Nachlesen nicht übernommen.',
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
const SYNC_DELETED_B = '/api/channels/kanal_b/emotes/sync-deleted';

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
  { sevenTvEmoteId: '7tv-1', name: 'PogU', imageUrl: null },
  { sevenTvEmoteId: '7tv-2', name: 'KEKW', imageUrl: null },
];

/** The plan of a confirmation that resolved nothing — what every plain copy runs. */
function addPlan(rows: ImportRow[]): TransferPlan {
  return { rows: rows.map((row) => ({ action: 'add', source: row, alias: row.name })) };
}

const SOURCE_X: ImportRow = { sevenTvEmoteId: 'src-x', name: 'Kappa', imageUrl: null };
const SOURCE_Y: ImportRow = { sevenTvEmoteId: 'src-y', name: 'Pog', imageUrl: null };
const SOURCE_Z: ImportRow = { sevenTvEmoteId: 'src-z', name: 'Sadge', imageUrl: null };

function addRow(source: ImportRow): TransferRow {
  return { action: 'add', source, alias: source.name };
}

function renameRow(source: ImportRow, alias: string): TransferRow {
  return { action: 'renameSource', source, alias };
}

/** Replaces the target entry holding the source's own name. */
function replaceRow(source: ImportRow, targetId: string): TransferRow {
  return {
    action: 'replace',
    source,
    alias: source.name,
    target: {
      sevenTvEmoteId: targetId,
      aliases: [source.name],
      hasAliaslessEntry: false,
      defaultName: null,
    },
  };
}

/** Renames the source id's own entry in the target from `currentAlias` to the source name. */
function adoptRow(source: ImportRow, currentAlias: string): TransferRow {
  return {
    action: 'adoptSourceName',
    source,
    alias: source.name,
    target: {
      sevenTvEmoteId: source.sevenTvEmoteId,
      aliases: [currentAlias],
      hasAliaslessEntry: false,
      defaultName: null,
    },
  };
}

function gqlRejection(message: string, status: number) {
  return { errors: [{ message, extensions: { code: 'BAD_REQUEST', status } }] };
}

/** One page of the tokenless set read (`loadSevenTvSetEntries`) holding exactly `entries`. */
function setEntriesPage(entries: { id: string; alias: string | null }[]) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: entries.length,
            pageCount: 1,
            items: entries.map((entry) => ({ alias: entry.alias, emote: { id: entry.id } })),
          },
        },
      },
    },
  };
}

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
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));

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
    service.startImport(
      TARGET_B,
      CHANNEL_ORIGIN,
      addPlan([{ sevenTvEmoteId: '7tv-1', name: 'Sitzgemüse', imageUrl: null }]),
    );

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
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));
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
    service.startImport(TARGET_B, FOREIGN_CHANNEL_ORIGIN, addPlan(ROWS));
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
    service.startImport(TARGET_B, FILE_ORIGIN, addPlan(ROWS));
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
    service.startImport(TARGET_B, LEADERBOARD_ORIGIN, addPlan(ROWS));
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
    service.startImport(TARGET_C, CHANNEL_ORIGIN, addPlan(ROWS));
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
    service.startImport(TARGET_UNTRACKED, CHANNEL_ORIGIN, addPlan(ROWS));
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

  // Finding 3 (Live-Verifikation K2 2026-09-21): a resync only ever syncs the channel's *active*
  // set — firing it for a copy into a tracked but non-active set would read the wrong set, succeed,
  // and tell the user a channel page that will never show these emotes just did.
  it('reports a tracked non-active target through the channel-scoped endpoint but never resyncs', () => {
    const targetNonActive = { setId: 'set-b', channelName: 'kanal_b', isActiveSet: false };
    service.startImport(targetNonActive, CHANNEL_ORIGIN, addPlan(ROWS));
    expect(service.run()?.targetIsActiveSet).toBe(false);
    runTwoRowsToDone();

    // The report is unaffected — the audit trail for the copy is correct regardless of which set
    // received it.
    const reportReq = httpMock.expectOne(SYNC_IMPORTED_B);
    expect(reportReq.request.body.targetEmoteSetId).toBe('set-b');
    reportReq.flush(null, { status: 204, statusText: 'No Content' });
    expect(service.syncReport()).toBe('succeeded');

    // Never even touched — afterEach's httpMock.verify() proves no resync request went out at all.
    expect(service.resyncTrigger()).toBe('idle');
  });

  it('defaults isActiveSet to true and setName to the id when a caller omits both', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));

    expect(service.run()?.targetIsActiveSet).toBe(true);
    expect(service.run()?.targetSetName).toBe('set-b');

    runTwoRowsToDone();
    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
  });

  it('aborts the whole run on a 7TV privileges rejection and reports nothing', () => {
    const threeRows = [...ROWS, { sevenTvEmoteId: '7tv-3', name: 'Sadge', imageUrl: null }];
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(threeRows));

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
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));

    httpMock.expectOne(GQL_ENDPOINT).flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(service.queue().map((item) => item.status)).toEqual(['failed', 'cancelled']);
    expect(service.abortedForPrivileges()).toBe(true);
    httpMock.expectNone(SYNC_IMPORTED_B);
  });

  it('keeps running on an ordinary 7TV rejection', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));

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
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));
    runTwoRowsToDone();

    httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
    httpMock
      .expectOne(RESYNC_B)
      .flush({ errorCode: 'resync_cooldown_active' }, { status: 429, statusText: 'Too Many' });

    expect(service.resyncTrigger()).toBe('cooldown');
    expect(service.syncReport()).toBe('succeeded');
  });

  it('re-sends a failed report against the current run on retrySyncReport()', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));
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
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));
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
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));

      expect(service.skippedDuplicates()).toBe(0);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reports the caller-supplied skip count even when every row was a duplicate and nothing queues', () => {
      // The fresh check filtered every row out, leaving an empty list — the engine refuses to
      // start on an empty queue, but the skip count must still reach the user.
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([]), 2);

      expect(service.isRunning()).toBe(false);
      expect(service.queue()).toEqual([]);
      expect(service.skippedDuplicates()).toBe(2);
    });

    it('reset() clears it back to 0', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([]), 2);
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
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));

      expect(service.duplicateCheckAvailable()).toBe(true);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reports false when the caller says its check could not run, even though the run itself still starts', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS), 0, false);

      expect(service.duplicateCheckAvailable()).toBe(false);
      // Fails open, same as always — an unverifiable check does not block the confirmed run.
      expect(service.isRunning()).toBe(true);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('reset() clears it back to true', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([]), 2, false);
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
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));

      expect(service.duplicateNoticePending()).toBe(false);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('becomes true when the call reports a skip count, even for a refused (all-duplicates) run', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([]), 2);

      expect(service.duplicateNoticePending()).toBe(true);
    });

    it('becomes true when the call reports the check unavailable, even with nothing skipped', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS), 0, false);

      expect(service.duplicateNoticePending()).toBe(true);

      runTwoRowsToDone();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('clears itself after DUPLICATE_NOTICE_MS without any dismiss call', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([]), 2);
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(3999);
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(1);
      expect(service.duplicateNoticePending()).toBe(false);
    });

    it("a second call within the window restarts it, rather than the first call's timer cutting the new notice short", () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([]), 2);
      vi.advanceTimersByTime(3000);

      service.startImport(TARGET_C, CHANNEL_ORIGIN, addPlan([]), 3);
      vi.advanceTimersByTime(2000);

      // 5000 ms after the first call, but only 2000 ms after the second — still pending.
      expect(service.duplicateNoticePending()).toBe(true);

      vi.advanceTimersByTime(2000);
      expect(service.duplicateNoticePending()).toBe(false);
    });

    it('reset() clears it immediately, without waiting out the timer', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([]), 2);
      expect(service.duplicateNoticePending()).toBe(true);

      service.reset();

      expect(service.duplicateNoticePending()).toBe(false);
    });
  });

  // R15: the engine sets isRunning false *before* the closing calls go out, so a second run can be
  // started while the first one's follow-up is still in flight. Everything the follow-up needs hangs
  // off the run record it closed over, and a late answer that no longer matches run() is dropped.
  it('drops the follow-up answers of a superseded run and retries the current one instead', () => {
    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));
    runTwoRowsToDone();

    const staleReport = httpMock.expectOne(SYNC_IMPORTED_B);
    const staleResync = httpMock.expectOne(RESYNC_B);
    expect(service.syncReport()).toBe('pending');

    // Run 2, started while run 1's follow-up is still open — the engine allows it, isRunning is
    // already false.
    expect(service.isRunning()).toBe(false);
    service.startImport(
      TARGET_C,
      FILE_ORIGIN,
      addPlan([{ sevenTvEmoteId: '7tv-9', name: 'Clueless', imageUrl: null }]),
    );
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

  /** Answers the next mutation and waits out the pacing delay behind it. */
  function answerNext(body: object = {}): void {
    httpMock.expectOne(GQL_ENDPOINT).flush(body);
    vi.advanceTimersByTime(RUN_DELAY_MS);
  }

  function nextMutation() {
    return httpMock.expectOne(GQL_ENDPOINT);
  }

  // The acceptance check of the plan-driven run: a plan of `add` rows only is a plain copy, and it
  // must put exactly the requests on the wire it always did — the mutations, then the report, then
  // the resync, in that order and with those bodies.
  it('sends exactly the plain-copy requests for a plan of add rows only', () => {
    const sent: { method: string; url: string; body: unknown }[] = [];
    const record = (req: ReturnType<HttpTestingController['expectOne']>) => {
      sent.push({ method: req.request.method, url: req.request.url, body: req.request.body });
      return req;
    };

    service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));
    record(httpMock.expectOne(GQL_ENDPOINT)).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    record(httpMock.expectOne(GQL_ENDPOINT)).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    record(httpMock.expectOne(SYNC_IMPORTED_B)).flush(null, { status: 204, statusText: 'OK' });
    record(httpMock.expectOne(RESYNC_B)).flush(null, { status: 202, statusText: 'Accepted' });

    // A literal copy of the mutation a plain copy has always sent — not read back from the
    // request, so a changed mutation body fails here.
    const addQuery = `
  mutation AddEmote($setId: Id!, $emoteId: Id!, $alias: String) {
    emoteSets {
      emoteSet(id: $setId) {
        addEmote(id: { emoteId: $emoteId, alias: $alias }) {
          id
        }
      }
    }
  }
`;
    expect(sent).toEqual([
      {
        method: 'POST',
        url: GQL_ENDPOINT,
        body: { query: addQuery, variables: { setId: 'set-b', emoteId: '7tv-1', alias: 'PogU' } },
      },
      {
        method: 'POST',
        url: GQL_ENDPOINT,
        body: { query: addQuery, variables: { setId: 'set-b', emoteId: '7tv-2', alias: 'KEKW' } },
      },
      {
        method: 'POST',
        url: SYNC_IMPORTED_B,
        body: {
          sevenTvEmoteIds: ['7tv-1', '7tv-2'],
          sourceChannelName: 'brudivoeller_tv',
          sourceKind: 'channel',
          leaderboardSort: null,
          targetEmoteSetId: 'set-b',
        },
      },
      { method: 'POST', url: RESYNC_B, body: {} },
    ]);
    expect(service.run()?.settlement).toBe('settled');
    expect(service.removalReport()).toBe('idle');
  });

  describe('transfer plan rows', () => {
    it('sends the alias a rename row carries, not the source name', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, { rows: [renameRow(SOURCE_X, 'KappaAlt')] });

      const req = nextMutation();
      expect(req.request.body.query).toContain('addEmote(');
      expect(req.request.body.variables).toEqual({
        setId: 'set-b',
        emoteId: 'src-x',
        alias: 'KappaAlt',
      });
      req.flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-x']);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('sends a replace row as REMOVE of the target, then ADD of the source, back to back on one set', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });

      expect(service.destructiveRunActive()).toBe(true);
      const remove = nextMutation();
      expect(remove.request.body.query).toContain('removeEmote(id: { emoteId: $emoteId })');
      expect(remove.request.body.variables).toEqual({ setId: 'set-b', emoteId: 'tgt-x' });
      remove.flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      // The ADD of the same row comes next — before the next row's request.
      const add = nextMutation();
      expect(add.request.body.query).toContain('addEmote(');
      expect(add.request.body.variables).toEqual({
        setId: 'set-b',
        emoteId: 'src-x',
        alias: 'Kappa',
      });
      add.flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      const next = nextMutation();
      expect(next.request.body.variables).toEqual({
        setId: 'set-b',
        emoteId: 'src-y',
        alias: 'Pog',
      });
      next.flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      expect(service.destructiveRunActive()).toBe(false);
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'OK' });
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('sends an adopt row as one updateEmoteAlias, old alias in the id and source name as argument, and no addEmote', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, { rows: [adoptRow(SOURCE_X, 'KappaOld')] });

      const req = nextMutation();
      expect(req.request.body.query).toContain(
        'updateEmoteAlias(id: { emoteId: $emoteId, alias: $currentAlias }, alias: $alias)',
      );
      expect(req.request.body.query).not.toContain('addEmote');
      expect(req.request.body.variables).toEqual({
        setId: 'set-b',
        emoteId: 'src-x',
        currentAlias: 'KappaOld',
        alias: 'Kappa',
      });
      req.flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      // An adopt reports nothing — only the resync pulls the renamed entry in.
      expect(service.queue()[0].status).toBe('done');
      httpMock.expectNone(SYNC_IMPORTED_B);
      httpMock.expectNone(SYNC_DELETED_B);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('gives a 409 its own reason, keeps 7TV text for the protocol and runs on', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [adoptRow(SOURCE_X, 'KappaOld'), addRow(SOURCE_Y)],
      });

      answerNext(gqlRejection('BAD_REQUEST emote name conflict', 409));
      answerNext({});

      const [adopt, add] = service.run()?.result?.items ?? [];
      expect(adopt.status).toBe('failed');
      expect(adopt.errorMessage).toBe('Name inzwischen vergeben.');
      expect(adopt.sevenTvErrorMessage).toBe('BAD_REQUEST emote name conflict');
      expect(add.status).toBe('done');
      expect(service.abortedForPrivileges()).toBe(false);

      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-y']);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('marks a replace that failed after its REMOVE with the gap reason and reports the removal', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, { rows: [replaceRow(SOURCE_X, 'tgt-x')] });

      answerNext({});
      answerNext(gqlRejection('BAD_REQUEST this emote has a conflicting name', 409));

      const [row] = service.run()?.result?.items ?? [];
      expect(row).toMatchObject({ status: 'failed', failedStep: 1, completedSteps: 1 });
      expect(row.errorMessage).toBe('Entfernt, aber nicht hinzugefügt.');
      expect(row.sevenTvErrorMessage).toBe('BAD_REQUEST this emote has a conflicting name');
      expect(service.run()?.removedCount).toBe(1);

      httpMock.expectNone(SYNC_IMPORTED_B);
      expect(httpMock.expectOne(SYNC_DELETED_B).request.body.sevenTvEmoteIds).toEqual(['tgt-x']);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it("reports the removal through sync-deleted with the target set's emoteSetId", () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, { rows: [replaceRow(SOURCE_X, 'tgt-x')] });
      answerNext({});
      answerNext({});

      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'OK' });
      const removal = httpMock.expectOne(SYNC_DELETED_B);
      expect(removal.request.method).toBe('POST');
      expect(removal.request.body).toEqual({ emoteSetId: 'set-b', sevenTvEmoteIds: ['tgt-x'] });
      expect(service.removalReport()).toBe('pending');
      removal.flush({ archivedCount: 1, notFoundIds: [] });
      expect(service.removalReport()).toBe('succeeded');
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('throws for a replace plan against an untracked target and sends nothing', () => {
      expect(() =>
        service.startImport(TARGET_UNTRACKED, CHANNEL_ORIGIN, {
          rows: [replaceRow(SOURCE_X, 'tgt-x')],
        }),
      ).toThrow();

      expect(service.isRunning()).toBe(false);
      expect(service.run()).toBeNull();
      httpMock.expectNone(GQL_ENDPOINT);
    });

    it('sends no removal report when no REMOVE was confirmed — and no ADD after a failed REMOVE', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });

      answerNext(gqlRejection('NOT_FOUND emote not in set', 404));
      // The next request is the next row's ADD, not the failed row's second step.
      const next = nextMutation();
      expect(next.request.body.variables.emoteId).toBe('src-y');
      next.flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      const [replace] = service.run()?.result?.items ?? [];
      expect(replace).toMatchObject({ status: 'failed', failedStep: 0, completedSteps: 0 });
      expect(replace.errorMessage).toBe('NOT_FOUND emote not in set');
      expect(service.run()?.removedCount).toBe(0);

      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'OK' });
      httpMock.expectNone(SYNC_DELETED_B);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.removalReport()).toBe('idle');
    });

    it('re-sends a failed removal report from the same run record', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, { rows: [replaceRow(SOURCE_X, 'tgt-x')] });
      answerNext({});
      answerNext({});
      const record = service.run();

      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'OK' });
      httpMock.expectOne(SYNC_DELETED_B).flush({}, { status: 401, statusText: 'Unauthorized' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.removalReport()).toBe('failed');

      service.retryRemovalReport();

      const retry = httpMock.expectOne(SYNC_DELETED_B);
      expect(retry.request.body).toEqual({ emoteSetId: 'set-b', sevenTvEmoteIds: ['tgt-x'] });
      retry.flush({ archivedCount: 1, notFoundIds: [] });
      expect(service.removalReport()).toBe('succeeded');
      expect(service.run()).toBe(record);
      httpMock.expectNone(SYNC_IMPORTED_B);
    });

    it('asks for unknown on a lost answer only when the plan has a replace row', () => {
      // Add-only: an HTTP 500 is `failed`, as it always was, and nothing is re-read.
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([SOURCE_X]));
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      expect(service.run()?.result?.items[0].status).toBe('failed');
      expect(service.run()?.settlement).toBe('settled');
      httpMock.expectNone(GQL_ENDPOINT);

      // With a replace row in the plan, the same answer on an add row is `unknown`.
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      expect(service.run()?.result?.items[1].status).toBe('unknown');
      expect(service.run()?.settlement).toBe('pending');

      // The one re-read, answered with a failure so the run settles without it.
      httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'unavailable' }] });
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'OK' });
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('clears up an unknown ADD through the re-read and reports it as imported', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 502, statusText: 'Bad Gateway' });
      vi.advanceTimersByTime(RUN_DELAY_MS);

      const read = httpMock.expectOne(GQL_ENDPOINT);
      expect(read.request.body.query).toContain('emotes(page: $page, perPage: $perPage)');
      expect(read.request.headers.has('Authorization')).toBe(false);
      read.flush(
        setEntriesPage([
          { id: 'src-x', alias: 'Kappa' },
          { id: 'src-y', alias: 'Pog' },
        ]),
      );

      expect(service.run()?.settlement).toBe('settled');
      const row = service.run()?.result?.items[1];
      expect(row).toMatchObject({ status: 'done', completedSteps: 1, failedStep: null });
      expect(service.run()?.unknownCount).toBe(0);
      expect(service.run()?.result?.doneKeys).toEqual(['src-x', 'src-y']);

      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual([
        'src-x',
        'src-y',
      ]);
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('settles an unknown REMOVE whose target is gone as a gap, counts it confirmed and reports the removal', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, { rows: [replaceRow(SOURCE_X, 'tgt-x')] });
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 503, statusText: 'Unavailable' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      // No ADD after an unanswered REMOVE: the next request is the re-read.
      const read = httpMock.expectOne(GQL_ENDPOINT);
      expect(read.request.body.query).toContain('emotes(page');
      read.flush(setEntriesPage([{ id: 'other', alias: 'Other' }]));

      const [row] = service.run()?.result?.items ?? [];
      expect(row).toMatchObject({ status: 'failed', completedSteps: 1, failedStep: 1 });
      expect(row.errorMessage).toBe('Entfernt, aber nicht hinzugefügt.');
      expect(service.run()?.removedCount).toBe(1);

      httpMock.expectNone(SYNC_IMPORTED_B);
      expect(httpMock.expectOne(SYNC_DELETED_B).request.body.sevenTvEmoteIds).toEqual(['tgt-x']);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('leaves rows unknown when the re-read fails, keeps them out of sync-imported, but still reports their confirmed REMOVE', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      // The replace row's ADD gets no answer: REMOVE confirmed, ADD unknown.
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      answerNext({});

      httpMock.expectOne(GQL_ENDPOINT).error(new ProgressEvent('error'));

      const [replace, add] = service.run()?.result?.items ?? [];
      expect(replace).toMatchObject({ status: 'unknown', completedSteps: 1, failedStep: 1 });
      expect(add.status).toBe('done');
      expect(service.run()).toMatchObject({
        settlement: 'settled',
        unknownCount: 1,
        removedCount: 1,
      });

      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-y']);
      expect(httpMock.expectOne(SYNC_DELETED_B).request.body.sevenTvEmoteIds).toEqual(['tgt-x']);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('sends no report before the settled outcome is published', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);

      expect(service.isRunning()).toBe(false);
      expect(service.run()?.settlement).toBe('pending');
      httpMock.expectNone(SYNC_IMPORTED_B);
      httpMock.expectNone(SYNC_DELETED_B);
      httpMock.expectNone(RESYNC_B);
      expect(service.syncReport()).toBe('idle');

      httpMock.expectOne(GQL_ENDPOINT).flush(setEntriesPage([{ id: 'src-x', alias: 'Kappa' }]));

      expect(service.run()?.settlement).toBe('settled');
      expect(service.run()?.result?.items[1].status).toBe('failed');
      expect(service.syncReport()).toBe('pending');
      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-x']);
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('settles and reports a run whose re-read outlives a second startImport, without ever showing it again', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      const read = httpMock.expectOne(GQL_ENDPOINT);

      const secondPlan = addPlan([SOURCE_Z]);
      service.startImport(TARGET_C, FILE_ORIGIN, secondPlan);
      const second = service.run();
      expect(service.items().map((item) => item.key)).toEqual(['src-z']);

      read.flush(
        setEntriesPage([
          { id: 'src-x', alias: 'Kappa' },
          { id: 'src-y', alias: 'Pog' },
        ]),
      );

      // Run 1's reports go out — they record 7TV changes that happened.
      const staleImported = httpMock.expectOne(SYNC_IMPORTED_B);
      expect(staleImported.request.body.sevenTvEmoteIds).toEqual(['src-x', 'src-y']);
      const staleRemoved = httpMock.expectOne(SYNC_DELETED_B);
      expect(staleRemoved.request.body.sevenTvEmoteIds).toEqual(['tgt-x']);
      const staleResync = httpMock.expectOne(RESYNC_B);
      // …but run 1 is never shown again, and none of its report states lands on run 2.
      expect(service.run()).toBe(second);
      expect(service.items().map((item) => item.key)).toEqual(['src-z']);
      expect(service.syncReport()).toBe('idle');
      expect(service.removalReport()).toBe('idle');
      expect(service.resyncTrigger()).toBe('idle');
      staleImported.flush({}, { status: 401, statusText: 'Unauthorized' });
      staleRemoved.flush({}, { status: 401, statusText: 'Unauthorized' });
      staleResync.flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.syncReport()).toBe('idle');
      expect(service.removalReport()).toBe('idle');
      expect(service.resyncTrigger()).toBe('idle');

      answerNext({});
      expect(service.run()?.plan).toBe(secondPlan);
      httpMock.expectOne(SYNC_IMPORTED_C).flush(null, { status: 204, statusText: 'OK' });
      httpMock.expectOne(RESYNC_C).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.syncReport()).toBe('succeeded');
    });

    it('keeps a re-read answer after reset() off the dock but still sends its reports', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      const read = httpMock.expectOne(GQL_ENDPOINT);

      service.reset();
      read.flush(setEntriesPage([{ id: 'src-x', alias: 'Kappa' }]));

      expect(service.run()).toBeNull();
      expect(service.items()).toEqual([]);
      const imported = httpMock.expectOne(SYNC_IMPORTED_B);
      expect(imported.request.body.sevenTvEmoteIds).toEqual(['src-x']);
      const removed = httpMock.expectOne(SYNC_DELETED_B);
      expect(removed.request.body.sevenTvEmoteIds).toEqual(['tgt-x']);
      imported.flush(null, { status: 204, statusText: 'OK' });
      removed.flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.syncReport()).toBe('idle');
      expect(service.removalReport()).toBe('idle');
      expect(service.resyncTrigger()).toBe('idle');
    });

    it("shows the engine queue while running and the run's own result once it is not", () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, { rows: [adoptRow(SOURCE_X, 'KappaOld')] });

      expect(service.items()).toEqual([
        expect.objectContaining({ key: 'src-x', status: 'in-progress' }),
      ]);
      expect(service.items()[0].transfer.action).toBe('adoptSourceName');
      expect(service.destructiveRunActive()).toBe(false);

      answerNext({});

      expect(service.isRunning()).toBe(false);
      expect(service.items()).toBe(service.run()?.result?.items);
      expect(service.items()[0].status).toBe('done');
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('leaves out a queue row the shown plan does not know instead of throwing', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan([SOURCE_X]));
      const shown = service.run();
      if (shown === null) {
        throw new Error('run expected');
      }

      service.run.set({ ...shown, plan: { rows: [] } });

      expect(service.items()).toEqual([]);
      // The record on screen is no longer the one the run started with, so its completion is
      // dropped like any superseded run's.
      answerNext({});
      httpMock.expectNone(SYNC_IMPORTED_B);
    });

    it('keeps an unknown ADD unknown when the re-read finds the source id under another name', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);

      httpMock.expectOne(GQL_ENDPOINT).flush(
        setEntriesPage([
          { id: 'src-x', alias: 'Kappa' },
          { id: 'src-y', alias: 'PogSomeoneElse' },
        ]),
      );

      const add = service.run()?.result?.items[1];
      expect(add?.status).toBe('unknown');
      expect(add?.sevenTvErrorMessage).toBeUndefined();
      expect(service.run()?.unknownCount).toBe(1);
      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-x']);
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('settles an unknown REMOVE whose target is still there as not applied, and reports no removal', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 503, statusText: 'Unavailable' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      answerNext({});

      httpMock.expectOne(GQL_ENDPOINT).flush(
        setEntriesPage([
          { id: 'tgt-x', alias: 'Kappa' },
          { id: 'src-y', alias: 'Pog' },
        ]),
      );

      const [replace] = service.run()?.result?.items ?? [];
      expect(replace).toMatchObject({ status: 'failed', failedStep: 0, completedSteps: 0 });
      expect(replace.errorMessage).toBe('Laut Nachlesen nicht übernommen. (7TV-Fehler (503).)');
      expect(replace.sevenTvErrorMessage).toBe('7TV-Fehler (503).');
      expect(service.run()?.removedCount).toBe(0);

      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-y']);
      httpMock.expectNone(SYNC_DELETED_B);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('treats an incomplete re-read like a failed one: the unknown rows stay unknown', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);

      // The source id *is* under its alias — but the read only vouches for part of the set.
      const partial = setEntriesPage([{ id: 'src-y', alias: 'Pog' }]);
      partial.data.emoteSets.emoteSet.emotes.totalCount = 7;
      httpMock.expectOne(GQL_ENDPOINT).flush(partial);

      expect(service.run()).toMatchObject({ settlement: 'settled', unknownCount: 1 });
      expect(service.run()?.result?.items[1].status).toBe('unknown');
      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-x']);
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('settles a re-read that never answers like a failed one once its time budget runs out', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);
      const read = httpMock.expectOne(GQL_ENDPOINT);

      vi.advanceTimersByTime(19_999);
      expect(service.run()?.settlement).toBe('pending');
      vi.advanceTimersByTime(1);

      expect(read.cancelled).toBe(true);
      expect(service.run()).toMatchObject({ settlement: 'settled', unknownCount: 1 });
      expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-x']);
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it.each([
      { live: 'Kappa', status: 'done' },
      { live: 'KappaOld', status: 'failed' },
    ])(
      'settles an unknown adopt whose target now sits under $live as $status',
      ({ live, status }) => {
        service.startImport(TARGET_B, CHANNEL_ORIGIN, {
          rows: [replaceRow(SOURCE_Y, 'tgt-y'), adoptRow(SOURCE_X, 'KappaOld')],
        });
        answerNext({});
        answerNext({});
        httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
        vi.advanceTimersByTime(RUN_DELAY_MS);

        httpMock.expectOne(GQL_ENDPOINT).flush(
          setEntriesPage([
            { id: 'src-y', alias: 'Pog' },
            { id: 'src-x', alias: live },
          ]),
        );

        const adopt = service.run()?.result?.items[1];
        expect(adopt?.status).toBe(status);
        if (status === 'failed') {
          expect(adopt?.errorMessage).toMatch(/^Laut Nachlesen nicht übernommen\./);
        }
        // An adopt reports nothing either way: the imported ids are the replace's alone.
        expect(httpMock.expectOne(SYNC_IMPORTED_B).request.body.sevenTvEmoteIds).toEqual(['src-y']);
        httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
        httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
      },
    );

    it('gives a replace cancelled between REMOVE and ADD the gap reason and reports the removal', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      nextMutation().flush({});
      // In the pacing pause before the ADD.
      service.cancel();

      const [replace, add] = service.run()?.result?.items ?? [];
      expect(replace).toMatchObject({ status: 'failed', failedStep: 1, completedSteps: 1 });
      expect(replace.errorMessage).toBe('Entfernt, aber nicht hinzugefügt.');
      expect(replace.sevenTvErrorMessage).toBe('Mitten in der Zeile abgebrochen.');
      expect(add.status).toBe('cancelled');
      httpMock.expectNone(GQL_ENDPOINT);

      httpMock.expectNone(SYNC_IMPORTED_B);
      expect(httpMock.expectOne(SYNC_DELETED_B).request.body.sevenTvEmoteIds).toEqual(['tgt-x']);
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('counts a replace run as destructive until its re-read has settled it', () => {
      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      answerNext({});
      answerNext({});
      httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 500, statusText: 'Server Error' });
      vi.advanceTimersByTime(RUN_DELAY_MS);

      expect(service.isRunning()).toBe(false);
      expect(service.run()?.settlement).toBe('pending');
      expect(service.destructiveRunActive()).toBe(true);

      httpMock.expectOne(GQL_ENDPOINT).flush({ errors: [{ message: 'unavailable' }] });

      expect(service.destructiveRunActive()).toBe(false);
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'OK' });
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });
  });

  // A tab that dies between REMOVE and ADD leaves a gap no protocol was written for yet — the
  // back-out file alone cannot say *which* row it was. `TestBed.tick()` flushes the constructor's
  // effect, the same pattern `LiveQuotaService`'s own spec uses for its effects.
  describe('beforeunload guard', () => {
    it('registers a handler exactly while destructiveRunActive is true, removed once the run settles', () => {
      const addSpy = vi.spyOn(window, 'addEventListener');
      const removeSpy = vi.spyOn(window, 'removeEventListener');

      service.startImport(TARGET_B, CHANNEL_ORIGIN, {
        rows: [replaceRow(SOURCE_X, 'tgt-x'), addRow(SOURCE_Y)],
      });
      TestBed.tick();
      expect(service.destructiveRunActive()).toBe(true);
      expect(addSpy).toHaveBeenCalledWith('beforeunload', expect.any(Function));
      expect(removeSpy).not.toHaveBeenCalledWith('beforeunload', expect.any(Function));

      // Three mutations run back to back: the replace row's REMOVE, its own ADD, then the plain
      // add row's ADD — see the identical drain in "sends a replace row as REMOVE of the target,
      // then ADD of the source, back to back on one set" above.
      answerNext({});
      answerNext({});
      answerNext({});
      TestBed.tick();

      expect(service.destructiveRunActive()).toBe(false);
      expect(removeSpy).toHaveBeenCalledWith('beforeunload', expect.any(Function));

      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'OK' });
      httpMock.expectOne(SYNC_DELETED_B).flush({ archivedCount: 1, notFoundIds: [] });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('never registers a handler for an add-only run', () => {
      const addSpy = vi.spyOn(window, 'addEventListener');

      service.startImport(TARGET_B, CHANNEL_ORIGIN, addPlan(ROWS));
      TestBed.tick();

      expect(service.destructiveRunActive()).toBe(false);
      expect(addSpy).not.toHaveBeenCalledWith('beforeunload', expect.any(Function));

      runTwoRowsToDone();
      TestBed.tick();
      httpMock.expectOne(SYNC_IMPORTED_B).flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne(RESYNC_B).flush(null, { status: 202, statusText: 'Accepted' });
      expect(addSpy).not.toHaveBeenCalledWith('beforeunload', expect.any(Function));
    });
  });
});
