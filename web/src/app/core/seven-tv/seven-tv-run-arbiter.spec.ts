import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ImportOrigin, ImportRow } from './import-source';
import { SyncDeletedInSetResponse } from './seven-tv-emote-set.model';
import { DELETE_DELAY_MS, DeleteQueueEmote, SevenTvDeleteService } from './seven-tv-delete.service';
import { SevenTvImportService } from './seven-tv-import.service';
import { RestoreStartTarget, SevenTvRestoreService } from './seven-tv-restore.service';
import {
  REFUSED_START_FEEDBACK_MS,
  refusedStartMessage,
  SEVEN_TV_RUN_KIND_LABEL_KEY,
  SevenTvRunArbiter,
  SevenTvRunKind,
  SevenTvRunParticipant,
} from './seven-tv-run-arbiter';
import { RUN_DELAY_MS } from './seven-tv-run-engine';
import { SevenTvTokenService } from './seven-tv-token.service';
import { SevenTvUndoService, UndoRunTarget } from './seven-tv-undo.service';
import { TransferPlan } from './transfer-plan';
import { UndoPlanRow, classifyUndoRows } from './undo-plan';

/** A registered participant whose three signals a case sets by hand (contract P5). */
interface StubParticipant extends SevenTvRunParticipant {
  isRunning: WritableSignal<boolean>;
  isSettling: WritableSignal<boolean>;
  destructiveOpen: WritableSignal<boolean>;
}

function stubParticipant(
  kind: SevenTvRunKind,
  state: { isRunning?: boolean; isSettling?: boolean; destructiveOpen?: boolean } = {},
): StubParticipant {
  return {
    kind,
    isRunning: signal(state.isRunning ?? false),
    isSettling: signal(state.isSettling ?? false),
    destructiveOpen: signal(state.destructiveOpen ?? false),
  };
}

/** The `beforeunload` calls a spy saw — other listeners on `window` are none of this suite's business. */
function unloadCalls(spy: { mock: { calls: unknown[][] } }): unknown[][] {
  return spy.mock.calls.filter(([type]) => type === 'beforeunload');
}

