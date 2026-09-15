import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { signal, WritableSignal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { PurgeRunRow } from '../export/purge-run-export';
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

function rows(): PurgeRunRow[] {
  return [
    { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done', errorMessage: null },
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

interface Harness {
  deps: RestoreFlowDeps;
  dialogOpen: ReturnType<typeof vi.fn>;
  getSetStatus: ReturnType<typeof vi.fn>;
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
    deps: { dialog, emoteAdminService, httpClient, tokenService, restoreService, arbiter },
    dialogOpen,
    getSetStatus,
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

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('opens the confirmation with the frozen channel once the token prompt confirms', () => {
    const { deps, dialogOpen, getSetStatus, hasToken } = setup();
    hasToken.set(false);
    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    firstClosed<boolean>(dialogOpen).next(true);

    expect(dialogOpen).toHaveBeenCalledTimes(2);
    expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
  });

  it('goes straight to the confirmation when a token is already stored', () => {
    const { deps, dialogOpen, getSetStatus } = setup();

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
  });

  it('starts the restore with the frozen set id, the frozen channel and the given rows once confirmed', () => {
    const { deps, dialogOpen, startRestore } = setup();
    const theRows = rows();

    startRestoreFlow(deps, CHANNEL, SET_ID, theRows);
    firstClosed<boolean>(dialogOpen).next(true);

    // Fourth argument is the duplicate check's skip count (#149/T5) — 0 here because the harness's
    // default 7TV read (`httpPost`) reports an empty target set, so nothing gets filtered. Fifth is
    // whether that check actually ran — true, since the fetch succeeded (#149).
    expect(startRestore).toHaveBeenCalledWith(
      SET_ID,
      CHANNEL,
      [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' }],
      0,
      true,
    );
  });

  // #149/T5: restore never had any duplicate protection — these two pin the fix in from the flow
  // layer down (the filtering logic itself is `already-present-filter.spec.ts`'s job).
  describe('duplicate protection (#149/T5)', () => {
    it('checks the target set fresh, right at confirm time, not from an earlier snapshot', () => {
      const { deps, dialogOpen, httpPost } = setup();

      startRestoreFlow(deps, CHANNEL, SET_ID, rows());
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

      startRestoreFlow(deps, CHANNEL, SET_ID, rows());
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(SET_ID, CHANNEL, [], 1, true);
    });

    // A second restore over the exact same protocol rows — e.g. the user runs restore, then runs
    // it again without anything having changed in between. Everything is already back in the set,
    // so nothing should be queued the second time.
    it('queues nothing on a second restore over rows already restored', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      const theRows = rows();
      httpPost.mockReturnValue(of(emoteSetPage(theRows.map((row) => row.sevenTvEmoteId))));

      startRestoreFlow(deps, CHANNEL, SET_ID, theRows);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(SET_ID, CHANNEL, [], theRows.length, true);
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

      startRestoreFlow(deps, CHANNEL, SET_ID, rows());
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        SET_ID,
        CHANNEL,
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' }],
        0,
        true,
      );
    });

    // #149: a failed check must fail open (every row still goes through, the run still starts) but
    // must not read as a clean all-clear — the flow forwards `available: false` from the filter
    // straight into `startRestore`'s fifth argument rather than swallowing it.
    it('fails open on a failed duplicate check and reports it as unavailable rather than a clean skip', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(throwError(() => new Error('network error')));
      const theRows = rows();

      startRestoreFlow(deps, CHANNEL, SET_ID, theRows);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        SET_ID,
        CHANNEL,
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' }],
        0,
        false,
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

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
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
    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    firstClosed<boolean>(dialogOpen).next(false);

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('does not run when the confirmation is cancelled', () => {
    const { deps, dialogOpen, startRestore } = setup();

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    firstClosed<boolean>(dialogOpen).next(false);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('silently drops a confirmed outcome while another 7TV run is already active', () => {
    const { deps, dialogOpen, startRestore, activeRun } = setup();
    activeRun.set('import');

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    firstClosed<boolean>(dialogOpen).next(true);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('projects the answered slot numbers into the confirmation', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ occupiedSlots: 42, capacity: 600 })));

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 42, capacity: 600 });
  });

  it('shows no slot projection once the set has no reported capacity', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ capacity: null })));

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());

    expect(confirmData(dialogOpen).slots()).toBeNull();
  });

  // Deliberately emits a *usable* status before the error: `slots` starts out null, so a test that
  // only errored would stay green with the error handler deleted outright.
  it('drops the slot projection again when the status request errors after answering', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    const status$ = new Subject<EmoteSetStatus>();
    getSetStatus.mockReturnValue(status$);

    startRestoreFlow(deps, CHANNEL, SET_ID, rows());
    status$.next(readyStatus({ occupiedSlots: 42, capacity: 600 }));
    expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 42, capacity: 600 });

    status$.error(new Error('boom'));

    expect(confirmData(dialogOpen).slots()).toBeNull();
  });
});
