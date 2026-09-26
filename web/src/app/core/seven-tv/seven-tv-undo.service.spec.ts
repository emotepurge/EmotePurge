import { HttpRequest, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { REPORT_TIMEOUT_MS } from './seven-tv-delete.service';
import {
  SyncDeletedInSetResponse,
  SyncRestoredInSetResponse,
  UnresolvedChannel,
} from './seven-tv-emote-set.model';
import { SevenTvRunArbiter } from './seven-tv-run-arbiter';
import { RUN_DELAY_MS } from './seven-tv-run-engine';
import { SevenTvSetEntries } from './seven-tv-set-entries';
import { SevenTvTokenService } from './seven-tv-token.service';
import {
  RECHECK_READ_TIMEOUT_MS,
  SevenTvUndoService,
  UNDO_NOTICE_MS,
  UNDO_SETTLE_READ_TIMEOUT_MS,
  UndoRunTarget,
} from './seven-tv-undo.service';
import { UndoCandidate, UndoSourceFileInfo } from './undo-candidate';
import { UndoPlan, UndoPlanRow, UndoSkippedRow, classifyUndoRows } from './undo-plan';

// The keys the engine and this service translate.
const DE_TRANSLATIONS = {
  massDelete: {
    errors: {
      tokenInvalid: 'Token ungültig.',
      rateLimited: 'Rate Limit.',
      networkError: 'Netzwerkfehler.',
      genericStatus: '7TV-Fehler ({{ status }}).',
      rateLimitedGaveUp: 'Übersprungen.',
      cancelledMidRow: 'Mitten in der Zeile abgebrochen.',
      beforeStepFailed: 'Prüfung fehlgeschlagen.',
    },
  },
  undo: {
    errors: {
      removedButNotRestored: 'Quelle entfernt, Ziel nicht vollständig zurück.',
      cancelledMidRow: 'Nach dem Entfernen abgebrochen.',
      restoreIncomplete: 'Nicht alle Einträge zurück.',
      unknownOutcome: 'Laut Nachlesen nicht übernommen.',
      skippedDrift: 'Übersprungen — Set verändert.',
      recheckUnavailable: 'Übersprungen — Set nicht lesbar.',
    },
  },
};
const T = DE_TRANSLATIONS.undo.errors;

const GQL_ENDPOINT = 'https://7tv.io/v4/gql';
const SET_ID = 'set-t';
const SYNC_DELETED = `/api/seventv/emote-sets/${SET_ID}/sync-deleted`;
const SYNC_RESTORED = `/api/seventv/emote-sets/${SET_ID}/sync-restored`;
const RESYNC = '/api/channels/kanal_t/resync';
/** The engine's blind wait after an HTTP 429 without rate-limit headers. */
const BLIND_RATE_LIMIT_WAIT_MS = 60_000;

const SOURCE_FILE: UndoSourceFileInfo = {
  stage: 'finished',
  exportedAt: '2026-09-25T10:00:00.000Z',
  verifiedAt: null,
  finishedAt: '2026-09-25T10:00:00.000Z',
  origin: { kind: 'channel', channelName: 'kanal_s' },
};

/** The target is the tracked channel's active set: reports expect `kanal_t`. */
const TARGET_ACTIVE: UndoRunTarget = {
  setId: SET_ID,
  expectedChannelName: 'kanal_t',
  hostChannelName: 'kanal_t',
  setName: 'tttt',
  ownerOrChannelLabel: 'kanal_t',
  trackedChannelName: 'kanal_t',
  ownerDisplayName: 'Olaf',
  sourceFile: SOURCE_FILE,
};
/** A non-active set of a tracked channel: nothing to expect, no resync of ours. */
const TARGET_NON_ACTIVE: UndoRunTarget = { ...TARGET_ACTIVE, expectedChannelName: null };
/** An untracked account's set (AK 22). */
const TARGET_UNTRACKED: UndoRunTarget = {
  ...TARGET_ACTIVE,
  expectedChannelName: null,
  trackedChannelName: null,
  ownerOrChannelLabel: 'Olaf',
};

interface LiveEntry {
  id: string;
  alias: string | null;
}

/** A replace candidate `n`: source `src-n` under `An`, the target `tgt-n` it replaced once held
 *  `entries` (by default exactly the collision alias `An`, F11). */
function cand(
  n: string,
  options: {
    entries?: (string | null)[];
    defaultName?: string | null;
    provenance?: UndoCandidate['provenance'];
  } = {},
): UndoCandidate {
  return {
    sourceSevenTvEmoteId: `src-${n}`,
    sourceName: `Source${n}`,
    alias: `A${n}`,
    fileStatus: 'done',
    target: {
      sevenTvEmoteId: `tgt-${n}`,
      entries: (options.entries ?? [`A${n}`]).map((alias) => ({ alias })),
      defaultName: options.defaultName ?? null,
    },
    provenance: options.provenance ?? 'confirmed',
  };
}

/** A typed read — what the flow and the dialog classify against. */
function typedRead(entries: LiveEntry[]): SevenTvSetEntries {
  const aliasesById = new Map<string, string[]>();
  const aliaslessIds = new Set<string>();
  const defaultNameById = new Map<string, string>();
  const animatedById = new Map<string, boolean>();
  for (const entry of entries) {
    const aliases = aliasesById.get(entry.id) ?? [];
    if (entry.alias === null) {
      aliaslessIds.add(entry.id);
    } else {
      aliases.push(entry.alias);
    }
    aliasesById.set(entry.id, aliases);
    defaultNameById.set(entry.id, '');
    animatedById.set(entry.id, false);
  }
  return {
    aliasesById,
    aliaslessIds,
    defaultNameById,
    animatedById,
    occupiedSlots: entries.length,
    complete: true,
  };
}

/** The stamped plan: the candidates classified against the live state the dialog saw. */
function plan(candidates: UndoCandidate[], live: LiveEntry[]): UndoPlan {
  return classifyUndoRows(candidates, typedRead(live));
}

/** The one row of a single-candidate plan — asserted to run in `mode`. */
function row(candidate: UndoCandidate, live: LiveEntry[], mode: UndoPlanRow['mode']): UndoPlanRow {
  const [only] = plan([candidate], live).rows;
  expect(only?.mode).toBe(mode);
  return only;
}

/** The standard `full` row of candidate `n`: the source stands exactly under `An`, the target is
 *  gone. */
function fullRow(n: string, options: Parameters<typeof cand>[1] = {}): UndoPlanRow {
  return row(cand(n, options), [{ id: `src-${n}`, alias: `A${n}` }], 'full');
}

/** The standard `addOnly` row of candidate `n`: the source is gone, the target too. */
function addOnlyRow(n: string, options: Parameters<typeof cand>[1] = {}): UndoPlanRow {
  return row(cand(n, options), [], 'addOnly');
}

/** One page of the tokenless set read (`loadSevenTvSetEntries`) holding exactly `entries`;
 *  `complete: false` makes the page's total disagree with what it carries. */
function readPage(entries: LiveEntry[], complete = true) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: entries.length + (complete ? 0 : 1),
            pageCount: 1,
            items: entries.map((entry) => ({ alias: entry.alias, emote: { id: entry.id } })),
          },
        },
      },
    },
  };
}

