import { Dialog } from '@angular/cdk/dialog';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { signal, WritableSignal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { ForeignEmoteSetResponse } from '../../core/seven-tv/foreign-emote-set.model';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { ImportConfirmDialogData, ImportConfirmOutcome } from './import-confirm-dialog';
import { ImportFlowDeps, startImportFlow } from './import-flow';
import { ImportTargetChoice } from './import-target-dialog';

/**
 * `startImportFlow` opens a dialog through the plain `Dialog` object it is handed and never
 * injects anything itself — see the class doc on `ImportFlowDeps`. That is what lets every test
 * here run without a `TestBed`: `dialog.open` is a bare `vi.fn()` that stands in for both the
 * confirm dialog and, when needed, the token prompt, distinguished by call order (first call is
 * always the confirm dialog).
 */

function source(rows: ImportRow[] = [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }]): ImportSource {
  return {
    origin: { kind: 'channel', channelName: 'origin-channel' },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}

/** A K2 picker choice — what `usage-stats-page.ts`'s `startImportFromChoice` hands to
 *  `startImportFlow` as a `'chosen'` target (spec F5, 8.6). */
function choice(overrides: Partial<ImportTargetChoice> = {}): ImportTargetChoice {
  return {
    scope: 'visible',
    emoteSetId: 'set-halloween',
    channelName: 'handofblood',
    ownerDisplayName: 'HandOfBlood',
    setName: 'Halloween',
    isTracked: true,
    twitchLogin: 'handofblood',
    // Deliberately *not* 'set-halloween' — the default choice is a non-active pick, so every
    // existing test in the 'a chosen target' describe block below keeps exercising the live-list
    // path unless it says otherwise. The dedicated active-set test overrides both to match.
    activeEmoteSetId: 'set-active',
    ...overrides,
  };
}

/** A live-list answer (`SevenTvEmoteSetService.loadEmoteSetPreview`, spec 6.4) for a `'chosen'`
 *  target's loader read. */
function liveTarget(overrides: Partial<ForeignEmoteSetResponse> = {}): ForeignEmoteSetResponse {
  return {
    channelName: 'handofblood',
    sevenTvUserId: null,
    emoteSetId: 'set-halloween',
    emoteSetName: 'Halloween',
    capacity: 500,
    totalCount: 338,
    truncated: false,
    emotes: [],
    ...overrides,
  };
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
    activeEmoteSetId: 'set-1',
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
  deps: ImportFlowDeps;
  /** `getSetWarning` as a bare mock, for asserting which set-warning URL/params a call used — the
   *  rest of `emoteAdminService` is reachable through `deps.emoteAdminService` too, but this one
   *  field is the only one any test below inspects directly. */
  getSetWarning: ReturnType<typeof vi.fn>;
  dialogOpen: ReturnType<typeof vi.fn>;
  /** One entry per `loadImportTarget` call — `emoteAdminService.getSetStatus` is the one blocking
   *  request kept under manual control; `listEmotes`/`getSetWarning` resolve synchronously so only
   *  this subject gates when a load settles (needed to pin down the generation counter). */
  statusSubjects: Subject<EmoteSetStatus>[];
  /** `loadImportTarget`'s dialog-open-time fetch (used to build the preview) — resolves
   *  synchronously to an empty target set by default, unless a test overrides it. Unrelated to the
   *  fresh #149/T5 re-check below since #149 P1: that one no longer asks our database at all. */
  listEmotes: ReturnType<typeof vi.fn>;
  /** The fresh #149/T5 re-check run right before `startImport` — since the P1 fix, this is a raw
   *  `HttpClient.post` straight to 7TV's `v4` GQL endpoint (`already-present-filter.ts`), not
   *  `emoteAdminService`. Resolves synchronously to an empty target set by default. */
  httpPost: ReturnType<typeof vi.fn>;
  /** `loadImportTarget`'s live-list read for a `'chosen'` target (spec F5) — only ever called for
   *  that kind, see the setup() note next to it. */
  loadEmoteSetPreview: ReturnType<typeof vi.fn>;
  startImport: ReturnType<typeof vi.fn>;
  hasToken: WritableSignal<boolean>;
  activeRun: WritableSignal<SevenTvRunKind | null>;
}

function setup(): Harness {
  const statusSubjects: Subject<EmoteSetStatus>[] = [];
  const listEmotes = vi.fn(() => of<EmoteListItem[]>([]));
  const getSetWarning = vi.fn(() =>
    of({
      available: true,
      isOwnSet: true,
      otherTrackedChannelsSharingSet: [],
      otherModeratedChannelsSharingSet: [],
    }),
  );
  const emoteAdminService = {
    getSetStatus: vi.fn(() => {
      const subject = new Subject<EmoteSetStatus>();
      statusSubjects.push(subject);
      return subject;
    }),
    // Resolves synchronously and is irrelevant to every test below beyond letting forkJoin settle
    // as soon as the status subject does — only that one is kept under manual control.
    listEmotes,
    getSetWarning,
  } as unknown as EmoteAdminService;

  // Only reached by a `'chosen'` target (the picker's own choice, spec F5) — the three
  // channel-only doors (file, foreign channel, leaderboard) always pass `'activeSet'`, which never
  // calls this. Default answer is irrelevant to every `'activeSet'` test below; the `'chosen'`
  // describe block sets its own return value per case.
  const loadEmoteSetPreview = vi.fn();
  const emoteSetService = { loadEmoteSetPreview } as unknown as SevenTvEmoteSetService;

  const httpPost = vi.fn(() => of(emoteSetPage()));
  const httpClient = { post: httpPost } as unknown as HttpClient;

  const hasToken = signal(true);
  const tokenService = { hasToken } as unknown as SevenTvTokenService;

  const startImport = vi.fn();
  const importService = { startImport } as unknown as SevenTvImportService;

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
      importService,
      arbiter,
    },
    getSetWarning,
    dialogOpen,
    statusSubjects,
    listEmotes,
    httpPost,
    loadEmoteSetPreview,
    startImport,
    hasToken,
    activeRun,
  };
}

