import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { signal, WritableSignal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { ForeignEmoteSetResponse } from '../../core/seven-tv/foreign-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { PurgeRunRow, RestoreRow } from '../export/purge-run-export';
import { RestoreConfirmDialogData } from './restore-confirm-dialog';
import { RestoreFlowDeps, startRestoreFlow } from './restore-flow';

/**
 * `startRestoreFlow` opens dialogs through the plain `Dialog` object it is handed and never
 * injects anything itself — see the class doc on `RestoreFlowDeps`. That is what lets every test
 * here run without a `TestBed`, same as `import-flow.spec.ts`: `dialog.open` is a bare `vi.fn()`
 * standing in for both the token prompt and the restore confirmation, distinguished by call order
 * — which one is call #0 depends on whether a token is already stored, so each test sets that up
 * explicitly rather than assuming a fixed position.
 */

const CHANNEL = 'frozen-channel';
const SET_ID = 'frozen-set';
const SET_NAME = 'Frozen Set';

function rows(): PurgeRunRow[] {
  return [
    {
      emoteId: 'e1',
      sevenTvEmoteId: '7tv-1',
      name: 'PogU',
      aliases: ['PogU'],
      status: 'done',
      errorMessage: null,
    },
  ];
}

/** A `filterAlreadyPresent` GQL page response (`already-present-filter.ts`) containing exactly the
 *  given 7TV emote ids, as the single (and last) page. */
function emoteSetPage(ids: string[] = []) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: ids.length,
            pageCount: 1,
            items: ids.map((id) => ({ emote: { id } })),
          },
        },
      },
    },
  };
}

/** Same page shape as `emoteSetPage`, but with each entry's alias — what the per-alias restore
 *  check (`filterAlreadyPresentForRestore`) reads. */
function emoteSetEntriesPage(entries: { id: string; alias: string }[]) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: entries.length,
            pageCount: 1,
            items: entries.map(({ id, alias }) => ({ alias, emote: { id } })),
          },
        },
      },
    },
  };
}

function duplicateCellRow(): PurgeRunRow {
  return {
    emoteId: 'e1',
    sevenTvEmoteId: '7tv-1',
    name: 'PogU',
    aliases: ['PogU', 'PogU2'],
    status: 'done',
    errorMessage: null,
  };
}

function readyStatus(overrides: Partial<EmoteSetStatus> = {}): EmoteSetStatus {
  return {
    activeEmoteSetId: SET_ID,
    capacity: 1000,
    occupiedSlots: 10,
    trackedSince: '2026-01-01T00:00:00Z',
    syncFailureReason: null,
    lastSyncAttemptAtUtc: null,
    botsExcludedSince: null,
    sharedChatSeparatedSince: null,
    duplicateNames: [],
    ...overrides,
  };
}

function readyPreview(overrides: Partial<ForeignEmoteSetResponse> = {}): ForeignEmoteSetResponse {
  return {
    channelName: CHANNEL,
    sevenTvUserId: null,
    emoteSetId: SET_ID,
    emoteSetName: SET_NAME,
    capacity: 1000,
    totalCount: 10,
    truncated: false,
    emotes: [],
    ...overrides,
  };
}

interface Harness {
  deps: RestoreFlowDeps;
  dialogOpen: ReturnType<typeof vi.fn>;
  getSetStatus: ReturnType<typeof vi.fn>;
  /** The non-active set's slot preview (spec #200, 8.3, K5) — only read when `isActiveSet` is
   *  false. Defaults to a response the active-set tests never touch. */
  loadEmoteSetPreview: ReturnType<typeof vi.fn>;
  /** The pre-run duplicate check (#149/T5) — since the P1 fix this is a raw `HttpClient.post`
   *  straight to 7TV's `v4` GQL endpoint (`already-present-filter.ts`), not `emoteAdminService`
   *  (our own database, wrong for restore — see that file's doc). Defaults to reporting an empty
   *  target set, i.e. no row gets filtered, unless a test overrides it. */
  httpPost: ReturnType<typeof vi.fn>;
  startRestore: ReturnType<typeof vi.fn>;
  hasToken: WritableSignal<boolean>;
  activeRun: WritableSignal<SevenTvRunKind | null>;
}

