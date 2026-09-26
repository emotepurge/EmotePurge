import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { DestroyRef, signal, WritableSignal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { ForeignEmoteSetResponse } from '../../core/seven-tv/foreign-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { PurgeRunRow, RestoreRow } from '../export/purge-run-export';
import { RestoreConfirmDialogData } from './restore-confirm-dialog';
import { ResolvedRestoreTarget, RestoreFlowDeps, startRestoreFlow } from './restore-flow';

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

/** A tracked, active target of the frozen `CHANNEL`/`SET_ID`/`SET_NAME` — the shape most tests
 *  below need, with only the fields a given test cares about overridden (spec 6.1). */
function target(overrides: Partial<ResolvedRestoreTarget> = {}): ResolvedRestoreTarget {
  return {
    emoteSetId: SET_ID,
    setName: SET_NAME,
    ownerDisplayName: 'SomeOwner',
    twitchLogin: CHANNEL,
    trackedChannelName: CHANNEL,
    isActiveSet: true,
    hostChannelName: CHANNEL,
    hostSelectedSetId: SET_ID,
    ...overrides,
  };
}

/** A minimal stand-in for `DestroyRef` (#255 P2a) — `startRestoreFlow` has no injection context of
 *  its own, so every caller (here, the test) hands in its own. `triggerDestroy()` is the test-only
 *  half: nothing in the real `DestroyRef` API exposes it, since only Angular itself ever calls a
 *  component's registered callbacks. */
interface FakeDestroyRef {
  onDestroy(callback: () => void): () => void;
  triggerDestroy(): void;
}

function fakeDestroyRef(): FakeDestroyRef {
  const callbacks = new Set<() => void>();
  return {
    onDestroy: (callback) => {
      callbacks.add(callback);
      return () => callbacks.delete(callback);
    },
    triggerDestroy: () => {
      for (const callback of callbacks) {
        callback();
      }
    },
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
  /** `RestoreFlowDeps.previewPending` (#255 P2a) — read directly by tests that check the flow's own
   *  re-entrancy guard, rather than only its externally visible effects. */
  previewPending: WritableSignal<boolean>;
  /** The fake behind `deps.destroyRef` — `triggerDestroy()` simulates the caller's teardown. */
  destroyRef: FakeDestroyRef;
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

  const previewPending = signal(false);
  const destroyRef = fakeDestroyRef();

  return {
    deps: {
      dialog,
      emoteAdminService,
      emoteSetService,
      httpClient,
      tokenService,
      restoreService,
      arbiter,
      previewPending,
      destroyRef: destroyRef as unknown as DestroyRef,
    },
    dialogOpen,
    getSetStatus,
    loadEmoteSetPreview,
    httpPost,
    previewPending,
    destroyRef,
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

    startRestoreFlow(deps, target(), rows());

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('opens the confirmation with the frozen channel once the token prompt confirms', () => {
    const { deps, dialogOpen, getSetStatus, hasToken } = setup();
    hasToken.set(false);
    startRestoreFlow(deps, target(), rows());

    firstClosed<boolean>(dialogOpen).next(true);

    expect(dialogOpen).toHaveBeenCalledTimes(2);
    expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
  });

  it('goes straight to the confirmation when a token is already stored', () => {
    const { deps, dialogOpen, getSetStatus } = setup();

    startRestoreFlow(deps, target(), rows());

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
  });

  it('starts the restore with the frozen set id, the frozen channel and the given rows once confirmed', () => {
    const { deps, dialogOpen, startRestore } = setup();
    const theRows = rows();

    startRestoreFlow(deps, target(), theRows);
    firstClosed<boolean>(dialogOpen).next(true);

    // Fourth argument is the duplicate check's skip count (#149/T5) — 0 here because the harness's
    // default 7TV read (`httpPost`) reports an empty target set, so nothing gets filtered. Fifth is
    // whether that check actually ran — true, since the fetch succeeded (#149). Sixth is how many
    // aliases it left out because another emote holds the name — 0, nothing is held.
    expect(startRestore).toHaveBeenCalledWith(
      expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
      [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
      0,
      true,
      0,
    );
  });

  // Spec 6.4, E12, E18 (derivation from the resolved target): a tracked, active target makes its
  // channel the expected hit and leaves the resync to the backend; a tracked, non-active target
  // expects no channel and names it for the client's own resync; an untracked target sends
  // neither and falls back to the owner's display name for the dock's label.
  describe('restore start target (spec 6.4)', () => {
    it('expects the tracked channel and resyncs nothing itself for its active set', () => {
      const { deps, dialogOpen, startRestore } = setup();

      startRestoreFlow(deps, target(), rows());
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore.mock.calls[0][0]).toEqual({
        setId: SET_ID,
        expectedChannelName: CHANNEL,
        resyncChannelName: null,
        hostChannelName: CHANNEL,
        setName: SET_NAME,
        ownerOrChannelLabel: CHANNEL,
      });
    });

    it('expects no channel and names the tracked channel for its own resync for a non-active set', () => {
      const { deps, dialogOpen, startRestore } = setup();

      startRestoreFlow(deps, target({ isActiveSet: false, setName: SET_ID }), rows());
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore.mock.calls[0][0]).toEqual({
        setId: SET_ID,
        expectedChannelName: null,
        resyncChannelName: CHANNEL,
        hostChannelName: CHANNEL,
        setName: SET_ID,
        ownerOrChannelLabel: CHANNEL,
      });
    });

    it('expects no channel, resyncs nothing itself, and labels the dock with the owner for an untracked target', () => {
      const { deps, dialogOpen, startRestore } = setup();

      startRestoreFlow(
        deps,
        target({ trackedChannelName: null, isActiveSet: false, ownerDisplayName: 'SomeOwner' }),
        rows(),
      );
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore.mock.calls[0][0]).toEqual({
        setId: SET_ID,
        expectedChannelName: null,
        resyncChannelName: null,
        hostChannelName: CHANNEL,
        setName: SET_NAME,
        ownerOrChannelLabel: 'SomeOwner',
      });
    });
  });

  // #149/T5: restore never had any duplicate protection — these two pin the fix in from the flow
  // layer down (the filtering logic itself is `already-present-filter.spec.ts`'s job).
  describe('duplicate protection (#149/T5)', () => {
    // Operator decision 2026-09-25 (#255, "Slot-Zahl nach dem Skip-Filter"): the check now also
    // runs once, fresh, before the confirmation opens (so its title/slot projection count what
    // will actually be sent) — and, unchanged from before, once again at confirm time, against
    // whatever the target set holds *then*, not the open-time snapshot. Two different answers
    // prove the second read is genuinely fresh rather than reusing the first.
    it('re-checks the target set fresh at confirm time, not from the open-time snapshot', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      // Open-time: nothing present yet, so the confirmation shows the full row.
      httpPost.mockReturnValueOnce(of(emoteSetPage([])));
      // Confirm-time: the row is now present (e.g. a second tab beat this one to it) — the fresh
      // read must catch that, not fall back on the first answer.
      httpPost.mockReturnValueOnce(of(emoteSetPage(['7tv-1'])));

      startRestoreFlow(deps, target(), rows());
      expect(confirmData(dialogOpen).addCount).toBe(1);

      firstClosed<boolean>(dialogOpen).next(true);

      expect(httpPost).toHaveBeenCalledTimes(2);
      expect(httpPost).toHaveBeenCalledWith('https://7tv.io/v4/gql', {
        query: expect.stringContaining('emoteSets'),
        variables: { id: SET_ID, page: 1, perPage: 500 },
      });
      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
        [],
        1,
        true,
        0,
      );
    });

    // #255: once the open-time check finds every row already present, there is nothing left to
    // confirm — no dialog opens at all, and the existing "everything already there" notice
    // (SevenTvRestoreService.duplicateNoticePending) reports it directly.
    it('drops a row already present in the target set, opens no dialog, and reports it as skipped', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(of(emoteSetPage(['7tv-1'])));

      startRestoreFlow(deps, target(), rows());

      expect(dialogOpen).not.toHaveBeenCalled();
      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
        [],
        1,
        true,
        0,
      );
    });

    // A second restore over the exact same protocol rows — e.g. the user runs restore, then runs
    // it again without anything having changed in between. Everything is already back in the set,
    // so nothing should be queued the second time, and no dialog opens (#255).
    it('queues nothing on a second restore over rows already restored, opening no dialog', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      const theRows = rows();
      httpPost.mockReturnValue(of(emoteSetPage(theRows.map((row) => row.sevenTvEmoteId))));

      startRestoreFlow(deps, target(), theRows);

      expect(dialogOpen).not.toHaveBeenCalled();
      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
        [],
        theRows.length,
        true,
        0,
      );
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

      startRestoreFlow(deps, target(), rows());
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
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

      startRestoreFlow(deps, target(), [duplicateCellRow()]);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU2'] }],
        1,
        true,
        0,
      );
    });

    // The #149 hole stays shut: the emote sits in the set under a name the row does not know, so
    // re-adding either alias would enter it a second time. Both aliases end up skipped, so the
    // open-time filter already leaves nothing to confirm (#255) — same shortcut as a plain
    // duplicate.
    it('drops the whole row when the emote is already in the set under an alias the row does not name, opening no dialog', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(of(emoteSetEntriesPage([{ id: '7tv-1', alias: 'Renamed' }])));

      startRestoreFlow(deps, target(), [duplicateCellRow()]);

      expect(dialogOpen).not.toHaveBeenCalled();
      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
        [],
        2,
        true,
        0,
      );
    });

    // A purge-run row whose name another emote took since the purge: left out before the run
    // instead of burning a ticket on a certain name conflict, and counted apart from "already
    // present" all the way into the run's notice.
    it('leaves out an alias another emote now holds and forwards that count to the run', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(of(emoteSetEntriesPage([{ id: '7tv-other', alias: 'PogU' }])));

      startRestoreFlow(deps, target(), [duplicateCellRow()]);
      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU2'] }],
        0,
        true,
        1,
      );
    });

    // #149: a failed check must fail open (every row still goes through, the run still starts) but
    // must not read as a clean all-clear — the flow forwards `available: false` from the filter
    // straight into `startRestore`'s fifth argument rather than swallowing it. #255: the same
    // failure also marks the confirmation's own count as an upper bound, since the open-time check
    // fails open too and cannot vouch for it.
    it('fails open on a failed duplicate check, marks the confirmation count an upper bound, and reports it as unavailable rather than a clean skip', () => {
      const { deps, dialogOpen, httpPost, startRestore } = setup();
      httpPost.mockReturnValue(throwError(() => new Error('network error')));
      const theRows = rows();

      startRestoreFlow(deps, target(), theRows);

      expect(confirmData(dialogOpen).countIsUpperBound).toBe(true);
      expect(confirmData(dialogOpen).addCount).toBe(1);

      firstClosed<boolean>(dialogOpen).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
        0,
        false,
        0,
      );
    });
  });

  // #149 P2 (independent review): the arbiter's mutual-exclusion check ran before the fresh
  // duplicate check's async fetch — a second run could start in that window and would have
  // overlapped this one. The open-time check (#255) resolves synchronously here (its default
  // mock) so the confirmation opens normally; only the *confirm-time* check hangs.
  it('abandons the start when another run claims the arbiter while the confirm-time check is still in flight', () => {
    const { deps, dialogOpen, httpPost, startRestore, activeRun } = setup();
    httpPost.mockReturnValueOnce(of(emoteSetPage()));
    const fetch = new Subject<ReturnType<typeof emoteSetPage>>();
    httpPost.mockReturnValueOnce(fetch);

    startRestoreFlow(deps, target(), rows());
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
    startRestoreFlow(deps, target(), rows());

    firstClosed<boolean>(dialogOpen).next(false);

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(getSetStatus).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  it('does not run when the confirmation is cancelled', () => {
    const { deps, dialogOpen, startRestore } = setup();

    startRestoreFlow(deps, target(), rows());
    firstClosed<boolean>(dialogOpen).next(false);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('silently drops a confirmed outcome while another 7TV run is already active', () => {
    const { deps, dialogOpen, startRestore, activeRun } = setup();
    activeRun.set('import');

    startRestoreFlow(deps, target(), rows());
    firstClosed<boolean>(dialogOpen).next(true);

    expect(startRestore).not.toHaveBeenCalled();
  });

  it('projects the answered slot numbers into the confirmation', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ occupiedSlots: 42, capacity: 600 })));

    startRestoreFlow(deps, target(), rows());

    expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 42, capacity: 600 });
  });

  it('shows no slot projection once the set has no reported capacity', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    getSetStatus.mockReturnValue(of(readyStatus({ capacity: null })));

    startRestoreFlow(deps, target(), rows());

    expect(confirmData(dialogOpen).slots()).toBeNull();
  });

  // Deliberately emits a *usable* status before the error: `slots` starts out null, so a test that
  // only errored would stay green with the error handler deleted outright.
  it('drops the slot projection again when the status request errors after answering', () => {
    const { deps, dialogOpen, getSetStatus } = setup();
    const status$ = new Subject<EmoteSetStatus>();
    getSetStatus.mockReturnValue(status$);

    startRestoreFlow(deps, target(), rows());
    status$.next(readyStatus({ occupiedSlots: 42, capacity: 600 }));
    expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 42, capacity: 600 });

    status$.error(new Error('boom'));

    expect(confirmData(dialogOpen).slots()).toBeNull();
  });

  // spec #200, 8.8 (AK 73): the confirmation data carries the resolved target's name and
  // active-set flag through unchanged.
  describe('naming the set (spec #200, 8.8)', () => {
    it('names the given set in the confirmation and marks it active', () => {
      const { deps, dialogOpen } = setup();

      startRestoreFlow(deps, target(), rows());

      expect(confirmData(dialogOpen).setName).toBe(SET_NAME);
      expect(confirmData(dialogOpen).isActiveSet).toBe(true);
    });

    // The "no known name" fallback itself moved upstream since #253: `target.setName` already
    // carries it (`EditableSetTarget.setName`, T4's `toEditableSetTarget`) — this flow only ever
    // passes it through unchanged. Covered there (`seven-tv-emote-set.service.spec.ts`), not here.

    it('marks the set not active, and reads its live slot preview instead of EmoteSetStatus, when isActiveSet is false', () => {
      const { deps, dialogOpen, getSetStatus, loadEmoteSetPreview } = setup();
      loadEmoteSetPreview.mockReturnValue(of(readyPreview({ totalCount: 900, capacity: 1000 })));

      startRestoreFlow(deps, target({ isActiveSet: false }), rows());

      expect(confirmData(dialogOpen).isActiveSet).toBe(false);
      expect(loadEmoteSetPreview).toHaveBeenCalledWith(CHANNEL, SET_ID);
      expect(getSetStatus).not.toHaveBeenCalled();
      expect(confirmData(dialogOpen).slots()).toEqual({ occupied: 900, capacity: 1000 });
    });

    it('shows no slot projection for a non-active set the preview reports no capacity for', () => {
      const { deps, dialogOpen, loadEmoteSetPreview } = setup();
      loadEmoteSetPreview.mockReturnValue(of(readyPreview({ capacity: null })));

      startRestoreFlow(deps, target({ isActiveSet: false }), rows());

      expect(confirmData(dialogOpen).slots()).toBeNull();
    });
  });

  // Task brief T6, "+6" (1 of 2): the slot-preview source is chosen per target class (spec 4.3,
  // point 8 — the same fork `loadImportTarget` already uses), not per `isActiveSet` alone.
  describe('slot preview fork by target class (spec 4.3, point 8)', () => {
    it('reads EmoteSetStatus by the tracked channel for a tracked, active target', () => {
      const { deps, getSetStatus, loadEmoteSetPreview } = setup();

      startRestoreFlow(deps, target({ trackedChannelName: CHANNEL, isActiveSet: true }), rows());

      expect(getSetStatus).toHaveBeenCalledWith(CHANNEL);
      expect(loadEmoteSetPreview).not.toHaveBeenCalled();
    });

    it('reads the live preview by the tracked channel for a tracked, non-active target', () => {
      const { deps, getSetStatus, loadEmoteSetPreview } = setup();

      startRestoreFlow(deps, target({ trackedChannelName: CHANNEL, isActiveSet: false }), rows());

      expect(loadEmoteSetPreview).toHaveBeenCalledWith(CHANNEL, SET_ID);
      expect(getSetStatus).not.toHaveBeenCalled();
    });

    it("reads the live preview by the account's twitchLogin for an untracked target", () => {
      const { deps, getSetStatus, loadEmoteSetPreview } = setup();

      startRestoreFlow(
        deps,
        target({ trackedChannelName: null, isActiveSet: false, twitchLogin: 'sevenTvOwner' }),
        rows(),
      );

      expect(loadEmoteSetPreview).toHaveBeenCalledWith('sevenTvOwner', SET_ID);
      expect(getSetStatus).not.toHaveBeenCalled();
    });
  });

  // Task brief T6, "+6" (2 of 2); spec E21, AK 19/35, spec 9.3: the foreign-to-view hint compares
  // the resolved target's set against the page's *selected* set, never a channel.
  describe('foreignToView (spec E21, AK 19/35)', () => {
    it("is false when the target is the page's selected set", () => {
      const { deps, dialogOpen } = setup();

      startRestoreFlow(deps, target({ hostSelectedSetId: SET_ID }), rows());

      expect(confirmData(dialogOpen).foreignToView).toBe(false);
    });

    it('is true for a different set of the same tracked channel', () => {
      const { deps, dialogOpen } = setup();

      startRestoreFlow(deps, target({ hostSelectedSetId: 'some-other-set' }), rows());

      expect(confirmData(dialogOpen).foreignToView).toBe(true);
    });

    it('is true when the page has no selected set at all', () => {
      const { deps, dialogOpen } = setup();

      startRestoreFlow(deps, target({ hostSelectedSetId: null }), rows());

      expect(confirmData(dialogOpen).foreignToView).toBe(true);
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

    startRestoreFlow(deps, target(), [duplicateRow]);

    expect(confirmData(dialogOpen).names).toEqual(['Kappa']);
    expect(confirmData(dialogOpen).addCount).toBe(2);
  });

  // Operator decision 2026-09-25 (#255, "Slot-Zahl nach dem Skip-Filter"): the confirmation's title
  // and slot projection count what the open-time check actually found, not every row the caller
  // named — a row already present in the target set neither counts towards addCount nor appears in
  // the preview list, while a row that is genuinely missing still does.
  it('shows only the rows the open-time check actually found missing, not every row the caller passed', () => {
    const { deps, dialogOpen, httpPost } = setup();
    // '7tv-1' (PogU, from rows()) is already in the target set; a second, genuinely missing row is
    // not.
    httpPost.mockReturnValue(of(emoteSetPage(['7tv-1'])));
    const missingRow: PurgeRunRow = {
      emoteId: 'e2',
      sevenTvEmoteId: '7tv-2',
      name: 'Kappa',
      aliases: ['Kappa'],
      status: 'done',
      errorMessage: null,
    };

    startRestoreFlow(deps, target(), [...rows(), missingRow]);

    expect(confirmData(dialogOpen).names).toEqual(['Kappa']);
    expect(confirmData(dialogOpen).addCount).toBe(1);
    expect(confirmData(dialogOpen).countIsUpperBound).toBe(false);
  });

  // #255 P2 (Codex review): a read that succeeds but only sees part of the target set
  // (`SevenTvSetEntries.complete: false` — here, 7TV's own `totalCount` promising one more entry
  // than this single page delivered) must not let the confirmation claim an exact count it never
  // verified. The filtering itself is unaffected — the found-present row still drops out, the
  // genuinely-missing one still shows — only the wording changes, same as a failed read.
  it('marks the count an upper bound, while still filtering rows normally, when the open-time read is truncated', () => {
    const { deps, dialogOpen, httpPost } = setup();
    // '7tv-1' (PogU, from rows()) is already in the target set; a second, genuinely missing row is
    // not — same setup as the test above, but the read's own totalCount does not match what this
    // page delivered.
    httpPost.mockReturnValue(
      of({
        data: {
          emoteSets: {
            emoteSet: {
              emotes: {
                totalCount: 2,
                pageCount: 1,
                items: [{ alias: 'PogU', emote: { id: '7tv-1' } }],
              },
            },
          },
        },
      }),
    );
    const missingRow: PurgeRunRow = {
      emoteId: 'e2',
      sevenTvEmoteId: '7tv-2',
      name: 'Kappa',
      aliases: ['Kappa'],
      status: 'done',
      errorMessage: null,
    };

    startRestoreFlow(deps, target(), [...rows(), missingRow]);

    expect(confirmData(dialogOpen).names).toEqual(['Kappa']);
    expect(confirmData(dialogOpen).addCount).toBe(1);
    expect(confirmData(dialogOpen).countIsUpperBound).toBe(true);
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

    startRestoreFlow(deps, target(), [transferRow, ...rows()]);

    expect(confirmData(dialogOpen).names).toEqual(['KappaDefault', 'PogU']);
    expect(confirmData(dialogOpen).addCount).toBe(2);
  });

  // #255 P2a: the open-time duplicate check (`loadRestoreConfirmPreview`) is now bounded by the
  // same timeout budget as every other 7TV read in this app, dropped on the caller's teardown, and
  // guarded against a second click while it is still out.
  describe('open-time check: timeout, teardown, double-click (#255 P2a)', () => {
    afterEach(() => {
      vi.useRealTimers();
    });

    it('opens the confirmation with an upper-bound count when the open-time check hangs past its timeout, and clears previewPending', () => {
      vi.useFakeTimers();
      const { deps, dialogOpen, httpPost, previewPending } = setup();
      const hang = new Subject<ReturnType<typeof emoteSetPage>>();
      httpPost.mockReturnValueOnce(hang);

      startRestoreFlow(deps, target(), rows());

      expect(dialogOpen).not.toHaveBeenCalled();
      expect(previewPending()).toBe(true);

      vi.advanceTimersByTime(20_000);

      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(confirmData(dialogOpen).countIsUpperBound).toBe(true);
      expect(confirmData(dialogOpen).names).toEqual(['PogU']);
      expect(previewPending()).toBe(false);
    });

    it('drops a late open-time answer after the caller tears down, never opening a confirmation', () => {
      const { deps, dialogOpen, httpPost, destroyRef } = setup();
      const fetch = new Subject<ReturnType<typeof emoteSetPage>>();
      httpPost.mockReturnValueOnce(fetch);

      startRestoreFlow(deps, target(), rows());
      destroyRef.triggerDestroy();
      fetch.next(emoteSetPage());
      fetch.complete();

      expect(dialogOpen).not.toHaveBeenCalled();
    });

    // #255 P2 (Codex review, second finding): `takeUntilDestroyed` tears the read down silently —
    // neither `next` nor `error` fires — so a reset reachable only from those never ran, and this
    // flag aliases `SevenTvRestoreService.restorePreCheckPending`, shared with `MassDeletePanel`'s
    // own restore button: leaving it `true` here left *both* restore entries disabled until a full
    // page reload, not just this caller's own.
    it('clears previewPending once the caller tears down mid-read, not just on a settled answer', () => {
      const { deps, httpPost, previewPending, destroyRef } = setup();
      httpPost.mockReturnValueOnce(new Subject<ReturnType<typeof emoteSetPage>>());

      startRestoreFlow(deps, target(), rows());
      expect(previewPending()).toBe(true);

      destroyRef.triggerDestroy();

      expect(previewPending()).toBe(false);
    });

    it('refuses a second open-time read while the first is still out, so only one confirmation ever opens', () => {
      const { deps, dialogOpen, httpPost } = setup();
      const fetch = new Subject<ReturnType<typeof emoteSetPage>>();
      httpPost.mockReturnValueOnce(fetch);

      startRestoreFlow(deps, target(), rows());
      // A second click on whatever button opened this flow, outracing its own disabled state (or
      // a caller with no such guard of its own) — the flow's own `previewPending` check inside
      // `openConfirm` is what stops this from starting a second read.
      startRestoreFlow(deps, target(), rows());

      expect(httpPost).toHaveBeenCalledTimes(1);

      fetch.next(emoteSetPage());
      fetch.complete();

      expect(dialogOpen).toHaveBeenCalledTimes(1);
    });
  });

  // #255 P2b (the #149 P2 fix's own reasoning, applied to the open-time "everything already
  // there" shortcut too): that shortcut starts a run exactly as much as the regular confirmed path
  // does, so it needs the same arbiter check right before it.
  it('drops the open-time "everything already there" shortcut when another run claims the arbiter while the check was out', () => {
    const { deps, dialogOpen, httpPost, startRestore, activeRun } = setup();
    const fetch = new Subject<ReturnType<typeof emoteSetPage>>();
    httpPost.mockReturnValueOnce(fetch);

    startRestoreFlow(deps, target(), rows());

    // A delete run starts elsewhere while the open-time check is still awaiting 7TV.
    activeRun.set('delete');
    // '7tv-1' (the only row, PogU) is already present — this would take the shortcut and start a
    // (skipped-only) restore were the arbiter not re-checked first.
    fetch.next(emoteSetPage(['7tv-1']));
    fetch.complete();

    expect(dialogOpen).not.toHaveBeenCalled();
    expect(startRestore).not.toHaveBeenCalled();
  });

  // #255 P3(7): a confirm-time check that fails outright must not undo the open-time check's own,
  // still-valid answer by sending every row unfiltered — it falls back to what the open-time check
  // already found missing instead.
  it('falls back to the open-time filtered rows when the confirm-time check fails, rather than sending everything unfiltered', () => {
    const { deps, dialogOpen, httpPost, startRestore } = setup();
    const missingRow: PurgeRunRow = {
      emoteId: 'e2',
      sevenTvEmoteId: '7tv-2',
      name: 'Kappa',
      aliases: ['Kappa'],
      status: 'done',
      errorMessage: null,
    };
    // Open-time: '7tv-1' (PogU) is already present, '7tv-2' (Kappa) is not.
    httpPost.mockReturnValueOnce(of(emoteSetPage(['7tv-1'])));
    // Confirm-time re-check fails outright (429, 503, no connection).
    httpPost.mockReturnValueOnce(throwError(() => new Error('network error')));

    startRestoreFlow(deps, target(), [...rows(), missingRow]);
    expect(confirmData(dialogOpen).names).toEqual(['Kappa']);

    firstClosed<boolean>(dialogOpen).next(true);

    // Without the fallback this would resend 'PogU' too, even though the open-time check already
    // proved it is already back — undoing its own protection the moment the freshest check fails.
    expect(startRestore).toHaveBeenCalledWith(
      expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
      [{ emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'Kappa', aliases: ['Kappa'] }],
      1,
      false,
      0,
    );
  });

  // #255 P1 (Codex review): a row the open-time check already found present is hidden from the
  // confirmation dialog entirely — it must stay hidden from the *run* too, even if it goes missing
  // from the target set again before the user confirms (another editor, or the confirmation simply
  // left open a while). Without the fix, the confirm-time re-check's own fresh read — which has to
  // query the full row set to apply its per-alias rule correctly — would see the row as newly
  // missing and resend it as an `ADD` the user never saw or agreed to.
  it('never sends a row the open-time check already hid, even if it goes missing again before confirm', () => {
    const { deps, dialogOpen, httpPost, startRestore } = setup();
    const missingRow: PurgeRunRow = {
      emoteId: 'e2',
      sevenTvEmoteId: '7tv-2',
      name: 'Kappa',
      aliases: ['Kappa'],
      status: 'done',
      errorMessage: null,
    };
    // Open-time: '7tv-1' (PogU) is already present -> hidden from the dialog; '7tv-2' (Kappa) is
    // not -> shown.
    httpPost.mockReturnValueOnce(of(emoteSetPage(['7tv-1'])));
    // Confirm-time: '7tv-1' has since been removed from the set too, so a full re-check now finds
    // BOTH rows missing.
    httpPost.mockReturnValueOnce(of(emoteSetPage([])));

    startRestoreFlow(deps, target(), [...rows(), missingRow]);
    expect(confirmData(dialogOpen).names).toEqual(['Kappa']);

    firstClosed<boolean>(dialogOpen).next(true);

    // 'PogU' (7tv-1) never appeared in the confirmation and must not appear in the run either,
    // however the confirm-time read now classifies it.
    expect(startRestore).toHaveBeenCalledWith(
      expect.objectContaining({ setId: SET_ID, hostChannelName: CHANNEL }),
      [{ emoteId: 'e2', sevenTvEmoteId: '7tv-2', name: 'Kappa', aliases: ['Kappa'] }],
      0,
      true,
      0,
    );
  });
});