/** The `data` the flow handed to `openImportConfirmDialog` — call #0 of `dialog.open`. */
function confirmData(dialogOpen: ReturnType<typeof vi.fn>): ImportConfirmDialogData {
  return dialogOpen.mock.calls[0][1].data as ImportConfirmDialogData;
}

function confirmClosed(
  dialogOpen: ReturnType<typeof vi.fn>,
): Subject<ImportConfirmOutcome | undefined> {
  return dialogOpen.mock.results[0].value.closed as Subject<ImportConfirmOutcome | undefined>;
}

function tokenPromptClosed(dialogOpen: ReturnType<typeof vi.fn>): Subject<boolean> {
  return dialogOpen.mock.results[1].value.closed as Subject<boolean>;
}

describe('startImportFlow', () => {
  it('opens the confirm dialog with the target loading, and starts the load immediately', () => {
    const { deps, dialogOpen, statusSubjects } = setup();

    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    expect(dialogOpen).toHaveBeenCalledTimes(1);
    expect(confirmData(dialogOpen).target()).toEqual({ status: 'loading' });
    expect(statusSubjects).toHaveLength(1);
  });

  it('resolves the target to ready once the load answers', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    statusSubjects[0].next(readyStatus({ activeEmoteSetId: 'set-9' }));
    statusSubjects[0].complete();

    expect(confirmData(dialogOpen).target()).toMatchObject({ status: 'ready', setId: 'set-9' });
  });

  it('resolves the target to failed when a blocking request errors with a non-404 status', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    statusSubjects[0].error(new HttpErrorResponse({ status: 500 }));

    expect(confirmData(dialogOpen).target()).toEqual({ status: 'failed' });
  });

  it('resolves the target to no-set when a blocking request 404s', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    statusSubjects[0].error(new HttpErrorResponse({ status: 404 }));

    expect(confirmData(dialogOpen).target()).toEqual({ status: 'no-set' });
  });

  it('drops a stale load answer after a retry, and applies the newer one once it lands', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });
    const data = confirmData(dialogOpen);

    data.retry();
    expect(statusSubjects).toHaveLength(2);

    // The superseded (first) request finally answers — must be ignored, target stays loading
    // because the current (second) request has not answered yet.
    statusSubjects[0].next(readyStatus({ activeEmoteSetId: 'stale-set' }));
    statusSubjects[0].complete();
    expect(data.target()).toEqual({ status: 'loading' });

    // The current request answers — this one applies.
    statusSubjects[1].next(readyStatus({ activeEmoteSetId: 'fresh-set' }));
    statusSubjects[1].complete();
    expect(data.target()).toMatchObject({ status: 'ready', setId: 'fresh-set' });
  });

  it('ignores a load answer that arrives after the confirm dialog has already closed', () => {
    const { deps, dialogOpen, statusSubjects } = setup();
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });
    const data = confirmData(dialogOpen);

    confirmClosed(dialogOpen).next(undefined);

    statusSubjects[0].next(readyStatus());
    statusSubjects[0].complete();

    expect(data.target()).toEqual({ status: 'loading' });
  });

  it('exposes runBlocked as a live view of the arbiter, for the dialog to disable its own button', () => {
    const { deps, dialogOpen, activeRun } = setup();
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });
    const data = confirmData(dialogOpen);

    expect(data.runBlocked()).toBe(false);

    activeRun.set('restore');

    expect(data.runBlocked()).toBe(true);
  });

  it('starts the import immediately when confirmed and a 7TV token is already stored', () => {
    const { deps, dialogOpen, startImport } = setup();
    const src = source();
    startImportFlow(deps, src, { kind: 'activeSet', channelName: 'target-channel' });

    const outcome: ImportConfirmOutcome = {
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    };
    confirmClosed(dialogOpen).next(outcome);

    // Fourth argument is the fresh #149/T5 re-check's skip count — 0 here because the harness's
    // default 7TV read (`httpPost`) reports an empty target set, so nothing gets filtered a second
    // time. Fifth is whether that check actually ran — true, since the fetch succeeded (#149).
    expect(startImport).toHaveBeenCalledWith(
      { setId: 'set-1', channelName: 'target-channel', ownerDisplayName: null },
      src.origin,
      outcome.rows,
      0,
      true,
    );
    // No second dialog — the token prompt is only for a missing token.
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  // #149/T5: `outcome.rows` already passed `buildImportPreview`'s dialog-open-time filter — this
  // pins the *second*, fresh check that runs right before the send, catching a row that became a
  // duplicate only after the dialog opened (another editor, another tab, a long-open dialog).
  describe('fresh pre-send duplicate check (#149/T5)', () => {
    it('re-checks the target set fresh at confirm time, not reusing the dialog-open snapshot', () => {
      const { deps, dialogOpen, listEmotes, httpPost } = setup();
      startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

      // The dialog-open-time fetch (for the preview) has already happened by now — and the fresh
      // check has not, since nothing has been confirmed yet.
      expect(listEmotes).toHaveBeenCalledTimes(1);
      expect(httpPost).not.toHaveBeenCalled();

      confirmClosed(dialogOpen).next({
        targetSetId: 'set-1',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
      });

      // The fresh check runs right here, at confirm time — against 7TV directly (#149 P1), not
      // `emoteAdminService` again.
      expect(listEmotes).toHaveBeenCalledTimes(1);
      expect(httpPost).toHaveBeenCalledTimes(1);
      expect(httpPost).toHaveBeenCalledWith('https://7tv.io/v4/gql', {
        query: expect.stringContaining('emoteSets'),
        variables: { id: 'set-1', page: 1, perPage: 500 },
      });
    });

    it('drops a row that appeared in the target set only after the dialog opened, and reports it as skipped', () => {
      const { deps, dialogOpen, startImport, httpPost } = setup();
      startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

      // Between the dialog opening (still-empty target, from setup()'s default) and the user
      // confirming, another editor added this exact emote to the target set under a different
      // alias — the dialog-time preview never saw it.
      httpPost.mockReturnValue(of(emoteSetPage(['7tv-1'])));

      confirmClosed(dialogOpen).next({
        targetSetId: 'set-1',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        { setId: 'set-1', channelName: 'target-channel', ownerDisplayName: null },
        source().origin,
        [],
        1,
        true,
      );
    });

    // #149: a failed check must fail open (every row still goes through, the run still starts) but
    // must not read as a clean all-clear — the flow forwards `available: false` from the filter
    // straight into `startImport`'s fifth argument rather than swallowing it.
    it('fails open on a failed duplicate check and reports it as unavailable rather than a clean skip', () => {
      const { deps, dialogOpen, startImport, httpPost } = setup();
      startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

      httpPost.mockReturnValue(throwError(() => new Error('network error')));

      confirmClosed(dialogOpen).next({
        targetSetId: 'set-1',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        { setId: 'set-1', channelName: 'target-channel', ownerDisplayName: null },
        source().origin,
        [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
        0,
        false,
      );
    });
  });

  // #149 P2 (independent review): the arbiter's mutual-exclusion check ran before the fresh
  // duplicate check's async fetch — a second run could start in that window and would have
  // overlapped this one. Pinned as behaviour, not implementation: confirming while the fetch is
  // still in flight, then having another run claim the arbiter before it answers, must abandon
  // this start.
  it('abandons the start when another run claims the arbiter while the fresh check is still in flight', () => {
    const { deps, dialogOpen, startImport, httpPost, activeRun } = setup();
    const fetch = new Subject<ReturnType<typeof emoteSetPage>>();
    httpPost.mockReturnValue(fetch);
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    confirmClosed(dialogOpen).next({
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    });

    // A delete run starts elsewhere while this import's own fresh check is still awaiting 7TV.
    activeRun.set('delete');
    fetch.next(emoteSetPage());
    fetch.complete();

    expect(startImport).not.toHaveBeenCalled();
  });

  it('starts nothing when the confirm dialog is dismissed without an outcome', () => {
    const { deps, dialogOpen, startImport } = setup();
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    confirmClosed(dialogOpen).next(undefined);

    expect(startImport).not.toHaveBeenCalled();
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  it('silently drops a confirmed outcome while another 7TV run is already active', () => {
    const { deps, dialogOpen, startImport, activeRun } = setup();
    activeRun.set('delete');
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    confirmClosed(dialogOpen).next({
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    });

    expect(startImport).not.toHaveBeenCalled();
  });

  it('prompts for a 7TV token when none is stored, and starts only once the prompt confirms', () => {
    const { deps, dialogOpen, startImport, hasToken } = setup();
    hasToken.set(false);
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    confirmClosed(dialogOpen).next({
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    });

    expect(dialogOpen).toHaveBeenCalledTimes(2);
    expect(startImport).not.toHaveBeenCalled();

    tokenPromptClosed(dialogOpen).next(true);

    expect(startImport).toHaveBeenCalledTimes(1);
  });

  it('does not start the import when the token prompt is dismissed', () => {
    const { deps, dialogOpen, startImport, hasToken } = setup();
    hasToken.set(false);
    startImportFlow(deps, source(), { kind: 'activeSet', channelName: 'target-channel' });

    confirmClosed(dialogOpen).next({
      targetSetId: 'set-1',
      rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
    });
    tokenPromptClosed(dialogOpen).next(false);

    expect(startImport).not.toHaveBeenCalled();
  });

  // The bug spec F5 names and the reason T2.5a and T2.5b were forced into one commit ("ein Picker
  // ohne Loader-Umbau schriebe ins falsche Set"): a `'chosen'` target must drive the loader — and
  // the eventual run — onto the *picked* set, never silently back onto the channel's active one.
  describe('a chosen target (spec F5, K2 picker)', () => {
    it('reads a non-active tracked set from the live list, never the channel active-set endpoint', () => {
      const { deps, dialogOpen, loadEmoteSetPreview, statusSubjects } = setup();
      loadEmoteSetPreview.mockReturnValue(of(liveTarget()));

      startImportFlow(deps, source(), { kind: 'chosen', choice: choice() });

      // The old bug: this used to be `getSetStatus('handofblood')`, which answers with the
      // channel's *active* set regardless of what was picked. Proving it was never called is as
      // important here as proving the live list was.
      expect(statusSubjects).toHaveLength(0);
      expect(loadEmoteSetPreview).toHaveBeenCalledWith('handofblood', 'set-halloween');

      expect(confirmData(dialogOpen).target()).toEqual({
        status: 'ready',
        setId: 'set-halloween',
        setName: 'Halloween',
        occupiedSlots: 338,
        capacity: 500,
        syncFailureReason: null,
        emotes: [],
        warning: {
          available: true,
          isOwnSet: true,
          otherTrackedChannelsSharingSet: [],
          otherModeratedChannelsSharingSet: [],
        },
      });
    });

    // The mirror of the test above (spec 8.6, fourth bullet; AK 36): choosing a set that *is* the
    // account's own active one must resolve exactly like an 'activeSet' target — same requests, no
    // live-list read at all. A first draft of this fix sent every tracked choice through the
    // live-list path unconditionally, which the loader's own tests could not catch (they only ever
    // see a `'trackedActive'` selection built by hand) — this is the caller-side test that pins the
    // fork the loader's own suite cannot.
    it('resolves a choice on the account active set exactly like an activeSet target — no live-list read', () => {
      const { deps, dialogOpen, loadEmoteSetPreview, statusSubjects } = setup();

      startImportFlow(deps, source(), {
        kind: 'chosen',
        choice: choice({ emoteSetId: 'set-active', activeEmoteSetId: 'set-active' }),
      });

      expect(loadEmoteSetPreview).not.toHaveBeenCalled();
      expect(statusSubjects).toHaveLength(1);

      statusSubjects[0].next(readyStatus({ activeEmoteSetId: 'set-active' }));
      statusSubjects[0].complete();

      expect(confirmData(dialogOpen).target()).toMatchObject({
        status: 'ready',
        setId: 'set-active',
      });
    });

    it('checks set-warning for the chosen set id, not the channel active-set warning', () => {
      const { deps, loadEmoteSetPreview, getSetWarning } = setup();
      loadEmoteSetPreview.mockReturnValue(of(liveTarget()));

      startImportFlow(deps, source(), { kind: 'chosen', choice: choice() });

      expect(getSetWarning).toHaveBeenCalledWith('handofblood', 'set-halloween');
    });

    it('passes the chosen (non-active) set through to the run, not the channel active set', () => {
      const { deps, dialogOpen, startImport, loadEmoteSetPreview } = setup();
      loadEmoteSetPreview.mockReturnValue(of(liveTarget()));
      startImportFlow(deps, source(), { kind: 'chosen', choice: choice() });

      // The confirm dialog would close with `target.setId` from the ready state above — simulated
      // here exactly as the earlier `'activeSet'` tests simulate a confirm.
      confirmClosed(dialogOpen).next({
        targetSetId: 'set-halloween',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        { setId: 'set-halloween', channelName: 'handofblood', ownerDisplayName: null },
        source().origin,
        [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
        0,
        true,
      );
    });

    it('names the channel and leaves the owner unset for a tracked choice, in the confirm dialog data', () => {
      const { deps, dialogOpen, loadEmoteSetPreview } = setup();
      loadEmoteSetPreview.mockReturnValue(of(liveTarget()));
      startImportFlow(deps, source(), { kind: 'chosen', choice: choice() });

      const data = confirmData(dialogOpen);
      expect(data.targetChannelName).toBe('handofblood');
      expect(data.targetOwnerDisplayName).toBeNull();
    });

    it('routes an untracked choice through its twitchLogin, never its display name', () => {
      const { deps, loadEmoteSetPreview } = setup();
      loadEmoteSetPreview.mockReturnValue(
        of(liveTarget({ channelName: 'stranger', emoteSetId: 'set-x', emoteSetName: 'Wegwerf' })),
      );
      const untracked = choice({
        channelName: null,
        isTracked: false,
        twitchLogin: 'stranger',
        ownerDisplayName: 'Ström & Änger', // a display name a URL path segment could never carry
        emoteSetId: 'set-x',
      });

      startImportFlow(deps, source(), { kind: 'chosen', choice: untracked });

      expect(loadEmoteSetPreview).toHaveBeenCalledWith('stranger', 'set-x');
    });

    it('starts a run with channelName: null for a confirmed untracked choice (T2.6)', () => {
      // Pre-T2.6 this flow refused to start anything for an untracked choice at all — a defensive
      // backstop for a state `usage-stats-page.ts`'s own guard was supposed to make unreachable.
      // T2.6 lifted that guard on purpose (the confirmation now happens earlier, inside the picker
      // itself, spec 8.6) — a `'chosen'` target with no channel name is exactly what a confirmed
      // untracked pick looks like once it reaches here, and this flow's job is to start it like any
      // other, not to refuse it a second time.
      const { deps, dialogOpen, startImport, loadEmoteSetPreview } = setup();
      loadEmoteSetPreview.mockReturnValue(
        of(liveTarget({ channelName: 'stranger', emoteSetId: 'set-x' })),
      );
      const untracked = choice({
        channelName: null,
        isTracked: false,
        twitchLogin: 'stranger',
        ownerDisplayName: 'Stranger',
      });
      const src = source();

      startImportFlow(deps, src, { kind: 'chosen', choice: untracked });
      const outcome: ImportConfirmOutcome = {
        targetSetId: 'set-x',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'Kappa' }],
      };
      confirmClosed(dialogOpen).next(outcome);

      // The owner name rides along too (T2.6) — import-progress-section.ts's own "Ziel: …" line
      // needs it once the confirm dialog (which showed the same name, AK 39) is gone.
      expect(startImport).toHaveBeenCalledWith(
        { setId: 'set-x', channelName: null, ownerDisplayName: 'Stranger' },
        src.origin,
        outcome.rows,
        0,
        true,
      );
    });
  });
});