function setup(): Harness {
  const getSetStatus = vi.fn(() => of(readyStatus()));
  const emoteAdminService = { getSetStatus } as unknown as EmoteAdminService;

  const loadEmoteSetPreview = vi.fn(() => of(readyPreview()));
  const emoteSetService = { loadEmoteSetPreview } as unknown as SevenTvEmoteSetService;

  const httpPost = vi.fn(() => of(emoteSetPage()));
  const httpClient = { post: httpPost } as unknown as HttpClient;

  const hasToken = signal(true);
  const tokenService = { hasToken } as unknown as SevenTvTokenService;

  const startRestore = vi.fn();
  const restoreService = { startRestore } as unknown as SevenTvRestoreService;

  const activeRun = signal<SevenTvRunKind | null>(null);
  const arbiter = { activeRun } as unknown as SevenTvRunArbiter;

  const dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));
  const dialog = { open: dialogOpen } as unknown as Dialog;

  return {
    deps: {
      dialog,
      emoteAdminService,
      emoteSetService,
      httpClient,
      tokenService,
      restoreService,
      arbiter,
    },
    dialogOpen,
    getSetStatus,
    loadEmoteSetPreview,
    httpPost,
    startRestore,
    hasToken,
    activeRun,
  };
}

/** The `data` the first call to `dialog.open` was handed. Every test that reads it has a token
 *  stored, so that first call IS the confirm dialog. */
function confirmData(dialogOpen: ReturnType<typeof vi.fn>): RestoreConfirmDialogData {
  return dialogOpen.mock.calls[0][1].data as RestoreConfirmDialogData;
}

/** The `closed` subject of the first `dialog.open` call — the token prompt when none is stored,
 *  the confirmation otherwise. */
function firstClosed<T>(dialogOpen: ReturnType<typeof vi.fn>): Subject<T> {
  return dialogOpen.mock.results[0].value.closed as Subject<T>;
}