// #256, contract P5 of the #254 spec (11.1): the arbiter is testable against stubs alone. This
// TestBed provides nothing but the arbiter — no HttpClient, no run service — so it also proves the
// arbiter injects no run service any more (P4): one that did would fail to construct here.
describe('SevenTvRunArbiter with stub participants', () => {
  let arbiter: SevenTvRunArbiter;
  let addSpy: ReturnType<typeof vi.spyOn>;
  let removeSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    addSpy = vi.spyOn(window, 'addEventListener');
    removeSpy = vi.spyOn(window, 'removeEventListener');
    TestBed.configureTestingModule({});
    arbiter = TestBed.inject(SevenTvRunArbiter);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('is free and adds no unload listener while nothing is registered', () => {
    TestBed.tick();

    expect(arbiter.activeRun()).toBeNull();
    expect(arbiter.activeClaim()).toBeNull();
    expect(arbiter.destructiveOpen()).toBe(false);
    expect(unloadCalls(addSpy)).toEqual([]);
  });

  it('counts a settling participant as busy and names the phase as the reason', () => {
    const participant = stubParticipant('restore', { isSettling: true });
    arbiter.register(participant);

    expect(arbiter.activeRun()).toBe('restore');
    expect(arbiter.activeClaim()).toEqual({ kind: 'restore', phase: 'settling' });

    participant.isSettling.set(false);

    expect(arbiter.activeRun()).toBeNull();
    expect(arbiter.activeClaim()).toBeNull();
  });

  it('names a running participant with the phase running, and settling once only that is left', () => {
    const participant = stubParticipant('delete', { isRunning: true });
    arbiter.register(participant);

    expect(arbiter.activeClaim()).toEqual({ kind: 'delete', phase: 'running' });

    participant.isRunning.set(false);
    participant.isSettling.set(true);

    expect(arbiter.activeClaim()).toEqual({ kind: 'delete', phase: 'settling' });
  });

  it.each([
    ['registered first', true],
    ['registered second', false],
  ])('prefers a running participant over a settling one, the running one %s', (_, runningFirst) => {
    const running = stubParticipant('import', { isRunning: true });
    const settling = stubParticipant('delete', { isSettling: true });
    for (const participant of runningFirst ? [running, settling] : [settling, running]) {
      arbiter.register(participant);
    }

    expect(arbiter.activeRun()).toBe('import');
    expect(arbiter.activeClaim()).toEqual({ kind: 'import', phase: 'running' });
  });

  it('prefers the participant registered first when two claim the same phase', () => {
    arbiter.register(stubParticipant('restore', { isSettling: true }));
    arbiter.register(stubParticipant('delete', { isSettling: true }));

    expect(arbiter.activeClaim()).toEqual({ kind: 'restore', phase: 'settling' });
  });

  it('sees a participant that registers after its answer was first read', () => {
    expect(arbiter.activeRun()).toBeNull();
    expect(arbiter.destructiveOpen()).toBe(false);

    arbiter.register(stubParticipant('delete', { isRunning: true, destructiveOpen: true }));

    expect(arbiter.activeRun()).toBe('delete');
    expect(arbiter.destructiveOpen()).toBe(true);
  });

  it('accepts the same kind twice and counts both', () => {
    const first = stubParticipant('import', { destructiveOpen: true });
    const second = stubParticipant('import', { isRunning: true });
    arbiter.register(first);
    arbiter.register(second);

    expect(arbiter.activeClaim()).toEqual({ kind: 'import', phase: 'running' });
    expect(arbiter.destructiveOpen()).toBe(true);
  });

  it('holds the unload guard for a destructive participant whose run is neither running nor settling', () => {
    // The case "the service's run() is null" (P5): a run detached from the display, still open.
    const participant = stubParticipant('delete', { destructiveOpen: true });
    arbiter.register(participant);
    TestBed.tick();

    expect(arbiter.activeRun()).toBeNull();
    expect(unloadCalls(addSpy)).toHaveLength(1);
    expect(unloadCalls(removeSpy)).toEqual([]);
    const handler = unloadCalls(addSpy)[0][1] as (event: BeforeUnloadEvent) => void;
    const event = { preventDefault: vi.fn(), returnValue: undefined as unknown };
    handler(event as unknown as BeforeUnloadEvent);
    expect(event.preventDefault).toHaveBeenCalled();

    participant.destructiveOpen.set(false);
    TestBed.tick();

    // The exact reference that was added is the one removed.
    expect(unloadCalls(removeSpy)).toEqual([['beforeunload', handler]]);
  });

  it('arms the unload guard as a union: one destructive participant is enough, both closed turn it off', () => {
    const first = stubParticipant('import', { destructiveOpen: true });
    const second = stubParticipant('delete');
    arbiter.register(first);
    arbiter.register(second);
    TestBed.tick();

    expect(arbiter.destructiveOpen()).toBe(true);
    expect(unloadCalls(addSpy)).toHaveLength(1);

    second.destructiveOpen.set(true);
    first.destructiveOpen.set(false);
    TestBed.tick();

    // Still one open — no churn on the window in between.
    expect(unloadCalls(addSpy)).toHaveLength(1);
    expect(unloadCalls(removeSpy)).toEqual([]);

    second.destructiveOpen.set(false);
    TestBed.tick();

    expect(arbiter.destructiveOpen()).toBe(false);
    expect(unloadCalls(removeSpy)).toHaveLength(1);
  });

  it('removes the unload listener when the arbiter is destroyed with it still armed', () => {
    arbiter.register(stubParticipant('delete', { destructiveOpen: true }));
    TestBed.tick();
    const handler = unloadCalls(addSpy)[0][1];

    TestBed.resetTestingModule();

    expect(unloadCalls(removeSpy)).toEqual([['beforeunload', handler]]);
  });

  it('notes a refused start with what was attempted and what blocked it, for REFUSED_START_FEEDBACK_MS', () => {
    arbiter.register(stubParticipant('delete', { isRunning: true }));

    arbiter.noteRefusedStart('import');

    expect(arbiter.refusedStart()).toEqual({
      attempted: 'import',
      blockedBy: { kind: 'delete', phase: 'running' },
    });
    vi.advanceTimersByTime(REFUSED_START_FEEDBACK_MS - 1);
    expect(arbiter.refusedStart()).not.toBeNull();
    vi.advanceTimersByTime(1);
    expect(arbiter.refusedStart()).toBeNull();
  });

  it('restarts the window on a second refusal instead of letting the first timer cut it short', () => {
    const participant = stubParticipant('import', { isRunning: true });
    arbiter.register(participant);

    arbiter.noteRefusedStart('delete');
    vi.advanceTimersByTime(3000);
    participant.isRunning.set(false);
    participant.isSettling.set(true);
    arbiter.noteRefusedStart('restore');

    // The first timer would have fired here.
    vi.advanceTimersByTime(REFUSED_START_FEEDBACK_MS - 1000);
    expect(arbiter.refusedStart()).toEqual({
      attempted: 'restore',
      blockedBy: { kind: 'import', phase: 'settling' },
    });
    vi.advanceTimersByTime(1000);
    expect(arbiter.refusedStart()).toBeNull();
  });

  it('notes nothing for a refusal while the arbiter is free — nothing blocked it', () => {
    arbiter.register(stubParticipant('delete'));

    arbiter.noteRefusedStart('import');

    expect(arbiter.refusedStart()).toBeNull();
  });
});