function isGql(req: HttpRequest<unknown>, fragment: string): boolean {
  const body = req.body as { query?: string } | null;
  return req.url === GQL_ENDPOINT && (body?.query ?? '').includes(fragment);
}
const isRead = (req: HttpRequest<unknown>) => isGql(req, 'emotes(page: $page');
const isRemove = (req: HttpRequest<unknown>) => isGql(req, 'removeEmote');
const isAdd = (req: HttpRequest<unknown>) => isGql(req, 'addEmote');
const isChannelRoute = (req: HttpRequest<unknown>) => req.url.startsWith('/api/channels/');

function answer(
  overrides: Partial<SyncDeletedInSetResponse> | Partial<SyncRestoredInSetResponse> = {},
): SyncDeletedInSetResponse & SyncRestoredInSetResponse {
  return {
    reportedCount: 1,
    channels: [],
    unresolvedChannel: null,
    resyncTriggered: [],
    ...overrides,
  } as SyncDeletedInSetResponse & SyncRestoredInSetResponse;
}

function gqlRejection(message: string, extensions: Record<string, unknown>) {
  return { errors: [{ message, extensions }] };
}

describe('SevenTvUndoService', () => {
  let service: SevenTvUndoService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;
  let readCount: number;

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
    service = TestBed.inject(SevenTvUndoService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
    readCount = 0;
  });

  afterEach(() => {
    // `httpMock.verify()` throws on a leftover open request — a real failure in the test above, not
    // a fixture bug. But if it runs first and throws, the two lines after it never run, and fake
    // timers stay installed for every test that follows: one red test then cascades into dozens.
    // `finally` keeps teardown unconditional while still surfacing the `verify()` failure itself.
    try {
      httpMock.verify();
    } finally {
      vi.useRealTimers();
      vi.restoreAllMocks();
    }
  });

  function start(
    rows: UndoPlanRow[],
    options: {
      target?: UndoRunTarget;
      skipped?: UndoSkippedRow[];
      acknowledgedUnproven?: boolean;
    } = {},
  ): void {
    service.startUndo(
      options.target ?? TARGET_ACTIVE,
      rows,
      options.skipped ?? [],
      options.acknowledgedUnproven ?? false,
    );
  }

  /** The recheck read of a REMOVE — asserted to be out while no REMOVE is (AK 38). */
  function expectRead(): TestRequest {
    httpMock.expectNone(isRemove);
    readCount += 1;
    return httpMock.expectOne(isRead);
  }

  function answerRead(entries: LiveEntry[], complete = true): void {
    expectRead().flush(readPage(entries, complete));
  }

  function expectRemove(sourceId: string): TestRequest {
    const req = httpMock.expectOne(isRemove);
    expect(req.request.body.variables).toEqual({ setId: SET_ID, emoteId: sourceId });
    return req;
  }

  /** Every ADD of the undo carries a non-empty alias (AK 31, service half). */
  function expectAdd(targetId: string, alias: string): TestRequest {
    const req = httpMock.expectOne(isAdd);
    expect(typeof req.request.body.variables.alias).toBe('string');
    expect(req.request.body.variables.alias.length).toBeGreaterThan(0);
    expect(req.request.body.variables).toEqual({ setId: SET_ID, emoteId: targetId, alias });
    return req;
  }

  function next(): void {
    vi.advanceTimersByTime(RUN_DELAY_MS);
  }

  /** A `full` row of candidate `n` from its recheck read to its last ADD, everything answered. */
  function runFull(n: string, adds: string[] = [`A${n}`]): void {
    answerRead([{ id: `src-${n}`, alias: `A${n}` }]);
    expectRemove(`src-${n}`).flush({});
    next();
    for (const alias of adds) {
      expectAdd(`tgt-${n}`, alias).flush({});
      next();
    }
  }

  function runAddOnly(n: string, adds: string[] = [`A${n}`]): void {
    for (const alias of adds) {
      expectAdd(`tgt-${n}`, alias).flush({});
      next();
    }
  }

  function expectReport(
    url: string,
    ids: string[],
    expectedChannelName: string | null = 'kanal_t',
  ) {
    const req = httpMock.expectOne(url);
    expect(req.request.body).toEqual({ sevenTvEmoteIds: ids, expectedChannelName });
    return req;
  }

  function failForGood(url: string, status = 503): void {
    httpMock.expectOne(url).flush('down', { status, statusText: 'Down' });
    vi.advanceTimersByTime(2000);
    httpMock.expectOne(url).flush('down', { status, statusText: 'Down' });
    vi.advanceTimersByTime(4000);
    httpMock.expectOne(url).flush('down', { status, statusText: 'Down' });
  }

  describe('step order (AK 9, 31)', () => {
    it('runs a full row as a fresh read, the REMOVE of the source, then one ADD per missing entry under its explicit alias', () => {
      // A #74 duplicate cell with an aliasless entry: three ADDs, the aliasless one under the
      // file's default name (E21).
      const full = fullRow('1', { entries: ['A1', 'B1', null], defaultName: 'D1' });
      start([full]);

      expect(service.queue().map((item) => [item.key, item.name])).toEqual([['src-1', 'A1']]);
      runFull('1', ['A1', 'B1', 'D1']);

      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
      expect(service.items()[0]).toMatchObject({ status: 'done', completedSteps: 4 });
      expect(service.summary()).toMatchObject({ done: 1, removedCount: 1, restoredCount: 3 });
      expect(service.run()?.phase).toBe('closed');
    });

    it('runs an addOnly row as its ADDs alone — no read, no REMOVE', () => {
      start([addOnlyRow('2', { entries: ['A2', 'B2'] })]);

      httpMock.expectNone(isRead);
      runAddOnly('2', ['A2', 'B2']);

      httpMock.expectNone(SYNC_DELETED);
      expectReport(SYNC_RESTORED, ['tgt-2']).flush(answer());
      expect(service.items()[0]).toMatchObject({ status: 'done', completedSteps: 2 });
    });

    it('reads exactly once per REMOVE — N full rows, N reads, each right before its REMOVE (AK 38)', () => {
      start([fullRow('1'), addOnlyRow('2'), fullRow('3'), fullRow('4')]);

      runFull('1');
      runAddOnly('2');
      runFull('3');
      runFull('4');

      expect(readCount).toBe(3);
      expectReport(SYNC_DELETED, ['src-1', 'src-3', 'src-4']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1', 'tgt-2', 'tgt-3', 'tgt-4']).flush(answer());
    });
  });

  describe('second origin lock (spec 17 K2, AK 28)', () => {
    const unprovenFull = () => fullRow('1', { provenance: 'unproven' });
    const unprovenAddOnly = () => addOnlyRow('2', { provenance: 'unproven' });

    it('runs only the addOnly row of a mixed unproven plan without the confirmation — no read, no REMOVE', () => {
      start([unprovenFull(), unprovenAddOnly()], { acknowledgedUnproven: false });

      httpMock.expectNone(isRead);
      httpMock.expectNone(isRemove);
      runAddOnly('2');
      expectReport(SYNC_RESTORED, ['tgt-2']).flush(answer());

      const run = service.run()!;
      expect(run.destructive).toBe(false);
      expect(run.skipped.map((skipped) => [skipped.candidate.alias, skipped.reason])).toEqual([
        ['A1', 'skippedUnproven'],
      ]);
      // The unproven full row never became a row or a queue item — only the addOnly one did (see
      // `buildUndoRunProtocol`'s tests in `transfer-undo-export.spec.ts` for what this state turns
      // into as a protocol: `requested: 1, skipped: 1`).
      expect(run.rows).toHaveLength(1);
      expect(run.result?.items).toHaveLength(1);
      expect(run.acknowledgedUnproven).toBe(false);
    });

    it('runs the unproven full row once the confirmation is given', () => {
      start([unprovenFull(), unprovenAddOnly()], { acknowledgedUnproven: true });

      expect(service.run()?.destructive).toBe(true);
      runFull('1');
      runAddOnly('2');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1', 'tgt-2']).flush(answer());
      expect(service.run()?.skipped).toEqual([]);
    });

    it('runs a confirmed full row without the confirmation', () => {
      start([fullRow('1')], { acknowledgedUnproven: false });

      runFull('1');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
    });

    it('locks a full row whose candidate is unproven even when the row claims otherwise (fail-closed)', () => {
      const tampered: UndoPlanRow = { ...unprovenFull(), provenance: 'confirmed' };
      start([tampered], { acknowledgedUnproven: false });

      httpMock.expectNone(isRead);
      expect(service.isRunning()).toBe(false);
      expect(service.noticeSkipped().map((skipped) => skipped.reason)).toEqual(['skippedUnproven']);
    });

    it('starts nothing when every row is locked and shows the skips as the transient notice', () => {
      const arbiter = TestBed.inject(SevenTvRunArbiter);
      start([unprovenFull()], { acknowledgedUnproven: false });

      expect(service.isRunning()).toBe(false);
      expect(service.run()).toBeNull();
      expect(arbiter.activeRun()).toBeNull();
      expect(service.destructiveOpen()).toBe(false);
      expect(service.noticePending()).toBe(true);
      vi.advanceTimersByTime(UNDO_NOTICE_MS);
      expect(service.noticePending()).toBe(false);
    });

    it('takes two runnable rows with the same source id out as duplicateInFile — the queue is keyed by it', () => {
      const first = fullRow('1');
      const second: UndoPlanRow = {
        ...fullRow('2'),
        candidate: { ...cand('2'), sourceSevenTvEmoteId: 'src-1' },
      };
      start([first, second, addOnlyRow('3')]);

      expect(service.run()?.skipped.map((skipped) => skipped.reason)).toEqual([
        'duplicateInFile',
        'duplicateInFile',
      ]);
      runAddOnly('3');
      expectReport(SYNC_RESTORED, ['tgt-3']).flush(answer());
    });

    it('takes a row whose source id equals its own target id out as duplicateInFile too — defence in depth beyond the classification', () => {
      const selfLinked: UndoPlanRow = {
        ...fullRow('1'),
        candidate: { ...cand('1'), target: { ...cand('1').target, sevenTvEmoteId: 'src-1' } },
      };
      start([selfLinked, addOnlyRow('3')]);

      expect(service.run()?.skipped.map((skipped) => skipped.reason)).toEqual(['duplicateInFile']);
      runAddOnly('3');
      expectReport(SYNC_RESTORED, ['tgt-3']).flush(answer());
    });

    it("takes a row whose source id equals another runnable row's target id out as duplicateInFile too", () => {
      const first = fullRow('1'); // target tgt-1
      const crossLinked: UndoPlanRow = {
        ...addOnlyRow('2'),
        candidate: { ...cand('2'), sourceSevenTvEmoteId: 'tgt-1' },
      };
      start([first, crossLinked, addOnlyRow('3')]);

      expect(service.run()?.skipped.map((skipped) => skipped.reason)).toEqual(['duplicateInFile']);
      runFull('1');
      runAddOnly('3');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1', 'tgt-3']).flush(answer());
    });
  });

  describe('transport and privileges (AK 10)', () => {
    it('ends a REMOVE without an answer unknown, not failed', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').error(new ProgressEvent('error'));
      next();

      // The re-read cannot be had either: the row stays unknown and is reported nowhere.
      expectRead().error(new ProgressEvent('error'));
      httpMock.expectNone(SYNC_DELETED);
      expect(service.items()[0]).toMatchObject({ status: 'unknown', failedStep: 0 });
      expect(service.summary()).toMatchObject({ unknownCount: 1, unknownRemovalCount: 1 });
    });

    it('ends an ADD answered with a 5xx unknown, not failed', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      next();
      expectAdd('tgt-1', 'A1').flush('boom', { status: 503, statusText: 'Unavailable' });
      next();

      expectRead().error(new ProgressEvent('error'));
      expect(service.items()[0]).toMatchObject({ status: 'unknown', failedStep: 1 });
      // The confirmed REMOVE of the same row is still reported (spec 4.5 point 16).
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      httpMock.expectNone(SYNC_RESTORED);
    });

    it('aborts on a 401, clears the token and cancels the rest', () => {
      start([fullRow('1'), fullRow('2')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush('no', { status: 401, statusText: 'Unauthorized' });

      httpMock.expectNone(isRead);
      expect(tokenService.getToken()).toBeNull();
      expect(service.items().map((item) => item.status)).toEqual(['failed', 'cancelled']);
      expect(service.abortedForPrivileges()).toBe(true);
      expect(service.run()?.phase).toBe('closed');
    });

    it('aborts on LACKING_PRIVILEGES over HTTP 200, clears the token too and still reports the confirmed REMOVE', () => {
      start([fullRow('1'), fullRow('2')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      next();
      expectAdd('tgt-1', 'A1').flush(
        gqlRejection('lacking privileges', { code: 'LACKING_PRIVILEGES', status: 403 }),
      );

      expect(tokenService.getToken()).toBeNull();
      expect(service.abortedForPrivileges()).toBe(true);
      expect(service.items()[0]).toMatchObject({
        status: 'failed',
        completedSteps: 1,
        errorMessage: T.removedButNotRestored,
        sevenTvErrorMessage: 'lacking privileges',
      });
      expect(service.items()[1].status).toBe('cancelled');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      httpMock.expectNone(SYNC_RESTORED);
    });

    it('fails a row on a GraphQL rejection and keeps going — a gap after the REMOVE is named', () => {
      start([fullRow('1'), fullRow('2')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      next();
      expectAdd('tgt-1', 'A1').flush(gqlRejection('name taken', { code: 'CONFLICT', status: 409 }));
      next();
      runFull('2');

      expect(tokenService.getToken()).toBe('write-token');
      expect(service.items()[0]).toMatchObject({
        status: 'failed',
        failedStep: 1,
        completedSteps: 1,
        errorMessage: T.removedButNotRestored,
      });
      expect(service.summary()).toMatchObject({ gapCount: 1, removedCount: 2, failed: 1 });
      expectReport(SYNC_DELETED, ['src-1', 'src-2']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-2']).flush(answer());
    });
  });

  describe('recheck before every REMOVE (E19; AK 26, 27, 38)', () => {
    it('skips a row whose source got a second alias right after the previous REMOVE — no REMOVE, no ADD, the next row runs', () => {
      start([fullRow('1'), fullRow('2'), fullRow('3')]);
      runFull('1');

      // 275 ms after the first row, the second source carries a second alias.
      answerRead([
        { id: 'src-2', alias: 'A2' },
        { id: 'src-2', alias: 'X2' },
      ]);
      httpMock.expectNone(isRemove);
      httpMock.expectNone(isAdd);
      expect(service.progress()).toEqual({ finished: 2, total: 3 });
      next();
      runFull('3');

      expect(service.items()[1]).toMatchObject({
        status: 'cancelled',
        completedSteps: 0,
        failedStep: null,
        skippedReason: 'skippedDrift',
        errorMessage: T.skippedDrift,
        sourceEntriesAtRemove: [{ alias: 'A2' }, { alias: 'X2' }],
      });
      expect(service.summary()).toMatchObject({ skippedInRun: 1, cancelled: 0, done: 2 });
      expect(service.summary().skippedByReason.skippedDrift).toBe(1);
      expectReport(SYNC_DELETED, ['src-1', 'src-3']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1', 'tgt-3']).flush(answer());
    });

    it('skips a row whose target came back under a foreign name — the source stays (AK 29, recheck)', () => {
      start([fullRow('1')]);
      answerRead([
        { id: 'src-1', alias: 'A1' },
        { id: 'tgt-1', alias: 'C1' },
      ]);
      next();

      httpMock.expectNone(isRemove);
      expect(service.items()[0]).toMatchObject({
        status: 'cancelled',
        skippedReason: 'skippedDrift',
      });
      expect(service.run()?.phase).toBe('closed');
    });

    it('reads again before the REMOVE is retried after a rate-limit pause, and the new read decides', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush('slow down', { status: 429, statusText: 'Too Many Requests' });

      vi.advanceTimersByTime(BLIND_RATE_LIMIT_WAIT_MS);
      answerRead([
        { id: 'src-1', alias: 'A1' },
        { id: 'src-1', alias: 'Y1' },
      ]);
      next();

      expect(readCount).toBe(2);
      httpMock.expectNone(isRemove);
      expect(service.items()[0]).toMatchObject({
        status: 'cancelled',
        skippedReason: 'skippedDrift',
        // F13: the last read before the REMOVE wins.
        sourceEntriesAtRemove: [{ alias: 'A1' }, { alias: 'Y1' }],
      });
    });

    it('sends the retried REMOVE after a fresh read that still matches', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush('slow down', { status: 429, statusText: 'Too Many Requests' });
      vi.advanceTimersByTime(BLIND_RATE_LIMIT_WAIT_MS);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      next();
      expectAdd('tgt-1', 'A1').flush({});
      next();

      expect(readCount).toBe(2);
      expect(service.items()[0].status).toBe('done');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
    });

    it('skips a row whose read fails as recheckUnavailable and runs the next', () => {
      start([fullRow('1'), fullRow('2')]);
      expectRead().flush('boom', { status: 500, statusText: 'Server Error' });
      next();
      runFull('2');

      expect(service.items()[0]).toMatchObject({
        status: 'cancelled',
        skippedReason: 'recheckUnavailable',
        errorMessage: T.recheckUnavailable,
        // No read answered for this row: the entries the stamped plan verified.
        sourceEntriesAtRemove: [{ alias: 'A1' }],
      });
      expectReport(SYNC_DELETED, ['src-2']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-2']).flush(answer());
    });

    it('treats an incomplete read as a failed one — nothing is classified against half a set', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }], false);
      next();

      httpMock.expectNone(isRemove);
      expect(service.items()[0]).toMatchObject({ skippedReason: 'recheckUnavailable' });
    });

    it('gives the read its own deadline and skips the row when it runs out', () => {
      start([fullRow('1')]);
      const read = expectRead();
      vi.advanceTimersByTime(RECHECK_READ_TIMEOUT_MS);
      next();

      expect(read.cancelled).toBe(true);
      httpMock.expectNone(isRemove);
      expect(service.items()[0]).toMatchObject({ skippedReason: 'recheckUnavailable' });
    });

    it('stops reading after three failed reads in a row — the remaining full rows are skipped, addOnly rows run', () => {
      start([fullRow('1'), fullRow('2'), fullRow('3'), addOnlyRow('4'), fullRow('5')]);
      for (let failed = 0; failed < 3; failed += 1) {
        expectRead().error(new ProgressEvent('error'));
        next();
      }
      runAddOnly('4');
      httpMock.expectNone(isRead);
      next();

      expect(readCount).toBe(3);
      expect(service.items().map((item) => [item.status, item.skippedReason])).toEqual([
        ['cancelled', 'recheckUnavailable'],
        ['cancelled', 'recheckUnavailable'],
        ['cancelled', 'recheckUnavailable'],
        ['done', null],
        ['cancelled', 'recheckUnavailable'],
      ]);
      expectReport(SYNC_RESTORED, ['tgt-4']).flush(answer());
    });

    it('counts only consecutive failures — a read that answers starts the count again', () => {
      start([fullRow('1'), fullRow('2'), fullRow('3'), fullRow('4'), fullRow('5')]);
      expectRead().error(new ProgressEvent('error'));
      next();
      runFull('2');
      expectRead().error(new ProgressEvent('error'));
      next();
      expectRead().error(new ProgressEvent('error'));
      next();
      runFull('5');

      expect(readCount).toBe(5);
      expectReport(SYNC_DELETED, ['src-2', 'src-5']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-2', 'tgt-5']).flush(answer());
    });
  });

  describe('cancel (spec 4.4 point 11)', () => {
    it('cancels a row whose read is still out — no request was sent, so it is not unknown', () => {
      start([fullRow('1')]);
      const read = expectRead();
      service.cancel();

      expect(read.cancelled).toBe(true);
      httpMock.expectNone(isRemove);
      expect(service.items()[0]).toMatchObject({ status: 'cancelled', skippedReason: null });
      expect(service.summary()).toMatchObject({ cancelled: 1, skippedInRun: 0 });
      expect(service.run()?.phase).toBe('closed');
    });

    it('ends a row whose REMOVE is in flight unknown — 7TV may have applied it', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      const remove = expectRemove('src-1');
      service.cancel();

      expect(remove.cancelled).toBe(true);
      expect(service.items()[0]).toMatchObject({ status: 'unknown', failedStep: 0 });
      expect(service.run()?.phase).toBe('settling');
      // The re-read finds the source gone: applied after all.
      expectRead().flush(readPage([]));
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
    });

    it('fails a row cancelled between its REMOVE and its first ADD with cancelledMidRow and reports the REMOVE', () => {
      start([fullRow('1'), fullRow('2')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      service.cancel();

      expect(service.items().map((item) => item.status)).toEqual(['failed', 'cancelled']);
      expect(service.items()[0]).toMatchObject({
        failedStep: 1,
        completedSteps: 1,
        errorMessage: T.cancelledMidRow,
      });
      expect(service.summary().gapCount).toBe(1);
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      httpMock.expectNone(SYNC_RESTORED);
    });
  });

  describe('settling after an unknown step (spec 4.6; AK 11, 34)', () => {
    /** A full row whose REMOVE went unanswered; leaves the re-read pending. */
    function removeUnanswered(entries: (string | null)[] = ['A1']): void {
      start([fullRow('1', { entries })]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush('boom', { status: 502, statusText: 'Bad Gateway' });
      next();
    }

    /** A full row with two ADDs whose first ADD went unanswered; leaves the re-read pending. */
    function firstAddUnanswered(): void {
      start([fullRow('1', { entries: ['A1', 'B1'] })]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      next();
      expectAdd('tgt-1', 'A1').flush('boom', { status: 502, statusText: 'Bad Gateway' });
      next();
    }

    it('confirms an unanswered REMOVE whose source is gone — failed at step 1, the source reported', () => {
      removeUnanswered();
      expect(service.settlement()).toBe('pending');
      expectRead().flush(readPage([]));

      expect(service.items()[0]).toMatchObject({
        status: 'failed',
        failedStep: 1,
        completedSteps: 1,
        errorMessage: T.removedButNotRestored,
      });
      expect(service.settlement()).toBe('settled');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      httpMock.expectNone(SYNC_RESTORED);
    });

    it('fails an unanswered REMOVE at step 0 when the source still stands exactly as planned', () => {
      removeUnanswered();
      expectRead().flush(readPage([{ id: 'src-1', alias: 'A1' }]));

      expect(service.items()[0]).toMatchObject({
        status: 'failed',
        failedStep: 0,
        completedSteps: 0,
        errorMessage: T.unknownOutcome,
      });
      httpMock.expectNone(SYNC_DELETED);
      expect(service.run()?.phase).toBe('closed');
    });

    it('leaves an unanswered REMOVE unknown when the source changed in another way', () => {
      removeUnanswered();
      expectRead().flush(
        readPage([
          { id: 'src-1', alias: 'A1' },
          { id: 'src-1', alias: 'X1' },
        ]),
      );

      expect(service.items()[0].status).toBe('unknown');
      expect(service.summary()).toMatchObject({ unknownCount: 1, unknownRemovalCount: 1 });
      httpMock.expectNone(SYNC_DELETED);
    });

    it('leaves an unanswered REMOVE unknown when the settle re-read comes back incomplete — never treats a partial read as readable', () => {
      removeUnanswered();
      // A single page whose own totalCount disagrees with what it carried — `complete: false`,
      // resolved without a second request (see `readPage`'s own doc).
      expectRead().flush(readPage([{ id: 'src-1', alias: 'A1' }], false));

      expect(service.items()[0]).toMatchObject({
        status: 'unknown',
        failedStep: 0,
        completedSteps: 0,
      });
      expect(service.settlement()).toBe('settled');
      httpMock.expectNone(SYNC_DELETED);
      httpMock.expectNone(SYNC_RESTORED);
    });

    it('confirms an unanswered ADD found on the target — the ADDs after it never ran, so the row is a gap', () => {
      firstAddUnanswered();
      expectRead().flush(readPage([{ id: 'tgt-1', alias: 'A1' }]));

      expect(service.items()[0]).toMatchObject({
        status: 'failed',
        completedSteps: 2,
        failedStep: 2,
        errorMessage: T.removedButNotRestored,
      });
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
    });

    it('settles the row done when the unanswered ADD was its last one', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      next();
      expectAdd('tgt-1', 'A1').error(new ProgressEvent('error'));
      next();
      expectRead().flush(readPage([{ id: 'tgt-1', alias: 'A1' }]));

      expect(service.items()[0]).toMatchObject({
        status: 'done',
        completedSteps: 2,
        failedStep: null,
      });
      expect(service.run()?.result?.doneKeys).toEqual(['src-1']);
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
    });

    it('fails an unanswered ADD missing from the target at its own step, without lowering completedSteps', () => {
      firstAddUnanswered();
      expectRead().flush(readPage([]));

      expect(service.items()[0]).toMatchObject({
        status: 'failed',
        failedStep: 1,
        completedSteps: 1,
        errorMessage: T.removedButNotRestored,
      });
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      httpMock.expectNone(SYNC_RESTORED);
    });

    it('counts an ADD left unknown after a confirmed REMOVE in unknownCount, but not in unknownRemovalCount', () => {
      firstAddUnanswered();
      expectRead().error(new ProgressEvent('error'));

      expect(service.items()[0]).toMatchObject({
        status: 'unknown',
        completedSteps: 1,
        failedStep: 1,
      });
      expect(service.summary()).toMatchObject({ unknownCount: 1, unknownRemovalCount: 0 });
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      httpMock.expectNone(SYNC_RESTORED);
    });

    it('clears up step 0 of an addOnly row as an ADD — never as a REMOVE, never in sync-deleted (AK 34)', () => {
      start([addOnlyRow('2')]);
      expectAdd('tgt-2', 'A2').flush('boom', { status: 503, statusText: 'Unavailable' });
      next();
      // The source is absent from the set — which must not read as a confirmed REMOVE.
      expectRead().flush(readPage([{ id: 'tgt-2', alias: 'A2' }]));

      expect(service.items()[0]).toMatchObject({ status: 'done', completedSteps: 1 });
      httpMock.expectNone(SYNC_DELETED);
      expectReport(SYNC_RESTORED, ['tgt-2']).flush(answer());
    });

    it('fails step 0 of an addOnly row at step 0 when its entry is missing — nothing reported', () => {
      start([addOnlyRow('2')]);
      expectAdd('tgt-2', 'A2').flush('boom', { status: 503, statusText: 'Unavailable' });
      next();
      expectRead().flush(readPage([]));

      expect(service.items()[0]).toMatchObject({
        status: 'failed',
        failedStep: 0,
        completedSteps: 0,
        errorMessage: T.unknownOutcome,
      });
      httpMock.expectNone(SYNC_DELETED);
      httpMock.expectNone(SYNC_RESTORED);
    });

    it('reads once for several unknown rows', () => {
      start([addOnlyRow('2'), addOnlyRow('3')]);
      expectAdd('tgt-2', 'A2').flush('boom', { status: 503, statusText: 'Unavailable' });
      next();
      expectAdd('tgt-3', 'A3').flush('boom', { status: 503, statusText: 'Unavailable' });
      next();

      expectRead().flush(
        readPage([
          { id: 'tgt-2', alias: 'A2' },
          { id: 'tgt-3', alias: 'A3' },
        ]),
      );
      expect(service.items().map((item) => item.status)).toEqual(['done', 'done']);
      expectReport(SYNC_RESTORED, ['tgt-2', 'tgt-3']).flush(answer());
    });

    it('keeps the rows unknown when the re-read runs out of time, and settles all the same', () => {
      firstAddUnanswered();
      const read = expectRead();
      expect(service.isSettling()).toBe(true);
      expect(service.run()?.phase).toBe('settling');

      vi.advanceTimersByTime(UNDO_SETTLE_READ_TIMEOUT_MS);

      expect(read.cancelled).toBe(true);
      expect(service.items()[0]).toMatchObject({ status: 'unknown', completedSteps: 1 });
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expect(service.isSettling()).toBe(false);
    });
  });

  describe('partial rows (E23, F19; AK 33, 37)', () => {
    /** An addOnly row whose second entry a third id holds: ADD A1, B1 left out. */
    const omittingRow = () =>
      row(cand('1', { entries: ['A1', 'B1'] }), [{ id: 'third', alias: 'B1' }], 'addOnly');

    it('marks an addOnly row with an omitted entry partial once all its ADDs are confirmed', () => {
      const addOnly = omittingRow();
      expect(addOnly.omittedEntries).toEqual([{ alias: 'B1', reason: 'targetNameTaken' }]);
      start([addOnly]);
      runAddOnly('1', ['A1']);
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());

      // The engine row stays done — the panel's projection; partial is the undo's own status.
      expect(service.items()[0]).toMatchObject({ status: 'done', undoStatus: 'partial' });
      expect(service.run()?.result?.doneKeys).toEqual(['src-1']);
      expect(service.summary()).toMatchObject({ partialRows: 1, done: 0, omittedEntryCount: 1 });
    });

    it('keeps an addOnly row with an omitted entry unknown when its ADD stays unanswered', () => {
      start([omittingRow()]);
      expectAdd('tgt-1', 'A1').flush('boom', { status: 503, statusText: 'Unavailable' });
      next();
      expectRead().error(new ProgressEvent('error'));

      expect(service.items()[0].undoStatus).toBe('unknown');
      expect(service.summary()).toMatchObject({
        unknownCount: 1,
        omittedEntryCount: 1,
        partialRows: 0,
      });
    });

    it('keeps an addOnly row with an omitted entry failed when its ADD is rejected', () => {
      start([omittingRow()]);
      expectAdd('tgt-1', 'A1').flush(gqlRejection('taken', { code: 'CONFLICT', status: 409 }));
      next();

      expect(service.items()[0].undoStatus).toBe('failed');
      expect(service.summary()).toMatchObject({ failed: 1, omittedEntryCount: 1, partialRows: 0 });
    });
  });

  it('adds exactly the missing entry next to a foreign one and carries the note (AK 36)', () => {
    // After a full row that failed at ADD B: S gone, A back on T, C foreign on T.
    const addOnly = row(
      cand('1', { entries: ['A1', 'B1'] }),
      [
        { id: 'tgt-1', alias: 'A1' },
        { id: 'tgt-1', alias: 'C1' },
      ],
      'addOnly',
    );
    start([addOnly]);
    expectAdd('tgt-1', 'B1').flush({});
    next();
    httpMock.expectNone(isRemove);
    expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());

    expect(service.summary().foreignNotedRows).toBe(1);
    // The note travels on the item itself; `buildUndoRunProtocol` (transfer-undo-export.spec.ts)
    // carries `UndoRunItem.notes` into the protocol row unchanged.
    expect(service.items()[0].notes).toEqual(['targetHasForeignEntries']);
  });

  describe('reports (spec 4.5; AK 12, 13, 14)', () => {
    it('sends sync-deleted first and sync-restored only once it has answered, with deduplicated ids', () => {
      start([fullRow('1'), fullRow('2'), addOnlyRow('3')]);
      runFull('1');
      runFull('2');
      runAddOnly('3');

      const deleted = expectReport(SYNC_DELETED, ['src-1', 'src-2']);
      httpMock.expectNone(SYNC_RESTORED);
      deleted.flush(answer({ reportedCount: 2 }));
      expectReport(SYNC_RESTORED, ['tgt-1', 'tgt-2', 'tgt-3']).flush(answer({ reportedCount: 3 }));

      httpMock.expectNone(isChannelRoute);
      expect(service.removalReport()).toBe('succeeded');
      expect(service.restoreReport()).toBe('succeeded');
      expect(service.run()?.phase).toBe('closed');
    });

    it('reports nothing when no step was confirmed', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush(gqlRejection('nope', { code: 'CONFLICT', status: 409 }));
      next();

      httpMock.expectNone(SYNC_DELETED);
      httpMock.expectNone(SYNC_RESTORED);
      expect(service.run()?.phase).toBe('closed');
      expect(service.isSettling()).toBe(false);
    });

    it('reads each answer threeway with its own reason', () => {
      start([fullRow('1')]);
      runFull('1');
      expectReport(SYNC_DELETED, ['src-1']).flush(
        answer({
          channels: [{ channelName: 'kanal_t', archivedCount: 0, notFoundIds: ['src-1'] }],
        }),
      );
      // A 403 is not retried.
      expectReport(SYNC_RESTORED, ['tgt-1']).flush('no', { status: 403, statusText: 'Forbidden' });

      expect([service.removalReport(), service.removalReportReason()]).toEqual([
        'partial',
        'shortfall',
      ]);
      expect([service.restoreReport(), service.restoreReportReason()]).toEqual([
        'failed',
        'forbidden',
      ]);
    });

    it('retries each report on its own with the same ids', () => {
      start([fullRow('1')]);
      runFull('1');
      failForGood(SYNC_DELETED);
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(
        answer({
          channels: [{ channelName: 'kanal_t', restoredCount: 0, notFoundIds: ['tgt-1'] }],
        }),
      );
      // Both failed-for-good is not the case here — no fallback resync.
      httpMock.expectNone(RESYNC);
      expect(service.removalReportReason()).toBe('unavailable');

      service.retryRemovalReport();
      expect(service.removalReport()).toBe('pending');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expect(service.removalReport()).toBe('succeeded');

      service.retryRestoreReport();
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
      expect(service.restoreReport()).toBe('succeeded');
      httpMock.expectNone(RESYNC);
    });

    it.each<UnresolvedChannel['reason']>(['notTracked', 'activeSetDiffers'])(
      'refuses to retry a report that ended in a channel mismatch (%s, N4)',
      (reason) => {
        start([fullRow('1')]);
        runFull('1');
        const mismatch = answer({ unresolvedChannel: { channelName: 'kanal_t', reason } });
        expectReport(SYNC_DELETED, ['src-1']).flush(mismatch);
        expectReport(SYNC_RESTORED, ['tgt-1']).flush(mismatch);

        service.retryRemovalReport();
        service.retryRestoreReport();

        httpMock.expectNone(SYNC_DELETED);
        httpMock.expectNone(SYNC_RESTORED);
      },
    );

    it('refuses a retry before the run has settled and while the report is out', () => {
      start([fullRow('1')]);
      service.retryRemovalReport();
      runFull('1');
      const deleted = expectReport(SYNC_DELETED, ['src-1']);
      service.retryRemovalReport();
      httpMock.expectNone(SYNC_DELETED);
      deleted.flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
    });

    it('does nothing for either retry during the settling window — the run already has a result there, only not a settled one', () => {
      // The engine's snapshot lands on the run record as soon as it completes (`onRunComplete`),
      // before the settle re-read resolves — so `current.result === null` alone would not refuse a
      // retry here; only the explicit `settlement !== 'settled'` check does. The REMOVE is already
      // confirmed here (`completedSteps: 1` in the raw, unsettled snapshot), so a retry that skipped
      // that check would find a non-empty id list and actually send the report early — unlike a row
      // whose REMOVE itself is still unknown, where the raw snapshot's `completedSteps: 0` alone
      // would keep the id list empty and hide the missing guard.
      // A full row with two ADDs whose first ADD goes unanswered (mirrors `firstAddUnanswered()`,
      // local to the settling describe block above): the REMOVE is confirmed before the ADD stalls.
      start([fullRow('1', { entries: ['A1', 'B1'] })]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      next();
      expectAdd('tgt-1', 'A1').flush('boom', { status: 502, statusText: 'Bad Gateway' });
      next();
      expect(service.run()?.result).not.toBeNull();
      expect(service.run()?.result?.items[0].completedSteps).toBe(1);
      expect(service.settlement()).toBe('pending');

      service.retryRemovalReport();
      service.retryRestoreReport();
      httpMock.expectNone(SYNC_DELETED);
      httpMock.expectNone(SYNC_RESTORED);

      expectRead().flush(readPage([]));
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
    });

    it('triggers no resync on success and shows backendTriggered from the first answer', () => {
      start([fullRow('1')]);
      runFull('1');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer({ resyncTriggered: ['KANAL_T'] }));
      expect(service.resyncTrigger()).toBe('backendTriggered');
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());

      httpMock.expectNone(RESYNC);
      expect(service.resyncTrigger()).toBe('backendTriggered');
    });

    it('shows backendTriggered from the second answer alone', () => {
      start([fullRow('1')]);
      runFull('1');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      expect(service.resyncTrigger()).toBe('idle');
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer({ resyncTriggered: ['kanal_t'] }));

      httpMock.expectNone(RESYNC);
      expect(service.resyncTrigger()).toBe('backendTriggered');
    });

    it('resyncs the expected channel exactly once when both reports failed for good (N1)', () => {
      start([fullRow('1')]);
      runFull('1');
      failForGood(SYNC_DELETED);
      httpMock.expectNone(RESYNC);
      failForGood(SYNC_RESTORED);

      const resync = httpMock.expectOne(RESYNC);
      expect(service.resyncTrigger()).toBe('pending');
      resync.flush(null, { status: 202, statusText: 'Accepted' });
      expect(service.resyncTrigger()).toBe('succeeded');
      httpMock.expectNone(RESYNC);
    });

    it('does not resync when only one of the two reports failed', () => {
      start([fullRow('1')]);
      runFull('1');
      expectReport(SYNC_DELETED, ['src-1']).flush(answer());
      failForGood(SYNC_RESTORED);

      httpMock.expectNone(RESYNC);
      expect(service.resyncTrigger()).toBe('idle');
    });

    it('never resyncs a non-active target, not even when both reports failed', () => {
      start([fullRow('1')], { target: TARGET_NON_ACTIVE });
      runFull('1');
      failForGood(SYNC_DELETED);
      failForGood(SYNC_RESTORED);

      httpMock.expectNone(isChannelRoute);
      expect(service.resyncTrigger()).toBe('idle');
    });

    it('resyncs when the only report a run sent failed for good — no report reached the backend', () => {
      start([addOnlyRow('2')]);
      runAddOnly('2');
      failForGood(SYNC_RESTORED);

      httpMock.expectOne(RESYNC).flush(null, { status: 429, statusText: 'Too Many Requests' });
      expect(service.resyncTrigger()).toBe('cooldown');
    });

    it('does not resync again after a manual retry that fails once more', () => {
      start([addOnlyRow('2')]);
      runAddOnly('2');
      failForGood(SYNC_RESTORED);
      httpMock.expectOne(RESYNC).flush(null, { status: 202, statusText: 'Accepted' });

      service.retryRestoreReport();
      failForGood(SYNC_RESTORED);
      httpMock.expectNone(RESYNC);
    });

    it('reports an untracked target with no expected channel and never resyncs (AK 22)', () => {
      start([fullRow('1')], { target: TARGET_UNTRACKED });
      runFull('1');
      expectReport(SYNC_DELETED, ['src-1'], null).flush(answer());
      expectReport(SYNC_RESTORED, ['tgt-1'], null).flush(answer());

      httpMock.expectNone(isChannelRoute);
      expect(service.resyncTrigger()).toBe('idle');
    });

    it('ends a report that never answers as failed/unavailable, attempt by attempt, and closes the run', () => {
      start([fullRow('1')]);
      runFull('1');
      for (const retryDelay of [2000, 4000, 0]) {
        const attempt = httpMock.expectOne(SYNC_DELETED);
        vi.advanceTimersByTime(REPORT_TIMEOUT_MS);
        expect(attempt.cancelled).toBe(true);
        vi.advanceTimersByTime(retryDelay);
      }
      expect([service.removalReport(), service.removalReportReason()]).toEqual([
        'failed',
        'unavailable',
      ]);
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
      expect(service.run()?.phase).toBe('closed');
    });

    it('ends a report whose answer cannot be read instead of leaving the run reporting', () => {
      start([fullRow('1')]);
      runFull('1');
      httpMock.expectOne(SYNC_DELETED).flush({});
      vi.advanceTimersByTime(2000);
      httpMock.expectOne(SYNC_DELETED).flush({});
      vi.advanceTimersByTime(4000);
      httpMock.expectOne(SYNC_DELETED).flush({});

      expect(service.removalReport()).toBe('failed');
      expectReport(SYNC_RESTORED, ['tgt-1']).flush(answer());
      expect(service.isSettling()).toBe(false);
    });
  });

  describe('run-bound lifecycle (#256; AK 15, 32, 39)', () => {
    it('keeps destructiveOpen from the start until both reports answered — also after reset()', () => {
      start([fullRow('1')]);
      expect(service.destructiveOpen()).toBe(true);
      runFull('1');
      service.reset();

      const deleted = httpMock.expectOne(SYNC_DELETED);
      expect(service.isSettling()).toBe(true);
      deleted.flush(answer());
      expect(service.destructiveOpen()).toBe(true);
      httpMock.expectOne(SYNC_RESTORED).flush(answer());

      expect(service.destructiveOpen()).toBe(false);
      expect(service.isSettling()).toBe(false);
    });

    it('never arms destructiveOpen for a pure addOnly run', () => {
      start([addOnlyRow('2')]);
      expect(service.destructiveOpen()).toBe(false);
      runAddOnly('2');
      const restored = httpMock.expectOne(SYNC_RESTORED);
      expect(service.isSettling()).toBe(true);
      expect(service.destructiveOpen()).toBe(false);
      restored.flush(answer());
    });

    it('lets a run reset() while running finish on its own record and send both reports once', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush({});
      next();
      service.reset();

      expect(service.run()).toBeNull();
      expect(service.items()).toEqual([]);
      expectAdd('tgt-1', 'A1').flush({});
      next();

      expect(service.queue()).toEqual([]);
      expect(service.isSettling()).toBe(true);
      httpMock.expectOne(SYNC_DELETED).flush(answer());
      httpMock.expectOne(SYNC_RESTORED).flush(answer());
      expect(service.isSettling()).toBe(false);
      expect(service.destructiveOpen()).toBe(false);
    });

    it('lets a run reset() during its re-read settle and report', () => {
      start([fullRow('1')]);
      answerRead([{ id: 'src-1', alias: 'A1' }]);
      expectRemove('src-1').flush('boom', { status: 502, statusText: 'Bad Gateway' });
      next();
      service.reset();

      expectRead().flush(readPage([]));
      httpMock.expectOne(SYNC_DELETED).flush(answer());
      expect(service.isSettling()).toBe(false);
      expect(service.destructiveOpen()).toBe(false);
    });

    it('lets a report reset() mid-flight land on its own record, and isSettling fall only afterwards', () => {
      start([fullRow('1')]);
      runFull('1');
      const deleted = httpMock.expectOne(SYNC_DELETED);
      service.reset();

      expect(service.isSettling()).toBe(true);
      deleted.flush(answer());
      expect(service.isSettling()).toBe(true);
      httpMock.expectOne(SYNC_RESTORED).flush(answer());
      expect(service.isSettling()).toBe(false);
      expect(service.run()).toBeNull();
    });

    it('shows a detached run again when one of its reports did not succeed', () => {
      start([fullRow('1')]);
      runFull('1');
      service.reset();
      httpMock.expectOne(SYNC_DELETED).flush(answer());
      failForGood(SYNC_RESTORED);

      httpMock.expectNone(RESYNC);
      expect(service.run()).not.toBeNull();
      expect([service.restoreReport(), service.restoreReportReason()]).toEqual([
        'failed',
        'unavailable',
      ]);
      expect(service.items().map((item) => item.key)).toEqual(['src-1']);
    });

    it('does not reopen a closed run for a manual retry', () => {
      start([fullRow('1')]);
      runFull('1');
      failForGood(SYNC_DELETED);
      httpMock.expectOne(SYNC_RESTORED).flush(answer());
      expect(service.run()?.phase).toBe('closed');

      service.retryRemovalReport();
      expect(service.isSettling()).toBe(false);
      expect(service.destructiveOpen()).toBe(false);
      httpMock.expectOne(SYNC_DELETED).flush(answer());
      expect(service.run()?.phase).toBe('closed');
    });

    it('drops a closed run when the page moves to another channel, and keeps it on the same one', () => {
      start([addOnlyRow('2')]);
      runAddOnly('2');
      httpMock.expectOne(SYNC_RESTORED).flush(answer());

      service.resetIfChannelChanged('kanal_t');
      expect(service.run()).not.toBeNull();
      service.resetIfChannelChanged('elsewhere');
      expect(service.run()).toBeNull();
    });

    it('keeps a running and a reporting run across a channel change — their reports still go out', () => {
      start([fullRow('1')]);
      service.resetIfChannelChanged('elsewhere');
      expect(service.run()).not.toBeNull();
      runFull('1');
      const deleted = httpMock.expectOne(SYNC_DELETED);
      service.resetIfChannelChanged('elsewhere');
      expect(service.run()).not.toBeNull();

      deleted.flush(answer());
      httpMock.expectOne(SYNC_RESTORED).flush(answer());
      expect(service.run()?.phase).toBe('closed');
    });

    it('takes a start the engine refuses back and shows the previous run again', () => {
      start([addOnlyRow('2')]);
      runAddOnly('2');
      httpMock.expectOne(SYNC_RESTORED).flush(answer());
      const previous = service.run();
      tokenService.clearToken();

      start([fullRow('1')]);

      expect(service.isRunning()).toBe(false);
      expect(service.run()).toBe(previous);
      expect(service.destructiveOpen()).toBe(false);
      httpMock.expectNone(isRead);
    });
  });

  // The `protocol (spec 6.4, 17 K4)` describe block that lived here moved to
  // `buildUndoRunProtocol` in `transfer-undo-export.spec.ts` (#254 layering fix): both its cases
  // tested the mapping from a settled run to the transfer-undo protocol, which now lives there —
  // as hand-built `UndoRunInfo`/`UndoRunItem` fixtures rather than a full HTTP-driven run, since
  // the mapping itself needs neither. The individual item states that scenario combined (a `full`
  // row that completes, one the recheck cancels as `skippedDrift`, an `addOnly` row left `partial`
  // by an omission, and the service's own two lock reasons) are each still covered on their own by
  // the other describe blocks in this file.

  it('shows the skipped candidates of a started run as a transient notice that reset() clears', () => {
    const skipped: UndoSkippedRow = {
      candidate: cand('9'),
      reason: 'sourceUnderOtherName',
      live: { sourceEntries: ['Other'], targetEntries: [] },
      omittedEntries: [],
    };
    start([addOnlyRow('2')], { skipped: [skipped] });

    expect(service.noticePending()).toBe(true);
    expect(service.noticeSkipped()).toEqual([skipped]);
    expect(service.summary().skippedByReason.sourceUnderOtherName).toBe(1);
    service.reset();
    expect(service.noticePending()).toBe(false);
    expect(service.noticeSkipped()).toEqual([]);

    runAddOnly('2');
    httpMock.expectOne(SYNC_RESTORED).flush(answer());
  });
});