describe('startRestoreFlow', () => {
  it('prompts for a token first when none is stored, before touching the set or the run', () => {
    const { deps, dialogOpen, getSetStatus, startRestore, hasToken } = setup();
    hasToken.set(false);

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('opens the confirmation with the frozen channel once the token prompt confirms', () => {
    const { deps, dialogOpen, getSetStatus, hasToken } = setup();
    hasToken.set(false);
    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());

    firstClosed<boolean>(dialogOpen).next(true);

    expect(dialogOpen).toHaveBeenCalledTimes(2);
    expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
  });

  it('goes straight to the confirmation when a token is already stored', () => {
    const { deps, dialogOpen, getSetStatus } = setup();

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
  });

  it('starts the restore with the frozen set id, the frozen channel and the given rows once confirmed', () => {
    const { deps, dialogOpen, startRestore } = setup();
    const theRows = rows();

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, theRows);
    firstClosed<boolean>(dialogOpen).next(true);

    // Fourth argument is the duplicate check's skip count (#149/T5) — 0 here because the harness's
    // default 7TV read (`httpPost`) reports an empty target set, so nothing gets filtered. Fifth is
    // whether that check actually ran — true, since the fetch succeeded (#149). Sixth is how many
    // aliases it left out because another emote holds the name — 0, nothing is held.
    expect(startRestore).toHaveBeenCalledWith(
      SET_ID,
      CHANNEL,
      [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
      0,
      true,
      0,
    );
  });

  // #149/T5: restore never had any duplicate protection — these two pin the fix in from the flow
  // layer down (the filtering logic itself is `already-present-filter.spec.ts`'s job).
  describe('duplicate protection (#149/T5)', () => {
    it('checks the target set fresh, right at confirm time, not from an earlier snapshot', () => {
      const { deps, dialogOpen, httpPost } = setup();

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());
      // The set-status fetch for the slot preview runs on dialog-open — the duplicate check must
      // not have run yet at that point, only once the user actually confirms.
      expect(httpPost).not.toHaveBeenCalled();

      firstClosed<boolean>(dialogOpen).next(true);

      expect(httpPost).toHaveBeenCalledWith('https://7tv.io/v4/gql', {
        query: expect.stringContaining('emoteSets'),
        variables: { id: SET_ID, page: 1, perPage: 500 },
      });
    });

    it('drops a row already present in the target set and reports it as skipped, queuing nothing else', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(of(emoteSetPage(['7tv-1'])));

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(SET_ID, CHANNEL, [], 1, true, 0);
    });

    // A second restore over the exact same protocol rows — e.g. the user runs restore, then runs
    // it again without anything having changed in between. Everything is already back in the set,
    // so nothing should be queued the second time.
    it('queues nothing on a second restore over rows already restored', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      const theRows = rows();
      httpPost.mockReturnValue(of(emoteSetPage(theRows.map((row) => row.sevenTvEmoteId))));

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, theRows);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(SET_ID, CHANNEL, [], theRows.length, true, 0);
    });

    // #149 P1 (independent review): the first version of this check asked our own database
    // (`EmoteAdminService.listEmotes`) — exactly wrong for restore, which runs *because* something
    // already went wrong. Right after a delete, our database can still list the deleted emote as
    // active while its closing `sync-deleted` report to our backend is still pending, failed, or
    // partial — checking against that stale mirror would misclassify the very row the user is
    // trying to restore as "already present" and silently drop it from the queue. Reading 7TV
    // itself has no such gap: 7TV's own answer here is empty (the row genuinely is not in the set),
    // so it must be queued regardless of what our database still believes.
    it('still queues a row whose deletion our own database has not caught up with yet', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      // 7TV's own current contents — empty, i.e. 7TV has already dropped the row (the delete
      // succeeded there), even though nothing here asked our database at all any more.
      httpPost.mockReturnValue(of(emoteSetPage([])));

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        SET_ID,
        CHANNEL,
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
        0,
        true,
        0,
      );
    });

    // Operator decision 2026-09-22 ("middle rule", spec 7.2): a restore of a #74 duplicate cell in
    // which one alias came back and the other failed is re-run from the same protocol — only the
    // missing alias may be queued, the present one is skipped (counted per ADD).
    it('re-adds only the missing alias of a duplicate cell whose other alias is already back', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(of(emoteSetEntriesPage([{ id: '7tv-1', alias: 'PogU' }])));

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, [duplicateCellRow()]);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        SET_ID,
        CHANNEL,
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU2'] }],
        1,
        true,
        0,
      );
    });

    // The #149 hole stays shut: the emote sits in the set under a name the row does not know, so
    // re-adding either alias would enter it a second time.
    it('drops the whole row when the emote is already in the set under an alias the row does not name', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(of(emoteSetEntriesPage([{ id: '7tv-1', alias: 'Renamed' }])));

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, [duplicateCellRow()]);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(SET_ID, CHANNEL, [], 2, true, 0);
    });

    // A purge-run row whose name another emote took since the purge: left out before the run
    // instead of burning a ticket on a certain name conflict, and counted apart from "already
    // present" all the way into the run's notice.
    it('leaves out an alias another emote now holds and forwards that count to the run', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(of(emoteSetEntriesPage([{ id: '7tv-other', alias: 'PogU' }])));

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, [duplicateCellRow()]);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        SET_ID,
        CHANNEL,
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU2'] }],
        0,
        true,
        1,
      );
    });

    // #149: a failed check must fail open (every row still goes through, the run still starts) but
    // must not read as a clean all-clear — the flow forwards `available: false` from the filter
    // straight into `startRestore`'s fifth argument rather than swallowing it.
    it('fails open on a failed duplicate check and reports it as unavailable rather than a clean skip', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(throwError(() => new Error('network error')));
      const theRows = rows();

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, theRows);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        SET_ID,
        CHANNEL,
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
        0,
        false,
        0,
      );
    });
  });

  // #149 P2 (independent review): the arbiter's mutual-exclusion check ran before the fresh
  // duplicate check's async fetch — a second run could start in that window and would have
  // overlapped this one.
  it('abandons the start when another run claims the arbiter while the fresh check is still in flight', () => {
    const { deps, dialogOpen, httpPost, startRestore, activeRun } = setup();
    const fetch = new Subject<ReturnType<typeof emoteSetPage>>();
    httpPost.mockReturnValue(fetch);

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());
    firstClosed<boolean>(dialogOpen).next(true);

    // A delete run starts elsewhere while this restore's own fresh check is still awaiting 7TV.
    activeRun.set('delete');
    fetch.next(emoteSetPage());
    fetch.complete();

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('never opens the confirmation and never runs when the token prompt is cancelled', () => {
    const { deps, dialogOpen, getSetStatus, startRestore, hasToken } = setup();
    hasToken.set(false);
    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());

    firstClosed<boolean>(dialogOpen).next(false);

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('does not run when the confirmation is cancelled', () => {
    const { deps, dialogOpen, startRestore } = setup();

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());
    firstClosed<boolean>(dialogOpen).next(false);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('silently drops a confirmed outcome while another 7TV run is already active', () => {
    const { deps, dialogOpen, startRestore, activeRun } = setup();
    activeRun.set('import');

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());
    firstClosed<boolean>(dialogOpen).next(true);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('projects the answered slot numbers into the confirmation', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ occupiedSlots: 42, capacity: 600 })));

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());

    expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 42, capacity: 600 });
  });

  it('shows no slot projection once the set has no reported capacity', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ capacity: null })));

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());

    expect(confirmData(dialogOpen).slots()).toBeNull();
  });

  // Deliberately emits a *usable* status before the error: `slots` starts out null, so a test that
  // only errored would stay green with the error handler deleted outright.
  it('drops the slot projection again when the status request errors after answering', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    const status$ = new Subject<EmoteSetStatus>();
    getSetStatus.mockReturnValue(status$);

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());
    status$.next(readyStatus({ occupiedSlots: 42, capacity: 600 }));
    expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 42, capacity: 600 });

    status$.error(new Error('boom'));

    expect(confirmData(dialogOpen).slots()).toBeNull();
  });

  // spec #200, 8.8 (AK 73): both confirmations name the set the run acts on, with the
  // "not currently active" addition gated on `isActiveSet` alone.
  describe('naming the set (spec #200, 8.8)', () => {
    it('names the given set in the confirmation and marks it active', () => {
      const { deps, dialogOpen } = setup();

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, rows());

      expect(confirmData(dialogOpen).setName).toBe(SET_NAME);
      expect(confirmData(dialogOpen).isActiveSet).toBe(true);
    });

    it('falls back to the set id when the set has no known name, same as every other unnamed set', () => {
      const { deps, dialogOpen } = setup();

      startRestoreFlow(deps, CHANNEL, SET_ID, null, true, rows());

      expect(confirmData(dialogOpen).setName).toBe(SET_ID);
    });

    it('marks the set not active, and reads its live slot preview instead of EmoteSetStatus, when isActiveSet is false', () => {
      const { deps, dialogOpen, getSetStatus, loadEmoteSetPreview } = setup();
      loadEmoteSetPreview.mockReturnValue(of(readyPreview({ totalCount: 900, capacity: 1000 })));

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, false, rows());

      expect(confirmData(dialogOpen).isActiveSet).toBe(false);
      expect(loadEmoteSetPreview).toHaveBeenCalledWith(CHANNEL, SET_ID);
      expect(getSetStatus).not.toHaveBeenCalled();
      expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 900, capacity: 1000 });
    });

    it('shows no slot projection for a non-active set the preview reports no capacity for', () => {
      const { deps, dialogOpen, loadEmoteSetPreview } = setup();
      loadEmoteSetPreview.mockReturnValue(of(readyPreview({ capacity: null })));

      startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, false, rows());

      expect(confirmData(dialogOpen).slots()).toBeNull();
    });
  });

  // spec #200, 7.2: the restore's own capacity math counts ADDs (one per alias), not rows — a #74
  // duplicate cell is one row in `names` but restores under two aliases.
  it('projects the ADD count, not the row count, for a duplicate cell restored under two aliases', () => {
    const { deps, dialogOpen } = setup();
    const duplicateRow: PurgeRunRow = {
      emoteId: 'e2',
      sevenTvEmoteId: '7tv-2',
      name: 'Kappa',
      aliases: ['Kappa', 'KappaAlt'],
      status: 'done',
      errorMessage: null,
    };

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, [duplicateRow]);

    expect(confirmData(dialogOpen).names).toEqual(['Kappa']);
    expect(confirmData(dialogOpen).addCount).toBe(2);
  });

  // A removed transfer target without a named alias: listed under its default name, and its one
  // entry without an alias is an ADD like any other.
  it('counts an entry without an alias as an ADD and lists its row under the default name', () => {
    const { deps, dialogOpen } = setup();
    const transferRow: RestoreRow = {
      emoteId: null,
      sevenTvEmoteId: 'tgt-1',
      name: 'KappaDefault',
      aliases: [null],
      defaultName: 'KappaDefault',
    };

    startRestoreFlow(deps, CHANNEL, SET_ID, SET_NAME, true, [transferRow, ...rows()]);

    expect(confirmData(dialogOpen).names).toEqual(['KappaDefault', 'PogU']);
    expect(confirmData(dialogOpen).addCount).toBe(2);
  });
});