// Only the keys the three services actually translate.
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
const SYNC_DELETED_SET_1 = '/api/seventv/emote-sets/set-1/sync-deleted';

const RESTORE_TARGET: RestoreStartTarget = {
  setId: 'set-1',
  expectedChannelName: 'sensitron',
  resyncChannelName: null,
  hostChannelName: 'sensitron',
  setName: 'Set 1',
  ownerOrChannelLabel: 'sensitron',
};

const EMOTES: DeleteQueueEmote[] = [
  { emoteId: 'internal-1', sevenTvEmoteId: '7tv-1', name: 'PogU' },
  { emoteId: 'internal-2', sevenTvEmoteId: '7tv-2', name: 'KEKW' },
];

// An import carries no internal id at all — it writes into another channel's set (#72).
const IMPORT_ROW: ImportRow = { sevenTvEmoteId: '7tv-3', name: 'Sadge', imageUrl: null };
const IMPORT_PLAN: TransferPlan = {
  rows: [{ action: 'add', source: IMPORT_ROW, alias: IMPORT_ROW.name }],
};
const IMPORT_ORIGIN: ImportOrigin = { kind: 'channel', channelName: 'sensitron' };

// A replace against an untracked target: its reports go set-centric, and no resync follows.
const TARGET_UNTRACKED = { setId: 'set-u', channelName: null, ownerDisplayName: 'Stranger' };
const SYNC_IMPORTED_SET_U = '/api/seventv/emote-sets/set-u/sync-imported';
const SYNC_DELETED_SET_U = '/api/seventv/emote-sets/set-u/sync-deleted';
const SOURCE_X: ImportRow = { sevenTvEmoteId: 'src-x', name: 'Kappa', imageUrl: null };
const REPLACE_PLAN: TransferPlan = {
  rows: [
    {
      action: 'replace',
      source: SOURCE_X,
      alias: SOURCE_X.name,
      target: {
        sevenTvEmoteId: 'tgt-x',
        aliases: [SOURCE_X.name],
        hasAliaslessEntry: false,
        defaultName: null,
      },
    },
  ],
};

// An undo of one replace (#254): the source `undo-src` stands exactly under `Kappa`, the target
// `undo-tgt` it replaced is gone — one `full` row, REMOVE then ADD.
const UNDO_TARGET: UndoRunTarget = {
  setId: 'set-u',
  expectedChannelName: null,
  hostChannelName: 'sensitron',
  setName: 'Set U',
  ownerOrChannelLabel: 'Stranger',
  trackedChannelName: null,
  ownerDisplayName: 'Stranger',
  sourceFile: {
    stage: 'finished',
    exportedAt: '2026-09-25T10:00:00.000Z',
    verifiedAt: null,
    finishedAt: '2026-09-25T10:00:00.000Z',
    origin: null,
  },
};
const SYNC_RESTORED_SET_U = '/api/seventv/emote-sets/set-u/sync-restored';

function undoFullRow(): UndoPlanRow {
  const plan = classifyUndoRows(
    [
      {
        sourceSevenTvEmoteId: 'undo-src',
        sourceName: 'Kappa',
        alias: 'Kappa',
        fileStatus: 'done',
        target: { sevenTvEmoteId: 'undo-tgt', entries: [{ alias: 'Kappa' }], defaultName: null },
        provenance: 'confirmed',
      },
    ],
    {
      aliasesById: new Map([['undo-src', ['Kappa']]]),
      aliaslessIds: new Set(),
      defaultNameById: new Map([['undo-src', 'Kappa']]),
      animatedById: new Map([['undo-src', false]]),
      occupiedSlots: 1,
      complete: true,
    },
  );
  return plan.rows[0];
}

function deletedAnswer(): SyncDeletedInSetResponse {
  return { reportedCount: 1, channels: [], unresolvedChannel: null, resyncTriggered: [] };
}

/** One page of the tokenless set read holding exactly `entries`. */
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

describe('SevenTvRunArbiter with the real run services', () => {
  let arbiter: SevenTvRunArbiter;
  let deleteService: SevenTvDeleteService;
  let restoreService: SevenTvRestoreService;
  let importService: SevenTvImportService;
  let undoService: SevenTvUndoService;
  let tokenService: SevenTvTokenService;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    vi.useFakeTimers();
    // The services log a closing measurement on finish() — silenced, it is not what this suite is about.
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
    arbiter = TestBed.inject(SevenTvRunArbiter);
    // Injected in this order, so the services register delete → restore → import → undo.
    deleteService = TestBed.inject(SevenTvDeleteService);
    restoreService = TestBed.inject(SevenTvRestoreService);
    importService = TestBed.inject(SevenTvImportService);
    undoService = TestBed.inject(SevenTvUndoService);
    tokenService = TestBed.inject(SevenTvTokenService);
    httpMock = TestBed.inject(HttpTestingController);
    tokenService.setToken('write-token');
  });

  afterEach(() => {
    // Most cases below end a run via cancel() before any row is confirmed, so no report follows and
    // the arbiter's own behaviour stays isolated from the services' closing calls. cancel() leaves
    // its in-flight GQL request marked cancelled, not gone — ignoreCancelled is this suite's
    // deliberate choice, not a fallback for an unrelated leak.
    httpMock.verify({ ignoreCancelled: true });
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('reports no active run when no service is running', () => {
    expect(arbiter.activeRun()).toBeNull();
  });

  it('reports "delete" while a delete run is active, then null again after it ends', () => {
    deleteService.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');

    expect(arbiter.activeRun()).toBe('delete');

    deleteService.cancel();

    expect(arbiter.activeRun()).toBeNull();
  });

  it('reports "restore" while a restore run is active, then null again after it ends', () => {
    restoreService.startRestore(RESTORE_TARGET, [EMOTES[0]]);

    expect(arbiter.activeRun()).toBe('restore');

    restoreService.cancel();

    expect(arbiter.activeRun()).toBeNull();
  });

  it('reports "import" while an import run is active, then null again after it ends', () => {
    importService.startImport(
      { setId: 'set-2', channelName: 'kanal_b' },
      IMPORT_ORIGIN,
      IMPORT_PLAN,
    );

    expect(arbiter.activeRun()).toBe('import');

    importService.cancel();

    expect(arbiter.activeRun()).toBeNull();
  });

  // Constructed: the start points prevent two runs at once (they check activeRun() first), so this
  // pins the display rule — running before settling (Plan-256 Festlegung 7) — against the real
  // services: the import registered last still wins over the delete registered first.
  it('prefers a running import over a delete that is still reporting', () => {
    deleteService.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);
    const deleteReport = httpMock.expectOne(SYNC_DELETED_SET_1);
    expect(arbiter.activeClaim()).toEqual({ kind: 'delete', phase: 'settling' });

    importService.startImport(
      { setId: 'set-2', channelName: 'kanal_b' },
      IMPORT_ORIGIN,
      IMPORT_PLAN,
    );

    expect(arbiter.activeClaim()).toEqual({ kind: 'import', phase: 'running' });

    importService.cancel();

    expect(arbiter.activeClaim()).toEqual({ kind: 'delete', phase: 'settling' });
    deleteReport.flush(deletedAnswer());
    expect(arbiter.activeRun()).toBeNull();
  });

  // Same construction with two running at once: among equals the first registered wins.
  it('prefers the service registered first when a delete and a restore run at once', () => {
    deleteService.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');
    restoreService.startRestore(RESTORE_TARGET, [EMOTES[1]]);

    expect(arbiter.activeRun()).toBe('delete');

    deleteService.cancel();

    expect(arbiter.activeRun()).toBe('restore');

    restoreService.cancel();
  });

  it('holds "delete" after the engine is done, until the report answers', () => {
    deleteService.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(DELETE_DELAY_MS);

    expect(deleteService.isRunning()).toBe(false);
    expect(arbiter.activeClaim()).toEqual({ kind: 'delete', phase: 'settling' });

    httpMock.expectOne(SYNC_DELETED_SET_1).flush(deletedAnswer());

    expect(arbiter.activeRun()).toBeNull();
  });

  it('holds "import" across the re-read of a lost replace answer and both reports, free only once both ended', () => {
    importService.startImport(TARGET_UNTRACKED, IMPORT_ORIGIN, REPLACE_PLAN);
    // REMOVE answered, ADD lost: the row ends unknown, and the run re-reads before it reports.
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 502, statusText: 'Bad Gateway' });
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(importService.isRunning()).toBe(false);
    expect(importService.run()?.phase).toBe('settling');
    expect(arbiter.activeClaim()).toEqual({ kind: 'import', phase: 'settling' });

    httpMock.expectOne(GQL_ENDPOINT).flush(setEntriesPage([{ id: 'src-x', alias: 'Kappa' }]));

    expect(importService.run()?.phase).toBe('reporting');
    expect(arbiter.activeClaim()).toEqual({ kind: 'import', phase: 'settling' });
    httpMock.expectOne(SYNC_IMPORTED_SET_U).flush(null, { status: 204, statusText: 'OK' });
    expect(arbiter.activeClaim()).toEqual({ kind: 'import', phase: 'settling' });
    httpMock.expectOne(SYNC_DELETED_SET_U).flush(deletedAnswer());

    expect(arbiter.activeRun()).toBeNull();
    expect(arbiter.destructiveOpen()).toBe(false);
  });

  // #254 (spec 9.3, AK 16, 32): the undo is the fourth kind — registered by its own constructor.
  it('reports "undo" while an undo runs and arms the unload guard for its full row', () => {
    undoService.startUndo(UNDO_TARGET, [undoFullRow()], [], false);

    expect(arbiter.activeClaim()).toEqual({ kind: 'undo', phase: 'running' });
    expect(arbiter.destructiveOpen()).toBe(true);

    // Cancelled while its recheck read is out: nothing was sent, nothing to report.
    undoService.cancel();

    expect(arbiter.activeRun()).toBeNull();
    expect(arbiter.destructiveOpen()).toBe(false);
  });

  it('holds "undo" while it settles — through both reports — and frees only after the last answer', () => {
    undoService.startUndo(UNDO_TARGET, [undoFullRow()], [], false);
    httpMock.expectOne(GQL_ENDPOINT).flush(setEntriesPage([{ id: 'undo-src', alias: 'Kappa' }]));
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);

    expect(undoService.isRunning()).toBe(false);
    expect(arbiter.activeClaim()).toEqual({ kind: 'undo', phase: 'settling' });
    httpMock.expectOne(SYNC_DELETED_SET_U).flush(deletedAnswer());
    expect(arbiter.activeClaim()).toEqual({ kind: 'undo', phase: 'settling' });
    expect(arbiter.destructiveOpen()).toBe(true);
    httpMock.expectOne(SYNC_RESTORED_SET_U).flush(deletedAnswer());

    expect(arbiter.activeRun()).toBeNull();
    expect(arbiter.destructiveOpen()).toBe(false);
  });

  it('refuses an undo start while an import settles and names the import as the reason', () => {
    importService.startImport(TARGET_UNTRACKED, IMPORT_ORIGIN, REPLACE_PLAN);
    httpMock.expectOne(GQL_ENDPOINT).flush({});
    vi.advanceTimersByTime(RUN_DELAY_MS);
    httpMock.expectOne(GQL_ENDPOINT).flush('boom', { status: 502, statusText: 'Bad Gateway' });
    vi.advanceTimersByTime(RUN_DELAY_MS);
    expect(arbiter.activeClaim()).toEqual({ kind: 'import', phase: 'settling' });

    // What the undo flow's start point does when the arbiter is busy (spec 6.3): note, no start.
    arbiter.noteRefusedStart('undo');

    expect(arbiter.refusedStart()).toEqual({
      attempted: 'undo',
      blockedBy: { kind: 'import', phase: 'settling' },
    });
    expect(undoService.isRunning()).toBe(false);
    httpMock.expectOne(GQL_ENDPOINT).flush(setEntriesPage([{ id: 'src-x', alias: 'Kappa' }]));
    httpMock.expectOne(SYNC_IMPORTED_SET_U).flush(null, { status: 204, statusText: 'OK' });
    httpMock.expectOne(SYNC_DELETED_SET_U).flush(deletedAnswer());
    expect(arbiter.activeRun()).toBeNull();
  });

  // The unload guard moved here from the import service (#256, contract P3). A tab that dies
  // between REMOVE and ADD, or before a removal is reported, leaves a gap no protocol was written
  // for yet. `TestBed.tick()` flushes the arbiter's effect.
  describe('beforeunload guard', () => {
    it('is armed by a replace import from its start until its reports have answered, shown or not', () => {
      const addSpy = vi.spyOn(window, 'addEventListener');
      const removeSpy = vi.spyOn(window, 'removeEventListener');

      importService.startImport(TARGET_UNTRACKED, IMPORT_ORIGIN, REPLACE_PLAN);
      TestBed.tick();
      expect(arbiter.destructiveOpen()).toBe(true);
      expect(unloadCalls(addSpy)).toHaveLength(1);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);
      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(RUN_DELAY_MS);

      // Its reports are still out, and nothing shows it any more: the guard holds.
      importService.reset();
      TestBed.tick();
      expect(importService.run()).toBeNull();
      expect(unloadCalls(removeSpy)).toEqual([]);

      httpMock.expectOne(SYNC_IMPORTED_SET_U).flush(null, { status: 204, statusText: 'OK' });
      TestBed.tick();
      expect(unloadCalls(removeSpy)).toEqual([]);
      httpMock.expectOne(SYNC_DELETED_SET_U).flush(deletedAnswer());
      TestBed.tick();

      expect(arbiter.destructiveOpen()).toBe(false);
      expect(unloadCalls(removeSpy)).toEqual([['beforeunload', unloadCalls(addSpy)[0][1]]]);
    });

    it('is never armed by an add-only import', () => {
      const addSpy = vi.spyOn(window, 'addEventListener');

      importService.startImport(
        { setId: 'set-2', channelName: 'kanal_b' },
        IMPORT_ORIGIN,
        IMPORT_PLAN,
      );
      TestBed.tick();
      importService.cancel();
      TestBed.tick();

      expect(arbiter.destructiveOpen()).toBe(false);
      expect(unloadCalls(addSpy)).toEqual([]);
    });

    it("is armed by a delete's destructiveOpen from its start until its report has answered", () => {
      const addSpy = vi.spyOn(window, 'addEventListener');
      const removeSpy = vi.spyOn(window, 'removeEventListener');

      deleteService.startDelete('set-1', 'sensitron', [EMOTES[0]], 'sensitron');
      TestBed.tick();
      expect(unloadCalls(addSpy)).toHaveLength(1);

      httpMock.expectOne(GQL_ENDPOINT).flush({});
      vi.advanceTimersByTime(DELETE_DELAY_MS);
      TestBed.tick();
      expect(deleteService.isRunning()).toBe(false);
      expect(unloadCalls(removeSpy)).toEqual([]);

      httpMock.expectOne(SYNC_DELETED_SET_1).flush(deletedAnswer());
      TestBed.tick();

      expect(unloadCalls(removeSpy)).toHaveLength(1);
    });

    it('is never armed by a restore', () => {
      const addSpy = vi.spyOn(window, 'addEventListener');

      restoreService.startRestore(RESTORE_TARGET, [EMOTES[0]]);
      TestBed.tick();
      restoreService.cancel();
      TestBed.tick();

      expect(unloadCalls(addSpy)).toEqual([]);
    });
  });
});

// #256 T4: the pure helper both renderers of the refused-start notice share (`usage-stats-page.ts`,
// `mass-delete-panel.ts`) — no TestBed needed, `translate` is a plain function.
describe('refusedStartMessage', () => {
  const translate = (key: string): string => `t(${key})`;

  it('builds the running-phase message key and translates the blocking kind through the Record', () => {
    expect(refusedStartMessage({ kind: 'delete', phase: 'running' }, translate)).toEqual({
      messageKey: 'sevenTvRun.notStarted.running',
      kind: 't(sevenTvRun.kind.delete)',
    });
  });

  it('builds the settling-phase message key for a different kind', () => {
    expect(refusedStartMessage({ kind: 'import', phase: 'settling' }, translate)).toEqual({
      messageKey: 'sevenTvRun.notStarted.settling',
      kind: 't(sevenTvRun.kind.import)',
    });
  });

  it('has an entry for every SevenTvRunKind, so the compiler catches a future one going missing', () => {
    const kinds: SevenTvRunKind[] = ['delete', 'restore', 'import', 'undo'];
    for (const kind of kinds) {
      expect(SEVEN_TV_RUN_KIND_LABEL_KEY[kind]).toBe(`sevenTvRun.kind.${kind}`);
    }
  });
});
